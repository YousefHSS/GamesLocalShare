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
