#requires -Version 7.5
param([switch]$Retained)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/local-dev.ps1" status | Out-Null
$client = Client
try {
    Login $client
    $html = GetText $client '/builds'
    if (!$html.Contains('vivarium-session') -or !$html.Contains('/workbench/@vite/client')) { throw 'Actual session/HMR bootstrap missing.' }
    $module = GetText $client '/workbench/src/main.ts'
    if (!$module.Contains('createWorkbench')) { throw 'Workbench module unavailable.' }
    if ($Retained) {
        $buildId = (Get-Content -Raw (Join-Path $repo 'artifacts/local-dev/verification.json') | ConvertFrom-Json).buildId
    } else {
        $response = Mutate $client 'POST' '/api/v1/builds' @{configurationId='Local_Success'}
        $buildId = ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).id
    }
    Until { $script:build = GetText $client "/api/v1/builds/$buildId" | ConvertFrom-Json; $build.state -eq 'FINISHED' }
    if ($build.status -ne 'SUCCESS') { throw 'Tiny success Build failed.' }
    $artifacts = GetText $client "/api/v1/builds/$buildId/artifacts" | ConvertFrom-Json
    $artifact = $artifacts.items | Where-Object path -eq 'proof.txt'
    if (!$artifact) { throw 'File artifact missing.' }
    $bytes = $client.GetByteArrayAsync($artifact.href).GetAwaiter().GetResult()
    $expected = [Text.Encoding]::UTF8.GetBytes("local-development-proof`r`n")
    if ([Convert]::ToHexString($bytes) -ne [Convert]::ToHexString($expected)) { throw 'Exact artifact bytes differ.' }
    $output = GetText $client "/api/v1/builds/$buildId/output" | ConvertFrom-Json
    $log = ''
    foreach ($entry in $output.items) {
        $chunk = $client.GetByteArrayAsync($entry.content.href).GetAwaiter().GetResult()
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($chunk)).ToLowerInvariant() -ne $entry.content.sha256) { throw 'Log chunk hash differs.' }
        $log += [Text.Encoding]::UTF8.GetString($chunk)
    }
    if ($log -ne "Hello from the local Vivarium Agent`r`n##teamcity[publishArtifacts 'proof.txt']`r`n") { throw 'Exact durable log bytes differ.' }
    @{ buildId=$buildId; status=$build.status; artifactSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)); artifactLength=$bytes.Length;
        retainedRead=[bool]$Retained; exactLog=$true; sessionBootstrap=$true; workbenchModule=$true; observedAt=[DateTimeOffset]::UtcNow } | ConvertTo-Json | Set-Content (Join-Path $repo 'artifacts/local-dev/verification.json')
    "Verified actual Build $buildId, cookie/session bootstrap, Workbench module and exact artifact bytes."
} finally { $client.Dispose() }
