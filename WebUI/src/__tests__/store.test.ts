import { describe, it, expect, beforeEach } from 'vitest';
import { useAppState, type GameInfo, type NetworkPeer } from '../store';

const makeGame = (id: string, overrides: Partial<GameInfo> = {}): GameInfo => ({
  appId: id,
  name: `Game ${id}`,
  installPath: `C:/games/${id}`,
  sizeOnDisk: 1024,
  formattedSize: '1 KB',
  buildId: 'v1',
  platform: 'Steam',
  isInstalled: true,
  isAvailableFromPeer: false,
  isHidden: false,
  lastUpdated: '2025-01-01',
  ...overrides,
});

describe('useAppState (zustand store)', () => {
  beforeEach(() => {
    useAppState.getState().reset();
  });

  it('exposes sensible defaults', () => {
    const s = useAppState.getState();
    expect(s.statusMessage).toBe('Ready');
    expect(s.isScanning).toBe(false);
    expect(s.isNetworkActive).toBe(false);
    expect(s.localGames).toEqual([]);
    expect(s.networkPeers).toEqual([]);
    expect(s.downloadQueue).toEqual([]);
    expect(s.drives).toEqual([]);
    expect(s.externalLibraries).toEqual([]);
    expect(s.crossLocationGames).toEqual([]);
    expect(s.selectedLocalGame).toBeNull();
    expect(s.selectedPeer).toBeNull();
  });

  it('updateState applies a partial patch', () => {
    useAppState.getState().updateState({ statusMessage: 'Scanning…', isScanning: true });
    const s = useAppState.getState();
    expect(s.statusMessage).toBe('Scanning…');
    expect(s.isScanning).toBe(true);
    // Untouched fields preserved
    expect(s.isNetworkActive).toBe(false);
  });

  it('updateState replaces collection fields wholesale', () => {
    useAppState.getState().updateState({ localGames: [makeGame('1'), makeGame('2')] });
    expect(useAppState.getState().localGames).toHaveLength(2);

    useAppState.getState().updateState({ localGames: [makeGame('3')] });
    expect(useAppState.getState().localGames).toEqual([expect.objectContaining({ appId: '3' })]);
  });

  it('reset returns the store to initial values', () => {
    useAppState.getState().updateState({
      statusMessage: 'Busy',
      isNetworkActive: true,
      localGames: [makeGame('1')],
    });

    useAppState.getState().reset();

    const s = useAppState.getState();
    expect(s.statusMessage).toBe('Ready');
    expect(s.isNetworkActive).toBe(false);
    expect(s.localGames).toEqual([]);
  });

  it('window.__updateState applies a patch from the C# bridge', () => {
    expect(typeof (window as any).__updateState).toBe('function');
    (window as any).__updateState({ localIpAddress: '192.168.1.5' });
    expect(useAppState.getState().localIpAddress).toBe('192.168.1.5');
  });

  it('window.__initState replaces the store completely', () => {
    const peer: NetworkPeer = {
      peerId: 'p1',
      displayName: 'TEST',
      ipAddress: '10.0.0.1',
      port: 45678,
      fileTransferPort: 45679,
      games: [],
      lastSeen: '2025-01-01',
      isOnline: true,
    };
    (window as any).__initState({
      ...useAppState.getState(),
      networkPeers: [peer],
      isNetworkActive: true,
    });
    expect(useAppState.getState().networkPeers).toHaveLength(1);
    expect(useAppState.getState().isNetworkActive).toBe(true);
  });

  it('selections can be set independently', () => {
    const game = makeGame('x');
    useAppState.getState().updateState({ selectedLocalGame: game });
    expect(useAppState.getState().selectedLocalGame).toEqual(game);
  });
});
