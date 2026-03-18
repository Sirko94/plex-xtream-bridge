// Ported from firestaerter3/Jellyfin-Xtream-Library (GPL v3)
// Removed all MediaBrowser/Jellyfin dependencies.

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using XtreamBridge.Converters;
using XtreamBridge.Models;

namespace XtreamBridge.Services;

/// <summary>
/// Typed HTTP client for the Xtream Codes player_api.php protocol.
/// Features: retry with exponential backoff, per-request delay, nullable-field tolerance.
/// </summary>
public sealed class XtreamClient
{
    private readonly HttpClient _http;
    private readonly ILogger<XtreamClient> _logger;
    private AppSettings _settings;

    // Retry config (overridable by tests)
    public int RequestDelayMs { get; set; } = 50;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;

    private readonly JsonSerializerSettings _json;

    public XtreamClient(HttpClient http, IOptionsMonitor<AppSettings> opts, ILogger<XtreamClient> logger)
    {
        _http = http;
        _logger = logger;
        _settings = opts.CurrentValue;
        opts.OnChange(s => _settings = s);

        // Null-tolerant deserialisation — mirrors the original plugin's error handler
        _json = new JsonSerializerSettings
        {
            Error = (_, args) =>
            {
                // Suppress errors on nullable properties (common with Xtream providers)
                if (args.ErrorContext.Member is string memberName)
                {
                    logger.LogDebug("JSON parse: ignoring null/type error on '{Member}'", memberName);
                    args.ErrorContext.Handled = true;
                }
            }
        };

        ConfigureUserAgent();
    }

    private void ConfigureUserAgent()
    {
        var ua = _settings.Bridge.UserAgent;
        _http.DefaultRequestHeaders.UserAgent.Clear();
        if (string.IsNullOrWhiteSpace(ua))
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(new ProductHeaderValue("XtreamBridge", ver)));
        }
        else
        {
            if (ProductInfoHeaderValue.TryParse(ua, out var parsed))
                _http.DefaultRequestHeaders.UserAgent.Add(parsed);
            else
                _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"({ua})"));
        }
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    public Task<XtreamPlayerApi?> AuthenticateAsync(CancellationToken ct = default)
        => QueryApiAsync<XtreamPlayerApi>(Url(), ct);

    // ── Live ──────────────────────────────────────────────────────────────────

    public Task<List<XtreamCategory>?> GetLiveCategoriesAsync(CancellationToken ct = default)
        => QueryApiAsync<List<XtreamCategory>>(Url("get_live_categories"), ct);

    public Task<List<XtreamLiveStream>?> GetLiveStreamsByCategoryAsync(int categoryId, CancellationToken ct = default)
        => QueryApiAsync<List<XtreamLiveStream>>(Url("get_live_streams", ("category_id", categoryId.ToString())), ct);

    public Task<List<XtreamLiveStream>?> GetAllLiveStreamsAsync(CancellationToken ct = default)
        => QueryApiAsync<List<XtreamLiveStream>>(Url("get_live_streams"), ct);

    // ── VOD ───────────────────────────────────────────────────────────────────

    public Task<List<XtreamCategory>?> GetVodCategoriesAsync(CancellationToken ct = default)
        => QueryApiAsync<List<XtreamCategory>>(Url("get_vod_categories"), ct);

    public Task<List<XtreamVodStream>?> GetVodStreamsByCategoryAsync(int categoryId, CancellationToken ct = default)
        => QueryApiAsync<List<XtreamVodStream>>(Url("get_vod_streams", ("category_id", categoryId.ToString())), ct);

    public async Task<XtreamVodInfoResponse?> GetVodInfoAsync(int vodId, CancellationToken ct = default)
    {
        try { return await QueryApiAsync<XtreamVodInfoResponse>(Url("get_vod_info", ("vod_id", vodId.ToString())), ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "VOD info fetch failed for {Id}", vodId); return null; }
    }

    // ── Series ────────────────────────────────────────────────────────────────

    public Task<List<XtreamCategory>?> GetSeriesCategoriesAsync(CancellationToken ct = default)
        => QueryApiAsync<List<XtreamCategory>>(Url("get_series_categories"), ct);

    public Task<List<XtreamSeries>?> GetSeriesByCategoryAsync(int categoryId, CancellationToken ct = default)
        => QueryApiAsync<List<XtreamSeries>>(Url("get_series", ("category_id", categoryId.ToString())), ct);

    public async Task<XtreamSeriesStreamInfo?> GetSeriesStreamsBySeriesAsync(int seriesId, CancellationToken ct = default)
    {
        try
        {
            var result = await QueryApiAsync<XtreamSeriesStreamInfo>(
                Url("get_series_info", ("series_id", seriesId.ToString())), ct);
            return result ?? new XtreamSeriesStreamInfo();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Series info fetch failed for {Id}", seriesId); return null; }
    }

    // ── EPG ───────────────────────────────────────────────────────────────────

    public async Task<XtreamEpgListings?> GetSimpleDataTableAsync(int streamId, CancellationToken ct = default)
    {
        try { return await QueryApiAsync<XtreamEpgListings>(Url("get_simple_data_table", ("stream_id", streamId.ToString())), ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "EPG fetch failed for stream {Id}", streamId); return null; }
    }

    // ── Stream URL builders (credential-hiding proxy pattern) ──────────────────

    public string BuildLiveStreamUrl(int streamId, string ext = "ts")
        => $"{_settings.Server.BaseUrl}/live/{E(_settings.Server.Username)}/{E(_settings.Server.Password)}/{streamId}.{ext}";

    public string BuildVodStreamUrl(int streamId, string ext = "mkv")
        => $"{_settings.Server.BaseUrl}/movie/{E(_settings.Server.Username)}/{E(_settings.Server.Password)}/{streamId}.{ext}";

    public string BuildSeriesStreamUrl(int episodeId, string ext = "mkv")
        => $"{_settings.Server.BaseUrl}/series/{E(_settings.Server.Username)}/{E(_settings.Server.Password)}/{episodeId}.{ext}";

    private static string E(string s) => Uri.EscapeDataString(s);

    // ── Core HTTP + retry ─────────────────────────────────────────────────────

    private async Task<T?> QueryApiAsync<T>(string url, CancellationToken ct)
    {
        var json = await GetStringWithRetryAsync(url, ct);

        var trimmed = json.TrimStart();

        // Detect HTML response — provider returned an error/redirect page instead of JSON
        if (trimmed.StartsWith('<'))
        {
            var preview = json.Length > 300 ? json[..300] : json;
            _logger.LogError(
                "Provider returned HTML instead of JSON — check BaseUrl and port number.\n" +
                "URL attempted: {Url}\nResponse preview: {Preview}", url, preview);
            throw new InvalidOperationException(
                "Provider returned HTML instead of JSON. Verify the BaseUrl includes the correct port (e.g. http://host:8080).");
        }

        // Guard: some providers return [] where {} is expected (e.g. SeriesStreamInfo)
        if (trimmed.StartsWith('[') && typeof(T) == typeof(XtreamSeriesStreamInfo))
        {
            _logger.LogWarning("Provider returned array for {Type} — returning empty object", typeof(T).Name);
            return (T)(object)new XtreamSeriesStreamInfo();
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(json, _json)
                ?? throw new JsonSerializationException($"Null result for {typeof(T).Name}");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON deserialisation failed for {Type}\nRaw response (first 300 chars): {Preview}",
                typeof(T).Name, json.Length > 300 ? json[..300] : json);
            throw;
        }
    }

    private async Task<string> GetStringWithRetryAsync(string url, CancellationToken ct)
    {
        int attempt = 0;
        int delay = RetryDelayMs;

        while (true)
        {
            try
            {
                _logger.LogDebug("GET {Url}", url);
                using var response = await _http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(ct);
                if (RequestDelayMs > 0) await Task.Delay(RequestDelayMs, ct);
                return content;
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode == HttpStatusCode.TooManyRequests ||
                      (ex.StatusCode.HasValue && (int)ex.StatusCode.Value >= 500))
            {
                if (attempt >= MaxRetries)
                {
                    _logger.LogError("HTTP {Code} after {N} retries: {Url}", (int?)ex.StatusCode, attempt, url);
                    throw;
                }
                _logger.LogWarning("HTTP {Code} — retry {A}/{M} in {D}ms: {Url}",
                    (int?)ex.StatusCode, attempt + 1, MaxRetries, delay, url);
                await Task.Delay(delay, ct);
                attempt++;
                delay *= 2; // exponential backoff
            }
        }
    }

    // ── URL builder ───────────────────────────────────────────────────────────

    private string Url(string? action = null, params (string Key, string Value)[] extra)
    {
        var creds = $"username={E(_settings.Server.Username)}&password={E(_settings.Server.Password)}";
        var query = action is null ? creds : $"{creds}&action={action}";
        foreach (var (k, v) in extra) query += $"&{k}={v}";
        return $"{_settings.Server.BaseUrl.TrimEnd('/')}/player_api.php?{query}";
    }
}
