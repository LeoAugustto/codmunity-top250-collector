using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CODWatchdogPrototype;

public sealed record ProcessSnapshot(string Name, int Pid, int? ParentPid, string? ExecutablePath, DateTimeOffset? StartedAtUtc, string WindowTitle, string WindowClass, IntPtr WindowHandle, bool WindowVisible, ProcessPriorityClass? Priority);
public sealed record ProtocolHandler(string Name, bool Registered, string ExecutableName, bool AcceptsUriArgument);
public sealed record BattleNetSignals(ProtocolHandler BattleNetProtocol, ProtocolHandler BlizzardProtocol, bool UpdateKeywordsObserved, IReadOnlyList<string> RecentSignalFiles);

public static class ProcessDiagnostics
{
    private static readonly string[] RelevantNames = ["cod", "battle.net", "battle.net launcher", "agent", "blizzard update agent", "blizzardbrowser", "codmunitytop250collector"];
    public static IReadOnlyList<ProcessSnapshot> ReadRelevantProcesses()
    {
        var result = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (!RelevantNames.Contains(name, StringComparer.OrdinalIgnoreCase) && !name.Contains("battle.net", StringComparison.OrdinalIgnoreCase) && !name.Contains("blizzard", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(Snapshot(process));
            }
            catch { }
            finally { process.Dispose(); }
        }
        return result.OrderBy(process => process.Name).ThenBy(process => process.Pid).ToArray();
    }
    public static ProcessSnapshot? FindCodProcess(IEnumerable<ProcessSnapshot> processes) => processes
        .Where(process => process.Name.Equals("cod.exe", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(process => process.WindowHandle != IntPtr.Zero)
        .ThenBy(process => process.Pid)
        .FirstOrDefault();
    public static ProcessSnapshot? FindPrimaryBattleNetWindow(IEnumerable<ProcessSnapshot> processes) => processes
        .Where(process => (process.Name.Equals("battle.net.exe", StringComparison.OrdinalIgnoreCase) || process.Name.Equals("battle.net launcher.exe", StringComparison.OrdinalIgnoreCase)) && process.WindowHandle != IntPtr.Zero && process.WindowVisible)
        .OrderByDescending(process => process.WindowTitle.Contains("Battle.net", StringComparison.OrdinalIgnoreCase))
        .ThenBy(process => process.Pid)
        .FirstOrDefault();
    public static IReadOnlyList<ProcessSnapshot> FindBattleNetProcesses(IEnumerable<ProcessSnapshot> processes) => processes
        .Where(process => process.Name.Equals("battle.net.exe", StringComparison.OrdinalIgnoreCase) || process.Name.Equals("battle.net launcher.exe", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    public static IReadOnlyList<ProcessSnapshot> FindAgents(IEnumerable<ProcessSnapshot> processes) => processes
        .Where(process => process.Name.Equals("agent.exe", StringComparison.OrdinalIgnoreCase) || process.Name.Contains("blizzard", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    public static ProcessSnapshot? FindCollectorProcess(IEnumerable<ProcessSnapshot> processes) => processes
        .Where(process => process.Name.Equals("CODMunityTop250Collector.exe", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(process => process.WindowHandle != IntPtr.Zero)
        .ThenBy(process => process.Pid)
        .FirstOrDefault();
    public static string? TryGetExecutablePathForDiagnostics(int processId) => NativeMethods.TryGetExecutablePath(processId);

    private static ProcessSnapshot Snapshot(Process process)
    {
        DateTimeOffset? started = null;
        ProcessPriorityClass? priority = null;
        try { started = process.StartTime.ToUniversalTime(); } catch { }
        try { priority = process.PriorityClass; } catch { }
        var handle = process.MainWindowHandle;
        return new(process.ProcessName + ".exe", process.Id, NativeMethods.TryGetParentProcessId(process.Handle), NativeMethods.TryGetExecutablePath(process.Id), started, Safe(() => process.MainWindowTitle), NativeMethods.GetWindowClass(handle), handle, NativeMethods.IsWindowVisible(handle), priority);
    }
    private static string Safe(Func<string> action) { try { return action(); } catch { return string.Empty; } }
}

public static class BattleNetDiagnostics
{
    public static BattleNetSignals ReadSignals()
    {
        var files = new List<FileInfo>();
        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net", "Logs")
        })
        {
            if (!Directory.Exists(root)) continue;
            try { files.AddRange(new DirectoryInfo(root).EnumerateFiles("*.log", SearchOption.AllDirectories)); } catch { }
        }
        var recent = files.OrderByDescending(file => file.LastWriteTimeUtc).Take(12).ToArray();
        return new(ReadProtocol("battlenet"), ReadProtocol("blizzard"), recent.Any(ContainsUpdateKeyword), recent.Select(file => file.Name).ToArray());
    }
    private static ProtocolHandler ReadProtocol(string protocol)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{protocol}\shell\open\command", writable: false);
        var command = key?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(command)) return new(protocol, false, "unknown", false);
        var match = System.Text.RegularExpressions.Regex.Match(command, "^\\s*(?:\\\"(?<exe>[^\\\"]+\\.exe)\\\"|(?<exe>\\S+\\.exe))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var executable = match.Success ? Path.GetFileName(match.Groups["exe"].Value) : "unresolved";
        return new(protocol, true, executable, command.Contains("%1", StringComparison.Ordinal) || command.Contains("%*", StringComparison.Ordinal));
    }
    private static bool ContainsUpdateKeyword(FileInfo file)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, stream.Length - 128 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return text.Contains("update", StringComparison.OrdinalIgnoreCase) || text.Contains("patch", StringComparison.OrdinalIgnoreCase) || text.Contains("install", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public sealed record CollectorLogSnapshot(DateTimeOffset? LastCycleOkUtc, CollectorActivityState Activity);

public static class CollectorHealthReader
{
    public static CollectorLogSnapshot Read()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CODMunity", "Top250Collector", "logs", "app.log");
        if (!File.Exists(path)) return new(null, CollectorActivityState.Unknown);
        try
        {
            DateTimeOffset? lastCycle = null;
            var activity = CollectorActivityState.Unknown;
            foreach (var line in File.ReadLines(path).Reverse().Take(4_000))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !DateTimeOffset.TryParse(parts[0], out var timestamp)) continue;
                if (lastCycle is null && parts[1].StartsWith("CYCLE_OK", StringComparison.Ordinal)) lastCycle = timestamp;
                if (activity == CollectorActivityState.Unknown)
                {
                    if (parts[1].StartsWith("COLLECTOR_ENABLED", StringComparison.Ordinal)) activity = CollectorActivityState.Enabled;
                    if (parts[1].StartsWith("COLLECTOR_DISABLED", StringComparison.Ordinal)) activity = CollectorActivityState.Disabled;
                }
                if (lastCycle is not null && activity != CollectorActivityState.Unknown) break;
            }
            return new(lastCycle, activity);
        }
        catch { return new(null, CollectorActivityState.Unknown); }
    }
}

public interface IScreenStateDetector { ScreenState Detect(ProcessSnapshot? codWindow, ProcessSnapshot? battleNetWindow); }
public enum ScreenState { Unknown, ServerDisconnectedRestart, ServerDisconnectedQuit, UpdateRequiresRestart, BattleNetUpdating }
public sealed class TemplateScreenStateDetector : IScreenStateDetector { public ScreenState Detect(ProcessSnapshot? codWindow, ProcessSnapshot? battleNetWindow) => ScreenState.Unknown; }

public static class WindowCapture
{
    public static string? Capture(ProcessSnapshot? snapshot, string outputDirectory)
    {
        using var image = CaptureBitmap(snapshot);
        if (image is null) return null;
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"watchdog-state-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.png");
        image.Save(path);
        foreach (var old in new DirectoryInfo(outputDirectory).EnumerateFiles("*.png").OrderByDescending(file => file.CreationTimeUtc).Skip(10)) old.Delete();
        return path;
    }
    public static Bitmap? CaptureBitmap(ProcessSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.WindowHandle == IntPtr.Zero || !NativeMethods.TryGetWindowRectangle(snapshot.WindowHandle, out var rectangle)) return null;
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0) return null;
        var image = new Bitmap(width, height);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.CopyFromScreen(rectangle.Left, rectangle.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return image;
        }
        catch { image.Dispose(); return null; }
    }
    public static uint GetWindowDpi(ProcessSnapshot? snapshot) => snapshot is null || snapshot.WindowHandle == IntPtr.Zero ? 0 : NativeMethods.GetWindowDpi(snapshot.WindowHandle);
    public static bool TryGetScreenPoint(ProcessSnapshot snapshot, int relativeX, int relativeY, out Point point)
    {
        point = default;
        if (snapshot.WindowHandle == IntPtr.Zero || !NativeMethods.TryGetWindowRectangle(snapshot.WindowHandle, out var rectangle)) return false;
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0 || relativeX < 0 || relativeY < 0 || relativeX >= width || relativeY >= height) return false;
        point = new Point(rectangle.Left + relativeX, rectangle.Top + relativeY);
        return true;
    }}

internal static class NativeMethods
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation { public IntPtr Reserved1; public IntPtr PebBaseAddress; public IntPtr Reserved2a; public IntPtr Reserved2b; public IntPtr UniqueProcessId; public IntPtr InheritedFromUniqueProcessId; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder executablePath, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, char[] className, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rectangle);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool IsWindowVisibleNative(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")] private static extern uint GetDpiForWindowNative(IntPtr hWnd);
    public static int? TryGetParentProcessId(IntPtr handle)
    {
        var info = new ProcessBasicInformation();
        return NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0 ? info.InheritedFromUniqueProcessId.ToInt32() : null;
    }
    public static string? TryGetExecutablePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(32_768);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
    public static string GetWindowClass(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return string.Empty;
        var buffer = new char[256];
        var count = GetClassNameW(handle, buffer, buffer.Length);
        return count > 0 ? new string(buffer, 0, count) : string.Empty;
    }
    public static bool IsWindowVisible(IntPtr handle) => handle != IntPtr.Zero && IsWindowVisibleNative(handle);
    public static uint GetWindowDpi(IntPtr handle) { try { return GetDpiForWindowNative(handle); } catch (EntryPointNotFoundException) { return 0; } }
    public static bool TryGetWindowRectangle(IntPtr handle, out Rect rectangle) => GetWindowRect(handle, out rectangle);
}
