using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CODMunityTop250Collector.Core;

public static class CollectorPaths
{
    public static string? RootOverride { get; set; }
    public static string Root => RootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CODMunity", "Top250Collector");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Settings => Path.Combine(Root, "settings.json");
}

// Unknown properties in legacy settings (including HistoryRetentionDays) are ignored by System.Text.Json.
public sealed record CollectorSettings(bool CollectorEnabled = true, bool StartWithWindows = false, bool KeepCallOfDutyRunning = false)
{
    public CollectorSettings Normalized() => this;
}

public sealed class SettingsStore(AppLogger log)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public CollectorSettings Load()
    {
        Directory.CreateDirectory(CollectorPaths.Root);
        if (!File.Exists(CollectorPaths.Settings)) return new CollectorSettings();
        try { return (JsonSerializer.Deserialize<CollectorSettings>(File.ReadAllText(CollectorPaths.Settings)) ?? new CollectorSettings()).Normalized(); }
        catch (Exception ex)
        {
            var backup = Path.Combine(CollectorPaths.Root, $"settings.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            try { File.Move(CollectorPaths.Settings, backup); } catch { }
            log.Write("SETTINGS_RECOVERED", ex.Message);
            return new CollectorSettings();
        }
    }
    public void Save(CollectorSettings value)
    {
        Directory.CreateDirectory(CollectorPaths.Root);
        var temp = CollectorPaths.Settings + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value.Normalized(), Json));
        File.Move(temp, CollectorPaths.Settings, true);
    }
}

public sealed class AppLogger
{
    private readonly object gate = new();
    public void Write(string eventName, string? detail = null)
    {
        lock (gate)
        {
            Directory.CreateDirectory(CollectorPaths.Logs); RotateIfNeeded();
            File.AppendAllText(Path.Combine(CollectorPaths.Logs, "app.log"), $"{DateTime.UtcNow:o} {eventName}{(string.IsNullOrWhiteSpace(detail) ? "" : " " + detail)}{Environment.NewLine}");
        }
    }
    private static void RotateIfNeeded()
    {
        var path = Path.Combine(CollectorPaths.Logs, "app.log"); if (!File.Exists(path) || new FileInfo(path).Length < 5 * 1024 * 1024) return;
        for (var i = 3; i >= 1; i--) { var from = i == 1 ? path : Path.Combine(CollectorPaths.Logs, $"app.{i - 1}.log"); var to = Path.Combine(CollectorPaths.Logs, $"app.{i}.log"); if (File.Exists(from)) File.Move(from, to, true); }
    }
}

public sealed record DatasetStatus(string Status = "WAITING", int? Players = null, DateTimeOffset? CapturedAt = null, long? DurationMs = null, string? Reason = null, IReadOnlyList<RankedPlayer>? PlayerRows = null, ApiSyncState ApiSync = ApiSyncState.NotConfigured);
public sealed record CollectorSnapshot(bool Enabled, string Game = "Closed", string Platform = "Unknown", string Build = "Unknown", string? Layout = null, DateTimeOffset? LastSuccess = null, DateTimeOffset? NextCollection = null, string CycleStatus = "COD_NOT_RUNNING", string? Error = null, DatasetStatus? Mp = null, DatasetStatus? Br = null, DatasetStatus? Resurgence = null);

public sealed class EngineStatusParser
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public CollectorSnapshot Parse(string json, bool enabled, DateTimeOffset cycleStarted)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var captured = ParseDate(root, "capturedAt"); if (captured is not null && captured < cycleStarted.AddSeconds(-1)) throw new InvalidDataException("Stale engine status.");
        DatasetStatus Dataset(string name)
        {
            if (!root.TryGetProperty("datasets", out var all) || !all.TryGetProperty(name, out var value)) return new();
            IReadOnlyList<RankedPlayer>? players = null;
            if (value.TryGetProperty("players", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                players = list.EnumerateArray()
                    .Where(p => GetInt(p, "position") is not null && GetInt(p, "sr") is not null && GetString(p, "name") is not null)
                    .Select(p => new RankedPlayer(GetInt(p, "position")!.Value, GetInt(p, "sr")!.Value, GetString(p, "name")!)).ToArray();
            }
            return new DatasetStatus(GetString(value, "status") ?? "WAITING", GetInt(value, "rows"), ParseDate(value, "capturedAt"), GetLong(value, "durationMs"), GetString(value, "reason"), players);
        }
        return new CollectorSnapshot(enabled, "Running", GetString(root, "platform") ?? "Unknown", string.Equals(GetString(root, "cycleStatus"), "UNSUPPORTED_BUILD", StringComparison.Ordinal) ? "Unsupported" : "Supported", GetString(root, "layout"), null, null, GetString(root, "cycleStatus") ?? "FAILED", null, Dataset("mp"), Dataset("br"), Dataset("resurgence"));
    }
    public string CopyBuildInfo(string json, string version)
    {
        using var doc = JsonDocument.Parse(json); var r = doc.RootElement;
        var platform = GetString(r, "platform") ?? "unknown"; var hash = GetString(r, "codSha256"); var runtime = GetString(r, "runtimeBuildId");
        return $"platform={platform}{(string.IsNullOrWhiteSpace(hash) ? "" : Environment.NewLine + "codSha256=" + hash)}{(string.IsNullOrWhiteSpace(runtime) ? "" : Environment.NewLine + "runtimeBuildId=" + runtime)}{Environment.NewLine}collectorVersion={version}";
    }
    private static string? GetString(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
    private static long? GetLong(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;
    private static DateTimeOffset? ParseDate(JsonElement e, string name) => e.TryGetProperty(name, out var v) && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;
}

public class EngineRunner(AppLogger log)
{
    public virtual async Task<(int ExitCode, string StdOut, string StdErr)> RunOnceAsync(string engineDirectory, VerifiedBuildIdentity? identity, CancellationToken token)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = Path.Combine(engineDirectory, "top250_orchestrator.ps1");
        if (!File.Exists(exe) || !File.Exists(script)) throw new FileNotFoundException("PowerShell engine files are unavailable.");
        // The app contract is one machine-readable JSON object on stdout; no standings/status files are read.
        var psi = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The PowerShell engine emits machine-readable JSON. Never decode its redirected pipe with an OEM code page.
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Once", "-InitialDelaySeconds", "0", "-Quiet", "-EmitJson" }) psi.ArgumentList.Add(argument);
        if (identity is not null)
        {
            psi.ArgumentList.Add("-VerifiedCodPid"); psi.ArgumentList.Add(identity.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-VerifiedCodPath"); psi.ArgumentList.Add(identity.Path);
            psi.ArgumentList.Add("-VerifiedPlatform"); psi.ArgumentList.Add(identity.Platform);
            psi.ArgumentList.Add("-VerifiedBuildSha256"); psi.ArgumentList.Add(identity.Sha256);
            if (!string.IsNullOrWhiteSpace(identity.Layout)) { psi.ArgumentList.Add("-VerifiedLayout"); psi.ArgumentList.Add(identity.Layout); }
        }
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell engine did not start.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token);
        return (process.ExitCode, await output, await error);
    }
}

public sealed record CodProcessDescriptor(int Pid, string Path, string Platform, long FileSize, DateTime LastWriteTimeUtc);
public sealed record VerifiedBuildIdentity(int Pid, string Path, string Platform, string Sha256, long FileSize, DateTime LastWriteTimeUtc, string? Layout = null);

public interface IBuildHasher { Task<string> ComputeSha256Async(string path, CancellationToken token); }

public sealed class FileBuildHasher : IBuildHasher
{
    public async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}

public sealed class BuildIdentityCache(IBuildHasher hasher, AppLogger log)
{
    private VerifiedBuildIdentity? cached;

    public async Task<VerifiedBuildIdentity?> GetOrCreateAsync(CodProcessDescriptor? process, CancellationToken token)
    {
        if (process is null || process.Platform is not ("battlenet" or "steam")) return null;
        if (Matches(cached, process))
        {
            log.Write("BUILD_IDENTITY_CACHE_HIT", $"pid={process.Pid} platform={process.Platform}");
            return cached;
        }
        if (cached is not null) Invalidate("pid-path-platform-or-file-changed");
        log.Write("BUILD_IDENTITY_CACHE_MISS", $"pid={process.Pid} platform={process.Platform}");
        var started = Stopwatch.GetTimestamp(); log.Write("BUILD_HASH_START", $"pid={process.Pid}");
        var sha256 = await hasher.ComputeSha256Async(process.Path, token);
        if (!IsSha256(sha256)) throw new InvalidDataException("Build hash format is invalid.");
        log.Write("BUILD_HASH_OK", $"pid={process.Pid} durationMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
        cached = new VerifiedBuildIdentity(process.Pid, process.Path, process.Platform, sha256, process.FileSize, process.LastWriteTimeUtc);
        return cached;
    }

    public void RememberValidatedLayout(VerifiedBuildIdentity identity, string? layout)
    {
        var current = cached;
        if (string.IsNullOrWhiteSpace(layout) || !Matches(current, identity) || current is null) return;
        cached = current with { Layout = layout };
    }

    public void Invalidate(string reason)
    {
        if (cached is null) return;
        log.Write("BUILD_IDENTITY_CACHE_INVALIDATED", $"reason={reason}");
        cached = null;
    }

    private static bool Matches(VerifiedBuildIdentity? identity, CodProcessDescriptor process) => identity is not null && identity.Pid == process.Pid && string.Equals(identity.Path, process.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(identity.Platform, process.Platform, StringComparison.Ordinal) && identity.FileSize == process.FileSize && identity.LastWriteTimeUtc == process.LastWriteTimeUtc;
    private static bool Matches(VerifiedBuildIdentity? left, VerifiedBuildIdentity right) => left is not null && left.Pid == right.Pid && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(left.Platform, right.Platform, StringComparison.Ordinal) && string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal) && left.FileSize == right.FileSize && left.LastWriteTimeUtc == right.LastWriteTimeUtc;
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

public class CodProcessMonitor
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder executablePath, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);

    public virtual int? FindSinglePid() { var list = Process.GetProcessesByName("cod"); try { return list.Length == 1 ? list[0].Id : null; } finally { foreach (var p in list) p.Dispose(); } }
    public virtual CodProcessDescriptor? Describe(int pid)
    {
        try
        {
            var path = QueryImagePath(pid);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var normalized = Path.GetFullPath(path); var platform = DetectPlatform(normalized);
            if (platform is not ("battlenet" or "steam")) return null;
            var file = new FileInfo(normalized);
            return new CodProcessDescriptor(pid, normalized, platform, file.Length, file.LastWriteTimeUtc);
        }
        catch { return null; }
    }
    private static string? QueryImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32768); var length = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
    private static string DetectPlatform(string path)
    {
        if (path.Contains("\\steamapps\\common\\Call of Duty HQ\\", StringComparison.OrdinalIgnoreCase)) return "steam";
        if (path.EndsWith("\\Call of Duty\\_retail_\\cod.exe", StringComparison.OrdinalIgnoreCase)) return "battlenet";
        return "unknown";
    }
}

public sealed class CollectorScheduler
{
    private readonly CodProcessMonitor monitor; private readonly EngineRunner engine; private readonly EngineStatusParser parser; private readonly RankedPlayersApiClient api; private readonly AppLogger log; private readonly string engineDir; private readonly BuildIdentityCache buildIdentity; private readonly SemaphoreSlim cycleLock = new(1, 1); private DateTimeOffset? nextCollectionAtUtc; private volatile bool updatePreparationRequested;
    public event Action<CollectorSnapshot>? Changed; public CollectorSettings Settings { get; private set; } = new(); public CollectorSnapshot Snapshot { get; private set; } = new(true); public bool IsRunningCycle => cycleLock.CurrentCount == 0;
    public CollectorScheduler(CodProcessMonitor monitor, EngineRunner engine, EngineStatusParser parser, RankedPlayersApiClient api, AppLogger log, string engineDir, BuildIdentityCache? buildIdentity = null) { this.monitor = monitor; this.engine = engine; this.parser = parser; this.api = api; this.log = log; this.engineDir = engineDir; this.buildIdentity = buildIdentity ?? new BuildIdentityCache(new FileBuildHasher(), log); }
    public void UpdateSettings(CollectorSettings settings) { Settings = settings.Normalized(); Publish(Snapshot with { Enabled = Settings.CollectorEnabled }); log.Write(Settings.CollectorEnabled ? "COLLECTOR_ENABLED" : "COLLECTOR_DISABLED"); }
    public async Task RunAsync(CancellationToken token)
    {
        int? knownPid = null;
        while (!token.IsCancellationRequested)
        {
            if (!Settings.CollectorEnabled) { nextCollectionAtUtc = null; Publish(Snapshot with { Enabled = false, Game = "Closed", CycleStatus = "DISABLED", NextCollection = null }); await Task.Delay(TimeSpan.FromSeconds(5), token); continue; }
            var pid = monitor.FindSinglePid();
            if (pid is null) { if (knownPid is not null) { log.Write("COD_CLOSED"); buildIdentity.Invalidate("process-closed"); } knownPid = null; nextCollectionAtUtc = null; Publish(Snapshot with { Enabled = true, Game = "Closed", CycleStatus = "COD_NOT_RUNNING", NextCollection = null, Error = null }); await Task.Delay(TimeSpan.FromSeconds(5), token); continue; }
            if (pid != knownPid) { if (knownPid is not null) buildIdentity.Invalidate("pid-changed"); knownPid = pid; log.Write("COD_DETECTED", $"pid={pid}"); Publish(Snapshot with { Enabled = true, Game = "Running", CycleStatus = "WAITING_FOR_GAME", NextCollection = null, Error = null }); await Task.Delay(TimeSpan.FromSeconds(30), token); if (monitor.FindSinglePid() != pid) continue; }
            if (updatePreparationRequested) { await Task.Delay(TimeSpan.FromSeconds(1), token); continue; }
            await RunCycleAsync(pid.Value, token); var until = DateTimeOffset.UtcNow.AddSeconds(300); nextCollectionAtUtc = until; Publish(Snapshot with { NextCollection = until });
            while (DateTimeOffset.UtcNow < until && !token.IsCancellationRequested && monitor.FindSinglePid() == knownPid) await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }
    public async Task SyncNowAsync(CancellationToken token)
    {
        if (!updatePreparationRequested && Settings.CollectorEnabled && monitor.FindSinglePid() is not null)
        {
            await RunCycleAsync(monitor.FindSinglePid()!.Value, token);
            if (nextCollectionAtUtc is { } until && until > DateTimeOffset.UtcNow) Publish(Snapshot with { NextCollection = until });
        }
    }
    private async Task RunCycleAsync(int pid, CancellationToken token)
    {
        if (updatePreparationRequested) return;
        if (!await cycleLock.WaitAsync(0, token)) return; var started = DateTimeOffset.UtcNow; Publish(Snapshot with { Game = "Running", CycleStatus = "RUNNING", NextCollection = null }); log.Write("CYCLE_START");
        try
        {
            var identity = await buildIdentity.GetOrCreateAsync(monitor.Describe(pid), token);
            var result = await engine.RunOnceAsync(engineDir, identity, token);
            if (string.IsNullOrWhiteSpace(result.StdOut)) throw new InvalidDataException("Engine did not return JSON.");
            var previous = Snapshot; var parsed = parser.Parse(result.StdOut, true, started);
            if (identity is not null && parsed.CycleStatus is ("OK" or "PARTIAL_READY")) buildIdentity.RememberValidatedLayout(identity, parsed.Layout);
            var snapshot = parsed with { LastSuccess = parsed.CycleStatus is "OK" or "PARTIAL_READY" ? DateTimeOffset.UtcNow : previous.LastSuccess, Mp = PreserveLastValid(parsed.Mp, previous.Mp), Br = PreserveLastValid(parsed.Br, previous.Br), Resurgence = PreserveLastValid(parsed.Resurgence, previous.Resurgence) };
            // Operational priority: Resurgence first, Multiplayer second, Battle Royale retained for future compatibility.
            snapshot = snapshot with { Resurgence = await SyncDatasetAsync("resurgence", snapshot.Resurgence, token), Mp = await SyncDatasetAsync("mp", snapshot.Mp, token), Br = await SyncDatasetAsync("br", snapshot.Br, token) };
            Publish(snapshot); log.Write(snapshot.CycleStatus is "OK" ? "CYCLE_OK" : snapshot.CycleStatus is "UNSUPPORTED_BUILD" ? "UNSUPPORTED_BUILD" : "CYCLE_PARTIAL", $"exitCode={result.ExitCode}");
        }
        catch (Exception ex) { Publish(Snapshot with { Game = "Running", CycleStatus = "FAILED", Error = "Engine error" }); log.Write("ENGINE_ERROR", ex.Message); }
        finally { cycleLock.Release(); }
    }
    public void RequestUpdatePreparation() => updatePreparationRequested = true;
    public void CancelUpdatePreparation() => updatePreparationRequested = false;
    public bool UpdatePreparationRequested => updatePreparationRequested;
    public async Task WaitForIdleAsync(CancellationToken token)
    {
        while (IsRunningCycle) await Task.Delay(100, token);
    }
    private static DatasetStatus? PreserveLastValid(DatasetStatus? candidate, DatasetStatus? previous) => candidate?.Status == "OK" ? candidate : previous;
    private async Task<DatasetStatus?> SyncDatasetAsync(string mode, DatasetStatus? dataset, CancellationToken token)
    {
        if (dataset?.Status != "OK") return dataset;
        var sync = await api.SyncAsync(mode, dataset.PlayerRows, token);
        return dataset with { ApiSync = sync.State };
    }
    private void Publish(CollectorSnapshot snapshot) { Snapshot = snapshot; Changed?.Invoke(snapshot); }
}

public static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run"; private const string Name = "CODMunityTop250Collector";
    public static void Set(bool enabled, string exePath) { using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true)!; if (enabled) key.SetValue(Name, $"\"{exePath}\""); else key.DeleteValue(Name, false); }
}
