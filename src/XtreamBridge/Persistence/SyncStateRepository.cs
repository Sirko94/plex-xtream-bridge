using System.Text.Json;
using Microsoft.Extensions.Options;
using XtreamBridge.Models;

namespace XtreamBridge.Persistence;

/// <summary>
/// Simple JSON file–based persistence for sync state.
/// Stored at /config/sync_state.json inside the container.
/// Thread-safe for concurrent reads; writes are serialised via a SemaphoreSlim.
/// </summary>
public sealed class SyncStateRepository
{
    private readonly string _filePath;
    private readonly ILogger<SyncStateRepository> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public SyncStateRepository(IConfiguration configuration, ILogger<SyncStateRepository> logger)
    {
        _logger = logger;
        var configDir = configuration["Paths:Config"] ?? "/config";
        _filePath = Path.Combine(configDir, "sync_state.json");
        Directory.CreateDirectory(configDir);
    }

    public async Task<SyncState> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return new SyncState();

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var state = await JsonSerializer.DeserializeAsync<SyncState>(stream, _jsonOptions, ct);
            return state ?? new SyncState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read sync state — starting fresh");
            return new SyncState();
        }
    }

    public async Task SaveAsync(SyncState state, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(state, _jsonOptions);
            await File.WriteAllTextAsync(_filePath, json, ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}
