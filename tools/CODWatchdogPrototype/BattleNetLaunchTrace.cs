using System.Text.RegularExpressions;
using System.IO;

namespace CODWatchdogPrototype;

public sealed record TraceFileStamp(string FullPath, string Name, long Length, DateTimeOffset LastWriteUtc);
public sealed record BattleNetTraceSnapshot(IReadOnlyList<ProcessSnapshot> Processes, IReadOnlyDictionary<string, TraceFileStamp> Files, BattleNetSignals Signals, IReadOnlyList<string> ShortcutNames);
public sealed record TraceFileChange(string Name, IReadOnlyList<string> HintCategories, IReadOnlyList<string> CandidateUris);
public sealed record BattleNetTraceDiff(IReadOnlyList<ProcessSnapshot> NewProcesses, IReadOnlyList<TraceFileChange> ChangedFiles);

public static class BattleNetLaunchTrace
{
    private static readonly string[] Extensions = [".log", ".json", ".config", ".ini"];
    public static BattleNetTraceSnapshot CaptureSnapshot()
    {
        var processes = ProcessDiagnostics.ReadRelevantProcesses();
        return new(processes, ReadTraceFiles(), BattleNetDiagnostics.ReadSignals(), FindShortcutNames());
    }
    public static BattleNetTraceDiff Compare(BattleNetTraceSnapshot before, BattleNetTraceSnapshot after)
    {
        var existingPids = before.Processes.Select(process => process.Pid).ToHashSet();
        var newProcesses = after.Processes.Where(process => !existingPids.Contains(process.Pid)).OrderBy(process => process.Pid).ToArray();
        var changed = new List<TraceFileChange>();
        foreach (var current in after.Files.Values)
        {
            if (!before.Files.TryGetValue(current.FullPath, out var previous) || previous.Length != current.Length || previous.LastWriteUtc != current.LastWriteUtc)
            {
                var hints = InspectChangedFile(current.FullPath);
                changed.Add(new(current.Name, hints.Categories, hints.CandidateUris));
            }
        }
        return new(newProcesses, changed.OrderBy(change => change.Name).ToArray());
    }
    private static IReadOnlyDictionary<string, TraceFileStamp> ReadTraceFiles()
    {
        var result = new Dictionary<string, TraceFileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net")
        })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Where(file => Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)).OrderByDescending(file => file.LastWriteTimeUtc).Take(300))
                    result[file.FullName] = new(file.FullName, file.Name, file.Length, file.LastWriteTimeUtc);
            }
            catch { }
        }
        return result;
    }
    private static IReadOnlyList<string> FindShortcutNames()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.lnk", SearchOption.AllDirectories))
                    if (file.Name.Contains("Call of Duty", StringComparison.OrdinalIgnoreCase) || file.Name.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)) names.Add(file.Name);
            }
            catch { }
        }
        return names.OrderBy(name => name).ToArray();
    }
    private static (IReadOnlyList<string> Categories, IReadOnlyList<string> CandidateUris) InspectChangedFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, stream.Length - 128 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var categories = new List<string>();
            foreach (var category in new[] { "launch", "execute", "process", "uri", "product", "application", "install" })
                if (text.Contains(category, StringComparison.OrdinalIgnoreCase)) categories.Add(category);
            var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(text, @"\b(?:battlenet|blizzard)://[^\s\""'<>()]+", RegexOptions.IgnoreCase))
            {
                if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)) continue;
                uris.Add(uri.GetLeftPart(UriPartial.Path));
            }
            return (categories, uris.Take(5).ToArray());
        }
        catch { return (Array.Empty<string>(), Array.Empty<string>()); }
    }
}
