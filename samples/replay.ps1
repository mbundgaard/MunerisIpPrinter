param(
    [string]$Address = "127.0.0.1",
    [int]$Port = 9100,
    [string]$File = "$PSScriptRoot\20260513_214004_001_req.bin"
)

$bytes = [System.IO.File]::ReadAllBytes($File)
$client = New-Object System.Net.Sockets.TcpClient
$client.Connect([System.Net.IPAddress]::Parse($Address), $Port)
$stream = $client.GetStream()
$stream.Write($bytes, 0, $bytes.Length)
$stream.Flush()

# read any reply (status pings get answered inline)
Start-Sleep -Milliseconds 300
$buf = New-Object byte[] 256
$got = 0
if ($stream.DataAvailable) { $got = $stream.Read($buf, 0, $buf.Length) }
$stream.Close()
$client.Close()
Write-Host "Sent $($bytes.Length) bytes to $Address`:$Port, got $got bytes back"
