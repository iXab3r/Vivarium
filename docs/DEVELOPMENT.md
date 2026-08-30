# Developing Vivarium

How Vivarium itself is built, tested, released, and upgraded. The *decisions* live in
[`ARCHITECTURE.md`](ARCHITECTURE.md) (D19 — portable distribution, D20 — test tiers); this file is
the practical companion.

## Building

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

## Test tiers (D20)

| Tier | What | Runs where |
|---|---|---|
| 1. Logic | scheduler/compat matching, matrix expansion, template vars, TRX/JUnit adapters vs golden files, blob store (hash verify, ref-counted GC), fencing/idempotency, state machines on **virtual time** | local Cake `CI`; TeamCity `Compile` |
| 2. Protocol/process | real Kestrel on loopback + real Agent processes: Session/Welcome, enrollment + authorization, exact upgrade health, real bootstrap activation/LKG rollback, reconnect → ghost re-adoption, responsiveness and result idempotency | local Cake `CI`; TeamCity `Compile`; native process fixtures where supported |
| 3. FakeMachineProvider | simulated pool VMs backed by local Agent processes: full D8 conveyor, pool grow/drain, INFRA recycling and canaries — deterministic, zero hypervisors | not automatic until the provider exists |
| 4. Real hypervisor E2E | QEMU/KVM and Hyper-V smoke on explicitly provisioned hosts | TeamCity self-hosted agents, scheduled/manual, later |

GitHub Actions is intentionally disabled and `.github/workflows/ci.yml` is absent. TeamCity owns CI/CD
through exactly three configurations: `Compile`, `Release`, and `Publish`. Compile runs the complete
test suite once, cross-publishes all four RIDs sequentially, and runs a short native product smoke.
Release packages only the exact Compile artifacts. Publish is a guarded deployment that uploads the
verified Release candidate. The default-branch VCS trigger belongs only to Compile; fork pull requests
must not execute repository-controlled code on persistent agents.

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

The TeamCity chain is `Compile -> Release -> Publish`. Compile owns the patch counter and exact source
SHA; Release inherits its artifacts and version; Publish uses the GitHub REST API to create or verify
`v<version>` at that source SHA, resume a compatible draft, upload missing assets, and publish it. An
already-published release with the exact expected asset names, sizes, and GitHub-reported SHA-256
digests is an idempotent success. Publish
requires one TeamCity password parameter, `github.release.token`, with repository Contents write
access. The pipeline can now produce initial portable releases, but signing, previous-release
compatibility CI, installer trust, and native evidence on additional operating systems remain release
quality gates. Binaries are unsigned for now, so Windows SmartScreen/MOTW and macOS Gatekeeper friction
remain documented limitations (§13).

## Upgrading a farm

- **Controller**: stop → back up `vivarium-data/` (SQLite backup + blob dir copy) → replace the
  binary → start. Schema migrations are forward-only and applied on startup.
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
