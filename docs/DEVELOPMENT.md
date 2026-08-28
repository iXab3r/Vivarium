# Developing Vivarium

How Vivarium itself is built, tested, released, and upgraded. The *decisions* live in
[`ARCHITECTURE.md`](ARCHITECTURE.md) (D19 — portable distribution, D20 — test tiers); this file is
the practical companion.

## Building

.NET 10 solution; `dotnet build` / `dotnet test` at the root are the whole story (AGENTS.md →
Verification). The release target is a matrix of self-contained single-file builds; explicit
`PublishSingleFile` is required because the project files do not set it globally:

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
| 1. Logic | scheduler/compat matching, matrix expansion, template vars, TRX/JUnit adapters vs golden files, blob store (hash verify, ref-counted GC), fencing/idempotency, state machines on **virtual time** | every push, GitHub Actions, all three OSes |
| 2. Protocol (in-process) | real Kestrel on a loopback port + real agent child processes: Session/Welcome, enrollment + authorization, upgrade handshake (bootstrap swaps a fake "new" agent), reconnect → ghost re-adoption, result idempotency, kill-mid-build | every push, GitHub Actions, all three OSes |
| 3. FakeMachineProvider | simulated pool VMs backed by local agent processes (revert = process restart + workdir reset): full D8 conveyor, pool grow/drain, INFRA recycling, canaries — deterministic, zero hypervisors | every push, GitHub Actions |
| 4. Real hypervisor E2E | QEMU/KVM smoke once that driver exists — GitHub's hosted **Linux** runners expose `/dev/kvm` (Windows runners cannot do Hyper-V); Hyper-V E2E on a **self-hosted** runner: the dev machine first, later the farm itself | KVM: CI, later; Hyper-V: self-hosted, scheduled/manual |

Today CI runs the solution and payload portability jobs on all three hosted OSes. Tier 2 contains the
implemented session/lifecycle/queue/control-plane tests, but upgrade-handshake and kill-mid-build
cross-process cases are not complete; tiers 3 and 4 await providers.

Payload portability smoke tests (ROADMAP Phase 0) run in the same hosted matrix: NUnit/MTP
self-contained + TRX on all three OSes, macOS ad-hoc signing of cross-published binaries, nextest
archive + `--workspace-remap`. Until the farm exists, GitHub Actions *is* our matrix — it bootstraps
the farm that will replace it for everything hosted runners cannot do (exact patch levels,
preinstalled software, interactive desktops, pristine reverts).

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
viv agent push out/agent/win-x64          # admin scope: publish to the controller's store
```

The target is for every connected agent to pick the build up on its next restart, with
`RestartAgent` broadcasting it immediately. Neither `viv agent push` nor the manifest endpoint exists
in the current binaries, and the bootstrap freeze gate remains pending (§7/D21).

## Releases (D19)

Planned: tag `vX.Y.Z` → GitHub Actions release workflow:

1. Build + test the full matrix.
2. Publish per-RID zips: `vivarium-controller-<rid>.zip` (the controller zip **embeds the agent +
   bootstrap packages for every RID** — an air-gapped farm never phones GitHub), `vivarium-agent-<rid>.zip`
   (bootstrap + current agent + `bootstrap.json.sample`), `viv-<rid>.zip`.
3. Attach `SHA256SUMS` and create the GitHub Release; the version flows from the tag (MinVer).

There is no release workflow yet. The portable controller/agent/bootstrap target keeps state in an
explicit data/install directory so uninstall is removal of that directory; `viv login` intentionally
keeps per-user trust and credentials in AppData/XDG config. Binaries are unsigned for now —
SmartScreen/MOTW friction on Windows and Gatekeeper prompts on macOS are known and documented (§13).

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
vivarium-agent enroll --url https://ctrl:8443 --fp SHA256:9F3A... --token <enroll-token>
```

These Downloads, setup-script, and explicit `enroll` entry points are target UX, not commands exposed
by the current binaries.

`enroll` is an **agent** verb (it writes `bootstrap.json` and registers the logon task for
bootstrap); bootstrap itself stays the frozen dumb loop. Running the agent interactively in a console
— no service, no task — is a first-class mode for debugging on any machine.
