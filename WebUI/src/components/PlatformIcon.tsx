/**
 * Small inline icon for a game's storefront/platform.
 *
 * Renders a recognizable logo for Steam, Epic Games, and Xbox; falls back to a
 * neutral folder badge for "External" (a generic non-store library) and for any
 * unknown value. Used in every game list so the player can tell at a glance
 * which store a title comes from without reading the row text.
 */
export type PlatformValue = 'Steam' | 'EpicGames' | 'Xbox' | 'External' | string | undefined;

export default function PlatformIcon({
  platform,
  className,
}: {
  platform?: PlatformValue;
  className?: string;
}) {
  const size = className ?? 'w-4 h-4';

  if (platform === 'EpicGames') {
    return (
      <span
        title="Epic Games"
        aria-label="Epic Games"
        className={`inline-flex items-center justify-center ${size} rounded-sm bg-slate-900 text-white text-[9px] font-black shrink-0`}
      >
        E
      </span>
    );
  }

  if (platform === 'Xbox') {
    return (
      <span
        title="Xbox"
        aria-label="Xbox"
        className={`inline-flex items-center justify-center ${size} rounded-full bg-green-600 text-white text-[9px] font-black shrink-0`}
      >
        X
      </span>
    );
  }

  if (platform === 'External') {
    return (
      <span
        title="External library"
        aria-label="External library"
        className={`inline-flex items-center justify-center ${size} rounded-sm bg-slate-700 text-slate-200 text-[9px] font-black shrink-0`}
      >
        ⌂
      </span>
    );
  }

  // Default: Steam (also covers undefined/unknown for legacy rows)
  return (
    <svg
      role="img"
      aria-label="Steam"
      viewBox="0 0 24 24"
      className={`${size} shrink-0 text-blue-400`}
      fill="currentColor"
    >
      <title>Steam</title>
      <path d="M12 2C6.48 2 2 6.48 2 12c0 4.55 3.05 8.39 7.22 9.6l-.46-1.53a3.99 3.99 0 0 1-2.5-2.4l2.05.85c.18.7.81 1.23 1.58 1.23a1.65 1.65 0 0 0 1.65-1.65v-.07l1.84-1.32c2.32 0 4.21-1.89 4.21-4.21s-1.89-4.21-4.21-4.21-4.21 1.89-4.21 4.21v.05L7.3 13.83a2.46 2.46 0 0 0-1.13.27l-3.96-1.64C2.07 7.05 6.59 2 12 2Zm3.37 6.21a2.81 2.81 0 1 1 0 5.62 2.81 2.81 0 0 1 0-5.62Zm0 .7a2.11 2.11 0 1 0 0 4.22 2.11 2.11 0 0 0 0-4.22ZM10.66 16.5l-1.07-.44c.18.38.51.69.92.84.81.34 1.74-.05 2.08-.86s-.05-1.74-.86-2.08a1.6 1.6 0 0 0-1.21-.01l1.1.46a1.18 1.18 0 1 1-.96 2.16Z" />
    </svg>
  );
}
