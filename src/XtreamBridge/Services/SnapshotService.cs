using System.Text.Json;
using XtreamBridge.Models;

namespace XtreamBridge.Services;

/// <summary>
/// Persists rolling content snapshots to /config/snapshots/.
/// Keeps at most 3 snapshots. Uses atomic writes (temp file → rename).
/// </summary>
public sealed class SnapshotService
{
    private readonly string _snapshotDir;
    private readonly ILogger<SnapshotService> _logger;
    private const int MaxSnapshots = 3;

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public SnapshotService(IConfiguration configuration, ILogger<SnapshotService> logger)
    {
        _logger = logger;
        var configDir = configuration["Paths:Config"] ?? "/config";
        _snapshotDir = Path.Combine(configDir, "snapshots");
        Directory.CreateDirectory(_snapshotDir);
    }

    /// <summary>Load the most recent snapshot, or null if none exists.</summary>
    public async Task<ContentSnapshot?> LoadLatestAsync(CancellationToken ct = default)
    {
        var files = GetSnapshotFiles();
        if (files.Length == 0) return null;

        var latest = files[^1]; // sorted ascending, take last
        try
        {
            await using var stream = File.OpenRead(latest);
            return await JsonSerializer.DeserializeAsync<ContentSnapshot>(stream, _jsonOpts, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load snapshot {File} — treating as no snapshot", latest);
            return null;
        }
    }

    /// <summary>Save a new snapshot, pruning old ones if over the limit.</summary>
    public async Task SaveAsync(ContentSnapshot snapshot, CancellationToken ct = default)
    {
        var ts = snapshot.TakenAt.UtcDateTime.ToString("yyyyMMddHHmmss");
        var finalPath = Path.Combine(_snapshotDir, $"snapshot_{ts}.json");
        var tempPath  = finalPath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(snapshot, _jsonOpts);
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, finalPath, overwrite: true);
            _logger.LogInformation("Snapshot saved: {File} ({Movies} movies, {Series} series)",
                Path.GetFileName(finalPath), snapshot.Movies.Count, snapshot.Series.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save snapshot");
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
            return;
        }

        PruneOldSnapshots();
    }

    /// <summary>List all snapshot files with metadata.</summary>
    public SnapshotInfo[] ListAsync()
    {
        return GetSnapshotFiles()
            .Select(f =>
            {
                var fi = new FileInfo(f);
                return new SnapshotInfo
                {
                    FileName = fi.Name,
                    Date     = fi.LastWriteTimeUtc,
                    SizeBytes = fi.Length
                };
            })
            .OrderByDescending(x => x.Date)
            .ToArray();
    }

    /// <summary>Delete all snapshots (forces full sync on next run).</summary>
    public void DeleteAll()
    {
        foreach (var f in GetSnapshotFiles())
        {
            try { File.Delete(f); } catch { /* ignore */ }
        }
        _logger.LogInformation("All snapshots deleted — next sync will be a full sync");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string[] GetSnapshotFiles()
        => Directory.GetFiles(_snapshotDir, "snapshot_*.json")
                    .OrderBy(f => f)
                    .ToArray();

    private void PruneOldSnapshots()
    {
        var files = GetSnapshotFiles();
        while (files.Length > MaxSnapshots)
        {
            try { File.Delete(files[0]); } catch { /* ignore */ }
            files = GetSnapshotFiles();
        }
    }
}

public sealed class SnapshotInfo
{
    public string FileName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public long SizeBytes { get; set; }
}
