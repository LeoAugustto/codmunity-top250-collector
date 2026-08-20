using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CODMunityTop250Collector.Core;

public enum CodWatchdogStatus { Disabled, Healthy, NoDataWarning, NoDataConfirmed, CheckingGameState, WaitingForBattleNet, WaitingForCod, WaitingForData, RestartingCod, RecoveryCooldown, RecoveryFailed, Unavailable }
public sealed record CodWatchdogSnapshot(CodWatchdogStatus Status, string Detail = "");
public enum WatchdogCollectionHealth { Healthy, Warning, Confirmed }

public sealed class BattleNetLaunchGate
{
    private bool requested;
    public bool TryRequestUri(bool battleNetAppRunning)
    {
        if (battleNetAppRunning || requested) return false;
        requested = true;
        return true;
    }
    public void Reset() => requested = false;
}
public static class CodWatchdogPolicy
{
    public static readonly TimeSpan WarningAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromMinutes(10);
    public const double ActivePlayThreshold = .950;
    public static WatchdogCollectionHealth AssessCollectionHealth(DateTimeOffset now, DateTimeOffset? lastCycleOk, DateTimeOffset processStartedAt)
    {
        var age = now - (lastCycleOk ?? processStartedAt);
        return age <= WarningAfter ? WatchdogCollectionHealth.Healthy : age < ConfirmAfter ? WatchdogCollectionHealth.Warning : WatchdogCollectionHealth.Confirmed;
    }
    public static bool IsPlayConfidenceSufficient(double confidence) => confidence >= ActivePlayThreshold;
    public static bool RequiresWindowRestore(bool windowExists, bool windowMinimized) => windowExists && windowMinimized;
    public static bool IsWindowUsable(bool windowExists, bool windowMinimized) => windowExists && !windowMinimized;
    public static Point WindowRelativeToScreen(int windowLeft, int windowTop, PlayMatch match) => new(windowLeft + match.CenterX, windowTop + match.CenterY);
}

// Production adaptation of the Battle.net-only recovery flow validated in tools/CODWatchdogPrototype.
public sealed class CodWatchdogService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan BattleNetTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CodStartTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CollectionResumeTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private readonly CodProcessMonitor monitor;
    private readonly CollectorScheduler scheduler;
    private readonly AppLogger log;
    private readonly string templatePath;
    private readonly RecoveryBudget budget = new();
    private readonly BattleNetLaunchGate battleNetLaunchGate = new();
    private WatchdogPhase phase;
    private CodWatchdogSnapshot snapshot = new(CodWatchdogStatus.Disabled);
    private DateTimeOffset deadlineUtc;
    private DateTimeOffset closeDeadlineUtc;
    private DateTimeOffset retryAfterUtc;
    private DateTimeOffset? startedCodUtc;
    private int? recoveryPid;
    private string? lastKnownPlatform;
    private bool staleConfirmed;
    private bool checkingGameState;
    private bool forceKillAttempted;
    private bool staleRecovery;
    private int? observedCodPid;
    private bool? lastEnabled;
    private bool? lastBattleNetAppRunning;
    private int? lastBattleNetPid;
    private IntPtr lastBattleNetWindow;

    public event Action<CodWatchdogSnapshot>? Changed;
    public CodWatchdogSnapshot Snapshot => snapshot;

    public void NotifySettingsChanged(CollectorSettings settings)
    {
        if (!settings.KeepCallOfDutyRunning) { ResetAttempt(); SetEnabledLogged(false); SetStatus(CodWatchdogStatus.Disabled, "Watchdog disabled"); }
    }

    public CodWatchdogService(CodProcessMonitor monitor, CollectorScheduler scheduler, AppLogger log, string templatePath)
    {
        this.monitor = monitor; this.scheduler = scheduler; this.log = log; this.templatePath = templatePath;
    }

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await EvaluateAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { log.Write("WATCHDOG_EXCEPTION", ex.GetType().Name); SetStatus(CodWatchdogStatus.RecoveryFailed, "Watcher error"); }
            await Task.Delay(PollInterval, token);
        }
    }

    private Task EvaluateAsync(CancellationToken token)
    {
        var settings = scheduler.Settings;
        if (!settings.KeepCallOfDutyRunning)
        {
            ResetAttempt(); SetEnabledLogged(false); SetStatus(CodWatchdogStatus.Disabled, "Watchdog disabled"); return Task.CompletedTask;
        }
        SetEnabledLogged(true);
        if (!settings.CollectorEnabled)
        {
            ResetAttempt(); SetStatus(CodWatchdogStatus.Disabled, "Watcher paused"); return Task.CompletedTask;
        }
        var now = DateTimeOffset.UtcNow;
        if (budget.IsCoolingDown(now)) { SetStatus(CodWatchdogStatus.RecoveryCooldown, "Recovery cooldown"); return Task.CompletedTask; }
        if (phase == WatchdogPhase.Failed && now < retryAfterUtc) { SetStatus(CodWatchdogStatus.RecoveryFailed, "Recovery retry pending"); return Task.CompletedTask; }
        if (phase == WatchdogPhase.Failed) ResetAttempt();

        var pid = monitor.FindSinglePid();
        var descriptor = pid is null ? null : monitor.Describe(pid.Value);
        if (descriptor is not null) lastKnownPlatform = descriptor.Platform;
        if (pid is not null && descriptor?.Platform is not "battlenet")
        {
            ResetAttempt(); SetStatus(CodWatchdogStatus.Unavailable, "Watchdog available for Battle.net only"); return Task.CompletedTask;
        }
        if (pid is null && lastKnownPlatform is not null && lastKnownPlatform != "battlenet")
        {
            ResetAttempt(); SetStatus(CodWatchdogStatus.Unavailable, "Watchdog available for Battle.net only"); return Task.CompletedTask;
        }

        if (phase == WatchdogPhase.WaitingForCodClose) return ContinueCodClose(pid, now);
        if (pid is not null) return ContinueWithCod(pid.Value, now);
        observedCodPid = null;
        return ContinueStartup(now);
    }

    private Task ContinueWithCod(int pid, DateTimeOffset now)
    {
        if (phase == WatchdogPhase.WaitingForCod)
        {
            recoveryPid = pid; startedCodUtc = ProcessStartedAt(pid) ?? now; phase = WatchdogPhase.WaitingForData; deadlineUtc = now.Add(CollectionResumeTimeout);
            log.Write("WATCHDOG_COD_STARTED", $"pid={pid}"); log.Write("WATCHDOG_WAITING_FOR_DATA"); SetStatus(CodWatchdogStatus.WaitingForData, "Waiting for collection"); return Task.CompletedTask;
        }
        if (phase == WatchdogPhase.WaitingForData)
        {
            if (HasCycleAfter(startedCodUtc)) { log.Write("WATCHDOG_RECOVERY_OK", staleRecovery ? "reason=CollectionRestored" : null); ResetAttempt(); SetStatus(CodWatchdogStatus.Healthy, "COD healthy"); return Task.CompletedTask; }
            if (now >= deadlineUtc) return Fail(now, "CollectionDidNotResume");
            SetStatus(CodWatchdogStatus.WaitingForData, "Waiting for collection"); return Task.CompletedTask;
        }
        if (phase is WatchdogPhase.WaitingForBattleNet or WatchdogPhase.WaitingForPlay) return Task.CompletedTask;

        var processStartedAt = ProcessStartedAt(pid) ?? now;
        var last = scheduler.Snapshot.LastSuccess;
        if (observedCodPid != pid)
        {
            observedCodPid = pid; staleConfirmed = false; checkingGameState = false;
        }
        if ((last is null || last < processStartedAt) && now - processStartedAt < StartupGrace)
        {
            SetStatus(CodWatchdogStatus.WaitingForData, "Waiting for first collection"); return Task.CompletedTask;
        }
        switch (CodWatchdogPolicy.AssessCollectionHealth(now, last, processStartedAt))
        {
            case WatchdogCollectionHealth.Healthy:
                staleConfirmed = false; checkingGameState = false; SetStatus(CodWatchdogStatus.Healthy, "COD healthy"); return Task.CompletedTask;
            case WatchdogCollectionHealth.Warning:
                staleConfirmed = false; checkingGameState = false; SetStatus(CodWatchdogStatus.NoDataWarning, "Collection delayed"); return Task.CompletedTask;
            case WatchdogCollectionHealth.Confirmed when !staleConfirmed:
                staleConfirmed = true; SetStatus(CodWatchdogStatus.NoDataConfirmed, "Collection stale confirmed"); return Task.CompletedTask;
            case WatchdogCollectionHealth.Confirmed when !checkingGameState:
                checkingGameState = true; SetStatus(CodWatchdogStatus.CheckingGameState, "Checking game state"); return Task.CompletedTask;
        }
        // The next stale evaluation is the validated recovery boundary.
        var freshPid = monitor.FindSinglePid();
        var freshLast = scheduler.Snapshot.LastSuccess;
        if (!scheduler.Settings.KeepCallOfDutyRunning || !scheduler.Settings.CollectorEnabled || freshPid != pid)
        {
            log.Write("WATCHDOG_RECOVERY_CANCELLED", "reason=PreCloseStateChanged"); ResetAttempt(); return Task.CompletedTask;
        }
        if (freshLast is { } freshTimestamp && now - freshTimestamp < CodWatchdogPolicy.ConfirmAfter)
        {
            log.Write("WATCHDOG_RECOVERY_CANCELLED", "reason=FreshCycleDetected"); ResetAttempt(); SetStatus(CodWatchdogStatus.Healthy, "COD healthy"); return Task.CompletedTask;
        }
        staleRecovery = true; recoveryPid = pid; phase = WatchdogPhase.WaitingForCodClose; deadlineUtc = now.Add(CloseGrace); closeDeadlineUtc = now.Add(CloseTimeout); forceKillAttempted = false;
        log.Write("WATCHDOG_RECOVERY_START", "reason=CollectionStale"); log.Write("WATCHDOG_COD_CLOSE_REQUESTED", "method=window"); RequestClose(pid); SetStatus(CodWatchdogStatus.RestartingCod, "Restarting Call of Duty"); return Task.CompletedTask;
    }

    private Task ContinueCodClose(int? pid, DateTimeOffset now)
    {
        if (pid is null)
        {
            log.Write("WATCHDOG_COD_CLOSED"); phase = WatchdogPhase.Idle; staleConfirmed = false; SetStatus(CodWatchdogStatus.WaitingForBattleNet, "Starting Call of Duty"); return Task.CompletedTask;
        }
        if (!forceKillAttempted && now >= deadlineUtc)
        {
            forceKillAttempted = true; log.Write("WATCHDOG_COD_CLOSE_FALLBACK", "method=terminate");
            if (!TerminatePid(pid.Value)) return Fail(now, "CodDidNotExit");
        }
        if (now >= closeDeadlineUtc) return Fail(now, "CodDidNotExit");
        SetStatus(CodWatchdogStatus.RestartingCod, "Restarting Call of Duty"); return Task.CompletedTask;
    }

    private Task ContinueStartup(DateTimeOffset now)
    {
        if (phase == WatchdogPhase.WaitingForCod)
        {
            if (now >= deadlineUtc) return Fail(now, "CodDidNotStart");
            SetStatus(CodWatchdogStatus.WaitingForCod, "Starting Call of Duty"); return Task.CompletedTask;
        }
        if (phase == WatchdogPhase.Idle) log.Write("WATCHDOG_RECOVERY_START", "reason=CodMissing");
        var battleNet = InspectBattleNet();
        LogBattleNetObservation(battleNet);
        if (!battleNet.AppRunning)
        {
            if (battleNetLaunchGate.TryRequestUri(battleNet.AppRunning))
            {
                phase = WatchdogPhase.WaitingForBattleNet; deadlineUtc = now.Add(BattleNetTimeout);
                log.Write("WATCHDOG_BNET_LAUNCH_REQUESTED", "uri=battlenet://game/auks");
                try
                {
                    var started = Process.Start(new ProcessStartInfo("battlenet://game/auks") { UseShellExecute = true });
                    log.Write("WATCHDOG_BNET_LAUNCH_RESULT", "success=true");
                    started?.Dispose();
                }
                catch
                {
                    log.Write("WATCHDOG_BNET_LAUNCH_RESULT", $"success=false win32Error={Marshal.GetLastWin32Error()}");
                    return Fail(now, "BattleNetDidNotOpen");
                }
            }
            if (phase != WatchdogPhase.WaitingForBattleNet) { phase = WatchdogPhase.WaitingForBattleNet; deadlineUtc = now.Add(BattleNetTimeout); }
            if (now >= deadlineUtc) return Fail(now, "BattleNetDidNotOpen");
            log.Write("WATCHDOG_WAITING_FOR_BNET"); SetStatus(CodWatchdogStatus.WaitingForBattleNet, "Starting Battle.net"); return Task.CompletedTask;
        }
        var primary = battleNet.PrimaryWindow;
        if (primary is null)
        {
            if (phase != WatchdogPhase.WaitingForBattleNet) { phase = WatchdogPhase.WaitingForBattleNet; deadlineUtc = now.Add(BattleNetTimeout); }
            if (now >= deadlineUtc) return Fail(now, "PrimaryWindowUnavailable");
            log.Write("WATCHDOG_WAITING_FOR_BNET"); SetStatus(CodWatchdogStatus.WaitingForBattleNet, "Waiting for Battle.net"); return Task.CompletedTask;
        }
        if (primary is { } minimizedWindow && CodWatchdogPolicy.RequiresWindowRestore(true, minimizedWindow.Minimized) && !RestoreBattleNetWindow(minimizedWindow))
        {
            if (now >= deadlineUtc) return Fail(now, "PrimaryWindowUnavailable");
            SetStatus(CodWatchdogStatus.WaitingForBattleNet, "Restoring Battle.net"); return Task.CompletedTask;
        }
        battleNet = InspectBattleNet(); LogBattleNetObservation(battleNet); primary = battleNet.PrimaryWindow;
        if (primary is null || primary.Value.Minimized)
        {
            if (now >= deadlineUtc) return Fail(now, "PrimaryWindowUnavailable");
            SetStatus(CodWatchdogStatus.WaitingForBattleNet, "Waiting for Battle.net"); return Task.CompletedTask;
        }
        if (phase is WatchdogPhase.Idle or WatchdogPhase.WaitingForBattleNet) { phase = WatchdogPhase.WaitingForPlay; deadlineUtc = now.Add(BattleNetTimeout); }
        if (!PlayTemplateMatcher.TryFind(templatePath, primary.Value, out var match, out var confidence))
        {
            if (now >= deadlineUtc) return Fail(now, "PlayNotConfident");
            SetStatus(CodWatchdogStatus.WaitingForBattleNet, "Waiting for Play"); return Task.CompletedTask;
        }
        log.Write("WATCHDOG_PLAY_MATCH", $"confidence={confidence:F4}");
        if (!CodWatchdogPolicy.IsPlayConfidenceSufficient(confidence))
        {
            if (now >= deadlineUtc) return Fail(now, "PlayNotConfident");
            SetStatus(CodWatchdogStatus.WaitingForBattleNet, "Waiting for Play"); return Task.CompletedTask;
        }
        battleNet = InspectBattleNet(); primary = battleNet.PrimaryWindow;
        if (!scheduler.Settings.KeepCallOfDutyRunning || !scheduler.Settings.CollectorEnabled || monitor.FindSinglePid() is not null || primary is null || primary.Value.Minimized || !PlayTemplateMatcher.TryFind(templatePath, primary.Value, out match, out confidence) || !CodWatchdogPolicy.IsPlayConfidenceSufficient(confidence))
        {
            log.Write("WATCHDOG_RECOVERY_CANCELLED", "reason=PlayRevalidationFailed"); ResetAttempt(); return Task.CompletedTask;
        }
        var delivery = BattleNetPlayClick.TrySend(primary.Value, match);
        foreach (var detail in delivery.Diagnostics) log.Write("WATCHDOG_" + detail.Event, detail.Detail);
        if (!delivery.Sent) return Fail(now, "ClickFailed");
        log.Write("WATCHDOG_PLAY_CLICK_SENT"); phase = WatchdogPhase.WaitingForCod; deadlineUtc = now.Add(CodStartTimeout); SetStatus(CodWatchdogStatus.WaitingForCod, "Starting Call of Duty"); return Task.CompletedTask;
    }
    private Task Fail(DateTimeOffset now, string reason)
    {
        phase = WatchdogPhase.Failed; retryAfterUtc = now.Add(RetryDelay); budget.RecordFailure(now);
        log.Write("WATCHDOG_RECOVERY_FAILED", $"reason={reason}"); SetStatus(CodWatchdogStatus.RecoveryFailed, "Recovery failed"); return Task.CompletedTask;
    }
    private bool HasCycleAfter(DateTimeOffset? start) => start is { } value && scheduler.Snapshot.LastSuccess is { } cycle && cycle >= value;
    private static DateTimeOffset? ProcessStartedAt(int pid) { try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime(); } catch { return null; } }
    private static bool RequestClose(int pid) { try { using var p = Process.GetProcessById(pid); return p.CloseMainWindow(); } catch { return false; } }
    private static bool TerminatePid(int pid) { try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: false); return true; } catch { return false; } }
    private BattleNetObservation InspectBattleNet()
    {
        var processes = Process.GetProcessesByName("Battle.net").Concat(Process.GetProcessesByName("Battle.net Launcher")).ToArray();
        try
        {
            var candidates = new List<BattleNetWindow>();
            foreach (var process in processes)
            {
                try
                {
                    var handle = process.MainWindowHandle; var title = process.MainWindowTitle;
                    if (handle != IntPtr.Zero && title.Contains("Battle.net", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(new(process.Id, handle, title, Native.IsWindowVisible(handle), Native.IsIconic(handle)));
                }
                catch { }
            }
            var primary = candidates.OrderByDescending(candidate => candidate.Title.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)).ThenByDescending(candidate => candidate.Visible).ThenBy(candidate => candidate.Pid).FirstOrDefault();
            return new(processes.Length > 0, processes.FirstOrDefault()?.Id, primary.Handle == IntPtr.Zero ? null : primary);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private void LogBattleNetObservation(BattleNetObservation observation)
    {
        if (lastBattleNetAppRunning != observation.AppRunning) { lastBattleNetAppRunning = observation.AppRunning; log.Write("WATCHDOG_BNET_APP_STATE", $"running={observation.AppRunning.ToString().ToLowerInvariant()}"); }
        if (observation.AppRunning && lastBattleNetPid != observation.ProcessPid) { lastBattleNetPid = observation.ProcessPid; log.Write("WATCHDOG_BNET_APP_DETECTED", $"pid={observation.ProcessPid?.ToString() ?? "unknown"}"); }
        var handle = observation.PrimaryWindow?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero && handle != lastBattleNetWindow) { lastBattleNetWindow = handle; log.Write("WATCHDOG_BNET_WINDOW_FOUND", $"hwnd=0x{handle.ToInt64():X}"); }
    }
    private bool RestoreBattleNetWindow(BattleNetWindow window)
    {
        log.Write("WATCHDOG_BNET_WINDOW_MINIMIZED"); Native.ShowWindow(window.Handle, 9); Thread.Sleep(200);
        if (Native.IsIconic(window.Handle) || !Native.TryGetWindowRect(window.Handle, out _)) { log.Write("WATCHDOG_BNET_WINDOW_RESTORE_FAILED"); return false; }
        log.Write("WATCHDOG_BNET_WINDOW_RESTORED");
        if (!Native.SetForeground(window.Handle)) log.Write("WATCHDOG_FOREGROUND_REQUEST_REJECTED", $"win32Error={Marshal.GetLastWin32Error()}");
        return true;
    }
    private void ResetAttempt() { battleNetLaunchGate.Reset(); phase = WatchdogPhase.Idle; staleConfirmed = false; checkingGameState = false; forceKillAttempted = false; startedCodUtc = null; recoveryPid = null; staleRecovery = false; deadlineUtc = default; closeDeadlineUtc = default; }
    private void SetEnabledLogged(bool enabled) { if (lastEnabled == enabled) return; lastEnabled = enabled; log.Write(enabled ? "WATCHDOG_ENABLED" : "WATCHDOG_DISABLED"); }
    private void SetStatus(CodWatchdogStatus status, string detail) { var value = new CodWatchdogSnapshot(status, detail); if (value == snapshot) return; var old = snapshot; snapshot = value; Changed?.Invoke(value); log.Write("WATCHDOG_STATE", $"old={old.Status} new={status}"); if (status == CodWatchdogStatus.RecoveryCooldown) log.Write("WATCHDOG_COOLDOWN"); }
    private enum WatchdogPhase { Idle, WaitingForBattleNet, WaitingForPlay, WaitingForCod, WaitingForData, WaitingForCodClose, Failed }
}

public sealed class RecoveryBudget
{
    private readonly Queue<DateTimeOffset> failures = new(); private DateTimeOffset? cooldownUntil;
    public void RecordFailure(DateTimeOffset now) { while (failures.TryPeek(out var old) && now - old > TimeSpan.FromMinutes(30)) failures.Dequeue(); failures.Enqueue(now); if (failures.Count >= 3) cooldownUntil = now.AddMinutes(30); }
    public bool IsCoolingDown(DateTimeOffset now) { while (failures.TryPeek(out var old) && now - old > TimeSpan.FromMinutes(30)) failures.Dequeue(); if (cooldownUntil is { } until && until <= now) cooldownUntil = null; return cooldownUntil is { } active && active > now; }
}

public readonly record struct BattleNetWindow(int Pid, IntPtr Handle, string Title, bool Visible, bool Minimized);
internal readonly record struct BattleNetObservation(bool AppRunning, int? ProcessPid, BattleNetWindow? PrimaryWindow);
public readonly record struct PlayMatch(int X, int Y, int Width, int Height, double Confidence) { public int CenterX => X + Width / 2; public int CenterY => Y + Height / 2; }
public sealed record ClickDiagnostic(string Event, string Detail);
public sealed record PlayClickDelivery(bool Sent, IReadOnlyList<ClickDiagnostic> Diagnostics);

public static class PlayTemplateMatcher
{
    public static bool TryFind(string path, BattleNetWindow window, out PlayMatch match, out double confidence)
    {
        match = default; confidence = 0;
        if (!File.Exists(path) || !Native.TryGetWindowRect(window.Handle, out var rect)) return false;
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top; if (width <= 0 || height <= 0) return false;
        using var capture = new Bitmap(width, height); using (var graphics = Graphics.FromImage(capture)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        using var template = new Bitmap(path); if (template.Width > capture.Width || template.Height > capture.Height) return false;
        var best = FindBest(capture, template); confidence = best.Confidence; match = best; return true;
    }
    private static PlayMatch FindBest(Bitmap target, Bitmap template)
    {
        if (template.Width > target.Width || template.Height > target.Height) return new(0, 0, template.Width, template.Height, 0);
        var targetPixels = ToGrayscale(target);
        var templatePixels = ToGrayscale(template);
        var samples = BuildSampleOffsets(template.Width, template.Height, 16);
        var best = new PlayMatch(0, 0, template.Width, template.Height, -1);
        const int coarseStep = 4;
        for (var y = 0; y <= target.Height - template.Height; y += coarseStep)
            for (var x = 0; x <= target.Width - template.Width; x += coarseStep)
                best = Better(best, Score(targetPixels, target.Width, templatePixels, template.Width, x, y, samples), x, y, template.Width, template.Height);
        var startX = Math.Max(0, best.X - coarseStep); var endX = Math.Min(target.Width - template.Width, best.X + coarseStep);
        var startY = Math.Max(0, best.Y - coarseStep); var endY = Math.Min(target.Height - template.Height, best.Y + coarseStep);
        for (var y = startY; y <= endY; y++)
            for (var x = startX; x <= endX; x++)
                best = Better(best, Score(targetPixels, target.Width, templatePixels, template.Width, x, y, samples), x, y, template.Width, template.Height);
        return best with { Confidence = Math.Max(0, best.Confidence) };
    }
    private static PlayMatch Better(PlayMatch current, double score, int x, int y, int width, int height) => score > current.Confidence ? new(x, y, width, height, score) : current;
    private static IReadOnlyList<Point> BuildSampleOffsets(int width, int height, int gridSize)
    {
        var points = new List<Point>();
        for (var row = 0; row < gridSize; row++)
            for (var column = 0; column < gridSize; column++)
                points.Add(new Point(column * (width - 1) / Math.Max(1, gridSize - 1), row * (height - 1) / Math.Max(1, gridSize - 1)));
        return points.Distinct().ToArray();
    }
    private static double Score(byte[] target, int targetWidth, byte[] template, int templateWidth, int x, int y, IReadOnlyList<Point> samples)
    {
        var difference = 0;
        foreach (var sample in samples) difference += Math.Abs(target[(y + sample.Y) * targetWidth + x + sample.X] - template[sample.Y * templateWidth + sample.X]);
        return 1d - difference / (samples.Count * 255d);
    }
    private static byte[] ToGrayscale(Bitmap bitmap)
    {
        var copy = bitmap.PixelFormat == PixelFormat.Format32bppArgb ? bitmap : bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[copy.Width * copy.Height];
            var data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var raw = new byte[Math.Abs(data.Stride) * copy.Height]; Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                for (var y = 0; y < copy.Height; y++)
                    for (var x = 0; x < copy.Width; x++)
                    {
                        var index = y * Math.Abs(data.Stride) + x * 4;
                        pixels[y * copy.Width + x] = (byte)((raw[index] * 114 + raw[index + 1] * 587 + raw[index + 2] * 299) / 1000);
                    }
            }
            finally { copy.UnlockBits(data); }
            return pixels;
        }
        finally { if (!ReferenceEquals(copy, bitmap)) copy.Dispose(); }
    }
}

public static class BattleNetPlayClick
{
    private const int SwRestore = 9; private const uint InputMouse = 0; private const uint LeftDown = 2; private const uint LeftUp = 4;
    public static PlayClickDelivery TrySend(BattleNetWindow window, PlayMatch match)
    {
        var diagnostics = new List<ClickDiagnostic>();
        if (!Native.TryGetWindowRect(window.Handle, out var rect)) return Failed(diagnostics, "COORDINATE_CONVERSION_FAILED", "reason=WindowRectUnavailable");
        var point = CodWatchdogPolicy.WindowRelativeToScreen(rect.Left, rect.Top, match); if (point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom) return Failed(diagnostics, "TARGET_VALIDATION_FAILED", "reason=PointOutsideWindow");
        diagnostics.Add(new("PLAY_CLICK_TARGET", $"windowPid={window.Pid} windowHandle=0x{window.Handle.ToInt64():X} windowRect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} relativeX={match.CenterX} relativeY={match.CenterY} screenX={point.X} screenY={point.Y} isVisible={Native.IsWindowVisible(window.Handle)} isIconic={Native.IsIconic(window.Handle)} foregroundPid={Native.PidOf(Native.GetForeground())?.ToString() ?? "unknown"} foregroundHandle=0x{Native.GetForeground().ToInt64():X}"));
        if (Native.IsIconic(window.Handle)) Native.ShowWindow(window.Handle, SwRestore);
        var requested = Native.SetForeground(window.Handle); Thread.Sleep(50); var foreground = Native.GetForeground(); if (!requested) diagnostics.Add(new("FOREGROUND_REQUEST_REJECTED", $"win32Error={Marshal.GetLastWin32Error()}")); if (foreground != window.Handle) diagnostics.Add(new("FOREGROUND_FAILED", $"foregroundPid={Native.PidOf(foreground)?.ToString() ?? "unknown"} advisory=true")); else diagnostics.Add(new("FOREGROUND_OK", $"pid={window.Pid}"));
        if (!Native.TryGetWindowRect(window.Handle, out rect)) return Failed(diagnostics, "COORDINATE_CONVERSION_FAILED", "reason=WindowRectChangedUnavailable"); point = CodWatchdogPolicy.WindowRelativeToScreen(rect.Left, rect.Top, match); var target = Native.WindowFromPoint(point); var targetPid = Native.PidOf(target); if (target == IntPtr.Zero || (Native.GetRoot(target) != window.Handle && targetPid != window.Pid)) return Failed(diagnostics, "TARGET_VALIDATION_FAILED", $"reason=ForeignWindow targetPid={targetPid?.ToString() ?? "unknown"}"); diagnostics.Add(new("PLAY_TARGET_VALIDATED", $"targetPid={targetPid?.ToString() ?? "unknown"}"));
        if (!Native.GetCursor(out var original)) return Failed(diagnostics, "CURSOR_POSITION_FAILED", $"stage=get win32Error={Marshal.GetLastWin32Error()}"); if (!Native.SetCursor(point)) return Failed(diagnostics, "CURSOR_POSITION_FAILED", $"stage=set win32Error={Marshal.GetLastWin32Error()}"); var accepted = SendClickNative(); var error = accepted == 2 ? 0 : Marshal.GetLastWin32Error(); diagnostics.Add(new("SENDINPUT_RESULT", $"requested=2 accepted={accepted} win32Error={error}")); if (!Native.SetCursor(original)) diagnostics.Add(new("CURSOR_RESTORE_FAILED", $"win32Error={Marshal.GetLastWin32Error()} advisory=true")); return accepted == 2 ? new(true, diagnostics) : new(false, diagnostics);
    }
    private static PlayClickDelivery Failed(List<ClickDiagnostic> diagnostics, string evt, string detail) { diagnostics.Add(new(evt, detail)); return new(false, diagnostics); }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] private struct Union { [FieldOffset(0)] public Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int Dx; public int Dy; public uint Data; public uint Flags; public uint Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    private static uint SendClickNative() { var inputs = new[] { new Input { Type = InputMouse, Data = new Union { Mouse = new Mouse { Flags = LeftDown } } }, new Input { Type = InputMouse, Data = new Union { Mouse = new Mouse { Flags = LeftUp } } } }; return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()); }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
internal static bool TryGetWindowRect(IntPtr window, out Rect rect) => GetWindowRect(window, out rect);
    internal static bool SetForeground(IntPtr window) => SetForegroundWindow(window);
    internal static IntPtr GetForeground() => GetForegroundWindow();
    internal static bool GetCursor(out Point point) => GetCursorPos(out point);
    internal static bool SetCursor(Point point) => SetCursorPos(point.X, point.Y);
    internal static IntPtr GetRoot(IntPtr window) => GetAncestor(window, 2);
    internal static int? PidOf(IntPtr window) { if (window == IntPtr.Zero) return null; GetWindowThreadProcessId(window, out var pid); return pid == 0 ? null : (int)pid; }
}