// Ported from firestaerter3/Jellyfin-Xtream-Library (GPL v3)
// Removed all MediaBrowser/Jellyfin dependencies.

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using XtreamBridge.Models;

namespace XtreamBridge.Services;

/// <summary>
/// Typed HTTP client for the Xtream Codes protocol.
/// Supports both the classic player_api.php GET format and the newer
/// POST /account/information REST format, with automatic detection.
/// Features: retry with exponential backoff, per-request delay, nullable-field tolerance.
/// </summary>
public sealed class XtreamClient
{
    private readonly HttpClient _http;
    private readonly ILogger<XtreamClient> _logger;
    private AppSettings _settings;

    // API base URL — may be updated after auth from server_info (e.g. different port)
    private string _apiBaseUrl = string.Empty;

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
        _apiBaseUrl = opts.CurrentValue.Server.BaseUrl;
        opts.OnChange(s =>
        {
            _settings = s;
            _apiBaseUrl = s.Server.BaseUrl; // reset on settings change
        });

        // Null-tolerant deserialisation — mirrors the original plugin's error handler
        _json = new JsonSerializerSettings
        {
            Error = (_, args) =>
            {
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

    /// <summary>
    /// Authenticates with the Xtream provider.
    /// Tries classic player_api.php GET first; if the server returns HTML,
    /// falls back to the newer POST /account/information REST endpoint.
    /// On success, updates _apiBaseUrl from server_info if the server specifies
    /// a different host/port for subsequent API calls.
    /// </summary>
    public async Task<XtreamPlayerApi?> AuthenticateAsync(CancellationToken ct = default)
    {
        var classicUrl = PlayerApiUrl();
        string json;
        try
        {
            json = await GetStringWithRetryAsync(classicUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Classic player_api.php unreachable — trying REST /account/information");
            return await AuthenticateViaRestAsync(ct);
        }

        var trimmed = json.TrimStart();

        // Server returned an HTML page — try the newer REST endpoint
        if (trimmed.StartsWith('<'))
        {
            _logger.LogWarning(
                "player_api.php returned HTML (not JSON) — falling back to POST /account/information.\n" +
                "Response preview: {Preview}", json.Length > 200 ? json[..200] : json);
            return await AuthenticateViaRestAsync(ct);
        }

        var auth = JsonConvert.DeserializeObject<XtreamPlayerApi>(json, _json);
        if (auth?.ServerInfo != null)
            ApplyServerInfo(auth.ServerInfo);
        return auth;
    }

    private async Task<XtreamPlayerApi?> AuthenticateViaRestAsync(CancellationToken ct)
    {
        var restUrl = $"{_settings.Server.BaseUrl.TrimEnd('/')}/account/information";
        _logger.LogInformation("Trying REST auth: POST {Url}", restUrl);

        var payload = JsonConvert.SerializeObject(new
        {
            username = _settings.Server.Username,
            password = _settings.Server.Password
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(restUrl, content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        var trimmed = json.TrimStart();

        if (trimmed.StartsWith('<'))
        {
            var preview = json.Length > 300 ? json[..300] : json;
            _logger.LogError(
                "Both player_api.php and /account/information returned HTML.\n" +
                "Check that BaseUrl is correct: {Url}\nResponse preview: {Preview}",
                _settings.Server.BaseUrl, preview);
            throw new InvalidOperationException(
                "Provider returned HTML for both API formats. Check BaseUrl.");
        }

        // Handle new wrapper format: {"data": {"user_info": ..., "server_info": ...}, "error": null}
        XtreamPlayerApi? auth;
        if (trimmed.Contains("\"data\"") && trimmed.Contains("\"error\""))
        {
            var wrapper = JsonConvert.DeserializeObject<XtreamAccountInfoResponse>(json, _json);
            auth = wrapper?.Data;
            _logger.LogDebug("REST auth used new wrapper format (data/error)");
        }
        else
        {
            auth = JsonConvert.DeserializeObject<XtreamPlayerApi>(json, _json);
            _logger.LogDebug("REST auth used flat format (user_info/server_info)");
        }

        if (auth?.ServerInfo != null)
            ApplyServerInfo(auth.ServerInfo);

        return auth;
    }

    /// <summary>
    /// Updates _apiBaseUrl from server_info if the server specifies
    /// a different URL/port for player_api.php calls.
    /// Stream URLs (live/movie/series) always use the user-configured BaseUrl.
    /// </summary>
    private void ApplyServerInfo(XtreamServerInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Url) || string.IsNullOrWhiteSpace(info.Port))
            return;

        var protocol = string.IsNullOrWhiteSpace(info.ServerProtocol) ? "http" : info.ServerProtocol;
        var candidate = $"{protocol}://{info.Url}:{info.Port}";

        if (candidate != _apiBaseUrl)
        {
            _logger.LogInformation("API base URL updated from server_info: {Old} → {New}", _apiBaseUrl, candidate);
            _apiBaseUrl = candidate;
        }
    }

    // ── Live ──────────────────────────────────────────────────────────────────

    public Task<List<XtreamCategory>?> GetLiveCategoriesAsync(CancellationToken ct = default)
        => QueryApiAsync<List<XtreamCategory>>(PlayerApiUrl("get_live_categories"), ct);

    public Task<List<XtreamLiveStream>?> GetLiveStreamsByCategoryAsync(int categoryId, CancellationToken ct = default)
        => QueryApiAsync<List<XtreamLiveStream>>(PlayerApiUrl("get_live_streams", ("category_id", categoryId.ToString())), ct);

    public Task<List<XtreamLiveStream>?> GetAllLiveStreamsAsync(CancellationToken ct = default)
        => QueryApiAsync<List<XtreamLiveStream>>(PlayerApiUrl("get_live_streams"), ct);

    // ── VOD ───────────────────────────────────────────────────────────────────

    public Task<List<XtreamCategory>?> GetVodCategoriesAsync(CancellationToken ct = default)
        => QueryApiAsync<List<XtreamCategory>>(PlayerApiUrl("get_vod_categories"), ct);

    public Task<List<XtreamVodStream>?> GetVodStreamsByCategoryAsync(int categoryId, CancellationToken ct = default)
        => QueryApiAsync<List<XtreamVodStream>>(PlayerApiUrl("get_vod_streams", ("category_id", categoryId.ToString())), ct);

    public async Task<XtreamVodInfoResponse?> GetVodInfoAsync(int vodId, CancellationToken ct = default)
    {
        try { return await QueryApiAsync<XtreamVodInfoResponse>(PlayerApiUrl("get_vod_info", ("vod_id", vodId.ToString())), ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "VOD info fetch failed for {Id}", vodId); return null; }
    }

    // ── Series ────────────────────────────────────────────────────────────────

    public Task<List<XtreamCategory>?> GetSeriesCategoriesAsync(CancellationToken ct = default)
        => QueryApiAsync<List<XtreamCategory>>(PlayerApiUrl("get_series_categories"), ct);

    public Task<List<XtreamSeries>?> GetSeriesByCategoryAsync(int categoryId, CancellationToken ct = default)
        => QueryApiAsync<List<XtreamSeries>>(PlayerApiUrl("get_series", ("category_id", categoryId.ToString())), ct);

    public async Task<XtreamSeriesStreamInfo?> GetSeriesStreamsBySeriesAsync(int seriesId, CancellationToken ct = default)
    {
        try
        {
            var result = await QueryApiAsync<XtreamSeriesStreamInfo>(
                PlayerApiUrl("get_series_info", ("series_id", seriesId.ToString())), ct);
            return result ?? new XtreamSeriesStreamInfo();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Series info fetch failed for {Id}", seriesId); return null; }
    }

    // ── EPG ───────────────────────────────────────────────────────────────────

    public async Task<XtreamEpgListings?> GetSimpleDataTableAsync(int streamId, CancellationToken ct = default)
    {
        try { return await QueryApiAsync<XtreamEpgListings>(PlayerApiUrl("get_simple_data_table", ("stream_id", streamId.ToString())), ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "EPG fetch failed for stream {Id}", streamId); return null; }
    }

    // ── Stream URL builders — always use user-configured BaseUrl ───────────────

    public string BuildLiveStreamUrl(int streamId, string ext = "ts")
        => $"{_settings.Server.BaseUrl.TrimEnd('/')}/live/{E(_settings.Server.Username)}/{E(_settings.Server.Password)}/{streamId}.{ext}";

    public string BuildVodStreamUrl(int streamId, string ext = "mkv")
        => $"{_settings.Server.BaseUrl.TrimEnd('/')}/movie/{E(_settings.Server.Username)}/{E(_settings.Server.Password)}/{streamId}.{ext}";

    public string BuildSeriesStreamUrl(int episodeId, string ext = "mkv")
        => $"{_settings.Server.BaseUrl.TrimEnd('/')}/series/{E(_settings.Server.Username)}/{E(_settings.Server.Password)}/{episodeId}.{ext}";

    private static string E(string s) => Uri.EscapeDataString(s);

    // ── Core HTTP + retry ─────────────────────────────────────────────────────

    private async Task<T?> QueryApiAsync<T>(string url, CancellationToken ct)
    {
        var json = await GetStringWithRetryAsync(url, ct);
        var trimmed = json.TrimStart();

        // Detect HTML — server is returning an error/redirect page
        if (trimmed.StartsWith('<'))
        {
            var preview = json.Length > 300 ? json[..300] : json;
            _logger.LogError(
                "Provider returned HTML instead of JSON for {Type}.\nURL: {Url}\nPreview: {Preview}",
                typeof(T).Name, url, preview);
            throw new InvalidOperationException(
                $"Provider returned HTML for {typeof(T).Name}. Check BaseUrl/port.");
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
            _logger.LogError(ex, "JSON deserialisation failed for {Type}\nRaw (first 300): {Preview}",
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
                delay *= 2;
            }
        }
    }

    // ── URL builder (uses _apiBaseUrl which may differ from BaseUrl after auth) ──

    private string PlayerApiUrl(string? action = null, params (string Key, string Value)[] extra)
    {
        var base_ = string.IsNullOrWhiteSpace(_apiBaseUrl)
            ? _settings.Server.BaseUrl
            : _apiBaseUrl;
        var creds = $"username={E(_settings.Server.Username)}&password={E(_settings.Server.Password)}";
        var query = action is null ? creds : $"{creds}&action={action}";
        foreach (var (k, v) in extra) query += $"&{k}={v}";
        return $"{base_.TrimEnd('/')}/player_api.php?{query}";
    }
}
