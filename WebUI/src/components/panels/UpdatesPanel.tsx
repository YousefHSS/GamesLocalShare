import { useAppState } from '../../store';
import { sendCommand } from '../../bridge';

export default function UpdatesPanel() {
  const availableSyncs = useAppState((state) => state.availableSyncs);
  const selectedSyncItem = useAppState((state) => state.selectedSyncItem);
  const selectedPeerGame = useAppState((state) => state.selectedPeerGame);
  const isTransferring = useAppState((state) => state.isTransferring);
  const currentTransferGameName = useAppState((state) => state.currentTransferGameName);
  const currentTransferProgress = useAppState((state) => state.currentTransferProgress);
  const currentTransferFormattedProgress = useAppState((state) => state.currentTransferFormattedProgress);
  const currentTransferSpeed = useAppState((state) => state.currentTransferSpeed);
  const currentTransferTimeRemaining = useAppState((state) => state.currentTransferTimeRemaining);

  return (
    <div className="bg-dark-panel rounded flex flex-col h-full overflow-hidden">
      {/* Header */}
      <div className="bg-accent-green px-4 py-3 flex items-center justify-between flex-shrink-0">
        <div className="flex items-center gap-2">
          <span>⬇️</span>
          <h2 className="font-bold text-white">Updates Available</h2>
        </div>
        <button
          onClick={() => sendCommand('AddAllUpdatesToQueue')}
          className="bg-green-600 px-2 py-1 rounded text-white text-xs hover:bg-opacity-80"
        >
          + Queue All
        </button>
      </div>

      {/* Updates list */}
      <div className="flex-1 overflow-y-auto">
        {availableSyncs.length === 0 ? (
          <div className="flex items-center justify-center h-full text-gray-400 text-sm">
            No updates available
          </div>
        ) : (
          <div className="space-y-1 p-2">
            {availableSyncs.map((sync, idx) => (
              <div
                key={idx}
                onClick={() => sendCommand('SelectSyncItem', { appId: sync.remoteGame.appId })}
                className={`p-2 rounded cursor-pointer transition text-xs ${
                  selectedSyncItem?.remoteGame.appId === sync.remoteGame.appId
                    ? 'bg-accent-green bg-opacity-20'
                    : 'hover:bg-dark-item'
                }`}
              >
                <p className="font-semibold text-white">{sync.displayName}</p>
                <p className="text-gray-400">{sync.syncDescription}</p>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Action buttons and transfer progress */}
      <div className="border-t border-dark-item p-2 space-y-2 flex-shrink-0">
        <div className="flex gap-2">
          <button
            onClick={() => sendCommand('StartSync')}
            disabled={!selectedSyncItem || isTransferring}
            className="flex-1 bg-accent-green px-3 py-1 rounded text-white text-xs hover:bg-opacity-80 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            Download Update
          </button>
          <button
            onClick={() => sendCommand('DownloadNewGame')}
            disabled={!selectedPeerGame || isTransferring}
            className="flex-1 bg-accent-blue px-3 py-1 rounded text-white text-xs hover:bg-opacity-80 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            Download New Game
          </button>
        </div>

        {/* Transfer progress */}
        {isTransferring && (
          <div className="bg-dark-item p-2 rounded space-y-1">
            <p className="font-semibold text-white text-xs">{currentTransferGameName}</p>
            <div className="w-full bg-dark-panel rounded h-1 overflow-hidden">
              <div
                className="h-full bg-accent-cyan transition-all"
                style={{ width: `${currentTransferProgress}%` }}
              />
            </div>
            <p className="text-gray-400 text-xs">{currentTransferFormattedProgress}</p>
            <p className="text-gray-400 text-xs">
              {currentTransferSpeed} | {currentTransferTimeRemaining}
            </p>
            <div className="flex gap-1 mt-1">
              <button
                onClick={() => sendCommand('PauseTransfer')}
                className="flex-1 bg-yellow-600 px-2 py-0.5 rounded text-white text-xs hover:bg-opacity-80"
              >
                Pause
              </button>
              <button
                onClick={() => sendCommand('StopTransfer')}
                className="flex-1 bg-red-600 px-2 py-0.5 rounded text-white text-xs hover:bg-opacity-80"
              >
                Stop
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
