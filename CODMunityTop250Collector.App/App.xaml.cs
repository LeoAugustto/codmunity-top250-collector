using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Reflection;
using CODMunityTop250Collector.Core;
using CODMunityTop250Collector.Updater;
using Forms = System.Windows.Forms;

namespace CODMunityTop250Collector.App;
public partial class App : System.Windows.Application
{
    private Mutex? mutex; private EventWaitHandle? showEvent; private CancellationTokenSource? cts; private MainWindow? window; private Forms.NotifyIcon? tray; private CollectorScheduler? scheduler; private CodWatchdogService? watchdog; private SettingsStore? settingsStore; private CollectorSettings settings = new(); private AppLogger? log; private PrivateUpdateClient? updater; private readonly AutomaticUpdateCoordinator automaticUpdate = new(); private readonly UpdateOperationGate updateOperation = new();
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            mutex = new Mutex(true, "Local\\CODMunityTop250Collector", out var first);
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CODMunityTop250Collector.Show");
            if (!first) { showEvent.Set(); Shutdown(); return; }
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            log = new AppLogger(); log.Write("APP_START"); settingsStore = new SettingsStore(log); settings = settingsStore.Load();
            var rankedApi = new RankedPlayersApiClient(new CompositeApiTokenProvider(new EnvironmentApiTokenProvider(), new EmbeddedRankedSyncTokenProvider()), log);
            var codMonitor = new CodProcessMonitor();
            scheduler = new CollectorScheduler(codMonitor, new EngineRunner(log), new EngineStatusParser(), rankedApi, log, Path.Combine(AppContext.BaseDirectory, "engine")); scheduler.UpdateSettings(settings); scheduler.Changed += s => Dispatcher.Invoke(() => { window?.Update(s); UpdateTray(s); });
            window = new MainWindow(this); window.SetVersion(AppVersion.Display); window.SetSettings(settings); CreateTray(); cts = new CancellationTokenSource();
            watchdog = new CodWatchdogService(codMonitor, scheduler, log, Path.Combine(AppContext.BaseDirectory, "Assets", "watchdog", "bnet-play.png"));
            watchdog.Changed += s => Dispatcher.Invoke(() => window?.SetWatchdogStatus(s));
            window.SetWatchdogStatus(watchdog.Snapshot);
            var publicKey = EmbeddedUpdateKey.LoadSubjectPublicKeyInfo();
            try { updater = new PrivateUpdateClient(publicKey, (evt, detail) => log?.Write(evt, detail)); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey); }
            _ = scheduler.RunAsync(cts.Token); _ = watchdog.RunAsync(cts.Token); _ = RunUpdateChecksAsync(cts.Token);
            _ = Task.Run(async () => { while (!cts.IsCancellationRequested) { showEvent.WaitOne(); await Dispatcher.InvokeAsync(ShowWindow); } });
        }
        catch (Exception ex)
        {
            try { (log ??= new AppLogger()).Write("APP_FATAL", $"{ex.GetType().Name} {ex.Message}"); } catch { }
            try { System.Windows.MessageBox.Show("CODMunity Top 250 Collector could not start. The error was recorded in the logs.", "CODMunity Top 250 Collector", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            Shutdown(-1);
        }
    }
    private void CreateTray()
    {
        tray = new Forms.NotifyIcon { Icon = LoadTrayIcon(), Visible = true, Text = "CODMunity Collector | Waiting for Call of Duty" };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("CODMunity Top 250 Collector", null, (_, _) => ShowWindow()); menu.Items.Add(new Forms.ToolStripSeparator());
        var enabled = new Forms.ToolStripMenuItem("Collector Enabled") { Checked = settings.CollectorEnabled, CheckOnClick = true }; enabled.CheckedChanged += (_, _) => Save(settings with { CollectorEnabled = enabled.Checked }); menu.Items.Add(enabled);
        var sync = new Forms.ToolStripMenuItem("Sync Now"); sync.Click += async (_, _) => { if (scheduler is not null) await scheduler.SyncNowAsync(CancellationToken.None); }; menu.Items.Add(sync);
        menu.Items.Add("Open Status", null, (_, _) => ShowWindow());
        var checkUpdates = new Forms.ToolStripMenuItem("Check for updates"); checkUpdates.Click += async (_, _) => await CheckForUpdatesFromTrayAsync(); menu.Items.Add(checkUpdates);
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = settings.StartWithWindows, CheckOnClick = true }; startup.CheckedChanged += (_, _) => { StartupRegistration.Set(startup.Checked, Environment.ProcessPath!); Save(settings with { StartWithWindows = startup.Checked }); }; menu.Items.Add(startup);
        menu.Items.Add("Exit", null, (_, _) => Shutdown()); tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => ShowWindow();
    }
    private System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "codmunity-logo.ico");
            if (File.Exists(assetPath)) return new System.Drawing.Icon(assetPath);
            log?.Write("TRAY_ICON_FALLBACK", "Bundled icon asset is missing.");
        }
        catch (Exception ex) { log?.Write("TRAY_ICON_FALLBACK", $"Bundled icon could not be loaded: {ex.Message}"); }

        try
        {
            var executableIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (executableIcon is not null) return executableIcon;
            log?.Write("TRAY_ICON_FALLBACK", "Executable-associated icon is unavailable.");
        }
        catch (Exception ex) { log?.Write("TRAY_ICON_FALLBACK", $"Executable-associated icon could not be loaded: {ex.Message}"); }

        log?.Write("TRAY_ICON_FALLBACK", "Using SystemIcons.Application.");
        return System.Drawing.SystemIcons.Application;
    }
    public void Save(CollectorSettings updated) { settings = updated.Normalized(); settingsStore!.Save(settings); scheduler!.UpdateSettings(settings); watchdog?.NotifySettingsChanged(settings); window?.SetSettings(settings); }
    public Task SyncNowAsync() => scheduler?.SyncNowAsync(CancellationToken.None) ?? Task.CompletedTask;
    public async Task CheckForUpdatesFromSettingsAsync() => await CheckForUpdatesAsync("settings", manual: true, CancellationToken.None);
    private async Task CheckForUpdatesFromTrayAsync()
    {
        await Dispatcher.InvokeAsync(() => { ShowWindow(); window?.ShowSettings(); });
        await CheckForUpdatesAsync("tray", manual: true, CancellationToken.None);
    }
    private async Task<bool> BeginAutomaticUpdateAsync(AvailableUpdate update, CancellationToken token)
    {
        if (updater is null || scheduler is null) return false;
        var outcome = await automaticUpdate.RunAsync(update,
            (candidate, cancellation) => updater.DownloadMsiAsync(candidate, Path.Combine(CollectorPaths.Root, "updates"), cancellation),
            scheduler.RequestUpdatePreparation,
            scheduler.WaitForIdleAsync,
            scheduler.CancelUpdatePreparation,
            (evt, detail) => log?.Write(evt, detail),
            StartUpdateHost,
            token);
        if (outcome != AutomaticUpdateOutcome.HandedOff) return false;
        Shutdown();
        return true;
    }
    public void ShowWindow() { window!.Show(); window.Activate(); window.WindowState = WindowState.Normal; }
    public void HideWindow() => window?.Hide();
    public void CopyBuildInfo() { try { System.Windows.Clipboard.SetText($"collectorVersion={AppVersion.Informational}"); } catch { } }
    private async Task RunUpdateChecksAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(UpdateCheckSchedule.InitialDelay, token);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (await CheckForUpdatesAsync("automatic", manual: false, token)) return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { log?.Write("UPDATE_CHECK_FAILED", ex.GetType().Name); }
                await Task.Delay(UpdateCheckSchedule.RecurringInterval, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log?.Write("UPDATE_CHECK_FAILED", ex.GetType().Name); }
    }
    private async Task<bool> CheckForUpdatesAsync(string source, bool manual, CancellationToken token)
    {
        if (updater is null || automaticUpdate.IsActive) return false;
        if (!updateOperation.TryBegin())
        {
            if (manual) window?.SetUpdateStatus("Update check already in progress...", null);
            return false;
        }

        var handedOff = false;
        try
        {
            if (!AppSemanticVersion.TryParse(AppVersion.Informational, out var current))
            {
                if (manual) window?.SetUpdateStatus("Unable to check for updates.\nSee logs for details.", null);
                return false;
            }
            if (manual)
            {
                log?.Write("UPDATE_CHECK_MANUAL_START", $"source={source}");
                window?.SetUpdateStatus("Checking for updates...", null);
            }
            var result = await updater.CheckAsync(current, token);
            var checkedAt = DateTimeOffset.UtcNow;
            switch (result.Kind)
            {
                case UpdateCheckKind.Available when result.Update is not null:
                    if (manual)
                    {
                        var approved = window is not null && await window.ConfirmManualUpdateAsync(result.Update.Version.ToString());
                        if (!approved) { window?.SetUpdateStatus($"Update available: v{result.Update.Version}", checkedAt); return false; }
                        window?.SetUpdateStatus($"Update available: v{result.Update.Version}\nDownloading update...", checkedAt);
                    }
                    handedOff = await BeginAutomaticUpdateAsync(result.Update, token);
                    if (!handedOff && manual) window?.SetUpdateStatus("Unable to check for updates.\nSee logs for details.", checkedAt);
                    return handedOff;
                case UpdateCheckKind.None:
                    if (manual) window?.SetUpdateStatus($"You're up to date.\nv{AppVersion.Display} is the latest version.", checkedAt);
                    return false;
                default:
                    if (manual) window?.SetUpdateStatus("Unable to check for updates.\nSee logs for details.", checkedAt);
                    return false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            log?.Write("UPDATE_CHECK_FAILED", ex.GetType().Name);
            if (manual) window?.SetUpdateStatus("Unable to check for updates.\nSee logs for details.", null);
            return false;
        }
        finally
        {
            if (!handedOff) updateOperation.End();
        }
    }
    private void StartUpdateHost(string msiPath, string expectedSha256)
    {
        var sourceDirectory = AppContext.BaseDirectory;
        var stage = Path.Combine(CollectorPaths.Root, "updates", "host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var names = new[] { "CODMunityTop250Collector.UpdateHost.exe", "CODMunityTop250Collector.UpdateHost.dll", "CODMunityTop250Collector.UpdateHost.deps.json", "CODMunityTop250Collector.UpdateHost.runtimeconfig.json" };
        foreach (var name in names)
        {
            var source = Path.Combine(sourceDirectory, name); if (!File.Exists(source)) throw new FileNotFoundException("Update host file is unavailable.", source);
            File.Copy(source, Path.Combine(stage, name), overwrite: true);
        }
        var start = new ProcessStartInfo(Path.Combine(stage, names[0])) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--msi"); start.ArgumentList.Add(msiPath);
        start.ArgumentList.Add("--sha256"); start.ArgumentList.Add(expectedSha256);
        start.ArgumentList.Add("--app"); start.ArgumentList.Add(Environment.ProcessPath ?? throw new InvalidOperationException("Application path is unavailable."));
        if (Process.Start(start) is null) throw new InvalidOperationException("Update host did not start.");
    }
    private void UpdateTray(CollectorSnapshot snapshot)
    {
        if (tray is null) return;
        tray.Text = !snapshot.Enabled ? "CODMunity Collector | Collector disabled"
            : snapshot.Game != "Running" ? "CODMunity Collector | Waiting for Call of Duty"
            : snapshot.CycleStatus == "RUNNING" ? "CODMunity Collector | Syncing..."
            : snapshot.LastSuccess is null ? "CODMunity Collector | COD running | Waiting for sync"
            : $"CODMunity Collector | COD running | Last sync {snapshot.LastSuccess.Value.ToLocalTime():HH:mm}";
    }
    protected override void OnExit(ExitEventArgs e) { cts?.Cancel(); updater?.Dispose(); tray?.Dispose(); log?.Write("APP_EXIT"); mutex?.ReleaseMutex(); mutex?.Dispose(); showEvent?.Dispose(); base.OnExit(e); }
}
internal static class AppVersion
{
    public static string Informational => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    public static string Display => Informational.Split('-', '+')[0];
}
