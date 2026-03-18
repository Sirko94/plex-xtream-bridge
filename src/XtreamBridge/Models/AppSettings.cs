namespace XtreamBridge.Models;

/// <summary>
/// Root configuration — mapped from appsettings.json / env vars.
/// Env prefix: XTREAM__ (e.g. XTREAM__Server__BaseUrl)
/// </summary>
public sealed class AppSettings
{
    public XtreamServerSettings Server { get; set; } = new();
    public BridgeSettings Bridge { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
}

public sealed class XtreamServerSettings
{
    /// <summary>Base URL of the Xtream Codes provider (e.g. http://provider.com:8080)</summary>
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class BridgeSettings
{
    /// <summary>
    /// Public URL/IP as seen by Plex (e.g. http://192.168.1.100:8080).
    /// Used to build stream proxy URLs in lineup.json.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Friendly device name shown in Plex DVR wizard</summary>
    public string DeviceName { get; set; } = "XtreamBridge";

    /// <summary>Number of virtual tuners advertised to Plex</summary>
    public int TunerCount { get; set; } = 6;

    /// <summary>Stable 8-char hex device ID (auto-generated on first run if empty)</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Custom User-Agent header sent to the Xtream provider</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>Enable Live TV / HDHomeRun emulation</summary>
    public bool EnableLiveTv { get; set; } = true;

    /// <summary>Enable STRM + NFO file generation for Movies/Series</summary>
    public bool EnableStrmGeneration { get; set; } = true;
}

public sealed class SyncSettings
{
    // ── Schedule ──────────────────────────────────────────────────────────────
    /// <summary>"Interval" or "Daily"</summary>
    public string ScheduleType { get; set; } = "Interval";

    /// <summary>Interval between full library refreshes (hours). Used when ScheduleType=Interval.</summary>
    public int RefreshIntervalHours { get; set; } = 6;

    /// <summary>Daily sync hour (0-23). Used when ScheduleType=Daily.</summary>
    public int DailyHour { get; set; } = 3;

    /// <summary>Daily sync minute (0-59).</summary>
    public int DailyMinute { get; set; } = 0;

    // ── Category filters (empty = all) ─────────────────────────────────────
    public List<int> LiveCategoryFilter { get; set; } = new();
    public List<int> VodCategoryFilter { get; set; } = new();
    public List<int> SeriesCategoryFilter { get; set; } = new();

    // ── Content settings ──────────────────────────────────────────────────
    /// <summary>Generate .nfo sidecar files alongside .strm files</summary>
    public bool GenerateNfoFiles { get; set; } = true;

    /// <summary>Preferred stream extension for live channels (ts or m3u8)</summary>
    public string LiveStreamExtension { get; set; } = "ts";

    /// <summary>Include adult channels in live lineup</summary>
    public bool IncludeAdultChannels { get; set; } = false;

    /// <summary>Strip codec/country/quality tags from channel names</summary>
    public bool EnableChannelNameCleaning { get; set; } = true;

    /// <summary>Extra terms to remove from channel names (one per line)</summary>
    public string ChannelRemoveTerms { get; set; } = string.Empty;

    /// <summary>
    /// Manual channel overrides: one per line, format: {stream_id}={Name}|{Number}|{LogoUrl}
    /// e.g. 1234=BBC One|1|http://...
    /// </summary>
    public string ChannelOverrides { get; set; } = string.Empty;

    // ── Parallelism + rate limiting ───────────────────────────────────────
    public int SyncParallelism { get; set; } = 10;
    public int RequestDelayMs { get; set; } = 50;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;

    // ── Orphan cleanup ────────────────────────────────────────────────────
    /// <summary>Delete .strm files that no longer exist in the provider</summary>
    public bool CleanupOrphans { get; set; } = true;

    /// <summary>
    /// Safety threshold: if more than this fraction of files would be deleted, abort cleanup.
    /// Prevents a provider outage from wiping the library.
    /// </summary>
    public double OrphanSafetyThreshold { get; set; } = 0.20;

    public void Validate()
    {
        SyncParallelism = Math.Clamp(SyncParallelism, 1, 20);
        RefreshIntervalHours = Math.Max(RefreshIntervalHours, 1);
        DailyHour = Math.Clamp(DailyHour, 0, 23);
        DailyMinute = Math.Clamp(DailyMinute, 0, 59);
        RequestDelayMs = Math.Max(RequestDelayMs, 0);
        MaxRetries = Math.Clamp(MaxRetries, 0, 10);
        RetryDelayMs = Math.Max(RetryDelayMs, 0);
        OrphanSafetyThreshold = Math.Clamp(OrphanSafetyThreshold, 0.0, 1.0);
    }
}
