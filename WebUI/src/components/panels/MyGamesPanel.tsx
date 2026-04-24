import { useAppState } from '../../store';
import { sendCommand } from '../../bridge';

export default function MyGamesPanel() {
  const localGames = useAppState((state) => state.localGames);
  const selectedLocalGame = useAppState((state) => state.selectedLocalGame);
  const isScanning = useAppState((state) => state.isScanning);

  console.log('[DEBUG] MyGamesPanel render, localGames:', localGames);
  console.log('[DEBUG] MyGamesPanel render, localGames count:', localGames.length);
  console.log('[DEBUG] MyGamesPanel render, first game:', localGames[0]);
  console.log('[DEBUG] MyGamesPanel render, selectedLocalGame:', selectedLocalGame);
  console.log('[DEBUG] MyGamesPanel render, isScanning:', isScanning);

  return (
    <div className="bg-dark-panel rounded flex flex-col h-full overflow-hidden">
      {/* Header */}
      <div className="bg-accent-blue px-4 py-3 flex items-center justify-between flex-shrink-0">
        <div className="flex items-center gap-2">
          <span>🎮</span>
          <h2 className="font-bold text-white">My Games</h2>
          <span className="text-xs opacity-80">({localGames.length})</span>
        </div>
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
        ) : (
          <div className="space-y-1 p-2">
            {localGames.map((game) => (
              <div
                key={game.appId}
                onClick={() => sendCommand('SelectLocalGame', { appId: game.appId })}
                className={`p-2 rounded cursor-pointer transition ${
                  selectedLocalGame?.appId === game.appId
                    ? 'bg-accent-blue bg-opacity-20'
                    : 'hover:bg-dark-item'
                }`}
              >
                <div className="flex gap-2 items-start">
                  <div className="w-12 h-16 bg-dark-item rounded flex-shrink-0 flex overflow-hidden items-center justify-center text-xl">
                    {false ? (
                      <img 
                        src={`https://cdn.cloudflare.steamstatic.com/steam/apps/${game.appId}/library_600x900.jpg`} 
                        alt={`${game.name} cover`}
                        className="w-full h-full object-cover"
                        onError={(e) => {
                          const target = e.target as HTMLImageElement;
                          // Fallback to header format if library cover fails
                          if (target.src.includes('library_600x900')) {
                            target.src = `https://cdn.cloudflare.steamstatic.com/steam/apps/${game.appId}/header.jpg`;
                          } else {
                            // If all fails, remove image to show fallback icon
                            target.style.display = 'none';
                            const parent = target.parentElement;
                            if (parent) {
                              const icon = document.createElement('span');
                              icon.innerText = '🎮';
                              parent.appendChild(icon);
                            }
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
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
