---
name: publish
description: Publish a single-file self-contained release build of Muneris IP Printer by running build.ps1, then report the output .exe path and size. Use when the user says "publish", "ship a build", or "make a release exe".
disable-model-invocation: true
---

Run the project's `build.ps1` to produce the single-file release exe.

Steps:

1. Run `pwsh -File .\build.ps1` from the repo root. (Use the PowerShell tool — `build.ps1` sets `$ErrorActionPreference = 'Stop'` and throws on failure.)
2. If the build fails, surface the error message verbatim and stop — do not retry.
3. On success, report the output path and size from the script's "Built:" / "Size:" lines. Output path is `bin\Release\net9.0-windows\win-x64\publish\MunerisIpPrinter.exe`.

If `$ARGUMENTS` contains `open` or `-Open`, pass `-Open` to the script so the publish folder opens in Explorer afterwards.

Do not run `dotnet publish` directly — the script sets specific flags (`PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, no debug symbols) and cleans the publish folder first.
