using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace GamesLocalShare.Services;

/// <summary>
/// Populates a CIK store on demand by invoking the user's CikExtractor tool. CikExtractor reads the
/// packed content-instance keys from the local registry and derives the device key, writing decrypted
/// .cik files (one per key GUID) that xvdtool then auto-selects from by GUID.
///
/// <para>CikExtractor requires administrator rights, so it is launched elevated (a UAC prompt). The keys
/// belong to the user, are derived locally, and are used only to decrypt the user's own licensed
/// content; the app itself never implements the cipher.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CikExtractorRunner
{
    // On-demand scheduled task that runs CikExtractor with highest privileges. Registered once (one UAC, or
    // silent if the app is already elevated); every refresh after that starts it via Start-ScheduledTask, which
    // runs the task elevated WITHOUT a prompt — so key extraction becomes silent.
    private const string TaskName = "GamesLocalShare-CikExtractor";

    /// <summary>
    /// Ensures the CIK store contains at least one .cik. If it already does, returns the folder holding
    /// them <b>without prompting</b>. Otherwise locates CikExtractor under <paramref name="toolPath"/>,
    /// runs it elevated to dump keys into <paramref name="cikStore"/>, and returns the folder that
    /// actually received the .cik files (CikExtractor may nest them in a "Cik" subfolder). Returns null
    /// when no keys could be produced.
    /// </summary>
    /// <param name="forceRefresh">When true, always re-run CikExtractor (ignoring any already-populated
    /// store or reusable output next to the tool). Used after a capture fails on a missing content key:
    /// a newly installed title's CIK is provisioned during its install, so a previously dumped store is
    /// stale and must be refreshed.</param>
    public async Task<string?> EnsureCiksAsync(string toolPath, string cikStore, Action<string>? log, CancellationToken ct, bool forceRefresh = false)
    {
        Directory.CreateDirectory(cikStore);

        // Already populated (e.g. dumped earlier this session) — don't prompt again.
        if (!forceRefresh)
        {
            var existing = FindCikFolder(cikStore);
            if (existing != null) return existing;
        }

        var exe = ResolveExe(toolPath);
        if (exe == null)
        {
            log?.Invoke($"CikExtractor not found under '{toolPath}'; relying on a pre-populated CIK store");
            return forceRefresh ? FindCikFolder(cikStore) : null;
        }

        // Reuse an already-extracted CIK output sitting next to the tool (its bin\…\Cik) without
        // prompting — CIKs are stable per device, so re-running CikExtractor each time is wasteful.
        // (Skipped on a forced refresh, which must dump the current device keys into the store.)
        if (!forceRefresh)
        {
            var exeDir = Path.GetDirectoryName(exe);
            if (exeDir != null)
            {
                var near = FindCikFolder(exeDir);
                if (near != null)
                {
                    log?.Invoke($"using existing CIKs from {near}");
                    return near;
                }
            }
        }

        // Preferred: run via the scheduled task (elevated, no UAC prompt after a one-time registration).
        if (TryRefreshViaTask(exe, cikStore, log))
        {
            var viaTask = FindCikFolder(cikStore);
            if (viaTask != null) return viaTask;
            log?.Invoke("CikExtractor task produced no keys — falling back to a direct run");
        }

        // Fallback: direct elevated launch (UAC prompt each time). Used when the task couldn't be registered
        // (declined) or ran but produced nothing.
        log?.Invoke("running CikExtractor (elevated) to populate the CIK store — accept the UAC prompt …");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true, // required for the runas verb
                Verb = "runas",          // request elevation (UAC)
                WorkingDirectory = Path.GetDirectoryName(exe) ?? cikStore,
            };
            psi.ArgumentList.Add("dump");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(cikStore);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                log?.Invoke("failed to start CikExtractor");
                return FindCikFolder(cikStore);
            }
            await proc.WaitForExitAsync(ct);
            log?.Invoke($"CikExtractor exited ({proc.ExitCode})");
        }
        catch (Exception ex)
        {
            // Most commonly: the user declined the UAC prompt (Win32 1223).
            log?.Invoke($"CikExtractor did not run: {ex.Message}");
        }

        var produced = FindCikFolder(cikStore);
        if (produced == null)
            log?.Invoke("CikExtractor produced no .cik files");
        return produced;
    }

    /// <summary>Removes the on-demand CikExtractor scheduled task (called from the uninstaller, which runs
    /// elevated, so the delete is silent). Best-effort — ignores failure.</summary>
    public static void UnregisterTask()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{TaskName}\" /F",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
        }
        catch { }
    }

    // ---- silent scheduled-task path -------------------------------------------------------------------

    /// <summary>Runs CikExtractor via its scheduled task (elevated, no UAC): registers the task once if needed
    /// (one prompt, or silent if the app is already elevated), then starts it and waits. Returns true if the
    /// task ran; false if it couldn't be registered (so the caller falls back to a direct elevated launch).</summary>
    private static bool TryRefreshViaTask(string exe, string cikStore, Action<string>? log)
    {
        try
        {
            if (!(TaskExists() && TaskMatchesCurrent(cikStore, exe)))
            {
                log?.Invoke(ElevationHelper.IsElevated()
                    ? "registering the CikExtractor scheduled task …"
                    : "setting up silent key extraction — accept this one-time UAC prompt (no prompts afterward) …");
                if (!ElevationHelper.RunPowerShellElevated(BuildRegisterCikTaskScript(exe, cikStore)))
                    return false;
                try { File.WriteAllText(MarkerPath(cikStore), exe); } catch { }
            }
            log?.Invoke("refreshing keys via the CikExtractor task (no prompt) …");
            RunTaskAndWait();
            return true;
        }
        catch (Exception ex) { log?.Invoke($"CikExtractor task path unavailable: {ex.Message}"); return false; }
    }

    private static string MarkerPath(string cikStore) => Path.Combine(cikStore, ".ciktask");

    /// <summary>True if the task exists AND was registered for the current exe (so a moved/updated tool triggers
    /// a re-register instead of silently running a stale command).</summary>
    private static bool TaskMatchesCurrent(string cikStore, string exe)
    {
        try
        {
            var m = MarkerPath(cikStore);
            return File.Exists(m) && string.Equals(File.ReadAllText(m).Trim(), exe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool TaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(10000)) return false;
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>PowerShell (run elevated once) that registers the on-demand task: runs
    /// <c>CikExtractor.exe dump -c &lt;store&gt;</c> as the current user with RunLevel Highest.</summary>
    private static string BuildRegisterCikTaskScript(string exe, string cikStore)
    {
        static string Q(string s) => s.Replace("'", "''");
        string user;
        try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); user = id.Name; }
        catch { user = $"{Environment.UserDomainName}\\{Environment.UserName}"; }
        var workDir = Path.GetDirectoryName(exe) ?? cikStore;
        // -Argument is a single PS string: dump -c "<store>" (the path double-quoted for spaces).
        var arg = "dump -c \"" + Q(cikStore) + "\"";
        return
            "$ErrorActionPreference='Stop'\n" +
            "try {\n" +
            $"  $action = New-ScheduledTaskAction -Execute '{Q(exe)}' -Argument '{arg}' -WorkingDirectory '{Q(workDir)}'\n" +
            $"  $principal = New-ScheduledTaskPrincipal -UserId '{Q(user)}' -LogonType Interactive -RunLevel Highest\n" +
            "  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(10)) -MultipleInstances IgnoreNew\n" +
            $"  Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Principal $principal -Settings $settings -Force | Out-Null\n" +
            "  exit 0\n} catch { exit 1 }";
    }

    /// <summary>Starts the task (silent — no UAC) and waits for it to finish (up to ~70 s).</summary>
    private static void RunTaskAndWait()
    {
        var script =
            "$ErrorActionPreference='SilentlyContinue'\n" +
            $"Start-ScheduledTask -TaskName '{TaskName}'\n" +
            "Start-Sleep -Milliseconds 700\n" +
            $"for($i=0;$i -lt 140;$i++){{ if((Get-ScheduledTask -TaskName '{TaskName}').State -ne 'Running'){{break}}; Start-Sleep -Milliseconds 500 }}\n" +
            $"exit (Get-ScheduledTaskInfo -TaskName '{TaskName}').LastTaskResult";
        var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {enc}",
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return;
        if (!p.WaitForExit(90000)) { try { p.Kill(true); } catch { } }
    }

    /// <summary>Returns the folder holding .cik files (the store or its "Cik" subfolder), or null.</summary>
    private static string? FindCikFolder(string cikStore)
    {
        try
        {
            if (Directory.EnumerateFiles(cikStore, "*.cik").Any()) return cikStore;
            var nested = Path.Combine(cikStore, "Cik");
            if (Directory.Exists(nested) && Directory.EnumerateFiles(nested, "*.cik").Any()) return nested;
        }
        catch { }
        return null;
    }

    /// <summary>Resolves CikExtractor.exe from either a direct exe path or a repo/folder root.</summary>
    private static string? ResolveExe(string toolPath)
    {
        try
        {
            if (File.Exists(toolPath) && toolPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return toolPath;
            if (!Directory.Exists(toolPath)) return null;

            // Prefer the newest built CikExtractor.exe under the repo (e.g. bin\Release\...).
            return Directory.EnumerateFiles(toolPath, "CikExtractor.exe", SearchOption.AllDirectories)
                .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
                .FirstOrDefault();
        }
        catch { return null; }
    }
}
