using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

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
