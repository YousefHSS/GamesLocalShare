using System.IO;

namespace GamesLocalShare.Services;

public class LocalFileTransport : IFileTransport
{
    public string Name => "Local";

    public Task<Stream> OpenReadAsync(string basePath, string relativePath, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(basePath, relativePath);
        Stream s = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Task.FromResult(s);
    }

    public async Task<Stream> OpenWriteAsync(string basePath, string relativePath, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(basePath, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await Task.CompletedTask;
        Stream s = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return s;
    }

    public Task SetLastWriteTimeUtcAsync(string basePath, string relativePath, DateTime time)
    {
        var fullPath = Path.Combine(basePath, relativePath);
        File.SetLastWriteTimeUtc(fullPath, time);
        return Task.CompletedTask;
    }
}
