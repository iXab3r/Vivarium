# Downloads the exact owner prereleases consumed by the production Host. Requires authenticated gh.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$feed = Join-Path $repositoryRoot 'artifacts/dependencies/nuget'
$null = New-Item -ItemType Directory -Force -Path $feed
$manifest = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $repositoryRoot 'dependency-artifacts.json') | ConvertFrom-Json
foreach ($release in $manifest.releases) {
    foreach ($file in $release.files) {
        if ($file.name -notmatch '^[A-Za-z0-9.-]+\.nupkg$' -or $file.sha256 -notmatch '^[0-9a-f]{64}$') {
            throw 'Invalid dependency artifact identity.'
        }
        $target = Join-Path $feed $file.name
        if (-not (Test-Path -LiteralPath $target)) {
            $staging = Join-Path $repositoryRoot ('artifacts/dependencies/download-' + [Guid]::NewGuid().ToString('N'))
            $null = New-Item -ItemType Directory -Path $staging
            & gh release download $release.tag --repo $release.repository --pattern $file.name --dir $staging
            if ($LASTEXITCODE -ne 0) { throw "Dependency download failed: $($file.name)" }
            $download = Join-Path $staging $file.name
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $download).Hash.ToLowerInvariant() -ne $file.sha256) {
                throw "Dependency hash differs: $($file.name)"
            }
            Move-Item -LiteralPath $download -Destination $target
        }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Dependency hash differs: $($file.name)"
        }
        Write-Output "Verified $($file.name)"
    }
}
