#requires -Version 7.5
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture=Join-Path $repo ('artifacts/local-dev/recovery-control-'+[guid]::NewGuid().ToString('N'))
$tool=Join-Path $fixture 'tools/local-dev'
New-Item -ItemType Directory $tool -Force|Out-Null
Copy-Item -LiteralPath "$PSScriptRoot/local-dev.ps1" -Destination $tool
$lab=Join-Path $fixture 'artifacts/local-dev/lab'
New-Item -ItemType Directory $lab -Force|Out-Null
$id='vivarium-local-dev-'+[guid]::NewGuid().ToString('N').Substring(0,12)
$volume=$id+'-data'
$receipt=@{id=$id;root=$lab;repo=$fixture;container=$id+'-pg';volume=$volume;processes=@{}}
$receipt|ConvertTo-Json|Set-Content (Join-Path $lab 'owner.json')
$oldPath=$env:PATH
$env:PATH="$(Join-Path $repo 'artifacts/p0-rest-first/tooling/dotnet');$env:PATH"
try {
    & docker volume create --label "vivarium.owner=$id" $volume|Out-Null
    if($LASTEXITCODE){throw 'Owned recovery fixture creation failed'}
    # This is the persisted boundary after volume creation and before container creation.
    & (Join-Path $tool 'local-dev.ps1') reset -ConfirmReset
    if(Test-Path -LiteralPath $lab){throw 'Incomplete owned root was not archived'}
    $remaining=& docker volume ls -q --filter "name=$volume"
    if($LASTEXITCODE -or $remaining -contains $volume){throw 'Owned partial volume remains'}
    'BEFORE_CONTAINER_CONFIRMED_RESET_PASS'
} finally {
    # Cleanup is independently fenced, including when testing the original broken reset.
    $existing=& docker volume ls -q --filter "name=$volume"
    if(!$LASTEXITCODE -and $existing -contains $volume){
        $labels=& docker volume inspect --format '{{json .Labels}}' $volume
        if($LASTEXITCODE -or ($labels|ConvertFrom-Json).'vivarium.owner' -ne $id){throw 'Refuse foreign fixture cleanup'}
        & docker volume rm $volume|Out-Null
        if($LASTEXITCODE){throw 'Owned fixture cleanup failed'}
    }
    $env:PATH=$oldPath
}
