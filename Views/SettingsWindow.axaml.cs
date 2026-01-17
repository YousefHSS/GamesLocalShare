using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamesLocalShare.Models;
using System.Collections.Generic;
using System.Linq;

namespace GamesLocalShare.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onSettingsSaved;
    private readonly List<GameInfo> _localGames;
    
    private CheckBox? _autoStartNetworkCheckBox;
    private CheckBox? _autoUpdateGamesCheckBox;
    private CheckBox? _autoResumeDownloadsCheckBox;
    private NumericUpDown? _updateIntervalNumeric;
    private CheckBox? _startWithWindowsCheckBox;
    private CheckBox? _minimizeToTrayCheckBox;
    private TextBlock? _hiddenGamesCountText;
    private ItemsControl? _hiddenGamesListBox;

    public SettingsWindow() : this(AppSettings.Load(), new List<GameInfo>(), () => { })
    {
    }

    public SettingsWindow(AppSettings settings, List<GameInfo> localGames, Action onSettingsSaved)
    {
        _settings = settings;
        _localGames = localGames;
        _onSettingsSaved = onSettingsSaved;
        
        InitializeComponent();
        LoadSettings();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        
        // Get references to controls
        _autoStartNetworkCheckBox = this.FindControl<CheckBox>("AutoStartNetworkCheckBox");
        _autoUpdateGamesCheckBox = this.FindControl<CheckBox>("AutoUpdateGamesCheckBox");
        _autoResumeDownloadsCheckBox = this.FindControl<CheckBox>("AutoResumeDownloadsCheckBox");
        _updateIntervalNumeric = this.FindControl<NumericUpDown>("UpdateIntervalNumeric");
        _startWithWindowsCheckBox = this.FindControl<CheckBox>("StartWithWindowsCheckBox");
        _minimizeToTrayCheckBox = this.FindControl<CheckBox>("MinimizeToTrayCheckBox");
        _hiddenGamesCountText = this.FindControl<TextBlock>("HiddenGamesCountText");
        _hiddenGamesListBox = this.FindControl<ItemsControl>("HiddenGamesListBox");
    }

    private void LoadSettings()
    {
        if (_autoStartNetworkCheckBox != null)
            _autoStartNetworkCheckBox.IsChecked = _settings.AutoStartNetwork;
        
        if (_autoUpdateGamesCheckBox != null)
            _autoUpdateGamesCheckBox.IsChecked = _settings.AutoUpdateGames;
        
        if (_autoResumeDownloadsCheckBox != null)
            _autoResumeDownloadsCheckBox.IsChecked = _settings.AutoResumeDownloads;
        
        if (_updateIntervalNumeric != null)
            _updateIntervalNumeric.Value = _settings.AutoUpdateCheckInterval;
        
        if (_startWithWindowsCheckBox != null)
            _startWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        
        if (_minimizeToTrayCheckBox != null)
            _minimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
        
        // Load hidden games list
        LoadHiddenGamesList();
    }

    private void LoadHiddenGamesList()
    {
        var hiddenGameNames = _localGames
            .Where(g => _settings.HiddenGameIds.Contains(g.AppId))
            .Select(g => g.Name)
            .OrderBy(name => name)
            .ToList();
        
        if (_hiddenGamesCountText != null)
        {
            _hiddenGamesCountText.Text = hiddenGameNames.Count == 1 
                ? "1 game" 
                : $"{hiddenGameNames.Count} games";
        }
        
        if (_hiddenGamesListBox != null)
        {
            if (hiddenGameNames.Count == 0)
            {
                _hiddenGamesListBox.ItemsSource = new List<string> { "No hidden games" };
            }
            else
            {
                _hiddenGamesListBox.ItemsSource = hiddenGameNames;
            }
        }
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        // Save all settings
        if (_autoStartNetworkCheckBox != null)
            _settings.AutoStartNetwork = _autoStartNetworkCheckBox.IsChecked ?? false;
        
        if (_autoUpdateGamesCheckBox != null)
            _settings.AutoUpdateGames = _autoUpdateGamesCheckBox.IsChecked ?? false;
        
        if (_autoResumeDownloadsCheckBox != null)
            _settings.AutoResumeDownloads = _autoResumeDownloadsCheckBox.IsChecked ?? false;
        
        if (_updateIntervalNumeric != null)
            _settings.AutoUpdateCheckInterval = (int)(_updateIntervalNumeric.Value ?? 30);
        
        if (_startWithWindowsCheckBox != null)
            _settings.StartWithWindows = _startWithWindowsCheckBox.IsChecked ?? false;
        
        if (_minimizeToTrayCheckBox != null)
            _settings.MinimizeToTray = _minimizeToTrayCheckBox.IsChecked ?? false;
        
        // Persist to disk
        _settings.Save();
        
        // Notify parent
        _onSettingsSaved();
        
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        // Reset to defaults
        if (_autoStartNetworkCheckBox != null)
            _autoStartNetworkCheckBox.IsChecked = false;
        
        if (_autoUpdateGamesCheckBox != null)
            _autoUpdateGamesCheckBox.IsChecked = false;
        
        if (_autoResumeDownloadsCheckBox != null)
            _autoResumeDownloadsCheckBox.IsChecked = false;
        
        if (_updateIntervalNumeric != null)
            _updateIntervalNumeric.Value = 30;
        
        if (_startWithWindowsCheckBox != null)
            _startWithWindowsCheckBox.IsChecked = false;
        
        if (_minimizeToTrayCheckBox != null)
            _minimizeToTrayCheckBox.IsChecked = false;
    }

    private void ShowAllHiddenGamesButton_Click(object? sender, RoutedEventArgs e)
    {
        // Clear all hidden game IDs
        _settings.HiddenGameIds.Clear();
        _settings.Save();
        
        // Update display
        LoadHiddenGamesList();
        
        // Notify parent to refresh
        _onSettingsSaved();
    }
}
