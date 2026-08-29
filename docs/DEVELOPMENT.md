# Developing Vivarium

How Vivarium itself is built, tested, released, and upgraded. The *decisions* live in
[`ARCHITECTURE.md`](ARCHITECTURE.md) (D19 — portable distribution, D20 — test tiers); this file is
the practical companion.

## Building

.NET 10 solution; `dotnet build` / `dotnet test` at the root remain the required baseline (AGENTS.md →
Verification). `global.json` pins the SDK, package references pin Cake.Frosting and application
dependencies, and `toolchains.lock.json` pins downloaded cargo-nextest and GitHub CLI bytes.

The provider-neutral build entry point is the Cake.Frosting application under `build/`:

```text
dotnet run --project build/Vivarium.Build.csproj -- --target Help
dotnet run --project build/Vivarium.Build.csproj -- --target CI
dotnet run --project build/Vivarium.Build.csproj -- --target PayloadSmoke
```

`CI` builds the solution in Release configuration and writes deterministic TRX under
`out/test-results/<os>`. The payload targets
cover native NUnit/MTP execution, Linux-to-macOS artifact transfer, and checksum-verified pinned
cargo-nextest archive/remap execution. TeamCity invokes the core Cake targets rather than duplicating
build commands in Kotlin DSL; the payload targets remain explicit diagnostics. Cake bounds every
`dotnet build`, `dotnet test`, and `dotnet publish` invocation
to one MSBuild worker to avoid memory spikes on the free self-hosted agents; independent TeamCity
configurations can still run concurrently when compatible agents are available.

For runnable local builds, use `Compile` with an optional target RID. With no `--rid`, it compiles for
the current host; `CompileAll` cross-compiles the complete supported matrix sequentially:

```text
dotnet run --project build/Vivarium.Build.csproj -- --target Compile
dotnet run --project build/Vivarium.Build.csproj -- --target Compile --rid win-x64
dotnet run --project build/Vivarium.Build.csproj -- --target Compile --rid linux-x64
dotnet run --project build/Vivarium.Build.csproj -- --target Compile --rid linux-arm64
dotnet run --project build/Vivarium.Build.csproj -- --target Compile --rid osx-arm64
dotnet run --project build/Vivarium.Build.csproj -- --target CompileAll
dotnet run --project build/Vivarium.Build.csproj -- --target Test
```

Each self-contained single-file tree is written to `out/build/<rid>/` with these runnable paths:

```text
server/viv-server[.exe]
server/appsettings*.json
server/Vivarium.Controller.staticwebassets.endpoints.json
server/web.config                             # Windows only
server/wwwroot/**
agent/viv-agent-update[.exe]
agent/bootstrap.json.sample
agent/agent/current/viv-agent[.exe]
agent/agent/version
cli/viv-cli[.exe]
```

Cross-publishing does not execute foreign binaries. `Test` always runs for the native host and rejects
an explicit non-host RID; target-native execution evidence must be collected on that target OS and
architecture. Compile stamps the selected product version into the binaries and the agent's runtime
version marker. `VivariumVersionBase` owns the human-selected `major.minor`; local builds default to
`major.minor.0`, `--build-counter <number>` produces `major.minor.number`, and
`--build-version <SemVer>` reproduces an exact version.

The Compile targets produce the matrix of self-contained single-file builds. The four shipped
executable projects set `PublishSingleFile` explicitly, while Cake supplies the RID, self-contained
mode, source identity, and deterministic debug settings:

```
dotnet publish src/Vivarium.Controller -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out/controller/win-x64
dotnet publish src/Vivarium.Agent -c Release -r <rid> --self-contained -p:PublishSingleFile=true -o out/agent/<rid>
dotnet publish src/Vivarium.Bootstrap -c Release -r <rid> --self-contained -p:PublishSingleFile=true -o out/bootstrap/<rid>
dotnet publish src/Vivarium.Cli -c Release -r <rid> --self-contained -p:PublishSingleFile=true -o out/cli/<rid>
```

Supported RIDs: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`. Trimming is enabled only where
proven safe (gRPC + Blazor are trim-hostile in places); NativeAOT is a possible later optimization,
never a requirement.

## Test tiers (D20)

| Tier | What | Runs where |
|---|---|---|
| 1. Logic | scheduler/compat matching, matrix expansion, template vars, TRX/JUnit adapters vs golden files, blob store (hash verify, ref-counted GC), fencing/idempotency, state machines on **virtual time** | manual/local Cake `CI`; TeamCity `Compile / Windows x64` |
| 2. Protocol (in-process) | real Kestrel on a loopback port + real agent child processes: Session/Welcome, enrollment + authorization, upgrade handshake (bootstrap swaps a fake "new" agent), reconnect → ghost re-adoption, result idempotency, kill-mid-build | manual/local Cake `CI`; TeamCity `Compile / Windows x64` |
| 3. FakeMachineProvider | simulated pool VMs backed by local agent processes (revert = process restart + workdir reset): full D8 conveyor, pool grow/drain, INFRA recycling, canaries — deterministic, zero hypervisors | no automatic CI until TeamCity activation |
| 4. Real hypervisor E2E | QEMU/KVM smoke once that driver exists — GitHub's hosted **Linux** runners expose `/dev/kvm` (Windows runners cannot do Hyper-V); Hyper-V E2E on a **self-hosted** runner: the dev machine first, later the farm itself | KVM: CI, later; Hyper-V: self-hosted, scheduled/manual |

GitHub Actions is intentionally disabled: the remote `ci` workflow is manually disabled and its YAML
is absent from `.github/workflows`. TeamCity owns CI/CD through four platform Compile configurations,
one Release configuration, and one paused Publish deployment. The complete test suite runs exactly
once, in `Compile / Windows x64`; every Compile configuration also runs a short native product smoke
for its own RID. This avoids repeating the same test suite four times while still proving that each
platform's produced executables start natively. Tier 2 contains the implemented
session/lifecycle/queue/control-plane tests, but upgrade-handshake and kill-mid-build cross-process
cases are not complete; tiers 3 and 4 await providers.

The retained payload targets cover the Phase-0 portability contract: NUnit/MTP self-contained + TRX on
all three OSes, macOS ad-hoc signing of cross-published binaries, and nextest archive +
`--workspace-remap`. Automatic TeamCity execution will be limited to the default branch; fork pull
requests must not execute repository-controlled code on persistent agents.

**Protocol compatibility must be a CI job, not just a promise**: after the first release exists, the
tier-2 suite will also run against the *previous release's* agent binaries cached from GitHub
Releases. Backward compatibility within a minor version is already the AGENTS.md rule; that pending
job will enforce it — the HLK lock-step failure is the cautionary tale (prior-art).

**Dogfooding target**: once installers, launcher upgrades, and a managed farm exist, the Vivarium repo
will carry its own `vivarium.yaml` and the farm will run the agent suite across its own machines.
Canary builds then gate agent rollouts so a broken agent build never reaches the whole fleet.

## The agent dev loop

Once D2's authenticated manifest store ships, working on the agent will not involve rebuilding images
or touching machines:

```
dotnet publish src/Vivarium.Agent -c Release -r win-x64 --self-contained -o out/agent/win-x64
viv-cli agent push out/agent/win-x64      # admin scope: publish to the controller's store
```

The target is for every connected agent to pick the build up on its next restart, with
`RestartAgent` broadcasting it immediately. Neither `viv-cli agent push` nor the manifest endpoint exists
in the current binaries, and the bootstrap freeze gate remains pending (§7/D21).

## Releases (D19)

The Cake release candidate workflow consumes previously compiled trees. The same counter or exact
version must be supplied to Compile and Release:

```text
dotnet run --project build/Vivarium.Build.csproj -- --target CompileAll \
  --build-counter 123
dotnet run --project build/Vivarium.Build.csproj -- --target Release \
  --build-version 0.1.123
dotnet run --project build/Vivarium.Build.csproj -- --target ReleaseSmoke \
  --rid <native-rid> --build-version 0.1.123
```

It performs the following work:

1. Require the four existing `out/build/<rid>/` Compile outputs. Release does not compile or test code.
2. Package per-RID zips: `viv-server-<rid>.zip` (the server zip **embeds the agent +
   updater packages for every RID** — an air-gapped farm never phones GitHub), `viv-agent-<rid>.zip`
   (`viv-agent-update` + current `viv-agent` + `bootstrap.json.sample`), `viv-cli-<rid>.zip`.
3. Create deterministic ZIPs with fixed timestamps and entry order while preserving native executable modes.
4. Run one host-native smoke from the final ZIP inside Release: controller startup plus static-asset
   HTTP probe, exact `viv-cli --version`, fail-closed agent usage, and missing-config updater behavior.
   `ReleaseSmoke` remains available to repeat that check explicitly; the full test suite is not repeated.

The exact agent-template tree is:

```text
viv-agent-update[.exe]
bootstrap.json.sample
agent/current/viv-agent[.exe]
agent/version
```

Each controller ZIP contains its static web assets and settings beside the single-file executable,
plus the exact four public `packages/agents/viv-agent-<rid>.zip` bytes. This is a candidate import layout; the controller-side
store and authenticated manifest endpoint are still not implemented.

The versioned TeamCity chain is `Build Number -> Compile / <RID> -> Release -> Publish`. One shared
Build Number dependency supplies the patch component to all four fresh Compile builds in a Release
chain, so every operating system receives the same `major.minor.build` code version. Release has
snapshot and artifact dependencies on all four Compile configurations and only packages those exact
outputs. Publish inherits the exact Release version, has an artifact dependency on Release, and only
uploads that ready candidate to GitHub. GitHub
publication is committed paused and has no trigger. Its Cake target resolves a checksum-pinned GitHub
CLI, requires immutable releases to be enabled, creates `v<version>` at the TeamCity source SHA when
needed, or verifies an existing tag points there, then creates or safely resumes a compatible draft,
verifies GitHub's remote `sha256:` asset digests, and publishes last. An already-published exact
immutable release is an idempotent success; mismatched or extra assets are never clobbered.

There is still no public end-user release. Stable activation is blocked until native evidence exists for
all four RIDs (especially Linux arm64), a macOS TeamCity agent exists, the controller's system-Git
prerequisite from the desired-configuration work is proven on every controller RID, the publish-only
TeamCity secret is configured, and the paused deployment passes draft-resume failure
tests. GitHub Actions is intentionally disabled by project-owner decision; GitHub publication remains a
paused TeamCity deployment and must not be used to bypass these release gates. The portable target keeps
state in an explicit data/install directory so uninstall is removal of that directory; `viv-cli login`
intentionally keeps per-user trust and credentials in AppData/XDG config. Binaries are unsigned for now
— SmartScreen/MOTW friction on Windows and Gatekeeper prompts on macOS are known and documented (§13).

## Upgrading a farm

- **Controller**: stop → back up `vivarium-data/` (SQLite backup + blob dir copy) → replace the
  binary → start. Schema migrations are forward-only and applied on startup.
- **Agents (target after D2 ships)**: update from the controller's store. Roll out with a canary build
  before broadcasting `RestartAgent` fleet-wide. The current Phase 1 implementation does not yet
  publish or serve agent manifests.
- **Bootstrap**: change-controlled, with its freeze gate still pending (§7). Resolving authenticated
  manifest/token handoff requires a numbered design discussion before any source change.
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
