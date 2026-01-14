using Avalonia.Controls;
using Avalonia.Input;
using GamesLocalShare.ViewModels;

namespace GamesLocalShare.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ViewModels.MainViewModel();
    }

    private void MainGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Close log panel if clicking outside of it
        if (DataContext is MainViewModel vm && vm.IsLogVisible)
        {
            var logBorder = this.FindControl<Border>("LogBorder");
            if (logBorder != null)
            {
                var position = e.GetPosition(logBorder);
                var bounds = logBorder.Bounds;
                
                // Check if click is outside the log border
                if (position.X < 0 || position.Y < 0 || 
                    position.X > bounds.Width || position.Y > bounds.Height)
                {
                    vm.IsLogVisible = false;
                }
            }
        }
    }
}
