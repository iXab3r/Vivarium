#requires -Version 7.5
$ErrorActionPreference = 'Stop'
$names = @('DOTNET_ROOT','PATH','DOTNET_CLI_HOME','VIVARIUM_LOCAL_DEV_ROOT','ASPNETCORE_ENVIRONMENT','Logging__LogLevel__Default','POSTGRES_PASSWORD')
$before = @{}
foreach ($name in $names) { $before[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
& "$PSScriptRoot/local-dev.ps1" status | Out-Null
foreach ($name in $names) {
    if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne $before[$name]) { throw "Caller environment changed: $name" }
}
"Caller environment retained after status."
