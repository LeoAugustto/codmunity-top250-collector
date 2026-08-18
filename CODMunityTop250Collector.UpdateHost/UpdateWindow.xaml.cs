using System.Windows;

namespace CODMunityTop250Collector.UpdateHost;

public partial class UpdateWindow : Window
{
    public UpdateWindow() => InitializeComponent();
    public void SetStatus(string text) => StatusText.Text = text;
    public void ShowFailure(string text)
    {
        StatusText.Text = "Update couldn't be completed";
        DetailText.Text = text;
        Progress.IsIndeterminate = false;
        CloseButton.Visibility = Visibility.Visible;
    }
    private void CloseButtonClick(object sender, RoutedEventArgs e) => Application.Current.Shutdown(1);
}
