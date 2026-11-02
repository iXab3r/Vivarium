$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$baselineRoot = Join-Path $repoRoot 'artifacts/teamcity-migration'
$outputRoot = Join-Path $repoRoot 'artifacts/teamcity-parity'
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$cliPath = (Get-Command teamcity-cli).Source
$jobs = (Get-Content (Join-Path $baselineRoot 'jobs.json') -Raw | ConvertFrom-Json).items
$tasks = [Collections.Generic.List[object]]::new()
foreach ($job in $jobs) {
    $history = Get-Content (Join-Path $baselineRoot "$($job.id)--builds.json") -Raw | ConvertFrom-Json
    $tasks.Add(@{key="$($job.id)--branches"; arguments=@('jobs','branches',$job.id,'--limit','25')})
    if ($history.items.Count -gt 0) {
        $buildId = $history.items[0].id.ToString()
        $tasks.Add(@{key="$($job.id)--latest-diagnosis"; arguments=@('builds','diagnose',$buildId)})
        $tasks.Add(@{key="$($job.id)--statistics"; arguments=@('builds','statistics','list',$buildId)})
    }
}
foreach ($agentId in @(10,15,2284)) {
    $tasks.Add(@{key="agent-$agentId-policy"; arguments=@('agents','policy','show',"$agentId")})
    $tasks.Add(@{key="agent-$agentId-incompatible"; arguments=@('agents','incompatible-jobs',"$agentId")})
}
$tasks.Add(@{key='mutes'; arguments=@('mutes','list','--limit','100')})
$tasks.Add(@{key='investigations'; arguments=@('investigations','list','--limit','100')})
$tasks.Add(@{key='server'; arguments=@('server','status')})
$samples = @('EyeAuras_Compile','EyeAuras_PoEBane_PoeBane_App_Compile','EyeAuras_CliFactory_Verify','EyeAuras_Workbench_NpmRelease','EyeAuras_CLink_CLink_CLink_Compile','Vivarium_Vivarium_CompileWindowsX64','EyeAuras_ReClass_Portable','EyeAuras_WebServerLinux_Compile')
foreach ($jobId in $samples) {
    $history = Get-Content (Join-Path $baselineRoot "$jobId--builds.json") -Raw | ConvertFrom-Json
    if ($history.items.Count -eq 0) { continue }
    $buildId = $history.items[0].id.ToString()
    foreach ($category in @('tests','artifacts','changes','tags','pin')) {
        $arguments = switch ($category) {
            'tests' { @('builds','tests',$buildId,'--limit','8') }
            'artifacts' { @('builds','artifacts','list',$buildId,'--limit','50') }
            'changes' { @('builds','changes',$buildId,'--limit','8') }
            'tags' { @('builds','tags','list',$buildId) }
            'pin' { @('builds','pin','show',$buildId) }
        }
        $tasks.Add(@{key="$jobId--sample-$category"; arguments=$arguments})
    }
}
$results = $tasks | ForEach-Object -Parallel {
    $task = $_
    try {
        $arguments = $task.arguments
        $raw = & $using:cliPath @arguments --json 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "CLI exit $LASTEXITCODE" }
        $parsed = $raw | ConvertFrom-Json -Depth 100 -NoEnumerate
        ConvertTo-Json -InputObject $parsed -Depth 100 | Set-Content -LiteralPath (Join-Path $using:outputRoot "$($task.key).json") -Encoding utf8
        [pscustomobject]@{key=$task.key; success=$true; error=$null}
    } catch {
        [pscustomobject]@{key=$task.key; success=$false; error=$_.Exception.Message}
    }
} -ThrottleLimit 4
ConvertTo-Json -InputObject @($results) -Depth 10 | Set-Content -LiteralPath (Join-Path $outputRoot 'collection-status.json') -Encoding utf8
[pscustomobject]@{tasks=$tasks.Count; failed=@($results | Where-Object {-not $_.success}).Count; capturedUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json
