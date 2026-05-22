import { useState } from 'react';
import { useAppState } from '../store';
import { sendCommand } from '../bridge';
import {
  X,
  Play,
  HardDrive,
  Wifi,
  Shield,
  AlertCircle,
  CheckCircle,
  Loader2,
  ArrowRight,
  FolderOpen,
} from 'lucide-react';

type FlowMode = 'sender' | 'receiver';

interface XboxTransferModalProps {
  onClose: () => void;
  mode?: FlowMode;
}

// Wizard step ids. Negative-free numbering; the working/result views are
// driven entirely by the backend xboxTransfer state once `launched` is set.
const STEP_CHOOSE = 0;
const STEP_ELEVATION = 1;
const STEP_SELECT_SOURCE = 2; // receiver only
const STEP_INSTRUCTIONS = 3; // receiver only
const STEP_SELECT_DEST = 10; // sender only

function baseName(p: string): string {
  if (!p) return '';
  const parts = p.split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] || p;
}

export default function XboxTransferModal({ onClose, mode = 'sender' }: XboxTransferModalProps) {
  const selectedLocalGame = useAppState((s) => s.selectedLocalGame);
  const xboxTransfer = useAppState((s) => s.xboxTransfer);
  const isElevated = useAppState((s) => s.isElevated);
  const isXboxTransferActive = useAppState((s) => s.isXboxTransferActive);
  const xboxDestinationPath = useAppState((s) => s.xboxDestinationPath);
  const xboxSourcePath = useAppState((s) => s.xboxSourcePath);

  const [step, setStep] = useState(STEP_CHOOSE);
  const [launched, setLaunched] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [xboxRoot, setXboxRoot] = useState('');
  const [force, setForce] = useState(false);

  const game = mode === 'sender' ? selectedLocalGame : null;
  const isXboxOverlayGame = game?.platform === 'Xbox' && game?.isOverlaySupported;

  const transferStep = xboxTransfer?.currentStep;
  const finished = transferStep === 'Complete' || transferStep === 'Failed';
  const awaitingResume = transferStep === 'WaitingForResume';

  const handleChooseDrive = () => {
    setError(null);
    if (!isElevated) {
      setStep(STEP_ELEVATION);
      return;
    }
    setStep(mode === 'sender' ? STEP_SELECT_DEST : STEP_SELECT_SOURCE);
  };

  const handleRequestElevation = () => sendCommand('RequestElevation');
  const handleBrowseDestination = () => sendCommand('BrowseXboxDestination');
  const handleBrowseSource = () => sendCommand('BrowseXboxSource');

  const handleStartSenderDrive = () => {
    if (!game?.installPath || !xboxDestinationPath) {
      setError('Please select a destination folder.');
      return;
    }
    setError(null);
    setLaunched(true);
    sendCommand('StartXboxStage', { sourcePath: game.installPath });
    // StartXboxStage validates and prepares; CompleteXboxStage does the copy.
    setTimeout(() => {
      sendCommand('CompleteXboxStage', { destinationPath: xboxDestinationPath });
    }, 500);
  };

  const handleStartReceiver = () => {
    if (!xboxSourcePath) {
      setError('Please select the staged game folder.');
      return;
    }
    setError(null);
    setLaunched(true);
    sendCommand('StartXboxTransfer', {
      sourcePath: xboxSourcePath,
      xboxRoot: xboxRoot.trim() || undefined,
      force,
    });
  };

  // ---- Shared sub-views -----------------------------------------------------

  const elevationGate = (
    <div className="space-y-4">
      <div className="flex items-center gap-2 text-yellow-400 font-semibold">
        <Shield size={20} />
        Administrator Required
      </div>
      <p className="text-gray-300 text-sm">
        Xbox transfer copies files into protected MSIXVC install folders and
        runs helper processes as SYSTEM. The app must be restarted with
        Administrator privileges.
      </p>
      <button
        onClick={handleRequestElevation}
        className="w-full bg-yellow-600 hover:bg-yellow-500 text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
      >
        <Shield size={18} />
        Restart as Administrator
      </button>
      <p className="text-gray-500 text-xs">
        The app will close and reopen with a UAC prompt. Reopen this window
        afterwards.
      </p>
    </div>
  );

  const networkButton = (
    <div className="w-full flex items-center gap-3 p-4 rounded-lg bg-dark-elem border border-gray-800 opacity-50 cursor-not-allowed">
      <Wifi className="text-gray-500" size={24} />
      <div className="text-left">
        <div className="text-gray-400 font-medium">
          {mode === 'sender' ? 'Stream to Peer' : 'Network Peer'}
          <span className="ml-2 text-xs text-gray-500">(coming soon)</span>
        </div>
        <div className="text-gray-600 text-xs">
          LAN streaming is not available yet for Xbox titles.
        </div>
      </div>
    </div>
  );

  // ---- Working / result view (backend-driven) ------------------------------

  const renderProgress = () => {
    if (finished) {
      const v = xboxTransfer?.verdict;
      const ok = v === 'FullSkip' || v === 'DeltaOnly';
      return (
        <div className="space-y-4">
          {ok ? (
            <div className="flex items-center gap-2 text-green-400 font-semibold">
              <CheckCircle size={24} />
              {mode === 'sender' ? 'Staging Complete' : 'Transfer Complete'}
            </div>
          ) : (
            <div className="flex items-center gap-2 text-red-400 font-semibold">
              <AlertCircle size={24} />
              {mode === 'sender' ? 'Staging Failed' : `Transfer ${v || 'Failed'}`}
            </div>
          )}
          <p className="text-gray-300 text-sm">{xboxTransfer?.statusMessage}</p>
          {xboxTransfer?.errorMessage && !ok && (
            <div className="bg-red-900/40 border border-red-700 text-red-300 text-sm p-3 rounded whitespace-pre-line">
              {xboxTransfer.errorMessage}
            </div>
          )}
          {mode === 'receiver' && (
            <div className="text-gray-400 text-xs space-y-1">
              <p>Verdict: <span className="font-semibold">{xboxTransfer?.verdict}</span></p>
              <p>Downloaded: {xboxTransfer?.networkReceivedMB?.toFixed(1)} MB</p>
              <p>Package installed: {xboxTransfer?.packageInstalled ? 'Yes' : 'No'}</p>
            </div>
          )}
          <button
            onClick={onClose}
            className="w-full bg-gray-600 hover:bg-gray-500 text-white py-2 rounded font-semibold transition"
          >
            Close
          </button>
        </div>
      );
    }

    return (
      <div className="space-y-4">
        {awaitingResume ? (
          <div className="bg-yellow-900/40 border border-yellow-600 rounded p-4 space-y-1">
            <div className="flex items-center gap-2 text-yellow-300 font-bold">
              <Play size={20} />
              Click RESUME in the Xbox app now
            </div>
            <p className="text-yellow-200/80 text-sm">
              The staged files are in place. Resuming the install finalizes it.
            </p>
          </div>
        ) : (
          <div className="flex items-center gap-2 text-blue-400 font-semibold">
            <Loader2 className="animate-spin" size={20} />
            {xboxTransfer?.statusMessage || 'Working...'}
          </div>
        )}

        {!!xboxTransfer && xboxTransfer.overlayProgress > 0 && (
          <div className="w-full bg-gray-700 rounded-full h-2">
            <div
              className="bg-blue-500 h-2 rounded-full transition-all"
              style={{ width: `${xboxTransfer.overlayProgress}%` }}
            />
          </div>
        )}

        {mode === 'receiver' && (xboxTransfer?.networkReceivedMB ?? 0) > 0 && (
          <div className="text-gray-400 text-xs">
            Downloaded so far: {xboxTransfer?.networkReceivedMB?.toFixed(1)} MB
          </div>
        )}

        <div className="text-gray-500 text-xs font-mono">
          Step: {transferStep || '...'}
        </div>

        {isXboxTransferActive && (
          <button
            onClick={() =>
              sendCommand(mode === 'sender' ? 'CancelXboxStage' : 'CancelXboxTransfer')
            }
            className="w-full bg-red-600 hover:bg-red-500 text-white py-2 rounded font-semibold transition"
          >
            Cancel
          </button>
        )}
      </div>
    );
  };

  // ---- Wizard views --------------------------------------------------------

  const renderWizard = () => {
    if (step === STEP_ELEVATION) return elevationGate;

    if (step === STEP_CHOOSE) {
      return (
        <div className="space-y-4">
          <h3 className="text-lg font-semibold text-white">
            {mode === 'sender' ? 'Share Xbox Game' : 'Receive Xbox Game'}
          </h3>
          <p className="text-gray-400 text-sm">
            {mode === 'sender'
              ? `${game?.name || 'This game'} can be staged for overlay transfer. How do you want to share it?`
              : 'How is the staged game available to you?'}
          </p>
          <button
            onClick={handleChooseDrive}
            className="w-full flex items-center gap-3 p-4 rounded-lg bg-dark-elem hover:bg-dark-hover border border-gray-700 transition"
          >
            <HardDrive className="text-blue-400" size={24} />
            <div className="text-left">
              <div className="text-white font-medium">
                {mode === 'sender' ? 'Stage to Drive' : 'Drive / USB'}
              </div>
              <div className="text-gray-400 text-xs">
                {mode === 'sender'
                  ? 'Copy to a USB or shared folder for manual handoff'
                  : 'A pre-staged copy on a local or shared drive'}
              </div>
            </div>
          </button>
          {networkButton}
        </div>
      );
    }

    // Sender: pick destination and start.
    if (mode === 'sender' && step === STEP_SELECT_DEST) {
      return (
        <div className="space-y-4">
          <h3 className="text-lg font-semibold text-white">Stage to Drive</h3>
          <p className="text-gray-400 text-sm">
            Select a destination folder (USB drive or shared folder). The game
            folder name is appended automatically.
          </p>
          <div className="flex gap-2">
            <input
              type="text"
              readOnly
              value={xboxDestinationPath}
              placeholder="Click Browse to select folder..."
              className="flex-1 bg-dark-elem border border-gray-700 rounded px-3 py-2 text-sm text-white"
            />
            <button
              onClick={handleBrowseDestination}
              className="bg-gray-700 hover:bg-gray-600 text-white px-3 py-2 rounded transition"
            >
              <FolderOpen size={18} />
            </button>
          </div>
          <button
            onClick={handleStartSenderDrive}
            disabled={!xboxDestinationPath}
            className="w-full bg-blue-600 hover:bg-blue-500 disabled:opacity-50 text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
          >
            <Play size={18} />
            Start Staging
          </button>
          <p className="text-gray-500 text-xs">
            This stops Gaming Services briefly and rescues content-protected
            executables. It can take several minutes for large titles.
          </p>
        </div>
      );
    }

    // Receiver: select the staged source folder.
    if (mode === 'receiver' && step === STEP_SELECT_SOURCE) {
      return (
        <div className="space-y-4">
          <h3 className="text-lg font-semibold text-white">Select Staged Game</h3>
          <p className="text-gray-400 text-sm">
            Pick the folder produced by the sender. It must contain
            <span className="font-mono text-gray-300"> transfer-summary.json</span>.
          </p>
          <div className="flex gap-2">
            <input
              type="text"
              readOnly
              value={xboxSourcePath}
              placeholder="Click Browse to select the staged folder..."
              className="flex-1 bg-dark-elem border border-gray-700 rounded px-3 py-2 text-sm text-white"
            />
            <button
              onClick={handleBrowseSource}
              className="bg-gray-700 hover:bg-gray-600 text-white px-3 py-2 rounded transition"
            >
              <FolderOpen size={18} />
            </button>
          </div>

          <div className="space-y-2 border-t border-gray-700 pt-3">
            <label className="block text-xs text-gray-400">
              Xbox install drive (optional)
            </label>
            <input
              type="text"
              value={xboxRoot}
              onChange={(e) => setXboxRoot(e.target.value)}
              placeholder="e.g. D:\XboxGames - leave blank to auto-detect"
              className="w-full bg-dark-elem border border-gray-700 rounded px-3 py-2 text-sm text-white"
            />
            <label className="flex items-center gap-2 text-xs text-gray-300 cursor-pointer">
              <input
                type="checkbox"
                checked={force}
                onChange={(e) => setForce(e.target.checked)}
                className="accent-yellow-500"
              />
              Force overlay even if safety checks fail (may corrupt the install)
            </label>
          </div>

          <button
            onClick={() => {
              if (!xboxSourcePath) {
                setError('Please select the staged game folder.');
                return;
              }
              setError(null);
              setStep(STEP_INSTRUCTIONS);
            }}
            disabled={!xboxSourcePath}
            className="w-full bg-blue-600 hover:bg-blue-500 disabled:opacity-50 text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
          >
            <ArrowRight size={18} />
            Continue
          </button>
        </div>
      );
    }

    // Receiver: install + pause instructions, then launch.
    if (mode === 'receiver' && step === STEP_INSTRUCTIONS) {
      return (
        <div className="space-y-4">
          <h3 className="text-lg font-semibold text-white">Install in the Xbox App</h3>
          <ol className="text-gray-300 text-sm list-decimal list-inside space-y-2">
            <li>Open the Xbox app on this PC.</li>
            <li>
              Find <strong>{baseName(xboxSourcePath) || 'the game'}</strong> and
              click <strong>Install</strong>.
            </li>
            <li>Wait ~10 seconds for the download to start, then click <strong>Pause</strong>.</li>
            <li>Leave the Xbox app open and click the button below.</li>
          </ol>
          <button
            onClick={handleStartReceiver}
            className="w-full bg-blue-600 hover:bg-blue-500 text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
          >
            <ArrowRight size={18} />
            I've Paused the Install
          </button>
          <p className="text-gray-500 text-xs">
            You will be prompted to click Resume once the overlay is applied.
          </p>
        </div>
      );
    }

    return null;
  };

  return (
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50">
      <div className="bg-[#1f1f2e] border border-gray-700 rounded-lg shadow-xl w-full max-w-md flex flex-col max-h-[90vh] overflow-hidden">
        <div className="flex justify-between items-center p-4 border-b border-gray-700">
          <h2 className="text-xl font-semibold text-white">
            {mode === 'sender' ? 'Share Xbox Game' : 'Receive Xbox Game'}
          </h2>
          <button onClick={onClose} className="text-gray-400 hover:text-white transition">
            <X size={20} />
          </button>
        </div>

        <div className="p-6 overflow-y-auto flex-1">
          {mode === 'sender' && !isXboxOverlayGame && !launched && step === STEP_CHOOSE && (
            <div className="text-yellow-400 text-sm mb-4 flex items-center gap-2">
              <AlertCircle size={16} />
              This game does not appear to support overlay transfer.
            </div>
          )}

          {error && (
            <div className="bg-red-900/40 border border-red-700 text-red-300 text-sm p-3 rounded mb-4">
              {error}
            </div>
          )}

          {launched ? renderProgress() : renderWizard()}
        </div>
      </div>
    </div>
  );
}
