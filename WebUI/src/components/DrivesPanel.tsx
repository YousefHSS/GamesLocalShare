import { useEffect, useMemo, useState } from 'react';
import { HardDrive, Copy, RefreshCw, ArrowRight, ArrowLeft, Check, HelpCircle, Info } from 'lucide-react';
import { useAppState, type CrossLocationGame, type ExternalLibrary, type DriveCandidate } from '../store';
import { sendCommand } from '../bridge';
import PlatformIcon from './PlatformIcon';

function statusBadgeClass(color: string): string {
  switch (color) {
    case 'green': return 'bg-green-900/40 text-green-300 border-green-700/50';
    case 'yellow': return 'bg-yellow-900/40 text-yellow-300 border-yellow-700/50';
    default: return 'bg-slate-800/60 text-slate-400 border-slate-700/50';
  }
}

function DirectionIcon({ direction }: { direction: CrossLocationGame['direction'] }) {
  switch (direction) {
    case 'InSync': return <Check className="w-3.5 h-3.5 text-green-400" />;
    case 'DeviceToDrive': return <ArrowRight className="w-3.5 h-3.5 text-yellow-400" />;
    case 'DriveToDevice': return <ArrowLeft className="w-3.5 h-3.5 text-yellow-400" />;
    default: return <HelpCircle className="w-3.5 h-3.5 text-slate-400" />;
  }
}

function isLibraryConnected(lib: ExternalLibrary, drives: DriveCandidate[]): boolean {
  return drives.some(d =>
    lib.driveSerial
      ? d.serial.toLowerCase() === lib.driveSerial.toLowerCase()
      : lib.rootPath.startsWith(d.driveLetter)
  );
}

export default function DrivesPanel() {
  const s = useAppState();
  const [isComparing, setIsComparing] = useState(false);
  const [crossGames, setCrossGames] = useState<CrossLocationGame[]>(s.crossLocationGames);
  const [pendingPath, setPendingPath] = useState<string | null>(null);
  const [pendingName, setPendingName] = useState('');
  const [manualPath, setManualPath] = useState('');
  // "Device-only" rows (games installed locally but not on the drive) dominate
  // the list once compared, so they're hidden by default. The user can opt in
  // when they actually want to see what's missing from a drive.
  const [showDeviceOnly, setShowDeviceOnly] = useState(false);

  // Keep local state in sync with store
  useEffect(() => {
    setCrossGames(s.crossLocationGames);
  }, [s.crossLocationGames]);

  // Register callback for compare result
  useEffect(() => {
    (window as any).__crossLocationGamesResult = (games: CrossLocationGame[]) => {
      setCrossGames(games);
      setIsComparing(false);
    };
    return () => {
      (window as any).__crossLocationGamesResult = undefined;
    };
  }, []);

  useEffect(() => {
    const handler = (path: string) => {
      setPendingPath(path);
      setPendingName(path.split(/[\\/]/).filter(Boolean).pop() ?? path);
    };
    const prev = (window as any).__driveBrowseResult;
    (window as any).__driveBrowseResult = handler;
    return () => {
      if ((window as any).__driveBrowseResult === handler) {
        (window as any).__driveBrowseResult = prev;
      }
    };
  }, []);

  const confirmAdd = () => {
    if (!pendingPath) return;
    sendCommand('AddExternalLibrary', { rootPath: pendingPath, displayName: pendingName });
    setPendingPath(null);
    setPendingName('');
  };

  const addManualPath = () => {
    const path = manualPath.trim();
    if (!path) return;
    const name = path.split(/[\\/]/).filter(Boolean).pop() ?? path;
    sendCommand('AddExternalLibrary', { rootPath: path, displayName: name });
    setManualPath('');
  };

  const handleScan = () => {
    sendCommand('ScanExternalLibraries');
  };

  const handleCompare = () => {
    setIsComparing(true);
    sendCommand('CompareGameLocations');
  };

  const handleAddLibrary = () => {
    sendCommand('BrowseDriveFolder');
  };

  const handleCopy = (game: CrossLocationGame, overrideDirection?: CrossLocationGame['direction']) => {
    if (!game.library) return;
    sendCommand('StartLocalCopy', {
      appId: game.appId,
      libraryId: game.library.id,
      direction: overrideDirection ?? game.direction,
    });
  };

  const canCopy = (dir: CrossLocationGame['direction']) =>
    dir === 'DeviceToDrive' || dir === 'DriveToDevice' ||
    dir === 'OnlyOnDevice' || dir === 'OnlyOnDrive';

  const deviceOnlyCount = useMemo(
    () => crossGames.filter(g => g.direction === 'OnlyOnDevice').length,
    [crossGames],
  );

  const visibleCrossGames = useMemo(
    () => (showDeviceOnly ? crossGames : crossGames.filter(g => g.direction !== 'OnlyOnDevice')),
    [crossGames, showDeviceOnly],
  );

  // Group cross-location games by library, both filtered and unfiltered. The
  // unfiltered map lets us distinguish "no comparison done yet" from "everything
  // for this library is hidden because device-only is off".
  const byLibrary = new Map<string, CrossLocationGame[]>();
  for (const g of visibleCrossGames) {
    if (!g.library) continue;
    const key = g.library.id;
    if (!byLibrary.has(key)) byLibrary.set(key, []);
    byLibrary.get(key)!.push(g);
  }
  const totalByLibrary = new Map<string, number>();
  for (const g of crossGames) {
    if (!g.library) continue;
    totalByLibrary.set(g.library.id, (totalByLibrary.get(g.library.id) ?? 0) + 1);
  }

  const libs = s.externalLibraries ?? [];

  // Pick the platform from whichever copy is present; deviceCopy first since
  // it's the canonical install on this machine.
  const platformFor = (g: CrossLocationGame) =>
    g.deviceCopy?.platform ?? g.externalCopy?.platform;

  return (
    <div className="flex-1 min-h-0 flex flex-col overflow-hidden">
      {/* Toolbar */}
      <div className="flex items-center gap-2 px-4 py-3 border-b border-slate-700/50 flex-shrink-0 flex-wrap">
        <button
          onClick={handleScan}
          disabled={s.isScanning}
          className="px-3 py-1.5 bg-blue-600 hover:bg-blue-700 disabled:bg-blue-800 disabled:cursor-not-allowed text-white rounded text-xs font-medium flex items-center gap-1.5"
        >
          {s.isScanning && <RefreshCw className="w-3.5 h-3.5 animate-spin" />}
          Scan External
        </button>
        <button
          onClick={handleCompare}
          disabled={isComparing || libs.length === 0}
          className="px-3 py-1.5 bg-purple-600 hover:bg-purple-700 disabled:bg-slate-700 disabled:cursor-not-allowed text-white rounded text-xs font-medium flex items-center gap-1.5"
        >
          {isComparing && <RefreshCw className="w-3.5 h-3.5 animate-spin" />}
          Compare Locations
        </button>
        <button
          onClick={handleAddLibrary}
          className="px-3 py-1.5 bg-slate-700 hover:bg-slate-600 text-white rounded text-xs font-medium flex items-center gap-1.5"
        >
          <HardDrive className="w-3.5 h-3.5" /> Add Library
        </button>
        <label className="flex items-center gap-1.5 text-xs text-slate-400 cursor-pointer select-none">
          <input
            type="checkbox"
            checked={showDeviceOnly}
            onChange={e => setShowDeviceOnly(e.target.checked)}
            className="w-3.5 h-3.5 accent-blue-500"
          />
          <span>
            Show device-only games
            {deviceOnlyCount > 0 && <span className="text-slate-500"> ({deviceOnlyCount})</span>}
          </span>
        </label>
        <div className="flex items-center gap-1 ml-auto">
          <input
            type="text"
            value={manualPath}
            onChange={e => setManualPath(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') addManualPath(); }}
            placeholder="or type a path (e.g. D:\)"
            className="bg-slate-900 border border-slate-700 rounded px-2 py-1 text-xs text-white placeholder-slate-500 focus:outline-none focus:border-blue-500 w-48"
          />
          <button
            onClick={addManualPath}
            disabled={!manualPath.trim()}
            className="px-2 py-1 bg-slate-700 hover:bg-slate-600 disabled:opacity-40 disabled:cursor-not-allowed text-white rounded text-xs font-medium"
          >
            Add
          </button>
        </div>
      </div>

      {/* Terminology hint */}
      <div className="flex items-center gap-1.5 px-4 py-1.5 text-[11px] text-slate-500 border-b border-slate-700/30 flex-shrink-0">
        <Info className="w-3 h-3 flex-shrink-0" />
        <span>"Device" means this PC's internal drives (where Steam, Epic, etc. install games). External libraries are listed below.</span>
      </div>

      {/* Pending add-library confirmation */}
      {pendingPath && (
        <div className="px-4 py-3 border-b border-slate-700/50 bg-blue-900/20 space-y-2 flex-shrink-0">
          <p className="text-xs text-blue-300">Selected: <span className="font-mono">{pendingPath}</span></p>
          <div className="flex items-center gap-2">
            <input
              type="text"
              value={pendingName}
              onChange={e => setPendingName(e.target.value)}
              placeholder="Display name"
              className="flex-1 bg-slate-800 border border-slate-700 rounded px-2 py-1 text-xs text-white focus:outline-none focus:border-blue-500"
            />
            <button onClick={confirmAdd} className="px-3 py-1 bg-blue-600 hover:bg-blue-700 text-white text-xs rounded font-medium">
              Add
            </button>
            <button onClick={() => { setPendingPath(null); setPendingName(''); }} className="px-3 py-1 bg-slate-700 hover:bg-slate-600 text-white text-xs rounded">
              Cancel
            </button>
          </div>
        </div>
      )}

      {/* Content */}
      <div className="flex-1 min-h-0 overflow-auto p-4 space-y-4">
        {libs.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-12 text-center">
            <HardDrive className="w-12 h-12 text-slate-600 mb-3" />
            <p className="text-slate-500 text-sm">No external libraries configured</p>
            <p className="text-slate-600 text-xs mt-1">Click "Add Library" to add a drive folder</p>
          </div>
        ) : (
          libs.map(lib => {
            const connected = isLibraryConnected(lib, s.drives);
            const gamesForLib = byLibrary.get(lib.id) ?? [];
            const totalForLib = totalByLibrary.get(lib.id) ?? 0;
            const hiddenDeviceOnly = totalForLib - gamesForLib.length;

            return (
              <div key={lib.id} className="bg-slate-800/40 border border-slate-700/50 rounded-lg overflow-hidden">
                {/* Library header */}
                <div className="flex items-center justify-between px-4 py-3 bg-slate-800/60 border-b border-slate-700/50">
                  <div className="flex items-center gap-2 min-w-0">
                    <HardDrive className={`w-4 h-4 flex-shrink-0 ${connected ? 'text-green-400' : 'text-slate-500'}`} />
                    <div className="min-w-0">
                      <p className="text-sm font-semibold text-white truncate">{lib.displayName}</p>
                      <p className="text-[10px] text-slate-500 font-mono truncate">{lib.rootPath}</p>
                    </div>
                  </div>
                  <span className={`text-[10px] px-2 py-0.5 rounded border font-medium flex-shrink-0 ml-2 ${
                    connected ? 'bg-green-900/40 text-green-300 border-green-700/50' : 'bg-slate-800 text-slate-500 border-slate-700'
                  }`}>
                    {connected ? 'Connected' : 'Disconnected'}
                  </span>
                </div>

                {/* Games in this library */}
                {gamesForLib.length === 0 ? (
                  <div className="px-4 py-3 text-xs text-slate-500 italic">
                    {hiddenDeviceOnly > 0
                      ? `${hiddenDeviceOnly} device-only game${hiddenDeviceOnly === 1 ? '' : 's'} hidden — toggle "Show device-only games" to view`
                      : connected
                        ? 'Click "Compare Locations" to see games'
                        : 'Drive not connected'}
                  </div>
                ) : (
                  <div className="divide-y divide-slate-700/30">
                    {gamesForLib.map(game => (
                      <div key={game.appId} className="flex items-center justify-between px-4 py-2.5 hover:bg-slate-700/20">
                        <div className="flex items-center gap-2 min-w-0 flex-1">
                          <DirectionIcon direction={game.direction} />
                          <PlatformIcon platform={platformFor(game)} />
                          <p className="text-sm text-white truncate">{game.displayName}</p>
                        </div>
                        <div className="flex items-center gap-2 ml-2 flex-shrink-0">
                          <span className={`text-[10px] px-1.5 py-0.5 rounded border ${statusBadgeClass(game.statusColor)}`}>
                            {game.statusText}
                          </span>
                          {canCopy(game.direction) && (
                            <button
                              onClick={() => handleCopy(game)}
                              disabled={s.isTransferring}
                              className="p-1 bg-slate-700 hover:bg-slate-600 disabled:opacity-40 disabled:cursor-not-allowed rounded"
                              title="Copy"
                            >
                              <Copy className="w-3.5 h-3.5 text-slate-300" />
                            </button>
                          )}
                          {game.direction === 'UnknownVersion' && (
                            <>
                              <button
                                onClick={() => handleCopy(game, 'DeviceToDrive')}
                                disabled={s.isTransferring}
                                className="p-1 bg-slate-700 hover:bg-slate-600 disabled:opacity-40 disabled:cursor-not-allowed rounded"
                                title="Copy device → drive (overwrite drive copy)"
                              >
                                <ArrowRight className="w-3.5 h-3.5 text-slate-300" />
                              </button>
                              <button
                                onClick={() => handleCopy(game, 'DriveToDevice')}
                                disabled={s.isTransferring}
                                className="p-1 bg-slate-700 hover:bg-slate-600 disabled:opacity-40 disabled:cursor-not-allowed rounded"
                                title="Copy drive → device (overwrite device copy)"
                              >
                                <ArrowLeft className="w-3.5 h-3.5 text-slate-300" />
                              </button>
                            </>
                          )}
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            );
          })
        )}
      </div>
    </div>
  );
}
