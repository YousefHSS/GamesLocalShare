import { useState, useEffect } from 'react';
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
  Monitor
} from 'lucide-react';

type FlowMode = 'sender' | 'receiver';
type TransferType = 'drive' | 'network' | null;

interface XboxTransferModalProps {
  onClose: () => void;
  mode?: FlowMode;
}

export default function XboxTransferModal({ onClose, mode = 'sender' }: XboxTransferModalProps) {
  const selectedLocalGame = useAppState((s) => s.selectedLocalGame);
  const selectedPeerGame = useAppState((s) => s.selectedPeerGame);
  const xboxTransfer = useAppState((s) => s.xboxTransfer);
  const isElevated = useAppState((s) => s.isElevated);
  const isXboxTransferActive = useAppState((s) => s.isXboxTransferActive);
  const xboxDestinationPath = useAppState((s) => s.xboxDestinationPath);
  const [step, setStep] = useState(0);
  const [error, setError] = useState<string | null>(null);

  const game = mode === 'sender' ? selectedLocalGame : selectedPeerGame;

  // Advance step based on backend state when in receiver mode
  useEffect(() => {
    if (mode !== 'receiver' || !xboxTransfer) return;
    const backendStep = xboxTransfer.currentStep;
    if (backendStep === 'Complete' || backendStep === 'Failed') {
      setStep(99);
      return;
    }
    const stepMap: Record<string, number> = {
      ChooseSource: 0,
      ElevationGate: 1,
      InstallInXboxApp: 2,
      WaitingForInstallPause: 3,
      PollingForFolder: 4,
      Overlaying: 5,
      ResettingAcls: 6,
      WaitingForResume: 7,
      Monitoring: 8,
    };
    const mapped = stepMap[backendStep];
    if (mapped !== undefined && mapped > step) setStep(mapped);
  }, [xboxTransfer, mode, step]);

  const handleChooseType = (type: TransferType) => {
    setError(null);
    if (mode === 'sender') {
      if (type === 'drive') {
        setStep(10); // Sender drive flow
      } else {
        setStep(20); // Sender network flow
      }
    } else {
      // Receiver flow
      if (!isElevated) {
        setStep(1); // Elevation gate
      } else {
        setStep(2); // Install instructions
      }
    }
  };

  const handleRequestElevation = () => {
    sendCommand('RequestElevation');
  };

  const handleStartSenderDrive = async () => {
    if (!game?.installPath || !xboxDestinationPath) {
      setError('Please select a destination folder');
      return;
    }
    setError(null);
    sendCommand('StartXboxStage', { sourcePath: game.installPath });
    // Wait a tick then complete with destination
    setTimeout(() => {
      sendCommand('CompleteXboxStage', { destinationPath: xboxDestinationPath });
    }, 500);
    setStep(11);
  };

  const handleStartSenderNetwork = async () => {
    if (!game?.installPath) {
      setError('No game selected');
      return;
    }
    setError(null);
    sendCommand('PrepareXboxNetwork', { sourcePath: game.installPath });
    setStep(21);
  };

  const handleBrowseDestination = () => {
    sendCommand('BrowseXboxDestination');
  };

  const isXboxOverlayGame = game?.platform === 'Xbox' && game?.isOverlaySupported;

  const renderStepContent = () => {
    // === RECEIVER FLOW ===
    if (mode === 'receiver') {
      switch (step) {
        case 0:
          return (
            <div className="space-y-4">
              <h3 className="text-lg font-semibold text-white">Choose Source</h3>
              <p className="text-gray-400 text-sm">
                How is the staged game available to you?
              </p>
              <button
                onClick={() => handleChooseType('drive')}
                className="w-full flex items-center gap-3 p-4 rounded-lg bg-dark-elem hover:bg-dark-hover border border-gray-700 transition"
              >
                <HardDrive className="text-blue-400" size={24} />
                <div className="text-left">
                  <div className="text-white font-medium">Drive / USB</div>
                  <div className="text-gray-400 text-xs">A pre-staged copy on a local or shared drive</div>
                </div>
              </button>
              <button
                onClick={() => handleChooseType('network')}
                className="w-full flex items-center gap-3 p-4 rounded-lg bg-dark-elem hover:bg-dark-hover border border-gray-700 transition"
              >
                <Wifi className="text-green-400" size={24} />
                <div className="text-left">
                  <div className="text-white font-medium">Network Peer</div>
                  <div className="text-gray-400 text-xs">Stream directly from a peer on your LAN</div>
                </div>
              </button>
            </div>
          );

        case 1:
          return (
            <div className="space-y-4">
              <div className="flex items-center gap-2 text-yellow-400 font-semibold">
                <Shield size={20} />
                Administrator Required
              </div>
              <p className="text-gray-300 text-sm">
                Xbox overlay transfer requires Administrator privileges to copy files into the protected MSIXVC install folder.
              </p>
              <button
                onClick={handleRequestElevation}
                className="w-full bg-yellow-600 hover:bg-yellow-500 text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
              >
                <Shield size={18} />
                Restart as Administrator
              </button>
              <p className="text-gray-500 text-xs">
                The app will close and reopen with a UAC prompt.
              </p>
            </div>
          );

        case 2:
          return (
            <div className="space-y-4">
              <h3 className="text-lg font-semibold text-white">Install in Xbox App</h3>
              <ol className="text-gray-300 text-sm list-decimal list-inside space-y-2">
                <li>Open the Xbox app on this PC</li>
                <li>Find <strong>{game?.name || 'the game'}</strong> and click <strong>Install</strong></li>
                <li>As soon as it starts downloading, click <strong>Pause</strong></li>
                <li>Come back here and click Continue</li>
              </ol>
              <button
                onClick={() => setStep(3)}
                className="w-full bg-blue-600 hover:bg-blue-500 text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
              >
                <ArrowRight size={18} />
                I've Paused the Install
              </button>
            </div>
          );

        case 3:
        case 4:
        case 5:
        case 6:
        case 7:
        case 8:
          return (
            <div className="space-y-4">
              <div className="flex items-center gap-2 text-blue-400 font-semibold">
                <Loader2 className="animate-spin" size={20} />
                {xboxTransfer?.statusMessage || 'Working...'}
              </div>
              {xboxTransfer && xboxTransfer.overlayProgress > 0 && (
                <div className="w-full bg-gray-700 rounded-full h-2">
                  <div
                    className="bg-blue-500 h-2 rounded-full transition-all"
                    style={{ width: `${xboxTransfer.overlayProgress}%` }}
                  />
                </div>
              )}
              <div className="text-gray-400 text-xs font-mono">
                Step: {xboxTransfer?.currentStep || '...'}
              </div>
              {isXboxTransferActive && (
                <button
                  onClick={() => sendCommand('CancelXboxTransfer')}
                  className="w-full bg-red-600 hover:bg-red-500 text-white py-2 rounded font-semibold transition"
                >
                  Cancel Transfer
                </button>
              )}
            </div>
          );

        case 99:
          return (
            <div className="space-y-4">
              {xboxTransfer?.verdict === 'FullSkip' || xboxTransfer?.verdict === 'DeltaOnly' ? (
                <div className="flex items-center gap-2 text-green-400 font-semibold">
                  <CheckCircle size={24} />
                  Transfer Complete!
                </div>
              ) : (
                <div className="flex items-center gap-2 text-red-400 font-semibold">
                  <AlertCircle size={24} />
                  Transfer {xboxTransfer?.verdict || 'Failed'}
                </div>
              )}
              <div className="text-gray-300 text-sm space-y-1">
                <p>Verdict: <span className="font-semibold">{xboxTransfer?.verdict}</span></p>
                <p>Network: {xboxTransfer?.networkReceivedMB.toFixed(1)} MB</p>
                <p>Installed: {xboxTransfer?.packageInstalled ? 'Yes' : 'No'}</p>
                {xboxTransfer?.errorMessage && (
                  <p className="text-red-400">{xboxTransfer.errorMessage}</p>
                )}
              </div>
              <button
                onClick={onClose}
                className="w-full bg-gray-600 hover:bg-gray-500 text-white py-2 rounded font-semibold transition"
              >
                Close
              </button>
            </div>
          );
      }
    }

    // === SENDER FLOW ===
    if (mode === 'sender') {
      switch (step) {
        case 0:
          return (
            <div className="space-y-4">
              <h3 className="text-lg font-semibold text-white">Share Xbox Game</h3>
              <p className="text-gray-400 text-sm">
                {game?.name || 'Selected game'} supports overlay transfer.
                How do you want to share it?
              </p>
              <button
                onClick={() => handleChooseType('drive')}
                className="w-full flex items-center gap-3 p-4 rounded-lg bg-dark-elem hover:bg-dark-hover border border-gray-700 transition"
              >
                <HardDrive className="text-blue-400" size={24} />
                <div className="text-left">
                  <div className="text-white font-medium">Stage to Drive</div>
                  <div className="text-gray-400 text-xs">Copy to a USB or shared folder for manual handoff</div>
                </div>
              </button>
              <button
                onClick={() => handleChooseType('network')}
                className="w-full flex items-center gap-3 p-4 rounded-lg bg-dark-elem hover:bg-dark-hover border border-gray-700 transition"
              >
                <Wifi className="text-green-400" size={24} />
                <div className="text-left">
                  <div className="text-white font-medium">Stream to Peer</div>
                  <div className="text-gray-400 text-xs">Let a peer download directly over your LAN</div>
                </div>
              </button>
            </div>
          );

        case 10:
          return (
            <div className="space-y-4">
              <h3 className="text-lg font-semibold text-white">Stage to Drive</h3>
              <p className="text-gray-400 text-sm">
                Select a destination folder (USB drive or shared folder).
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
                disabled={!xboxDestinationPath || isXboxTransferActive}
                className="w-full bg-blue-600 hover:bg-blue-500 disabled:opacity-50 text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
              >
                {isXboxTransferActive ? <Loader2 className="animate-spin" size={18} /> : <Play size={18} />}
                Start Staging
              </button>
            </div>
          );

        case 11:
          return (
            <div className="space-y-4">
              <div className="flex items-center gap-2 text-blue-400 font-semibold">
                <Loader2 className="animate-spin" size={20} />
                {xboxTransfer?.statusMessage || 'Staging...'}
              </div>
              {xboxTransfer && xboxTransfer.overlayProgress > 0 && (
                <div className="w-full bg-gray-700 rounded-full h-2">
                  <div
                    className="bg-blue-500 h-2 rounded-full transition-all"
                    style={{ width: `${xboxTransfer.overlayProgress}%` }}
                  />
                </div>
              )}
              {isXboxTransferActive && (
                <button
                  onClick={() => sendCommand('CancelXboxStage')}
                  className="w-full bg-red-600 hover:bg-red-500 text-white py-2 rounded font-semibold transition"
                >
                  Cancel
                </button>
              )}
              {!isXboxTransferActive && xboxTransfer?.currentStep === 'Complete' && (
                <button
                  onClick={onClose}
                  className="w-full bg-green-600 hover:bg-green-500 text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
                >
                  <CheckCircle size={18} />
                  Done
                </button>
              )}
            </div>
          );

        case 20:
          return (
            <div className="space-y-4">
              <h3 className="text-lg font-semibold text-white">Stream to Peer</h3>
              <p className="text-gray-400 text-sm">
                This will start a network sender so peers can stream the game directly.
                Keep this window open until the peer finishes receiving.
              </p>
              <button
                onClick={handleStartSenderNetwork}
                disabled={isXboxTransferActive}
                className="w-full bg-green-600 hover:bg-green-500 disabled:opacity-50 text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
              >
                <Wifi size={18} />
                Start Network Sender
              </button>
            </div>
          );

        case 21:
          return (
            <div className="space-y-4">
              <div className="flex items-center gap-2 text-green-400 font-semibold">
                <Monitor size={20} />
                Network Sender Running
              </div>
              <p className="text-gray-300 text-sm">
                Peers can now stream <strong>{game?.name}</strong> from this device.
              </p>
              <p className="text-gray-400 text-xs">
                Port: 45680
              </p>
              <button
                onClick={() => {
                  sendCommand('StopXboxNetwork');
                  onClose();
                }}
                className="w-full bg-red-600 hover:bg-red-500 text-white py-2 rounded font-semibold transition"
              >
                Stop Sender & Close
              </button>
            </div>
          );
      }
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
          {!isXboxOverlayGame && step === 0 && (
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

          {renderStepContent()}
        </div>

        {/* Step indicator */}
        {mode === 'receiver' && step > 0 && step < 99 && (
          <div className="px-6 pb-4">
            <div className="flex justify-between text-xs text-gray-500">
              <span>{step >= 1 ? 'Elevated' : '...'}</span>
              <span>{step >= 2 ? 'Paused' : '...'}</span>
              <span>{step >= 5 ? 'Overlay' : '...'}</span>
              <span>{step >= 8 ? 'Monitor' : '...'}</span>
            </div>
            <div className="w-full bg-gray-700 rounded-full h-1 mt-1">
              <div
                className="bg-blue-500 h-1 rounded-full transition-all"
                style={{ width: `${Math.min((step / 8) * 100, 100)}%` }}
              />
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
