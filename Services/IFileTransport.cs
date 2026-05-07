namespace GamesLocalShare.Services;

public interface IFileTransport
{
    string Name { get; }
    Task<Stream> OpenReadAsync(string basePath, string relativePath, CancellationToken ct = default);
    Task<Stream> OpenWriteAsync(string basePath, string relativePath, CancellationToken ct = default);
    Task SetLastWriteTimeUtcAsync(string basePath, string relativePath, DateTime time);
}
