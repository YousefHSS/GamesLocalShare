using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace GamesLocalShare.Services;

/// <summary>
/// Helpers for on-demand UAC elevation. The app manifest stays at asInvoker;
/// elevation is only requested when starting an Xbox transfer operation.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevationHelper
{
    /// <summary>
    /// Returns true if the current process is running with administrator privileges.
    /// </summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Relaunches the current executable with "runas" verb to trigger a UAC prompt.
    /// Forwards the provided arguments so the new instance can resume state.
    /// Returns true if relaunch was initiated; the current process should then exit.
    /// </summary>
    /// <summary>
    /// Runs a PowerShell script with administrator privileges and waits for it to
    /// finish. When the app is already elevated the script runs in a hidden child
    /// directly; otherwise the app's own exe is elevated in its --run-elevated-ps
    /// helper mode (so the UAC prompt shows the app's name and icon) and it runs
    /// the script. Returns true when the script exited with code 0; false on
    /// failure or when the user declined the UAC prompt.
    /// </summary>
    public static bool RunPowerShellElevated(string psScript)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            ProcessStartInfo psi;
            if (IsElevated())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {enc}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            else
            {
                var selfExe = Environment.ProcessPath;
                psi = new ProcessStartInfo
                {
                    FileName = string.IsNullOrEmpty(selfExe) ? "powershell.exe" : selfExe,
                    Arguments = string.IsNullOrEmpty(selfExe)
                        ? $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {enc}"
                        : $"--run-elevated-ps {enc}",
                    UseShellExecute = true,   // required for Verb=runas
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
            }

            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED (user declined UAC prompt)
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool RelaunchAsAdmin(string[]? forwardArgs = null)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe))
            return false;

        var args = string.Join(" ", forwardArgs ?? Array.Empty<string>());

        var psi = new ProcessStartInfo
        {
            FileName = currentExe,
            Arguments = args,
            Verb = "runas",
            UseShellExecute = true,
        };

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED (user declined UAC prompt)
            return false;
        }
        catch
        {
            return false;
        }
    }
}
