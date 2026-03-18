namespace XtreamBridge.Models;

/// <summary>
/// Real-time sync progress — shared singleton updated by SyncBackgroundService,
/// read by ConfigController via GET /api/sync/status.
/// </summary>
public sealed class SyncProgress
{
    public bool IsRunning { get; set; }

    /// <summary>"idle" | "auth" | "live" | "movies" | "series" | "epg" | "done"</summary>
    public string Phase { get; set; } = "idle";

    public int TotalItems { get; set; }
    public int ItemsProcessed { get; set; }
    public string CurrentItem { get; set; } = string.Empty;

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // ── Last sync results ─────────────────────────────────────────────────────

    public int MoviesCreated { get; set; }
    public int MoviesSkipped { get; set; }
    public int MoviesRemoved { get; set; }

    public int SeriesCreated { get; set; }
    public int SeriesSkipped { get; set; }
    public int SeriesRemoved { get; set; }

    public int LiveChannels { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastSyncCompleted { get; set; }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>0–100 percentage, or null if total is unknown.</summary>
    public int? ProgressPercent =>
        TotalItems > 0 ? (int)Math.Round((double)ItemsProcessed / TotalItems * 100) : null;

    public void Reset()
    {
        IsRunning      = true;
        Phase          = "auth";
        TotalItems     = 0;
        ItemsProcessed = 0;
        CurrentItem    = string.Empty;
        StartedAt      = DateTimeOffset.UtcNow;
        CompletedAt    = null;
        LastError      = null;
    }

    public void Complete(string? error = null)
    {
        IsRunning   = false;
        Phase       = error is null ? "done" : "idle";
        CompletedAt = DateTimeOffset.UtcNow;
        LastError   = error;
        if (error is null) LastSyncCompleted = CompletedAt;
    }
}
