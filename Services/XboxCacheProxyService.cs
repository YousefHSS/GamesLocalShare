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
    private string _streamCikFolder = "", _streamBlobDir = "";
    private sealed class StreamState
    {
        public string File = "", Host = "", RawPath = "", Ip = "";
        public long Total = -1;
        public LibXboxOne.StreamingCaptureController? Ctl;
        public readonly List<(long s, long e)> Fed = new();   // covered ranges (completeness + gap-fill)
        public bool Armed, Aborted, Parked, Arming;
        public DateTime LastWriteUtc = DateTime.UtcNow;
        public readonly object Lock = new();
    }
    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _streamAbandoned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Arms streaming skeleton capture. When <paramref name="enabled"/>, the proxy tries to capture a
    /// title's skeleton in-stream (no full package on disk) instead of the sparse-.part tee, loading keys from
    /// <paramref name="cikFolder"/> and writing the growing skeleton blob under <paramref name="blobDir"/>.
    /// Falls back to the tee whenever streaming can't be armed or aborts. Safe to call before or after Start.</summary>
    public void ConfigureStreamingCapture(bool enabled, string? cikFolder, string? blobDir)
    {
        _streamingEnabled = enabled;
        _streamCikFolder = cikFolder ?? "";
        _streamBlobDir = string.IsNullOrWhiteSpace(blobDir)
            ? Path.Combine(Path.GetTempPath(), "gls-stream-blobs") : blobDir!;
        try { if (enabled) Directory.CreateDirectory(_streamBlobDir); } catch { }
        Log?.Invoke(enabled
            ? $"streaming capture ENABLED (cik: {_streamCikFolder}, blobs: {_streamBlobDir})"
            : "streaming capture disabled");
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
            lock (st.Lock) { try { st.Ctl?.Dispose(); } catch { } st.Ctl = null; }
            if (!st.Parked && !st.Aborted) Interlocked.Decrement(ref Filling);
        }
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
                            // Prefer streaming capture when armed; only fall back to the sparse-.part tee when
                            // streaming isn't handling this object (disabled, cached, or abandoned).
                            scap = TryGetStream(file, hostHdr, rawPath, ip, objTotal);
                            if (scap == null) tee = GetOrCreateFill(file, hostHdr, rawPath, ip, objTotal);
                        }
                        else StartFill(file, hostHdr, rawPath, ip);
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
                        // Streaming capture is driven by its own in-order SequentialFeed (see ArmStream), not by
                        // the Store's out-of-order requests — so nothing is fed here. When scap != null the tee
                        // is null (streaming owns the object) and the bytes just forward to the Store.
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

    /// <summary>Returns the streaming state that should capture this object, creating it (and kicking a
    /// background arm that prefetches the header + builds the map) on the first MISS. Returns null when
    /// streaming is disabled, the object is already cached, or streaming was abandoned for it — the caller then
    /// uses the sparse-.part tee.</summary>
    private StreamState? TryGetStream(string file, string host, string rawPath, string ip, long total)
    {
        if (!_streamingEnabled) return null;
        lock (_gate)
        {
            if (File.Exists(file) || _inProgress.Contains(file) || _fills.ContainsKey(file)) return null;
            if (_streamAbandoned.Contains(file)) return null;
            if (_streams.TryGetValue(file, out var existing)) return existing.Parked ? null : existing;
            var st = new StreamState { File = file, Host = host, RawPath = rawPath, Ip = ip, Total = total, Arming = true };
            _streams[file] = st;
            Interlocked.Increment(ref Filling);
            StatsChanged?.Invoke();
            var th = new Thread(() => ArmStream(st)) { IsBackground = true, Name = "xbox-stream-arm" };
            th.Start();
            return st;
        }
    }

    /// <summary>Background arm: prefetch the header front + last sector from the CDN and build the streaming
    /// capture controller. On failure the object is abandoned (falls back to the tee via a fresh whole-object
    /// download so it still caches).</summary>
    private void ArmStream(StreamState st)
    {
        const long frontLen = 64L << 20;
        try
        {
            long fl = Math.Min(frontLen, st.Total);
            int tailLen = (int)Math.Min(4096, st.Total);
            long tailOff = st.Total - tailLen;
            byte[] front = FetchRangeBytes(st.Host, st.Ip, st.RawPath, 0, fl)
                           ?? throw new IOException("front prefetch failed");
            byte[] tail = tailLen > 0 && tailOff > fl
                ? (FetchRangeBytes(st.Host, st.Ip, st.RawPath, tailOff, tailOff + tailLen) ?? Array.Empty<byte>())
                : Array.Empty<byte>();

            string blob = Path.Combine(_streamBlobDir, MakeBlobName(st.File));
            var ctl = LibXboxOne.StreamingCaptureController.TryBegin(st.Total, front, fl, tail, tailOff,
                _streamCikFolder, blob, m => Log?.Invoke(m));
            if (ctl == null) { AbandonStream(st, "could not arm (no CIK / unsupported package)"); return; }
            lock (st.Lock) { st.Ctl = ctl; st.Armed = true; st.Arming = false; st.LastWriteUtc = DateTime.UtcNow; }
            Log?.Invoke($"STREAM armed {Path.GetFileName(st.File)} ({st.Total / 1048576.0:F0} MB)");

            // Drive the capture with a dedicated IN-ORDER feed of the object, independent of the Store's
            // request order. The Store arms us mid-download and requests ranges in its own order/parallelism;
            // feeding those directly would pile high-offset bytes into the reorder buffer while the cursor is
            // stuck at 0 → overflow. A sequential fetch keeps the feed ascending (drains immediately, no reorder
            // pressure). Costs ~1× extra bandwidth for the capture; the storage win (no full package on disk) is
            // the point. Reuses the already-fetched front.
            var feeder = new Thread(() => SequentialFeed(st, front, fl)) { IsBackground = true, Name = "xbox-stream-feed" };
            feeder.Start();
        }
        catch (Exception ex) { AbandonStream(st, ex.Message); }
    }

    /// <summary>Feeds the whole object to the streaming controller in ascending order (front reused, the rest
    /// fetched in bounded chunks), then coverage completes and the controller parks. Runs on its own thread so
    /// it doesn't gate the Store's requests.</summary>
    private void SequentialFeed(StreamState st, byte[] front, long frontLen)
    {
        try
        {
            int fl = (int)Math.Min(frontLen, front.LongLength);
            if (fl > 0)
            {
                if (!StreamFeed(st, 0, front, 0, fl)) return;   // aborted → fell back to tee inside
                StreamEndRange(st, 0, fl);
            }
            const long step = 8L << 20;
            for (long off = fl; off < st.Total; off += step)
            {
                lock (st.Lock) { if (st.Aborted || st.Parked) return; }
                long end = Math.Min(off + step, st.Total);
                byte[]? data = FetchRangeBytes(st.Host, st.Ip, st.RawPath, off, end);
                if (data == null) { StreamAbort(st, $"capture fetch failed [{off},{end})"); return; }
                if (!StreamFeed(st, off, data, 0, data.Length)) return;
                StreamEndRange(st, off, data.Length);
            }
        }
        catch (Exception ex) { StreamAbort(st, "capture feed: " + ex.Message); }
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
        if (complete)
        {
            Interlocked.Decrement(ref Filling);
            Log?.Invoke($"STREAM ready {Path.GetFileName(st.File)} — awaiting install to finalize skeleton");
            StatsChanged?.Invoke();
        }
    }

    /// <summary>Abandons streaming for an object and kicks a normal whole-object download so it still caches
    /// (no regression). Rare: only on reorder-buffer overflow, version-mismatch bloat, or an error.</summary>
    private void StreamAbort(StreamState st, string reason)
    {
        lock (st.Lock) { if (st.Aborted) return; st.Aborted = true; try { st.Ctl?.Dispose(); } catch { } st.Ctl = null; }
        lock (_gate) { _streams.Remove(st.File); _streamAbandoned.Add(st.File); }
        Interlocked.Decrement(ref Filling);
        Log?.Invoke($"STREAM abort {Path.GetFileName(st.File)} — {reason}; falling back to full download");
        StatsChanged?.Invoke();
        StartFill(st.File, st.Host, st.RawPath, st.Ip);
    }

    private void AbandonStream(StreamState st, string reason)
    {
        lock (st.Lock) { st.Aborted = true; st.Arming = false; try { st.Ctl?.Dispose(); } catch { } st.Ctl = null; }
        lock (_gate) { _streams.Remove(st.File); _streamAbandoned.Add(st.File); }
        Interlocked.Decrement(ref Filling);
        Log?.Invoke($"STREAM not armed {Path.GetFileName(st.File)} — {reason}; using tee");
        StatsChanged?.Invoke();
        StartFill(st.File, st.Host, st.RawPath, st.Ip);
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
        bool ok;
        lock (st.Lock)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(skelPath)!); } catch { }
            ok = st.Ctl.Complete(installDir, skelPath,
                (o, c) => FetchRangeBytes(st.Host, st.Ip, st.RawPath, o, o + c) ?? new byte[c], out status);
        }
        if (ok)
        {
            lock (_gate) { _streams.Remove(st.File); }
            try { st.Ctl.Dispose(); } catch { }
            Log?.Invoke($"STREAM finalized {Path.GetFileName(st.File)} — {status}");
            StatsChanged?.Invoke();
        }
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
