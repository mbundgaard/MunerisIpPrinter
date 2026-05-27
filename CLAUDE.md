# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

WPF desktop app (`net9.0-windows`, nullable + implicit usings enabled) that listens on TCP 9100 for ESC/POS receipt data from a POS system and renders each receipt in a tab. Multiple printers map to loopback addresses (127.0.0.1, 127.0.0.2, …) — the listener routes incoming connections to the right tab by local-endpoint IP. Single external NuGet dep: `System.Text.Encoding.CodePages`.

## Build / Run

- `dotnet build` — debug build to `bin\Debug\net9.0-windows\MunerisIpPrinter.exe`
- `.\build.ps1` — single-file self-contained release publish to `bin\Release\net9.0-windows\win-x64\publish\MunerisIpPrinter.exe` (add `-Open` to open the folder)
- No test project — do not invent one. Verify changes by running the app.

## Architecture notes

- One `PrintListener` on 0.0.0.0:9100 serves all configured printers. Routing is by the connection's *local* endpoint (last octet), not the remote endpoint.
- `WebApiServer` (port 9101, `/screenshot`) is only started in single-tab mode (`!_multiTab`).
- Persistence: `MunerisIpPrinter.json` next to the .exe holds settings; `MunerisIpPrinter.bin` is a multi-slot binary blob holding logo bitmaps (slot = address octet) and per-printer history (slot = 1000 + octet). `SlotStore` writes serialize the whole file under a lock.
- Settings changes require a full app restart — `MainWindow.OpenSettings` relaunches via `cmd.exe /c timeout 2 & start "" "<exe>"` and calls `Application.Shutdown()`. There is no live reload; remind the user if they edit settings code.
- Receipt rendering: `EscPosParser` consumes a stream and emits jobs on `GS V` (cut); `EscPosTextExtractor` decodes bytes via codepage tables; `LogoBitmap` decodes `GS * x y data` to a WPF `BitmapSource`.

## Conventions

- File-scoped namespaces, nullable annotations, `readonly` fields on services.
- Be terse — skip end-of-turn "here's what I changed" recaps; the diff is visible.
