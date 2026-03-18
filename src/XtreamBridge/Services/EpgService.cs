using System.Text;
using System.Xml;
using Microsoft.Extensions.Options;
using XtreamBridge.Models;

namespace XtreamBridge.Services;

/// <summary>
/// Generates an XMLTV-compliant EPG document from Xtream Codes EPG data.
/// Plex points its DVR guide source at GET /epg.xml.
/// </summary>
public sealed class EpgService
{
    private readonly XtreamClient _client;
    private readonly IOptionsMonitor<AppSettings> _opts;
    private readonly ILogger<EpgService> _logger;

    private string? _cachedXml;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

    public EpgService(XtreamClient client, IOptionsMonitor<AppSettings> opts, ILogger<EpgService> logger)
    {
        _client = client;
        _opts = opts;
        _logger = logger;
    }

    public async Task<string> GetXmlTvAsync(CancellationToken ct = default)
    {
        if (_cachedXml is not null && DateTimeOffset.UtcNow < _cacheExpiry)
            return _cachedXml;

        _logger.LogInformation("Regenerating XMLTV EPG…");
        var xml = await BuildXmlTvAsync(ct);
        _cachedXml = xml;
        _cacheExpiry = DateTimeOffset.UtcNow.AddHours(_opts.CurrentValue.Sync.RefreshIntervalHours);
        return xml;
    }

    public void InvalidateCache() => _cacheExpiry = DateTimeOffset.MinValue;

    private async Task<string> BuildXmlTvAsync(CancellationToken ct)
    {
        var settings = _opts.CurrentValue;
        var liveStreams = await _client.GetAllLiveStreamsAsync(ct) ?? new();

        var sb = new StringBuilder();
        var xmlSettings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var sw = new StringWriter(sb);
        using var w = XmlWriter.Create(sw, xmlSettings);

        w.WriteStartDocument();
        w.WriteDocType("tv", null, "xmltv.dtd", null);
        w.WriteStartElement("tv");
        w.WriteAttributeString("source-info-name", "XtreamBridge");
        w.WriteAttributeString("generator-info-name", "XtreamBridge");

        // <channel> elements
        foreach (var s in liveStreams)
        {
            var chanId = s.EpgChannelId ?? s.StreamId.ToString();
            w.WriteStartElement("channel");
            w.WriteAttributeString("id", chanId);
            w.WriteElementString("display-name", s.Name);
            if (!string.IsNullOrEmpty(s.StreamIcon))
            {
                w.WriteStartElement("icon");
                w.WriteAttributeString("src", s.StreamIcon);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        // <programme> elements
        var withEpg = liveStreams
            .Where(s => !string.IsNullOrEmpty(s.EpgChannelId))
            .ToList();

        _logger.LogInformation("Fetching EPG for {N} channels…", withEpg.Count);
        foreach (var stream in withEpg)
        {
            if (ct.IsCancellationRequested) break;
            var epg = await _client.GetSimpleDataTableAsync(stream.StreamId, ct);
            if (epg is null) continue;

            foreach (var entry in epg.Listings)
            {
                try { WriteProgram(w, entry, stream); }
                catch { /* skip malformed entry */ }
            }
        }

        w.WriteEndElement(); // </tv>
        w.Flush();
        return sb.ToString();
    }

    private static void WriteProgram(XmlWriter w, XtreamEpgProgram entry, XtreamLiveStream stream)
    {
        var start = FormatXmlTvDate(entry.StartTimestamp);
        var stop  = FormatXmlTvDate(entry.StopTimestamp);
        if (start is null || stop is null) return;

        var chanId = stream.EpgChannelId ?? stream.StreamId.ToString();
        w.WriteStartElement("programme");
        w.WriteAttributeString("start",   start);
        w.WriteAttributeString("stop",    stop);
        w.WriteAttributeString("channel", chanId);

        // Title is already decoded by Base64Converter
        w.WriteStartElement("title");
        w.WriteAttributeString("lang", string.IsNullOrEmpty(entry.Language) ? "en" : entry.Language);
        w.WriteString(entry.Title);
        w.WriteEndElement();

        if (!string.IsNullOrEmpty(entry.Description))
        {
            w.WriteStartElement("desc");
            w.WriteAttributeString("lang", string.IsNullOrEmpty(entry.Language) ? "en" : entry.Language);
            w.WriteString(entry.Description);
            w.WriteEndElement();
        }

        w.WriteEndElement(); // </programme>
    }

    private static string? FormatXmlTvDate(long ts)
    {
        if (ts <= 0) return null;
        return DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.ToString("yyyyMMddHHmmss") + " +0000";
    }
}
