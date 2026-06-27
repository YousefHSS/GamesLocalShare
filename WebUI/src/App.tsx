import { useEffect, useState } from 'react';
import {
  Wifi, WifiOff, Users, Download, AlertCircle, Settings,
  Play, Pause, RefreshCw, Plus, FileText, Signal, X,
  Square, Trash2, RotateCcw, FolderOpen, EyeOff, Eye, Search, HardDrive, Boxes,
} from 'lucide-react';
import { useAppState, type GameInfo, type SettingsPayload } from './store';
import { sendCommand } from './bridge';
import SettingsModal from './components/SettingsModal';
import DrivesPanel from './components/DrivesPanel';
import SkeletonPanel from './components/SkeletonPanel';
import PlatformIcon from './components/PlatformIcon';


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
  const [localGameFilter, setLocalGameFilter] = useState('');
  const [peerGameFilter, setPeerGameFilter] = useState('');
  const [settingsPayload, setSettingsPayload] = useState<SettingsPayload | null>(null);
  const [showDrives, setShowDrives] = useState(false);
  const [showSkeleton, setShowSkeleton] = useState(false);

  useEffect(() => {
    (window as any).__openSettings = (p: SettingsPayload) => setSettingsPayload(p);
    return () => { (window as any).__openSettings = undefined; };
  }, []);

  const filteredLocalGames = localGameFilter.trim()
    ? s.localGames.filter(g => g.name.toLowerCase().includes(localGameFilter.toLowerCase()))
    : s.localGames;
  const filteredPeerGames = peerGameFilter.trim()
    ? s.availableFromPeers.filter(g => g.name.toLowerCase().includes(peerGameFilter.toLowerCase()))
    : s.availableFromPeers;

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
  const xboxHealthy = s.isSkeletonWatching && s.isCacheProxyRunning;
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
            <div className="px-4 pt-3 pb-1 flex-shrink-0">
              <div className="relative">
                <Search className="w-3.5 h-3.5 text-slate-500 absolute left-2.5 top-1/2 -translate-y-1/2 pointer-events-none" />
                <input
                  type="text"
                  value={localGameFilter}
                  onChange={(e) => setLocalGameFilter(e.target.value)}
                  placeholder="Search my games..."
                  className="w-full bg-slate-900/50 border border-slate-700 rounded-lg pl-8 pr-7 py-1.5 text-xs text-white placeholder-slate-500 focus:outline-none focus:border-blue-500 transition-colors"
                />
                {localGameFilter && (
                  <button onClick={() => setLocalGameFilter('')} className="absolute right-1.5 top-1/2 -translate-y-1/2 text-slate-500 hover:text-slate-300" title="Clear">
                    <X className="w-3.5 h-3.5" />
                  </button>
                )}
              </div>
            </div>
            <div key={`local-${filteredLocalGames.length}`} className="flex-1 overflow-auto p-4 pt-2 space-y-3 stagger-children">
              {filteredLocalGames.map((g) => (
                <div
                  key={g.appId}
                  onClick={() => sendCommand('SelectLocalGame', { appId: g.appId })}
                  onContextMenu={(e) => openGameMenu(e, g)}
                  className={`bg-slate-900/50 rounded-lg p-3 border transition-all group cursor-pointer ${
                    s.selectedLocalGame?.appId === g.appId ? 'border-blue-500' : 'border-slate-700/50 hover:border-blue-500/50'
                  } ${g.isHidden ? 'opacity-60' : ''}`}
                >
                  <div className="flex gap-3">
                    {g.coverUrl && (
                      <div className="w-16 h-20 bg-slate-700 rounded overflow-hidden flex-shrink-0 flex items-center justify-center">
                        <img
                          src={g.coverUrl}
                          alt={g.name}
                          className="w-full h-full object-cover"
                          onError={(e) => { e.currentTarget.style.display = 'none'; }}
                        />
                      </div>
                    )}
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-1.5">
                        <PlatformIcon platform={g.platform} />
                        <h4 className="font-semibold text-white text-sm truncate group-hover:text-blue-400 transition-colors">{g.name}</h4>
                      </div>
                      <p className="text-xs text-slate-400 mt-0.5">build {g.buildId}</p>
                      <p className="text-xs text-blue-400 mt-1 font-medium">{g.formattedSize}</p>
                    </div>
                  </div>
                </div>
              ))}
              {s.localGames.length === 0 && <Empty icon={<FileText className="w-12 h-12 text-slate-600 mb-3" />} title="No games found" sub='Click "Scan My Games"' />}
              {s.localGames.length > 0 && filteredLocalGames.length === 0 && (
                <Empty icon={<Search className="w-12 h-12 text-slate-600 mb-3" />} title="No matches" sub={`No games match "${localGameFilter}"`} />
              )}
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

              <div key={`peers-${s.networkPeers.length}`} className="flex-1 overflow-auto space-y-2 stagger-children">
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
                {s.availableFromPeers.length > 0 && (
                  <div className="relative mb-2 flex-shrink-0">
                    <Search className="w-3.5 h-3.5 text-slate-500 absolute left-2.5 top-1/2 -translate-y-1/2 pointer-events-none" />
                    <input
                      type="text"
                      value={peerGameFilter}
                      onChange={(e) => setPeerGameFilter(e.target.value)}
                      placeholder="Search new games..."
                      className="w-full bg-slate-900/50 border border-slate-700 rounded pl-8 pr-7 py-1 text-xs text-white placeholder-slate-500 focus:outline-none focus:border-purple-500 transition-colors"
                    />
                    {peerGameFilter && (
                      <button onClick={() => setPeerGameFilter('')} className="absolute right-1.5 top-1/2 -translate-y-1/2 text-slate-500 hover:text-slate-300" title="Clear">
                        <X className="w-3.5 h-3.5" />
                      </button>
                    )}
                  </div>
                )}
                <div key={`peer-games-${filteredPeerGames.length}`} className="flex-1 overflow-auto space-y-1 stagger-children">
                  {s.availableFromPeers.length === 0 ? (
                    <div className="text-xs text-slate-400 mt-1 italic">No new games found</div>
                  ) : filteredPeerGames.length === 0 ? (
                    <div className="text-xs text-slate-400 mt-1 italic">No matches for "{peerGameFilter}"</div>
                  ) : (
                    filteredPeerGames.map(game => {
                      const selected = s.selectedPeerGame?.appId === game.appId;
                      return (
                        <div
                          key={game.appId}
                          onClick={() => sendCommand('SelectPeerGame', { appId: game.appId })}
                          className={`bg-slate-900/50 rounded p-2 border cursor-pointer ${
                            selected ? 'border-purple-500' : 'border-slate-700/50 hover:border-purple-500/50'
                          }`}
                        >
                          <div className="flex items-center gap-1.5">
                            <PlatformIcon platform={game.platform} />
                            <p className="text-white text-xs font-medium truncate">{game.name}</p>
                          </div>
                          <p className="text-slate-400 text-[10px] mt-0.5">{game.buildId} • {game.formattedSize}</p>
                          {selected && (
                            game.platform === 'Xbox' ? (
                              <button
                                onClick={(e) => {
                                  e.stopPropagation();
                                  const peer = s.networkPeers.find(p => p.games.some(g => g.appId === game.appId));
                                  if (!peer) {
                                    alert(`Could not find peer for game ${game.name} (${game.appId}). Peers: ${s.networkPeers.length}`);
                                    return;
                                  }
                                  sendCommand('StartXboxPeerInstall', {
                                    peerHost: peer.ipAddress,
                                    gameAppId: game.appId,
                                  });
                                }}
                                className="mt-2 w-full py-1.5 bg-green-600 hover:bg-green-700 text-white rounded text-xs font-medium flex items-center justify-center gap-1"
                                title="Stream-install from the peer: the game is rebuilt on the fly from the other PC's installed files (no full package stored on either PC). You'll click Install in the Xbox app."
                              >
                                <Download className="w-3 h-3" /> Download (stream from peer)
                              </button>
                            ) : (
                              <button
                                onClick={(e) => { e.stopPropagation(); sendCommand('DownloadNewGame'); }}
                                className="mt-2 w-full py-1.5 bg-purple-600 hover:bg-purple-700 text-white rounded text-xs font-medium flex items-center justify-center gap-1"
                              >
                                <Download className="w-3 h-3" /> Download
                              </button>
                            )
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
            <div key={`syncs-${s.availableSyncs.length}`} className="flex-1 overflow-auto p-4 space-y-2 stagger-children">
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
            <div key={`inc-${s.incompleteTransfers.length}`} className="flex-1 overflow-auto p-4 space-y-2 stagger-children">
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
                s.downloadQueue.map((q) => {
                  const statusLower = (q.statusText || '').toLowerCase();
                  const canRetry = statusLower.includes('fail') || statusLower.includes('paus');
                  return (
                    <div key={q.gameAppId} className="px-3 py-2 border-b border-slate-800 flex items-center justify-between">
                      <div className="min-w-0 flex-1">
                        <p className="text-xs text-white truncate">{q.gameName}</p>
                        <p className="text-[10px]" style={{ color: q.statusColor }}>{q.statusText}</p>
                      </div>
                      <div className="flex items-center gap-0.5">
                        {canRetry && (
                          <button title="Retry" onClick={() => sendCommand('RetryQueueItem', { appId: q.gameAppId })} className="p-1 hover:bg-slate-700 rounded">
                            <RotateCcw className="w-3 h-3 text-cyan-400" />
                          </button>
                        )}
                        <button title="Move up" onClick={() => sendCommand('MoveQueueItemUp', { appId: q.gameAppId })} className="p-1 hover:bg-slate-700 rounded text-slate-400 text-xs">▲</button>
                        <button title="Move down" onClick={() => sendCommand('MoveQueueItemDown', { appId: q.gameAppId })} className="p-1 hover:bg-slate-700 rounded text-slate-400 text-xs">▼</button>
                        <button title="Remove" onClick={() => sendCommand('RemoveFromQueue', { appId: q.gameAppId })} className="p-1 hover:bg-slate-700 rounded">
                          <X className="w-3 h-3 text-slate-400" />
                        </button>
                      </div>
                    </div>
                  );
                })
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
        <div className="bg-gradient-to-r from-blue-900/60 to-purple-900/60 border-t border-blue-700/50 px-3 sm:px-6 py-2.5 animate-slide-up">
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

      {s.isXboxTransferActive && s.xboxTransfer && (() => {
        const xt = s.xboxTransfer;
        const failed = xt.currentStep === 'Failed';
        const done = xt.currentStep === 'Complete';
        const finished = failed || done;
        const downloading = xt.currentStep === 'DownloadingFromPeer';
        const awaitingResume = xt.currentStep === 'WaitingForResume';
        const paused = xt.isPaused;
        const barColor = failed
          ? 'from-red-900/60 to-red-800/60 border-red-700/50'
          : done
            ? 'from-emerald-900/60 to-green-800/60 border-green-700/50'
            : awaitingResume
              ? 'from-yellow-900/70 to-amber-800/70 border-yellow-500/60'
              : paused
                ? 'from-amber-900/60 to-yellow-900/60 border-amber-700/50'
                : 'from-green-900/60 to-emerald-900/60 border-green-700/50';

        // Build info line like the normal transfer bar
        const infoParts: string[] = [];
        if (xt.networkReceivedMB > 0) {
          const totalMB = xt.sourceBytes > 0 ? (xt.sourceBytes / 1024 / 1024) : 0;
          infoParts.push(totalMB > 0
            ? `${xt.networkReceivedMB.toFixed(1)} / ${totalMB.toFixed(0)} MB`
            : `${xt.networkReceivedMB.toFixed(1)} MB`);
        }
        const showSpeed = xt.networkSpeedMBps > 0 && !paused;
        const speedLabel = showSpeed
          ? (s.showSpeedInMbps
              ? `${(xt.networkSpeedMBps * 8).toFixed(1)} Mbps`
              : `${xt.networkSpeedMBps.toFixed(1)} MB/s`)
          : '';
        if (paused) infoParts.push('PAUSED');
        if (xt.networkEta && !paused) infoParts.push(`ETA ${xt.networkEta}`);
        if (!downloading && xt.overlayProgress > 0 && infoParts.length === 0 && !showSpeed) infoParts.push(`${xt.overlayProgress.toFixed(1)}%`);

        return (
          <div className={`bg-gradient-to-r ${barColor} border-t px-3 sm:px-6 py-2.5 animate-slide-up`}>
            <div className="max-w-7xl mx-auto flex flex-wrap items-center gap-3 sm:gap-4">
              {failed
                ? <AlertCircle className="w-5 h-5 text-red-400 flex-shrink-0" />
                : done
                  ? <Download className="w-5 h-5 text-green-400 flex-shrink-0" />
                  : awaitingResume
                    ? <Play className="w-5 h-5 text-yellow-300 animate-pulse flex-shrink-0" />
                    : paused
                      ? <Pause className="w-5 h-5 text-amber-400 flex-shrink-0" />
                      : <Download className="w-5 h-5 text-green-400 animate-pulse flex-shrink-0" />
              }
              <div className="flex-1 min-w-0">
                <div className="flex items-center justify-between mb-1">
                  <span className="text-sm font-medium text-white truncate">
                    {awaitingResume
                      ? <>Click <strong className="text-yellow-200">RESUME</strong> in the Xbox app now</>
                      : <>{xt.gameName || 'Xbox Transfer'}</>}
                    <span className="ml-2 text-[10px] text-green-400 font-mono">XBOX</span>
                  </span>
                  <span className="text-xs text-slate-300 font-mono ml-2 flex-shrink-0 flex items-center gap-1">
                    {showSpeed && (
                      <>
                        <button
                          onClick={() => sendCommand('ToggleSpeedUnit')}
                          title="Click to toggle Mbps/MB/s"
                          className="hover:text-white hover:underline cursor-pointer"
                        >
                          {speedLabel}
                        </button>
                        {infoParts.length > 0 && <span>\u00b7</span>}
                      </>
                    )}
                    {infoParts.join(' \u00b7 ')}
                  </span>
                </div>
                {!finished && !awaitingResume && (
                  <div className="w-full bg-slate-800 rounded h-2 overflow-hidden">
                    {xt.indeterminate ? (
                      <div className="h-full w-full bg-gradient-to-r from-green-500 to-emerald-500 animate-pulse" />
                    ) : (
                      <div
                        className={`h-full transition-all ${paused ? 'bg-gradient-to-r from-amber-500 to-yellow-500' : 'bg-gradient-to-r from-green-500 to-emerald-500'}`}
                        style={{ width: `${Math.min(xt.overlayProgress, 100)}%` }}
                      />
                    )}
                  </div>
                )}
                <p className={`text-[10px] mt-1 truncate ${failed ? 'text-red-300' : awaitingResume ? 'text-yellow-200' : 'text-slate-400'}`}>
                  {xt.errorMessage || xt.statusMessage}
                </p>
              </div>
              <div className="flex gap-1 flex-shrink-0">
                {finished ? (
                  <button
                    onClick={() => sendCommand('DismissXboxTransfer')}
                    className="px-3 py-1.5 bg-slate-600 hover:bg-slate-500 text-white rounded text-xs font-medium flex items-center gap-1"
                  >
                    <X className="w-3 h-3" /> Dismiss
                  </button>
                ) : (
                  <>
                    {downloading && (
                      paused ? (
                        <button
                          onClick={() => sendCommand('ResumeXboxTransfer')}
                          className="px-3 py-1.5 bg-green-600 hover:bg-green-700 text-white rounded text-xs font-medium flex items-center gap-1"
                        >
                          <Play className="w-3 h-3" /> Resume
                        </button>
                      ) : (
                        <button
                          onClick={() => sendCommand('PauseXboxTransfer')}
                          className="px-3 py-1.5 bg-amber-600 hover:bg-amber-700 text-white rounded text-xs font-medium flex items-center gap-1"
                        >
                          <Pause className="w-3 h-3" /> Pause
                        </button>
                      )
                    )}
                    <button
                      onClick={() => { if (confirm('Stop the Xbox transfer?')) sendCommand('CancelXboxTransfer'); }}
                      className="px-3 py-1.5 bg-red-600 hover:bg-red-700 text-white rounded text-xs font-medium flex items-center gap-1"
                    >
                      <Square className="w-3 h-3" /> Stop
                    </button>
                  </>
                )}
              </div>
            </div>
          </div>
        );
      })()}

      <div className="bg-slate-950 border-t border-slate-800 px-3 sm:px-6 py-2.5 flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-4">
          <button onClick={() => sendCommand('OpenSettings')} className="flex items-center gap-2 px-3 py-1.5 hover:bg-slate-800 rounded transition-colors group">
            <Settings className="w-4 h-4 text-slate-400 group-hover:text-slate-300" />
            <span className="text-sm text-slate-400 group-hover:text-slate-300">Settings</span>
          </button>
          <button onClick={() => setShowDrives(v => !v)} className={`flex items-center gap-2 px-3 py-1.5 hover:bg-slate-800 rounded transition-colors group ${showDrives ? 'bg-slate-800' : ''}`}>
            <HardDrive className={`w-4 h-4 ${showDrives ? 'text-blue-400' : 'text-slate-400 group-hover:text-slate-300'}`} />
            <span className={`text-sm ${showDrives ? 'text-blue-400' : 'text-slate-400 group-hover:text-slate-300'}`}>
              Drives{s.drives.length > 0 ? ` (${s.drives.length})` : ''}
            </span>
          </button>
          {s.isWindows && (
            <button onClick={() => setShowSkeleton(v => !v)} className={`flex items-center gap-2 px-3 py-1.5 hover:bg-slate-800 rounded transition-colors group ${showSkeleton ? 'bg-slate-800' : ''}`}>
              <Boxes className={`w-4 h-4 ${xboxHealthy ? 'text-green-400' : showSkeleton ? 'text-blue-400' : 'text-slate-400 group-hover:text-slate-300'}`} />
              <span className={`text-sm ${xboxHealthy ? 'text-green-400' : showSkeleton ? 'text-blue-400' : 'text-slate-400 group-hover:text-slate-300'}`}>
                Xbox Ready{s.skeletonCaptures.length > 0 ? ` (${s.skeletonCaptures.length})` : ''}
              </span>
            </button>
          )}
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

      {settingsPayload && (
        <SettingsModal payload={settingsPayload} onClose={() => setSettingsPayload(null)} />
      )}

      {showDrives && (
        <div className="fixed inset-0 z-40 bg-black/60 flex items-center justify-center p-4 animate-fade-in" onClick={e => { if (e.target === e.currentTarget) setShowDrives(false); }}>
          <div className="bg-slate-900 border border-slate-700 rounded-xl shadow-2xl w-full max-w-2xl max-h-[85vh] flex flex-col overflow-hidden animate-scale-in">
            <div className="bg-gradient-to-r from-blue-600 to-cyan-600 px-5 py-4 flex items-center justify-between flex-shrink-0">
              <div className="flex items-center gap-3">
                <HardDrive className="w-6 h-6 text-white" />
                <h2 className="text-lg font-bold text-white">External Drives</h2>
              </div>
              <button onClick={() => setShowDrives(false)} className="text-white/80 hover:text-white" title="Close">
                <X className="w-5 h-5" />
              </button>
            </div>
            <DrivesPanel />
          </div>
        </div>
      )}

      {showSkeleton && (
        <div className="fixed inset-0 z-40 bg-black/60 flex items-center justify-center p-4 animate-fade-in" onClick={e => { if (e.target === e.currentTarget) setShowSkeleton(false); }}>
          <div className="bg-slate-900 border border-slate-700 rounded-xl shadow-2xl w-full max-w-2xl max-h-[85vh] flex flex-col overflow-hidden animate-scale-in">
            <div className="bg-gradient-to-r from-emerald-600 to-green-600 px-5 py-4 flex items-center justify-between flex-shrink-0">
              <div className="flex items-center gap-3">
                <Boxes className="w-6 h-6 text-white" />
                <h2 className="text-lg font-bold text-white">Xbox Single-Copy (Advanced)</h2>
              </div>
              <button onClick={() => setShowSkeleton(false)} className="text-white/80 hover:text-white" title="Close">
                <X className="w-5 h-5" />
              </button>
            </div>
            <SkeletonPanel />
          </div>
        </div>
      )}

      {ctxMenu && (
        <div
          className="fixed z-50 bg-slate-900 border border-slate-700 rounded-lg shadow-2xl py-1 min-w-[200px] animate-pop-in origin-top-left"
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
        <div className="absolute bottom-12 right-2 sm:right-6 left-2 sm:left-auto sm:w-[500px] h-80 bg-slate-950 border border-slate-700 rounded-lg shadow-2xl flex flex-col overflow-hidden animate-fade-in-up">
          <div className="flex items-center justify-between px-3 py-2 border-b border-slate-800 bg-slate-900">
            <span className="text-sm font-semibold text-slate-300">Log</span>
            <div className="flex gap-3 items-center">
              <button
                onClick={async () => {
                  const text = s.logMessages.map(m => `${m.formattedTime} ${m.message}`).join('\n');
                  try {
                    await navigator.clipboard.writeText(text);
                  } catch {
                    const ta = document.createElement('textarea');
                    ta.value = text;
                    document.body.appendChild(ta);
                    ta.select();
                    document.execCommand('copy');
                    document.body.removeChild(ta);
                  }
                }}
                className="text-xs text-slate-400 hover:text-slate-200"
              >Copy</button>
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
