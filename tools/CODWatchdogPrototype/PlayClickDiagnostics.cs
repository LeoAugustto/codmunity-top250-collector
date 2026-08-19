using System.Drawing;
using System.Runtime.InteropServices;

namespace CODWatchdogPrototype;

public sealed record PlayClickDeliveryResult(bool Succeeded, IReadOnlyList<RecoverySignal> Signals);

internal static class NativeRecoveryInput
{
    private const int SwRestore = 9;
    private const uint InputMouse = 0;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int type, out int value, int length, out int returnLength);

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

    public static PlayClickDeliveryResult TryDeliverPlayClick(ProcessSnapshot primaryWindow, TemplateMatchResult match)
    {
        var signals = new List<RecoverySignal>();
        if (primaryWindow.WindowHandle == IntPtr.Zero || !NativeMethods.TryGetWindowRectangle(primaryWindow.WindowHandle, out var rectangle))
            return Fail(signals, "COORDINATE_CONVERSION_FAILED", "reason=WindowRectUnavailable");
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (match.CenterX < 0 || match.CenterY < 0 || match.CenterX >= width || match.CenterY >= height)
            return Fail(signals, "COORDINATE_CONVERSION_FAILED", "reason=RelativePointOutsideWindow");
        var point = new Point(rectangle.Left + match.CenterX, rectangle.Top + match.CenterY);
        var foreground = GetForegroundWindow();
        var iconic = IsIconic(primaryWindow.WindowHandle);
        signals.Add(new("PLAY_CLICK_TARGET", $"windowPid={primaryWindow.Pid} windowHandle=0x{primaryWindow.WindowHandle.ToInt64():X} windowRect={rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom} relativeX={match.CenterX} relativeY={match.CenterY} screenX={point.X} screenY={point.Y} isVisible={IsWindowVisible(primaryWindow.WindowHandle)} isIconic={iconic} foregroundPid={ProcessIdOf(foreground)?.ToString() ?? "unknown"} foregroundHandle=0x{foreground.ToInt64():X}"));
        if (point.X < rectangle.Left || point.X >= rectangle.Right || point.Y < rectangle.Top || point.Y >= rectangle.Bottom)
            return Fail(signals, "PLAY_TARGET_INVALID", "reason=PointOutsideWindowRect");

        var watchdogElevation = DescribeElevation(Environment.ProcessId);
        var battleNetElevation = DescribeElevation(primaryWindow.Pid);
        signals.Add(new("PLAY_CLICK_INTEGRITY", $"watchdogElevated={watchdogElevation} battleNetElevated={battleNetElevation}"));
        if (watchdogElevation == "false" && battleNetElevation == "true") signals.Add(new("CLICK_BLOCKED_POSSIBLE_UIPI"));

        if (iconic)
        {
            ShowWindow(primaryWindow.WindowHandle, SwRestore);
            Thread.Sleep(50);
            if (IsIconic(primaryWindow.WindowHandle)) signals.Add(new("WINDOW_RESTORE_FAILED", "reason=StillIconic"));
        }
        var foregroundRequested = SetForegroundWindow(primaryWindow.WindowHandle);
        Thread.Sleep(50);
        var foregroundAfter = GetForegroundWindow();
        if (!foregroundRequested) signals.Add(new("FOREGROUND_REQUEST_REJECTED", $"win32Error={Marshal.GetLastWin32Error()}"));
        if (foregroundAfter != primaryWindow.WindowHandle)
            signals.Add(new("FOREGROUND_FAILED", $"foregroundPid={ProcessIdOf(foregroundAfter)?.ToString() ?? "unknown"} foregroundHandle=0x{foregroundAfter.ToInt64():X} advisory=true"));
        else signals.Add(new("FOREGROUND_OK", $"pid={ProcessIdOf(foregroundAfter)}"));

        // CaptureBitmap uses GetWindowRect + CopyFromScreen, so match coordinates are window-relative.
        if (!NativeMethods.TryGetWindowRectangle(primaryWindow.WindowHandle, out rectangle)) return Fail(signals, "COORDINATE_CONVERSION_FAILED", "reason=WindowRectChangedUnavailable");
        width = rectangle.Right - rectangle.Left; height = rectangle.Bottom - rectangle.Top;
        if (match.CenterX < 0 || match.CenterY < 0 || match.CenterX >= width || match.CenterY >= height) return Fail(signals, "COORDINATE_CONVERSION_FAILED", "reason=WindowGeometryChanged");
        point = new Point(rectangle.Left + match.CenterX, rectangle.Top + match.CenterY);
        var target = WindowFromPoint(point);
        var targetPid = ProcessIdOf(target);
        var root = target == IntPtr.Zero ? IntPtr.Zero : GetAncestor(target, 2); // GA_ROOT
        if (!PlayClickValidation.IsBattleNetTarget(target, root, primaryWindow.WindowHandle, targetPid, primaryWindow.Pid))
            return Fail(signals, "PLAY_TARGET_INVALID", $"reason=ForeignWindow targetHandle=0x{target.ToInt64():X} targetPid={targetPid?.ToString() ?? "unknown"}");
        signals.Add(new("PLAY_TARGET_VALIDATED", $"targetHandle=0x{target.ToInt64():X} targetPid={targetPid?.ToString() ?? "unknown"} screenX={point.X} screenY={point.Y}"));

        if (!GetCursorPos(out var oldCursor)) return Fail(signals, "CURSOR_POSITION_FAILED", $"stage=get win32Error={Marshal.GetLastWin32Error()}");
        if (!SetCursorPos(point.X, point.Y)) return Fail(signals, "CURSOR_POSITION_FAILED", $"stage=set win32Error={Marshal.GetLastWin32Error()}");
        var inputs = new[] { MouseInput(MouseLeftDown), MouseInput(MouseLeftUp) };
        var accepted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        var error = accepted == inputs.Length ? 0 : Marshal.GetLastWin32Error();
        signals.Add(new("SENDINPUT_RESULT", $"requested={inputs.Length} accepted={accepted} win32Error={error}"));
        if (!SetCursorPos(oldCursor.X, oldCursor.Y)) signals.Add(new("CURSOR_RESTORE_FAILED", $"win32Error={Marshal.GetLastWin32Error()} advisory=true"));
        return PlayClickValidation.IsSendInputAccepted((uint)inputs.Length, accepted) ? new(true, signals) : new(false, signals);
    }
    private static PlayClickDeliveryResult Fail(List<RecoverySignal> signals, string eventName, string detail)
    {
        signals.Add(new(eventName, detail));
        return new(false, signals);
    }
    private static INPUT MouseInput(uint flags) => new() { Type = InputMouse, Data = new InputUnion { Mouse = new MOUSEINPUT { Flags = flags } } };
    private static int? ProcessIdOf(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        GetWindowThreadProcessId(handle, out var pid);
        return pid == 0 ? null : unchecked((int)pid);
    }
    private static string DescribeElevation(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return "unknown";
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return "unknown";
            try { return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) ? (elevated != 0).ToString().ToLowerInvariant() : "unknown"; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
}