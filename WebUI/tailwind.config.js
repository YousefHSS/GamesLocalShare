/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  theme: {
    extend: {
      colors: {
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
    },
  },
  plugins: [],
}
