#requires -Version 7.5
$ErrorActionPreference='Stop'
. "$PSScriptRoot/local-dev.ps1" status | Out-Null
$definitions=Join-Path $lab 'definitions'
$relative='.vivarium/unbound/Local/Settings.g.cs'
$sourcePath=Join-Path $definitions $relative
if (Native git @('-C',$definitions,'status','--porcelain')) { throw 'Commit local definition edits before this controlled probe.' }
$savedBytes=[IO.File]::ReadAllBytes($sourcePath)
$text=[Text.Encoding]::UTF8.GetString($savedBytes)
$needle='echo Hello from the local Vivarium Agent\n'
if (!$text.Contains($needle)) { throw 'The owned sample no longer has the expected success step.' }
$probeBytes=[Text.Encoding]::UTF8.GetBytes($text.Replace($needle,'if defined POSTGRES_PASSWORD exit /b 42\necho Hello from the local Vivarium Agent\n'))
$previousCallerPassword=[Environment]::GetEnvironmentVariable('POSTGRES_PASSWORD','Process')
$changed=$false; $client=$null
function CommitProbe([string]$message) {
    Native git @('-C',$definitions,'-c','user.name=Local verification','-c','user.email=local@example.invalid',
        'commit','--only','-m',$message,'--',$relative) | Out-Null
}
function ReconcileProbe {
    $response=Mutate $client 'POST' '/api/v1/configuration-changes/reconcile' $null
    Until { (GetText $client $response.Headers.Location | ConvertFrom-Json).state -eq 'applied' } 180
}
try {
    $env:POSTGRES_PASSWORD='synthetic-caller-only-control'
    & "$PSScriptRoot/local-dev.ps1" stop
    & "$PSScriptRoot/local-dev.ps1" start
    if ($env:POSTGRES_PASSWORD -cne 'synthetic-caller-only-control') { throw 'Caller password environment was not restored.' }
    $client=Client; Login $client
    [IO.File]::WriteAllBytes($sourcePath,$probeBytes); $changed=$true
    CommitProbe 'Probe local workload environment boundary'
    ReconcileProbe
    $response=Mutate $client 'POST' '/api/v1/builds' @{configurationId='Local_Success'}
    $buildId=($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).id
    Until { $script:probeResult=GetText $client "/api/v1/builds/$buildId" | ConvertFrom-Json; $probeResult.state -eq 'FINISHED' }
    if ($probeResult.status -ne 'SUCCESS') { throw 'A warm-launched native workload inherited the caller database environment.' }
    @{buildId=$buildId;status=$probeResult.status;callerRetained=$true;nativeWorkloadPasswordAbsent=$true} |
        ConvertTo-Json | Set-Content (Join-Path $repo 'artifacts/local-dev/workload-environment.json')
    'WARM_NATIVE_WORKLOAD_ENVIRONMENT_PASS'
} finally {
    try {
        if ($changed) {
            $actual=[IO.File]::ReadAllBytes($sourcePath)
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($actual)) -ne
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($probeBytes))) { throw 'Source changed during probe; refusing overwrite.' }
            [IO.File]::WriteAllBytes($sourcePath,$savedBytes)
            CommitProbe 'Restore local sample after environment probe'
            ReconcileProbe
        }
    } finally {
        if ($client) { $client.Dispose() }
        $value=if($null -eq $previousCallerPassword){[NullString]::Value}else{$previousCallerPassword}
        [Environment]::SetEnvironmentVariable('POSTGRES_PASSWORD',$value,'Process')
    }
}
