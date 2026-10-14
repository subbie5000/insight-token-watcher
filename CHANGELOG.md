# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.0] - 2026-10-14

### Added
- Windows tray app with hover popup: per-account quota windows, balances,
  burn-rate forecast vs. reset countdown, 60-minute sparklines.
- Providers: Anthropic (unified rate-limit headers, 5h/7d windows),
  Kimi Code subscription, Moonshot/Kimi balance, GLM (z.ai / BigModel), DeepSeek.
- Settings dialog with per-account connection test; keys stored DPAPI-encrypted.
- Headless import (`TokenWatch.exe --import`) and `TokenWatch.Cli` for debugging.
- Balloon warning when an account turns critical; optional autostart.
