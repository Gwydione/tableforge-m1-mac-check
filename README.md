# TableForge Mac Check — macOS validation harness

A **test harness**, not TableForge and not a release. It validates selected TableForge 1.0.0 behaviour on macOS:
copying tables from PDFs in Preview (E2), 3D dddice dice embedded with Avalonia's NativeWebView/WKWebView (E4), result
editing with macOS shortcuts (E3), and optionally dice in Safari (E1).

This public repository exists only to run a manually triggered GitHub Actions trial on standard macOS runners
(`.github/workflows/mac-check-trial.yml`). There are no releases and no installable packages here (the trial's test
packages exist only as short-lived workflow artifacts; they are ad-hoc signed and not notarized).

- `src/MacCheck.Core` — UI-free code, including byte-identical copies of 26 TableForge 1.0.0 source files (see
  `PROVENANCE.md`); `src/MacCheck.App` — the Avalonia wizard (including a development `--selftest` mode).
- `tests/MacCheck.Tests` — automated tests (model, privacy, packaging, headless UI).
- `corpus/` — 8 synthetic test PDFs with expected tables; `tests/MacCheck.Tests/e2-fixtures/` — their text as captured in
  other PDF readers; `tests/MacCheck.Tests/preview-runner-fixtures/` — their text as macOS Preview copied it on the
  runners of the first trial.
- `packaging/macpack.py` — assembles, verifies and zips the macOS app bundle.
- `packaging/macos-package-trial.sh` — on the macOS runner: seals the bundle with Apple's `codesign`, checks the seal
  before and after zip round trips (ditto, Archive Utility, unzip), and a bounded Gatekeeper assessment.

Build: .NET SDK 10.0.401 (`global.json`), Avalonia 12.1.3, Avalonia.Controls.WebView 12.1.0.

```
dotnet test tests/MacCheck.Tests
dotnet publish src/MacCheck.App -c Release -r osx-arm64 --self-contained -o build/publish
python3 packaging/macpack.py bundle --publish build/publish --rid osx-arm64 --version 0.1.0 --build-id local --icon packaging/icon-source.png --out build/app
```

The dice test contacts dddice.com with a temporary anonymous guest. Nothing here uses credentials.

Copyright (c) 2026 RPG Frequencies. All rights reserved — see `LICENSE.txt`. Not open source.
