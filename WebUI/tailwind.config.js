import v4Palette from './tailwind.v4-palette.js';

/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  theme: {
    extend: {
      colors: {
        // Exact Tailwind v4 default palette (oklch) so our v3 build renders the same
        // shades as the Figma v11 design (which compiles with Tailwind v4). Spread first
        // so our custom brand names below can still override/extend.
        ...v4Palette,
        'dark-bg': '#1E1E1E',
        'dark-panel': '#2D2D30',
        'dark-item': '#3C3C3C',
        'accent-blue': '#0078D4',
        'accent-purple': '#9C27B0',
        'accent-purple2': '#8B5CF6',
        'accent-green': '#4CAF50',
        'accent-error': '#EF4444',
        'accent-warning': '#F59E0B',
        'accent-cyan': '#06B6D4',
      },
      keyframes: {
        'fade-in': {
          '0%': { opacity: '0' },
          '100%': { opacity: '1' },
        },
        'fade-in-up': {
          '0%': { opacity: '0', transform: 'translateY(8px)' },
          '100%': { opacity: '1', transform: 'translateY(0)' },
        },
        'fade-in-down': {
          '0%': { opacity: '0', transform: 'translateY(-8px)' },
          '100%': { opacity: '1', transform: 'translateY(0)' },
        },
        'slide-up': {
          '0%': { opacity: '0', transform: 'translateY(100%)' },
          '100%': { opacity: '1', transform: 'translateY(0)' },
        },
        'slide-down': {
          '0%': { opacity: '0', transform: 'translateY(-100%)' },
          '100%': { opacity: '1', transform: 'translateY(0)' },
        },
        'scale-in': {
          '0%': { opacity: '0', transform: 'scale(0.92)' },
          '100%': { opacity: '1', transform: 'scale(1)' },
        },
        'pop-in': {
          '0%': { opacity: '0', transform: 'scale(0.85)' },
          '60%': { opacity: '1', transform: 'scale(1.03)' },
          '100%': { opacity: '1', transform: 'scale(1)' },
        },
        'shimmer': {
          '0%': { backgroundPosition: '-200% 0' },
          '100%': { backgroundPosition: '200% 0' },
        },
      },
      animation: {
        'fade-in': 'fade-in 180ms ease-out both',
        'fade-in-up': 'fade-in-up 220ms ease-out both',
        'fade-in-down': 'fade-in-down 220ms ease-out both',
        'slide-up': 'slide-up 260ms cubic-bezier(0.16, 1, 0.3, 1) both',
        'slide-down': 'slide-down 220ms cubic-bezier(0.16, 1, 0.3, 1) both',
        'scale-in': 'scale-in 200ms cubic-bezier(0.16, 1, 0.3, 1) both',
        'pop-in': 'pop-in 220ms cubic-bezier(0.34, 1.56, 0.64, 1) both',
        'shimmer': 'shimmer 1.6s linear infinite',
      },
    },
  },
  plugins: [],
}
