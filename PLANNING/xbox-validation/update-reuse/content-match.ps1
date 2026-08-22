<#
  content-match.ps1 - how much CONTENT survives an update, independent of where it sits?

  diff-packages.ps1 answers "how much is identical AT THE SAME OFFSET" and, on real update
  pairs, returns a near-zero number. That number is misleading on its own, because XVC data
  is AES-XTS encrypted with the data unit tied to the page index: move a page and its
  ciphertext changes completely even when the plaintext is untouched. Same-offset comparison
  therefore measures LAYOUT STABILITY, not content reuse.

  This script measures content reuse instead. Run it on DECRYPTED packages:

      XVDTool.exe -eu -pdu --cikfolder <cikdir> -o old_dec.bin <old.msixvc>
      XVDTool.exe -eu -pdu --cikfolder <cikdir> -o new_dec.bin <new.msixvc>
      .\content-match.ps1 -Old old_dec.bin -New new_dec.bin

  ALWAYS pass -o so xvdtool writes a copy; it must not modify the cached package. Verify the
  originals' SHA256 afterwards if you want to be certain.

  It reports three numbers:

    A) SAME OFFSET match   - what naive same-offset reuse could take. Expect this to be low.
    B) FOUND ANYWHERE      - fraction of NEW blocks that exist somewhere in OLD. This is the
                             ceiling for a content-addressed reuse design, but zero-filled
                             blocks inflate it.
    C) FOUND ANYWHERE, nonzero only - the honest ceiling.

  Also prints "old distinct nonzero" so you can confirm the match is not an artifact of a few
  endlessly repeated filler blocks: if distinct is close to total, blocks are genuinely unique
  and a high B/C means real content really did survive.

  Blocks are keyed by the first 8 bytes of MD5. At ~450k blocks the collision probability is
  ~5e-9, i.e. irrelevant here. MD5 is used for speed, not security.

  Exit codes: 0 ok | 1 error | 2 bad args
#>
param(
  [Parameter(Mandatory)][string]$Old,
  [Parameter(Mandatory)][string]$New,
  [int]$Block = 4096
)

if (-not (Test-Path -LiteralPath $Old)) { Write-Host "ERROR: Old not found: $Old"; exit 2 }
if (-not (Test-Path -LiteralPath $New)) { Write-Host "ERROR: New not found: $New"; exit 2 }
if ($Block -le 0) { Write-Host "ERROR: Block must be > 0"; exit 2 }

$code = @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;

public static class ContentMatch {
  static int ReadFull(Stream s, byte[] buf, int want) {
    int g = 0; while (g < want) { int r = s.Read(buf, g, want - g); if (r <= 0) break; g += r; } return g;
  }
  static bool IsZero(byte[] b, int n) {
    for (int i = 0; i < n; i++) if (b[i] != 0) return false;
    return true;
  }
  static ulong Key(byte[] h) {
    ulong v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | h[i]; return v;
  }
  // NB: a local function here would not compile - Add-Type on Windows PowerShell 5.1 uses an
  // old C# compiler with no local-function support. Keep helpers as static members.
  static double p(long a, long b) { return b > 0 ? 100.0 * a / b : 0.0; }

  public static string Run(string oldP, string newP, int block) {
    var log = new StringBuilder();
    var md5 = MD5.Create();
    byte[] buf = new byte[block];

    // ---- pass 1: index the OLD package ----
    var oldSeq = new List<ulong>();
    var oldAll = new HashSet<ulong>();
    var oldNZ  = new HashSet<ulong>();
    long oldZero = 0;
    using (var fa = new BufferedStream(File.OpenRead(oldP), 1 << 20)) {
      while (true) {
        int g = ReadFull(fa, buf, block);
        if (g != block) break;
        bool z = IsZero(buf, block);
        ulong k = Key(md5.ComputeHash(buf, 0, block));
        oldSeq.Add(k);
        oldAll.Add(k);
        if (z) oldZero++; else oldNZ.Add(k);
      }
    }

    // ---- pass 2: walk the NEW package ----
    long total = 0, zero = 0, sameOffset = 0, anywhere = 0, anywhereNZ = 0, totalNZ = 0;
    using (var fb = new BufferedStream(File.OpenRead(newP), 1 << 20)) {
      long i = 0;
      while (true) {
        int g = ReadFull(fb, buf, block);
        if (g != block) break;
        bool z = IsZero(buf, block);
        ulong k = Key(md5.ComputeHash(buf, 0, block));
        total++;
        if (z) zero++; else totalNZ++;
        if (i < oldSeq.Count && oldSeq[(int)i] == k) sameOffset++;
        if (oldAll.Contains(k)) anywhere++;
        if (!z && oldNZ.Contains(k)) anywhereNZ++;
        i++;
      }
    }

    long missing = totalNZ - anywhereNZ;

    log.AppendLine("== DECRYPTED (plaintext) content match, block=" + block + " ==");
    log.AppendLine("");
    log.AppendLine("old blocks total     : " + oldSeq.Count.ToString("N0"));
    log.AppendLine("old blocks all-zero  : " + oldZero.ToString("N0") + "  (" + p(oldZero, oldSeq.Count).ToString("F2") + " %)");
    log.AppendLine("old distinct nonzero : " + oldNZ.Count.ToString("N0") + "   (close to total => blocks are unique, so a high B/C is real)");
    log.AppendLine("");
    log.AppendLine("new blocks total     : " + total.ToString("N0"));
    log.AppendLine("new blocks all-zero  : " + zero.ToString("N0") + "  (" + p(zero, total).ToString("F2") + " %)");
    log.AppendLine("new blocks nonzero   : " + totalNZ.ToString("N0"));
    log.AppendLine("");
    log.AppendLine("A) SAME OFFSET match     : " + sameOffset.ToString("N0") + " / " + total.ToString("N0") + "  = " + p(sameOffset, total).ToString("F2") + " %");
    log.AppendLine("B) FOUND ANYWHERE (all)  : " + anywhere.ToString("N0") + " / " + total.ToString("N0") + "  = " + p(anywhere, total).ToString("F2") + " %");
    log.AppendLine("C) FOUND ANYWHERE (nonzero only) : " + anywhereNZ.ToString("N0") + " / " + totalNZ.ToString("N0") + "  = " + p(anywhereNZ, totalNZ).ToString("F2") + " %");
    log.AppendLine("");
    log.AppendLine("genuinely new content : " + missing.ToString("N0") + " blocks = "
                   + ((missing * (long)block) / 1048576.0).ToString("F1") + " MB"
                   + "   <-- what an update would actually have to download");
    log.AppendLine("");
    log.AppendLine("A = what naive same-offset reuse could take.");
    log.AppendLine("B = ceiling for content-addressed reuse, inflated by zero-fill.");
    log.AppendLine("C = the honest ceiling: real content that survived the update.");
    return log.ToString();
  }
}
'@

Add-Type -TypeDefinition $code -Language CSharp

$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
  [ContentMatch]::Run((Resolve-Path -LiteralPath $Old), (Resolve-Path -LiteralPath $New), $Block)
} catch {
  Write-Host "ERROR: $_"
  exit 1
}
"elapsed {0:N1}s" -f $sw.Elapsed.TotalSeconds
exit 0
