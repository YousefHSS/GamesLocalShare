import { useEffect, useState } from 'react';
import {
  Wifi, WifiOff, Users, Download, AlertCircle, Settings,
  Play, Pause, RefreshCw, Plus, FileText, Signal, X,
  Square, Trash2, RotateCcw, FolderOpen, EyeOff, Eye,
} from 'lucide-react';
import { useAppState, type GameInfo } from './store';
import { sendCommand } from './bridge';

interface GameContextMenu {
  x: number;
  y: number;
  game: GameInfo;
}

export default function App() {
  const s = useAppState();
  const [peerIP, setPeerIP] = useState('');
  const [incompleteTab, setIncompleteTab] = useState<'incomplete' | 'queue'>('incomplete');
  const [ctxMenu, setCtxMenu] = useState<GameContextMenu | null>(null);

  useEffect(() => {
    if (!ctxMenu) return;
    const close = () => setCtxMenu(null);
    window.addEventListener('click', close);
    window.addEventListener('resize', close);
    window.addEventListener('blur', close);
    return () => {
      window.removeEventListener('click', close);
      window.removeEventListener('resize', close);
      window.removeEventListener('blur', close);
    };
  }, [ctxMenu]);

  const openGameMenu = (e: React.MouseEvent, game: GameInfo) => {
    e.preventDefault();
    e.stopPropagation();
    setCtxMenu({ x: e.clientX, y: e.clientY, game });
  };

  const networkActive = s.isNetworkActive;
  const currentStep = s.localGames.length === 0 ? 1 : !networkActive ? 2 : s.networkPeers.length === 0 ? 3 : 4;

  const stepBadge = (n: number) => `px-3 py-1 rounded text-xs font-medium ${
    currentStep === n
      ? 'bg-blue-500/20 text-blue-400 border border-blue-500/30'
      : 'bg-slate-800 text-slate-500'
  }`;

  return (
    <div className="h-screen bg-slate-900 flex flex-col select-none text-slate-200">
      <div className="bg-slate-950 border-b border-slate-800 px-3 sm:px-4 py-2 flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <div className="w-8 h-8 bg-gradient-to-br from-blue-500 to-purple-500 rounded-lg flex items-center justify-center flex-shrink-0">
            <Wifi className="w-5 h-5 text-white" />
          </div>
          <span className="text-slate-200 font-semibold truncate">Games Local Share</span>
          <span className="text-slate-500 text-sm hidden md:inline">- LAN Game Sync</span>
        </div>
        <div className="text-xs text-slate-500 font-mono truncate hidden sm:block">{s.statusMessage}</div>
      </div>

      <div className="bg-slate-900 border-b border-slate-800 px-3 sm:px-6 py-3 sm:py-4">
        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3 max-w-7xl mx-auto">
          <div className="flex flex-wrap items-center gap-2 sm:gap-4">
            <div className="flex items-center gap-2">
              <div className={stepBadge(1)}>Step 1</div>
              <button
                onClick={() => sendCommand('ScanLocalGames')}
                disabled={s.isScanning}
                className="px-4 py-2 bg-blue-600 hover:bg-blue-700 disabled:bg-blue-800 disabled:cursor-not-allowed text-white rounded-lg transition-colors font-medium text-sm flex items-center gap-2"
              >
                {s.isScanning && <RefreshCw className="w-4 h-4 animate-spin" />}
                Scan My Games
              </button>
            </div>
            <div className="w-8 h-px bg-slate-700 hidden sm:block" />
            <div className="flex items-center gap-2">
              <div className={stepBadge(2)}>Step 2</div>
              <button
                onClick={() => sendCommand(networkActive ? 'StopNetwork' : 'StartNetwork')}
                className={`px-4 py-2 ${
                  networkActive ? 'bg-red-600 hover:bg-red-700' : 'bg-purple-600 hover:bg-purple-700'
                } text-white rounded-lg transition-colors font-medium text-sm flex items-center gap-2`}
              >
                {networkActive ? <WifiOff className="w-4 h-4" /> : <Wifi className="w-4 h-4" />}
                {networkActive ? 'Stop Network' : 'Start Network'}
              </button>
            </div>
            <div className="w-8 h-px bg-slate-700 hidden sm:block" />
            <div className="flex items-center gap-2">
              <div className={stepBadge(3)}>Step 3</div>
              <button
                onClick={() => sendCommand('ScanForPeers')}
                disabled={!networkActive || s.isScanningPeers}
                className="px-4 py-2 bg-green-600 hover:bg-green-700 disabled:bg-slate-700 disabled:cursor-not-allowed text-white rounded-lg transition-colors font-medium text-sm flex items-center gap-2"
              >
                {s.isScanningPeers ? <RefreshCw className="w-4 h-4 animate-spin" /> : <Users className="w-4 h-4" />}
                Scan for Peers
              </button>
            </div>
          </div>

          <div className="flex items-center gap-3 px-4 py-2 bg-slate-800/50 rounded-lg border border-slate-700/50 self-start lg:self-auto">
            <span className="text-slate-400 text-sm">Your IP:</span>
            <button
              onClick={() => sendCommand('CopyLocalIp')}
              className="text-blue-400 font-mono font-semibold hover:underline"
              title="Click to copy"
            >
              {s.localIpAddress || '—'}
            </button>
            <div className="flex items-center gap-1.5">
              <div className={`w-2 h-2 rounded-full ${networkActive ? 'bg-green-500 animate-pulse' : 'bg-slate-600'}`} />
              <span className={`text-sm font-medium ${networkActive ? 'text-green-400' : 'text-slate-500'}`}>
                {networkActive ? 'Online' : 'Offline'}
              </span>
            </div>
          </div>
        </div>
      </div>

      <div className="px-3 sm:px-6 py-3 bg-slate-800/30 border-b border-slate-800">
        <div className="max-w-7xl mx-auto">
          <p className="text-sm text-slate-400">
            <span className="font-semibold text-slate-300">How to use:</span> 1) Scan games | 2) Start network | 3) Find peers | 4) Download updates OR new games from peers |{' '}
            <span className="text-pink-400 ml-2 italic">Incomplete downloads can be resumed!</span>
          </p>
        </div>
      </div>

      <div className="flex-1 overflow-auto p-3 sm:p-6">
        <div className="max-w-7xl mx-auto grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4 xl:h-full auto-rows-[minmax(0,70vh)] xl:auto-rows-auto">
          <Panel title="My Games" count={s.localGames.length} icon={<Play className="w-4 h-4 text-white" />} gradient="from-blue-600 to-blue-700" subColor="text-blue-100">
            <div className="flex-1 overflow-auto p-4 space-y-3">
              {s.localGames.map((g) => (
                <div
                  key={g.appId}
                  onClick={() => sendCommand('SelectLocalGame', { appId: g.appId })}
                  onContextMenu={(e) => openGameMenu(e, g)}
                  className={`bg-slate-900/50 rounded-lg p-3 border transition-all group cursor-pointer ${
                    s.selectedLocalGame?.appId === g.appId ? 'border-blue-500' : 'border-slate-700/50 hover:border-blue-500/50'
                  } ${g.isHidden ? 'opacity-60' : ''}`}
                >
                  <div className="flex gap-3">
                    <div className="w-16 h-20 bg-slate-700 rounded overflow-hidden flex-shrink-0 flex items-center justify-center">
                      <img
                        src={`https://cdn.cloudflare.steamstatic.com/steam/apps/${g.appId}/header.jpg`}
                        alt={g.name}
                        className="w-full h-full object-cover"
                        onError={(e) => {
                          const img = e.currentTarget;
                          img.outerHTML = `<div class="w-full h-full flex items-center justify-center text-[10px] text-red-400">img blocked<br/>${g.appId}</div>`;
                        }}
                      />
                    </div>
                    <div className="flex-1 min-w-0">
                      <h4 className="font-semibold text-white text-sm truncate group-hover:text-blue-400 transition-colors">{g.name}</h4>
                      <p className="text-xs text-slate-400 mt-0.5">build {g.buildId}</p>
                      <p className="text-xs text-blue-400 mt-1 font-medium">{g.formattedSize}</p>
                    </div>
                  </div>
                </div>
              ))}
              {s.localGames.length === 0 && <Empty icon={<FileText className="w-12 h-12 text-slate-600 mb-3" />} title="No games found" sub='Click "Scan My Games"' />}
            </div>
          </Panel>

          <Panel
            title="Network Peers"
            count={s.networkPeers.length}
            icon={<Users className="w-4 h-4 text-white" />}
            gradient="from-purple-600 to-purple-700"
            subColor="text-purple-100"
            actions={<>
              <button onClick={() => sendCommand('TestConnection')} className="px-2 py-1 bg-white/20 hover:bg-white/30 rounded text-xs font-medium text-white transition-colors">Test</button>
              <button onClick={() => sendCommand('RefreshPeers')} className="px-2 py-1 bg-white/20 hover:bg-white/30 rounded text-xs font-medium text-white transition-colors">Refresh</button>
            </>}
          >
            <div className="p-4 space-y-4 flex-1 flex flex-col overflow-hidden">
              <div className="space-y-2">
                <label className="text-xs text-slate-400 font-medium">Connect to peer manually</label>
                <div className="flex gap-2">
                  <input
                    type="text"
                    value={peerIP}
                    onChange={(e) => setPeerIP(e.target.value)}
                    placeholder="IP address (e.g., 192.168.0.100)"
                    className="flex-1 bg-slate-900/50 border border-slate-700 rounded-lg px-3 py-2 text-sm text-white placeholder-slate-500 focus:outline-none focus:border-purple-500 transition-colors"
                  />
                  <button onClick={() => sendCommand('ConnectManualIp', { ip: peerIP })} className="px-4 py-2 bg-purple-600 hover:bg-purple-700 text-white rounded-lg text-sm font-medium transition-colors">Connect</button>
                </div>
              </div>

              <div className="flex-1 overflow-auto space-y-2">
                {s.networkPeers.map((p) => (
                  <div
                    key={p.peerId}
                    onClick={() => sendCommand('SelectPeer', { peerId: p.peerId })}
                    className={`bg-slate-900/50 rounded-lg p-3 border cursor-pointer ${
                      s.selectedPeer?.peerId === p.peerId ? 'border-purple-500' : 'border-slate-700/50 hover:border-purple-500/50'
                    }`}
                  >
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-2">
                        <Signal className={`w-4 h-4 ${p.isOnline ? 'text-green-400' : 'text-slate-500'}`} />
                        <div>
                          <p className="text-white text-sm font-medium">{p.displayName}</p>
                          <p className="text-slate-400 text-xs font-mono">{p.ipAddress}</p>
                        </div>
                      </div>
                      <span className="text-xs text-slate-400">{p.games?.length ?? 0} games</span>
                    </div>
                  </div>
                ))}
                {s.networkPeers.length === 0 && <Empty icon={<Users className="w-10 h-10 text-slate-600 mx-auto mb-2" />} title="No peers found" sub="Start network & scan" />}
              </div>

              <div className="bg-gradient-to-r from-purple-600/20 to-purple-700/20 rounded-lg p-3 border border-purple-500/30 flex flex-col max-h-[50%] flex-shrink-0">
                <div className="flex items-center justify-between mb-2 flex-shrink-0">
                  <div className="flex items-center gap-2">
                    <Plus className="w-4 h-4 text-purple-400" />
                    <span className="text-white text-sm font-medium">New Games from Peers</span>
                  </div>
                  <span className="text-xs text-purple-400 font-semibold">({s.availableFromPeers.length})</span>
                </div>
                <div className="flex-1 overflow-auto space-y-1">
                  {s.availableFromPeers.length === 0 ? (
                    <div className="text-xs text-slate-400 mt-1 italic">No new games found</div>
                  ) : (
                    s.availableFromPeers.map(game => {
                      const selected = s.selectedPeerGame?.appId === game.appId;
                      return (
                        <div
                          key={game.appId}
                          onClick={() => sendCommand('SelectPeerGame', { appId: game.appId })}
                          className={`bg-slate-900/50 rounded p-2 border cursor-pointer ${
                            selected ? 'border-purple-500' : 'border-slate-700/50 hover:border-purple-500/50'
                          }`}
                        >
                          <p className="text-white text-xs font-medium truncate">{game.name}</p>
                          <p className="text-slate-400 text-[10px] mt-0.5">{game.buildId} • {game.formattedSize}</p>
                          {selected && (
                            <button
                              onClick={(e) => { e.stopPropagation(); sendCommand('DownloadNewGame'); }}
                              className="mt-2 w-full py-1.5 bg-purple-600 hover:bg-purple-700 text-white rounded text-xs font-medium flex items-center justify-center gap-1"
                            >
                              <Download className="w-3 h-3" /> Download
                            </button>
                          )}
                        </div>
                      );
                    })
                  )}
                </div>
              </div>
            </div>
          </Panel>

          <Panel
            title="Updates Available"
            count={s.availableSyncs.length}
            icon={<Download className="w-4 h-4 text-white" />}
            gradient="from-green-600 to-green-700"
            subColor="text-green-100"
            actions={<button onClick={() => sendCommand('AddAllUpdatesToQueue')} className="px-2 py-1 bg-white/20 hover:bg-white/30 rounded text-xs font-medium text-white transition-colors">+ Queue All</button>}
          >
            <div className="flex-1 overflow-auto p-4 space-y-2">
              {s.availableSyncs.map((sy, i) => (
                <div
                  key={i}
                  onClick={() => sendCommand('SelectSyncItem', { appId: sy.remoteGame?.appId })}
                  className={`bg-slate-900/50 rounded-lg p-3 border cursor-pointer ${
                    s.selectedSyncItem?.remoteGame?.appId === sy.remoteGame?.appId ? 'border-green-500' : 'border-slate-700/50 hover:border-green-500/50'
                  }`}
                >
                  <p className="text-white text-sm font-medium truncate">{sy.displayName}</p>
                  <p className="text-xs text-slate-400 mt-0.5">{sy.syncDescription}</p>
                </div>
              ))}
              {s.availableSyncs.length === 0 && <Empty icon={<Download className="w-12 h-12 text-slate-600 mb-3" />} title="No updates" sub="Connect to peers to check for updates" />}
            </div>
            <div className="p-4 border-t border-slate-700/50">
              <button onClick={() => sendCommand('StartSync')} disabled={!s.selectedSyncItem} className="w-full py-2.5 bg-slate-700 hover:bg-slate-600 disabled:opacity-50 text-slate-300 rounded-lg text-sm font-medium transition-colors">Download Update</button>
            </div>
          </Panel>

          <Panel
            title="Transfers"
            count={s.incompleteTransfers.length + s.downloadQueue.length}
            icon={<AlertCircle className="w-4 h-4 text-white" />}
            gradient="from-red-600 to-cyan-700"
            subColor="text-red-100"
            actions={incompleteTab === 'incomplete'
              ? <button onClick={() => sendCommand('AddAllIncompleteToQueue')} className="px-2 py-1 bg-white/20 hover:bg-white/30 rounded text-xs font-medium text-white transition-colors">+ Queue All</button>
              : <>
                  <button onClick={() => sendCommand('RetryFailedAndPaused')} className="px-2 py-1 bg-white/20 hover:bg-white/30 rounded text-xs font-medium text-white transition-colors flex items-center gap-1"><RotateCcw className="w-3 h-3" />Retry</button>
                  <button onClick={() => sendCommand('ClearQueue')} className="px-2 py-1 bg-white/20 hover:bg-white/30 rounded text-xs font-medium text-white transition-colors">Clear</button>
                </>
            }
          >
            <div className="flex border-b border-slate-700/50 bg-slate-900/40 flex-shrink-0">
              <button
                onClick={() => setIncompleteTab('incomplete')}
                className={`flex-1 px-3 py-2 text-xs font-medium flex items-center justify-center gap-1.5 transition-colors ${
                  incompleteTab === 'incomplete' ? 'text-red-300 border-b-2 border-red-500 bg-slate-900/40' : 'text-slate-400 hover:text-slate-200'
                }`}
              >
                <AlertCircle className="w-3.5 h-3.5" />
                Incomplete <span className="text-slate-500">({s.incompleteTransfers.length})</span>
              </button>
              <button
                onClick={() => setIncompleteTab('queue')}
                className={`flex-1 px-3 py-2 text-xs font-medium flex items-center justify-center gap-1.5 transition-colors ${
                  incompleteTab === 'queue' ? 'text-cyan-300 border-b-2 border-cyan-500 bg-slate-900/40' : 'text-slate-400 hover:text-slate-200'
                }`}
              >
                <Download className="w-3.5 h-3.5" />
                Queue <span className="text-slate-500">({s.downloadQueue.length})</span>
              </button>
            </div>
            {incompleteTab === 'incomplete' ? (
            <div className="flex-1 overflow-auto p-4 space-y-2">
              {s.incompleteTransfers.map((t) => {
                const selected = s.selectedIncompleteTransfer?.gameAppId === t.gameAppId;
                return (
                  <div
                    key={t.gameAppId}
                    onClick={() => sendCommand('SelectIncompleteTransfer', { appId: t.gameAppId })}
                    className={`bg-slate-900/50 rounded-lg p-3 border cursor-pointer ${
                      selected ? 'border-red-500' : 'border-slate-700/50 hover:border-red-500/50'
                    }`}
                  >
                    <p className="text-white text-sm font-medium truncate">{t.gameName}</p>
                    <p className="text-xs text-slate-400 mt-0.5">{t.formattedProgress}</p>
                    <div className="w-full bg-slate-800 rounded h-1 mt-2 overflow-hidden">
                      <div className="bg-red-500 h-full transition-all" style={{ width: `${t.progressPercent}%` }} />
                    </div>
                    {selected && (
                      <div className="flex gap-2 mt-2">
                        <button
                          onClick={(e) => { e.stopPropagation(); sendCommand('ResumeTransfer'); }}
                          className="flex-1 px-2 py-1 bg-green-600 hover:bg-green-700 text-white rounded text-xs font-medium flex items-center justify-center gap-1"
                        >
                          <Play className="w-3 h-3" /> Resume
                        </button>
                        <button
                          onClick={(e) => { e.stopPropagation(); if (confirm(`Delete incomplete transfer for ${t.gameName}?`)) sendCommand('DeleteIncompleteTransfer'); }}
                          className="px-2 py-1 bg-red-600 hover:bg-red-700 text-white rounded text-xs font-medium flex items-center justify-center"
                        >
                          <Trash2 className="w-3 h-3" />
                        </button>
                      </div>
                    )}
                  </div>
                );
              })}
              {s.incompleteTransfers.length === 0 && <Empty icon={<AlertCircle className="w-12 h-12 text-slate-600 mb-3" />} title="No incomplete downloads" sub="All downloads completed" />}
            </div>
            ) : (
            <div className="flex-1 overflow-auto">
              {s.downloadQueue.length === 0 ? (
                <div className="p-6 text-center">
                  <Download className="w-12 h-12 text-slate-600 mb-3 mx-auto" />
                  <p className="text-slate-500 text-sm">Queue is empty</p>
                  <p className="text-slate-600 text-xs mt-1">Add games to download</p>
                </div>
              ) : (
                s.downloadQueue.map((q) => (
                  <div key={q.gameAppId} className="px-3 py-2 border-b border-slate-800 flex items-center justify-between">
                    <div className="min-w-0 flex-1">
                      <p className="text-xs text-white truncate">{q.gameName}</p>
                      <p className="text-[10px]" style={{ color: q.statusColor }}>{q.statusText}</p>
                    </div>
                    <div className="flex items-center gap-0.5">
                      <button title="Move up" onClick={() => sendCommand('MoveQueueItemUp', { appId: q.gameAppId })} className="p-1 hover:bg-slate-700 rounded text-slate-400 text-xs">▲</button>
                      <button title="Move down" onClick={() => sendCommand('MoveQueueItemDown', { appId: q.gameAppId })} className="p-1 hover:bg-slate-700 rounded text-slate-400 text-xs">▼</button>
                      <button title="Remove" onClick={() => sendCommand('RemoveFromQueue', { appId: q.gameAppId })} className="p-1 hover:bg-slate-700 rounded">
                        <X className="w-3 h-3 text-slate-400" />
                      </button>
                    </div>
                  </div>
                ))
              )}
            </div>
            )}
            <div className="p-4 border-t border-slate-700/50 space-y-2">
              <button
                onClick={() => sendCommand(s.isQueueProcessing ? 'PauseQueue' : 'StartQueue')}
                className={`w-full py-2.5 ${
                  s.isQueueProcessing ? 'bg-amber-600 hover:bg-amber-700' : 'bg-cyan-600 hover:bg-cyan-700'
                } text-white rounded-lg text-sm font-medium transition-colors flex items-center justify-center gap-2`}
              >
                {s.isQueueProcessing ? <><Pause className="w-4 h-4" />Pause Queue</> : <><Play className="w-4 h-4" />Start Queue</>}
              </button>
            </div>
          </Panel>
        </div>
      </div>

      {s.isTransferring && (
        <div className="bg-gradient-to-r from-blue-900/60 to-purple-900/60 border-t border-blue-700/50 px-3 sm:px-6 py-2.5">
          <div className="max-w-7xl mx-auto flex flex-wrap items-center gap-3 sm:gap-4">
            <Download className="w-5 h-5 text-blue-400 animate-pulse flex-shrink-0" />
            <div className="flex-1 min-w-0">
              <div className="flex items-center justify-between mb-1">
                <span className="text-sm font-medium text-white truncate">{s.currentTransferGameName || 'Transferring...'}</span>
                <span className="text-xs text-slate-300 font-mono ml-2 flex-shrink-0">
                  {s.currentTransferFormattedProgress} • {s.currentTransferSpeed} • ETA {s.currentTransferTimeRemaining}
                </span>
              </div>
              <div className="w-full bg-slate-800 rounded h-2 overflow-hidden">
                <div className="bg-gradient-to-r from-blue-500 to-purple-500 h-full transition-all" style={{ width: `${s.currentTransferProgress}%` }} />
              </div>
              {s.currentTransferFile && <p className="text-[10px] text-slate-400 mt-1 truncate font-mono">{s.currentTransferFile}</p>}
            </div>
            <div className="flex gap-1 flex-shrink-0">
              <button onClick={() => sendCommand('ToggleSpeedUnit')} title="Toggle Mbps/MBps" className="px-2 py-1 bg-slate-700/60 hover:bg-slate-700 rounded text-xs text-slate-200">{s.showSpeedInMbps ? 'Mbps' : 'MB/s'}</button>
              <button onClick={() => sendCommand('PauseTransfer')} className="px-3 py-1.5 bg-amber-600 hover:bg-amber-700 text-white rounded text-xs font-medium flex items-center gap-1"><Pause className="w-3 h-3" />Pause</button>
              <button onClick={() => { if (confirm('Stop the current transfer?')) sendCommand('StopTransfer'); }} className="px-3 py-1.5 bg-red-600 hover:bg-red-700 text-white rounded text-xs font-medium flex items-center gap-1"><Square className="w-3 h-3" />Stop</button>
            </div>
          </div>
        </div>
      )}

      <div className="bg-slate-950 border-t border-slate-800 px-3 sm:px-6 py-2.5 flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-4">
          <button onClick={() => sendCommand('OpenSettings')} className="flex items-center gap-2 px-3 py-1.5 hover:bg-slate-800 rounded transition-colors group">
            <Settings className="w-4 h-4 text-slate-400 group-hover:text-slate-300" />
            <span className="text-sm text-slate-400 group-hover:text-slate-300">Settings</span>
          </button>
          <button onClick={() => sendCommand('ToggleHighSpeedMode')} className="flex items-center gap-2 px-3 py-1.5 hover:bg-slate-800 rounded transition-colors group">
            <Wifi className={`w-4 h-4 ${s.highSpeedMode ? 'text-amber-400' : 'text-slate-400'}`} />
            <span className={`text-sm ${s.highSpeedMode ? 'text-amber-400' : 'text-slate-400 group-hover:text-slate-300'}`}>{s.highSpeedMode ? 'High-Speed' : 'WiFi Mode'}</span>
          </button>
        </div>
        <div className="flex items-center gap-4">
          <span className="text-sm text-slate-500">{s.lastError || 'Games Local Share'}</span>
          <button onClick={() => sendCommand('ToggleLog')} className="flex items-center gap-2 px-3 py-1.5 hover:bg-slate-800 rounded transition-colors group">
            <FileText className="w-4 h-4 text-slate-400 group-hover:text-slate-300" />
            <span className="text-sm text-slate-400 group-hover:text-slate-300">Log</span>
          </button>
        </div>
      </div>

      {ctxMenu && (
        <div
          className="fixed z-50 bg-slate-900 border border-slate-700 rounded-lg shadow-2xl py-1 min-w-[200px]"
          style={{ left: Math.min(ctxMenu.x, window.innerWidth - 220), top: Math.min(ctxMenu.y, window.innerHeight - 120) }}
          onClick={(e) => e.stopPropagation()}
          onContextMenu={(e) => e.preventDefault()}
        >
          <button
            onClick={() => { sendCommand('OpenGameFolder', { appId: ctxMenu.game.appId }); setCtxMenu(null); }}
            className="w-full text-left px-3 py-2 text-sm text-slate-200 hover:bg-slate-800 flex items-center gap-2"
          >
            <FolderOpen className="w-4 h-4 text-blue-400" />
            Open game folder
          </button>
          <button
            onClick={() => { sendCommand('ToggleGameVisibility', { appId: ctxMenu.game.appId }); setCtxMenu(null); }}
            className="w-full text-left px-3 py-2 text-sm text-slate-200 hover:bg-slate-800 flex items-center gap-2"
          >
            {ctxMenu.game.isHidden ? <Eye className="w-4 h-4 text-green-400" /> : <EyeOff className="w-4 h-4 text-amber-400" />}
            {ctxMenu.game.isHidden ? 'Show on network' : 'Hide from network'}
          </button>
        </div>
      )}

      {s.isLogVisible && (
        <div className="absolute bottom-12 right-2 sm:right-6 left-2 sm:left-auto sm:w-[500px] h-80 bg-slate-950 border border-slate-700 rounded-lg shadow-2xl flex flex-col overflow-hidden">
          <div className="flex items-center justify-between px-3 py-2 border-b border-slate-800 bg-slate-900">
            <span className="text-sm font-semibold text-slate-300">Log</span>
            <div className="flex gap-2">
              <button onClick={() => sendCommand('ClearLog')} className="text-xs text-slate-400 hover:text-slate-200">Clear</button>
              <button onClick={() => sendCommand('ToggleLog')} className="text-slate-400 hover:text-slate-200"><X className="w-4 h-4" /></button>
            </div>
          </div>
          <div className="flex-1 overflow-auto p-2 font-mono text-xs space-y-0.5">
            {s.logMessages.map((m, i) => (
              <div key={i} className="flex gap-2">
                <span className="text-slate-600">{m.formattedTime}</span>
                <span style={{ color: m.typeColor }}>{m.message}</span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

function Panel({
  title, count, icon, gradient, subColor, actions, children,
}: {
  title: string; count: number; icon: React.ReactNode; gradient: string;
  subColor: string; actions?: React.ReactNode; children: React.ReactNode;
}) {
  return (
    <div className="bg-slate-800/40 rounded-xl border border-slate-700/50 flex flex-col overflow-hidden">
      <div className={`bg-gradient-to-r ${gradient} px-4 py-3 flex items-center justify-between`}>
        <div className="flex items-center gap-2">
          <div className="w-8 h-8 bg-white/20 rounded-lg flex items-center justify-center">{icon}</div>
          <div>
            <h3 className="font-semibold text-white">{title}</h3>
            <p className={`text-xs ${subColor}`}>({count})</p>
          </div>
        </div>
        {actions && <div className="flex gap-1">{actions}</div>}
      </div>
      {children}
    </div>
  );
}

function Empty({ icon, title, sub }: { icon: React.ReactNode; title: string; sub: string }) {
  return (
    <div className="flex flex-col items-center justify-center py-12 text-center">
      {icon}
      <p className="text-slate-500 text-sm">{title}</p>
      <p className="text-slate-600 text-xs mt-1">{sub}</p>
    </div>
  );
}
