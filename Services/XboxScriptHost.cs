using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace GamesLocalShare.Services;

/// <summary>
/// Deploys and runs the proven Xbox MSIXVC transfer PowerShell scripts.
///
/// The scripts ship in the build output under xbox-scripts\ (see the csproj
/// Content item). They write runs\ and tools\ next to themselves, so they
/// cannot run from a read-only install location. This host copies them to a
/// writable per-user folder and launches them from there.
///
/// The scripts self-elevate via Assert-Elevated: if the host process is NOT
/// already elevated, the real work detaches into a UAC-spawned process whose
/// stdout cannot be captured. Callers must therefore ensure the app is
/// elevated (ElevationHelper.IsElevated) before invoking a script.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class XboxScriptHost
{
    private static readonly string[] ScriptFiles =
    {
        "_common.ps1",
        "xbox-transfer-sender.ps1",
        "xbox-transfer-receiver-overlay.ps1",
    };

    /// <summary>Writable folder the scripts are deployed to and run from.</summary>
    public string WorkDir { get; }

    /// <summary>Folder where the scripts write run logs and verdict JSON.</summary>
    public string RunsDir => Path.Combine(WorkDir, "runs");

    public string SenderScript => Path.Combine(WorkDir, "xbox-transfer-sender.ps1");
    public string ReceiverScript => Path.Combine(WorkDir, "xbox-transfer-receiver-overlay.ps1");

    private XboxScriptHost(string workDir) => WorkDir = workDir;

    /// <summary>
    /// Copies the bundled scripts to %LOCALAPPDATA%\GamesLocalShare\xbox-transfer
    /// (overwriting older copies) and returns a host bound to that folder.
    /// </summary>
    public static XboxScriptHost Deploy()
    {
        var workDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GamesLocalShare", "xbox-transfer");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(Path.Combine(workDir, "runs"));
        Directory.CreateDirectory(Path.Combine(workDir, "tools"));

        var sourceDir = ResolveSourceDir();
        foreach (var name in ScriptFiles)
        {
            var src = Path.Combine(sourceDir, name);
            if (!File.Exists(src))
                throw new FileNotFoundException($"Bundled Xbox script not found: {src}");

            var dst = Path.Combine(workDir, name);
            if (!File.Exists(dst) ||
                File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dst))
            {
                File.Copy(src, dst, overwrite: true);
            }
        }

        return new XboxScriptHost(workDir);
    }

    /// <summary>
    /// Locates the bundled xbox-scripts folder. Normally next to the executable;
    /// falls back to the in-repo PLANNING path for development runs.
    /// </summary>
    private static string ResolveSourceDir()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "xbox-scripts");
        if (Directory.Exists(beside) &&
            File.Exists(Path.Combine(beside, "_common.ps1")))
            return beside;

        // Dev fallback: walk up looking for the PLANNING source of truth.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var planning = Path.Combine(dir.FullName,
                "PLANNING", "xbox-validation", "auto");
            if (File.Exists(Path.Combine(planning, "_common.ps1")))
                return planning;
            dir = dir.Parent;
        }

        return beside; // let Deploy() throw a clear FileNotFoundException
    }

    /// <summary>
    /// Launches a script with powershell.exe, streaming stdout and stderr
    /// line-by-line to <paramref name="onOutput"/>. Returns the exit code.
    /// </summary>
    /// <param name="confirmPausePrompt">
    /// When true, a newline is written to stdin so the receiver script's
    /// "Press Enter when the install is paused" Read-Host returns immediately.
    /// </param>
    public async Task<int> RunAsync(
        string scriptPath,
        IReadOnlyList<string> args,
        Action<string> onOutput,
        bool confirmPausePrompt = false,
        CancellationToken ct = default,
        string? cancelSentinelName = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = WorkDir,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                onOutput(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                onOutput(e.Data);
        };

        string? sentinelPath = null;
        if (!string.IsNullOrEmpty(cancelSentinelName))
        {
            sentinelPath = Path.Combine(RunsDir, cancelSentinelName);
            try
            {
                Directory.CreateDirectory(RunsDir);
                if (File.Exists(sentinelPath)) File.Delete(sentinelPath);
            }
            catch { }
        }

        if (!proc.Start())
            throw new InvalidOperationException("Failed to start powershell.exe");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // The receiver script blocks on Read-Host until it gets a line. The
        // caller has already had the user confirm the pause in the UI.
        if (confirmPausePrompt)
        {
            try { await proc.StandardInput.WriteLineAsync(); } catch { }
        }
        try { proc.StandardInput.Close(); } catch { }

        using var reg = ct.Register(() =>
        {
            // Sentinel first so the SYSTEM child (unkillable from an
            // unelevated process) self-terminates; then kill the visible tree.
            if (sentinelPath != null)
            {
                try { File.WriteAllText(sentinelPath, DateTime.UtcNow.ToString("o")); }
                catch { }
            }
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { }
        });

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    /// <summary>
    /// Launches a script in a visible PowerShell console window with no
    /// output redirection. The user sees all output (Write-Host, colors,
    /// progress) exactly as they would running the script manually.
    /// Uses -AutoConfirm to skip the Read-Host pause prompt.
    /// Returns the exit code.
    /// </summary>
    public async Task<int> RunVisibleAsync(
        string scriptPath,
        IReadOnlyList<string> args,
        CancellationToken ct = default,
        string? cancelSentinelName = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = WorkDir,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        // Clear any stale sentinel from a previous run before we launch,
        // otherwise the new SYSTEM child would see it and exit immediately.
        string? sentinelPath = null;
        if (!string.IsNullOrEmpty(cancelSentinelName))
        {
            sentinelPath = Path.Combine(RunsDir, cancelSentinelName);
            try
            {
                Directory.CreateDirectory(RunsDir);
                if (File.Exists(sentinelPath)) File.Delete(sentinelPath);
            }
            catch { }
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!proc.Start())
            throw new InvalidOperationException("Failed to start powershell.exe");

        using var reg = ct.Register(() =>
        {
            // Drop the sentinel FIRST so the SYSTEM child (which we cannot
            // kill from an unelevated process) self-terminates. Then kill
            // the visible powershell tree we own directly.
            if (sentinelPath != null)
            {
                try { File.WriteAllText(sentinelPath, DateTime.UtcNow.ToString("o")); }
                catch { }
            }
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { }
        });

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    /// <summary>
    /// Returns the most recently written receiver overlay verdict JSON file,
    /// or null if none exists. Used to recover the verdict after a run.
    /// </summary>
    public string? LatestVerdictFile()
    {
        if (!Directory.Exists(RunsDir))
            return null;
        return Directory.EnumerateFiles(RunsDir, "receiver-overlay-verdict-*.json")
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .Select(fi => fi.FullName)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns the last few lines of the most recent SYSTEM-phase error/log for
    /// scripts matching <paramref name="logPrefix"/> (e.g. "receiver-overlay").
    /// The SYSTEM child's stderr goes to a .err file that is not streamed live,
    /// so this is how the real cause of a generic failure is recovered.
    /// </summary>
    public string? LatestSystemError(string logPrefix, int maxLines = 15)
    {
        if (!Directory.Exists(RunsDir))
            return null;

        FileInfo? Newest(string pattern) =>
            Directory.EnumerateFiles(RunsDir, pattern)
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Length > 0)
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();

        // Prefer the .err file; fall back to the tail of the stdout log.
        var file = Newest($"{logPrefix}-system-*.log.err")
                   ?? Newest($"{logPrefix}-system-*.log");
        if (file == null)
            return null;

        try
        {
            var lines = File.ReadLines(file.FullName)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            return lines.Count == 0 ? null : string.Join("\n", lines.TakeLast(maxLines));
        }
        catch { return null; }
    }
}
