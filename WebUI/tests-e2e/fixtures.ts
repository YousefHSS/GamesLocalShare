import { test as base, expect, type Page } from '@playwright/test';

/**
 * Test fixtures.
 *
 * Every test gets:
 *   - a `page` that has already navigated to the app and waited for it to mount,
 *     with a fake `window.chrome.webview` installed via init script before any
 *     React code runs;
 *   - a `bridge` helper for inspecting/dispatching commands.
 *
 * The init script must be added BEFORE navigation so the React bundle sees the
 * fake bridge as soon as it boots.
 */

export interface RecordedCommand {
  cmd: string;
  payload?: unknown;
}

export interface BridgeFixture {
  /** All commands the WebUI has sent to the C# side, oldest first. */
  commands(): Promise<RecordedCommand[]>;
  /** Last command the WebUI sent. Throws if none. */
  lastCommand(): Promise<RecordedCommand>;
  /** Push a partial state update (mimics the C# side calling `__updateState`). */
  dispatchState(patch: Record<string, unknown>): Promise<void>;
  /** Replace the whole store (mimics `__initState`). */
  initState(state: Record<string, unknown>): Promise<void>;
  /** Trigger the openSettings callback to show the SettingsModal. */
  openSettings(payload: Record<string, unknown>): Promise<void>;
}

type Fixtures = {
  page: Page;
  bridge: BridgeFixture;
};

export const test = base.extend<Fixtures>({
  // Override the default `page` fixture so every test gets a navigated, mounted app.
  page: async ({ page }, use) => {
    await page.addInitScript(() => {
      (window as any).__recordedCommands = [];
      (window as any).chrome = {
        webview: {
          postMessage: (msg: string) => {
            try {
              (window as any).__recordedCommands.push(JSON.parse(msg));
            } catch {
              (window as any).__recordedCommands.push({ raw: msg });
            }
          },
        },
      };
    });

    await page.goto('/');
    // Wait for the React app to mount — the "Scan My Games" button is always rendered.
    await page.getByRole('button', { name: /Scan My Games/i }).waitFor({ state: 'visible' });

    await use(page);
  },

  bridge: async ({ page }, use) => {
    const fixture: BridgeFixture = {
      commands: () => page.evaluate(() => (window as any).__recordedCommands as RecordedCommand[]),
      lastCommand: async () => {
        const all = await page.evaluate(() => (window as any).__recordedCommands as RecordedCommand[]);
        if (all.length === 0) throw new Error('No commands recorded');
        return all[all.length - 1];
      },
      dispatchState: async (patch) => {
        await page.evaluate((p) => (window as any).__updateState(p), patch);
      },
      initState: async (state) => {
        await page.evaluate((s) => (window as any).__initState(s), state);
      },
      openSettings: async (payload) => {
        await page.evaluate((p) => (window as any).__openSettings(p), payload);
      },
    };

    await use(fixture);
  },
});

export { expect };
