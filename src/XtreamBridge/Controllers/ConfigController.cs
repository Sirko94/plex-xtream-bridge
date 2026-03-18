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
///   GET  /api/config              — return current AppSettings
///   POST /api/config              — save AppSettings to /config/appsettings.override.json
///   POST /api/config/test         — test Xtream credentials (does NOT save)
///   GET  /api/status              — sync state + lineup count
///   GET  /api/sync/status         — real-time SyncProgress
///   POST /api/sync/trigger        — trigger incremental sync (returns 200 immediately)
///   POST /api/sync/trigger?full=true — trigger full sync
///   GET  /api/snapshots           — list snapshot files
///   DELETE /api/snapshots         — delete all snapshots (force full on next run)
///   GET  /api/categories/live     — list live categories
///   GET  /api/categories/vod      — list VOD categories
///   GET  /api/categories/series   — list series categories
/// </summary>
[ApiController]
[Route("api")]
public sealed class ConfigController : ControllerBase
{
    private readonly IOptionsMonitor<AppSettings> _opts;
    private readonly SyncStateRepository _repo;
    private readonly SyncBackgroundService _sync;
    private readonly XtreamClient _client;
    private readonly SnapshotService _snapshots;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigController> _logger;

    private static readonly JsonSerializerOptions _prettyJson = new() { WriteIndented = true };

    public ConfigController(
        IOptionsMonitor<AppSettings> opts,
        SyncStateRepository repo,
        SyncBackgroundService sync,
        XtreamClient client,
        SnapshotService snapshots,
        IConfiguration configuration,
        ILogger<ConfigController> logger)
    {
        _opts          = opts;
        _repo          = repo;
        _sync          = sync;
        _client        = client;
        _snapshots     = snapshots;
        _configuration = configuration;
        _logger        = logger;
    }

    // ── Config CRUD ───────────────────────────────────────────────────────────

    [HttpGet("config")]
    public IActionResult GetConfig() => Ok(_opts.CurrentValue);

    [HttpPost("config")]
    public async Task<IActionResult> SaveConfig([FromBody] AppSettings incoming, CancellationToken ct)
    {
        incoming.Sync.Validate();

        var configDir    = _configuration["Paths:Config"] ?? "/config";
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
            if (body.Contains("\"auth\":1") || body.Contains("\"auth\": 1"))
                return Ok(new { success = true, message = "Connexion réussie ✓" });

            return Ok(new { success = false, error = "Authentification échouée — vérifiez identifiants" });
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
        var state  = await _repo.LoadAsync(ct);
        var lineup = _sync.GetLineup();
        return Ok(new
        {
            lastSync     = state.LastFullSync == DateTimeOffset.MinValue ? (DateTimeOffset?)null : state.LastFullSync,
            liveChannels = lineup.Count,
            syncedMovies = state.SyncedVodIds.Count,
            syncedSeries = state.SyncedSeriesIds.Count,
            deviceId     = state.DeviceId,
            serverUtc    = DateTimeOffset.UtcNow
        });
    }

    // ── Sync progress (real-time) ─────────────────────────────────────────────

    [HttpGet("sync/status")]
    public IActionResult GetSyncStatus() => Ok(_sync.Progress);

    // ── Sync trigger ──────────────────────────────────────────────────────────

    [HttpPost("sync/trigger")]
    public IActionResult TriggerSync([FromQuery] bool full = false)
    {
        _ = _sync.TriggerNowAsync(fullSync: full);
        return Accepted(new { message = full ? "Synchronisation complète démarrée" : "Synchronisation démarrée" });
    }

    // ── Snapshots ─────────────────────────────────────────────────────────────

    [HttpGet("snapshots")]
    public IActionResult GetSnapshots()
    {
        var list = _snapshots.ListAsync();
        return Ok(list);
    }

    [HttpDelete("snapshots")]
    public IActionResult DeleteSnapshots()
    {
        _snapshots.DeleteAll();
        return Ok(new { message = "Snapshots supprimés — la prochaine sync sera complète" });
    }

    // ── Category lists ────────────────────────────────────────────────────────

    [HttpGet("categories/live")]
    public async Task<IActionResult> GetLiveCategories(CancellationToken ct)
    {
        var cats = await _client.GetLiveCategoriesAsync(ct);
        return cats is null ? StatusCode(502, "Impossible de joindre le fournisseur") : Ok(cats);
    }

    [HttpGet("categories/vod")]
    public async Task<IActionResult> GetVodCategories(CancellationToken ct)
    {
        var cats = await _client.GetVodCategoriesAsync(ct);
        return cats is null ? StatusCode(502, "Impossible de joindre le fournisseur") : Ok(cats);
    }

    [HttpGet("categories/series")]
    public async Task<IActionResult> GetSeriesCategories(CancellationToken ct)
    {
        var cats = await _client.GetSeriesCategoriesAsync(ct);
        return cats is null ? StatusCode(502, "Impossible de joindre le fournisseur") : Ok(cats);
    }
}
