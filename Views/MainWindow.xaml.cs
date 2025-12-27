using MahApps.Metro.Controls;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using GamesLocalShare.Models;

namespace GamesLocalShare.Views;

/// <summary>
/// Main window for the Games Local Share application
/// </summary>
public partial class MainWindow : MetroWindow
{
    public MainWindow()
    {
        System.Diagnostics.Debug.WriteLine("MainWindow constructor called.");
        InitializeComponent();
        System.Diagnostics.Debug.WriteLine("InitializeComponent completed.");
        LoadIcons();
    }

    private void LoadIcons()
    {
        try
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            string iconsDir = Path.Combine(basePath, "Icons");

            SetImageSourceIfExists(ImageGames, Path.Combine(iconsDir, "controller.png"));
            SetImageSourceIfExists(ImagePeers, Path.Combine(iconsDir, "desktop.png"));
            SetImageSourceIfExists(ImageUpdates, Path.Combine(iconsDir, "sync.png"));
            SetImageSourceIfExists(ImageDownload, Path.Combine(iconsDir, "download.png"));
            SetImageSourceIfExists(ImagePause, Path.Combine(iconsDir, "pause.png"));
            SetImageSourceIfExists(ImageWarning, Path.Combine(iconsDir, "warning.png"));
            SetImageSourceIfExists(ImageLog, Path.Combine(iconsDir, "log.png"));
            // Optional: wired/wifi/check
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadIcons error: {ex.Message}");
        }
    }

    private void SetImageSourceIfExists(System.Windows.Controls.Image img, string filePath)
    {
        if (img == null) return;
        try
        {
            if (File.Exists(filePath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                img.Source = bitmap;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load icon {filePath}: {ex.Message}");
        }
    }

    private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is MenuItem mi)
            {
                // Debug log to verify CommandParameter
                System.Diagnostics.Debug.WriteLine($"OpenGameFolder_Click: CommandParameter={mi.CommandParameter}");

                // The CommandParameter binding should carry the GameInfo
                var game = mi.CommandParameter as GameInfo;
                if (game == null)
                {
                    System.Diagnostics.Debug.WriteLine("OpenGameFolder_Click: CommandParameter was null or not a GameInfo");
                    MessageBox.Show("Could not determine the selected game.", "Open Game Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var path = game.InstallPath;
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    System.Diagnostics.Debug.WriteLine($"OpenGameFolder_Click: Install path invalid: {path}");
                    MessageBox.Show($"Game folder does not exist:\n{path}", "Open Game Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenGameFolder_Click error: {ex}");
            MessageBox.Show($"Failed to open folder: {ex.Message}", "Open Game Folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
