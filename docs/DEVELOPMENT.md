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

The first managed-local configuration repository implementation uses the system `git` executable
(D29). Development/controller hosts exercising that slice need a compatible Git on `PATH`. This is not
yet a release-support claim: controller packaging must bundle Git or verify/document the prerequisite
on every supported controller RID before an end-user release.

## Test tiers (D20)

| Tier | What | Runs where |
|---|---|---|
| 1. Logic | scheduler/compat matching, matrix expansion, template vars, TRX/JUnit adapters vs golden files, blob store (hash verify, ref-counted GC), fencing/idempotency, state machines on **virtual time** | every push, GitHub Actions, all three OSes |
| 2. Protocol/process | real Kestrel on loopback + real Agent processes: Session/Welcome, enrollment + authorization, exact upgrade health, real bootstrap activation/LKG rollback, reconnect → ghost re-adoption, result idempotency, kill-mid-build | every push; bootstrap process fixtures currently Linux/macOS while Windows remains a named gate |
| 3. FakeMachineProvider | simulated pool VMs backed by local agent processes (revert = process restart + workdir reset): full D8 conveyor, pool grow/drain, INFRA recycling, canaries — deterministic, zero hypervisors | every push, GitHub Actions |
| 4. Real hypervisor E2E | QEMU/KVM smoke once that driver exists — GitHub's hosted **Linux** runners expose `/dev/kvm` (Windows runners cannot do Hyper-V); Hyper-V E2E on a **self-hosted** runner: the dev machine first, later the farm itself | KVM: CI, later; Hyper-V: self-hosted, scheduled/manual |

Today CI runs the solution and payload portability jobs on all three hosted OSes. Tier 2 contains the
implemented session/lifecycle/queue/REST tests plus two-Agent upgrade isolation and restart recovery.
On Linux/macOS it also launches the real bootstrap and Agent child processes for successful activation
and one-shot failed-candidate rollback. Equivalent Windows process evidence and kill-mid-build remain
to complete; tiers 3 and 4 await providers.

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

## The Agent release loop

Agent and Server are inseparable components of one release. The release job publishes the Server with
one bounded Agent ZIP per supported RID and a schema-v1 catalog. The catalog must contain exactly one
entry for every supported RID, and every Agent version must exactly equal the Server version. A packaged
Server discovers `agent-packages/catalog.json` beside its executable; an explicit
`--agent-package-catalog` / `VIVARIUM_AGENT_PACKAGE_CATALOG` override exists for packaging and test
fixtures. Starting the new Server makes its Agent component available but never restarts the fleet.

The canary command selects the machine, not package bytes:

```
viv agent upgrade <agent-id> --reason "current Server release canary"
viv agent upgrade-status <operation-id>
# Before handoff this cancels; after handoff it durably requests exact LKG rollback.
viv agent upgrade-rollback <operation-id> --reason "canary failed"
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
vivarium-agent enroll --url https://ctrl:8443 --fp SHA256:9F3A... --token <enroll-token>
```

These Downloads, setup-script, and explicit `enroll` entry points are target UX, not commands exposed
by the current binaries.

`enroll` is an **agent** verb (it writes `bootstrap.json` and registers the logon task for
bootstrap); bootstrap itself stays the frozen dumb loop. Running the agent interactively in a console
— no service, no task — is a first-class mode for debugging on any machine.
