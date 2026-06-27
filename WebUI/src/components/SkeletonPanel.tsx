import { useState } from 'react';
import {
  Boxes, FolderOpen, Play, Square, Info, Archive, RotateCcw, HardDriveDownload,
  Server, ChevronDown, ChevronRight, CheckCircle2,
} from 'lucide-react';
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

function StatusPill({ on, onLabel, offLabel }: { on: boolean; onLabel: string; offLabel: string }) {
  return (
    <span className={`inline-flex items-center gap-1.5 px-2 py-1 rounded text-[11px] font-medium ${
      on ? 'bg-green-900/40 text-green-300 border border-green-700/50'
         : 'bg-slate-800 text-slate-400 border border-slate-700/50'
    }`}>
      <span className={`w-1.5 h-1.5 rounded-full ${on ? 'bg-green-400 animate-pulse' : 'bg-slate-600'}`} />
      {on ? onLabel : offLabel}
    </span>
  );
}

export default function SkeletonPanel() {
  const s = useAppState();
  const [advanced, setAdvanced] = useState(false);

  const watching = s.isSkeletonWatching;
  const proxy = s.isCacheProxyRunning;
  const healthy = watching && proxy;
  const totalSaved = s.skeletonCaptures.reduce((acc, c) => acc + (c.savedBytes || 0), 0);

  return (
    <div className="flex-1 min-h-0 flex flex-col overflow-hidden">
      {/* Automatic status header */}
      <div className="px-4 py-3 border-b border-slate-700/50 flex-shrink-0">
        <div className="flex items-center gap-2 flex-wrap">
          {healthy ? (
            <span className="inline-flex items-center gap-1.5 text-sm font-medium text-green-400">
              <CheckCircle2 className="w-4 h-4" /> Xbox single-copy is running automatically
            </span>
          ) : (
            <span className="text-sm font-medium text-amber-300">Xbox single-copy is partially active</span>
          )}
          <button
            onClick={() => setAdvanced(v => !v)}
            className="ml-auto inline-flex items-center gap-1 text-[11px] text-slate-400 hover:text-slate-200"
          >
            {advanced ? <ChevronDown className="w-3.5 h-3.5" /> : <ChevronRight className="w-3.5 h-3.5" />}
            Advanced
          </button>
        </div>
        <div className="flex items-center gap-2 mt-2 flex-wrap">
          <StatusPill on={watching} onLabel="Auto-capture on" offLabel="Auto-capture off" />
          <StatusPill on={proxy} onLabel="Cache proxy on" offLabel="Cache proxy off" />
          {proxy && s.cacheProxyStats && (
            <span className="text-[11px] text-slate-400 font-mono ml-auto">{s.cacheProxyStats}</span>
          )}
        </div>
        {!healthy && (
          <p className="text-[11px] text-slate-500 mt-2">
            Capture and serving run on their own once both are on. If they didn't start, accept the admin
            prompt — or use the Advanced controls below. (Auto-start can be toggled in Settings → Xbox Game Pass.)
          </p>
        )}
      </div>

      {/* Advanced manual controls (hidden by default) */}
      {advanced && (
        <div className="px-4 py-3 border-b border-slate-700/50 flex-shrink-0 space-y-2 bg-slate-900/40">
          <div className="flex items-center gap-2 flex-wrap">
            <button
              onClick={() => sendCommand('ToggleSkeletonWatcher')}
              className={`px-3 py-1.5 ${
                watching ? 'bg-red-600 hover:bg-red-700' : 'bg-green-600 hover:bg-green-700'
              } text-white rounded text-xs font-medium flex items-center gap-1.5`}
            >
              {watching ? <><Square className="w-3.5 h-3.5" /> Stop Watching</> : <><Play className="w-3.5 h-3.5" /> Start Watching</>}
            </button>
            <button
              onClick={() => sendCommand('ToggleCacheProxy')}
              className={`px-3 py-1.5 ${
                proxy ? 'bg-red-600 hover:bg-red-700' : 'bg-purple-600 hover:bg-purple-700'
              } text-white rounded text-xs font-medium flex items-center gap-1.5`}
            >
              <Server className="w-3.5 h-3.5" />
              {proxy ? 'Stop Proxy' : 'Start Proxy'}
            </button>
            <button
              onClick={() => sendCommand('OpenSkeletonDropFolder')}
              className="px-3 py-1.5 bg-slate-700 hover:bg-slate-600 text-white rounded text-xs font-medium flex items-center gap-1.5"
            >
              <FolderOpen className="w-3.5 h-3.5" /> Drop Folder
            </button>
          </div>
          {s.skeletonDropFolder && (
            <p className="text-[11px] text-slate-500">
              Drop folder: <span className="font-mono text-slate-400">{s.skeletonDropFolder}</span>
            </p>
          )}
        </div>
      )}

      {/* How it works */}
      <div className="flex items-start gap-1.5 px-4 py-2 text-[11px] text-slate-500 border-b border-slate-700/30 flex-shrink-0">
        <Info className="w-3 h-3 flex-shrink-0 mt-0.5" />
        <span>
          Install Xbox games normally. When an install completes its encrypted package is captured as a
          ~tiny skeleton and self-verified — then the full package is safe to delete and can be rebuilt
          byte-identically from the skeleton plus the installed files. Everything happens automatically.
        </span>
      </div>

      {/* Content: captures + log */}
      <div className="flex-1 min-h-0 overflow-auto p-4 space-y-4">
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
              <p className="text-slate-600 text-xs mt-1">Install an Xbox game and it'll be captured automatically</p>
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
                        {advanced && (
                          <>
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
                          </>
                        )}
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
