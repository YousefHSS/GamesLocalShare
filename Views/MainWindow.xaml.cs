using MahApps.Metro.Controls;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamesLocalShare.Models;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

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
            string svgDir = Path.Combine(basePath, "Icons", "SVGs");

            SetImageSourceIfExists(ImageGames, Path.Combine(svgDir, "controller.svg"));
            SetImageSourceIfExists(ImagePeers, Path.Combine(svgDir, "desktop.svg"));
            SetImageSourceIfExists(ImageUpdates, Path.Combine(svgDir, "sync.svg"));
            SetImageSourceIfExists(ImageDownload, Path.Combine(svgDir, "download.svg"));
            SetImageSourceIfExists(ImagePause, Path.Combine(svgDir, "pause.svg"));
            SetImageSourceIfExists(ImageWarning, Path.Combine(svgDir, "warning.svg"));
            SetImageSourceIfExists(ImageLog, Path.Combine(svgDir, "log.svg"));

            // Load optional SVGs into resources
            LoadSvgIntoResource("IconWired", Path.Combine(basePath, "Icons", "wired.svg"));
            LoadSvgIntoResource("IconWifi", Path.Combine(basePath, "Icons", "wifi.svg"));
            LoadSvgIntoResource("IconCheck", Path.Combine(svgDir, "check.svg"));

            // Load PNG icons into resources (steam/epic/xbox)
            LoadBitmapIntoResource("IconSteam", Path.Combine(basePath, "Icons", "steam.png"));
            LoadBitmapIntoResource("IconEpic", Path.Combine(basePath, "Icons", "epic.png"));
            LoadBitmapIntoResource("IconXbox", Path.Combine(basePath, "Icons", "xbox.png"));
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
                if (Path.GetExtension(filePath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    var reader = new FileSvgReader(new WpfDrawingSettings());
                    var drawing = reader.Read(filePath) as Drawing;
                    if (drawing != null)
                    {
                        img.Source = new DrawingImage(drawing);
                    }
                }
                else
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    img.Source = bitmap;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load icon {filePath}: {ex.Message}");
        }
    }

    private void LoadSvgIntoResource(string resourceKey, string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var reader = new FileSvgReader(new WpfDrawingSettings());
                var drawing = reader.Read(filePath) as Drawing;
                if (drawing != null)
                {
                    this.Resources[resourceKey] = new DrawingImage(drawing);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load SVG resource {resourceKey}: {ex.Message}");
        }
    }

    private void LoadBitmapIntoResource(string resourceKey, string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                // Store as ImageSource so bindings to Image.Source work
                this.Resources[resourceKey] = bitmap as ImageSource;
                System.Diagnostics.Debug.WriteLine($"Loaded {resourceKey} from {filePath}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"File not found: {filePath} for {resourceKey}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load bitmap resource {resourceKey}: {ex.Message}");
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
