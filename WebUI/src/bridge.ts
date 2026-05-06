/**
 * Bridge for communicating with the C# backend via WebView messaging
 */

declare global {
  interface Window {
    chrome: {
      webview: {
        postMessage: (message: string) => void;
      };
    };
  }
}

export type CommandName =
  | 'ScanLocalGames'
  | 'StartNetwork'
  | 'StopNetwork'
  | 'ScanForPeers'
  | 'ConfigureFirewall'
  | 'CopyLocalIp'
  | 'ConnectManualIp'
  | 'TestConnection'
  | 'RefreshPeers'
  | 'CopyPeerIp'
  | 'StartSync'
  | 'DownloadNewGame'
  | 'PauseTransfer'
  | 'StopTransfer'
  | 'ToggleSpeedUnit'
  | 'AddAllUpdatesToQueue'
  | 'ResumeTransfer'
  | 'AddAllIncompleteToQueue'
  | 'DeleteIncompleteTransfer'
  | 'StartQueue'
  | 'PauseQueue'
  | 'ClearQueue'
  | 'RetryFailedAndPaused'
  | 'RetryQueueItem'
  | 'MoveQueueItemUp'
  | 'MoveQueueItemDown'
  | 'RemoveFromQueue'
  | 'SelectLocalGame'
  | 'SelectPeer'
  | 'SelectSyncItem'
  | 'SelectPeerGame'
  | 'SelectIncompleteTransfer'
  | 'OpenGameFolder'
  | 'ToggleGameVisibility'
  | 'OpenSettings'
  | 'SaveSettings'
  | 'BrowseEpicFolder'
  | 'UnhideAllGames'
  | 'ToggleHighSpeedMode'
  | 'ToggleLog'
  | 'ClearLog'
  | 'ShowTroubleshoot';

export interface CommandPayload {
  [key: string]: any;
}

export function sendCommand(cmd: CommandName, payload?: CommandPayload): void {
  try {
    if (window.chrome?.webview) {
      const message = JSON.stringify({ cmd, payload });
      window.chrome.webview.postMessage(message);
    } else {
      console.warn('WebView bridge not available');
    }
  } catch (error) {
    console.error('Failed to send command:', error);
  }
}
