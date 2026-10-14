# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
- Headless import (`TokenWatch.exe --import`) and `TokenWatch.Cli` for debugging.
- Balloon warning when an account turns critical; optional autostart.
