using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace GamesLocalShare.Services;

/// <summary>
/// Helper for managing Windows startup functionality.
/// Startup is registered as a Task Scheduler logon task with RunLevel=Highest so the
/// app starts already elevated — the cache proxy's hosts/urlacl setup and the Xbox
/// transfer flows then run without any per-session UAC prompts. Creating or removing
/// the task needs one elevation; if the user declines it, enabling falls back to the
/// old per-user Run registry key (app starts non-elevated and prompts on demand).
/// On non-Windows platforms, these methods return appropriate defaults.
/// </summary>
public static class StartupHelper
{
    private const string AppName = "GamesLocalShare";
    private const string TaskName = "GamesLocalShare";
    private static readonly string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Checks if the application is set to run on Windows startup
    /// (either via the elevated scheduled task or the legacy Run key).
    /// </summary>
    public static bool IsStartupEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return IsStartupEnabledWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsStartupEnabledWindows()
    {
        return StartupTaskExists() || RunKeyEntryExists();
    }

    /// <summary>
    /// Enables or disables running the application on Windows startup
    /// </summary>
    public static bool SetStartupEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            Debug.WriteLine("Startup configuration is only supported on Windows");
            return false;
        }

        return SetStartupEnabledWindows(enabled);
    }

    [SupportedOSPlatform("windows")]
    private static bool SetStartupEnabledWindows(bool enabled)
    {
        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Debug.WriteLine("Could not determine executable path");
                return false;
            }

            if (ElevationHelper.RunPowerShellElevated(BuildRegisterTaskScript(exePath)))
            {
                // The task now owns startup; remove the legacy entry so the app
                // isn't launched twice at logon.
                SetRunKeyEnabled(false);
                Debug.WriteLine($"Registered elevated startup task: {exePath}");
                return true;
            }

            // Elevation declined/failed — fall back to the non-elevated Run key.
            Debug.WriteLine("Task registration declined/failed; falling back to Run key");
            return SetRunKeyEnabled(true);
        }

        var ok = SetRunKeyEnabled(false);
        if (StartupTaskExists())
        {
            ok = ElevationHelper.RunPowerShellElevated(
                "$ErrorActionPreference='Stop'\n" +
                $"try {{ Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false; exit 0 }} catch {{ exit 1 }}") && ok;
        }
        return ok;
    }

    /// <summary>
    /// Script that registers (or replaces) the logon task. The user is embedded from
    /// the current (non-elevated) identity so the task targets the logged-on user even
    /// if elevation happens under a different admin account.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string BuildRegisterTaskScript(string exePath)
    {
        static string Q(string s) => s.Replace("'", "''");

        string user;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            user = id.Name;
        }
        catch
        {
            user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        }

        var workDir = Path.GetDirectoryName(exePath) ?? "";

        // ExecutionTimeLimit of zero = no time limit (the app runs indefinitely).
        return
            "$ErrorActionPreference = 'Stop'\n" +
            "try {\n" +
            $"    $action = New-ScheduledTaskAction -Execute '{Q(exePath)}' -Argument '--minimized' -WorkingDirectory '{Q(workDir)}'\n" +
            $"    $trigger = New-ScheduledTaskTrigger -AtLogOn -User '{Q(user)}'\n" +
            "    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)\n" +
            $"    $principal = New-ScheduledTaskPrincipal -UserId '{Q(user)}' -LogonType Interactive -RunLevel Highest\n" +
            $"    Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null\n" +
            "    exit 0\n" +
            "} catch {\n" +
            // Surface the failure for diagnosis — the exit code alone can't be
            // distinguished from a declined UAC prompt by the caller.
            "    try {\n" +
            "        $d = Join-Path $env:LOCALAPPDATA 'GamesLocalShare'\n" +
            "        New-Item -ItemType Directory -Force -Path $d | Out-Null\n" +
            "        $_ | Out-String | Set-Content -Path (Join-Path $d 'startup-task-error.log')\n" +
            "    } catch {}\n" +
            "    exit 1\n" +
            "}";
    }

    [SupportedOSPlatform("windows")]
    private static bool StartupTaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(10000)) return false;
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error querying startup task: {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool RunKeyEntryExists()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error checking startup status: {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool SetRunKeyEnabled(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey, true);
            if (key == null)
            {
                Debug.WriteLine("Could not open registry key");
                return false;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
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
