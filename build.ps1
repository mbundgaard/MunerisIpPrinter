# Builds Muneris IP Printer for .NET Framework 4.6.2.
# Output: bin\Release\publish\MunerisIpPrinter-<version>.exe
#
# .NET 4.6.2 is part of every supported Windows install (Win10+ and Server 2016+),
# so a single small .exe is enough — no runtime to bundle and no self-contained variant.
#
# Usage:
#   .\build.ps1            # build
#   .\build.ps1 -Open      # build and open the publish folder in Explorer

param(
    [switch]$Open
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$csproj = Get-Content 'MunerisIpPrinter.csproj'
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) -as [string]
if (-not $version) { $version = '0.0.0' }

$publishDir = 'bin\Release\publish'
if (Test-Path $publishDir) {
    Write-Host "Cleaning previous publish output..." -ForegroundColor DarkGray
    Remove-Item $publishDir -Recurse -Force
}

Write-Host "Building net462 release..." -ForegroundColor Cyan
dotnet build MunerisIpPrinter.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

$built = 'bin\Release\net462\MunerisIpPrinter.exe'
if (-not (Test-Path $built)) { throw "expected output not found: $built" }

New-Item -ItemType Directory -Path $publishDir | Out-Null
$final = Join-Path $publishDir ("MunerisIpPrinter-{0}.exe" -f $version)
Copy-Item $built $final -Force

$sizeKb = [Math]::Round((Get-Item $final).Length / 1KB, 1)
Write-Host ""
Write-Host "Built: $final" -ForegroundColor Green
Write-Host ("Size:  {0} KB" -f $sizeKb) -ForegroundColor Green

if ($Open) {
    Start-Process explorer.exe (Resolve-Path $publishDir).Path
}
