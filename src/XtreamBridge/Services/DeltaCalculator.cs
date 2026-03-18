using System.Security.Cryptography;
using System.Text;
using XtreamBridge.Models;

namespace XtreamBridge.Services;

/// <summary>
/// Pure static helper — compares current provider state vs last snapshot
/// and produces a SyncDelta listing what is new, modified, or removed.
/// No Jellyfin dependencies.
/// </summary>
public static class DeltaCalculator
{
    // ── Movie delta ───────────────────────────────────────────────────────────

    public static SyncDelta CalculateMovieDelta(
        IEnumerable<XtreamVodStream> currentStreams,
        ContentSnapshot? previousSnapshot)
    {
        var delta = new SyncDelta();
        var prev  = previousSnapshot?.Movies ?? new Dictionary<string, MovieSnapshot>();

        var currentMap = new Dictionary<string, MovieSnapshot>();

        foreach (var vod in currentStreams)
        {
            var key      = vod.StreamId.ToString();
            var checksum = ComputeMovieChecksum(vod);
            var snap = new MovieSnapshot
            {
                StreamId           = vod.StreamId,
                Name               = vod.Name,
                CategoryId         = vod.CategoryId ?? 0,
                ContainerExtension = vod.ContainerExtension,
                Checksum           = checksum
            };

            currentMap[key] = snap;

            if (!prev.TryGetValue(key, out var old))
            {
                delta.NewMovies.Add(snap);
            }
            else if (old.Checksum != checksum)
            {
                delta.ModifiedMovies.Add(snap);
            }
            // else: unchanged — skip
        }

        // Items in previous snapshot but not in current → removed
        foreach (var (key, old) in prev)
        {
            if (!currentMap.ContainsKey(key))
                delta.RemovedMovies.Add(old);
        }

        return delta;
    }

    // ── Series delta ──────────────────────────────────────────────────────────

    public static SyncDelta CalculateSeriesDelta(
        IEnumerable<XtreamSeries> currentSeries,
        ContentSnapshot? previousSnapshot)
    {
        var delta = new SyncDelta();
        var prev  = previousSnapshot?.Series ?? new Dictionary<string, SeriesSnapshot>();

        var currentMap = new Dictionary<string, SeriesSnapshot>();

        foreach (var series in currentSeries)
        {
            var key      = series.SeriesId.ToString();
            var checksum = ComputeSeriesChecksum(series);
            var snap = new SeriesSnapshot
            {
                SeriesId   = series.SeriesId,
                Name       = series.Name,
                CategoryId = series.CategoryId ?? 0,
                Checksum   = checksum
            };

            currentMap[key] = snap;

            if (!prev.TryGetValue(key, out var old))
            {
                delta.NewSeries.Add(snap);
            }
            else if (old.Checksum != checksum)
            {
                delta.ModifiedSeries.Add(snap);
            }
        }

        foreach (var (key, old) in prev)
        {
            if (!currentMap.ContainsKey(key))
                delta.RemovedSeries.Add(old);
        }

        return delta;
    }

    // ── Build snapshot from current streams ───────────────────────────────────

    public static Dictionary<string, MovieSnapshot> BuildMovieSnapshot(
        IEnumerable<XtreamVodStream> streams)
    {
        var map = new Dictionary<string, MovieSnapshot>();
        foreach (var vod in streams)
        {
            var key = vod.StreamId.ToString();
            map[key] = new MovieSnapshot
            {
                StreamId           = vod.StreamId,
                Name               = vod.Name,
                CategoryId         = vod.CategoryId ?? 0,
                ContainerExtension = vod.ContainerExtension,
                Checksum           = ComputeMovieChecksum(vod)
            };
        }
        return map;
    }

    public static Dictionary<string, SeriesSnapshot> BuildSeriesSnapshot(
        IEnumerable<XtreamSeries> series)
    {
        var map = new Dictionary<string, SeriesSnapshot>();
        foreach (var s in series)
        {
            var key = s.SeriesId.ToString();
            map[key] = new SeriesSnapshot
            {
                SeriesId   = s.SeriesId,
                Name       = s.Name,
                CategoryId = s.CategoryId ?? 0,
                Checksum   = ComputeSeriesChecksum(s)
            };
        }
        return map;
    }

    // ── Checksum helpers ──────────────────────────────────────────────────────

    public static string ComputeMovieChecksum(XtreamVodStream vod)
        => Md5($"{vod.StreamId}_{vod.Name}_{vod.ContainerExtension}");

    public static string ComputeSeriesChecksum(XtreamSeries series)
        => Md5($"{series.SeriesId}_{series.Name}");

    private static string Md5(string input)
    {
        var bytes  = Encoding.UTF8.GetBytes(input);
        var hash   = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
