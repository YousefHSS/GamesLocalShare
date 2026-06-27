using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;

namespace GamesLocalShare.Services;

/// <summary>
/// Watches for Xbox MSIXVC installs completing and, when the <i>encrypted</i> package for that title is
/// available, auto-captures a skeleton via <see cref="SkeletonService"/> (xvdtool decrypts on the fly -
/// no full decrypted copy ever hits disk). The game is then stored as (installed files) + (~16 MB
/// skeleton) instead of keeping the full package.
///
/// <para>On install-detect the encrypted package is <b>auto-located</b> under <see cref="PackageCacheRoot"/>
/// by matching <c>&lt;PackageFullName&gt;.msixvc</c> to the title's PackageFamilyName (read from the install
/// folder ACL). A manually dropped <c>&lt;TitleFolderName&gt;.msixvc</c> in <see cref="DropFolder"/> is also
/// honored as a fallback. CIKs come from <see cref="CikStore"/>, populated on demand from the user's
/// CikExtractor (<see cref="CikExtractorPath"/>); xvdtool auto-selects the matching key.
/// The skeleton is written to <c>&lt;SkeletonStore&gt;\&lt;TitleFolderName&gt;.skl</c>.</para>
///
/// <para>Decryption is performed by xvdtool with the user's own CIK on their own licensed content; the
/// app never implements the cipher itself.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SkeletonWatcherService : IDisposable
{
    private readonly SkeletonService _skeleton;
    private readonly CikExtractorRunner _cikRunner = new();
    private readonly string _cikStore;
    private string _cacheRoot;
    private readonly string? _cikExtractorPath;
    private readonly List<FileSystemWatcher> _watchers = new();
    // installed title folder name -> install dir
    private readonly ConcurrentDictionary<string, string> _installs = new(StringComparer.OrdinalIgnoreCase);
    // single-flight guard per title
    private readonly ConcurrentDictionary<string, bool> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    // titles with a background poller waiting for their package to finish caching
    private readonly ConcurrentDictionary<string, bool> _pending = new(StringComparer.OrdinalIgnoreCase);
    // persistent capture log (mirrors every status line so failures survive an app restart)
    private readonly string _captureLog;
    private readonly object _logLock = new();
    // watched library roots (kept so installs can be re-seeded after a buffer-overflow re-scan)
    private string[] _roots = Array.Empty<string>();
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public string DropFolder { get; }
    public string SkeletonStore { get; }
    /// <summary>Folder of .cik files (CikExtractor output) xvdtool selects the package's key from.</summary>
    public string CikStore => _cikStore;
    /// <summary>Root searched (recursively) for cached <c>&lt;PackageFullName&gt;.msixvc</c> packages.</summary>
    public string PackageCacheRoot => _cacheRoot;

    /// <summary>Updates the package cache root (e.g. after the user changes it in Settings). Capture/restore
    /// pick it up immediately; if the watcher is already running, the cache-folder watcher is re-pointed too.</summary>
    public void UpdateCacheRoot(string? cacheRoot)
    {
        var newRoot = string.IsNullOrWhiteSpace(cacheRoot) ? @"F:\xbox-cache" : cacheRoot!;
        if (string.Equals(_cacheRoot, newRoot, StringComparison.OrdinalIgnoreCase)) return;
        _cacheRoot = newRoot;
        Report($"package cache root changed to: {_cacheRoot}");
        if (IsRunning)
        {
            // Restart so the cache-folder FileSystemWatcher re-points at the new root.
            var roots = _roots;
            Stop();
            Start(roots);
        }
    }
    /// <summary>CikExtractor repo root or exe used to populate the CIK store on demand (may be null).</summary>
    public string? CikExtractorPath => _cikExtractorPath;

    /// <summary>Free-text progress/log line (script output, status changes).</summary>
    public event Action<string>? Status;
    /// <summary>Raised when a title's install is detected complete. Arg = install dir.</summary>
    public event Action<string>? InstallDetected;
    /// <summary>Raised after a verified capture. Arg = the capture result.</summary>
    public event Action<SkeletonCaptureResult>? CaptureCompleted;
    /// <summary>Raised when a capture attempt fails or does not verify. Arg = message.</summary>
    public event Action<string>? CaptureFailed;
    /// <summary>Raised after a verified restore. Arg = the restored title's folder name.</summary>
    public event Action<string>? RestoreCompleted;
    /// <summary>Raised when a restore attempt fails. Arg = message.</summary>
    public event Action<string>? RestoreFailed;

    public SkeletonWatcherService(SkeletonService skeleton, string? dropFolder = null,
        string? skeletonStore = null, string? cikStore = null,
        string? cacheRoot = null, string? cikExtractorPath = null)
    {
        _skeleton = skeleton;
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GamesLocalShare", "xbox-skeleton");
        DropFolder = dropFolder ?? Path.Combine(baseDir, "incoming");
        SkeletonStore = skeletonStore ?? Path.Combine(baseDir, "skeletons");
        _cikStore = cikStore ?? Path.Combine(baseDir, "cik");
        // Default the package cache root to the LAN-cache proxy's CacheDir (xbox-cache-proxy.ps1),
        // where downloaded <PackageFullName>.msixvc packages are persisted. Configurable via settings.
        _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot) ? @"F:\xbox-cache" : cacheRoot;
        _cikExtractorPath = string.IsNullOrWhiteSpace(cikExtractorPath) ? null : cikExtractorPath;
        _captureLog = Path.Combine(baseDir, "capture.log");
        Directory.CreateDirectory(DropFolder);
        Directory.CreateDirectory(SkeletonStore);
        Directory.CreateDirectory(_cikStore);
    }

    /// <summary>Path of the persistent capture log (every status line is appended here).</summary>
    public string CaptureLogPath => _captureLog;

    /// <summary>Raises <see cref="Status"/> and also appends the line (timestamped) to the persistent
    /// capture log, so the reason a title did/didn't capture survives an app restart.</summary>
    private void Report(string line)
    {
        try
        {
            lock (_logLock)
                File.AppendAllText(_captureLog,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { }
        var h = Status;
        h?.Invoke(line);
    }

    /// <summary>
    /// Starts watching the given XboxGames roots plus the drop folder.
    /// Pass <see cref="XboxLibraryScanner.GetLibraryFolders"/> for the roots.
    /// </summary>
    public void Start(IEnumerable<string> xboxRoots)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _roots = xboxRoots.Where(Directory.Exists).ToArray();

        foreach (var root in _roots)
        {
            // Seed already-installed titles so a later U-drop can match them.
            SeedInstalls(root);

            try
            {
                // (a) .xvi file watcher — catches a .xvi landing directly under an already-named title folder.
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName,
                    Filter = "*.xvi",
                    InternalBufferSize = 64 * 1024, // big tree (e.g. F:\Games) floods events during installs
                    EnableRaisingEvents = true,
                };
                w.Created += OnXviAppeared;
                w.Renamed += (s, e) => OnXviAppeared(s,
                    new FileSystemEventArgs(WatcherChangeTypes.Created,
                        Path.GetDirectoryName(e.FullPath) ?? root, Path.GetFileName(e.FullPath)));
                w.Error += OnWatcherError;
                _watchers.Add(w);

                // (b) directory-name watcher — catches the <GUID> → <Title> folder rename that Xbox does on
                // install completion (the real "install complete" signal that the .xvi filter above misses).
                var dw = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true,
                };
                dw.Renamed += OnDirRenamed;
                dw.Created += OnDirCreated;
                dw.Error += OnWatcherError;
                _watchers.Add(dw);
            }
            catch (Exception ex) { Report($"watch failed for {root}: {ex.Message}"); }
        }

        try
        {
            var dw = new FileSystemWatcher(DropFolder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            dw.Created += OnDrop;
            dw.Changed += OnDrop;
            dw.Renamed += (s, e) => OnDrop(s,
                new FileSystemEventArgs(WatcherChangeTypes.Created, DropFolder, e.Name ?? ""));
            _watchers.Add(dw);
        }
        catch (Exception ex) { Report($"drop-folder watch failed: {ex.Message}"); }

        // Watch the package cache itself, so a package that finishes downloading AFTER its install completed
        // (proxy fill lag, or the app was restarted mid-fill) still triggers a capture for the matching
        // installed title — even if no install event fires for it.
        try
        {
            if (Directory.Exists(_cacheRoot))
            {
                var cw = new FileSystemWatcher(_cacheRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    Filter = "*.msixvc",
                    EnableRaisingEvents = true,
                };
                cw.Created += OnCachePackage;
                cw.Renamed += (s, e) => OnCachePackage(s,
                    new FileSystemEventArgs(WatcherChangeTypes.Created,
                        Path.GetDirectoryName(e.FullPath) ?? _cacheRoot, Path.GetFileName(e.FullPath)));
                _watchers.Add(cw);
            }
        }
        catch (Exception ex) { Report($"cache watch failed for {_cacheRoot}: {ex.Message}"); }

        IsRunning = true;
        Report($"watching {_watchers.Count} location(s); {_installs.Count} installed title(s). " +
                       $"Auto-locating packages under: {_cacheRoot}  (or drop one in: {DropFolder})");

        // Sweep already-installed titles: capture any that have a cached package and no skeleton yet.
        // (FileSystemWatcher only fires on a NEW .xvi, so without this, games installed before the
        // watcher started would never be captured.)
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => CaptureExistingInstallsAsync(ct), ct);
    }

    /// <summary>
    /// On start, captures any already-installed title that has a cached package and no skeleton yet.
    /// Runs sequentially (one capture at a time) to avoid hammering the disk; <see cref="TryCaptureAsync"/>'s
    /// single-flight guard prevents overlap with watcher-triggered captures.
    /// </summary>
    private async Task CaptureExistingInstallsAsync(CancellationToken ct)
    {
        int candidates = 0;
        foreach (var kv in _installs.ToArray())
        {
            if (ct.IsCancellationRequested) return;
            var name = kv.Key;
            var installDir = kv.Value;
            if (File.Exists(Path.Combine(SkeletonStore, name + ".skl"))) continue; // already captured
            var pkg = LocatePackage(installDir);
            if (pkg == null) continue;
            candidates++;
            Report($"existing install with cached package: {name}  ({Path.GetFileName(pkg)})");
            await TryCaptureAsync(name, installDir, pkg);
        }
        if (candidates == 0)
            Report("no existing installs have a cached package to capture (install/redownload one to trigger)");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        try { _cts?.Cancel(); } catch { }
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
        IsRunning = false;
        Report("watcher stopped");
    }

    private void OnXviAppeared(object? sender, FileSystemEventArgs e)
    {
        var installDir = Path.GetDirectoryName(e.FullPath);
        if (installDir != null) HandleInstallDir(installDir);
    }

    /// <summary>
    /// Xbox installs into a <c>&lt;GUID&gt;\</c> staging folder and, on completion, <b>renames the folder</b> to
    /// the title (e.g. <c>&lt;GUID&gt;</c> → <c>Bluey Game</c>). That directory rename is the real "install
    /// complete" signal: the <c>.xvi</c> file itself fires no event (only its parent dir is renamed), and a
    /// <c>"*.xvi"</c> filter never sees a directory rename. So we watch directory names too and route both the
    /// rename and a freshly-named directory here.
    /// </summary>
    private void OnDirRenamed(object? sender, RenamedEventArgs e) => HandleInstallDir(e.FullPath);

    private void OnDirCreated(object? sender, FileSystemEventArgs e)
    {
        var dir = e.FullPath;
        if (IsGuid(Path.GetFileName(dir))) return; // GUID staging dir — wait for its rename instead
        // A directory may appear named before its .xvi has landed; give it a short grace window.
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
            {
                if (SafeHasXvi(dir)) { HandleInstallDir(dir); return; }
                try { await Task.Delay(1000, ct); } catch { return; }
            }
        }, ct);
    }

    /// <summary>On buffer overflow (a busy tree can drop events) re-seed installs and re-sweep so a title
    /// whose completion event was lost still gets captured.</summary>
    private void OnWatcherError(object? sender, ErrorEventArgs e)
    {
        Report($"watcher buffer overflowed — re-scanning installs ({e.GetException().Message})");
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() =>
        {
            foreach (var root in _roots) SeedInstalls(root); // pick up titles whose events were dropped
            return CaptureExistingInstallsAsync(ct);
        }, ct);
    }

    /// <summary>Adds every already-named title folder (non-GUID, has a .xvi envelope) under a root to the
    /// installed-title map.</summary>
    private void SeedInstalls(string root)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!IsGuid(name) && SafeHasXvi(dir))
                    _installs[name] = dir;
            }
        }
        catch { }
    }

    /// <summary>Core install-detected handling, shared by the <c>.xvi</c>-file and directory-name watchers.
    /// Idempotent and guarded so the two paths firing for the same title don't double-capture.</summary>
    private void HandleInstallDir(string installDir)
    {
        try
        {
            var name = Path.GetFileName(installDir);
            if (string.IsNullOrEmpty(name) || IsGuid(name)) return; // still in the GUID staging stage
            if (!SafeHasXvi(installDir)) return; // not an MSIXVC install (no envelope yet)

            bool isNew = !_installs.ContainsKey(name);
            _installs[name] = installDir;

            // Announce the completed install BEFORE the skeleton-exists check, so consumers (e.g. the streaming
            // peer-install finalizer) still hear it even when a skeleton is already present (it was copied from
            // the peer). The check below only skips re-CAPTURING, not detection.
            if (isNew) InstallDetected?.Invoke(installDir);

            // Already have a verified skeleton for this title — nothing more to do (don't re-capture).
            if (File.Exists(Path.Combine(SkeletonStore, name + ".skl")))
                return;

            // Prefer a manually dropped package; otherwise auto-locate it in the cache.
            var pkg = FindDropFor(name) ?? LocatePackage(installDir);
            if (pkg != null)
            {
                if (isNew) Report($"install detected: {name}  (package: {Path.GetFileName(pkg)})");
                _ = TryCaptureAsync(name, installDir, pkg);
            }
            else
            {
                // Install completed but the package isn't in the cache yet — the proxy may still be filling it
                // (.part not yet renamed to final .msixvc), especially for small/fast games. Don't give up:
                // poll for it (and the cache-root watcher will also catch it).
                if (isNew) Report($"install detected: {name}  (package not cached yet — watching for it)");
                ScheduleCaptureWhenReady(name, installDir);
            }
        }
        catch { }
    }

    private void OnDrop(object? sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) return;
        if (path.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase)) return;

        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            if (!await WaitSettledAsync(path, ct)) return;
            if (_installs.TryGetValue(name, out var installDir))
                await TryCaptureAsync(name, installDir, path);
            else
                Report($"package dropped for \"{name}\" but no matching installed title yet");
        }, ct);
    }

    /// <summary>
    /// An install completed but its package isn't cached yet. Polls for the cached <c>.msixvc</c> (the proxy
    /// may still be filling it as a <c>.part</c>) and captures once it appears. Re-checks for up to ~15 min and
    /// bails — with a logged reason — if a skeleton shows up, the install is removed, or it times out, instead
    /// of silently giving up after one miss.
    /// </summary>
    private void ScheduleCaptureWhenReady(string name, string installDir)
    {
        if (File.Exists(Path.Combine(SkeletonStore, name + ".skl"))) return;
        if (!_pending.TryAdd(name, true)) return; // already waiting on this title
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 180 && !ct.IsCancellationRequested; i++) // ~15 min @ 5 s
                {
                    if (File.Exists(Path.Combine(SkeletonStore, name + ".skl"))) return;
                    if (!Directory.Exists(installDir))
                    {
                        Report($"{name}: install removed before its package finished caching — capture aborted " +
                               $"(reinstall and keep it installed to capture)");
                        return;
                    }
                    var pkg = FindDropFor(name) ?? LocatePackage(installDir);
                    if (pkg != null)
                    {
                        Report($"{name}: cached package now present ({Path.GetFileName(pkg)}) — capturing");
                        await TryCaptureAsync(name, installDir, pkg);
                        return;
                    }
                    try { await Task.Delay(5000, ct); } catch { return; }
                }
                Report($"{name}: no cached package appeared within 15 min — not captured. " +
                       $"Was the download routed through the proxy? You can also drop its \"{name}.msixvc\" to capture.");
            }
            finally { _pending.TryRemove(name, out _); }
        }, ct);
    }

    /// <summary>
    /// A package landed (or was renamed to its final name) in the cache. Once it has finished being written,
    /// match it back to an installed title with no skeleton yet and capture it. This catches the case where
    /// the package finished downloading after the install completed (proxy fill lag / app restart), which the
    /// install-side <c>.xvi</c> event alone would miss.
    /// </summary>
    private void OnCachePackage(object? sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        if (!path.EndsWith(".msixvc", StringComparison.OrdinalIgnoreCase)) return; // ignore .part
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await WaitSettledAsync(path, ct)) return;
                foreach (var kv in _installs.ToArray())
                {
                    if (ct.IsCancellationRequested) return;
                    var name = kv.Key;
                    var installDir = kv.Value;
                    if (File.Exists(Path.Combine(SkeletonStore, name + ".skl"))) continue;
                    if (!Directory.Exists(installDir)) continue;
                    var pkg = LocatePackage(installDir);
                    if (pkg != null && string.Equals(
                            Path.GetFullPath(pkg), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    {
                        Report($"{name}: package finished caching — capturing");
                        await TryCaptureAsync(name, installDir, pkg);
                        return;
                    }
                }
            }
            catch { }
        }, ct);
    }

    private string? FindDropFor(string name)
    {
        try
        {
            foreach (var f in Directory.GetFiles(DropFolder, name + ".*"))
            {
                if (!f.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Locates the cached encrypted package for an installed title under <see cref="PackageCacheRoot"/>.
    /// <para>Primary match is the <b>content GUID</b> — the <c>.xvi</c> filename at the install root — which
    /// the LAN-cache proxy nests the package under in its CDN-URL path
    /// (<c>&lt;cache&gt;\assets1.xboxlive.com\…\&lt;contentGuid&gt;\…\&lt;PackageFullName&gt;.msixvc</c>). This is
    /// reliable even when the install folder carries no SYSAPPID ACE. As a fallback, when no content GUID is
    /// available, the PackageFamilyName from the folder ACL is matched against the cache filename
    /// (<c>&lt;Name&gt;_…__&lt;PublisherHash&gt;.msixvc</c>).</para>
    /// <para>When several versions match, the highest version wins (newest write time as a tiebreaker).
    /// Returns null if none match.</para>
    /// </summary>
    private string? LocatePackage(string installDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_cacheRoot) || !Directory.Exists(_cacheRoot))
                return null;

            // Primary: content GUID from the .xvi at the install root (LAN-cache nests the pkg under it).
            string? contentGuid = null;
            try
            {
                var xvi = Directory.EnumerateFiles(installDir, "*.xvi").FirstOrDefault();
                if (xvi != null) contentGuid = Path.GetFileNameWithoutExtension(xvi);
            }
            catch { }
            if (contentGuid != null && !Guid.TryParse(contentGuid, out _)) contentGuid = null;

            // Fallback: PackageFamilyName (<Name>_<PublisherHash>) from the folder ACL.
            string? prefix = null, suffix = null;
            if (contentGuid == null)
            {
                var pfn = XboxLibraryScanner.ExtractPfnFromAcl(installDir);
                if (string.IsNullOrEmpty(pfn)) return null;
                int us = pfn.LastIndexOf('_');
                if (us <= 0 || us >= pfn.Length - 1) return null;
                prefix = pfn.Substring(0, us) + "_";       // "<Name>_"
                suffix = "__" + pfn.Substring(us + 1);     // "__<PublisherHash>" (before .msixvc)
            }

            string? best = null;
            Version? bestVer = null;
            DateTime bestTime = DateTime.MinValue;

            foreach (var f in Directory.EnumerateFiles(_cacheRoot, "*.msixvc", SearchOption.AllDirectories))
            {
                if (contentGuid != null)
                {
                    if (f.IndexOf(contentGuid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                else
                {
                    var stem0 = Path.GetFileNameWithoutExtension(f);
                    if (!stem0.StartsWith(prefix!, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!stem0.EndsWith(suffix!, StringComparison.OrdinalIgnoreCase)) continue;
                }

                // Version segment in <Name>_<Version>_<Arch>__<PublisherHash>.
                Version? ver = null;
                foreach (var part in Path.GetFileNameWithoutExtension(f).Split('_'))
                    if (Version.TryParse(part, out var v)) { ver = v; break; }
                DateTime t;
                try { t = File.GetLastWriteTimeUtc(f); } catch { t = DateTime.MinValue; }

                bool better = best == null
                    || (ver != null && (bestVer == null || ver > bestVer))
                    || (ver == null && bestVer == null && t > bestTime);
                if (better) { best = f; bestVer = ver; bestTime = t; }
            }
            return best;
        }
        catch { return null; }
    }

    private async Task TryCaptureAsync(string name, string installDir, string packagePath)
    {
        if (!_inFlight.TryAdd(name, true)) return; // already capturing this title
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            var skel = Path.Combine(SkeletonStore, name + ".skl");

            // Ensure the CIK store has keys (invokes CikExtractor elevated only if needed).
            var cikFolder = await EnsureCikFolderAsync(ct);

            Report($"capturing skeleton for {name} …");
            var res = await _skeleton.CaptureAsync(
                packagePath, installDir, skel, cikFolder,
                onOutput: line => Report(line),
                ct: ct);

            // A freshly installed title's content key is provisioned during its install, so a CIK store
            // dumped earlier won't have it. Re-run CikExtractor to pull the current device keys and retry
            // once. This makes the on-demand CIK step self-healing for new games.
            if (!res.Ok && res.KeyMissing && _cikExtractorPath != null)
            {
                var which = string.IsNullOrEmpty(res.MissingCik) ? "" : res.MissingCik + " ";
                Report($"{name}: content key {which}not in the CIK store — refreshing via CikExtractor (accept the UAC prompt) …");
                var refreshed = await _cikRunner.EnsureCiksAsync(
                    _cikExtractorPath, _cikStore, line => Report(line), ct, forceRefresh: true);
                if (!string.IsNullOrEmpty(refreshed))
                {
                    cikFolder = refreshed!;
                    Report($"retrying capture for {name} with refreshed keys …");
                    res = await _skeleton.CaptureAsync(
                        packagePath, installDir, skel, cikFolder,
                        onOutput: line => Report(line),
                        ct: ct);
                }
            }

            if (res.Ok && res.Verified)
            {
                // Record where this title's genuine package lived + how to rebuild it, so the package can be
                // RESTORED on demand (skeleton + install -> genuine .msixvc back into the served cache path)
                // for a Gaming Services Verify/update HIT after the user deletes the multi-GB package.
                WriteManifest(name, skel, installDir, packagePath, res.USize);
                CaptureCompleted?.Invoke(res);
                Report($"✓ {name}: skeleton {res.SkelFileSize / 1048576.0:F2} MB " +
                               $"replaces a {res.USize / 1048576.0:F0} MB package - safe to delete the package");
            }
            else
            {
                var msg = $"{name}: capture did not verify (exit {res.ExitCode})";
                Report(msg);
                CaptureFailed?.Invoke(msg);
            }
        }
        catch (Exception ex)
        {
            var msg = $"{name}: capture failed - {ex.Message}";
            Report(msg);
            CaptureFailed?.Invoke(msg);
        }
        finally
        {
            _inFlight.TryRemove(name, out _);
        }
    }

    /// <summary>Resolves the folder of .cik files to pass to xvdtool, populating it on demand via
    /// CikExtractor only if needed. Falls back to the configured store.</summary>
    private async Task<string> EnsureCikFolderAsync(CancellationToken ct)
    {
        var cikFolder = _cikStore;
        if (_cikExtractorPath != null)
        {
            var ensured = await _cikRunner.EnsureCiksAsync(
                _cikExtractorPath, _cikStore, line => Report(line), ct);
            if (!string.IsNullOrEmpty(ensured)) cikFolder = ensured!;
        }
        return cikFolder;
    }

    // ---- Restore (serve) -------------------------------------------------

    /// <summary>Sidecar recorded next to a verified skeleton so its genuine package can be rebuilt to the
    /// exact path the LAN-cache proxy serves from.</summary>
    private sealed record RestoreManifest
    {
        public string Name { get; init; } = "";
        public string InstallDir { get; init; } = "";
        /// <summary>Original package path (the LAN-cache file the genuine .msixvc must be restored to).</summary>
        public string CachePath { get; init; } = "";
        /// <summary>Cache root the package lived under, so the nested relative path (the CDN URL path the
        /// proxy serves from) can be re-rooted onto a chosen destination drive when reconstructing.</summary>
        public string CacheRoot { get; init; } = "";
        public long PackageBytes { get; init; }
        public string CapturedAt { get; init; } = "";
    }

    private static string ManifestPathFor(string skelPath) => skelPath + ".json";

    /// <summary>The package's path relative to its cache root (e.g. <c>assets1.xboxlive.com\…\&lt;PFN&gt;.msixvc</c>),
    /// i.e. the CDN URL path a proxy serves it from. Falls back to the file name if the root is unknown.</summary>
    private static string RelativeCachePath(RestoreManifest man)
    {
        if (!string.IsNullOrEmpty(man.CacheRoot)
            && man.CachePath.StartsWith(man.CacheRoot, StringComparison.OrdinalIgnoreCase))
            return man.CachePath.Substring(man.CacheRoot.Length).TrimStart('\\', '/');
        return Path.GetFileName(man.CachePath);
    }

    private void WriteManifest(string name, string skelPath, string installDir, string packagePath, long uSize)
    {
        try
        {
            var m = new RestoreManifest
            {
                Name = name,
                InstallDir = installDir,
                CachePath = packagePath,
                CacheRoot = _cacheRoot,
                PackageBytes = uSize,
                CapturedAt = DateTime.Now.ToString("o"),
            };
            File.WriteAllText(ManifestPathFor(skelPath),
                JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Report($"could not write restore manifest for {name}: {ex.Message}"); }
    }

    private RestoreManifest? ReadManifest(string skelPath)
    {
        try
        {
            var p = ManifestPathFor(skelPath);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<RestoreManifest>(File.ReadAllText(p));
        }
        catch { return null; }
    }

    /// <summary>A previously captured skeleton found on disk (so the UI can list them after a restart, not just
    /// the ones captured live this session).</summary>
    public sealed record CapturedSkeleton(string Name, string SkeletonPath, long SkeletonBytes,
        long PackageBytes, string CapturedAt);

    /// <summary>Enumerates the verified skeletons already on disk in <see cref="SkeletonStore"/>, reading each
    /// sidecar manifest for the original package size and capture time. Newest first.</summary>
    public IReadOnlyList<CapturedSkeleton> GetCapturedSkeletons()
    {
        var list = new List<CapturedSkeleton>();
        try
        {
            foreach (var skel in Directory.EnumerateFiles(SkeletonStore, "*.skl"))
            {
                long skelBytes;
                try { skelBytes = new FileInfo(skel).Length; } catch { skelBytes = 0; }
                var man = ReadManifest(skel);
                long pkgBytes = man?.PackageBytes ?? 0;
                string capturedAt = man?.CapturedAt ?? "";
                if (string.IsNullOrEmpty(capturedAt))
                {
                    try { capturedAt = File.GetLastWriteTime(skel).ToString("o"); } catch { }
                }
                list.Add(new CapturedSkeleton(
                    Path.GetFileNameWithoutExtension(skel), skel, skelBytes, pkgBytes, capturedAt));
            }
        }
        catch { }
        return list.OrderByDescending(s => s.CapturedAt).ToList();
    }

    /// <summary>True when a captured skeleton also has the manifest needed to restore its package.</summary>
    public bool CanRestore(string name)
    {
        var skel = Path.Combine(SkeletonStore, name + ".skl");
        return File.Exists(skel) && ReadManifest(skel) != null;
    }

    /// <summary>
    /// Rebuilds a title's genuine <c>.msixvc</c> from its skeleton + installed files and writes it to the
    /// LAN-cache path the proxy serves from, so a Gaming Services Verify/update gets a HIT instead of a full
    /// re-download. The package is staged to a temp file and only atomically moved into place once it is
    /// confirmed byte-identical to the original (final SHA == the skeleton's gsha).
    /// <para>When <paramref name="destinationRoot"/> is given (e.g. an external drive), the package is rebuilt
    /// under that root at the same nested CDN path (<c>&lt;root&gt;\assets1.xboxlive.com\…\&lt;PFN&gt;.msixvc</c>)
    /// so a proxy pointed at that root on another PC serves it as a HIT. When null, it rebuilds in place at the
    /// original cache path (serve-for-Verify on this PC).</para>
    /// </summary>
    public async Task<bool> RestoreAsync(string name, string? destinationRoot = null)
    {
        if (!_inFlight.TryAdd(name, true))
        {
            Report($"{name}: busy (capture/restore already in progress)");
            return false;
        }
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            var skel = Path.Combine(SkeletonStore, name + ".skl");
            if (!File.Exists(skel))
            {
                RestoreFailed?.Invoke($"{name}: no skeleton found to restore from");
                return false;
            }

            var man = ReadManifest(skel);
            if (man == null || string.IsNullOrWhiteSpace(man.CachePath))
            {
                RestoreFailed?.Invoke($"{name}: no restore manifest (re-capture to record the cache path)");
                return false;
            }

            var installDir = !string.IsNullOrWhiteSpace(man.InstallDir) && Directory.Exists(man.InstallDir)
                ? man.InstallDir
                : (_installs.TryGetValue(name, out var d) ? d : null);
            if (installDir == null || !Directory.Exists(installDir))
            {
                RestoreFailed?.Invoke($"{name}: installed files not found (need the install to rebuild the package)");
                return false;
            }

            // Target = original cache path (serve in place) or the same nested path re-rooted onto a chosen
            // destination drive (portable: copy the drive to another PC, point its proxy at this root).
            bool toChosen = !string.IsNullOrWhiteSpace(destinationRoot);
            string target = toChosen
                ? Path.Combine(destinationRoot!, RelativeCachePath(man))
                : man.CachePath;

            if (!toChosen && File.Exists(target))
            {
                Report($"{name}: package already present at the cache path - nothing to restore");
                RestoreCompleted?.Invoke(name);
                return true;
            }

            var cikFolder = await EnsureCikFolderAsync(ct);

            try { Directory.CreateDirectory(Path.GetDirectoryName(target)!); } catch { }
            var staging = target + ".restoring";
            TryDeleteFile(staging);

            var where = toChosen ? target : Path.GetFileName(target);
            Report($"reconstructing package for {name} (rebuild + re-encrypt → {where}) …");
            var res = await _skeleton.RestoreToPackageAsync(
                skel, installDir, cikFolder, staging,
                onOutput: line => Report(line),
                ct: ct);

            if (!res.Ok)
            {
                TryDeleteFile(staging);
                RestoreFailed?.Invoke($"{name}: reconstruct failed - {res.Error}");
                return false;
            }

            // Atomic publish: only now does the target path get a complete, genuine package.
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(staging, target);
            }
            catch (Exception ex)
            {
                TryDeleteFile(staging);
                RestoreFailed?.Invoke($"{name}: rebuilt package but could not move it into place: {ex.Message}");
                return false;
            }

            if (toChosen)
                Report($"✓ {name}: reconstructed genuine package ({res.PackageBytes / 1048576.0:F0} MB) → {target}  " +
                               $"(point another PC's proxy at \"{destinationRoot}\" to serve it as a HIT)");
            else
                Report($"✓ {name}: restored genuine package ({res.PackageBytes / 1048576.0:F0} MB) to the cache " +
                               $"- Verify/update will now HIT (no re-download)");
            RestoreCompleted?.Invoke(name);
            return true;
        }
        catch (Exception ex)
        {
            RestoreFailed?.Invoke($"{name}: restore failed - {ex.Message}");
            return false;
        }
        finally
        {
            _inFlight.TryRemove(name, out _);
        }
    }

    private static void TryDeleteFile(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    /// <summary>Waits until a file's size is stable (finished being written).</summary>
    private static async Task<bool> WaitSettledAsync(string path, CancellationToken ct)
    {
        long last = -1;
        int stable = 0;
        for (int i = 0; i < 1200 && !ct.IsCancellationRequested; i++) // up to ~10 min
        {
            long len;
            try { len = new FileInfo(path).Length; }
            catch { return false; }

            if (len > 0 && len == last)
            {
                if (++stable >= 3) return true; // stable across ~1.5 s
            }
            else
            {
                stable = 0;
                last = len;
            }

            try { await Task.Delay(500, ct); }
            catch { return false; }
        }
        return false;
    }

    private static bool SafeHasXvi(string dir)
    {
        try { return Directory.GetFiles(dir, "*.xvi").Length > 0; }
        catch { return false; }
    }

    private static bool IsGuid(string s) => Guid.TryParse(s, out _);

    public void Dispose() => Stop();
}
