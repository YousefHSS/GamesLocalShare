using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GamesLocalShare.Services;

/// <summary>
/// In-app LAN cache for Xbox / Microsoft Store content (MSIXVC blobs) — the C# port of
/// xbox-cache-proxy.ps1, so the app itself runs the proxy (Start/Stop from the UI) instead of a
/// separate elevated script.
///
/// <para>Serves the ENCRYPTED .msixvc bytes Microsoft's CDN (assets1.xboxlive.com) would serve, but from a
/// local cache over LAN. The bytes are byte-identical to the CDN's, so Gaming Services trusts the install and
/// Verify/updates work. No decryption, no keys, no license bypass (same idea as Microsoft Connected Cache).</para>
///
/// <para>HIT: serve the requested byte Range from disk. MISS: forward live to the REAL origin (resolved via a
/// DNS query to a public resolver, bypassing our own hosts redirect) and, on the first miss for an object,
/// start ONE background thread that downloads the whole package to <c>&lt;file&gt;.part</c> then renames it to
/// <c>&lt;file&gt;</c> — but only when the download is COMPLETE (Content-Length verified), so a dropped
/// connection never publishes a truncated file.</para>
///
/// <para>Binding port 80 and editing the hosts file need elevation; the in-process listener runs in the
/// (non-elevated) app once a one-time URL ACL is granted, and the hosts redirect is applied/reverted via a
/// single elevated helper on Start/Stop.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class XboxCacheProxyService : IDisposable
{
    private const string Prefix = "http://+:80/";
    private const string HostsBegin = "# BEGIN xbox-lan-cache";
    private const string HostsEnd = "# END xbox-lan-cache";

    private readonly HttpClient _live;
    private readonly HttpClient _bg;
    private readonly Dictionary<string, string> _dns = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly HashSet<string> _inProgress = new(StringComparer.OrdinalIgnoreCase);
    // Single-download tee: per-object fill state, keyed by the final cache path. Created on the first MISS
    // for an object and shared by every concurrent ranged request for it. The install stream is teed into a
    // sparse <file>.part at each chunk's Content-Range offset; the file is promoted (atomic rename) only when
    // the merged filled ranges cover [0, Total). A background sweep gap-fills any bytes the Store never
    // requested. This avoids the second full download that StartFill did — and keeps the package byte-for-byte
    // matched to what was installed. Guarded per-object by FillState.Lock; the dictionary itself by _gate.
    private sealed class FillState
    {
        public string File = "", Part = "", Host = "", RawPath = "", Ip = "";
        public long Total = -1;                              // full object size (Content-Range total / 200 Content-Length)
        public FileStream? Sparse;                           // single owner; all tee-writes go through Lock
        public readonly List<(long s, long e)> Filled = new(); // merged, sorted, half-open [s,e)
        public bool Promoted;
        public DateTime LastWriteUtc = DateTime.UtcNow;
        public readonly object Lock = new();
    }
    private readonly Dictionary<string, FillState> _fills = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _fillCts;
    private Thread? _fillSweep;

    // ---- streaming skeleton capture (experimental; off unless XboxStreamingCapture is set) --------------------
    // When armed for an object, its install download is captured to a small skeleton IN-STREAM — decrypt +
    // classify pages as they pass through the proxy, never writing the full encrypted package — instead of the
    // sparse-.part tee. Best-effort: any failure (no CIK yet, unsupported/ non-Fixed package, reorder-buffer
    // overflow, version-mismatch bloat) abandons streaming for that object and it falls back to the normal tee
    // (StartFill), so there is no regression. On complete coverage the armed controller is PARKED keyed by its
    // cache path (which contains the content GUID); the watcher finalizes it once the install is available.
    private bool _streamingEnabled;
    // When true, ALSO cache the full encrypted package to disk (the sparse-.part tee) — an opt-in LAN-caching
    // feature, and the fallback when streaming can't capture a title. When false, streaming is the only capture
    // path and a title that can't be streamed is simply forwarded (no skeleton, no disk copy) so the install
    // still completes. The Store always receives the genuine forwarded bytes regardless, so this flag only
    // affects what (if anything) this PC keeps on disk — never whether the install succeeds.
    private bool _teeEnabled;
    // Written by the background CIK prewarm (PrewarmCiks) and read by ArmFromBuffer on another thread.
    private volatile string _streamCikFolder = "";
    private string _streamBlobDir = "";
    private int _cikPrewarming;                               // 0/1 guard: at most one prewarm in flight
    private sealed class StreamState
    {
        public string File = "", Host = "", RawPath = "", Ip = "";
        public long Total = -1, FrontLen;
        public LibXboxOne.StreamingCaptureController? Ctl;
        public readonly List<(long s, long e)> Fed = new();   // ranges fed to the controller (coverage + gap-fill)
        // 1x-bandwidth capture: reuse the Store's OWN download instead of fetching a second copy. Bytes the
        // Store forwards before the controller is built are held here (bounded); we arm from the buffered front
        // (no separate front download) and then feed the buffer + all subsequent Store bytes into the capture.
        // Only genuine gaps (ranges the Store never requests) are refetched — a few MB, not the whole package.
        public readonly SortedDictionary<long, byte[]> PreArm = new();
        public long PreArmBytes, FrontContig;                 // FrontContig = contiguous byte coverage from 0 in PreArm
        public long LoggedFrontMb;                            // throttles the buffering-progress log
        // Front sizing. FrontLen starts at the default and is REVISED UPWARD once the header has buffered
        // (ProbeFrontLen) — the arm step reads at DriveDataOffset, which grows with package size. ForceFront
        // and PreArmCap track FrontLen: as fixed constants they would trip BEFORE a large front assembled and
        // arm prematurely on a short front, which is the very thing the probe exists to prevent.
        public bool FrontProbed;
        public long ForceFront = DefaultFrontLen + ForceFrontSlack;
        public long PreArmCap = DefaultFrontLen + PreArmSlack;
        public bool Armed, Aborted, Parked, Arming;
        // Progress-reporting only. Restored marks a park recovered from a previous run (it never streamed in
        // this session, so it has no meaningful start time of its own); Finalizing is set while the watcher's
        // finalize hook runs so the UI can distinguish "waiting for the install" from "working on it".
        public bool Restored, Finalizing;
        public long StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public string? TitleName;                             // resolved lazily via ResolveTitleName, then cached
        public DateTime LastWriteUtc = DateTime.UtcNow;
        // When the capture backlog first went over the soft cap (null = under it). Distinguishes a transient
        // burst — every capture starts with one, see CaptureBacklogGrace — from a consumer that can't keep up.
        public DateTime? OverCapSinceUtc;
        // Backlog caps for THIS stream. Arming hands the whole pre-arm buffer to the queue in one go, so the
        // queue legitimately starts life holding that much; the caps are raised by the drain size at arm and
        // drop to the base values once the backlog has actually been worked down to them (CapsSettled).
        // Sizing them from the drain is not slack: the drain MOVES bytes out of PreArm into the queue, so peak
        // memory is unchanged by it — aborting on the drain frees nothing and just discards a live capture.
        public long QueueSoftCap = CaptureQueueCapBytes;
        public long QueueHardCap = CaptureQueueHardCapBytes;
        public bool CapsSettled;
        // Spill/park files backing this capture. Deleted on abort and after a successful finalize; a park that
        // outlives the process is what a later run resumes from.
        public string BlobPath = "", FrontPath = "", ParkPath = "";
        // Set when PreArm has hit its cap while arming: further bytes are not buffered (the gap-fill refetches
        // those ranges at Complete) so a slow arm can't grow the buffer without bound. Only reached once the
        // stall budget below is spent — throttling briefly is preferred to dropping.
        public bool PreArmSaturated;
        // Throttling budget, measured in WALL CLOCK from the first stall. Summing per-thread stall time was
        // wrong: the Store downloads on ~8 parallel connections that all stall together, so a 60s budget was
        // spent in ~8s of real time and throttling switched itself off almost immediately. What we actually
        // care about is how long the download has been held back, which is elapsed time, not thread-seconds.
        public long ThrottleStartTicks;   // Environment.TickCount64 at the first stall, 0 = never stalled
        public long StallMs;              // aggregate thread stall, for reporting only
        public long LoggedStallSec;
        public readonly object Lock = new();
        // Background-capture queue: the download threads copy+enqueue (fast, never blocked by decrypt); one
        // consumer thread decrypts/feeds the controller. Blocks the download only if it fills (decrypt can't keep
        // up), so a fast download isn't throttled by per-chunk decrypt latency.
        public ChunkQueue? Queue;
    }
    // Front sizing. The 64 MB default is the span the offline test proved builds the map, and it holds as long
    // as the embedded drive starts within it. It does not for large packages: the arm step reads at
    // DriveDataOffset, which sits past the hash tree (~1/169 of the package), so it is ~22 MB into a 3.7 GB
    // package but ~121 MB into a 20 GB one — beyond a flat 64 MB front, where PartialPackageStream silently
    // serves zeros. ProbeFrontLen therefore reads the header (offset 0, cheap) and revises FrontLen to
    // DriveDataOffset + VolumeMetadataSpan. The slacks are ADDITIVE so the default front reproduces the
    // previous flat constants exactly (64+64 = 128, 64+192 = 256) while scaling with a larger front.
    private const long DefaultFrontLen = 64L << 20;
    private const long VolumeMetadataSpan = 64L << 20;        // partition table + NTFS boot sector + $MFT, past DriveDataOffset
    private const long ForceFrontSlack = 64L << 20;           // buffered FrontLen+this w/o a contiguous front → complete from the CDN + arm
    private const long PreArmSlack = 192L << 20;              // buffered FrontLen+this w/o a contiguous front → give up streaming
    // Background-capture backlog caps (the producer never blocks). The SOFT cap is the steady-state target: a
    // backlog above it only means the consumer is losing if it STAYS there for CaptureBacklogGrace — arming
    // drains the entire pre-arm buffer into the queue in one shot, so every capture legitimately starts out
    // near (or over) the soft cap and needs a moment to work it down. The HARD cap bounds memory regardless.
    private const long CaptureQueueCapBytes = 256L << 20;
    private const long CaptureQueueHardCapBytes = 640L << 20;
    private static readonly TimeSpan CaptureBacklogGrace = TimeSpan.FromSeconds(20);
    // How long a stream must go without bytes before the reaper treats it as finished (gap-fill if armed,
    // give up if not). An UNARMED stream gets much longer: assembling a package-sized front legitimately
    // involves lulls (the Store interleaves objects), and a 222 MB front is exposed to that far longer than
    // the old flat 64 MB one — reaping it at 30s throws away captures that were still progressing.
    // 30s was far too short for a package-sized download: a network blip or an Xbox app restart is enough to
    // make the Store go quiet, and the reaper then declared a 27 GB download "finished" at 55% coverage and
    // discarded the capture. Both states get the long timeout.
    private const int StreamIdleSeconds = 150;
    // Backpressure. Holding a download thread briefly so the capture can catch up is preferable to dropping
    // bytes (which leaves a hole the capture's reorder buffer then can't bridge) or giving up outright. Both
    // budgets are deliberately small: a single chunk waits at most CaptureStallBudget, and once a stream has
    // spent CaptureTotalStallBudgetMs in aggregate we stop throttling entirely and revert to the old
    // drop/give-up behaviour — the install must never be held up by a capture that is simply stuck.
    private static readonly TimeSpan CaptureStallBudget = TimeSpan.FromSeconds(2);
    // Wall-clock window, from the first stall, in which throttling is allowed at all. Not a sum of per-thread
    // stall time — the Store's ~8 parallel connections stall together, so summing spent a 60s budget in ~8s.
    private const long CaptureThrottleWindowMs = 120_000;
    private const int CaptureStallSliceMs = 10;
    // Update guard: how much of a package we'll pull from the CDN to COMPLETE a streaming capture, as a
    // fraction of the package total (gap-fill) / of the front (header holes). An update re-downloads only its
    // changed blocks and reuses the rest locally, so finishing its capture would re-fetch most of the game —
    // which we refuse: past these fractions we abandon capture and just forward the update (no extra download).
    // A fresh install delivers ~the whole package itself, so its completion cost stays well under these.
    // (Temporary — until cross-version reuse lands; see PLANNING/xbox-validation/update-reuse.)
    private const double MaxCompleteFraction = 0.15;         // gap-fill cap vs package total
    private const double MaxHeaderHoleFraction = 0.75;       // header-hole cap vs front

    /// <summary>Optional hook (set by the app): refresh the CIK store via CikExtractor (elevated) and return the
    /// folder now holding the keys, or null. Invoked when streaming detects a title's content key is missing, so
    /// it can be fetched and the capture retried while the download is still in flight (keeping the 1x path).</summary>
    public Func<string?>? RequestCikRefresh;

    /// <summary>A producer/consumer queue of encrypted chunks. <see cref="Add"/> NEVER blocks the producer — the
    /// download thread must never be throttled or frozen by decrypt latency. The cap is advisory and enforced
    /// entirely by the caller: <see cref="StreamOnStoreBytes"/> compares <see cref="Bytes"/> against the OWNING
    /// STREAM's soft/hard caps (which start raised by the arm-time drain and settle to the base values) and gives
    /// up capture (forward-only) when the backlog is genuinely runaway, so memory stays bounded without ever
    /// stalling the install. <see cref="TryTake"/> blocks until an item is available or the queue is closed and
    /// drained.</summary>
    private sealed class ChunkQueue
    {
        private readonly Queue<(long off, byte[] data)> _q = new();
        private readonly long _cap;
        private long _bytes;
        private bool _closed;
        private readonly object _g = new();
        public ChunkQueue(long capBytes) { _cap = capBytes; }
        public long Bytes { get { lock (_g) return _bytes; } }
        public long Cap => _cap;
        public void Add(long off, byte[] data)
        {
            lock (_g)
            {
                if (_closed) return;
                _q.Enqueue((off, data)); _bytes += data.Length; Monitor.PulseAll(_g);
            }
        }
        public bool TryTake(out (long off, byte[] data) item)
        {
            lock (_g)
            {
                while (_q.Count == 0 && !_closed) Monitor.Wait(_g);
                if (_q.Count == 0) { item = default; return false; }
                item = _q.Dequeue(); _bytes -= item.data.Length; Monitor.PulseAll(_g);
                return true;
            }
        }
        public void Close() { lock (_g) { _closed = true; Monitor.PulseAll(_g); } }
        /// <summary>Drops every queued chunk and returns how many bytes were released. Closing alone only
        /// stops new work — an aborted stream's consumer exits without draining, so the chunks (large object
        /// heap byte[]s) stay referenced by the queue until the whole state is collected.</summary>
        public long DiscardAll()
        {
            lock (_g) { long n = _bytes; _q.Clear(); _bytes = 0; Monitor.PulseAll(_g); return n; }
        }
    }
    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _streamAbandoned = new(StringComparer.OrdinalIgnoreCase);
    // Objects whose skeleton was already streamed-captured. Their package is intentionally NOT on disk, so a
    // later MISS (Gaming Services verify / trailing install reads / a peer) must be served transiently from the
    // CDN — never teed to disk (which would re-materialize the full package we just avoided).
    private readonly HashSet<string> _streamCaptured = new(StringComparer.OrdinalIgnoreCase);
    // Inert sentinel returned for already-captured objects: non-null so the caller skips the tee; Parked so
    // StreamOnStoreBytes is a no-op (the bytes just forward to the requester).
    private static readonly StreamState CapturedSentinel = new() { Parked = true };

    /// <summary>Arms streaming skeleton capture. When <paramref name="enabled"/>, the proxy tries to capture a
    /// title's skeleton in-stream (no full package on disk) instead of the sparse-.part tee, loading keys from
    /// <paramref name="cikFolder"/> and writing the growing skeleton blob under <paramref name="blobDir"/>.
    /// Falls back to the tee whenever streaming can't be armed or aborts. Safe to call before or after Start.</summary>
    public void ConfigureStreamingCapture(bool enabled, string? cikFolder, string? blobDir, bool cacheFullPackage = false)
    {
        _streamingEnabled = enabled;
        _teeEnabled = cacheFullPackage;
        _streamCikFolder = cikFolder ?? "";
        _streamBlobDir = string.IsNullOrWhiteSpace(blobDir)
            ? Path.Combine(Path.GetTempPath(), "gls-stream-blobs") : blobDir!;
        try { if (enabled) Directory.CreateDirectory(_streamBlobDir); } catch { }
        Log?.Invoke(enabled
            ? $"streaming capture ENABLED (cik: {_streamCikFolder}, blobs: {_streamBlobDir}); full-package cache {(_teeEnabled ? "ON" : "off")}"
            : $"streaming capture disabled; full-package cache {(_teeEnabled ? "ON" : "off")}");
        if (enabled) RestoreParks();
    }

    /// <summary>Re-registers captures parked by a previous run so they finalize normally. A parked capture is
    /// finished — every byte streamed and hashed — and only waits for the game to finish installing, which can
    /// outlast the app. Without this, closing the app discarded a completed capture and the whole title had to
    /// be downloaded again. Any park whose backing files are gone (or that no longer loads) is swept up.</summary>
    private void RestoreParks()
    {
        List<string> parks;
        try { parks = Directory.GetFiles(_streamBlobDir, "*.park").ToList(); }
        catch { return; }
        if (parks.Count == 0) return;

        int restored = 0;
        foreach (var park in parks)
        {
            string file = "", host = "", ip = "", rawPath = "", frontPath = "";
            long total = 0, frontLen = 0;
            void ReadOrigin(Stream s)
            {
                file = LibXboxOne.StreamingSkeletonizer.RStr(s);
                host = LibXboxOne.StreamingSkeletonizer.RStr(s);
                ip = LibXboxOne.StreamingSkeletonizer.RStr(s);
                rawPath = LibXboxOne.StreamingSkeletonizer.RStr(s);
                frontPath = LibXboxOne.StreamingSkeletonizer.RStr(s);
                total = (long)LibXboxOne.StreamingSkeletonizer.RU64(s);
                frontLen = (long)LibXboxOne.StreamingSkeletonizer.RU64(s);
            }

            // Peek the origin first: Resume needs the package size and front length to rebuild the stream.
            var ctl = LibXboxOne.StreamingSkeletonizer.TryReadOrigin(park, ReadOrigin) && total > 0
                ? LibXboxOne.StreamingCaptureController.Resume(park, frontPath, total, frontLen,
                    _streamCikFolder, Log, readOrigin: ReadOrigin)
                : null;
            if (ctl == null)
            {
                Log?.Invoke($"STREAM discarding an unusable parked capture ({Path.GetFileName(park)})");
                TryDelete(park); TryDelete(frontPath);
                continue;
            }

            var st = new StreamState
            {
                File = file, Host = host, Ip = ip, RawPath = rawPath, Total = total, FrontLen = frontLen,
                Ctl = ctl, Parked = true, BlobPath = park.Substring(0, park.Length - ".park".Length),
                FrontPath = frontPath, ParkPath = park, Restored = true,
            };
            lock (_gate) _streams[st.File] = st;
            restored++;
            Log?.Invoke($"STREAM restored a parked capture from a previous run: {Path.GetFileName(file)} "
                      + $"({total / 1048576.0:F0} MB) — it will finalize once the game is installed");
        }
        if (restored > 0) { StatsChanged?.Invoke(); RaiseCaptureProgress(force: true); }
    }

    private static string ProbeFrontPath(string parkPath)
    {
        var b = parkPath.Substring(0, parkPath.Length - ".park".Length);
        return b + ".front";
    }

    private static void TryDelete(string? p)
    {
        if (string.IsNullOrEmpty(p)) return;
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
    // Active peer-origin overrides: when a request's URL path contains one of these keys (e.g. a title's
    // PackageFullName / content GUID), forward it to a peer's streaming-reconstruct endpoint instead of the CDN
    // and serve it transiently (NO disk fill) - the streaming single-copy receive path.
    private readonly Dictionary<string, (string host, int port)> _peerOrigins = new(StringComparer.OrdinalIgnoreCase);
    // Extra roots (besides _cacheDir) checked for a HIT — e.g. an external drive holding a reconstructed package
    // for a Smart drive-receive. Served read-only/transient (never written, never deleted).
    private readonly HashSet<string> _serveRoots = new(StringComparer.OrdinalIgnoreCase);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _cacheDir = "";
    private string[] _hosts = { "assets1.xboxlive.com" };
    private bool _hostsApplied;

    public bool IsRunning { get; private set; }
    public string CacheDir => _cacheDir;

    // Live counters (surfaced to the UI).
    public long Hits, Misses, Cached, Filling, Errors, Bytes;
    // Active transfer progress: bytes served for the current title (peer-origin OR drive HIT) and the package
    // total. Reset when a transfer begins. <see cref="ProgressKey"/> is the URL-path substring that identifies
    // the active title's package (its content GUID / PackageFullName).
    public long PeerBytes, PeerTotal;
    public string? ProgressKey;

    /// <summary>Free-text log/status line.</summary>
    public event Action<string>? Log;
    /// <summary>Raised on any counter change so the UI can refresh stats.</summary>
    public event Action? StatsChanged;

    /// <summary>Raised (throttled to ~1/s, immediately on a stage change) while any streaming capture is live,
    /// so the UI can redraw its progress bars from <see cref="SnapshotCaptures"/>. This replaces scraping the
    /// <c>STREAM</c> log lines: the counters below are the same ones the capture itself runs on, so the bar
    /// cannot drift from reality the way a log-line match silently did whenever a message was reworded.</summary>
    public event Action? CaptureProgressChanged;

    /// <summary>Optional hook: maps a package cache path to the friendly installed-title name (the watcher
    /// owns the content-GUID → install-name index). Falls back to the package filename when unset or unknown —
    /// a capture starts before its title is installed, so a name is not always available yet.</summary>
    public Func<string, string?>? ResolveTitleName;

    public XboxCacheProxyService()
    {
        var h1 = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None };
        _live = new HttpClient(h1) { Timeout = TimeSpan.FromSeconds(30) };
        var h2 = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None };
        _bg = new HttpClient(h2) { Timeout = TimeSpan.FromMinutes(10) };
        ServicePointManager.DefaultConnectionLimit = 512;
        ServicePointManager.Expect100Continue = false;
    }

    /// <summary>
    /// Starts the proxy: resolves the real CDN IPs (bypassing hosts), grants the URL ACL + applies the hosts
    /// redirect (one elevated step), then binds the in-process listener on port 80. Returns false with a
    /// logged reason on failure.
    /// </summary>
    public async Task<bool> StartAsync(string cacheDir, IEnumerable<string>? originHosts = null)
    {
        if (IsRunning) return true;
        _cacheDir = string.IsNullOrWhiteSpace(cacheDir) ? Models.AppSettings.DefaultXboxCacheDir : cacheDir;
        var hosts = (originHosts ?? Enumerable.Empty<string>()).Where(h => !string.IsNullOrWhiteSpace(h)).ToArray();
        _hosts = hosts.Length > 0 ? hosts : new[] { "assets1.xboxlive.com" };

        try { Directory.CreateDirectory(_cacheDir); } catch (Exception ex) { Log?.Invoke($"cannot create cache dir: {ex.Message}"); return false; }

        // Resolve REAL origin IPs via a public resolver so forwards bypass our own hosts redirect.
        _dns.Clear();
        foreach (var h in _hosts)
        {
            var ip = ResolveARecord(h);
            if (ip != null) { _dns[h] = ip; Log?.Invoke($"origin {h} -> {ip} (real CDN)"); }
            else Log?.Invoke($"WARN: could not resolve real IP for {h}; misses for it will 404");
        }
        if (_dns.Count == 0) { Log?.Invoke("no origin hosts resolved; aborting start"); return false; }

        // Grant URL ACL (once) + apply hosts redirect to loopback — single elevated step (UAC).
        Log?.Invoke("requesting elevation to bind port 80 + redirect hosts (accept the UAC prompt) …");
        if (!await ApplyElevatedAsync())
        {
            Log?.Invoke("elevated setup failed/declined; proxy not started");
            return false;
        }
        _hostsApplied = true;

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"failed to bind {Prefix}: {ex.Message}");
            await RevertElevatedAsync();
            _hostsApplied = false;
            return false;
        }

        _cts = new CancellationTokenSource();
        Hits = Misses = Cached = Filling = Errors = Bytes = 0;
        IsRunning = true;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _fillCts = new CancellationTokenSource();
        _fillSweep = new Thread(() => GapFillSweep(_fillCts.Token)) { IsBackground = true, Name = "xbox-gapfill" };
        _fillSweep.Start();
        Log?.Invoke($"LAN cache proxy listening on :80  (cache: {_cacheDir})");
        StatsChanged?.Invoke();
        return true;
    }

    /// <summary>Stops the listener and reverts the hosts redirect (elevated). The URL ACL is left in place
    /// so future starts need no further elevation for binding.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning && !_hostsApplied) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _fillCts?.Cancel(); } catch { }
        // Discard any in-flight tee fills: an incomplete .part must never survive to look promotable.
        List<FillState> pending;
        lock (_gate) { pending = _fills.Values.ToList(); _fills.Clear(); }
        foreach (var st in pending)
        {
            lock (st.Lock)
            {
                if (st.Promoted) continue;
                try { st.Sparse?.Dispose(); } catch { }
                st.Sparse = null;
                try { if (File.Exists(st.Part)) File.Delete(st.Part); } catch { }
            }
            Interlocked.Decrement(ref Filling);
        }
        // Discard any in-flight streaming captures (an unfinalized skeleton is not kept).
        List<StreamState> pendingStreams;
        lock (_gate) { pendingStreams = _streams.Values.ToList(); _streams.Clear(); _streamAbandoned.Clear(); }
        foreach (var st in pendingStreams)
        {
            st.Queue?.Close();
            lock (st.Lock) { try { st.Ctl?.Dispose(); } catch { } st.Ctl = null; }
            if (!st.Parked && !st.Aborted) Interlocked.Decrement(ref Filling);
        }
        if (pendingStreams.Count > 0) RaiseCaptureProgress(force: true); // no captures left to draw
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
        if (_hostsApplied)
        {
            Log?.Invoke("reverting hosts redirect (accept the UAC prompt) …");
            await RevertElevatedAsync();
            _hostsApplied = false;
        }
        Log?.Invoke("LAN cache proxy stopped");
        StatsChanged?.Invoke();
    }

    // ---- peer streaming origins (receiver side of the single-copy peer transfer) ----------------------

    /// <summary>Registers a peer streaming origin: any request whose URL path contains <paramref name="matchKey"/>
    /// (e.g. the title's PackageFullName or content GUID) is forwarded to <c>http://host:port</c> instead of the
    /// CDN and served transiently (never written to disk). Used during a streaming peer install so the receiver
    /// pulls genuine bytes from the sender's on-the-fly reconstruct endpoint without ever storing the package.</summary>
    public void SetPeerOrigin(string matchKey, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(matchKey) || string.IsNullOrWhiteSpace(host) || port <= 0) return;
        Interlocked.Exchange(ref PeerBytes, 0);
        Interlocked.Exchange(ref PeerTotal, 0);
        ProgressKey = matchKey;
        lock (_gate) { _peerOrigins[matchKey] = (host, port); }
        Log?.Invoke($"peer origin set: \"{matchKey}\" -> {host}:{port} (transient, no disk)");
    }

    /// <summary>Removes a peer streaming origin (call when a streaming install finishes/aborts).</summary>
    public void ClearPeerOrigin(string matchKey)
    {
        if (string.IsNullOrWhiteSpace(matchKey)) return;
        lock (_gate) { _peerOrigins.Remove(matchKey); }
        ProgressKey = null;
        Log?.Invoke($"peer origin cleared: \"{matchKey}\"");
    }

    /// <summary>Removes all peer streaming origins.</summary>
    public void ClearAllPeerOrigins()
    {
        lock (_gate) { _peerOrigins.Clear(); }
    }

    private bool TryGetPeerOrigin(string rawPath, out string host, out int port)
    {
        host = ""; port = 0;
        lock (_gate)
        {
            foreach (var kv in _peerOrigins)
                if (rawPath.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    host = kv.Value.host; port = kv.Value.port; return true;
                }
        }
        return false;
    }

    // ---- drive serve (Smart receive: serve a reconstructed package straight from an external drive) --------

    /// <summary>Adds an external drive root (holding <c>&lt;root&gt;\assets1.xboxlive.com\…\&lt;PFN&gt;.msixvc</c>)
    /// as an extra HIT source and arms transfer-bar progress for it. The package is served read-only from the
    /// drive — never copied or written — so there is no extra storage on this PC.</summary>
    public void BeginDriveServe(string root, string matchKey, long total)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        lock (_gate) { _serveRoots.Add(root); }
        ProgressKey = matchKey;
        Interlocked.Exchange(ref PeerBytes, 0);
        Interlocked.Exchange(ref PeerTotal, total > 0 ? total : 0);
        Log?.Invoke($"serving from drive: {root} (key \"{matchKey}\")");
    }

    /// <summary>Stops serving from a drive root (call when the drive install is done/cancelled).</summary>
    public void EndDriveServe(string root)
    {
        if (!string.IsNullOrWhiteSpace(root)) lock (_gate) { _serveRoots.Remove(root); }
        ProgressKey = null;
    }

    /// <summary>Resolves a request to a file under the main cache dir or any registered extra serve root.</summary>
    private string ResolveServedFile(string hostHdr, string rel)
    {
        var primary = Path.Combine(Path.Combine(_cacheDir, hostHdr), rel);
        if (File.Exists(primary)) return primary;
        string[] roots;
        lock (_gate) { roots = _serveRoots.ToArray(); }
        foreach (var r in roots)
        {
            var cand = Path.Combine(Path.Combine(r, hostHdr), rel);
            if (File.Exists(cand)) return cand;
        }
        return primary; // default (likely a MISS)
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        while (!ct.IsCancellationRequested && listener != null)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        string rawPath = req.RawUrl ?? "/";
        string hostHdr = (req.UserHostName ?? "").Split(':')[0];
        string? range = req.Headers["Range"];
        bool started = false;
        try
        {
            string rel = rawPath;
            int qi = rel.IndexOf('?'); if (qi >= 0) rel = rel.Substring(0, qi);
            rel = rel.TrimStart('/').Replace('/', '\\');
            rel = Regex.Replace(rel, "[:*?\"<>|]", "_");
            // Resolve from the main cache dir, or any registered extra serve root (e.g. an external drive holding
            // a reconstructed package for a Smart drive-receive). First existing wins.
            string file = ResolveServedFile(hostHdr, rel);

            if (File.Exists(file))
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                long total = fs.Length, start = 0, end = total - 1; int code = 200;
                var m = Regex.Match(range ?? "", @"bytes=(\d+)-(\d*)");
                if (m.Success)
                {
                    start = long.Parse(m.Groups[1].Value);
                    if (m.Groups[2].Value != "") end = long.Parse(m.Groups[2].Value);
                    if (end > total - 1) end = total - 1;
                    // Unsatisfiable range (start past EOF, or inverted): answer 416 instead of computing a
                    // negative length (which would throw on ContentLength64 and 502 the request).
                    if (start > end || start > total - 1)
                    {
                        res.StatusCode = 416;
                        res.Headers["Content-Range"] = $"bytes */{total}";
                        res.ContentLength64 = 0;
                        started = true;
                        Interlocked.Increment(ref Hits);
                        StatsChanged?.Invoke();
                        return; // finally{} closes the response
                    }
                    code = 206;
                }
                long len = end - start + 1;
                res.StatusCode = code;
                res.Headers["Accept-Ranges"] = "bytes";
                if (code == 206) res.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
                res.ContentLength64 = len;
                started = true;
                fs.Position = start;
                byte[] buf = new byte[262144]; long remaining = len;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    int n = fs.Read(buf, 0, toRead);
                    if (n <= 0) break;
                    res.OutputStream.Write(buf, 0, n);
                    remaining -= n;
                }
                long servedHit = len - remaining;
                Interlocked.Increment(ref Hits);
                Interlocked.Add(ref Bytes, servedHit);
                // Drive-receive progress: count HITs for the active title toward the transfer bar.
                if (ProgressKey != null && rawPath.IndexOf(ProgressKey, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (PeerTotal == 0) Interlocked.Exchange(ref PeerTotal, total);
                    Interlocked.Add(ref PeerBytes, servedHit);
                }
                StatsChanged?.Invoke();
            }
            else if (!_dns.ContainsKey(hostHdr))
            {
                // not one of our origin hosts (WPAD/telemetry on :80) -> 404 instantly, never forward
                res.StatusCode = 404; started = true;
            }
            else
            {
                // MISS. If a peer streaming origin is registered for this path, forward to the peer's
                // reconstruct endpoint (transient - NO disk fill): the streaming single-copy receive. Otherwise
                // forward live to the real CDN and background-fill the package to disk as usual.
                bool viaPeer = TryGetPeerOrigin(rawPath, out var peerHost, out var peerPort);
                string target;
                string ip = "";
                if (viaPeer)
                {
                    target = $"http://{peerHost}:{peerPort}{rawPath}";
                }
                else
                {
                    ip = _dns[hostHdr];
                    target = "http://" + ip + rawPath;
                }
                var msg = new HttpRequestMessage(HttpMethod.Get, target);
                if (!viaPeer) msg.Headers.Host = hostHdr; // CDN needs the real host; the peer endpoint ignores it
                if (!string.IsNullOrEmpty(range)) msg.Headers.TryAddWithoutValidation("Range", range);
                long sent = 0; int sc;
                FillState? tee = null; long teeBase = 0; StreamState? scap = null;
                using (var resp = _live.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    sc = (int)resp.StatusCode;
                    res.StatusCode = sc;
                    ContentRangeHeaderValue? crange = resp.Content.Headers.ContentRange;
                    if (crange != null) res.Headers["Content-Range"] = crange.ToString();
                    if (viaPeer && crange?.Length != null && PeerTotal == 0)
                        System.Threading.Interlocked.Exchange(ref PeerTotal, crange.Length.Value);
                    long? cl = resp.Content.Headers.ContentLength;
                    if (cl != null) res.ContentLength64 = cl.Value;
                    res.Headers["Accept-Ranges"] = "bytes";
                    started = true;

                    // Single-download tee: cache the install bytes as they stream to the Store (no second pull).
                    // Learn the full object size from Content-Range total (206) or Content-Length (200). When it's
                    // known, tee this request's body into the shared sparse .part at its absolute offset; when it
                    // isn't, fall back to the legacy whole-object StartFill so the object is still cached (it
                    // self-guards against a tee already owning this file).
                    if (!viaPeer && (sc == 200 || sc == 206))
                    {
                        long objTotal = crange != null ? (crange.Length ?? -1) : (cl ?? -1);
                        teeBase = crange?.From ?? 0;
                        if (objTotal > 0)
                        {
                            // Streaming is the primary capture path. The sparse-.part tee only runs when the
                            // full-package cache is opted in AND streaming isn't handling this object (disabled,
                            // cached, or abandoned). With the tee off, an object streaming can't capture is just
                            // forwarded — the Store still gets its bytes, so the install completes regardless.
                            scap = TryGetStream(file, hostHdr, rawPath, ip, objTotal);
                            if (scap == null && _teeEnabled) tee = GetOrCreateFill(file, hostHdr, rawPath, ip, objTotal);
                        }
                        else if (_teeEnabled) StartFill(file, hostHdr, rawPath, ip); // unknown size: streaming can't arm
                    }

                    // Never write more than the declared Content-Length: if the upstream body runs longer than
                    // its header, HttpListener throws "Bytes to be written to the stream exceed the Content-Length"
                    // and aborts the response, corrupting the Store's download. Bound the copy to cl (when known).
                    long cap = cl ?? long.MaxValue;
                    using var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                    byte[] buf = new byte[262144]; int rn; long absOff = teeBase;
                    while (sent < cap)
                    {
                        int want = (int)Math.Min(buf.Length, cap - sent);
                        rn = stream.Read(buf, 0, want);
                        if (rn <= 0) break;
                        res.OutputStream.Write(buf, 0, rn);
                        if (tee != null) TeeWrite(tee, absOff, buf, rn);
                        // 1x-bandwidth streaming capture: reuse the Store's own forwarded bytes (buffer→arm→feed).
                        if (scap != null) StreamOnStoreBytes(scap, absOff, buf, rn);
                        absOff += rn;
                        sent += rn;
                    }
                    if (tee != null) EndTeeRange(tee, teeBase, sent);
                }
                Interlocked.Increment(ref Misses);
                Interlocked.Add(ref Bytes, sent);
                if (viaPeer) Interlocked.Add(ref PeerBytes, sent);
                StatsChanged?.Invoke();
            }
        }
        catch
        {
            if (!started) { try { res.StatusCode = 502; } catch { } }
            Interlocked.Increment(ref Errors);
            StatsChanged?.Invoke();
        }
        finally
        {
            try { res.OutputStream.Close(); } catch { }
            try { res.Close(); } catch { }
        }
    }

    // Start ONE background thread that downloads the whole object sequentially to .part, then renames it to
    // the final cache path — but only when the download is COMPLETE (Content-Length matches). A connection
    // dropped mid-fill leaves a short .part that is discarded so the cache is never poisoned.
    private void StartFill(string file, string hostHdr, string rawPath, string ip)
    {
        lock (_gate)
        {
            if (_inProgress.Contains(file) || File.Exists(file) || _fills.ContainsKey(file)) return;
            _inProgress.Add(file);
        }
        Interlocked.Increment(ref Filling);
        StatsChanged?.Invoke();
        var th = new Thread(() =>
        {
            string part = file + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var msg = new HttpRequestMessage(HttpMethod.Get, "http://" + ip + rawPath);
                msg.Headers.Host = hostHdr;
                using var resp = _bg.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                int sc = (int)resp.StatusCode;
                if (sc == 200)
                {
                    long expected = resp.Content.Headers.ContentLength ?? -1;
                    long written = 0;
                    using (var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var f = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        byte[] buf = new byte[1 << 20]; int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) { f.Write(buf, 0, n); written += n; }
                    }
                    if (expected >= 0 && written != expected)
                    {
                        try { File.Delete(part); } catch { }
                        Log?.Invoke($"FILL SHORT {Path.GetFileName(file)} got={written} expected={expected} (discarded; will refill)");
                    }
                    else
                    {
                        if (File.Exists(file)) File.Delete(part); else File.Move(part, file);
                        Interlocked.Increment(ref Cached);
                        Log?.Invoke($"FILL DONE {Path.GetFileName(file)} ({written / 1048576.0:F1} MB)");
                    }
                }
                else Log?.Invoke($"FILL skip code={sc} {rawPath}");
            }
            catch (Exception ex)
            {
                try { File.Delete(part); } catch { }
                Log?.Invoke($"FILL ERR {Path.GetFileName(file)}: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref Filling);
                lock (_gate) { _inProgress.Remove(file); }
                StatsChanged?.Invoke();
            }
        })
        { IsBackground = true };
        th.Start();
    }

    // ---- single-download tee (capture the install stream) ----------------

    /// <summary>Gets (or creates) the tee state for an object, sizing a sparse <c>.part</c> to
    /// <paramref name="total"/>. Returns null when the object is already cached, is being handled by a legacy
    /// <see cref="StartFill"/>, or the sparse file can't be created — in which case this request simply isn't
    /// teed (another request, or the gap-fill, still completes it).</summary>
    private FillState? GetOrCreateFill(string file, string host, string rawPath, string ip, long total)
    {
        lock (_gate)
        {
            if (File.Exists(file) || _inProgress.Contains(file)) return null; // cached, or a StartFill owns it
            if (_fills.TryGetValue(file, out var existing)) return existing.Promoted ? null : existing;
            var part = file + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                try { if (File.Exists(part)) File.Delete(part); } catch { } // discard a stale prior-run .part
                var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                fs.SetLength(total); // NTFS zero-fills lazily, so sizing a 10 GB file here is cheap
                var st = new FillState { File = file, Part = part, Host = host, RawPath = rawPath, Ip = ip, Total = total, Sparse = fs };
                _fills[file] = st;
                Interlocked.Increment(ref Filling);
                StatsChanged?.Invoke();
                return st;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"TEE init failed {Path.GetFileName(file)}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Writes one chunk of the live install stream into the object's sparse .part at its absolute
    /// offset. A disk error just leaves that span unfilled (the gap-fill covers it); it never corrupts the
    /// promoted file, which is gated on complete coverage.</summary>
    private static void TeeWrite(FillState st, long absOffset, byte[] buf, int count)
    {
        if (count <= 0) return;
        lock (st.Lock)
        {
            if (st.Promoted || st.Sparse == null) return;
            try { st.Sparse.Seek(absOffset, SeekOrigin.Begin); st.Sparse.Write(buf, 0, count); }
            catch { /* leave the range unmarked below is the caller's job; nothing is promoted early */ }
        }
    }

    /// <summary>Marks <c>[start, start+length)</c> as filled and promotes the object when the merged ranges
    /// cover <c>[0, Total)</c>.</summary>
    private void EndTeeRange(FillState st, long start, long length)
    {
        if (length <= 0) return;
        bool complete;
        lock (st.Lock)
        {
            if (st.Promoted) return;
            AddRangeLocked(st.Filled, start, start + length);
            st.LastWriteUtc = DateTime.UtcNow;
            complete = st.Total > 0 && st.Filled.Count == 1 && st.Filled[0].s <= 0 && st.Filled[0].e >= st.Total;
        }
        if (complete) Promote(st);
    }

    /// <summary>Flushes and atomically renames a fully-filled <c>.part</c> to the final cache file, then drops
    /// the tee state. Firing this makes the file a HIT and triggers the skeleton watcher's auto-capture.</summary>
    private void Promote(FillState st)
    {
        bool removed = false;
        lock (st.Lock)
        {
            if (st.Promoted) return;
            st.Promoted = true;
            try { st.Sparse?.Flush(); st.Sparse?.Dispose(); } catch { }
            st.Sparse = null;
            try
            {
                if (File.Exists(st.File)) { try { File.Delete(st.Part); } catch { } }
                else File.Move(st.Part, st.File);
                Interlocked.Increment(ref Cached);
                Log?.Invoke($"FILL DONE (tee) {Path.GetFileName(st.File)} ({st.Total / 1048576.0:F1} MB)");
                removed = true;
            }
            catch (Exception ex) { Log?.Invoke($"TEE promote failed {Path.GetFileName(st.File)}: {ex.Message}"); }
        }
        lock (_gate) { _fills.Remove(st.File); }
        Interlocked.Decrement(ref Filling);
        if (removed) StatsChanged?.Invoke();
    }

    /// <summary>Adds a half-open interval and re-merges the (kept sorted, non-overlapping) list in place.</summary>
    private static void AddRangeLocked(List<(long s, long e)> list, long s, long e)
    {
        if (e <= s) return;
        list.Add((s, e));
        list.Sort((a, b) => a.s.CompareTo(b.s));
        var merged = new List<(long s, long e)>();
        foreach (var iv in list)
        {
            if (merged.Count > 0 && iv.s <= merged[^1].e)
            {
                var last = merged[^1];
                if (iv.e > last.e) merged[^1] = (last.s, iv.e);
            }
            else merged.Add(iv);
        }
        list.Clear();
        list.AddRange(merged);
    }

    /// <summary>The complement of the filled ranges within <c>[0, total)</c> — the bytes the Store never
    /// requested, which the gap-fill fetches to complete the object.</summary>
    private static List<(long s, long e)> ComplementLocked(List<(long s, long e)> filled, long total)
    {
        var gaps = new List<(long s, long e)>();
        long cursor = 0;
        foreach (var iv in filled) // filled is merged & sorted
        {
            if (iv.s > cursor) gaps.Add((cursor, Math.Min(iv.s, total)));
            cursor = Math.Max(cursor, iv.e);
            if (cursor >= total) break;
        }
        if (cursor < total) gaps.Add((cursor, total));
        return gaps;
    }

    /// <summary>Background sweep: for any tee object that has gone idle (the install stopped requesting bytes)
    /// but isn't complete, fetch ONLY the missing ranges from the CDN so the object promotes. Typically a
    /// tiny transfer (trailing padding), never the whole package.</summary>
    private void GapFillSweep(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Thread.Sleep(10000); } catch { }
            if (ct.IsCancellationRequested) break;
            List<FillState> snapshot;
            lock (_gate) { snapshot = _fills.Values.ToList(); }
            foreach (var st in snapshot)
            {
                if (ct.IsCancellationRequested) break;
                bool idle; long total; List<(long s, long e)> gaps = new();
                lock (st.Lock)
                {
                    if (st.Promoted) continue;
                    total = st.Total;
                    idle = total > 0 && (DateTime.UtcNow - st.LastWriteUtc).TotalSeconds > 30;
                    if (idle) gaps = ComplementLocked(st.Filled, total);
                }
                if (!idle || gaps.Count == 0) continue;
                Log?.Invoke($"GAP-FILL {Path.GetFileName(st.File)}: {gaps.Count} missing range(s)");
                foreach (var g in gaps)
                {
                    if (ct.IsCancellationRequested) break;
                    try { FetchRange(st, g.s, g.e, ct); }
                    catch (Exception ex) { Log?.Invoke($"GAP-FILL ERR {Path.GetFileName(st.File)} [{g.s},{g.e}): {ex.Message}"); }
                }
            }

            // Streaming captures: once the Store goes idle, fetch only the ranges it never sent (trailing
            // padding / never-requested regions) and feed them so coverage completes. This is the ONLY extra
            // bandwidth on the 1x path — typically a few MB, not the whole package.
            List<StreamState> streamSnap;
            lock (_gate) { streamSnap = _streams.Values.ToList(); }
            foreach (var st in streamSnap)
            {
                if (ct.IsCancellationRequested) break;
                bool idle, armed, arming; long total; List<(long s, long e)> gaps = new();
                long frontContig, frontLen, preArmBytes;
                lock (st.Lock)
                {
                    if (st.Aborted || st.Parked) continue;
                    armed = st.Armed; arming = st.Arming;
                    total = st.Total;
                    frontContig = st.FrontContig; frontLen = st.FrontLen; preArmBytes = st.PreArmBytes;
                    idle = total > 0 && (DateTime.UtcNow - st.LastWriteUtc).TotalSeconds > StreamIdleSeconds;
                    if (idle && armed) gaps = ComplementLocked(st.Fed, total);
                }
                if (!idle) continue;
                if (!armed)
                {
                    if (arming) continue; // arming in progress — let ArmFromBuffer finish
                    // Two very different causes land here and used to report identically, which sends the reader
                    // hunting for an update that never happened. An UPDATE reuses local blocks, so the Store
                    // sends almost none of the front. Anything else means the front was partway assembled and
                    // the Store simply stopped — report how far it actually got. This matters more since the
                    // front became package-sized: a 222 MB front is exposed to an idle lull far longer than the
                    // old flat 64 MB one was.
                    AbandonStream(st, frontLen > 0 && frontContig < frontLen / 4
                        ? $"update: download finished without assembling the header (reused local blocks; only "
                          + $"{frontContig / 1048576.0:F0} of {frontLen / 1048576.0:F0} MB of the front was sent)"
                        : $"front [0,{frontLen / 1048576.0:F0} MB) never completed — Store sent {frontContig / 1048576.0:F0} MB "
                          + $"contiguous ({preArmBytes / 1048576.0:F0} MB buffered) then went idle for {StreamIdleSeconds}s; no skeleton");
                    continue;
                }
                if (gaps.Count == 0) continue;
                long gapBytes = 0; foreach (var g in gaps) gapBytes += g.e - g.s;
                // Don't re-download most of the package just to finish a skeleton — that's the "an update
                // re-downloads the whole game" behavior. Past the cap the Store reused local blocks (an update),
                // so abandon capture and forward only; the update installs from the Store's own bytes.
                if (gapBytes > (long)(total * MaxCompleteFraction))
                {
                    // Report the observation, not a guessed cause. An UPDATE (which reuses local blocks) and
                    // an INTERRUPTED download look identical from here — both leave a large unsent remainder —
                    // and this used to assert "update" either way, which sent the reader hunting for the wrong
                    // thing after a network drop mid-download.
                    double sentPct = total > 0 ? 100.0 * (total - gapBytes) / total : 0;
                    Log?.Invoke($"STREAM incomplete {Path.GetFileName(st.File)} — Store sent {(total - gapBytes) / 1048576.0:F1} of "
                              + $"{total / 1048576.0:F1} MB ({sentPct:F0}%) then stopped for {StreamIdleSeconds}s; completing the "
                              + $"skeleton would need {gapBytes / 1048576.0:F1} MB, so not fetching it. Expected for an update "
                              + $"(reuses local blocks); if the download was interrupted, restart it for a clean capture.");
                    StreamAbort(st, $"only {sentPct:F0}% of the package was sent; skipping gap-fill rather than re-downloading it");
                    continue;
                }
                Log?.Invoke($"STREAM gap-fill {Path.GetFileName(st.File)}: {gaps.Count} range(s), {gapBytes / 1048576.0:F1} MB the Store never sent");
                const long step = 8L << 20;
                foreach (var g in gaps)
                {
                    for (long s = g.s; s < g.e && !ct.IsCancellationRequested; s += step)
                    {
                        long e = Math.Min(s + step, g.e);
                        try
                        {
                            byte[]? data = FetchRangeBytes(st.Host, st.Ip, st.RawPath, s, e);
                            if (data == null) { Log?.Invoke($"STREAM gap-fill failed {Path.GetFileName(st.File)} [{s},{e})"); break; }
                            if (!StreamFeed(st, s, data, 0, data.Length)) break; // aborted → fell back to tee
                            StreamEndRange(st, s, data.Length);
                        }
                        catch (Exception ex) { Log?.Invoke($"STREAM gap-fill ERR {Path.GetFileName(st.File)}: {ex.Message}"); break; }
                    }
                }
            }
        }
    }

    /// <summary>Fetches a single byte range from the CDN and tees it into the object's sparse .part, promoting
    /// when this completes coverage.</summary>
    private void FetchRange(FillState st, long start, long endExcl, CancellationToken ct)
    {
        long len = endExcl - start;
        if (len <= 0) return;
        var msg = new HttpRequestMessage(HttpMethod.Get, "http://" + st.Ip + st.RawPath);
        msg.Headers.Host = st.Host;
        msg.Headers.Range = new RangeHeaderValue(start, endExcl - 1);
        using var resp = _bg.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
        int sc = (int)resp.StatusCode;
        if (sc != 200 && sc != 206) { Log?.Invoke($"GAP-FILL skip code={sc} {st.RawPath}"); return; }
        using var s = resp.Content.ReadAsStreamAsync(ct).GetAwaiter().GetResult();
        byte[] buf = new byte[1 << 20]; int n; long abs = start, got = 0;
        while (got < len && (n = s.Read(buf, 0, (int)Math.Min(buf.Length, len - got))) > 0)
        {
            TeeWrite(st, abs, buf, n); abs += n; got += n;
        }
        EndTeeRange(st, start, got);
    }

    // ---- streaming skeleton capture --------------------------------------

    /// <summary>A live streaming capture, as the UI sees it. <see cref="Percent"/> is progress within
    /// <see cref="Stage"/>, not through the whole job — the stages are wildly different lengths (a 64 MB front
    /// vs a 26 GB download vs a wait that can outlive the app), so a single blended number would be a lie.
    /// </summary>
    public sealed record StreamCaptureSnapshot(
        string Id, string Name, string Stage, long BytesDone, long BytesTotal, int Percent, long StartedAtMs);

    /// <summary>Minimum gap between unforced capture-progress notifications. One a second is well past what
    /// the eye reads on a progress bar, and the armed path would otherwise raise thousands per second.</summary>
    private const int ProgressThrottleMs = 1000;

    private long _lastProgressTicks;
    private int _progressPumpQueued;
    private readonly object _pumpGate = new();

    /// <summary>Point-in-time view of every live streaming capture, for the UI's progress bars. Aborted
    /// captures are omitted — they are no longer going to produce a skeleton.</summary>
    public List<StreamCaptureSnapshot> SnapshotCaptures()
    {
        List<StreamState> states;
        lock (_gate) states = _streams.Values.ToList();

        var result = new List<StreamCaptureSnapshot>(states.Count);
        foreach (var st in states)
        {
            string stage; long done, total, startedAt;
            lock (st.Lock)
            {
                if (st.Aborted) continue;
                startedAt = st.StartedAtMs;
                if (st.Finalizing)   { stage = "finalizing"; done = 0;            total = 0; }
                else if (st.Parked)  { stage = st.Restored ? "restored" : "waiting"; done = st.Total; total = st.Total; }
                else if (st.Armed)   { stage = "capturing";  done = CoveredBytesLocked(st); total = Math.Max(0, st.Total); }
                else                 { stage = "buffering";  done = Math.Min(st.FrontContig, st.FrontLen); total = st.FrontLen; }
            }
            // A finalize reports its own percent through the engine's "PROGRESS n" lines; -1 lets the UI show
            // an indeterminate bar until the first one lands rather than a bar pinned at 0%.
            int pct = stage == "finalizing" ? -1
                    : total > 0 ? (int)Math.Clamp(done * 100 / total, 0, 100)
                    : 0;
            result.Add(new StreamCaptureSnapshot(st.File, ResolveName(st), stage, done, total, pct, startedAt));
        }
        return result;
    }

    /// <summary>Total bytes of the package fed to the capture so far. Caller holds <c>st.Lock</c>.</summary>
    private static long CoveredBytesLocked(StreamState st)
    {
        long covered = 0;
        foreach (var (s, e) in st.Fed) covered += Math.Max(0, e - s);
        return st.Total > 0 ? Math.Min(covered, st.Total) : covered;
    }

    /// <summary>Friendly title name for a capture, resolved once and cached. A capture typically starts before
    /// its title exists on disk, so this stays the package filename until the install shows up.</summary>
    private string ResolveName(StreamState st)
    {
        var cached = st.TitleName;
        if (!string.IsNullOrEmpty(cached)) return cached!;
        string? name = null;
        try { name = ResolveTitleName?.Invoke(st.File); } catch { }
        if (string.IsNullOrWhiteSpace(name)) return Path.GetFileNameWithoutExtension(st.File);
        st.TitleName = name;
        return name!;
    }

    /// <summary>Signals the UI that capture progress moved. Throttled to ~1/s because the armed path calls this
    /// once per forwarded chunk (thousands per second on a fast link); <paramref name="force"/> bypasses the
    /// throttle for stage transitions, which are rare and must never be dropped.
    /// <para>The handler is NEVER run on the calling thread. Callers reach here holding <c>_gate</c> or
    /// <c>st.Lock</c> on the download's hot path, and the handler calls back into
    /// <see cref="SnapshotCaptures"/>, which takes both — running it inline would nest the proxy's locks
    /// through arbitrary UI code, the same shape as the finalize deadlock documented in
    /// <see cref="TryFinalizeStreamed"/>. Queueing it also keeps a slow handler off the download thread.</para>
    /// </summary>
    private void RaiseCaptureProgress(bool force = false)
    {
        var handler = CaptureProgressChanged;
        if (handler == null) return;
        long now = Environment.TickCount64;
        if (force) Interlocked.Exchange(ref _lastProgressTicks, now);
        else
        {
            long last = Interlocked.Read(ref _lastProgressTicks);
            if (now - last < ProgressThrottleMs) return;
            if (Interlocked.CompareExchange(ref _lastProgressTicks, now, last) != last) return; // another thread won
        }
        // Single-flight: one pending pump is enough, since the handler reads current state rather than a
        // queued delta. _pumpGate then serializes execution so two pumps can't publish snapshots out of order.
        if (Interlocked.Exchange(ref _progressPumpQueued, 1) == 1) return;
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                lock (_pumpGate)
                {
                    Interlocked.Exchange(ref _progressPumpQueued, 0);
                    try { handler(); } catch { }
                }
            }, null);
        }
        catch { Interlocked.Exchange(ref _progressPumpQueued, 0); }
    }

    /// <summary>Returns the streaming state that should capture this object, creating it on the first MISS.
    /// The capture is armed lazily from the Store's OWN forwarded front bytes (no separate front download) and
    /// fed from the Store's ongoing bytes — 1x bandwidth. Returns null when streaming is disabled, the object is
    /// already cached, or streaming was abandoned for it — the caller then uses the sparse-.part tee.</summary>
    private StreamState? TryGetStream(string file, string host, string rawPath, string ip, long total)
    {
        if (!_streamingEnabled) return null;
        lock (_gate)
        {
            if (File.Exists(file) || _inProgress.Contains(file) || _fills.ContainsKey(file)) return null;
            if (_streamAbandoned.Contains(file)) return null;               // arming failed earlier → use the tee
            if (_streamCaptured.Contains(file)) return CapturedSentinel;    // already captured → serve transient, no tee
            // Return the state even when Parked/Arming so the caller never tees an object streaming owns.
            if (_streams.TryGetValue(file, out var existing)) return existing;
            var st = new StreamState
            {
                File = file, Host = host, RawPath = rawPath, Ip = ip, Total = total,
                // 64 MB front is the size the offline test proved builds the map. It's reused from the Store's
                // OWN bytes (buffered, not separately downloaded), so a larger front costs memory, not bandwidth.
                FrontLen = Math.Min(DefaultFrontLen, total),
            };
            _streams[file] = st;
            Interlocked.Increment(ref Filling);
            StatsChanged?.Invoke();
            Log?.Invoke($"STREAM start {Path.GetFileName(file)} — capturing from the install download (arms once its first {st.FrontLen / 1048576} MB arrives)");
            PrewarmCiks();
            RaiseCaptureProgress(force: true);
            return st;
        }
    }

    /// <summary>Captures the Store's own forwarded bytes for the streaming skeleton — the 1x-bandwidth path.
    /// Before the controller is armed, buffers each chunk; once the front <c>[0,FrontLen)</c> has arrived, arms
    /// FROM those buffered bytes (no separate front download). After arming, feeds each chunk straight into the
    /// capture. Only genuine gaps (ranges the Store never sends) are refetched later by the gap-fill.</summary>
    private void StreamOnStoreBytes(StreamState st, long absOff, byte[] buf, int len)
    {
        if (len <= 0) return;
        ThrottleForCapture(st, len);
        bool feedNow = false, startArm = false, abandon = false; long progressMb = 0;
        long backlogGiveUp = 0; bool backlogHard = false; string? probedFront = null; long saturated = -1;
        lock (st.Lock)
        {
            if (st.Aborted || st.Parked) return;
            if (st.Armed)
            {
                feedNow = true;
                // Backlog gate. Sampling the queue once and giving up the moment it's over cap misfires on the
                // arm-time burst: ArmFromBuffer hands the whole pre-arm buffer over at once, so the consumer's
                // first instant is already at cap and the very next chunk killed the capture. Require the
                // backlog to stay over the soft cap for CaptureBacklogGrace before calling the consumer beaten.
                var q0 = st.Queue;
                long backlog = q0 == null ? 0 : q0.Bytes + len;
                if (q0 == null) st.OverCapSinceUtc = null;
                else
                {
                    // The arm-time burst is worked off once the backlog reaches the base cap; from then on this
                    // stream is held to the normal steady-state limits.
                    if (!st.CapsSettled && backlog <= CaptureQueueCapBytes)
                    {
                        st.CapsSettled = true;
                        st.QueueSoftCap = CaptureQueueCapBytes;
                        st.QueueHardCap = CaptureQueueHardCapBytes;
                    }
                    if (backlog <= st.QueueSoftCap) st.OverCapSinceUtc = null;
                    else
                    {
                        var now = DateTime.UtcNow;
                        st.OverCapSinceUtc ??= now;
                        if (backlog > st.QueueHardCap) { backlogGiveUp = backlog; backlogHard = true; feedNow = false; }
                        else if (now - st.OverCapSinceUtc.Value > CaptureBacklogGrace) { backlogGiveUp = backlog; feedNow = false; }
                    }
                }
            }
            else
            {
                // While Arming, the pre-arm buffer used to grow without bound: the cap below is only consulted
                // when !Arming, and arming a large package is slow (a 222 MB front on a 26.8 GB title took ~7
                // minutes), so the buffer reached >1 GB and blew the queue's cap the instant it was drained in.
                // Past the cap, stop buffering instead — those ranges simply aren't fed, and the gap-fill
                // refetches them at Complete. Bounded memory, capture preserved.
                bool store = true;
                if (st.Arming && st.PreArmBytes + len > st.PreArmCap)
                {
                    store = false;
                    if (!st.PreArmSaturated) { st.PreArmSaturated = true; saturated = st.PreArmBytes; }
                }
                // Keep the LONGEST chunk seen at each offset (parallel/overlapping Store requests can deliver a
                // short then a long chunk at the same offset). FrontContig is computed from the ACTUAL stored
                // bytes so the assembled front never has a zero-filled hole.
                if (store && (!st.PreArm.TryGetValue(absOff, out var existing) || existing.Length < len))
                {
                    var copy = new byte[len];
                    Array.Copy(buf, 0, copy, 0, len);
                    st.PreArm[absOff] = copy;
                    st.PreArmBytes += existing == null ? len : (len - existing.Length);
                }
                st.FrontContig = ContiguousFrontLocked(st.PreArm);
                st.LastWriteUtc = DateTime.UtcNow;
                // As soon as the header itself is contiguously buffered, size the front for THIS package.
                if (!st.FrontProbed && st.FrontContig >= LibXboxOne.XvdStreamLayout.HeaderProbeBytes)
                    probedFront = ProbeFrontLenLocked(st);
                if (!st.Arming)
                {
                    // Arm when the header is contiguously buffered, OR once we've buffered enough total (a
                    // parallel download whose front hasn't assembled) — ArmFromBuffer then fetches the few
                    // remaining header holes from the CDN. Only give up if even that much never accumulates.
                    if (st.FrontContig >= st.FrontLen || st.PreArmBytes >= st.ForceFront) { st.Arming = true; startArm = true; }
                    else if (st.PreArmBytes > st.PreArmCap) abandon = true;
                    else
                    {
                        long mb = st.FrontContig / (16L << 20); // log every ~16 MB of front progress
                        if (mb > st.LoggedFrontMb) { st.LoggedFrontMb = mb; progressMb = st.FrontContig; }
                    }
                }
            }
        }
        if (backlogGiveUp > 0)
        {
            StreamAbort(st, backlogHard
                ? $"decrypt backlog {backlogGiveUp / 1048576} MB (over the {st.QueueHardCap / 1048576} MB hard cap); giving up capture to keep the install at full speed"
                : $"decrypt backlog stuck at ~{backlogGiveUp / 1048576} MB for {CaptureBacklogGrace.TotalSeconds:F0}s; giving up capture to keep the install at full speed");
            return;
        }
        if (feedNow)
        {
            // Hand off to the background consumer — copy (buf is reused) + enqueue. Never decrypts on the
            // download thread. Add() never blocks; the backlog gate above (evaluated under st.Lock) decides when
            // the consumer is hopelessly behind, so we give up capture (forward-only) rather than stall the
            // Store's install — correctness of the download always wins over capturing this title.
            var copy = new byte[len];
            Array.Copy(buf, 0, copy, 0, len);
            st.Queue?.Add(absOff, copy);
            return;
        }
        RaiseCaptureProgress(); // pre-arm: the front is growing, so the buffering bar has moved
        if (probedFront != null) Log?.Invoke(probedFront);
        if (saturated >= 0)
            Log?.Invoke($"STREAM pre-arm buffer full {Path.GetFileName(st.File)} — {saturated / 1048576} MB held while arming; "
                      + "not buffering further bytes (their ranges are refetched when the skeleton is finalized)");
        if (progressMb > 0)
            Log?.Invoke($"STREAM buffering {Path.GetFileName(st.File)} — {progressMb / 1048576} / {st.FrontLen / 1048576} MB of the install download (arms at {st.FrontLen / 1048576} MB)");
        if (abandon) { AbandonStream(st, $"front [0,{st.FrontLen / 1048576} MB) not assembled within {st.PreArmCap / 1048576} MB (non-sequential download)"); return; }
        if (startArm) new Thread(() => ArmFromBuffer(st)) { IsBackground = true, Name = "xbox-stream-arm" }.Start();
    }

    /// <summary>Briefly holds the calling download thread while the capture catches up, rather than dropping
    /// bytes or giving up. Two cases are worth waiting on:
    /// <list type="bullet">
    /// <item><b>Arming</b> — the controller doesn't exist yet, so bytes can only be buffered. Dropping them
    /// leaves a hole the capture's reorder buffer later can't bridge (it must drain in ascending offset order),
    /// which killed a 26.8 GB capture 31s after it armed. Headroom appears the moment arming finishes and the
    /// buffer drains, so these waits are short.</item>
    /// <item><b>Armed</b> — the decrypt consumer is behind. Waiting lets it drain instead of abandoning the
    /// capture.</item>
    /// </list>
    /// <para>Never holds <c>st.Lock</c> while sleeping, and never waits unboundedly: a single chunk waits at
    /// most <see cref="CaptureStallBudget"/>, and once the stream's aggregate stall reaches
    /// <see cref="CaptureTotalStallBudgetMs"/> this returns immediately forever after, handing control back to
    /// the drop/give-up paths. The install finishing correctly always outranks capturing it.</para></summary>
    private void ThrottleForCapture(StreamState st, int len)
    {
        // Fast path runs for every chunk of every download, so it must stay allocation-free: two cheap checks
        // and out. Only a stream that actually needs headroom enters the wait loop.
        long start = Interlocked.Read(ref st.ThrottleStartTicks);
        long now = Environment.TickCount64;
        if (start != 0 && now - start >= CaptureThrottleWindowMs) return;   // budget spent
        if (!NeedsCaptureHeadroom(st, len)) return;
        if (start == 0)
        {
            Interlocked.CompareExchange(ref st.ThrottleStartTicks, now, 0);
            start = Interlocked.Read(ref st.ThrottleStartTicks);
        }

        long deadline = now + (long)CaptureStallBudget.TotalMilliseconds;
        long stalled = 0;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(CaptureStallSliceMs);
            stalled += CaptureStallSliceMs;
            if (Environment.TickCount64 - start >= CaptureThrottleWindowMs) break;
            if (!NeedsCaptureHeadroom(st, len)) break;
        }
        if (stalled <= 0) return;
        long total = Interlocked.Add(ref st.StallMs, stalled);
        // Log at most once per elapsed second of throttling — the previous version logged per thread per
        // slice, which buried the rest of the log under dozens of near-identical lines.
        long elapsedSec = (Environment.TickCount64 - start) / 1000;
        long logged = Interlocked.Read(ref st.LoggedStallSec);
        if (elapsedSec > logged && Interlocked.CompareExchange(ref st.LoggedStallSec, elapsedSec, logged) == logged)
            Log?.Invoke($"STREAM throttling {Path.GetFileName(st.File)} — holding the download so the capture can "
                      + $"keep up ({elapsedSec}s elapsed of a {CaptureThrottleWindowMs / 1000}s window, "
                      + $"{total / 1000.0:F0}s across all connections)");
    }

    /// <summary>True while this stream's capture is behind enough that holding the download briefly would
    /// help: the pre-arm buffer is full while arming, or the decrypt queue is over its soft cap.</summary>
    private static bool NeedsCaptureHeadroom(StreamState st, int len)
    {
        lock (st.Lock)
        {
            if (st.Aborted || st.Parked) return false;
            if (st.Armed) { var q = st.Queue; return q != null && q.Bytes + len > st.QueueSoftCap; }
            return st.Arming && st.PreArmBytes + len > st.PreArmCap;
        }
    }

    /// <summary>Sizes the front for THIS package from its just-buffered header. The arm step reads at
    /// <c>DriveDataOffset</c>, which sits past the hash tree and so grows with package size (~22 MB into a
    /// 3.7 GB package, ~121 MB into a 20 GB one) — a flat 64 MB front only reaches it below ~10.8 GB, and
    /// past that the arm silently reads zeros. Revises FrontLen (and the thresholds that gate it) upward;
    /// never shrinks it, so a package whose header says it needs less still gets the proven default.
    /// Caller must hold <c>st.Lock</c>. Returns a line to log outside the lock, or null.</summary>
    private static string? ProbeFrontLenLocked(StreamState st)
    {
        st.FrontProbed = true; // one attempt: a header that won't parse won't parse later either
        int probeLen = LibXboxOne.XvdStreamLayout.HeaderProbeBytes;
        var head = new byte[probeLen];
        foreach (var kv in st.PreArm) // ascending (SortedDictionary)
        {
            if (kv.Key >= probeLen) break;
            int copyLen = (int)Math.Min(kv.Value.Length, probeLen - kv.Key);
            if (copyLen > 0) Array.Copy(kv.Value, 0, head, (int)kv.Key, copyLen);
        }
        if (!LibXboxOne.XvdStreamLayout.TryGetRequiredFront(head, probeLen, st.Total, VolumeMetadataSpan,
                out long required, out long driveDataOffset, out string why))
            // Not fatal: fall through on the default front and let TryBegin decide (it now rejects a short
            // front loudly rather than capturing from zeros).
            return $"STREAM front probe {Path.GetFileName(st.File)} — using default {st.FrontLen / 1048576} MB ({why})";

        if (required <= st.FrontLen) return null; // default already covers it — the common small-package case
        st.FrontLen = required;
        st.ForceFront = required + ForceFrontSlack;
        st.PreArmCap = required + PreArmSlack;
        return $"STREAM front {Path.GetFileName(st.File)} — {st.Total / 1048576.0:F0} MB package puts its embedded drive at "
             + $"{driveDataOffset / 1048576.0:F0} MB; buffering {required / 1048576.0:F0} MB before arming";
    }

    /// <summary>Arms the controller from the buffered front bytes (no separate front download), then feeds the
    /// whole pre-arm buffer into the capture in order and switches to live feeding. The last sector (a few KB,
    /// which the Store may not have sent yet) is fetched separately — negligible bandwidth. On failure the
    /// object falls back to the tee.</summary>
    private void ArmFromBuffer(StreamState st)
    {
        // Phase timings. Arming a large package took ~7 minutes on a 26.8 GB title and the log gave no way to
        // tell whether that was CDN hole fetches or the NTFS map build inside TryBegin — so time each phase.
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var sw = new System.Diagnostics.Stopwatch();
        double tHoles = 0, tFront = 0, tTail = 0, tBegin = 0; long holeBytes = 0; int holeCount = 0;
        try
        {
            long fl = st.FrontLen;

            // Fix: complete any holes in [0,FrontLen) from the CDN so the header is whole regardless of the
            // Store's (possibly very parallel) download order. Extra bandwidth = just the holes (usually small).
            sw.Restart();
            var holes = FrontHoles(st, fl);
            if (holes.Count > 0)
            {
                long hb = 0; foreach (var h in holes) hb += h.e - h.s;
                // If the Store delivered almost none of the header, it's reusing local blocks (an update with an
                // unchanged header). Completing it would re-download the package, so don't — forward only.
                if (hb > (long)(fl * MaxHeaderHoleFraction))
                {
                    AbandonStream(st, $"update: header mostly unchanged ({hb / 1048576.0:F1} of {fl / 1048576.0:F1} MB not sent); not re-downloading to capture");
                    return;
                }
                Log?.Invoke($"STREAM completing header {Path.GetFileName(st.File)} — fetching {holes.Count} hole(s), {hb / 1048576.0:F1} MB the parallel download hadn't delivered");
                holeCount = holes.Count; holeBytes = hb;
                foreach (var h in holes)
                {
                    byte[]? data = FetchRangeBytes(st.Host, st.Ip, st.RawPath, h.s, h.e);
                    if (data == null) { AbandonStream(st, $"header hole fetch failed [{h.s},{h.e})"); return; }
                    lock (st.Lock) { st.PreArm[h.s] = data; st.PreArmBytes += data.Length; }
                }
            }
            tHoles = sw.Elapsed.TotalSeconds;

            sw.Restart();
            byte[] front = new byte[fl];
            lock (st.Lock)
            {
                foreach (var kv in st.PreArm) // ascending (SortedDictionary)
                {
                    if (kv.Key >= fl) break;
                    int copyLen = (int)Math.Min(kv.Value.Length, fl - kv.Key);
                    if (copyLen > 0) Array.Copy(kv.Value, 0, front, (int)kv.Key, copyLen);
                }
            }
            tFront = sw.Elapsed.TotalSeconds;

            sw.Restart();
            int tailLen = (int)Math.Min(4096, st.Total);
            long tailOff = st.Total - tailLen;
            byte[] tail = tailLen > 0 && tailOff >= fl
                ? (FetchRangeBytes(st.Host, st.Ip, st.RawPath, tailOff, tailOff + tailLen) ?? Array.Empty<byte>())
                : Array.Empty<byte>();
            tTail = sw.Elapsed.TotalSeconds;

            string blob = Path.Combine(_streamBlobDir, MakeBlobName(st.File));

            // Spill the assembled front to disk and hand the capture a FileStream over it. The front is the
            // biggest thing a capture holds (DriveDataOffset + 64 MB — ~222 MB for a 27 GB package) and it
            // stays resident until the skeleton is finalized, which waits on the game finishing installing.
            // NOT DeleteOnClose: a parked capture is resumable across a restart, and the front is what its
            // decrypt context is rebuilt from — so it has to outlive the process. Cleanup is explicit instead
            // (ReleaseBuffers on abort, DiscardPark after a successful finalize, and a sweep at startup).
            // On any IO failure fall back to the in-memory front — capturing matters more than the saving.
            FileStream? frontFs = null;
            st.BlobPath = blob;
            try
            {
                string frontPath = blob + ".front";
                using (var w = new FileStream(frontPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    w.Write(front, 0, (int)fl);
                frontFs = new FileStream(frontPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                                         FileOptions.RandomAccess);
                st.FrontPath = frontPath;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"STREAM front spill failed for {Path.GetFileName(st.File)} ({ex.Message}) — keeping it in memory");
                try { frontFs?.Dispose(); } catch { }
                frontFs = null;
            }

            sw.Restart();
            var ctl = TryBeginWithKeyRefresh(st, front, frontFs, fl, tail, tailOff, blob);
            if (ctl == null) { try { frontFs?.Dispose(); } catch { } }
            front = null!; // the capture reads from frontFs now; let the assembled array go
            tBegin = sw.Elapsed.TotalSeconds;
            Log?.Invoke($"STREAM arm timing {Path.GetFileName(st.File)} — holes {tHoles:F1}s ({holeCount}, {holeBytes / 1048576.0:F1} MB), "
                      + $"front {tFront:F1}s, tail {tTail:F1}s, begin {tBegin:F1}s, total {swTotal.Elapsed.TotalSeconds:F1}s");
            if (ctl == null) { AbandonStream(st, "could not arm (see prior line)"); return; }

            // Start the background consumer, then hand off the buffered bytes + switch to live enqueueing.
            var consumer = new Thread(() => StreamConsumer(st)) { IsBackground = true, Name = "xbox-stream-consume" };
            List<(long off, byte[] data)> drain;
            long drainBytes;
            lock (st.Lock)
            {
                st.Ctl = ctl;
                drain = st.PreArm.Select(kv => (kv.Key, kv.Value)).ToList();
                drainBytes = st.PreArmBytes;
                // Raise this stream's caps by what we're about to hand the queue. These bytes are already
                // resident (the drain moves them out of PreArm), so admitting them costs no extra memory —
                // whereas rejecting them aborts a capture to defend a limit already breached. The caps fall
                // back to the base values once the consumer has worked the backlog down (see CapsSettled).
                st.QueueSoftCap = CaptureQueueCapBytes + drainBytes;
                st.QueueHardCap = CaptureQueueHardCapBytes + drainBytes;
                st.CapsSettled = false;
                st.Queue = new ChunkQueue(st.QueueHardCap);
                st.PreArm.Clear(); st.PreArmBytes = 0;
                st.Armed = true; st.Arming = false; st.LastWriteUtc = DateTime.UtcNow;
            }
            var queue = st.Queue!;
            consumer.Start();
            Log?.Invoke($"STREAM armed {Path.GetFileName(st.File)} ({st.Total / 1048576.0:F0} MB) from the Store's own bytes — 1x bandwidth"
                      + (drainBytes > CaptureQueueCapBytes ? $"; handing {drainBytes / 1048576} MB of buffered bytes to the capture" : ""));
            RaiseCaptureProgress(force: true); // buffering → capturing
            // Enqueue everything buffered so far; live bytes now arriving (Armed==true) enqueue too. The consumer
            // feeds the controller, which reorders by offset — enqueue order doesn't matter.
            foreach (var (off, data) in drain) queue.Add(off, data);
        }
        catch (Exception ex) { AbandonStream(st, ex.Message); }
    }

    /// <summary>The missing sub-ranges of <c>[0, fl)</c> not covered by the buffered chunks — the header pieces
    /// a parallel download hasn't delivered yet, which <see cref="ArmFromBuffer"/> fetches from the CDN.</summary>
    private static List<(long s, long e)> FrontHoles(StreamState st, long fl)
    {
        var covered = new List<(long s, long e)>();
        lock (st.Lock)
            foreach (var kv in st.PreArm)
                if (kv.Key < fl) { long e = Math.Min(kv.Key + kv.Value.Length, fl); if (e > kv.Key) covered.Add((kv.Key, e)); }
        covered.Sort((a, b) => a.s.CompareTo(b.s));
        var merged = new List<(long s, long e)>();
        foreach (var iv in covered)
            if (merged.Count > 0 && iv.s <= merged[^1].e) { if (iv.e > merged[^1].e) merged[^1] = (merged[^1].s, iv.e); }
            else merged.Add(iv);
        var holes = new List<(long s, long e)>(); long cur = 0;
        foreach (var iv in merged) { if (iv.s > cur) holes.Add((cur, iv.s)); cur = Math.Max(cur, iv.e); if (cur >= fl) break; }
        if (cur < fl) holes.Add((cur, fl));
        return holes;
    }

    /// <summary>Kicks a CIK-store refresh in the BACKGROUND when a package download starts, so the key is already
    /// in the store by the time the front is buffered and <see cref="ArmFromBuffer"/> arms on its first try.
    /// <para>Without it, a missing key is only discovered inside ArmFromBuffer, which then runs CikExtractor
    /// elevated <b>synchronously</b> (10-25 s observed). The download isn't paused for that, and while
    /// <c>Arming</c> is set the pre-arm buffer grows unbounded — so arming drained a &gt;cap pile straight into the
    /// capture queue and the capture was given up the same second it armed. Prewarming removes that window.</para>
    /// <para>Safe to call speculatively: the hook is rate-limited (10 min) and dumps ALL device keys in one run,
    /// so calls within the cooldown reuse the store without prompting.</para></summary>
    private void PrewarmCiks()
    {
        if (RequestCikRefresh == null) return;
        if (Interlocked.CompareExchange(ref _cikPrewarming, 1, 0) != 0) return; // one in flight is enough
        new Thread(() =>
        {
            try
            {
                var folder = RequestCikRefresh?.Invoke();
                if (!string.IsNullOrEmpty(folder)) _streamCikFolder = folder!;
            }
            catch (Exception ex) { Log?.Invoke($"STREAM CIK prewarm error: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _cikPrewarming, 0); }
        })
        { IsBackground = true, Name = "xbox-cik-prewarm" }.Start();
    }

    /// <summary>Builds the capture controller; if it can't because a content key is missing, refreshes the CIK
    /// store (CikExtractor, elevated) once and retries — the download keeps buffering meanwhile, so a title
    /// whose key wasn't ready is still captured at 1x. Returns null (reason already logged) if it still can't.</summary>
    private LibXboxOne.StreamingCaptureController? TryBeginWithKeyRefresh(StreamState st, byte[] front,
        FileStream? frontFs, long fl, byte[] tail, long tailOff, string blob)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string lastMsg = "";
            var ctl = frontFs != null
                ? LibXboxOne.StreamingCaptureController.TryBeginFromFile(st.Total, frontFs, fl, tail, tailOff,
                    _streamCikFolder, blob, m => { lastMsg = m; Log?.Invoke(m); }, bufferCap: 256L << 20)
                : LibXboxOne.StreamingCaptureController.TryBegin(st.Total, front, fl, tail, tailOff,
                    _streamCikFolder, blob, m => { lastMsg = m; Log?.Invoke(m); }, bufferCap: 256L << 20);
            if (ctl != null) return ctl;
            if (attempt == 0 && lastMsg.IndexOf("MISSING KEY", StringComparison.OrdinalIgnoreCase) >= 0 && RequestCikRefresh != null)
            {
                Log?.Invoke($"STREAM {Path.GetFileName(st.File)} — content key not in the store; refreshing CIKs (accept the UAC prompt) …");
                string? folder = null;
                try { folder = RequestCikRefresh(); } catch (Exception ex) { Log?.Invoke($"STREAM CIK refresh error: {ex.Message}"); }
                if (!string.IsNullOrEmpty(folder)) { _streamCikFolder = folder!; continue; } // retry with refreshed keys
            }
            return null;
        }
        return null;
    }

    /// <summary>Background consumer: dequeues encrypted chunks and feeds them to the capture controller
    /// (decrypt/classify happen here, off the download threads). Exits when the queue is closed and drained
    /// (park/abort/stop).</summary>
    private void StreamConsumer(StreamState st)
    {
        var q = st.Queue;
        if (q == null) return;
        try
        {
            while (q.TryTake(out var item))
            {
                if (st.Aborted) break;
                if (StreamFeed(st, item.off, item.data, 0, item.data.Length))
                    StreamEndRange(st, item.off, item.data.Length);
                else break; // aborted inside StreamFeed (error) → gave up capture
            }
        }
        catch (Exception ex)
        {
            // The consumer is the ONLY thing that drains and closes the queue. If decrypt/classify throws (a
            // LibXboxOne edge case, a lazy dependency-resolution failure, a late CIK) and this thread died
            // without closing the queue, every download thread would block on the queue and the install would
            // freeze near 100% with no CPU. Give up capture instead: StreamAbort closes the queue and unblocks
            // the download; the Store keeps its already-forwarded bytes.
            StreamAbort(st, $"capture consumer error: {ex.Message}");
        }
    }

    /// <summary>Feeds one chunk to the streaming controller. Returns false once the capture has aborted (the
    /// caller stops feeding; a whole-object tee download is kicked so the object still caches).</summary>
    private bool StreamFeed(StreamState st, long absOff, byte[] buf, int off, int len)
    {
        LibXboxOne.StreamingCaptureController? ctl;
        lock (st.Lock) { if (st.Aborted || st.Parked || st.Ctl == null) return !st.Aborted; ctl = st.Ctl; st.LastWriteUtc = DateTime.UtcNow; }
        if (!ctl.Feed(absOff, buf, off, len)) { StreamAbort(st, ctl.AbortReason); return false; }
        return true;
    }

    /// <summary>Marks <c>[start,start+length)</c> covered and parks the controller when coverage reaches
    /// <c>[0, Total)</c>.</summary>
    private void StreamEndRange(StreamState st, long start, long length)
    {
        if (length <= 0) return;
        bool complete;
        lock (st.Lock)
        {
            if (st.Aborted || st.Parked) return;
            AddRangeLocked(st.Fed, start, start + length);
            st.LastWriteUtc = DateTime.UtcNow;
            complete = st.Total > 0 && st.Fed.Count == 1 && st.Fed[0].s <= 0 && st.Fed[0].e >= st.Total;
            if (complete) st.Parked = true;
        }
        if (!complete)
        {
            RaiseCaptureProgress(); // armed: coverage grew, so the capture bar has moved
            return;
        }
        st.Queue?.Close(); // coverage done → let the background consumer exit
        Interlocked.Decrement(ref Filling);
        Log?.Invoke($"STREAM ready {Path.GetFileName(st.File)} — awaiting install to finalize skeleton");
        SavePark(st);
        StatsChanged?.Invoke();
        RaiseCaptureProgress(force: true); // capturing → waiting
    }

    /// <summary>Persists a just-parked capture so it survives the app closing. A park can wait a long time —
    /// the skeleton can only be finalized once the game has finished installing — and until now that whole
    /// wait lived in memory: closing the app threw away a completed capture and the title had to be
    /// downloaded again. Everything heavy (blob, structural region, front) is already on disk; this writes the
    /// small binding table plus the origin needed to refetch at finalize.</summary>
    private void SavePark(StreamState st)
    {
        var ctl = st.Ctl;
        if (ctl == null || string.IsNullOrEmpty(st.FrontPath)) return;
        try
        {
            var park = st.BlobPath + ".park";
            ctl.SavePark(park, s =>
            {
                LibXboxOne.StreamingSkeletonizer.WStr(s, st.File);
                LibXboxOne.StreamingSkeletonizer.WStr(s, st.Host);
                LibXboxOne.StreamingSkeletonizer.WStr(s, st.Ip);
                LibXboxOne.StreamingSkeletonizer.WStr(s, st.RawPath);
                LibXboxOne.StreamingSkeletonizer.WStr(s, st.FrontPath);
                WriteU64(s, (ulong)st.Total);
                WriteU64(s, (ulong)st.FrontLen);
            });
            st.ParkPath = park;
            Log?.Invoke($"STREAM parked {Path.GetFileName(st.File)} — saved to disk; it will finalize even if the app restarts");
            RaiseCaptureProgress(force: true);
        }
        catch (Exception ex) { Log?.Invoke($"STREAM park save failed for {Path.GetFileName(st.File)}: {ex.Message}"); }
    }

    private static void WriteU64(Stream s, ulong v) { for (int i = 0; i < 8; i++) s.WriteByte((byte)(v >> 8 * i)); }

    /// <summary>Gives up streaming for an object. With the full-package cache opted in, kicks a normal
    /// whole-object download so it still caches; otherwise the object is just forwarded (no skeleton, no disk
    /// copy) — the Store already has/gets its bytes either way, so the install is unaffected. Rare: only on a
    /// reorder-buffer overflow, version-mismatch bloat, a decrypt error, or the decrypt backlog filling.</summary>
    private void StreamAbort(StreamState st, string reason)
    {
        lock (st.Lock) { if (st.Aborted) return; st.Aborted = true; try { st.Ctl?.Dispose(); } catch { } st.Ctl = null; }
        st.Queue?.Close();
        lock (_gate) { _streams.Remove(st.File); _streamAbandoned.Add(st.File); }
        Interlocked.Decrement(ref Filling);
        ReleaseBuffers(st);
        GiveUpCapture(st, reason);
    }

    /// <summary>Drops the buffered bytes a dead stream is still holding. Without this an abandoned capture
    /// kept its whole pre-arm buffer (up to PreArmCap) AND everything sitting in the chunk queue alive: the
    /// consumer exits on <c>Aborted</c> without draining, and <see cref="ChunkQueue.Close"/> only flips a
    /// flag. Those are multi-hundred-MB byte[] allocations on the large object heap, which is why the process
    /// sat at ~1.7 GB after giving up a capture. Freeing them here makes the memory collectable immediately
    /// instead of whenever the last thread referencing the state happens to exit.</summary>
    private void ReleaseBuffers(StreamState st)
    {
        long freed;
        lock (st.Lock)
        {
            freed = st.PreArmBytes;
            st.PreArm.Clear();
            st.PreArmBytes = 0; st.FrontContig = 0;
        }
        freed += st.Queue?.DiscardAll() ?? 0;
        if (freed > 64L << 20)
            Log?.Invoke($"STREAM released {freed / 1048576} MB of buffered bytes from {Path.GetFileName(st.File)}");
        DiscardPark(st);
    }

    /// <summary>Removes the on-disk spill/park for a capture that is finished with — aborted, or finalized.
    /// The skeletonizer's own Dispose removes the blob and structural spill; the front and park are the
    /// proxy's to clean, since it opened them deliberately without DeleteOnClose so a park could outlive the
    /// process.</summary>
    private static void DiscardPark(StreamState st)
    {
        foreach (var p in new[] { st.ParkPath, st.FrontPath })
        {
            if (string.IsNullOrEmpty(p)) continue;
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
        st.ParkPath = ""; st.FrontPath = "";
    }

    private void AbandonStream(StreamState st, string reason)
    {
        lock (st.Lock) { st.Aborted = true; st.Arming = false; try { st.Ctl?.Dispose(); } catch { } st.Ctl = null; }
        st.Queue?.Close();
        lock (_gate) { _streams.Remove(st.File); _streamAbandoned.Add(st.File); }
        Interlocked.Decrement(ref Filling);
        ReleaseBuffers(st);
        GiveUpCapture(st, reason);
    }

    /// <summary>Shared tail of the give-up paths: when the full-package cache is opted in, fall back to the tee
    /// (whole-object download) so the package is still cached; otherwise forward-only (nothing kept on disk).</summary>
    private void GiveUpCapture(StreamState st, string reason)
    {
        if (_teeEnabled)
        {
            Log?.Invoke($"STREAM give up {Path.GetFileName(st.File)} — {reason}; caching full package instead");
            StartFill(st.File, st.Host, st.RawPath, st.Ip);
        }
        else
            Log?.Invoke($"STREAM give up {Path.GetFileName(st.File)} — {reason}; forwarding only (no skeleton, no disk copy)");
        StatsChanged?.Invoke();
        RaiseCaptureProgress(force: true); // the capture is off the books — drop its row rather than leave it stuck
    }

    /// <summary>Finalizes a parked streamed skeleton against a now-available install. Matches the parked
    /// controller by <paramref name="contentGuid"/> (the proxy nests the package under it in the cache path).
    /// Returns true and writes the <c>.skl</c> to <paramref name="skelPath"/> on success.</summary>
    public bool TryFinalizeStreamed(string? contentGuid, string installDir, string skelPath, out string status, out string? servedPath)
    {
        status = ""; servedPath = null;
        StreamState? st = null;
        lock (_gate)
        {
            foreach (var s in _streams.Values)
                if (s.Parked && !s.Aborted &&
                    (string.IsNullOrEmpty(contentGuid) || s.File.IndexOf(contentGuid, StringComparison.OrdinalIgnoreCase) >= 0))
                { st = s; break; }
        }
        if (st?.Ctl == null) { status = "no matching streamed capture"; return false; }
        servedPath = st.File;

        // Do NOT hold st.Lock across Complete(). Complete() runs WriteXskl and makes BLOCKING CDN range fetches
        // (FetchRangeBytes) for every byte the Store never sent. The producer (StreamOnStoreBytes) and consumer
        // both take st.Lock on their hot path, so holding it here froze every in-flight Store request on st.Lock
        // and jammed the thread pool — the async fetch continuation could then never get a worker thread, so
        // Complete() never returned and the whole proxy deadlocked at 100% (0 CPU, all threads in Wait). The
        // stream is Parked before finalize, so the controller is idle and its origin fields are immutable:
        // snapshot them and finalize lock-free.
        LibXboxOne.StreamingCaptureController? ctl; string host, ip, rawPath;
        lock (st.Lock) { ctl = st.Ctl; host = st.Host; ip = st.Ip; rawPath = st.RawPath; }
        if (ctl == null) { status = "no matching streamed capture"; return false; }

        // Flip the row to "finalizing" for the duration. Complete() can run for many minutes on a large title,
        // and it is the one stage where the row would otherwise still read "waiting for the install" while the
        // app is in fact working hard on it. Cleared in the finally so a throwing Complete() can't strand it.
        lock (st.Lock) st.Finalizing = true;
        RaiseCaptureProgress(force: true);

        bool ok;
        try
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(skelPath)!); } catch { }
            ok = ctl.Complete(installDir, skelPath,
                (o, c) => FetchRangeBytes(host, ip, rawPath, o, o + c) ?? new byte[c], out status);
        }
        finally { lock (st.Lock) st.Finalizing = false; }

        if (ok)
        {
            // Captured → the package is intentionally not on disk; serve future MISSes transiently, never tee.
            lock (_gate) { _streams.Remove(st.File); _streamCaptured.Add(st.File); }
            try { st.Ctl.Dispose(); } catch { }
            DiscardPark(st);   // the skeleton is written; the park it could have been rebuilt from is now dead weight
            Log?.Invoke($"STREAM finalized {Path.GetFileName(st.File)} — {status}");
            StatsChanged?.Invoke();
        }
        RaiseCaptureProgress(force: true); // finalized (row gone) or back to waiting for a retry
        return ok;
    }

    /// <summary>True if a parked streamed capture matches the content GUID (so the watcher skips batch capture).</summary>
    public bool HasParkedStreamed(string? contentGuid)
    {
        lock (_gate)
            foreach (var s in _streams.Values)
                if (s.Parked && !s.Aborted &&
                    (string.IsNullOrEmpty(contentGuid) || s.File.IndexOf(contentGuid, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
        return false;
    }

    /// <summary>Ranged GET returning the raw bytes of <c>[start, endExcl)</c> from the origin, or null on
    /// failure. Used to prefetch the streaming header and to refetch unmatched-drop bytes at finalize.</summary>
    private byte[]? FetchRangeBytes(string host, string ip, string rawPath, long start, long endExcl)
    {
        long len = endExcl - start;
        if (len <= 0) return Array.Empty<byte>();
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Get, "http://" + ip + rawPath);
            msg.Headers.Host = host;
            msg.Headers.Range = new RangeHeaderValue(start, endExcl - 1);
            using var resp = _bg.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            int sc = (int)resp.StatusCode;
            if (sc != 200 && sc != 206) return null;
            using var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            var outBuf = new byte[len]; long got = 0; int n;
            var tmp = new byte[1 << 20];
            while (got < len && (n = s.Read(tmp, 0, (int)Math.Min(tmp.Length, len - got))) > 0)
            { Array.Copy(tmp, 0, outBuf, got, n); got += n; }
            return got == len ? outBuf : null;
        }
        catch { return null; }
    }

    /// <summary>Contiguous byte coverage from offset 0 across the buffered chunks (walks ascending, stops at the
    /// first gap). Used to decide when the front is complete enough to arm — from the ACTUAL stored bytes.</summary>
    private static long ContiguousFrontLocked(SortedDictionary<long, byte[]> pre)
    {
        long end = 0;
        foreach (var kv in pre)
        {
            if (kv.Key > end) break;             // gap before this chunk
            long e = kv.Key + kv.Value.Length;
            if (e > end) end = e;
        }
        return end;
    }

    private static string MakeBlobName(string file)
    {
        // Stable per-object blob name (hash of the cache path) so restarts don't collide.
        var bytes = System.Text.Encoding.UTF8.GetBytes(file);
        uint h = 2166136261; foreach (var b in bytes) { h = (h ^ b) * 16777619; }
        return $"{Path.GetFileNameWithoutExtension(file)}.{h:x8}.blob.tmp";
    }

    // ---- elevation (URL ACL + hosts) -------------------------------------

    private Task<bool> ApplyElevatedAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
        // URL ACL so the non-elevated app can bind http://+:80/ on later starts.
        // Grant the listen right via SDDL (Everyone = WD) so it's locale-independent (user=Everyone fails on
        // non-English Windows). Quoted so PowerShell passes the parens to netsh literally.
        sb.AppendLine("$acl = netsh http show urlacl url=http://+:80/ 2>$null | Out-String");
        sb.AppendLine("if ($acl -notmatch '\\+:80') { netsh http add urlacl url=http://+:80/ \"sddl=D:(A;;GX;;;WD)\" | Out-Null }");
        // Hosts redirect: our origin hosts -> loopback (this PC's Store hits the local proxy).
        sb.AppendLine("$hp=\"$env:WINDIR\\System32\\drivers\\etc\\hosts\"");
        sb.AppendLine($"$b='{HostsBegin}'; $e='{HostsEnd}'");
        sb.AppendLine("$c=@(); try { $c = Get-Content -LiteralPath $hp -ErrorAction Stop } catch {}");
        sb.AppendLine("$o = New-Object System.Collections.Generic.List[string]; $in=$false");
        sb.AppendLine("foreach($l in $c){ if($l -eq $b){$in=$true;continue}; if($l -eq $e){$in=$false;continue}; if(-not $in){$o.Add($l)} }");
        sb.AppendLine("$o.Add($b)");
        foreach (var h in _hosts) sb.AppendLine($"$o.Add(\"127.0.0.1`t{h}\")");
        sb.AppendLine("$o.Add($e)");
        sb.AppendLine("for($i=0;$i -lt 15;$i++){ try { [System.IO.File]::WriteAllLines($hp,$o,(New-Object System.Text.ASCIIEncoding)); break } catch { Start-Sleep -Milliseconds 700 } }");
        sb.AppendLine("ipconfig /flushdns | Out-Null");
        return RunElevatedPowerShellAsync(sb.ToString());
    }

    private Task<bool> RevertElevatedAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
        sb.AppendLine("$hp=\"$env:WINDIR\\System32\\drivers\\etc\\hosts\"");
        sb.AppendLine($"$b='{HostsBegin}'; $e='{HostsEnd}'");
        sb.AppendLine("$c=@(); try { $c = Get-Content -LiteralPath $hp -ErrorAction Stop } catch {}");
        sb.AppendLine("$o = New-Object System.Collections.Generic.List[string]; $in=$false");
        sb.AppendLine("foreach($l in $c){ if($l -eq $b){$in=$true;continue}; if($l -eq $e){$in=$false;continue}; if(-not $in){$o.Add($l)} }");
        sb.AppendLine("for($i=0;$i -lt 15;$i++){ try { [System.IO.File]::WriteAllLines($hp,$o,(New-Object System.Text.ASCIIEncoding)); break } catch { Start-Sleep -Milliseconds 700 } }");
        sb.AppendLine("ipconfig /flushdns | Out-Null");
        return RunElevatedPowerShellAsync(sb.ToString());
    }

    private async Task<bool> RunElevatedPowerShellAsync(string psScript)
    {
        // When the app is already elevated the script runs in-process (no UAC); otherwise
        // the app's own exe is elevated (one UAC, app icon) to run it. See ElevationHelper.
        var ok = await Task.Run(() => ElevationHelper.RunPowerShellElevated(psScript));
        if (!ok) Log?.Invoke("elevation failed (declined?)");
        return ok;
    }

    // ---- DNS (A record via a public resolver, bypassing hosts) -----------

    /// <summary>Resolves an A (IPv4) record by querying a public resolver over UDP, so the result is the REAL
    /// CDN IP regardless of our own hosts redirect. Returns null on failure.</summary>
    private static string? ResolveARecord(string host, string dnsServer = "1.1.1.1")
    {
        try
        {
            var query = BuildDnsQuery(host);
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 4000;
            udp.Connect(IPAddress.Parse(dnsServer), 53);
            udp.Send(query, query.Length);
            IPEndPoint? remote = null;
            var resp = udp.Receive(ref remote);
            return ParseFirstA(resp);
        }
        catch { return null; }
    }

    private static byte[] BuildDnsQuery(string host)
    {
        var ms = new MemoryStream();
        // Header: id, flags(RD=1), qd=1, an/ns/ar=0.
        ushort id = 0x1234;
        ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id);
        ms.WriteByte(0x01); ms.WriteByte(0x00); // RD
        ms.WriteByte(0x00); ms.WriteByte(0x01); // QDCOUNT=1
        ms.WriteByte(0x00); ms.WriteByte(0x00);
        ms.WriteByte(0x00); ms.WriteByte(0x00);
        ms.WriteByte(0x00); ms.WriteByte(0x00);
        foreach (var label in host.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0x00);                 // root
        ms.WriteByte(0x00); ms.WriteByte(0x01); // QTYPE=A
        ms.WriteByte(0x00); ms.WriteByte(0x01); // QCLASS=IN
        return ms.ToArray();
    }

    private static string? ParseFirstA(byte[] resp)
    {
        if (resp.Length < 12) return null;
        int qd = (resp[4] << 8) | resp[5];
        int an = (resp[6] << 8) | resp[7];
        int pos = 12;
        for (int i = 0; i < qd; i++)
        {
            pos = SkipName(resp, pos);
            pos += 4; // QTYPE + QCLASS
        }
        for (int i = 0; i < an && pos + 12 <= resp.Length; i++)
        {
            pos = SkipName(resp, pos);
            int type = (resp[pos] << 8) | resp[pos + 1];
            int rdlen = (resp[pos + 8] << 8) | resp[pos + 9];
            int rdata = pos + 10;
            if (type == 1 && rdlen == 4 && rdata + 4 <= resp.Length)
                return $"{resp[rdata]}.{resp[rdata + 1]}.{resp[rdata + 2]}.{resp[rdata + 3]}";
            pos = rdata + rdlen;
        }
        return null;
    }

    private static int SkipName(byte[] b, int pos)
    {
        while (pos < b.Length)
        {
            int len = b[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2; // compression pointer
            pos += len + 1;
        }
        return pos;
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _live.Dispose();
        _bg.Dispose();
    }
}
