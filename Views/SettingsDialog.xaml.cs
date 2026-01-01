using System.Windows;
using GamesLocalShare.Models;
using GamesLocalShare.Services;

namespace GamesLocalShare.Views;

/// <summary>
/// Settings dialog for configuring application preferences
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly List<GameInfo> _allGames;

    public AppSettings Settings => _settings;
    public bool SettingsChanged { get; private set; }

    public SettingsDialog(AppSettings settings, List<GameInfo> allGames)
    {
        InitializeComponent();
        _settings = settings;
        _allGames = allGames;

        LoadSettings();
    }

    private void LoadSettings()
    {
        // Load current settings into UI
        StartWithWindowsToggle.IsOn = _settings.StartWithWindows;
        AutoStartNetworkToggle.IsOn = _settings.AutoStartNetwork;
        AutoUpdateGamesToggle.IsOn = _settings.AutoUpdateGames;
        UpdateIntervalInput.Value = _settings.AutoUpdateCheckInterval;
        MinimizeToTrayToggle.IsOn = _settings.MinimizeToTray;

        // Load hidden games list
        var hiddenGames = _allGames
            .Where(g => _settings.IsGameHidden(g.AppId))
            .ToList();

        HiddenGamesList.ItemsSource = hiddenGames;
        NoHiddenGamesText.Visibility = hiddenGames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Save settings
            var oldStartWithWindows = _settings.StartWithWindows;
            
            _settings.StartWithWindows = StartWithWindowsToggle.IsOn;
            _settings.AutoStartNetwork = AutoStartNetworkToggle.IsOn;
            _settings.AutoUpdateGames = AutoUpdateGamesToggle.IsOn;
            _settings.AutoUpdateCheckInterval = (int)UpdateIntervalInput.Value;
            _settings.MinimizeToTray = MinimizeToTrayToggle.IsOn;

            // Update Windows startup if changed
            if (oldStartWithWindows != _settings.StartWithWindows)
            {
                var success = StartupHelper.SetStartupEnabled(_settings.StartWithWindows);
                if (!success)
                {
                    MessageBox.Show(
                        "Failed to update Windows startup setting. You may need to run as Administrator.",
                        "Startup Setting Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    
                    // Revert the setting if it failed
                    _settings.StartWithWindows = oldStartWithWindows;
                }
            }

            _settings.Save();
            SettingsChanged = true;

            MessageBox.Show(
                "Settings saved successfully!",
                "Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error saving settings: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void UnhideGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not GameInfo game)
            return;

        _settings.UnhideGame(game.AppId);
        LoadSettings(); // Refresh the list
        SettingsChanged = true;
    }
}
