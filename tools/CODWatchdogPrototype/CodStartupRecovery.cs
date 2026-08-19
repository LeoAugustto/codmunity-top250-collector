using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace CODWatchdogPrototype;

public interface ICodStartupRecoveryActions
{
    bool OpenValidatedBattleNetUri();
    PlayClickDeliveryResult TryClickPlay(ProcessSnapshot primaryWindow, TemplateMatchResult match);
    bool RequestGracefulCodClose(ProcessSnapshot cod);
    bool ForceTerminateCod(ProcessSnapshot cod);
}

public sealed class ActiveCodStartupRecoveryActions : ICodStartupRecoveryActions
{
    public bool OpenValidatedBattleNetUri()
    {
        try
        {
            Process.Start(new ProcessStartInfo(BattleNetLaunchUriKnowledge.Uri) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    public PlayClickDeliveryResult TryClickPlay(ProcessSnapshot primaryWindow, TemplateMatchResult match) => NativeRecoveryInput.TryDeliverPlayClick(primaryWindow, match);

    public bool RequestGracefulCodClose(ProcessSnapshot cod)
    {
        try
        {
            using var process = Process.GetProcessById(cod.Pid);
            return process.CloseMainWindow();
        }
        catch { return false; }
    }

    public bool ForceTerminateCod(ProcessSnapshot cod)
    {
        try
        {
            using var process = Process.GetProcessById(cod.Pid);
            process.Kill(entireProcessTree: false);
            return true;
        }
        catch { return false; }
    }
}

public sealed record CodStartupRecoveryInput(
    bool CodRunning,
    bool BattleNetAppRunning,
    bool CollectorRunning,
    CollectorActivityState CollectorActivity,
    ProcessSnapshot? Cod,
    ProcessSnapshot? PrimaryBattleNetWindow,
    VisualDetectionResult? VisualDetection,
    DateTimeOffset? LastCycleOkUtc,
    bool CollectionStaleConfirmed,
    bool ForceStaleForTest);

public sealed record RecoverySignal(string Name, string? Detail = null);
public sealed record RecoveryTickResult(WatchdogState? StateOverride, IReadOnlyList<RecoverySignal> Signals);

public sealed class CodStartupRecoveryController
{
    private const double ActivePlayThreshold = 0.95;
    private static readonly TimeSpan BattleNetTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CodStartTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CodCloseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CollectionResumeTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private readonly ICodStartupRecoveryActions actions;
    private RecoveryPhase phase;
    private RecoveryReason reason = RecoveryReason.CodMissing;
    private DateTimeOffset deadlineUtc;
    private DateTimeOffset closeDeadlineUtc;
    private DateTimeOffset? recoveredCodStartedUtc;
    private DateTimeOffset retryAfterUtc;
    private bool forceKillAttempted;
    private bool playNotConfidentReported;
    private bool dryRunStaleReported;
    private bool recoveryStartLogged;

    public CodStartupRecoveryController(ICodStartupRecoveryActions actions) => this.actions = actions;

    public RecoveryTickResult Tick(
        CodStartupRecoveryInput input,
        WatchdogState baseState,
        bool active,
        DateTimeOffset now,
        Func<CodStartupRecoveryInput> refresh,
        Action<DateTimeOffset> recordFailure)
    {
        var signals = new List<RecoverySignal>();
        if (baseState == WatchdogState.Cooldown) return new(WatchdogState.Cooldown, signals);
        if (!input.CollectorRunning || input.CollectorActivity == CollectorActivityState.Disabled)
        {
            Reset();
            return new(null, signals);
        }

        if (input.CodRunning && phase == RecoveryPhase.Idle && (baseState == WatchdogState.CheckingGameState || input.ForceStaleForTest))
            return BeginCollectionStaleRecovery(input, active, now, refresh, recordFailure, signals);

        if (phase == RecoveryPhase.DryRunStale)
        {
            if (!input.CodRunning || !input.CollectionStaleConfirmed) Reset();
            return new(WatchdogState.CheckingGameState, signals);
        }

        if (phase == RecoveryPhase.WaitingForCodClose)
            return ContinueCodClose(input, now, recordFailure, signals);

        if (input.CodRunning)
            return ContinueAfterCodAppeared(input, now, recordFailure, signals);

        if (phase == RecoveryPhase.Failed && now < retryAfterUtc) return new(WatchdogState.RecoveryFailed, signals);
        if (phase == RecoveryPhase.Failed) phase = RecoveryPhase.Idle;

        if (phase == RecoveryPhase.WaitingForCod)
        {
            if (now < deadlineUtc) return new(WatchdogState.WaitingForCod, signals);
            return Fail(now, recordFailure, signals, "CodDidNotStart");
        }

        return ContinueStartup(input, active, now, refresh, recordFailure, signals);
    }

    private RecoveryTickResult BeginCollectionStaleRecovery(
        CodStartupRecoveryInput input,
        bool active,
        DateTimeOffset now,
        Func<CodStartupRecoveryInput> refresh,
        Action<DateTimeOffset> recordFailure,
        List<RecoverySignal> signals)
    {
        // Re-read the exact health sources immediately before any destructive action.
        var fresh = refresh();
        if (!fresh.CollectorRunning || fresh.CollectorActivity == CollectorActivityState.Disabled || !fresh.CodRunning)
        {
            signals.Add(new("RECOVERY_CANCELLED", "reason=PreCloseStateChanged"));
            if (!fresh.CodRunning)
            {
                reason = RecoveryReason.CollectionStale;
                phase = RecoveryPhase.Idle;
                recoveryStartLogged = false;
                return new(WatchdogState.CodNotRunning, signals);
            }
            Reset();
            return new(null, signals);
        }
        if (!fresh.CollectionStaleConfirmed && !fresh.ForceStaleForTest)
        {
            signals.Add(new("RECOVERY_CANCELLED", "reason=FreshCycleDetected"));
            Reset();
            return new(WatchdogState.Healthy, signals);
        }

        reason = RecoveryReason.CollectionStale;
        if (!active)
        {
            if (!dryRunStaleReported)
            {
                signals.Add(new("RECOVERY_PLANNED", "reason=CollectionStale"));
                signals.Add(new("WATCHDOG_ACTION_PLANNED", "close_cod"));
                signals.Add(new("WATCHDOG_ACTION_PLANNED", "restart_cod_via_battlenet"));
                dryRunStaleReported = true;
            }
            phase = RecoveryPhase.DryRunStale;
            return new(WatchdogState.CheckingGameState, signals);
        }

        recoveryStartLogged = true;
        signals.Add(new("RECOVERY_START", "reason=CollectionStale"));
        signals.Add(new("COD_CLOSE_REQUESTED", "method=window"));
        actions.RequestGracefulCodClose(fresh.Cod!);
        phase = RecoveryPhase.WaitingForCodClose;
        deadlineUtc = now.Add(GracefulCloseTimeout);
        closeDeadlineUtc = now.Add(CodCloseTimeout);
        forceKillAttempted = false;
        return new(WatchdogState.RestartingCod, signals);
    }

    private RecoveryTickResult ContinueCodClose(CodStartupRecoveryInput input, DateTimeOffset now, Action<DateTimeOffset> recordFailure, List<RecoverySignal> signals)
    {
        if (!input.CodRunning)
        {
            signals.Add(new("COD_CLOSED"));
            phase = RecoveryPhase.Idle;
            recoveryStartLogged = true;
            return new(WatchdogState.CodNotRunning, signals);
        }
        if (!forceKillAttempted && now >= deadlineUtc)
        {
            forceKillAttempted = true;
            signals.Add(new("COD_CLOSE_FALLBACK", "method=terminate"));
            if (!actions.ForceTerminateCod(input.Cod!)) return Fail(now, recordFailure, signals, "CodDidNotExit");
        }
        if (now >= closeDeadlineUtc) return Fail(now, recordFailure, signals, "CodDidNotExit");
        return new(WatchdogState.RestartingCod, signals);
    }

    private RecoveryTickResult ContinueAfterCodAppeared(CodStartupRecoveryInput input, DateTimeOffset now, Action<DateTimeOffset> recordFailure, List<RecoverySignal> signals)
    {
        if (phase == RecoveryPhase.WaitingForCod)
        {
            recoveredCodStartedUtc = input.Cod?.StartedAtUtc ?? now;
            phase = RecoveryPhase.WaitingForData;
            deadlineUtc = now.Add(CollectionResumeTimeout);
            signals.Add(new("COD_STARTED", input.Cod is null ? null : $"pid={input.Cod.Pid}"));
            signals.Add(new("WAITING_FOR_DATA"));
        }
        if (phase == RecoveryPhase.WaitingForData)
        {
            if (recoveredCodStartedUtc is { } started && input.LastCycleOkUtc is { } cycle && cycle >= started)
            {
                signals.Add(new("RECOVERY_OK", reason == RecoveryReason.CollectionStale ? "reason=CollectionRestored" : null));
                Reset();
                return new(WatchdogState.Healthy, signals);
            }
            if (now >= deadlineUtc) return Fail(now, recordFailure, signals, "CollectionDidNotResume");
            return new(WatchdogState.WaitingForData, signals);
        }
        return new(null, signals);
    }

    private RecoveryTickResult ContinueStartup(
        CodStartupRecoveryInput input,
        bool active,
        DateTimeOffset now,
        Func<CodStartupRecoveryInput> refresh,
        Action<DateTimeOffset> recordFailure,
        List<RecoverySignal> signals)
    {
        if (!recoveryStartLogged)
        {
            signals.Add(new("RECOVERY_START", $"reason={(reason == RecoveryReason.CollectionStale ? "CollectionStale" : "CodMissing")}"));
            recoveryStartLogged = true;
        }
        if (!input.BattleNetAppRunning)
        {
            if (phase != RecoveryPhase.WaitingForBnet)
            {
                phase = RecoveryPhase.WaitingForBnet;
                deadlineUtc = now.Add(BattleNetTimeout);
                if (active)
                {
                    signals.Add(new("BNET_LAUNCH_REQUESTED", $"uri={BattleNetLaunchUriKnowledge.Uri}"));
                    if (!actions.OpenValidatedBattleNetUri()) return Fail(now, recordFailure, signals, "BattleNetDidNotOpen");
                }
                else signals.Add(new("WATCHDOG_ACTION_PLANNED", "open_battlenet_launcher_uri"));
            }
            if (now >= deadlineUtc) return Fail(now, recordFailure, signals, "BattleNetDidNotOpen");
            return new(WatchdogState.WaitingForBnet, signals);
        }
        if (input.PrimaryBattleNetWindow is null)
        {
            if (phase != RecoveryPhase.WaitingForBnet)
            {
                phase = RecoveryPhase.WaitingForBnet;
                deadlineUtc = now.Add(BattleNetTimeout);
                signals.Add(new("BNET_AVAILABLE"));
            }
            if (now >= deadlineUtc) return Fail(now, recordFailure, signals, "PrimaryWindowUnavailable");
            return new(WatchdogState.WaitingForBnet, signals);
        }
        if (phase is RecoveryPhase.Idle or RecoveryPhase.WaitingForBnet)
        {
            if (phase == RecoveryPhase.WaitingForBnet) signals.Add(new("BNET_AVAILABLE"));
            phase = RecoveryPhase.WaitingForPlay;
            deadlineUtc = now.Add(BattleNetTimeout);
            playNotConfidentReported = false;
            signals.Add(new("BNET_PRIMARY_READY"));
        }
        var match = input.VisualDetection?.Match;
        if (input.VisualDetection?.Status != VisualDetectionStatus.Match || match is null || match.Confidence < ActivePlayThreshold)
        {
            if (!playNotConfidentReported) signals.Add(new("RECOVERY_BLOCKED", "reason=PlayNotConfident"));
            playNotConfidentReported = true;
            if (now >= deadlineUtc) return Fail(now, recordFailure, signals, "PlayNotConfident");
            return new(WatchdogState.CodNotRunning, signals);
        }
        var fresh = refresh();
        var freshMatch = fresh.VisualDetection?.Match;
        if (fresh.CodRunning) return new(null, new[] { new RecoverySignal("RECOVERY_CANCELLED", "reason=CodAlreadyStarted") });
        if (!fresh.BattleNetAppRunning || fresh.PrimaryBattleNetWindow is null || fresh.VisualDetection?.Status != VisualDetectionStatus.Match || freshMatch is null || freshMatch.Confidence < ActivePlayThreshold)
            return new(WatchdogState.CodNotRunning, new[] { new RecoverySignal("RECOVERY_BLOCKED", "reason=PlayRevalidationFailed") });
        signals.Add(new("PLAY_CLICK_REQUESTED", $"x={freshMatch.CenterX} y={freshMatch.CenterY}"));
        if (active)
        {
            var delivery = actions.TryClickPlay(fresh.PrimaryBattleNetWindow, freshMatch);
            signals.AddRange(delivery.Signals);
            if (!delivery.Succeeded) return Fail(now, recordFailure, signals, "ClickFailed");
            signals.Add(new("PLAY_CLICK_SENT"));
        }
        else signals.Add(new("WATCHDOG_ACTION_PLANNED", "click_play"));
        phase = RecoveryPhase.WaitingForCod;
        deadlineUtc = now.Add(CodStartTimeout);
        signals.Add(new("WAITING_FOR_COD"));
        return new(WatchdogState.WaitingForCod, signals);
    }

    private RecoveryTickResult Fail(DateTimeOffset now, Action<DateTimeOffset> recordFailure, List<RecoverySignal> signals, string reason)
    {
        phase = RecoveryPhase.Failed;
        retryAfterUtc = now.Add(RetryDelay);
        recordFailure(now);
        signals.Add(new("RECOVERY_FAILED", $"reason={reason}"));
        return new(WatchdogState.RecoveryFailed, signals);
    }

    private void Reset()
    {
        phase = RecoveryPhase.Idle;
        reason = RecoveryReason.CodMissing;
        recoveredCodStartedUtc = null;
        deadlineUtc = default;
        closeDeadlineUtc = default;
        forceKillAttempted = false;
        playNotConfidentReported = false;
        dryRunStaleReported = false;
        recoveryStartLogged = false;
    }

    private enum RecoveryPhase { Idle, DryRunStale, WaitingForCodClose, WaitingForBnet, WaitingForPlay, WaitingForCod, WaitingForData, Failed }
    private enum RecoveryReason { CodMissing, CollectionStale }
}
