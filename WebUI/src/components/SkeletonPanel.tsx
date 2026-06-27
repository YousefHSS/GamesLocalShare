import { Boxes, FolderOpen, Play, Square, Info, Archive, RotateCcw, HardDriveDownload, Server } from 'lucide-react';
import { useAppState } from '../store';
import { sendCommand } from '../bridge';

function fmtBytes(n: number): string {
  if (!n || n <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = n;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v >= 100 || i === 0 ? 0 : 1)} ${units[i]}`;
}

export default function SkeletonPanel() {
  const s = useAppState();
  const watching = s.isSkeletonWatching;

  const totalSaved = s.skeletonCaptures.reduce((acc, c) => acc + (c.savedBytes || 0), 0);

  return (
    <div className="flex-1 min-h-0 flex flex-col overflow-hidden">
      {/* Toolbar */}
      <div className="flex items-center gap-2 px-4 py-3 border-b border-slate-700/50 flex-shrink-0 flex-wrap">
        <button
          onClick={() => sendCommand('ToggleSkeletonWatcher')}
          className={`px-3 py-1.5 ${
            watching ? 'bg-red-600 hover:bg-red-700' : 'bg-green-600 hover:bg-green-700'
          } text-white rounded text-xs font-medium flex items-center gap-1.5`}
        >
          {watching ? <><Square className="w-3.5 h-3.5" /> Stop Watching</> : <><Play className="w-3.5 h-3.5" /> Start Watching</>}
        </button>
        <button
          onClick={() => sendCommand('OpenSkeletonDropFolder')}
          className="px-3 py-1.5 bg-slate-700 hover:bg-slate-600 text-white rounded text-xs font-medium flex items-center gap-1.5"
        >
          <FolderOpen className="w-3.5 h-3.5" /> Open Drop Folder
        </button>
        <div className="flex items-center gap-1.5 ml-auto">
          <div className={`w-2 h-2 rounded-full ${watching ? 'bg-green-500 animate-pulse' : 'bg-slate-600'}`} />
          <span className={`text-xs font-medium ${watching ? 'text-green-400' : 'text-slate-500'}`}>
            {watching ? 'Watching' : 'Idle'}
          </span>
        </div>
      </div>

      {/* LAN cache proxy */}
      <div className="flex items-center gap-2 px-4 py-2 border-b border-slate-700/50 flex-shrink-0 flex-wrap">
        <Server className="w-3.5 h-3.5 text-purple-400 flex-shrink-0" />
        <span className="text-xs font-medium text-slate-300">LAN Cache Proxy</span>
        <button
          onClick={() => sendCommand('ToggleCacheProxy')}
          className={`px-3 py-1 ${
            s.isCacheProxyRunning ? 'bg-red-600 hover:bg-red-700' : 'bg-purple-600 hover:bg-purple-700'
          } text-white rounded text-xs font-medium flex items-center gap-1.5`}
        >
          {s.isCacheProxyRunning ? <><Square className="w-3 h-3" /> Stop Proxy</> : <><Play className="w-3 h-3" /> Start Proxy</>}
        </button>
        {s.isCacheProxyRunning && s.cacheProxyStats && (
          <span className="text-[11px] text-slate-400 font-mono ml-auto">{s.cacheProxyStats}</span>
        )}
        {!s.isCacheProxyRunning && (
          <span className="text-[11px] text-slate-500 ml-auto">serves cached packages on :80 (needs admin — UAC on start)</span>
        )}
      </div>

      {/* How it works */}
      <div className="flex items-start gap-1.5 px-4 py-2 text-[11px] text-slate-500 border-b border-slate-700/30 flex-shrink-0">
        <Info className="w-3 h-3 flex-shrink-0 mt-0.5" />
        <span>
          When a game install completes, its encrypted package (<span className="font-mono">&lt;PackageFullName&gt;.msixvc</span>)
          is auto-located in the configured cache root and a ~17&nbsp;MB skeleton is captured and self-verified — once verified
          you can delete the full package and rebuild it byte-identically from the skeleton plus the installed files. xvdtool
          decrypts on the fly with your own CIK (no full decrypted copy ever hits disk); you can also drop a package manually
          as <span className="font-mono">&lt;TitleFolder&gt;.msixvc</span> in the drop folder.
        </span>
      </div>

      {s.skeletonDropFolder && (
        <div className="px-4 py-1.5 text-[11px] text-slate-500 border-b border-slate-700/30 flex-shrink-0">
          Drop folder: <span className="font-mono text-slate-400">{s.skeletonDropFolder}</span>
        </div>
      )}

      {/* Content: captures + log */}
      <div className="flex-1 min-h-0 overflow-auto p-4 space-y-4">
        {/* Captures */}
        <div>
          <div className="flex items-center justify-between mb-2">
            <h4 className="text-xs font-semibold text-slate-300 flex items-center gap-1.5">
              <Archive className="w-3.5 h-3.5 text-blue-400" /> Captured Skeletons ({s.skeletonCaptures.length})
            </h4>
            {totalSaved > 0 && (
              <span className="text-[11px] text-green-400 font-medium">{fmtBytes(totalSaved)} saved</span>
            )}
          </div>
          {s.skeletonCaptures.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-8 text-center">
              <Boxes className="w-10 h-10 text-slate-600 mb-2" />
              <p className="text-slate-500 text-sm">No skeletons captured yet</p>
              <p className="text-slate-600 text-xs mt-1">Start watching, then install a game (or drop a package)</p>
            </div>
          ) : (
            <div className="space-y-2">
              {s.skeletonCaptures.map((c, i) => {
                const pct = c.packageBytes > 0 ? (1 - c.skeletonBytes / c.packageBytes) * 100 : 0;
                return (
                  <div key={`${c.name}-${i}`} className="bg-slate-800/40 border border-slate-700/50 rounded-lg px-3 py-2">
                    <div className="flex items-center justify-between gap-2">
                      <p className="text-sm text-white truncate">{c.name}</p>
                      <div className="flex items-center gap-1.5 flex-shrink-0">
                        <span className="text-[10px] px-1.5 py-0.5 rounded border bg-green-900/40 text-green-300 border-green-700/50">
                          −{pct.toFixed(0)}%
                        </span>
                        <button
                          onClick={() => sendCommand('RestoreSkeleton', { name: c.name })}
                          title="Rebuild the genuine package from this skeleton + installed files and serve it back to the cache in place (so Verify/update HITs, no re-download)"
                          className="px-2 py-0.5 bg-slate-700 hover:bg-blue-700 text-white rounded text-[10px] font-medium flex items-center gap-1"
                        >
                          <RotateCcw className="w-3 h-3" /> Restore
                        </button>
                        <button
                          onClick={() => sendCommand('ReconstructSkeleton', { name: c.name })}
                          title="Reconstruct the genuine package to a folder you choose (e.g. an external drive), keeping the nested CDN path so another PC's proxy serves it as a HIT"
                          className="px-2 py-0.5 bg-slate-700 hover:bg-emerald-700 text-white rounded text-[10px] font-medium flex items-center gap-1"
                        >
                          <HardDriveDownload className="w-3 h-3" /> Reconstruct…
                        </button>
                      </div>
                    </div>
                    <p className="text-[11px] text-slate-400 mt-0.5 font-mono">
                      {fmtBytes(c.skeletonBytes)} skeleton replaces {fmtBytes(c.packageBytes)} package
                      <span className="text-green-400"> · saves {fmtBytes(c.savedBytes)}</span>
                    </p>
                  </div>
                );
              })}
            </div>
          )}
        </div>

        {/* Activity log */}
        <div>
          <h4 className="text-xs font-semibold text-slate-300 mb-2">Activity</h4>
          {s.skeletonLog.length === 0 ? (
            <p className="text-xs text-slate-500 italic">No activity yet</p>
          ) : (
            <div className="bg-slate-950 border border-slate-800 rounded-lg p-2 font-mono text-[11px] space-y-0.5 max-h-48 overflow-auto">
              {s.skeletonLog.map((line, i) => (
                <div key={i} className="text-slate-400 whitespace-pre-wrap break-all">{line}</div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
