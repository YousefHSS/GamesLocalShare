import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright config for end-to-end UI tests.
 *
 * The Vite dev server is started automatically. Each test runs in a real
 * Chromium browser and interacts with the React app the same way a user would.
 * The C# `chrome.webview` host is faked via an init script (see tests-e2e/fixtures.ts).
 *
 * One-time setup (run before the first test):
 *   npm run test:ui:install
 */
export default defineConfig({
  testDir: 'tests-e2e',
  fullyParallel: true,
  retries: 0,
  workers: 1,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: {
    command: 'npm run dev',
    url: 'http://localhost:5173',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
