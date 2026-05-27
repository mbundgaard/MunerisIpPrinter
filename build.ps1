# Builds Muneris IP Printer as a single self-contained Windows .exe.
# Output: bin\Release\net9.0-windows\win-x64\publish\MunerisIpPrinter.exe
#
# Usage:
#   .\build.ps1            # build only
#   .\build.ps1 -Open      # build and open the publish folder in Explorer

param(
    [switch]$Open
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$rid = 'win-x64'
$publishDir = Join-Path 'bin\Release\net9.0-windows' "$rid\publish"

if (Test-Path $publishDir) {
    Write-Host "Cleaning previous publish output..." -ForegroundColor DarkGray
    Remove-Item $publishDir -Recurse -Force
}

Write-Host "Publishing single-file self-contained .exe ($rid)..." -ForegroundColor Cyan
dotnet publish `
    -c Release `
    -r $rid `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $publishDir 'MunerisIpPrinter.exe'
if (-not (Test-Path $exe)) { throw "expected output not found: $exe" }
$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "Built: $exe" -ForegroundColor Green
Write-Host "Size:  $sizeMb MB" -ForegroundColor Green

if ($Open) {
    Start-Process explorer.exe (Resolve-Path $publishDir).Path
}
