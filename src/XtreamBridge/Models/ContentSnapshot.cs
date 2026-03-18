namespace XtreamBridge.Models;

/// <summary>
/// A lightweight snapshot of a single VOD stream — used for delta calculation.
/// Checksum = MD5(stream_id + "_" + name + "_" + container_extension)
/// </summary>
public sealed class MovieSnapshot
{
    public int StreamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Year { get; set; }
    public int CategoryId { get; set; }
    public string ContainerExtension { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
}

/// <summary>
/// A lightweight snapshot of a series entry.
/// Checksum = MD5(series_id + "_" + name + "_" + last_modified)
/// </summary>
public sealed class SeriesSnapshot
{
    public int SeriesId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Year { get; set; }
    public int CategoryId { get; set; }
    public string Checksum { get; set; } = string.Empty;
}

/// <summary>
/// A full content snapshot persisted to disk.
/// Key = stream_id (as string) for JSON serialisation compatibility.
/// </summary>
public sealed class ContentSnapshot
{
    public DateTimeOffset TakenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>stream_id → snapshot</summary>
    public Dictionary<string, MovieSnapshot> Movies { get; set; } = new();

    /// <summary>series_id → snapshot</summary>
    public Dictionary<string, SeriesSnapshot> Series { get; set; } = new();
}

/// <summary>
/// The delta between the current provider state and the last snapshot.
/// </summary>
public sealed class SyncDelta
{
    public List<MovieSnapshot> NewMovies { get; set; } = new();
    public List<MovieSnapshot> ModifiedMovies { get; set; } = new();
    public List<MovieSnapshot> RemovedMovies { get; set; } = new();

    public List<SeriesSnapshot> NewSeries { get; set; } = new();
    public List<SeriesSnapshot> ModifiedSeries { get; set; } = new();
    public List<SeriesSnapshot> RemovedSeries { get; set; } = new();
}

/// <summary>Counts derived from a SyncDelta.</summary>
public sealed class DeltaStatistics
{
    public int NewMovies { get; set; }
    public int ModifiedMovies { get; set; }
    public int RemovedMovies { get; set; }
    public int NewSeries { get; set; }
    public int ModifiedSeries { get; set; }
    public int RemovedSeries { get; set; }

    public static DeltaStatistics FromDelta(SyncDelta d) => new()
    {
        NewMovies      = d.NewMovies.Count,
        ModifiedMovies = d.ModifiedMovies.Count,
        RemovedMovies  = d.RemovedMovies.Count,
        NewSeries      = d.NewSeries.Count,
        ModifiedSeries = d.ModifiedSeries.Count,
        RemovedSeries  = d.RemovedSeries.Count,
    };
}
