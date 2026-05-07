import { describe, it, expect, vi, beforeEach } from 'vitest';
import { sendCommand } from '../bridge';

describe('sendCommand', () => {
  beforeEach(() => {
    (window as any).chrome = {
      webview: {
        postMessage: vi.fn(),
      },
    };
  });

  it('serializes command and payload as JSON to chrome.webview.postMessage', () => {
    sendCommand('ScanLocalGames');
    const post = window.chrome.webview.postMessage as unknown as ReturnType<typeof vi.fn>;
    expect(post).toHaveBeenCalledTimes(1);
    expect(post).toHaveBeenCalledWith(JSON.stringify({ cmd: 'ScanLocalGames', payload: undefined }));
  });

  it('passes payload through to the bridge', () => {
    sendCommand('ConnectManualIp', { ip: '192.168.0.5' });
    const post = window.chrome.webview.postMessage as unknown as ReturnType<typeof vi.fn>;
    const arg = post.mock.calls[0][0] as string;
    expect(JSON.parse(arg)).toEqual({
      cmd: 'ConnectManualIp',
      payload: { ip: '192.168.0.5' },
    });
  });

  it('warns when no chrome.webview is available', () => {
    delete (window as any).chrome;
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    sendCommand('ScanLocalGames');
    expect(warn).toHaveBeenCalledWith('WebView bridge not available');
  });

  it('catches and logs errors from postMessage', () => {
    (window as any).chrome = {
      webview: {
        postMessage: () => {
          throw new Error('boom');
        },
      },
    };
    const err = vi.spyOn(console, 'error').mockImplementation(() => {});
    sendCommand('ScanLocalGames');
    expect(err).toHaveBeenCalled();
    expect(err.mock.calls[0][0]).toBe('Failed to send command:');
  });
});
