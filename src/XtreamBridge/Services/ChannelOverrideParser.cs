// Ported from firestaerter3/Jellyfin-Xtream-Library (GPL v3)

using XtreamBridge.Models;

namespace XtreamBridge.Services;

public sealed class ChannelOverride
{
    public int StreamId { get; set; }
    public string? Name { get; set; }
    public int? Number { get; set; }
    public string? LogoUrl { get; set; }
}

/// <summary>
/// Parses the ChannelOverrides config string.
/// Format: one entry per line → {stream_id}={Name}|{Number}|{LogoUrl}
/// Example: 1234=BBC One|1|http://cdn.example.com/bbc.png
/// </summary>
public static class ChannelOverrideParser
{
    private static readonly char[] LineSeps = ['\n', '\r'];

    public static Dictionary<int, ChannelOverride> Parse(string? overridesText)
    {
        var result = new Dictionary<int, ChannelOverride>();
        if (string.IsNullOrWhiteSpace(overridesText)) return result;

        foreach (var raw in overridesText.Split(LineSeps, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;

            if (!int.TryParse(line[..eq].Trim(), out var id)) continue;

            var parts = line[(eq + 1)..].Split('|');
            var ovr = new ChannelOverride { StreamId = id };

            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                ovr.Name = parts[0].Trim();
            if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var num))
                ovr.Number = num;
            if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
                ovr.LogoUrl = parts[2].Trim();

            result[id] = ovr;
        }
        return result;
    }

    public static void Apply(XtreamLiveStream channel, ChannelOverride? ovr)
    {
        if (ovr is null) return;
        if (!string.IsNullOrEmpty(ovr.Name))   channel.Name = ovr.Name;
        if (ovr.Number.HasValue)               channel.Num  = ovr.Number.Value;
        if (!string.IsNullOrEmpty(ovr.LogoUrl)) channel.StreamIcon = ovr.LogoUrl;
    }
}
