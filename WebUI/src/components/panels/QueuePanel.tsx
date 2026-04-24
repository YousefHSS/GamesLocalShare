import { useAppState } from '../../store';
import { sendCommand } from '../../bridge';

export default function QueuePanel() {
  const incompleteTransfers = useAppState((state) => state.incompleteTransfers);
  const downloadQueue = useAppState((state) => state.downloadQueue);
  const isQueueProcessing = useAppState((state) => state.isQueueProcessing);
  const hasRunnableItems = downloadQueue.some((item) =>
    item.status === 'Queued' || item.status === 'Paused' || item.status === 0 || item.status === 4
  );

  return (
    <div className="bg-dark-panel rounded flex flex-col h-full overflow-hidden">
      {/* Incomplete Transfers Section */}
      <div className="border-b border-dark-item flex-shrink-0 max-h-[120px] flex flex-col overflow-hidden">
        <div className="bg-red-600 px-4 py-2 flex items-center justify-between flex-shrink-0">
          <h2 className="font-bold text-white text-sm">Incomplete</h2>
          <button
            onClick={() => sendCommand('AddAllIncompleteToQueue')}
            className="bg-red-700 px-2 py-0.5 rounded text-white text-xs hover:bg-opacity-80"
          >
            + Queue All
          </button>
        </div>
        <div className="flex-1 overflow-y-auto p-1">
          {incompleteTransfers.length === 0 ? (
            <p className="text-gray-400 text-xs">No incomplete transfers</p>
          ) : (
            <div className="space-y-1">
              {incompleteTransfers.map((transfer) => (
                <div key={transfer.gameAppId} className="p-1 rounded bg-dark-item hover:bg-opacity-80 text-xs">
                  <p className="text-white text-xs font-semibold">{transfer.gameName}</p>
                  <div className="w-full bg-dark-panel rounded h-0.5 my-0.5">
                    <div
                      className="h-full bg-red-500"
                      style={{ width: `${transfer.progressPercent}%` }}
                    />
                  </div>
                  <p className="text-gray-400 text-xs">{transfer.formattedProgress}</p>
                </div>
              ))}
            </div>
          )}
        </div>
        <div className="px-2 py-1 flex-shrink-0">
          <button
            onClick={() => sendCommand('ResumeTransfer')}
            className="w-full bg-red-600 px-2 py-1 rounded text-white text-xs hover:bg-opacity-80"
          >
            Resume Selected
          </button>
        </div>
      </div>

      {/* Download Queue Section */}
      <div className="flex-1 flex flex-col overflow-hidden">
        <div className="bg-cyan-600 px-4 py-2 flex items-center justify-between flex-shrink-0">
          <h2 className="font-bold text-white text-sm">Download Queue ({downloadQueue.length})</h2>
          <button
            onClick={() => sendCommand('ClearQueue')}
            className="bg-cyan-700 px-2 py-0.5 rounded text-white text-xs hover:bg-opacity-80"
          >
            Clear
          </button>
        </div>

        <div className="flex-1 overflow-y-auto p-2">
          {downloadQueue.length === 0 ? (
            <p className="text-gray-400 text-xs">Queue is empty</p>
          ) : (
            <div className="space-y-1">
              {downloadQueue.map((item) => (
                <div
                  key={item.gameAppId}
                  className="bg-dark-item p-2 rounded text-xs space-y-1"
                >
                  <div className="flex items-center justify-between">
                    <span className="text-white font-semibold">{item.typeIcon} {item.gameName}</span>
                    <div className="flex gap-1">
                      <button
                        onClick={() => sendCommand('MoveQueueItemUp', { appId: item.gameAppId })}
                        className="text-gray-400 hover:text-white"
                      >
                        ▲
                      </button>
                      <button
                        onClick={() => sendCommand('MoveQueueItemDown', { appId: item.gameAppId })}
                        className="text-gray-400 hover:text-white"
                      >
                        ▼
                      </button>
                      <button
                        onClick={() => sendCommand('RemoveFromQueue', { appId: item.gameAppId })}
                        className="text-gray-400 hover:text-white"
                      >
                        ✕
                      </button>
                    </div>
                  </div>

                  {(item.status === 'Downloading' || item.status === 1) && (
                    <div className="w-full bg-dark-panel rounded h-1">
                      <div
                        className="h-full bg-cyan-500"
                        style={{ width: `${item.progress}%` }}
                      />
                    </div>
                  )}

                  <div className="flex items-center justify-between">
                    <span style={{ color: item.statusColor }} className="text-xs font-medium">
                      {item.statusText}
                    </span>
                    <span className="text-gray-400">{item.formattedSize}</span>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Queue controls */}
        <div className="px-2 py-2 border-t border-dark-item flex-shrink-0 flex gap-2">
          <button
            onClick={() => sendCommand('StartQueue')}
            disabled={isQueueProcessing || !hasRunnableItems}
            className="flex-1 bg-cyan-600 px-3 py-1 rounded text-white text-xs hover:bg-opacity-80 disabled:opacity-50"
          >
            Start Queue
          </button>
          {isQueueProcessing && (
            <button
              onClick={() => sendCommand('PauseQueue')}
              className="flex-1 bg-yellow-600 px-3 py-1 rounded text-white text-xs hover:bg-opacity-80"
            >
              Pause
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
