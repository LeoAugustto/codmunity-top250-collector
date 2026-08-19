namespace CODWatchdogPrototype;

public enum WatchdogState
{
    Healthy, CodNotRunning, BnetNotRunning, CollectorNotRunning, CollectorDisabled,
    NoDataWarning, NoDataConfirmed, CheckingGameState,
    ServerDisconnectedRestart, ServerDisconnectedQuit,
    BnetUpdateInProgress, GameUpdateRequiresRestart,
    RestartingCod, WaitingForBnet, WaitingForCod, WaitingForData,
    RecoveryFailed, Cooldown, SystemRecoveryRequired
}

public enum CollectorActivityState { Unknown, Enabled, Disabled }
public enum SimulatedState { None, CodMissing, NoData, ServerDisconnectedQuit, ServerDisconnectedRestart, BnetUpdate }

public sealed record WatchdogConfiguration(
    TimeSpan WarningAfter,
    TimeSpan ConfirmAfter,
    TimeSpan StartupDataGrace,
    TimeSpan PollInterval,
    int MaxRecoveries,
    TimeSpan RecoveryWindow,
    TimeSpan Cooldown)
{
    public static readonly WatchdogConfiguration Default = new(
        WarningAfter: TimeSpan.FromMinutes(5),
        ConfirmAfter: TimeSpan.FromMinutes(10),
        StartupDataGrace: TimeSpan.FromMinutes(3),
        PollInterval: TimeSpan.FromSeconds(10),
        MaxRecoveries: 3,
        RecoveryWindow: TimeSpan.FromMinutes(30),
        Cooldown: TimeSpan.FromMinutes(30));
}

public sealed record WatchdogObservation(
    bool CodRunning,
    bool BattleNetAppRunning,
    bool BattleNetAgentRunning,
    bool CollectorRunning,
    CollectorActivityState CollectorActivity,
    bool BattleNetUpdateInProgress,
    bool GameUpdateRequiresRestart,
    bool ServerDisconnectedRestart,
    bool ServerDisconnectedQuit,
    DateTimeOffset? CodStartedAtUtc,
    DateTimeOffset? LastValidCollectionUtc,
    bool ApiSyncFailed);

public sealed record WatchdogTransition(WatchdogState Previous, WatchdogState Current, IReadOnlyList<string> PlannedActions);

public sealed class RecoveryBudget
{
    private readonly Queue<DateTimeOffset> failures = new();
    private DateTimeOffset? cooldownUntil;
    public void RecordFailure(DateTimeOffset now, WatchdogConfiguration configuration)
    {
        Trim(now, configuration);
        failures.Enqueue(now);
        if (failures.Count >= configuration.MaxRecoveries) cooldownUntil = now.Add(configuration.Cooldown);
    }
    public bool IsCoolingDown(DateTimeOffset now, WatchdogConfiguration configuration)
    {
        Trim(now, configuration);
        return cooldownUntil is { } until && until > now;
    }
    private void Trim(DateTimeOffset now, WatchdogConfiguration configuration)
    {
        while (failures.TryPeek(out var oldest) && now - oldest > configuration.RecoveryWindow) failures.Dequeue();
        if (cooldownUntil is { } until && until <= now) cooldownUntil = null;
    }
}

public sealed class WatchdogStateMachine
{
    private readonly WatchdogConfiguration configuration;
    private readonly RecoveryBudget budget;
    public WatchdogState State { get; private set; } = WatchdogState.Healthy;
    public WatchdogStateMachine(WatchdogConfiguration? configuration = null, RecoveryBudget? budget = null)
    {
        this.configuration = configuration ?? WatchdogConfiguration.Default;
        this.budget = budget ?? new RecoveryBudget();
    }
    public void RecordRecoveryFailure(DateTimeOffset now) => budget.RecordFailure(now, configuration);
    public WatchdogTransition Evaluate(WatchdogObservation observation, DateTimeOffset now)
    {
        var actions = new List<string>();
        var next = Resolve(observation, now, actions);
        var transition = new WatchdogTransition(State, next, actions);
        State = next;
        return transition;
    }
    private WatchdogState Resolve(WatchdogObservation observation, DateTimeOffset now, List<string> actions)
    {
        if (budget.IsCoolingDown(now, configuration)) return WatchdogState.Cooldown;
        // The Collector is a hard prerequisite for every recovery action. Never launch or restart COD when it is absent or disabled.
        if (!observation.CollectorRunning) return WatchdogState.CollectorNotRunning;
        if (observation.CollectorActivity == CollectorActivityState.Disabled) return WatchdogState.CollectorDisabled;
        if (!observation.CodRunning)
        {
            if (!observation.BattleNetAppRunning)
            {
                actions.Add("open_battlenet_launcher_uri");
                return WatchdogState.BnetNotRunning;
            }
            actions.Add("launch_cod_via_battlenet_unvalidated");
            return WatchdogState.CodNotRunning;
        }
        if (observation.BattleNetUpdateInProgress) return WatchdogState.BnetUpdateInProgress;
        if (observation.GameUpdateRequiresRestart) return WatchdogState.GameUpdateRequiresRestart;
        if (observation.ServerDisconnectedRestart) return WatchdogState.ServerDisconnectedRestart;
        if (observation.ServerDisconnectedQuit) return WatchdogState.ServerDisconnectedQuit;

        var hasCycleInCurrentCodSession = observation.LastValidCollectionUtc is { } cycle &&
            (observation.CodStartedAtUtc is null || cycle >= observation.CodStartedAtUtc.Value);
        if (!hasCycleInCurrentCodSession)
        {
            var runningFor = observation.CodStartedAtUtc is { } started ? now - started : TimeSpan.Zero;
            if (runningFor < configuration.StartupDataGrace) return WatchdogState.WaitingForData;
            return ResolveStale(runningFor);
        }
        return ResolveStale(now - observation.LastValidCollectionUtc!.Value);
    }
    private WatchdogState ResolveStale(TimeSpan age)
    {
        if (age <= configuration.WarningAfter) return WatchdogState.Healthy;
        if (age < configuration.ConfirmAfter) return WatchdogState.NoDataWarning;
        return State is WatchdogState.NoDataConfirmed or WatchdogState.CheckingGameState
            ? WatchdogState.CheckingGameState
            : WatchdogState.NoDataConfirmed;
    }
}

public sealed class WatchdogLoop
{
    private readonly Func<CancellationToken, Task> delayAsync;
    private readonly Func<DateTimeOffset> now;
    public WatchdogLoop(Func<CancellationToken, Task>? delayAsync = null, Func<DateTimeOffset>? now = null)
    {
        this.delayAsync = delayAsync ?? (token => Task.Delay(WatchdogConfiguration.Default.PollInterval, token));
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }
    public async Task RunAsync(Func<WatchdogObservation> observe, WatchdogStateMachine machine, Action<WatchdogTransition, WatchdogObservation> onTransition, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var observation = observe();
            onTransition(machine.Evaluate(observation, now()), observation);
            await delayAsync(cancellationToken);
        }
    }
}
