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
        _cacheDir = string.IsNullOrWhiteSpace(cacheDir) ? @"F:\xbox-cache" : cacheDir;
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
                if (viaPeer)
                {
                    target = $"http://{peerHost}:{peerPort}{rawPath}";
                }
                else
                {
                    string ip = _dns[hostHdr];
                    StartFill(file, hostHdr, rawPath, ip);
                    target = "http://" + ip + rawPath;
                }
                var msg = new HttpRequestMessage(HttpMethod.Get, target);
                if (!viaPeer) msg.Headers.Host = hostHdr; // CDN needs the real host; the peer endpoint ignores it
                if (!string.IsNullOrEmpty(range)) msg.Headers.TryAddWithoutValidation("Range", range);
                long sent = 0; int sc;
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
                    // Never write more than the declared Content-Length: if the upstream body runs longer than
                    // its header, HttpListener throws "Bytes to be written to the stream exceed the Content-Length"
                    // and aborts the response, corrupting the Store's download. Bound the copy to cl (when known).
                    long cap = cl ?? long.MaxValue;
                    using var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                    byte[] buf = new byte[262144]; int rn;
                    while (sent < cap)
                    {
                        int want = (int)Math.Min(buf.Length, cap - sent);
                        rn = stream.Read(buf, 0, want);
                        if (rn <= 0) break;
                        res.OutputStream.Write(buf, 0, rn);
                        sent += rn;
                    }
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
            if (_inProgress.Contains(file) || File.Exists(file)) return;
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
        // When the app is already elevated, run the urlacl/hosts steps in-process (no UAC). Otherwise spawn a
        // single elevated helper (one UAC) via runas.
        bool alreadyElevated = false;
        try { alreadyElevated = ElevationHelper.IsElevated(); } catch { }

        try
        {
            var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {enc}",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            if (alreadyElevated)
            {
                // No elevation prompt needed; capture output so failures are diagnosable.
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }
            else
            {
                psi.UseShellExecute = true;   // required for Verb=runas
                psi.Verb = "runas";           // triggers the UAC prompt
            }
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"elevation failed (declined?): {ex.Message}");
            return false;
        }
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
