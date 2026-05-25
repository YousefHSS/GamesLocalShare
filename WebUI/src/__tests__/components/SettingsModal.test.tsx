import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import SettingsModal from '../../components/SettingsModal';
import type { SettingsPayload } from '../../store';
import { useAppState } from '../../store';

const basePayload: SettingsPayload = {
  settings: {
    autoStartNetwork: false,
    autoUpdateGames: false,
    autoResumeDownloads: false,
    autoUpdateCheckInterval: 30,
    startWithWindows: false,
    minimizeToTray: false,
    epicInstallRoot: '',
    xboxRootPath: '',
  },
  hiddenGames: [],
  externalLibraries: [],
  isWindows: true,
  settingsPath: 'C:\\Users\\Test\\AppData\\Roaming\\GamesLocalShare\\settings.json',
};

let postMessageMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  postMessageMock = vi.fn();
  (window as any).chrome = { webview: { postMessage: postMessageMock } };
  useAppState.getState().reset();
});

afterEach(() => {
  delete (window as any).__epicBrowseResult;
  delete (window as any).__driveBrowseResult;
});

function lastCommand() {
  const arg = postMessageMock.mock.calls[postMessageMock.mock.calls.length - 1][0] as string;
  return JSON.parse(arg) as { cmd: string; payload?: any };
}

describe('SettingsModal', () => {
  it('renders the settings sections', () => {
    render(<SettingsModal payload={basePayload} onClose={() => {}} />);
    expect(screen.getByText('Application Settings')).toBeInTheDocument();
    expect(screen.getByText('General')).toBeInTheDocument();
    expect(screen.getByText('Auto-Update')).toBeInTheDocument();
    expect(screen.getByText('Game Stores')).toBeInTheDocument();
    expect(screen.getByText('External Drive Libraries')).toBeInTheDocument();
    expect(screen.getByText('Hidden Games')).toBeInTheDocument();
  });

  it('clicking Save sends the SaveSettings command with the form values', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<SettingsModal payload={basePayload} onClose={onClose} />);

    const checkbox = screen.getByLabelText(/Auto-start network/i, { selector: 'input' });
    await user.click(checkbox);

    await user.click(screen.getByRole('button', { name: 'Save' }));

    const cmd = lastCommand();
    expect(cmd.cmd).toBe('SaveSettings');
    expect(cmd.payload.autoStartNetwork).toBe(true);
    expect(onClose).toHaveBeenCalled();
  });

  it('Cancel does not send SaveSettings and calls onClose', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<SettingsModal payload={basePayload} onClose={onClose} />);

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(onClose).toHaveBeenCalled();
    expect(postMessageMock).not.toHaveBeenCalled();
  });

  it('Reset to defaults restores DEFAULTS', async () => {
    const user = userEvent.setup();
    const payload: SettingsPayload = {
      ...basePayload,
      settings: { ...basePayload.settings, autoStartNetwork: true, autoUpdateCheckInterval: 90 },
    };
    render(<SettingsModal payload={payload} onClose={() => {}} />);

    const checkbox = screen.getByLabelText(/Auto-start network/i, { selector: 'input' }) as HTMLInputElement;
    expect(checkbox.checked).toBe(true);

    await user.click(screen.getByRole('button', { name: /Reset to defaults/i }));

    expect(checkbox.checked).toBe(false);
    const intervalInput = screen.getByDisplayValue('30') as HTMLInputElement;
    expect(intervalInput.value).toBe('30');
  });

  it('Browse Epic folder triggers BrowseEpicFolder', async () => {
    const user = userEvent.setup();
    render(<SettingsModal payload={basePayload} onClose={() => {}} />);

    await user.click(screen.getByRole('button', { name: /Browse\.\.\./i }));
    expect(lastCommand().cmd).toBe('BrowseEpicFolder');
  });

  it('Epic browse result populates the install root field', async () => {
    render(<SettingsModal payload={basePayload} onClose={() => {}} />);
    const callback = (window as any).__epicBrowseResult as (path: string) => void;
    expect(typeof callback).toBe('function');

    act(() => {
      callback('D:\\Epic Games');
    });

    // The input is identified by its placeholder; locate it by its current value after the update.
    const input = await screen.findByDisplayValue('D:\\Epic Games');
    expect((input as HTMLInputElement).value).toBe('D:\\Epic Games');
  });

  it('Browse drive folder shows the pending-add UI; confirm dispatches AddExternalLibrary', async () => {
    const user = userEvent.setup();
    render(<SettingsModal payload={basePayload} onClose={() => {}} />);

    await user.click(screen.getByRole('button', { name: /Browse & Add Library/i }));
    expect(lastCommand().cmd).toBe('BrowseDriveFolder');

    const callback = (window as any).__driveBrowseResult as (path: string) => void;
    act(() => {
      callback('E:\\GameVault');
    });

    // The chosen path lives inside a span next to "Selected folder:". Find it via display value
    // of the editable display-name field that auto-fills from the path's last segment.
    expect(await screen.findByDisplayValue('GameVault')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /^Add Library$/ }));

    const cmd = lastCommand();
    expect(cmd.cmd).toBe('AddExternalLibrary');
    expect(cmd.payload).toMatchObject({ rootPath: 'E:\\GameVault', displayName: 'GameVault' });
  });

  it('Configured external libraries can be removed', async () => {
    const user = userEvent.setup();
    useAppState.getState().updateState({
      externalLibraries: [
        {
          id: 'lib-1',
          displayName: 'My Drive',
          rootPath: 'F:\\Games',
          driveSerial: 'F:',
          isRemovable: true,
          scanSubfolders: true,
        },
      ],
    });
    render(<SettingsModal payload={basePayload} onClose={() => {}} />);

    expect(screen.getByText('My Drive')).toBeInTheDocument();
    await user.click(screen.getByTitle('Remove library'));

    const cmd = lastCommand();
    expect(cmd.cmd).toBe('RemoveExternalLibrary');
    expect(cmd.payload).toEqual({ id: 'lib-1' });
  });

  it('Hidden games count and Show all button are wired to UnhideAllGames', async () => {
    const user = userEvent.setup();
    const payload: SettingsPayload = {
      ...basePayload,
      hiddenGames: [
        { appId: '570', name: 'Dota 2' },
        { appId: '440', name: 'TF2' },
      ],
    };
    render(<SettingsModal payload={payload} onClose={() => {}} />);

    expect(screen.getByText(/2 games/)).toBeInTheDocument();
    expect(screen.getByText('Dota 2')).toBeInTheDocument();
    expect(screen.getByText('TF2')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Show all/i }));
    expect(lastCommand().cmd).toBe('UnhideAllGames');
  });

  it('startWithWindows toggle is disabled on non-Windows', () => {
    const payload: SettingsPayload = { ...basePayload, isWindows: false };
    render(<SettingsModal payload={payload} onClose={() => {}} />);
    const cb = screen.getByLabelText(/Start application with Windows/i, { selector: 'input' }) as HTMLInputElement;
    expect(cb.disabled).toBe(true);
  });

  it('clicking the backdrop closes the modal', () => {
    const onClose = vi.fn();
    const { container } = render(<SettingsModal payload={basePayload} onClose={onClose} />);
    const backdrop = container.firstChild as HTMLElement;
    fireEvent.click(backdrop);
    expect(onClose).toHaveBeenCalled();
  });
});
