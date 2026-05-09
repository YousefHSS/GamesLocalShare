import { useEffect, useState } from 'react';
import { X, Settings as SettingsIcon, FolderOpen, RotateCcw, HardDrive, Trash2 } from 'lucide-react';
import { useAppState } from '../store';
import { sendCommand } from '../bridge';
import type { AppSettingsForm, SettingsPayload } from '../store';

const DEFAULTS: AppSettingsForm = {
  autoStartNetwork: false,
  autoUpdateGames: false,
  autoResumeDownloads: false,
  autoUpdateCheckInterval: 30,
  startWithWindows: false,
  minimizeToTray: false,
  epicInstallRoot: '',
};

export default function SettingsModal({
  payload,
  onClose,
}: {
  payload: SettingsPayload;
  onClose: () => void;
}) {
  const s = useAppState();
  const [form, setForm] = useState<AppSettingsForm>(payload.settings);
  const [pendingLibraryPath, setPendingLibraryPath] = useState<string | null>(null);
  const [pendingLibraryName, setPendingLibraryName] = useState('');

  useEffect(() => {
    const handler = (path: string) => {
      setForm(prev => ({ ...prev, epicInstallRoot: path }));
    };
    const prev = (window as any).__epicBrowseResult;
    (window as any).__epicBrowseResult = handler;
    return () => {
      if ((window as any).__epicBrowseResult === handler) {
        (window as any).__epicBrowseResult = prev;
      }
    };
  }, []);

  useEffect(() => {
    const handler = (path: string) => {
      setPendingLibraryPath(path);
      setPendingLibraryName(path.split(/[\\/]/).filter(Boolean).pop() ?? path);
    };
    const prev = (window as any).__driveBrowseResult;
    (window as any).__driveBrowseResult = handler;
    return () => {
      if ((window as any).__driveBrowseResult === handler) {
        (window as any).__driveBrowseResult = prev;
      }
    };
  }, []);

  const confirmAddLibrary = () => {
    if (!pendingLibraryPath) return;
    sendCommand('AddExternalLibrary', { rootPath: pendingLibraryPath, displayName: pendingLibraryName });
    setPendingLibraryPath(null);
    setPendingLibraryName('');
  };

  const cancelAddLibrary = () => {
    setPendingLibraryPath(null);
    setPendingLibraryName('');
  };

  // Re-sync if payload changes (e.g. after UnhideAllGames pushes a fresh snapshot)
  useEffect(() => {
    setForm(payload.settings);
  }, [payload]);

  const set = <K extends keyof AppSettingsForm>(key: K, value: AppSettingsForm[K]) =>
    setForm(prev => ({ ...prev, [key]: value }));

  const save = () => {
    sendCommand('SaveSettings', { ...form });
    onClose();
  };

  const reset = () => setForm(DEFAULTS);

  const onBackdrop = (e: React.MouseEvent) => {
    if (e.target === e.currentTarget) onClose();
  };

  return (
    <div
      className="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4 animate-fade-in"
      onClick={onBackdrop}
    >
      <div className="bg-slate-900 border border-slate-700 rounded-xl shadow-2xl w-full max-w-2xl max-h-[90vh] flex flex-col overflow-hidden animate-scale-in">
        {/* Header */}
        <div className="bg-gradient-to-r from-blue-600 to-purple-600 px-5 py-4 flex items-center justify-between flex-shrink-0">
          <div className="flex items-center gap-3">
            <SettingsIcon className="w-6 h-6 text-white" />
            <h2 className="text-lg font-bold text-white">Application Settings</h2>
          </div>
          <button onClick={onClose} className="text-white/80 hover:text-white" title="Close">
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Body */}
        <div className="flex-1 overflow-auto p-5 space-y-5">
          {/* General */}
          <Section title="General">
            <Toggle
              label="Auto-start network on application startup"
              hint="Automatically start network discovery when the app launches"
              checked={form.autoStartNetwork}
              onChange={v => set('autoStartNetwork', v)}
            />
            <Toggle
              label="Start application with Windows"
              hint="Auto-start the application when Windows starts"
              checked={form.startWithWindows}
              onChange={v => set('startWithWindows', v)}
              disabled={!payload.isWindows}
            />
            <Toggle
              label="Minimize to system tray instead of closing"
              hint="X button minimizes to the tray instead of exiting"
              checked={form.minimizeToTray}
              onChange={v => set('minimizeToTray', v)}
            />
          </Section>

          {/* Auto-Update */}
          <Section title="Auto-Update">
            <Toggle
              label="Automatically download game updates from peers"
              checked={form.autoUpdateGames}
              onChange={v => set('autoUpdateGames', v)}
            />
            <Toggle
              label="Auto-resume incomplete downloads on startup"
              checked={form.autoResumeDownloads}
              onChange={v => set('autoResumeDownloads', v)}
            />
            <div className="space-y-1.5">
              <label className="text-sm text-slate-200">Check for updates every</label>
              <div className="flex items-center gap-2">
                <input
                  type="number"
                  min={5}
                  max={1440}
                  step={5}
                  value={form.autoUpdateCheckInterval}
                  onChange={e => {
                    const n = parseInt(e.target.value, 10);
                    if (!isNaN(n)) set('autoUpdateCheckInterval', n);
                  }}
                  className="w-32 bg-slate-800 border border-slate-700 rounded px-3 py-1.5 text-sm text-white focus:outline-none focus:border-blue-500"
                />
                <span className="text-xs text-slate-400">minutes (5-1440)</span>
              </div>
            </div>
          </Section>

          {/* Game Stores */}
          <Section title="Game Stores">
            <div className="space-y-1.5">
              <label className="text-sm text-slate-200">Epic Games install folder</label>
              <p className="text-xs text-slate-500">
                Where transferred Epic Games titles will be installed. Leave blank to auto-detect from existing Epic installs.
              </p>
              <div className="flex gap-2">
                <input
                  type="text"
                  value={form.epicInstallRoot}
                  onChange={e => set('epicInstallRoot', e.target.value)}
                  placeholder="e.g. D:\Epic Games"
                  className="flex-1 bg-slate-800 border border-slate-700 rounded px-3 py-1.5 text-sm text-white focus:outline-none focus:border-blue-500"
                />
                <button
                  onClick={() => sendCommand('BrowseEpicFolder')}
                  className="px-3 py-1.5 bg-slate-700 hover:bg-slate-600 text-white rounded text-sm flex items-center gap-1.5"
                >
                  <FolderOpen className="w-3.5 h-3.5" /> Browse...
                </button>
              </div>
            </div>
          </Section>

          {/* External Drive Libraries */}
          <Section title="External Drive Libraries">
            <p className="text-xs text-slate-400">
              Add folders on external drives to scan for games. These are shown in the Drives panel.
            </p>

            {/* Configured libraries */}
            {s.externalLibraries && s.externalLibraries.length > 0 ? (
              <div className="space-y-1.5 max-h-40 overflow-y-auto pr-1">
                {s.externalLibraries.map(lib => (
                  <div key={lib.id} className="flex items-center justify-between bg-slate-800/60 border border-slate-700 rounded px-3 py-2">
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-1.5">
                        <HardDrive className="w-3.5 h-3.5 text-blue-400 flex-shrink-0" />
                        <span className="text-sm text-white truncate">{lib.displayName}</span>
                      </div>
                      <p className="text-[10px] text-slate-500 font-mono truncate mt-0.5">{lib.rootPath}</p>
                    </div>
                    <button
                      onClick={() => sendCommand('RemoveExternalLibrary', { id: lib.id })}
                      className="ml-2 p-1 hover:bg-red-900/40 rounded text-slate-400 hover:text-red-400"
                      title="Remove library"
                    >
                      <Trash2 className="w-3.5 h-3.5" />
                    </button>
                  </div>
                ))}
              </div>
            ) : (
              <p className="text-xs text-slate-500 italic">No external libraries configured.</p>
            )}

            {/* Pending confirmation after browse */}
            {pendingLibraryPath && (
              <div className="bg-blue-900/20 border border-blue-700/50 rounded p-3 space-y-2 animate-fade-in-up">
                <p className="text-xs text-blue-300">Selected folder: <span className="font-mono">{pendingLibraryPath}</span></p>
                <div className="space-y-1">
                  <label className="text-xs text-slate-300">Display name</label>
                  <input
                    type="text"
                    value={pendingLibraryName}
                    onChange={e => setPendingLibraryName(e.target.value)}
                    className="w-full bg-slate-800 border border-slate-700 rounded px-3 py-1.5 text-sm text-white focus:outline-none focus:border-blue-500"
                  />
                </div>
                <div className="flex gap-2">
                  <button
                    onClick={confirmAddLibrary}
                    className="px-3 py-1.5 bg-blue-600 hover:bg-blue-700 text-white text-xs rounded font-medium"
                  >
                    Add Library
                  </button>
                  <button
                    onClick={cancelAddLibrary}
                    className="px-3 py-1.5 bg-slate-700 hover:bg-slate-600 text-white text-xs rounded"
                  >
                    Cancel
                  </button>
                </div>
              </div>
            )}

            <button
              onClick={() => sendCommand('BrowseDriveFolder')}
              className="px-3 py-1.5 bg-slate-700 hover:bg-slate-600 text-white rounded text-sm flex items-center gap-1.5 self-start"
            >
              <FolderOpen className="w-3.5 h-3.5" /> Browse & Add Library...
            </button>
          </Section>

          {/* Hidden Games */}
          <Section title="Hidden Games">
            <p className="text-xs text-slate-400">
              Hidden games are not shared with other computers. Right-click a game in "My Games" to hide/show it.
            </p>
            <div className="flex items-center justify-between">
              <span className="text-sm text-slate-200">
                Currently hidden:{' '}
                <span className="text-blue-400 font-semibold">
                  {payload.hiddenGames.length === 1 ? '1 game' : `${payload.hiddenGames.length} games`}
                </span>
              </span>
              <button
                onClick={() => sendCommand('UnhideAllGames')}
                disabled={payload.hiddenGames.length === 0}
                className="px-3 py-1.5 bg-slate-700 hover:bg-slate-600 disabled:opacity-40 disabled:cursor-not-allowed text-white rounded text-xs"
              >
                Show all
              </button>
            </div>
            {payload.hiddenGames.length > 0 && (
              <div className="bg-slate-800/60 border border-slate-700 rounded p-2 max-h-32 overflow-auto">
                {payload.hiddenGames.map(g => (
                  <div key={g.appId} className="text-xs text-slate-400 py-0.5 truncate">
                    {g.name}
                  </div>
                ))}
              </div>
            )}
          </Section>

          {/* About */}
          <Section title="About">
            <div className="text-sm text-white">Games Local Share</div>
            <div className="text-xs text-slate-400">Version 1.0.0 · Share & sync games over LAN</div>
            <div className="text-[10px] text-slate-500 font-mono break-all mt-1">
              {payload.settingsPath}
            </div>
          </Section>
        </div>

        {/* Footer */}
        <div className="border-t border-slate-700 px-5 py-3 flex items-center justify-between flex-shrink-0 bg-slate-900/80">
          <button
            onClick={reset}
            className="px-3 py-1.5 bg-slate-700 hover:bg-slate-600 text-white text-sm rounded flex items-center gap-1.5"
          >
            <RotateCcw className="w-3.5 h-3.5" /> Reset to defaults
          </button>
          <div className="flex gap-2">
            <button
              onClick={onClose}
              className="btn-press px-4 py-1.5 bg-slate-700 hover:bg-slate-600 text-white text-sm rounded"
            >
              Cancel
            </button>
            <button
              onClick={save}
              className="btn-press px-5 py-1.5 bg-blue-600 hover:bg-blue-700 text-white text-sm rounded font-medium"
            >
              Save
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="bg-slate-800/40 border border-slate-700/50 rounded-lg p-4 space-y-3 animate-fade-in-up">
      <h3 className="text-sm font-bold text-blue-400">{title}</h3>
      {children}
    </div>
  );
}

function Toggle({
  label,
  hint,
  checked,
  onChange,
  disabled,
}: {
  label: string;
  hint?: string;
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
}) {
  return (
    <label className={`flex items-start gap-2.5 ${disabled ? 'opacity-50' : 'cursor-pointer'}`}>
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={e => onChange(e.target.checked)}
        className="mt-0.5 w-4 h-4 accent-blue-500"
      />
      <div className="min-w-0 flex-1">
        <div className="text-sm text-white">{label}</div>
        {hint && <div className="text-xs text-slate-500 mt-0.5">{hint}</div>}
      </div>
    </label>
  );
}
