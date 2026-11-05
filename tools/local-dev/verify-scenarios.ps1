#requires -Version 7.5
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/local-dev.ps1" status | Out-Null
$client = Client
try {
    Login $client
    $receipt = @{}
    foreach ($configuration in @('Local_Failure','Local_Cancel')) {
        $response = Mutate $client 'POST' '/api/v1/builds' @{configurationId=$configuration}
        $id = ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).id
        if ($configuration -eq 'Local_Cancel') {
            Until { (GetText $client "/api/v1/builds/$id" | ConvertFrom-Json).state -eq 'RUNNING' }
            Until { (GetText $client "/api/v1/builds/$id/output" | ConvertFrom-Json).items.Count -gt 0 }
            Mutate $client 'PUT' "/api/v1/builds/$id/cancellation" @{reason='Owned local verification';mode='graceful'} | Out-Null
        }
        Until { $script:result = GetText $client "/api/v1/builds/$id" | ConvertFrom-Json; $result.state -eq 'FINISHED' }
        $expected = if ($configuration -eq 'Local_Failure') { 'FAILURE' } else { 'UNKNOWN' }
        if ($result.statusText -like '*ownership*expired*') { throw 'Cancellation failed to return an execution receipt.' }
        if ($result.status -ne $expected) { throw "Unexpected sample result: $configuration" }
        $receipt[$configuration] = @{id=$id;state=$result.state;status=$result.status}
    }
    Until { (GetText $client '/api/v1/agents' | ConvertFrom-Json).items[0].ready }
    $receipt | ConvertTo-Json | Set-Content (Join-Path $repo 'artifacts/local-dev/scenarios-verification.json')
    'Verified controlled failure and cancellation after real process output.'
} finally { $client.Dispose() }
