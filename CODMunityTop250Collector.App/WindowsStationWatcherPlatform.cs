using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CODMunityTop250Collector.Core;

namespace CODMunityTop250Collector.App;

// Windows-only adapter for the small, testable StationWatcher in Core. It never launches cod.exe
// directly: every launch goes through the locally validated Battle.net URI and Play control.
internal sealed class WindowsStationWatcherPlatform(Action<string, string?> log) : IStationWatcherPlatform
{
    private const string BattleNetUri = "battlenet://game/auks";
    private const double PlayThreshold = .950;
    private readonly string playTemplatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "watchdog", "bnet-play.png");
    private DateTimeOffset? nextLaunchUriUtc;
    private DateTimeOffset? nextRestoreUriUtc;
    private bool lastBattleNetRunning;
    private IntPtr handledSafeModeWindow;
    private int handledSafeModePid;

    public StationCodProcess? FindCodProcess()
    {
        // Presence deliberately means only that a cod.exe process exists.  Do not use a main
        // window, responsiveness, or foreground state here: a suspended/hung process is present.
        var processes = Process.GetProcessesByName("cod");
        try
        {
            if (processes.Length == 0) return null;
            var process = processes[0];
            try { return new StationCodProcess(process.Id, process.StartTime.ToUniversalTime()); }
            catch { return new StationCodProcess(process.Id, DateTimeOffset.UtcNow); }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public void ResetLaunchAttempt() { nextLaunchUriUtc = null; nextRestoreUriUtc = null; }

    public async Task<BattleNetLaunchProgress> AdvanceCodLaunchAsync(CancellationToken token)
    {
        var primary = FindPrimaryBattleNetWindow(out var appRunning);
        if (appRunning != lastBattleNetRunning)
        {
            lastBattleNetRunning = appRunning;
            log("WATCHER_BNET_APP_STATE", "running=" + appRunning.ToString().ToLowerInvariant());
        }
        if (!appRunning)
        {
            RequestBattleNetUri("WATCHER_BNET_LAUNCH_REQUESTED", ref nextLaunchUriUtc);
            return BattleNetLaunchProgress.WaitingForBattleNet;
        }
        if (primary is null)
        {
            log("WATCHER_BNET_PROCESS_ONLY", null);
            RequestBattleNetUri("WATCHER_BNET_RESTORE_REQUESTED", ref nextRestoreUriUtc);
            return BattleNetLaunchProgress.WaitingForBattleNet;
        }
        log("WATCHER_BNET_WINDOW_FOUND", "hwnd=0x" + primary.Value.Handle.ToInt64().ToString("X"));
        if (Native.IsIconic(primary.Value.Handle))
        {
            log("WATCHER_BNET_WINDOW_MINIMIZED", null);
            Native.ShowWindow(primary.Value.Handle, Native.SwRestore);
            await Task.Delay(300, token);
            primary = FindPrimaryBattleNetWindow(out _);
            if (primary is null || Native.IsIconic(primary.Value.Handle))
            {
                log("WATCHER_BNET_WINDOW_RESTORE_FAILED", null);
                return BattleNetLaunchProgress.WaitingForBattleNet;
            }
            log("WATCHER_BNET_WINDOW_RESTORED", null);
        }
        Native.SetForegroundWindow(primary.Value.Handle);
        if (!File.Exists(playTemplatePath))
        {
            log("WATCHER_PLAY_TEMPLATE_MISSING", "path=Assets\\watchdog\\bnet-play.png");
            return BattleNetLaunchProgress.Failed;
        }
        if (!PlayTemplateMatcher.TryFind(playTemplatePath, primary.Value.Handle, out var match)) return BattleNetLaunchProgress.Failed;
        if (match.Confidence < PlayThreshold) return BattleNetLaunchProgress.PlayUnavailable;
        log("WATCHER_PLAY_FOUND", $"confidence={match.Confidence:F4}");
        if (FindCodProcess() is not null) return BattleNetLaunchProgress.WaitingForBattleNet;
        return TryClickPlay(primary.Value, match) ? BattleNetLaunchProgress.PlayClicked : BattleNetLaunchProgress.Failed;
    }

    private void RequestBattleNetUri(string eventName, ref DateTimeOffset? nextRequestUtc)
    {
        var now = DateTimeOffset.UtcNow;
        if (nextRequestUtc is { } next && next > now) return;
        nextRequestUtc = now.AddSeconds(60);
        log(eventName, "uri=" + BattleNetUri);
        try
        {
            using var started = Process.Start(new ProcessStartInfo(BattleNetUri) { UseShellExecute = true });
            log("WATCHER_BNET_LAUNCH_RESULT", "success=true");
        }
        catch { log("WATCHER_BNET_LAUNCH_RESULT", "success=false win32Error=" + Marshal.GetLastWin32Error()); }
    }
    public async Task<bool> IsInternetAvailableAsync(CancellationToken token)
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) return false;
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(IPAddress.Parse("1.1.1.1"), 443, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public async Task<bool> CloseCodForRecoveryAsync(int pid, CancellationToken token)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            log("WATCHER_COD_KILL_REQUESTED", "pid=" + pid);
            try { process.Kill(entireProcessTree: false); }
            catch (Exception ex)
            {
                log("WATCHER_COD_KILL_FAILED", "pid=" + pid + " reason=" + ex.GetType().Name);
                return false;
            }
            if (await WaitForExitAsync(process, TimeSpan.FromSeconds(30), token))
            {
                log("WATCHER_COD_EXIT_CONFIRMED", "pid=" + pid);
                return true;
            }
            log("WATCHER_COD_KILL_FAILED", "pid=" + pid + " reason=ExitNotConfirmed");
            return false;
        }
        catch (ArgumentException)
        {
            log("WATCHER_COD_EXIT_CONFIRMED", "pid=" + pid);
            return true;
        }
        catch (Exception ex)
        {
            log("WATCHER_COD_KILL_FAILED", "pid=" + pid + " reason=" + ex.GetType().Name);
            return false;
        }
    }
    public Task<SafeModeDismissResult> DismissSafeModeNoAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var cod = FindCodProcess();
        if (cod is null) return Task.FromResult(SafeModeDismissResult.NotPresent);

        // Native COD recovery dialogs use standard Win32 Yes/No/Cancel control IDs.  Do not
        // use the localized title, prompt, or button caption as an identity signal.
        var dialog = Native.FindSafeModeDialog(cod.Value.Pid);
        if (dialog == IntPtr.Zero)
        {
            handledSafeModeWindow = IntPtr.Zero;
            handledSafeModePid = 0;
            return Task.FromResult(SafeModeDismissResult.NotPresent);
        }
        if (dialog == handledSafeModeWindow && cod.Value.Pid == handledSafeModePid) return Task.FromResult(SafeModeDismissResult.NotPresent);
        log("WATCHER_SAFE_MODE_DETECTED", "hwnd=0x" + dialog.ToInt64().ToString("X") + " pid=" + cod.Value.Pid);

        var noButton = Native.FindChildButtonByDialogControlId(dialog, SafeModeDialogRules.IdNo);
        if (noButton == IntPtr.Zero)
        {
            log("WATCHER_SAFE_MODE_FAILED", "reason=NoButtonUnavailable");
            return Task.FromResult(SafeModeDismissResult.Failed);
        }
        log("WATCHER_SAFE_MODE_BUTTON_FOUND", "id=" + SafeModeDialogRules.IdNo);
        _ = Native.SendMessage(noButton, Native.BmClick, IntPtr.Zero, IntPtr.Zero);
        handledSafeModeWindow = dialog;
        handledSafeModePid = cod.Value.Pid;
        log("WATCHER_SAFE_MODE_NO", null);
        return Task.FromResult(SafeModeDismissResult.Dismissed);
    }
    public Task<bool> BringCodToForegroundAsync(int pid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.GetProcessById(pid);
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) return Task.FromResult(false);
            if (Native.IsIconic(handle)) Native.ShowWindow(handle, Native.SwRestore);
            return Task.FromResult(Native.SetForegroundWindow(handle));
        }
        catch { return Task.FromResult(false); }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken token)
    {
        if (process.HasExited) return true;
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(token);
        delay.CancelAfter(timeout);
        try { await process.WaitForExitAsync(delay.Token); return true; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return process.HasExited; }
    }

    private static BattleNetWindow? FindPrimaryBattleNetWindow(out bool appRunning)
    {
        var processes = Process.GetProcessesByName("Battle.net").Concat(Process.GetProcessesByName("Battle.net Launcher")).ToArray();
        try
        {
            appRunning = processes.Length > 0;
            var windows = new List<BattleNetWindow>();
            foreach (var process in processes)
            {
                try
                {
                    var handle = process.MainWindowHandle;
                    var title = process.MainWindowTitle;
                    if (handle != IntPtr.Zero && title.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)) windows.Add(new(process.Id, handle, title));
                }
                catch { }
            }
            return windows.OrderByDescending(window => window.Title.Equals("Battle.net", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(window => Native.IsWindowVisible(window.Handle))
                .ThenBy(window => window.Pid)
                .Select(window => (BattleNetWindow?)window).FirstOrDefault();
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static bool TryClickPlay(BattleNetWindow window, PlayMatch match)
    {
        if (!Native.TryGetWindowRect(window.Handle, out var rect)) return false;
        var point = new Point(rect.Left + match.CenterX, rect.Top + match.CenterY);
        if (point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom) return false;
        var target = Native.WindowFromPoint(point);
        if (target == IntPtr.Zero || (Native.GetRootWindow(target) != window.Handle && Native.GetWindowPid(target) != window.Pid)) return false;
        if (!Native.GetCursorPos(out var previous) || !Native.SetCursorPos(point.X, point.Y)) return false;
        try { return Native.SendLeftClick() == 2; }
        finally { Native.SetCursorPos(previous.X, previous.Y); }
    }

    private readonly record struct BattleNetWindow(int Pid, IntPtr Handle, string Title);
    private readonly record struct PlayMatch(int X, int Y, int Width, int Height, double Confidence)
    {
        public int CenterX => X + Width / 2;
        public int CenterY => Y + Height / 2;
    }

    private static class PlayTemplateMatcher
    {
        public static bool TryFind(string templatePath, IntPtr window, out PlayMatch match)
        {
            match = default;
            if (!File.Exists(templatePath)) return false;
            if (!Native.TryGetWindowRect(window, out var rect)) return false;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;
            using var screenshot = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(screenshot)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            using var template = new Bitmap(templatePath);
            if (template.Width > screenshot.Width || template.Height > screenshot.Height) return false;
            match = FindBest(screenshot, template);
            return true;
        }

        private static PlayMatch FindBest(Bitmap target, Bitmap template)
        {
            var targetPixels = ToGrayscale(target);
            var templatePixels = ToGrayscale(template);
            var samples = BuildSampleOffsets(template.Width, template.Height);
            var best = new PlayMatch(0, 0, template.Width, template.Height, -1);
            const int step = 4;
            for (var y = 0; y <= target.Height - template.Height; y += step)
                for (var x = 0; x <= target.Width - template.Width; x += step)
                    best = Better(best, Score(targetPixels, target.Width, templatePixels, template.Width, x, y, samples), x, y, template.Width, template.Height);
            for (var y = Math.Max(0, best.Y - step); y <= Math.Min(target.Height - template.Height, best.Y + step); y++)
                for (var x = Math.Max(0, best.X - step); x <= Math.Min(target.Width - template.Width, best.X + step); x++)
                    best = Better(best, Score(targetPixels, target.Width, templatePixels, template.Width, x, y, samples), x, y, template.Width, template.Height);
            return best with { Confidence = Math.Max(0, best.Confidence) };
        }

        private static PlayMatch Better(PlayMatch current, double score, int x, int y, int width, int height) => score > current.Confidence ? new(x, y, width, height, score) : current;
        private static IReadOnlyList<Point> BuildSampleOffsets(int width, int height)
        {
            var result = new List<Point>();
            for (var row = 0; row < 16; row++) for (var column = 0; column < 16; column++) result.Add(new(column * (width - 1) / 15, row * (height - 1) / 15));
            return result.Distinct().ToArray();
        }
        private static double Score(byte[] target, int targetWidth, byte[] template, int templateWidth, int x, int y, IReadOnlyList<Point> samples)
        {
            var difference = 0;
            foreach (var sample in samples) difference += Math.Abs(target[(y + sample.Y) * targetWidth + x + sample.X] - template[sample.Y * templateWidth + sample.X]);
            return 1d - difference / (samples.Count * 255d);
        }
        private static byte[] ToGrayscale(Bitmap bitmap)
        {
            var pixels = new byte[bitmap.Width * bitmap.Height];
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var raw = new byte[Math.Abs(data.Stride) * bitmap.Height];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++)
                {
                    var index = y * Math.Abs(data.Stride) + x * 4;
                    pixels[y * bitmap.Width + x] = (byte)((raw[index] * 114 + raw[index + 1] * 587 + raw[index + 2] * 299) / 1000);
                }
                return pixels;
            }
            finally { bitmap.UnlockBits(data); }
        }
    }

    private static class Native
    {
        internal const int SwRestore = 9;
        internal static readonly IntPtr BmClick = new(0x00F5);
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct PointNative { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetCursorPos(out PointNative point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int maxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetDlgCtrlID(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SendMessage(IntPtr window, IntPtr message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);

        internal static bool TryGetWindowRect(IntPtr window, out Rect rect) => GetWindowRect(window, out rect);
        internal static IntPtr GetRootWindow(IntPtr window) => GetAncestor(window, 2);
        internal static int? GetWindowPid(IntPtr window) { if (window == IntPtr.Zero) return null; GetWindowThreadProcessId(window, out var pid); return pid == 0 ? null : (int)pid; }
        internal static uint SendLeftClick()
        {
            var inputs = new[] { new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 2 } } }, new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 4 } } } };
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        }
        internal static IntPtr FindSafeModeDialog(int codProcessId)
        {
            var result = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                var hasYes = FindChildButtonByDialogControlId(window, SafeModeDialogRules.IdYes) != IntPtr.Zero;
                var hasNo = FindChildButtonByDialogControlId(window, SafeModeDialogRules.IdNo) != IntPtr.Zero;
                var hasCancel = FindChildButtonByDialogControlId(window, SafeModeDialogRules.IdCancel) != IntPtr.Zero;
                if (!SafeModeDialogRules.IsCandidate(true, codProcessId, GetWindowPid(window) ?? 0, ReadClassName(window), hasYes, hasNo, hasCancel)) return true;
                result = window;
                return false;
            }, IntPtr.Zero);
            return result;
        }

        internal static IntPtr FindChildButtonByDialogControlId(IntPtr parent, int controlId)
        {
            var result = IntPtr.Zero;
            EnumChildWindows(parent, (window, _) =>
            {
                if (string.Equals(ReadClassName(window), "Button", StringComparison.Ordinal) && GetDlgCtrlID(window) == controlId)
                {
                    result = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }
        private static string ReadWindowText(IntPtr window) { var text = new System.Text.StringBuilder(256); _ = GetWindowText(window, text, text.Capacity); return text.ToString(); }
        private static string ReadClassName(IntPtr window) { var text = new System.Text.StringBuilder(256); _ = GetClassName(window, text, text.Capacity); return text.ToString(); }
    }
}