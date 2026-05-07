import { test, expect } from './fixtures';

test('current-transfer banner appears while transferring and Pause works', async ({ page, bridge }) => {
  await bridge.dispatchState({
    isTransferring: true,
    currentTransferGameName: 'Halo Infinite',
    currentTransferProgress: 42.5,
    currentTransferFile: 'data/level3.bin',
    currentTransferSpeed: '5 MB/s',
    currentTransferTimeRemaining: '00:02:15',
    currentTransferFormattedProgress: '42.5% (425 MB / 1 GB)',
  });

  await expect(page.getByText('Halo Infinite')).toBeVisible();
  await expect(page.getByText(/42\.5% \(425 MB \/ 1 GB\)/)).toBeVisible();
  await expect(page.getByText(/data\/level3\.bin/)).toBeVisible();

  await page.getByRole('button', { name: /Pause/i }).first().click();
  expect((await bridge.lastCommand()).cmd).toBe('PauseTransfer');
});

test('Queue tab shows queued items and remove button dispatches RemoveFromQueue', async ({ page, bridge }) => {
  await bridge.dispatchState({
    downloadQueue: [
      {
        type: 'NewDownload',
        gameName: 'Half-Life 2',
        gameAppId: '220',
        sourcePeerName: 'PEER',
        totalBytes: 6_000_000_000,
        downloadedBytes: 0,
        status: 'Queued',
        progress: 0,
        statusText: 'Queued',
        statusColor: '#6B7280',
        typeIcon: '⬇️',
        formattedSize: '5.59 GB',
        formattedProgress: '0% (0 B / 5.59 GB)',
      },
    ],
  });

  // Switch to the Queue tab in the Transfers panel
  await page.getByRole('button', { name: /Queue\s*\(1\)/ }).click();
  await expect(page.getByText('Half-Life 2')).toBeVisible();

  await page.getByTitle('Remove').click();
  expect(await bridge.lastCommand()).toEqual({
    cmd: 'RemoveFromQueue',
    payload: { appId: '220' },
  });
});

test('Start Queue → Pause Queue', async ({ page, bridge }) => {
  await page.getByRole('button', { name: /Start Queue/i }).click();
  expect((await bridge.lastCommand()).cmd).toBe('StartQueue');

  await bridge.dispatchState({ isQueueProcessing: true });
  await page.getByRole('button', { name: /Pause Queue/i }).click();
  expect((await bridge.lastCommand()).cmd).toBe('PauseQueue');
});
