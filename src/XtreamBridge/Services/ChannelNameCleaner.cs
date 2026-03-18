// Ported from firestaerter3/Jellyfin-Xtream-Library (GPL v3)

using System.Text.RegularExpressions;

namespace XtreamBridge.Services;

/// <summary>
/// Strips quality/codec/country tags from IPTV channel names.
/// e.g. "FR: TF1 | FHD" → "TF1"
/// </summary>
public static partial class ChannelNameCleaner
{
    private static readonly char[] LineSeparators = ['\n', '\r'];

    [GeneratedRegex(@"^(UK|US|DE|FR|ES|IT|NL|CA|AU|BE|CH|AT|PT|BR|MX|AR|PL|CZ|RO|HU|TR|GR|SE|NO|DK|FI|IE|IN|PK|ZA|AE|SA|EG|MA|NG|KE|JP|KR|CN|TW|HK|SG|MY|TH|VN|PH|ID|NZ|RU|UA|IL)\s*[:\|\-]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CountryPrefix();

    [GeneratedRegex(@"\s*\|\s*(HD|FHD|UHD|4K|SD|720p|1080p|2160p|HEVC|H\.?264|H\.?265)\s*\|?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTagSep();

    [GeneratedRegex(@"\s+(HD|FHD|UHD|4K|SD)$", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTagEnd();

    [GeneratedRegex(@"\s*(1080[pi]?|720[pi]?|4K|2160[pi]?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionSuffix();

    [GeneratedRegex(@"\s*(HEVC|H\.?264|H\.?265|AVC|MPEG-?[24]|VP9|AV1)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CodecInfo();

    [GeneratedRegex(@"\s*[\[\(](HD|FHD|UHD|4K|SD|HEVC|H\.?264|H\.?265|720p|1080p)[\]\)]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedTags();

    [GeneratedRegex(@"(^\s*\|\s*|\s*\|\s*$)")]
    private static partial Regex LeadingTrailingPipe();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpaces();

    public static string Clean(string name, string? customRemoveTerms = null, bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        if (!enabled) return name.Trim();

        var result = name;

        // Apply user-defined remove terms first
        if (!string.IsNullOrWhiteSpace(customRemoveTerms))
        {
            foreach (var term in ParseTerms(customRemoveTerms))
                if (!string.IsNullOrWhiteSpace(term))
                    result = result.Replace(term, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        result = CountryPrefix().Replace(result, string.Empty);
        result = QualityTagSep().Replace(result, " ");
        result = BracketedTags().Replace(result, string.Empty);
        result = CodecInfo().Replace(result, string.Empty);
        result = ResolutionSuffix().Replace(result, string.Empty);
        result = QualityTagEnd().Replace(result, string.Empty);
        result = LeadingTrailingPipe().Replace(result, string.Empty);
        result = MultipleSpaces().Replace(result, " ");
        result = result.Trim();

        return string.IsNullOrWhiteSpace(result) ? name.Trim() : result;
    }

    public static IEnumerable<string> ParseTerms(string? terms)
    {
        if (string.IsNullOrWhiteSpace(terms)) yield break;
        foreach (var line in terms.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (!string.IsNullOrEmpty(t)) yield return t;
        }
    }
}
