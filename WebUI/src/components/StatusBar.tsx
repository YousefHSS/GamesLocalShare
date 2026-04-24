import { useAppState } from '../store';
import { sendCommand } from '../bridge';

export default function StatusBar() {
  const statusMessage = useAppState((state) => state.statusMessage);
  const highSpeedMode = useAppState((state) => state.highSpeedMode);
  const isLogVisible = useAppState((state) => state.isLogVisible);

  return (
    <div className="bg-dark-panel px-4 py-2 flex items-center justify-between border-t border-dark-item">
      {/* Left: Status message */}
      <p className="text-sm text-gray-300 flex-1">{statusMessage}</p>

      {/* Right: Buttons */}
      <div className="flex items-center gap-2">
        <button
          onClick={() => sendCommand('OpenSettings')}
          className="px-2 py-1 text-gray-400 hover:text-white text-sm"
          title="Settings"
        >
          ⚙️
        </button>

        <button
          onClick={() => sendCommand('ToggleHighSpeedMode')}
          className={`px-3 py-1 rounded text-xs font-medium transition ${
            highSpeedMode
              ? 'bg-accent-cyan text-white'
              : 'bg-dark-item text-gray-400 hover:text-white'
          }`}
        >
          {highSpeedMode ? 'High Speed' : 'WiFi Mode'}
        </button>

        <span className="text-gray-500 text-sm">Games Local Share</span>

        <button
          onClick={() => sendCommand('ToggleLog')}
          className={`px-2 py-1 text-sm transition ${
            isLogVisible ? 'text-accent-cyan' : 'text-gray-400 hover:text-white'
          }`}
          title="Activity Log"
        >
          📋
        </button>
      </div>
    </div>
  );
}
