using CODWatchdogPrototype;
using System.Globalization;
using System.IO;

var options = PrototypeOptions.Parse(args);
var logger = new WatchdogLogger();
var detector = new TemplateScreenStateDetector();
var uiInspector = new BattleNetUiAutomationInspector();
var visualDetector = new BattleNetVisualDetector();
var actionsEnabled = options.Active && options.Simulated == SimulatedState.None;
logger.Write("WATCHDOG_START", actionsEnabled ? "mode=active" : "mode=dry-run");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };

if (options.TraceBattleNetLaunch)
{
    await TraceBattleNetLaunchAsync(logger, cancellation.Token);
    return;
}
if (options.CreateTemplatePlay)
{
    var sample = ReadSample(options, detector, visualDetector, detectVisual: false);
    Console.WriteLine(BattleNetTemplateCreator.CreatePlayTemplate(sample.PrimaryBattleNetWindow, out var message) ? message : message);
    return;
}
if (options.DetectBattleNetState)
{
    var sample = ReadSample(options, detector, visualDetector, detectVisual: false);
    PrintVisualDetection(visualDetector.DetectPlay(sample.PrimaryBattleNetWindow));
    return;
}
if (options.InspectBattleNetUi)
{
    var sample = ReadSample(options, detector, visualDetector, detectVisual: false);
    PrintUiInspection(uiInspector.Inspect(sample.PrimaryBattleNetWindow));
    return;
}
if (options.Diagnose)
{
    var sample = ReadSample(options, detector, visualDetector, detectVisual: false);
    var machine = new WatchdogStateMachine();
    PrintDiagnostics(sample, machine.Evaluate(sample.Observation, DateTimeOffset.UtcNow).Current);
    return;
}
if (options.TestPlayClick)
{
    await TestPlayClickAsync(options, actionsEnabled, logger, detector, visualDetector, cancellation.Token);
    return;
}
if (options.CaptureState)
{
    var sample = ReadSample(options, detector, visualDetector, detectVisual: false);
    var captured = WindowCapture.Capture(sample.Cod ?? sample.PrimaryBattleNetWindow, Path.Combine(AppContext.BaseDirectory, "diagnostics", "captures"));
    Console.WriteLine(captured is null ? "WATCHDOG_CAPTURE no-eligible-window" : "WATCHDOG_CAPTURE saved");
    return;
}

var stateMachine = new WatchdogStateMachine();
var reporter = new WatchdogReporter(logger);
var recovery = new CodStartupRecoveryController(new ActiveCodStartupRecoveryActions());
var loop = new WatchdogLoop();
WatchdogSample? latest = null;
Console.WriteLine($"WATCHDOG_START mode={(actionsEnabled ? "active" : "dry-run")} pollSeconds={(int)WatchdogConfiguration.Default.PollInterval.TotalSeconds}");

void EvaluateAndReport(WatchdogSample sample, WatchdogTransition baseTransition, DateTimeOffset now)
{
    var recoveryResult = recovery.Tick(
        ToRecoveryInput(sample, options.TestStaleRecovery), baseTransition.Current, actionsEnabled, now,
        () => ToRecoveryInput(ReadSample(options, detector, visualDetector, detectVisual: true), options.TestStaleRecovery),
        stateMachine.RecordRecoveryFailure);
    var effectiveState = recoveryResult.StateOverride ?? baseTransition.Current;
    reporter.Report(new WatchdogTransition(baseTransition.Previous, effectiveState, Array.Empty<string>()), sample);
    reporter.ReportRecovery(recoveryResult);
}

try
{
    if (options.Once)
    {
        latest = ReadSample(options, detector, visualDetector, detectVisual: true);
        var now = DateTimeOffset.UtcNow;
        EvaluateAndReport(latest, stateMachine.Evaluate(latest.Observation, now), now);
    }
    else
    {
        await loop.RunAsync(
            () => (latest = ReadSample(options, detector, visualDetector, detectVisual: true)).Observation,
            stateMachine,
            (transition, _) => EvaluateAndReport(latest!, transition, DateTimeOffset.UtcNow),
            cancellation.Token);
    }
}catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    logger.Write("WATCHDOG_STOP", "reason=ctrl-c");
    Console.WriteLine("WATCHDOG_STOP ctrl-c");
}

static WatchdogSample ReadSample(PrototypeOptions options, IScreenStateDetector detector, BattleNetVisualDetector visualDetector, bool detectVisual)
{
    var processes = ProcessDiagnostics.ReadRelevantProcesses();
    var cod = ProcessDiagnostics.FindCodProcess(processes);
    var battleNetProcesses = ProcessDiagnostics.FindBattleNetProcesses(processes);
    var agents = ProcessDiagnostics.FindAgents(processes);
    var primaryBattleNetWindow = ProcessDiagnostics.FindPrimaryBattleNetWindow(battleNetProcesses);
    var collector = ProcessDiagnostics.FindCollectorProcess(processes);
    var collectorLog = CollectorHealthReader.Read();
    var signals = BattleNetDiagnostics.ReadSignals();
    var screen = detector.Detect(cod, primaryBattleNetWindow);
    var visual = detectVisual && cod is null && battleNetProcesses.Count > 0 && primaryBattleNetWindow is not null ? visualDetector.DetectPlay(primaryBattleNetWindow) : null;
    var lastCycle = options.Simulated == SimulatedState.NoData ? DateTimeOffset.UtcNow.AddMinutes(-11) : collectorLog.LastCycleOkUtc;
    var observation = new WatchdogObservation(
        CodRunning: options.Simulated != SimulatedState.CodMissing && cod is not null,
        BattleNetAppRunning: battleNetProcesses.Count > 0,
        BattleNetAgentRunning: agents.Count > 0,
        CollectorRunning: collector is not null,
        CollectorActivity: collectorLog.Activity,
        BattleNetUpdateInProgress: options.Simulated == SimulatedState.BnetUpdate || screen == ScreenState.BattleNetUpdating,
        GameUpdateRequiresRestart: screen == ScreenState.UpdateRequiresRestart,
        ServerDisconnectedRestart: options.Simulated == SimulatedState.ServerDisconnectedRestart || screen == ScreenState.ServerDisconnectedRestart,
        ServerDisconnectedQuit: options.Simulated == SimulatedState.ServerDisconnectedQuit || screen == ScreenState.ServerDisconnectedQuit,
        CodStartedAtUtc: cod?.StartedAtUtc,
        LastValidCollectionUtc: lastCycle,
        ApiSyncFailed: false);
    return new(processes, cod, collector, primaryBattleNetWindow, battleNetProcesses, agents, signals, collectorLog, visual, observation);
}

static CodStartupRecoveryInput ToRecoveryInput(WatchdogSample sample, bool forceStaleForTest)
{
    var cycle = sample.Observation.LastValidCollectionUtc;
    var stale = forceStaleForTest || (sample.Observation.CodRunning && cycle is { } timestamp && DateTimeOffset.UtcNow - timestamp >= WatchdogConfiguration.Default.ConfirmAfter);
    return new(sample.Observation.CodRunning, sample.Observation.BattleNetAppRunning, sample.Observation.CollectorRunning,
        sample.Observation.CollectorActivity, sample.Cod, sample.PrimaryBattleNetWindow, sample.VisualDetection, cycle, stale, forceStaleForTest);
}

static async Task TestPlayClickAsync(PrototypeOptions options, bool actionsEnabled, WatchdogLogger logger, IScreenStateDetector detector, BattleNetVisualDetector visualDetector, CancellationToken cancellationToken)
{
    var sample = ReadSample(options, detector, visualDetector, detectVisual: true);
    if (sample.Cod is not null) { Console.WriteLine("PLAY_CLICK_TEST_ABORT reason=CodAlreadyRunning"); return; }
    if (!sample.Observation.BattleNetAppRunning || sample.PrimaryBattleNetWindow is null) { Console.WriteLine("PLAY_CLICK_TEST_ABORT reason=PrimaryWindowUnavailable"); return; }
    var visual = sample.VisualDetection;
    if (visual?.Status != VisualDetectionStatus.Match || visual.Match is not { } match || match.Confidence < 0.95)
    {
        Console.WriteLine($"PLAY_CLICK_TEST_ABORT reason=PlayNotConfident bestConfidence={FormatConfidence(visual?.BestConfidence ?? 0)}");
        return;
    }
    Console.WriteLine($"BNET_VISUAL_MATCH state=Play confidence={FormatConfidence(match.Confidence)} x={match.X} y={match.Y} width={match.Width} height={match.Height} centerX={match.CenterX} centerY={match.CenterY}");
    if (!actionsEnabled)
    {
        logger.Write("WATCHDOG_ACTION_PLANNED", "click_play");
        Console.WriteLine("WATCHDOG_ACTION_PLANNED click_play");
        return;
    }
    var delivery = new ActiveCodStartupRecoveryActions().TryClickPlay(sample.PrimaryBattleNetWindow, match);
    foreach (var signal in delivery.Signals)
    {
        logger.Write(signal.Name, signal.Detail);
        Console.WriteLine($"{signal.Name}{(string.IsNullOrWhiteSpace(signal.Detail) ? string.Empty : " " + signal.Detail)}");
    }
    if (!delivery.Succeeded) { logger.Write("PLAY_CLICK_TEST_FAILED"); Console.WriteLine("PLAY_CLICK_TEST_FAILED"); return; }
    logger.Write("PLAY_CLICK_SENT");
    Console.WriteLine("PLAY_CLICK_SENT");
    var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
    while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var cod = ProcessDiagnostics.FindCodProcess(ProcessDiagnostics.ReadRelevantProcesses());
        if (cod is null) continue;
        logger.Write("PLAY_CLICK_TEST_OK", $"codPid={cod.Pid}");
        Console.WriteLine($"PLAY_CLICK_TEST_OK codPid={cod.Pid}");
        return;
    }
    if (!cancellationToken.IsCancellationRequested) { logger.Write("PLAY_CLICK_TEST_FAILED", "reason=CodDidNotStart"); Console.WriteLine("PLAY_CLICK_TEST_FAILED reason=CodDidNotStart"); }
}
static async Task TraceBattleNetLaunchAsync(WatchdogLogger logger, CancellationToken cancellationToken)
{
    var before = BattleNetLaunchTrace.CaptureSnapshot();
    var cod = ProcessDiagnostics.FindCodProcess(before.Processes);
    var battleNet = ProcessDiagnostics.FindBattleNetProcesses(before.Processes);
    var agents = ProcessDiagnostics.FindAgents(before.Processes);
    if (cod is not null) { Console.WriteLine("TRACE_ABORT cod-already-running"); return; }
    if (battleNet.Count == 0) { Console.WriteLine("TRACE_ABORT battlenet-app-unavailable"); return; }
    logger.Write("TRACE_BNET_LAUNCH_START", $"bnetProcesses={battleNet.Count} agents={agents.Count}");
    Console.WriteLine("TRACE_READY");
    Console.WriteLine("Manually start Call of Duty from Battle.net.");
    Console.WriteLine($"TRACE_BASELINE battlenetProcesses={battleNet.Count} agents={agents.Count} files={before.Files.Count} shortcuts={before.ShortcutNames.Count} battlenetProtocol={before.Signals.BattleNetProtocol.Registered} blizzardProtocol={before.Signals.BlizzardProtocol.Registered}");
    foreach (var shortcut in before.ShortcutNames) Console.WriteLine($"TRACE_SHORTCUT_HINT name={shortcut}");
    try
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        ProcessSnapshot? startedCod = null;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var current = BattleNetLaunchTrace.CaptureSnapshot();
            startedCod = ProcessDiagnostics.FindCodProcess(current.Processes);
            if (startedCod is null) continue;
            logger.Write("TRACE_COD_APPEARED", $"pid={startedCod.Pid} parentPid={startedCod.ParentPid?.ToString() ?? "unknown"}");
            Console.WriteLine($"TRACE_COD_APPEARED pid={startedCod.Pid} parentPid={startedCod.ParentPid?.ToString() ?? "unknown"}");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var after = BattleNetLaunchTrace.CaptureSnapshot();
            PrintTraceDiff(BattleNetLaunchTrace.Compare(before, after));
            logger.Write("TRACE_BNET_LAUNCH_COMPLETE", $"codPid={startedCod.Pid}");
            return;
        }
        if (!cancellationToken.IsCancellationRequested) Console.WriteLine("TRACE_TIMEOUT cod-not-observed");
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        logger.Write("TRACE_BNET_LAUNCH_STOP", "reason=ctrl-c");
        Console.WriteLine("TRACE_STOP ctrl-c");
    }
}

static void PrintTraceDiff(BattleNetTraceDiff diff)
{
    Console.WriteLine($"TRACE_RESULT newProcesses={diff.NewProcesses.Count} changedFiles={diff.ChangedFiles.Count}");
    foreach (var process in diff.NewProcesses) Console.WriteLine($"TRACE_NEW_PROCESS name={process.Name} pid={process.Pid} parentPid={process.ParentPid?.ToString() ?? "unknown"}");
    foreach (var file in diff.ChangedFiles)
    {
        var categories = file.HintCategories.Count == 0 ? "none" : string.Join(',', file.HintCategories);
        Console.WriteLine($"TRACE_CHANGED_FILE name={file.Name} hints={categories}");
        foreach (var candidateUri in file.CandidateUris) Console.WriteLine($"TRACE_CANDIDATE_UNVALIDATED uri={candidateUri}");
    }
}

static void PrintDiagnostics(WatchdogSample sample, WatchdogState state)
{
    Console.WriteLine($"WATCHDOG_DIAG cod={Describe(sample.Cod)}");
    Console.WriteLine($"WATCHDOG_DIAG collector={Describe(sample.Collector)} activity={sample.CollectorLog.Activity}");
    Console.WriteLine($"WATCHDOG_DIAG battlenet-app-running={sample.Observation.BattleNetAppRunning} processes={sample.BattleNetProcesses.Count} agent-running={sample.Observation.BattleNetAgentRunning} agents={sample.Agents.Count}");
    Console.WriteLine($"WATCHDOG_DIAG battlenet-primary-window={Describe(sample.PrimaryBattleNetWindow)}");
    foreach (var agent in sample.Agents) Console.WriteLine($"AGENT {Describe(agent)}");
    Console.WriteLine($"WATCHDOG_DIAG protocols battlenet={DescribeProtocol(sample.Signals.BattleNetProtocol)} blizzard={DescribeProtocol(sample.Signals.BlizzardProtocol)}");
    Console.WriteLine($"WATCHDOG_DIAG known-uri={BattleNetLaunchUriKnowledge.Uri} classification={BattleNetLaunchUriKnowledge.Classification} gameLaunch=false");
    Console.WriteLine($"WATCHDOG_DIAG update-log-hint={sample.Signals.UpdateKeywordsObserved} recentLogs={sample.Signals.RecentSignalFiles.Count} authoritative=false");
    Console.WriteLine($"WATCHDOG_DIAG lastCycleUtc={(sample.Observation.LastValidCollectionUtc?.ToString("O") ?? "none")} ageSeconds={Age(sample.Observation.LastValidCollectionUtc)} state={state}");
}

static void PrintUiInspection(BattleNetUiInspection inspection)
{
    if (!inspection.PrimaryWindowAvailable) { Console.WriteLine("BNET_PRIMARY_WINDOW_UNAVAILABLE"); return; }
    Console.WriteLine($"BNET_UIA_INSPECT elements={inspection.RelevantElements.Count} traversalTruncated={inspection.TraversalTruncated}");
    foreach (var element in inspection.RelevantElements)
    {
        var bounds = element.Bounds;
        var patterns = element.SupportedPatterns.Count == 0 ? "none" : string.Join(',', element.SupportedPatterns);
        Console.WriteLine($"BNET_UIA_ELEMENT name={Sanitize(element.Name)} automationId={Sanitize(element.AutomationId)} controlType={element.ControlType} enabled={element.IsEnabled} offscreen={element.IsOffscreen} bounds={bounds.Left:0},{bounds.Top:0},{bounds.Width:0},{bounds.Height:0} patterns={patterns}");
    }
    Console.WriteLine(inspection.PlayCandidate ? "BNET_PLAY_UIA_CANDIDATE" : "BNET_PLAY_UIA_NOT_FOUND");
}

static void PrintVisualDetection(VisualDetectionResult result)
{
    if (result.Status == VisualDetectionStatus.Match && result.Match is { } match)
    {
        Console.WriteLine($"BNET_VISUAL_MATCH state=Play confidence={FormatConfidence(match.Confidence)} x={match.X} y={match.Y} width={match.Width} height={match.Height} centerX={match.CenterX} centerY={match.CenterY}");
        return;
    }
    Console.WriteLine($"BNET_VISUAL_NO_MATCH state=Play bestConfidence={FormatConfidence(result.BestConfidence)} reason={result.Status}");
}

static string Describe(ProcessSnapshot? process) => process is null ? "missing" : $"pid={process.Pid} path={process.ExecutablePath ?? "unknown"} windowHandle=0x{process.WindowHandle.ToInt64():X} visible={process.WindowVisible} class={process.WindowClass} title={Sanitize(process.WindowTitle)}";
static string DescribeProtocol(ProtocolHandler handler) => $"registered={handler.Registered} executable={handler.ExecutableName} acceptsUri={handler.AcceptsUriArgument}";
static string Age(DateTimeOffset? timestamp) => timestamp is null ? "unknown" : Math.Max(0, (int)(DateTimeOffset.UtcNow - timestamp.Value).TotalSeconds).ToString();
static string FormatConfidence(double value) => value.ToString("F4", CultureInfo.InvariantCulture);
static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

internal sealed record PrototypeOptions(bool Active, bool Diagnose, bool CaptureState, bool TraceBattleNetLaunch, bool InspectBattleNetUi, bool CreateTemplatePlay, bool DetectBattleNetState, bool TestPlayClick, bool Once, bool TestStaleRecovery, SimulatedState Simulated)
{
    public static PrototypeOptions Parse(string[] args)
    {
        var active = args.Contains("--active", StringComparer.OrdinalIgnoreCase);
        var diagnose = args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase);
        var capture = args.Contains("--capture-state", StringComparer.OrdinalIgnoreCase) || args.Contains("--capture-templates", StringComparer.OrdinalIgnoreCase);
        var trace = args.Contains("--trace-bnet-launch", StringComparer.OrdinalIgnoreCase);
        var inspect = args.Contains("--inspect-bnet-ui", StringComparer.OrdinalIgnoreCase);
        var detectVisual = args.Contains("--detect-bnet-state", StringComparer.OrdinalIgnoreCase);
        var testPlayClick = args.Contains("--test-play-click", StringComparer.OrdinalIgnoreCase);
        var createIndex = Array.FindIndex(args, argument => argument.Equals("--create-template", StringComparison.OrdinalIgnoreCase));
        var createPlay = createIndex >= 0 && createIndex + 1 < args.Length && args[createIndex + 1].Equals("play", StringComparison.OrdinalIgnoreCase);
        if (createIndex >= 0 && !createPlay) throw new ArgumentException("Only '--create-template play' is supported in this revision.");
        var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
        var testStaleRecovery = args.Contains("--test-stale-recovery", StringComparer.OrdinalIgnoreCase);
        var simulation = SimulatedState.None;
        var index = Array.FindIndex(args, argument => argument.Equals("--simulate", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length)
        {
            var requested = args[index + 1].Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
            simulation = Enum.GetValues<SimulatedState>().FirstOrDefault(value => value.ToString().Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (simulation == SimulatedState.None && !requested.Equals("none", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Unknown simulation.");
        }
        if ((testStaleRecovery || testPlayClick) && simulation != SimulatedState.None) throw new ArgumentException("--test-stale-recovery and --test-play-click cannot be combined with --simulate.");
        return new(active, diagnose, capture, trace, inspect, createPlay, detectVisual, testPlayClick, once, testStaleRecovery, simulation);
    }
}

internal sealed record WatchdogSample(IReadOnlyList<ProcessSnapshot> Processes, ProcessSnapshot? Cod, ProcessSnapshot? Collector, ProcessSnapshot? PrimaryBattleNetWindow, IReadOnlyList<ProcessSnapshot> BattleNetProcesses, IReadOnlyList<ProcessSnapshot> Agents, BattleNetSignals Signals, CollectorLogSnapshot CollectorLog, VisualDetectionResult? VisualDetection, WatchdogObservation Observation);

internal sealed class WatchdogReporter
{
    private readonly WatchdogLogger logger;
    private int? previousCodPid;
    private int? previousCollectorPid;
    private int? previousPrimaryBattleNetPid;
    private bool? previousBattleNetAppAvailable;
    private bool? previousBattleNetAgentAvailable;
    private CollectorActivityState? previousCollectorActivity;
    private WatchdogState? previousState;
    private string? previousHealth;
    private bool? previousUpdateHint;
    private VisualDetectionStatus? previousVisualStatus;
    private DateTimeOffset nextHeartbeatUtc = DateTimeOffset.MinValue;
    public WatchdogReporter(WatchdogLogger logger) => this.logger = logger;
    public void Report(WatchdogTransition transition, WatchdogSample sample)
    {
        if (previousState != transition.Current) { logger.Write("WATCHDOG_STATE", $"old={transition.Previous} new={transition.Current}"); Console.WriteLine($"WATCHDOG_STATE {transition.Current}"); previousState = transition.Current; }
        LogProcessChange("COD", sample.Cod, ref previousCodPid);
        LogProcessChange("COLLECTOR", sample.Collector, ref previousCollectorPid);
        LogBattleNet(sample);
        if (previousCollectorActivity != sample.CollectorLog.Activity) { logger.Write("COLLECTOR_ACTIVITY", $"state={sample.CollectorLog.Activity}"); previousCollectorActivity = sample.CollectorLog.Activity; }
        var health = HealthCategory(transition.Current);
        if (health != previousHealth) { logger.Write(health, HealthDetail(sample.Observation)); previousHealth = health; }
        if (previousUpdateHint != sample.Signals.UpdateKeywordsObserved) { logger.Write("UPDATE_SIGNAL_ADVISORY", $"recentLogs={sample.Signals.RecentSignalFiles.Count} activeState=false"); previousUpdateHint = sample.Signals.UpdateKeywordsObserved; }
        LogVisualDetection(transition.Current, sample.VisualDetection);
        if (DateTimeOffset.UtcNow >= nextHeartbeatUtc) { logger.Write("WATCHDOG_HEARTBEAT", $"state={transition.Current}"); nextHeartbeatUtc = DateTimeOffset.UtcNow.AddMinutes(5); }
        if (transition.Previous != transition.Current) foreach (var action in transition.PlannedActions) { logger.Write("WATCHDOG_ACTION_PLANNED", action); Console.WriteLine($"WATCHDOG_ACTION_PLANNED {action}"); }
    }
    public void ReportRecovery(RecoveryTickResult result)
    {
        foreach (var signal in result.Signals)
        {
            logger.Write(signal.Name, signal.Detail);
            if (signal.Name is "WATCHDOG_ACTION_PLANNED" or "RECOVERY_FAILED" or "RECOVERY_BLOCKED" or "PLAY_CLICK_SENT")
                Console.WriteLine($"{signal.Name}{(string.IsNullOrWhiteSpace(signal.Detail) ? string.Empty : " " + signal.Detail)}");
        }
    }
    private void LogVisualDetection(WatchdogState state, VisualDetectionResult? visual)
    {
        if (visual is null || previousVisualStatus == visual.Status) return;
        previousVisualStatus = visual.Status;
        if (visual.Status == VisualDetectionStatus.Match && visual.Match is { } match)
        {
            logger.Write("BNET_VISUAL_MATCH", $"state=Play confidence={match.Confidence.ToString("F4", CultureInfo.InvariantCulture)} x={match.X} y={match.Y} width={match.Width} height={match.Height} centerX={match.CenterX} centerY={match.CenterY}");
            if (state == WatchdogState.CodNotRunning) logger.Write("WATCHDOG_ACTION_PLANNED", "click_play");
            return;
        }
        logger.Write("BNET_VISUAL_NO_MATCH", $"state=Play bestConfidence={visual.BestConfidence.ToString("F4", CultureInfo.InvariantCulture)} reason={visual.Status}");
    }
    private void LogBattleNet(WatchdogSample sample)
    {
        if (previousBattleNetAppAvailable != sample.Observation.BattleNetAppRunning) { logger.Write(sample.Observation.BattleNetAppRunning ? "BNET_APP_AVAILABLE" : "BNET_APP_UNAVAILABLE"); previousBattleNetAppAvailable = sample.Observation.BattleNetAppRunning; }
        if (previousBattleNetAgentAvailable != sample.Observation.BattleNetAgentRunning) { logger.Write(sample.Observation.BattleNetAgentRunning ? "BNET_AGENT_AVAILABLE" : "BNET_AGENT_UNAVAILABLE"); previousBattleNetAgentAvailable = sample.Observation.BattleNetAgentRunning; }
        var currentPrimaryPid = sample.PrimaryBattleNetWindow?.Pid;
        if (currentPrimaryPid != previousPrimaryBattleNetPid) { logger.Write("BNET_PRIMARY_CHANGED", $"oldPid={previousPrimaryBattleNetPid?.ToString() ?? "none"} newPid={currentPrimaryPid?.ToString() ?? "none"}"); previousPrimaryBattleNetPid = currentPrimaryPid; }
    }
    private void LogProcessChange(string prefix, ProcessSnapshot? process, ref int? previousPid)
    {
        var pid = process?.Pid;
        if (pid == previousPid) return;
        logger.Write(pid is null ? $"{prefix}_MISSING" : $"{prefix}_DETECTED", pid is null ? null : $"pid={pid}");
        previousPid = pid;
    }
    private static string HealthCategory(WatchdogState state) => state switch
    {
        WatchdogState.Healthy => "DATA_HEALTHY",
        WatchdogState.NoDataWarning or WatchdogState.NoDataConfirmed or WatchdogState.CheckingGameState => "NO_DATA",
        WatchdogState.WaitingForData => "WAITING_FOR_DATA",
        WatchdogState.CollectorNotRunning => "COLLECTOR_NOT_RUNNING",
        WatchdogState.CollectorDisabled => "COLLECTOR_DISABLED",
        _ => "DATA_NOT_EVALUATED"
    };
    private static string HealthDetail(WatchdogObservation observation) => observation.LastValidCollectionUtc is not { } cycle ? "ageSeconds=unknown" : $"ageSeconds={Math.Max(0, (int)(DateTimeOffset.UtcNow - cycle).TotalSeconds)}";
}

internal sealed class WatchdogLogger
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CODMunity", "CODWatchdogPrototype", "logs", "watchdog.log");
    public void Write(string eventName, string? detail = null) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {eventName}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail)}{Environment.NewLine}"); }
}
