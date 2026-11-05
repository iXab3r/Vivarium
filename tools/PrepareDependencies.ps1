# Compatibility entry point; Cake owns pinned acquisition and verification for local and CI builds.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $repositoryRoot
try {
    & dotnet run --project build/Vivarium.Build.csproj -- --target PrepareDependencies
    if ($LASTEXITCODE -ne 0) { throw 'Pinned dependency preparation failed.' }
}
finally { Pop-Location }
