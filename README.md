# Muneris IP Printer

A Windows desktop app that listens on TCP **9100** for ESC/POS receipt data from
POS systems and renders each receipt on-screen. Up to 15 logical printers map to
loopback addresses (`127.0.0.1`, `127.0.0.2`, …) — incoming connections are
routed to the right view by their local-endpoint IP, so a single host can stand
in for a whole kitchen's worth of printers.

## Install

Download the latest single-file `MunerisIpPrinter.exe` from the
[Releases](https://github.com/mbundgaard/MunerisIpPrinter/releases) page. No
installer needed — just run it. The app checks GitHub for newer releases at
startup and surfaces a small "update available" link in the sidebar when there
is one.

## Configure

Open the hamburger menu in the bottom-left of the sidebar → **Settings**. Add
one entry per printer. Each gets a loopback address (`127.0.0.1`, `.2`, …) —
point the corresponding POS printer record at that address on port `9100`.
Settings changes apply after a restart.

You can rename a printer at any time by hovering its sidebar row and clicking
the pencil icon. The name is persisted immediately.

## Features

- **Sidebar of printers** with new-receipt count badge and inline rename
- **Receipt viewer** — accurate ESC/POS rendering including codepage switches
  and downloaded bit-image logos
- **Copy text / Copy image** above each receipt (image is published as both
  CF_BITMAP and PNG for paste compatibility across Slack, browsers, Word, etc.)
- **Per-printer history** — keep the last N receipts on disk and replay them
  any time
- **Resizable sidebar** — the width is persisted across runs
- **`/screenshot` HTTP endpoint** on `localhost:9101` for capturing the current
  window as a PNG

## Build from source

```powershell
dotnet build           # debug build → bin\Debug\net9.0-windows\MunerisIpPrinter.exe
.\build.ps1            # single-file self-contained release → bin\Release\…\publish\MunerisIpPrinter.exe
```

Targets `net9.0-windows`. Single external NuGet dependency:
`System.Text.Encoding.CodePages`.

## License

MIT — see [LICENSE](LICENSE).
