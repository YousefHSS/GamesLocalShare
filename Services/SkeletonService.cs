using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Thin wrapper around the bundled <c>xvdtool</c> (tools\xvdtool\XVDTool.exe) for single-copy storage of
/// Xbox MSIXVC titles.
///
/// <para><b>capture</b>: given the <i>encrypted</i> .msixvc, the installed game folder, and a CIK store
/// (a CikExtractor "Cik" folder), xvdtool decrypts the package <b>on the fly</b> (no full decrypted copy
/// ever hits disk), writes a ~16 MB skeleton (.skl), and self-verifies it rebuilds the package byte-
/// identically. After a verified capture the game is stored as (installed files) + (tiny skeleton).</para>
///
/// <para><b>reconstruct</b>: given a .skl + the installed files, rebuilds the decrypted package U' (pure
/// file I/O). <b>restore</b> goes all the way to the genuine, byte-identical .msixvc: reconstruct, then
/// re-encrypt the data pages and stamp the skeleton's genuine structural region (header + hash tree) back
/// over the front — the header is never rewritten, so nothing is rehashed or resigned.</para>
///
/// <para>Decryption/encryption are done by xvdtool with the user's own CIK on the user's own licensed
/// content; the app never implements the cipher itself.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SkeletonService
{
    private readonly string _toolPath;

    /// <summary>How much CPU capture/reconstruct may use — sets the xvdtool per-chunk throttle and the
    /// child process priority. Updated from <see cref="Models.AppSettings.CaptureCpuLimit"/>.</summary>
    public Models.CaptureCpuLimit CpuLimit { get; set; } = Models.CaptureCpuLimit.Balanced;

    public SkeletonService() => _toolPath = ResolveTool();

    /// <summary>Resolved path of the xvdtool executable the service launches.</summary>
    public string ToolPath => _toolPath;

    /// <summary>
    /// Captures a skeleton straight from the encrypted .msixvc + installed game folder, decrypting on the
    /// fly. <paramref name="cikFolder"/> is a folder of .cik files (e.g. CikExtractor output); xvdtool
    /// auto-selects the one matching the package's key GUID.
    /// </summary>
    public async Task<SkeletonCaptureResult> CaptureAsync(
        string encryptedPackagePath,
        string installPath,
        string skeletonPath,
        string cikFolder,
        Action<string>? onOutput = null,
        bool elevated = false,
        CancellationToken ct = default)
    {
        var resultJson = skeletonPath + ".result.json";
        TryDelete(resultJson);

        var args = new List<string>
        {
            "--capture",
            "--cikfolder", cikFolder,
            "--install", installPath,
            "--skel", skeletonPath,
            "--throttle", CpuLimit.ThrottleMs().ToString(),
            encryptedPackagePath,
        };

        // Watch xvdtool's output for the "key not available" signal so the caller can refresh CIKs and
        // retry. A freshly installed title's content key is provisioned during its install, so a CIK store
        // dumped earlier won't contain it.
        bool keyMissing = false;
        string? missingCik = null;
        Action<string> sink = line =>
        {
            if (line.IndexOf("No XVC key available", StringComparison.OrdinalIgnoreCase) >= 0)
                keyMissing = true;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"Did not find CIK ([0-9a-fA-F-]{36})");
            if (m.Success) { keyMissing = true; missingCik = m.Groups[1].Value; }
            onOutput?.Invoke(line);
        };

        // Xbox install files can be readable only by an elevated (high-integrity) process. When the caller
        // detected unreadable install files, run xvdtool elevated (UAC) so it can dedupe them; results come
        // from the on-disk result.json (an elevated + shell-executed process can't stream stdout).
        int exit = (elevated && !ElevationHelper.IsElevated())
            ? await RunElevatedAsync(args, onOutput, ct)
            : await RunAsync(args, sink, ct);
        var res = ParseCapture(resultJson, exit);
        return keyMissing ? res with { KeyMissing = true, MissingCik = missingCik ?? "" } : res;
    }

    /// <summary>
    /// Rebuilds the decrypted package (U') from a skeleton + the installed files (pure I/O, no keys).
    /// </summary>
    public async Task<SkeletonReconstructResult> ReconstructAsync(
        string skeletonPath,
        string installPath,
        string outPackagePath,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        var resultJson = outPackagePath + ".result.json";
        TryDelete(resultJson);

        var args = new List<string>
        {
            "--reconstruct",
            "--skel", skeletonPath,
            "--install", installPath,
            "-o", outPackagePath,
        };

        int exit = await RunAsync(args, onOutput, ct);
        return ParseReconstruct(resultJson, exit);
    }

    /// <summary>
    /// Re-verifies an existing .skl against the installed files WITHOUT writing U' and without any
    /// decryption (fast integrity re-check). Returns true on a byte-identical rebuild (exit code 0).
    /// </summary>
    public async Task<bool> VerifyAsync(
        string skeletonPath,
        string installPath,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "--reconstruct",
            "--skel", skeletonPath,
            "--install", installPath,
        };
        return await RunAsync(args, onOutput, ct) == 0;
    }

    /// <summary>
    /// Rebuilds the genuine encrypted <c>.msixvc</c> from a skeleton + the installed files in one xvdtool
    /// call: reconstruct U', re-encrypt its data pages with the CIK, and stamp the skeleton's genuine
    /// structural region (header + hash tree) back over the front. xvdtool verifies the result against the
    /// genuine package hash recorded at capture; the outcome is read from the on-disk result.json.
    /// </summary>
    public async Task<SkeletonRestoreResult> RestoreGenuineAsync(
        string skeletonPath,
        string installPath,
        string cikFolder,
        string outPackagePath,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        var resultJson = outPackagePath + ".result.json";
        TryDelete(resultJson);

        var args = new List<string>
        {
            "--restore",
            "--skel", skeletonPath,
            "--install", installPath,
            "--cikfolder", cikFolder,
            "-o", outPackagePath,
        };

        int exit = await RunAsync(args, onOutput, ct);
        return ParseRestore(resultJson, exit, outPackagePath);
    }

    /// <summary>
    /// Full restore to the genuine <c>.msixvc</c> at <paramref name="outPackagePath"/>, byte-identical to the
    /// original (final SHA == the gsha stored in the .skl). This is the package the LAN-cache proxy serves
    /// back to Gaming Services for a Verify/update HIT — no re-download.
    /// <para>The output is written straight to <paramref name="outPackagePath"/>; the caller is responsible
    /// for staging (e.g. writing to a temp path then moving into the served cache path).</para>
    /// <para>Alias for <see cref="RestoreGenuineAsync"/>, kept for callers.</para>
    /// </summary>
    public Task<SkeletonRestoreResult> RestoreToPackageAsync(
        string skeletonPath,
        string installPath,
        string cikFolder,
        string outPackagePath,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
        => RestoreGenuineAsync(skeletonPath, installPath, cikFolder, outPackagePath, onOutput, ct);

    /// <summary>Runs xvdtool elevated via the UAC "runas" verb (so it can read high-integrity Xbox install
    /// files). Shell-execute is required for runas, which precludes stdout redirection, so progress isn't
    /// streamed; the outcome is read from the on-disk result.json by the caller. Returns the exit code, or
    /// -1 when the prompt was declined or the launch failed.</summary>
    private async Task<int> RunElevatedAsync(IReadOnlyList<string> args, Action<string>? onOutput, CancellationToken ct)
    {
        onOutput?.Invoke("reading protected install files needs elevation — accept the UAC prompt …");
        var psi = new ProcessStartInfo
        {
            FileName = _toolPath,
            UseShellExecute = true, // required for the runas verb
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(_toolPath) ?? AppContext.BaseDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) { onOutput?.Invoke("elevated capture: failed to start"); return -1; }
            using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            // Win32 1223 = user declined the UAC prompt.
            onOutput?.Invoke($"elevated capture did not run: {ex.Message}");
            return -1;
        }
    }

    private async Task<int> RunAsync(IReadOnlyList<string> args, Action<string>? onOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _toolPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_toolPath) ?? AppContext.BaseDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput?.Invoke(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onOutput?.Invoke(e.Data); };

        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start xvdtool ({_toolPath})");

        // Deprioritise the CPU-heavy capture/reconstruct so the machine stays responsive.
        try { proc.PriorityClass = CpuLimit.Priority(); } catch { }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private static SkeletonCaptureResult ParseCapture(string jsonPath, int exit)
    {
        if (!File.Exists(jsonPath))
            return new SkeletonCaptureResult { Ok = false, ExitCode = exit };
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var r = doc.RootElement;
            return new SkeletonCaptureResult
            {
                Ok = GetBool(r, "ok"),
                Verified = GetBool(r, "verified"),
                USize = GetLong(r, "uSize"),
                USha = GetStr(r, "uSha"),
                FilesMatched = (int)GetLong(r, "filesMatched"),
                FoundBytes = GetLong(r, "foundBytes"),
                SkeletonBytes = GetLong(r, "skeletonBytes"),
                SkelFileSize = GetLong(r, "skelFileSize"),
                SkelPath = GetStr(r, "skelPath"),
                ExitCode = exit,
            };
        }
        catch
        {
            return new SkeletonCaptureResult { Ok = false, ExitCode = exit };
        }
    }

    private static SkeletonReconstructResult ParseReconstruct(string jsonPath, int exit)
    {
        if (!File.Exists(jsonPath))
            return new SkeletonReconstructResult { Ok = false, ExitCode = exit };
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var r = doc.RootElement;
            return new SkeletonReconstructResult
            {
                Ok = GetBool(r, "ok"),
                Identical = GetBool(r, "identical"),
                USize = GetLong(r, "uSize"),
                OutSha = GetStr(r, "outSha"),
                ExpectedSha = GetStr(r, "expectedSha"),
                Gsha = GetStr(r, "gsha"),
                OutPath = GetStr(r, "outPath"),
                ExitCode = exit,
            };
        }
        catch
        {
            return new SkeletonReconstructResult { Ok = false, ExitCode = exit };
        }
    }

    /// <summary>Reads the <c>--restore</c> outcome. xvdtool writes the same result.json for a failure before
    /// the rebuild (v1 skeleton, missing gsha, structural size mismatch) with an <c>error</c> string, so a
    /// missing file means the process died before it could report anything.</summary>
    private static SkeletonRestoreResult ParseRestore(string jsonPath, int exit, string outPackagePath)
    {
        if (!File.Exists(jsonPath))
            return new SkeletonRestoreResult
            {
                OutPath = outPackagePath,
                Error = $"xvdtool did not report a restore result (exit {exit})",
            };
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var r = doc.RootElement;
            bool ok = GetBool(r, "ok");
            var err = GetStr(r, "error");
            return new SkeletonRestoreResult
            {
                Ok = ok,
                ReconstructIdentical = ok,
                Encrypted = ok,
                OutSha = GetStr(r, "outSha"),
                Gsha = GetStr(r, "gsha"),
                PackageBytes = GetLong(r, "uSize"),
                OutPath = GetStr(r, "outPath") is { Length: > 0 } p ? p : outPackagePath,
                Error = ok ? null
                    : err.Length > 0 ? err
                    : "restored package is not byte-identical to the genuine package (gsha)",
            };
        }
        catch
        {
            return new SkeletonRestoreResult
            {
                OutPath = outPackagePath,
                Error = $"could not read the restore result (exit {exit})",
            };
        }
    }

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : 0;

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string ResolveTool()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "tools", "xvdtool", "XVDTool.exe");
        if (File.Exists(beside)) return beside;

        // Dev fallback: walk up to the in-repo bundled tool.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var devPath = Path.Combine(dir.FullName, "tools", "xvdtool", "XVDTool.exe");
            if (File.Exists(devPath)) return devPath;
            dir = dir.Parent;
        }

        return beside; // let the launch fail with a clear "file not found"
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}

/// <summary>Outcome of an xvdtool capture (parsed from &lt;skel&gt;.result.json).</summary>
public sealed record SkeletonCaptureResult
{
    /// <summary>True only when xvdtool captured AND self-verified byte-identical.</summary>
    public bool Ok { get; init; }
    public bool Verified { get; init; }
    public long USize { get; init; }
    public string USha { get; init; } = "";
    public int FilesMatched { get; init; }
    public long FoundBytes { get; init; }
    public long SkeletonBytes { get; init; }
    public long SkelFileSize { get; init; }
    public string SkelPath { get; init; } = "";
    public int ExitCode { get; init; }
    /// <summary>True when capture failed because the package's content key wasn't in the CIK store
    /// (caller should refresh CIKs via CikExtractor and retry).</summary>
    public bool KeyMissing { get; init; }
    /// <summary>The specific CIK GUID xvdtool reported missing, when known.</summary>
    public string MissingCik { get; init; } = "";
}

/// <summary>Outcome of an xvdtool reconstruct (parsed from &lt;out&gt;.result.json).</summary>
public sealed record SkeletonReconstructResult
{
    /// <summary>True only when U' is byte-identical to the original U.</summary>
    public bool Ok { get; init; }
    public bool Identical { get; init; }
    public long USize { get; init; }
    public string OutSha { get; init; } = "";
    public string ExpectedSha { get; init; } = "";
    /// <summary>SHA-256 of the genuine ENCRYPTED package (.msixvc), stored in the .skl at capture.</summary>
    public string Gsha { get; init; } = "";
    public string OutPath { get; init; } = "";
    public int ExitCode { get; init; }
}

/// <summary>
/// Outcome of a restore-to-package: rebuild U' from the skeleton, re-encrypt it to the genuine .msixvc,
/// and confirm the result is byte-identical to the original package (final SHA == the .skl's gsha).
/// </summary>
public sealed record SkeletonRestoreResult
{
    /// <summary>True only when the re-encrypted package SHA matches the genuine package SHA (gsha).</summary>
    public bool Ok { get; init; }
    /// <summary>True if U' rebuilt byte-identically (before re-encryption).</summary>
    public bool ReconstructIdentical { get; init; }
    /// <summary>True if the in-place re-encryption (-ee -pdu) succeeded.</summary>
    public bool Encrypted { get; init; }
    /// <summary>SHA-256 of the produced .msixvc.</summary>
    public string OutSha { get; init; } = "";
    /// <summary>Expected genuine-package SHA-256 (from the .skl).</summary>
    public string Gsha { get; init; } = "";
    public long PackageBytes { get; init; }
    public string OutPath { get; init; } = "";
    /// <summary>Set when a step failed; null on success.</summary>
    public string? Error { get; init; }
}
