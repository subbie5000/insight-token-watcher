# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
