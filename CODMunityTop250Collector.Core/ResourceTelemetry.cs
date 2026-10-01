using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CODMunityTop250Collector.Core;

public enum ResourcePressureLevel { Normal, Warning, Critical }

public sealed record SystemResourceSnapshot(
    long CommitUsedBytes,
    long CommitLimitBytes,
    long PhysicalTotalBytes,
    long PhysicalAvailableBytes,
    long WorkingSetBytes,
    long PrivateBytes,
    long PeakWorkingSetBytes,
    int HandleCount,
    int ThreadCount,
    long ManagedHeapBytes)
{
    public long CommitHeadroomBytes => Math.Max(0, CommitLimitBytes - CommitUsedBytes);
    public double CommitPercent => CommitLimitBytes <= 0 ? 0 : (double)CommitUsedBytes * 100 / CommitLimitBytes;
}

public sealed record ResourceConsumer(string Name, int ProcessId, long PrivateBytes, long WorkingSetBytes);

public interface ISystemResourceProvider
{
    SystemResourceSnapshot Capture();
    IReadOnlyList<ResourceConsumer> GetTopPrivateMemoryConsumers(int count);
}

public static class ResourcePressurePolicy
{
    public const double WarningCommitPercent = 90;
    public const double CriticalCommitPercent = 95;
    public const long CriticalCommitHeadroomBytes = 1024L * 1024 * 1024;

    public static ResourcePressureLevel Evaluate(SystemResourceSnapshot snapshot)
    {
        if (snapshot.CommitLimitBytes <= 0) return ResourcePressureLevel.Normal;
        if (snapshot.CommitPercent >= CriticalCommitPercent || snapshot.CommitHeadroomBytes <= CriticalCommitHeadroomBytes) return ResourcePressureLevel.Critical;
        return snapshot.CommitPercent >= WarningCommitPercent ? ResourcePressureLevel.Warning : ResourcePressureLevel.Normal;
    }
}

public sealed class ResourceTelemetry(ISystemResourceProvider provider, AppLogger log)
{
    private ResourcePressureLevel lastLevel;

    public ResourcePressureLevel LastLevel => lastLevel;

    public SystemResourceSnapshot Sample(string source)
    {
        SystemResourceSnapshot snapshot;
        try { snapshot = provider.Capture(); }
        catch (Exception ex)
        {
            log.Write("RESOURCE_SAMPLE_UNAVAILABLE", $"source={source} type={ex.GetType().Name}");
            return new SystemResourceSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        var level = ResourcePressurePolicy.Evaluate(snapshot);
        log.Write("RESOURCE_SAMPLE", Format(source, snapshot));
        if (level == ResourcePressureLevel.Warning)
            log.Write("RESOURCE_PRESSURE_WARNING", Format(source, snapshot));
        if (level == ResourcePressureLevel.Critical)
        {
            log.Write("RESOURCE_PRESSURE_CRITICAL", Format(source, snapshot));
            if (lastLevel != ResourcePressureLevel.Critical)
            {
                var rank = 1;
                try
                {
                    foreach (var consumer in provider.GetTopPrivateMemoryConsumers(5))
                        log.Write("RESOURCE_PRESSURE_PROCESS", $"rank={rank++} name={consumer.Name} pid={consumer.ProcessId} privateMb={Mb(consumer.PrivateBytes)} workingSetMb={Mb(consumer.WorkingSetBytes)}");
                }
                catch (Exception ex) { log.Write("RESOURCE_PRESSURE_PROCESS_UNAVAILABLE", $"type={ex.GetType().Name}"); }
            }
        }
        else if (lastLevel == ResourcePressureLevel.Critical)
            log.Write("RESOURCE_PRESSURE_RECOVERED", Format(source, snapshot));
        lastLevel = level;
        return snapshot;
    }

    public async Task RunPeriodicAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            if (!token.IsCancellationRequested) Sample("periodic");
        }
    }

    private static string Format(string source, SystemResourceSnapshot sample) => string.Join(' ', new[]
    {
        "source=" + source,
        "workingSetMb=" + Mb(sample.WorkingSetBytes),
        "privateMb=" + Mb(sample.PrivateBytes),
        "peakWorkingSetMb=" + Mb(sample.PeakWorkingSetBytes),
        "handles=" + sample.HandleCount.ToString(CultureInfo.InvariantCulture),
        "threads=" + sample.ThreadCount.ToString(CultureInfo.InvariantCulture),
        "gcHeapMb=" + Mb(sample.ManagedHeapBytes),
        "physicalAvailableMb=" + Mb(sample.PhysicalAvailableBytes),
        "physicalTotalMb=" + Mb(sample.PhysicalTotalBytes),
        "commitUsedMb=" + Mb(sample.CommitUsedBytes),
        "commitLimitMb=" + Mb(sample.CommitLimitBytes),
        "commitHeadroomMb=" + Mb(sample.CommitHeadroomBytes),
        "commitPct=" + sample.CommitPercent.ToString("F1", CultureInfo.InvariantCulture)
    });
    private static string Mb(long bytes) => (Math.Max(0, bytes) / (1024d * 1024d)).ToString("F1", CultureInfo.InvariantCulture);
}

public sealed class WindowsSystemResourceProvider : ISystemResourceProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation performanceInformation, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public SystemResourceSnapshot Capture()
    {
        var performance = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GetPerformanceInfo(ref performance, performance.Size)) throw new InvalidOperationException("GetPerformanceInfo failed.");
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new InvalidOperationException("GlobalMemoryStatusEx failed.");
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var pageSize = checked((long)performance.PageSize);
        long Bytes(nuint pages) => checked((long)pages * pageSize);
        return new SystemResourceSnapshot(
            Bytes(performance.CommitTotal), Bytes(performance.CommitLimit), checked((long)memory.TotalPhys), checked((long)memory.AvailPhys),
            process.WorkingSet64, process.PrivateMemorySize64, process.PeakWorkingSet64, process.HandleCount, process.Threads.Count,
            GC.GetGCMemoryInfo().HeapSizeBytes);
    }

    public IReadOnlyList<ResourceConsumer> GetTopPrivateMemoryConsumers(int count)
    {
        var consumers = new List<ResourceConsumer>();
        foreach (var process in Process.GetProcesses())
        {
            try { consumers.Add(new ResourceConsumer(process.ProcessName, process.Id, process.PrivateMemorySize64, process.WorkingSet64)); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
        return consumers.OrderByDescending(item => item.PrivateBytes).Take(Math.Max(0, count)).ToArray();
    }
}
