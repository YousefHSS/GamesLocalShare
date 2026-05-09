using System.IO;
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

public static class SteamManifestParser
{
    public static GameInfo? ParseAppManifest(string manifestPath, string steamappsFolder, IList<string>? errors = null)
    {
        try
        {
            var content = File.ReadAllText(manifestPath);
            var vdf = VdfConvert.Deserialize(content);
            var appState = vdf.Value;

            var appId = appState["appid"]?.ToString();
            var name = appState["name"]?.ToString();
            var installDir = appState["installdir"]?.ToString();
            var buildId = appState["buildid"]?.ToString();
            var sizeOnDiskStr = appState["SizeOnDisk"]?.ToString();
            var lastUpdatedStr = appState["LastUpdated"]?.ToString();
            var stateFlags = appState["StateFlags"]?.ToString();

            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name))
                return null;

            var installPath = Path.Combine(steamappsFolder, "common", installDir ?? name);
            if (!Directory.Exists(installPath))
            {
                errors?.Add($"Skipping {name} ({appId}) - install dir not found: {installPath}");
                return null;
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(installPath).Any())
                {
                    errors?.Add($"Skipping {name} ({appId}) - install dir is empty");
                    return null;
                }
            }
            catch (Exception ex)
            {
                errors?.Add($"Skipping {name} ({appId}) - cannot access dir: {ex.Message}");
                return null;
            }

            if (!string.IsNullOrEmpty(stateFlags) && int.TryParse(stateFlags, out int flags) && flags == 0)
            {
                errors?.Add($"Skipping {name} ({appId}) - StateFlags=0 (not installed)");
                return null;
            }

            long.TryParse(sizeOnDiskStr, out long sizeOnDisk);
            DateTime lastUpdated = DateTime.MinValue;
            if (long.TryParse(lastUpdatedStr, out long unix))
                lastUpdated = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;

            if (sizeOnDisk == 0)
            {
                try { sizeOnDisk = GetDirectorySize(installPath); } catch { }
            }

            int parsedStateFlags = 0;
            if (!string.IsNullOrEmpty(stateFlags)) int.TryParse(stateFlags, out parsedStateFlags);

            return new GameInfo
            {
                AppId = appId,
                Name = name,
                InstallPath = installPath,
                BuildId = buildId ?? "unknown",
                SizeOnDisk = sizeOnDisk,
                LastUpdated = lastUpdated,
                Platform = GamePlatform.Steam,
                IsInstalled = true,
                StateFlags = parsedStateFlags,
                CoverUrl = int.TryParse(appId, out var sid)
                    ? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{sid}/header.jpg"
                    : null,
            };
        }
        catch (Exception ex)
        {
            errors?.Add($"Error parsing {Path.GetFileName(manifestPath)}: {ex.Message}");
            return null;
        }
    }

    private static long GetDirectorySize(string path)
    {
        long size = 0;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            size += file.Length;
        return size;
    }
}
