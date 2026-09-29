namespace CODMunityTop250Collector.Core;

// The watcher deliberately relies only on local collection health. Remote API delivery is
// represented in DatasetStatus.ApiSync but is never consulted here.
public enum StationWatcherState
{
    Disabled,
    WaitingForBattleNet,
    WaitingForCod,
    WaitingForData,
    LayoutUnsupported,
    Healthy,
    Offline,
    RecoveryCooldown,
    CrashLoopBackoff
}

public enum BattleNetLaunchProgress
{
    WaitingForBattleNet,
    PlayUnavailable,
    PlayClicked,
    Failed
}

public enum SafeModeDismissResult { NotPresent, Dismissed, Failed }
public static class SafeModeDialogRules
{
    public const int IdYes = 6;
    public const int IdNo = 7;
    public const int IdCancel = 2;

    // Ownerless native dialogs are valid: the direct dialog process identity is authoritative.
    public static bool IsCandidate(bool activeLaunchContext, int codProcessId, int dialogProcessId, string dialogClass, bool hasYes, bool hasNo, bool hasCancel) =>
        activeLaunchContext &&
        dialogProcessId == codProcessId &&
        string.Equals(dialogClass, "#32770", StringComparison.Ordinal) &&
        hasYes && hasNo && hasCancel;
}

public readonly record struct StationCodProcess(int Pid, DateTimeOffset StartedAtUtc);
public sealed record StationWatcherSnapshot(StationWatcherState State, string Detail = "");

public sealed record StationWatcherConfiguration(
    TimeSpan PollInterval,
    TimeSpan StaleAfter,
    TimeSpan LaunchRetryAfter,
    TimeSpan CodStartTimeout,
    TimeSpan DataResumeTimeout,
    TimeSpan RecoveryCooldown,
    TimeSpan CrashLoopWindow,
    TimeSpan CrashLoopBackoff,
    int CrashLoopLimit, TimeSpan CollectionAttemptGrace = default)
{
    public static StationWatcherConfiguration Default { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromMinutes(6),
        TimeSpan.FromMinutes(9),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(30),
        3,
        TimeSpan.FromSeconds(60));
}

public interface IStationWatcherPlatform
{
    StationCodProcess? FindCodProcess();
    void ResetLaunchAttempt();
    Task<BattleNetLaunchProgress> AdvanceCodLaunchAsync(CancellationToken token);
    Task<bool> IsInternetAvailableAsync(CancellationToken token);
    Task<bool> CloseCodForRecoveryAsync(int pid, CancellationToken token);
    Task<SafeModeDismissResult> DismissSafeModeNoAsync(CancellationToken token);
    Task<bool> BringCodToForegroundAsync(int pid, CancellationToken token);
}

public sealed class StationWatcher
{
    private readonly Func<CollectorSettings> settings;
    private readonly Func<CollectorSnapshot> collection;
    private readonly IStationWatcherPlatform platform;
    private readonly Action<string, string?> log;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly StationWatcherConfiguration configuration;
    private readonly Queue<DateTimeOffset> recoveryFailures = new();
    private bool lastEnabled;
    private bool launchActive;
    private bool recoveryActive;
    private bool offlineLogged;
    private bool localStaleLogged;
    private bool layoutUnsupportedLogged;
    private DateTimeOffset? deferredCycleStartedUtc;
    private bool codObservedDuringLaunch;
    private DateTimeOffset nextLaunchAttemptUtc;
    private DateTimeOffset? codStartDeadlineUtc;
    private DateTimeOffset? dataDeadlineUtc;
    private DateTimeOffset? retryAfterUtc;
    private DateTimeOffset? cooldownUntilUtc;
    private DateTimeOffset? crashLoopUntilUtc;
    private int? foregroundedPid;
    private StationWatcherSnapshot snapshot = new(StationWatcherState.Disabled);

    public StationWatcher(Func<CollectorSettings> settings, Func<CollectorSnapshot> collection, IStationWatcherPlatform platform, Action<string, string?> log, Func<DateTimeOffset>? utcNow = null, StationWatcherConfiguration? configuration = null)
    {
        this.settings = settings;
        this.collection = collection;
        this.platform = platform;
        this.log = log;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.configuration = configuration ?? StationWatcherConfiguration.Default;
    }

    public event Action<StationWatcherSnapshot>? Changed;
    public StationWatcherSnapshot Snapshot => snapshot;

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await EvaluateOnceAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log("WATCHER_RECOVERY_FAILED", $"reason=Unhandled type={ex.GetType().Name}");
                SetState(StationWatcherState.RecoveryCooldown, "Watcher action failed");
                retryAfterUtc = utcNow().Add(configuration.LaunchRetryAfter);
            }
            await Task.Delay(configuration.PollInterval, token);
        }
    }

    public async Task EvaluateOnceAsync(CancellationToken token)
    {
        var now = utcNow();
        var currentSettings = settings();
        var enabled = currentSettings.WatcherEnabled && currentSettings.CollectorEnabled;
        if (!enabled)
        {
            if (lastEnabled) log("WATCHER_DISABLED", null);
            lastEnabled = false;
            ResetAttempt();
            SetState(StationWatcherState.Disabled, currentSettings.WatcherEnabled ? "Collector disabled" : "Watcher disabled");
            return;
        }

        if (!lastEnabled) log("WATCHER_ENABLED", null);
        lastEnabled = true;
        var observedCod = platform.FindCodProcess();
        if (observedCod is { } safeModeCod)
        {
            var safeMode = await platform.DismissSafeModeNoAsync(token);
            if (safeMode == SafeModeDismissResult.Dismissed && launchActive && codStartDeadlineUtc is not null)
            {
                // The native dialog can appear after COD has already been observed. Keep the
                // original bounded launch alive rather than falling back to Battle.net Play.
                codObservedDuringLaunch = false;
                SetState(StationWatcherState.WaitingForCod, "Waiting for Call of Duty");
                return;
            }
        }

        if (crashLoopUntilUtc is { } backoffUntil && backoffUntil > now)
        {
            SetState(StationWatcherState.CrashLoopBackoff, "Automatic recovery paused");
            return;
        }
        if (crashLoopUntilUtc is not null) crashLoopUntilUtc = null;

        if (cooldownUntilUtc is { } cooldownUntil && cooldownUntil > now)
        {
            SetState(StationWatcherState.RecoveryCooldown, "Recovery cooldown");
            return;
        }
        if (cooldownUntilUtc is not null) cooldownUntilUtc = null;

        if (retryAfterUtc is { } retryAfter && retryAfter > now)
        {
            SetState(StationWatcherState.RecoveryCooldown, "Recovery retry pending");
            return;
        }
        retryAfterUtc = null;

        var cod = observedCod ?? platform.FindCodProcess();
        if (cod is null)
        {
            // A launch attempt remains active through a native Safe Mode prompt.  A transient
            // COD process exit must not cause another Battle.net Play click before its timeout.
            if (launchActive && codStartDeadlineUtc is { } startDeadline)
            {
                if (now >= startDeadline) Fail(now, "CodDidNotStart");
                else SetState(StationWatcherState.WaitingForCod, "Waiting for Call of Duty");
                return;
            }
            await AdvanceCodLaunchAsync(now, token);
            return;
        }

        if (launchActive && codStartDeadlineUtc is not null)
        {
            await ObserveLaunchCodAsync(cod.Value, now, token);
            return;
        }

        await MonitorHealthyCodAsync(cod.Value, now, token);
    }

    private async Task AdvanceCodLaunchAsync(DateTimeOffset now, CancellationToken token)
    {
        if (!launchActive)
        {
            launchActive = true;
            platform.ResetLaunchAttempt();
            nextLaunchAttemptUtc = now;
            log("WATCHER_WAITING_FOR_BNET", null);
        }
        if (now < nextLaunchAttemptUtc)
        {
            SetState(StationWatcherState.WaitingForBattleNet, "Waiting for Battle.net");
            return;
        }

        var progress = await platform.AdvanceCodLaunchAsync(token);
        switch (progress)
        {
            case BattleNetLaunchProgress.PlayClicked:
                log("WATCHER_PLAY_CLICK", null);
                codStartDeadlineUtc = now.Add(configuration.CodStartTimeout);
                SetState(StationWatcherState.WaitingForCod, "Waiting for Call of Duty");
                break;
            case BattleNetLaunchProgress.PlayUnavailable:
                log("WATCHER_PLAY_UNAVAILABLE", "retrySeconds=" + (int)configuration.LaunchRetryAfter.TotalSeconds);
                nextLaunchAttemptUtc = now.Add(configuration.LaunchRetryAfter);
                SetState(StationWatcherState.WaitingForBattleNet, "Waiting for Play");
                break;
            case BattleNetLaunchProgress.Failed:
                log("WATCHER_RECOVERY_FAILED", "reason=BattleNetLaunchUnavailable");
                nextLaunchAttemptUtc = now.Add(configuration.LaunchRetryAfter);
                SetState(StationWatcherState.WaitingForBattleNet, "Waiting for Battle.net");
                break;
            default:
                SetState(StationWatcherState.WaitingForBattleNet, "Waiting for Battle.net");
                break;
        }
    }

    private async Task ObserveLaunchCodAsync(StationCodProcess cod, DateTimeOffset now, CancellationToken token)
    {
        if (!codObservedDuringLaunch)
        {
            codObservedDuringLaunch = true;
            SetState(StationWatcherState.WaitingForCod, "Waiting for Call of Duty");
            return;
        }

        if (foregroundedPid != cod.Pid)
        {
            await platform.BringCodToForegroundAsync(cod.Pid, token);
            foregroundedPid = cod.Pid;
        }
        if (dataDeadlineUtc is null)
        {
            log("WATCHER_COD_STARTED", $"pid={cod.Pid}");
            dataDeadlineUtc = now.Add(configuration.DataResumeTimeout);
            SetState(StationWatcherState.WaitingForData, "Waiting for valid local collection");
            log("WATCHER_WAITING_FOR_DATA", null);
        }
        await WaitForLocalCollectionAsync(cod, now);
    }

    private Task WaitForLocalCollectionAsync(StationCodProcess cod, DateTimeOffset now)
    {
        if (HasLocalCollectionAfter(cod.StartedAtUtc))
        {
            launchActive = false;
            codStartDeadlineUtc = null;
            dataDeadlineUtc = null;
            codObservedDuringLaunch = false;
            if (recoveryActive)
            {
                recoveryActive = false;
                cooldownUntilUtc = now.Add(configuration.RecoveryCooldown);
                log("WATCHER_RECOVERY_OK", "reason=CollectionRestored");
                log("WATCHER_COOLDOWN", "seconds=" + (int)configuration.RecoveryCooldown.TotalSeconds);
                SetState(StationWatcherState.RecoveryCooldown, "Recovery cooldown");
            }
            else SetState(StationWatcherState.Healthy, "Local collection healthy");
            return Task.CompletedTask;
        }
        if (dataDeadlineUtc is { } deadline && now >= deadline) Fail(now, "CollectionDidNotResume");
        else SetState(StationWatcherState.WaitingForData, "Waiting for valid local collection");
        return Task.CompletedTask;
    }
    private async Task MonitorHealthyCodAsync(StationCodProcess cod, DateTimeOffset now, CancellationToken token)
    {
        var localSnapshot = collection();
        if (localSnapshot.CycleStatus is "UNSUPPORTED_BUILD" or "LAYOUT_DISCOVERY_FAILED" or "LAYOUT_DISCOVERY_AMBIGUOUS" or "LAYOUT_VALIDATION_PENDING")
        {
            if (!layoutUnsupportedLogged)
            {
                log("WATCHER_LAYOUT_UNSUPPORTED", "cycleStatus=" + localSnapshot.CycleStatus);
                layoutUnsupportedLogged = true;
            }
            SetState(StationWatcherState.LayoutUnsupported, "Layout support required; COD recovery paused");
            return;
        }
        layoutUnsupportedLogged = false;
        var lastLocalSuccess = localSnapshot.LastSuccess;
        var healthBaseline = lastLocalSuccess is { } last && last >= cod.StartedAtUtc ? last : cod.StartedAtUtc;
        if (now - healthBaseline < configuration.StaleAfter)
        {
            offlineLogged = false;
            localStaleLogged = false;
        deferredCycleStartedUtc = null;
            SetState(StationWatcherState.Healthy, "Local collection healthy");
            return;
        }

        var cycle = collection();
        var grace = configuration.CollectionAttemptGrace == default ? TimeSpan.FromSeconds(60) : configuration.CollectionAttemptGrace;
        if (cycle.CycleStatus == "RUNNING" && cycle.CycleStartedAt is { } cycleStarted && now - cycleStarted >= TimeSpan.Zero && now - cycleStarted < grace)
        {
            if (deferredCycleStartedUtc != cycleStarted)
            {
                log("WATCHER_STALE_DEFERRED", $"reason=CollectionInProgress ageSeconds={Math.Max(0, (int)(now - healthBaseline).TotalSeconds)} attemptAgeSeconds={Math.Max(0, (int)(now - cycleStarted).TotalSeconds)}");
                deferredCycleStartedUtc = cycleStarted;
            }
            SetState(StationWatcherState.WaitingForData, "Collection in progress");
            return;
        }
        deferredCycleStartedUtc = null;
        if (!localStaleLogged)
        {
            log("WATCHER_LOCAL_STALE", "ageSeconds=" + Math.Max(0, (int)(now - healthBaseline).TotalSeconds));
            localStaleLogged = true;
        }

        if (!await platform.IsInternetAvailableAsync(token))
        {
            if (!offlineLogged) log("WATCHER_INTERNET_OFFLINE", null);
            offlineLogged = true;
            SetState(StationWatcherState.Offline, "Internet unavailable; COD recovery deferred");
            return;
        }

        // Re-read only the local collection timestamp after the asynchronous connectivity check.
        // A fresh CYCLE_OK cancels recovery; API status is intentionally not considered.
        lastLocalSuccess = collection().LastSuccess;
        healthBaseline = lastLocalSuccess is { } refreshed && refreshed >= cod.StartedAtUtc ? refreshed : cod.StartedAtUtc;
        if (now - healthBaseline < configuration.StaleAfter)
        {
            SetState(StationWatcherState.Healthy, "Local collection healthy");
            return;
        }

        var recheckedSettings = settings();
        var recheckedCod = platform.FindCodProcess();
        if (!recheckedSettings.WatcherEnabled || !recheckedSettings.CollectorEnabled || recheckedCod is null || recheckedCod.Value.Pid != cod.Pid)
        {
            log("WATCHER_RECOVERY_CANCELLED", "reason=StateChangedBeforeClose");
            if (!recheckedSettings.WatcherEnabled || !recheckedSettings.CollectorEnabled) SetState(StationWatcherState.Disabled, "Watcher disabled");
            return;
        }        recoveryActive = true;
        log("WATCHER_INTERNET_ONLINE", null);
        log("WATCHER_RECOVERY_START", "reason=LocalDataStale");
        var closed = await platform.CloseCodForRecoveryAsync(cod.Pid, token);
        if (!closed) { Fail(now, "CodDidNotExit"); return; }
        launchActive = false;
        codStartDeadlineUtc = null;
        dataDeadlineUtc = null;
        foregroundedPid = null;
        await AdvanceCodLaunchAsync(now, token);
    }

    private bool HasLocalCollectionAfter(DateTimeOffset processStartedAt) => collection().LastSuccess is { } success && success >= processStartedAt;

    private void Fail(DateTimeOffset now, string reason)
    {
        log("WATCHER_RECOVERY_FAILED", "reason=" + reason);
        launchActive = false;
        codStartDeadlineUtc = null;
        dataDeadlineUtc = null;
        if (recoveryActive)
        {
            recoveryActive = false;
            RecordRecoveryFailure(now);
            if (crashLoopUntilUtc is { } until && until > now)
            {
                log("WATCHER_CRASH_LOOP_BACKOFF", "seconds=" + (int)configuration.CrashLoopBackoff.TotalSeconds);
                SetState(StationWatcherState.CrashLoopBackoff, "Automatic recovery paused");
                return;
            }
        }
        retryAfterUtc = now.Add(configuration.LaunchRetryAfter);
        SetState(StationWatcherState.RecoveryCooldown, "Recovery retry pending");
    }

    private void RecordRecoveryFailure(DateTimeOffset now)
    {
        while (recoveryFailures.TryPeek(out var oldest) && now - oldest > configuration.CrashLoopWindow) recoveryFailures.Dequeue();
        recoveryFailures.Enqueue(now);
        if (recoveryFailures.Count >= configuration.CrashLoopLimit) crashLoopUntilUtc = now.Add(configuration.CrashLoopBackoff);
    }

    private void ResetAttempt()
    {
        launchActive = false;
        recoveryActive = false;
        offlineLogged = false;
        codStartDeadlineUtc = null;
        localStaleLogged = false;
        deferredCycleStartedUtc = null;
        dataDeadlineUtc = null;
        retryAfterUtc = null;
        foregroundedPid = null;
        platform.ResetLaunchAttempt();
    }

    private void SetState(StationWatcherState state, string detail)
    {
        var next = new StationWatcherSnapshot(state, detail);
        if (next == snapshot) return;
        var previous = snapshot;
        snapshot = next;
        Changed?.Invoke(next);
        log("WATCHER_STATE", $"old={previous.State} new={state}");
    }
}
