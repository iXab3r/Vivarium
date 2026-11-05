# Local Host, Agent and Workbench development

This Windows first increment runs the actual PostgreSQL Host and a separate physical Agent with
HTTPS Vite HMR. Prerequisites: PowerShell 7.5+, Docker Desktop Linux Engine, Git, installed Node
supported by Workbench, and restored .NET dependencies through the usual private feed setup.
For a clean checkout, follow [dependency preparation](DEVELOPMENT.md#building) first:
`dotnet run --project build/Vivarium.Build.csproj -- --target PrepareDependencies`.
Missing private packages require the existing repository-read credential setup; cached verified
packages require no network. The local launcher does not acquire or print credentials.
Install the SDK specified by `global.json` with `dotnet` on PATH. If the repository already has a
private SDK at `artifacts/p0-rest-first/tooling/dotnet/dotnet.exe`, the launcher uses that cache;
otherwise it uses the installed executable. It sets DOTNET_ROOT/PATH accordingly and keeps
DOTNET_CLI_HOME in repository tooling storage. It builds Controller and Agent without packaging.
PowerShell 7.5 preserves process-receipt timestamps with
[`ConvertFrom-Json -DateKind String`](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/convertfrom-json?view=powershell-7.5#-datekind).

From the repository root:

```powershell
./tools/local-dev/local-dev.ps1 start
./tools/local-dev/local-dev.ps1 status
./tools/local-dev/verify.ps1
./tools/local-dev/verify-hmr.ps1
./tools/local-dev/verify-scenarios.ps1
./tools/local-dev/verify-environment.ps1
./tools/local-dev/verify-bootstrap-recovery.ps1
./tools/local-dev/local-dev.ps1 restart-host
./tools/local-dev/verify.ps1 -Retained
./tools/local-dev/local-dev.ps1 stop
```

Start prints the selected HTTPS UI URL and separate debugger PIDs. Accept that lab's self-signed
certificate locally in your browser; no system trust store is changed. Read the protected
`artifacts/local-dev/lab/operator.private.json` for the `superuser` cookie-login password. It rotates
on every Host restart; sign in again if needed. It is never an API bearer key. Keep the file private.

The default development page explains and links to **Local development examples**. **Success** emits
a log and `proof.txt`; **Failure** exits 7; **Cancel me** waits twenty seconds for cancellation from Builds.
Windows command scripts have no main window for graceful close, so the example finishes its bounded
wait before acknowledging graceful cancellation. It must return a process receipt and leave the Agent ready.
Inspect configurations, history, durable logs and artifacts. Step declarations are in the local
Git source; the fuller UI step inspector remains tracked by issue173. The seed is copied once to
`artifacts/local-dev/lab/definitions`, a separate Git repository. Edit and commit there, then invoke
the ordinary authorized configuration-reconcile API. Later starts preserve edits.

UI edits in `src/Vivarium.Controller/Workbench/src` use real HMR. HTTPS Vite forwards application
routes to the actual Host with certificate verification against the lab certificate. The Host supplies
HTML session/CSRF bootstrap; cookies, antiforgery and current API authority remain production mechanisms.
The websocket uses that same HTTPS origin. Explicit `VIVARIUM_LOCAL_DEV_ROOT` and Development environment
are required, and Host endpoints must use loopback. Published defaults still serve static assets.

For backend edits, `restart-host` stops the fenced Host, rebuilds Controller and starts its production
`run --settings-directory` command. Agent remains running and reconnects. Attach a .NET debugger to
each printed PID separately. Exact DLL paths/arguments are in the launcher. For debugger-controlled
launch, stop the corresponding owned process first and inherit the launcher's environment/settings
and Agent data paths; never run two Hosts against one lab. C# definitions still use the production
pinned networkless D43 Docker evaluator. No TeamCity, release archive or image publication is needed.

Stop the lab before a full root `dotnet build`/test checkpoint, then start it again afterward.
The root frontend build runs `npm ci`; on Windows, running Vite holds the Rollup native module
and prevents that reinstall. Ordinary UI edits use HMR, and `restart-host` skips the frontend
build. Keep root checkpoints separate from the warm development loop.
Lifecycle commands restore the caller's SDK, logging and Development environment variables
on both success and failure; child Host/Agent processes retain their own launch environment.

All endpoints bind `127.0.0.1`. PostgreSQL 17.10 uses the pinned digest and separately named, owner-labelled
container/volume. Private Chiral settings, TLS/signing certificates, DP keys, Git, objects and Agent
identity persist across stop/restart. The lab root ACL removes inheritance and grants the current user.
Process receipts record PID, executable and creation time. Stop signals only those fenced processes
and the verified owned container, retaining data. Deliberate reset is separate:

```powershell
./tools/local-dev/local-dev.ps1 reset -ConfirmReset
```

Reset removes the verified container/labelled database volume and renames the exact owned directory
to a timestamped archive. No recursive filesystem deletion or global Docker operation occurs. Archived
private files remain sensitive. Next start creates a new installation.
If initial setup fails before a container exists, confirmed reset independently checks any
existing labelled volume/container and archives the owned root. Missing resources are allowed;
foreign resources still refuse cleanup. Reset without `-ConfirmReset` changes no resources.

Docker preflight failure leaves existing infrastructure untouched. Resolve Docker availability through
the normal operator workflow and retry. Readiness failures retain owned state and protected lab logs for
diagnosis. Ownership/PID mismatches refuse adoption or signalling. Inspect partial initialization before
choosing reset. No private VM, remote network or existing farm resource is used.

CLI verification proves login, session/module delivery, real execution, exact durable log/artifact
bytes, restart retention and TLS HMR update transport. Graphical layout, DOM execution and interactive
controls require a separate UI check. Sanitized receipts/timings are under `artifacts/local-dev`;
cold measurements assume already cached images/dependencies. Parent delivery owns root checks and
independent milestone review.
