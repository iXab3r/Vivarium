#requires -Version 7.5
[CmdletBinding()]
param([ValidateSet('start','status','stop','restart-host','reset')][string]$Action = 'status', [switch]$ConfirmReset)
$previousEnvironment = @{}
foreach ($name in @('DOTNET_ROOT','PATH','DOTNET_CLI_HOME','VIVARIUM_LOCAL_DEV_ROOT','ASPNETCORE_ENVIRONMENT','Logging__LogLevel__Default')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$lab = Join-Path $repo 'artifacts/local-dev/lab'
$ownerFile = Join-Path $lab 'owner.json'
$dotnet = Join-Path $repo 'artifacts/p0-rest-first/tooling/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) {
    $installed = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if (!$installed) { throw 'Install the SDK specified by global.json and put dotnet on PATH.' }
    $dotnet = $installed.Source
}
$env:DOTNET_ROOT = Split-Path $dotnet
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_HOME = Join-Path $repo 'artifacts/p0-rest-first/tooling/cli-home'
$env:VIVARIUM_LOCAL_DEV_ROOT = $lab
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Logging__LogLevel__Default = 'Warning'
foreach ($part in @((Join-Path $repo 'artifacts'),(Join-Path $repo 'artifacts/local-dev'),$lab)) {
    if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Lab path contains a reparse point.' }
}
$hostDll = Join-Path $repo 'src/Vivarium.Controller/bin/Debug/net10.0/Vivarium.Controller.dll'
$agentDll = Join-Path $repo 'src/Vivarium.Agent/bin/Debug/net10.0/Vivarium.Agent.dll'
$workbench = Join-Path $repo 'src/Vivarium.Controller/Workbench'
function Native([string]$exe, [string[]]$Arguments) {
    $result = & $exe @Arguments 2>&1
    if ($LASTEXITCODE) { throw "$exe failed ($LASTEXITCODE)." }
    return ($result -join "`n")
}
function Save { $script:state | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $ownerFile }
function Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $portNumber = $listener.LocalEndpoint.Port; $listener.Stop(); return $portNumber
}
function VerifyContainer {
    $data = (Native docker @('inspect', $state.container) | ConvertFrom-Json)[0]
    if ($data.Config.Labels.'vivarium.owner' -ne $state.id) { throw 'Container ownership differs.' }
    if ($state.volume) {
        $volume = Native docker @('volume','inspect','--format','{{json .Labels}}',$state.volume) | ConvertFrom-Json
        if ($volume.'vivarium.owner' -ne $state.id -or $data.Mounts[0].Name -ne $state.volume) { throw 'Database volume ownership differs.' }
    }
    if (!$state.volume) { throw 'Owned labelled volume receipt is required.' }
    if ($data.HostConfig.PortBindings.'5432/tcp'[0].HostIp -ne '127.0.0.1') { throw 'Database is not loopback bound.' }
}
function Alive($record) {
    if (!$record) { return $false }
    $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
    if (!$process) { return $false }
    if ($process.Path -ne $record.exe -or $process.StartTime.ToUniversalTime().ToString('O') -ne $record.started) {
        throw 'Process ownership fence differs; refusing to signal reused PID.'
    }
    return $true
}
function Launch([string]$name, [string]$exe, [string[]]$Arguments, [string]$directory) {
    if (Alive $state.processes.$name) { return }
    $quoted = $Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }
    $process = Start-Process -FilePath $exe -ArgumentList $quoted -WorkingDirectory $directory -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $lab "$name.out.log") -RedirectStandardError (Join-Path $lab "$name.err.log")
    $state.processes.$name = @{ pid = $process.Id; exe = [IO.Path]::GetFullPath($exe); started = $process.StartTime.ToUniversalTime().ToString('O') }
    Save
}
function StopProcess([string]$name) {
    $record = $state.processes.$name
    if (Alive $record) { $ownedProcess = Get-Process -Id $record.pid; $ownedProcess.Kill($true); $ownedProcess.WaitForExit(15000) | Out-Null }
    $state.processes.$name = $null; Save
}
function HostCommand([string]$command, $document) {
    $inputFile = Join-Path $lab 'command.private.json'
    $document | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $inputFile
    $process = Start-Process $dotnet -ArgumentList @($hostDll, $command, '--settings-directory', (Join-Path $lab 'nodes')) `
        -WindowStyle Hidden -PassThru -Wait -RedirectStandardInput $inputFile `
        -RedirectStandardOutput (Join-Path $lab 'command.private.log') -RedirectStandardError (Join-Path $lab 'command.err.log')
    if ($process.ExitCode) { throw "Host $command failed; inspect protected command logs." }
    Remove-Item -LiteralPath $inputFile
}
function Client {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    # A compiled delegate avoids PowerShell runspace callbacks on HTTP worker threads.
    $handler.ServerCertificateCustomValidationCallback = [LocalDevTls]::Pin($state.thumbprint)
    $client = [Net.Http.HttpClient]::new($handler); $client.BaseAddress = [uri]$state.origin
    $client.Timeout = [TimeSpan]::FromSeconds(10); return $client
}
function GetText($client, [string]$path) { return $client.GetStringAsync($path).GetAwaiter().GetResult() }
function Login($client) {
    $html = GetText $client '/Identity/Account/Login'
    $token = [regex]::Match($html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $credential = Get-Content -Raw (Join-Path $lab 'operator.private.json') | ConvertFrom-Json
    $values = [Collections.Generic.Dictionary[string,string]]::new()
    $values.Add('login','superuser'); $values.Add('token',$credential.password); $values.Add('returnUrl','/')
    $values.Add('__RequestVerificationToken',[Net.WebUtility]::HtmlDecode($token))
    $response = $client.PostAsync('/Identity/Account/Login',[Net.Http.FormUrlEncodedContent]::new($values)).GetAwaiter().GetResult()
    if ([int]$response.StatusCode -ne 302) { throw 'Cookie login failed.' }
    $sessionHtml = GetText $client '/'
    $sessionJson = [regex]::Match($sessionHtml, '<script id="vivarium-session" type="application/json">(.*?)</script>').Groups[1].Value
    $client.DefaultRequestHeaders.Add('RequestVerificationToken',($sessionJson | ConvertFrom-Json).requestVerificationToken)
    $client.DefaultRequestHeaders.Accept.ParseAdd('application/json')
}
function Mutate($client,[string]$method,[string]$path,$body) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($method),$path)
    if ($null -ne $body) { $request.Content = [Net.Http.StringContent]::new(($body | ConvertTo-Json -Depth 20 -Compress),[Text.Encoding]::UTF8,'application/json') }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    if (!$response.IsSuccessStatusCode) { throw "Mutation $path returned $([int]$response.StatusCode)." }
    return $response
}
function Until([scriptblock]$predicate,[int]$seconds = 90) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do { if (& $predicate) { return }; Start-Sleep -Milliseconds 200 } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Local readiness deadline exceeded; inspect protected lab logs.'
}
Add-Type @'
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class LocalDevTls {
 public static RemoteCertificateValidationCallback SocketPin(string thumbprint)
 => (_, certificate, _, _) => certificate != null && certificate.GetCertHashString() == thumbprint;
 public static Func<HttpRequestMessage,X509Certificate2,X509Chain,SslPolicyErrors,bool> Pin(string thumbprint)
 => (_, certificate, _, _) => certificate != null && certificate.Thumbprint == thumbprint;
}
'@
$lockName = 'Local\VivariumLocalDev-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lab.ToUpperInvariant()))).Substring(0,24)
$launchMutex = [Threading.Mutex]::new($false,$lockName)
if (!$launchMutex.WaitOne(0)) { $launchMutex.Dispose(); throw 'Another local development lifecycle command is running.' }
try {
if (Test-Path -LiteralPath $ownerFile) {
    $state = Get-Content -Raw -LiteralPath $ownerFile | ConvertFrom-Json -AsHashtable -DateKind String
    if ($state.root -ne $lab -or $state.repo -ne $repo -or $state.id -notmatch '^vivarium-local-dev-[a-f0-9]{12}$') { throw 'Lab ownership differs.' }
} elseif ($Action -ne 'start') { throw 'No owned lab exists. Run start.' }
if ($Action -eq 'status') {
    foreach ($name in @('host','agent','vite')) { "$name running: $(Alive $state.processes.$name)" }
    VerifyContainer
    "UI: $($state.origin)   Host: $($state.backend)"
    "Private operator credentials: $(Join-Path $lab 'operator.private.json')"
    if ((Alive $state.processes.host) -and (Alive $state.processes.vite)) {
        $statusClient = Client
        try {
            Login $statusClient
            $registration = (GetText $statusClient '/api/v1/agents' | ConvertFrom-Json).items
            foreach ($item in $registration) {
                "Agent $($item.id): connected=$($item.connected) authorized=$($item.authorized) enabled=$($item.enabled) reconciled=$($item.reconciled) ready=$($item.ready) health=$($item.health)"
            }
        } finally { $statusClient.Dispose() }
    }
    return
}
if ($Action -in @('stop','reset')) {
    foreach ($name in @('agent','host','vite')) { StopProcess $name }
    VerifyContainer
    Native docker @('stop', $state.container) | Out-Null
    if ($Action -eq 'reset') {
        if (!$ConfirmReset) { throw 'Stopped with data retained. Reset additionally requires -ConfirmReset.' }
        Native docker @('rm', $state.container) | Out-Null
        if ($state.volume) { Native docker @('volume','rm',$state.volume) | Out-Null }
        # Rename the exact owned root instead of recursively deleting any data.
        Move-Item -LiteralPath $lab -Destination ($lab + '-reset-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
    }
    return
}
Native docker @('version','--format','{{.Server.Version}}') | Out-Null
if ($Action -eq 'restart-host') { StopProcess 'host' }
if (!(Test-Path -LiteralPath $ownerFile)) {
    if (Test-Path -LiteralPath $lab) { throw 'Unowned lab directory exists; refusing to adopt.' }
    New-Item -ItemType Directory -Path $lab | Out-Null
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Native icacls @($lab,'/inheritance:r','/grant:r',"*${sid}:(OI)(CI)F") | Out-Null
    $state = @{ id = 'vivarium-local-dev-' + [guid]::NewGuid().ToString('N').Substring(0,12); root=$lab; repo=$repo;
        origin="https://127.0.0.1:$(Port)"; backend="https://127.0.0.1:$(Port)"; storage="http://127.0.0.1:$(Port)"; processes=@{} }
    $state.container = $state.id + '-pg'
    $state.volume = $state.id + '-data'
    Native docker @('volume','create','--label',"vivarium.owner=$($state.id)",$state.volume) | Out-Null
    $state.pgPort = Port
    $state.pgPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    Save
    foreach ($name in @('https','ServerSigningCertificate','ServerEncryptionCertificate','ClientSigningCertificate','ClientEncryptionCertificate')) {
        $key = [Security.Cryptography.RSA]::Create(2048)
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$name",$key,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        if ($name -eq 'https') {
            $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new(); $san.AddIpAddress([Net.IPAddress]::Loopback)
            $request.CertificateExtensions.Add($san.Build())
        }
        $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5),[DateTimeOffset]::UtcNow.AddYears(1))
        [IO.File]::WriteAllBytes((Join-Path $lab "$name.pfx"), $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx))
        if ($name -eq 'https') {
            $state.thumbprint = $cert.Thumbprint; $state.fingerprint = $cert.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
            [IO.File]::WriteAllText((Join-Path $lab 'https.pem'),$cert.ExportCertificatePem())
            [IO.File]::WriteAllText((Join-Path $lab 'https-key.pem'),$key.ExportPkcs8PrivateKeyPem())
        }
        $cert.Dispose(); $key.Dispose()
    }
    Save
    $env:POSTGRES_PASSWORD = $state.pgPassword
    try { Native docker @('run','-d','--name',$state.container,'--label',"vivarium.owner=$($state.id)",'--restart','no','--memory','512m','--cpus','1','-p',"127.0.0.1:$($state.pgPort):5432",'-e','POSTGRES_PASSWORD','--mount',"type=volume,source=$($state.volume),target=/var/lib/postgresql/data",'postgres@sha256:0af65001d05296a2ead57ac4a6412433d8913d1bb5d0c88435a7d1e1ee5cb04b') | Out-Null }
    finally { Remove-Item Env:POSTGRES_PASSWORD }
    $definitions = Join-Path $lab 'definitions'
    New-Item -ItemType Directory -Path $definitions | Out-Null
    Copy-Item -Path (Join-Path $PSScriptRoot 'sample/*') -Destination $definitions -Recurse -Force
    Native git @('-C',$definitions,'init','-b','main') | Out-Null
    Native git @('-C',$definitions,'add','--all') | Out-Null
    Native git @('-C',$definitions,'-c','user.name=Local development','-c','user.email=local@example.invalid','commit','-m','Seed local development examples') | Out-Null
    $state.initialized = $false; Save
}
VerifyContainer
Native docker @('start',$state.container) | Out-Null
if (!(Test-Path (Join-Path $workbench 'node_modules/vite/bin/vite.js'))) { Push-Location $workbench; try { Native npm @('ci','--no-audit','--fund=false') | Out-Null } finally { Pop-Location } }
Until { (& docker exec $state.container pg_isready -U postgres 2>$null) | Out-Null; $LASTEXITCODE -eq 0 }
foreach ($project in @('Vivarium.Controller','Vivarium.Agent')) {
    if (($project -eq 'Vivarium.Controller' -and (Alive $state.processes.host)) -or ($project -eq 'Vivarium.Agent' -and (Alive $state.processes.agent))) { continue }
    Native $dotnet @('build',(Join-Path $repo "src/$project/$project.csproj"),'-p:BuildWorkbench=false','--verbosity','quiet') | Out-Null
}
if (!$state.initialized) {
    $connection = "Host=127.0.0.1;Port=$($state.pgPort);Username=postgres;Password=$($state.pgPassword);Database=postgres;Maximum Pool Size=16"
    $identity = @{ SuperuserEnabled=$true; TokenLifetimeDays=3; DataProtectionDirectory=(Join-Path $lab 'keys'); Providers=@(); TokenCreationPermitLimit=10; TokenCreationWindowSeconds=60; TokenCreationConcurrencyLimit=2; TokenCreationQueueLimit=0; TokenRedemptionConcurrencyLimit=2 }
    foreach ($name in @('ServerSigningCertificate','ServerEncryptionCertificate','ClientSigningCertificate','ClientEncryptionCertificate')) { $identity[$name] = @{ Path=(Join-Path $lab "$name.pfx") } }
    HostCommand 'init' @{
        'vivarium-host' = @{ PublicOrigin=$state.origin; ListenUrl=$state.backend; StorageListenUrl=$state.storage; CertificatePath=(Join-Path $lab 'https.pfx'); DefinitionSources=@{ RepositoryId='local-development'; Path=(Join-Path $lab 'definitions'); SettingsRepositories=@() } }
        'vivarium-database' = @{ ConnectionString=$connection }
        'vivarium-identity' = $identity
        'vivarium-storage' = @{ Backend='Local'; Root=(Join-Path $lab 'objects'); PublicBaseUrl=$state.storage; SigningKey=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)); MaxObjectBytes=1048576; DefaultLifetimeSeconds=60; MaxLifetimeSeconds=300 }
    }
    HostCommand 'migrate' @{ ConnectionString=$connection }
    $state.initialized=$true; Save
}
Launch 'host' $dotnet @($hostDll,'run','--settings-directory',(Join-Path $lab 'nodes')) $repo
Launch 'vite' (Get-Command node).Source @((Join-Path $workbench 'node_modules/vite/bin/vite.js'),'--config','vite.dev.config.mjs') $workbench
$client = Client
try {
    Until { try { (GetText $client '/health/live') -eq 'Healthy' } catch { $false } }
    Login $client
    if (!$state.catalogued) {
        $response = Mutate $client 'POST' '/api/v1/configuration-changes/reconcile' $null
        Until { $result = GetText $client $response.Headers.Location | ConvertFrom-Json; if ($result.state -in @('failed','rejected')) { throw 'Catalogue reconciliation failed.' }; $result.state -eq 'applied' } 180
        $state.catalogued=$true; Save
    }
    $bootstrap = Join-Path $lab 'agent-bootstrap.private.json'
    if (!(Test-Path -LiteralPath $bootstrap)) {
        $response = Mutate $client 'POST' '/api/v1/enrollment-tokens' $null
        $enrollment = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        @{ controllerUrl=$state.backend; certFingerprint=$state.fingerprint; enrollToken=$enrollment.token } | ConvertTo-Json | Set-Content -LiteralPath $bootstrap
    }
    Launch 'agent' $dotnet @($agentDll,'--config',$bootstrap,'--data',(Join-Path $lab 'agent-data')) $repo
    Until {
        $agents = (GetText $client '/api/v1/agents' | ConvertFrom-Json).items
        if (!$agents -or !$agents[0].connected) { return $false }
        $script:localAgent = $agents[0]; return $true
    }
    if (!$localAgent.authorized) { Mutate $client 'PUT' "/api/v1/agents/$($localAgent.id)/authorization" @{authorized=$true} | Out-Null }
    Until { (GetText $client "/api/v1/agents/$($localAgent.id)" | ConvertFrom-Json).reconciled }
    if (!$localAgent.enabled) { Mutate $client 'PUT' "/app/rest/agents/id:$($localAgent.teamCityId)/enabledInfo" @{status=$true;comment=@{text='Owned local development Agent'}} | Out-Null }
    Until { (GetText $client "/api/v1/agents/$($localAgent.id)" | ConvertFrom-Json).ready }
    # The one-use proof is no longer needed once the durable Agent credential is confirmed.
    @{ controllerUrl=$state.backend; certFingerprint=$state.fingerprint } | ConvertTo-Json | Set-Content -LiteralPath $bootstrap

} finally { $client.Dispose() }
"Ready: $($state.origin) (Host debugger: $($state.processes.host.pid), Agent debugger: $($state.processes.agent.pid))"
"Private credentials (rotated on Host restart): $(Join-Path $lab 'operator.private.json')"

} finally { $launchMutex.ReleaseMutex(); $launchMutex.Dispose() }
} finally {
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        $value = if ($null -eq $entry.Value) { [NullString]::Value } else { $entry.Value }
        [Environment]::SetEnvironmentVariable($entry.Key, $value, 'Process')
    }
}
