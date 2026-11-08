# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.8.0] - 2026-11-08

### Added
- **Sort by clicking a column title** (KONTO, 5 STUNDEN / 5 H, WOCHE). Each click cycles
  ascending → descending → default order; the choice is remembered.
  - KONTO: natural name order ("Anthropic 2" before "Anthropic 10").
  - 5 STUNDEN / WOCHE: *which account runs out first*. Accounts projected to run dry within
    the column's horizon come first (blocked ones at the top, then soonest empty – a quota
    window before its reset, a balance whose runway ends within 5 h / a week), then the
    accounts that make it, fullest first. Rows without data for the column stay at the end.
  - The active column shows a direction triangle and what the order means ("zuerst leer",
    "zuletzt leer", "A–Z"); hovering a title explains the click cycle in the footer.

### Fixed
- **Linux:** clicks on pin, refresh, settings and rows never fired (the release handler
  checked "left button pressed", which Avalonia reports as false on release).
- **Linux:** the tray icon was drawn into the top-left 32 px of a 256 px bitmap and showed
  as a tiny dot; the launcher icon showed a "0 %" gauge instead of the brand ring.
- **Linux:** cells now follow the same column rules as Windows (a weekly-only account no
  longer shows its week under "5 H", info-only accounts no longer get a bar), stale values
  are grey and raise no alarms, runways need a measured rate and count down live, the
  stale reason distinguishes token expiry / no connection / failed query, waiting accounts
  no longer count as "OK", disabled accounts are hidden, "noch X/Y" labels are unambiguous
  and use German decimals, save errors in the settings show instead of ending the app.
- **Linux:** popup placement mixed device-independent and physical pixels (off-screen on
  HiDPI screens).
- A parked popup that grew since it was parked is shifted back onto the screen
  (Windows and Linux) instead of cutting off its footer.
- UI saves (sort order, parked position, settings) no longer overwrite a config import that
  landed since the last reload, and editing an account without touching its key keeps a
  key the import rotated meanwhile.
- The "Prognose-Zeitfenster" setting (burn-rate look-back) was ignored; it now applies.
- Import: Groq, Together and Cerebras map to their own provider types, Mistral, xAI,
  Gemini and Perplexity are importable, info-only providers get their 1 h default interval.
- Generic OpenAI-compatible providers: a reset header like "20ms" was read as 20 minutes.
- Epoch timestamps in µs/ns are recognised and out-of-range values no longer throw.
- A window of unknown length listed first (e.g. a monthly MCP quota) no longer takes the
  "5 h" column from the real 5-hour window.
- OpenRouter: an unexpected `/credits` response no longer discards the key data.
- Windows: the tray context menu no longer closes on the first mouse move (the hover popup
  opened over it); a failed save of the parked position can no longer raise an error dialog.

## [1.7.4] - 2026-11-04

### Fixed
- Linux: adding, editing and removing accounts in the settings saved nothing.
- Linux: live config reload did not tick reliably (timer started off the UI thread).

## [1.7.3] - 2026-11-04

### Changed
- Tray icon: a ring showing the mean weekly usage of all Anthropic accounts with the percent
  in the middle (red at 100 %); problem accounts show as a corner dot.

## [1.7.2] - 2026-10-28

### Changed
- Windows: critical/error rows are tinted amber instead of red; the text stays white.

## [1.7.1] - 2026-10-23

### Changed
- Cell colours follow usage: lime when unused, amber while in use, red at the limit,
  grey for stale values. Warning lines keep answering "does it last until the reset?".

## [1.7.0] - 2026-10-18

### Added
- Seven new providers: Groq, Mistral, xAI Grok and Cerebras (header-based, share the
  generic parser), plus Together AI, Google Gemini and Perplexity. Those three expose
  **no quota API at all** — they run as info-only accounts (reachability ping, default
  poll 1 h, explanatory note instead of invented numbers).
- The provider catalog moved to a data file (`providers.json`, embedded in Core) that
  the Windows/Linux dialogs and the upcoming iOS port all read; a test pins it against
  the runtime definitions so they cannot drift.

## [1.6.0] - 2026-10-17

### Added
- **Linux build** (Avalonia, .NET 8): tray icon (StatusNotifier/AppIndicator), the
  same compact popup (rows, bars, forecast lines, tooltips, scrollbar, drag-to-park),
  settings with account management and autostart, `notify-send` crit notifications,
  live config reload, `--import`/`--render` parity. Ships as a self-contained
  single binary, a .deb package and a tarball.
- Config/import/key-protection moved into Core with an `IKeyProtector` abstraction:
  DPAPI LocalMachine on Windows (unchanged, existing configs stay valid), a `v2:`-marked
  plain store plus chmod 0600 on Linux (no DPAPI there).
- CI now builds and tests on Windows **and** Ubuntu, including a headless popup render.

## [1.5.0] - 2026-10-15

### Added
- The popup can be dragged by its title band to any spot on any monitor.
  Dragging pins it automatically; the position is remembered
  (`Settings.PopupX/PopupY`) and restored on next show. Falls back to the
  tray anchor when the monitor is gone. Data refreshes no longer move the
  form mid-drag.

## [1.4.0] - 2026-10-14

### Changed
- Renamed the app to **Insight Token Watcher** (`InsightTokenWatcher.exe`,
  `%APPDATA%\InsightTokenWatcher`). Config, encrypted keys and history migrate
  automatically from `%APPDATA%\TokenWatch` on first start; the autostart
  registry entry is renamed when toggled.

## [1.3.0] - 2026-10-14

### Changed
- Popup redesigned as a compact table: one row per account, the short window (5 h)
  in the left column and the long window (week) in the right, so the same kind of
  window lines up across all accounts. Forecast text only appears when an account
  is tight or critical; full details are in a hover tooltip.
- Popup is a single owner-drawn surface with smooth wheel scrolling and a slim
  custom scrollbar; header shows a status summary; pin button keeps it open.
- Rows are ordered by provider (stable) instead of jumping around by severity.
- Tray icon: one ring segment per account in its status color.
- Dialogs scale with system DPI and use dark title bars.
- Polling slows down to ≤ 5 min while an account is idle and speeds back up on change.

### Fixed
- Scrolling the popup could leave it blank: cards were child controls in an
  auto-scroll panel and were recreated on every poll, shifting while scrolled
  (also leaked window handles).
- A failed poll replaced valid values with an error; the last good values now stay
  visible (marked stale). Expired tokens no longer raise a critical alarm, network
  hiccups only after three failures in a row.
- The running app never picked up an external import: the watcher ignored the
  atomic rename and the store re-used its in-memory copy instead of re-reading.
- Balloon warnings and updates were dropped until the popup had been opened once.
- A transient read error on startup could reset the config and later overwrite it.
- The popup followed the cursor while hovering the tray icon.
- Burn-rate history is kept when an account's token is rotated.

## [1.2.0] - 2026-10-14

### Changed
- Redesigned hover popup: rounded corners, hover highlight on cards, gradient
  bars, status glyphs (✓ / ! / ✗), sparklines with area fill, crit cards get a
  tinted border.
- Tray icon redrawn as a gauge ring with status-colored arc instead of a filled chip.

### Fixed
- Dead space in cards for balance/info windows (per-type row heights).
- Popup no longer shows stale data after being hidden (dirty flag + signature).
- No more full-card rebuild flicker when nothing visible changed.
- Layout constants scale with system DPI (100 %/125 %/150 %).

## [1.1.0] - 2026-10-14

### Added
- New providers: OpenAI (organization costs via Admin key, legacy credit_grants
  fallback), OpenRouter (key limit + credit balance), and a generic
  OpenAI-compatible probe parsing `x-ratelimit-*` headers (Groq, Together, …).
- Informational windows (spend totals without a known limit) render without bar/forecast.

### Fixed
- Anthropic: meta headers (`7d-surpassed-threshold`, `overage-*`) no longer appear
  as phantom windows; statuses shown in German.
- z.ai: window labels derived from the entry's own unit/number fields
  (5-hour / weekly quota); auth errors reported as HTTP 200 with `code:401` are
  detected.
- Remaining counts now read "noch X von Y" instead of an ambiguous fraction.

## [1.0.0] - 2026-10-14

### Added
- Windows tray app with hover popup: per-account quota windows, balances,
  burn-rate forecast vs. reset countdown, 60-minute sparklines.
- Providers: Anthropic (unified rate-limit headers, 5h/7d windows),
  Kimi Code subscription, Moonshot/Kimi balance, GLM (z.ai / BigModel), DeepSeek.
- Settings dialog with per-account connection test; keys stored DPAPI-encrypted.
- Headless import (`InsightTokenWatcher.exe --import`) and `InsightTokenWatcher.Cli` for debugging.
- Balloon warning when an account turns critical; optional autostart.
