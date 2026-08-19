using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using CODMunityTop250Collector.Core;
using MediaBrush = System.Windows.Media.Brush;

namespace CODMunityTop250Collector.App;
public partial class MainWindow : Window
{
    private readonly App app;
    private bool updating;
    private CollectorSnapshot currentSnapshot = new(true);
    private readonly DispatcherTimer countdownTimer;

    public MainWindow(App app)
    {
        InitializeComponent();
        this.app = app;
        countdownTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        countdownTimer.Tick += (_, _) => RefreshNextSync();
        countdownTimer.Start();
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        SetSettings(new CollectorSettings());
        Update(new CollectorSnapshot(true));
    }

    public void SetSettings(CollectorSettings settings)
    {
        updating = true;
        Enabled.IsChecked = settings.CollectorEnabled;
        Startup.IsChecked = settings.StartWithWindows;
        KeepCodRunning.IsChecked = settings.KeepCallOfDutyRunning;
        updating = false;
    }
    public void SetVersion(string version) { VersionText.Text = "v" + version; SettingsVersionText.Text = version; SettingsUpdateVersionText.Text = "v" + version; }
    public void SetWatchdogStatus(CodWatchdogSnapshot status)
    {
        WatchdogStatusText.Text = status.Status switch
        {
            CodWatchdogStatus.Disabled => "Watchdog disabled",
            CodWatchdogStatus.Healthy => "COD health monitoring active",
            CodWatchdogStatus.NoDataWarning => "Collection is delayed",
            CodWatchdogStatus.NoDataConfirmed or CodWatchdogStatus.CheckingGameState => "Checking Call of Duty health",
            CodWatchdogStatus.RecoveryCooldown => "Recovery cooldown active",
            CodWatchdogStatus.Unavailable => "Available for Battle.net only",
            _ => status.Detail
        };
    }
    public void SetUpdateStatus(string status, DateTimeOffset? checkedAt)
    {
        UpdateDefaultStatusPanel.Visibility = Visibility.Collapsed;
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = status;
        if (checkedAt is not null) LastUpdateCheckText.Text = "Last checked: " + checkedAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    public Task<bool> ConfirmManualUpdateAsync(string version)
    {
        var dialog = new Window
        {
            Owner = this, Title = "Update available", Width = 390, Height = 190, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (MediaBrush)FindResource("Bg"), Foreground = (MediaBrush)FindResource("TextPrimary"), ShowInTaskbar = false
        };
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "Update available", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var detail = new TextBlock { Text = $"v{version} is available.\nCurrent version: v{AppVersion.Display}", Foreground = (MediaBrush)FindResource("TextMuted"), Margin = new Thickness(0, 12, 0, 12) }; Grid.SetRow(detail, 1); root.Children.Add(detail);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var later = new System.Windows.Controls.Button { Content = "Later", Style = (Style)FindResource("GhostButton"), MinWidth = 82 };
        var install = new System.Windows.Controls.Button { Content = "Install update", Style = (Style)FindResource("PrimaryButton"), MinWidth = 116, Margin = new Thickness(8, 0, 0, 0) };
        later.Click += (_, _) => dialog.DialogResult = false; install.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(later); buttons.Children.Add(install); Grid.SetRow(buttons, 2); root.Children.Add(buttons); dialog.Content = root;
        return Task.FromResult(dialog.ShowDialog() == true);
    }

    public void Update(CollectorSnapshot snapshot)
    {
        currentSnapshot = snapshot;
        var isSyncing = snapshot.CycleStatus == "RUNNING";
        var unsupported = snapshot.Build == "Unsupported" || snapshot.CycleStatus is "UNSUPPORTED_BUILD" or "BUILD_ID_UNAVAILABLE";
        var failed = snapshot.CycleStatus is "FAILED" or "PARTIAL_FAILURE" || !string.IsNullOrWhiteSpace(snapshot.Error);
        var waitingForData = snapshot.CycleStatus is "WAITING_FOR_CACHE" or "WAITING_FOR_GAME";
        var gameRunning = snapshot.Game == "Running";

        PlatformPill.Visibility = gameRunning && !unsupported ? Visibility.Visible : Visibility.Collapsed;
        PlatformPillText.Text = FriendlyPlatform(snapshot.Platform);

        if (!snapshot.Enabled)
            SetPrimaryStatus("Collector disabled", "Turn it back on in Settings to resume automatic collection.", (MediaBrush)FindResource("Gray"));
        else if (unsupported)
            SetPrimaryStatus("Unsupported game build", "Collector update required for this game build.", (MediaBrush)FindResource("Yellow"));
        else if (failed)
            SetPrimaryStatus("Collection failed", "The collector will try again on the next scheduled sync.", (MediaBrush)FindResource("Red"));
        else if (!gameRunning)
            SetPrimaryStatus("Waiting for Call of Duty", "The collector starts automatically when the game opens.", (MediaBrush)FindResource("Gray"));
        else if (isSyncing)
            SetPrimaryStatus("Syncing now...", "Collecting the three Top 250 leaderboards.", (MediaBrush)FindResource("Yellow"));
        else if (waitingForData)
            SetPrimaryStatus("Waiting for leaderboard data", "Call of Duty is running. Collection starts automatically.", (MediaBrush)FindResource("Yellow"));
        else
            SetPrimaryStatus("Collector active", "Call of Duty is running normally.", (MediaBrush)FindResource("Green"));

        var hasLastSync = snapshot.LastSuccess is not null;
        TimingInlineText.Visibility = hasLastSync ? Visibility.Visible : Visibility.Collapsed;
        TimingSeparator.Visibility = hasLastSync ? Visibility.Visible : Visibility.Collapsed;
        if (hasLastSync) TimingInlineText.Text = $"Last sync {FormatTime(snapshot.LastSuccess!.Value)}";
        RefreshNextSync();
        SyncButton.Content = isSyncing ? "Syncing..." : "Sync now";
        SyncButton.IsEnabled = snapshot.Enabled && gameRunning && !isSyncing;
        CopyBuildButton.Visibility = unsupported ? Visibility.Visible : Visibility.Collapsed;

        SetDataset(MpPlayers, MpStatus, MpApiStatus, MpDot, snapshot.Mp, snapshot.Enabled, gameRunning);
        SetDataset(BrPlayers, BrStatus, BrApiStatus, BrDot, snapshot.Br, snapshot.Enabled, gameRunning);
        SetDataset(ResPlayers, ResStatus, ResApiStatus, ResDot, snapshot.Resurgence, snapshot.Enabled, gameRunning);
    }

    private void SetPrimaryStatus(string headline, string detail, MediaBrush color)
    {
        StatusHeadline.Text = headline;
        StatusDetail.Text = detail;
        StatusDot.Fill = color;
    }

    private void SetDataset(TextBlock players, TextBlock status, TextBlock apiStatus, Ellipse dot, DatasetStatus? dataset, bool enabled, bool gameRunning)
    {
        if (!enabled) { SetDataset(players, status, apiStatus, dot, "—", "Disabled", "", "Gray"); return; }
        if (dataset?.Status == "OK")
        {
            var saved = gameRunning ? FormatUpdated(dataset.CapturedAt) : FormatSaved(dataset.CapturedAt);
            var (api, color) = dataset.ApiSync switch
            {
                ApiSyncState.Ok => ($"Synced to CODMunity • {DateTimeOffset.Now.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}", "Green"),
                ApiSyncState.Failed => ("CODMunity sync failed", "Red"),
                ApiSyncState.NotConfigured => ("API not configured", "Gray"),
                _ => ("Waiting to sync", "Yellow")
            };
            SetDataset(players, status, apiStatus, dot, (dataset.Players ?? 0).ToString(), saved, api, color); return;
        }
        if (!gameRunning) { SetDataset(players, status, apiStatus, dot, "—", "Waiting", "", "Gray"); return; }
        if (dataset?.Status == "FAILED") { SetDataset(players, status, apiStatus, dot, "—", "Collection failed", "", "Red"); return; }
        SetDataset(players, status, apiStatus, dot, "—", dataset?.Status == "NOT_READY" ? "Waiting for data" : "Waiting", "", "Yellow");
    }

    private void SetDataset(TextBlock players, TextBlock status, TextBlock apiStatus, Ellipse dot, string count, string message, string apiMessage, string colorResource)
    {
        var brush = (MediaBrush)FindResource(colorResource);
        players.Text = count;
        status.Text = message;
        status.Foreground = (MediaBrush)FindResource("TextMuted");
        apiStatus.Text = apiMessage;
        apiStatus.Foreground = brush;
        apiStatus.Visibility = string.IsNullOrEmpty(apiMessage) ? Visibility.Collapsed : Visibility.Visible;
        dot.Visibility = string.IsNullOrEmpty(apiMessage) ? Visibility.Collapsed : Visibility.Visible;
        dot.Fill = brush;
    }

    private static string FormatUpdated(DateTimeOffset? capturedAt)
    {
        if (capturedAt is null) return "Updated";
        return "Updated " + FormatLocalDateTime(capturedAt.Value, includeSeconds: false);
    }
    private static string FormatSaved(DateTimeOffset? capturedAt) => capturedAt is null ? "Last saved" : "Last saved " + FormatLocalDateTime(capturedAt.Value, includeSeconds: false);

    private static string FormatTime(DateTimeOffset time) => FormatLocalDateTime(time, includeSeconds: true);
    private static string FormatLocalDateTime(DateTimeOffset timestamp, bool includeSeconds) =>
        timestamp.ToLocalTime().ToString(includeSeconds ? "G" : "g", CultureInfo.CurrentCulture);
    private static string FriendlyPlatform(string platform) => platform.ToLowerInvariant() switch { "battlenet" => "Battle.net", "steam" => "Steam", "xbox" => "Xbox", _ => "Detecting..." };
    private void RefreshNextSync()
    {
        var snapshot = currentSnapshot;
        var isSyncing = snapshot.CycleStatus == "RUNNING";
        var waitingForData = snapshot.CycleStatus is "WAITING_FOR_CACHE" or "WAITING_FOR_GAME";
        if (!snapshot.Enabled) { NextSyncText.Text = "Collector disabled"; return; }
        if (snapshot.Game != "Running") { NextSyncText.Text = "Waiting for Call of Duty"; return; }
        if (isSyncing) { NextSyncText.Text = "Syncing now..."; return; }
        if (snapshot.LastSuccess is null) { NextSyncText.Text = "Waiting for first sync"; return; }
        if (waitingForData) { NextSyncText.Text = "Waiting for data"; return; }
        if (snapshot.NextCollection is not { } next) { NextSyncText.Text = "Starting..."; return; }
        var remaining = next - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) { NextSyncText.Text = "Starting..."; return; }
        NextSyncText.Text = $"Next sync in {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
    }
    private CollectorSettings Current => new(Enabled.IsChecked == true, Startup.IsChecked == true, KeepCodRunning.IsChecked == true);
    private void EnabledChanged(object sender, RoutedEventArgs e) { if (!updating) app.Save(Current); }
    private void StartupChanged(object sender, RoutedEventArgs e) { if (!updating) app.Save(Current); }
    private void KeepCodRunningChanged(object sender, RoutedEventArgs e) { if (!updating) app.Save(Current); }
    private async void SyncNow(object sender, RoutedEventArgs e) => await app.SyncNowAsync();
    private void CopyBuild(object sender, RoutedEventArgs e) => app.CopyBuildInfo();
    public void ShowSettings() => SettingsBackdrop.Visibility = Visibility.Visible;
    private void ShowSettings(object sender, RoutedEventArgs e) => ShowSettings();
    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await app.CheckForUpdatesFromSettingsAsync();
    private void CloseSettings(object sender, RoutedEventArgs e) => SettingsBackdrop.Visibility = Visibility.Collapsed;
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void HideToTray(object sender, RoutedEventArgs e) => app.HideWindow();
    private void TitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is System.Windows.Controls.Button) return;
        }

        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void WindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Escape && SettingsBackdrop.Visibility == Visibility.Visible) { CloseSettings(sender, e); e.Handled = true; } }
}
