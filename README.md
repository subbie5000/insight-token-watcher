# Insight Token Watcher

LLM quota and balances in the Windows system tray. Hover the tray icon to get a live
popup: utilization per quota window, burn rate, projected time-to-empty and the
countdown to the reset — so you know at a glance whether your quota lasts until it
resets.

![status](https://img.shields.io/badge/platform-Windows-blue)

Supported providers:

| Provider | Data source | Windows shown |
|---|---|---|
| Anthropic (API key or OAuth / setup token) | Rate-limit headers on a minimal `POST /v1/messages` (`max_tokens=1`, Haiku); model-scoped limits from `GET /api/oauth/usage` | 5-hour, 7-day, model weeks (Fable, …) |
| Kimi Code (subscription) | `GET api.kimi.com/coding/v1/usages` | Weekly quota + 5-hour window |
| Moonshot / Kimi balance (prepaid) | `GET api.moonshot.ai/v1/users/me/balance` | Balance (no reset) |
| GLM (z.ai / BigModel) | `GET {base}/api/monitor/usage/quota/limit` | Token/credit quotas with reset |
| DeepSeek | `GET api.deepseek.com/user/balance` | Balance (no reset) |
| OpenAI | `GET /v1/organization/costs` (Admin key) or legacy `credit_grants` | Spend 30 d / balance |
| OpenRouter | `GET /api/v1/key` + `GET /api/v1/credits` | Key limit + balance |
| OpenAI-compatible (generic: Groq, Together, Cerebras, …) | `x-ratelimit-*` headers on a minimal `chat/completions` call | Requests/tokens limits |

**UI language: German.** The core library, tests and docs are English; the tray UI
strings are German. PRs for i18n are welcome.

## Install

1. Download `InsightTokenWatcher.exe` from [Releases](../../releases) (or build it, see below)
   and put it anywhere, e.g. `C:\Tools\InsightTokenWatcher\InsightTokenWatcher.exe`. No .NET runtime
   needed (self-contained single file).
2. Start it — the icon appears in the tray (possibly behind the "‹" overflow).
3. Right-click → **Einstellungen…** → **Neu…** per account:
   - Choose the provider, paste the API key, hit **Verbindung testen**.
   - Anthropic: pick **API-Key** for `sk-ant-api…` keys, **OAuth / Bearer** for
     subscription setup tokens (`sk-ant-oat…`).
4. Optional: enable "Mit Windows starten" (autostart).

API keys are stored DPAPI-encrypted (`%APPDATA%\InsightTokenWatcher\config.json`,
`LocalMachine` scope) — never in plaintext.

## Install (Linux)

Same app, same engine — Avalonia build for Linux desktops (Ubuntu 22.04+, GNOME/KDE/Xfce).

```bash
# either: .deb package from Releases
sudo dpkg -i insight-token-watcher_1.6.0_amd64.deb

# or: self-contained single binary (no .NET runtime needed)
tar xzf insight-token-watcher-linux-x64.tar.gz
./insight-token-watcher
```

**Fedora / Nobara / RHEL** (rpm-based):

```bash
sudo dnf install ./insight-token-watcher-1.6.0-1.x86_64.rpm
# or distro-independent: the tarball binary above works everywhere
```

- The app appears as a tray icon (StatusNotifier/AppIndicator; on desktops without a
  tray host the popup opens at startup instead and you reach the menu via the popup's
  gear icon).
- Accounts are added in **Einstellungen…** exactly like on Windows.
- **`--import accounts.json`** imports accounts headlessly (same format as Windows).
- Optional autostart via **Mit Anmeldung starten** (`~/.config/autostart/*.desktop`).

Key storage: there is no DPAPI on Linux. Keys are written to
`~/.config/insight-token-watcher/config.json` with mode 0600 (owner-only) and a `v2:`
marker so they never look like DPAPI blobs; the file is never synced by the app itself.
For hardened keyrings you can keep the whole `~/.config` on an encrypted home.

Linux differences: no hover-on-tray (click toggles the popup), the popup anchors at the
bottom-right of the primary screen (drag it anywhere, position is remembered), and
crit notifications use `notify-send` when available.

## Usage

- **Hover the tray icon** → popup with all accounts; **left-click** pins it open.
- **Drag the popup by its title band** to park it anywhere, on any monitor. The position
  is remembered across restarts.
- One card per account: the **short window** (e.g. 5 h) in the first column, the
  **long window** (week) in the second, each with bar, percentage and reset countdown —
  so the same kind of window lines up across all accounts. Balance accounts show the
  remaining credit and how long it lasts.
- **Model limits (Fable):** Claude subscriptions have a separate weekly limit for the
  Fable model. As soon as an account reports one, a third column **FABLE** appears (the
  popup gets wider). A used-up Fable week marks the account amber with the status
  "FABLE LEER" — never as blocked, because every other model still runs on it.
- **Pace tick:** a thin vertical line on every bar marks how much of the window's time has
  passed (a week that resets in two days is 5/7 through). A bar that ends left of the tick is
  below the pace of time. It is the *average* pace since the window started; warnings still
  come from the forecast, which uses the burn rate of the last hour – the two can disagree
  (heavy use early in the week, none since). Hover a row for the exact share.
- **Glass (Windows 11 22H2+):** the popup is a translucent pane over the blurred desktop.
  Its opacity follows the brightness behind it – clearly translucent over a dark desktop,
  nearly solid over a white window – so text keeps a contrast of at least 4.5:1. For that
  the app reads the brightness of the screen around the popup about once a second (a single
  number; the pixels are not kept). Switch it off under *Einstellungen → Popup als Glas*;
  without transparency effects, in high-contrast mode and in remote sessions the popup is
  opaque anyway.
- A healthy account stays quiet (its second line names the provider). Only exceptions
  get a coloured status word and a forecast line ("Woche: leer in … – Reset erst in …!").
- **Hover a row** for details (all windows, exact reset times, forecasts); **click a
  row** to re-poll that account immediately.
- **Click a column title** to sort: KONTO by name, 5 STUNDEN / WOCHE / FABLE by *which
  account runs out first* (soon-empty accounts on top, then the fullest). Clicking
  again reverses the order ("zuletzt leer"), a third click restores the default; the
  choice is remembered.
- **Accounts that cannot be used right now** sit below all others under the caption
  "NICHT NUTZBAR", whatever the sort order – the one that works again first on top. They
  carry the status "GESPERRT" and a red card. Red is reserved for this state.
- **Cell colors** follow usage: lime = unused, amber = in use, red = limit reached,
  grey = stale. Whether an account lasts until its reset is answered by the forecast
  line and the row tint (amber for tight, soon-empty or failing accounts).
- If a poll fails, the last good values stay visible (marked stale) instead of
  being replaced by an error.
- Tray icon: a ring with the mean weekly usage of your Anthropic accounts and the percent
  in the middle; a corner dot marks accounts with problems.
- Balloon warning when an account starts running dry or gets blocked (can be disabled).

## How the forecast works

Every account is polled on its own interval (default 60 s; slowed to ≤ 5 min while
nothing changes, back to the base rate on the first change). From the samples of the
last 60 minutes the app computes a burn rate (least-squares slope) and projects the
time-to-empty, which is then compared against the time-to-reset:

- **OK** – time-to-empty > time-to-reset (you make it)
- **KNAPP** (tight) – less than 15 % margin
- **LÄUFT LEER** (runs dry) – the window runs dry before it resets

Quota resets and balance top-ups are detected and reset the rate calculation.
Balance accounts without reset: OK > 2 days, tight > 12 h, running dry below.

**GESPERRT** (blocked) is not a forecast but a fact about now: a general limit stands at
100 % until its reset, or a prepaid balance cannot pay for requests (DeepSeek reports
`is_available: false`; Moonshot and OpenRouter at or below zero). A used-up model limit
(Fable) does not block the account. Because the forecast extrapolates the last hour, an
account in heavy use is easily "LÄUFT LEER" – it still works; "GESPERRT" does not.

## Measurement cost

Anthropic is polled via a minimal request (1 output token, Haiku): ≈ $0.00002 per
poll, i.e. ~2–3 ct/month per account at the 60 s default. All other providers use
free status endpoints.

The model limits of a Claude subscription (Fable) are read every 5 minutes from the
subscription's own usage endpoint, which spends no tokens. Setup tokens
(`claude setup-token`) are not allowed to read it; for those the app sends one
1-token request with the Fable model every 15 minutes instead — the only request that
touches the Fable limit it measures.

## Headless import & CLI

`InsightTokenWatcher.exe --import accounts.json` imports accounts without starting the GUI
(keys are encrypted on the fly). Format:

```json
{
  "accounts": [
    { "name": "Anthropic Main", "provider": "anthropic", "auth": "bearer", "apiKey": "sk-ant-…" },
    { "name": "DeepSeek", "provider": "deepseek", "apiKey": "sk-…" }
  ]
}
```

`provider`: `anthropic` | `kimicode` | `moonshot` | `zai` | `deepseek` | `openai` |
`openrouter` | `genericopenai`;
optional: `auth` (`apikey`/`bearer`), `baseUrlOverride`, `model`, `pollSeconds`, `enabled`.
Accounts are matched by name, so re-importing updates keys in place (e.g. from a
token-rotation job); a running instance reloads the config automatically.

`InsightTokenWatcher.Cli accounts.json` polls once and prints the result (handy for debugging
credentials). On Windows, `InsightTokenWatcher.Cli --app-config` does the same against the
encrypted app config.

`InsightTokenWatcher.exe --render <dir> [--scale 1.5]` renders the popup with synthetic demo
data into PNG files (plus a scroll/update stress check) — useful for UI work without
touching the tray.

## Note on Anthropic OAuth tokens

`claude` OAuth access tokens expire after hours and are rotated by whichever tool
owns the refresh token. Insight Token Watcher deliberately does **not** refresh tokens: the
refresh token belongs to the tool that issued the login, and consuming it elsewhere
would break that tool. When a token expires, the account keeps showing its last
values marked "Token abgelaufen" until a fresh token arrives — paste one, re-import
it, or create a long-lived token once via `claude setup-token`.

## Build

Requires the .NET 8 SDK:

```powershell
.\build.ps1
```

Output: `dist\InsightTokenWatcher.exe` (single-file, self-contained).
Tests (provider parsers + forecast logic):

```powershell
dotnet run --project tests\InsightTokenWatcher.CoreTests
```

## Security & privacy

- Keys never leave the machine except to the provider they belong to; at rest they
  are DPAPI-encrypted (`LocalMachine` scope — any process on the same machine could
  decrypt them; other machines cannot).
- No telemetry, no third-party endpoints.
- The app only ever *reads* quota/balance endpoints; the Anthropic probe is a
  1-token request.

## License

MIT — see [LICENSE](LICENSE). German docs: [README.de.md](README.de.md).
