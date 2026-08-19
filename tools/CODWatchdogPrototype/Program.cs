using CODWatchdogPrototype;

var options = PrototypeOptions.Parse(args);
var logger = new WatchdogLogger();
var detector = new TemplateScreenStateDetector();
logger.Write("WATCHDOG_START", options.Active ? "mode=active-nondestructive" : "mode=dry-run");

if (options.Diagnose)
{
    var sample = ReadSample(options, detector);
    var machine = new WatchdogStateMachine();
    var transition = machine.Evaluate(sample.Observation, DateTimeOffset.UtcNow);
    PrintDiagnostics(sample, transition.Current);
    return;
}

if (options.CaptureState)
{
    var sample = ReadSample(options, detector);
    var captured = WindowCapture.Capture(sample.Cod ?? sample.PrimaryBattleNet, Path.Combine(AppContext.BaseDirectory, "diagnostics", "captures"));
    Console.WriteLine(captured is null ? "WATCHDOG_CAPTURE no-eligible-window" : "WATCHDOG_CAPTURE saved");
    return;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var stateMachine = new WatchdogStateMachine();
var reporter = new WatchdogReporter(logger);
var loop = new WatchdogLoop();
WatchdogSample? latest = null;
Console.WriteLine($"WATCHDOG_START mode={(options.Active ? "active-nondestructive" : "dry-run")} pollSeconds={(int)WatchdogConfiguration.Default.PollInterval.TotalSeconds}");
try
{
    if (options.Once)
    {
        latest = ReadSample(options, detector);
        reporter.Report(stateMachine.Evaluate(latest.Observation, DateTimeOffset.UtcNow), latest);
    }
    else
    {
        await loop.RunAsync(
            () => (latest = ReadSample(options, detector)).Observation,
            stateMachine,
            (transition, _) => reporter.Report(transition, latest!),
            cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    logger.Write("WATCHDOG_STOP", "reason=ctrl-c");
    Console.WriteLine("WATCHDOG_STOP ctrl-c");
}

static WatchdogSample ReadSample(PrototypeOptions options, IScreenStateDetector detector)
{
    var processes = ProcessDiagnostics.ReadRelevantProcesses();
    var cod = ProcessDiagnostics.FindCodProcess(processes);
    var primaryBattleNet = ProcessDiagnostics.FindPrimaryBattleNetProcess(processes);
    var signals = BattleNetDiagnostics.ReadSignals();
    var screen = detector.Detect(cod, primaryBattleNet);
    var lastCycle = options.Simulated == SimulatedState.NoData ? DateTimeOffset.UtcNow.AddMinutes(-11) : CollectorHealthReader.ReadLastCycleOkUtc();
    var observation = new WatchdogObservation(
        CodRunning: options.Simulated != SimulatedState.CodMissing && cod is not null,
        BattleNetRunning: primaryBattleNet is not null,
        BattleNetUpdateInProgress: options.Simulated == SimulatedState.BnetUpdate || screen == ScreenState.BattleNetUpdating,
        GameUpdateRequiresRestart: screen == ScreenState.UpdateRequiresRestart,
        ServerDisconnectedRestart: options.Simulated == SimulatedState.ServerDisconnectedRestart || screen == ScreenState.ServerDisconnectedRestart,
        ServerDisconnectedQuit: options.Simulated == SimulatedState.ServerDisconnectedQuit || screen == ScreenState.ServerDisconnectedQuit,
        CodStartedAtUtc: cod?.StartedAtUtc,
        LastValidCollectionUtc: lastCycle,
        ApiSyncFailed: false);
    return new(processes, cod, primaryBattleNet, ProcessDiagnostics.FindBattleNetProcesses(processes), ProcessDiagnostics.FindAgents(processes), signals, observation);
}

static void PrintDiagnostics(WatchdogSample sample, WatchdogState state)
{
    Console.WriteLine($"WATCHDOG_DIAG cod={Describe(sample.Cod)}");
    Console.WriteLine($"WATCHDOG_DIAG battlenet-primary={Describe(sample.PrimaryBattleNet)}");
    Console.WriteLine($"WATCHDOG_DIAG battlenet-subprocess-count={sample.BattleNetProcesses.Count}");
    Console.WriteLine($"WATCHDOG_DIAG agent-count={sample.Agents.Count}");
    foreach (var agent in sample.Agents) Console.WriteLine($"AGENT {Describe(agent)}");
    Console.WriteLine($"WATCHDOG_DIAG protocols battlenet={DescribeProtocol(sample.Signals.BattleNetProtocol)} blizzard={DescribeProtocol(sample.Signals.BlizzardProtocol)}");
    Console.WriteLine($"WATCHDOG_DIAG update-log-hint={sample.Signals.UpdateKeywordsObserved} recentLogs={sample.Signals.RecentSignalFiles.Count} authoritative=false");
    Console.WriteLine($"WATCHDOG_DIAG lastCycleUtc={(sample.Observation.LastValidCollectionUtc?.ToString("O") ?? "none")} ageSeconds={Age(sample.Observation.LastValidCollectionUtc)} state={state}");
}

static string Describe(ProcessSnapshot? process) => process is null
    ? "missing"
    : $"pid={process.Pid} path={process.ExecutablePath ?? "unknown"} windowHandle=0x{process.WindowHandle.ToInt64():X} class={process.WindowClass} title={Sanitize(process.WindowTitle)}";
static string DescribeProtocol(ProtocolHandler handler) => $"registered={handler.Registered} executable={handler.ExecutableName} acceptsUri={handler.AcceptsUriArgument}";
static string Age(DateTimeOffset? timestamp) => timestamp is null ? "unknown" : Math.Max(0, (int)(DateTimeOffset.UtcNow - timestamp.Value).TotalSeconds).ToString();
static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

internal sealed record PrototypeOptions(bool Active, bool Diagnose, bool CaptureState, bool Once, SimulatedState Simulated)
{
    public static PrototypeOptions Parse(string[] args)
    {
        var active = args.Contains("--active", StringComparer.OrdinalIgnoreCase);
        var diagnose = args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase);
        var capture = args.Contains("--capture-state", StringComparer.OrdinalIgnoreCase) || args.Contains("--capture-templates", StringComparer.OrdinalIgnoreCase);
        var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
        var simulation = SimulatedState.None;
        var index = Array.FindIndex(args, argument => argument.Equals("--simulate", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length)
        {
            var requested = args[index + 1].Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
            simulation = Enum.GetValues<SimulatedState>().FirstOrDefault(value => value.ToString().Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (simulation == SimulatedState.None && !requested.Equals("none", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Unknown simulation.");
        }
        return new(active, diagnose, capture, once, simulation);
    }
}

internal sealed record WatchdogSample(
    IReadOnlyList<ProcessSnapshot> Processes,
    ProcessSnapshot? Cod,
    ProcessSnapshot? PrimaryBattleNet,
    IReadOnlyList<ProcessSnapshot> BattleNetProcesses,
    IReadOnlyList<ProcessSnapshot> Agents,
    BattleNetSignals Signals,
    WatchdogObservation Observation);

internal sealed class WatchdogReporter
{
    private readonly WatchdogLogger logger;
    private int? previousCodPid;
    private int? previousBattleNetPid;
    private WatchdogState? previousState;
    private string? previousHealth;
    private bool? previousUpdateHint;
    private DateTimeOffset nextHeartbeatUtc = DateTimeOffset.MinValue;
    public WatchdogReporter(WatchdogLogger logger) => this.logger = logger;
    public void Report(WatchdogTransition transition, WatchdogSample sample)
    {
        if (previousState != transition.Current)
        {
            logger.Write("WATCHDOG_STATE", $"old={transition.Previous} new={transition.Current}");
            Console.WriteLine($"WATCHDOG_STATE {transition.Current}");
            previousState = transition.Current;
        }
        LogProcessChange("COD", sample.Cod, ref previousCodPid);
        LogProcessChange("BNET", sample.PrimaryBattleNet, ref previousBattleNetPid);
        var health = HealthCategory(transition.Current);
        if (health != previousHealth)
        {
            logger.Write(health, HealthDetail(sample.Observation));
            previousHealth = health;
        }
        if (previousUpdateHint != sample.Signals.UpdateKeywordsObserved)
        {
            logger.Write("UPDATE_SIGNAL_ADVISORY", $"recentLogs={sample.Signals.RecentSignalFiles.Count} activeState=false");
            previousUpdateHint = sample.Signals.UpdateKeywordsObserved;
        }
        if (DateTimeOffset.UtcNow >= nextHeartbeatUtc)
        {
            logger.Write("WATCHDOG_HEARTBEAT", $"state={transition.Current}");
            nextHeartbeatUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        if (transition.Previous != transition.Current)
        {
            foreach (var action in transition.PlannedActions)
            {
                logger.Write("WATCHDOG_ACTION_PLANNED", action);
                Console.WriteLine($"WATCHDOG_ACTION_PLANNED {action}");
            }
        }
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
        _ => "DATA_NOT_EVALUATED"
    };
    private static string HealthDetail(WatchdogObservation observation) => observation.LastValidCollectionUtc is not { } cycle
        ? "ageSeconds=unknown"
        : $"ageSeconds={Math.Max(0, (int)(DateTimeOffset.UtcNow - cycle).TotalSeconds)}";
}

internal sealed class WatchdogLogger
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CODMunity", "CODWatchdogPrototype", "logs", "watchdog.log");
    public void Write(string eventName, string? detail = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {eventName}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail)}{Environment.NewLine}");
    }
}
