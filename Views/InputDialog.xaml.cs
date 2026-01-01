using System.Windows;

namespace GamesLocalShare.Views;

/// <summary>
/// Simple input dialog for getting user input
/// </summary>
public partial class InputDialog : Window
{
    public string ResponseText { get; private set; } = string.Empty;

    public InputDialog(string prompt, string title = "Input")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ResponseTextBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ResponseText = ResponseTextBox.Text;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ResponseTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            OkButton_Click(sender, e);
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            CancelButton_Click(sender, e);
        }
    }
}
