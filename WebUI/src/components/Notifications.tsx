import { useEffect } from 'react';
import { AlertCircle, AlertTriangle, CheckCircle, Info, X } from 'lucide-react';
import { useNotifications, type AppNotification, type NotifyAction, type NotifyLevel } from '../store';

const LEVEL_ICON: Record<NotifyLevel, React.ComponentType<{ className?: string }>> = {
  error: AlertCircle,
  warning: AlertTriangle,
  success: CheckCircle,
  info: Info,
};

// Accent color per level (border/icon). Kept in one place so toasts and alerts match.
const LEVEL_ACCENT: Record<NotifyLevel, string> = {
  error: 'text-red-400 border-red-500/50',
  warning: 'text-amber-400 border-amber-500/50',
  success: 'text-emerald-400 border-emerald-500/50',
  info: 'text-blue-400 border-blue-500/50',
};

export default function Notifications() {
  const notifications = useNotifications((s) => s.notifications);
  const toasts = notifications.filter((n) => n.variant === 'toast');
  const alerts = notifications.filter((n) => n.variant === 'alert');

  return (
    <>
      {/* Toast stack — bottom-right, above the footer/log */}
      <div className="fixed bottom-16 right-2 sm:right-6 z-[60] flex flex-col gap-2 w-[22rem] max-w-[calc(100vw-1rem)] pointer-events-none">
        {toasts.map((n) => (
          <ToastCard key={n.id} n={n} />
        ))}
      </div>

      {/* Alerts — one centered modal at a time (most recent on top) */}
      {alerts.length > 0 && <AlertModal n={alerts[alerts.length - 1]} />}
    </>
  );
}

function useActionRunner() {
  const dismiss = useNotifications((s) => s.dismiss);
  return (n: AppNotification, a: NotifyAction) => {
    try {
      a.run?.();
    } finally {
      if (a.dismiss !== false) dismiss(n.id);
    }
  };
}

function ActionButtons({ n }: { n: AppNotification }) {
  const runAction = useActionRunner();
  if (!n.actions || n.actions.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-2 mt-2 justify-end">
      {n.actions.map((a, i) => (
        <button
          key={i}
          onClick={() => runAction(n, a)}
          className={`btn-press px-3 py-1.5 rounded text-xs font-medium transition-colors ${
            a.style === 'primary'
              ? 'bg-blue-600 hover:bg-blue-700 text-white'
              : 'bg-slate-700 hover:bg-slate-600 text-slate-100'
          }`}
        >
          {a.label}
        </button>
      ))}
    </div>
  );
}

function ToastCard({ n }: { n: AppNotification }) {
  const dismiss = useNotifications((s) => s.dismiss);
  const Icon = LEVEL_ICON[n.level];

  useEffect(() => {
    if (!n.timeout || n.timeout <= 0) return;
    const t = window.setTimeout(() => dismiss(n.id), n.timeout);
    return () => window.clearTimeout(t);
  }, [n.id, n.timeout, dismiss]);

  return (
    <div
      className={`pointer-events-auto bg-slate-900 border ${LEVEL_ACCENT[n.level]} rounded-lg shadow-2xl p-3 animate-fade-in-up`}
      role="status"
    >
      <div className="flex items-start gap-2.5">
        <Icon className={`w-5 h-5 flex-shrink-0 ${LEVEL_ACCENT[n.level].split(' ')[0]}`} />
        <div className="min-w-0 flex-1">
          <div className="text-sm font-semibold text-white">{n.title}</div>
          {n.message && <div className="text-xs text-slate-300 mt-0.5 break-words">{n.message}</div>}
          <ActionButtons n={n} />
        </div>
        <button
          onClick={() => dismiss(n.id)}
          className="text-slate-500 hover:text-slate-300 flex-shrink-0"
          title="Dismiss"
        >
          <X className="w-4 h-4" />
        </button>
      </div>
    </div>
  );
}

function AlertModal({ n }: { n: AppNotification }) {
  const dismiss = useNotifications((s) => s.dismiss);
  const Icon = LEVEL_ICON[n.level];

  return (
    <div
      className="fixed inset-0 z-[70] bg-black/60 flex items-center justify-center p-4 animate-fade-in"
      onClick={(e) => { if (e.target === e.currentTarget) dismiss(n.id); }}
    >
      <div className="bg-slate-900 border border-slate-700 rounded-xl shadow-2xl w-full max-w-md overflow-hidden animate-scale-in">
        <div className="p-5 space-y-3">
          <div className="flex items-center gap-2.5">
            <Icon className={`w-6 h-6 ${LEVEL_ACCENT[n.level].split(' ')[0]}`} />
            <h2 className="text-base font-bold text-white">{n.title}</h2>
          </div>
          {n.message && <p className="text-sm text-slate-300 leading-relaxed">{n.message}</p>}
        </div>
        <div className="border-t border-slate-700 px-5 py-3 bg-slate-900/80">
          {n.actions && n.actions.length > 0 ? (
            <ActionButtons n={n} />
          ) : (
            <div className="flex justify-end">
              <button
                onClick={() => dismiss(n.id)}
                className="btn-press px-4 py-1.5 bg-blue-600 hover:bg-blue-700 text-white text-sm rounded font-medium"
              >
                OK
              </button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
