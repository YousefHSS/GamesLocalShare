import { useAppState } from '../store';
import { sendCommand } from '../bridge';

export default function LogOverlay() {
  const logMessages = useAppState((state) => state.logMessages);

  return (
    <div className="absolute bottom-12 left-2 right-2 max-w-md bg-dark-panel rounded border border-dark-item shadow-lg max-h-64 flex flex-col">
      {/* Header */}
      <div className="px-3 py-2 border-b border-dark-item flex items-center justify-between flex-shrink-0">
        <h3 className="font-bold text-white text-sm">Activity Log</h3>
        <button
          onClick={() => sendCommand('ClearLog')}
          className="text-gray-400 hover:text-white text-xs"
        >
          Clear
        </button>
      </div>

      {/* Log messages */}
      <div className="flex-1 overflow-y-auto text-xs font-mono">
        {logMessages.length === 0 ? (
          <div className="p-2 text-gray-500">No messages</div>
        ) : (
          <div className="space-y-0">
            {logMessages.map((msg, idx) => (
              <div
                key={idx}
                className="px-2 py-1 hover:bg-dark-item transition cursor-pointer border-b border-dark-item last:border-b-0"
              >
                <div className="flex gap-2">
                  <span
                    className="flex-shrink-0"
                    style={{ color: msg.typeColor || '#9CA3AF' }}
                  >
                    {msg.typeIcon}
                  </span>
                  <span className="text-gray-400 flex-shrink-0 w-12">{msg.formattedTime}</span>
                  <span className="text-gray-300 truncate">{msg.message}</span>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
