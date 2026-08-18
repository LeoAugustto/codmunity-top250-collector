namespace CODMunityTop250Collector.UpdateHost;

public static class UpdateHostPresentation
{
    // Successful automatic updates are deliberately headless; only failures get a window.
    public static bool ShouldShowFailureWindow(UpdateInstallationResult result) => !result.AppRestarted;
}
