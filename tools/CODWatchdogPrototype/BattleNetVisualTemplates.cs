using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;

namespace CODWatchdogPrototype;

public enum BattleNetVisualState { Play, PlayingNow, Update, Updating, Restart }
public enum VisualDetectionStatus { WindowUnavailable, TemplateMissing, CaptureFailed, Match, NoMatch }
public sealed record VisualTemplateMetadata(string TemplateType, int Width, int Height, int SourceWindowWidth, int SourceWindowHeight, uint WindowDpi, DateTimeOffset CreatedAtUtc);
public sealed record TemplateMatchResult(double Confidence, int X, int Y, int Width, int Height)
{
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
}
public sealed record VisualDetectionResult(VisualDetectionStatus Status, TemplateMatchResult? Match, double BestConfidence);

public static class BattleNetTemplateStorage
{
    public static string LocalDirectory => Path.Combine(FindProjectRoot() ?? AppContext.BaseDirectory, "tools", "CODWatchdogPrototype", "templates", "local");
    public static string GetImagePath(BattleNetVisualState state) => Path.Combine(LocalDirectory, $"bnet-{ToFileName(state)}.png");
    public static string GetMetadataPath(BattleNetVisualState state) => Path.Combine(LocalDirectory, $"bnet-{ToFileName(state)}.json");
    public static bool Exists(BattleNetVisualState state) => File.Exists(GetImagePath(state));
    private static string ToFileName(BattleNetVisualState state) => state switch { BattleNetVisualState.Play => "play", BattleNetVisualState.PlayingNow => "playing-now", BattleNetVisualState.Update => "update", BattleNetVisualState.Updating => "updating", BattleNetVisualState.Restart => "restart", _ => throw new ArgumentOutOfRangeException(nameof(state)) };
    private static string? FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
        return null;
    }
}

public static class BattleNetTemplateCreator
{
    public static bool IsCropSelectionAccepted(Bitmap? crop) => crop is not null;
    public static bool CreatePlayTemplate(ProcessSnapshot? primaryBattleNetWindow, out string message)
    {
        if (primaryBattleNetWindow is null || primaryBattleNetWindow.WindowHandle == IntPtr.Zero || !primaryBattleNetWindow.WindowVisible) { message = "BNET_PRIMARY_WINDOW_UNAVAILABLE"; return false; }
        using var capture = WindowCapture.CaptureBitmap(primaryBattleNetWindow);
        if (capture is null) { message = "BNET_TEMPLATE_CAPTURE_FAILED"; return false; }
        using var crop = TemplateCropSelectionForm.Select(capture);
        if (!IsCropSelectionAccepted(crop) || crop is null) { message = "BNET_TEMPLATE_CREATION_CANCELLED"; return false; }
        Directory.CreateDirectory(BattleNetTemplateStorage.LocalDirectory);
        var imagePath = BattleNetTemplateStorage.GetImagePath(BattleNetVisualState.Play);
        if (File.Exists(imagePath))
        {
            var overwrite = System.Windows.Forms.MessageBox.Show("A local Play template already exists. Replace it?", "CODMunity Watchdog", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning);
            if (overwrite != System.Windows.Forms.DialogResult.Yes) { message = "BNET_TEMPLATE_CREATION_CANCELLED"; return false; }
        }
        crop.Save(imagePath, ImageFormat.Png);
        var metadata = new VisualTemplateMetadata("play", crop.Width, crop.Height, capture.Width, capture.Height, WindowCapture.GetWindowDpi(primaryBattleNetWindow), DateTimeOffset.UtcNow);
        File.WriteAllText(BattleNetTemplateStorage.GetMetadataPath(BattleNetVisualState.Play), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        message = $"BNET_TEMPLATE_SAVED state=Play width={crop.Width} height={crop.Height}";
        return true;
    }
}

public sealed class BattleNetVisualDetector
{
    public const double DefaultThreshold = 0.92;
    private readonly double threshold;
    public BattleNetVisualDetector(double threshold = DefaultThreshold) => this.threshold = threshold;
    public VisualDetectionResult DetectPlay(ProcessSnapshot? primaryBattleNetWindow)
    {
        if (primaryBattleNetWindow is null || primaryBattleNetWindow.WindowHandle == IntPtr.Zero || !primaryBattleNetWindow.WindowVisible) return new(VisualDetectionStatus.WindowUnavailable, null, 0);
        var templatePath = BattleNetTemplateStorage.GetImagePath(BattleNetVisualState.Play);
        if (!File.Exists(templatePath)) return new(VisualDetectionStatus.TemplateMissing, null, 0);
        using var capture = WindowCapture.CaptureBitmap(primaryBattleNetWindow);
        if (capture is null) return new(VisualDetectionStatus.CaptureFailed, null, 0);
        using var template = new Bitmap(templatePath);
        var match = TemplateMatcher.FindBestMatch(capture, template);
        return match.Confidence >= threshold ? new(VisualDetectionStatus.Match, match, match.Confidence) : new(VisualDetectionStatus.NoMatch, null, match.Confidence);
    }
}

public static class TemplateMatcher
{
    public static TemplateMatchResult FindBestMatch(Bitmap target, Bitmap template)
    {
        if (template.Width > target.Width || template.Height > target.Height) return new(0, 0, 0, template.Width, template.Height);
        var targetPixels = ToGrayscale(target);
        var templatePixels = ToGrayscale(template);
        var samples = BuildSampleOffsets(template.Width, template.Height, 16);
        var best = new TemplateMatchResult(-1, 0, 0, template.Width, template.Height);
        const int coarseStep = 4;
        for (var y = 0; y <= target.Height - template.Height; y += coarseStep)
            for (var x = 0; x <= target.Width - template.Width; x += coarseStep)
                best = Better(best, Score(targetPixels, target.Width, templatePixels, template.Width, x, y, samples), x, y, template.Width, template.Height);
        var startX = Math.Max(0, best.X - coarseStep);
        var endX = Math.Min(target.Width - template.Width, best.X + coarseStep);
        var startY = Math.Max(0, best.Y - coarseStep);
        var endY = Math.Min(target.Height - template.Height, best.Y + coarseStep);
        for (var y = startY; y <= endY; y++)
            for (var x = startX; x <= endX; x++)
                best = Better(best, Score(targetPixels, target.Width, templatePixels, template.Width, x, y, samples), x, y, template.Width, template.Height);
        return best with { Confidence = Math.Max(0, best.Confidence) };
    }
    private static TemplateMatchResult Better(TemplateMatchResult current, double score, int x, int y, int width, int height) => score > current.Confidence ? new(score, x, y, width, height) : current;
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
            var bytes = new byte[bitmap.Width * bitmap.Height];
            var data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var raw = new byte[Math.Abs(data.Stride) * copy.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                for (var y = 0; y < copy.Height; y++)
                    for (var x = 0; x < copy.Width; x++)
                    {
                        var index = y * Math.Abs(data.Stride) + x * 4;
                        bytes[y * copy.Width + x] = (byte)((raw[index] * 114 + raw[index + 1] * 587 + raw[index + 2] * 299) / 1000);
                    }
            }
            finally { copy.UnlockBits(data); }
            return bytes;
        }
        finally { if (!ReferenceEquals(copy, bitmap)) copy.Dispose(); }
    }
}

internal sealed class TemplateCropSelectionForm : System.Windows.Forms.Form
{
    private readonly Bitmap source;
    private readonly Bitmap display;
    private readonly System.Windows.Forms.PictureBox picture;
    private readonly System.Windows.Forms.Label dimensions;
    private Point start;
    private Rectangle selection;
    private bool selecting;
    private TemplateCropSelectionForm(Bitmap source)
    {
        this.source = source;
        var working = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var scale = Math.Min(1d, Math.Min((working.Width - 120d) / source.Width, (working.Height - 180d) / source.Height));
        display = scale < 1 ? new Bitmap(source, new Size((int)(source.Width * scale), (int)(source.Height * scale))) : new Bitmap(source);
        Text = "Select Battle.net Play button"; KeyPreview = true; StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen; ClientSize = new Size(display.Width, display.Height + 44); FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        picture = new System.Windows.Forms.PictureBox { Image = display, Dock = System.Windows.Forms.DockStyle.Top, Height = display.Height, Width = display.Width, SizeMode = System.Windows.Forms.PictureBoxSizeMode.Normal };
        dimensions = new System.Windows.Forms.Label { Dock = System.Windows.Forms.DockStyle.Bottom, Height = 44, Text = "Drag around Play. Enter saves; Esc cancels.", TextAlign = ContentAlignment.MiddleCenter };
        Controls.Add(picture); Controls.Add(dimensions);
        picture.MouseDown += (_, eventArgs) => { selecting = true; start = eventArgs.Location; selection = Rectangle.Empty; picture.Invalidate(); };
        picture.MouseMove += (_, eventArgs) => { if (!selecting) return; selection = Normalize(start, eventArgs.Location); UpdateDimensions(); picture.Invalidate(); };
        picture.MouseUp += (_, eventArgs) => { selecting = false; selection = Normalize(start, eventArgs.Location); UpdateDimensions(); picture.Invalidate(); };
        picture.Paint += (_, eventArgs) => { if (!selection.IsEmpty) using (var pen = new Pen(Color.Lime, 2)) eventArgs.Graphics.DrawRectangle(pen, selection); };
        KeyDown += (_, eventArgs) => { if (eventArgs.KeyCode == System.Windows.Forms.Keys.Escape) { DialogResult = System.Windows.Forms.DialogResult.Cancel; Close(); } if (eventArgs.KeyCode == System.Windows.Forms.Keys.Enter && !selection.IsEmpty) { DialogResult = System.Windows.Forms.DialogResult.OK; Close(); } };
    }
    public static Bitmap? Select(Bitmap source)
    {
        using var form = new TemplateCropSelectionForm(source);
        return form.ShowDialog() == System.Windows.Forms.DialogResult.OK ? form.CreateCrop() : null;
    }
    private Bitmap CreateCrop()
    {
        var factorX = source.Width / (double)display.Width;
        var factorY = source.Height / (double)display.Height;
        var requested = new Rectangle((int)Math.Floor(selection.X * factorX), (int)Math.Floor(selection.Y * factorY), Math.Max(1, (int)Math.Ceiling(selection.Width * factorX)), Math.Max(1, (int)Math.Ceiling(selection.Height * factorY)));
        var crop = Rectangle.Intersect(requested, new Rectangle(0, 0, source.Width, source.Height));
        return source.Clone(crop, PixelFormat.Format32bppArgb);
    }
    private void UpdateDimensions() => dimensions.Text = selection.IsEmpty ? "Drag around Play. Enter saves; Esc cancels." : $"{selection.Width} x {selection.Height} display pixels - Enter saves; Esc cancels.";
    private static Rectangle Normalize(Point first, Point second) => Rectangle.FromLTRB(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), Math.Max(first.X, second.X), Math.Max(first.Y, second.Y));
    protected override void Dispose(bool disposing) { if (disposing) display.Dispose(); base.Dispose(disposing); }
}
