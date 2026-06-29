import { useEffect, useMemo, useState } from 'react';
import {
  Search, X, Download, Copy, Share2, WifiOff, HardDrive, Users,
  ArrowLeftRight, Check, RefreshCw, Signal,
} from 'lucide-react';
import { useAppState, type GameInfo, type NetworkPeer, type ExternalLibrary } from '../store';
import { sendCommand } from '../bridge';
import PlatformIcon from './PlatformIcon';

/**
 * Two-pane "Sync" view (prototype, behind the header toggle).
 *
 * Left = My Library. Right = a target you pick: a network Peer or an External Drive.
 * Drag direction = data direction, which keeps the app's pull-only constraint intuitive:
 *   - peer game  → My Library  = download/pull (the only way games move off a peer)
 *   - my game    → Drive       = copy/update the drive's copy
 *   - my game    → Peer        = "share with network" (peers download from you; we just
 *                                 make sure the game is visible) — reframes the impossible push
 * Every row also has a button so drag-and-drop is a shortcut, not the only path.
 */

type Target =
  | { kind: 'peer'; id: string }
  | { kind: 'drive'; id: string }
  | null;

type DragPayload = { appId: string; origin: 'mine' | 'peer' };

/** Drive letter for a library from its root path, e.g. "E:\\Games" -> "E:". */
function driveLetterOf(lib: ExternalLibrary): string | null {
  const m = /^([A-Za-z]):/.exec(lib.rootPath ?? '');
  return m ? `${m[1].toUpperCase()}:` : null;
}

export default function SyncView() {
  const s = useAppState();
  const [leftFilter, setLeftFilter] = useState('');
  const [rightFilter, setRightFilter] = useState('');
  const [target, setTarget] = useState<Target>(null);
  const [dropZone, setDropZone] = useState<'left' | 'right' | null>(null);
  const [toast, setToast] = useState<string | null>(null);

  const peers = s.networkPeers;
  const libs = s.externalLibraries;

  // Default the target to the first peer (or drive) once data arrives.
  useEffect(() => {
    if (target) return;
    if (peers.length > 0) setTarget({ kind: 'peer', id: peers[0].peerId });
    else if (libs.length > 0) setTarget({ kind: 'drive', id: libs[0].id });
  }, [peers, libs, target]);

  const selectedPeer = target?.kind === 'peer' ? peers.find(p => p.peerId === target.id) ?? null : null;
  const selectedLib = target?.kind === 'drive' ? libs.find(l => l.id === target.id) ?? null : null;

  // Keep the backend selection in sync so existing download commands target this peer.
  useEffect(() => {
    if (selectedPeer) sendCommand('SelectPeer', { peerId: selectedPeer.peerId });
  }, [selectedPeer?.peerId]);

  const flash = (msg: string) => { setToast(msg); window.setTimeout(() => setToast(null), 3200); };

  // Cover art for a row that may not carry its own coverUrl (peer games arrive over the
  // network; drive-comparison rows are built separately and never had covers loaded).
  // Fall back to the matching library game's cover, then a deterministic Steam image.
  const resolveCover = (explicit: string | null | undefined, appId: string, platform?: string): string | null => {
    if (explicit) return explicit;
    const local = s.localGames.find(lg => lg.appId === appId);
    if (local?.coverUrl) return local.coverUrl;
    if ((!platform || platform === 'Steam') && /^\d+$/.test(appId))
      return `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/header.jpg`;
    return null;
  };

  // ---- diff state (drive uses the backend's computed cross-location rows) ----
  const myAppIds = useMemo(() => new Set(s.localGames.map(g => g.appId)), [s.localGames]);
  const updateAppIds = useMemo(
    () => new Set(s.availableSyncs.map(sy => sy.remoteGame?.appId).filter(Boolean) as string[]),
    [s.availableSyncs],
  );
  const newAppIds = useMemo(() => new Set(s.availableFromPeers.map(g => g.appId)), [s.availableFromPeers]);

  const myGames = leftFilter.trim()
    ? s.localGames.filter(g => g.name.toLowerCase().includes(leftFilter.toLowerCase()))
    : s.localGames;

  // Peer's library (right pane when a peer is selected).
  const peerGames = useMemo(() => {
    const list = selectedPeer?.games ?? [];
    return rightFilter.trim()
      ? list.filter(g => g.name.toLowerCase().includes(rightFilter.toLowerCase()))
      : list;
  }, [selectedPeer, rightFilter]);

  // Drive contents (right pane when a drive is selected) from cross-location compare.
  const driveGames = useMemo(() => {
    if (!selectedLib) return [];
    const rows = s.crossLocationGames.filter(g => g.library?.id === selectedLib.id && g.externalCopy);
    return rightFilter.trim()
      ? rows.filter(g => g.displayName.toLowerCase().includes(rightFilter.toLowerCase()))
      : rows;
  }, [selectedLib, s.crossLocationGames, rightFilter]);

  type Badge = { label: string; cls: string };
  const badgeCls: Record<string, string> = {
    sync: 'bg-green-900/40 text-green-300 border-green-700/50',
    update: 'bg-amber-900/40 text-amber-300 border-amber-700/50',
    new: 'bg-blue-900/40 text-blue-300 border-blue-700/50',
    only: 'bg-slate-800/60 text-slate-400 border-slate-700/50',
  };
  const peerBadge = (g: GameInfo): Badge => {
    if (updateAppIds.has(g.appId)) return { label: 'Update available', cls: badgeCls.update };
    if (newAppIds.has(g.appId)) return { label: 'New', cls: badgeCls.new };
    if (myAppIds.has(g.appId)) return { label: 'In sync', cls: badgeCls.sync };
    return { label: 'Get', cls: badgeCls.new };
  };
  const myBadge = (g: GameInfo): Badge => {
    const onPeer = selectedPeer?.games.some(x => x.appId === g.appId);
    if (target?.kind === 'peer') {
      if (updateAppIds.has(g.appId)) return { label: 'Older here', cls: badgeCls.update };
      if (onPeer) return { label: 'In sync', cls: badgeCls.sync };
      return { label: 'Only here', cls: badgeCls.only };
    }
    return { label: g.isHidden ? 'Hidden' : 'Shared', cls: badgeCls.only };
  };

  // ---- actions (wired to existing backend commands) ----
  const downloadFromPeer = (g: GameInfo) => {
    if (updateAppIds.has(g.appId)) {
      sendCommand('SelectSyncItem', { appId: g.appId });
      sendCommand('StartSync');
      flash(`Downloading update for ${g.name}…`);
      return;
    }
    sendCommand('SelectPeerGame', { appId: g.appId });
    if (g.platform === 'Xbox') {
      const peer = selectedPeer ?? peers.find(p => p.games.some(x => x.appId === g.appId));
      if (!peer) { flash(`Couldn't find the peer for ${g.name}.`); return; }
      sendCommand('StartXboxPeerInstall', { peerHost: peer.ipAddress, gameAppId: g.appId });
    } else {
      sendCommand('DownloadNewGame');
    }
    flash(`Downloading ${g.name} from ${selectedPeer?.displayName ?? 'peer'}…`);
  };

  const copyToDrive = (g: GameInfo) => {
    if (!selectedLib) return;
    if (g.platform === 'Xbox') sendCommand('CopyXboxGameToDrive', { appId: g.appId, libraryId: selectedLib.id });
    else sendCommand('StartLocalCopy', { appId: g.appId, libraryId: selectedLib.id, direction: 'DeviceToDrive' });
    flash(`Copying ${g.name} → ${selectedLib.displayName}…`);
  };

  const shareWithNetwork = (g: GameInfo) => {
    if (g.isHidden) sendCommand('ToggleGameVisibility', { appId: g.appId });
    flash(`${g.name} is shared — ${selectedPeer?.displayName ?? 'peers'} can download it from you.`);
  };

  // ---- drag and drop ----
  const startDrag = (e: React.DragEvent, appId: string, origin: 'mine' | 'peer') => {
    e.dataTransfer.setData('application/json', JSON.stringify({ appId, origin } as DragPayload));
    e.dataTransfer.effectAllowed = 'copyMove';
  };
  const readDrag = (e: React.DragEvent): DragPayload | null => {
    try { return JSON.parse(e.dataTransfer.getData('application/json')); } catch { return null; }
  };
  const onDropLeft = (e: React.DragEvent) => {
    e.preventDefault(); setDropZone(null);
    const d = readDrag(e);
    if (!d || d.origin !== 'peer') return;
    const g = selectedPeer?.games.find(x => x.appId === d.appId)
      ?? s.availableFromPeers.find(x => x.appId === d.appId);
    if (g) downloadFromPeer(g);
  };
  const onDropRight = (e: React.DragEvent) => {
    e.preventDefault(); setDropZone(null);
    const d = readDrag(e);
    if (!d || d.origin !== 'mine') return;
    const g = s.localGames.find(x => x.appId === d.appId);
    if (!g) return;
    if (target?.kind === 'drive') copyToDrive(g);
    else if (target?.kind === 'peer') shareWithNetwork(g);
  };

  const networkActive = s.isNetworkActive;

  return (
    <div className="flex-1 min-h-0 flex flex-col p-3 sm:p-4">
      <div className="max-w-[110rem] w-full mx-auto flex-1 min-h-0 grid grid-cols-1 lg:grid-cols-[1fr_auto_1fr] gap-3 lg:gap-4">

        {/* LEFT — My Library */}
        <Pane
          title="My Library"
          count={s.localGames.length}
          icon={<HardDrive className="w-4 h-4 text-white" />}
          gradient="from-blue-600 to-blue-700"
          highlight={dropZone === 'left'}
          onDragOver={(e) => { if (e.dataTransfer.types.includes('application/json')) { e.preventDefault(); setDropZone('left'); } }}
          onDragLeave={() => setDropZone(z => (z === 'left' ? null : z))}
          onDrop={onDropLeft}
          search={<SearchBox value={leftFilter} onChange={setLeftFilter} placeholder="Search my games…" accent="blue" />}
        >
          {myGames.map(g => {
            const b = myBadge(g);
            return (
              <Row
                key={g.appId}
                draggable
                onDragStart={(e) => startDrag(e, g.appId, 'mine')}
                cover={g.coverUrl}
                icon={<PlatformIcon platform={g.platform} />}
                name={g.name}
                sub={`${g.formattedSize}${g.buildId ? ` · build ${g.buildId}` : ''}`}
                badge={b}
                action={
                  target?.kind === 'drive'
                    ? <RowBtn onClick={() => copyToDrive(g)} icon={<Copy className="w-3 h-3" />} label="Copy" tone="blue" />
                    : <RowBtn onClick={() => shareWithNetwork(g)} icon={<Share2 className="w-3 h-3" />} label={g.isHidden ? 'Share' : 'Shared'} tone="slate" disabled={!g.isHidden} />
                }
              />
            );
          })}
          {s.localGames.length === 0 && <Empty icon={<HardDrive className="w-10 h-10" />} title="No games" sub='Run "Scan My Games"' />}
        </Pane>

        {/* CENTER — direction hint */}
        <div className="hidden lg:flex flex-col items-center justify-center px-1 text-slate-600">
          <ArrowLeftRight className="w-6 h-6" />
          <span className="text-[0.625rem] text-slate-500 mt-2 text-center leading-tight max-w-[5rem]">
            drag a game across
          </span>
        </div>

        {/* RIGHT — target (peer or drive) */}
        <Pane
          title={selectedPeer ? selectedPeer.displayName : selectedLib ? selectedLib.displayName : 'Select a target'}
          count={selectedPeer ? selectedPeer.games.length : driveGames.length}
          icon={target?.kind === 'drive' ? <HardDrive className="w-4 h-4 text-white" /> : <Users className="w-4 h-4 text-white" />}
          gradient={target?.kind === 'drive' ? 'from-cyan-600 to-cyan-700' : 'from-purple-600 to-purple-700'}
          titleBadge={
            selectedLib && driveLetterOf(selectedLib) && !selectedLib.displayName.trim().toUpperCase().startsWith(driveLetterOf(selectedLib)!)
              ? <span className="text-[0.625rem] font-semibold text-white/90 bg-black/20 border border-white/25 rounded px-1.5 py-px">{driveLetterOf(selectedLib)}</span>
              : selectedPeer
                ? <span className={`w-2 h-2 rounded-full ${selectedPeer.isOnline ? 'bg-green-400' : 'bg-slate-400'}`} />
                : undefined
          }
          highlight={dropZone === 'right'}
          onDragOver={(e) => { if (e.dataTransfer.types.includes('application/json')) { e.preventDefault(); setDropZone('right'); } }}
          onDragLeave={() => setDropZone(z => (z === 'right' ? null : z))}
          onDrop={onDropRight}
          header={
            <TargetPicker
              peers={peers}
              libs={libs}
              target={target}
              onPick={setTarget}
            />
          }
          search={
            (selectedPeer || selectedLib)
              ? <SearchBox value={rightFilter} onChange={setRightFilter} placeholder="Search…" accent={target?.kind === 'drive' ? 'cyan' : 'purple'} />
              : undefined
          }
        >
          {!networkActive && target?.kind === 'peer' && (
            <Empty icon={<WifiOff className="w-10 h-10" />} title="Network is off" sub='Start the network to see peers' />
          )}

          {/* Peer target */}
          {selectedPeer && peerGames.map(g => (
            <Row
              key={g.appId}
              draggable
              onDragStart={(e) => startDrag(e, g.appId, 'peer')}
              cover={resolveCover(g.coverUrl, g.appId, g.platform)}
              icon={<PlatformIcon platform={g.platform} />}
              name={g.name}
              sub={`${g.formattedSize}${g.buildId ? ` · build ${g.buildId}` : ''}`}
              badge={peerBadge(g)}
              action={
                myAppIds.has(g.appId) && !updateAppIds.has(g.appId)
                  ? <span className="text-green-400"><Check className="w-3.5 h-3.5" /></span>
                  : <RowBtn onClick={() => downloadFromPeer(g)} icon={<Download className="w-3 h-3" />} label="Get" tone="green" />
              }
            />
          ))}
          {selectedPeer && peerGames.length === 0 && (
            <Empty icon={<Signal className="w-10 h-10" />} title="No games shared" sub="This peer isn't sharing games yet" />
          )}

          {/* Drive target */}
          {selectedLib && s.crossLocationGames.length === 0 && (
            <div className="flex flex-col items-center justify-center py-10 text-center gap-3">
              <HardDrive className="w-10 h-10 text-slate-600" />
              <p className="text-slate-500 text-sm">Compare this drive to see its games</p>
              <button onClick={() => sendCommand('CompareGameLocations')} className="px-3 py-1.5 bg-cyan-600 hover:bg-cyan-700 text-white rounded text-xs font-medium flex items-center gap-1.5">
                <RefreshCw className="w-3.5 h-3.5" /> Compare Locations
              </button>
              <p className="text-slate-600 text-[0.625rem] max-w-[16rem]">…or just drag a game from the left onto this drive to copy it.</p>
            </div>
          )}
          {selectedLib && driveGames.map(g => (
            <Row
              key={g.appId}
              cover={resolveCover(g.deviceCopy?.coverUrl ?? g.externalCopy?.coverUrl, g.appId, g.deviceCopy?.platform ?? g.externalCopy?.platform)}
              icon={<PlatformIcon platform={g.deviceCopy?.platform ?? g.externalCopy?.platform} />}
              name={g.displayName}
              sub={g.statusText}
              badge={{ label: g.direction === 'InSync' ? 'In sync' : 'On drive', cls: g.direction === 'InSync' ? badgeCls.sync : badgeCls.only }}
              action={null}
            />
          ))}
          {selectedLib && s.crossLocationGames.length > 0 && driveGames.length === 0 && (
            <Empty icon={<HardDrive className="w-10 h-10" />} title="No games on this drive" sub="Pick another drive, or drag a game from the left to copy it here" />
          )}
        </Pane>
      </div>

      {toast && (
        <div className="fixed bottom-16 left-1/2 -translate-x-1/2 z-50 bg-slate-800 border border-slate-600 text-slate-100 text-sm rounded-lg px-4 py-2 shadow-2xl animate-fade-in-up">
          {toast}
        </div>
      )}
    </div>
  );
}

// ---- small presentational helpers (match the app's design system) ----

function Pane(props: {
  title: string; count: number; icon: React.ReactNode; gradient: string; titleBadge?: React.ReactNode;
  highlight?: boolean; header?: React.ReactNode; search?: React.ReactNode; children: React.ReactNode;
  onDragOver?: (e: React.DragEvent) => void; onDragLeave?: () => void; onDrop?: (e: React.DragEvent) => void;
}) {
  return (
    <div
      onDragOver={props.onDragOver}
      onDragLeave={props.onDragLeave}
      onDrop={props.onDrop}
      className={`min-h-0 flex flex-col rounded-xl border overflow-hidden transition-colors ${
        props.highlight ? 'border-blue-400 bg-blue-500/5 ring-2 ring-blue-400/40' : 'border-slate-700/50 bg-slate-800/40'
      }`}
    >
      <div className={`bg-gradient-to-r ${props.gradient} px-4 py-2.5 flex items-center justify-between gap-2 flex-shrink-0`}>
        <div className="flex items-center gap-2 min-w-0">
          <div className="w-7 h-7 bg-white/20 rounded-lg flex items-center justify-center flex-shrink-0">{props.icon}</div>
          <h3 className="font-semibold text-white truncate">{props.title}</h3>
          {props.titleBadge}
          <span className="text-xs text-white/70">({props.count})</span>
        </div>
        {props.header}
      </div>
      {props.search && <div className="px-3 pt-2.5 flex-shrink-0">{props.search}</div>}
      <div className="flex-1 min-h-0 overflow-auto p-3 space-y-2 stagger-children">{props.children}</div>
    </div>
  );
}

function Row(props: {
  cover?: string | null; icon: React.ReactNode; name: string; sub: string;
  badge: { label: string; cls: string }; action: React.ReactNode;
  draggable?: boolean; onDragStart?: (e: React.DragEvent) => void;
}) {
  return (
    <div
      draggable={props.draggable}
      onDragStart={props.onDragStart}
      className={`bg-slate-900/50 rounded-lg p-2.5 border border-slate-700/50 flex items-center gap-2.5 ${
        props.draggable ? 'cursor-grab active:cursor-grabbing hover:border-slate-500' : ''
      }`}
    >
      <div className="w-14 h-20 rounded-md bg-slate-800 overflow-hidden flex items-center justify-center flex-shrink-0">
        {props.cover
          ? <img src={props.cover} alt="" className="w-full h-full object-cover" onError={(e) => { e.currentTarget.style.display = 'none'; }} />
          : <span className="opacity-40">{props.icon}</span>}
      </div>
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-1.5">
          {props.icon}
          <p className="text-sm text-white truncate">{props.name}</p>
        </div>
        <p className="text-[0.625rem] text-slate-400 truncate mt-0.5">{props.sub}</p>
      </div>
      <span className={`text-[0.625rem] px-1.5 py-0.5 rounded border whitespace-nowrap ${props.badge.cls}`}>{props.badge.label}</span>
      <div className="flex-shrink-0">{props.action}</div>
    </div>
  );
}

function RowBtn(props: { onClick: () => void; icon: React.ReactNode; label: string; tone: 'green' | 'blue' | 'slate'; disabled?: boolean }) {
  const tones: Record<string, string> = {
    green: 'bg-green-600 hover:bg-green-700',
    blue: 'bg-blue-600 hover:bg-blue-700',
    slate: 'bg-slate-700 hover:bg-slate-600',
  };
  return (
    <button
      onClick={props.onClick}
      disabled={props.disabled}
      className={`px-2 py-1 ${tones[props.tone]} disabled:opacity-40 disabled:cursor-default text-white rounded text-[0.625rem] font-medium flex items-center gap-1`}
    >
      {props.icon}{props.label}
    </button>
  );
}

function SearchBox(props: { value: string; onChange: (v: string) => void; placeholder: string; accent: 'blue' | 'purple' | 'cyan' }) {
  const focus = { blue: 'focus:border-blue-500', purple: 'focus:border-purple-500', cyan: 'focus:border-cyan-500' }[props.accent];
  return (
    <div className="relative">
      <Search className="w-3.5 h-3.5 text-slate-500 absolute left-2.5 top-1/2 -translate-y-1/2 pointer-events-none" />
      <input
        value={props.value}
        onChange={e => props.onChange(e.target.value)}
        placeholder={props.placeholder}
        className={`w-full bg-slate-900/50 border border-slate-700 rounded-lg pl-8 pr-7 py-1.5 text-xs text-white placeholder-slate-500 focus:outline-none transition-colors ${focus}`}
      />
      {props.value && (
        <button onClick={() => props.onChange('')} className="absolute right-1.5 top-1/2 -translate-y-1/2 text-slate-500 hover:text-slate-300">
          <X className="w-3.5 h-3.5" />
        </button>
      )}
    </div>
  );
}

function TargetPicker(props: {
  peers: NetworkPeer[];
  libs: ExternalLibrary[];
  target: Target; onPick: (t: Target) => void;
}) {
  const value = props.target ? `${props.target.kind}:${props.target.id}` : '';
  return (
    <select
      value={value}
      onChange={(e) => {
        const [kind, id] = e.target.value.split(/:(.+)/);
        if (kind === 'peer' || kind === 'drive') props.onPick({ kind, id } as Target);
      }}
      className="bg-white/15 hover:bg-white/25 text-white text-xs rounded px-2 py-1 max-w-[12rem] focus:outline-none cursor-pointer"
      title="Choose a peer or drive to sync with"
    >
      <optgroup label="Network peers" className="text-slate-900">
        {props.peers.length > 0
          ? props.peers.map(p => (
              <option key={p.peerId} value={`peer:${p.peerId}`} className="text-slate-900">
                {p.isOnline ? '● ' : '○ '}{p.displayName} ({p.games?.length ?? 0})
              </option>
            ))
          : <option value="" disabled className="text-slate-500">No peers online — Scan for Peers</option>}
      </optgroup>
      <optgroup label="External drives" className="text-slate-900">
        {props.libs.length > 0
          ? props.libs.map(l => (
              <option key={l.id} value={`drive:${l.id}`} className="text-slate-900">
                {driveLetterOf(l) && !l.displayName.trim().toUpperCase().startsWith(driveLetterOf(l)!) ? `${driveLetterOf(l)} ` : ''}{l.displayName}
              </option>
            ))
          : <option value="" disabled className="text-slate-500">No drives added</option>}
      </optgroup>
    </select>
  );
}

function Empty(props: { icon: React.ReactNode; title: string; sub: string }) {
  return (
    <div className="flex flex-col items-center justify-center py-10 text-center text-slate-600">
      <div className="mb-2">{props.icon}</div>
      <p className="text-slate-500 text-sm">{props.title}</p>
      <p className="text-slate-600 text-xs mt-1">{props.sub}</p>
    </div>
  );
}
