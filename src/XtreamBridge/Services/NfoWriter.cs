using System.Text;
using System.Xml;
using XtreamBridge.Models;

namespace XtreamBridge.Services;

/// <summary>
/// Writes .nfo sidecar files in Kodi/Plex-compatible XML format.
/// Uses XmlWriter for clean, safe output. No Jellyfin dependencies.
/// </summary>
public static class NfoWriter
{
    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Indent             = true,
        Encoding           = Encoding.UTF8,
        OmitXmlDeclaration = false,
        IndentChars        = "  "
    };

    // ── Movie NFO ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a movie NFO using data from the bulk get_vod_streams response.
    /// No get_vod_info call needed — all fields come from the bulk data.
    /// TMDb enrichment (plot, cast, etc.) is optional and uses the BestTmdbId already present.
    /// </summary>
    public static async Task WriteMovieNfoAsync(
        string path,
        XtreamVodStream vod,
        TmdbResult? tmdb = null)
    {
        if (File.Exists(path)) return;

        // Prefer TMDb data when available, fall back to bulk stream data
        var title         = tmdb?.Title         ?? vod.Name;
        var originalTitle = tmdb?.OriginalTitle  ?? vod.Name;
        var year          = ParseYear(tmdb?.ReleaseDate) > 0
                                ? ParseYear(tmdb?.ReleaseDate)
                                : ParseYear(vod.Year);
        var overview      = tmdb?.Overview       ?? vod.Plot     ?? string.Empty;
        var posterUrl     = tmdb?.PosterUrl       ?? vod.StreamIcon ?? string.Empty;
        var rating        = vod.Rating            ?? vod.Rating5Based ?? string.Empty;
        var genre         = vod.Genre             ?? string.Empty;
        var director      = tmdb?.Director        ?? vod.Director ?? string.Empty;
        var cast          = tmdb?.Cast            ?? vod.Cast     ?? string.Empty;
        var tmdbId        = tmdb is not null ? tmdb.TmdbId.ToString() : vod.BestTmdbId;

        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb))
        using (var w = XmlWriter.Create(sw, XmlSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("movie");

            w.WriteElementString("title", title);
            w.WriteElementString("originaltitle", originalTitle);
            if (year > 0) w.WriteElementString("year", year.ToString());
            if (!string.IsNullOrEmpty(rating))    w.WriteElementString("rating",    rating);
            if (!string.IsNullOrEmpty(overview))   w.WriteElementString("plot",      overview);
            if (!string.IsNullOrEmpty(genre))      w.WriteElementString("genre",     genre);
            if (!string.IsNullOrEmpty(director))   w.WriteElementString("director",  director);
            if (!string.IsNullOrEmpty(cast))       w.WriteElementString("credits",   cast);
            if (!string.IsNullOrEmpty(posterUrl))  w.WriteElementString("thumb",     posterUrl);
            if (!string.IsNullOrEmpty(vod.YoutubeTrailer)) w.WriteElementString("trailer", $"plugin://plugin.video.youtube/?action=play_video&videoid={vod.YoutubeTrailer}");

            if (!string.IsNullOrEmpty(tmdbId))
            {
                w.WriteStartElement("uniqueid");
                w.WriteAttributeString("type", "tmdb");
                w.WriteAttributeString("default", "true");
                w.WriteString(tmdbId);
                w.WriteEndElement();
            }

            // fileinfo / streamdetails (helps Plex identify the file)
            w.WriteStartElement("fileinfo");
            w.WriteStartElement("streamdetails");
            w.WriteStartElement("video");
            w.WriteElementString("codec", string.IsNullOrEmpty(vod.ContainerExtension) ? "h264" : vod.ContainerExtension);
            w.WriteEndElement(); // video
            w.WriteStartElement("audio");
            w.WriteElementString("codec", "aac");
            w.WriteEndElement(); // audio
            w.WriteEndElement(); // streamdetails
            w.WriteEndElement(); // fileinfo

            w.WriteEndElement(); // movie
            w.WriteEndDocument();
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
    }

    // ── Series (tvshow) NFO ───────────────────────────────────────────────────

    public static async Task WriteSeriesNfoAsync(
        string path,
        XtreamSeries series,
        TmdbResult? tmdb = null)
    {
        if (File.Exists(path)) return;

        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb))
        using (var w = XmlWriter.Create(sw, XmlSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("tvshow");

            w.WriteElementString("title", series.Name);
            if (tmdb?.Title is not null && tmdb.Title != series.Name)
                w.WriteElementString("originaltitle", tmdb.Title);

            if (!string.IsNullOrEmpty(series.Plot ?? tmdb?.Overview))
                w.WriteElementString("plot", series.Plot ?? tmdb?.Overview ?? string.Empty);

            if (!string.IsNullOrEmpty(series.Genre))
                w.WriteElementString("genre", series.Genre);

            if (!string.IsNullOrEmpty(series.Director))
                w.WriteElementString("director", series.Director);

            if (!string.IsNullOrEmpty(series.Cast))
                w.WriteElementString("credits", series.Cast);

            if (!string.IsNullOrEmpty(series.Cover))
                w.WriteElementString("thumb", series.Cover);

            if (series.Rating.HasValue)
                w.WriteElementString("rating", series.Rating.Value.ToString("0.##"));

            var tmdbIdVal = tmdb is not null ? tmdb.TmdbId.ToString() : null;
            if (!string.IsNullOrEmpty(tmdbIdVal))
            {
                w.WriteStartElement("uniqueid");
                w.WriteAttributeString("type", "tmdb");
                w.WriteAttributeString("default", "true");
                w.WriteString(tmdbIdVal);
                w.WriteEndElement();
            }

            w.WriteEndElement(); // tvshow
            w.WriteEndDocument();
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
    }

    // ── Episode NFO ───────────────────────────────────────────────────────────

    public static async Task WriteEpisodeNfoAsync(
        string path,
        XtreamEpisode ep,
        string seriesName)
    {
        if (File.Exists(path)) return;

        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb))
        using (var w = XmlWriter.Create(sw, XmlSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("episodedetails");

            w.WriteElementString("title", ep.Title);
            w.WriteElementString("showtitle", seriesName);
            w.WriteElementString("season", ep.Season.ToString());
            w.WriteElementString("episode", ep.EpisodeNum.ToString());

            if (!string.IsNullOrEmpty(ep.Info?.ReleaseDate))
                w.WriteElementString("aired", ep.Info.ReleaseDate);

            if (!string.IsNullOrEmpty(ep.Info?.Plot))
                w.WriteElementString("plot", ep.Info.Plot);

            var durationMin = (ep.Info?.DurationSecs ?? 0) / 60;
            if (durationMin > 0)
                w.WriteElementString("runtime", durationMin.ToString());

            if (ep.Info?.Rating.HasValue == true)
                w.WriteElementString("rating", ep.Info.Rating.Value.ToString("0.##"));

            if (!string.IsNullOrEmpty(ep.Info?.MovieImage))
                w.WriteElementString("thumb", ep.Info.MovieImage);

            // fileinfo / streamdetails
            w.WriteStartElement("fileinfo");
            w.WriteStartElement("streamdetails");
            w.WriteStartElement("video");
            w.WriteElementString("codec", string.IsNullOrEmpty(ep.ContainerExtension) ? "h264" : ep.ContainerExtension);
            w.WriteEndElement();
            w.WriteStartElement("audio");
            w.WriteElementString("codec", "aac");
            w.WriteEndElement();
            w.WriteEndElement(); // streamdetails
            w.WriteEndElement(); // fileinfo

            w.WriteEndElement(); // episodedetails
            w.WriteEndDocument();
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return 0;
        if (date.Length >= 4 && int.TryParse(date[..4], out var y) && y > 1900 && y < 2200) return y;
        return 0;
    }
}
