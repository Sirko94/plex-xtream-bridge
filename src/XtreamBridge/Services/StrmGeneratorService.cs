using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XtreamBridge.Models;
using XtreamBridge.Persistence;

namespace XtreamBridge.Services;

/// <summary>
/// Generates .strm and .nfo sidecar files under /output for Plex to pick up.
/// Supports snapshot-based incremental sync, orphan cleanup with safety threshold,
/// and optional TMDb metadata enrichment.
///
/// Layout:
///   /output/Movies/{Category}/{Title} ({Year}).strm|.nfo
///   /output/Series/{Category}/{Series}/tvshow.nfo
///   /output/Series/{Category}/{Series}/Season {N}/S{NN}E{NN} - {Title}.strm|.nfo
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
        var filter     = _opts.CurrentValue.Sync.VodCategoryFilter;

        // Single request for all streams — avoids per-category 429 rate limiting
        _logger.LogInformation("Fetching all VOD streams in one request…");
        var allStreams = await _client.GetAllVodStreamsAsync(ct) ?? new();
        if (filter.Count > 0)
            allStreams = allStreams.Where(s => s.CategoryId.HasValue && filter.Contains(s.CategoryId.Value)).ToList();
        _logger.LogInformation("VOD streams fetched: {Count}", allStreams.Count);

        // Load previous snapshot and compute delta
        ContentSnapshot? prevSnapshot = null;
        if (_opts.CurrentValue.Sync.EnableSnapshotSync && !forceFullSync)
            prevSnapshot = await _snapshots.LoadLatestAsync(ct);

        var delta   = DeltaCalculator.CalculateMovieDelta(allStreams, prevSnapshot);
        var isFirst = prevSnapshot is null || forceFullSync;

        // Which streams need processing?
        var toProcess = isFirst
            ? allStreams
            : delta.NewMovies.Concat(delta.ModifiedMovies)
                             .Select(snap => allStreams.FirstOrDefault(s => s.StreamId == snap.StreamId))
                             .Where(s => s is not null)
                             .Select(s => s!)
                             .ToList();

        progress.TotalItems     = toProcess.Count;
        progress.ItemsProcessed = 0;

        int created = 0, skipped = 0, removed = 0;
        int processedCount = 0;

        // Build category map for stream lookup
        var streamCatMap = allStreams.ToDictionary(s => s.StreamId, s => catMap.TryGetValue(s.CategoryId ?? 0, out var cn) ? cn : "Uncategorized");

        await Parallel.ForEachAsync(toProcess, new ParallelOptions { MaxDegreeOfParallelism = _opts.CurrentValue.Sync.SyncParallelism, CancellationToken = ct },
            async (vod, innerCt) =>
            {
                var catName = streamCatMap.TryGetValue(vod.StreamId, out var cn) ? cn : "Uncategorized";
                progress.CurrentItem = vod.Name;

                XtreamVodInfoResponse? info = null;
                if (_opts.CurrentValue.Sync.GenerateNfoFiles)
                    info = await _client.GetVodInfoAsync(vod.StreamId, innerCt);

                var details  = info?.Info;
                var year     = ParseYear(details?.ReleaseDate);
                var title    = Sanitize(details?.Name ?? vod.Name);
                var fileName = year > 0 ? $"{title} ({year})" : title;
                var dir      = Path.Combine(OutputRoot, "Movies", catName);
                Directory.CreateDirectory(dir);

                var url      = _client.BuildVodStreamUrl(vod.StreamId, vod.ContainerExtension);
                var strmPath = Path.Combine(dir, $"{fileName}.strm");
                WriteStrmIfNew(strmPath, url);

                if (_opts.CurrentValue.Sync.GenerateNfoFiles)
                {
                    TmdbResult? tmdb = null;
                    if (_opts.CurrentValue.Sync.EnableMetadataLookup && !string.IsNullOrEmpty(_opts.CurrentValue.Sync.TmdbApiKey))
                        tmdb = await _metadata.SearchMovieAsync(vod.Name, year, innerCt);

                    var nfoPath = Path.Combine(dir, $"{fileName}.nfo");
                    await NfoWriter.WriteMovieNfoAsync(nfoPath, vod, details, tmdb);
                }

                state.SyncedVodIds.Add(vod.StreamId);
                Interlocked.Increment(ref created);
                progress.ItemsProcessed = Interlocked.Increment(ref processedCount);
            });

        // Orphan cleanup
        if (_opts.CurrentValue.Sync.CleanupOrphans && (isFirst || delta.RemovedMovies.Count > 0))
        {
            var currentIds = new HashSet<int>(allStreams.Select(s => s.StreamId));
            removed = await CleanupMovieOrphansAsync(currentIds, allStreams.Count, ct);
        }

        // Save new snapshot
        if (_opts.CurrentValue.Sync.EnableSnapshotSync)
        {
            var snap = prevSnapshot ?? new ContentSnapshot();
            snap.TakenAt = DateTimeOffset.UtcNow;
            snap.Movies  = DeltaCalculator.BuildMovieSnapshot(allStreams);
            await _snapshots.SaveAsync(snap, ct);
        }

        await _repo.SaveAsync(state, ct);
        _logger.LogInformation("VOD sync complete: {Created} created, {Skipped} skipped, {Removed} removed", created, skipped, removed);
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
        var filter     = _opts.CurrentValue.Sync.SeriesCategoryFilter;

        // Single request for all series — avoids per-category 429 rate limiting
        _logger.LogInformation("Fetching all series in one request…");
        var allSeries = await _client.GetAllSeriesAsync(ct) ?? new();
        if (filter.Count > 0)
            allSeries = allSeries.Where(s => s.CategoryId.HasValue && filter.Contains(s.CategoryId.Value)).ToList();
        _logger.LogInformation("Series fetched: {Count}", allSeries.Count);

        ContentSnapshot? prevSnapshot = null;
        if (_opts.CurrentValue.Sync.EnableSnapshotSync && !forceFullSync)
            prevSnapshot = await _snapshots.LoadLatestAsync(ct);

        var delta   = DeltaCalculator.CalculateSeriesDelta(allSeries, prevSnapshot);
        var isFirst = prevSnapshot?.Series.Count == 0 || forceFullSync;

        var toProcess = isFirst
            ? allSeries
            : delta.NewSeries.Concat(delta.ModifiedSeries)
                             .Select(snap => allSeries.FirstOrDefault(s => s.SeriesId == snap.SeriesId))
                             .Where(s => s is not null)
                             .Select(s => s!)
                             .ToList();

        progress.TotalItems = toProcess.Count;
        progress.ItemsProcessed = 0;

        int created = 0, skipped = 0, removed = 0;

        var seriesCatMap = allSeries.ToDictionary(s => s.SeriesId, s => catMap.TryGetValue(s.CategoryId ?? 0, out var cn) ? cn : "Uncategorized");

        foreach (var series in toProcess)
        {
            if (ct.IsCancellationRequested) break;

            progress.CurrentItem = series.Name;

            var catName   = seriesCatMap.TryGetValue(series.SeriesId, out var cn) ? cn : "Uncategorized";
            var seriesDir = Path.Combine(OutputRoot, "Series", catName, Sanitize(series.Name));
            Directory.CreateDirectory(seriesDir);

            var info = await _client.GetSeriesStreamsBySeriesAsync(series.SeriesId, ct);
            if (info?.Episodes is null)
            {
                skipped++;
                continue;
            }

            if (_opts.CurrentValue.Sync.GenerateNfoFiles)
            {
                TmdbResult? tmdb = null;
                if (_opts.CurrentValue.Sync.EnableMetadataLookup && !string.IsNullOrEmpty(_opts.CurrentValue.Sync.TmdbApiKey))
                    tmdb = await _metadata.SearchSeriesAsync(series.Name, 0, ct);

                await NfoWriter.WriteSeriesNfoAsync(Path.Combine(seriesDir, "tvshow.nfo"), series, tmdb);
            }

            var sem = new SemaphoreSlim(_opts.CurrentValue.Sync.SyncParallelism, _opts.CurrentValue.Sync.SyncParallelism);
            var epTasks = info.Episodes
                .SelectMany(kv => kv.Value)
                .Select(async ep =>
                {
                    var seasonDir = Path.Combine(seriesDir, $"Season {ep.Season:D2}");
                    Directory.CreateDirectory(seasonDir);

                    var epFile = $"S{ep.Season:D2}E{ep.EpisodeNum:D2} - {Sanitize(ep.Title)}";
                    var url    = _client.BuildSeriesStreamUrl(ep.EpisodeId, ep.ContainerExtension);
                    WriteStrmIfNew(Path.Combine(seasonDir, $"{epFile}.strm"), url);

                    if (_opts.CurrentValue.Sync.GenerateNfoFiles)
                        await NfoWriter.WriteEpisodeNfoAsync(Path.Combine(seasonDir, $"{epFile}.nfo"), ep, series.Name);
                });

            await Task.WhenAll(epTasks);

            state.SyncedSeriesIds.Add(series.SeriesId.ToString());
            created++;
            progress.ItemsProcessed++;
        }

        // Orphan cleanup
        if (_opts.CurrentValue.Sync.CleanupOrphans)
        {
            var currentIds = new HashSet<int>(allSeries.Select(s => s.SeriesId));
            removed = CleanupSeriesOrphans(currentIds, allSeries.Count);
        }

        // Update snapshot
        if (_opts.CurrentValue.Sync.EnableSnapshotSync)
        {
            var snap = prevSnapshot ?? new ContentSnapshot();
            snap.TakenAt = DateTimeOffset.UtcNow;
            snap.Series  = DeltaCalculator.BuildSeriesSnapshot(allSeries);
            await _snapshots.SaveAsync(snap, ct);
        }

        await _repo.SaveAsync(state, ct);
        _logger.LogInformation("Series sync complete: {Created} created, {Skipped} skipped, {Removed} removed", created, skipped, removed);
        return (created, skipped, removed);
    }

    // ── Orphan cleanup ────────────────────────────────────────────────────────

    private async Task<int> CleanupMovieOrphansAsync(
        HashSet<int> currentIds,
        int totalCount,
        CancellationToken ct)
    {
        var moviesRoot = Path.Combine(OutputRoot, "Movies");
        if (!Directory.Exists(moviesRoot)) return 0;

        var strmFiles = Directory.GetFiles(moviesRoot, "*.strm", SearchOption.AllDirectories);
        var toRemove  = new List<string>();

        // A movie strm is an orphan if its stream_id no longer appears in currentIds
        // We infer the stream_id from the URL inside the strm file
        foreach (var strm in strmFiles)
        {
            try
            {
                var url = await File.ReadAllTextAsync(strm, ct);
                if (TryExtractVodId(url, out var id) && !currentIds.Contains(id))
                    toRemove.Add(strm);
            }
            catch { /* skip */ }
        }

        if (toRemove.Count == 0) return 0;

        var percent = totalCount > 0 ? (double)toRemove.Count / totalCount : 1.0;
        if (percent > _opts.CurrentValue.Sync.OrphanSafetyThreshold)
        {
            _logger.LogWarning(
                "Orphan cleanup aborted — would remove {Count} files ({Pct:P0}), exceeds {Threshold:P0} threshold",
                toRemove.Count, percent, _opts.CurrentValue.Sync.OrphanSafetyThreshold);
            return 0;
        }

        foreach (var f in toRemove)
        {
            try
            {
                File.Delete(f);
                var nfo = Path.ChangeExtension(f, ".nfo");
                if (File.Exists(nfo)) File.Delete(nfo);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete orphan {F}", f); }
        }

        _logger.LogInformation("Removed {Count} movie orphans", toRemove.Count);
        return toRemove.Count;
    }

    private int CleanupSeriesOrphans(HashSet<int> currentIds, int totalCount)
    {
        var seriesRoot = Path.Combine(OutputRoot, "Series");
        if (!Directory.Exists(seriesRoot)) return 0;
        return 0; // Series orphan cleanup: more complex, not implemented for safety
    }

    // ── File helpers ──────────────────────────────────────────────────────────

    private static void WriteStrmIfNew(string path, string url)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, url, Encoding.UTF8);
    }

    private static bool TryExtractVodId(string url, out int id)
    {
        id = 0;
        // Pattern: .../movie/{user}/{pass}/{id}.{ext}
        var match = Regex.Match(url, @"/movie/[^/]+/[^/]+/(\d+)\.");
        if (match.Success) return int.TryParse(match.Groups[1].Value, out id);
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly Regex _invalid = new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);
    private static string Sanitize(string s) => _invalid.Replace(s, "_").Trim().TrimEnd('.');

    private static int ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return 0;
        if (date.Length >= 4 && int.TryParse(date[..4], out var y) && y > 1900 && y < 2200) return y;
        return 0;
    }

    private static IEnumerable<int> GetCategoryIds(List<int> filter, IEnumerable<int> all)
        => filter.Count > 0 ? filter : all;
}
