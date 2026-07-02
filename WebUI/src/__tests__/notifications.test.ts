import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { useNotifications } from '../store';

describe('useNotifications store', () => {
  beforeEach(() => {
    useNotifications.getState().clear();
  });

  it('push adds a notification with sensible defaults', () => {
    const id = useNotifications.getState().push({ title: 'Hi' });
    const list = useNotifications.getState().notifications;
    expect(list).toHaveLength(1);
    expect(list[0].id).toBe(id);
    expect(list[0].level).toBe('info');
    expect(list[0].variant).toBe('toast');
    expect(list[0].timeout).toBeGreaterThan(0); // info toasts auto-dismiss
  });

  it('errors and alerts are sticky (timeout 0)', () => {
    useNotifications.getState().push({ title: 'Boom', level: 'error' });
    useNotifications.getState().push({ title: 'Decide', variant: 'alert', level: 'warning' });
    const list = useNotifications.getState().notifications;
    expect(list[0].timeout).toBe(0);
    expect(list[1].timeout).toBe(0);
  });

  it('dismiss removes a notification by id', () => {
    const id = useNotifications.getState().push({ title: 'Bye' });
    useNotifications.getState().dismiss(id);
    expect(useNotifications.getState().notifications).toHaveLength(0);
  });

  it('pushing with an existing id replaces it in place', () => {
    useNotifications.getState().push({ id: 'same', title: 'First' });
    useNotifications.getState().push({ id: 'same', title: 'Second' });
    const list = useNotifications.getState().notifications;
    expect(list).toHaveLength(1);
    expect(list[0].title).toBe('Second');
  });
});

describe('window.__notify bridge', () => {
  let postMessageMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    useNotifications.getState().clear();
    postMessageMock = vi.fn();
    (window as any).chrome = { webview: { postMessage: postMessageMock } };
  });

  afterEach(() => {
    delete (window as any).chrome;
  });

  it('is registered and pushes into the store', () => {
    expect(typeof (window as any).__notify).toBe('function');
    (window as any).__notify({ title: 'From C#', level: 'error' });
    const list = useNotifications.getState().notifications;
    expect(list).toHaveLength(1);
    expect(list[0].title).toBe('From C#');
    expect(list[0].level).toBe('error');
  });

  it('maps a backend action command to a sendCommand call', () => {
    (window as any).__notify({
      title: 'No cache folder',
      level: 'warning',
      actions: [{ label: 'Choose folder…', command: 'BrowseXboxCacheRoot', payload: { retry: 'ToggleCacheProxy' } }],
    });
    const n = useNotifications.getState().notifications[0];
    expect(n.actions).toHaveLength(1);
    expect(typeof n.actions![0].run).toBe('function');

    // Invoking the action posts the mapped command to the C# bridge.
    n.actions![0].run!();
    expect(postMessageMock).toHaveBeenCalledTimes(1);
    const sent = JSON.parse(postMessageMock.mock.calls[0][0] as string);
    expect(sent.cmd).toBe('BrowseXboxCacheRoot');
    expect(sent.payload).toEqual({ retry: 'ToggleCacheProxy' });
  });
});
