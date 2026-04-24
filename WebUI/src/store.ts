import { create } from 'zustand';

export interface GameInfo {
  appId: string;
  name: string;
  installPath: string;
  sizeOnDisk: number;
  formattedSize: string;
  buildId: string;
  platform: 'Steam' | 'EpicGames' | 'Xbox';
  isInstalled: boolean;
  isAvailableFromPeer: boolean;
  isHidden: boolean;
  lastUpdated: string;
}

export interface NetworkPeer {
  peerId: string;
  displayName: string;
  ipAddress: string;
  port: number;
  fileTransferPort: number;
  games: GameInfo[];
  lastSeen: string;
  isOnline: boolean;
}

export interface GameSyncInfo {
  localGame: GameInfo | null;
  remoteGame: GameInfo;
  remotePeer: NetworkPeer;
  isNewDownload: boolean;
  localIsOlder: boolean;
  status: string;
  progress: number;
  transferSpeed: number;
  displayName: string;
  syncDescription: string;
}

export interface TransferState {
  gameAppId: string;
  gameName: string;
  targetPath: string;
  sourcePeerIp: string;
  sourcePeerName: string;
  buildId: string;
  totalBytes: number;
  transferredBytes: number;
  progressPercent: number;
  formattedProgress: string;
  completedFiles: string[];
  pendingFiles: any[];
  startedAt: string;
  lastUpdated: string;
  isNewDownload: boolean;
}

export interface DownloadQueueItem {
  type: string;
  gameName: string;
  gameAppId: string;
  sourcePeerName: string;
  totalBytes: number;
  downloadedBytes: number;
  status: string | number;
  progress: number;
  statusText: string;
  statusColor: string;
  typeIcon: string;
  formattedSize: string;
  formattedProgress: string;
  syncInfo?: GameSyncInfo;
  transferState?: TransferState;
}

export interface LogMessage {
  timestamp: string;
  formattedTime: string;
  message: string;
  type: string;
  typeColor: string;
  typeIcon: string;
}

export interface AppState {
  // Scalar properties
  statusMessage: string;
  isScanning: boolean;
  isNetworkActive: boolean;
  isScanningPeers: boolean;
  localIpAddress: string;
  manualPeerIp: string;
  isTransferring: boolean;
  firewallConfigured: boolean;
  isAdmin: boolean;
  isWindows: boolean;
  highSpeedMode: boolean;
  isLogVisible: boolean;
  isQueueProcessing: boolean;
  showSpeedInMbps: boolean;
  lastError: string;

  // Transfer progress
  currentTransferGameName: string;
  currentTransferProgress: number;
  currentTransferFile: string;
  currentTransferSpeed: string;
  currentTransferTimeRemaining: string;
  currentTransferTotalBytes: number;
  currentTransferDownloadedBytes: number;
  currentTransferFormattedProgress: string;

  // Collections
  localGames: GameInfo[];
  networkPeers: NetworkPeer[];
  availableSyncs: GameSyncInfo[];
  availableFromPeers: GameInfo[];
  incompleteTransfers: TransferState[];
  downloadQueue: DownloadQueueItem[];
  logMessages: LogMessage[];

  // Selections
  selectedLocalGame: GameInfo | null;
  selectedPeer: NetworkPeer | null;
  selectedSyncItem: GameSyncInfo | null;
  selectedPeerGame: GameInfo | null;
  selectedIncompleteTransfer: TransferState | null;
  currentQueueItem: DownloadQueueItem | null;

  // Actions
  updateState: (patch: Partial<AppState>) => void;
  reset: () => void;
}

const initialState: Omit<AppState, 'updateState' | 'reset'> = {
  statusMessage: 'Ready',
  isScanning: false,
  isNetworkActive: false,
  isScanningPeers: false,
  localIpAddress: '',
  manualPeerIp: '',
  isTransferring: false,
  firewallConfigured: false,
  isAdmin: false,
  isWindows: true,
  highSpeedMode: false,
  isLogVisible: false,
  isQueueProcessing: false,
  showSpeedInMbps: false,
  lastError: '',

  currentTransferGameName: '',
  currentTransferProgress: 0,
  currentTransferFile: '',
  currentTransferSpeed: '',
  currentTransferTimeRemaining: '',
  currentTransferTotalBytes: 0,
  currentTransferDownloadedBytes: 0,
  currentTransferFormattedProgress: '',

  localGames: [],
  networkPeers: [],
  availableSyncs: [],
  availableFromPeers: [],
  incompleteTransfers: [],
  downloadQueue: [],
  logMessages: [],

  selectedLocalGame: null,
  selectedPeer: null,
  selectedSyncItem: null,
  selectedPeerGame: null,
  selectedIncompleteTransfer: null,
  currentQueueItem: null,
};

export const useAppState = create<AppState>((set) => ({
  ...initialState,
  updateState: (patch) => set((state) => ({ ...state, ...patch })),
  reset: () => set(initialState),
}));

// Setup C# → JS global functions
declare global {
  function __initState(state: AppState): void;
  function __updateState(patch: Partial<AppState>): void;
}

(window as any).__initState = (state: AppState) => {
  useAppState.setState(state);
};

(window as any).__updateState = (patch: Partial<AppState>) => {
  useAppState.setState(patch);
};
