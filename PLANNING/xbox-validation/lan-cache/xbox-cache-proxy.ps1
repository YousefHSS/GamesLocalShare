<#
  xbox-cache-proxy.ps1 - LAN cache for Xbox / Microsoft Store game content (MSIXVC blobs).

  Serves the ENCRYPTED .msixvc package bytes Microsoft's CDN (assets1.xboxlive.com) would
  serve, but from a local cache over LAN. The bytes are byte-identical to the CDN's, so the
  install writes the correct at-rest ciphertext -> Gaming Services trusts it and "Verify &
  Repair" / updates work. No decryption, no keys, no license bypass (same idea as Microsoft
  Connected Cache, self-hosted).

  Compiled C# core on the native .NET thread pool (no per-request PowerShell runspace).

  HIT  : object on disk -> serve the requested byte Range from disk (LAN speed).
  MISS : forward live to the REAL origin (resolved via -DnsServer, bypassing any hosts redirect)
         AND, on the first miss for an uncached object, start ONE background thread that
         downloads the whole package sequentially to <file>.part and renames it to <file> when
         complete. Single writer -> reliable. After that, all requests are HITs.
         (First download of a game costs ~2x bandwidth: the live install + the background fill.
          Subsequent installs / Verify / other PCs are local HITs. prime-blob.ps1 can still be
          used to pre-fill ahead of time with no live install running = 1x.)

  Usage (elevated, on the CACHE PC):
    .\xbox-cache-proxy.ps1
    .\xbox-cache-proxy.ps1 -CacheDir F:\xbox-cache -OriginHosts assets1.xboxlive.com,assets2.xboxlive.com

  Ctrl+C to stop. ASCII only. Windows PowerShell 5.1 compatible.
#>
param(
  [int]$Port = 80,
  [string]$CacheDir = "F:\xbox-cache",
  [string[]]$OriginHosts = @('assets1.xboxlive.com'),
  [string]$DnsServer = '1.1.1.1',
  [string]$LogDir = "F:\Documents\GamesLocalShare\PLANNING\xbox-validation\lan-cache\logs",
  # STRICT TEST MODE (Path B): never forward a miss to the real CDN (return 404 instead),
  # and serve the cached file from -CanonHost for ANY requested host. This guarantees GS
  # cannot obtain a single genuine byte from us regardless of which CDN host it picks.
  [switch]$NoOrigin,
  [string]$CanonHost = 'assets1.xboxlive.com'
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated (binding port 80 needs admin)." -ForegroundColor Red; return }

New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $LogDir "cache-$stamp.log"

# --- pre-resolve origin hostnames to REAL IPs (bypass any hosts-file redirect) ---
$dnsMap = @{}
foreach ($h in $OriginHosts) {
  try {
    $ans = Resolve-DnsName -Name $h -Server $DnsServer -Type A -ErrorAction Stop | Where-Object { $_.IPAddress } | Select-Object -First 1
    if ($ans) { $dnsMap[$h] = $ans.IPAddress }
  } catch { Write-Host ("WARN: could not pre-resolve {0}: {1}" -f $h, $_.Exception.Message) -ForegroundColor Yellow }
}

# --- compiled C# proxy core (background single-writer auto-fill) ---
$cs = @'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections;
using System.Collections.Generic;

public class XboxLanCacheV5 {
  static string CacheDir, LogPath;
  static Dictionary<string,string> Dns;
  static HttpClient Http;   // live forwards (header timeout)
  static HttpClient Bg;     // background full downloads (header timeout; body unbounded)
  static readonly object LogLock = new object();
  static HttpListener Listener;
  static volatile bool Running;
  static bool NoOrigin; static string CanonHost;
  public static long Hits, Misses, Bytes, Errors, Cached, Filling;

  static readonly object Gate = new object();
  static readonly HashSet<string> InProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

  public static void Start(string prefix, string cacheDir, string logPath, Hashtable dnsMap, bool noOrigin, string canonHost) {
    CacheDir = cacheDir; LogPath = logPath; NoOrigin = noOrigin; CanonHost = canonHost;
    Dns = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    foreach (DictionaryEntry e in dnsMap) Dns[(string)e.Key] = (string)e.Value;
    ServicePointManager.DefaultConnectionLimit = 512;
    ServicePointManager.Expect100Continue = false;
    var h1 = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None };
    Http = new HttpClient(h1); Http.Timeout = TimeSpan.FromSeconds(30);
    var h2 = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None };
    Bg = new HttpClient(h2); Bg.Timeout = TimeSpan.FromMinutes(10); // headers only (ResponseHeadersRead)
    Listener = new HttpListener(); Listener.Prefixes.Add(prefix); Listener.Start();
    Running = true;
    var t = new Thread(AcceptLoop); t.IsBackground = true; t.Start();
  }

  public static void Stop() { Running = false; try { Listener.Stop(); } catch {} }

  static void AcceptLoop() {
    while (Running) {
      HttpListenerContext ctx;
      try { ctx = Listener.GetContext(); } catch { break; }
      ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
    }
  }

  static void Log(string s) { try { lock (LogLock) { File.AppendAllText(LogPath, s + Environment.NewLine); } } catch {} }

  // Start ONE background thread that downloads the whole object sequentially to .part, then
  // renames to the final cache path. Idempotent: only one fill per file at a time.
  static void StartFill(string file, string hostHdr, string rawPath, string ip) {
    lock (Gate) {
      if (InProgress.Contains(file) || File.Exists(file)) return;
      InProgress.Add(file);
    }
    Interlocked.Increment(ref Filling);
    var th = new Thread(delegate() {
      string part = file + ".part";
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        var msg = new HttpRequestMessage(HttpMethod.Get, "http://" + ip + rawPath);
        msg.Headers.Host = hostHdr;
        var resp = Bg.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        int sc = (int)resp.StatusCode;
        if (sc == 200) {
          long expected = resp.Content.Headers.ContentLength.HasValue ? resp.Content.Headers.ContentLength.Value : -1;
          long written = 0;
          using (var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
          using (var f = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)) {
            byte[] buf = new byte[1 << 20]; int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) { f.Write(buf, 0, n); written += n; }
          }
          // Only publish a COMPLETE object. A connection dropped mid-fill can end the read loop
          // without throwing, leaving a short .part; renaming that to the final name would poison the
          // cache (served as a HIT forever, and StartFill skips re-filling once the file exists).
          if (expected >= 0 && written != expected) {
            try { File.Delete(part); } catch {}
            Log(string.Format("[{0}] FILL SHORT {1}  got={2} expected={3} (discarded; will refill)", DateTime.Now.ToString("HH:mm:ss"), rawPath, written, expected));
          } else {
            if (File.Exists(file)) File.Delete(part); else File.Move(part, file);
            Interlocked.Increment(ref Cached);
            Log(string.Format("[{0}] FILL DONE {1}  bytes={2}", DateTime.Now.ToString("HH:mm:ss"), file, written));
          }
        } else {
          Log(string.Format("[{0}] FILL skip code={1} {2}", DateTime.Now.ToString("HH:mm:ss"), sc, rawPath));
        }
        resp.Dispose();
      } catch (Exception ex) {
        try { File.Delete(part); } catch {}
        Log(string.Format("[{0}] FILL ERR {1}  {2}", DateTime.Now.ToString("HH:mm:ss"), rawPath, ex.Message));
      } finally {
        Interlocked.Decrement(ref Filling);
        lock (Gate) { InProgress.Remove(file); }
      }
    });
    th.IsBackground = true; th.Start();
  }

  static void Handle(HttpListenerContext ctx) {
    var req = ctx.Request; var res = ctx.Response;
    string rawPath = req.RawUrl;
    string hostHdr = (req.UserHostName ?? "").Split(':')[0];
    string range = req.Headers["Range"];
    bool started = false;
    try {
      string rel = rawPath;
      int qi = rel.IndexOf('?'); if (qi >= 0) rel = rel.Substring(0, qi);
      rel = rel.TrimStart('/').Replace('/', '\\');
      rel = Regex.Replace(rel, "[:*?\"<>|]", "_");
      string file = Path.Combine(Path.Combine(CacheDir, hostHdr), rel);
      if (NoOrigin && !File.Exists(file) && !string.IsNullOrEmpty(CanonHost)) {
        string alt = Path.Combine(Path.Combine(CacheDir, CanonHost), rel);
        if (File.Exists(alt)) file = alt;   // serve the canonical cached file for ANY host
      }

      if (File.Exists(file)) {
        // ---- HIT: serve byte range from disk ----
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) {
          long total = fs.Length, start = 0, end = total - 1; int code = 200;
          var m = Regex.Match(range ?? "", @"bytes=(\d+)-(\d*)");
          if (m.Success) {
            start = long.Parse(m.Groups[1].Value);
            if (m.Groups[2].Value != "") end = long.Parse(m.Groups[2].Value);
            if (end > total - 1) end = total - 1;
            if (start <= end) code = 206;
          }
          long len = end - start + 1;
          res.StatusCode = code;
          res.Headers["Accept-Ranges"] = "bytes";
          if (code == 206) res.Headers["Content-Range"] = "bytes " + start + "-" + end + "/" + total;
          res.ContentLength64 = len;
          started = true;
          fs.Position = start;
          byte[] buf = new byte[262144]; long remaining = len;
          while (remaining > 0) {
            int toRead = (int)Math.Min(buf.Length, remaining);
            int n = fs.Read(buf, 0, toRead);
            if (n <= 0) break;
            res.OutputStream.Write(buf, 0, n);
            remaining -= n;
          }
          Interlocked.Increment(ref Hits); Interlocked.Add(ref Bytes, len - remaining);
          Log(string.Format("[{0}] HIT  {1}  range={2}  bytes={3}", DateTime.Now.ToString("HH:mm:ss"), rawPath, range, len - remaining));
        }
      } else if (NoOrigin) {
        // strict test mode: never serve genuine bytes from the real CDN; a miss is a 404.
        res.StatusCode = 404; started = true;
        Interlocked.Increment(ref Misses);
        Log(string.Format("[{0}] MISS-404 (NoOrigin) {1}  range={2}", DateTime.Now.ToString("HH:mm:ss"), rawPath, range));
      } else if (!Dns.ContainsKey(hostHdr)) {
        // not one of our origin hosts (WPAD/telemetry on :80) -> 404 instantly, never forward
        res.StatusCode = 404; started = true;
      } else {
        // ---- MISS: kick off background fill (once) + forward this request live ----
        string ip = Dns[hostHdr];
        StartFill(file, hostHdr, rawPath, ip);
        var msg = new HttpRequestMessage(HttpMethod.Get, "http://" + ip + rawPath);
        msg.Headers.Host = hostHdr;
        if (!string.IsNullOrEmpty(range)) msg.Headers.TryAddWithoutValidation("Range", range);
        long sent = 0; int sc = 0;
        using (var resp = Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()) {
          sc = (int)resp.StatusCode;
          res.StatusCode = sc;
          ContentRangeHeaderValue crange = resp.Content.Headers.ContentRange;
          if (crange != null) res.Headers["Content-Range"] = crange.ToString();
          if (resp.Content.Headers.ContentLength != null) res.ContentLength64 = (long)resp.Content.Headers.ContentLength;
          res.Headers["Accept-Ranges"] = "bytes";
          started = true;
          using (var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()) {
            byte[] buf = new byte[262144]; int rn;
            while ((rn = stream.Read(buf, 0, buf.Length)) > 0) { res.OutputStream.Write(buf, 0, rn); sent += rn; }
          }
        }
        Interlocked.Increment(ref Misses); Interlocked.Add(ref Bytes, sent);
        Log(string.Format("[{0}] MISS {1}  range={2}  code={3}  bytes={4}", DateTime.Now.ToString("HH:mm:ss"), rawPath, range, sc, sent));
      }
    } catch (Exception ex) {
      if (!started) { try { res.StatusCode = 502; } catch {} }
      Interlocked.Increment(ref Errors);
      Log(string.Format("[{0}] ERR  {1}  {2}", DateTime.Now.ToString("HH:mm:ss"), rawPath, ex.Message));
    } finally {
      try { res.OutputStream.Close(); } catch {}
      try { res.Close(); } catch {}
    }
  }
}
'@

if (-not ([System.Management.Automation.PSTypeName]'XboxLanCacheV5').Type) {
  Add-Type -TypeDefinition $cs -ReferencedAssemblies 'System.Net.Http' -ErrorAction Stop
}

Write-Host "=== Xbox LAN cache proxy (C# core, background auto-fill) ===" -ForegroundColor Cyan
Write-Host ("Cache dir : {0}" -f $CacheDir)
Write-Host ("Log       : {0}" -f $log)
Write-Host  "Origin IPs (real, hosts-file-bypassed):" -ForegroundColor Green
foreach ($k in $dnsMap.Keys) { Write-Host ("   {0} -> {1}" -f $k, $dnsMap[$k]) -ForegroundColor Green }
Write-Host "This PC's LAN IPv4 (use for the downloader's hosts file):" -ForegroundColor Green
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.*' -and $_.IPAddress -ne '127.0.0.1' } | ForEach-Object { Write-Host ("   {0}  ({1})" -f $_.IPAddress, $_.InterfaceAlias) -ForegroundColor Green }
Write-Host ""

if ($NoOrigin) {
  Write-Host ("STRICT MODE (-NoOrigin): misses return 404, NEVER forwarded to real CDN." ) -ForegroundColor Magenta
  Write-Host ("  Any host's request is served from CanonHost '{0}' cache. Path B isolation ON." -f $CanonHost) -ForegroundColor Magenta
}

$prefix = "http://+:$Port/"
try { [XboxLanCacheV5]::Start($prefix, $CacheDir, $log, $dnsMap, $NoOrigin.IsPresent, $CanonHost) }
catch {
  Write-Host ("Start failed: {0}; adding urlacl and retrying..." -f $_.Exception.Message) -ForegroundColor Yellow
  & netsh http add urlacl url=$prefix user=Everyone | Out-Null
  [XboxLanCacheV5]::Start($prefix, $CacheDir, $log, $dnsMap, $NoOrigin.IsPresent, $CanonHost)
}
Write-Host ("LISTENING on {0}  -- Ctrl+C to stop." -f $prefix) -ForegroundColor Cyan
Write-Host ""

try {
  while ($true) {
    Start-Sleep -Seconds 2
    Write-Host ("  hits={0}  misses={1}  filling={2}  cached-files={3}  errors={4}  served={5:N1} MB" -f [XboxLanCacheV5]::Hits, [XboxLanCacheV5]::Misses, [XboxLanCacheV5]::Filling, [XboxLanCacheV5]::Cached, [XboxLanCacheV5]::Errors, ([XboxLanCacheV5]::Bytes/1MB)) -ForegroundColor DarkCyan
  }
}
finally {
  [XboxLanCacheV5]::Stop()
  Write-Host ""
  Write-Host ("Stopped. hits={0} misses={1} cached-files={2} errors={3} served={4:N1} MB. Log: {5}" -f [XboxLanCacheV5]::Hits, [XboxLanCacheV5]::Misses, [XboxLanCacheV5]::Cached, [XboxLanCacheV5]::Errors, ([XboxLanCacheV5]::Bytes/1MB), $log) -ForegroundColor Cyan
}
