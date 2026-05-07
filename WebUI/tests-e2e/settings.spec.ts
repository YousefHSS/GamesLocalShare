import { test, expect } from './fixtures';

const basePayload = {
  settings: {
    autoStartNetwork: false,
    autoUpdateGames: false,
    autoResumeDownloads: false,
    autoUpdateCheckInterval: 30,
    startWithWindows: false,
    minimizeToTray: false,
    epicInstallRoot: '',
  },
  hiddenGames: [{ appId: '570', name: 'Dota 2' }],
  externalLibraries: [],
  isWindows: true,
  settingsPath: 'C:/Users/Test/AppData/Roaming/GamesLocalShare/settings.json',
};

test('clicking Settings dispatches OpenSettings', async ({ page, bridge }) => {
  await page.getByRole('button', { name: /Settings/i }).click();
  expect((await bridge.lastCommand()).cmd).toBe('OpenSettings');
});

test('SettingsModal save flow', async ({ page, bridge }) => {
  await bridge.openSettings(basePayload);

  // Modal opens with sections rendered
  await expect(page.getByRole('heading', { name: 'Application Settings' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Hidden Games' })).toBeVisible();

  // Toggle "Auto-start network"
  await page.getByText('Auto-start network on application startup').click();

  // Save
  await page.getByRole('button', { name: 'Save' }).click();

  const last = await bridge.lastCommand();
  expect(last.cmd).toBe('SaveSettings');
  expect((last.payload as Record<string, unknown>).autoStartNetwork).toBe(true);
});

test('Cancel closes the modal without dispatching SaveSettings', async ({ page, bridge }) => {
  await bridge.openSettings(basePayload);
  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByRole('heading', { name: 'Application Settings' })).toHaveCount(0);

  const all = await bridge.commands();
  expect(all.find((c) => c.cmd === 'SaveSettings')).toBeUndefined();
});

test('Reset to defaults reverts the form', async ({ page, bridge }) => {
  const altered = {
    ...basePayload,
    settings: { ...basePayload.settings, autoStartNetwork: true, autoUpdateCheckInterval: 90 },
  };
  await bridge.openSettings(altered);

  // The interval input shows 90 before reset
  await expect(page.getByRole('spinbutton')).toHaveValue('90');

  await page.getByRole('button', { name: /Reset to defaults/i }).click();

  await expect(page.getByRole('spinbutton')).toHaveValue('30');
});

test('UnhideAllGames is wired to the Show all button', async ({ page, bridge }) => {
  await bridge.openSettings(basePayload);

  await expect(page.getByText(/1 game/)).toBeVisible();
  await page.getByRole('button', { name: /Show all/i }).click();
  expect((await bridge.lastCommand()).cmd).toBe('UnhideAllGames');
});
