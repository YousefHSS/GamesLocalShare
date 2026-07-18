using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        var newRoot = string.IsNullOrWhiteSpace(cacheRoot) ? Models.AppSettings.DefaultXboxCacheDir : cacheRoot!;
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
        _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot) ? Models.AppSettings.DefaultXboxCacheDir : cacheRoot;
        // Prefer an explicit user setting; otherwise fall back to the CikExtractor bundled next to the app
        // (tools\cikextractor) so a fresh install can dump CIKs without the user configuring anything.
        _cikExtractorPath = string.IsNullOrWhiteSpace(cikExtractorPath)
            ? ResolveBundledCikExtractor()
            : cikExtractorPath;
        _captureLog = Path.Combine(baseDir, "capture.log");
        Directory.CreateDirectory(DropFolder);
        Directory.CreateDirectory(SkeletonStore);
        Directory.CreateDirectory(_cikStore);
    }

    /// <summary>
    /// Locates the CikExtractor bundled next to the app (tools\cikextractor\CikExtractor.exe), used when
    /// the user hasn't configured an explicit <see cref="CikExtractorPath"/>. Mirrors how
    /// <see cref="SkeletonService"/> resolves the bundled xvdtool. Returns null if no bundled copy exists.
    /// </summary>
    private static string? ResolveBundledCikExtractor()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "tools", "cikextractor", "CikExtractor.exe");
        if (File.Exists(beside)) return beside;

        // Dev fallback: walk up to the in-repo bundled tool.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var devPath = Path.Combine(dir.FullName, "tools", "cikextractor", "CikExtractor.exe");
            if (File.Exists(devPath)) return devPath;
            dir = dir.Parent;
        }
        return null;
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

        // Periodic reconcile: an update rewrites the install in place (bumping the version in
        // Content\appxmanifest.xml) and may not raise a watcher event we catch. Re-check installed
        // versions against captured skeletons on an interval so a title that updated while running
        // (or whose install event was missed) still gets re-captured.
        _ = Task.Run(() => ReconcileLoopAsync(ct), ct);
    }

    /// <summary>On-demand: re-scan installed titles and capture any that have a cached package but no
    /// (up-to-date) skeleton — the same sweep that runs on start/reconcile, triggered now from the UI so the
    /// user doesn't wait for the periodic pass. Requires the watcher to be running. The per-title
    /// single-flight guard prevents overlap with automatic captures.</summary>
    public void CaptureMissingNow()
    {
        if (!IsRunning)
        {
            Report("capture from cache: start watching first (the watcher must be running to capture)");
            return;
        }
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            foreach (var root in _roots) SeedInstalls(root); // pick up newly installed titles first
            Report("capturing skeletons from cached packages (installed titles without one) …");
            await CaptureExistingInstallsAsync(ct, announceIdle: true);
        }, ct);
    }

    /// <summary>
    /// On start, captures any already-installed title that has a cached package and no skeleton yet.
    /// Runs sequentially (one capture at a time) to avoid hammering the disk; <see cref="TryCaptureAsync"/>'s
    /// single-flight guard prevents overlap with watcher-triggered captures.
    /// </summary>
    private async Task CaptureExistingInstallsAsync(CancellationToken ct, bool announceIdle = true)
    {
        int candidates = 0;
        foreach (var kv in _installs.ToArray())
        {
            if (ct.IsCancellationRequested) return;
            var name = kv.Key;
            var installDir = kv.Value;
            if (!NeedsCapture(name, installDir, out var why)) continue; // up-to-date skeleton already present
            var pkg = FindDropFor(name) ?? LocatePackage(installDir);
            if (pkg == null)
            {
                // New title, or an update landed (installed version > skeleton version), but there's no
                // encrypted package cached to (re)capture from. Log the reason so it's visible; the cache
                // watcher picks the package up if it appears later.
                Report($"{name}: {why}, but no cached package present to capture from " +
                       "(route its download/update through the proxy, or drop its .msixvc)");
                continue;
            }
            candidates++;
            Report($"{name}: {why} — capturing from {Path.GetFileName(pkg)}");
            await TryCaptureAsync(name, installDir, pkg);
        }
        if (candidates == 0 && announceIdle)
            Report("no existing installs need capture (skeletons up to date, or none has a cached package)");
    }

    /// <summary>Periodically re-checks installed titles against their captured skeletons so an update that
    /// landed while the watcher was running (install rewritten in place, version bumped) triggers a
    /// re-capture even when no file-watch event was raised for it. Cheap: reads Content\appxmanifest.xml.</summary>
    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(10), ct); }
                catch { return; }
                foreach (var root in _roots) SeedInstalls(root); // also pick up newly installed titles
                await CaptureExistingInstallsAsync(ct, announceIdle: false);
            }
        }
        catch { }
    }

    /// <summary>Decides whether a title needs (re)capturing: true if it has no skeleton yet, or if the
    /// installed version (Content\appxmanifest.xml) is newer than the version its existing skeleton was
    /// captured for — i.e. an update landed. When either version can't be determined, an existing skeleton
    /// is treated as up-to-date so we never spuriously re-capture. <paramref name="reason"/> is a
    /// log-friendly explanation of the decision.</summary>
    private bool NeedsCapture(string name, string installDir, out string reason)
    {
        var skel = Path.Combine(SkeletonStore, name + ".skl");
        if (!File.Exists(skel)) { reason = "no skeleton yet"; return true; }

        var installedVer = GetInstalledVersion(installDir);
        var capturedVer = ParseVer(ReadManifest(skel)?.CapturedVersion);
        if (installedVer != null && capturedVer != null && installedVer > capturedVer)
        {
            reason = $"update detected (installed v{installedVer} > skeleton v{capturedVer})";
            return true;
        }
        reason = capturedVer == null
            ? "skeleton present (its captured version is unknown — re-capture once to enable update tracking)"
            : $"skeleton up to date (v{capturedVer})";
        return false;
    }

    private static Version? ParseVer(string? s) => Version.TryParse(s, out var v) ? v : null;

    private static readonly Regex IdentityVersionRx = new(
        @"<Identity\b[^>]*?\bVersion=""([0-9]+(?:\.[0-9]+){1,3})""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads the installed package version from the title's <c>Content\appxmanifest.xml</c>
    /// (falling back to <c>Content\MicrosoftGame.Config</c>), both of which carry the
    /// <c>&lt;Identity … Version="a.b.c.d"/&gt;</c> that an update bumps. Returns null when it can't be read
    /// or parsed (e.g. restrictive ACLs), so callers fall back to "skeleton present = up to date".</summary>
    internal static Version? GetInstalledVersion(string installDir)
    {
        foreach (var rel in new[] { "appxmanifest.xml", "MicrosoftGame.Config" })
        {
            try
            {
                var p = Path.Combine(installDir, "Content", rel);
                if (!File.Exists(p)) continue;
                var m = IdentityVersionRx.Match(File.ReadAllText(p));
                if (m.Success && Version.TryParse(m.Groups[1].Value, out var v)) return v;
            }
            catch { }
        }
        return null;
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
                if (!SafeHasXvi(dir)) continue;
                var name = ResolveInstallName(dir);
                if (name == null) continue; // unresolved GUID staging folder — skip until it names itself
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
            if (!SafeHasXvi(installDir)) return; // not an MSIXVC install (no envelope yet)
            var name = ResolveInstallName(installDir);
            if (name == null) return; // still in the GUID staging stage (no title resolvable from the manifest yet)

            bool isNew = !_installs.ContainsKey(name);
            _installs[name] = installDir;

            // Announce the completed install BEFORE the skeleton-exists check, so consumers (e.g. the streaming
            // peer-install finalizer) still hear it even when a skeleton is already present (it was copied from
            // the peer). The check below only skips re-CAPTURING, not detection.
            if (isNew) InstallDetected?.Invoke(installDir);

            // (Re)capture only when there's no skeleton yet, or the install has been updated past the version
            // the existing skeleton was captured for. An up-to-date skeleton is left alone.
            if (!NeedsCapture(name, installDir, out var captureReason))
                return;

            // Prefer a manually dropped package; otherwise auto-locate it in the cache.
            var pkg = FindDropFor(name) ?? LocatePackage(installDir);
            if (pkg != null)
            {
                Report($"{name}: {captureReason} (package: {Path.GetFileName(pkg)}) — capturing");
                _ = TryCaptureAsync(name, installDir, pkg);
            }
            else
            {
                // Needs capture but the package isn't in the cache yet — the proxy may still be filling it
                // (.part not yet renamed to final .msixvc), or an update only pulled a delta. Don't give up:
                // poll for it (and the cache-root watcher will also catch it).
                Report($"{name}: {captureReason} — package not cached yet, watching for it");
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
        if (!NeedsCapture(name, installDir, out _)) return;
        if (!_pending.TryAdd(name, true)) return; // already waiting on this title
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 180 && !ct.IsCancellationRequested; i++) // ~15 min @ 5 s
                {
                    // Re-resolve the live folder each tick: a staging install may be renamed (GUID -> friendly)
                    // between ticks, so never pin to a path that has moved. null = not present right now
                    // (mid-rename or uninstalled); keep waiting — the loop timeout below covers a real uninstall.
                    var live = ResolveLiveInstallDir(name, installDir);
                    if (live == null)
                    {
                        try { await Task.Delay(5000, ct); } catch { return; }
                        continue;
                    }
                    installDir = live;
                    _installs[name] = live;
                    if (!NeedsCapture(name, installDir, out _)) return;
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
                    if (!NeedsCapture(name, installDir, out _)) continue;
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
    /// <para>Prefers the package whose version <b>exactly matches the installed version</b>
    /// (<c>Content\appxmanifest.xml</c>). If the install's version is known but only a <i>different</i>
    /// version is cached, returns null instead of capturing from the mismatched package — a version-drifted
    /// capture yields a near-useless whole-package skeleton (e.g. Rematch v1.204.5.0 pkg vs v1.204.6.0 install
    /// → 10 GB skeleton). When the install version can't be read, falls back to the highest cached version.
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

            // The installed version drives an EXACT match; a mismatched-version package is never captured.
            var installedVer = GetInstalledVersion(installDir); // null when the manifest can't be read

            string? best = null, exact = null;
            Version? bestVer = null;
            DateTime bestTime = DateTime.MinValue, exactTime = DateTime.MinValue;

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

                // Exact installed-version match (newest write among equals wins).
                if (installedVer != null && ver != null && NormVer(ver) == NormVer(installedVer)
                    && (exact == null || t > exactTime))
                {
                    exact = f; exactTime = t;
                }

                bool better = best == null
                    || (ver != null && (bestVer == null || ver > bestVer))
                    || (ver == null && bestVer == null && t > bestTime);
                if (better) { best = f; bestVer = ver; bestTime = t; }
            }

            if (exact != null) return exact;

            // Install version known but only a different-version package is cached: do NOT capture from it
            // (version drift → useless whole-package skeleton). Wait for the matching package to be cached.
            if (installedVer != null && best != null && bestVer != null && NormVer(bestVer) != NormVer(installedVer))
            {
                Report($"{Path.GetFileName(installDir)}: cached package is v{bestVer} but install is v{installedVer} — " +
                       "waiting for the matching-version package (not capturing from a mismatched version)");
                return null;
            }
            if (installedVer != null && best == null) return null; // nothing cached yet for this title

            return best; // install version unknown -> legacy highest-version behaviour
        }
        catch { return null; }
    }

    /// <summary>Normalises a version to 4 components (missing parts as 0) so a 3-part manifest version and a
    /// 4-part package-filename version compare equal.</summary>
    private static Version NormVer(Version v) =>
        new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private async Task TryCaptureAsync(string name, string installDir, string packagePath)
    {
        if (!_inFlight.TryAdd(name, true)) return; // already capturing this title
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            var skel = Path.Combine(SkeletonStore, name + ".skl");

            // The tee promotes the cached package the instant its download completes — which is during Xbox's
            // install staging, before the content-GUID staging folder is renamed to the friendly title. Re-resolve
            // the folder that exists right now; if none does yet, the install hasn't settled, so defer instead of
            // capturing against a path that's about to move (which xvdtool reports as "install root not found").
            var live = ResolveLiveInstallDir(name, installDir);
            if (live == null)
            {
                Report($"{name}: install still staging (folder not settled) — will capture once it finalizes");
                ScheduleCaptureWhenReady(name, installDir);
                return;
            }
            installDir = live;
            _installs[name] = live;

            // Ensure the CIK store has keys (invokes CikExtractor elevated only if needed).
            var cikFolder = await EnsureCikFolderAsync(ct);

            // Xbox game files can be readable only by an elevated process. If the install has files this
            // (non-elevated) process can't read, capture elevated so they can be deduped instead of dumped
            // whole into the skeleton (e.g. GameMaker titles that pack the game into one protected .exe).
            // Only elevate when a MEANINGFUL amount is unreadable: many games (Unity) have a small
            // ACL-locked .exe but readable bulk data, and capture fine without elevation. Elevating for a
            // few MB isn't worth a UAC prompt; a big unreadable file (a GameMaker game's whole .exe) is.
            long unreadable = UnreadableInstallBytes(installDir);
            bool needsElevation = unreadable > 32L * 1024 * 1024; // 32 MB
            if (needsElevation)
                Report($"{name}: {unreadable / 1048576.0:F0} MB of install files need elevation to read — capturing elevated (accept the UAC prompt)");

            Report($"capturing skeleton for {name} …");
            var res = await _skeleton.CaptureAsync(
                packagePath, installDir, skel, cikFolder,
                onOutput: line => Report(line),
                elevated: needsElevation,
                ct: ct);

            // If the elevated capture didn't run (UAC declined) or failed, fall back to a non-elevated capture
            // so the title still gets a (larger) working skeleton rather than none.
            if (needsElevation && !res.Ok && !res.KeyMissing)
            {
                Report($"{name}: elevated capture unavailable — falling back to a non-elevated capture (skeleton may be larger)");
                res = await _skeleton.CaptureAsync(
                    packagePath, installDir, skel, cikFolder,
                    onOutput: line => Report(line),
                    ct: ct);
                needsElevation = false; // don't elevate the retries below
            }

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
                        elevated: needsElevation,
                        ct: ct);
                }
            }

            // If the capture failed because the install folder was renamed out from under xvdtool mid-run
            // (GUID staging -> friendly title), re-resolve the now-settled folder and retry once.
            if (!res.Ok && !res.KeyMissing && !Directory.Exists(installDir))
            {
                var settled = ResolveLiveInstallDir(name, installDir);
                if (settled != null && !string.Equals(settled, installDir, StringComparison.OrdinalIgnoreCase))
                {
                    installDir = settled;
                    _installs[name] = settled;
                    Report($"{name}: install folder was renamed during capture — retrying against \"{Path.GetFileName(settled)}\" …");
                    res = await _skeleton.CaptureAsync(
                        packagePath, installDir, skel, cikFolder,
                        onOutput: line => Report(line),
                        elevated: needsElevation,
                        ct: ct);
                }
                else
                {
                    Report($"{name}: install folder moved during capture and hasn't settled — will retry when it finalizes");
                    ScheduleCaptureWhenReady(name, installDir);
                    return;
                }
            }

            if (res.Ok && res.Verified)
            {
                // Record where this title's genuine package lived + how to rebuild it, so the package can be
                // RESTORED on demand (skeleton + install -> genuine .msixvc back into the served cache path)
                // for a Gaming Services Verify/update HIT after the user deletes the multi-GB package.
                // The installed version is stamped in too, so a later update is detected and re-captured.
                var ver = GetInstalledVersion(installDir);
                WriteManifest(name, skel, installDir, packagePath, res.USize, ver?.ToString());
                CaptureCompleted?.Invoke(res);
                var vtag = ver != null ? $" v{ver}" : "";
                Report($"✓ {name}{vtag}: skeleton {res.SkelFileSize / 1048576.0:F2} MB " +
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
        /// <summary>Installed package version (Content\appxmanifest.xml Identity) at capture time, so a later
        /// update (a higher version) can be detected and the skeleton re-captured.</summary>
        public string CapturedVersion { get; init; } = "";
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

    private void WriteManifest(string name, string skelPath, string installDir, string packagePath, long uSize,
        string? capturedVersion)
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
                CapturedVersion = capturedVersion ?? "",
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

    /// <summary>Total bytes of files under <paramref name="installDir"/> that this (non-elevated) process
    /// cannot open for read — Xbox protects some game files so only an elevated/high-integrity process can
    /// read them. Used to decide whether capture must run elevated so those files are deduped instead of
    /// dumped whole into the skeleton. File size (metadata) is readable even when the data is ACL-blocked;
    /// sharing/other IO errors are not counted (they're not an ACL block).</summary>
    private static long UnreadableInstallBytes(string installDir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                try { using var _ = File.Open(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                catch (UnauthorizedAccessException)
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                catch { /* sharing / transient — not an ACL block */ }
            }
        }
        catch { }
        return total;
    }

    private static bool IsGuid(string s) => Guid.TryParse(s, out _);

    /// <summary>
    /// Resolves the tracking name for an install folder. A friendly-named folder (e.g. "Rematch") keeps its
    /// name. A GUID-named folder is an Xbox title that was never renamed from its content-GUID install-staging
    /// name; instead of skipping it forever (some titles keep the GUID name permanently), resolve the real
    /// title from its <c>Content\appxmanifest.xml</c> so it is tracked like any other game. Returns null when
    /// the folder is an unresolved GUID (genuinely mid-install — no readable manifest title yet), so callers
    /// skip it until it becomes resolvable.
    /// </summary>
    private static string? ResolveInstallName(string dir)
    {
        var folder = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(folder)) return null;
        if (!IsGuid(folder)) return folder;
        return ReadTitleFromManifest(dir); // null while still staging
    }

    /// <summary>
    /// Returns the install folder that currently exists on disk for <paramref name="name"/>. Xbox stages a
    /// fresh install under a content-GUID folder and renames it to the friendly title when it finalizes, so a
    /// path recorded earlier can vanish mid-install. If <paramref name="recordedDir"/> still exists it wins;
    /// otherwise the library roots are re-scanned for the folder that resolves to this title. Returns null when
    /// no matching folder exists right now (the install is mid-rename, or was uninstalled).
    /// </summary>
    private string? ResolveLiveInstallDir(string name, string recordedDir)
    {
        if (Directory.Exists(recordedDir)) return recordedDir;
        foreach (var root in _roots)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (!SafeHasXvi(dir)) continue;
                    if (string.Equals(ResolveInstallName(dir), name, StringComparison.OrdinalIgnoreCase))
                        return dir;
                }
            }
            catch { }
        }
        return null;
    }

    private static readonly Regex ManifestDisplayNameRx = new(
        @"<DisplayName>\s*([^<]+?)\s*</DisplayName>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ManifestIdentityNameRx = new(
        @"<Identity\b[^>]*?\bName=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads a filesystem-safe title from a title's <c>Content\appxmanifest.xml</c>: the
    /// <c>&lt;Properties&gt;&lt;DisplayName&gt;</c> (e.g. "Buckshot Roulette"), falling back to the friendly
    /// tail of the package <c>Identity</c> Name (e.g. "CRITICALREFLEX.BuckshotRoulette" → "BuckshotRoulette").
    /// Returns null when no manifest/name is available (an ms-resource placeholder is treated as unavailable).</summary>
    private static string? ReadTitleFromManifest(string installDir)
    {
        try
        {
            var p = Path.Combine(installDir, "Content", "appxmanifest.xml");
            if (!File.Exists(p)) return null;
            var xml = File.ReadAllText(p);

            var dn = ManifestDisplayNameRx.Match(xml);
            if (dn.Success)
            {
                var v = dn.Groups[1].Value.Trim();
                if (v.Length > 0 && !v.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                    return SanitizeName(v);
            }

            var idn = ManifestIdentityNameRx.Match(xml);
            if (idn.Success)
            {
                var id = idn.Groups[1].Value;
                var dot = id.LastIndexOf('.');
                var friendly = (dot >= 0 && dot < id.Length - 1) ? id.Substring(dot + 1) : id;
                if (friendly.Length > 0) return SanitizeName(friendly);
            }
        }
        catch { }
        return null;
    }

    private static string SanitizeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
        return s.Trim();
    }

    public void Dispose() => Stop();
}
