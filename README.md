# TokenWatch

LLM quota and balances in the Windows system tray. Hover the tray icon to get a live
popup: utilization per quota window, burn rate, projected time-to-empty and the
countdown to the reset — so you know at a glance whether your quota lasts until it
resets.

![status](https://img.shields.io/badge/platform-Windows-blue)

Supported providers:

| Provider | Data source | Windows shown |
|---|---|---|
| Anthropic (API key or OAuth / setup token) | Rate-limit headers on a minimal `POST /v1/messages` (`max_tokens=1`, Haiku) | 5-hour, 7-day, possibly Sonnet/Opus week |
| Kimi Code (subscription) | `GET api.kimi.com/coding/v1/usages` | Weekly quota + 5-hour window |
| Moonshot / Kimi balance (prepaid) | `GET api.moonshot.ai/v1/users/me/balance` | Balance (no reset) |
| GLM (z.ai / BigModel) | `GET {base}/api/monitor/usage/quota/limit` | Token/credit quotas with reset |
| DeepSeek | `GET api.deepseek.com/user/balance` | Balance (no reset) |

**UI language: German.** The core library, tests and docs are English; the tray UI
strings are German. PRs for i18n are welcome.

## Install

1. Download `TokenWatch.exe` from [Releases](../../releases) (or build it, see below)
   and put it anywhere, e.g. `C:\Tools\TokenWatch\TokenWatch.exe`. No .NET runtime
   needed (self-contained single file).
2. Start it — the icon appears in the tray (possibly behind the "‹" overflow).
3. Right-click → **Einstellungen…** → **Neu…** per account:
   - Choose the provider, paste the API key, hit **Verbindung testen**.
   - Anthropic: pick **API-Key** for `sk-ant-api…` keys, **OAuth / Bearer** for
     subscription setup tokens (`sk-ant-oat…`).
4. Optional: enable "Mit Windows starten" (autostart).

API keys are stored DPAPI-encrypted (`%APPDATA%\TokenWatch\config.json`,
`LocalMachine` scope) — never in plaintext.

## Usage

- **Hover the tray icon** → popup with all accounts.
- **Click an account card** → re-poll that account immediately.
- **Colors**: lime = OK, amber = tight, red = critical/error.
- Per window: utilization bar, a forecast line
  ("Noch 3 h 41 min · Reset in 2 h 14 min – reicht bis zum Reset" vs.
  "Leer in … – Reset erst in …!") and a 60-minute history sparkline.
- Balance accounts (DeepSeek/Moonshot): remaining credit + "lasts N days".
- Balloon warning when an account turns critical (can be disabled).

## How the forecast works

Every account is polled on its own interval (default 60 s). From the samples of the
last 60 minutes the app computes a burn rate (least-squares slope) and projects the
time-to-empty, which is then compared against the time-to-reset:

- **OK** – time-to-empty > time-to-reset (you make it)
- **KNAPP** (tight) – less than 15 % margin
- **KRITISCH** (critical) – the window runs dry before it resets

Quota resets and balance top-ups are detected and reset the rate calculation.
Balance accounts without reset: OK > 2 days, tight > 12 h, critical below.

## Measurement cost

Anthropic is polled via a minimal request (1 output token, Haiku): ≈ $0.00002 per
poll, i.e. ~2–3 ct/month per account at the 60 s default. All other providers use
free status endpoints.

## Headless import & CLI

`TokenWatch.exe --import accounts.json` imports accounts without starting the GUI
(keys are encrypted on the fly). Format:

```json
{
  "accounts": [
    { "name": "Anthropic Main", "provider": "anthropic", "auth": "bearer", "apiKey": "sk-ant-…" },
    { "name": "DeepSeek", "provider": "deepseek", "apiKey": "sk-…" }
  ]
}
```

`provider`: `anthropic` | `kimicode` | `moonshot` | `zai` | `deepseek`;
optional: `auth` (`apikey`/`bearer`), `baseUrlOverride`, `model`, `pollSeconds`, `enabled`.

`TokenWatch.Cli accounts.json` polls once and prints the result (handy for debugging
credentials). On Windows, `TokenWatch.Cli --app-config` does the same against the
encrypted app config.

## Note on Anthropic OAuth tokens

`claude` OAuth access tokens expire after hours and are rotated by whichever tool
owns the refresh token. TokenWatch deliberately does **not** refresh tokens: the
refresh token belongs to the tool that issued the login, and consuming it elsewhere
would break that tool. When a token expires, the account shows a 401 error — paste a
fresh one, or create a long-lived token once via `claude setup-token` and use that.

## Build

Requires the .NET 8 SDK:

```powershell
.\build.ps1
```

Output: `dist\TokenWatch.exe` (single-file, self-contained).
Tests (provider parsers + forecast logic):

```powershell
dotnet run --project tests\TokenWatch.CoreTests
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
