# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

WPF desktop app (SDK-style csproj, `net462`, nullable + implicit usings, `LangVersion=latest`)
that listens on TCP 9100 for ESC/POS receipt data and renders each receipt on screen.
Target deployment is a portable single ~1 MB .exe everyone keeps in their toolkit and copies into
test environments — `.NET Framework 4.6.2` is the in-box runtime on every Windows 10+/Server 2016+
machine, so no installer or self-contained bundle is needed. No external NuGet dependencies.

Up to 15 logical printers map to loopback addresses (127.0.0.1, 127.0.0.2, …). Each address has its
own listener socket so routing is intrinsic to the socket (and so Windows Defender Firewall stays
quiet — loopback-only listeners don't trigger the "allow access" prompt).

## Build / Run

- `dotnet build` — debug build to `bin\Debug\net462\MunerisIpPrinter.exe`
- `.\build.ps1` — release build, copies single .exe to `bin\Release\publish\MunerisIpPrinter.exe` (~960 KB)
- `.\build.ps1 -Bump` — auto-bump `<Version>` to `yyyy.M.d.<prev+1>` (CalVer) before building
- `.\build.ps1 -Open` — opens the publish folder in Explorer
- No test project — do not invent one. Verify changes by running the app.

## Release flow

1. `.\build.ps1 -Bump` (writes new version into csproj, builds)
2. `git add -A && git commit -m "vYYYY.M.D.B: …"`
3. `git tag -a vYYYY.M.D.B -m "…"`
4. `git push origin main vYYYY.M.D.B`
5. `gh release create vYYYY.M.D.B 'bin\Release\publish\MunerisIpPrinter.exe' --title '…' --notes '…' --latest`

The published asset is always `MunerisIpPrinter.exe` (stable filename) so user shortcuts survive
auto-update swaps. The version is embedded in the assembly only.

## Architecture notes

- **Listener.** `PrintListener` binds one `TcpListener` per configured `127.0.0.X` address on port
  9100. `BindWithRetry` retries `AddressAlreadyInUse` for ~4 s to cover the brief window during
  restart where the prior instance is still releasing the port. Routing is implicit (each accepted
  connection's local endpoint matches its listener's address). `AcceptTcpClientAsync` has no
  `CancellationToken` overload on net462 — cancellation is achieved by calling `listener.Stop()`,
  which makes the pending accept throw. Same pattern for `ReadAsync`/`WriteAsync`: use the
  `(buffer, offset, count, ct)` overload, not the `Span` or `Memory<byte>` variants (net462 lacks
  those).
- **WebApiServer** (port 9101) is started unconditionally on app load — a loopback-only local
  HTTP API meant for an AI agent to drive receipt-design iteration. Routes: `GET /` (a
  self-describing plaintext guide listing the live printers + the send/fetch loop), `GET /printers`,
  `GET /latest?printer=N` (PNG of that instance's newest receipt paper only — reuses
  `PrinterView.RenderNewestReceiptPng` → `RenderBorder`), `GET /latest.txt?printer=N`
  (`EscPosTextExtractor`), `GET /screenshot` (whole window), `POST /clear` (clear all receipts, or
  `?printer=N` for one — live, no restart), and `POST /printers/add|rename|remove`
  which save `AppSettings` then `RestartToApply()` (no live reload — same restart path as the
  Settings dialog). `MainWindow` implements `IApiHost`; the server marshals every host call onto the
  UI thread via `Dispatcher`. `printer=N` is the loopback last octet (1-based). No JSON — reads are
  plaintext, writes take query params. The hamburger menu's **Copy AI prompt** item copies a short
  seed prompt that points an agent at `http://127.0.0.1:9101/` to self-discover the rest.
- **Persistence.** Everything lives in `%LOCALAPPDATA%\MunerisIpPrinter\MunerisIpPrinter.bin`,
  a multi-slot binary blob keyed by int. Slot layout:
  - **slot 0** — `AppSettings` (binary, versioned, written via `BinaryReader`/`BinaryWriter`)
  - **slots 1..255** — per-printer logo bitmaps, keyed by the loopback address's last octet
  - **slots 1000..1255** — per-printer receipt history, keyed by `1000 + octet`
  `SlotStore` serializes every write under a lock (whole file rewrite). The .exe folder is left
  pristine. A static migrator in `AppSettings` moves a legacy bin next to the .exe into the
  AppData location on first run.
- **No JSON anywhere.** net462 has no `System.Text.Json` in-box; settings are binary. The one
  remaining JSON consumer (`UpdateChecker` parsing GitHub's release API) uses a ~50-line
  hand-rolled field extractor — do not introduce a JSON NuGet.
- **Settings changes require a restart.** `MainWindow.OpenSettings` calls
  `Relauncher.RelaunchAfterExit(exe)` and then `Application.Current.Shutdown()`. There is no live
  reload — remind the user if they're editing the settings model and expecting in-session effect.
- **Relauncher** spawns a detached PowerShell that `Wait-Process`es on the current PID, optionally
  moves a downloaded .exe into place (the auto-update path), then `Start-Process`es the target.
  Replaces the old "sleep N seconds and hope" cmd-based pattern that lost the restart race.
- **Auto-update.** Three steps, all driven from `MainWindow.PollForUpdatesAsync` (fires at startup
  and every 4 hours):
  1. `UpdateChecker.CheckAsync` hits `repos/mbundgaard/MunerisIpPrinter/releases/latest`, compares
     tag (`vMajor.Minor.Build.Revision`) to running version on all four components, and extracts
     the `MunerisIpPrinter.exe` asset URL (falls back to legacy `MunerisIpPrinter-<version>.exe`).
  2. `UpdateApplier.DownloadAsync` streams the asset to
     `%TEMP%\MunerisIpPrinter-update-<version>.exe.partial`, then atomic-renames to the
     non-`.partial` name on success.
  3. Sidebar link flips to **Update ready!** — clicking calls `UpdateApplier.ApplyAndExit`. Or,
     on next launch, `App.OnStartup` detects any staged temp file with a higher version, hands
     off to `Relauncher`, and shuts down before any port is bound.
- **Receipt rendering.** `EscPosParser` walks the byte stream and emits events for cuts (`GS V`),
  status-query responses, and logo definitions (`GS *`). Receipts are split at cuts.
  `EscPosTextExtractor` decodes the text honoring `ESC t` codepage switches (net462 has all
  codepages in-box, no `CodePagesEncodingProvider.RegisterProvider` needed). `LogoBitmap` decodes
  `GS * x y data` into a WPF `BitmapSource`.
- **Stacked receipts.** Each `PrinterView` is a single `ScrollViewer` with all the printer's
  receipts stacked newest-on-top. No chip-based selection. Each receipt has hover-revealed copy
  icons (text + image). The image copy puts both `CF_BITMAP` and `PNG` on the clipboard so
  paste-targets like Slack/browsers (PNG-only) and Word/Paint (CF_BITMAP) both work.

## net462 gotchas to remember

These are the BCL/language features that aren't on net462 — if you reach for them, prefer the
workarounds in parens:

- `Math.Clamp` → inline `value < min ? min : value > max ? max : value`
- `Span<T>`/`ReadOnlySpan<T>` → `byte[] + offset + length`
- `s[a..b]` range slicing → `s.Substring(a, b - a)`
- `arr[^1]` from-end indexing → `arr[arr.Length - 1]`
- `Encoding.Latin1` → `Encoding.GetEncoding(28591)`
- `Environment.ProcessPath` → `Assembly.GetEntryAssembly()?.Location`
- `AcceptTcpClientAsync(CancellationToken)` / `ReadAsync(byte[], CancellationToken)` /
  `WriteAsync(byte[], CancellationToken)` → use the parameterless or 4-arg
  `(buf, offset, count, ct)` overloads and rely on socket-close to cancel
- `HttpClient.GetStringAsync(url, ct)` → drop the `ct` overload
- `init` / `required` / `record struct` → already covered by
  `Infrastructure/CompilerPolyfills.cs`; if a new C# feature error pops up, add the polyfill
  attribute there rather than removing the language feature
- `System.Net.Http` is in a separate assembly — already added as `<Reference>` in csproj
- TLS 1.2 isn't always default on net462 → `UpdateChecker`/`UpdateApplier` set
  `ServicePointManager.SecurityProtocol |= Tls12`

## Conventions

- File-scoped namespaces, nullable annotations, `readonly` fields on services.
- Versioning is CalVer (`yyyy.M.d.build`). Bump via `build.ps1 -Bump`; don't edit `<Version>`
  by hand.
- Tags match the version exactly with a `v` prefix: `v2026.5.30.13`.
- Native `MessageBox` is banned; use `ConfirmDialog.Ask(...)` for yes/no, `ConfirmDialog.Show(...)`
  for OK-only. Keeps the dark theme intact.
- Be terse — skip end-of-turn "here's what I changed" recaps; the diff is visible.
