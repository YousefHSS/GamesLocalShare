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
