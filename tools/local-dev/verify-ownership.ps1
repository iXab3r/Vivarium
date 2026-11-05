#requires -Version 7.5
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/local-dev.ps1" status | Out-Null
$self = Get-Process -Id $PID
$record = @{pid=$PID;exe=$self.Path;started=$self.StartTime.ToUniversalTime().ToString('O')}
if (!(Alive $record)) { throw 'Current process fence failed.' }
$record.started = '2000-01-01T00:00:00.0000000Z'
$refused = $false
try { Alive $record | Out-Null } catch { $refused = $_.Exception.Message.Contains('ownership fence differs') }
if (!$refused) { throw 'Reused PID fence was accepted.' }
$original = $state.id
try {
    $state.id = 'vivarium-local-dev-000000000000'
    $refused = $false
    try { VerifyContainer } catch { $refused = $_.Exception.Message.Contains('ownership differs') }
    if (!$refused) { throw 'Foreign container label was accepted.' }
} finally { $state.id = $original }
'Verified live process fence, reused PID refusal, and foreign Docker ownership refusal without signalling resources.'
