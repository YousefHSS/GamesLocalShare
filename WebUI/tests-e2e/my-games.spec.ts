import { test, expect } from './fixtures';

const games = [
  {
    appId: '570',
    name: 'Dota 2',
    installPath: 'C:/Steam/steamapps/common/dota 2',
    sizeOnDisk: 50_000_000_000,
    formattedSize: '46.57 GB',
    buildId: 'v100',
    platform: 'Steam' as const,
    isInstalled: true,
    isAvailableFromPeer: false,
    isHidden: false,
    lastUpdated: '2025-04-01',
    coverUrl: null,
  },
  {
    appId: '413150',
    name: 'Stardew Valley',
    installPath: 'C:/Steam/steamapps/common/Stardew Valley',
    sizeOnDisk: 500_000_000,
    formattedSize: '476 MB',
    buildId: 'v1.6',
    platform: 'Steam' as const,
    isInstalled: true,
    isAvailableFromPeer: false,
    isHidden: true,
    lastUpdated: '2024-11-10',
    coverUrl: null,
  },
];

test('renders games and filters by name', async ({ page, bridge }) => {
  await bridge.dispatchState({ localGames: games });

  await expect(page.getByText('Dota 2')).toBeVisible();
  await expect(page.getByText('Stardew Valley')).toBeVisible();

  await page.getByPlaceholder('Search my games...').fill('stardew');
  await expect(page.getByText('Dota 2')).toHaveCount(0);
  await expect(page.getByText('Stardew Valley')).toBeVisible();
});

test('clicking a game dispatches SelectLocalGame with appId', async ({ page, bridge }) => {
  await bridge.dispatchState({ localGames: games });
  await page.getByText('Dota 2').click();

  expect(await bridge.lastCommand()).toEqual({
    cmd: 'SelectLocalGame',
    payload: { appId: '570' },
  });
});

test('right-click opens the context menu and dispatches OpenGameFolder', async ({ page, bridge }) => {
  await bridge.dispatchState({ localGames: games });

  await page.getByText('Dota 2').click({ button: 'right' });
  await page.getByRole('button', { name: /Open game folder/i }).click();

  expect(await bridge.lastCommand()).toEqual({
    cmd: 'OpenGameFolder',
    payload: { appId: '570' },
  });
});

test('right-click toggles hide/show in the menu and sends ToggleGameVisibility', async ({ page, bridge }) => {
  await bridge.dispatchState({ localGames: games });

  // Stardew is hidden, so the menu should offer "Show on network"
  await page.getByText('Stardew Valley').click({ button: 'right' });
  await page.getByRole('button', { name: /Show on network/i }).click();

  expect(await bridge.lastCommand()).toEqual({
    cmd: 'ToggleGameVisibility',
    payload: { appId: '413150' },
  });
});
