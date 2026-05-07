using System.IO;

namespace GamesLocalShare.Tests.Helpers;

/// <summary>
/// Disposable temp directory for tests that need real filesystem I/O.
/// Cleans itself up on dispose, even if Files inside are read-only.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory(string? prefix = null)
    {
        var name = (prefix ?? "gls-test") + "-" + Guid.NewGuid().ToString("N")[..8];
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
        Directory.CreateDirectory(Path);
    }

    public string CreateSubdir(string relative)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(p);
        return p;
    }

    public string WriteFile(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        var dir = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
        return full;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        var dir = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Path)) return;
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(Path, recursive: true);
        }
        catch { }
    }
}
