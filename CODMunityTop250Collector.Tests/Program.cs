using CODMunityTop250Collector.Core;
using CODMunityTop250Collector.Diagnostics;
using CODMunityTop250Collector.ReleaseTool;
using CODMunityTop250Collector.App;
using CODMunityTop250Collector.UpdateHost;
using CODMunityTop250Collector.Updater;
using ReleaseManifest = CODMunityTop250Collector.ReleaseTool.ReleaseManifest;
using ManifestJson = CODMunityTop250Collector.ReleaseTool.ManifestJson;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
var root = Path.Combine(Path.GetTempPath(), "CODMunityTop250CollectorTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); CollectorPaths.RootOverride = root;
if (args.Length == 1 && string.Equals(args[0], "--live-api-sync", StringComparison.Ordinal))
{
    try { await LiveApiSyncFromStandardInput(); }
    finally { CollectorPaths.RootOverride = null; Directory.Delete(root, true); }
    return;
}
try
{
    var log = new AppLogger(); var parser = new EngineStatusParser(); var store = new SettingsStore(log);
    store.Save(new CollectorSettings(false, true, true)); if (store.Load() != new CollectorSettings(false, true, true)) throw new Exception("settings load/save");
    File.WriteAllText(CollectorPaths.Settings, "{\"CollectorEnabled\":true,\"StartWithWindows\":false,\"HistoryRetentionDays\":7}");
    if (store.Load() != new CollectorSettings(true, false)) throw new Exception("legacy settings compatibility");
    File.WriteAllText(CollectorPaths.Settings, "{bad json"); if (store.Load() != new CollectorSettings()) throw new Exception("corrupt settings recovery"); if (!Directory.EnumerateFiles(root, "settings.corrupt.*.json").Any()) throw new Exception("corrupt backup");
    var json = "{\"capturedAt\":\"2026-01-01T00:00:00Z\",\"platform\":\"steam\",\"layout\":\"steam-current\",\"cycleStatus\":\"OK\",\"codSha256\":\"test-build-sha\",\"datasets\":{\"mp\":{\"status\":\"OK\",\"rows\":250},\"br\":{\"status\":\"NOT_READY\"},\"resurgence\":{\"status\":\"OK\",\"rows\":250}}}";
    var snapshot = parser.Parse(json, true, DateTimeOffset.Parse("2025-12-31T23:59:59Z")); if (snapshot.Platform != "steam" || snapshot.Mp?.Players != 250 || snapshot.Br?.Status != "NOT_READY" || snapshot.BuildIdentity != "test-build-sha") throw new Exception("status parsing");
    var supportInfo = CollectorSupportInfo.Format(snapshot, "0.1.25-alpha", "8.0.30", ".NET 8.0.30", "Microsoft Windows", "X64");
    if (!supportInfo.Contains("collectorVersion=0.1.25-alpha", StringComparison.Ordinal) || !supportInfo.Contains("runtimeVersion=8.0.30", StringComparison.Ordinal) || !supportInfo.Contains("gameBuildIdentity=test-build-sha", StringComparison.Ordinal) || !supportInfo.Contains("layoutDiscoveryStatus=resolved", StringComparison.Ordinal) || !supportInfo.Contains("lastCollectionResult=OK", StringComparison.Ordinal) || !supportInfo.Contains("lastApiSyncResult=resurgence:NotConfigured", StringComparison.Ordinal)) throw new Exception("support build info fields");
    LayoutDiscoveryUiStateTests(parser);
    var info = parser.CopyBuildInfo("{\"platform\":\"xbox\",\"runtimeBuildId\":\"pe64-test\"}", "0.1.0-alpha"); if (info.Contains(Environment.UserName) || !info.Contains("runtimeBuildId=pe64-test")) throw new Exception("build info sanitization");
    var localHydratedTime = DateTimeOffset.Parse("2026-01-01T00:00:03Z").ToLocalTime();
    var ptBr = CultureInfo.GetCultureInfo("pt-BR");
    var enUs = CultureInfo.GetCultureInfo("en-US");
    var ptBrStatus = localHydratedTime.ToString("G", ptBr);
    var enUsStatus = localHydratedTime.ToString("G", enUs);
    var ptBrCard = localHydratedTime.ToString("g", ptBr);
    if (!ptBrStatus.Contains(localHydratedTime.ToString("d", ptBr)) || !ptBrStatus.Contains(localHydratedTime.ToString("T", ptBr)) || !enUsStatus.Contains(localHydratedTime.ToString("d", enUs)) || !enUsStatus.Contains(localHydratedTime.ToString("T", enUs)) || !ptBrCard.Contains(localHydratedTime.ToString("d", ptBr)) || !ptBrCard.Contains(localHydratedTime.ToString("t", ptBr))) throw new Exception("localized timestamp formatting");
    StartupRegistrationTests();
    StationWatcherTests().GetAwaiter().GetResult();
    SafeModeNativeDialogContractTests();
    LayoutDiscoveryContractTests();
    LayoutEvidenceCaptureContractTests();
    LiveEvidenceCaptureContractTests();
    LiveRowEvidenceCaptureContractTests();
    TemporalEvidenceCaptureContractTests();
    InstallerLaunchContractTests();
    ReleaseSigningTests(root);
    PrivateUpdaterTests(root);
    AutomaticUpdateTests(root);
    File.WriteAllBytes(Path.Combine(CollectorPaths.Logs, "app.log"), new byte[5 * 1024 * 1024]); log.Write("TEST"); if (!File.Exists(Path.Combine(CollectorPaths.Logs, "app.1.log"))) throw new Exception("log rotation");
    ApiSyncTests(log, root).GetAwaiter().GetResult();
    UnicodeMachineReadableTransportTests();
    BuildIdentityCacheTests(log).GetAwaiter().GetResult();
    SchedulerBuildIdentityIntegrationTests(log).GetAwaiter().GetResult();
    SchedulerApiPriorityTests(log).GetAwaiter().GetResult();
    EngineManagedIdentityContractTests();
    Console.WriteLine("PASS settings compatibility, station watcher state safety, startup registration reconciliation, installer launch contract, in-memory engine status parsing, API sync validation/retry safety, build identity cache safety, release signing/verification safety, private updater trust/download safety, automatic update coordination, UpdateHost safety, log rotation");
}
finally { CollectorPaths.RootOverride = null; Directory.Delete(root, true); }



static void SafeModeNativeDialogContractTests()
{
    // Real COD observation: class #32770, direct cod.exe PID, owner=0, IDs 6/7/2.
    // Labels deliberately are not an input to this contract.
    if (!SafeModeDialogRules.IsCandidate(true, 14804, 14804, "#32770", true, true, true))
        throw new Exception("ownerless COD Safe Mode dialog was rejected");
    if (SafeModeDialogRules.IsCandidate(false, 14804, 14804, "#32770", true, true, true))
        throw new Exception("Safe Mode dialog outside launch context was accepted");
    if (SafeModeDialogRules.IsCandidate(true, 14804, 99, "#32770", true, true, true))
        throw new Exception("non-COD dialog was accepted");
    if (SafeModeDialogRules.IsCandidate(true, 14804, 14804, "#32770", true, true, false))
        throw new Exception("incomplete dialog signature was accepted");

    var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var platformSource = File.ReadAllText(Path.Combine(repositoryRoot, "CODMunityTop250Collector.App", "WindowsStationWatcherPlatform.cs"));
    var appSource = File.ReadAllText(Path.Combine(repositoryRoot, "CODMunityTop250Collector.App", "App.xaml.cs"));
    var removedStaleOverrideVariable = "CODMUNITY_WATCHER_TEST_" + "STALE_SECONDS";
    var removedStaleOverrideEvent = "WATCHER_TEST_STALE_" + "OVERRIDE";
    if (appSource.Contains(removedStaleOverrideVariable, StringComparison.Ordinal) || appSource.Contains(removedStaleOverrideEvent, StringComparison.Ordinal)) throw new Exception("Temporary Watcher stale override remains.");
    if (!platformSource.Contains("SafeModeDialogRules.IdNo", StringComparison.Ordinal) ||
        !platformSource.Contains("handledSafeModeWindow", StringComparison.Ordinal) ||
        !platformSource.Contains("WATCHER_BNET_PROCESS_ONLY", StringComparison.Ordinal) ||
        !platformSource.Contains("WATCHER_BNET_RESTORE_REQUESTED", StringComparison.Ordinal) ||
        !platformSource.Contains("nextRestoreUriUtc", StringComparison.Ordinal) ||
        !appSource.Contains("ToolStripMenuItem(\"Watcher\")", StringComparison.Ordinal) ||
        !appSource.Contains("WatcherEnabled = trayWatcher.Checked", StringComparison.Ordinal) ||
        !appSource.Contains("trayWatcher.Checked = settings.WatcherEnabled", StringComparison.Ordinal) ||
        platformSource.Contains("FindChildButton(dialog, \"No\")", StringComparison.Ordinal) ||
        platformSource.Contains("GwOwner", StringComparison.Ordinal))
        throw new Exception("Safe Mode native dialog implementation contract");
}
static async Task StationWatcherTests()
{
    var now = DateTimeOffset.Parse("2026-08-30T00:00:00Z");
    var config = new StationWatcherConfiguration(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), 3);
    CollectorSettings settings = new(CollectorEnabled: true, StartWithWindows: false, WatcherEnabled: false);
    CollectorSnapshot snapshot = new(true);
    var platform = new FakeStationWatcherPlatform();
    var watcher = new StationWatcher(() => settings, () => snapshot, platform, (_, _) => { }, () => now, config);
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    if (watcher.Snapshot.State != StationWatcherState.Disabled || platform.LaunchCalls != 0 || platform.CloseCalls != 0) throw new Exception("disabled watcher acted");

    settings = settings with { WatcherEnabled = true };
    var startupSafePlatform = new FakeStationWatcherPlatform { Cod = new StationCodProcess(100, now), SafeModeResult = SafeModeDismissResult.Dismissed };
    var startupSafeWatcher = new StationWatcher(() => settings with { WatcherEnabled = true }, () => snapshot, startupSafePlatform, (_, _) => { }, () => now, config);
    await startupSafeWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (startupSafePlatform.SafeModeCalls != 1 || startupSafePlatform.LaunchCalls != 0) throw new Exception("pre-existing Safe Mode was not handled without Battle.net launch");
    platform.LaunchResults.Enqueue(BattleNetLaunchProgress.PlayClicked);
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    if (watcher.Snapshot.State != StationWatcherState.WaitingForCod || platform.LaunchCalls != 1) throw new Exception("COD missing did not enter Battle.net Play flow");
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    if (platform.LaunchCalls != 1) throw new Exception("Play was repeated while waiting for COD");

    now = now.AddMinutes(1);
    var retryPlatform = new FakeStationWatcherPlatform();
    var retryWatcher = new StationWatcher(() => settings, () => snapshot, retryPlatform, (_, _) => { }, () => now, config);
    retryPlatform.LaunchResults.Enqueue(BattleNetLaunchProgress.PlayUnavailable);
    await retryWatcher.EvaluateOnceAsync(CancellationToken.None);
    now = now.AddSeconds(59);
    await retryWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (retryPlatform.LaunchCalls != 1) throw new Exception("Play-unavailable retry spam");
    now = now.AddSeconds(2);
    retryPlatform.LaunchResults.Enqueue(BattleNetLaunchProgress.PlayClicked);
    await retryWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (retryPlatform.LaunchCalls != 2) throw new Exception("Play-unavailable retry missing");

    now = now.AddMinutes(1);
    platform.Cod = new StationCodProcess(301, now);
    platform.SafeModeResult = SafeModeDismissResult.Dismissed;
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    if (watcher.Snapshot.State != StationWatcherState.WaitingForCod || platform.SafeModeCalls != 1 || platform.ForegroundCalls != 0) throw new Exception("Safe Mode did not remain inside the current launch attempt");
    platform.Cod = null;
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    if (watcher.Snapshot.State != StationWatcherState.WaitingForCod || platform.LaunchCalls != 1) throw new Exception("Safe Mode launch attempted a second Battle.net Play click");
    platform.Cod = new StationCodProcess(302, now);
    platform.SafeModeResult = SafeModeDismissResult.NotPresent;
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    if (watcher.Snapshot.State != StationWatcherState.WaitingForData || platform.SafeModeCalls < 3 || platform.ForegroundCalls != 1) throw new Exception("COD launch transition / Safe Mode handling");
    platform.SafeModeResult = SafeModeDismissResult.Dismissed;
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    if (watcher.Snapshot.State != StationWatcherState.WaitingForCod || platform.LaunchCalls != 1) throw new Exception("Safe Mode during WaitingForData restarted Battle.net Play");
    platform.SafeModeResult = SafeModeDismissResult.NotPresent;
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    await watcher.EvaluateOnceAsync(CancellationToken.None);

    snapshot = new CollectorSnapshot(true, Game: "Running", LastSuccess: now);
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    if (watcher.Snapshot.State != StationWatcherState.Healthy) throw new Exception("new local collection did not make watcher healthy");
    await watcher.EvaluateOnceAsync(CancellationToken.None);
    if (platform.ForegroundCalls != 1) throw new Exception("watcher repeatedly forced foreground");
    var gateCycleStarted = now.AddSeconds(-5);
    var gateSnapshot = new CollectorSnapshot(true, Game: "Running", LastSuccess: now.AddMinutes(-11), CycleStatus: "RUNNING", CycleStartedAt: gateCycleStarted);
    var gatePlatform = new FakeStationWatcherPlatform { Cod = new StationCodProcess(350, now.AddMinutes(-30)), InternetAvailable = true, ClearCodOnClose = true };
    var gateWatcher = new StationWatcher(() => settings, () => gateSnapshot, gatePlatform, (_, _) => { }, () => now, config);
    await gateWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (gatePlatform.CloseCalls != 0 || gateWatcher.Snapshot.State != StationWatcherState.WaitingForData) throw new Exception("recent collection did not defer stale recovery");
    gateSnapshot = gateSnapshot with { CycleStatus = "OK", CycleStartedAt = null, LastSuccess = now };
    await gateWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (gatePlatform.CloseCalls != 0 || gateWatcher.Snapshot.State != StationWatcherState.Healthy) throw new Exception("successful deferred collection did not cancel recovery");

    gateSnapshot = new CollectorSnapshot(true, Game: "Running", LastSuccess: now.AddMinutes(-11), CycleStatus: "RUNNING", CycleStartedAt: gateCycleStarted);
    gatePlatform = new FakeStationWatcherPlatform { Cod = new StationCodProcess(351, now.AddMinutes(-30)), InternetAvailable = true, ClearCodOnClose = true };
    gateWatcher = new StationWatcher(() => settings, () => gateSnapshot, gatePlatform, (_, _) => { }, () => now.AddSeconds(61), config);
    gatePlatform.LaunchResults.Enqueue(BattleNetLaunchProgress.PlayClicked);
    await gateWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (gatePlatform.CloseCalls != 1 || gatePlatform.LaunchCalls != 1) throw new Exception("stuck collection beyond grace did not recover");

    now = now.AddMinutes(1);
    var apiFailurePlatform = new FakeStationWatcherPlatform { Cod = new StationCodProcess(401, now.AddMinutes(-30)), InternetAvailable = true };
    var apiFailureSnapshot = new CollectorSnapshot(true, Game: "Running", LastSuccess: now, Mp: new DatasetStatus("OK", 250, ApiSync: ApiSyncState.Failed));
    var apiFailureWatcher = new StationWatcher(() => settings, () => apiFailureSnapshot, apiFailurePlatform, (_, _) => { }, () => now, config);
    await apiFailureWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (apiFailureWatcher.Snapshot.State != StationWatcherState.Healthy || apiFailurePlatform.CloseCalls != 0) throw new Exception("API failure triggered COD recovery");

    now = now.AddMinutes(1);
    var offlinePlatform = new FakeStationWatcherPlatform { Cod = new StationCodProcess(501, now.AddMinutes(-30)), InternetAvailable = false };
    var staleSnapshot = new CollectorSnapshot(true, Game: "Running", LastSuccess: now.AddMinutes(-11));
    var offlineWatcher = new StationWatcher(() => settings, () => staleSnapshot, offlinePlatform, (_, _) => { }, () => now, config);
    await offlineWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (offlineWatcher.Snapshot.State != StationWatcherState.Offline || offlinePlatform.CloseCalls != 0) throw new Exception("offline stale collection restarted COD");

    now = now.AddMinutes(1);
    var recoveryPlatform = new FakeStationWatcherPlatform { Cod = new StationCodProcess(601, now.AddMinutes(-30)), InternetAvailable = true, ClearCodOnClose = true, UiResponsive = false, SafeModeResult = SafeModeDismissResult.Dismissed };
    recoveryPlatform.LaunchResults.Enqueue(BattleNetLaunchProgress.PlayClicked);
    var recoverySnapshot = new CollectorSnapshot(true, Game: "Running", LastSuccess: now.AddMinutes(-11));
    var recoveryWatcher = new StationWatcher(() => settings, () => recoverySnapshot, recoveryPlatform, (_, _) => { }, () => now, config);
    await recoveryWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (recoveryPlatform.CloseCalls != 1 || recoveryPlatform.LaunchCalls != 1 || recoveryWatcher.Snapshot.State != StationWatcherState.WaitingForCod) throw new Exception("stale hung process did not exit before Battle.net launch");
    recoveryPlatform.Cod = new StationCodProcess(602, now);
    await recoveryWatcher.EvaluateOnceAsync(CancellationToken.None);
    recoveryPlatform.SafeModeResult = SafeModeDismissResult.NotPresent;
    await recoveryWatcher.EvaluateOnceAsync(CancellationToken.None);
    recoverySnapshot = recoverySnapshot with { LastSuccess = now };
    await recoveryWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (recoveryWatcher.Snapshot.State != StationWatcherState.RecoveryCooldown) throw new Exception("recovery did not require new local collection/cooldown");
    now = now.AddMinutes(1);
    recoverySnapshot = recoverySnapshot with { LastSuccess = now.AddMinutes(-11) };
    await recoveryWatcher.EvaluateOnceAsync(CancellationToken.None);
    if (recoveryPlatform.CloseCalls != 1) throw new Exception("cooldown did not block immediate second recovery");

    now = now.AddMinutes(1);
    var failureConfig = config with { StaleAfter = TimeSpan.FromSeconds(1), LaunchRetryAfter = TimeSpan.FromSeconds(2) };
    var crashPlatform = new FakeStationWatcherPlatform { Cod = new StationCodProcess(701, now.AddMinutes(-30)), InternetAvailable = true, CloseResult = false };
    var crashSnapshot = new CollectorSnapshot(true, Game: "Running", LastSuccess: now.AddMinutes(-11));
    var crashWatcher = new StationWatcher(() => settings, () => crashSnapshot, crashPlatform, (_, _) => { }, () => now, failureConfig);
    for (var attempt = 0; attempt < 3; attempt++)
    {
        await crashWatcher.EvaluateOnceAsync(CancellationToken.None);
        now = now.AddSeconds(3);
    }
    if (crashPlatform.CloseCalls != 3 || crashPlatform.LaunchCalls != 0 || crashWatcher.Snapshot.State != StationWatcherState.CrashLoopBackoff) throw new Exception("kill failure proceeded to Battle.net or skipped crash-loop backoff");

    foreach (var unresolvedCycleStatus in new[] { "UNSUPPORTED_BUILD", "LAYOUT_DISCOVERY_FAILED", "LAYOUT_DISCOVERY_AMBIGUOUS" })
    {
        var unsupportedPlatform = new FakeStationWatcherPlatform { Cod = new StationCodProcess(801, now.AddMinutes(-30)), InternetAvailable = true };
        var unsupportedSnapshot = new CollectorSnapshot(true, Game: "Running", LastSuccess: now.AddMinutes(-11), CycleStatus: unresolvedCycleStatus);
        var unsupportedWatcher = new StationWatcher(() => settings, () => unsupportedSnapshot, unsupportedPlatform, (_, _) => { }, () => now, config);
        await unsupportedWatcher.EvaluateOnceAsync(CancellationToken.None);
        if (unsupportedWatcher.Snapshot.State != StationWatcherState.LayoutUnsupported || unsupportedPlatform.CloseCalls != 0 || unsupportedPlatform.LaunchCalls != 0) throw new Exception("unresolved layout caused Watcher recovery: " + unresolvedCycleStatus);
    }
}
static void LayoutDiscoveryUiStateTests(EngineStatusParser parser)
{
    const string status = "{\"capturedAt\":\"2026-01-01T00:00:00Z\",\"platform\":\"battlenet\",\"codSha256\":\"new-build\",\"layout\":\"unknown\",\"cycleStatus\":\"UNSUPPORTED_BUILD\",\"datasets\":{}}";
    var started = DateTimeOffset.Parse("2025-12-31T23:59:59Z");
    var pending = parser.Parse(status, true, started);
    if (pending.Build != "LayoutDiscoveryPending" || pending.LayoutDiscoveryStatus != "pending") throw new Exception("unknown build discovery pending state");
    var failed = parser.Parse(status.Replace("UNSUPPORTED_BUILD", "LAYOUT_DISCOVERY_FAILED", StringComparison.Ordinal), true, started);
    if (failed.Build != "LayoutUnsupported" || failed.LayoutDiscoveryStatus != "unsupported") throw new Exception("discovery failed state");
    var ambiguous = parser.Parse(status.Replace("UNSUPPORTED_BUILD", "LAYOUT_DISCOVERY_AMBIGUOUS", StringComparison.Ordinal), true, started);
    if (ambiguous.Build != "LayoutAmbiguous" || ambiguous.LayoutDiscoveryStatus != "ambiguous") throw new Exception("discovery ambiguous state");
    var malformed = parser.Parse(status.Replace("\"cycleStatus\":\"UNSUPPORTED_BUILD\"", "\"layoutDiscoveryStatus\":\"unexpected\",\"cycleStatus\":\"WAITING_FOR_CACHE\"", StringComparison.Ordinal), true, started);
    if (malformed.Build != "LayoutUnsupported" || malformed.LayoutDiscoveryStatus != "unsupported") throw new Exception("malformed discovery status did not fail closed");
    var resolved = parser.Parse(status.Replace("\"layout\":\"unknown\",\"cycleStatus\":\"UNSUPPORTED_BUILD\"", "\"layout\":\"bnet-autodiscovered\",\"layoutDiscoveryStatus\":\"resolved\",\"cycleStatus\":\"WAITING_FOR_CACHE\"", StringComparison.Ordinal), true, started);
    if (resolved.Build != "Supported" || resolved.LayoutDiscoveryStatus != "resolved") throw new Exception("resolved discovered layout state");
    var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var windowSource = File.ReadAllText(Path.Combine(repositoryRoot, "CODMunityTop250Collector.App", "MainWindow.xaml.cs"));
    if (!windowSource.Contains("New game build detected", StringComparison.Ordinal) || !windowSource.Contains("Waiting for layout discovery.", StringComparison.Ordinal) || !windowSource.Contains("Collection will resume automatically when the layout is validated.", StringComparison.Ordinal)) throw new Exception("layout discovery pending UI copy");
}
static void LayoutDiscoveryContractTests()
{
    var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var engineRoot = Path.Combine(repositoryRoot, "engine");
    var modulePath = Path.Combine(engineRoot, "layout_discovery.psm1");
    var orchestratorPath = Path.Combine(engineRoot, "top250_orchestrator.ps1");
    var manifestPath = Path.Combine(engineRoot, "build_layouts.json");
    if (!File.Exists(modulePath) || !File.Exists(orchestratorPath)) throw new Exception("layout discovery module missing");
    var orchestrator = File.ReadAllText(orchestratorPath);
    if (!orchestrator.Contains("Get-BuildLayout -Manifest $manifest -Identity $activeIdentity", StringComparison.Ordinal) ||
        !orchestrator.Contains("Resolve-UnknownBnetLayout", StringComparison.Ordinal) ||
        !orchestrator.Contains("LAYOUT_DISCOVERY_AMBIGUOUS", StringComparison.Ordinal) ||
        !orchestrator.Contains("LAYOUT_CACHE_REJECTED", StringComparison.Ordinal) ||
        orchestrator.Contains("Get-BnetLayoutFromSignature", StringComparison.Ordinal))
        throw new Exception("layout discovery fail-closed routing contract");
    using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
    var known = manifest.RootElement.GetProperty("sha256Builds").GetProperty("97413B95AA94A9694690BA70B16189E4827E2B363850F873387E8A88101D4FE4");
    if (known.GetProperty("layout").GetString() != "bnet-20260820" || known.GetProperty("exporterSet").GetString() != "bnet-dynamic" ||
        manifest.RootElement.GetProperty("battlenetDiscovery").GetProperty("methods").GetArrayLength() != 0)
        throw new Exception("known build bypass or untrusted discovery catalog contract");
    var season6 = manifest.RootElement.GetProperty("sha256Builds").GetProperty("DA8DE033EFE34B77DA0D6014EFFA9C551C0E30A3CFB4A3B39A52D6B86AD03A09");
    var season6Definition = manifest.RootElement.GetProperty("battlenetLayoutDefinitions").GetProperty("bnet-20260918").GetProperty("datasets");
    if (season6.GetProperty("layout").GetString() != "bnet-20260918" || season6.GetProperty("exporterSet").GetString() != "bnet-dynamic" ||
        season6Definition.GetProperty("mp").GetProperty("stateRva").GetString() != "0x12BDED10" || season6Definition.GetProperty("mp").GetProperty("countRva").GetString() != "0x12C1A45C" || season6Definition.GetProperty("mp").GetProperty("entry0Rva").GetString() != "0x12C1A460" ||
        season6Definition.GetProperty("br").GetProperty("stateRva").GetString() != "0x12C60E00" || season6Definition.GetProperty("br").GetProperty("countRva").GetString() != "0x12C9C54C" || season6Definition.GetProperty("br").GetProperty("entry0Rva").GetString() != "0x12C9C550" ||
        season6Definition.GetProperty("resurgence").GetProperty("stateRva").GetString() != "0x12CE2EF0" || season6Definition.GetProperty("resurgence").GetProperty("countRva").GetString() != "0x12D1E63C" || season6Definition.GetProperty("resurgence").GetProperty("entry0Rva").GetString() != "0x12D1E640")
        throw new Exception("season 06 static Battle.net layout contract");
    var testScript = Path.Combine(repositoryRoot, "tools", "test-layout-discovery.ps1");
    var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = repositoryRoot
    };
    psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass"); psi.ArgumentList.Add("-File"); psi.ArgumentList.Add(testScript);
    using var process = Process.Start(psi) ?? throw new Exception("layout discovery test did not start");
    var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
    if (process.ExitCode != 0 || !stdout.Contains("LAYOUT_DISCOVERY_TESTS_OK", StringComparison.Ordinal)) throw new Exception("layout discovery tests failed: " + stderr);
}
static void LayoutEvidenceCaptureContractTests()
{
    var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var capturePath = Path.Combine(repositoryRoot, "tools", "capture-layout-evidence.ps1");
    var workflowPath = Path.Combine(repositoryRoot, "docs", "BATTLE-NET-LAYOUT-EVIDENCE.md");
    var capture = File.ReadAllText(capturePath);
    var workflow = File.ReadAllText(workflowPath);
    if (!capture.Contains("static-file-only", StringComparison.Ordinal) ||
        !capture.Contains("RVA_NOT_FILE_BACKED", StringComparison.Ordinal) ||
        !capture.Contains("Find-NearbyRipRelativeReferences", StringComparison.Ordinal) ||
        !capture.Contains("97413B95AA94A9694690BA70B16189E4827E2B363850F873387E8A88101D4FE4", StringComparison.Ordinal) ||
        capture.Contains("ReadProcessMemory", StringComparison.Ordinal) || capture.Contains("OpenProcess", StringComparison.Ordinal) || capture.Contains("Get-Process", StringComparison.Ordinal) ||
        !workflow.Contains("Next COD update procedure", StringComparison.Ordinal) || !workflow.Contains("fail-closed", StringComparison.Ordinal))
        throw new Exception("layout evidence capture safety contract");
}
static void LiveEvidenceCaptureContractTests()
{
    var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var capturePath = Path.Combine(repositoryRoot, "tools", "capture-live-layout-evidence.ps1");
    var capture = File.ReadAllText(capturePath);
    if (!capture.Contains("PROCESS_VM_READ", StringComparison.Ordinal) || !capture.Contains("PROCESS_QUERY_LIMITED_INFORMATION", StringComparison.Ordinal) ||
        !capture.Contains("LIVE_EVIDENCE_SKIPPED reason=COD_NOT_RUNNING", StringComparison.Ordinal) || !capture.Contains("playerDataStored=$false", StringComparison.Ordinal) ||
        !capture.Contains("entry0BytesRedacted=$true", StringComparison.Ordinal) || !capture.Contains("reference-analysis.json", StringComparison.Ordinal) || capture.Contains("$LASTEXITCODE", StringComparison.Ordinal) || capture.Contains("HashData", StringComparison.Ordinal) || capture.Contains("ToHexString", StringComparison.Ordinal) ||
        capture.Contains("WriteProcessMemory", StringComparison.Ordinal) || capture.Contains("CreateRemoteThread", StringComparison.Ordinal) ||
        capture.Contains("VirtualProtectEx", StringComparison.Ordinal) || capture.Contains("Process.Start", StringComparison.Ordinal))
        throw new Exception("live evidence capture read-only safety contract");
}
static void LiveRowEvidenceCaptureContractTests()
{
    var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var capturePath = Path.Combine(repositoryRoot, "tools", "capture-live-row-evidence.ps1");
    var capture = File.ReadAllText(capturePath);
    if (!capture.Contains("FirstVisibleName", StringComparison.Ordinal) || !capture.Contains("SecondVisibleName", StringComparison.Ordinal) ||
        !capture.Contains("PROCESS_VM_READ", StringComparison.Ordinal) || !capture.Contains("playerNamesStored = $false", StringComparison.Ordinal) ||
        !capture.Contains("playerXuidsStored = $false", StringComparison.Ordinal) || !capture.Contains("playerRecordBytesStored = $false", StringComparison.Ordinal) ||
        !capture.Contains("PointerHitLimitReached", StringComparison.Ordinal) || capture.Contains("WriteProcessMemory", StringComparison.Ordinal) ||
        capture.Contains("CreateRemoteThread", StringComparison.Ordinal) || capture.Contains("VirtualProtectEx", StringComparison.Ordinal) ||
        capture.Contains("Process.Start", StringComparison.Ordinal))
        throw new Exception("live row evidence capture read-only safety contract");
}
static void TemporalEvidenceCaptureContractTests()
{
    var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var capture = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "capture-layout-temporal-baseline.ps1"));
    var comparison = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "compare-layout-temporal-baselines.ps1"));
    var targetedCapture = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "capture-targeted-temporal-evidence.ps1"));
    var targetedComparison = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "compare-targeted-temporal-evidence.ps1"));
    var targetedTriangulation = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "triangulate-targeted-temporal-evidence.ps1"));
    var scalarCapture = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "capture-top250-candidate-scalars.ps1"));
    var scalarComparison = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "compare-top250-candidate-scalars.ps1"));
    var recordContractProbe = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "probe-top250-record-contract.ps1"));
    if (!capture.Contains("season06_top250_empty_baseline", StringComparison.Ordinal) ||
        !capture.Contains("season06_top250_populated", StringComparison.Ordinal) ||
        !capture.Contains("PROCESS_VM_READ", StringComparison.Ordinal) ||
        !capture.Contains("moduleRelative = $true", StringComparison.Ordinal) ||
        !capture.Contains("writableCommittedOnly = $true", StringComparison.Ordinal) ||
        !capture.Contains("playerNamesStored = $false", StringComparison.Ordinal) ||
        !capture.Contains("playerXuidsStored = $false", StringComparison.Ordinal) ||
        !capture.Contains("playerRecordBytesStored = $false", StringComparison.Ordinal) ||
        capture.Contains("WriteProcessMemory", StringComparison.Ordinal) ||
        capture.Contains("CreateRemoteThread", StringComparison.Ordinal) ||
        capture.Contains("VirtualProtectEx", StringComparison.Ordinal) ||
        capture.Contains("Process.Start", StringComparison.Ordinal))
        throw new Exception("temporal evidence capture read-only privacy contract");
    if (!comparison.Contains("hash-changed", StringComparison.Ordinal) ||
        !comparison.Contains("newly-committed-or-topology-changed", StringComparison.Ordinal) ||
        !comparison.Contains("disappeared-or-topology-changed", StringComparison.Ordinal) ||
        !comparison.Contains("TEMPORAL_BASELINE_SHA_MISMATCH", StringComparison.Ordinal))
        throw new Exception("temporal evidence comparison contract");
    if (!targetedCapture.Contains("season06_targeted_empty", StringComparison.Ordinal) ||
        !targetedCapture.Contains("season06_targeted_populated", StringComparison.Ordinal) ||
        !targetedCapture.Contains("RvaStart = 0x128BE000L", StringComparison.Ordinal) ||
        !targetedCapture.Contains("RvaEndExclusive = 0x12DBE000L", StringComparison.Ordinal) ||
        !targetedCapture.Contains("BlockBytes = 4096", StringComparison.Ordinal) ||
        !targetedCapture.Contains("PROCESS_VM_READ", StringComparison.Ordinal) ||
        !targetedCapture.Contains("playerNamesStored=$false", StringComparison.Ordinal) ||
        !targetedCapture.Contains("playerXuidsStored=$false", StringComparison.Ordinal) ||
        !targetedCapture.Contains("playerRecordBytesStored=$false", StringComparison.Ordinal) ||
        targetedCapture.Contains("WriteProcessMemory", StringComparison.Ordinal) ||
        targetedCapture.Contains("CreateRemoteThread", StringComparison.Ordinal) ||
        targetedCapture.Contains("VirtualProtectEx", StringComparison.Ordinal) ||
        targetedCapture.Contains("Process.Start", StringComparison.Ordinal))
        throw new Exception("targeted temporal evidence capture read-only privacy contract");
    if (!targetedComparison.Contains("TARGETED_TEMPORAL_SCOPE_MISMATCH", StringComparison.Ordinal) ||
        !targetedComparison.Contains("hash-changed", StringComparison.Ordinal) ||
        !targetedComparison.Contains("unreadable", StringComparison.Ordinal))
        throw new Exception("targeted temporal comparison contract");
    if (!targetedTriangulation.Contains("TARGETED_TRIANGULATION_SCOPE_MISMATCH", StringComparison.Ordinal) ||
        !targetedTriangulation.Contains("all-three-different", StringComparison.Ordinal) ||
        !targetedTriangulation.Contains("rawPageBytesStored=$false", StringComparison.Ordinal) ||
        targetedTriangulation.Contains("ReadProcessMemory", StringComparison.Ordinal) ||
        targetedTriangulation.Contains("WriteProcessMemory", StringComparison.Ordinal))
        throw new Exception("targeted temporal triangulation privacy contract");
    var requiredCandidatePages = new[]
    {
        "0x129B6000L", "0x12A03000L", "0x12B0A000L", "0x12B9B000L", "0x12B9C000L", "0x12BDE000L",
        "0x12C60000L", "0x12CE2000L", "0x12D67000L", "0x12D80000L", "0x12D97000L", "0x12D99000L",
        "0x12D81000L", "0x12D94000L", "0x12D95000L", "0x12B24000L", "0x12C1B000L", "0x12C1C000L", "0x12C1D000L"
    };
    if (requiredCandidatePages.Any(page => !scalarCapture.Contains(page, StringComparison.Ordinal)) ||
        !scalarCapture.Contains("PROCESS_VM_READ", StringComparison.Ordinal) ||
        !scalarCapture.Contains("PROCESS_QUERY_LIMITED_INFORMATION", StringComparison.Ordinal) ||
        !scalarCapture.Contains("ObservedPopulation", StringComparison.Ordinal) ||
        !scalarCapture.Contains("PopulationVerified", StringComparison.Ordinal) ||
        !scalarCapture.Contains("UiState", StringComparison.Ordinal) ||
        !scalarCapture.Contains("rawPageBytesStored = $false", StringComparison.Ordinal) ||
        !scalarCapture.Contains("stringsStored = $false", StringComparison.Ordinal) ||
        !scalarCapture.Contains("playerNamesStored = $false", StringComparison.Ordinal) ||
        !scalarCapture.Contains("playerXuidsStored = $false", StringComparison.Ordinal) ||
        !scalarCapture.Contains("$value -le 250", StringComparison.Ordinal) ||
        scalarCapture.Contains("WriteProcessMemory", StringComparison.Ordinal) ||
        scalarCapture.Contains("CreateRemoteThread", StringComparison.Ordinal) ||
        scalarCapture.Contains("VirtualProtectEx", StringComparison.Ordinal) ||
        scalarCapture.Contains("Process.Start", StringComparison.Ordinal))
        throw new Exception("candidate scalar capture read-only privacy contract");
    if (!scalarComparison.Contains("CANDIDATE_SCALAR_SCOPE_MISMATCH", StringComparison.Ordinal) ||
        !scalarComparison.Contains("exact-population-follower", StringComparison.Ordinal) ||
        !scalarComparison.Contains("state-like-small-transition", StringComparison.Ordinal) ||
        !scalarComparison.Contains("ObservationOverrides", StringComparison.Ordinal) ||
        !scalarComparison.Contains("populationVerified", StringComparison.Ordinal) ||
        !scalarComparison.Contains("StableSecondThirdButFirstDifferent", StringComparison.Ordinal) ||
        !scalarComparison.Contains("rawPageBytesStored = $false", StringComparison.Ordinal))
        throw new Exception("candidate scalar comparison contract");
    if (!recordContractProbe.Contains("RvaStart = 0x128BE000L", StringComparison.Ordinal) ||
        !recordContractProbe.Contains("RvaEndExclusive = 0x12DBE000L", StringComparison.Ordinal) ||
        !recordContractProbe.Contains("recordStrideHypothesis = '0x458'", StringComparison.Ordinal) ||
        !recordContractProbe.Contains("PROCESS_VM_READ", StringComparison.Ordinal) ||
        !recordContractProbe.Contains("PROCESS_QUERY_LIMITED_INFORMATION", StringComparison.Ordinal) ||
        !recordContractProbe.Contains("playerNamesStored = $false", StringComparison.Ordinal) ||
        !recordContractProbe.Contains("playerXuidsStored = $false", StringComparison.Ordinal) ||
        recordContractProbe.Contains("WriteProcessMemory", StringComparison.Ordinal) ||
        recordContractProbe.Contains("CreateRemoteThread", StringComparison.Ordinal) ||
        recordContractProbe.Contains("VirtualProtectEx", StringComparison.Ordinal) ||
        recordContractProbe.Contains("Process.Start", StringComparison.Ordinal))
        throw new Exception("record contract probe read-only privacy contract");
}
static void InstallerLaunchContractTests()
{
    var installerDirectory = Path.Combine(AppContext.BaseDirectory, "Installer");
    var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var watcherSourceAsset = Path.Combine(repositoryRoot, "CODMunityTop250Collector.App", "Assets", "watchdog", "bnet-play.png");
    var validationHelper = Path.Combine(repositoryRoot, "tools", "validate-local-install.ps1");
    var validationScript = File.ReadAllText(validationHelper);
    if (!validationScript.Contains("-ReplaceAndValidate", StringComparison.Ordinal) || !validationScript.Contains("-uninstall", StringComparison.Ordinal) || !validationScript.Contains("Test-InstalledPayload", StringComparison.Ordinal) || !validationScript.Contains("Get-FileHash", StringComparison.Ordinal) || !validationScript.Contains("[IO.Path]::GetFullPath", StringComparison.Ordinal)) throw new Exception("same-version validation helper contract");

    var appProject = Path.Combine(repositoryRoot, "CODMunityTop250Collector.App", "CODMunityTop250Collector.App.csproj");
    if (!File.Exists(watcherSourceAsset) || !File.ReadAllText(appProject).Contains("<Content Include=\"Assets\\watchdog\\**\\*\">", StringComparison.Ordinal)) throw new Exception("Watcher template publish contract");
    var msi = File.ReadAllText(Path.Combine(installerDirectory, "Package.wxs"));
    const string launchCondition = "WIXUI_EXITDIALOGOPTIONALCHECKBOX = 1 AND UILevel &gt;= 4 AND NOT (REMOVE = &quot;ALL&quot;) AND NOT REINSTALL AND (NOT Installed OR WIX_UPGRADE_DETECTED)";
    if (!msi.Contains("CustomAction Id=\"LaunchCollector\" FileRef=\"CollectorExe\"", StringComparison.Ordinal) || !msi.Contains(launchCondition, StringComparison.Ordinal)) throw new Exception("MSI launch contract");
    if (!msi.Contains("Source=\"$(var.ProjectDir)..\\dist\\portable\\Assets\\watchdog\\bnet-play.png\"", StringComparison.Ordinal) || !msi.Contains("<Directory Id=\"WatchdogAssetsFolder\" Name=\"watchdog\">", StringComparison.Ordinal)) throw new Exception("MSI watcher template payload contract");
    if (!MsiLaunchAllowed(true, false, false, 5, false, false)) throw new Exception("MSI clean install launch contract");
    if (!MsiLaunchAllowed(true, true, true, 5, false, false)) throw new Exception("MSI major upgrade launch contract");
    if (MsiLaunchAllowed(true, false, false, 2, false, false)) throw new Exception("MSI silent launch contract");
    if (MsiLaunchAllowed(true, true, false, 5, false, true)) throw new Exception("MSI repair launch contract");
    if (MsiLaunchAllowed(true, true, false, 5, true, false)) throw new Exception("MSI uninstall launch contract");

    var bundle = File.ReadAllText(Path.Combine(installerDirectory, "Bundle.wxs"));
    if (!bundle.Contains("LaunchTarget=\"[LocalAppDataFolder]Programs\\CODMunity Top 250 Collector\\CODMunityTop250Collector.exe\"", StringComparison.Ordinal) ||
        !bundle.Contains("LaunchWorkingFolder=\"[LocalAppDataFolder]Programs\\CODMunity Top 250 Collector\"", StringComparison.Ordinal) ||
        !bundle.Contains("<MsiPackage Id=\"CODMunityTop250CollectorMsi\"", StringComparison.Ordinal) || !bundle.Contains("Visible=\"no\"", StringComparison.Ordinal)) throw new Exception("Setup bundle registration contract");
}

static void StartupRegistrationTests()
{
    const string currentExecutable = @"C:\Program Files\CODMunity\CODMunityTop250Collector.exe";
    if (WindowsStartupRegistration.FormatCommand(currentExecutable) != @"""C:\Program Files\CODMunity\CODMunityTop250Collector.exe""") throw new Exception("startup command quoting");

    var registration = new FakeStartupRegistration();
    var applier = new StartupSettingsApplier(registration);
    var initial = new CollectorSettings(CollectorEnabled: true, StartWithWindows: false);
    var settingsOn = initial with { StartWithWindows = true };
    if (applier.Apply(initial, settingsOn, currentExecutable) != StartupRegistrationResult.Registered || registration.Calls.Count != 1 || registration.Calls[0] != (true, currentExecutable)) throw new Exception("Settings startup enable registration");

    var trayOff = settingsOn with { StartWithWindows = false };
    if (applier.Apply(settingsOn, trayOff, currentExecutable) != StartupRegistrationResult.Removed || registration.Calls.Count != 2 || registration.Calls[1] != (false, currentExecutable)) throw new Exception("tray startup disable shared registration");

    var unrelatedCollectorChange = trayOff with { CollectorEnabled = false };
    if (applier.Apply(trayOff, unrelatedCollectorChange, currentExecutable) is not null || registration.Calls.Count != 2) throw new Exception("unrelated collector setting changed startup registration");

    registration.NextResult = StartupRegistrationResult.Repaired;
    if (applier.Apply(settingsOn, settingsOn, currentExecutable, reconcileOnStartup: true) != StartupRegistrationResult.Repaired || registration.Calls.Count != 3 || registration.Calls[2] != (true, currentExecutable)) throw new Exception("startup registration reconciliation");
}

static bool MsiLaunchAllowed(bool checkboxSelected, bool installed, bool majorUpgradeDetected, int uiLevel, bool removeAll, bool reinstall)
    => checkboxSelected && uiLevel >= 4 && !removeAll && !reinstall && (!installed || majorUpgradeDetected);

static async Task ApiSyncTests(AppLogger log, string root)
{
    var rows = Enumerable.Range(1, 250).Select(i => new RankedPlayer(i, 30000 - i, "player-" + i)).ToArray();
    if (!RankedPlayersApiClient.TryValidateNativeLeaderboard(rows, out _, out _)) throw new Exception("valid native leaderboard rejected");
    var tiesA = rows.Select((row, i) => i == 2 ? row with { Position = 2, Sr = rows[1].Sr } : row).ToArray(); // 1, 2, 2, 4, 5...
    var tiesB = rows.Select((row, i) => i == 3 ? row with { Position = 3, Sr = rows[2].Sr } : i == 4 ? row with { Position = 5 } : row).ToArray(); // 1, 2, 3, 3, 5...
    var multipleTies = rows.Select((row, i) => i is 3 or 4 ? row with { Position = 3, Sr = rows[2].Sr } : i == 5 ? row with { Position = 6 } : row).ToArray();
    var tiesAtObservedRanks = rows.Select((row, i) => i switch
    {
        50 => row with { Position = 50, Sr = rows[49].Sr },  // tie at ordinal 51; rank 51 is legitimately skipped
        128 => row with { Position = 128, Sr = rows[127].Sr },
        136 => row with { Position = 136, Sr = rows[135].Sr },
        193 => row with { Position = 193, Sr = rows[192].Sr },
        248 => row with { Position = 248, Sr = rows[247].Sr },
        _ => row
    }).ToArray();
    foreach (var tied in new[] { tiesA, tiesB, multipleTies, tiesAtObservedRanks })
        if (!RankedPlayersApiClient.TryValidateNativeLeaderboard(tied, out _, out _)) throw new Exception("valid COD tie ranking rejected");
    var ordinalPayload = RankedPlayersApiClient.MapToApiOrdinalPositions(tiesAtObservedRanks);
    if (!RankedPlayersApiClient.TryValidateApiRequestPlayers(ordinalPayload, out _, out _) || ordinalPayload.Select(p => p.Position).Distinct().Count() != 250 || !ordinalPayload.Select(p => p.Position).SequenceEqual(Enumerable.Range(1, 250))) throw new Exception("tied native ranks were not mapped to API ordinals");
    if (RankedPlayersApiClient.TryValidateApiRequestPlayers(tiesAtObservedRanks, out _, out _)) throw new Exception("native tied ranks accepted as API slots");
    if (RankedPlayersApiClient.TryValidatePlayers(rows.Select((p, i) => i == 0 ? p with { Position = 0 } : p), out _, out _)) throw new Exception("position zero accepted");
    if (RankedPlayersApiClient.TryValidatePlayers(rows.Select((p, i) => i == 249 ? p with { Position = 251 } : p), out _, out _)) throw new Exception("position 251 accepted");
    if (RankedPlayersApiClient.TryValidatePlayers(rows.Select((p, i) => i == 5 ? p with { Position = -1 } : p), out _, out _)) throw new Exception("negative position accepted");
    if (RankedPlayersApiClient.TryValidatePlayers(rows.Select((p, i) => i == 5 ? p with { Sr = 0 } : p), out _, out _)) throw new Exception("nonpositive SR accepted");
    if (RankedPlayersApiClient.TryValidatePlayers(rows.Select((p, i) => i == 5 ? p with { Name = " " } : p), out _, out _)) throw new Exception("empty name accepted");
    var partialRows = rows.Take(7).ToArray();
    if (!RankedPlayersApiClient.TryValidatePlayers(partialRows, out var validatedPartial, out _) || validatedPartial.Count != 7) throw new Exception("valid partial native leaderboard rejected");
    var partialOrdinalPayload = RankedPlayersApiClient.MapToApiOrdinalPositions(validatedPartial);
    if (!RankedPlayersApiClient.TryValidateApiRequestPlayers(partialOrdinalPayload, out _, out _) || !partialOrdinalPayload.Select(player => player.Position).SequenceEqual(Enumerable.Range(1, 7))) throw new Exception("partial ordinal API payload rejected");
    if (RankedPlayersApiClient.TryValidatePlayers(Array.Empty<RankedPlayer>(), out _, out _) || RankedPlayersApiClient.TryValidatePlayers(rows.Append(new RankedPlayer(251, 1, "extra")), out _, out _)) throw new Exception("invalid player count accepted");
    var handler = new RecordingHandler([HttpStatusCode.InternalServerError, HttpStatusCode.OK]);
    using var client = new RankedPlayersApiClient(new StaticTokenProvider("synthetic-token"), log, handler);
    var result = await client.SyncAsync("mp", rows, CancellationToken.None);
    if (result.State != ApiSyncState.Ok || handler.Calls != 2 || handler.Body?.Contains("xuid", StringComparison.OrdinalIgnoreCase) == true || handler.Authorization != "Bearer synthetic-token") throw new Exception("API retry/payload safety");
    using var modes = new RankedPlayersApiClient(new StaticTokenProvider("synthetic-token"), log, new RecordingHandler([HttpStatusCode.Created, HttpStatusCode.Accepted, HttpStatusCode.NoContent]));
    foreach (var mode in new[] { "mp", "br", "resurgence" }) if ((await modes.SyncAsync(mode, rows, CancellationToken.None)).State != ApiSyncState.Ok) throw new Exception("API mode/2xx handling");
    var tiedHandler = new RecordingHandler([HttpStatusCode.OK]);
    using var tiedClient = new RankedPlayersApiClient(new StaticTokenProvider("synthetic-token"), log, tiedHandler);
    if ((await tiedClient.SyncAsync("resurgence", tiesB, CancellationToken.None)).State != ApiSyncState.Ok) throw new Exception("tied ranking did not reach API client");
    AssertOrdinalApiPayload(tiedHandler.Body);
    var partialHandler = new RecordingHandler([HttpStatusCode.OK]);
    using var partialClient = new RankedPlayersApiClient(new StaticTokenProvider("synthetic-token"), log, partialHandler);
    if ((await partialClient.SyncAsync("mp", partialRows, CancellationToken.None)).State != ApiSyncState.Ok || partialHandler.Calls != 1) throw new Exception("partial leaderboard did not reach API client");
    AssertOrdinalApiPayload(partialHandler.Body, 7);
    var emptyHandler = new RecordingHandler([HttpStatusCode.OK]);
    using var emptyClient = new RankedPlayersApiClient(new StaticTokenProvider("synthetic-token"), log, emptyHandler);
    var emptyResult = await emptyClient.SyncAsync("br", Array.Empty<RankedPlayer>(), CancellationToken.None);
    if (emptyResult.State != ApiSyncState.Skipped || emptyResult.Reason != "empty-leaderboard" || emptyHandler.Calls != 0) throw new Exception("empty leaderboard performed API sync");
    var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var mainWindow = File.ReadAllText(Path.Combine(repositoryRoot, "CODMunityTop250Collector.App", "MainWindow.xaml.cs"));
    if (!mainWindow.Contains("No Top 250 players yet", StringComparison.Ordinal) || !mainWindow.Contains("No sync required", StringComparison.Ordinal)) throw new Exception("empty leaderboard UI contract");
    var runtimeFirst = new CompositeApiTokenProvider(new StaticTokenProvider("runtime-token"), new StaticTokenProvider("embedded-token"));
    var embeddedFallback = new CompositeApiTokenProvider(new StaticTokenProvider(" "), new StaticTokenProvider("embedded-token"));
    if (runtimeFirst.GetToken() != "runtime-token" || embeddedFallback.GetToken() != "embedded-token") throw new Exception("token provider priority");
    using var unconfigured = new RankedPlayersApiClient(new StaticTokenProvider(null), log, new RecordingHandler([HttpStatusCode.OK]));
    if ((await unconfigured.SyncAsync("br", rows, CancellationToken.None)).State != ApiSyncState.NotConfigured) throw new Exception("missing API token");
    var networkFailure = new HttpRequestException("Authorization: Bearer synthetic-token", new SocketException((int)SocketError.ConnectionRefused)); // RELEASE_SECRET_AUDIT_SYNTHETIC_FIXTURE
    var diagnostic = NetworkFailureDiagnostics.Format(networkFailure, CancellationToken.None);
    if (!diagnostic.Contains("type=HttpRequestException", StringComparison.Ordinal) || !diagnostic.Contains("innerType=SocketException", StringComparison.Ordinal) || !diagnostic.Contains("innerSocketError=ConnectionRefused", StringComparison.Ordinal) || diagnostic.Contains("synthetic-token", StringComparison.OrdinalIgnoreCase)) throw new Exception("network diagnostic sanitization");
    using var externalCancellation = new CancellationTokenSource();
    externalCancellation.Cancel();
    var externalCancellationDiagnostic = NetworkFailureDiagnostics.Format(new TaskCanceledException("cancelled"), externalCancellation.Token);
    if (!externalCancellationDiagnostic.Contains("cancellation=external", StringComparison.Ordinal)) throw new Exception("external cancellation diagnostics");
    var authenticationDiagnostic = NetworkFailureDiagnostics.Format(new HttpRequestException("TLS failure", new AuthenticationException("certificate failed")), CancellationToken.None);
    if (!authenticationDiagnostic.Contains("innerAuthenticationType=AuthenticationException", StringComparison.Ordinal) || !authenticationDiagnostic.Contains("innerMessage=\"certificate failed\"", StringComparison.Ordinal)) throw new Exception("authentication diagnostics");
    using var failedClient = new RankedPlayersApiClient(new StaticTokenProvider("synthetic-token"), log, new FakeHandler(_ => throw networkFailure));
    if ((await failedClient.SyncAsync("mp", rows, CancellationToken.None)).State != ApiSyncState.Failed) throw new Exception("network failure state");
    var appLog = File.ReadAllText(Path.Combine(CollectorPaths.Logs, "app.log"));
    if (!appLog.Contains("API_SYNC_FAILED", StringComparison.Ordinal) || !appLog.Contains("innerSocketError=ConnectionRefused", StringComparison.Ordinal) || appLog.Contains("synthetic-token", StringComparison.OrdinalIgnoreCase)) throw new Exception("API failure diagnostics logging");
}

static async Task LiveApiSyncFromStandardInput()
{
    var input = await Console.In.ReadToEndAsync();
    using var document = JsonDocument.Parse(input);
    if (!document.RootElement.TryGetProperty("datasets", out var datasets)) throw new InvalidDataException("LIVE_API_DATASETS_MISSING");
    var providerType = typeof(CODMunityTop250Collector.App.App).Assembly.GetType("CODMunityTop250Collector.App.EmbeddedRankedSyncTokenProvider", throwOnError: false);
    if (providerType is null || Activator.CreateInstance(providerType, nonPublic: true) is not IApiTokenProvider provider) throw new InvalidOperationException("LIVE_API_EMBEDDED_PROVIDER_UNAVAILABLE");
    var log = new AppLogger();
    using var client = new RankedPlayersApiClient(provider, log);
    foreach (var mode in new[] { "resurgence", "mp", "br" })
    {
        if (!datasets.TryGetProperty(mode, out var dataset) || !string.Equals(dataset.GetProperty("status").GetString(), "OK", StringComparison.Ordinal)) throw new InvalidDataException($"LIVE_API_DATASET_NOT_READY mode={mode}");
        var rows = dataset.GetProperty("players").EnumerateArray().Select(player => new RankedPlayer(
            player.GetProperty("position").GetInt32(), player.GetProperty("sr").GetInt32(), player.GetProperty("name").GetString() ?? string.Empty)).ToArray();
        var sync = await client.SyncAsync(mode, rows, CancellationToken.None);
        Console.WriteLine($"LIVE_API_SYNC mode={mode} rows={rows.Length} state={sync.State} status={sync.StatusCode?.ToString() ?? "none"}");
        if (rows.Length > 0 && sync.State != ApiSyncState.Ok) throw new InvalidOperationException($"LIVE_API_SYNC_FAILED mode={mode} state={sync.State}");
        if (rows.Length == 0 && sync.State != ApiSyncState.Skipped) throw new InvalidOperationException($"LIVE_API_EMPTY_NOT_SKIPPED mode={mode} state={sync.State}");
    }
}

static async Task BuildIdentityCacheTests(AppLogger log)
{
    const string sha = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    var hasher = new CountingBuildHasher(sha);
    var cache = new BuildIdentityCache(hasher, log);
    var stamp = DateTime.UnixEpoch;
    var bnet = new CodProcessDescriptor(1234, @"C:\Games\Call of Duty\_retail_\cod.exe", "battlenet", 100, stamp);
    for (var cycle = 0; cycle < 3; cycle++)
        if ((await cache.GetOrCreateAsync(bnet, CancellationToken.None))?.Sha256 != sha) throw new Exception("build identity cache result");
    if (hasher.Calls != 1) throw new Exception("three same-PID Battle.net cycles must hash exactly once");
    var cacheLog = File.ReadAllText(Path.Combine(CollectorPaths.Logs, "app.log"));
    if (Count(cacheLog, "BUILD_IDENTITY_CACHE_MISS pid=1234 platform=battlenet") != 1 ||
        Count(cacheLog, "BUILD_HASH_START pid=1234") != 1 ||
        Count(cacheLog, "BUILD_HASH_OK pid=1234") != 1 ||
        Count(cacheLog, "BUILD_IDENTITY_CACHE_HIT pid=1234 platform=battlenet") != 2)
        throw new Exception("build identity cache logging sequence");
    var pathChanged = bnet with { Path = @"D:\Games\Call of Duty\_retail_\cod.exe" };
    await cache.GetOrCreateAsync(pathChanged, CancellationToken.None);
    var platformChanged = pathChanged with { Platform = "steam" };
    await cache.GetOrCreateAsync(platformChanged, CancellationToken.None);
    var pidChanged = platformChanged with { Pid = 5678 };
    await cache.GetOrCreateAsync(pidChanged, CancellationToken.None);
    if (hasher.Calls != 4) throw new Exception("PID/path/platform changes must invalidate build identity");
    cache.RememberValidatedLayout((await cache.GetOrCreateAsync(pidChanged, CancellationToken.None))!, "bnet-current");
    if ((await cache.GetOrCreateAsync(pidChanged, CancellationToken.None))?.Layout != "bnet-current") throw new Exception("validated layout cache");
    cache.Invalidate("process-closed");
    await cache.GetOrCreateAsync(pidChanged, CancellationToken.None);
    if (hasher.Calls != 5) throw new Exception("closed process invalidation");
    if (await cache.GetOrCreateAsync(pidChanged with { Platform = "xbox" }, CancellationToken.None) is not null || hasher.Calls != 5) throw new Exception("Xbox must not use file SHA cache");
}

static int Count(string value, string needle) => value.Split(needle, StringSplitOptions.None).Length - 1;

static void UnicodeMachineReadableTransportTests()
{
    var names = new[] { "@JUANツ", "VØLNKRIX", "神 MOUSTAKIY 神", "乂 RiQeu 乂", "Pilot🚀", "Игрок", "東京" };
    foreach (var name in names)
    {
        var field = new byte[64];
        var bytes = Encoding.UTF8.GetBytes(name);
        Array.Copy(bytes, field, bytes.Length);
        var terminator = Array.IndexOf(field, (byte)0);
        if (!string.Equals(Encoding.UTF8.GetString(field, 0, terminator), name, StringComparison.Ordinal)) throw new Exception("UTF-8 name field decoding");
    }

    var child = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    var command = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $n=@(('@JUAN'+[char]0x30C4),('V'+[char]0x00D8+'LNKRIX'),([char]0x795E+' MOUSTAKIY '+[char]0x795E),([char]0x4E42+' RiQeu '+[char]0x4E42),('Pilot'+[char]0xD83D+[char]0xDE80),([char]0x0418+[char]0x0433+[char]0x0440+[char]0x043E+[char]0x043A),([char]0x6771+[char]0x4EAC)); [Console]::Out.Write(([ordered]@{names=$n}|ConvertTo-Json -Compress))";
    var psi = new ProcessStartInfo(child)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    };
    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) psi.ArgumentList.Add(argument);
    using var process = Process.Start(psi) ?? throw new Exception("Unicode PowerShell child did not start");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0 || !string.IsNullOrEmpty(stderr)) throw new Exception("Unicode PowerShell child failed");
    using var childJson = JsonDocument.Parse(stdout);
    var received = childJson.RootElement.GetProperty("names").EnumerateArray().Select(value => value.GetString()).ToArray();
    if (!received.SequenceEqual(names)) throw new Exception("PowerShell redirected UTF-8 JSON transport");

    var apiJson = JsonSerializer.Serialize(new { players = received.Select((name, index) => new RankedPlayer(index + 1, 1000 + index, name!)) });
    using var apiDocument = JsonDocument.Parse(apiJson);
    var finalNames = apiDocument.RootElement.GetProperty("players").EnumerateArray().Select(player => player.GetProperty("Name").GetString()).ToArray();
    if (!finalNames.SequenceEqual(names)) throw new Exception("Core/API Unicode JSON transport");

    var engine = Path.Combine(AppContext.BaseDirectory, "engine");
    var orchestrator = File.ReadAllText(Path.Combine(engine, "top250_orchestrator.ps1"));
    if (!orchestrator.Contains("[Console]::OutputEncoding = $script:MachineReadableUtf8", StringComparison.Ordinal) || !orchestrator.Contains("$psi.StandardOutputEncoding", StringComparison.Ordinal)) throw new Exception("orchestrator UTF-8 transport contract");
    foreach (var exporter in Directory.EnumerateFiles(Path.Combine(engine, "exporters"), "*.ps1", SearchOption.AllDirectories))
        if (!File.ReadAllText(exporter).Contains("[Console]::OutputEncoding = $script:MachineReadableUtf8", StringComparison.Ordinal)) throw new Exception($"exporter UTF-8 transport contract: {exporter}");
}

static void EngineManagedIdentityContractTests()
{
    var engine = Path.Combine(AppContext.BaseDirectory, "engine");
    var orchestrator = File.ReadAllText(Path.Combine(engine, "top250_orchestrator.ps1"));
    if (!orchestrator.Contains("-VerifiedBuildSha256", StringComparison.Ordinal) || !orchestrator.Contains("ProcessPriorityClass]::BelowNormal", StringComparison.Ordinal)) throw new Exception("managed engine identity or exporter priority contract");
    if (!orchestrator.Contains("Get-OrchestratorSha256Hex", StringComparison.Ordinal) || orchestrator.Contains("[Security.Cryptography.SHA256]::HashData", StringComparison.Ordinal)) throw new Exception("orchestrator PowerShell 5 SHA-256 compatibility contract");
    var layouts = JsonDocument.Parse(File.ReadAllText(Path.Combine(engine, "build_layouts.json")));
    var season5 = layouts.RootElement.GetProperty("sha256Builds").GetProperty("97413B95AA94A9694690BA70B16189E4827E2B363850F873387E8A88101D4FE4");
    if (season5.GetProperty("layout").GetString() != "bnet-20260820" || season5.GetProperty("exporterSet").GetString() != "bnet-dynamic" || !orchestrator.Contains("$Manifest.battlenetLayoutDefinitions", StringComparison.Ordinal)) throw new Exception("static Battle.net layout must route through dynamic exporter");
    foreach (var exporter in new[]
    {
        "exporters/bnet/top250_mp.ps1", "exporters/bnet/top250_br.ps1", "exporters/bnet/top250_resurgence.ps1",
        "exporters/bnet-20260814/top250_mp.ps1", "exporters/bnet-20260814/top250_br.ps1", "exporters/bnet-20260814/top250_resurgence.ps1",
        "exporters/steam/top250_mp.ps1", "exporters/steam/top250_br.ps1", "exporters/steam/top250_resurgence.ps1",
        "exporters/top250_bnet_dynamic_layout.ps1"
    })
    {
        var source = File.ReadAllText(Path.Combine(engine, exporter.Replace('/', Path.DirectorySeparatorChar)));
        if (!source.Contains("VerifiedBuildSha256", StringComparison.Ordinal) || !source.Contains("IsNullOrWhiteSpace($VerifiedBuildSha256)", StringComparison.Ordinal)) throw new Exception($"managed identity fallback missing: {exporter}");
    }
}

static async Task SchedulerBuildIdentityIntegrationTests(AppLogger log)
{
    const string sha = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    var hasher = new CountingBuildHasher(sha);
    var cache = new BuildIdentityCache(hasher, log);
    var monitor = new FakeCodProcessMonitor(new CodProcessDescriptor(4321, @"C:\Games\Call of Duty\_retail_\cod.exe", "battlenet", 200, DateTime.UnixEpoch));
    var runner = new FakeEngineRunner(log);
    using var api = new RankedPlayersApiClient(new StaticTokenProvider(null), log, new RecordingHandler([HttpStatusCode.OK]));
    var scheduler = new CollectorScheduler(monitor, runner, new EngineStatusParser(), api, log, AppContext.BaseDirectory, cache);
    for (var cycle = 0; cycle < 3; cycle++) await scheduler.SyncNowAsync(CancellationToken.None);
    if (hasher.Calls != 1 || runner.Identities.Count != 3 || runner.Identities.Any(identity => identity?.Sha256 != sha)) throw new Exception("scheduler production composition did not reuse verified build identity");
    var content = File.ReadAllText(Path.Combine(CollectorPaths.Logs, "app.log"));
    if (Count(content, "BUILD_IDENTITY_CACHE_MISS pid=4321 platform=battlenet") != 1 || Count(content, "BUILD_HASH_START pid=4321") != 1 || Count(content, "BUILD_HASH_OK pid=4321") != 1 || Count(content, "BUILD_IDENTITY_CACHE_HIT pid=4321 platform=battlenet") != 2) throw new Exception("scheduler production composition logging");
}

static async Task SchedulerApiPriorityTests(AppLogger log)
{
    var descriptor = new CodProcessDescriptor(9876, @"C:\Games\Call of Duty\_retail_\cod.exe", "battlenet", 200, DateTime.UnixEpoch);
    var monitor = new FakeCodProcessMonitor(descriptor);
    var runner = new PriorityEngineRunner(log);
    var successHandler = new RecordingHandler([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK]);
    using (var api = new RankedPlayersApiClient(new StaticTokenProvider("synthetic-token"), log, successHandler))
    {
        var scheduler = new CollectorScheduler(monitor, runner, new EngineStatusParser(), api, log, AppContext.BaseDirectory, new BuildIdentityCache(new CountingBuildHasher(new string('A', 64)), log));
        await scheduler.SyncNowAsync(CancellationToken.None);
        if (!successHandler.Modes.SequenceEqual(["resurgence", "mp", "br"])) throw new Exception("API mode priority order");
        if (scheduler.Snapshot.Resurgence?.ApiSync != ApiSyncState.Ok || scheduler.Snapshot.Mp?.ApiSync != ApiSyncState.Ok || scheduler.Snapshot.Br?.ApiSync != ApiSyncState.Ok) throw new Exception("independent API sync statuses");
    }
    var failureHandler = new RecordingHandler([HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.OK]);
    using (var api = new RankedPlayersApiClient(new StaticTokenProvider("synthetic-token"), log, failureHandler))
    {
        var scheduler = new CollectorScheduler(monitor, runner, new EngineStatusParser(), api, log, AppContext.BaseDirectory, new BuildIdentityCache(new CountingBuildHasher(new string('B', 64)), log));
        await scheduler.SyncNowAsync(CancellationToken.None);
        if (!failureHandler.Modes.SequenceEqual(["resurgence", "mp", "br"])) throw new Exception("Resurgence failure blocked lower-priority modes");
        if (scheduler.Snapshot.CycleStatus != "OK" || scheduler.Snapshot.Resurgence?.ApiSync != ApiSyncState.Failed || scheduler.Snapshot.Resurgence?.PlayerRows?.Count != 250 || scheduler.Snapshot.Mp?.ApiSync != ApiSyncState.Ok || scheduler.Snapshot.Br?.ApiSync != ApiSyncState.Ok) throw new Exception("API failure changed collection result or independent statuses");
    }
}

static void AssertOrdinalApiPayload(string? body, int expectedRows = 250)
{
    using var json = JsonDocument.Parse(body ?? throw new Exception("missing API payload"));
    var players = json.RootElement.GetProperty("players");
    if (players.GetArrayLength() != expectedRows) throw new Exception("API payload player count");
    for (var index = 0; index < expectedRows; index++)
    {
        var player = players[index];
        if (player.GetProperty("position").GetInt32() != index + 1) throw new Exception("API payload position is not ordinal");
        if (player.TryGetProperty("nativeRank", out _) || player.TryGetProperty("ordinal", out _) || player.TryGetProperty("xuid", out _)) throw new Exception("internal rank data leaked into API payload");
    }
}

static void ReleaseSigningTests(string root)
{
    var signingRoot = Path.Combine(root, "release-signing"); Directory.CreateDirectory(signingRoot);
    var msiPath = Path.Combine(signingRoot, "fixture.msi");
    File.WriteAllBytes(msiPath, RandomNumberGenerator.GetBytes(4096));
    var passphrase = "temporary-passphrase".ToCharArray();
    var wrongPassphrase = "wrong-passphrase".ToCharArray();
    var keyDirectory = Path.Combine(signingRoot, "keypair");
    ReleaseSigning.WriteKeyPair(keyDirectory, passphrase, force: false);
    var generatedPrivatePath = Path.Combine(keyDirectory, "codmunity-top250-release-private.pem");
    var generatedPublicPath = Path.Combine(keyDirectory, "codmunity-top250-release-public.pem");
    if (!File.Exists(generatedPrivatePath) || !File.Exists(generatedPublicPath)) throw new Exception("temporary keypair generation");
    using (var generatedKey = ReleaseSigning.LoadEncryptedPrivateKey(generatedPrivatePath, passphrase)) { }
    ExpectFailure(() => ReleaseSigning.WriteKeyPair(keyDirectory, passphrase, force: false), "keypair overwrite accepted without force");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var encryptedPrivate = ReleaseSigning.ExportEncryptedPrivateKey(key, passphrase);
    using var importedKey = ReleaseSigning.ImportEncryptedPrivateKey(encryptedPrivate, passphrase);
    try { using var _ = ReleaseSigning.ImportEncryptedPrivateKey(encryptedPrivate, wrongPassphrase); throw new Exception("wrong private-key passphrase accepted"); }
    catch (CryptographicException) { }
    var publicDer = key.ExportSubjectPublicKeyInfo();
    var fingerprint = ReleaseSigning.PublicKeyFingerprint(publicDer);
    if (fingerprint != ReleaseSigning.PublicKeyFingerprint(publicDer)) throw new Exception("public key fingerprint instability");
    var manifest = new ReleaseManifest
    {
        Channel = "private-alpha", Version = "0.1.5-alpha", MsiVersion = "0.1.5", MinimumUpdaterVersion = "0.1.4-alpha",
        Url = "https://gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com/n/gr5favu0uzxf/b/codmunity-updates/o/top250%2Freleases%2Ffixture.msi", Sha256 = Convert.ToHexString(ReleaseSigning.ComputeSha256(msiPath)),
        Size = new FileInfo(msiPath).Length, PublishedAtUtc = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero), ReleaseNotes = "Synthetic test release."
    };
    var manifestBytes = ManifestJson.Serialize(manifest);
    var signature = ReleaseSigning.SignManifest(importedKey, manifestBytes);
    using var publicKey = ECDsa.Create(); publicKey.ImportSubjectPublicKeyInfo(publicDer, out _);
    if (!ReleaseSigning.VerifyManifest(publicKey, manifestBytes, signature)) throw new Exception("valid manifest signature rejected");
    var alteredManifest = (byte[])manifestBytes.Clone(); alteredManifest[0] ^= 0x01;
    if (ReleaseSigning.VerifyManifest(publicKey, alteredManifest, signature)) throw new Exception("altered manifest signature accepted");
    var wrongSignature = (byte[])signature.Clone(); wrongSignature[0] ^= 0x01;
    if (ReleaseSigning.VerifyManifest(publicKey, manifestBytes, wrongSignature)) throw new Exception("altered signature accepted");
    using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    if (ReleaseSigning.VerifyManifest(wrongKey, manifestBytes, signature)) throw new Exception("wrong public key accepted");
    var manifestPath = Path.Combine(signingRoot, "manifest.json");
    var signaturePath = Path.Combine(signingRoot, "manifest.sig");
    var publicPath = Path.Combine(signingRoot, "public.pem");
    var signedOutput = Path.Combine(signingRoot, "signed-output");
    ReleaseSigning.WriteManifestAndSignature(signedOutput, manifest, importedKey, force: false);
    File.WriteAllText(Path.Combine(signedOutput, "public.pem"), new string(PemEncoding.Write("PUBLIC KEY", publicDer)));
    ReleaseSigning.VerifyReleaseFiles(Path.Combine(signedOutput, "manifest.json"), Path.Combine(signedOutput, "manifest.sig"), Path.Combine(signedOutput, "public.pem"), msiPath);
    File.WriteAllBytes(manifestPath, manifestBytes);
    File.WriteAllText(signaturePath, Convert.ToBase64String(signature) + "\n");
    File.WriteAllText(publicPath, new string(PemEncoding.Write("PUBLIC KEY", publicDer)));
    ReleaseSigning.VerifyReleaseFiles(manifestPath, signaturePath, publicPath, msiPath);
    var sizeMismatch = manifest with { Size = manifest.Size + 1 };
    var sizeMismatchBytes = ManifestJson.Serialize(sizeMismatch);
    var sizeMismatchSignature = ReleaseSigning.SignManifest(importedKey, sizeMismatchBytes);
    var sizeManifestPath = Path.Combine(signingRoot, "size-mismatch.json");
    var sizeSignaturePath = Path.Combine(signingRoot, "size-mismatch.sig");
    File.WriteAllBytes(sizeManifestPath, sizeMismatchBytes); File.WriteAllText(sizeSignaturePath, Convert.ToBase64String(sizeMismatchSignature));
    ExpectFailure(() => ReleaseSigning.VerifyReleaseFiles(sizeManifestPath, sizeSignaturePath, publicPath, msiPath), "size mismatch accepted");
    File.AppendAllText(msiPath, "x");
    ExpectFailure(() => ReleaseSigning.VerifyReleaseFiles(manifestPath, signaturePath, publicPath, msiPath), "altered MSI accepted");
    ExpectFailure(() => ManifestJson.Serialize(manifest with { Url = "http://gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com/n/gr5favu0uzxf/b/codmunity-updates/o/top250%2Freleases%2Ffixture.msi" }), "HTTP release URL accepted");
    Array.Clear(passphrase); Array.Clear(wrongPassphrase);
    CryptographicOperations.ZeroMemory(encryptedPrivate); CryptographicOperations.ZeroMemory(publicDer); CryptographicOperations.ZeroMemory(manifestBytes); CryptographicOperations.ZeroMemory(signature); CryptographicOperations.ZeroMemory(alteredManifest); CryptographicOperations.ZeroMemory(wrongSignature); CryptographicOperations.ZeroMemory(sizeMismatchBytes); CryptographicOperations.ZeroMemory(sizeMismatchSignature);
}

static void ExpectFailure(Action action, string message)
{
    try { action(); }
    catch (Exception) { return; }
    throw new Exception(message);
}

static void PrivateUpdaterTests(string root)
{
    var embedded = EmbeddedUpdateKey.LoadSubjectPublicKeyInfo();
    try
    {
        if (UpdateTrust.PublicKeyFingerprint(embedded) != UpdateTrust.ExpectedPublicKeyFingerprint) throw new Exception("embedded production public-key fingerprint");
    }
    finally { CryptographicOperations.ZeroMemory(embedded); }

    using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var publicDer = signer.ExportSubjectPublicKeyInfo();
    var expectedFingerprint = UpdateTrust.PublicKeyFingerprint(publicDer);
    var payload = RandomNumberGenerator.GetBytes(4096);
    var sha = Convert.ToHexString(SHA256.HashData(payload));
    const string validMsiUrl = "https://gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com/n/gr5favu0uzxf/b/codmunity-updates/o/top250%2Freleases%2FCODMunityTop250Collector-0.1.5-alpha-win-x64.msi";
    const string expectedManifestEndpoint = "https://gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com/n/gr5favu0uzxf/b/codmunity-updates/o/top250%2Fmanifest.json";
    const string expectedSignatureEndpoint = "https://gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com/n/gr5favu0uzxf/b/codmunity-updates/o/top250%2Fmanifest.sig";
    if (UpdateTrust.ManifestUri.AbsoluteUri != expectedManifestEndpoint || UpdateTrust.SignatureUri.AbsoluteUri != expectedSignatureEndpoint) throw new Exception("OCI fixed endpoints");
    var manifest = new UpdateManifest(1, "private-alpha", "0.1.5-alpha", "0.1.5", "0.1.4-alpha", validMsiUrl, sha, payload.Length, new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero), "Synthetic release.");
    var manifestBytes = SerializeUpdateManifest(manifest);
    var signature = signer.SignData(manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    using var publicKey = UpdateTrust.ImportPublicKey(publicDer);
    if (!UpdateTrust.VerifyManifest(publicKey, manifestBytes, signature)) throw new Exception("valid update signature rejected");
    var changed = (byte[])manifestBytes.Clone(); changed[0] ^= 1; if (UpdateTrust.VerifyManifest(publicKey, changed, signature)) throw new Exception("changed update manifest accepted");
    var wrongSignature = (byte[])signature.Clone(); wrongSignature[0] ^= 1; if (UpdateTrust.VerifyManifest(publicKey, manifestBytes, wrongSignature)) throw new Exception("wrong update signature accepted");
    using var wrongPublicKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); if (UpdateTrust.VerifyManifest(wrongPublicKey, manifestBytes, signature)) throw new Exception("wrong update public key accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { SchemaVersion = 2 }), "unknown schema accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Channel = "public" }), "wrong channel accepted");
    UpdateManifestValidator.Validate(manifest);
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url.Replace("https://", "http://", StringComparison.Ordinal) }), "HTTP URL accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url.Replace("gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com", "codmunity-top250.duckdns.org", StringComparison.Ordinal) }), "DuckDNS host accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url.Replace("/n/gr5favu0uzxf/", "/n/othernamespace/", StringComparison.Ordinal) }), "wrong OCI namespace accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url.Replace("/b/codmunity-updates/", "/b/other-bucket/", StringComparison.Ordinal) }), "wrong OCI bucket accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url.Replace("top250%2Freleases%2F", "top250%2Fother%2F", StringComparison.Ordinal) }), "foreign OCI prefix accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url + "?query=1" }), "query accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url + "#fragment" }), "fragment accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url.Replace("https://", "https://user:password@", StringComparison.Ordinal) }), "credentials accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url.Replace(".com/", ".com:444/", StringComparison.Ordinal) }), "custom port accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url.Replace("top250%2Freleases%2F", "top250%252Freleases%252F", StringComparison.Ordinal) }), "double encoding accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Url = manifest.Url.Replace("releases%2F", "releases%2F..%2F", StringComparison.Ordinal) }), "traversal accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Sha256 = "ABC" }), "invalid SHA accepted");
    ExpectFailure(() => UpdateManifestValidator.Validate(manifest with { Size = UpdateTrust.MaxMsiBytes + 1 }), "oversized MSI accepted");
    if (!AppSemanticVersion.TryParse("0.1.5-alpha", out var newer) || !AppSemanticVersion.TryParse("0.1.4-alpha", out var current) || newer.CompareTo(current) <= 0 || !AppSemanticVersion.TryParse("0.2.0-alpha", out var majorMinor) || !AppSemanticVersion.TryParse("0.1.99-alpha", out var oldMinor) || majorMinor.CompareTo(oldMinor) <= 0) throw new Exception("semantic version comparison");
    if (!AppSemanticVersion.TryParse("0.1.21-alpha", out _) || !AppSemanticVersion.TryParse("0.1.21-alpha+commit", out var alphaWithMetadata) || !AppSemanticVersion.TryParse("1.2.3+build", out _) || !AppSemanticVersion.TryParse("1.2.3-beta.1+commit.abcdef", out _) || !AppSemanticVersion.TryParse("0.1.21-alpha", out var alphaWithoutMetadata) || alphaWithMetadata.CompareTo(alphaWithoutMetadata) != 0) throw new Exception("semantic version build metadata");
    if (AppSemanticVersion.TryParse("1.2.3+", out _) || AppSemanticVersion.TryParse("1.2.3+build..metadata", out _) || AppSemanticVersion.TryParse("1.2.3+build_metadata", out _) || AppSemanticVersion.TryParse("1.2.3+build+metadata", out _)) throw new Exception("malformed semantic version metadata accepted");
    var handler = new FakeHandler(request =>
    {
        var address = request.RequestUri!.AbsoluteUri;
        if (address == UpdateTrust.ManifestUri.AbsoluteUri) return FakeResponse(manifestBytes);
        if (address == UpdateTrust.SignatureUri.AbsoluteUri) return FakeResponse(Encoding.ASCII.GetBytes(Convert.ToBase64String(signature)));
        if (address == manifest.Url) return FakeResponse(payload);
        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
    });
    using var client = new PrivateUpdateClient(publicDer, (_, _) => { }, handler, expectedFingerprint);
    var available = client.CheckAsync(current, CancellationToken.None).GetAwaiter().GetResult();
    if (available.Kind != UpdateCheckKind.Available || available.Update is null) throw new Exception("newer signed update not detected");
    var metadataEvents = new List<string>();
    if (!AppSemanticVersion.TryParse("0.1.21-alpha+1873921903f3a864142321ef568575b79b83edaa", out var installedWithMetadata)) throw new Exception("installed metadata version rejected");
    var metadataManifest = manifest with { Version = "0.1.22-alpha", MinimumUpdaterVersion = "0.1.20-alpha" };
    using (var metadataClient = new PrivateUpdateClient(publicDer, (evt, _) => metadataEvents.Add(evt), CreateUpdateHandler(signer, publicDer, metadataManifest, payload), expectedFingerprint))
    {
        var metadataAvailable = metadataClient.CheckAsync(installedWithMetadata, CancellationToken.None).GetAwaiter().GetResult();
        if (metadataAvailable.Kind != UpdateCheckKind.Available || metadataAvailable.Update?.Manifest.Version != "0.1.22-alpha" || !metadataEvents.Contains("UPDATE_CHECK_START") || !metadataEvents.Contains("UPDATE_AVAILABLE")) throw new Exception("metadata current version update check");
    }
    if (!AppSemanticVersion.TryParse("0.1.22-alpha+somecommit", out var latestWithMetadata)) throw new Exception("latest metadata version rejected");
    using (var sameMetadataClient = new PrivateUpdateClient(publicDer, (_, _) => { }, CreateUpdateHandler(signer, publicDer, metadataManifest, payload), expectedFingerprint))
    {
        if (sameMetadataClient.CheckAsync(latestWithMetadata, CancellationToken.None).GetAwaiter().GetResult().Kind != UpdateCheckKind.None) throw new Exception("metadata changed version precedence");
    }
    var sameHandler = CreateUpdateHandler(signer, publicDer, manifest with { Version = "0.1.4-alpha" }, payload);
    using (var sameClient = new PrivateUpdateClient(publicDer, (_, _) => { }, sameHandler, expectedFingerprint)) if (sameClient.CheckAsync(current, CancellationToken.None).GetAwaiter().GetResult().Kind != UpdateCheckKind.None) throw new Exception("same version offered");
    var olderHandler = CreateUpdateHandler(signer, publicDer, manifest with { Version = "0.1.3-alpha" }, payload);
    using (var olderClient = new PrivateUpdateClient(publicDer, (_, _) => { }, olderHandler, expectedFingerprint)) if (olderClient.CheckAsync(current, CancellationToken.None).GetAwaiter().GetResult().Kind != UpdateCheckKind.None) throw new Exception("older version offered");
    var minimumHandler = CreateUpdateHandler(signer, publicDer, manifest with { MinimumUpdaterVersion = "0.1.5-alpha" }, payload);
    using (var minimumClient = new PrivateUpdateClient(publicDer, (_, _) => { }, minimumHandler, expectedFingerprint)) if (minimumClient.CheckAsync(current, CancellationToken.None).GetAwaiter().GetResult().Kind != UpdateCheckKind.MinimumUpdaterVersionNotMet) throw new Exception("minimum updater version ignored");
    var updatesDirectory = Path.Combine(root, "updates");
    var downloaded = client.DownloadMsiAsync(available.Update, updatesDirectory, CancellationToken.None).GetAwaiter().GetResult();
    if (!File.ReadAllBytes(downloaded).SequenceEqual(payload)) throw new Exception("valid MSI download mismatch");
    ExpectFailure(() => client.DownloadMsiAsync(available.Update with { Manifest = manifest with { Sha256 = new string('A', 64) } }, updatesDirectory, CancellationToken.None).GetAwaiter().GetResult(), "invalid MSI hash accepted");
    ExpectFailure(() => client.DownloadMsiAsync(available.Update with { Manifest = manifest with { Size = payload.Length + 1 } }, updatesDirectory, CancellationToken.None).GetAwaiter().GetResult(), "invalid MSI size accepted");
    var notPublishedEvents = new List<string>();
    using (var notPublishedClient = new PrivateUpdateClient(publicDer, (evt, _) => notPublishedEvents.Add(evt), new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), expectedFingerprint))
    {
        if (notPublishedClient.CheckAsync(current, CancellationToken.None).GetAwaiter().GetResult().Kind != UpdateCheckKind.None || !notPublishedEvents.Contains("UPDATE_CHECK_NOT_PUBLISHED")) throw new Exception("manifest 404 not treated as not-published");
    }
    var signatureNotPublishedEvents = new List<string>();
    using (var signatureNotPublishedClient = new PrivateUpdateClient(publicDer, (evt, _) => signatureNotPublishedEvents.Add(evt), new FakeHandler(request => request.RequestUri!.AbsoluteUri == UpdateTrust.ManifestUri.AbsoluteUri ? FakeResponse(manifestBytes) : new HttpResponseMessage(HttpStatusCode.NotFound)), expectedFingerprint))
    {
        if (signatureNotPublishedClient.CheckAsync(current, CancellationToken.None).GetAwaiter().GetResult().Kind != UpdateCheckKind.None || !signatureNotPublishedEvents.Contains("UPDATE_CHECK_NOT_PUBLISHED")) throw new Exception("signature 404 not treated as not-published");
    }
    var timeoutEvents = new List<(string Event, string? Detail)>();
    using (var timeoutClient = new PrivateUpdateClient(publicDer, (evt, detail) => timeoutEvents.Add((evt, detail)), new FakeHandler(_ => throw new TaskCanceledException()), expectedFingerprint))
    {
        if (timeoutClient.CheckAsync(current, CancellationToken.None).GetAwaiter().GetResult().Kind != UpdateCheckKind.Failed || !timeoutEvents.Any(e => e.Event == "UPDATE_CHECK_FAILED" && e.Detail?.Contains("cancellation=timeout", StringComparison.Ordinal) == true)) throw new Exception("timeout diagnostics");
    }
    var updaterFailureEvents = new List<(string Event, string? Detail)>();
    using (var updaterFailureClient = new PrivateUpdateClient(publicDer, (evt, detail) => updaterFailureEvents.Add((evt, detail)), new FakeHandler(_ => throw new HttpRequestException("Authorization: Bearer synthetic-token", new SocketException((int)SocketError.ConnectionRefused))), expectedFingerprint)) // RELEASE_SECRET_AUDIT_SYNTHETIC_FIXTURE
    {
        if (updaterFailureClient.CheckAsync(current, CancellationToken.None).GetAwaiter().GetResult().Kind != UpdateCheckKind.Failed) throw new Exception("updater network failure state");
        var detail = updaterFailureEvents.Single(e => e.Event == "UPDATE_CHECK_FAILED").Detail ?? string.Empty;
        if (!detail.Contains("innerSocketError=ConnectionRefused", StringComparison.Ordinal) || detail.Contains("synthetic-token", StringComparison.OrdinalIgnoreCase)) throw new Exception("updater diagnostics sanitization");
    }
    if (UpdateHostArguments.TryParse(Array.Empty<string>(), out _)) throw new Exception("UpdateHost accepted empty arguments");
    if (UpdateHostArguments.TryParse(new[] { "--parent-pid", "1", "--msi", "x", "--sha256", "ABC", "--app", "x" }, out _)) throw new Exception("UpdateHost accepted invalid arguments");
    CryptographicOperations.ZeroMemory(publicDer); CryptographicOperations.ZeroMemory(payload); CryptographicOperations.ZeroMemory(manifestBytes); CryptographicOperations.ZeroMemory(signature); CryptographicOperations.ZeroMemory(changed); CryptographicOperations.ZeroMemory(wrongSignature);
}

static void AutomaticUpdateTests(string root)
{
    if (UpdateCheckSchedule.InitialDelay != TimeSpan.FromSeconds(5) || UpdateCheckSchedule.RecurringInterval != TimeSpan.FromMinutes(10)) throw new Exception("update check schedule");
    var manifest = new UpdateManifest(1, "private-alpha", "0.1.7-alpha", "0.1.7", "0.1.6-alpha", "https://gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com/n/gr5favu0uzxf/b/codmunity-updates/o/top250%2Freleases%2FCODMunityTop250Collector-0.1.7-alpha-win-x64.msi", new string('A', 64), 1, new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero), "Synthetic release.");
    AppSemanticVersion.TryParse("0.1.7-alpha", out var version);
    var update = new AvailableUpdate(manifest, version);
    var events = new List<string>();
    var coordinator = new AutomaticUpdateCoordinator();
    var result = coordinator.RunAsync(update,
        (_, _) => { events.Add("download"); return Task.FromResult("fixture.msi"); },
        () => events.Add("pause"),
        _ => { events.Add("wait"); return Task.CompletedTask; },
        () => events.Add("resume"),
        (evt, detail) => events.Add(evt + " " + detail),
        (_, _) => events.Add("host"),
        CancellationToken.None).GetAwaiter().GetResult();
    var prefix = string.Join('|', events.Take(3));
    if (result != AutomaticUpdateOutcome.HandedOff || prefix != "download|pause|wait" || !events.Contains("host") || !events.Contains("UPDATE_INSTALL_HANDOFF version=0.1.7-alpha")) throw new Exception("automatic update handoff sequence");

    events.Clear();
    var failure = new AutomaticUpdateCoordinator().RunAsync(update,
        (_, _) => { events.Add("download"); throw new CryptographicException(); },
        () => events.Add("pause"), _ => Task.CompletedTask, () => events.Add("resume"),
        (evt, detail) => events.Add(evt + " " + detail), (_, _) => events.Add("host"), CancellationToken.None).GetAwaiter().GetResult();
    if (failure != AutomaticUpdateOutcome.Failed || events.Contains("pause") || events.Contains("host") || !events.Any(x => x.StartsWith("UPDATE_AUTO_UPDATE_FAILED", StringComparison.Ordinal))) throw new Exception("download failure started update host");

    events.Clear();
    var runningGate = new TaskCompletionSource();
    var deferred = new AutomaticUpdateCoordinator().RunAsync(update,
        (_, _) => { events.Add("download"); return Task.FromResult("fixture.msi"); },
        () => events.Add("pause"), _ => { events.Add("wait"); return runningGate.Task; }, () => events.Add("resume"),
        (evt, detail) => events.Add(evt + " " + detail), (_, _) => events.Add("host"), CancellationToken.None);
    if (events.Contains("host")) throw new Exception("running collector did not defer update host");
    runningGate.SetResult();
    if (deferred.GetAwaiter().GetResult() != AutomaticUpdateOutcome.HandedOff || !events.Contains("host")) throw new Exception("deferred update did not continue after cycle");

    if (!AppSemanticVersion.TryParse("0.1.6-alpha", out var current) || current.ToString() != "0.1.6-alpha") throw new Exception("updater version logging format");
    var updateGate = new UpdateOperationGate();
    if (!updateGate.TryBegin() || updateGate.TryBegin() || !updateGate.IsActive) throw new Exception("manual and automatic update checks are not serialized");
    updateGate.End(); if (updateGate.IsActive || !updateGate.TryBegin()) throw new Exception("update operation gate does not release"); updateGate.End();
    if (UpdateHostPresentation.ShouldShowFailureWindow(new UpdateInstallationResult(0, true, false)) || !UpdateHostPresentation.ShouldShowFailureWindow(new UpdateInstallationResult(1603, false, false, "failed"))) throw new Exception("UpdateHost headless success/failure UI policy");
    if (UpdateInstallation.InstallUiLevelNone != 2) throw new Exception("Windows Installer API UI level");
    if (WindowsInstallerApi.InstallLogMode != 0x00001B16 || (WindowsInstallerApi.InstallLogMode & WindowsInstallerApi.InstallLogModeLogOnlyOnError) != 0) throw new Exception("Windows Installer API log mode");
    if (WindowsInstallerApi.InstallLogAttributesFlushEachLine != 0x00000002) throw new Exception("Windows Installer API log attributes");
    var installerCalls = new List<string>();
    var fakeInstaller = new FakeWindowsInstallerApi(0, installerCalls);
    var hostLogDirectory = Path.Combine(root, "update-host-log");
    var nativeExit = UpdateInstallation.InstallValidatedMsiAsync("fixture.msi", fakeInstaller, (evt, detail) => installerCalls.Add(evt + " " + detail), hostLogDirectory).GetAwaiter().GetResult();
    if (nativeExit != 0 || string.Join('|', installerCalls) != "enable-log|UPDATEHOST_MSI_LOG_ENABLE result=0 path=logs/update-msi.log mode=0x00001B16 attributes=0x00000002|set-ui-2|UPDATEHOST_MSI_UI_SET requested=2 previous=0|UPDATEHOST_MSI_INSTALL_START |install fixture.msi REBOOT=ReallySuppress|UPDATEHOST_MSI_INSTALL_EXIT code=0") throw new Exception("Windows Installer API invocation");
    if (!Directory.Exists(hostLogDirectory)) throw new Exception("Windows Installer API log directory");
    var unavailableLogEvents = new List<string>();
    var unavailableLogExit = UpdateInstallation.InstallValidatedMsiAsync("fixture.msi", new FakeWindowsInstallerApi(0, new List<string>(), 87), (evt, detail) => unavailableLogEvents.Add(evt + " " + detail), Path.Combine(root, "update-host-log-unavailable")).GetAwaiter().GetResult();
    if (unavailableLogExit != 0 || !unavailableLogEvents.Contains("UPDATEHOST_MSI_LOG_ENABLE result=87 path=logs/update-msi.log mode=0x00001B16 attributes=0x00000002")) throw new Exception("Windows Installer API log result");
    var rebootExit = UpdateInstallation.InstallValidatedMsiAsync("fixture.msi", new FakeWindowsInstallerApi(3010, new List<string>()), (_, _) => { }).GetAwaiter().GetResult();
    var failureExit = UpdateInstallation.InstallValidatedMsiAsync("fixture.msi", new FakeWindowsInstallerApi(1603, new List<string>()), (_, _) => { }).GetAwaiter().GetResult();
    if (!UpdateInstallation.IsSuccessfulMsiExitCode(rebootExit) || UpdateInstallation.IsSuccessfulMsiExitCode(failureExit)) throw new Exception("Windows Installer API exit-code handling");
    if (!UpdateInstallation.IsSuccessfulMsiExitCode(0) || !UpdateInstallation.IsSuccessfulMsiExitCode(3010) || UpdateInstallation.IsSuccessfulMsiExitCode(1603)) throw new Exception("MSI exit-code handling");
}

static FakeHandler CreateUpdateHandler(ECDsa signer, byte[] publicDer, UpdateManifest manifest, byte[] payload)
{
    var bytes = SerializeUpdateManifest(manifest);
    var signature = signer.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    return new FakeHandler(request =>
    {
        var address = request.RequestUri!.AbsoluteUri;
        if (address == UpdateTrust.ManifestUri.AbsoluteUri) return FakeResponse(bytes);
        if (address == UpdateTrust.SignatureUri.AbsoluteUri) return FakeResponse(Encoding.ASCII.GetBytes(Convert.ToBase64String(signature)));
        if (address == manifest.Url) return FakeResponse(payload);
        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
    });
}

static byte[] SerializeUpdateManifest(UpdateManifest manifest) => Encoding.UTF8.GetBytes($$"""{"schemaVersion":{{manifest.SchemaVersion}},"channel":"{{manifest.Channel}}","version":"{{manifest.Version}}","msiVersion":"{{manifest.MsiVersion}}","minimumUpdaterVersion":"{{manifest.MinimumUpdaterVersion}}","url":"{{manifest.Url}}","sha256":"{{manifest.Sha256}}","size":{{manifest.Size}},"publishedAtUtc":"{{manifest.PublishedAtUtc:yyyy-MM-ddTHH:mm:ssZ}}","releaseNotes":"{{manifest.ReleaseNotes}}"}""");
static HttpResponseMessage FakeResponse(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = respond(request); response.RequestMessage ??= request; return Task.FromResult(response);
    }
}

sealed class FakeWindowsInstallerApi(uint installResult, List<string> calls, uint logResult = 0) : IWindowsInstallerApi
{
    public uint SetInternalUI(uint uiLevel) { calls.Add($"set-ui-{uiLevel}"); return 0; }
    public uint EnableLog(string logPath) { calls.Add("enable-log"); return logResult; }
    public uint InstallProduct(string validatedMsiPath, string properties) { calls.Add($"install {validatedMsiPath} {properties}"); return installResult; }
}

sealed class CountingBuildHasher(string sha256) : IBuildHasher
{
    public int Calls { get; private set; }
    public Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        Calls++;
        return Task.FromResult(sha256);
    }
}

sealed class FakeCodProcessMonitor(CodProcessDescriptor descriptor) : CodProcessMonitor
{
    public override int? FindSinglePid() => descriptor.Pid;
    public override CodProcessDescriptor? Describe(int pid) => pid == descriptor.Pid ? descriptor : null;
}

sealed class FakeEngineRunner(AppLogger log) : EngineRunner(log)
{
    public List<VerifiedBuildIdentity?> Identities { get; } = [];
    public override Task<(int ExitCode, string StdOut, string StdErr)> RunOnceAsync(string engineDirectory, VerifiedBuildIdentity? identity, CancellationToken token)
    {
        Identities.Add(identity);
        var timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var json = $"{{\"capturedAt\":\"{timestamp}\",\"platform\":\"battlenet\",\"layout\":\"bnet-current\",\"cycleStatus\":\"OK\",\"datasets\":{{\"mp\":{{\"status\":\"WAITING\"}},\"br\":{{\"status\":\"WAITING\"}},\"resurgence\":{{\"status\":\"WAITING\"}}}}}}";
        return Task.FromResult((0, json, string.Empty));
    }
}

sealed class PriorityEngineRunner(AppLogger log) : EngineRunner(log)
{
    public override Task<(int ExitCode, string StdOut, string StdErr)> RunOnceAsync(string engineDirectory, VerifiedBuildIdentity? identity, CancellationToken token)
    {
        var capturedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var players = Enumerable.Range(1, 250).Select(position => new { position, sr = 30000 - position, name = $"player-{position}" }).ToArray();
        var json = JsonSerializer.Serialize(new { capturedAt, platform = "battlenet", layout = "bnet-current", cycleStatus = "OK", datasets = new { mp = new { status = "OK", rows = 250, capturedAt, players }, br = new { status = "OK", rows = 250, capturedAt, players }, resurgence = new { status = "OK", rows = 250, capturedAt, players } } });
        return Task.FromResult((0, json, string.Empty));
    }
}


sealed class FakeStartupRegistration : IStartupRegistration
{
    public List<(bool Enabled, string ExecutablePath)> Calls { get; } = [];
    public StartupRegistrationResult? NextResult { get; set; }
    public StartupRegistrationResult Reconcile(bool enabled, string executablePath)
    {
        Calls.Add((enabled, executablePath));
        var result = NextResult ?? (enabled ? StartupRegistrationResult.Registered : StartupRegistrationResult.Removed);
        NextResult = null;
        return result;
    }
}

sealed class FakeStationWatcherPlatform : IStationWatcherPlatform
{
    public StationCodProcess? Cod { get; set; }
    public bool InternetAvailable { get; set; } = true;
    public bool ClearCodOnClose { get; set; }
    // Presence is intentionally independent from this test-only UI responsiveness signal.
    public bool UiResponsive { get; set; } = true;
    public bool CloseResult { get; set; } = true;
    public SafeModeDismissResult SafeModeResult { get; set; } = SafeModeDismissResult.NotPresent;
    public Queue<BattleNetLaunchProgress> LaunchResults { get; } = new();
    public int LaunchCalls { get; private set; }
    public int CloseCalls { get; private set; }
    public int SafeModeCalls { get; private set; }
    public int ForegroundCalls { get; private set; }
    public int ResetCalls { get; private set; }
    public StationCodProcess? FindCodProcess() => Cod;
    public void ResetLaunchAttempt() => ResetCalls++;
    public Task<BattleNetLaunchProgress> AdvanceCodLaunchAsync(CancellationToken token)
    {
        LaunchCalls++;
        return Task.FromResult(LaunchResults.Count > 0 ? LaunchResults.Dequeue() : BattleNetLaunchProgress.WaitingForBattleNet);
    }
    public Task<bool> IsInternetAvailableAsync(CancellationToken token) => Task.FromResult(InternetAvailable);
    public Task<bool> CloseCodForRecoveryAsync(int pid, CancellationToken token)
    {
        CloseCalls++;
        if (ClearCodOnClose && CloseResult) Cod = null;
        return Task.FromResult(CloseResult);
    }
    public Task<SafeModeDismissResult> DismissSafeModeNoAsync(CancellationToken token) { SafeModeCalls++; return Task.FromResult(SafeModeResult); }
    public Task<bool> BringCodToForegroundAsync(int pid, CancellationToken token) { ForegroundCalls++; return Task.FromResult(true); }
}
sealed class StaticTokenProvider(string? token) : IApiTokenProvider { public string? GetToken() => token; }
sealed class RecordingHandler(IEnumerable<HttpStatusCode> statuses) : HttpMessageHandler
{
    private readonly Queue<HttpStatusCode> statuses = new(statuses); public int Calls { get; private set; } public string? Body { get; private set; } public string? Authorization { get; private set; } public List<string> Modes { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++; Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token); Authorization = request.Headers.Authorization?.ToString();
        if (Body is not null) { using var json = JsonDocument.Parse(Body); Modes.Add(json.RootElement.GetProperty("mode").GetString() ?? string.Empty); }
        return new HttpResponseMessage(statuses.Count > 0 ? statuses.Dequeue() : HttpStatusCode.OK);
    }
}
