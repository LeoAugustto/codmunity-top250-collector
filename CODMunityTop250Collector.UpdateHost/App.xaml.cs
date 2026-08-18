using System.Windows;

namespace CODMunityTop250Collector.UpdateHost;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _ = RunAsync(e.Args);
    }

    private async Task RunAsync(string[] args)
    {
        if (!UpdateHostArguments.TryParse(args, out var options) || options is null)
        {
            ShowFailure("The update request is invalid.");
            return;
        }
        var result = await UpdateInstallation.RunAsync(options, _ => { });
        if (UpdateHostPresentation.ShouldShowFailureWindow(result))
        {
            ShowFailure(result.Error ?? "The update could not be completed.");
            return;
        }
        await Task.Delay(350);
        Shutdown(0);
    }

    private void ShowFailure(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var window = new UpdateWindow();
            MainWindow = window;
            window.Show();
            window.ShowFailure(message);
        });
    }
}
