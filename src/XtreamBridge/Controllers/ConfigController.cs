using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using XtreamBridge.Models;
using XtreamBridge.Persistence;
using XtreamBridge.Services;

namespace XtreamBridge.Controllers;

/// <summary>
/// REST API consumed by the Web UI.
///
///   GET  /api/config          — return current AppSettings
///   POST /api/config          — save AppSettings to /config/appsettings.override.json
///   POST /api/config/test     — test Xtream credentials (does NOT save)
///   GET  /api/status          — sync state + lineup count
///   POST /api/sync/trigger    — trigger an immediate sync
///   GET  /api/categories/live — list live categories (requires valid credentials)
///   GET  /api/categories/vod  — list VOD categories
///   GET  /api/categories/series — list series categories
/// </summary>
[ApiController]
[Route("api")]
public sealed class ConfigController : ControllerBase
{
    private readonly IOptionsMonitor<AppSettings> _opts;
    private readonly SyncStateRepository _repo;
    private readonly SyncBackgroundService _sync;
    private readonly XtreamClient _client;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigController> _logger;

    private static readonly JsonSerializerOptions _prettyJson = new() { WriteIndented = true };

    public ConfigController(
        IOptionsMonitor<AppSettings> opts,
        SyncStateRepository repo,
        SyncBackgroundService sync,
        XtreamClient client,
        IConfiguration configuration,
        ILogger<ConfigController> logger)
    {
        _opts = opts;
        _repo = repo;
        _sync = sync;
        _client = client;
        _configuration = configuration;
        _logger = logger;
    }

    // ── Config CRUD ───────────────────────────────────────────────────────────

    [HttpGet("config")]
    public IActionResult GetConfig() => Ok(_opts.CurrentValue);

    [HttpPost("config")]
    public async Task<IActionResult> SaveConfig([FromBody] AppSettings incoming, CancellationToken ct)
    {
        incoming.Sync.Validate();

        var configDir = _configuration["Paths:Config"] ?? "/config";
        var overridePath = Path.Combine(configDir, "appsettings.override.json");

        var doc = new
        {
            Server = incoming.Server,
            Bridge = incoming.Bridge,
            Sync   = incoming.Sync
        };

        var json = JsonSerializer.Serialize(doc, _prettyJson);
        await System.IO.File.WriteAllTextAsync(overridePath, json, ct);

        _logger.LogInformation("Config saved to {Path}", overridePath);
        return Ok(new { saved = true, path = overridePath });
    }

    // ── Connection test ───────────────────────────────────────────────────────

    [HttpPost("config/test")]
    public async Task<IActionResult> TestConnection([FromBody] XtreamServerSettings creds, CancellationToken ct)
    {
        // Temporarily build a client against the provided credentials
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url = $"{creds.BaseUrl.TrimEnd('/')}/player_api.php" +
                  $"?username={Uri.EscapeDataString(creds.Username)}" +
                  $"&password={Uri.EscapeDataString(creds.Password)}";
        try
        {
            var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return Ok(new { success = false, error = $"HTTP {(int)response.StatusCode}" });

            var body = await response.Content.ReadAsStringAsync(ct);
            // Auth=1 means success
            if (body.Contains("\"auth\":1") || body.Contains("\"auth\": 1"))
            {
                // Try to extract expiry info for display
                return Ok(new { success = true, message = "Connection successful ✓" });
            }
            return Ok(new { success = false, error = "Authentication failed — check username/password" });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var state = await _repo.LoadAsync(ct);
        var lineup = _sync.GetLineup();
        return Ok(new
        {
            lastSync        = state.LastFullSync == DateTimeOffset.MinValue ? (DateTimeOffset?)null : state.LastFullSync,
            liveChannels    = lineup.Count,
            syncedMovies    = state.SyncedVodIds.Count,
            syncedSeries    = state.SyncedSeriesIds.Count,
            deviceId        = state.DeviceId,
            serverUtc       = DateTimeOffset.UtcNow
        });
    }

    // ── Manual sync trigger ───────────────────────────────────────────────────

    [HttpPost("sync/trigger")]
    public IActionResult TriggerSync()
    {
        _ = _sync.TriggerNowAsync();
        return Accepted(new { message = "Sync started in background" });
    }

    // ── Category lists (for filter UI) ────────────────────────────────────────

    [HttpGet("categories/live")]
    public async Task<IActionResult> GetLiveCategories(CancellationToken ct)
    {
        var cats = await _client.GetLiveCategoriesAsync(ct);
        return cats is null ? StatusCode(502, "Could not reach provider") : Ok(cats);
    }

    [HttpGet("categories/vod")]
    public async Task<IActionResult> GetVodCategories(CancellationToken ct)
    {
        var cats = await _client.GetVodCategoriesAsync(ct);
        return cats is null ? StatusCode(502, "Could not reach provider") : Ok(cats);
    }

    [HttpGet("categories/series")]
    public async Task<IActionResult> GetSeriesCategories(CancellationToken ct)
    {
        var cats = await _client.GetSeriesCategoriesAsync(ct);
        return cats is null ? StatusCode(502, "Could not reach provider") : Ok(cats);
    }
}
