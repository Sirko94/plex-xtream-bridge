using Microsoft.Extensions.Options;
using XtreamBridge.Models;
using XtreamBridge.Persistence;
using XtreamBridge.Services;

namespace XtreamBridge.Services;

/// <summary>
/// Hosted background service — runs a full library refresh on schedule.
/// Exposes a SyncProgress singleton updated in real time and read by ConfigController.
/// Also builds and caches the live-channel lineup used by DiscoveryController.
/// </summary>
public sealed class SyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppSettings> _opts;
    private readonly SyncStateRepository _repo;
    private readonly EpgService _epgService;
    private readonly ILogger<SyncBackgroundService> _logger;

    // ── In-memory lineup ──────────────────────────────────────────────────────
    private List<HdHomeRunLineupEntry> _lineup = new();
    private readonly SemaphoreSlim _lineupLock = new(1, 1);

    // ── Sync trigger ──────────────────────────────────────────────────────────
    private readonly SemaphoreSlim _triggerSem = new(0, 1);
    private bool _triggerFullSync = false;

    // ── Progress ──────────────────────────────────────────────────────────────
    public SyncProgress Progress { get; } = new SyncProgress();

    public SyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppSettings> opts,
        SyncStateRepository repo,
        EpgService epgService,
        ILogger<SyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _opts         = opts;
        _repo         = repo;
        _epgService   = epgService;
        _logger       = logger;
    }

    public IReadOnlyList<HdHomeRunLineupEntry> GetLineup() => _lineup;

    /// <summary>Kick off an immediate sync from the Config UI.</summary>
    public Task TriggerNowAsync(bool fullSync = false)
    {
        _triggerFullSync = fullSync;
        try { _triggerSem.Release(); } catch { /* already signalled */ }
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _opts.CurrentValue;
        _logger.LogInformation("SyncBackgroundService started. Schedule: {Type}", settings.Sync.ScheduleType);

        // Run immediately on startup
        await RunFullSyncAsync(stoppingToken, forceFullSync: false);

        while (!stoppingToken.IsCancellationRequested)
        {
            settings = _opts.CurrentValue;
            var delay = ComputeDelay(settings.Sync);
            _logger.LogInformation("Next sync in {Delay}", delay);

            try
            {
                await Task.WhenAny(
                    Task.Delay(delay, stoppingToken),
                    _triggerSem.WaitAsync(stoppingToken).ContinueWith(_ => { }, stoppingToken));
            }
            catch (OperationCanceledException) { break; }

            var full = _triggerFullSync;
            _triggerFullSync = false;
            await RunFullSyncAsync(stoppingToken, forceFullSync: full);
        }
    }

    // ── Full sync ─────────────────────────────────────────────────────────────

    private async Task RunFullSyncAsync(CancellationToken ct, bool forceFullSync)
    {
        var settings = _opts.CurrentValue;

        if (string.IsNullOrWhiteSpace(settings.Server.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.Server.Username))
        {
            _logger.LogWarning("Sync skipped — no Xtream credentials configured. Open the web UI to set them up.");
            return;
        }

        // Mark progress as started
        Progress.Reset();

        _logger.LogInformation("=== Full library sync started (force={Force}) ===", forceFullSync);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var client  = scope.ServiceProvider.GetRequiredService<XtreamClient>();
            var strmGen = scope.ServiceProvider.GetRequiredService<StrmGeneratorService>();

            var state = await _repo.LoadAsync(ct);

            // 1. Verify auth
            Progress.Phase = "auth";
            XtreamPlayerApi? auth;
            try
            {
                auth = await client.AuthenticateAsync(ct);
            }
            catch (Exception ex)
            {
                var msg = $"Cannot reach Xtream provider at {settings.Server.BaseUrl}";
                _logger.LogError(ex, msg);
                Progress.Complete(msg);
                return;
            }

            if (auth is null)
            {
                var msg = $"Xtream provider returned empty response — check BaseUrl: {settings.Server.BaseUrl}";
                _logger.LogError(msg);
                Progress.Complete(msg);
                return;
            }

            if (auth.UserInfo.Auth != 1)
            {
                var msg = $"Authentication failed (auth={auth.UserInfo.Auth}, status={auth.UserInfo.Status})";
                _logger.LogError(msg);
                Progress.Complete(msg);
                return;
            }

            _logger.LogInformation("Authenticated as {User} (status: {Status}, exp: {Exp})",
                auth.UserInfo.Username, auth.UserInfo.Status,
                auth.UserInfo.ExpDate?.ToShortDateString() ?? "N/A");

            // 2. Rebuild live lineup
            if (settings.Bridge.EnableLiveTv)
            {
                Progress.Phase = "live";
                await RebuildLineupAsync(client, state, settings, ct);
            }

            // 3. STRM generation
            if (settings.Bridge.EnableStrmGeneration)
            {
                // Movies
                var (movCreated, movSkipped, movRemoved) =
                    await strmGen.SyncMoviesAsync(Progress, ct, forceFullSync);

                Progress.MoviesCreated = movCreated;
                Progress.MoviesSkipped = movSkipped;
                Progress.MoviesRemoved = movRemoved;

                // Series
                var (serCreated, serSkipped, serRemoved) =
                    await strmGen.SyncSeriesAsync(Progress, ct, forceFullSync);

                Progress.SeriesCreated = serCreated;
                Progress.SeriesSkipped = serSkipped;
                Progress.SeriesRemoved = serRemoved;
            }

            // 4. EPG
            Progress.Phase = "epg";
            _epgService.InvalidateCache();

            state.LastFullSync = DateTimeOffset.UtcNow;
            await _repo.SaveAsync(state, ct);

            _logger.LogInformation("=== Full library sync complete ===");
            Progress.Complete();
        }
        catch (OperationCanceledException)
        {
            Progress.Complete("Annulé");
            _logger.LogWarning("Sync cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed with unhandled exception");
            Progress.Complete(ex.Message);
        }
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

        var entries    = new List<HdHomeRunLineupEntry>();
        var channelNum = 1;

        foreach (var s in allStreams)
        {
            if (s.IsAdult && !settings.Sync.IncludeAdultChannels) continue;
            if (filter.Count > 0 && s.CategoryId.HasValue && !filter.Contains(s.CategoryId.Value)) continue;

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

        Progress.LiveChannels = entries.Count;
        _logger.LogInformation("Lineup rebuilt: {Count} live channels", entries.Count);
    }

    // ── Schedule helpers ──────────────────────────────────────────────────────

    private static TimeSpan ComputeDelay(SyncSettings sync)
    {
        if (string.Equals(sync.ScheduleType, "Daily", StringComparison.OrdinalIgnoreCase))
        {
            var now  = DateTime.Now;
            var next = DateTime.Today.AddHours(sync.DailyHour).AddMinutes(sync.DailyMinute);
            if (next <= now) next = next.AddDays(1);
            return next - now;
        }
        return TimeSpan.FromHours(sync.RefreshIntervalHours);
    }
}
