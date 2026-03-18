using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XtreamBridge.Models;
using XtreamBridge.Persistence;

namespace XtreamBridge.Services;

/// <summary>
/// Generates .strm and .nfo files under OutputPath for Plex.
///
/// Sync strategy (minimises API calls):
///   1. Fetch ALL streams in one bulk request (get_vod_streams / get_series)
///   2. Load last snapshot and compute delta (new / modified / removed)
///   3. For UNCHANGED items: skip entirely if .strm already on disk
///   4. For NEW or MODIFIED items only: call get_vod_info / get_series_info
///   5. Orphan cleanup: diff disk files vs current stream list (by stream_id in URL)
///   6. Save new snapshot
///
/// Layout:
///   {OutputRoot}/Movies/{Category}/{Title} ({Year}).strm|.nfo
///   {OutputRoot}/Series/{Category}/{Series}/tvshow.nfo
///   {OutputRoot}/Series/{Category}/{Series}/Season {NN}/S{NN}E{NN} - {Title}.strm|.nfo
/// </summary>
public sealed class StrmGeneratorService
{
    private readonly XtreamClient _client;
    private readonly SyncStateRepository _repo;
    private readonly SnapshotService _snapshots;
    private readonly MetadataService _metadata;
    private readonly IOptionsMonitor<AppSettings> _opts;
    private readonly ILogger<StrmGeneratorService> _logger;

    private AppSettings Settings => _opts.CurrentValue;
    private string OutputRoot    => string.IsNullOrWhiteSpace(Settings.Bridge.OutputPath)
                                        ? "/output"
                                        : Settings.Bridge.OutputPath;

    public StrmGeneratorService(
        XtreamClient client,
        SyncStateRepository repo,
        SnapshotService snapshots,
        MetadataService metadata,
        IOptionsMonitor<AppSettings> opts,
        ILogger<StrmGeneratorService> logger)
    {
        _client    = client;
        _repo      = repo;
        _snapshots = snapshots;
        _metadata  = metadata;
        _opts      = opts;
        _logger    = logger;
    }

    // ── Movies ────────────────────────────────────────────────────────────────

    public async Task<(int Created, int Skipped, int Removed)> SyncMoviesAsync(
        SyncProgress progress,
        CancellationToken ct,
        bool forceFullSync = false)
    {
        _logger.LogInformation("VOD sync started (force={Force})…", forceFullSync);
        progress.Phase = "movies";

        var state      = await _repo.LoadAsync(ct);
        var categories = await _client.GetVodCategoriesAsync(ct) ?? new();
        var catMap     = categories.ToDictionary(c => c.CategoryId, c => Sanitize(c.CategoryName));
        var filter     = Settings.Sync.VodCategoryFilter;

        _logger.LogInformation("Fetching all VOD streams in one request…");
        var allStreams = await _client.GetAllVodStreamsAsync(ct) ?? new();
        if (filter.Count > 0)
            allStreams = allStreams.Where(s => s.CategoryId.HasValue && filter.Contains(s.CategoryId.Value)).ToList();
        _logger.LogInformation("VOD streams fetched: {Count}", allStreams.Count);

        // ── Snapshot delta ────────────────────────────────────────────────────
        ContentSnapshot? prevSnapshot = null;
        if (Settings.Sync.EnableSnapshotSync && !forceFullSync)
            prevSnapshot = await _snapshots.LoadLatestAsync(ct);

        var delta = DeltaCalculator.CalculateMovieDelta(allStreams, prevSnapshot);

        // Stream IDs that actually need API calls (new or provider-side modified)
        var changedIds = new HashSet<int>(
            delta.NewMovies.Select(m => m.StreamId)
            .Concat(delta.ModifiedMovies.Select(m => m.StreamId)));

        // On first run, treat all as changed unless disk check overrides below
        if (prevSnapshot is null || forceFullSync)
            changedIds = new HashSet<int>(allStreams.Select(s => s.StreamId));

        // ── Build disk index: stream_id → existing .strm path ─────────────────
        var diskIndex = BuildMovieDiskIndex(ct);

        progress.TotalItems     = allStreams.Count;
        progress.ItemsProcessed = 0;

        int created = 0, skipped = 0, removed = 0;
        int processedCount = 0;

        var streamCatMap = allStreams.ToDictionary(
            s => s.StreamId,
            s => catMap.TryGetValue(s.CategoryId ?? 0, out var cn) ? cn : "Uncategorized");

        await Parallel.ForEachAsync(
            allStreams,
            new ParallelOptions { MaxDegreeOfParallelism = Settings.Sync.SyncParallelism, CancellationToken = ct },
            async (vod, innerCt) =>
            {
                progress.CurrentItem = vod.Name;

                var catName = streamCatMap.TryGetValue(vod.StreamId, out var cn) ? cn : "Uncategorized";

                // ── Fast path: unchanged + already on disk ─────────────────────
                if (!changedIds.Contains(vod.StreamId) && diskIndex.ContainsKey(vod.StreamId))
                {
                    Interlocked.Increment(ref skipped);
                    progress.ItemsProcessed = Interlocked.Increment(ref processedCount);
                    return;
                }

                // ── Slow path: new or modified — NO get_vod_info needed ────────
                // All data (name, year, poster, tmdb_id, plot…) comes from the bulk response.
                // TMDb API is called optionally to enrich with extra metadata.

                var year     = ParseYear(vod.Year);
                var title    = Sanitize(vod.Name);
                var fileName = year > 0 ? $"{title} ({year})" : title;
                var dir      = Path.Combine(OutputRoot, "Movies", catName);
                Directory.CreateDirectory(dir);

                var url      = _client.BuildVodStreamUrl(vod.StreamId, vod.ContainerExtension);
                var strmPath = Path.Combine(dir, $"{fileName}.strm");
                File.WriteAllText(strmPath, url, Encoding.UTF8);

                if (Settings.Sync.GenerateNfoFiles)
                {
                    TmdbResult? tmdb = null;
                    if (Settings.Sync.EnableMetadataLookup && !string.IsNullOrEmpty(Settings.Sync.TmdbApiKey))
                    {
                        // Use TMDb ID from bulk data if available — avoids a search request
                        if (!string.IsNullOrEmpty(vod.BestTmdbId) && int.TryParse(vod.BestTmdbId, out var tmdbId))
                            tmdb = await _metadata.GetMovieByIdAsync(tmdbId, innerCt);
                        else
                            tmdb = await _metadata.SearchMovieAsync(vod.Name, year, innerCt);
                    }

                    await NfoWriter.WriteMovieNfoAsync(Path.Combine(dir, $"{fileName}.nfo"), vod, tmdb);
                }

                state.SyncedVodIds.Add(vod.StreamId);
                Interlocked.Increment(ref created);
                progress.ItemsProcessed = Interlocked.Increment(ref processedCount);
            });

        // ── Orphan cleanup: diff disk vs provider ─────────────────────────────
        if (Settings.Sync.CleanupOrphans)
        {
            var currentIds = new HashSet<int>(allStreams.Select(s => s.StreamId));
            removed = await CleanupMovieOrphansAsync(diskIndex, currentIds, allStreams.Count, ct);
        }

        // ── Save snapshot ─────────────────────────────────────────────────────
        if (Settings.Sync.EnableSnapshotSync)
        {
            var snap = prevSnapshot ?? new ContentSnapshot();
            snap.TakenAt = DateTimeOffset.UtcNow;
            snap.Movies  = DeltaCalculator.BuildMovieSnapshot(allStreams);
            await _snapshots.SaveAsync(snap, ct);
        }

        await _repo.SaveAsync(state, ct);
        _logger.LogInformation("VOD sync: {C} created/updated, {S} skipped (unchanged), {R} removed",
            created, skipped, removed);
        return (created, skipped, removed);
    }

    // ── Series ────────────────────────────────────────────────────────────────

    public async Task<(int Created, int Skipped, int Removed)> SyncSeriesAsync(
        SyncProgress progress,
        CancellationToken ct,
        bool forceFullSync = false)
    {
        _logger.LogInformation("Series sync started (force={Force})…", forceFullSync);
        progress.Phase = "series";

        var state      = await _repo.LoadAsync(ct);
        var categories = await _client.GetSeriesCategoriesAsync(ct) ?? new();
        var catMap     = categories.ToDictionary(c => c.CategoryId, c => Sanitize(c.CategoryName));
        var filter     = Settings.Sync.SeriesCategoryFilter;

        _logger.LogInformation("Fetching all series in one request…");
        var allSeries = await _client.GetAllSeriesAsync(ct) ?? new();
        if (filter.Count > 0)
            allSeries = allSeries.Where(s => s.CategoryId.HasValue && filter.Contains(s.CategoryId.Value)).ToList();
        _logger.LogInformation("Series fetched: {Count}", allSeries.Count);

        // ── Snapshot delta ────────────────────────────────────────────────────
        ContentSnapshot? prevSnapshot = null;
        if (Settings.Sync.EnableSnapshotSync && !forceFullSync)
            prevSnapshot = await _snapshots.LoadLatestAsync(ct);

        var delta = DeltaCalculator.CalculateSeriesDelta(allSeries, prevSnapshot);

        var changedIds = new HashSet<int>(
            delta.NewSeries.Select(s => s.SeriesId)
            .Concat(delta.ModifiedSeries.Select(s => s.SeriesId)));

        if (prevSnapshot?.Series.Count == 0 || forceFullSync)
            changedIds = new HashSet<int>(allSeries.Select(s => s.SeriesId));

        progress.TotalItems     = allSeries.Count;
        progress.ItemsProcessed = 0;

        int created = 0, skipped = 0, removed = 0;

        var seriesCatMap = allSeries.ToDictionary(
            s => s.SeriesId,
            s => catMap.TryGetValue(s.CategoryId ?? 0, out var cn) ? cn : "Uncategorized");

        foreach (var series in allSeries)
        {
            if (ct.IsCancellationRequested) break;
            progress.CurrentItem = series.Name;

            var catName   = seriesCatMap.TryGetValue(series.SeriesId, out var cn) ? cn : "Uncategorized";
            var seriesDir = Path.Combine(OutputRoot, "Series", catName, Sanitize(series.Name));

            // ── Fast path: unchanged + tvshow.nfo already on disk ─────────────
            if (!changedIds.Contains(series.SeriesId) && File.Exists(Path.Combine(seriesDir, "tvshow.nfo")))
            {
                skipped++;
                progress.ItemsProcessed++;
                continue;
            }

            // ── Slow path: new or modified — call get_series_info ─────────────
            Directory.CreateDirectory(seriesDir);
            var info = await _client.GetSeriesStreamsBySeriesAsync(series.SeriesId, ct);
            if (info?.Episodes is null)
            {
                skipped++;
                progress.ItemsProcessed++;
                continue;
            }

            if (Settings.Sync.GenerateNfoFiles)
            {
                TmdbResult? tmdb = null;
                if (Settings.Sync.EnableMetadataLookup && !string.IsNullOrEmpty(Settings.Sync.TmdbApiKey))
                {
                    if (!string.IsNullOrEmpty(series.BestTmdbId) && int.TryParse(series.BestTmdbId, out var tmdbId))
                        tmdb = await _metadata.GetSeriesByIdAsync(tmdbId, ct);
                    else
                        tmdb = await _metadata.SearchSeriesAsync(series.Name, 0, ct);
                }
                await NfoWriter.WriteSeriesNfoAsync(Path.Combine(seriesDir, "tvshow.nfo"), series, tmdb);
            }

            foreach (var ep in info.Episodes.SelectMany(kv => kv.Value))
            {
                var seasonDir = Path.Combine(seriesDir, $"Season {ep.Season:D2}");
                Directory.CreateDirectory(seasonDir);

                var epFile   = $"S{ep.Season:D2}E{ep.EpisodeNum:D2} - {Sanitize(ep.Title)}";
                var url      = _client.BuildSeriesStreamUrl(ep.EpisodeId, ep.ContainerExtension);
                var strmPath = Path.Combine(seasonDir, $"{epFile}.strm");

                // Only write if new (don't overwrite existing episode files)
                if (!File.Exists(strmPath))
                    File.WriteAllText(strmPath, url, Encoding.UTF8);

                if (Settings.Sync.GenerateNfoFiles)
                {
                    var nfoPath = Path.Combine(seasonDir, $"{epFile}.nfo");
                    if (!File.Exists(nfoPath))
                        await NfoWriter.WriteEpisodeNfoAsync(nfoPath, ep, series.Name);
                }
            }

            state.SyncedSeriesIds.Add(series.SeriesId.ToString());
            created++;
            progress.ItemsProcessed++;
        }

        // ── Orphan cleanup: series folders whose ID no longer exists ──────────
        if (Settings.Sync.CleanupOrphans)
        {
            var currentIds = new HashSet<int>(allSeries.Select(s => s.SeriesId));
            removed = CleanupSeriesOrphans(currentIds, allSeries.Count);
        }

        // ── Save snapshot ─────────────────────────────────────────────────────
        if (Settings.Sync.EnableSnapshotSync)
        {
            var snap = prevSnapshot ?? new ContentSnapshot();
            snap.TakenAt = DateTimeOffset.UtcNow;
            snap.Series  = DeltaCalculator.BuildSeriesSnapshot(allSeries);
            await _snapshots.SaveAsync(snap, ct);
        }

        await _repo.SaveAsync(state, ct);
        _logger.LogInformation("Series sync: {C} created/updated, {S} skipped (unchanged), {R} removed",
            created, skipped, removed);
        return (created, skipped, removed);
    }

    // ── Disk index ─────────────────────────────────────────────────────────────
    // Builds a map of stream_id → .strm file path by reading existing files.
    // Used to detect unchanged items (skip) and orphans (delete).

    private Dictionary<int, string> BuildMovieDiskIndex(CancellationToken ct)
    {
        var index     = new Dictionary<int, string>();
        var moviesDir = Path.Combine(OutputRoot, "Movies");
        if (!Directory.Exists(moviesDir)) return index;

        foreach (var strm in Directory.EnumerateFiles(moviesDir, "*.strm", SearchOption.AllDirectories))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var url = File.ReadAllText(strm, Encoding.UTF8);
                if (TryExtractVodId(url, out var id))
                    index.TryAdd(id, strm);
            }
            catch { /* skip unreadable */ }
        }
        return index;
    }

    // ── Orphan cleanup ─────────────────────────────────────────────────────────

    private async Task<int> CleanupMovieOrphansAsync(
        Dictionary<int, string> diskIndex,
        HashSet<int> currentIds,
        int totalCount,
        CancellationToken ct)
    {
        var orphans = diskIndex
            .Where(kv => !currentIds.Contains(kv.Key))
            .Select(kv => kv.Value)
            .ToList();

        if (orphans.Count == 0) return 0;

        var percent = totalCount > 0 ? (double)orphans.Count / totalCount : 1.0;
        if (percent > Settings.Sync.OrphanSafetyThreshold)
        {
            _logger.LogWarning(
                "Orphan cleanup aborted — {Count} files ({Pct:P0}) would be deleted, exceeds {T:P0} threshold",
                orphans.Count, percent, Settings.Sync.OrphanSafetyThreshold);
            return 0;
        }

        int deleted = 0;
        foreach (var strm in orphans)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                File.Delete(strm);
                var nfo = Path.ChangeExtension(strm, ".nfo");
                if (File.Exists(nfo)) File.Delete(nfo);
                deleted++;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete orphan {F}", strm); }
        }
        _logger.LogInformation("Removed {Count} movie orphans", deleted);
        return deleted;
    }

    private int CleanupSeriesOrphans(HashSet<int> currentIds, int totalCount)
    {
        var seriesRoot = Path.Combine(OutputRoot, "Series");
        if (!Directory.Exists(seriesRoot)) return 0;

        // Read stream IDs from .strm files inside each series folder
        var orphanDirs = new List<string>();
        foreach (var catDir in Directory.EnumerateDirectories(seriesRoot))
        foreach (var serDir in Directory.EnumerateDirectories(catDir))
        {
            var ids = new HashSet<int>();
            foreach (var strm in Directory.EnumerateFiles(serDir, "*.strm", SearchOption.AllDirectories))
            {
                try
                {
                    var url = File.ReadAllText(strm, Encoding.UTF8);
                    if (TryExtractSeriesId(url, out var id)) ids.Add(id);
                }
                catch { }
            }
            if (ids.Count > 0 && !ids.Any(id => currentIds.Contains(id)))
                orphanDirs.Add(serDir);
        }

        if (orphanDirs.Count == 0) return 0;

        var percent = totalCount > 0 ? (double)orphanDirs.Count / totalCount : 1.0;
        if (percent > Settings.Sync.OrphanSafetyThreshold)
        {
            _logger.LogWarning(
                "Series orphan cleanup aborted — {Count} folders ({Pct:P0}) would be deleted, exceeds threshold",
                orphanDirs.Count, percent);
            return 0;
        }

        int deleted = 0;
        foreach (var dir in orphanDirs)
        {
            try { Directory.Delete(dir, recursive: true); deleted++; }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete orphan series folder {D}", dir); }
        }
        _logger.LogInformation("Removed {Count} orphan series folders", deleted);
        return deleted;
    }

    // ── URL parsers ────────────────────────────────────────────────────────────

    private static bool TryExtractVodId(string url, out int id)
    {
        id = 0;
        var m = Regex.Match(url, @"/movie/[^/]+/[^/]+/(\d+)\.");
        return m.Success && int.TryParse(m.Groups[1].Value, out id);
    }

    private static bool TryExtractSeriesId(string url, out int id)
    {
        id = 0;
        var m = Regex.Match(url, @"/series/[^/]+/[^/]+/(\d+)\.");
        return m.Success && int.TryParse(m.Groups[1].Value, out id);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static readonly Regex _invalid = new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);
    private static string Sanitize(string s) => _invalid.Replace(s, "_").Trim().TrimEnd('.');

    private static int ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return 0;
        if (date.Length >= 4 && int.TryParse(date[..4], out var y) && y > 1900 && y < 2200) return y;
        return 0;
    }
}
