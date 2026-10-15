# Contributing

Contributions welcome. Hard constraints:

- **No new runtime dependencies** for the core library (InsightTokenWatcher.Core) —
  `System.Text.Json` and the BCL only. The app project currently adds only
  `System.Security.Cryptography.ProtectedData`.
- Provider parsers must stay defensive: unknown JSON shapes must degrade to a
  readable error, never to an exception.
- Every parser change needs a fixture test in `tests/InsightTokenWatcher.CoreTests`
  (record the real response shape in the test).
- Run `dotnet run --project tests/InsightTokenWatcher.CoreTests` before submitting; CI is green or no merge.
- Code, comments and docs in English. The tray UI strings are intentionally German.
