import { useAppState } from '../../store';
import { sendCommand } from '../../bridge';

export default function PeersPanel() {
  const networkPeers = useAppState((state) => state.networkPeers);
  const availableFromPeers = useAppState((state) => state.availableFromPeers);
  const selectedPeer = useAppState((state) => state.selectedPeer);
  const manualPeerIp = useAppState((state) => state.manualPeerIp);
  const isNetworkActive = useAppState((state) => state.isNetworkActive);

  return (
    <div className="bg-dark-panel rounded flex flex-col h-full overflow-hidden">
      {/* Header */}
      <div className="bg-accent-purple px-4 py-3 flex items-center justify-between flex-shrink-0">
        <div className="flex items-center gap-2">
          <span>🖥️</span>
          <h2 className="font-bold text-white">Network Peers</h2>
          <span className="text-xs opacity-80">({networkPeers.length})</span>
        </div>
        <div className="flex gap-1">
          <button
            onClick={() => sendCommand('TestConnection')}
            className="bg-accent-purple2 px-2 py-1 rounded text-white text-xs hover:bg-opacity-80"
          >
            Test
          </button>
          <button
            onClick={() => sendCommand('RefreshPeers')}
            className="bg-accent-purple2 px-2 py-1 rounded text-white text-xs hover:bg-opacity-80"
          >
            Refresh
          </button>
        </div>
      </div>

      {/* Manual IP input */}
      <div className="bg-dark-item px-2 py-2 flex gap-2 flex-shrink-0">
        <input
          type="text"
          value={manualPeerIp}
          onChange={(e) => useAppState.setState({ manualPeerIp: e.target.value })}
          placeholder="IP address"
          disabled={!isNetworkActive}
          className="flex-1 bg-dark-panel px-2 py-1 rounded text-white text-sm disabled:opacity-50"
        />
        <button
          onClick={() => sendCommand('ConnectManualIp', { ip: manualPeerIp })}
          disabled={!isNetworkActive}
          className="bg-accent-blue px-2 py-1 rounded text-white text-xs hover:bg-opacity-80 disabled:opacity-50"
        >
          Connect
        </button>
      </div>

      {/* Peers list */}
      <div className="flex-1 overflow-y-auto">
        {networkPeers.length === 0 ? (
          <div className="flex items-center justify-center h-full text-gray-400 text-sm">
            No peers found
          </div>
        ) : (
          <div className="space-y-1 p-2">
            {networkPeers.map((peer) => (
              <div
                key={peer.peerId}
                onClick={() => sendCommand('SelectPeer', { peerId: peer.peerId })}
                className={`p-2 rounded cursor-pointer transition ${
                  selectedPeer?.peerId === peer.peerId
                    ? 'bg-accent-purple bg-opacity-20'
                    : 'hover:bg-dark-item'
                }`}
              >
                <div className="flex items-center justify-between">
                  <div>
                    <p className="font-semibold text-white text-sm">{peer.displayName}</p>
                    <p className="text-xs text-gray-400">{peer.ipAddress}</p>
                  </div>
                  <div className="bg-accent-purple px-2 py-1 rounded text-white text-xs">
                    {peer.games.length}
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* New Games from Peers */}
      <div className="border-t border-dark-item">
        <div className="bg-accent-purple px-4 py-2 text-sm font-bold text-white">
          New Games from Peers
        </div>
        <div className="p-2 text-xs text-gray-400">
          {availableFromPeers.length === 0
            ? 'No new games available'
            : `${availableFromPeers.length} games available`}
        </div>
      </div>
    </div>
  );
}
