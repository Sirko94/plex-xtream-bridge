namespace XtreamBridge.Models;

/// <summary>
/// Persisted to /config/sync_state.json.
/// Tracks what has already been synced to avoid redundant I/O on restarts.
/// </summary>
public sealed class SyncState
{
    public DateTimeOffset LastFullSync { get; set; } = DateTimeOffset.MinValue;

    /// <summary>stream_id → channel number</summary>
    public Dictionary<int, int> LiveChannelMap { get; set; } = new();

    /// <summary>VOD stream IDs already written as .strm files</summary>
    public HashSet<int> SyncedVodIds { get; set; } = new();

    /// <summary>Series IDs (as string keys) already fully written</summary>
    public HashSet<string> SyncedSeriesIds { get; set; } = new();

    /// <summary>Stable device ID for HDHomeRun emulation (generated once)</summary>
    public string DeviceId { get; set; } = string.Empty;
}
