<#
  xskl  -  Xbox SKeLeton  (v1, contiguous matcher)

  Stores a genuine Xbox package as (install files you already have) + a tiny
  skeleton, so you never keep a second 1.78 GB copy and never re-download.

  LIFECYCLE  (decrypt / encrypt are YOUR key-touching steps, not done here):
    genuine.msixvc --[ xvdtool -eu  + CIK ]--> U   (unencrypted package)
    U + install/   --[ xskl capture        ]--> skeleton.skl   (~17 MB)
        (self-verify green) -> delete U and genuine.msixvc
    skeleton.skl + install/ --[ xskl reconstruct ]--> U'  (== U, byte-identical)
    U' --[ xvdtool -ee -pdu + CIK ]--> genuine.msixvc

  USAGE:
    xskl.ps1 capture     -U <U.msixvc> -Install <dir> -Skel <out.skl> [-CikId <guid>] [-Genuine <genuine.msixvc>]
    xskl.ps1 reconstruct -Skel <in.skl> -Install <dir> -Out <U_prime.img>

  SAFETY: capture reconstructs from the skeleton + install internally and
  compares SHA-256 to U. It prints "SAFE TO DELETE" ONLY on a byte-identical
  match. If verify fails, nothing should be deleted.
#>
param(
  [Parameter(Position=0)][string]$Verb = '',
  [string]$U = '',
  [string]$Install = '',
  [string]$Skel = '',
  [string]$Out = '',
  [string]$CikId = '',
  [string]$Genuine = '',
  [int]$Block = 4096
)

$code = @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;

public static class Xskl {
  class FileEnt { public string rel; public long size; public byte[] sha; public long uoff; }

  static int ReadFull(Stream s, byte[] buf, int want){ int g=0; while(g<want){ int r=s.Read(buf,g,want-g); if(r<=0) break; g+=r; } return g; }
  static ulong Fnv(byte[] b,int off,int len){ ulong h=14695981039346656037UL; for(int i=0;i<len;i++){ h^=b[off+i]; h*=1099511628211UL; } return h; }
  static bool Eq(byte[] a,byte[] b){ if(a.Length!=b.Length) return false; for(int i=0;i<a.Length;i++) if(a[i]!=b[i]) return false; return true; }
  static bool IsZero(byte[] b){ for(int i=0;i<b.Length;i++) if(b[i]!=0) return false; return true; }
  static string Hex(byte[] b){ StringBuilder sb=new StringBuilder(); for(int i=0;i<b.Length;i++) sb.Append(b[i].ToString("x2")); return sb.ToString(); }
  static string JStr(string s){ StringBuilder sb=new StringBuilder("\""); foreach(char c in s){ if(c=='\\'||c=='"') sb.Append('\\').Append(c); else if(c=='\n') sb.Append("\\n"); else if(c=='\r') sb.Append("\\r"); else if(c=='\t') sb.Append("\\t"); else sb.Append(c); } sb.Append('"'); return sb.ToString(); }
  static void WriteText(string path,string text){ try{ File.WriteAllText(path,text); }catch{} }
  static byte[] ShaFile(string p){ using(SHA256 h=SHA256.Create()) using(FileStream f=File.OpenRead(p)) return h.ComputeHash(f); }

  static void WU16(Stream s,ushort v){ s.WriteByte((byte)v); s.WriteByte((byte)(v>>8)); }
  static void WU32(Stream s,uint v){ for(int i=0;i<4;i++) s.WriteByte((byte)(v>>(8*i))); }
  static void WU64(Stream s,ulong v){ for(int i=0;i<8;i++) s.WriteByte((byte)(v>>(8*i))); }
  static ushort RU16(Stream s){ int a=s.ReadByte(); int b=s.ReadByte(); return (ushort)(a|(b<<8)); }
  static uint RU32(Stream s){ uint v=0; for(int i=0;i<4;i++) v|=((uint)(byte)s.ReadByte())<<(8*i); return v; }
  static ulong RU64(Stream s){ ulong v=0; for(int i=0;i<8;i++) v|=((ulong)(byte)s.ReadByte())<<(8*i); return v; }

  static bool CompareAt(FileStream ufs,long uoff,string fpath,long sz){
    if(uoff+sz>ufs.Length) return false;
    using(FileStream ff=File.OpenRead(fpath)){
      ufs.Position=uoff; byte[] a=new byte[65536]; byte[] b=new byte[65536]; long rem=sz;
      while(rem>0){ int want=(int)Math.Min(rem,(long)65536);
        int ga=ReadFull(ufs,a,want); int gb=ReadFull(ff,b,want);
        if(ga!=want||gb!=want) return false;
        for(int i=0;i<want;i++) if(a[i]!=b[i]) return false; rem-=want; }
    } return true;
  }
  static void CopyRangeAbs(FileStream src,long srcPos,FileStream dst,long dstPos,long len){
    src.Position=srcPos; dst.Position=dstPos; byte[] buf=new byte[1<<20]; long rem=len;
    while(rem>0){ int want=(int)Math.Min(rem,(long)buf.Length); int g=ReadFull(src,buf,want); dst.Write(buf,0,g); rem-=g; if(g<want) break; }
  }
  static byte[] CopyFileToOut(string src,FileStream dst,long dstPos){
    using(SHA256 sha=SHA256.Create()) using(FileStream f=File.OpenRead(src)){
      dst.Position=dstPos; byte[] buf=new byte[1<<20]; int n;
      while((n=f.Read(buf,0,buf.Length))>0){ dst.Write(buf,0,n); sha.TransformBlock(buf,0,n,null,0); }
      sha.TransformFinalBlock(new byte[0],0,0); return sha.Hash;
    }
  }

  public static string Capture(string upath,string installRoot,string skelPath,string cikId,string genuinePath,int block){
    StringBuilder log=new StringBuilder();
    if(!File.Exists(upath)) throw new Exception("U not found: "+upath);
    if(!Directory.Exists(installRoot)) throw new Exception("install root not found: "+installRoot);
    long ulen=new FileInfo(upath).Length;
    byte[] usha=ShaFile(upath);

    Dictionary<ulong,List<long>> idx=new Dictionary<ulong,List<long>>();
    using(FileStream fs=File.OpenRead(upath)){ byte[] buf=new byte[block]; long off=0;
      while(true){ int got=ReadFull(fs,buf,block); if(got<block) break; ulong hh=Fnv(buf,0,block);
        List<long> l; if(!idx.TryGetValue(hh,out l)){ l=new List<long>(); idx[hh]=l; } l.Add(off); off+=block; } }

    string root=Path.GetFullPath(installRoot).TrimEnd('\\','/');
    List<FileEnt> files=new List<FileEnt>();
    int prot=0; long protB=0; int miss=0; long missB=0; long foundB=0; int small=0; long smallB=0;
    using(FileStream ufs=File.OpenRead(upath)){
      foreach(string fp in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)){
        long sz; try{ sz=new FileInfo(fp).Length; }catch{ continue; }
        if(sz<block){ small++; smallB+=sz; continue; }
        byte[] first=new byte[block]; bool denied=false;
        try{ using(FileStream ff=File.OpenRead(fp)){ ReadFull(ff,first,block); } }catch{ denied=true; }
        if(denied){ prot++; protB+=sz; continue; }
        ulong hh=Fnv(first,0,block); long mo=-1; List<long> cands;
        if(idx.TryGetValue(hh,out cands)){ foreach(long c in cands){ if(CompareAt(ufs,c,fp,sz)){ mo=c; break; } } }
        if(mo>=0){ FileEnt e=new FileEnt(); e.rel=fp.Substring(root.Length).TrimStart('\\','/'); e.size=sz; e.sha=ShaFile(fp); e.uoff=mo; files.Add(e); foundB+=sz; }
        else { miss++; missB+=sz; }
      }
    }

    List<long[]> iv=new List<long[]>(); foreach(FileEnt e in files) iv.Add(new long[]{e.uoff,e.uoff+e.size});
    iv.Sort(delegate(long[] a,long[] b){ return a[0].CompareTo(b[0]); });
    List<long[]> cov=new List<long[]>(); long cs=-1,ce=-1;
    foreach(long[] r in iv){ if(cs<0){ cs=r[0]; ce=r[1]; } else if(r[0]<=ce){ if(r[1]>ce) ce=r[1]; } else { cov.Add(new long[]{cs,ce}); cs=r[0]; ce=r[1]; } }
    if(cs>=0) cov.Add(new long[]{cs,ce});
    List<long[]> ranges=new List<long[]>(); long p=0;
    foreach(long[] c in cov){ if(c[0]>p) ranges.Add(new long[]{p,c[0]-p}); if(c[1]>p) p=c[1]; }
    if(p<ulen) ranges.Add(new long[]{p,ulen-p});
    long skelB=0; foreach(long[] r in ranges) skelB+=r[1];

    byte[] cik=new byte[16]; if(!String.IsNullOrEmpty(cikId)){ try{ cik=Guid.Parse(cikId).ToByteArray(); }catch{} }
    byte[] gsha=new byte[32]; if(!String.IsNullOrEmpty(genuinePath) && File.Exists(genuinePath)) gsha=ShaFile(genuinePath);

    using(FileStream sk=new FileStream(skelPath,FileMode.Create,FileAccess.Write)){
      sk.Write(new byte[]{(byte)'X',(byte)'S',(byte)'K',(byte)'L'},0,4);
      WU16(sk,1); WU32(sk,(uint)block); WU64(sk,(ulong)ulen);
      sk.Write(usha,0,32); sk.Write(cik,0,16); sk.Write(gsha,0,32);
      WU32(sk,(uint)files.Count); WU32(sk,(uint)ranges.Count);
      foreach(FileEnt e in files){ byte[] pb=Encoding.UTF8.GetBytes(e.rel); WU16(sk,(ushort)pb.Length); sk.Write(pb,0,pb.Length);
        WU64(sk,(ulong)e.size); sk.Write(e.sha,0,32); WU32(sk,1); WU64(sk,(ulong)e.uoff); WU64(sk,0); WU64(sk,(ulong)e.size); }
      foreach(long[] r in ranges){ WU64(sk,(ulong)r[0]); WU64(sk,(ulong)r[1]); }
      using(FileStream ufs=File.OpenRead(upath)){ foreach(long[] r in ranges){ CopyRangeAbs(ufs,r[0],sk,sk.Position,r[1]); } }
    }
    long skelFile=new FileInfo(skelPath).Length;

    log.AppendLine("U size           : "+ulen.ToString("N0"));
    log.AppendLine("U sha256         : "+Hex(usha));
    log.AppendLine("files matched    : "+files.Count+"  ("+foundB.ToString("N0")+" B)");
    log.AppendLine("protected->skel  : "+prot+"  ("+protB.ToString("N0")+" B)");
    log.AppendLine("unmatched->skel  : "+miss+"  ("+missB.ToString("N0")+" B)");
    log.AppendLine("tiny(<blk)->skel : "+small+"  ("+smallB.ToString("N0")+" B)");
    log.AppendLine("skeleton ranges  : "+ranges.Count);
    log.AppendLine("skeleton bytes   : "+skelB.ToString("N0")+"  ("+(skelB/1048576.0).ToString("F2")+" MB)");
    log.AppendLine("skeleton.skl size: "+skelFile.ToString("N0")+"  ("+(skelFile/1048576.0).ToString("F2")+" MB)");

    string tmp=skelPath+".verify.tmp";
    byte[] vsha;
    try{ Reconstruct(skelPath,root,tmp); vsha=ShaFile(tmp); }
    finally{ try{ if(File.Exists(tmp)) File.Delete(tmp); }catch{} try{ if(File.Exists(tmp+".result.json")) File.Delete(tmp+".result.json"); }catch{} }
    bool verified=Eq(vsha,usha);
    log.AppendLine("");
    log.AppendLine("SELF-VERIFY (rebuild skeleton+install, compare to U):");
    log.AppendLine("  rebuilt sha    : "+Hex(vsha));
    if(verified) log.AppendLine("  *** VERIFIED byte-identical -- SAFE TO DELETE the package and U ***");
    else         log.AppendLine("  !!! VERIFY FAILED -- DO NOT DELETE anything; skeleton unusable !!!");
    string cj="{\"verb\":\"capture\",\"ok\":"+(verified?"true":"false")+",\"verified\":"+(verified?"true":"false")
      +",\"uSize\":"+ulen+",\"uSha\":\""+Hex(usha)+"\",\"filesMatched\":"+files.Count+",\"foundBytes\":"+foundB
      +",\"skeletonBytes\":"+skelB+",\"skelFileSize\":"+skelFile+",\"skelPath\":"+JStr(skelPath)+"}";
    WriteText(skelPath+".result.json",cj);
    return log.ToString();
  }

  public static string Reconstruct(string skelPath,string installRoot,string outPath){
    StringBuilder log=new StringBuilder();
    string root=Path.GetFullPath(installRoot).TrimEnd('\\','/');
    using(FileStream sk=File.OpenRead(skelPath)){
      byte[] magic=new byte[4]; ReadFull(sk,magic,4);
      if(!(magic[0]=='X'&&magic[1]=='S'&&magic[2]=='K'&&magic[3]=='L')) throw new Exception("bad .skl magic");
      ushort ver=RU16(sk); int block=(int)RU32(sk); long ulen=(long)RU64(sk);
      byte[] usha=new byte[32]; ReadFull(sk,usha,32);
      byte[] cik=new byte[16]; ReadFull(sk,cik,16);
      byte[] gsha=new byte[32]; ReadFull(sk,gsha,32);
      uint nFiles=RU32(sk); uint nRanges=RU32(sk);
      List<FileEnt> fe=new List<FileEnt>();
      for(uint i=0;i<nFiles;i++){ FileEnt e=new FileEnt();
        ushort pl=RU16(sk); byte[] pb=new byte[pl]; ReadFull(sk,pb,pl); e.rel=Encoding.UTF8.GetString(pb);
        e.size=(long)RU64(sk); e.sha=new byte[32]; ReadFull(sk,e.sha,32);
        uint nx=RU32(sk); long u0=0;
        for(uint x=0;x<nx;x++){ long uo=(long)RU64(sk); long fo=(long)RU64(sk); long ln=(long)RU64(sk); if(x==0) u0=uo; }
        e.uoff=u0; fe.Add(e); }
      List<long[]> ranges=new List<long[]>();
      for(uint i=0;i<nRanges;i++){ long uo=(long)RU64(sk); long ln=(long)RU64(sk); ranges.Add(new long[]{uo,ln}); }
      long blobStart=sk.Position;

      using(FileStream outfs=new FileStream(outPath,FileMode.Create,FileAccess.ReadWrite)){
        outfs.SetLength(ulen);
        long bpos=blobStart;
        foreach(long[] r in ranges){ CopyRangeAbs(sk,bpos,outfs,r[0],r[1]); bpos+=r[1]; }
        foreach(FileEnt e in fe){
          string full=Path.Combine(root,e.rel);
          if(!File.Exists(full)) throw new Exception("missing install file: "+e.rel);
          if(new FileInfo(full).Length!=e.size) throw new Exception("size mismatch: "+e.rel);
          byte[] got=CopyFileToOut(full,outfs,e.uoff);
          if(!Eq(got,e.sha)) throw new Exception("content sha mismatch (file changed?): "+e.rel);
        }
      }
      byte[] outsha=ShaFile(outPath);
      bool identical=Eq(outsha,usha);
      log.AppendLine("U' size : "+ulen.ToString("N0"));
      log.AppendLine("U' sha  : "+Hex(outsha));
      log.AppendLine("U  sha  : "+Hex(usha));
      if(identical) log.AppendLine("RESULT  : *** BYTE-IDENTICAL to U -- reconstruction OK ***");
      else          log.AppendLine("RESULT  : !!! MISMATCH -- output is NOT the original package !!!");
      if(!IsZero(cik)) log.AppendLine("next    : xvdtool -ee -pdu -cikfile <"+new Guid(cik).ToString()+".cik>  "+outPath);
      string rj="{\"verb\":\"reconstruct\",\"ok\":"+(identical?"true":"false")+",\"identical\":"+(identical?"true":"false")
        +",\"uSize\":"+ulen+",\"outSha\":\""+Hex(outsha)+"\",\"expectedSha\":\""+Hex(usha)+"\",\"outPath\":"+JStr(outPath)+"}";
      WriteText(outPath+".result.json",rj);
    }
    return log.ToString();
  }
}
'@

Add-Type -TypeDefinition $code -Language CSharp

function Show-Usage {
  Write-Host "xskl - Xbox SKeLeton (v1)"
  Write-Host ""
  Write-Host "  capture     -U <U.msixvc> -Install <dir> -Skel <out.skl> [-CikId <guid>] [-Genuine <genuine.msixvc>]"
  Write-Host "  reconstruct -Skel <in.skl> -Install <dir> -Out <U_prime.img>"
}

function Read-Ok($jsonPath){
  if(-not (Test-Path $jsonPath)){ return $false }
  try { return [bool]((Get-Content $jsonPath -Raw | ConvertFrom-Json).ok) } catch { return $false }
}

# Exit codes: 0 ok | 1 exception | 2 bad args | 10 verify/identical failed
$sw=[System.Diagnostics.Stopwatch]::StartNew()
switch ($Verb.ToLower()) {
  'capture' {
    if(-not $U -or -not $Install -or -not $Skel){ Show-Usage; exit 2 }
    try { [Xskl]::Capture($U,$Install,$Skel,$CikId,$Genuine,$Block) }
    catch { Write-Error $_; exit 1 }
    "elapsed {0:N1}s" -f $sw.Elapsed.TotalSeconds
    if(Read-Ok "$Skel.result.json"){ exit 0 } else { exit 10 }
  }
  'reconstruct' {
    if(-not $Skel -or -not $Install -or -not $Out){ Show-Usage; exit 2 }
    try { [Xskl]::Reconstruct($Skel,$Install,$Out) }
    catch { Write-Error $_; exit 1 }
    "elapsed {0:N1}s" -f $sw.Elapsed.TotalSeconds
    if(Read-Ok "$Out.result.json"){ exit 0 } else { exit 10 }
  }
  default { Show-Usage; exit 2 }
}