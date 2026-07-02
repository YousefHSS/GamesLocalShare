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
        // Headless helper mode: run an elevated PowerShell script on behalf of a
        // non-elevated instance. Elevating our own exe (instead of powershell.exe)
        // makes the UAC prompt show the app's name and icon. Exits without
        // starting the UI; the exit code is the script's exit code.
        if (args.Length >= 2 && args[0] == "--run-elevated-ps")
        {
            Environment.Exit(RunEncodedPowerShell(args[1]));
        }

        // Headless helper modes for the installer/uninstaller (which already run
        // elevated, so the task registers/unregisters without any UAC prompt).
        if (args.Length >= 1 && (args[0] == "--register-startup" || args[0] == "--unregister-startup"))
        {
            var ok = Services.StartupHelper.SetStartupEnabled(args[0] == "--register-startup");
            Environment.Exit(ok ? 0 : 1);
        }

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


    private static int RunEncodedPowerShell(string encodedCommand)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return 1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch
        {
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseDesktopWebView()
            .LogToTrace();
}
