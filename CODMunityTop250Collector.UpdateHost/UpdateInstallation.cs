using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CODMunityTop250Collector.UpdateHost;

public sealed record UpdateHostArguments(int ParentPid, string MsiPath, string ExpectedSha256, string AppPath)
{
    private static readonly Regex Sha256 = new("^[0-9A-F]{64}$", RegexOptions.CultureInvariant);
    public static bool TryParse(string[] args, out UpdateHostArguments? result)
    {
        result = null;
        if (args.Length != 8) return false;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[i], args[i + 1])) return false;
        }
        if (!values.TryGetValue("--parent-pid", out var pidText) || !int.TryParse(pidText, out var pid) || pid <= 0 || !values.TryGetValue("--msi", out var msi) || !values.TryGetValue("--sha256", out var hash) || !values.TryGetValue("--app", out var app)) return false;
        try { msi = Path.GetFullPath(msi); app = Path.GetFullPath(app); } catch { return false; }
        if (!File.Exists(msi) || !string.Equals(Path.GetExtension(msi), ".msi", StringComparison.OrdinalIgnoreCase) || !File.Exists(app) || !string.Equals(Path.GetFileName(app), "CODMunityTop250Collector.exe", StringComparison.OrdinalIgnoreCase) || !Sha256.IsMatch(hash)) return false;
        result = new(pid, msi, hash, app); return true;
    }
}

public sealed record UpdateInstallationResult(int ExitCode, bool AppRestarted, bool RebootRequired, string? Error = null);

public interface IWindowsInstallerApi
{
    uint SetInternalUI(uint uiLevel);
    uint EnableLog(string logPath);
    uint InstallProduct(string validatedMsiPath, string properties);
}

public sealed class WindowsInstallerApi : IWindowsInstallerApi
{
    public const uint InstallLogModeError = 0x00000002;
    public const uint InstallLogModeWarning = 0x00000004;
    public const uint InstallLogModeInfo = 0x00000010;
    public const uint InstallLogModeActionStart = 0x00000100;
    public const uint InstallLogModeActionData = 0x00000200;
    public const uint InstallLogModeCommonData = 0x00000800;
    public const uint InstallLogModeVerbose = 0x00001000;
    public const uint InstallLogModeLogOnlyOnError = 0x00004000;
    public const uint InstallLogMode = InstallLogModeError | InstallLogModeWarning | InstallLogModeInfo |
                                       InstallLogModeActionStart | InstallLogModeActionData |
                                       InstallLogModeCommonData | InstallLogModeVerbose;
    public const uint InstallLogAttributesFlushEachLine = 0x00000002;

    [DllImport("msi.dll", EntryPoint = "MsiSetInternalUI", ExactSpelling = true, CallingConvention = CallingConvention.Winapi, SetLastError = false)]
    private static extern uint MsiSetInternalUI(uint uiLevel, IntPtr ownerWindow);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiEnableLogW", CallingConvention = CallingConvention.Winapi, SetLastError = false)]
    private static extern uint MsiEnableLog(uint logMode, [MarshalAs(UnmanagedType.LPWStr)] string logFile, uint logAttributes);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiInstallProductW", CallingConvention = CallingConvention.Winapi, SetLastError = false)]
    private static extern uint MsiInstallProduct([MarshalAs(UnmanagedType.LPWStr)] string packagePath, [MarshalAs(UnmanagedType.LPWStr)] string commandLine);

    public uint SetInternalUI(uint uiLevel) => MsiSetInternalUI(uiLevel, IntPtr.Zero);
    public uint EnableLog(string logPath) => MsiEnableLog(InstallLogMode, logPath, InstallLogAttributesFlushEachLine);
    public uint InstallProduct(string validatedMsiPath, string properties) => MsiInstallProduct(validatedMsiPath, properties);
}

public static class UpdateInstallation
{
    public const uint InstallUiLevelNone = 2;
    public const string RebootSuppressionProperties = "REBOOT=ReallySuppress";
    public static bool IsSuccessfulMsiExitCode(int exitCode) => exitCode is 0 or 3010;
    public static async Task<int> InstallValidatedMsiAsync(string validatedMsiPath, IWindowsInstallerApi installerApi, Action<string, string?> log, string? logsDirectory = null)
    {
        return await Task.Run(() =>
        {
            var logs = logsDirectory ?? GetLogsDirectory();
            Directory.CreateDirectory(logs);
            var msiLogPath = Path.Combine(logs, "update-msi.log");
            var logResult = installerApi.EnableLog(msiLogPath);
            log("UPDATEHOST_MSI_LOG_ENABLE", $"result={logResult} path=logs/update-msi.log mode=0x{WindowsInstallerApi.InstallLogMode:X8} attributes=0x{WindowsInstallerApi.InstallLogAttributesFlushEachLine:X8}");
            var previousUiLevel = installerApi.SetInternalUI(InstallUiLevelNone);
            log("UPDATEHOST_MSI_UI_SET", $"requested={InstallUiLevelNone} previous={previousUiLevel}");
            log("UPDATEHOST_MSI_INSTALL_START", null);
            var result = installerApi.InstallProduct(validatedMsiPath, RebootSuppressionProperties);
            log("UPDATEHOST_MSI_INSTALL_EXIT", $"code={result}");
            return unchecked((int)result);
        });
    }

    public static async Task<UpdateInstallationResult> RunAsync(UpdateHostArguments options, Action<string> status, IWindowsInstallerApi? installerApi = null)
    {
        try
        {
            Log("UPDATE_HOST_START");
            status("Waiting for CODMunity to close...");
            try
            {
                using var parent = Process.GetProcessById(options.ParentPid);
                if (!parent.WaitForExit(60000)) return Fail(3, "parent-timeout", "CODMunity did not close in time.");
            }
            catch (ArgumentException) { }

            status("Verifying update...");
            var actual = ComputeSha256(options.MsiPath); var expected = Convert.FromHexString(options.ExpectedSha256);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return Fail(4, "hash-mismatch", "The downloaded update could not be verified.");
            }
            finally { CryptographicOperations.ZeroMemory(actual); CryptographicOperations.ZeroMemory(expected); }

            status("Installing update...");
            var installExitCode = await InstallValidatedMsiAsync(options.MsiPath, installerApi ?? new WindowsInstallerApi(), (evt, detail) => Log(evt, detail));
            if (!IsSuccessfulMsiExitCode(installExitCode)) return Fail(installExitCode, $"exitCode={installExitCode}", "The update could not be installed.");

            Log("UPDATE_INSTALL_OK", $"exitCode={installExitCode}{(installExitCode == 3010 ? " rebootRequired=true" : string.Empty)}");
            status("Starting CODMunity...");
            var restarted = Process.Start(new ProcessStartInfo(options.AppPath) { UseShellExecute = true }) is not null;
            if (!restarted) return Fail(5, "restart-failed", "The update was installed, but CODMunity could not be restarted.");
            return new(installExitCode, true, installExitCode == 3010);
        }
        catch (Exception ex) { return Fail(1, ex.GetType().Name, "The update could not be completed."); }
    }

    private static UpdateInstallationResult Fail(int exitCode, string detail, string message)
    {
        Log("UPDATE_INSTALL_FAILED", detail);
        return new(exitCode, false, false, message);
    }
    private static byte[] ComputeSha256(string path) { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
    private static string GetLogsDirectory() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CODMunity", "Top250Collector", "logs");
    private static void Log(string evt, string? detail = null)
    {
        var logs = GetLogsDirectory();
        Directory.CreateDirectory(logs); File.AppendAllText(Path.Combine(logs, "update-host.log"), $"{DateTimeOffset.UtcNow:O} {evt}{(detail is null ? string.Empty : " " + detail)}{Environment.NewLine}");
    }
}
