using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using XtreamBridge.Models;
using XtreamBridge.Services;

namespace XtreamBridge.Controllers;

/// <summary>
/// Credential-hiding stream proxy.
///
/// Plex (and the lineup.json) always talk to this controller.
/// This controller issues HTTP 302 redirects to the real Xtream stream URL,
/// keeping provider credentials entirely server-side.
///
/// Routes:
///   GET /stream/live/{id}        → live channel (ts/m3u8)
///   GET /stream/vod/{id}/{ext}   → VOD movie
///   GET /stream/series/{id}/{ext} → series episode
///
/// For scenarios where clients can't follow redirects (some Plex clients),
/// set PassThrough=true in config to pipe the bytes through instead.
/// </summary>
[ApiController]
[Route("stream")]
public sealed class StreamController : ControllerBase
{
    private readonly XtreamClient _client;
    private readonly SyncSettings _sync;
    private readonly ILogger<StreamController> _logger;

    public StreamController(
        XtreamClient client,
        IOptions<AppSettings> opts,
        ILogger<StreamController> logger)
    {
        _client = client;
        _sync = opts.Value.Sync;
        _logger = logger;
    }

    // ── Live ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Redirects to the real live stream URL.
    /// Plex DVR uses this URL from lineup.json to actually play the stream.
    /// </summary>
    [HttpGet("live/{streamId:int}")]
    public IActionResult LiveStream(int streamId, [FromQuery] string? ext)
    {
        var extension = ext ?? _sync.LiveStreamExtension;
        var url = _client.BuildLiveStreamUrl(streamId, extension);
        _logger.LogDebug("Redirecting live stream {Id} → {Ext}", streamId, extension);
        return Redirect(url); // HTTP 302
    }

    // ── VOD ───────────────────────────────────────────────────────────────────

    [HttpGet("vod/{streamId:int}/{ext}")]
    public IActionResult VodStream(int streamId, string ext)
    {
        var url = _client.BuildVodStreamUrl(streamId, ext);
        _logger.LogDebug("Redirecting VOD {Id}.{Ext}", streamId, ext);
        return Redirect(url);
    }

    // ── Series ────────────────────────────────────────────────────────────────

    [HttpGet("series/{episodeId:int}/{ext}")]
    public IActionResult SeriesStream(int episodeId, string ext)
    {
        var url = _client.BuildSeriesStreamUrl(episodeId, ext);
        _logger.LogDebug("Redirecting series episode {Id}.{Ext}", episodeId, ext);
        return Redirect(url);
    }

    // ── Status / Health ───────────────────────────────────────────────────────

    [HttpGet("/health")]
    public IActionResult Health() => Ok(new { status = "ok", utc = DateTimeOffset.UtcNow });
}
