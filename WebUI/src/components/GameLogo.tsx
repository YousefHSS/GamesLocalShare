interface GameLogoProps {
  variant: 'network' | 'sync' | 'badge' | 'geometric';
  size?: number;
}

export default function GameLogo({ variant, size = 200 }: GameLogoProps) {
  if (variant === 'network') {
    return (
      <svg width={size} height={size} viewBox="0 0 200 200" fill="none" xmlns="http://www.w3.org/2000/svg">
        {/* Outer circle */}
        <circle cx="100" cy="100" r="95" stroke="url(#gradient1)" strokeWidth="3" />

        {/* Network nodes */}
        <circle cx="100" cy="30" r="12" fill="url(#gradient1)" />
        <circle cx="157" cy="65" r="12" fill="url(#gradient1)" />
        <circle cx="157" cy="135" r="12" fill="url(#gradient1)" />
        <circle cx="100" cy="170" r="12" fill="url(#gradient1)" />
        <circle cx="43" cy="135" r="12" fill="url(#gradient1)" />
        <circle cx="43" cy="65" r="12" fill="url(#gradient1)" />

        {/* Center game controller */}
        <path
          d="M 75 92 Q 75 82 82 82 L 95 82 L 95 78 Q 95 74 99 74 L 101 74 Q 105 74 105 78 L 105 82 L 118 82 Q 125 82 125 92 L 125 108 Q 125 116 118 116 L 82 116 Q 75 116 75 108 Z"
          fill="url(#gradient2)"
        />

        {/* Controller body shadow/depth */}
        <path
          d="M 77 94 Q 77 86 82 86 L 95 86 L 95 80 Q 95 78 99 78 L 101 78 Q 105 78 105 80 L 105 86 L 118 86 Q 123 86 123 94 L 123 106 Q 123 112 118 112 L 82 112 Q 77 112 77 106 Z"
          fill="#1e293b"
          opacity="0.3"
        />

        {/* D-pad housing */}
        <circle cx="87" cy="99" r="9" fill="#1e293b" opacity="0.2" />

        {/* D-pad */}
        <rect x="85.5" y="93" width="3" height="12" rx="1" fill="white" />
        <rect x="81" y="97.5" width="12" height="3" rx="1" fill="white" />

        {/* Button housing */}
        <circle cx="113" cy="99" r="9" fill="#1e293b" opacity="0.2" />

        {/* Face buttons (ABXY style) */}
        <circle cx="113" cy="93" r="3.5" fill="#fbbf24" />
        <circle cx="119" cy="99" r="3.5" fill="#ef4444" />
        <circle cx="113" cy="105" r="3.5" fill="#22c55e" />
        <circle cx="107" cy="99" r="3.5" fill="#3b82f6" />

        {/* Button highlights */}
        <circle cx="113" cy="93" r="1.5" fill="white" opacity="0.6" />
        <circle cx="119" cy="99" r="1.5" fill="white" opacity="0.6" />
        <circle cx="113" cy="105" r="1.5" fill="white" opacity="0.6" />
        <circle cx="107" cy="99" r="1.5" fill="white" opacity="0.6" />

        {/* Shoulder buttons/triggers */}
        <rect x="78" y="82" width="10" height="4" rx="2" fill="#475569" opacity="0.7" />
        <rect x="112" y="82" width="10" height="4" rx="2" fill="#475569" opacity="0.7" />

        {/* Connection lines */}
        <line x1="100" y1="42" x2="100" y2="74" stroke="url(#gradient1)" strokeWidth="2" />
        <line x1="145" y1="75" x2="125" y2="88" stroke="url(#gradient1)" strokeWidth="2" />
        <line x1="145" y1="125" x2="125" y2="110" stroke="url(#gradient1)" strokeWidth="2" />
        <line x1="100" y1="158" x2="100" y2="116" stroke="url(#gradient1)" strokeWidth="2" />
        <line x1="55" y1="125" x2="75" y2="110" stroke="url(#gradient1)" strokeWidth="2" />
        <line x1="55" y1="75" x2="75" y2="88" stroke="url(#gradient1)" strokeWidth="2" />

        <defs>
          <linearGradient id="gradient1" x1="0" y1="0" x2="200" y2="200" gradientUnits="userSpaceOnUse">
            <stop stopColor="#3b82f6" />
            <stop offset="1" stopColor="#8b5cf6" />
          </linearGradient>
          <linearGradient id="gradient2" x1="75" y1="74" x2="125" y2="116" gradientUnits="userSpaceOnUse">
            <stop stopColor="#475569" />
            <stop offset="1" stopColor="#1e293b" />
          </linearGradient>
        </defs>
      </svg>
    );
  }

  if (variant === 'sync') {
    return (
      <svg width={size} height={size} viewBox="0 0 200 200" fill="none" xmlns="http://www.w3.org/2000/svg">
        {/* Circular sync arrows background */}
        <path
          d="M 100 20 A 80 80 0 1 1 99.9 20"
          stroke="url(#gradient1)"
          strokeWidth="8"
          strokeLinecap="round"
          fill="none"
        />
        {/* Arrow head top */}
        <path d="M 95 25 L 100 15 L 105 25 Z" fill="url(#gradient1)" />

        <path
          d="M 100 180 A 80 80 0 1 1 100.1 180"
          stroke="url(#gradient2)"
          strokeWidth="8"
          strokeLinecap="round"
          fill="none"
        />
        {/* Arrow head bottom */}
        <path d="M 105 175 L 100 185 L 95 175 Z" fill="url(#gradient2)" />

        {/* Game controller simplified */}
        <rect x="60" y="80" width="80" height="40" rx="12" fill="url(#gradient3)" />

        {/* D-pad */}
        <rect x="75" y="95" width="3" height="12" rx="1" fill="white" opacity="0.9" />
        <rect x="71" y="99" width="11" height="3" rx="1" fill="white" opacity="0.9" />

        {/* Buttons */}
        <circle cx="115" cy="95" r="4" fill="white" opacity="0.9" />
        <circle cx="125" cy="95" r="4" fill="white" opacity="0.9" />
        <circle cx="120" cy="89" r="4" fill="white" opacity="0.9" />
        <circle cx="120" cy="101" r="4" fill="white" opacity="0.9" />

        <defs>
          <linearGradient id="gradient1" x1="100" y1="0" x2="100" y2="100" gradientUnits="userSpaceOnUse">
            <stop stopColor="#06b6d4" />
            <stop offset="1" stopColor="#3b82f6" />
          </linearGradient>
          <linearGradient id="gradient2" x1="100" y1="100" x2="100" y2="200" gradientUnits="userSpaceOnUse">
            <stop stopColor="#3b82f6" />
            <stop offset="1" stopColor="#8b5cf6" />
          </linearGradient>
          <linearGradient id="gradient3" x1="60" y1="80" x2="140" y2="120" gradientUnits="userSpaceOnUse">
            <stop stopColor="#1e293b" />
            <stop offset="1" stopColor="#334155" />
          </linearGradient>
        </defs>
      </svg>
    );
  }

  return null;
}
