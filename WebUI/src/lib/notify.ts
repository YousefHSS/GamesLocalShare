import { useNotifications, type AppNotification } from '../store';

/**
 * Frontend helper to raise a toast/alert without a backend round trip. Same UI as
 * backend-raised notifications (window.__notify) — this just calls the shared store.
 */
export function notify(n: Partial<AppNotification> & { title: string }): string {
  return useNotifications.getState().push(n);
}

export function dismissNotification(id: string): void {
  useNotifications.getState().dismiss(id);
}

/**
 * Warn before performing a Basic (non-updatable) Xbox copy. The transferred game plays
 * but can't be updated (an update re-downloads the whole title), so we make the user
 * confirm. `onConfirm` runs only if they choose "Copy anyway".
 */
export function confirmBasicCopy(gameName: string, onConfirm: () => void): void {
  notify({
    variant: 'alert',
    level: 'warning',
    title: 'Copy as a Basic (non-updatable) copy?',
    message:
      `"${gameName}" will be copied as a Basic copy. It can be played on the other PC, ` +
      `but it can't be updated — an update re-downloads the whole game. Reinstall it with ` +
      `this app open to enable an Updatable copy.`,
    actions: [
      { label: 'Copy anyway', style: 'primary', run: onConfirm },
      { label: 'Cancel' },
    ],
  });
}
