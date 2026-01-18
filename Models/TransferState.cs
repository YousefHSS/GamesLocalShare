using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamesLocalShare.Models;

/// <summary>
/// JSON serialization context for TransferState
/// </summary>
[JsonSerializable(typeof(TransferState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class TransferStateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Represents the state of an in-progress or incomplete transfer
/// </summary>
public class TransferState
{
    /// <summary>
    /// Game AppId being transferred
    /// </summary>
    public string GameAppId { get; set; } = string.Empty;

    /// <summary>
    /// Game name for display
    /// </summary>
    public string GameName { get; set; } = string.Empty;

    /// <summary>
    /// Target installation path
    /// </summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Source peer IP address
    /// </summary>
    public string SourcePeerIp { get; set; } = string.Empty;

    /// <summary>
    /// Source peer name
    /// </summary>
    public string SourcePeerName { get; set; } = string.Empty;

    /// <summary>
    /// Build ID being downloaded
    /// </summary>
    public string BuildId { get; set; } = string.Empty;

    /// <summary>
    /// Total size in bytes
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Bytes already transferred
    /// </summary>
    public long TransferredBytes { get; set; }

    /// <summary>
    /// List of files that have been completely downloaded
    /// </summary>
    public List<string> CompletedFiles { get; set; } = [];

    /// <summary>
    /// Files that still need to be downloaded
    /// </summary>
    public List<TransferFileState> PendingFiles { get; set; } = [];

    /// <summary>
    /// When the transfer was started
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// When the transfer was last updated
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.Now;

    /// <summary>
    /// Whether this is a new download or an update
    /// </summary>
    public bool IsNewDownload { get; set; }

    /// <summary>
    /// Progress percentage
    /// </summary>
    public double ProgressPercent => TotalBytes > 0 ? (double)TransferredBytes / TotalBytes * 100 : 0;

    /// <summary>
    /// Formatted progress
    /// </summary>
    public string FormattedProgress => $"{ProgressPercent:0.0}% ({FormatBytes(TransferredBytes)} / {FormatBytes(TotalBytes)})";

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Gets the state file path for a game
    /// </summary>
    public static string GetStateFilePath(string targetPath)
    {
        return Path.Combine(targetPath, ".gamesync_transfer");
    }

    /// <summary>
    /// Saves the transfer state to disk
    /// </summary>
    public void Save()
    {
        try
        {
            LastUpdated = DateTime.Now;
            var stateFile = GetStateFilePath(TargetPath);
            var directory = Path.GetDirectoryName(stateFile);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(this, TransferStateJsonContext.Default.TransferState);
            File.WriteAllText(stateFile, json);
            System.Diagnostics.Debug.WriteLine($"TransferState.Save: Saved state for {GameName} at {stateFile}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving transfer state: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads a transfer state from disk
    /// </summary>
    public static TransferState? Load(string targetPath)
    {
        try
        {
            var stateFile = GetStateFilePath(targetPath);
            if (File.Exists(stateFile))
            {
                var json = File.ReadAllText(stateFile);
                return JsonSerializer.Deserialize(json, TransferStateJsonContext.Default.TransferState);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading transfer state: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Deletes the transfer state file (called when transfer completes)
    /// </summary>
    public void Delete()
    {
        try
        {
            var stateFile = GetStateFilePath(TargetPath);
            if (File.Exists(stateFile))
            {
                File.Delete(stateFile);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error deleting transfer state: {ex.Message}");
        }
    }
}

/// <summary>
/// State of a single file in the transfer
/// </summary>
public class TransferFileState
{
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public long BytesTransferred { get; set; }
    public string Hash { get; set; } = string.Empty;
    public bool IsComplete => BytesTransferred >= Size;
}
