import { useState } from 'react';
import { useAppState, type NetworkPeer, type GameInfo } from '../store';
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
  ArrowLeft,
  FolderOpen,
  Monitor,
} from 'lucide-react';

type FlowMode = 'sender' | 'receiver';

interface XboxTransferModalProps {
  onClose: () => void;
  mode?: FlowMode;
  initialPeer?: NetworkPeer;
  initialGame?: GameInfo;
}

// Wizard step ids
const STEP_CHOOSE = 0;
const STEP_ELEVATION = 1;
const STEP_SELECT_SOURCE = 2;  // receiver: pick staged folder (drive mode)
const STEP_INSTRUCTIONS = 3;   // receiver: install/pause instructions
const STEP_SELECT_DEST = 10;   // sender: pick drive destination
const STEP_SELECT_PEER = 20;   // receiver: pick a network peer
const STEP_NETWORK_SENDER = 30; // sender: staging + waiting for receiver

function baseName(p: string): string {
  if (!p) return '';
  const parts = p.split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] || p;
}

export default function XboxTransferModal({ onClose, mode = 'sender', initialPeer, initialGame }: XboxTransferModalProps) {
  const selectedLocalGame = useAppState((s) => s.selectedLocalGame);
  const xboxTransfer = useAppState((s) => s.xboxTransfer);
  const isElevated = useAppState((s) => s.isElevated);
  const isXboxTransferActive = useAppState((s) => s.isXboxTransferActive);
  const xboxDestinationPath = useAppState((s) => s.xboxDestinationPath);
  const xboxSourcePath = useAppState((s) => s.xboxSourcePath);
  const xboxRootPath = useAppState((s) => s.xboxRootPath);
  const networkPeers = useAppState((s) => s.networkPeers);
  const updateState = useAppState((s) => s.updateState);

  const [step, setStep] = useState(() => {
    // If opened from PeersPanel with a pre-selected peer, skip to instructions
    if (mode === 'receiver' && initialPeer && initialGame) {
      return isElevated ? STEP_INSTRUCTIONS : STEP_ELEVATION;
    }
    return STEP_CHOOSE;
  });
  const [launched, setLaunched] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [force, setForce] = useState(false);
  const [isNetworkMode, setIsNetworkMode] = useState(!!initialPeer);
  const [selectedNetworkPeer, setSelectedNetworkPeer] = useState<NetworkPeer | null>(initialPeer ?? null);
  const [selectedNetworkGame, setSelectedNetworkGame] = useState<GameInfo | null>(initialGame ?? null);
  const [acknowledgedPause, setAcknowledgedPause] = useState(false);

  const game = mode === 'sender' ? selectedLocalGame : null;
  const isXboxOverlayGame = game?.platform === 'Xbox' && game?.isOverlaySupported;

  const transferStep = xboxTransfer?.currentStep;
  const finished = transferStep === 'Complete' || transferStep === 'Failed';
  const awaitingResume = transferStep === 'WaitingForResume';
  const waitingForReceiver = transferStep === 'WaitingForReceiver';

  const handleChooseDrive = () => {
    setError(null);
    setIsNetworkMode(false);
    if (!isElevated) {
      setStep(STEP_ELEVATION);
      return;
    }
    setStep(mode === 'sender' ? STEP_SELECT_DEST : STEP_SELECT_SOURCE);
  };

  const handleChooseNetwork = () => {
    setError(null);
    setIsNetworkMode(true);
    if (!isElevated) {
      setStep(STEP_ELEVATION);
      return;
    }
    if (mode === 'sender') {
      setStep(STEP_NETWORK_SENDER);
    } else {
      setStep(STEP_SELECT_PEER);
    }
  };

  const handleRequestElevation = () => sendCommand('RequestElevation');
  const handleBrowseDestination = () => sendCommand('BrowseXboxDestination');
  const handleBrowseSource = () => sendCommand('BrowseXboxSource');

  const handleStartSenderDrive = () => {
    if (!game?.installPath) {
      setError(`No game selected or game has no install path. (game=${game?.name ?? 'null'}, installPath=${game?.installPath ?? 'null'})`);
      return;
    }
    if (!xboxDestinationPath) {
      setError('Please select a destination folder.');
      return;
    }
    setError(null);
    sendCommand('StartXboxStage', { sourcePath: game.installPath });
    // StartXboxStage validates and prepares; CompleteXboxStage does the copy.
    setTimeout(() => {
      sendCommand('CompleteXboxStage', { destinationPath: xboxDestinationPath });
    }, 500);
    // Close the wizard — progress is shown by the global Xbox transfer bar,
    // matching how regular drive transfers behave.
    onClose();
  };

  const handleStartSenderNetwork = () => {
    if (!game?.installPath) {
      setError('No game selected or game has no install path.');
      return;
    }
    setError(null);
    setLaunched(true);
    sendCommand('PrepareXboxNetwork', { sourcePath: game.installPath });
  };

  const handleStopNetwork = () => {
    sendCommand('StopXboxNetwork');
    setLaunched(false);
  };

  const handleStartReceiver = () => {
    if (!xboxSourcePath) {
      setError('Please select the staged game folder.');
      return;
    }
    setError(null);
    sendCommand('StartXboxTransfer', {
      sourcePath: xboxSourcePath,
      xboxRoot: xboxRootPath.trim() || undefined,
      force,
    });
    // Close the wizard — progress is shown by the global Xbox transfer bar,
    // matching how regular drive transfers behave.
    onClose();
  };

  const handleStartNetworkReceiver = () => {
    if (!selectedNetworkPeer) {
      setError('No peer selected.');
      return;
    }
    setError(null);
    setLaunched(true);
    sendCommand('StartXboxNetworkTransfer', {
      peerHost: selectedNetworkPeer.ipAddress,
      peerPort: selectedNetworkPeer.xboxOverlayPort,
      gameAppId: selectedNetworkGame?.appId ?? '',
      xboxRoot: xboxRootPath.trim() || undefined,
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

  // ---- Working / result view (backend-driven) ------------------------------

  // Step-specific user guidance
  const stepGuidance: Record<string, { hint: string; action?: 'wait' | 'resume' }> = {
    DownloadingFromPeer: {
      hint: 'Downloading game files from peer. Do not close either app.',
      action: 'wait',
    },
    WaitingForReceiver: {
      hint: 'TCP server is ready. Tell the receiver to connect from their app.',
      action: 'wait',
    },
    PollingForFolder: {
      hint: 'Keep the Xbox app open with the install paused. The script is searching for the download folder on disk.',
      action: 'wait',
    },
    Overlaying: {
      hint: 'Do NOT touch the Xbox app. Files are being copied into the install folder.',
      action: 'wait',
    },
    ResettingAcls: {
      hint: 'Almost done — do NOT touch the Xbox app yet.',
      action: 'wait',
    },
    WaitingForResume: {
      hint: 'Go to the Xbox app and click RESUME on the install now.',
      action: 'resume',
    },
    Monitoring: {
      hint: 'Monitoring network traffic after Resume. Wait for it to finish.',
      action: 'wait',
    },
    CopyingFiles: {
      hint: 'Staging game files. This may take several minutes for large titles.',
      action: 'wait',
    },
    ValidatingSource: {
      hint: 'Validating staged files...',
      action: 'wait',
    },
  };

  const renderProgress = () => {
    if (finished) {
      const v = xboxTransfer?.verdict;
      const ok = v === 'FullSkip' || v === 'DeltaOnly';
      const wasStillPaused = v === 'StillPaused';
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
          {wasStillPaused && (
            <div className="bg-yellow-900/40 border border-yellow-700 text-yellow-300 text-sm p-3 rounded">
              It looks like the install was never resumed in the Xbox app. Try
              again: after the overlay finishes, switch to the Xbox app and click
              <strong> Resume</strong> on the paused install.
            </div>
          )}
          {xboxTransfer?.errorMessage && !ok && !wasStillPaused && (
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

    // Sender network: waiting for receiver — show stop button
    if (waitingForReceiver && mode === 'sender') {
      return (
        <div className="space-y-4">
          <div className="flex items-center gap-2 text-green-400 font-semibold">
            <Wifi size={20} className="animate-pulse" />
            Waiting for Receiver
          </div>
          <p className="text-gray-300 text-sm">{xboxTransfer?.statusMessage}</p>
          <div className="bg-gray-800/60 border border-gray-700 rounded p-3 text-sm text-gray-300">
            The staged game is ready to stream. On the receiver PC, open the app
            and use <strong>Receive Xbox Game &gt; Network Peer</strong> to connect.
          </div>
          <button
            onClick={handleStopNetwork}
            className="w-full bg-red-600 hover:bg-red-500 text-white py-2 rounded font-semibold transition"
          >
            Stop Serving
          </button>
        </div>
      );
    }

    const guidance = transferStep ? stepGuidance[transferStep] : undefined;

    return (
      <div className="space-y-4">
        {awaitingResume ? (
          <div className="bg-yellow-900/40 border border-yellow-600 rounded p-4 space-y-2">
            <div className="flex items-center gap-2 text-yellow-300 font-bold text-lg">
              <Play size={22} />
              Click RESUME in the Xbox app now
            </div>
            <p className="text-yellow-200/80 text-sm">
              The staged files are in place. Open the Xbox app, find the paused
              install, and click <strong>Resume</strong>. The transfer will
              monitor network traffic to verify the result.
            </p>
          </div>
        ) : (
          <>
            <div className="flex items-center gap-2 text-blue-400 font-semibold">
              <Loader2 className="animate-spin" size={20} />
              {xboxTransfer?.statusMessage || 'Working...'}
            </div>
            {guidance && (
              <div className="bg-gray-800/60 border border-gray-700 rounded p-3 text-sm text-gray-300">
                {guidance.hint}
              </div>
            )}
          </>
        )}

        {!!xboxTransfer && xboxTransfer.overlayProgress > 0 && (
          <div className="w-full bg-gray-700 rounded-full h-2">
            <div
              className="bg-blue-500 h-2 rounded-full transition-all"
              style={{ width: `${xboxTransfer.overlayProgress}%` }}
            />
          </div>
        )}

        {(xboxTransfer?.networkReceivedMB ?? 0) > 0 && (
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

  const renderPeerSelection = () => {
    const peersWithXbox = networkPeers.filter(p =>
      p.isOnline && p.games.some(g => g.platform === 'Xbox')
    );

    // Sub-step: pick a specific game from a selected peer
    if (selectedNetworkPeer) {
      const xboxGames = selectedNetworkPeer.games.filter(
        g => g.platform === 'Xbox'
      );
      return (
        <div className="space-y-4">
          <button
            onClick={() => { setSelectedNetworkPeer(null); setSelectedNetworkGame(null); }}
            className="flex items-center gap-1 text-gray-400 hover:text-white text-sm transition"
          >
            <ArrowLeft size={14} />
            Back to peers
          </button>
          <h3 className="text-lg font-semibold text-white">
            Xbox Games on {selectedNetworkPeer.displayName}
          </h3>
          {xboxGames.length === 0 ? (
            <p className="text-gray-400 text-sm">No Xbox overlay games found on this peer.</p>
          ) : (
            <div className="space-y-1 max-h-48 overflow-y-auto">
              {xboxGames.map((g) => (
                <button
                  key={g.appId}
                  onClick={() => {
                    setSelectedNetworkGame(g);
                    setStep(STEP_INSTRUCTIONS);
                  }}
                  className={`w-full text-left p-3 rounded-lg border transition ${
                    selectedNetworkGame?.appId === g.appId
                      ? 'bg-accent-blue bg-opacity-20 border-blue-500'
                      : 'bg-dark-item border-gray-700 hover:bg-[#4a4a4a]'
                  }`}
                >
                  <p className="text-white font-medium text-sm truncate">{g.name}</p>
                  <p className="text-gray-400 text-xs">{g.formattedSize}</p>
                </button>
              ))}
            </div>
          )}
        </div>
      );
    }

    // Main: pick a peer
    return (
      <div className="space-y-4">
        <h3 className="text-lg font-semibold text-white">Select Network Peer</h3>
        <p className="text-gray-400 text-sm">
          Choose a peer that has Xbox games staged for streaming.
        </p>
        {peersWithXbox.length === 0 ? (
          <div className="bg-gray-800/60 border border-gray-700 rounded p-4 text-center">
            <Monitor className="mx-auto mb-2 text-gray-500" size={32} />
            <p className="text-gray-400 text-sm">No peers with Xbox games found.</p>
            <p className="text-gray-500 text-xs mt-1">
              Make sure the sender has started &quot;Stream to Peer&quot; and both PCs are on the same network.
            </p>
          </div>
        ) : (
          <div className="space-y-1 max-h-48 overflow-y-auto">
            {peersWithXbox.map((peer) => {
              const xboxCount = peer.games.filter(
                g => g.platform === 'Xbox'
              ).length;
              return (
                <button
                  key={peer.peerId}
                  onClick={() => setSelectedNetworkPeer(peer)}
                  className="w-full flex items-center gap-3 p-3 rounded-lg bg-dark-item hover:bg-[#4a4a4a] border border-gray-700 transition text-left"
                >
                  <Monitor className="text-green-400 flex-shrink-0" size={20} />
                  <div className="flex-1 min-w-0">
                    <p className="text-white font-medium text-sm truncate">{peer.displayName}</p>
                    <p className="text-gray-400 text-xs">{peer.ipAddress}</p>
                  </div>
                  <span className="bg-green-600/20 text-green-400 text-xs px-2 py-1 rounded">
                    {xboxCount} Xbox
                  </span>
                </button>
              );
            })}
          </div>
        )}
      </div>
    );
  };

  const renderWizard = () => {
    if (step === STEP_ELEVATION) return elevationGate;

    if (step === STEP_CHOOSE) {
      return (
        <div className="space-y-4">
          <p className="text-gray-400 text-sm">
            {mode === 'sender'
              ? `${game?.name || 'This game'} can be staged for overlay transfer. How do you want to share it?`
              : 'How is the staged game available to you?'}
          </p>
          <button
            onClick={handleChooseDrive}
            className="w-full flex items-center gap-3 p-4 rounded-lg bg-dark-item hover:bg-[#4a4a4a] border border-gray-700 transition"
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
          <button
            onClick={handleChooseNetwork}
            className="w-full flex items-center gap-3 p-4 rounded-lg bg-dark-item hover:bg-[#4a4a4a] border border-gray-700 transition"
          >
            <Wifi className="text-green-400" size={24} />
            <div className="text-left">
              <div className="text-white font-medium">
                {mode === 'sender' ? 'Stream to Peer' : 'Network Peer'}
              </div>
              <div className="text-gray-400 text-xs">
                {mode === 'sender'
                  ? 'Stage locally, then stream over LAN to a receiver'
                  : 'Download from a peer on your local network'}
              </div>
            </div>
          </button>
        </div>
      );
    }

    // Sender: network staging + serving
    if (mode === 'sender' && step === STEP_NETWORK_SENDER) {
      return (
        <div className="space-y-4">
          <h3 className="text-lg font-semibold text-white">Stream to Peer</h3>
          <p className="text-gray-400 text-sm">
            The game will be staged to a temp folder (to rescue protected executables),
            then served over the network. The receiver can connect from their app.
          </p>
          <button
            onClick={handleStartSenderNetwork}
            disabled={!game?.installPath}
            className="w-full bg-green-600 hover:bg-green-500 disabled:opacity-50 text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
          >
            <Wifi size={18} />
            Start Staging & Serve
          </button>
          <p className="text-gray-500 text-xs">
            This stops Gaming Services briefly and rescues content-protected
            executables. It can take several minutes for large titles. After staging,
            the TCP server starts automatically.
          </p>
        </div>
      );
    }

    // Sender: pick destination and start (drive mode).
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
              className="flex-1 bg-dark-item border border-gray-700 rounded px-3 py-2 text-sm text-white"
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

    // Receiver: select a network peer
    if (mode === 'receiver' && step === STEP_SELECT_PEER) {
      return renderPeerSelection();
    }

    // Receiver: select the staged source folder (drive mode).
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
              className="flex-1 bg-dark-item border border-gray-700 rounded px-3 py-2 text-sm text-white"
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
            <div className="flex gap-2">
              <input
                type="text"
                value={xboxRootPath}
                onChange={(e) => {
                  updateState({ xboxRootPath: e.target.value });
                  sendCommand('SetXboxPath', { xboxRootPath: e.target.value });
                }}
                placeholder="e.g. D:\XboxGames - leave blank to auto-detect"
                className="flex-1 bg-dark-item border border-gray-600 rounded px-3 py-2 text-sm text-white placeholder-gray-500"
              />
              <button
                onClick={() => sendCommand('BrowseXboxRoot')}
                className="bg-gray-700 hover:bg-gray-600 text-white px-3 py-2 rounded transition"
                title="Browse for the Xbox install folder"
              >
                <FolderOpen size={18} />
              </button>
            </div>
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
      const gameName = isNetworkMode
        ? (selectedNetworkGame?.name || 'the game')
        : (baseName(xboxSourcePath) || 'the game');

      return (
        <div className="space-y-4">
          <h3 className="text-lg font-semibold text-white">
            {isNetworkMode ? 'Prepare & Install' : 'Install in the Xbox App'}
          </h3>

          {isNetworkMode && selectedNetworkPeer && (
            <div className="bg-green-900/30 border border-green-700 rounded p-3 text-sm text-green-300 flex items-center gap-2">
              <Wifi size={16} />
              Receiving from {selectedNetworkPeer.displayName} ({selectedNetworkPeer.ipAddress})
            </div>
          )}

          {isNetworkMode && (
            <div className="bg-yellow-900/30 border border-yellow-700 rounded p-3 text-sm text-yellow-300 space-y-1">
              <p className="font-semibold">On the sender PC first:</p>
              <p className="text-yellow-200/80 text-xs">
                Open the app on <strong>{selectedNetworkPeer?.displayName || 'the sender PC'}</strong>,
                select this game, click <strong>Share via Xbox Overlay</strong> &gt; <strong>Stream to Peer</strong>,
                and wait until it says &quot;Waiting for receiver&quot;.
              </p>
            </div>
          )}

          <div className="bg-red-900/30 border border-red-700 rounded p-3 text-sm text-red-200 space-y-1">
            <p className="font-semibold flex items-center gap-2">
              <AlertCircle size={16} />
              Required: start &amp; pause the install first
            </p>
            <p className="text-red-200/80 text-xs">
              The overlay cannot apply if there is no paused download in the Xbox app.
              Complete every step below before continuing — the button stays
              disabled until you confirm.
            </p>
          </div>

          <ol className="text-gray-300 text-sm list-decimal list-inside space-y-2">
            {isNetworkMode && (
              <li>Make sure the sender shows <strong>&quot;Waiting for receiver&quot;</strong>.</li>
            )}
            <li>Open the Xbox app on this PC.</li>
            <li>
              Find <strong>{gameName}</strong> and
              click <strong>Install</strong>.
            </li>
            <li>Wait ~10 seconds for the download to start, then click <strong>Pause</strong>.</li>
            <li>Leave the Xbox app open and click the button below.</li>
          </ol>

          {isNetworkMode && (
            <div className="space-y-2 border-t border-gray-700 pt-3">
              <label className="block text-xs text-gray-400">
                Xbox install drive (optional)
              </label>
              <div className="flex gap-2">
                <input
                  type="text"
                  value={xboxRootPath}
                  onChange={(e) => {
                    updateState({ xboxRootPath: e.target.value });
                    sendCommand('SetXboxPath', { xboxRootPath: e.target.value });
                  }}
                  placeholder="e.g. D:\XboxGames - leave blank to auto-detect"
                  className="flex-1 bg-dark-item border border-gray-600 rounded px-3 py-2 text-sm text-white placeholder-gray-500"
                />
                <button
                  onClick={() => sendCommand('BrowseXboxRoot')}
                  className="bg-gray-700 hover:bg-gray-600 text-white px-3 py-2 rounded transition"
                  title="Browse for the Xbox install folder"
                >
                  <FolderOpen size={18} />
                </button>
              </div>
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
          )}

          <label className="flex items-start gap-2 text-sm text-gray-200 cursor-pointer bg-gray-800/60 border border-gray-700 rounded p-3">
            <input
              type="checkbox"
              checked={acknowledgedPause}
              onChange={(e) => setAcknowledgedPause(e.target.checked)}
              className="mt-0.5 accent-blue-500"
            />
            <span>
              I have clicked <strong>Install</strong> on <strong>{gameName}</strong> in
              the Xbox app and the download is currently <strong>paused</strong>.
            </span>
          </label>

          <button
            onClick={isNetworkMode ? handleStartNetworkReceiver : handleStartReceiver}
            disabled={!acknowledgedPause}
            className="w-full bg-blue-600 hover:bg-blue-500 disabled:opacity-40 disabled:cursor-not-allowed text-white py-2 rounded font-semibold transition flex items-center justify-center gap-2"
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
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50 p-6">
      <div className="bg-[#1f1f2e] border border-gray-700 rounded-lg shadow-xl w-full max-w-md flex flex-col max-h-full overflow-hidden">
        <div className="flex justify-between items-center p-4 border-b border-gray-700">
          <h2 className="text-xl font-semibold text-white">
            {mode === 'sender' ? 'Share Xbox Game' : 'Receive Xbox Game'}
          </h2>
          <button onClick={onClose} className="text-gray-400 hover:text-white transition">
            <X size={20} />
          </button>
        </div>

        <div className="p-6 overflow-y-auto flex-1 min-h-0">
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
