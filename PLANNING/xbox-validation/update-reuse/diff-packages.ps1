<#
  diff-packages.ps1 — measure the cross-version REUSE POTENTIAL for Xbox package updates.

  Compares two ENCRYPTED .msixvc packages of the SAME game — an OLD version and the NEW
  version — block-by-block AT THE SAME OFFSET. It answers the one make-or-break question for
  update-time gap reuse (filling the ranges the Store didn't re-download from local data
  instead of the CDN):

      "What fraction of the package is byte-identical at the same offset between versions?"

  How to read the result:
    HIGH identical %  -> same-offset reuse is VALID. On an update we can fill the un-sent
                         ranges from local data rather than re-downloading them. The identical
                         % is the CEILING on how much download we can avoid.
    ~0% identical     -> either the content key (CIK) rotated (so unchanged content encrypts
                         to totally different bytes) OR the layout shifted (offsets moved).
                         Naive same-offset reuse won't work; reuse would have to move down to
                         the decrypt + re-encrypt layer (much harder). This result KILLS the
                         simple design early — which is exactly why we measure first.

  HOW TO PRODUCE THE TWO PACKAGES (one real update):
    1. In Settings > Xbox, turn ON "Also cache the full package to disk".
    2. Have the game installed at version N  -> its full .msixvc is now in the cache.
    3. Let the game update to version N+1     -> the new version's .msixvc (a different
       content GUID, so a different file) is also cached; the old one stays.
    4. Find both under <cache>\assets1.xboxlive.com\...  (two *.msixvc for the same title)
       and run this script on them.

  USAGE:
    diff-packages.ps1 -Old <old.msixvc> -New <new.msixvc> [-Block 4096] [-MapBuckets 120]

  Exit codes: 0 ok | 1 error | 2 bad args
#>
param(
  [Parameter(Mandatory)][string]$Old,
  [Parameter(Mandatory)][string]$New,
  [int]$Block = 4096,
  [int]$MapBuckets = 120
)

if (-not (Test-Path -LiteralPath $Old)) { Write-Error "Old package not found: $Old"; exit 2 }
if (-not (Test-Path -LiteralPath $New)) { Write-Error "New package not found: $New"; exit 2 }
if ($Block -le 0) { Write-Error "Block must be > 0"; exit 2 }
if ($MapBuckets -le 0) { $MapBuckets = 120 }

$code = @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class PkgDiff {
  [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
  static extern int memcmp(byte[] a, byte[] b, UIntPtr count);

  static int ReadFull(Stream s, byte[] buf, int want) {
    int g = 0; while (g < want) { int r = s.Read(buf, g, want - g); if (r <= 0) break; g += r; } return g;
  }

  public static string Run(string oldP, string newP, int block, int buckets) {
    var log = new StringBuilder();
    long la = new FileInfo(oldP).Length, lb = new FileInfo(newP).Length;
    long common = Math.Min(la, lb);
    long span = Math.Max(1, common);

    long totalBlocks = 0, identBlocks = 0, identBytes = 0;
    long firstDiff = -1;
    long changedRegions = 0; bool inChanged = false; long regStart = 0;
    var topRegions = new List<long[]>(); // {start, len} of contiguous CHANGED spans

    long[] bTot = new long[buckets];
    long[] bId  = new long[buckets];

    byte[] a = new byte[block], b = new byte[block];
    long off = 0;

    // BufferedStream keeps the underlying disk reads large (1 MB) while we consume block-sized pieces.
    using (var fa = new BufferedStream(File.OpenRead(oldP), 1 << 20))
    using (var fb = new BufferedStream(File.OpenRead(newP), 1 << 20)) {
      while (off + block <= common) {
        int ga = ReadFull(fa, a, block);
        int gb = ReadFull(fb, b, block);
        if (ga != block || gb != block) break;

        bool same = memcmp(a, b, (UIntPtr)block) == 0;
        totalBlocks++;
        int bi = (int)((double)off / span * buckets); if (bi >= buckets) bi = buckets - 1;
        bTot[bi]++;

        if (same) {
          identBlocks++; identBytes += block; bId[bi]++;
          if (inChanged) { topRegions.Add(new long[] { regStart, off - regStart }); inChanged = false; }
        } else {
          if (firstDiff < 0) firstDiff = off;
          if (!inChanged) { inChanged = true; regStart = off; changedRegions++; }
        }
        off += block;
      }
    }
    if (inChanged) topRegions.Add(new long[] { regStart, off - regStart });

    double pct = totalBlocks > 0 ? 100.0 * identBlocks / totalBlocks : 0.0;

    log.AppendLine("== package diff (same-offset, block=" + block + ") ==");
    log.AppendLine("old size          : " + la.ToString("N0") + "  (" + (la / 1048576.0).ToString("F1") + " MB)");
    log.AppendLine("new size          : " + lb.ToString("N0") + "  (" + (lb / 1048576.0).ToString("F1") + " MB)");
    log.AppendLine("size delta        : " + (lb - la).ToString("N0") + " B");
    log.AppendLine("common length     : " + common.ToString("N0") + "  (" + (common / 1048576.0).ToString("F1") + " MB)");
    log.AppendLine("blocks compared   : " + totalBlocks.ToString("N0"));
    log.AppendLine("identical blocks  : " + identBlocks.ToString("N0"));
    log.AppendLine("IDENTICAL @ offset: " + pct.ToString("F2") + " %   <-- reuse ceiling (download we could avoid)");
    log.AppendLine("identical bytes   : " + identBytes.ToString("N0") + "  (" + (identBytes / 1048576.0).ToString("F1") + " MB)");
    log.AppendLine("first difference  : " + (firstDiff < 0 ? "(none)" : firstDiff.ToString("N0")));
    log.AppendLine("changed regions   : " + changedRegions.ToString("N0"));

    topRegions.Sort(delegate (long[] x, long[] y) { return y[1].CompareTo(x[1]); });
    log.AppendLine("");
    log.AppendLine("largest changed regions (offset, length):");
    int show = Math.Min(10, topRegions.Count);
    for (int i = 0; i < show; i++)
      log.AppendLine(String.Format("  {0,15:N0}  {1,15:N0} B  ({2:F1} MB)", topRegions[i][0], topRegions[i][1], topRegions[i][1] / 1048576.0));
    if (topRegions.Count == 0) log.AppendLine("  (none — packages identical over the common length)");

    // Coarse identity map: one char per bucket. '#' ~all identical, '.' ~all changed, digits = tens of %.
    log.AppendLine("");
    log.AppendLine("identity map (left=start .. right=end of common length):");
    var map = new StringBuilder();
    for (int i = 0; i < buckets; i++) {
      if (bTot[i] == 0) { map.Append(' '); continue; }
      double r = (double)bId[i] / bTot[i];
      if (r >= 0.999) map.Append('#');
      else if (r <= 0.001) map.Append('.');
      else map.Append((char)('0' + (int)Math.Min(9, r * 10)));
    }
    log.AppendLine("  " + map.ToString());
    log.AppendLine("  legend: '#'=identical  '.'=changed  0-9=tens-of-% identical  ' '=no data");
    return log.ToString();
  }
}
'@

Add-Type -TypeDefinition $code -Language CSharp

$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
  [PkgDiff]::Run((Resolve-Path -LiteralPath $Old), (Resolve-Path -LiteralPath $New), $Block, $MapBuckets)
} catch {
  Write-Error $_
  exit 1
}
"elapsed {0:N1}s" -f $sw.Elapsed.TotalSeconds
exit 0
