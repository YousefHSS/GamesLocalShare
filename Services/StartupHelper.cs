using Microsoft.Win32;
using System.Diagnostics;

namespace GamesLocalShare.Services;

/// <summary>
/// Helper for managing Windows startup functionality
/// </summary>
public static class StartupHelper
{
    private const string AppName = "GamesLocalShare";
    private static readonly string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Checks if the application is set to run on Windows startup
    /// </summary>
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
            var value = key?.GetValue(AppName);
            return value != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error checking startup status: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Enables or disables running the application on Windows startup
    /// </summary>
    public static bool SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
            if (key == null)
            {
                Debug.WriteLine("Could not open registry key");
                return false;
            }

            if (enabled)
            {
                // Get the path to the executable
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    Debug.WriteLine("Could not determine executable path");
                    return false;
                }

                // Add to startup with --minimized argument
                key.SetValue(AppName, $"\"{exePath}\" --minimized");
                Debug.WriteLine($"Added to startup: {exePath}");
            }
            else
            {
                // Remove from startup
                key.DeleteValue(AppName, false);
                Debug.WriteLine("Removed from startup");
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error setting startup: {ex.Message}");
            return false;
        }
    }
}
