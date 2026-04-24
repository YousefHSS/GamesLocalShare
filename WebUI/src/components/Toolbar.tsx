import { useAppState } from '../store';
import { sendCommand } from '../bridge';
import GameLogo from './GameLogo';

export default function Toolbar() {
  const isScanning = useAppState((state) => state.isScanning);
  const isNetworkActive = useAppState((state) => state.isNetworkActive);
  const isScanningPeers = useAppState((state) => state.isScanningPeers);
  const firewallConfigured = useAppState((state) => state.firewallConfigured);
  const localIpAddress = useAppState((state) => state.localIpAddress);
  const isOnline = useAppState((state) => state.isNetworkActive);

  return (
    <div className="bg-accent-blue px-4 py-4 flex items-center justify-between">
      {/* Left side: Logo and buttons */}
      <div className="flex items-center gap-4">
        <div className="flex items-center gap-2">
          <GameLogo variant="network" size={32} />
          <span className="text-sm font-semibold text-white hidden sm:inline">Games Local Share</span>
        </div>

        {/* Step 1: Scan */}
        <div className="flex items-center gap-2">
          <span className="bg-white text-accent-blue text-xs font-bold px-2 py-1 rounded">1</span>
          <button
            onClick={() => sendCommand('ScanLocalGames')}
            disabled={isScanning}
            className="bg-accent-purple px-3 py-1 rounded text-white text-sm font-medium hover:bg-accent-purple2 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            Scan My Games
          </button>
        </div>

        {/* Step 2: Network */}
        <div className="flex items-center gap-2">
          <span className="bg-white text-accent-blue text-xs font-bold px-2 py-1 rounded">2</span>
          {!isNetworkActive ? (
            <button
              onClick={() => sendCommand('StartNetwork')}
              className="bg-accent-purple px-3 py-1 rounded text-white text-sm font-medium hover:bg-accent-purple2"
            >
              Start Network
            </button>
          ) : (
            <button
              onClick={() => sendCommand('StopNetwork')}
              className="bg-red-500 px-3 py-1 rounded text-white text-sm font-medium hover:bg-red-600"
            >
              Stop Network
            </button>
          )}
        </div>

        {/* Step 3: Peers (only show when network active) */}
        {isNetworkActive && (
          <div className="flex items-center gap-2">
            <span className="bg-white text-accent-blue text-xs font-bold px-2 py-1 rounded">3</span>
            <button
              onClick={() => sendCommand('ScanForPeers')}
              disabled={isScanningPeers}
              className="bg-accent-purple px-3 py-1 rounded text-white text-sm font-medium hover:bg-accent-purple2 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Scan for Peers
            </button>
          </div>
        )}

        {/* Firewall config button */}
        {!firewallConfigured && (
          <button
            onClick={() => sendCommand('ConfigureFirewall')}
            className="bg-red-500 px-3 py-1 rounded text-white text-sm font-medium hover:bg-red-600 flex items-center gap-1"
          >
            <span>⚠</span>
            <span>Configure Firewall</span>
          </button>
        )}
      </div>

      {/* Right side: IP and status */}
      <div className="flex items-center gap-4">
        <div className="flex items-center gap-2 text-white text-sm">
          <span>Your IP:</span>
          <button
            onClick={() => sendCommand('CopyLocalIp')}
            className="font-bold underline hover:opacity-80"
            title="Click to copy"
          >
            {localIpAddress || '...'}
          </button>
        </div>
        <span className={`text-sm font-medium ${isOnline ? 'text-green-400' : 'text-gray-400'}`}>
          {isOnline ? 'Online' : 'Offline'}
        </span>
      </div>
    </div>
  );
}
