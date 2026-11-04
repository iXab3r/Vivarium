# Developing Vivarium

How Vivarium itself is built, tested, released, and upgraded. The *decisions* live in
[`ARCHITECTURE.md`](ARCHITECTURE.md) (D19 — portable distribution, D20 — test tiers); this file is
the practical companion.

## Acceptance hierarchy and evidence selection (D37)

System AC are canonical English business scenarios in [docs/acceptance](acceptance/README.md).
They are currently specified-unbound, not passing tests. The [testing model](design/testing-model.md)
links them to TCM base-model invariants, exact native/TeamCity REST variants and supporting component
checks. Existing D20 tiers below describe execution boundaries, not the requirement hierarchy.

Choose risk/observable/oracle and smallest sufficient composition before runner/topology. Reuse
existing tests and Automation/Reqnroll; a business scenario need not be UI/E2E, but actual process/UI/
storage claims require those real boundaries. Each milestone runs affected checks and a small
cumulative implemented TeamCity base suite with relevant extension-neutrality checks. Keep long
correctness, ordinary feedback and cost diagnostics distinct, with explicit commands and gaps.

Current wiring: `Vivarium.Tests` uses NUnit/Test SDK. Reusing Automation/Reqnroll describes the
pinned external consumer integration to be delivered through #15/#40, not existing executable
Vivarium Gherkin bindings. P0 #35 starts on the current NUnit stack; specified features remain
unbound until their actual integration and evidence exist.

## API ledger, review and verification cadence (D39)

Follow [task-backed API delivery](design/api-ledger-delivery.md). Under D40, implement finished
[subsystems](design/subsystems.md); their slice tasks parent same-milestone REST-contract/acceptance
subtasks. Milestones aggregate those slices and their parent owns final closure. Publish mechanical slice
and global API-complete with exact scope and valid case evidence; unknown denominators stay unknown.
Read ledger/results for status without running tests. During TDD run affected cases; batch broader
checks at coherent handoff/milestone gates and reuse unchanged valid evidence. Do not duplicate
full REST/E2E/platform runs or formal reviews per task/edit/reviewer. Required checks still apply.

At milestone end perform independent aggregate correctness/domain/simplicity and explicit
Security/Adversarial review at the exact revision. Fix all findings and introduced/exposed technical
tails through same-milestone tasks, verify narrowly, renew affected verdicts, then deliver/close.
No debts carried into next-milestone implementation; no security follow-up waiver at closure.

The [offline ledger command](design/api-ledger-accounting.md) reads local structured registers,
standard TRX, hashes and explicit candidate reuse decisions. It does not launch tests/builds or probe
TeamCity. Its generated status remains partial with unknown global scope and API-complete false.

## Building

Before restoring the fresh Host dependencies, run `pwsh -File tools/PrepareDependencies.ps1` from
the repository root with GitHub CLI access to the owner releases. `dependency-artifacts.json` pins
the exact Chiral/Storage prerelease bytes; preparation verifies SHA256 before populating the local
feed selected by `NuGet.Config`. These are the adopted Depot package APIs, not local source projects.
The Controller lock file pins the resolved runtime graph.

The in-progress fresh-install runtime has explicit local commands:

```text
viv-server init --settings-directory <private-directory>
viv-server migrate --settings-directory <private-directory>
viv-server nodes --settings-directory <private-directory>
viv-server run --settings-directory <private-directory>
```

`init` reads private node JSON on stdin, keyed by `vivarium-host`, `vivarium-database`,
`vivarium-identity` and `vivarium-storage`. Schemas are embedded from `Hosting/Schemas`; Storage
supplies its own schema. Provision persistent private TLS/Identity certificates and Data Protection
paths before running. The Identity token lifetime cap is explicit (1–365 days), with no inherited
default. `migrate` separately reads `{"ConnectionString":"<maintenance connection>"}` on stdin;
it never persists that privileged input as runtime configuration. Run local maintenance with the
Host stopped. Runtime DB credentials may have narrower permissions than maintenance credentials.

The optional managed Host `OutputRetentionDays` setting enables age expiry of completed Build output
after restart (1–3650 days). Omission/null preserves output by age; ingestion quotas still apply.
Expiry records availability gaps without changing producer evidence, then retries private Storage
deletion. Already committed expiry/deletion intent survives restart or later policy disablement.
Artifacts and full TeamCity/per-project retention remain separate contracts.

Native C#/Git run admission accepts optional `secretEnvironment` under `run_build` plus
`customize_build_parameters`. These D45 temporary values override ordinary Agent environment
aliases, never expand into scripts/parameters, and have no readback API. Recovering queued/running
work requires the existing persistent Identity Data Protection directory as well as PostgreSQL and
object data: collected secret input and new durable assignments are encrypted per Build. Registered
exact UTF-8 values are masked in stdout/stderr before Agent spool persistence, including across
chunk boundaries. Permanent Project secrets and arbitrary artifact redaction are separate scope.

`nodes` emits masked Chiral views. Saves remain separate from active resources until restart;
startup refuses missing/unready nodes, pending/unknown/checksum-drifted schema and unusable Identity
keys before opening listeners. Object HTTP stays on a distinct loopback listener. The installation
password is disclosed only in the intended private startup log after its framework hash commits.
These commands establish infrastructure, ordinary account/My keys pages and Workbench Access.
The fresh C#/Git catalogue path is in progress: optional Host `DefinitionSources` locates the
control repository and explicit local settings-source registry. Prepare schema-2 C# declarations
using the [selected activation contract](design/csharp-activation.md), provision its pinned Linux
SDK image, then submit the native authenticated reconcile action and observe its status location.
The Host must have Docker daemon access; project code receives neither that access nor Host mounts,
network, settings or credentials. An unavailable evaluator cannot fall back to Host execution.
Catalogue metadata is read under current project visibility, while Agent/build/chain integration
remains due. This is not a completed local-run or US-013 claim.

The Controller build includes the locked TypeScript/Vite Workbench assets. Root `dotnet build`
runs `npm ci` and `npm run build` when frontend inputs change, so Node/npm are build prerequisites.
An externally prepared frontend may use `-p:BuildWorkbench=false`; its `Workbench/dist` must already
exist and is still copied into the Host output/publish tree. This is how the owned Linux browser
contour separates its pinned Node build stage from .NET compilation.

Selected real Host/Git/Docker/PostgreSQL checks are explicit in `tests/Vivarium.DefinitionProof`:
`ProductionEvaluatorPhysicalTests`, `ProductionCatalogPhysicalTests`, `RuntimeHostPhysicalTests`,
and the existing Host/Identity/access groups. The closed ordinary Identity and Workbench Access
browser contour is prepared with `python tools/identity/prepare.py`, then executed with
`python tools/identity/run.py <printed-context-path>`. Its summaries require clean owned processes
and container removal; it is not the D43 author-code evaluator or complete R0/story acceptance.

`RuntimeHostPhysicalTests` additionally executes the published, byte-verified TeamCity CLI 0.5.6
as separate Node processes, with an isolated profile and stock ordinary-account credentials.
It requires both pinned npm tarballs in `artifacts/us013-cli056-20261102` and matching installed
`@eyeauras/teamcity-cli` / sibling `@eyeauras/cli-factory` packages; `VIVARIUM_PROOF_TEAMCITY_CLI`
can select the CLI package directory. Fetch the exact tarballs with `npm pack` for version `0.5.6`;
the fixture verifies the recorded archive digests and every packaged installed file. The owned Host
listens on its allocated public port for the dedicated Docker Agent; Storage remains loopback-only.
The Linux process uses pinned runtime image
`mcr.microsoft.com/dotnet/runtime@sha256:939555726cae4c916d212323ac3c0792996dcac2614f00e82cc40fcf059e1af3`,
its own labelled volume and unprivileged UID, and is removed before the later Windows secret journey.
`DefinitionGitWriterPhysicalTests` separately cover real Git CAS, checkout recovery, local-edit
preservation, and rejection of hook/filter execution. These are supporting integration checks;
the R0 ledger still requires explicit complete-case bindings and milestone review.

`R0AcceptanceCases` runs independent selected journeys against the actual fresh Host, PostgreSQL,
private Storage and owned Windows Agent. One migrated/bootstrap database and one genuinely activated
C#/Git database serve as templates; every case receives its own database, Git checkout, private keys,
object directory and ordinary credentials. Only one case Host is live at a time. Runtime connections
use a bounded PostgreSQL pool; teardown clears only that case's pool before cloning/dropping its
database. Reusing the administrative unpooled connection for every runtime query can exhaust Windows
ephemeral ports and is a fixture failure, not REST conformance evidence.

Run this group explicitly with `dotnet test tests/Vivarium.DefinitionProof --filter
FullyQualifiedName~R0AcceptanceCases`. Its bounded process gates, selective production clocks and
transport/Storage faults exercise current access, live/resumed output, redaction, gaps, expiry,
Host restart, C#/Git rejection and source handoff. The separate Identity browser contour proves real
OIDC entry and a pre-grant My keys bearer observing current grant/withdrawal/disable effects.
These standard NUnit/TRX results require explicit case and input-provenance bindings; passing the
group alone does not close the selected reference cohort, R0 or US-013.

The [first executable REST cohort](design/teamcity-rest-first-cohort.md) now publishes concrete
ordinary/replay/composition/raw commands and evidence. Root build also compiles the separately
invoked `tests/Vivarium.TeamCity.Conformance` project; its legacy-controller raw five-case product-ready
run remains nonzero. Fresh Host process checks do not replace that baseline or create delivered
TeamCity GREEN cases. Broader
P0 foundation was delivered in PR #164; R0 product binding and cumulative conformance remain open.
The [local mutation cohort](design/teamcity-rest-local-mutations.md)
separately records twelve observed/replay-bound workflows and ten product-prerequisite-blocked variants.

D38 adds [explicit REST conformance verification](design/teamcity-rest-test-first.md) from the first
P0 harness delivery: standard solution checks, reference-replay control, delivered conformance cases
and the full raw product report. The implementation must publish exact commands and wire every
target into required verification. Reuse current NUnit/Test SDK and standard result artifacts; no
replacement runner is implied. A dedicated conformance target may remain outside the ordinary
all-GREEN selection while required behavior is unfinished. All targets remain available/buildable;
execute them at the [declared due checkpoints and reuse rules](design/api-ledger-delivery.md#valid-evidence-reuse-including-delivery-to-main),
including the mandatory first raw baseline and required real milestone/cumulative acceptance.
Ordinary handoff is root build/test plus affected checks; target availability imposes no automatic
full future-known-RED run per task or status query.
Raw full-suite RED remains visible/nonzero; passing a declared partial gate does not mean full
compatibility. Setup failures and regressions in previously passing cases cannot be allowed RED.

P0.REST.2 first proves a small deterministic composition around production services/HTTP middleware,
with controlled timers and scripted external actors. WireMock is the HTTP replay adapter, not a
required extra hop for every case or a second TeamCity model. Measure code/data/setup/run/cleanup
cost and prove reset/isolation before expanding the corpus. Real PG/Git/Storage, Kestrel/CLI/Agent
and process/platform claims retain their corresponding real checks; see the linked D38 contract.

.NET 10 solution; `dotnet build` / `dotnet test` at the root remain the required baseline
(AGENTS.md → Verification). `global.json` pins the SDK, package references pin Cake.Frosting and
application dependencies, and `toolchains.lock.json` pins downloaded cargo-nextest bytes.

The provider-neutral build entry point is the Cake.Frosting application under `build/`:

```text
dotnet run --project build/Vivarium.Build.csproj -- --target Help
dotnet run --project build/Vivarium.Build.csproj -- --target CI
dotnet run --project build/Vivarium.Build.csproj -- --target PayloadSmoke
```

`CI` builds the solution in Release configuration and writes deterministic TRX under
`out/test-results/<os>`. TeamCity invokes Cake targets rather than duplicating build commands in
Kotlin DSL. Every `dotnet build`, `dotnet test`, and `dotnet publish` invocation is bounded to one
MSBuild worker to avoid memory spikes on self-hosted agents.

For runnable local builds, `Compile` targets the current host by default; `CompileAll`
cross-publishes the supported matrix sequentially:

```text
dotnet run --project build/Vivarium.Build.csproj -- --target Compile
dotnet run --project build/Vivarium.Build.csproj -- --target Compile --rid win-x64
dotnet run --project build/Vivarium.Build.csproj -- --target Compile --rid linux-x64
dotnet run --project build/Vivarium.Build.csproj -- --target Compile --rid linux-arm64
dotnet run --project build/Vivarium.Build.csproj -- --target Compile --rid osx-arm64
dotnet run --project build/Vivarium.Build.csproj -- --target CompileAll
dotnet run --project build/Vivarium.Build.csproj -- --target DockerImage
dotnet run --project build/Vivarium.Build.csproj -- --target Test
```

Each self-contained single-file tree is written to `out/build/<rid>/`:

```text
server/viv-server[.exe]
server/appsettings*.json
server/wwwroot/**
agent/viv-agent-update[.exe]
agent/bootstrap.json.sample
agent/agent/current/viv-agent[.exe]
agent/agent/version
cli/viv-cli[.exe]
```

Supported RIDs are `win-x64`, `linux-x64`, `linux-arm64`, and `osx-arm64`. Cross-publishing does not
execute foreign binaries. `Test` always runs for the native host and rejects an explicit foreign RID.
Compile stamps one product version into every binary and the Agent runtime marker.
`VivariumVersionBase` owns `major.minor`; local builds default to `major.minor.0`,
`--build-counter <number>` produces `major.minor.number`, and `--build-version <SemVer>` reproduces an
exact version. CLR Assembly/File versions remain `major.minor.0.0` so an unbounded TeamCity counter
cannot overflow their 16-bit numeric fields; runtime/package/release identity uses the SemVer
informational version. Trimming remains disabled until proven safe; NativeAOT is only a possible later
optimization.

The first managed-local configuration repository implementation uses the system `git` executable
(D29). Development/controller hosts exercising that slice need a compatible Git on `PATH`. This is not
yet a release-support claim: controller packaging must bundle Git or verify/document the prerequisite
on every supported controller RID before an end-user release.

`DockerImage` requires a local Docker engine and an existing `out/build/linux-x64/server` tree. It
builds `viv-server:<version>` from those already-compiled bytes and runs the containerized server's
`--version` probe; it never recompiles the product inside Docker.

## Test tiers (D20)

| Tier | What | Runs where |
|---|---|---|
| 1. Logic | scheduler/compat matching, matrix expansion, template vars, TRX/JUnit adapters vs golden files, blob store (hash verify, ref-counted GC), fencing/idempotency, state machines on **virtual time** | local Cake `CI`; TeamCity `Compile` |
| 2. Protocol/process | real Kestrel on loopback + real Agent processes: Session/Welcome, enrollment + authorization, exact upgrade health, real bootstrap activation/LKG rollback, reconnect → ghost re-adoption, responsiveness and result idempotency | local Cake `CI`; TeamCity `Compile`; native process fixtures where supported |
| 3. FakeMachineProvider | simulated pool VMs backed by local Agent processes: full D8 conveyor, pool grow/drain, INFRA recycling and canaries — deterministic, zero hypervisors | not automatic until the provider exists |
| 4. Real hypervisor E2E | QEMU/KVM and Hyper-V smoke on explicitly provisioned hosts | TeamCity self-hosted agents, scheduled/manual, later |

GitHub Actions is intentionally disabled and `.github/workflows/ci.yml` is absent. TeamCity owns CI/CD
through four explicit configurations:
`Compile`, `Release`, `Publish / GitHub`, and `Publish / Docker`. Compile runs the complete test suite
once, cross-publishes all four RIDs sequentially, and runs a short native product smoke. Release
packages only the exact Compile artifacts. The independent GitHub and Docker publishers consume
Release directly; Docker requires a Linux-container engine only for image packaging. Cross-publishing
foreign RIDs does not require an agent for their operating system. The default-branch VCS trigger
belongs only to Compile; fork pull requests must not execute repository-controlled code on persistent
agents.

Tier 2 includes the implemented session/lifecycle/queue/REST tests, two-Agent upgrade isolation,
restart recovery, and real Bootstrap child success/rollback fixtures on Linux/macOS. Equivalent Windows
process evidence, native D31 containment, and kill-mid-build coverage remain gates; tiers 3 and 4 await
providers. Payload portability targets remain explicit diagnostics for NUnit/MTP, macOS transfer, and
checksum-verified cargo-nextest archive/remap execution.

**Protocol compatibility must be a CI job, not just a promise**: after the first release exists, the
tier-2 suite will also run against the *previous release's* agent binaries cached from GitHub
Releases. Backward compatibility within a minor version is already the AGENTS.md rule; that pending
job will enforce it — the HLK lock-step failure is the cautionary tale (prior-art).

**Dogfooding target**: once installers, launcher upgrades, and a managed farm exist, the Vivarium repo
will carry its own `vivarium.yaml` and the farm will run the agent suite across its own machines.
Canary builds then gate agent rollouts so a broken agent build never reaches the whole fleet.

## The Agent release loop

Agent and Server are inseparable components of one release. The release job publishes the Server with
one bounded Agent ZIP per supported RID and a schema-v1 catalog. The catalog must contain exactly one
entry for every supported RID, and every Agent version must exactly equal the Server version. A packaged
Server discovers `agent-packages/catalog.json` beside its executable; an explicit
`--agent-package-catalog` / `VIVARIUM_AGENT_PACKAGE_CATALOG` override exists for packaging and test
fixtures. Starting the new Server makes its Agent component available but never restarts the fleet.

The canary command selects the machine, not package bytes:

```
viv-cli agent upgrade <agent-id> --reason "current Server release canary"
viv-cli agent upgrade-status <operation-id>
# Before handoff this cancels; after handoff it durably requests exact LKG rollback.
viv-cli agent upgrade-rollback <operation-id> --reason "canary failed"
```

The controller drains that Agent without interrupting its active Build and records `HANDOFF_READY`
before either package bytes or `RestartAgent` become available. It restores eligibility only after the
exact new process and bootstrap complete the durable promotion/commit handshake. Cancelling before
handoff releases the Agent; cancelling afterward requests LKG rollback and retains the drain until the
controller observes the exact previous digest. Retry by creating a new operation after `rolled-back`.
Both upgrade and rollback wait for a terminal result by default; use `--no-wait` to submit either
operation asynchronously. This slice is deliberately per-Agent; release channels,
group selection, and fleet-wide canary/rolling orchestration still need to be built on the same
operation.

Old packages remain available only to finish, diagnose, or roll back already recorded operations. A new
upgrade cannot select one. Source builds without a release catalog may run for controller development,
but Agent upgrade creation fails closed until a valid current-release catalog is present. The hidden raw
package endpoint is enabled only by integration fixtures; it is deliberately absent from the CLI and
OpenAPI.

## Releases (D19)

The Cake release candidate workflow consumes previously compiled trees. The same counter or exact
version must be supplied to Compile and Release:

```text
dotnet run --project build/Vivarium.Build.csproj -- --target CompileAll --build-counter 123
dotnet run --project build/Vivarium.Build.csproj -- --target Release --build-version 0.1.123
dotnet run --project build/Vivarium.Build.csproj -- --target ReleaseSmoke \
  --rid <native-rid> --build-version 0.1.123
```

Release performs no compilation or tests. It requires all four `out/build/<rid>/` trees and creates
deterministic `viv-server-<rid>.zip`, `viv-agent-<rid>.zip`, and `viv-cli-<rid>.zip` assets. Each public
Agent archive is an unstamped installation template:

```text
viv-agent-update[.exe]
bootstrap.json.sample
agent/current/viv-agent[.exe]
agent/version
```

Each Server archive embeds those exact four public templates under `packages/agents/`. It also embeds
four separate child-only D30 packages under `agent-packages/` plus their schema-v1 `catalog.json`;
Server startup imports that catalog and fails closed if it is incomplete, corrupt, or version-skewed.
Release runs a native smoke from the final ZIP, including Server startup/catalog import, static assets,
exact `viv-cli --version`, fail-closed Agent usage, and missing-config updater behavior.

The TeamCity graph is `Compile -> Release -> { Publish / GitHub, Publish / Docker }`. Compile owns the
patch counter and exact source SHA, cross-publishes all four RIDs, and guarantees one
`major.minor.build` code version for the complete artifact set. Release inherits those exact artifacts
and version without rebuilding.

`Publish / GitHub` uses the GitHub REST API to create or verify `v<version>` at the source SHA, resume
only a byte-identical draft, upload missing assets, and publish it. An already-published release with
the exact expected asset names, sizes, and GitHub-reported SHA-256 digests is an idempotent success. It
requires one TeamCity password parameter, `github.release.token`, with repository Contents write
access; its absence does not block Docker publication.

`Publish / Docker` builds the exact `linux-x64` Server ZIP into
`registry.eyeauras.net:5000/ixab3r/viv-server`, probes its stamped version, and pushes both `<version>`
and `latest` tags. It does not deploy or restart a controller. Dockerfile, context, and version inputs
come from the DSL; the overridable destination inputs default to
`DockerRepository=registry.eyeauras.net:5000/` and `DockerImageName=ixab3r/viv-server`.

The pipeline can now produce initial portable releases, but signing, previous-release compatibility
CI, installer trust, the portable Controller system-Git prerequisite, and native evidence on
additional operating systems remain release quality gates. Binaries are unsigned for now, so Windows
SmartScreen/MOTW and macOS Gatekeeper friction remain documented limitations (§13).

## Upgrading a farm

The controller package instructions immediately below describe the **current legacy SQLite
prototype**, not the D35 replacement backup contract. The new Host uses PostgreSQL, Storage, private
Chiral settings and durable authentication keys. Its supported release/restore procedure must prove
the [complete backup boundary](design/persistence.md#backup-and-restore); copying `vivarium-data/`
alone is insufficient when resources are external. Agent bootstrap freeze rules remain in force.

- **Controller (Docker)**: back up the `vivarium-data` volume, then run
  `docker compose -f deploy/docker-compose.yml pull` followed by
  `docker compose -f deploy/docker-compose.yml up -d`. The replacement container reuses
  `/var/lib/vivarium`; schema migrations are forward-only and applied on startup. Pin the image to an
  explicit version instead of `latest` when rollout timing must be controlled.
- **Controller (portable ZIP)**: stop → back up `vivarium-data/` (SQLite backup + blob dir copy) →
  replace the binary → start. Schema migrations are forward-only and applied on startup.
- **Agents**: import/publish immutable packages, upgrade a canary Agent, then request the remaining
  per-Agent operations centrally. Active work drains first; no host login or reboot is required.
  Upgrade creation requires the current `vivarium.bootstrap-supervisor.v1` capability; an
  interactively/manual-only Agent is deliberately ineligible.
  Fleet/group rollout automation is still pending and must not bypass the per-Agent operation.
- **Bootstrap**: D30 implements authenticated manifest/token handoff, content-addressed activation,
  a singleton supervisor/child lease, crash-recoverable two-sided finalization, skew-safe monotonic
  rollback, exact prior binding, strict persisted-package receipts, durable child-termination
  reporting, and exact LKG reconciliation. It remains change-controlled until Windows,
  bad-download/interrupted-activation, bootstrap-kill/re-adoption, and D21 installer-authenticity
  evidence close the freeze gate.
- **CLI**: replace the binary. Slightly stale clients are a compatibility target, not yet an enforced
  guarantee; the previous-release suite and capability handshake remain roadmap work.

## Enrollment paths

The planned installer slice provides two equivalent doors (§8.4, D19), both converging on the panel's
**Authorize** click:

1. **Preconfigured zip** — TeamCity-style, the comfortable default: the panel's Downloads page stamps
   the archive at request time with a ready `bootstrap.json` (controller URL, certificate
   fingerprint, enroll token). Unzip → run → the machine appears unauthorized. Works from a USB stick
   and in air-gapped labs; the GitHub-Releases agent zips are the unstamped templates this is built
   from.
2. **One-liner** — for a shell you are already in (§8.4).

For automation there is also the scriptable form:

```
viv-agent enroll --url https://ctrl:8443 --fp SHA256:9F3A... --token <enroll-token>
```

These Downloads, setup-script, and explicit `enroll` entry points are target UX, not commands exposed
by the current binaries.

`enroll` is an **agent** verb (it writes `bootstrap.json` and registers the logon task for
bootstrap); bootstrap itself stays the frozen dumb loop. Running the agent interactively in a console
— no service, no task — is a first-class mode for debugging on any machine.

## Owned US-013 actual T3 acceptance

The actual package-only recipe is prepared by `tools/us013-acceptance/prepare-source.py` from
T3 commit `7ff65cada80dd817aa5d7467b85e78ec30c578a9`. It writes only the owned
`artifacts/us013-t3-recovery-v3` source/receipts (prior prepared checkouts are preserved); `--clone-from` is read-only. The script verifies
existing adapted bytes before reuse. Its tracked publisher inputs and source-plan translation
make preparation reproducible. No built T3 GUI, CLI archive or packaged server is launched.

On the selected Windows machine, using its existing source repository:

```powershell
python tools/us013-acceptance/prepare-source.py --clone-from D:\Work\t3code
node --test tools/us013-acceptance/publication-state-tests.mjs
dotnet test tests/Vivarium.DefinitionProof --filter TestCategory=US013ActualT3Setup
```

`US013ActualT3Setup` compiles the three real declarations through the isolated evaluator and
checks fresh Storage configuration; it neither builds T3 nor publishes. The actual
`US013ActualT3` case requires `VIVARIUM_US013_TOKEN_FILE` to name the private file delivered by
the secret-entry tool, containing a fine-grained token for only `iXab3r/vivarium-us013-e2e`,
Contents read/write. The token value must not be put in a command, Git or a log. The fixture
removes that file-path variable from child environments, and D48 gives the value only to
Prepare/Release through one encrypted per-run input; Windows receives no publication value.

The real case uses a separate Engine capped at12GiB/8CPU, a dedicated Linux Agent and this
Windows Agent, explicit physical-host requirement mappings, a fresh PostgreSQL/Storage Host
and the unchanged published TeamCity CLI. The build deadlines remain90/90/15minutes; the CLI
wait has210minutes plus a bounded process margin. Prerequisites include the recorded Windows
VS/Python/vite-plus build tools, Docker and pinned Playwright Chromium. Before heavy work a
credential probe creates and removes one uniquely named ref in the test repository; this is
fixture credential preparation, not Agent publication evidence.

```powershell
$env:VIVARIUM_US013_TOKEN_FILE = '<private secret-card file path>'
dotnet test tests/Vivarium.DefinitionProof --filter TestCategory=US013ActualT3
```

The scoped run records progress under `artifacts/us013-t3-acceptance/runs`, retains source,
actual Agent/result/byte provenance and verifies a controlled partial-draft publisher restart,
published-state repetition, exact GitHub downloaded asset hashes and native/compatible/UI
readbacks after Host restart. Default publication is a real test-repository release. An absent
credential or missing prerequisite is not a successful or skipped acceptance case. The complete
story still needs the cumulative ledger/R0 and independent milestone review/main gates.

The actual recipe fixture uses a unique short `artifacts/w/<8-hex-id>` Host workspace and explicit `a` Windows Agent data root;
`workspace.json` in the run evidence records its exact root. It retains that private workspace
after stopping owned runtime resources, avoiding an unbounded recursive attribute walk through
installed dependency trees during test teardown. Retained workspaces require separate owned
cleanup after evidence review. `US013ActualT3Runtime` is a cheap focused regression: supply
`US013_ESBUILD_PROBE` with the existing Windows esbuild 0.28.2 executable. It checks Node and
shell spawning in the real fixture path layout and retained-workspace disposal without a build,
database, credential, publication or application launch.

The focused runtime category also requires `US013_NSIS_PROBE` (installed makensis.exe) and
`US013_NSIS_TEMPLATES` (existing app-builder-lib templates). It copies the unchanged templates
into the actual pnpm checkout layout, checks every template path stays below250characters,
and compiles a real NSIS script including StdUtils.nsh. No installer is executed.

The actual T3 Windows Agent requires native Windows `System32/tar.exe` for Node archive
extraction with drive-letter paths. Only this owned Agent prepends System32 to its inherited
PATH, retaining all other toolchain entries; ordinary fixtures and machine settings are unchanged.
The runtime category proves inherited Cygwin-first failure when present, native tar selection
and version, and exact extracted bytes from an archive and destination containing spaces.

The actual T3 fixture opts into owned PostgreSQL retention. Terminal disposal retains its
owned container, volume and network, the case database, Storage objects, definitions, managed
settings and identity keys. The Windows workspace ACL restricts inherited access before any
private state is created. `recovery.private.json` inside the case directory contains private
connection/settings references: never attach it to an issue or print its contents.
`R0AcceptanceInstallation.ReopenRetainedAsync(checkpoint)` verifies Docker ownership labels,
connects to the retained PostgreSQL and starts the common Host directly from existing managed
settings, without seeding, cloning a database or replacing keys. Keep PostgreSQL running at its
original loopback port. This is fixture retention, not a portable backup or a product restore API.
`US013Recovery` proves a real tiny Build and authorized artifact SHA through disposal/reopen,
including denial of an anonymous artifact inventory read. Ordinary fixtures retain their cleanup.

For Release-only recovery, first reopen that private checkpoint and enumerate the original
builds/artifacts through the existing authorized API. Preserve the recorded source endpoint/SHA
and restore its read-only Git service, then enroll an owned compatible Linux Agent and submit
`T3Personal_Release` through the common admission service under the original actor's current
grants. Verify successful-only dependency selection reuses the original Prepare/Windows builds
before allowing execution; stop if it selects either producer for execution. Do not seed build
rows, re-register loose bytes, or change the old adapted source/manifest identity. A further
heavy run requires root coordination; the one-run acceptance command is not a recovery command.
