using System.Text;
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
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ConfigController> _logger;

    private static readonly JsonSerializerOptions _prettyJson = new() { WriteIndented = true };

    public ConfigController(
        IOptionsMonitor<AppSettings> opts,
        SyncStateRepository repo,
        SyncBackgroundService sync,
        XtreamClient client,
        SnapshotService snapshots,
        IConfiguration configuration,
        IHttpClientFactory httpFactory,
        ILogger<ConfigController> logger)
    {
        _opts          = opts;
        _repo          = repo;
        _httpFactory   = httpFactory;
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
        // Use the same typed HttpClient as XtreamClient (has VLC User-Agent + Accept headers)
        var http = _httpFactory.CreateClient(nameof(XtreamClient));
        var e    = (string s) => Uri.EscapeDataString(s);
        var base_ = creds.BaseUrl.TrimEnd('/');

        try
        {
            // 1. Try classic player_api.php GET
            var classicUrl = $"{base_}/player_api.php?username={e(creds.Username)}&password={e(creds.Password)}";
            var body = await TryGetAsync(http, classicUrl, ct);

            // 2. If HTML, try REST POST /account/information
            if (body is null || body.TrimStart().StartsWith('<'))
            {
                var restUrl = $"{base_}/account/information";
                var payload = System.Text.Json.JsonSerializer.Serialize(new { username = creds.Username, password = creds.Password });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(restUrl, content, ct);
                body = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
            }

            if (body is null || body.TrimStart().StartsWith('<'))
                return Ok(new { success = false, error = "Le serveur retourne du HTML — URL incorrecte ?" });

            // Parse auth field
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Handle both flat {user_info:{auth:1}} and wrapped {data:{user_info:{auth:1}}}
            if (root.TryGetProperty("data", out var data)) root = data;

            if (root.TryGetProperty("user_info", out var ui) &&
                ui.TryGetProperty("auth", out var authProp) &&
                authProp.GetInt32() == 1)
            {
                var status = ui.TryGetProperty("status", out var s) ? s.GetString() : "?";
                var exp    = ui.TryGetProperty("exp_date", out var ex) ? ex.ToString() : "N/A";
                return Ok(new { success = true, message = $"Connexion réussie ✓ (status: {status}, exp: {exp})" });
            }

            return Ok(new { success = false, error = "Authentification refusée — vérifiez identifiant/mot de passe" });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    private static async Task<string?> TryGetAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            var resp = await http.GetAsync(url, ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
        }
        catch { return null; }
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
