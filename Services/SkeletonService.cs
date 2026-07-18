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
/// file I/O). <b>encrypt</b> re-encrypts U' back into the genuine, byte-identical .msixvc.</para>
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

        int exit = await RunAsync(args, sink, ct);
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
    /// Re-encrypts a reconstructed package U' back into the genuine .msixvc (in place), using a CIK store.
    /// This is the final restore step after <see cref="ReconstructAsync"/>.
    /// </summary>
    public async Task<bool> EncryptAsync(
        string packagePath,
        string cikFolder,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-ee", "-pdu",
            "--cikfolder", cikFolder,
            packagePath,
        };
        return await RunAsync(args, onOutput, ct) == 0;
    }

    /// <summary>
    /// Full restore: rebuilds U' from the skeleton + installed files, re-encrypts it in place into the
    /// genuine <c>.msixvc</c> at <paramref name="outPackagePath"/>, and confirms the result is byte-identical
    /// to the original package (final SHA == the gsha stored in the .skl). This is the package the LAN-cache
    /// proxy serves back to Gaming Services for a Verify/update HIT — no re-download.
    /// <para>The output is written straight to <paramref name="outPackagePath"/>; the caller is responsible
    /// for staging (e.g. writing to a temp path then moving into the served cache path).</para>
    /// </summary>
    public async Task<SkeletonRestoreResult> RestoreToPackageAsync(
        string skeletonPath,
        string installPath,
        string cikFolder,
        string outPackagePath,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        // 1) skeleton + install -> U' (pure I/O, SHA-checked vs the .skl's usha by xvdtool).
        var rec = await ReconstructAsync(skeletonPath, installPath, outPackagePath, onOutput, ct);
        if (!rec.Ok || !rec.Identical)
        {
            return new SkeletonRestoreResult
            {
                ReconstructIdentical = rec.Identical,
                Gsha = rec.Gsha,
                PackageBytes = rec.USize,
                OutPath = outPackagePath,
                Error = $"reconstruct did not rebuild U byte-identically (exit {rec.ExitCode})",
            };
        }

        // 2) U' -> genuine encrypted .msixvc, in place.
        bool enc = await EncryptAsync(outPackagePath, cikFolder, onOutput, ct);
        if (!enc)
        {
            return new SkeletonRestoreResult
            {
                ReconstructIdentical = true,
                Encrypted = false,
                Gsha = rec.Gsha,
                PackageBytes = rec.USize,
                OutPath = outPackagePath,
                Error = "re-encryption (-ee -pdu) failed",
            };
        }

        // 3) Confirm the produced package equals the genuine one (gsha from the .skl).
        string outSha = await Task.Run(() => Sha256File(outPackagePath), ct);
        bool match = !string.IsNullOrEmpty(rec.Gsha)
                     && outSha.Equals(rec.Gsha, StringComparison.OrdinalIgnoreCase);

        return new SkeletonRestoreResult
        {
            Ok = match,
            ReconstructIdentical = true,
            Encrypted = true,
            OutSha = outSha,
            Gsha = rec.Gsha,
            PackageBytes = rec.USize,
            OutPath = outPackagePath,
            Error = match ? null
                : string.IsNullOrEmpty(rec.Gsha)
                    ? "skeleton has no genuine-package hash (gsha) to verify against"
                    : "restored package SHA does not match the genuine package (gsha)",
        };
    }

    private static string Sha256File(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
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
