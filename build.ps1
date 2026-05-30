# Builds Muneris IP Printer for .NET Framework 4.6.2.
# Output: bin\Release\publish\MunerisIpPrinter-<version>.exe
#
# .NET 4.6.2 is part of every supported Windows install (Win10+ and Server 2016+),
# so a single small .exe is enough — no runtime to bundle and no self-contained variant.
#
# Usage:
#   .\build.ps1            # build at the current csproj <Version>
#   .\build.ps1 -Bump      # increment to yyyy.M.d.<prev+1> (CalVer), then build
#   .\build.ps1 -Open      # build and open the publish folder in Explorer
#   .\build.ps1 -Bump -Open

param(
    [switch]$Open,
    [switch]$Bump
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csprojPath = 'MunerisIpPrinter.csproj'

function Bump-Version {
    # Reads <Version>, parses the last component as the monotonic build counter, and
    # rewrites <Version> to today.year.month.day.<counter+1>. Works for both legacy
    # semver (1.0.4 -> 2026.5.30.5) and CalVer (2026.5.30.5 -> 2026.5.31.6).
    $content = Get-Content $csprojPath -Raw
    if ($content -notmatch '<Version>([^<]+)</Version>') {
        throw "Could not find <Version> in $csprojPath"
    }
    $current = $Matches[1]
    $parts = $current -split '\.'
    $counter = ([int]$parts[-1]) + 1
    $today = [DateTime]::Today
    $newVersion = "$($today.Year).$($today.Month).$($today.Day).$counter"
    $newContent = $content -replace '<Version>[^<]+</Version>', "<Version>$newVersion</Version>"
    [System.IO.File]::WriteAllText((Resolve-Path $csprojPath), $newContent)
    Write-Host "Version bumped: $current -> $newVersion" -ForegroundColor Yellow
    return $newVersion
}

if ($Bump) {
    Bump-Version | Out-Null
}

[xml]$csproj = Get-Content $csprojPath
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) -as [string]
if (-not $version) { $version = '0.0.0' }

$publishDir = 'bin\Release\publish'
if (Test-Path $publishDir) {
    Write-Host "Cleaning previous publish output..." -ForegroundColor DarkGray
    Remove-Item $publishDir -Recurse -Force
}

Write-Host "Building net462 release..." -ForegroundColor Cyan
dotnet build $csprojPath -c Release
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
