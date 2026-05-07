using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

public class IncrementalSyncEngine
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    public event EventHandler<string>? LogMessageRaised;
    public event EventHandler<TransferProgressEventArgs>? ProgressChanged;

    public async Task<FileManifest> BuildFileManifestAsync(string gamePath, Action<string, int, int>? onFileHashed = null)
    {
        var manifest = new FileManifest { GamePath = gamePath };
        await Task.Run(() =>
        {
            var dirInfo = new DirectoryInfo(gamePath);
            var allFiles = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => f.Name is not ".gamesync_transfer" and not ".gamesync_meta")
                .ToList();
            for (int i = 0; i < allFiles.Count; i++)
            {
                var file = allFiles[i];
                try
                {
                    var rel = Path.GetRelativePath(gamePath, file.FullName);
                    manifest.Files.Add(new FileTransferInfo
                    {
                        RelativePath = rel,
                        Size = file.Length,
                        LastModified = file.LastWriteTimeUtc,
                        Hash = ComputeQuickHash(file.FullName, file.Length)
                    });
                    onFileHashed?.Invoke(rel, i + 1, allFiles.Count);
                }
                catch { }
            }
        }).ConfigureAwait(false);
        return manifest;
    }

    public async Task<List<FileTransferInfo>> GetFilesToSyncAsync(string destPath, List<FileTransferInfo> sourceFiles)
    {
        var result = new List<FileTransferInfo>();
        int hashChecks = 0, skippedTimestamp = 0, skippedHash = 0, mtimeRepaired = 0;
        int dlMissing = 0, dlSize = 0, dlHash = 0, dlNoHash = 0;

        await Task.Run(() =>
        {
            foreach (var src in sourceFiles)
            {
                var destFile = Path.Combine(destPath, src.RelativePath);
                if (!File.Exists(destFile)) { dlMissing++; result.Add(src); continue; }

                var info = new FileInfo(destFile);
                if (info.Length != src.Size) { dlSize++; result.Add(src); continue; }

                var delta = info.LastWriteTimeUtc - src.LastModified;
                if (delta.Duration() <= TimestampTolerance) { skippedTimestamp++; continue; }

                if (!string.IsNullOrEmpty(src.Hash))
                {
                    hashChecks++;
                    var localHash = ComputeQuickHash(destFile, info.Length);
                    if (string.Equals(localHash, src.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        skippedHash++;
                        if (TryRepairMtime(destFile, src.LastModified)) mtimeRepaired++;
                    }
                    else { dlHash++; result.Add(src); }
                    continue;
                }

                // Source hash unavailable — hash the destination to confirm it's present and readable.
                // If dest is hashable, the file is likely intact; skip rather than re-copy.
                var destHash = ComputeQuickHash(destFile, info.Length);
                if (!string.IsNullOrEmpty(destHash))
                {
                    skippedTimestamp++;
                    if (TryRepairMtime(destFile, src.LastModified)) mtimeRepaired++;
                    continue;
                }
                dlNoHash++; result.Add(src);
            }
        }).ConfigureAwait(false);

        var summary = $"Incremental sync analysis: total={sourceFiles.Count}, hashChecks={hashChecks}, " +
                      $"skip[timestamp={skippedTimestamp},hashMatch={skippedHash}], " +
                      $"download[missing={dlMissing},sizeDiff={dlSize},hashDiff={dlHash},noHash={dlNoHash}]" +
                      (mtimeRepaired > 0 ? $", mtimeRepaired={mtimeRepaired}" : "") +
                      $", total to transfer={result.Count}";
        System.Diagnostics.Debug.WriteLine(summary);
        LogMessageRaised?.Invoke(this, summary);
        return result;
    }

    private static bool TryRepairMtime(string path, DateTime sourceMtime)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, sourceMtime);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<string>> VerifyCompletedFilesAsync(string destPath, List<FileTransferInfo> sourceFiles, TransferState state)
    {
        var corrupted = new List<string>();
        var byPath = sourceFiles.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var snapshot = state.CompletedFiles.ToList();

        await Task.Run(() =>
        {
            foreach (var rel in snapshot)
            {
                if (!byPath.TryGetValue(rel, out var src)) { corrupted.Add(rel); continue; }
                var full = Path.Combine(destPath, rel);
                if (!File.Exists(full)) { corrupted.Add(rel); continue; }
                var fi = new FileInfo(full);
                if (fi.Length != src.Size) { corrupted.Add(rel); continue; }
                if (string.IsNullOrEmpty(src.Hash)) continue;
                var h = ComputeQuickHash(full, fi.Length);
                if (!string.Equals(h, src.Hash, StringComparison.OrdinalIgnoreCase)) corrupted.Add(rel);
            }
        }).ConfigureAwait(false);

        return corrupted;
    }

    public async Task<bool> CopyGameAsync(
        string sourcePath,
        string destPath,
        GameInfo gameInfo,
        IFileTransport transport,
        TransferState? resumeState,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destPath);

        // Tag the destination with the source version up front. If the transfer is later
        // interrupted, scans will still see this folder as the version we're syncing toward
        // (preventing a partial copy from appearing "newer" than the source by folder mtime).
        WriteSyncMeta(destPath, gameInfo);

        var lastHashReport = DateTime.UtcNow;
        var manifest = await BuildFileManifestAsync(sourcePath, (rel, done, total) =>
        {
            var now = DateTime.UtcNow;
            if ((now - lastHashReport).TotalMilliseconds < 100 && done != total) return;
            lastHashReport = now;
            ProgressChanged?.Invoke(this, new TransferProgressEventArgs
            {
                GameAppId = gameInfo.AppId,
                Progress = 0,
                TransferredBytes = 0,
                TotalBytes = 0,
                SpeedBytesPerSecond = 0,
                CurrentFile = $"Analyzing source ({done}/{total}): {rel}",
            });
        }).ConfigureAwait(false);

        TransferState state;
        if (resumeState != null)
        {
            state = resumeState;
            var corrupted = await VerifyCompletedFilesAsync(destPath, manifest.Files, state).ConfigureAwait(false);
            foreach (var c in corrupted) state.CompletedFiles.Remove(c);
        }
        else
        {
            state = new TransferState
            {
                GameAppId = gameInfo.AppId,
                GameName = gameInfo.Name,
                TargetPath = destPath,
                SourcePeerIp = "local",
                SourcePeerName = "Local Device",
                BuildId = gameInfo.BuildId,
                TotalBytes = manifest.Files.Sum(f => f.Size),
                IsNewDownload = true,
            };
        }

        ProgressChanged?.Invoke(this, new TransferProgressEventArgs
        {
            GameAppId = gameInfo.AppId,
            Progress = 0,
            TransferredBytes = 0,
            TotalBytes = 0,
            SpeedBytesPerSecond = 0,
            CurrentFile = "Comparing destination files...",
        });

        var toSync = await GetFilesToSyncAsync(destPath, manifest.Files).ConfigureAwait(false);

        // Progress reflects this session's work (the incremental delta), not the whole game,
        // so the bar reads 0→100% across what's actually being copied right now.
        long sessionTransferred = 0;
        long sessionTotal = toSync.Sum(f => f.Size);
        long persistentTransferred = state.TransferredBytes;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long lastSpeedSampleBytes = 0;
        var lastSpeedSampleTime = stopwatch.Elapsed;
        long lastSpeed = 0;

        foreach (var file in toSync)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Streams must be fully disposed before SetLastWriteTimeUtc — Windows
                // can't update mtime while the dest file is still open with FileShare.None.
                // Use an inner block so `dst` releases its handle before the metadata call.
                await using (var src = await transport.OpenReadAsync(sourcePath, file.RelativePath, ct).ConfigureAwait(false))
                await using (var dst = await transport.OpenWriteAsync(destPath, file.RelativePath, ct).ConfigureAwait(false))
                {
                    var buffer = new byte[256 * 1024];
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        sessionTransferred += read;

                        var now = stopwatch.Elapsed;
                        var sampleDelta = now - lastSpeedSampleTime;
                        if (sampleDelta.TotalMilliseconds >= 500)
                        {
                            lastSpeed = (long)((sessionTransferred - lastSpeedSampleBytes) / sampleDelta.TotalSeconds);
                            lastSpeedSampleBytes = sessionTransferred;
                            lastSpeedSampleTime = now;
                        }

                        var remaining = Math.Max(0, sessionTotal - sessionTransferred);
                        var eta = lastSpeed > 0 ? TimeSpan.FromSeconds((double)remaining / lastSpeed) : TimeSpan.Zero;

                        ProgressChanged?.Invoke(this, new TransferProgressEventArgs
                        {
                            GameAppId = gameInfo.AppId,
                            Progress = sessionTotal > 0 ? (double)sessionTransferred / sessionTotal * 100 : 0,
                            TransferredBytes = sessionTransferred,
                            TotalBytes = sessionTotal,
                            SpeedBytesPerSecond = lastSpeed,
                            CurrentFile = file.RelativePath,
                            EstimatedTimeRemaining = eta,
                        });
                    }

                    await dst.FlushAsync(ct).ConfigureAwait(false);
                }

                try
                {
                    await transport.SetLastWriteTimeUtcAsync(destPath, file.RelativePath, file.LastModified).ConfigureAwait(false);
                }
                catch (Exception mtimeEx)
                {
                    LogMessageRaised?.Invoke(this, $"Could not stamp mtime on {file.RelativePath}: {mtimeEx.Message} (next sync will fall back to hashing)");
                }

                state.CompletedFiles.Add(file.RelativePath);
                state.TransferredBytes = persistentTransferred + sessionTransferred;
                state.Save();
            }
            catch (OperationCanceledException) { state.Save(); throw; }
            catch (Exception ex)
            {
                LogMessageRaised?.Invoke(this, $"Failed to copy {file.RelativePath}: {ex.Message}");
            }
        }

        var stillCorrupted = await VerifyCompletedFilesAsync(destPath, manifest.Files, state).ConfigureAwait(false);
        if (stillCorrupted.Count == 0)
        {
            var stateFile = Path.Combine(destPath, ".gamesync_transfer");
            if (File.Exists(stateFile)) File.Delete(stateFile);

            // Refresh sidecar (in case mtime/buildId changed during the transfer window).
            WriteSyncMeta(destPath, gameInfo);

            LogMessageRaised?.Invoke(this, $"Local copy complete: {gameInfo.Name} → {destPath}");
            return true;
        }

        LogMessageRaised?.Invoke(this, $"Copy completed with {stillCorrupted.Count} corrupted files for {gameInfo.Name}");
        return false;
    }

    private static void WriteSyncMeta(string destPath, GameInfo gameInfo)
    {
        try
        {
            var meta = new
            {
                appId = gameInfo.AppId,
                buildId = gameInfo.BuildId,
                lastUpdated = gameInfo.LastUpdated.ToUniversalTime().ToString("O"),
                platform = gameInfo.Platform.ToString(),
            };
            File.WriteAllText(
                Path.Combine(destPath, ".gamesync_meta"),
                JsonSerializer.Serialize(meta));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to write .gamesync_meta: {ex.Message}");
        }
    }

    public static string ComputeQuickHash(string filePath, long fileSize)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var md5 = MD5.Create();
            if (fileSize <= 2 * 1024 * 1024)
                return Convert.ToHexString(md5.ComputeHash(stream));

            const int SampleSize = 1024 * 1024;
            var buffer = new byte[SampleSize];
            ReadFully(stream, buffer, SampleSize);
            md5.TransformBlock(buffer, 0, SampleSize, buffer, 0);

            var midOffset = Math.Clamp(fileSize / 2 - SampleSize / 2, SampleSize, fileSize - SampleSize);
            stream.Seek(midOffset, SeekOrigin.Begin);
            ReadFully(stream, buffer, SampleSize);
            md5.TransformBlock(buffer, 0, SampleSize, buffer, 0);

            stream.Seek(-SampleSize, SeekOrigin.End);
            ReadFully(stream, buffer, SampleSize);
            md5.TransformBlock(buffer, 0, SampleSize, buffer, 0);

            var sizeBytes = BitConverter.GetBytes(fileSize);
            md5.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);
            return Convert.ToHexString(md5.Hash!);
        }
        catch { return string.Empty; }
    }

    private static void ReadFully(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n == 0) break;
            read += n;
        }
    }
}
