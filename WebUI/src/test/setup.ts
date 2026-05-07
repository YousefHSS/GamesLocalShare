import '@testing-library/jest-dom/vitest';
import { afterEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';

afterEach(() => {
  cleanup();
});

// Provide a default chrome.webview shim. Tests that care about it
// can override with vi.spyOn(window.chrome.webview, 'postMessage').
if (!('chrome' in window)) {
  (window as any).chrome = {
    webview: {
      postMessage: vi.fn(),
    },
  };
}
