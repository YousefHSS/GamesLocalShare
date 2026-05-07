import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from '../App';
import { useAppState, type GameInfo } from '../store';

let postMessageMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  postMessageMock = vi.fn();
  (window as any).chrome = { webview: { postMessage: postMessageMock } };
  useAppState.getState().reset();
});

function lastCommand() {
  const last = postMessageMock.mock.calls[postMessageMock.mock.calls.length - 1][0] as string;
  return JSON.parse(last) as { cmd: string; payload?: any };
}

const game = (id: string, overrides: Partial<GameInfo> = {}): GameInfo => ({
  appId: id,
  name: `Game ${id}`,
  installPath: `C:/games/${id}`,
  sizeOnDisk: 1024 * 1024,
  formattedSize: '1 MB',
  buildId: 'v1',
  platform: 'Steam',
  isInstalled: true,
  isAvailableFromPeer: false,
  isHidden: false,
  lastUpdated: '2025-01-01',
  ...overrides,
});

describe('App – top-level smoke', () => {
  it('renders the four primary panels', () => {
    render(<App />);
    expect(screen.getByText('My Games')).toBeInTheDocument();
    expect(screen.getByText('Network Peers')).toBeInTheDocument();
    expect(screen.getByText('Updates Available')).toBeInTheDocument();
    expect(screen.getByText('Transfers')).toBeInTheDocument();
  });

  it('Step 1 button dispatches ScanLocalGames', async () => {
    const user = userEvent.setup();
    render(<App />);
    await user.click(screen.getByRole('button', { name: /Scan My Games/i }));
    expect(lastCommand().cmd).toBe('ScanLocalGames');
  });

  it('Step 2 button toggles between StartNetwork and StopNetwork', async () => {
    const user = userEvent.setup();
    render(<App />);

    await user.click(screen.getByRole('button', { name: /Start Network/i }));
    expect(lastCommand().cmd).toBe('StartNetwork');

    act(() => {
      useAppState.getState().updateState({ isNetworkActive: true });
    });

    await user.click(await screen.findByRole('button', { name: /Stop Network/i }));
    expect(lastCommand().cmd).toBe('StopNetwork');
  });

  it('Step 3 (Scan for Peers) is disabled until network is active', async () => {
    const user = userEvent.setup();
    render(<App />);
    const peerScan = screen.getByRole('button', { name: /Scan for Peers/i });
    expect(peerScan).toBeDisabled();

    act(() => {
      useAppState.getState().updateState({ isNetworkActive: true });
    });

    const enabled = await screen.findByRole('button', { name: /Scan for Peers/i });
    expect(enabled).not.toBeDisabled();

    await user.click(enabled);
    expect(lastCommand().cmd).toBe('ScanForPeers');
  });

  it('renders local games and filters them by name', async () => {
    const user = userEvent.setup();
    useAppState.getState().updateState({
      localGames: [game('1', { name: 'Halo' }), game('2', { name: 'Stardew Valley' })],
    });
    render(<App />);

    expect(screen.getByText('Halo')).toBeInTheDocument();
    expect(screen.getByText('Stardew Valley')).toBeInTheDocument();

    await user.type(screen.getByPlaceholderText(/Search my games/i), 'halo');
    expect(screen.getByText('Halo')).toBeInTheDocument();
    expect(screen.queryByText('Stardew Valley')).toBeNull();
  });

  it('clicking a local game dispatches SelectLocalGame with appId', async () => {
    const user = userEvent.setup();
    useAppState.getState().updateState({ localGames: [game('1', { name: 'Halo' })] });
    render(<App />);

    await user.click(screen.getByText('Halo'));
    const cmd = lastCommand();
    expect(cmd.cmd).toBe('SelectLocalGame');
    expect(cmd.payload).toEqual({ appId: '1' });
  });

  it('Connect button dispatches ConnectManualIp with the typed IP', async () => {
    const user = userEvent.setup();
    render(<App />);

    await user.type(screen.getByPlaceholderText(/IP address/i), '192.168.1.50');
    await user.click(screen.getByRole('button', { name: /^Connect$/i }));

    expect(lastCommand()).toEqual({
      cmd: 'ConnectManualIp',
      payload: { ip: '192.168.1.50' },
    });
  });

  it('Settings button dispatches OpenSettings', async () => {
    const user = userEvent.setup();
    render(<App />);
    await user.click(screen.getByRole('button', { name: /Settings/i }));
    expect(lastCommand().cmd).toBe('OpenSettings');
  });

  it('Drives button toggles the drives modal', async () => {
    const user = userEvent.setup();
    render(<App />);
    expect(screen.queryByText('External Drives')).toBeNull();
    await user.click(screen.getByRole('button', { name: /^Drives/i }));
    expect(screen.getByText('External Drives')).toBeInTheDocument();
  });

  it('Pause/Stop transfer buttons appear only while transferring', async () => {
    const user = userEvent.setup();
    useAppState.getState().updateState({
      isTransferring: true,
      currentTransferGameName: 'Halo',
      currentTransferProgress: 50,
      currentTransferFormattedProgress: '50.0% (500 MB / 1 GB)',
      currentTransferSpeed: '5 MB/s',
      currentTransferTimeRemaining: '00:01:30',
    });
    render(<App />);

    expect(screen.getByText('Halo')).toBeInTheDocument();
    expect(screen.getByText('5 MB/s', { exact: false })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Pause/i }));
    expect(lastCommand().cmd).toBe('PauseTransfer');
  });

  it('Updates queue – Queue All triggers AddAllUpdatesToQueue', async () => {
    const user = userEvent.setup();
    render(<App />);
    // Two "+Queue All" buttons exist (Updates and Incomplete tabs); the first is for updates.
    const buttons = screen.getAllByRole('button', { name: /\+ Queue All/i });
    expect(buttons.length).toBeGreaterThanOrEqual(1);
    await user.click(buttons[0]);
    expect(lastCommand().cmd).toBe('AddAllUpdatesToQueue');
  });

  it('Switching to the Queue tab shows the queue list', async () => {
    const user = userEvent.setup();
    useAppState.getState().updateState({
      downloadQueue: [
        {
          type: 'NewDownload',
          gameName: 'Halo',
          gameAppId: '42',
          sourcePeerName: 'PEER',
          totalBytes: 1024,
          downloadedBytes: 512,
          status: 'Downloading',
          progress: 50,
          statusText: 'Downloading',
          statusColor: '#3B82F6',
          typeIcon: '⬇️',
          formattedSize: '1 KB',
          formattedProgress: '50.0% (512 B / 1 KB)',
        },
      ],
    });
    render(<App />);

    // The Queue tab button shows a count "Queue (1)" – it's the only one in the Transfers panel
    const queueTab = screen.getByRole('button', { name: /Queue.*1/ });
    await user.click(queueTab);

    expect(screen.getByText('Halo')).toBeInTheDocument();
  });
});
