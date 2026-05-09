using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

public static class CrossLocationMatcher
{
    // Steam's libraryfolders.vdf can register an external drive as a Steam library, which
    // causes SteamLibraryScanner to surface the drive's copy as a "local" game. If we don't
    // filter those out, the device-copy match below pairs an external entry with itself and
    // reports InSync even when the real on-device install has a newer build.
    public static List<GameInfo> FilterDeviceGames(IEnumerable<GameInfo> localGames, IEnumerable<string> externalRoots)
    {
        var roots = externalRoots.Where(p => !string.IsNullOrEmpty(p)).ToList();
        return localGames
            .Where(g => !roots.Any(r => g.InstallPath.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static GameInfo? FindDeviceCopy(IEnumerable<GameInfo> deviceGames, GameInfo extGame)
    {
        return deviceGames.FirstOrDefault(g =>
            g.AppId == extGame.AppId ||
            string.Equals(g.Name, extGame.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                System.IO.Path.GetFileName(g.InstallPath.TrimEnd('\\', '/', ' ')),
                extGame.Name,
                StringComparison.OrdinalIgnoreCase));
    }

    // Steam StateFlags bitmask: 4 == StateInstalled with no pending update/download/repair.
    // Any other value (0=uninstalled, 16=update required, 1026=update queued, 256=updating,
    // 128=files corrupt, etc.) means Steam is not certain the install is current.
    private const int SteamStateFullyInstalled = 4;

    public static CopyDirection ClassifyDirection(GameInfo? deviceCopy, GameInfo extGame)
    {
        if (deviceCopy == null) return CopyDirection.OnlyOnDrive;

        var deviceHasBuild = !string.IsNullOrEmpty(deviceCopy.BuildId)
            && !deviceCopy.BuildId.Equals("unknown", StringComparison.OrdinalIgnoreCase);
        var extHasBuild = !string.IsNullOrEmpty(extGame.BuildId)
            && !extGame.BuildId.Equals("unknown", StringComparison.OrdinalIgnoreCase);

        // Both sides have a real BuildId — definitive comparison.
        if (deviceHasBuild && extHasBuild)
        {
            if (string.Equals(deviceCopy.BuildId, extGame.BuildId, StringComparison.OrdinalIgnoreCase))
                return CopyDirection.InSync;
            return deviceCopy.LastUpdated > extGame.LastUpdated
                ? CopyDirection.DeviceToDrive
                : CopyDirection.DriveToDevice;
        }

        // Only one side has a BuildId (typical: device is a Steam install with .acf,
        // drive is a generic folder dropped in via Explorer with no .gamesync_meta).
        // Folder mtime is unreliable here (it reflects when the folder was *touched*,
        // not when the game was *updated*), so fall back to Steam's StateFlags.
        if (deviceHasBuild && !extHasBuild)
        {
            if (deviceCopy.Platform == GamePlatform.Steam
                && deviceCopy.StateFlags == SteamStateFullyInstalled)
            {
                // Steam considers the device install fully current. Drive content version is
                // unknown but it can be at most as new as Steam's latest known build, so
                // overwriting it with the device copy is safe.
                return CopyDirection.DeviceToDrive;
            }
            return CopyDirection.UnknownVersion;
        }

        if (!deviceHasBuild && extHasBuild)
        {
            // Mirror case: drive has a sidecar (we copied it via this app), device is a
            // generic install we can't version. Rare, but treat symmetrically.
            return CopyDirection.UnknownVersion;
        }

        // Neither side has version info — genuinely unknown.
        return CopyDirection.UnknownVersion;
    }
}
