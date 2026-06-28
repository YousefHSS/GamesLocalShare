# Games Local Share — Design Context

A design spec for the main dashboard (the React + Tailwind WebUI hosted inside the
Avalonia desktop shell). Everything below is extracted from the live code
(`tailwind.config.js`, `src/index.css`, `src/App.tsx`, `src/components/*`). Use it to
recreate or restyle the UI in Figma. Hex values are the resolved Tailwind v3 defaults.

> Reference files: `WebUI/tailwind.config.js`, `WebUI/src/index.css`,
> `WebUI/src/App.tsx`, `WebUI/src/components/SettingsModal.tsx`, `…/PlatformIcon.tsx`.

---

## 1. Foundations

### Theme
Dark, dense desktop dashboard. Neutral base = Tailwind **slate**; each functional panel
owns an accent hue (blue / purple / green / red-cyan). Surfaces use translucency
(`/40`, `/50`) over a near-black background for soft layering.

### Layout model — "fluid reflow"
- Root font is **near-constant, clamped**: `html { font-size: clamp(13px, calc(0.4vw + 10px), 16px) }`.
  Text stays readable (≈13–16px) across window sizes instead of zooming.
- The panel grid is **auto-fit**, not breakpoint-snapped:
  `grid-cols-[repeat(auto-fit,minmax(18rem,1fr))] gap-4 h-full auto-rows-[minmax(16rem,1fr)]`,
  capped at `max-w-[110rem]`, centered. Panels reflow 4 → fewer columns as width shrinks;
  rows fill height with a 16rem minimum.
- All sizing is `rem`-based so it scales with the root font.

### Color palette

**Neutrals (slate) — base UI**
| Token | Hex | Usage |
|---|---|---|
| slate-950 | `#020617` | top bar, status bar, log overlay bg |
| slate-900 | `#0F172A` | app background, modal bg, card bg (`/50`) |
| slate-800 | `#1E293B` | panel bg (`/40`), inputs, progress track, hover |
| slate-700 | `#334155` | borders (`/50`), chips, dividers |
| slate-600 | `#475569` | scrollbar thumb, secondary borders |
| slate-500 | `#64748B` | muted text, placeholders, icons |
| slate-400 | `#94A3B8` | secondary text |
| slate-300 | `#CBD5E1` | body text on dark |
| slate-200 | `#E2E8F0` | primary text |

**Accents (per function)**
| Hue | 400 | 500 | 600 | 700 | Used for |
|---|---|---|---|---|---|
| Blue | `#60A5FA` | `#3B82F6` | `#2563EB` | `#1D4ED8` | My Games, primary actions, brand |
| Purple | `#C084FC` | `#A855F7` | `#9333EA` | `#7E22CE` | Network Peers, new-games |
| Green | `#4ADE80` | `#22C55E` | `#16A34A` | `#15803D` | Updates, online, Xbox/success |
| Emerald | — | `#10B981` | `#059669` | `#047857` | Xbox transfer success gradient |
| Red | `#F87171` | `#EF4444` | `#DC2626` | `#B91C1C` | Transfers, stop, errors |
| Cyan | — | `#06B6D4` | `#0891B2` | `#0E7490` | Queue, start-queue, Transfers gradient |
| Amber | `#FBBF24` | `#F59E0B` | `#D97706` | `#B45309` | pause, warnings, high-speed |
| Yellow | — | `#EAB308` | — | — | "awaiting resume" state |
| Pink | `#F472B6` | — | — | — | inline emphasis ("can be resumed!") |

**Legacy brand tokens** (defined in `tailwind.config.js`, kept for parity with the
Avalonia shell): `dark-bg #1E1E1E`, `dark-panel #2D2D30`, `dark-item #3C3C3C`,
`accent-blue #0078D4`, `accent-purple #9C27B0`, `accent-purple2 #8B5CF6`,
`accent-green #4CAF50`, `accent-error #EF4444`, `accent-warning #F59E0B`,
`accent-cyan #06B6D4`.

### Typography
- **Font stack:** `-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu,
  Cantarell, 'Fira Sans', 'Droid Sans', 'Helvetica Neue', sans-serif` (on Windows → **Segoe UI**).
- **Antialiased.** `user-select: none` on the app root (desktop feel).
- **Scale** (rem → px at the 16px ceiling):
  | Class | Size | Common use |
  |---|---|---|
  | `text-[0.625rem]` | 10px | meta lines, drive tag, chips |
  | `text-xs` | 12px | labels, hints, card sub-text |
  | `text-sm` | 14px | body, buttons, titles |
  | `text-base` | 16px | — |
  | `text-lg` | 18px | modal headings |
- **Weights:** `font-medium` (buttons/labels), `font-semibold` (titles), `font-bold` (modal/headers), `font-black` (platform badges), `font-mono` (IPs, sizes, progress, log).

### Spacing & radius
- Spacing scale (Tailwind rem): `1=4px 2=8px 3=12px 4=16px 5=20px 6=24px`. Common: panel padding `p-4`, modal `p-5`, gaps `gap-2/gap-4`, button `px-4 py-2`.
- Radius: `rounded` 4px · `rounded-lg` 8px · `rounded-xl` 12px · `rounded-full` (pills/dots).
- Borders: 1px, usually `slate-700/50` or an accent at `/30–/50`.

### Elevation & effects
- Modals/popovers: `shadow-2xl`.
- Translucent surfaces over bg (no hard cards): panels `bg-slate-800/40`, cards `bg-slate-900/50`.
- Backdrops: `bg-black/60` behind modals.
- Gradients: linear `to-r` (panel headers, transfer bars) and `to-br` (brand logo tile).
- Scrollbars: 0.5rem, track `#2D2D30`, thumb `#4B5563` (hover `#6B7280`), radius 0.25rem.

### Motion
Defined in `tailwind.config.js` + `index.css`. Honors `prefers-reduced-motion`.
| Name | Duration / easing | Use |
|---|---|---|
| fade-in | 180ms ease-out | modals/overlays |
| fade-in-up | 220–280ms cubic-bezier(.16,1,.3,1) | list items (staggered) |
| slide-up | 260ms cubic-bezier(.16,1,.3,1) | transfer bars |
| scale-in | 200ms cubic-bezier(.16,1,.3,1) | modal cards (from 0.92) |
| pop-in | 220ms cubic-bezier(.34,1.56,.64,1) | context menu (overshoot) |
| shimmer | 1.6s linear infinite | skeleton/placeholder |
| stagger-children | each child +25ms delay (cap 250ms) | list entrance |
Buttons: `transition-colors`; pressables scale to `0.96` on `:active` (`.btn-press`).

---

## 2. Layout structure (top → bottom)

A single `h-screen flex flex-col` column:

1. **Top bar** — `bg-slate-950`, border-b. Left: brand tile (gradient blue→purple,
   `w-8 h-8 rounded-lg`, Wifi icon) + "Games Local Share" + muted "- LAN Game Sync".
   Right: mono status message.
2. **Toolbar** — `bg-slate-900`. Centered `max-w-7xl`. Step badges + 3 primary action
   buttons (Scan / Start Network / Scan Peers) with thin divider lines, plus an
   "Your IP" pill with an online/offline dot on the right.
3. **How-to bar** — `bg-slate-800/30`, one-line usage hint; pink emphasis on the resume note.
4. **Main grid** — the four panels (see §3). Scrolls vertically (`overflow-auto p-3 sm:p-6`).
5. **Transfer bars** (conditional) — normal transfer + Xbox transfer, full-width gradient
   strips with progress (see §4).
6. **Status bar** — `bg-slate-950`. Left: Settings / Drives / Xbox-Ready / WiFi-Mode toggles.
   Right: last-error text + Log toggle.

Overlays (absolute/fixed): Settings modal, Drives modal, Xbox single-copy modal, right-click
context menu, log panel (bottom-right).

---

## 3. The four panels

**Shared panel shell:** `bg-slate-800/40 rounded-xl border border-slate-700/50 flex flex-col overflow-hidden`.
**Header:** gradient bar `px-4 py-3`, white icon in a `w-8 h-8 bg-white/20 rounded-lg` tile,
title (`font-semibold text-white`) + count sub-line, optional right-aligned action buttons
(`bg-white/20 hover:bg-white/30 rounded text-xs`).

| Panel | Header gradient | Accent | Contents |
|---|---|---|---|
| **My Games** | `from-blue-600 to-blue-700` | blue | search input, **store-filter chips**, scrollable game cards |
| **Network Peers** | `from-purple-600 to-purple-700` | purple | manual-IP connect, peer list, nested "New Games from Peers" box (purple-tinted) |
| **Updates Available** | `from-green-600 to-green-700` | green | sync items, footer "Download Update" button |
| **Transfers** | `from-red-600 to-cyan-700` | red→cyan | tabs (Incomplete / Queue), list, footer "Start/Pause Queue" |

### Game card (My Games)
`bg-slate-900/50 rounded-lg p-3 border border-slate-700/50`, hover `border-blue-500/50`,
selected `border-blue-500`. Layout: cover thumb `w-16 h-20 rounded` (object-cover) + info
column: platform icon + name (`text-sm font-semibold`, hover → blue-400), `build {id}`
(text-xs slate-400), then a row with size (`text-xs text-blue-400`) and a **drive tag**.

### Store-filter chips (My Games)
Pills shown only when >1 store present. `rounded-full text-[0.625rem] font-medium border px-2 py-0.5`.
- Idle: `bg-slate-900/50 text-slate-400 border-slate-700`.
- Active: `bg-blue-500/20 text-blue-300 border-blue-500/40`.
- Each store chip shows its `PlatformIcon` + label (All · Steam · Epic · Xbox · External).

### Drive tag
`text-[0.625rem] font-semibold text-slate-300 bg-slate-700/60 border border-slate-600/50 rounded px-1.5 py-px` — shows the install drive letter (e.g. `E:`), tooltip = full path.

### Platform icons (`PlatformIcon.tsx`)
- **Steam:** blue (`text-blue-400`) Steam glyph SVG.
- **Epic:** black rounded-sm tile, white bold "E".
- **Xbox:** green-600 **circle**, white bold "X".
- **External:** slate-700 tile, "⌂".
Default size `w-4 h-4` (chips use `w-3 h-3`).

### Search input
`bg-slate-900/50 border border-slate-700 rounded-lg pl-8 pr-7 py-1.5 text-xs`, leading Search
icon, trailing clear (X) when filled, focus `border-blue-500`.

### Empty state
Centered, muted: large `w-12 h-12 text-slate-600` icon + title (`text-sm text-slate-500`) +
sub (`text-xs text-slate-600`).

---

## 4. Buttons & status elements

### Buttons
Base: `px-4 py-2 rounded-lg font-medium text-sm transition-colors`, often with a leading
`w-4 h-4` lucide icon. Solid color by intent (hover = +100 shade):
- Primary/blue `bg-blue-600`, purple `bg-purple-600`, success/green `bg-green-600`,
  danger/stop `bg-red-600`, queue/cyan `bg-cyan-600`, pause/amber `bg-amber-600`.
- Disabled: dimmed shade + `cursor-not-allowed` (e.g. `disabled:bg-blue-800`, `disabled:opacity-50`).
- Small ghost (panel-header actions, status bar): `bg-white/20 hover:bg-white/30` or
  `hover:bg-slate-800 rounded`.
- Step badge: `px-3 py-1 rounded text-xs font-medium`; active `bg-blue-500/20 text-blue-400 border border-blue-500/30`, inactive `bg-slate-800 text-slate-500`.

### Progress bars
Track `bg-slate-800 rounded h-2 overflow-hidden`; fill is a gradient:
- Normal transfer: `from-blue-500 to-purple-500`.
- Xbox active: `from-green-500 to-emerald-500`; paused → `from-amber-500 to-yellow-500`;
  indeterminate → full-width pulsing gradient.
Card mini-bars use `h-1` with a solid accent fill.

### Transfer bar strip (full width, `animate-slide-up`)
Gradient background varies by state:
- Normal: `from-blue-900/60 to-purple-900/60` border-blue-700/50.
- Xbox active: `from-green-900/60 to-emerald-900/60`; failed `from-red-900/60…`;
  done `from-emerald-900/60 to-green-800/60`; awaiting-resume `from-yellow-900/70 to-amber-800/70`;
  paused `from-amber-900/60 to-yellow-900/60`.
Contains: state icon, title + mono progress/speed/ETA, progress bar, file/status line, and
right-aligned Pause/Resume/Stop/Dismiss buttons (`text-xs`, icon + label).

### Online indicator
`w-2 h-2 rounded-full`; online `bg-green-500 animate-pulse` + "Online" (green-400),
offline `bg-slate-600` + "Offline" (slate-500).

---

## 5. Overlays

### Modal shell (Settings / Drives / Xbox)
Backdrop `fixed inset-0 bg-black/60 flex items-center justify-center p-4 animate-fade-in`.
Card: `bg-slate-900 border border-slate-700 rounded-xl shadow-2xl w-full max-w-2xl
max-h-[85–90vh] flex flex-col overflow-hidden animate-scale-in`.
Header: gradient bar (`from-blue-600 to-purple-600` settings; `from-blue-600 to-cyan-600`
drives; `from-emerald-600 to-green-600` xbox) with icon + bold title + close X.
Body: `p-5 space-y-5`, grouped **Sections** (label + divider) of Toggles, number inputs,
and text inputs (`bg-slate-800 border border-slate-700 rounded px-3 py-1.5 text-sm`).

### Context menu (right-click game)
`fixed bg-slate-900 border border-slate-700 rounded-lg shadow-2xl py-1 min-w-[12.5rem]
animate-pop-in`; items `px-3 py-2 text-sm hover:bg-slate-800` with a colored leading icon.

### Log panel
Bottom-right `bg-slate-950 border border-slate-700 rounded-lg shadow-2xl`, `h-80`,
mono `text-xs` lines (timestamp slate-600 + colored message), Copy/Clear/close controls.

---

## 6. Figma rebuild notes

- **Create variables/tokens first:** the slate ramp + the accent ramps in §1, plus
  spacing (4/8/12/16/20/24), radius (4/8/12/full), and the type scale (10/12/14/16/18).
- **Frames:** design at a desktop width (e.g. **1440×900** or 1920×1080). The real app
  scales fluidly, but a fixed frame is fine for editing; keep the 4-column grid with
  ~`288px` (18rem) min column width and `16px` gaps, centered with ~`1760px` (110rem) max.
- **Components to build:** Panel (with header-gradient + accent variants), Game card,
  Platform icon (4 variants), Store chip (idle/active), Drive tag, Button (intent ×
  state), Step badge, Progress bar (state variants), Transfer strip (state variants),
  Modal shell, Context menu item, Empty state, Online dot.
- **Gradients:** headers/strips are left→right; brand tile is top-left→bottom-right.
- **Don't hardcode pixel type if you re-export to code** — the app's source uses rem so
  it scales; in Figma just pick the px equivalents above.
- Real screenshots: maximize the app and capture the 4-panel dashboard, a transfer bar
  (start a transfer), and the Settings modal for the fullest component coverage.
