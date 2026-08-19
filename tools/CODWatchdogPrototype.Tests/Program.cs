using CODWatchdogPrototype;
using System.Windows;
using System.Drawing;

var now = DateTimeOffset.UtcNow;
WatchdogObservation WithAge(int minutes, bool collectorRunning = true) => new(true, true, true, collectorRunning, CollectorActivityState.Enabled, false, false, false, false, now.AddMinutes(-20), now.AddMinutes(-minutes), false);
WatchdogObservation NoSessionData(int codRunningMinutes) => new(true, true, true, true, CollectorActivityState.Enabled, false, false, false, false, now.AddMinutes(-codRunningMinutes), null, false);

var machine = new WatchdogStateMachine();
Assert(machine.Evaluate(WithAge(2), now).Current == WatchdogState.Healthy, "collector running plus fresh data is healthy");
Assert(machine.Evaluate(WithAge(7), now).Current == WatchdogState.NoDataWarning, "age 7 minutes is warning");
Assert(machine.Evaluate(WithAge(11), now).Current == WatchdogState.NoDataConfirmed, "age 11 minutes is confirmed");
Assert(machine.Evaluate(WithAge(11), now).Current == WatchdogState.CheckingGameState, "repeated confirmed age advances to checking");
Assert(machine.Evaluate(WithAge(1), now).Current == WatchdogState.Healthy, "new local data recovers health");
Assert(new WatchdogStateMachine().Evaluate(NoSessionData(2), now).Current == WatchdogState.WaitingForData, "new COD session waits for first data");

var collectorMissing = WithAge(30, collectorRunning: false);
var collectorRecoveryMachine = new WatchdogStateMachine();
Assert(collectorRecoveryMachine.Evaluate(collectorMissing, now).Current == WatchdogState.CollectorNotRunning, "missing collector overrides stale COD diagnosis");
Assert(collectorRecoveryMachine.Evaluate(collectorMissing, now).Current != WatchdogState.CheckingGameState, "missing collector never enters game checking");
Assert(collectorRecoveryMachine.Evaluate(WithAge(1), now).Current == WatchdogState.Healthy, "collector return plus new cycle is healthy");
Assert(new WatchdogStateMachine().Evaluate(WithAge(1) with { CollectorActivity = CollectorActivityState.Disabled }, now).Current == WatchdogState.CollectorDisabled, "disabled collector is distinct");

var codMissingWithLauncher = new WatchdogObservation(false, true, true, true, CollectorActivityState.Enabled, false, false, false, false, null, null, false);
var missing = new WatchdogStateMachine().Evaluate(codMissingWithLauncher, now);
Assert(missing.Current == WatchdogState.CodNotRunning && missing.PlannedActions.Single() == "launch_cod_via_battlenet_unvalidated", "COD missing only plans unvalidated Battle.net launch");
var bnetClosed = new WatchdogStateMachine().Evaluate(new(false, false, false, true, CollectorActivityState.Enabled, false, false, false, false, null, null, false), now);
Assert(bnetClosed.Current == WatchdogState.BnetNotRunning && bnetClosed.PlannedActions.Single() == "open_battlenet_launcher_uri", "closed Battle.net only plans launcher URI");
var agentOnly = new WatchdogObservation(false, false, true, true, CollectorActivityState.Enabled, false, false, false, false, null, null, false);
Assert(agentOnly.BattleNetAgentRunning && !agentOnly.BattleNetAppRunning, "Agent alone is not a Battle.net app");
Assert(new WatchdogStateMachine().Evaluate(agentOnly, now).Current == WatchdogState.BnetNotRunning, "Agent alone does not make launcher available");
Assert(new WatchdogStateMachine().Evaluate(WithAge(1) with { ApiSyncFailed = true }, now).Current == WatchdogState.Healthy, "API failure alone is healthy");

var noWindow = Snapshot("Battle.net.exe", 7600, IntPtr.Zero, false, "");
var primary = Snapshot("Battle.net.exe", 15660, new IntPtr(0x100568), true, "Battle.net");
Assert(ProcessDiagnostics.FindPrimaryBattleNetWindow([noWindow, primary])?.Pid == 15660, "primary Battle.net selects visible main window");
Assert(ProcessDiagnostics.FindPrimaryBattleNetWindow([noWindow]) is null, "Battle.net processes without a window still have no primary window");
Assert(ProcessDiagnostics.FindBattleNetProcesses([noWindow]).Count > 0, "Battle.net process set remains available without a primary window");
var primaryChangeMachine = new WatchdogStateMachine();
Assert(primaryChangeMachine.Evaluate(WithAge(1), now).Current == WatchdogState.Healthy, "initial state healthy");
Assert(primaryChangeMachine.Evaluate(WithAge(1), now).Current == WatchdogState.Healthy, "primary PID changes do not affect state");
Assert(ProcessDiagnostics.TryGetExecutablePathForDiagnostics(-1) is null, "image-path query failure returns unknown without crash");

Assert(new BattleNetUiAutomationInspector().Inspect(null).PrimaryWindowAvailable is false, "UI inspector handles missing primary window");
var fakePlay = new UiAutomationElementInfo("Play", "play", System.Windows.Automation.ControlType.Button.ProgrammaticName, true, false, new Rect(1, 2, 3, 4), ["InvokePattern"]);
Assert(BattleNetUiInspectionClassifier.Create(true, false, [fakePlay]).PlayCandidate, "enabled Play button with InvokePattern is a candidate");
var fakeNoPlay = new UiAutomationElementInfo("Playing Now", "status", System.Windows.Automation.ControlType.Text.ProgrammaticName, true, false, Rect.Empty, Array.Empty<string>());
Assert(!BattleNetUiInspectionClassifier.Create(true, false, [fakeNoPlay]).PlayCandidate, "tree without Play yields no candidate");

using var template = CreatePattern(12, 8);
using var targetWithTemplate = new Bitmap(60, 40);
using (var graphics = Graphics.FromImage(targetWithTemplate)) { graphics.Clear(Color.Black); graphics.DrawImageUnscaled(template, 7, 9); }
var exact = TemplateMatcher.FindBestMatch(targetWithTemplate, template);
Assert(exact.Confidence >= BattleNetVisualDetector.DefaultThreshold, "synthetic exact template exceeds threshold");
Assert(exact.X == 7 && exact.Y == 9 && exact.CenterX == 13 && exact.CenterY == 13, "match coordinates and center are window-relative");
using var targetWithoutTemplate = new Bitmap(60, 40);
using (var graphics = Graphics.FromImage(targetWithoutTemplate)) graphics.Clear(Color.FromArgb(32, 32, 32));
var absent = TemplateMatcher.FindBestMatch(targetWithoutTemplate, template);
Assert(absent.Confidence < BattleNetVisualDetector.DefaultThreshold, "synthetic absent template remains below threshold");
Assert(new BattleNetVisualDetector().DetectPlay(null).Status == VisualDetectionStatus.WindowUnavailable, "visual detector handles unavailable window");
Assert(!BattleNetTemplateCreator.IsCropSelectionAccepted(null), "template creator cancellation is recognized without writing files");
var prototypeDirectory = Path.Combine(FindProjectRoot(), "tools", "CODWatchdogPrototype");
var prototypeSource = Directory.EnumerateFiles(prototypeDirectory, "*.cs", SearchOption.AllDirectories)
    .Where(path => !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) && !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase))
    .Select(File.ReadAllText)
    .ToArray();
Assert(prototypeSource.All(source => !source.Contains("Start-Process cod.exe", StringComparison.Ordinal)), "prototype never launches cod.exe directly");
Assert(prototypeSource.Where(source => source.Contains("Process.Start(", StringComparison.Ordinal)).All(source => source.Contains("BattleNetLaunchUriKnowledge.Uri", StringComparison.Ordinal)), "only the validated Battle.net URI may be launched");

var cooldownMachine = new WatchdogStateMachine();
cooldownMachine.RecordRecoveryFailure(now); cooldownMachine.RecordRecoveryFailure(now); cooldownMachine.RecordRecoveryFailure(now);
Assert(cooldownMachine.Evaluate(WithAge(1), now).Current == WatchdogState.Cooldown, "repeated recovery failures enter cooldown");
Assert(cooldownMachine.Evaluate(codMissingWithLauncher, now).Current == WatchdogState.Cooldown, "cooldown blocks launch plan");

var recoveryPrimary = Snapshot("Battle.net.exe", 42, new IntPtr(0x1234), true, "Battle.net");
var playMatch = new TemplateMatchResult(0.991, 20, 30, 12, 10);
var highConfidencePlay = new VisualDetectionResult(VisualDetectionStatus.Match, playMatch, playMatch.Confidence);
CodStartupRecoveryInput RecoveryInput(bool codRunning = false, bool bnetRunning = true, bool collectorRunning = true, CollectorActivityState activity = CollectorActivityState.Enabled, VisualDetectionResult? visual = null, DateTimeOffset? cycle = null, bool stale = false, bool forceStale = false) => new(codRunning, bnetRunning, collectorRunning, activity, codRunning ? Snapshot("cod.exe", 99, IntPtr.Zero, false, "") with { StartedAtUtc = now } : null, bnetRunning ? recoveryPrimary : null, visual, cycle, stale, forceStale);
var freshStaleActions = new FakeRecoveryActions(); var freshStaleRecovery = new CodStartupRecoveryController(freshStaleActions);
freshStaleRecovery.Tick(RecoveryInput(codRunning: true, cycle: now.AddMinutes(-1)), WatchdogState.Healthy, true, now, () => RecoveryInput(codRunning: true, cycle: now.AddMinutes(-1)), _ => { });
Assert(freshStaleActions.CloseCount == 0, "fresh collection never closes COD");
var staleDryActions = new FakeRecoveryActions(); var staleDryRecovery = new CodStartupRecoveryController(staleDryActions);
var staleDry = staleDryRecovery.Tick(RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), WatchdogState.CheckingGameState, false, now, () => RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), _ => { });
Assert(staleDryActions.CloseCount == 0 && staleDry.Signals.Any(signal => signal.Name == "RECOVERY_PLANNED") && staleDry.Signals.Count(signal => signal.Name == "WATCHDOG_ACTION_PLANNED") == 2, "stale dry-run only plans close and Battle.net restart");
var staleActiveActions = new FakeRecoveryActions(); var staleActiveRecovery = new CodStartupRecoveryController(staleActiveActions);
var staleActive = staleActiveRecovery.Tick(RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), WatchdogState.CheckingGameState, true, now, () => RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), _ => { });
Assert(staleActive.StateOverride == WatchdogState.RestartingCod && staleActiveActions.CloseCount == 1, "confirmed stale collection requests graceful COD close in active mode");
var confirmedActions = new FakeRecoveryActions(); var confirmedRecovery = new CodStartupRecoveryController(confirmedActions);
confirmedRecovery.Tick(RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), WatchdogState.NoDataConfirmed, true, now, () => RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), _ => { });
Assert(confirmedActions.CloseCount == 0, "NoDataConfirmed never closes COD on the threshold evaluation");
var restoredActions = new FakeRecoveryActions(); var restoredRecovery = new CodStartupRecoveryController(restoredActions);
restoredRecovery.Tick(RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), WatchdogState.CheckingGameState, true, now, () => RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), _ => { });
var codClosed = restoredRecovery.Tick(RecoveryInput(codRunning: false, visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now.AddSeconds(1), () => RecoveryInput(), _ => { });
var startup = restoredRecovery.Tick(RecoveryInput(codRunning: false, visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now.AddSeconds(2), () => RecoveryInput(codRunning: false, visual: highConfidencePlay), _ => { });
var waitingData = restoredRecovery.Tick(RecoveryInput(codRunning: true, cycle: null), WatchdogState.WaitingForData, true, now.AddSeconds(3), () => RecoveryInput(), _ => { });
var restored = restoredRecovery.Tick(RecoveryInput(codRunning: true, cycle: now.AddSeconds(4)), WatchdogState.Healthy, true, now.AddSeconds(4), () => RecoveryInput(), _ => { });
Assert(codClosed.Signals.Any(signal => signal.Name == "COD_CLOSED") && startup.StateOverride == WatchdogState.WaitingForCod && waitingData.StateOverride == WatchdogState.WaitingForData && restored.Signals.Any(signal => signal.Name == "RECOVERY_OK" && signal.Detail == "reason=CollectionRestored"), "closed COD reuses startup recovery and requires a new CYCLE_OK before success");
staleActiveRecovery.Tick(RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), WatchdogState.CheckingGameState, true, now.AddSeconds(6), () => RecoveryInput(), _ => { });
Assert(staleActiveActions.ForceKillCount == 1, "graceful close fallback terminates only COD after grace timeout");
var freshBeforeCloseActions = new FakeRecoveryActions(); var freshBeforeCloseRecovery = new CodStartupRecoveryController(freshBeforeCloseActions);
var freshBeforeClose = freshBeforeCloseRecovery.Tick(RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), WatchdogState.CheckingGameState, true, now, () => RecoveryInput(codRunning: true, cycle: now.AddMinutes(-1)), _ => { });
Assert(freshBeforeCloseActions.CloseCount == 0 && freshBeforeClose.Signals.Any(signal => signal.Name == "RECOVERY_CANCELLED" && signal.Detail == "reason=FreshCycleDetected"), "fresh CYCLE_OK cancels before close");
var collectorGoneActions = new FakeRecoveryActions(); var collectorGoneRecovery = new CodStartupRecoveryController(collectorGoneActions);
collectorGoneRecovery.Tick(RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), WatchdogState.CheckingGameState, true, now, () => RecoveryInput(codRunning: true, collectorRunning: false), _ => { });
Assert(collectorGoneActions.CloseCount == 0, "missing Collector cancels before close");
var disabledActions = new FakeRecoveryActions(); var disabledRecovery = new CodStartupRecoveryController(disabledActions);
disabledRecovery.Tick(RecoveryInput(codRunning: true, cycle: now.AddMinutes(-11), stale: true), WatchdogState.CheckingGameState, true, now, () => RecoveryInput(codRunning: true, activity: CollectorActivityState.Disabled), _ => { });
Assert(disabledActions.CloseCount == 0, "disabled Collector cancels before close");
var activeActions = new FakeRecoveryActions(); var activeRecovery = new CodStartupRecoveryController(activeActions);
var bnetLaunch = activeRecovery.Tick(RecoveryInput(bnetRunning: false), WatchdogState.BnetNotRunning, true, now, () => RecoveryInput(bnetRunning: false), _ => { });
Assert(activeActions.OpenCount == 1 && bnetLaunch.StateOverride == WatchdogState.WaitingForBnet, "active COD missing opens the validated Battle.net URI once");
activeRecovery.Tick(RecoveryInput(bnetRunning: false), WatchdogState.BnetNotRunning, true, now.AddSeconds(10), () => RecoveryInput(bnetRunning: false), _ => { });
Assert(activeActions.OpenCount == 1, "waiting for Battle.net never relaunches URI");
var clickAllowed = activeRecovery.Tick(RecoveryInput(visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now.AddSeconds(11), () => RecoveryInput(visual: highConfidencePlay), _ => { });
Assert(activeActions.ClickCount == 1 && clickAllowed.StateOverride == WatchdogState.WaitingForCod, "confidence .991 permits exactly one click");
activeRecovery.Tick(RecoveryInput(visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now.AddSeconds(12), () => RecoveryInput(visual: highConfidencePlay), _ => { });
Assert(activeActions.ClickCount == 1, "waiting for COD prevents repeated clicks");
var lowActions = new FakeRecoveryActions(); var lowRecovery = new CodStartupRecoveryController(lowActions);
foreach (var confidence in new[] { 0.861, 0.949 }) { var m = new TemplateMatchResult(confidence, 1, 1, 4, 4); lowRecovery.Tick(RecoveryInput(visual: new VisualDetectionResult(VisualDetectionStatus.Match, m, confidence)), WatchdogState.CodNotRunning, true, now, () => RecoveryInput(), _ => { }); }
Assert(lowActions.ClickCount == 0, "confidence below .950 forbids clicks");
var playTimeoutActions = new FakeRecoveryActions(); var playTimeoutRecovery = new CodStartupRecoveryController(playTimeoutActions); var playFailures = 0;
playTimeoutRecovery.Tick(RecoveryInput(visual: new VisualDetectionResult(VisualDetectionStatus.NoMatch, null, .861)), WatchdogState.CodNotRunning, true, now, () => RecoveryInput(), _ => playFailures++);
var playTimeout = playTimeoutRecovery.Tick(RecoveryInput(visual: new VisualDetectionResult(VisualDetectionStatus.NoMatch, null, .861)), WatchdogState.CodNotRunning, true, now.AddSeconds(61), () => RecoveryInput(), _ => playFailures++);
Assert(playTimeout.StateOverride == WatchdogState.RecoveryFailed && playFailures == 1 && playTimeoutActions.ClickCount == 0, "unconfident Play blocks then times out without clicking");
var boundaryActions = new FakeRecoveryActions(); var boundaryRecovery = new CodStartupRecoveryController(boundaryActions); var boundary = new TemplateMatchResult(.950, 1, 1, 4, 4);
boundaryRecovery.Tick(RecoveryInput(visual: new VisualDetectionResult(VisualDetectionStatus.Match, boundary, .950)), WatchdogState.CodNotRunning, true, now, () => RecoveryInput(visual: new VisualDetectionResult(VisualDetectionStatus.Match, boundary, .950)), _ => { });
Assert(boundaryActions.ClickCount == 1, "confidence .950 permits click");
var raceActions = new FakeRecoveryActions(); var raceRecovery = new CodStartupRecoveryController(raceActions); raceRecovery.Tick(RecoveryInput(visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now, () => RecoveryInput(codRunning: true), _ => { });
Assert(raceActions.ClickCount == 0, "COD appearing during revalidation cancels click");
var clickFailureActions = new FakeRecoveryActions { ClickSucceeds = false }; var clickFailureRecovery = new CodStartupRecoveryController(clickFailureActions); var clickFailures = 0;
var clickFailure = clickFailureRecovery.Tick(RecoveryInput(visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now, () => RecoveryInput(visual: highConfidencePlay), _ => clickFailures++);
clickFailureRecovery.Tick(RecoveryInput(visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now.AddSeconds(10), () => RecoveryInput(visual: highConfidencePlay), _ => clickFailures++);
Assert(clickFailure.StateOverride == WatchdogState.RecoveryFailed && clickFailureActions.ClickCount == 1 && clickFailures == 1, "failed click enters retry delay without repeated clicks");
var dryActions = new FakeRecoveryActions(); var dryRecovery = new CodStartupRecoveryController(dryActions); var dry = dryRecovery.Tick(RecoveryInput(bnetRunning: false), WatchdogState.BnetNotRunning, false, now, () => RecoveryInput(bnetRunning: false), _ => { });
Assert(dryActions.OpenCount == 0 && dry.Signals.Any(signal => signal.Name == "WATCHDOG_ACTION_PLANNED"), "dry-run plans URI without launching it");
var blockedActions = new FakeRecoveryActions(); var blockedRecovery = new CodStartupRecoveryController(blockedActions); blockedRecovery.Tick(RecoveryInput(collectorRunning: false, visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now, () => RecoveryInput(), _ => { }); blockedRecovery.Tick(RecoveryInput(activity: CollectorActivityState.Disabled, visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now, () => RecoveryInput(), _ => { });
Assert(blockedActions.OpenCount == 0 && blockedActions.ClickCount == 0, "missing or disabled Collector blocks all actions");
var timeoutActions = new FakeRecoveryActions(); var timeoutRecovery = new CodStartupRecoveryController(timeoutActions); var failures = 0; timeoutRecovery.Tick(RecoveryInput(visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now, () => RecoveryInput(visual: highConfidencePlay), _ => failures++); var timeout = timeoutRecovery.Tick(RecoveryInput(visual: highConfidencePlay), WatchdogState.CodNotRunning, true, now.AddSeconds(121), () => RecoveryInput(visual: highConfidencePlay), _ => failures++);
Assert(timeout.StateOverride == WatchdogState.RecoveryFailed && failures == 1 && timeoutActions.ClickCount == 1, "COD start timeout fails recovery without another click");
var clickRect = new WindowScreenRect(100, 200, 500, 800);
Assert(PlayClickValidation.TryGetScreenPoint(clickRect, 163, 533, out var clickPoint) && clickPoint == new System.Drawing.Point(263, 733), "window-relative template center converts through GetWindowRect screen coordinates");
Assert(PlayClickValidation.IsPointInside(clickRect, clickPoint) && !PlayClickValidation.IsPointInside(clickRect, new System.Drawing.Point(500, 800)), "target point bounds are validated against the current window rectangle");
Assert(!PlayClickValidation.TryGetScreenPoint(clickRect, 400, 0, out _), "relative point outside window is rejected");
Assert(PlayClickValidation.IsBattleNetTarget(new IntPtr(2), new IntPtr(1), new IntPtr(1), 100, 100), "child HWND belonging to Battle.net is accepted");
Assert(!PlayClickValidation.IsBattleNetTarget(new IntPtr(2), new IntPtr(3), new IntPtr(1), 200, 100), "foreign target HWND is rejected");
Assert(PlayClickValidation.ForegroundRequestIsAdvisory(false), "foreground request rejection does not mask target or SendInput diagnostics");
Assert(!PlayClickValidation.IsSendInputAccepted(2, 0) && PlayClickValidation.IsSendInputAccepted(2, 2), "SendInput accepts only both mouse down and mouse up");
Assert(PlayClickValidation.IsRestoreFailureAdvisory(true, false), "cursor restore failure remains advisory after input delivery");
Assert(!PlayClickValidation.IsConfidenceEligible(new VisualDetectionResult(VisualDetectionStatus.NoMatch, null, .991)), "test-play-click requires a real template match");
var belowThreshold = new TemplateMatchResult(.949, 0, 0, 4, 4);
Assert(!PlayClickValidation.IsConfidenceEligible(new VisualDetectionResult(VisualDetectionStatus.Match, belowThreshold, .949)), "test-play-click blocks confidence below .950");
using var cancellation = new CancellationTokenSource();
var loopCalls = 0;
var loop = new WatchdogLoop(_ => { cancellation.Cancel(); return Task.CompletedTask; }, () => now);
await loop.RunAsync(() => WithAge(1), new WatchdogStateMachine(), (_, _) => loopCalls++, cancellation.Token);
Assert(loopCalls == 1, "loop accepts fake delay and cancellation without real wait");

Console.WriteLine("PASS COD Watchdog Prototype offline state-machine, launcher and loop tests");

static ProcessSnapshot Snapshot(string name, int pid, IntPtr window, bool visible, string title) => new(name, pid, null, null, null, title, string.Empty, window, visible, null);
static Bitmap CreatePattern(int width, int height)
{
    var bitmap = new Bitmap(width, height);
    for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++) bitmap.SetPixel(x, y, (x + y) % 3 == 0 ? Color.White : (x % 2 == 0 ? Color.DeepSkyBlue : Color.DarkBlue));
    return bitmap;
}
static string FindProjectRoot()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
    throw new DirectoryNotFoundException("Project root not found.");
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class FakeRecoveryActions : ICodStartupRecoveryActions
{
    public int OpenCount { get; private set; }
    public int ClickCount { get; private set; }
    public int CloseCount { get; private set; }
    public int ForceKillCount { get; private set; }
    public bool ClickSucceeds { get; init; } = true;
    public bool OpenValidatedBattleNetUri() { OpenCount++; return true; }
    public PlayClickDeliveryResult TryClickPlay(ProcessSnapshot primaryWindow, TemplateMatchResult match) { ClickCount++; return new(ClickSucceeds, Array.Empty<RecoverySignal>()); }
    public bool RequestGracefulCodClose(ProcessSnapshot cod) { CloseCount++; return true; }
    public bool ForceTerminateCod(ProcessSnapshot cod) { ForceKillCount++; return true; }
}
