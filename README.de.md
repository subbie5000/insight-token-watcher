# Insight Token Watcher

LLM-Kontostände und Quota-Fenster im Windows-System-Tray. Mouse-over auf das Tray-Icon
öffnet das Live-Popup: Auslastung, Verbrauchsrate, Restlaufzeit und Countdown bis zum
Reset pro Konto – so siehst du sofort, ob dein Kontingent bis zum Reset reicht.

## Unterstützte Anbieter

| Anbieter | Datenquelle | Fenster |
|---|---|---|
| Anthropic (API-Key oder OAuth/Setup-Token) | Rate-Limit-Header auf einem Mini-Call (`max_tokens=1`, Haiku); Modell-Limits aus `GET /api/oauth/usage` | 5-Std-, 7-Tage-Fenster, Modell-Wochen (Fable, …) |
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

## Installation (Linux)

Dieselbe App, dieselbe Mess-Engine – Avalonia-Build für Linux (Ubuntu 22.04+, GNOME/KDE/Xfce):

```bash
# entweder: .deb-Paket aus den Releases
sudo dpkg -i insight-token-watcher_1.6.0_amd64.deb

# oder: selbstständiges Einzel-Binary (keine .NET-Runtime nötig)
tar xzf insight-token-watcher-linux-x64.tar.gz
./insight-token-watcher
```

**Fedora / Nobara / RHEL** (rpm-basiert):

```bash
sudo dnf install ./insight-token-watcher-1.6.0-1.x86_64.rpm
# oder distro-unabhängig: das tar.gz-Binary von oben läuft überall
```

- Tray-Icon per StatusNotifier/AppIndicator; auf Desktops ohne Tray öffnet sich das
  Popup beim Start, das Menü erreicht man dann über das Zahnrad im Popup.
- Konten wie unter Windows unter **Einstellungen…** anlegen; `--import accounts.json`
  importiert headless (gleiches Format wie Windows).
- Autostart über „Mit Anmeldung starten“ (`~/.config/autostart/*.desktop`).

Schlüssel auf Linux: Es gibt kein DPAPI. Die Keys liegen in
`~/.config/insight-token-watcher/config.json` mit Rechten 0600 (nur der Besitzer) und
einem `v2:`-Marker – die App synct sie nie irgendwohin. Wer es härter will, hält
`~/.config` auf einem verschlüsselten Home.

Unterschiede zu Windows: kein Hover aufs Tray (Klick schaltet das Popup um), das Popup
verankert sich unten rechts (per Titel ziehbar, Position wird gemerkt), Warnmeldungen
kommen über `notify-send`, sofern installiert.

## Bedienung

- **Mouse-over aufs Tray-Icon** → Popup mit allen Konten.
- **Popup am Titel ziehen** → an beliebiger Stelle auf beliebigem Monitor parken
  (heftet automatisch an; Position bleibt über Neustarts erhalten).
- **Klick auf ein Konto** im Popup → sofortige Neumessung.
- **Fable-Limit:** Claude-Abos haben für das Fable-Modell ein eigenes Wochenlimit. Sobald
  ein Konto eins meldet, erscheint eine dritte Spalte **FABLE** (das Popup wird breiter).
  Ist die Fable-Woche aufgebraucht, wird das Konto amber markiert und trägt den Status
  „FABLE LEER“ – nie „GESPERRT“, weil alle anderen Modelle dort weiterlaufen.
- **Tempo-Strich:** Ein dünner senkrechter Strich auf jedem Balken zeigt, wie viel Zeit des
  Fensters schon um ist (eine Woche, die in zwei Tagen zurückgesetzt wird, ist zu 5/7 um).
  Endet der Balken links vom Strich, liegt der Verbrauch unter dem Zeitanteil. Das ist das
  *durchschnittliche* Tempo seit Fensterbeginn; Warnungen kommen weiterhin aus der Prognose
  (Verbrauch der letzten Stunde) – beide können sich widersprechen, etwa bei viel Verbrauch
  am Wochenanfang und keinem mehr seither. Mouse-over zeigt den genauen Anteil.
- **Glas (Windows 11 22H2+):** Das Popup liegt als durchscheinende Scheibe über dem
  verschwommenen Desktop. Die Deckkraft folgt der Helligkeit dahinter – über dunklem Desktop
  deutlich durchscheinend, über einem weißen Fenster fast deckend –, damit Text mindestens
  4,5:1 Kontrast behält. Dafür liest die App etwa einmal pro Sekunde die Helligkeit des
  Bildschirms rund um das Popup (eine Zahl; die Pixel werden nicht aufbewahrt). Abschaltbar
  unter *Einstellungen → Popup als Glas*; ohne Transparenzeffekte, im Hochkontrast-Modus und
  in Remote-Sitzungen bleibt das Popup ohnehin deckend.
- **Klick auf einen Spaltenkopf** sortiert: KONTO nach Name, 5 STUNDEN / WOCHE / FABLE danach,
  *welches Konto zuerst leer ist* (bald leere oben, dann die vollsten).
  Zweiter Klick dreht die Reihenfolge um („zuletzt leer“), dritter Klick stellt den
  Standard wieder her; die Wahl bleibt gespeichert.
- **Konten, die gerade nicht nutzbar sind**, stehen unabhängig von der Sortierung unter
  allen anderen, unter der Überschrift „NICHT NUTZBAR“ – das als Nächstes freie zuerst. Sie
  tragen den Status „GESPERRT“ und eine rote Karte. Rot steht nur für diesen Zustand.
- **Zellfarben** nach Verbrauch: lime = unbenutzt, amber = in Benutzung, rot = Limit
  erreicht, grau = veraltet. Ob ein Konto bis zum Reset reicht, sagen Prognosezeile und
  Zeilenhintergrund (amber bei knapp, bald leer oder Fehler).
- **Tray-Icon**: Ring mit der mittleren Wochenauslastung deiner Anthropic-Konten, Prozent
  in der Mitte; ein Eckpunkt markiert Konten mit Problemen.
- Pro Konto eine Karte, pro Fenster Prozentwert, Reset-Countdown und Balken. Ein
  unauffälliges Konto bleibt ruhig (zweite Zeile nennt den Anbieter); nur Ausnahmen
  bekommen ein farbiges Statuswort und eine Prognosezeile
  („Woche: leer in … – Reset erst in …!“).
- **Mouse-over auf ein Konto** zeigt alle Fenster mit genauer Reset-Zeit.
- Guthaben-Konten (DeepSeek/Moonshot): Restbetrag + Reichweite bei aktuellem Verbrauch.
- Balloon-Warnung, sobald ein Konto leerläuft oder gesperrt wird (abschaltbar).

## Wie die Prognose funktioniert

Jede Minute (einstellbar 30–3600 s) wird der Stand abgefragt. Aus den letzten
60 Minuten berechnet die App die Verbrauchsrate (lineare Regression) und daraus
die Zeit bis zum Leerlaufen. Diese wird gegen die Restzeit bis zum Reset gestellt:

- **OK** – Restlaufzeit > Zeit bis Reset (du schaffst es)
- **KNAPP** – Restlaufzeit nur knapp über der Reset-Zeit (< 15 % Puffer)
- **LÄUFT LEER** – Kontingent geht vor dem Reset leer

Quota-Resets und Guthaben-Aufladungen werden erkannt und setzen die Rate-Berechnung
zurück. Guthaben ohne Reset: OK > 2 Tage, KNAPP > 12 h, LÄUFT LEER darunter.

**GESPERRT** ist keine Prognose, sondern der Stand jetzt: Ein allgemeines Limit steht bis
zum Reset bei 100 %, oder ein Prepaid-Guthaben kann keine Anfragen mehr bezahlen (DeepSeek
meldet `is_available: false`; Moonshot und OpenRouter bei null oder darunter). Ein
aufgebrauchtes Modell-Limit (Fable) sperrt das Konto nicht. Weil die Prognose die letzte
Stunde hochrechnet, ist ein stark genutztes Konto schnell „LÄUFT LEER“ – es funktioniert
weiter; „GESPERRT“ nicht.

## Kosten der Messung

Anthropic wird über einen Mini-Request (1 Output-Token, Haiku) abgefragt:
≈ 0,00002 $ pro Abfrage, also ~2–3 Cent/Monat je Konto bei 60-s-Intervall.
Die anderen Anbieter lesen reine Status-Endpunkte (kostenlos).

Das Fable-Limit eines Claude-Abos wird alle 5 Minuten aus dem Usage-Endpunkt des Abos
gelesen – das kostet keine Tokens. Setup-Tokens (`claude setup-token`) dürfen ihn nicht
lesen; für sie sendet die App stattdessen alle 15 Minuten eine 1-Token-Anfrage mit dem
Fable-Modell – die einzige Anfrage, die das gemessene Fable-Limit selbst berührt.

## Build aus dem Quellcode

Voraussetzung: .NET 8 SDK. Dann:

```powershell
.\build.ps1
```

Ergebnis: `dist\InsightTokenWatcher.exe` (single-file, self-contained – keine Runtime nötig).
Tests (Provider-Parser + Prognose-Logik): `dotnet run --project tests\InsightTokenWatcher.CoreTests`
