import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  Search, X, Download, Copy, Share2, Wifi, WifiOff, HardDrive, Users, Monitor,
  ChevronDown, ArrowLeftRight, ArrowDownToLine, Ban, Check, RefreshCw, Signal,
  FolderOpen, Eye, EyeOff,
} from 'lucide-react';
import { useAppState, type GameInfo, type NetworkPeer, type ExternalLibrary, type CrossLocationGame } from '../store';
import { sendCommand } from '../bridge';
import PlatformIcon from './PlatformIcon';

/**
 * Two-pane "Sync" view (the Figma v11 "dual-pane Commander" layout).
 *
 * Left = My PC (Local). Right = a Source you pick: a network Peer or an External Drive.
 * Drag direction = data direction, which keeps the app's pull-only constraint intuitive:
 *   - peer game  → My PC   = download/pull (the only way games move off a peer)
 *   - drive game → My PC   = copy/restore from the drive (DriveToDevice; Xbox via receive-from-drive)
 *   - my game    → Drive   = copy/update the drive's copy
 *   - my game    → Peer    = "share with network" — only meaningful for a HIDDEN game
 *                            (unhide so peers can pull it). An already-shared game can't be
 *                            pushed, so the drop shows the red "Cannot push to Network Peer".
 * Every row also has a button so drag-and-drop is a shortcut, not the only path.
 */

type Target =
  | { kind: 'peer'; id: string }
  | { kind: 'drive'; id: string }
  | null;

type DragPayload = { appId: string; origin: 'mine' | 'peer' | 'drive'; isHidden: boolean };

/** Drive letter for a library from its root path, e.g. "E:\\Games" -> "E:". */
function driveLetterOf(lib: ExternalLibrary): string | null {
  const m = /^([A-Za-z]):/.exec(lib.rootPath ?? '');
  return m ? `${m[1].toUpperCase()}:` : null;
}

/** Show the drive letter prefix only when the display name doesn't already start with it. */
function driveLabel(lib: ExternalLibrary): string {
  const letter = driveLetterOf(lib);
  const prefix = letter && !lib.displayName.trim().toUpperCase().startsWith(letter) ? `${letter} ` : '';
  return `${prefix}${lib.displayName}`;
}

export default function SyncView() {
  const s = useAppState();
  const [leftFilter, setLeftFilter] = useState('');
  const [rightFilter, setRightFilter] = useState('');
  // Store (platform) + sync-state filters per pane (matches the Figma v11 filter bar).
  const [leftStore, setLeftStore] = useState('all');
  const [leftState, setLeftState] = useState('all');
  const [rightStore, setRightStore] = useState('all');
  const [rightState, setRightState] = useState('all');
  const [target, setTarget] = useState<Target>(null);
  const [dropZone, setDropZone] = useState<'left' | 'right' | null>(null);
  const [dragging, setDragging] = useState<{ origin: 'mine' | 'peer' | 'drive'; isHidden: boolean } | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  // Right-click context menu for a local game (hide/show from peers, open folder).
  const [ctxMenu, setCtxMenu] = useState<{ x: number; y: number; game: GameInfo } | null>(null);

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

  // Always clear the drag state when a drag ends anywhere, so overlays don't get stuck.
  useEffect(() => {
    const clear = () => { setDragging(null); setDropZone(null); };
    window.addEventListener('dragend', clear);
    window.addEventListener('drop', clear);
    return () => { window.removeEventListener('dragend', clear); window.removeEventListener('drop', clear); };
  }, []);

  const flash = (msg: string) => { setToast(msg); window.setTimeout(() => setToast(null), 3200); };

  // Open the right-click menu for a local game at the cursor.
  const openCtx = (e: React.MouseEvent, game: GameInfo) => {
    e.preventDefault();
    e.stopPropagation();
    setCtxMenu({ x: e.clientX, y: e.clientY, game });
  };
  // Dismiss the context menu on any click, scroll, resize, or Escape.
  useEffect(() => {
    if (!ctxMenu) return;
    const close = () => setCtxMenu(null);
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setCtxMenu(null); };
    window.addEventListener('click', close);
    window.addEventListener('resize', close);
    window.addEventListener('scroll', close, true);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('click', close);
      window.removeEventListener('resize', close);
      window.removeEventListener('scroll', close, true);
      window.removeEventListener('keydown', onKey);
    };
  }, [ctxMenu]);

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

  // ---- normalized sync state per row (drives the "sync state" filter) ----
  // 'in_sync' = same game on both sides · 'update' = the other side is newer · 'unsynced' = only one side has it.
  type SyncState = 'in_sync' | 'update' | 'unsynced';
  const onSource = (appId: string): boolean =>
    target?.kind === 'peer'
      ? !!selectedPeer?.games.some(x => x.appId === appId)
      : target?.kind === 'drive'
        ? s.crossLocationGames.some(x => x.appId === appId && x.library?.id === selectedLib?.id && x.externalCopy)
        : false;
  const myStateOf = (g: GameInfo): SyncState =>
    updateAppIds.has(g.appId) ? 'update' : onSource(g.appId) ? 'in_sync' : 'unsynced';
  const peerStateOf = (g: GameInfo): SyncState =>
    updateAppIds.has(g.appId) ? 'update' : myAppIds.has(g.appId) ? 'in_sync' : 'unsynced';
  const driveStateOf = (g: CrossLocationGame): SyncState =>
    g.direction === 'InSync' ? 'in_sync' : g.direction === 'DriveToDevice' ? 'update' : 'unsynced';

  const matches = (name: string, q: string) => !q.trim() || name.toLowerCase().includes(q.toLowerCase());

  const myGames = s.localGames.filter(g =>
    (leftStore === 'all' || g.platform === leftStore) &&
    (leftState === 'all' || myStateOf(g) === leftState) &&
    matches(g.name, leftFilter));

  // Peer's library (right pane when a peer is selected).
  const peerGames = useMemo(() => {
    const list = selectedPeer?.games ?? [];
    return list.filter(g =>
      (rightStore === 'all' || g.platform === rightStore) &&
      (rightState === 'all' || peerStateOf(g) === rightState) &&
      matches(g.name, rightFilter));
  }, [selectedPeer, rightFilter, rightStore, rightState, updateAppIds, myAppIds]);

  // Drive contents (right pane when a drive is selected) from cross-location compare.
  const driveGames = useMemo(() => {
    if (!selectedLib) return [];
    return s.crossLocationGames.filter(g => {
      if (g.library?.id !== selectedLib.id || !g.externalCopy) return false;
      const platform = g.deviceCopy?.platform ?? g.externalCopy?.platform;
      return (rightStore === 'all' || platform === rightStore) &&
        (rightState === 'all' || driveStateOf(g) === rightState) &&
        matches(g.displayName, rightFilter);
    });
  }, [selectedLib, s.crossLocationGames, rightFilter, rightStore, rightState]);

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

  // Copy a game from the selected drive onto this PC. Regular games are a plain DriveToDevice
  // file copy; Xbox (MSIXVC) games go through the folder-picker "receive from drive" flow.
  const copyDriveToPc = (g: CrossLocationGame) => {
    if (!selectedLib) return;
    const platform = g.externalCopy?.platform ?? g.deviceCopy?.platform;
    if (platform === 'Xbox') {
      sendCommand('ReceiveXboxFromDrive');
      flash(`Receiving ${g.displayName} from drive — pick the copied folder…`);
      return;
    }
    sendCommand('StartLocalCopy', { appId: g.appId, libraryId: selectedLib.id, direction: 'DriveToDevice' });
    flash(`Copying ${g.displayName} → My PC…`);
  };

  // Can this drive row be pulled onto the PC? (Only-on-drive, newer-on-drive, or unknown version.)
  const driveCanPull = (g: CrossLocationGame) =>
    g.direction === 'OnlyOnDrive' || g.direction === 'DriveToDevice' || g.direction === 'UnknownVersion';

  const driveBadge = (g: CrossLocationGame): Badge => {
    switch (g.direction) {
      case 'InSync': return { label: 'In sync', cls: badgeCls.sync };
      case 'DriveToDevice': return { label: 'Update PC', cls: badgeCls.update };
      case 'OnlyOnDrive': return { label: 'Not on PC', cls: badgeCls.new };
      case 'DeviceToDrive': return { label: 'Newer on PC', cls: badgeCls.only };
      default: return { label: 'On drive', cls: badgeCls.only };
    }
  };

  // ---- drag and drop ----
  const startDrag = (e: React.DragEvent, appId: string, origin: 'mine' | 'peer' | 'drive', isHidden: boolean) => {
    e.dataTransfer.setData('application/json', JSON.stringify({ appId, origin, isHidden } as DragPayload));
    e.dataTransfer.effectAllowed = 'copyMove';
    setDragging({ origin, isHidden });
  };
  const readDrag = (e: React.DragEvent): DragPayload | null => {
    try { return JSON.parse(e.dataTransfer.getData('application/json')); } catch { return null; }
  };
  const onDropLeft = (e: React.DragEvent) => {
    e.preventDefault(); setDropZone(null);
    const d = readDrag(e);
    if (!d) return;
    if (d.origin === 'peer') {
      const g = selectedPeer?.games.find(x => x.appId === d.appId)
        ?? s.availableFromPeers.find(x => x.appId === d.appId);
      if (g) downloadFromPeer(g);
    } else if (d.origin === 'drive') {
      const g = driveGames.find(x => x.appId === d.appId)
        ?? s.crossLocationGames.find(x => x.appId === d.appId && x.externalCopy);
      if (g) copyDriveToPc(g);
    }
  };
  const onDropRight = (e: React.DragEvent) => {
    e.preventDefault(); setDropZone(null);
    const d = readDrag(e);
    if (!d || d.origin !== 'mine') return;
    const g = s.localGames.find(x => x.appId === d.appId);
    if (!g) return;
    if (target?.kind === 'drive') copyToDrive(g);
    else if (target?.kind === 'peer') {
      if (g.isHidden) shareWithNetwork(g);
      else flash(`${g.name} is already shared — peers pull it from you (can't push to a peer).`);
    }
  };

  const networkActive = s.isNetworkActive;

  // ---- drag overlay hints (validity-coloured, shown while hovering a pane) ----
  // A drag from the right pane (a peer game or a drive game) can be dropped on the left to pull it onto the PC.
  const canDropOnPc = dragging?.origin === 'peer' || dragging?.origin === 'drive';
  const sourceName = selectedPeer?.displayName ?? selectedLib?.displayName ?? 'the source';
  const leftOverlay = dropZone === 'left' && canDropOnPc
    ? <DragOverlay tone="green" icon={<ArrowDownToLine className="w-7 h-7" />}
        title={dragging?.origin === 'drive' ? 'Drop here to Copy to PC' : 'Drop here to Download'}
        sub={`Copied from ${sourceName} to your PC`} />
    : null;

  // Pane border/background tint while a valid (or invalid) drag hovers — colour matches
  // the action, exactly like Figma v11 (green = pull to local, purple = copy to drive,
  // red = can't push to a peer).
  const leftTone: 'green' | 'purple' | 'red' | null =
    dropZone === 'left' && canDropOnPc ? 'green' : null;
  let rightTone: 'green' | 'purple' | 'red' | null = null;
  if (dropZone === 'right' && dragging?.origin === 'mine') {
    rightTone = target?.kind === 'drive' ? 'purple' : (dragging.isHidden ? 'green' : 'red');
  }

  let rightOverlay: React.ReactNode = null;
  if (dropZone === 'right' && dragging?.origin === 'mine') {
    if (target?.kind === 'drive') {
      rightOverlay = <DragOverlay tone="purple" icon={<HardDrive className="w-7 h-7" />} title="Drop to Copy to Drive"
        sub={`Copied to ${selectedLib?.displayName ?? 'this drive'}`} />;
    } else if (target?.kind === 'peer') {
      rightOverlay = dragging.isHidden
        ? <DragOverlay tone="green" icon={<Share2 className="w-7 h-7" />} title="Share with network"
            sub="Unhide so peers can download it from you" />
        : <DragOverlay tone="red" icon={<Ban className="w-7 h-7" />} title="Cannot push to Network Peer"
            sub="It's already shared — peers pull from you. You can't force a transfer onto another PC." />;
    }
  }

  const sourceLabel = selectedPeer ? selectedPeer.displayName : selectedLib ? driveLabel(selectedLib) : 'Select a source';
  const sourceSub = selectedPeer ? selectedPeer.ipAddress : selectedLib ? selectedLib.rootPath : '';

  return (
    <div className="flex-1 min-h-0 flex flex-col p-3 sm:p-4">
      <div className="max-w-[110rem] w-full mx-auto flex-1 min-h-0 grid grid-cols-1 lg:grid-cols-[1fr_auto_1fr] gap-3 lg:gap-4">

        {/* LEFT — My PC (Local) */}
        <Pane
          icon={<Monitor className="w-4 h-4 text-white" />}
          gradient="from-blue-600/90 to-blue-800/90"
          title="My PC (Local)"
          titleBadge={
            <span className="text-[0.625rem] font-medium text-white/90 bg-black/20 border border-white/20 rounded px-1.5 py-px whitespace-nowrap">
              IP: {s.localIpAddress || '—'}
            </span>
          }
          count={myGames.length}
          highlightTone={leftTone}
          onDragOver={(e) => { if (e.dataTransfer.types.includes('application/json')) { e.preventDefault(); setDropZone('left'); } }}
          onDragLeave={() => setDropZone(z => (z === 'left' ? null : z))}
          onDrop={onDropLeft}
          header={
            <button
              onClick={() => sendCommand('ScanLocalGames')}
              disabled={s.isScanning}
              className="px-2.5 py-1 bg-black/20 hover:bg-black/30 disabled:opacity-60 rounded text-xs font-medium text-white flex items-center gap-1.5 flex-shrink-0"
            >
              <RefreshCw className={`w-3 h-3 ${s.isScanning ? 'animate-spin' : ''}`} /> Scan Local
            </button>
          }
          search={
            <FilterBar
              search={leftFilter} onSearch={setLeftFilter} placeholder="Search my games…" accent="blue" side="local"
              store={leftStore} onStore={setLeftStore}
              state={leftState} onState={setLeftState}
            />
          }
          overlay={leftOverlay}
        >
          {myGames.map(g => (
            <Row
              key={g.appId}
              draggable
              onDragStart={(e) => startDrag(e, g.appId, 'mine', !!g.isHidden)}
              onContextMenu={(e) => openCtx(e, g)}
              cover={g.coverUrl}
              icon={<PlatformIcon platform={g.platform} />}
              name={g.name}
              sub={`${g.formattedSize}${g.buildId ? ` · build ${g.buildId}` : ''}`}
              badge={myBadge(g)}
              action={
                target?.kind === 'drive'
                  ? <RowBtn onClick={() => copyToDrive(g)} icon={<Copy className="w-3 h-3" />} label="Copy" tone="blue" />
                  : <RowBtn onClick={() => shareWithNetwork(g)} icon={<Share2 className="w-3 h-3" />} label={g.isHidden ? 'Share' : 'Shared'} tone="slate" disabled={!g.isHidden} />
              }
            />
          ))}
          {s.localGames.length === 0 && <Empty icon={<HardDrive className="w-10 h-10" />} title="No games" sub='Click "Scan Local"' />}
        </Pane>

        {/* CENTER — direction indicator */}
        <div className="hidden lg:flex flex-col items-center justify-center px-1">
          <div className="w-px h-16 bg-gradient-to-b from-transparent via-slate-600 to-transparent" />
          <div className="my-3 w-9 h-9 rounded-full bg-slate-800 border border-slate-600 flex items-center justify-center text-slate-400 shadow-lg">
            <ArrowLeftRight className="w-4 h-4" />
          </div>
          <div className="w-px h-16 bg-gradient-to-t from-transparent via-slate-600 to-transparent" />
        </div>

        {/* RIGHT — Source (peer or drive) */}
        <Pane
          icon={target?.kind === 'drive' ? <HardDrive className="w-4 h-4 text-white" /> : <Users className="w-4 h-4 text-white" />}
          gradient={target?.kind === 'drive' ? 'from-cyan-700/90 to-blue-800/90' : 'from-purple-700/90 to-purple-900/90'}
          titleControl={
            <SourceDropdown
              peers={peers}
              libs={libs}
              target={target}
              onPick={setTarget}
              label={sourceLabel}
              sub={sourceSub}
            />
          }
          highlightTone={rightTone}
          onDragOver={(e) => { if (e.dataTransfer.types.includes('application/json')) { e.preventDefault(); setDropZone('right'); } }}
          onDragLeave={() => setDropZone(z => (z === 'right' ? null : z))}
          onDrop={onDropRight}
          header={
            <button
              onClick={() => sendCommand(networkActive ? 'StopNetwork' : 'StartNetwork')}
              className={`px-2.5 py-1 rounded text-xs font-medium text-white flex items-center gap-1.5 flex-shrink-0 ${
                networkActive ? 'bg-red-500/80 hover:bg-red-500' : 'bg-green-500/80 hover:bg-green-500'
              }`}
            >
              {networkActive ? <WifiOff className="w-3 h-3" /> : <Wifi className="w-3 h-3" />}
              {networkActive ? 'Disconnect' : 'Connect LAN'}
            </button>
          }
          search={
            (selectedPeer || selectedLib)
              ? <FilterBar
                  search={rightFilter} onSearch={setRightFilter} placeholder="Search…" accent={target?.kind === 'drive' ? 'cyan' : 'purple'} side="source"
                  store={rightStore} onStore={setRightStore}
                  state={rightState} onState={setRightState}
                />
              : undefined
          }
          overlay={rightOverlay}
        >
          {/* Peer target, network off → connect prompt */}
          {target?.kind === 'peer' && !networkActive && (
            <div className="flex flex-col items-center justify-center text-center py-12 px-6">
              <div className="w-16 h-16 bg-slate-800 rounded-full flex items-center justify-center mb-4 border border-slate-700">
                <WifiOff className="w-8 h-8 text-slate-500" />
              </div>
              <h3 className="text-base font-semibold text-slate-300 mb-1.5">Network is Offline</h3>
              <p className="text-sm text-slate-400 mb-5 max-w-xs">
                Connect to the LAN to see games available from {selectedPeer?.displayName ?? 'this peer'} and other peers.
              </p>
              <button onClick={() => sendCommand('StartNetwork')} className="px-5 py-2 bg-green-600 hover:bg-green-500 text-white rounded-lg font-medium text-sm shadow-lg shadow-green-900/20 flex items-center gap-2">
                <Wifi className="w-4 h-4" /> Start Network Discovery
              </button>
            </div>
          )}

          {/* Peer target, network on */}
          {target?.kind === 'peer' && networkActive && selectedPeer && peerGames.map(g => (
            <Row
              key={g.appId}
              draggable
              onDragStart={(e) => startDrag(e, g.appId, 'peer', false)}
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
          {target?.kind === 'peer' && networkActive && selectedPeer && peerGames.length === 0 && (
            <Empty icon={<Signal className="w-10 h-10" />} title="No games shared" sub="This peer isn't sharing games yet" />
          )}

          {/* Drive target */}
          {target?.kind === 'drive' && selectedLib && s.crossLocationGames.length === 0 && (
            <div className="flex flex-col items-center justify-center py-10 text-center gap-3">
              <HardDrive className="w-10 h-10 text-slate-600" />
              <p className="text-slate-500 text-sm">Compare this drive to see its games</p>
              <button onClick={() => sendCommand('CompareGameLocations')} className="px-3 py-1.5 bg-cyan-600 hover:bg-cyan-700 text-white rounded text-xs font-medium flex items-center gap-1.5">
                <RefreshCw className="w-3.5 h-3.5" /> Compare Locations
              </button>
              <p className="text-slate-600 text-[0.625rem] max-w-[16rem]">…or just drag a game from the left onto this drive to copy it.</p>
            </div>
          )}
          {target?.kind === 'drive' && selectedLib && driveGames.map(g => {
            const platform = g.deviceCopy?.platform ?? g.externalCopy?.platform;
            const canPull = driveCanPull(g);
            return (
              <Row
                key={g.appId}
                draggable={canPull}
                onDragStart={canPull ? (e) => startDrag(e, g.appId, 'drive', false) : undefined}
                cover={resolveCover(g.deviceCopy?.coverUrl ?? g.externalCopy?.coverUrl, g.appId, platform)}
                icon={<PlatformIcon platform={platform} />}
                name={g.displayName}
                sub={g.statusText}
                badge={driveBadge(g)}
                action={
                  canPull
                    ? <RowBtn onClick={() => copyDriveToPc(g)} icon={<Download className="w-3 h-3" />} label={platform === 'Xbox' ? 'Receive' : 'Get'} tone="green" />
                    : g.direction === 'InSync'
                      ? <span className="text-green-400"><Check className="w-3.5 h-3.5" /></span>
                      : null
                }
              />
            );
          })}
          {target?.kind === 'drive' && selectedLib && s.crossLocationGames.length > 0 && driveGames.length === 0 && (
            <Empty icon={<HardDrive className="w-10 h-10" />} title="No games on this drive" sub="Pick another source, or drag a game from the left to copy it here" />
          )}

          {/* No source chosen at all */}
          {!target && (
            <Empty icon={<Users className="w-10 h-10" />} title="Select a source" sub="Pick a network peer or an external drive above" />
          )}
        </Pane>
      </div>

      {toast && (
        <div className="fixed bottom-16 left-1/2 -translate-x-1/2 z-50 bg-slate-800 border border-slate-600 text-slate-100 text-sm rounded-lg px-4 py-2 shadow-2xl animate-fade-in-up">
          {toast}
        </div>
      )}

      {ctxMenu && createPortal(
        <div
          className="fixed z-[71] bg-slate-900 border border-slate-700 rounded-lg shadow-2xl py-1 min-w-[12rem] animate-pop-in origin-top-left"
          style={{
            left: Math.min(ctxMenu.x, window.innerWidth - 224),
            top: Math.min(ctxMenu.y, window.innerHeight - 110),
          }}
          onClick={(e) => e.stopPropagation()}
          onContextMenu={(e) => e.preventDefault()}
        >
          <button
            onClick={() => { sendCommand('OpenGameFolder', { appId: ctxMenu.game.appId, installPath: ctxMenu.game.installPath }); setCtxMenu(null); }}
            className="w-full text-left px-3 py-2 text-sm text-slate-200 hover:bg-slate-800 flex items-center gap-2"
          >
            <FolderOpen className="w-4 h-4 text-blue-400" /> Open game folder
          </button>
          <button
            onClick={() => { sendCommand('ToggleGameVisibility', { appId: ctxMenu.game.appId }); flash(ctxMenu.game.isHidden ? `${ctxMenu.game.name} is now visible to peers.` : `${ctxMenu.game.name} is now hidden from peers.`); setCtxMenu(null); }}
            className="w-full text-left px-3 py-2 text-sm text-slate-200 hover:bg-slate-800 flex items-center gap-2"
          >
            {ctxMenu.game.isHidden
              ? <><Eye className="w-4 h-4 text-green-400" /> Show to peers</>
              : <><EyeOff className="w-4 h-4 text-amber-400" /> Hide from peers</>}
          </button>
        </div>,
        document.body,
      )}
    </div>
  );
}

// ---- small presentational helpers (match the app's design system) ----

function Pane(props: {
  title?: string; count?: number; icon: React.ReactNode; gradient: string; titleBadge?: React.ReactNode;
  titleControl?: React.ReactNode; highlightTone?: 'green' | 'purple' | 'red' | null; header?: React.ReactNode;
  search?: React.ReactNode; overlay?: React.ReactNode; children: React.ReactNode;
  onDragOver?: (e: React.DragEvent) => void; onDragLeave?: () => void; onDrop?: (e: React.DragEvent) => void;
}) {
  const toneCls: Record<string, string> = {
    green: 'border-green-500 bg-green-500/5',
    purple: 'border-purple-500 bg-purple-500/5',
    red: 'border-red-500/60 bg-red-500/5',
  };
  const borderCls = props.highlightTone ? toneCls[props.highlightTone] : 'border-slate-700/50 bg-slate-800/40';
  return (
    <div
      onDragOver={props.onDragOver}
      onDragLeave={props.onDragLeave}
      onDrop={props.onDrop}
      className={`min-h-0 flex flex-col rounded-xl border-2 overflow-hidden transition-colors ${borderCls}`}
    >
      <div className={`bg-gradient-to-r ${props.gradient} px-4 py-2.5 flex items-center justify-between gap-2 flex-shrink-0`}>
        <div className="flex items-center gap-2 min-w-0">
          <div className="w-7 h-7 bg-white/20 rounded-lg flex items-center justify-center flex-shrink-0">{props.icon}</div>
          {props.titleControl ? props.titleControl : (
            <>
              <h3 className="font-semibold text-white truncate">{props.title}</h3>
              {props.titleBadge}
              {typeof props.count === 'number' && <span className="text-xs text-white/70 whitespace-nowrap">({props.count})</span>}
            </>
          )}
        </div>
        {props.header}
      </div>
      {props.search && <div className="px-3 pt-2.5 flex-shrink-0">{props.search}</div>}
      <div className="relative flex-1 min-h-0">
        <div className="absolute inset-0 overflow-auto p-3 space-y-2 stagger-children">{props.children}</div>
        {props.overlay}
      </div>
    </div>
  );
}

function DragOverlay(props: { tone: 'green' | 'purple' | 'red'; icon: React.ReactNode; title: string; sub: string }) {
  const tones: Record<string, string> = {
    green: 'bg-green-500/15 text-green-300 border-green-500/40',
    purple: 'bg-purple-500/15 text-purple-300 border-purple-500/40',
    red: 'bg-red-500/15 text-red-300 border-red-500/40',
  };
  return (
    <div className="absolute inset-0 z-20 flex items-center justify-center bg-slate-950/60 backdrop-blur-[1px] pointer-events-none">
      <div className={`px-6 py-4 rounded-xl border flex flex-col items-center gap-2 text-center animate-scale-in ${tones[props.tone]}`}>
        <div className="opacity-90">{props.icon}</div>
        <span className="font-bold text-base">{props.title}</span>
        <span className="text-xs opacity-80 max-w-[14rem]">{props.sub}</span>
      </div>
    </div>
  );
}

function Row(props: {
  cover?: string | null; icon: React.ReactNode; name: string; sub: string;
  badge: { label: string; cls: string }; action: React.ReactNode;
  draggable?: boolean; onDragStart?: (e: React.DragEvent) => void;
  onContextMenu?: (e: React.MouseEvent) => void;
}) {
  return (
    <div
      draggable={props.draggable}
      onDragStart={props.onDragStart}
      onContextMenu={props.onContextMenu}
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

// Plain-language state filter options (no "sync" jargon). Phrased per pane so the
// "only on one side" option reads naturally:
//   - My PC pane:  a game not on the source is "Only on my PC"
//   - Source pane: a game not on my PC is "Not on my PC" (i.e. available to get)
type Opt = { value: string; label: string };
const STATE_OPTIONS_LOCAL: Opt[] = [
  { value: 'all', label: 'All games' },
  { value: 'in_sync', label: 'Up to date' },
  { value: 'update', label: 'Update available' },
  { value: 'unsynced', label: 'Only on my PC' },
];
const STATE_OPTIONS_SOURCE: Opt[] = [
  { value: 'all', label: 'All games' },
  { value: 'in_sync', label: 'Up to date' },
  { value: 'update', label: 'Update available' },
  { value: 'unsynced', label: 'Not on my PC' },
];
// Store filter options. Values match GameInfo.platform ('EpicGames', not 'Epic').
const STORE_OPTIONS: Opt[] = [
  { value: 'all', label: 'All stores' },
  { value: 'Steam', label: 'Steam' },
  { value: 'EpicGames', label: 'Epic' },
  { value: 'Xbox', label: 'Xbox' },
  { value: 'External', label: 'External' },
];

/** Search box + a state dropdown + a store dropdown, matching the Figma v11 filter bar. */
function FilterBar(props: {
  search: string; onSearch: (v: string) => void; placeholder: string; accent: 'blue' | 'purple' | 'cyan';
  side: 'local' | 'source';
  store: string; onStore: (v: string) => void;
  state: string; onState: (v: string) => void;
}) {
  return (
    <div className="flex items-center gap-2">
      <div className="flex-1 min-w-0">
        <SearchBox value={props.search} onChange={props.onSearch} placeholder={props.placeholder} accent={props.accent} />
      </div>
      <FilterDropdown value={props.state} onChange={props.onState} accent={props.accent}
        options={props.side === 'local' ? STATE_OPTIONS_LOCAL : STATE_OPTIONS_SOURCE} />
      <FilterDropdown value={props.store} onChange={props.onStore} accent={props.accent} options={STORE_OPTIONS} />
    </div>
  );
}

/** Custom dropdown styled to match the app (rounded dark menu via a portal), replacing the
 *  squarish native <select>. */
function FilterDropdown(props: { value: string; onChange: (v: string) => void; accent: 'blue' | 'purple' | 'cyan'; options: Opt[] }) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ left: number; top: number; width: number } | null>(null);
  const ref = useRef<HTMLButtonElement>(null);
  const hover = { blue: 'hover:border-blue-500', purple: 'hover:border-purple-500', cyan: 'hover:border-cyan-500' }[props.accent];
  const current = props.options.find(o => o.value === props.value) ?? props.options[0];
  const active = props.value !== 'all';
  const openMenu = () => {
    const r = ref.current?.getBoundingClientRect();
    if (r) setPos({ left: r.left, top: r.bottom + 4, width: r.width });
    setOpen(true);
  };
  return (
    <div className="relative flex-shrink-0">
      <button
        ref={ref}
        onClick={() => (open ? setOpen(false) : openMenu())}
        className={`flex items-center gap-1.5 bg-slate-900/50 border rounded-lg px-2.5 py-1.5 text-xs whitespace-nowrap transition-colors ${hover} ${active ? 'border-slate-500 text-white' : 'border-slate-700 text-slate-300'}`}
      >
        <span>{current.label}</span>
        <ChevronDown className={`w-3 h-3 text-slate-500 transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>
      {open && pos && createPortal(
        <>
          <div className="fixed inset-0 z-[60]" onClick={() => setOpen(false)} />
          <div
            className="fixed bg-slate-800 border border-slate-600 rounded-lg shadow-2xl z-[61] py-1 overflow-auto max-h-72"
            style={{ left: pos.left, top: pos.top, minWidth: Math.max(pos.width, 168) }}
          >
            {props.options.map(o => (
              <button
                key={o.value}
                onClick={() => { props.onChange(o.value); setOpen(false); }}
                className={`w-full text-left px-3 py-1.5 text-xs flex items-center justify-between gap-3 hover:bg-slate-700 ${o.value === props.value ? 'text-white bg-slate-700/40' : 'text-slate-300'}`}
              >
                <span>{o.label}</span>
                {o.value === props.value && <Check className="w-3.5 h-3.5 text-green-400 flex-shrink-0" />}
              </button>
            ))}
          </div>
        </>,
        document.body,
      )}
    </div>
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

/** v11-style custom source selector: shows the current source + a dropdown grouped into
 *  Network Peers and External Drives. Replaces the old native <select>. */
function SourceDropdown(props: {
  peers: NetworkPeer[];
  libs: ExternalLibrary[];
  target: Target; onPick: (t: Target) => void;
  label: string; sub: string;
}) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const isSel = (kind: 'peer' | 'drive', id: string) => props.target?.kind === kind && props.target.id === id;

  // The pane uses overflow-hidden (for rounded corners), so render the menu in a portal
  // anchored to the trigger's screen rect — otherwise it gets clipped.
  const openMenu = () => {
    const r = triggerRef.current?.getBoundingClientRect();
    if (r) setPos({ left: r.left, top: r.bottom + 6 });
    setOpen(true);
  };
  const pick = (t: Target) => { props.onPick(t); setOpen(false); };

  return (
    <div className="relative min-w-0">
      <button ref={triggerRef} onClick={() => (open ? setOpen(false) : openMenu())} className="flex flex-col items-start min-w-0 text-left">
        <span className="flex items-center gap-1 min-w-0">
          <span className="font-semibold text-white truncate max-w-[14rem]">{props.label}</span>
          <ChevronDown className={`w-3.5 h-3.5 text-white/70 flex-shrink-0 transition-transform ${open ? 'rotate-180' : ''}`} />
        </span>
        {props.sub && (
          <span className="text-[0.625rem] text-white/70 bg-black/20 rounded px-1.5 py-px truncate max-w-[14rem] mt-0.5">{props.sub}</span>
        )}
      </button>

      {open && pos && createPortal(
        <>
          <div className="fixed inset-0 z-[60]" onClick={() => setOpen(false)} />
          <div
            className="fixed w-64 bg-slate-800 border border-slate-600 rounded-lg shadow-2xl z-[61] py-1 max-h-72 overflow-auto"
            style={{ left: pos.left, top: pos.top }}
          >
            <div className="px-3 py-1.5 text-[0.625rem] font-semibold text-slate-400 uppercase tracking-wider">Network Peers</div>
            {props.peers.length > 0
              ? props.peers.map(p => (
                  <button
                    key={p.peerId}
                    onClick={() => pick({ kind: 'peer', id: p.peerId })}
                    className={`w-full text-left px-3 py-2 text-xs flex items-center gap-2 hover:bg-slate-700 ${isSel('peer', p.peerId) ? 'bg-slate-700/50' : ''}`}
                  >
                    <Users className="w-3.5 h-3.5 text-purple-400 flex-shrink-0" />
                    <span className="truncate flex-1">{p.displayName}</span>
                    <span className="text-[0.625rem] text-slate-500">{p.games?.length ?? 0}</span>
                    <span className={`w-1.5 h-1.5 rounded-full flex-shrink-0 ${p.isOnline ? 'bg-green-400' : 'bg-slate-500'}`} />
                  </button>
                ))
              : <div className="px-3 py-2 text-xs text-slate-500">No peers online — Scan for Peers</div>}

            <div className="px-3 py-1.5 mt-1 border-t border-slate-700 text-[0.625rem] font-semibold text-slate-400 uppercase tracking-wider">External Drives</div>
            {props.libs.length > 0
              ? props.libs.map(l => (
                  <button
                    key={l.id}
                    onClick={() => pick({ kind: 'drive', id: l.id })}
                    className={`w-full text-left px-3 py-2 text-xs flex items-center gap-2 hover:bg-slate-700 ${isSel('drive', l.id) ? 'bg-slate-700/50' : ''}`}
                  >
                    <HardDrive className="w-3.5 h-3.5 text-cyan-400 flex-shrink-0" />
                    <span className="truncate flex-1">{driveLabel(l)}</span>
                  </button>
                ))
              : <div className="px-3 py-2 text-xs text-slate-500">No drives added</div>}
          </div>
        </>,
        document.body,
      )}
    </div>
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
