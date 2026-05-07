import { test, expect } from './fixtures';

test.describe('Smoke', () => {
  test('boots the four primary panels', async ({ page }) => {
    await expect(page.getByText('Games Local Share').first()).toBeVisible();
    await expect(page.getByRole('heading', { name: 'My Games' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Network Peers' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Updates Available' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Transfers' })).toBeVisible();
  });

  test('Step 1 — Scan My Games dispatches ScanLocalGames', async ({ page, bridge }) => {
    await page.getByRole('button', { name: /Scan My Games/i }).click();
    expect(await bridge.lastCommand()).toEqual({ cmd: 'ScanLocalGames', payload: undefined });
  });

  test('Step 2 — Start Network → Stop Network dispatches both commands', async ({ page, bridge }) => {
    await page.getByRole('button', { name: /Start Network/i }).click();
    expect((await bridge.lastCommand()).cmd).toBe('StartNetwork');

    await bridge.dispatchState({ isNetworkActive: true });

    await page.getByRole('button', { name: /Stop Network/i }).click();
    expect((await bridge.lastCommand()).cmd).toBe('StopNetwork');
  });

  test('Step 3 — Scan for Peers is gated behind network active', async ({ page, bridge }) => {
    const btn = page.getByRole('button', { name: /Scan for Peers/i });
    await expect(btn).toBeDisabled();

    await bridge.dispatchState({ isNetworkActive: true });
    await expect(btn).toBeEnabled();

    await btn.click();
    expect((await bridge.lastCommand()).cmd).toBe('ScanForPeers');
  });
});
