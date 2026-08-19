using System.Windows;
using System.Windows.Automation;

namespace CODWatchdogPrototype;

// Future action contracts only. This diagnostic revision provides no implementation that opens the launcher or invokes Play.
public interface IBattleNetLauncher { }
public interface IBattleNetPlayController { }

public static class BattleNetLaunchUriKnowledge
{
    public const string Uri = "battlenet://game/auks";
    public const string Classification = "VALIDATED_LAUNCHER_URI";
}

public sealed record UiAutomationElementInfo(string Name, string AutomationId, string ControlType, bool IsEnabled, bool IsOffscreen, Rect Bounds, IReadOnlyList<string> SupportedPatterns);
public sealed record BattleNetUiInspection(bool PrimaryWindowAvailable, bool TraversalTruncated, IReadOnlyList<UiAutomationElementInfo> RelevantElements, bool PlayCandidate);
public interface IBattleNetUiInspector { BattleNetUiInspection Inspect(ProcessSnapshot? primaryBattleNetWindow); }

public sealed class BattleNetUiAutomationInspector : IBattleNetUiInspector
{
    private const int MaxElementsVisited = 500;
    public BattleNetUiInspection Inspect(ProcessSnapshot? primaryBattleNetWindow)
    {
        if (primaryBattleNetWindow is null || primaryBattleNetWindow.WindowHandle == IntPtr.Zero || !primaryBattleNetWindow.WindowVisible)
            return new(false, false, Array.Empty<UiAutomationElementInfo>(), false);
        try
        {
            var root = AutomationElement.FromHandle(primaryBattleNetWindow.WindowHandle);
            var found = new List<UiAutomationElementInfo>();
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);
            var visited = 0;
            while (queue.Count > 0 && visited < MaxElementsVisited)
            {
                var element = queue.Dequeue();
                visited++;
                var info = TryDescribeRelevantElement(element);
                if (info is not null) found.Add(info);
                try
                {
                    for (var child = TreeWalker.ControlViewWalker.GetFirstChild(element); child is not null; child = TreeWalker.ControlViewWalker.GetNextSibling(child)) queue.Enqueue(child);
                }
                catch (ElementNotAvailableException) { }
            }
            return BattleNetUiInspectionClassifier.Create(true, queue.Count > 0, found);
        }
        catch (ElementNotAvailableException) { return new(false, false, Array.Empty<UiAutomationElementInfo>(), false); }
        catch (InvalidOperationException) { return new(false, false, Array.Empty<UiAutomationElementInfo>(), false); }
    }
    private static UiAutomationElementInfo? TryDescribeRelevantElement(AutomationElement element)
    {
        try
        {
            var name = element.Current.Name ?? string.Empty;
            if (!BattleNetUiInspectionClassifier.IsRelevantName(name)) return null;
            var patterns = new List<string>();
            AddPattern(element, InvokePattern.Pattern, "InvokePattern", patterns);
            AddPattern(element, SelectionItemPattern.Pattern, "SelectionItemPattern", patterns);
            AddPattern(element, TogglePattern.Pattern, "TogglePattern", patterns);
            AddPattern(element, ValuePattern.Pattern, "ValuePattern", patterns);
            AddPattern(element, ExpandCollapsePattern.Pattern, "ExpandCollapsePattern", patterns);
            return new(name, element.Current.AutomationId ?? string.Empty, element.Current.ControlType?.ProgrammaticName ?? "Unknown", element.Current.IsEnabled, element.Current.IsOffscreen, element.Current.BoundingRectangle, patterns);
        }
        catch (ElementNotAvailableException) { return null; }
    }
    private static void AddPattern(AutomationElement element, AutomationPattern pattern, string name, List<string> target)
    {
        try { if (element.TryGetCurrentPattern(pattern, out _)) target.Add(name); }
        catch (ElementNotAvailableException) { }
    }
}

public static class BattleNetUiInspectionClassifier
{
    private static readonly string[] RelevantTerms = ["play", "playing now", "update", "updating", "restart", "install", "scan and repair", "call of duty"];
    public static bool IsRelevantName(string value) => RelevantTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    public static BattleNetUiInspection Create(bool primaryWindowAvailable, bool traversalTruncated, IReadOnlyList<UiAutomationElementInfo> relevantElements)
    {
        var playCandidate = relevantElements.Any(element =>
            element.Name.Equals("Play", StringComparison.OrdinalIgnoreCase) &&
            element.ControlType.Equals(ControlType.Button.ProgrammaticName, StringComparison.OrdinalIgnoreCase) &&
            element.IsEnabled &&
            element.SupportedPatterns.Contains("InvokePattern", StringComparer.Ordinal));
        return new(primaryWindowAvailable, traversalTruncated, relevantElements, playCandidate);
    }
}
