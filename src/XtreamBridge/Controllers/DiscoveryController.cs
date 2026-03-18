using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using XtreamBridge.Models;
using XtreamBridge.Persistence;
using XtreamBridge.Services;

namespace XtreamBridge.Controllers;

/// <summary>
/// Implements the HDHomeRun device discovery protocol so Plex can find
/// this container as a network tuner via its DVR setup wizard.
///
/// Plex probes:
///   GET /discover.json       — device identity
///   GET /device.xml          — UPNP descriptor (optional, belt-and-suspenders)
///   GET /lineup_status.json  — scan status
///   GET /lineup.json         — full channel list
/// </summary>
[ApiController]
public sealed class DiscoveryController : ControllerBase
{
    private readonly AppSettings _settings;
    private readonly SyncStateRepository _repo;
    private readonly SyncBackgroundService _sync;
    private readonly ILogger<DiscoveryController> _logger;

    public DiscoveryController(
        IOptions<AppSettings> opts,
        SyncStateRepository repo,
        SyncBackgroundService sync,
        ILogger<DiscoveryController> logger)
    {
        _settings = opts.Value;
        _repo = repo;
        _sync = sync;
        _logger = logger;
    }

    // ── HDHomeRun discovery ───────────────────────────────────────────────────

    [HttpGet("/discover.json")]
    public async Task<IActionResult> Discover(CancellationToken ct)
    {
        var state = await _repo.LoadAsync(ct);
        var deviceId = EnsureDeviceId(state);

        var baseUrl = _settings.Bridge.PublicBaseUrl.TrimEnd('/');
        return Ok(new HdHomeRunDiscover
        {
            FriendlyName = _settings.Bridge.DeviceName,
            TunerCount = _settings.Bridge.TunerCount,
            DeviceId = deviceId,
            BaseUrl = baseUrl,
            LineupUrl = $"{baseUrl}/lineup.json"
        });
    }

    [HttpGet("/device.xml")]
    public IActionResult DeviceXml()
    {
        // Minimal UPNP descriptor — some Plex versions check this
        var xml = $"""
<?xml version="1.0" encoding="UTF-8"?>
<root xmlns="urn:schemas-upnp-org:device-1-0">
  <specVersion><major>1</major><minor>0</minor></specVersion>
  <device>
    <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
    <friendlyName>{_settings.Bridge.DeviceName}</friendlyName>
    <manufacturer>Silicondust</manufacturer>
    <modelName>HDTC-2US</modelName>
    <modelNumber>HDTC-2US</modelNumber>
    <serialNumber>{_settings.Bridge.DeviceId}</serialNumber>
  </device>
</root>
""";
        return Content(xml, "application/xml");
    }

    [HttpGet("/lineup_status.json")]
    public IActionResult LineupStatus() =>
        Ok(new HdHomeRunLineupStatus());

    [HttpGet("/lineup.json")]
    public IActionResult Lineup()
    {
        var entries = _sync.GetLineup();
        _logger.LogDebug("Serving lineup: {Count} channels", entries.Count);
        return Ok(entries);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string EnsureDeviceId(SyncState state)
    {
        if (!string.IsNullOrEmpty(_settings.Bridge.DeviceId))
            return _settings.Bridge.DeviceId;

        if (string.IsNullOrEmpty(state.DeviceId))
        {
            // Generate a stable random 8-char hex ID and persist it
            state.DeviceId = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8];
            _ = _repo.SaveAsync(state); // fire-and-forget
        }
        return state.DeviceId;
    }
}
