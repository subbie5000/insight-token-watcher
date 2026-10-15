# Insight Token Watcher

LLM-Kontostände und Quota-Fenster im Windows-System-Tray. Mouse-over auf das Tray-Icon
öffnet das Live-Popup: Auslastung, Verbrauchsrate, Restlaufzeit und Countdown bis zum
Reset pro Konto – so siehst du sofort, ob dein Kontingent bis zum Reset reicht.

## Unterstützte Anbieter

| Anbieter | Datenquelle | Fenster |
|---|---|---|
| Anthropic (API-Key oder OAuth/Setup-Token) | Rate-Limit-Header auf einem Mini-Call (`max_tokens=1`, Haiku) | 5-Std-, 7-Tage-, ggf. Sonnet-/Opus-Woche |
| Kimi Code (Abo) | `GET api.kimi.com/coding/v1/usages` | Wochenkontingent + 5-Std-Fenster |
| Moonshot / Kimi Guthaben (Prepaid) | `GET api.moonshot.ai/v1/users/me/balance` | Guthaben (kein Reset) |
| GLM / z.ai / BigModel | `GET {base}/api/monitor/usage/quota/limit` | Token-/Credit-Kontingente mit Reset |
| DeepSeek | `GET api.deepseek.com/user/balance` | Guthaben (kein Reset) |
| OpenAI | `GET /v1/organization/costs` (Admin-Key) oder Legacy `credit_grants` | Ausgaben 30 d / Guthaben |
| OpenRouter | `GET /api/v1/key` + `GET /api/v1/credits` | Key-Limit + Guthaben |
| OpenAI-kompatibel (generisch: Groq, Together, Cerebras, …) | `x-ratelimit-*`-Header eines Mini-`chat/completions`-Calls | Requests-/Token-Limits |

## Installation

1. `InsightTokenWatcher.exe` irgendwo ablegen, z. B. `C:\Tools\InsightTokenWatcher\InsightTokenWatcher.exe`.
2. Starten – das Icon erscheint im Tray (ggf. im Überlauf-Menü „^“).
3. Rechtsklick → **Einstellungen…** → **Neu…** pro Konto:
   - Name frei wählbar (z. B. „Anthropic Firma“, „Anthropic privat“, …)
   - Anbieter wählen, API-Schlüssel einfügen
   - Bei Anthropic: **Auth-Typ** wählen – `API-Key` für `sk-ant-api…`-Schlüssel,
     `OAuth / Bearer` für Subscription-Setup-Tokens (`sk-ant-oat…`)
   - **Verbindung testen** prüft sofort, ob Daten kommen.
4. „Mit Windows starten“ aktiviert Autostart.

Die API-Schlüssel liegen DPAPI-verschlüsselt (pro Windows-Benutzer) in
`%APPDATA%\InsightTokenWatcher\config.json` – im Klartext sind sie nirgends.

## Bedienung

- **Mouse-over aufs Tray-Icon** → Popup mit allen Konten.
- **Popup am Titel ziehen** → an beliebiger Stelle auf beliebigem Monitor parken
  (heftet automatisch an; Position bleibt über Neustarts erhalten).
- **Klick auf ein Konto** im Popup → sofortige Neumessung.
- **Farben**: lime = OK, amber = knapp, rot = kritisch/Fehler.
- Pro Fenster: Balken + „verbraucht X %“, Prognosezeile
  („Noch 3 h 41 min · Reset in 2 h 14 min – reicht bis zum Reset“ bzw.
  „Leer in … – Reset erst in …!“) und ein 60-Minuten-Verlauf.
- Guthaben-Konten (DeepSeek/Moonshot): Restbetrag + „reicht noch N Tage“.
- Balloon-Warnung, sobald ein Konto kritisch wird (abschaltbar).

## Wie die Prognose funktioniert

Jede Minute (einstellbar 30–3600 s) wird der Stand abgefragt. Aus den letzten
60 Minuten berechnet die App die Verbrauchsrate (lineare Regression) und daraus
die Zeit bis zum Leerlaufen. Diese wird gegen die Restzeit bis zum Reset gestellt:

- **OK** – Restlaufzeit > Zeit bis Reset (du schaffst es)
- **KNAPP** – Restlaufzeit nur knapp über der Reset-Zeit (< 15 % Puffer)
- **KRITISCH** – Kontingent geht vor dem Reset leer

Quota-Resets und Guthaben-Aufladungen werden erkannt und setzen die Rate-Berechnung
zurück. Guthaben ohne Reset: OK > 2 Tage, KNAPP > 12 h, KRITISCH darunter.

## Kosten der Messung

Anthropic wird über einen Mini-Request (1 Output-Token, Haiku) abgefragt:
≈ 0,00002 $ pro Abfrage, also ~2–3 Cent/Monat je Konto bei 60-s-Intervall.
Die anderen Anbieter lesen reine Status-Endpunkte (kostenlos).

## Build aus dem Quellcode

Voraussetzung: .NET 8 SDK. Dann:

```powershell
.\build.ps1
```

Ergebnis: `dist\InsightTokenWatcher.exe` (single-file, self-contained – keine Runtime nötig).
Tests (Provider-Parser + Prognose-Logik): `dotnet run --project tests\InsightTokenWatcher.CoreTests`
