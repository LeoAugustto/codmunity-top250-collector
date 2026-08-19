using System.Drawing;

namespace CODWatchdogPrototype;

public readonly record struct WindowScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class PlayClickValidation
{
    public const double ActiveThreshold = 0.95;

    public static bool TryGetScreenPoint(WindowScreenRect rectangle, int relativeX, int relativeY, out Point point)
    {
        point = default;
        if (rectangle.Width <= 0 || rectangle.Height <= 0 || relativeX < 0 || relativeY < 0 || relativeX >= rectangle.Width || relativeY >= rectangle.Height) return false;
        point = new Point(rectangle.Left + relativeX, rectangle.Top + relativeY);
        return true;
    }

    public static bool IsPointInside(WindowScreenRect rectangle, Point point) =>
        point.X >= rectangle.Left && point.X < rectangle.Right && point.Y >= rectangle.Top && point.Y < rectangle.Bottom;

    public static bool IsBattleNetTarget(IntPtr target, IntPtr root, IntPtr primaryWindow, int? targetPid, int primaryPid) =>
        target != IntPtr.Zero && (root == primaryWindow || targetPid == primaryPid);

    // A rejected foreground request is advisory: target validation and SendInput determine delivery safety.
    public static bool ForegroundRequestIsAdvisory(bool foregroundRequested) => !foregroundRequested;
    public static bool IsSendInputAccepted(uint requested, uint accepted) => requested == accepted;
    public static bool IsRestoreFailureAdvisory(bool clickWasSent, bool cursorRestored) => clickWasSent && !cursorRestored;
    public static bool IsConfidenceEligible(VisualDetectionResult? visual) => visual?.Status == VisualDetectionStatus.Match && visual.Match is { } match && match.Confidence >= ActiveThreshold;
}