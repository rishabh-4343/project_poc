# Auditor — UI Specification (CEO-demo grade)

This is binding for all frontend work. Build to it exactly. The goal: this must look and
feel like a shipped enterprise security product, not a prototype. It will be shown to the
CEO. "Good enough" is not good enough — every screen is polished, consistent, and animated.

## 0. The quality bar (treat as acceptance criteria)
A screen is DONE only when all of these are true:
- Consistent spacing, alignment, and typography across every element (no ad-hoc margins).
- Every interactive element has hover, focus-visible, active, and disabled states.
- Loading = skeletons (never a spinner alone or a blank flash). Empty = a designed empty
  state with an icon + one line + a primary action. Errors = inline, human, actionable.
- Smooth, purposeful motion (see section 5). Nothing pops in abruptly.
- Light AND dark mode both look intentional and correct.
- Works down to 1280px comfortably; usable at laptop 1440px (the demo screen).
- Real, meaningful data in every view — never "lorem", never "Item 1".
- Numbers use tabular figures and align in columns; paths/IDs use the mono font.

## 1. Stack (exact — do not substitute)
- Next.js (App Router) + TypeScript.
- **Tailwind CSS** for all styling. No CSS-in-JS, no inline style objects for layout.
- **Motion** (`motion`, import from `motion/react`) for ALL animation. No CSS keyframes for
  component animation; Tailwind transitions are fine only for simple hover/color changes.
- **shadcn/ui** (Radix + Tailwind) for primitives: button, input, select, dialog, drawer,
  dropdown, tabs, tooltip, badge, card, table, skeleton, toast (sonner), separator, switch.
  This gives a consistent, accessible, professional base — use it, don't hand-roll primitives.
- **lucide-react** for icons. **Recharts** (shadcn chart wrapper) for dashboard charts.
- **TanStack Query** for data, **TanStack Table** for the audit table (sorting, column sizing).
- `@microsoft/signalr` for the live feed.
- Fonts: **Geist** (UI) + **Geist Mono** (paths, SIDs, IDs). (Inter + JetBrains Mono is an
  acceptable fallback.) Load via next/font.

## 2. Design tokens (define once as CSS variables + Tailwind theme)
Direction: "refined enterprise security console." Calm slate neutrals, one confident accent,
clear semantic colors. Light-first, first-class dark.

Neutrals (slate):
  --bg            light #F8FAFC   dark #0B0F17
  --surface       light #FFFFFF   dark #111725
  --surface-2     light #F1F5F9   dark #161D2E
  --border        light #E2E8F0   dark #243043
  --text          light #0F172A   dark #E6EDF7
  --text-muted    light #64748B   dark #93A3B8

Accent (indigo):
  --accent        #4F46E5   (hover #4338CA)   --accent-fg #FFFFFF
  --accent-soft   light #EEF2FF  dark #1E1B4B

Semantic (status/risk — NOT the accent):
  --success  #16A34A   (Allowed)        soft light #DCFCE7 / dark #052E16
  --danger   #DC2626   (Denied/Delete)  soft light #FEE2E2 / dark #450A0A
  --warning  #D97706   (medium risk)    soft light #FEF3C7 / dark #451A03
  --info     #0284C7   (reads/info)     soft light #E0F2FE / dark #082F49

Scale: radius md=8px, lg=12px, cards=12px. Shadow: subtle only
  (card: 0 1px 2px rgba(2,6,23,.06), elevated: 0 8px 24px rgba(2,6,23,.12)).
Type scale: 12 / 13 / 14 (body) / 16 / 18 / 22 / 28. Line-height 1.5 body, 1.2 headings.
Spacing: 4px base; use 8/12/16/24/32 consistently. Density: comfortable, not cramped.

## 3. App shell (every screen)
- **Left sidebar** (240px, collapsible to 64px): product logo/name at top, nav items with
  lucide icons (Dashboard, Agents, Servers, Sessions, Reports), active item has the accent
  bar + accent-soft background. Footer: environment badge + theme toggle.
- **Top bar** (56px): page title + breadcrumb on the left; on the right a global search,
  a live "agents online" pill, and a user avatar menu.
- **Content**: max-width container, 24–32px padding, section headings with clear hierarchy.
- Sidebar collapse and theme toggle animate smoothly (Motion).

## 4. Screens (what to build, in order of demo importance)
1. **Dashboard** — the first impression. A row of 4 metric cards (Events today, Active
   sessions, Agents online, Denied events) each with a label, big tabular number, a small
   delta vs yesterday, and a sparkline. Below: a line/area chart of events over time, a
   "Top users by activity" bar list, and a "Recent integrity events" panel. Numbers count up
   on load (Motion). Cards stagger in.
2. **Live session view** — THE demo moment. A dense audit table that updates in real time as
   the driver captures events. New rows **animate in at the top** (Motion: fade + slide +
   a brief accent highlight that fades). Columns: When, Server, Who, Object (icon+name),
   Path (mono, truncated with tooltip), Operation (badge: color by type), Status
   (Allowed=success / Denied=danger), Process, From (IP). A header with the session scope,
   a live "recording" indicator (pulsing dot), a Stop button, filters (operation, user,
   allowed/denied), and Export CSV. Keyset "load older" on scroll.
3. **New session** — pick a server (card list with status), add folders (chips), tick
   operations (grouped: Changes / Security / Reads with a "high volume" note on reads),
   then Start — which transitions to the live view.
4. **Agents / Servers** — list with live status pills (online/offline/last-seen), version,
   and actions. Register forms in a drawer, not a new page.
5. **Reports** — session history table; open one to see its recorded events (same table as
   live, static); export.

## 5. Animation (Motion) — specific, not decorative
Use Motion deliberately; motion should explain or delight, never distract.
- Page/section entrance: content fades + rises 8px, 200–300ms, ease-out. Stagger children 30–50ms.
- Metric numbers: count up over ~600ms on first load (and on live change).
- **Live table rows**: new row enters with opacity 0→1, y -8→0, and a one-time background
  flash from accent-soft → transparent over ~1.2s. Use AnimatePresence + layout animation so
  existing rows slide down smoothly. This is the standout moment — make it crisp, not janky.
- Tab/route changes: subtle crossfade (150ms).
- Drawers/dialogs: slide+fade via shadcn/Radix (already animated) — keep defaults tasteful.
- Hover: buttons/cards lift 1px + shadow step, 120ms.
- Respect `prefers-reduced-motion`: disable transforms, keep opacity only.
- Timing: fast (120ms) for hover, medium (200–300ms) for entrances, never >400ms. Easing:
  ease-out for entrances, ease-in-out for moves. No bounce/spring except a tiny spring on
  the live "recording" pulse.

## 6. Components to standardize (build once, reuse)
- `StatusBadge` (operation + allowed/denied + risk — color from semantic tokens).
- `MetricCard` (label, number count-up, delta, sparkline).
- `LiveIndicator` (pulsing dot + "Recording").
- `DataTable` (sticky header, hover row, zebra optional, mono path cell with tooltip,
  column sizing, empty + skeleton states).
- `PageHeader`, `EmptyState`, `Drawer` wrappers.
Never restyle these ad-hoc per screen.

## 7. Do / Don't
DO: generous whitespace, strong alignment, one accent, semantic color only for meaning,
    skeletons, tooltips on truncation, keyboard focus rings, tabular numbers.
DON'T: multiple accent colors, rainbow badges, heavy shadows, emojis as icons, centered
    body text, random radii, spinners as the only loading state, abrupt pop-ins, lorem ipsum.

## 8. How to use this with Copilot
Each frontend task prompt must end with: "Follow auditor-pack/docs/UI-SPEC.md exactly —
Tailwind + Motion + shadcn/ui, the tokens and animation rules in it, and meet its quality
bar. Match the reference mockup if one is provided."
