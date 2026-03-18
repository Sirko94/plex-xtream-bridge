using System.Text.Json;
using Microsoft.Extensions.Options;
using XtreamBridge.Models;

namespace XtreamBridge.Services;

/// <summary>Result from a TMDb metadata lookup.</summary>
public sealed class TmdbResult
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public int Year { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Director { get; set; }
    public string? Cast { get; set; }
    public string? Genre { get; set; }
}

/// <summary>
/// Fetches movie/series metadata from the TMDb v3 API.
/// Results are cached in-memory for 1 hour.
/// If no API key is configured, all methods return null (metadata enrichment is skipped).
/// </summary>
public sealed class MetadataService
{
    private const string TmdbBase    = "https://api.themoviedb.org/3";
    private const string TmdbImageBase = "https://image.tmdb.org/t/p/w500";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<AppSettings> _opts;
    private readonly ILogger<MetadataService> _logger;

    private readonly Dictionary<string, (TmdbResult? Result, DateTimeOffset Expires)> _cache = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public MetadataService(
        IHttpClientFactory factory,
        IOptionsMonitor<AppSettings> opts,
        ILogger<MetadataService> logger)
    {
        _http  = factory.CreateClient("tmdb");
        _opts  = opts;
        _logger = logger;
    }

    // ── Movie ─────────────────────────────────────────────────────────────────

    /// <summary>Fetch movie by known TMDb ID — faster than search, no ambiguity.</summary>
    public Task<TmdbResult?> GetMovieByIdAsync(int tmdbId, CancellationToken ct = default)
        => SearchAsync($"movie:id:{tmdbId}", () => FetchMovieByIdAsync(tmdbId, ct));

    public Task<TmdbResult?> SearchMovieAsync(string name, int year, CancellationToken ct = default)
        => SearchAsync($"movie:{name}:{year}", () => FetchMovieAsync(name, year, ct));

    // ── Series ────────────────────────────────────────────────────────────────

    /// <summary>Fetch series by known TMDb ID — faster than search, no ambiguity.</summary>
    public Task<TmdbResult?> GetSeriesByIdAsync(int tmdbId, CancellationToken ct = default)
        => SearchAsync($"series:id:{tmdbId}", () => FetchSeriesByIdAsync(tmdbId, ct));

    public Task<TmdbResult?> SearchSeriesAsync(string name, int year, CancellationToken ct = default)
        => SearchAsync($"series:{name}:{year}", () => FetchSeriesAsync(name, year, ct));

    // ── Core cache + fetch ────────────────────────────────────────────────────

    private async Task<TmdbResult?> SearchAsync(string cacheKey, Func<Task<TmdbResult?>> fetcher)
    {
        var apiKey = _opts.CurrentValue.Sync.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        await _cacheLock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow < cached.Expires)
                return cached.Result;
        }
        finally { _cacheLock.Release(); }

        TmdbResult? result = null;
        try
        {
            result = await fetcher();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TMDb lookup failed for key {Key}", cacheKey);
        }

        await _cacheLock.WaitAsync();
        try { _cache[cacheKey] = (result, DateTimeOffset.UtcNow.Add(CacheTtl)); }
        finally { _cacheLock.Release(); }

        return result;
    }

    private async Task<TmdbResult?> FetchMovieByIdAsync(int tmdbId, CancellationToken ct)
    {
        var apiKey = _opts.CurrentValue.Sync.TmdbApiKey;
        var url = $"{TmdbBase}/movie/{tmdbId}?api_key={Uri.EscapeDataString(apiKey)}&language=fr-FR";
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseMovieResult(doc.RootElement);
    }

    private async Task<TmdbResult?> FetchSeriesByIdAsync(int tmdbId, CancellationToken ct)
    {
        var apiKey = _opts.CurrentValue.Sync.TmdbApiKey;
        var url = $"{TmdbBase}/tv/{tmdbId}?api_key={Uri.EscapeDataString(apiKey)}&language=fr-FR";
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseSeriesResult(doc.RootElement);
    }

    private async Task<TmdbResult?> FetchMovieAsync(string name, int year, CancellationToken ct)
    {
        var apiKey = _opts.CurrentValue.Sync.TmdbApiKey;
        var url = $"{TmdbBase}/search/movie?api_key={Uri.EscapeDataString(apiKey)}" +
                  $"&query={Uri.EscapeDataString(name)}" +
                  (year > 0 ? $"&year={year}" : "");

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var results = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0) return null;

        var item = results[0];
        return ParseMovieResult(item);
    }

    private async Task<TmdbResult?> FetchSeriesAsync(string name, int year, CancellationToken ct)
    {
        var apiKey = _opts.CurrentValue.Sync.TmdbApiKey;
        var url = $"{TmdbBase}/search/tv?api_key={Uri.EscapeDataString(apiKey)}" +
                  $"&query={Uri.EscapeDataString(name)}" +
                  (year > 0 ? $"&first_air_date_year={year}" : "");

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var results = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0) return null;

        var item = results[0];
        return ParseSeriesResult(item);
    }

    // ── JSON parsing ──────────────────────────────────────────────────────────

    private static TmdbResult? ParseMovieResult(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idEl)) return null;
        var releaseDate = GetStr(item, "release_date");
        return new TmdbResult
        {
            TmdbId        = idEl.GetInt32(),
            Title         = GetStr(item, "title"),
            OriginalTitle = GetStr(item, "original_title"),
            Year          = ParseYear(releaseDate),
            Overview      = GetStr(item, "overview"),
            PosterUrl     = BuildPosterUrl(GetStr(item, "poster_path")),
            ReleaseDate   = releaseDate
        };
    }

    private static TmdbResult? ParseSeriesResult(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idEl)) return null;
        var firstAir = GetStr(item, "first_air_date");
        return new TmdbResult
        {
            TmdbId        = idEl.GetInt32(),
            Title         = GetStr(item, "name"),
            OriginalTitle = GetStr(item, "original_name"),
            Year          = ParseYear(firstAir),
            Overview      = GetStr(item, "overview"),
            PosterUrl     = BuildPosterUrl(GetStr(item, "poster_path")),
            ReleaseDate   = firstAir
        };
    }

    private static string GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static string? BuildPosterUrl(string? path)
        => string.IsNullOrEmpty(path) ? null : $"{TmdbImageBase}{path}";

    private static int ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4) return 0;
        return int.TryParse(date[..4], out var y) && y > 1900 ? y : 0;
    }
}
