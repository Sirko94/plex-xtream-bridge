using Microsoft.Extensions.Options;
using XtreamBridge.Models;
using XtreamBridge.Persistence;

namespace XtreamBridge.Services;

/// <summary>
/// Hosted background service — runs a full library refresh on schedule.
/// Replaces Jellyfin IScheduledTask with a standard .NET BackgroundService.
/// Also builds and caches the live-channel lineup used by DiscoveryController.
/// </summary>
public sealed class SyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppSettings> _opts;
    private readonly SyncStateRepository _repo;
    private readonly EpgService _epgService;
    private readonly ILogger<SyncBackgroundService> _logger;

    // In-memory lineup — rebuilt on each sync
    private List<HdHomeRunLineupEntry> _lineup = new();
    private readonly SemaphoreSlim _lineupLock = new(1, 1);

    // Manual trigger
    private readonly SemaphoreSlim _triggerSem = new(0, 1);

    public SyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppSettings> opts,
        SyncStateRepository repo,
        EpgService epgService,
        ILogger<SyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _opts = opts;
        _repo = repo;
        _epgService = epgService;
        _logger = logger;
    }

    public IReadOnlyList<HdHomeRunLineupEntry> GetLineup() => _lineup;

    /// <summary>Kick off an immediate sync from the Config UI.</summary>
    public Task TriggerNowAsync()
    {
        try { _triggerSem.Release(); } catch { /* already signalled */ }
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _opts.CurrentValue;
        _logger.LogInformation("SyncBackgroundService started. Schedule: {Type}",
            settings.Sync.ScheduleType);

        // Run immediately on startup
        await RunFullSyncAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            settings = _opts.CurrentValue;
            var delay = ComputeDelay(settings.Sync);
            _logger.LogInformation("Next sync in {Delay}", delay);

            // Wait for either the scheduled delay or a manual trigger
            try
            {
                await Task.WhenAny(
                    Task.Delay(delay, stoppingToken),
                    _triggerSem.WaitAsync(stoppingToken).ContinueWith(_ => { }, stoppingToken));
            }
            catch (OperationCanceledException) { break; }

            await RunFullSyncAsync(stoppingToken);
        }
    }

    // ── Full sync ─────────────────────────────────────────────────────────────

    private async Task RunFullSyncAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== Full library sync started ===");
        var settings = _opts.CurrentValue;

        // Skip sync entirely if credentials are not configured yet
        if (string.IsNullOrWhiteSpace(settings.Server.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.Server.Username))
        {
            _logger.LogWarning("Sync skipped — no Xtream credentials configured. Open the web UI to set them up.");
            return;
        }

        var state = await _repo.LoadAsync(ct);

        using var scope = _scopeFactory.CreateScope();
        var client  = scope.ServiceProvider.GetRequiredService<XtreamClient>();
        var strmGen = scope.ServiceProvider.GetRequiredService<StrmGeneratorService>();

        // 1. Verify auth
        var auth = await client.AuthenticateAsync(ct);
        if (auth?.UserInfo.Auth != 1)
        {
            _logger.LogError("Xtream authentication failed — check Server credentials");
            return;
        }
        _logger.LogInformation("Authenticated as {User} (status: {Status}, exp: {Exp})",
            auth.UserInfo.Username, auth.UserInfo.Status,
            auth.UserInfo.ExpDate?.ToShortDateString() ?? "N/A");

        // 2. Rebuild live lineup
        if (settings.Bridge.EnableLiveTv)
            await RebuildLineupAsync(client, state, settings, ct);

        // 3. STRM generation
        if (settings.Bridge.EnableStrmGeneration)
        {
            await strmGen.SyncMoviesAsync(ct);
            await strmGen.SyncSeriesAsync(ct);
        }

        // 4. Invalidate EPG cache
        _epgService.InvalidateCache();

        state.LastFullSync = DateTimeOffset.UtcNow;
        await _repo.SaveAsync(state, ct);
        _logger.LogInformation("=== Full library sync complete ===");
    }

    private async Task RebuildLineupAsync(
        XtreamClient client,
        SyncState state,
        AppSettings settings,
        CancellationToken ct)
    {
        var allStreams = await client.GetAllLiveStreamsAsync(ct) ?? new();
        var filter    = settings.Sync.LiveCategoryFilter;
        var overrides = ChannelOverrideParser.Parse(settings.Sync.ChannelOverrides);

        var entries = new List<HdHomeRunLineupEntry>();
        var channelNum = 1;

        foreach (var s in allStreams)
        {
            if (s.IsAdult && !settings.Sync.IncludeAdultChannels) continue;
            if (filter.Count > 0 && s.CategoryId.HasValue && !filter.Contains(s.CategoryId.Value)) continue;

            // Apply any override from config
            if (overrides.TryGetValue(s.StreamId, out var ovr))
                ChannelOverrideParser.Apply(s, ovr);

            var cleanName = ChannelNameCleaner.Clean(
                s.Name,
                settings.Sync.ChannelRemoveTerms,
                settings.Sync.EnableChannelNameCleaning);

            var guideNum = ovr?.Number?.ToString() ?? channelNum.ToString();
            var proxyUrl = $"{settings.Bridge.PublicBaseUrl.TrimEnd('/')}/stream/live/{s.StreamId}";

            entries.Add(new HdHomeRunLineupEntry
            {
                GuideNumber = guideNum,
                GuideName   = cleanName,
                StreamUrl   = proxyUrl,
                Hd          = 1
            });

            state.LiveChannelMap[s.StreamId] = channelNum;
            channelNum++;
        }

        await _lineupLock.WaitAsync(ct);
        try { _lineup = entries; }
        finally { _lineupLock.Release(); }

        _logger.LogInformation("Lineup rebuilt: {Count} live channels", entries.Count);
    }

    // ── Schedule helpers ──────────────────────────────────────────────────────

    private static TimeSpan ComputeDelay(SyncSettings sync)
    {
        if (string.Equals(sync.ScheduleType, "Daily", StringComparison.OrdinalIgnoreCase))
        {
            var now   = DateTime.Now;
            var next  = DateTime.Today.AddHours(sync.DailyHour).AddMinutes(sync.DailyMinute);
            if (next <= now) next = next.AddDays(1);
            return next - now;
        }
        return TimeSpan.FromHours(sync.RefreshIntervalHours);
    }
}
