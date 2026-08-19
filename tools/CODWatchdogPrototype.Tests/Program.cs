using CODWatchdogPrototype;

var now = DateTimeOffset.UtcNow;
WatchdogObservation WithAge(int minutes) => new(true, true, false, false, false, false, now.AddMinutes(-20), now.AddMinutes(-minutes), false);
WatchdogObservation NoSessionData(int codRunningMinutes) => new(true, true, false, false, false, false, now.AddMinutes(-codRunningMinutes), null, false);

var machine = new WatchdogStateMachine();
Assert(machine.Evaluate(WithAge(2), now).Current == WatchdogState.Healthy, "age 2 minutes is healthy");
Assert(machine.Evaluate(WithAge(7), now).Current == WatchdogState.NoDataWarning, "age 7 minutes is warning");
Assert(machine.Evaluate(WithAge(11), now).Current == WatchdogState.NoDataConfirmed, "age 11 minutes is confirmed");
Assert(machine.Evaluate(WithAge(11), now).Current == WatchdogState.CheckingGameState, "repeated confirmed age advances to checking");
Assert(machine.Evaluate(WithAge(1), now).Current == WatchdogState.Healthy, "new local data recovers health");
Assert(new WatchdogStateMachine().Evaluate(NoSessionData(2), now).Current == WatchdogState.WaitingForData, "new COD session waits for first data");

var missingObservation = new WatchdogObservation(false, true, false, false, false, false, null, null, false);
var missing = new WatchdogStateMachine().Evaluate(missingObservation, now);
Assert(missing.Current == WatchdogState.CodNotRunning && missing.PlannedActions.Single() == "launch_cod_via_battlenet_unvalidated", "COD missing only plans unvalidated Battle.net launch");
Assert(new WatchdogStateMachine().Evaluate(new(true, true, true, false, false, false, now, now, false), now).Current == WatchdogState.BnetUpdateInProgress, "update waits");
Assert(new WatchdogStateMachine().Evaluate(WithAge(1) with { ApiSyncFailed = true }, now).Current == WatchdogState.Healthy, "API failure alone is healthy");

var cooldownMachine = new WatchdogStateMachine();
cooldownMachine.RecordRecoveryFailure(now); cooldownMachine.RecordRecoveryFailure(now); cooldownMachine.RecordRecoveryFailure(now);
Assert(cooldownMachine.Evaluate(WithAge(1), now).Current == WatchdogState.Cooldown, "repeated recovery failures enter cooldown");
Assert(cooldownMachine.Evaluate(missingObservation, now).Current == WatchdogState.Cooldown, "cooldown blocks launch plan");

var noWindow = Snapshot("Battle.net.exe", 7600, IntPtr.Zero, "");
var primary = Snapshot("Battle.net.exe", 15660, new IntPtr(0x100568), "Battle.net");
Assert(ProcessDiagnostics.FindPrimaryBattleNetProcess([noWindow, primary])?.Pid == 15660, "primary Battle.net prefers visible main window");
Assert(ProcessDiagnostics.TryGetExecutablePathForDiagnostics(-1) is null, "image-path query failure returns unknown without crash");

using var cancellation = new CancellationTokenSource();
var loopCalls = 0;
var loop = new WatchdogLoop(
    _ => { cancellation.Cancel(); return Task.CompletedTask; },
    () => now);
await loop.RunAsync(() => WithAge(1), new WatchdogStateMachine(), (_, _) => loopCalls++, cancellation.Token);
Assert(loopCalls == 1, "loop accepts fake delay and cancellation without real wait");

Console.WriteLine("PASS COD Watchdog Prototype offline state-machine and loop tests");

static ProcessSnapshot Snapshot(string name, int pid, IntPtr window, string title) => new(name, pid, null, null, null, title, string.Empty, window, null);
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
