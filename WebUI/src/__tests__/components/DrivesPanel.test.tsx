import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import DrivesPanel from '../../components/DrivesPanel';
import {
  useAppState,
  type ExternalLibrary,
  type CrossLocationGame,
  type GameInfo,
} from '../../store';

const localGame: GameInfo = {
  appId: 'sample',
  name: 'Sample',
  installPath: 'C:/games/sample',
  sizeOnDisk: 1024,
  formattedSize: '1 KB',
  buildId: 'v1',
  platform: 'Steam',
  isInstalled: true,
  isAvailableFromPeer: false,
  isHidden: false,
  lastUpdated: '2025-01-01',
};

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

const lib1: ExternalLibrary = {
  id: 'lib-1',
  displayName: 'External SSD',
  rootPath: 'E:\\Games',
  driveSerial: 'E:',
  isRemovable: true,
  scanSubfolders: true,
};

describe('DrivesPanel', () => {
  it('renders an empty state when there are no libraries', () => {
    render(<DrivesPanel />);
    expect(screen.getByText(/No external libraries configured/i)).toBeInTheDocument();
    // Compare button is disabled while there are no libraries
    expect(screen.getByRole('button', { name: /Compare Locations/i })).toBeDisabled();
  });

  it('shows Connected badge when a drive matching the serial is plugged in', () => {
    useAppState.getState().updateState({
      externalLibraries: [lib1],
      drives: [
        { driveLetter: 'E:\\', volumeLabel: 'Vault', serial: 'E:', isRemovable: true, isAvailable: true },
      ],
    });
    render(<DrivesPanel />);
    expect(screen.getByText('External SSD')).toBeInTheDocument();
    expect(screen.getByText('Connected')).toBeInTheDocument();
  });

  it('shows Disconnected when the drive is not in the current drives list', () => {
    useAppState.getState().updateState({
      externalLibraries: [lib1],
      drives: [],
    });
    render(<DrivesPanel />);
    expect(screen.getByText('Disconnected')).toBeInTheDocument();
  });

  it('Scan External dispatches ScanExternalLibraries', async () => {
    const user = userEvent.setup();
    render(<DrivesPanel />);
    await user.click(screen.getByRole('button', { name: /Scan External/i }));
    expect(lastCommand().cmd).toBe('ScanExternalLibraries');
  });

  it('Compare Locations is enabled with libraries and dispatches CompareGameLocations', async () => {
    const user = userEvent.setup();
    useAppState.getState().updateState({ externalLibraries: [lib1] });
    render(<DrivesPanel />);

    const compare = screen.getByRole('button', { name: /Compare Locations/i });
    expect(compare).not.toBeDisabled();
    await user.click(compare);
    expect(lastCommand().cmd).toBe('CompareGameLocations');
  });

  it('Add Library button dispatches BrowseDriveFolder', async () => {
    const user = userEvent.setup();
    render(<DrivesPanel />);
    await user.click(screen.getByRole('button', { name: /Add Library/i }));
    expect(lastCommand().cmd).toBe('BrowseDriveFolder');
  });

  it('typing a path and pressing Add dispatches AddExternalLibrary', async () => {
    const user = userEvent.setup();
    render(<DrivesPanel />);

    const input = screen.getByPlaceholderText(/or type a path/i);
    await user.type(input, 'D:\\Steam Library');
    await user.click(screen.getByRole('button', { name: /^Add$/ }));

    const cmd = lastCommand();
    expect(cmd.cmd).toBe('AddExternalLibrary');
    expect(cmd.payload).toMatchObject({
      rootPath: 'D:\\Steam Library',
      displayName: 'Steam Library',
    });
  });

  it('Enter key in the path input also dispatches AddExternalLibrary', async () => {
    const user = userEvent.setup();
    render(<DrivesPanel />);

    const input = screen.getByPlaceholderText(/or type a path/i);
    await user.type(input, 'D:\\Lib{Enter}');

    expect(lastCommand().cmd).toBe('AddExternalLibrary');
  });

  it('renders cross-location games and dispatches StartLocalCopy with direction', async () => {
    const user = userEvent.setup();
    useAppState.getState().updateState({
      externalLibraries: [lib1],
      drives: [{ driveLetter: 'E:\\', volumeLabel: '', serial: 'E:', isRemovable: true, isAvailable: true }],
    });

    render(<DrivesPanel />);

    // Use a DriveToDevice row so it's visible without flipping the device-only toggle.
    const sample: CrossLocationGame = {
      deviceCopy: null,
      externalCopy: { ...localGame, name: 'Halo', appId: '42' },
      library: lib1,
      direction: 'DriveToDevice',
      displayName: 'Halo',
      appId: '42',
      statusText: 'Newer on drive → copy to device',
      statusColor: 'yellow',
    };
    act(() => {
      (window as any).__crossLocationGamesResult([sample]);
    });

    expect(await screen.findByText('Halo')).toBeInTheDocument();

    await user.click(screen.getByTitle('Copy'));
    const cmd = lastCommand();
    expect(cmd.cmd).toBe('StartLocalCopy');
    expect(cmd.payload).toMatchObject({
      appId: '42',
      libraryId: 'lib-1',
      direction: 'DriveToDevice',
    });
  });

  it('hides OnlyOnDevice rows by default and shows them after toggling the checkbox', async () => {
    const user = userEvent.setup();
    useAppState.getState().updateState({ externalLibraries: [lib1] });
    render(<DrivesPanel />);

    const sample: CrossLocationGame = {
      deviceCopy: { ...localGame, name: 'OnlyHere', appId: '99' },
      externalCopy: null,
      library: lib1,
      direction: 'OnlyOnDevice',
      displayName: 'OnlyHere',
      appId: '99',
      statusText: 'Only on device',
      statusColor: 'slate',
    };
    act(() => {
      (window as any).__crossLocationGamesResult([sample]);
    });

    // Initially hidden — message explains why
    expect(screen.queryByText('OnlyHere')).toBeNull();
    expect(
      screen.getByText(/1 device-only game hidden/i),
    ).toBeInTheDocument();

    // Counter on the checkbox label
    expect(screen.getByRole('checkbox', { name: /Show device-only games/i })).toBeInTheDocument();
    expect(screen.getByText('(1)')).toBeInTheDocument();

    // Toggle on → row appears
    await user.click(screen.getByRole('checkbox', { name: /Show device-only games/i }));
    expect(await screen.findByText('OnlyHere')).toBeInTheDocument();

    // Toggle off → hidden again
    await user.click(screen.getByRole('checkbox', { name: /Show device-only games/i }));
    expect(screen.queryByText('OnlyHere')).toBeNull();
  });

  it('keeps non-device-only directions visible regardless of the toggle', () => {
    useAppState.getState().updateState({ externalLibraries: [lib1] });
    render(<DrivesPanel />);

    act(() => {
      (window as any).__crossLocationGamesResult([
        {
          deviceCopy: { ...localGame, appId: '1' },
          externalCopy: { ...localGame, appId: '1' },
          library: lib1,
          direction: 'InSync',
          displayName: 'Matched',
          appId: '1',
          statusText: 'In sync',
          statusColor: 'green',
        },
        {
          deviceCopy: null,
          externalCopy: { ...localGame, appId: '2' },
          library: lib1,
          direction: 'OnlyOnDrive',
          displayName: 'DriveOnly',
          appId: '2',
          statusText: 'Only on drive',
          statusColor: 'slate',
        },
      ] satisfies CrossLocationGame[]);
    });

    expect(screen.getByText('Matched')).toBeInTheDocument();
    expect(screen.getByText('DriveOnly')).toBeInTheDocument();
  });

  it('renders the platform icon next to each game name', () => {
    useAppState.getState().updateState({ externalLibraries: [lib1] });
    render(<DrivesPanel />);

    act(() => {
      (window as any).__crossLocationGamesResult([
        {
          deviceCopy: { ...localGame, appId: '440', name: 'TF2', platform: 'Steam' },
          externalCopy: { ...localGame, appId: '440', name: 'TF2', platform: 'Steam' },
          library: lib1,
          direction: 'InSync',
          displayName: 'TF2',
          appId: '440',
          statusText: 'In sync',
          statusColor: 'green',
        },
        {
          deviceCopy: null,
          externalCopy: { ...localGame, appId: 'fortnite', name: 'Fortnite', platform: 'EpicGames' },
          library: lib1,
          direction: 'OnlyOnDrive',
          displayName: 'Fortnite',
          appId: 'fortnite',
          statusText: 'Only on drive',
          statusColor: 'slate',
        },
      ] satisfies CrossLocationGame[]);
    });

    // Steam SVG and Epic "E" badge should both be in the rendered list
    expect(screen.getByLabelText('Steam')).toBeInTheDocument();
    expect(screen.getByLabelText('Epic Games')).toBeInTheDocument();
  });

  it('does not show a Copy button for InSync rows', () => {
    useAppState.getState().updateState({ externalLibraries: [lib1] });
    render(<DrivesPanel />);

    act(() => {
      (window as any).__crossLocationGamesResult([
        {
          deviceCopy: null,
          externalCopy: null,
          library: lib1,
          direction: 'InSync',
          displayName: 'TF2',
          appId: '440',
          statusText: 'In sync',
          statusColor: 'green',
        } satisfies CrossLocationGame,
      ]);
    });

    expect(screen.queryByTitle('Copy')).toBeNull();
  });
});
