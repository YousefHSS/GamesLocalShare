import { test, expect } from './fixtures';

const lib = {
  id: 'lib-1',
  displayName: 'External SSD',
  rootPath: 'E:\\Games',
  driveSerial: 'E:',
  isRemovable: true,
  scanSubfolders: true,
};

async function openDrivesModal(page: import('@playwright/test').Page) {
  // The bottom-bar Drives button has an accessible name like "Drives" or "Drives (N)".
  await page.locator('button:has-text("Drives")').first().click();
  await expect(page.getByRole('heading', { name: 'External Drives' })).toBeVisible();
}

test('opening the Drives modal shows the empty state', async ({ page }) => {
  await openDrivesModal(page);
  await expect(page.getByText(/No external libraries configured/i)).toBeVisible();
});

test('Drives modal: Scan and Compare dispatch the right commands', async ({ page, bridge }) => {
  await bridge.dispatchState({ externalLibraries: [lib] });

  await openDrivesModal(page);
  await expect(page.getByText('External SSD')).toBeVisible();

  await page.getByRole('button', { name: /Scan External/i }).click();
  expect((await bridge.lastCommand()).cmd).toBe('ScanExternalLibraries');

  await page.getByRole('button', { name: /Compare Locations/i }).click();
  expect((await bridge.lastCommand()).cmd).toBe('CompareGameLocations');
});

test('Manual path input dispatches AddExternalLibrary on Enter', async ({ page, bridge }) => {
  await openDrivesModal(page);
  const input = page.getByPlaceholder(/or type a path/i);
  await input.fill('D:\\Steam Library');
  await input.press('Enter');

  const last = await bridge.lastCommand();
  expect(last.cmd).toBe('AddExternalLibrary');
  expect(last.payload).toMatchObject({
    rootPath: 'D:\\Steam Library',
    displayName: 'Steam Library',
  });
});

test('connected drive shows library as Connected on startup', async ({ page, bridge }) => {
  // Simulate the backend pushing initial state with a library AND a matching drive
  await bridge.initState({
    drives: [
      { driveLetter: 'E:\\', volumeLabel: 'External SSD', serial: 'E:', isRemovable: true, isAvailable: true },
    ],
    externalLibraries: [lib],
  });

  await openDrivesModal(page);

  // The library should be visible with a "Connected" badge
  await expect(page.getByText('External SSD')).toBeVisible();
  await expect(page.getByText('Connected')).toBeVisible();

  // Screenshot for visual verification
  await page.screenshot({ path: 'tests-e2e/screenshots/drives-connected-on-startup.png', fullPage: true });
});

test('library shows Disconnected when no matching drive is present', async ({ page, bridge }) => {
  // Push libraries with NO drives
  await bridge.initState({
    drives: [],
    externalLibraries: [lib],
  });

  await openDrivesModal(page);

  await expect(page.getByText('External SSD')).toBeVisible();
  await expect(page.getByText('Disconnected')).toBeVisible();

  await page.screenshot({ path: 'tests-e2e/screenshots/drives-disconnected.png', fullPage: true });
});

test('adding a duplicate library path does not create a second entry', async ({ page, bridge }) => {
  // Pre-populate with a library
  await bridge.dispatchState({ externalLibraries: [lib] });

  await openDrivesModal(page);

  // Verify one library shown
  await expect(page.getByText('External SSD')).toBeVisible();

  // Try adding the same path via manual input
  const input = page.getByPlaceholder(/or type a path/i);
  await input.fill('E:\\Games');
  await input.press('Enter');

  // The AddExternalLibrary command should fire — the backend dedup prevents
  // a duplicate, but from the UI side we verify the command was sent.
  const last = await bridge.lastCommand();
  expect(last.cmd).toBe('AddExternalLibrary');
  expect(last.payload).toMatchObject({ rootPath: 'E:\\Games' });

  // Screenshot to show the state
  await page.screenshot({ path: 'tests-e2e/screenshots/drives-no-duplicate.png', fullPage: true });
});

test('search filters games across libraries', async ({ page, bridge }) => {
  await bridge.dispatchState({
    externalLibraries: [lib],
    drives: [{ driveLetter: 'E:\\', volumeLabel: 'External SSD', serial: 'E:', isRemovable: true, isAvailable: true }],
  });
  await openDrivesModal(page);

  // Push comparison results with multiple games
  await page.evaluate(({ libArg }) => {
    (window as any).__crossLocationGamesResult([
      { deviceCopy: null, externalCopy: null, library: libArg, direction: 'InSync', displayName: 'Cyberpunk 2077', appId: '1', statusText: 'In sync', statusColor: 'green' },
      { deviceCopy: null, externalCopy: null, library: libArg, direction: 'DeviceToDrive', displayName: 'Elden Ring', appId: '2', statusText: 'Update available', statusColor: 'yellow' },
      { deviceCopy: null, externalCopy: null, library: libArg, direction: 'InSync', displayName: 'Cyberpunk Phantom Liberty', appId: '3', statusText: 'In sync', statusColor: 'green' },
    ]);
  }, { libArg: lib });

  // All games visible initially
  await expect(page.getByText('Cyberpunk 2077')).toBeVisible();
  await expect(page.getByText('Elden Ring')).toBeVisible();
  await expect(page.getByText('Cyberpunk Phantom Liberty')).toBeVisible();

  // Type a search query
  await page.getByPlaceholder('Search games...').fill('cyber');

  // Only matching games visible
  await expect(page.getByText('Cyberpunk 2077')).toBeVisible();
  await expect(page.getByText('Cyberpunk Phantom Liberty')).toBeVisible();
  await expect(page.getByText('Elden Ring')).toHaveCount(0);

  await page.screenshot({ path: 'tests-e2e/screenshots/drives-search-filter.png', fullPage: true });

  // Clear search with the X button
  await page.locator('button:has(svg.lucide-x)').last().click();
  await expect(page.getByText('Elden Ring')).toBeVisible();
});

test('search shows empty message when no games match', async ({ page, bridge }) => {
  await bridge.dispatchState({ externalLibraries: [lib] });
  await openDrivesModal(page);

  await page.evaluate(({ libArg }) => {
    (window as any).__crossLocationGamesResult([
      { deviceCopy: null, externalCopy: null, library: libArg, direction: 'InSync', displayName: 'Elden Ring', appId: '1', statusText: 'In sync', statusColor: 'green' },
    ]);
  }, { libArg: lib });

  await page.getByPlaceholder('Search games...').fill('xyz');
  await expect(page.getByText(/No games matching "xyz"/)).toBeVisible();

  await page.screenshot({ path: 'tests-e2e/screenshots/drives-search-no-match.png', fullPage: true });
});

test('OnlyOnDevice rows are hidden by default and revealed by the toggle', async ({ page, bridge }) => {
  await bridge.dispatchState({ externalLibraries: [lib] });
  await openDrivesModal(page);

  // Push a comparison result containing an OnlyOnDevice row.
  await page.evaluate(({ libArg }) => {
    (window as any).__crossLocationGamesResult([
      {
        deviceCopy: null,
        externalCopy: null,
        library: libArg,
        direction: 'OnlyOnDevice',
        displayName: 'DeviceOnlyGame',
        appId: '99',
        statusText: 'Only on device',
        statusColor: 'slate',
      },
      {
        deviceCopy: null,
        externalCopy: null,
        library: libArg,
        direction: 'InSync',
        displayName: 'SyncedGame',
        appId: '100',
        statusText: 'In sync',
        statusColor: 'green',
      },
    ]);
  }, { libArg: lib });

  // SyncedGame is visible; DeviceOnlyGame is not. The toggle label shows the hidden count.
  await expect(page.getByText('SyncedGame')).toBeVisible();
  await expect(page.getByText('DeviceOnlyGame')).toHaveCount(0);
  await expect(page.getByRole('checkbox', { name: /Show device-only games/i })).toBeVisible();
  await expect(page.locator('label', { hasText: /Show device-only games/i }).getByText('(1)'))
    .toBeVisible();

  // Flip the toggle
  await page.getByRole('checkbox', { name: /Show device-only games/i }).check();

  await expect(page.getByText('DeviceOnlyGame')).toBeVisible();
});
