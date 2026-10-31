$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$snapshotRoot = Join-Path $repoRoot 'artifacts/teamcity-migration'
New-Item -ItemType Directory -Force -Path $snapshotRoot | Out-Null
$cliPath = (Get-Command teamcity-cli).Source

function Read-Snapshot([string] $name, [string[]] $arguments) {
    $content = & $cliPath @arguments --json 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Snapshot failed: $name" }
    $parsed = $content | ConvertFrom-Json -Depth 100 -NoEnumerate
    ConvertTo-Json -InputObject $parsed -Depth 100 | Set-Content -LiteralPath (Join-Path $snapshotRoot "$name.json") -Encoding utf8
    return $parsed
}

$projects = Read-Snapshot 'projects' @('projects','list','--limit','250','--include-archived')
$jobs = Read-Snapshot 'jobs' @('jobs','list','--limit','250')
$agents = Read-Snapshot 'agents' @('agents','list','--limit','250')
$roots = Read-Snapshot 'vcs-roots' @('vcs','roots','list','--limit','250')
if ($jobs.hasMore -or $projects.hasMore -or $roots.hasMore -or $agents.hasMore) {
    throw 'Incomplete top-level census: pagination is required.'
}

$tasks = [Collections.Generic.List[object]]::new()
foreach ($job in $jobs.items) {
    foreach ($category in @('steps','parameters','agent-requirements','snapshot-dependencies','artifact-dependencies','vcs','triggers','features','templates')) {
        $tasks.Add(@{ key = "$($job.id)--$category"; arguments = @('jobs',$category,'list',$job.id) })
    }
    $tasks.Add(@{ key = "$($job.id)--builds"; arguments = @('builds','list','--job',$job.id,'--limit','3') })
}
foreach ($project in $projects.items) {
    foreach ($category in @('parameters','features')) {
        $tasks.Add(@{ key = "$($project.id)--project-$category"; arguments = @('projects',$category,'list',$project.id) })
    }
    $tasks.Add(@{ key = "$($project.id)--versioned-settings"; arguments = @('projects','versioned-settings','config','show',$project.id) })
}

$results = $tasks | ForEach-Object -Parallel {
    $task = $_
    $path = Join-Path $using:snapshotRoot "$($task.key).json"
    try {
        $commandArguments = $task.arguments
        $content = & $using:cliPath @commandArguments --json 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "CLI exit $LASTEXITCODE" }
        $parsed = $content | ConvertFrom-Json -Depth 100 -NoEnumerate
        ConvertTo-Json -InputObject $parsed -Depth 100 | Set-Content -LiteralPath $path -Encoding utf8
        [pscustomobject]@{ key = $task.key; success = $true; error = $null }
    } catch {
        [pscustomobject]@{ key = $task.key; success = $false; error = $_.Exception.Message }
    }
} -ThrottleLimit 4
$results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $snapshotRoot 'collection-status.json') -Encoding utf8
[pscustomobject]@{
    capturedUtc = [DateTime]::UtcNow.ToString('o')
    projects = $projects.items.Count
    jobs = $jobs.items.Count
    agents = $agents.items.Count
    vcsRoots = $roots.items.Count
    tasks = $tasks.Count
    failed = @($results | Where-Object { -not $_.success }).Count
    snapshotRoot = $snapshotRoot
} | ConvertTo-Json
