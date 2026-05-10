using System;
using System.IO;
using Avalonia;
using Avalonia.WebView.Desktop;

namespace GamesLocalShare;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // WebView2 creates its user-data folder next to the exe by default. That path is
        // not writable when the app is installed under Program Files, which causes the
        // embedded WebView to fail silently and render a black screen. Force it to
        // %LOCALAPPDATA%\GamesLocalShare\WebView2 so it always has a writable location.
        var udf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GamesLocalShare", "WebView2");
        Directory.CreateDirectory(udf);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", udf);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }


    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseDesktopWebView()
            .LogToTrace();
}
