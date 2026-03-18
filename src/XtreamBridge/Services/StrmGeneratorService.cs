using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XtreamBridge.Models;
using XtreamBridge.Persistence;

namespace XtreamBridge.Services;

/// <summary>
/// Generates .strm and .nfo sidecar files under /output for Plex to pick up.
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
    private readonly AppSettings _settings;
    private readonly string _outputRoot;
    private readonly ILogger<StrmGeneratorService> _logger;

    public StrmGeneratorService(
        XtreamClient client,
        SyncStateRepository repo,
        IOptions<AppSettings> opts,
        IConfiguration configuration,
        ILogger<StrmGeneratorService> logger)
    {
        _client = client;
        _repo = repo;
        _settings = opts.Value;
        _outputRoot = configuration["Paths:Output"] ?? "/output";
        _logger = logger;
    }

    // ── Movies ────────────────────────────────────────────────────────────────

    public async Task SyncMoviesAsync(CancellationToken ct)
    {
        _logger.LogInformation("VOD sync started…");
        var state = await _repo.LoadAsync(ct);

        var categories = await _client.GetVodCategoriesAsync(ct) ?? new();
        var catMap = categories.ToDictionary(c => c.CategoryId, c => Sanitize(c.CategoryName));
        var filter = _settings.Sync.VodCategoryFilter;

        int added = 0;
        foreach (var catId in GetCategoryIds(filter, categories.Select(c => c.CategoryId)))
        {
            if (ct.IsCancellationRequested) break;
            var streams = await _client.GetVodStreamsByCategoryAsync(catId, ct) ?? new();
            var catName = catMap.TryGetValue(catId, out var cn) ? cn : "Uncategorized";

            foreach (var vod in streams)
            {
                if (ct.IsCancellationRequested) break;
                if (state.SyncedVodIds.Contains(vod.StreamId)) continue;

                XtreamVodInfoResponse? info = null;
                if (_settings.Sync.GenerateNfoFiles)
                    info = await _client.GetVodInfoAsync(vod.StreamId, ct);

                var details = info?.Info;
                var year    = ParseYear(details?.ReleaseDate);
                var title   = Sanitize(details?.Name ?? vod.Name);
                var fileName = year > 0 ? $"{title} ({year})" : title;
                var dir = Path.Combine(_outputRoot, "Movies", catName);
                Directory.CreateDirectory(dir);

                var url = _client.BuildVodStreamUrl(vod.StreamId, vod.ContainerExtension);
                WriteStrm(Path.Combine(dir, $"{fileName}.strm"), url);

                if (_settings.Sync.GenerateNfoFiles && details is not null)
                    WriteMovieNfo(Path.Combine(dir, $"{fileName}.nfo"), vod, details, year, title);

                state.SyncedVodIds.Add(vod.StreamId);
                added++;
                if (added % 50 == 0) await _repo.SaveAsync(state, ct);
            }
        }

        await _repo.SaveAsync(state, ct);
        _logger.LogInformation("VOD sync complete: {N} new items", added);
    }

    // ── Series ────────────────────────────────────────────────────────────────

    public async Task SyncSeriesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Series sync started…");
        var state = await _repo.LoadAsync(ct);

        var categories = await _client.GetSeriesCategoriesAsync(ct) ?? new();
        var catMap = categories.ToDictionary(c => c.CategoryId, c => Sanitize(c.CategoryName));
        var filter = _settings.Sync.SeriesCategoryFilter;

        int added = 0;
        foreach (var catId in GetCategoryIds(filter, categories.Select(c => c.CategoryId)))
        {
            if (ct.IsCancellationRequested) break;
            var seriesList = await _client.GetSeriesByCategoryAsync(catId, ct) ?? new();
            var catName = catMap.TryGetValue(catId, out var cn) ? cn : "Uncategorized";

            foreach (var series in seriesList)
            {
                if (ct.IsCancellationRequested) break;
                var key = series.SeriesId.ToString();
                if (state.SyncedSeriesIds.Contains(key)) continue;

                var info = await _client.GetSeriesStreamsBySeriesAsync(series.SeriesId, ct);
                if (info?.Episodes is null) continue;

                var seriesDir = Path.Combine(_outputRoot, "Series", catName, Sanitize(series.Name));
                Directory.CreateDirectory(seriesDir);

                if (_settings.Sync.GenerateNfoFiles)
                    WriteSeriesNfo(Path.Combine(seriesDir, "tvshow.nfo"), series, info);

                foreach (var (_, episodes) in info.Episodes)
                foreach (var ep in episodes)
                {
                    var seasonDir = Path.Combine(seriesDir, $"Season {ep.Season:D2}");
                    Directory.CreateDirectory(seasonDir);

                    var epFile = $"S{ep.Season:D2}E{ep.EpisodeNum:D2} - {Sanitize(ep.Title)}";
                    var url = _client.BuildSeriesStreamUrl(ep.EpisodeId, ep.ContainerExtension);
                    WriteStrm(Path.Combine(seasonDir, $"{epFile}.strm"), url);

                    if (_settings.Sync.GenerateNfoFiles)
                        WriteEpisodeNfo(Path.Combine(seasonDir, $"{epFile}.nfo"), ep, series.Name);
                }

                state.SyncedSeriesIds.Add(key);
                added++;
                if (added % 10 == 0) await _repo.SaveAsync(state, ct);
            }
        }

        await _repo.SaveAsync(state, ct);
        _logger.LogInformation("Series sync complete: {N} new shows", added);
    }

    // ── File writers ──────────────────────────────────────────────────────────

    private static void WriteStrm(string path, string url)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, url, Encoding.UTF8);
    }

    private void WriteMovieNfo(string path, XtreamVodStream vod, XtreamVodInfoDetails details, int year, string title)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>{XE(title)}</title>
  <originaltitle>{XE(details.OriginalName ?? title)}</originaltitle>
  <year>{year}</year>
  <rating>{details.Rating}</rating>
  <plot>{XE(details.Plot ?? "")}</plot>
  <genre>{XE(details.Genre ?? "")}</genre>
  <director>{XE(details.Director ?? "")}</director>
  <credits>{XE(details.Cast ?? "")}</credits>
  <thumb>{XE(details.MovieImage ?? "")}</thumb>
  <runtime>{(details.DurationSecs ?? 0) / 60}</runtime>
  {(details.TmdbId is not null ? $"<uniqueid type=\"tmdb\">{details.TmdbId}</uniqueid>" : "")}
</movie>
""", Encoding.UTF8);
    }

    private void WriteSeriesNfo(string path, XtreamSeries series, XtreamSeriesStreamInfo info)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<tvshow>
  <title>{XE(series.Name)}</title>
  <year>{ParseYear(null)}</year>
  <plot>{XE(series.Plot)}</plot>
  <genre>{XE(series.Genre)}</genre>
  <director>{XE(series.Director)}</director>
  <credits>{XE(series.Cast)}</credits>
  <thumb>{XE(series.Cover)}</thumb>
  {(info.Info.Tmdb is not null ? $"<uniqueid type=\"tmdb\">{info.Info.Tmdb}</uniqueid>" : "")}
</tvshow>
""", Encoding.UTF8);
    }

    private void WriteEpisodeNfo(string path, XtreamEpisode ep, string showName)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<episodedetails>
  <title>{XE(ep.Title)}</title>
  <showtitle>{XE(showName)}</showtitle>
  <season>{ep.Season}</season>
  <episode>{ep.EpisodeNum}</episode>
  <aired>{XE(ep.Info?.ReleaseDate ?? "")}</aired>
  <plot>{XE(ep.Info?.Plot ?? "")}</plot>
  <runtime>{(ep.Info?.DurationSecs ?? 0) / 60}</runtime>
  <rating>{ep.Info?.Rating}</rating>
  <thumb>{XE(ep.Info?.MovieImage ?? "")}</thumb>
</episodedetails>
""", Encoding.UTF8);
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

    private static string XE(string? s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";

    private static IEnumerable<int> GetCategoryIds(List<int> filter, IEnumerable<int> all)
        => filter.Count > 0 ? filter : all;
}
