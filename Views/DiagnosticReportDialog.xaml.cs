using System.Windows;

namespace GamesLocalShare.Views;

/// <summary>
/// Dialog for displaying scrollable diagnostic reports
/// </summary>
public partial class DiagnosticReportDialog : Window
{
    public DiagnosticReportDialog(string report, string title = "Network Diagnostic Report")
    {
        InitializeComponent();
        Title = title;
        ReportTextBox.Text = report;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ReportTextBox.Text);
            CopyButton.Content = "? Copied!";
            
            // Reset button text after 2 seconds
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            timer.Tick += (s, args) =>
            {
                CopyButton.Content = "Copy to Clipboard";
                timer.Stop();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to copy: {ex.Message}", "Copy Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
