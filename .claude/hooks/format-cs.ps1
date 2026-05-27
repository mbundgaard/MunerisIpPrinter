$ErrorActionPreference = 'SilentlyContinue'
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $f = $payload.tool_input.file_path
    if (-not $f) { exit 0 }
    if ($f -notlike '*.cs') { exit 0 }
    if ($f -match '[\\/](obj|bin)[\\/]') { exit 0 }
    Push-Location $PSScriptRoot\..\..
    try { dotnet format MunerisIpPrinter.csproj --include $f 2>&1 | Out-Null } finally { Pop-Location }
} catch { }
exit 0
