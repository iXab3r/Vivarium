#requires -Version 7.5
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/local-dev.ps1" status | Out-Null
$client = Client
$socket = [Net.WebSockets.ClientWebSocket]::new()
$socket.Options.RemoteCertificateValidationCallback = [LocalDevTls]::SocketPin($state.thumbprint)
$socket.Options.AddSubProtocol('vite-hmr')
$cancel = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(20))
$cssPath = Join-Path $workbench 'src/style.css'
$original = [IO.File]::ReadAllBytes($cssPath)
try {
    # Exercise the real transformed module graph before asking Vite to send file updates.
    $null = GetText $client '/workbench/src/main.ts'
    $null = GetText $client '/workbench/src/style.css'
    $viteClient = GetText $client '/workbench/@vite/client'
    $token = [regex]::Match($viteClient, 'const wsToken = "([^"]+)"').Groups[1].Value
    if (!$token) { throw 'Vite websocket token absent.' }
    $null = $socket.ConnectAsync([uri]($state.origin.Replace('https:','wss:') + '/workbench/?token=' + $token),$cancel.Token).GetAwaiter().GetResult()
    $buffer = [byte[]]::new(65536)
    $segment = [ArraySegment[byte]]::new($buffer)
    $result = $socket.ReceiveAsync($segment,$cancel.Token).GetAwaiter().GetResult()
    if (![Text.Encoding]::UTF8.GetString($buffer,0,$result.Count).Contains('connected')) { throw 'HMR handshake absent.' }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    [IO.File]::AppendAllText($cssPath,"`n/* owned CLI HMR transport proof */`n")
    do {
        $result = $socket.ReceiveAsync($segment,$cancel.Token).GetAwaiter().GetResult()
        $message = [Text.Encoding]::UTF8.GetString($buffer,0,$result.Count) | ConvertFrom-Json
    } until ($message.type -eq 'update')
    $timer.Stop()
    @{ hmrWebSocket=$true; tlsPinned=$true; editToUpdateMilliseconds=$timer.Elapsed.TotalMilliseconds;
        note='Transport and transformed module proof; graphical rendering and interactive controls not exercised'; observedAt=[DateTimeOffset]::UtcNow } |
        ConvertTo-Json | Set-Content (Join-Path $repo 'artifacts/local-dev/hmr-verification.json')
    "HMR update received in $($timer.Elapsed.TotalMilliseconds) ms."
} finally {
    [IO.File]::WriteAllBytes($cssPath,$original)
    $socket.Dispose(); $client.Dispose(); $cancel.Dispose()
}
