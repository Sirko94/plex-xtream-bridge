using Microsoft.AspNetCore.Mvc;
using XtreamBridge.Services;

namespace XtreamBridge.Controllers;

/// <summary>
/// Serves the XMLTV EPG guide data for Plex DVR.
///
/// In Plex DVR settings, set the "Electronic Program Guide" source to:
///   http://{bridge-host}/epg.xml
/// </summary>
[ApiController]
public sealed class EpgController : ControllerBase
{
    private readonly EpgService _epg;

    public EpgController(EpgService epg) => _epg = epg;

    [HttpGet("/epg.xml")]
    [ResponseCache(Duration = 3600)] // let Plex cache for 1h
    public async Task<IActionResult> GetEpg(CancellationToken ct)
    {
        var xml = await _epg.GetXmlTvAsync(ct);
        return Content(xml, "application/xml; charset=utf-8");
    }
}
