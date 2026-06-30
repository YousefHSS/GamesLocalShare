import { useState } from 'react';
import { useAppState } from '../../store';
import { sendCommand } from '../../bridge';
import XboxTransferModal from '../XboxTransferModal';

export default function MyGamesPanel() {
  const localGames = useAppState((state) => state.localGames);
  const selectedLocalGame = useAppState((state) => state.selectedLocalGame);
  const isScanning = useAppState((state) => state.isScanning);
  const showExternalGames = useAppState((state) => state.showExternalGames);
  const updateState = useAppState((state) => state.updateState);
  const [showTransferModal, setShowTransferModal] = useState(false);

  // Filter: when "Show external" is off, hide games living on external drives/libraries.
  const hasExternalGames = localGames.some((g) => g.isExternal);
  const visibleGames = showExternalGames
    ? localGames
    : localGames.filter((g) => !g.isExternal);

  return (
    <div className="bg-dark-panel rounded flex flex-col h-full overflow-hidden">
      {/* Header */}
      <div className="bg-accent-blue px-4 py-3 flex items-center justify-between flex-shrink-0">
        <div className="flex items-center gap-2">
          <span>🎮</span>
          <h2 className="font-bold text-white">My Games</h2>
          <span className="text-xs opacity-80">({visibleGames.length})</span>
        </div>
        {hasExternalGames && (
          <label className="flex items-center gap-1.5 text-xs text-white cursor-pointer select-none">
            <input
              type="checkbox"
              className="accent-accent-purple"
              checked={showExternalGames}
              onChange={(e) => updateState({ showExternalGames: e.target.checked })}
            />
            Show external drives
          </label>
        )}
      </div>

      {/* Content */}
      <div className="flex-1 overflow-y-auto">
        {isScanning ? (
          <div className="flex items-center justify-center h-full">
            <div className="text-gray-400">Scanning...</div>
          </div>
        ) : localGames.length === 0 ? (
          <div className="flex flex-col items-center justify-center h-full gap-2">
            <div className="text-gray-400 text-sm">No games found</div>
            <div className="text-gray-600 text-xs">Click 'Scan My Games' above</div>
            <button
              onClick={() => sendCommand('ShowTroubleshoot')}
              className="mt-2 bg-accent-blue px-3 py-1 rounded text-white text-xs hover:bg-accent-purple"
            >
              Troubleshoot
            </button>
          </div>
        ) : visibleGames.length === 0 ? (
          <div className="flex flex-col items-center justify-center h-full gap-2">
            <div className="text-gray-400 text-sm">External games hidden</div>
            <div className="text-gray-600 text-xs">Enable 'Show external drives' to see them</div>
          </div>
        ) : (
          <div className="space-y-1 p-2">
            {visibleGames.map((game) => (
              <div
                key={`${game.appId}|${game.installPath}`}
                onClick={() => sendCommand('SelectLocalGame', { appId: game.appId })}
                className={`p-2 rounded cursor-pointer transition ${
                  selectedLocalGame?.appId === game.appId
                    ? 'bg-accent-blue bg-opacity-20'
                    : 'hover:bg-dark-item'
                }`}
              >
                <div className="flex gap-2 items-start">
                  <div className="w-12 h-16 bg-dark-item rounded flex-shrink-0 flex overflow-hidden items-center justify-center text-xl">
                    {game.coverUrl ? (
                      <img 
                        src={game.coverUrl} 
                        alt={`${game.name} cover`}
                        className="w-full h-full object-cover"
                        onError={(e) => {
                          const target = e.target as HTMLImageElement;
                          target.style.display = 'none';
                          const parent = target.parentElement;
                          if (parent) {
                            const icon = document.createElement('span');
                            icon.innerText = '🎮';
                            parent.appendChild(icon);
                          }
                        }}
                      />
                    ) : (
                      <span>🎮</span>
                    )}
                  </div>
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                      <p className="font-semibold text-white text-sm truncate">{game.name}</p>
                      {game.isHidden && (
                        <span className="bg-red-500 text-white text-xs px-1 rounded">HIDDEN</span>
                      )}
                    </div>
                    <div className="flex gap-2 text-xs text-gray-400">
                      <span className="text-accent-blue">{game.buildId}</span>
                      <span>-</span>
                      <span>{game.formattedSize}</span>
                    </div>
                  </div>
                  {selectedLocalGame?.appId === game.appId && game.platform === 'Xbox' && (
                    <button
                      className="ml-auto bg-blue-600 hover:bg-blue-500 text-white px-3 py-1 rounded text-xs transition"
                      onClick={(e) => { e.stopPropagation(); setShowTransferModal(true); }}
                    >
                      {game.isOverlaySupported ? 'Share via Xbox Overlay' : 'Xbox Transfer'}
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
      {showTransferModal && <XboxTransferModal onClose={() => setShowTransferModal(false)} />}
    </div>
  );
}
