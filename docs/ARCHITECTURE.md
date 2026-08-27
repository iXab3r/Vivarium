# Vivarium Architecture

> This document holds the *shape* of the system; [`AGENTS.md`](../AGENTS.md) holds the *rules* for working on it.
> Status: **design phase** — everything here is a decision record, not a description of running code.
> When a decision changes, this file changes in the same commit.

## 1. Problem and goals

Vivarium runs test corpora against many operating-system configurations: Windows 10/11 at an exact
patch level, Windows with specific third-party software preinstalled, Ubuntu, macOS. Every run starts
from a pristine, versioned machine state, and results land in one *test × scenario* matrix.

Goals, in priority order:

1. **Reproducible machine state.** An image-backed scenario is a versioned, sealed snapshot; a
   pristine build always starts from it. Physical scenarios trade reproducibility for realness —
   deliberately (D16).
2. **Central control.** One controller with a web panel: queue, fleet, image registry, results. Monitoring a farm by hand does not scale past two VMs.
3. **Payload-agnostic builds.** NUnit/.NET is the default test vehicle, but the runner contract must fit anything — Rust test binaries, plain scripts, one-off commands.
4. **Cheap scenario authoring.** Adding "Win10 19044 + product X v1.2" is a small recipe diff plus one build command, not an afternoon of clicking.

Non-goals:

- Not a CI server. CI (TeamCity, GitHub Actions, anything) calls Vivarium via CLI/API and consumes the matrix.
- Not container-based. Real OS installs are the point: patch levels, drivers, services, interactive desktop sessions, macOS.
- Not a general VM manager. Only the operations the test loop needs.

## 2. Core model

Vivarium adopts TeamCity's model **wholesale — entities, statuses, and semantics** — and gives it an
automation-first spin: builds run bulk test corpora across machine *conditions*, and the machine side
grows providers, a pristine lifecycle, and an image registry. TeamCity even contains the seed of our
split already: regular agents (installed once, authorized, auto-upgraded) vs cloud profiles (agents
spawned from images on demand, auto-authorized, discarded after idle). Vivarium generalizes the second
into machine providers (D15) and adds what TeamCity never had: versioned images with sealed snapshots
and revert-to-pristine before a build.

| TeamCity | Vivarium |
|---|---|
| Project / Build Configuration / Build | Same, verbatim (D14) |
| Build Queue; agent requirements vs agent parameters | Same, verbatim |
| Agent status: connected × authorized × enabled × idle/building | Same, verbatim (D8) |
| Agent auto-upgrade from the server | Same — launcher handshake (D2) |
| Regular agents | **Enrolled agents**: physical boxes, long-lived VMs (D16) |
| Cloud profiles / cloud agents | **Machine providers**: hypervisor (clones of ImageVersions), cloud, static pool (D15) |
| Service messages `##teamcity[…]` | Same, verbatim (D14) |
| Artifacts / build log | Content-addressed blob store / streamed log |
| Investigations & muted tests | Planned: matrix triage — mute a test × scenario cell |
| — | **Image registry + pristine clean policy**: sealed versioned snapshots, recipes, drift detection |

Agents may be pets *or* cattle: an enrolled physical machine is a classic TeamCity agent; a
provider-spawned clone lives for exactly one build. **Pristine is a capability and a clean policy
(D5), not the only lifecycle** — a build configuration that just wants a real connected machine runs
on one, as is.

## 3. Components

One controller process, thin drivers, deliberately dumb guests.

```mermaid
flowchart LR
    CLI["viv CLI / CI"] --> C
    UI["Blazor Server panel"] --- C
    C["Controller<br/>ASP.NET Core: gRPC AgentHub + HTTP blob store + scheduler + SQLite"]
    C -- "clone / revert / start / stop / console-endpoint" --> D["Host drivers<br/>Hyper-V · QEMU/KVM · Tart"]
    subgraph Clone ["Machine: VM clone or enrolled physical box"]
        B["Bootstrap (frozen, baked into image / installed once)"] --> A["Agent (pulled + auto-upgraded)"]
    end
    D --> Clone
    A -- "gRPC reverse connect: hello / jobs / logs / status" --> C
    A -- "HTTP: pull payload, push artifacts (sha256)" --> C
```

- **Controller** (`Vivarium.Controller`): build queue, image registry, scheduler, machine providers, agent rendezvous (gRPC), blob store, result store (SQLite), Blazor Server web panel. One Kestrel host serves all of it.
- **Machine providers** (D15): supply agents to the queue — a static pool of enrolled machines (physical boxes, hand-managed VMs), hypervisor providers that spawn clones of `ImageVersion`s on demand (host drivers live here), and later cloud providers for short-living instances.
- **Host driver** (per hypervisor): `Clone(imageVersion) → VmInstance`, `Revert`, `Start`, `Stop`, `TakeSnapshot`, `GetConsoleEndpoint`, plus MAC assignment on clone. Nothing else — no guest file copy, no guest exec (see D1). In-process .NET implementations first; if third-party drivers ever appear, garm's external-executable provider contract is the sanctioned escape hatch.
- **Bootstrap** (`Vivarium.Bootstrap`): the only thing baked into images. Frozen contract (§7).
- **Agent** (`Vivarium.Agent`): pulled by bootstrap at boot; executes jobs, streams logs, uploads results. Deliberately dumb — all decisions live in the controller.
- **CLI** (`Vivarium.Cli`, binary `viv`): submit builds, ad-hoc exec, status, authorize — a client of the same gRPC API as the panel. This is also the CI integration point.
- **Contracts** (`Vivarium.Contracts`): the `.proto` files and generated types shared by all of the above.

## 4. Key decisions

Numbered so later docs and commits can reference them.

### D1. Agents reverse-connect; drivers stay minimal

The controller never reaches *into* a guest (no SSH, WinRM, PowerShell Direct, guest-ops APIs — every
hypervisor has a different zoo of these). Instead the guest agent dials out to a well-known controller
address after boot: hello → receive build → pull payload → stream logs → push results. This is the model
every CI agent uses, and it collapses the per-hypervisor driver surface to clone/revert/start/stop —
which is exactly why adding QEMU or Tart later is cheap. No IP discovery, no firewall pain, no guest
credentials. Physical machines make this non-negotiable: there is no hypervisor to reach through at all.

### D2. Only a frozen bootstrap is baked into images

Baking the agent into snapshots means every agent bugfix rebuilds every snapshot — the #1 operational
pain of VM farms. Images carry only a tiny **bootstrap** with a frozen contract (§7); the real agent is
downloaded from the controller at boot (manifest + sha256), so agent updates are "publish a file".
The controller can tell a running agent to restart (`RestartAgent`), and bootstrap picks up the new version.

The handshake is TeamCity's: on hello the controller compares agent versions and orders a restart when
stale. On physical machines this is the *only* update path — install once by hand, upgrade centrally
forever — which is why bootstrap must stay boring. Sealed snapshots may carry yesterday's agent; the
post-revert upgrade costs one small LAN download, and a periodic maintenance re-seal folds the current
agent back into hot images (D13).

### D3. The build contract is files-in / process / files-out

The runner does not know what NUnit is. A build is: payload blobs (sha256-addressed) → unpack → run
steps (commands with env/cwd/timeout, D14) → collect declared globs → exit codes. *Result adapters* on the controller
side parse well-known formats into the result model:

- **Default payload — NUnit on .NET**, published **self-contained** per RID, executed as a plain exe
  producing TRX (Microsoft.Testing.Platform route; NUnitLite is the classic fallback). No SDK or runtime
  is ever installed in guests — a "pristine customer machine" stays pristine.
- **Rust** plugs into the same pipe: `cargo nextest archive` + the static nextest binary shipped inside
  the payload, JUnit XML out.
- Tests that drive an arbitrary SUT treat it as just another payload artifact; the agent passes
  `VIVARIUM_SUT_PATH`, `VIVARIUM_SCENARIO`, `VIVARIUM_RESULTS_DIR` env vars.

### D4. gRPC control plane, plain-HTTP data plane

One bidirectional gRPC stream per agent (`Session`) carries hello, job assignment, status, log chunks,
heartbeats. Bulk data — payloads and artifacts — moves over plain HTTP on the same Kestrel host
(`GET/PUT /blobs/{sha256}`): resumable, idempotent, deduplicated by construction, debuggable with curl,
and free of gRPC message-size ceilings.

Two consequences of memory-restore drive transport details:

- **Guest clocks wake up in the past**, which breaks TLS certificate validation — including the agent's
  own connection. MVP: h2c (gRPC over cleartext HTTP/2) inside an isolated host-only network, plus a
  bearer token. Later option: pinned self-signed cert with a validation callback that ignores dates.
  Additionally, the controller sends its wall-clock with every hello response and build assignment, and
  the elevated agent corrects guest skew immediately — Cuckoo has shipped exactly this `clock`
  parameter for fifteen years.
- **The agent's TCP connection is dead after restore but doesn't know it.** gRPC keepalive pings
  (~10 s interval / 5 s timeout) on both sides detect it in seconds; the agent treats any disconnect
  as "reconnect and re-hello"; the controller treats a lost lease as an INFRA failure (D9).

### D5. Pristine is a clean policy; its revert point is a memory snapshot

Per build configuration (or machine), the **clean policy** is one of: `pristine` (revert to the sealed
checkpoint before the build — requires snapshot capability), `reboot` (the honest reset physical
machines can offer), `clean-workdir`, or `none` (run on the connected machine as-is — plain TeamCity
behavior). Configurations state what they need through ordinary agent requirements; checkpoints are
first-class, not mandatory. The rest of this decision describes how `pristine` works on managed VMs.

The runtime snapshot of an image version is taken on a *booted, logged-in, idle* system with bootstrap
waiting. Revert-with-memory brings a live agent back in ~2–5 s instead of a 30–90 s cold Windows boot.
Cold boot remains a per-scenario option (boot-time behavior is itself worth testing). Parallel pristine
builds use linked clones / differencing disks (AVHDX, qcow2 backing, APFS COW for Tart) so N clones of a 40 GB
image are instant and near-free; a 32 GB host comfortably runs ~5–6 Windows VMs at 2 vCPU / 4 GB.

### D6. Provisioning is a build with a different epilogue

A build's epilogue is one of: **revert** (pristine builds), **none** (persistent machines — plain
TeamCity behavior), **keep** (debug quarantine), or **seal** (provisioning):
reboot → autologon → wait for a clean hello → take the memory snapshot → register a new `ImageVersion`.
One machinery for everything; there is no separate "image builder" tool. Recipes are declarative files
in git (§8.2), including honest `manual` steps for software that cannot be installed silently.

### D7. Clone ↔ agent correlation is MAC-based

With three parallel clones of the same image, "which VM just said hello?" must be answerable. The driver
assigns each clone a known MAC; the agent reports its MAC (plus a per-boot `boot_id` GUID) in `Hello`.
No per-boot config injection into guests is needed, which keeps memory snapshots valid.

This applies to provider-spawned clones, which are **auto-authorized** because their parent image is
trusted (TeamCity treats cloud agents the same way). Enrolled agents — physical machines, long-lived
VMs — carry a persistent identity instead: a GUID generated at install plus an authorization token
issued when the operator authorizes them, exactly TeamCity's regular-agent flow (§8.4, D16).

### D8. Agent status is TeamCity's; machine lifecycle is Vivarium's

Two separate layers, deliberately.

**Agent status — TeamCity 1:1.** Four independent axes: *connected/disconnected* (a network fact),
*authorized/unauthorized* (an operator decision — unauthorized agents connect and are visible but
never receive builds), *enabled/disabled* (an operator toggle), *idle/building/upgrading*.
Compatibility is computed per build configuration from requirements vs agent parameters, exactly as
TeamCity does. These statuses are mandatory, first-screen information.

**Machine lifecycle — managed machines only.** Provider-spawned VMs additionally walk an explicit
conveyor: `CLONING → BOOTING → READY → BUSY → COLLECTING → REVERTING | DESTROYING`, plus `SEALING`
(provisioning) and `QUARANTINE` (keep-on-fail). Every transition is timestamped and has a timeout;
"stuck in BOOTING for 120 s" is an INFRA alert and an automatic recycle. Physical machines have no
conveyor — their recycle is a reboot, or an operator.

Following LAVA, *health* (good / bad / maintenance / retired — set by canaries and operators) is a
third, orthogonal axis: a canary failure marks an image version, machine, or host bad and removes it
from scheduling without touching in-flight builds. Together these three layers are the scheduler
skeleton, the main panel view, and the alerting source — what makes "monitoring the farm" tractable.

### D9. Failure taxonomy: INFRA / TEST / CRASH

- `INFRA` — no hello, lost heartbeat, revert failure, timeout before the payload ever ran. Retried
  silently — on another clone, or after a reboot on a physical machine; never shown as a test failure.
- `TEST` — the payload ran and reported failures (TRX/JUnit). Never auto-retried.
- `CRASH` — nonzero exit without a result file; dumps collected.

Mixing infra noise into test results is the fastest way to make the matrix untrustworthy; the taxonomy
is enforced in the data model, not by convention. Prior art converges here: GitLab's custom-executor
contract separates `BUILD_FAILURE` from `SYSTEM_FAILURE` exit codes and auto-retries only the latter;
syzkaller classifies merged console+process output into crash / lost-connection / no-output with typed,
bounded-retry infra errors.

### D10. Guests run an interactive desktop by default

Input synthesis, overlays, foreground windows, and installers need a real unlocked session. Images are
built with: autologon, agent launched as a logon task of that user, **elevated** (UAC off or
highest-available — a non-elevated agent can neither fix the clock nor send input to elevated windows
past UIPI), screen lock/screensaver disabled, fixed resolution. Session type is reported in `Hello`; build configurations
can require `interactive`. Enrolled physical machines intended for UI tests follow the same checklist
by hand — the agent reports the truth either way.

### D11. Drift detection

The agent reports the *actual* OS build in `Hello`. If an image claims 19044 and the guest reports
19045, Windows Update leaked through — the panel flags the image version red instead of silently
poisoning the matrix. Images are built with updates/telemetry disabled and Defender exclusions for the
work directory (unless "AV enabled" *is* the scenario — then it is a scenario axis, §8.2).

### D12. Debugging affordances are first-class

- **WER LocalDumps** (Windows) / `core_pattern` (Linux) preconfigured in images — crash dumps of the SUT collect themselves.
- **Screenshot on failure** taken by the agent.
- **keep-on-fail**: the clone moves to `QUARANTINE` instead of being reverted; connect via console.
- **snapshot-the-corpse**: optionally snapshot the VM *at the moment of failure* — the failed state
  becomes revertable-to forever. Nearly free with this machinery; almost nobody offers it.
- **Console access**: the driver exposes a console endpoint (Hyper-V `.rdp` / vmconnect, VNC for
  QEMU/Tart). An embedded web console is a later nicety.

### D13. Fleet maintenance is scheduled work, not heroics

Linked-clone chains grow; blobs accumulate; images rot. The controller schedules: periodic re-baseline
(fresh clone from base), disk compaction, blob GC, snapshot-chain pruning, and **health-check canary
builds** — a trivial boot-hello-run build per image version on a cadence, so a rotten image is caught
by a canary, not by a real run at 2 a.m. Host disk/CPU/RAM are shown on the panel with alerts.

### D14. The work model is TeamCity's, names included

`Project` → `Build Configuration` (steps, requirements, parameters, artifact rules) → `Build`
(queued → running → finished), scheduled from a `Build Queue` onto compatible agents. A scenario
matrix expands into a **matrix build** whose cells are ordinary builds, aggregated composite-style.
Steps have execution policies (default / even-if-failed / always) so diagnostics collection runs even
after a failing test step. Earlier drafts of this document said Suite/Run/Job; the adopted names are
build configuration / matrix build / build.

Live progress uses **TeamCity's service-message protocol verbatim**: the agent scans step stdout for
`##teamcity[testStarted …]` / `testFailed` / `progressMessage` / … and forwards them as structured
events. Every reporter that already speaks TeamCity — NUnit's TeamCity listener, pytest-teamcity,
Gradle, dozens more — becomes a live Vivarium progress source with zero integration work. Authoritative
results remain the collected TRX/JUnit files (D3); service messages only stream the build as it runs.

### D15. Machines come from providers

The scheduler asks `MachineProvider`s for capacity; a provider implements
`Acquire(requirements) → machine` / `Release(machine)`:

- **Static pool** — enrolled machines (physical boxes, hand-managed VMs). Capacity is what it is.
- **Hypervisor provider** (Hyper-V / QEMU / Tart) — spawns clones of sealed `ImageVersion`s when the
  queue holds compatible builds and the pool is below its cap, and destroys/reverts them per policy.
  This is TeamCity's cloud-profile logic verbatim, with snapshots added; spawned agents are
  auto-authorized (D7).
- **Cloud provider** (Azure first; later) — short-living instances from cloud images; the agent is
  baked into the image or installed by an init script and reverse-connects like everyone else. Not in
  the first release, but the seam exists from day one — GitLab's fleeting and garm validated exactly
  this scaler-vs-provider split (see prior-art).

### D16. Physical machines are first-class

A physical box is enrolled with the same one-liner, authorized like any TeamCity agent, and described
by its parameters — it *is* a scenario, with capacity 1 and no pristine capability. Its clean policies
are `reboot` / `clean-workdir` / `none`; INFRA failures mark it bad and notify instead of recycling —
there is nothing to recycle. Later options for pristine-on-metal: PXE re-imaging or disk-restore
tooling, plus WoL/IPMI power management. Builds on physical cells record the agent's full parameter
snapshot in place of an `ImageVersion` (§6).

### D17. Build configurations are code in the tested repo

`vivarium.yaml` lives next to the code it tests; `viv run` submits it together with payload blobs
(sha256-deduped upload). The panel authors the *fleet* — agents, images, pools — and shows results; it
does not author test configurations in v1. Automation-first means the run definition versions with the
product, GitHub-Actions-style, rather than living in server-side UI state. Named matrix cells select
agents via requirement expressions (`os.family == windows`) or, from Phase 2, images
(`image: win10-19044-clean`); template variables (`{rid}`, `{os}`, `{arch}`, `{exe}`, `{results}`)
specialize payload paths and steps per cell so one definition covers every OS. Any red cell makes
`viv run --wait` exit nonzero — CI integration is an exit code, not a plugin.
[`walkthrough.md`](walkthrough.md) is the normative UX for all of this.

### D18. Scenarios are environment × parameters × repetition

The matrix is not an OS list. A **scenario** — the named unit that becomes a matrix column and a rerun
target — is a machine selector (`agent:` expression or `image:`) *plus* a parameter bag *plus* an
optional repeat count. Two definition styles normalize to the same thing: cross-product axes
(`matrix:` with `exclude:`/`include:` pruning, GitHub-Actions-style — the machine axis is just one
axis among value axes) and an explicit named `scenarios:` list for hand-picked combos. Parameters
reach the build as `{param.*}` template variables and `VIVARIUM_PARAM_*` environment variables;
selecting a test subset per scenario is just an argument (`--filter {param.suite}`), not machinery.
Each cell build records its scenario name, resolved parameters, and iteration index alongside "what it
ran on" (§6).

`repeat: N` is first-class (flake hunting, stress): every iteration is an ordinary build; the matrix
cell aggregates them into a pass rate (47/50), and `viv run --repeat N` overrides ad hoc. Repeats on
pristine cells are truly independent runs — that combination is the honest flakiness detector. Several
scenarios matching the same persistent agent serialize on its queue (TeamCity semantics); image-backed
scenarios fan out as parallel clones instead.

The boundary with in-test parameterization stays sharp: values only the test process cares about
belong in NUnit `[TestCase]`; Vivarium parameterizes what the process cannot — the environment, the
invocation, the machine. Naming follows from the matrix itself: *rows* are already test cases (the
payload framework's, with per-test history across scenarios), so the columns are *scenarios*, not cases.

## 5. Protocol sketch

```proto
syntax = "proto3";
package vivarium.v1;

service AgentHub {
  rpc Session (stream AgentMsg) returns (stream ControllerMsg);
}

message AgentMsg {
  oneof msg {
    Hello hello = 1;
    StepStatus status = 2;     // FETCHING / RUNNING step N / COLLECTING
    LogChunk log = 3;          // stdout/stderr, chunked, bounded buffering
    ServiceMessage event = 4;  // parsed ##teamcity[...] from step stdout (D14)
    BuildResult result = 5;    // per-step exit codes + sha256 list of uploaded artifacts
    Heartbeat heartbeat = 6;
  }
}

message ControllerMsg {
  oneof msg {
    BuildAssignment build = 1;
    CancelBuild cancel = 2;
    RestartAgent restart = 3;  // exit; bootstrap fetches the current agent version (D2)
  }
}

message Hello {
  string agent_id = 1;         // persistent GUID (enrolled) / ephemeral (provider-spawned)
  string auth_token = 2;       // issued at authorization; empty while unauthorized (D7)
  map<string, string> parameters = 3;  // os.build, software.*, machine.kind, pristine, ...
  string image_id = 4;         // set for provider-spawned agents
  string boot_id = 5;          // GUID per boot
  string mac = 6;              // clone correlation (D7)
  string agent_version = 7;    // upgrade handshake (D2)
  OsInfo os = 8;               // ACTUAL os/build — drift detection (D11)
  bool interactive = 9;        // live desktop present
}

message BuildAssignment {
  string build_id = 1;
  repeated Blob payload = 2;      // {url, sha256, unpack_to}
  repeated Step steps = 3;        // RunSpec + execution policy (default/even-if-failed/always)
  repeated string collect = 4;    // artifact globs
  OnFail on_fail = 5;             // NONE / KEEP_MACHINE / SNAPSHOT_MACHINE
  map<string, string> parameters = 6;  // resolved scenario params → env VIVARIUM_PARAM_* (D18)
}
```

Blob endpoints: `GET/PUT /blobs/{sha256}`. Bootstrap endpoints: `GET /bootstrap/manifest?os=&arch=`,
`GET /setup.ps1`, `GET /setup.sh` (§8.4).

Build flow: the queue holds builds awaiting compatible agents → a provider supplies one (an idle
enrolled agent, or a freshly spawned clone) → assignment → payload pull (sha-verified) → steps run
(log stream + service messages + heartbeats) → artifact push → result → adapters parse TRX/JUnit →
epilogue per clean policy (D5/D6).

## 6. Data model

TeamCity's entities plus the machine/image layer:

`Project` → `BuildConfiguration` (steps, requirements, parameters, artifact rules, matrix axes) →
`Build` (state, failure class per D9, log, `TestOccurrence`s, artifacts; a matrix build is a composite
aggregating its per-scenario cells). Queue rows reference builds awaiting compatible agents.

`Agent` (identity, version, status axes per D8, parameters, pool) ↔ `Machine` (kind:
`physical | managed-vm | cloud`; capabilities: pristine / console / power; conveyor state for managed
kinds) ← `MachineProvider` (static pool / hypervisor / cloud). `Host` (hypervisor node: driver,
capacity, cpu/ram/disk) → `Image` (recipe ref, lineage) → `ImageVersion` (snapshot ref, recipe hash,
parent version, declared+actual OS build, sealed-at) → managed machines cloned from it. Plus `Blob`.

Every build records what it actually ran on: the exact `ImageVersion` for image-backed cells, or the
agent's full parameter snapshot for physical cells — a historical cell never silently changes meaning,
and the matrix can show "started failing at product-X 1.2 → 1.3".

Storage: SQLite + blob directory. No external services.

## 7. Bootstrap contract (frozen)

The only code baked into images — and installed on physical machines by the setup one-liner. In role
it is exactly TeamCity's agent launcher: the version handshake and the swap live here. It must never
need to change; entirety of its behavior:

1. Read `bootstrap.json` next to itself: `{ controllerUrl, imageId, secret }`.
2. Loop: `GET /bootstrap/manifest?os=…&arch=…` → `{version, sha256, url}`; if the local agent differs,
   download, verify sha256, swap atomically (temp + rename); launch the agent with the config;
   wait for exit; repeat with jittered backoff.

Self-contained single-file .NET; size is irrelevant inside a 40 GB image. Rebuilding images is required
only when the scenario's software set changes (legitimate) or bootstrap itself changes (should not happen).

## 8. Images

### 8.1 Layers

1. **Base images** — OS at an exact patch level + bootstrap + autologon + updates/telemetry off +
   Defender exclusions + WER dumps + fixed resolution. Built manually at first; unattended
   (autounattend.xml / autoinstall) later. Exact-build Windows media comes from UUP dump (operator-run;
   `rgl/uup-dump-get-windows-iso` is the automation model), and `autounattend.xml` can be generated
   from recipe fields with the embeddable MIT `cschneegans/unattend-generator` library.
2. **Scenario versions** — base + provisioning recipe, sealed (D6) into an `ImageVersion` with a
   memory-state runtime snapshot.

### 8.2 Recipes

```yaml
# images/win10-19044-avx.yaml
parent: win10-19044-clean@v3
steps:
  - run: avx-setup-1.2.exe /S
    payload: avx-setup-1.2.exe
  - run: powershell -File tune.ps1
  - manual: "Installer X has no silent mode - click through it in the console, then press Done"
verify: { os_build: 19044 }
network: nat        # nat | offline | full — a first-class scenario axis
```

`manual` pauses the pipeline, points the operator at the VM console, and resumes on confirmation —
manual work is legalized and versioned instead of happening outside the system.

Steps will grow into a typed catalog (Azure DevTest Labs' artifact manifests — title, target OS, typed
parameters, run command — rendered as forms in the panel) and support reboot-and-resume semantics for
multi-reboot installs (Boxstarter's trick). Network profiles are enforced at the host level — deny-all
with an allowlist for the build's duration, as Ludus' testing mode does — which also stops Windows
Update drift *during* long builds, not only between rebuilds.

### 8.3 Runtime snapshot definition

Sealed at: booted → autologon completed → bootstrap idle in its pre-connect wait → memory snapshot.
After every revert, bootstrap's pending connection naturally dies (D4) and it reconnects fresh.
A sealed `ImageVersion` is immutable — clones derive only from sealed versions and never mutate them
(Proxmox's linked-clones-only-from-templates invariant).

### 8.4 Enrollment and authorization

Getting any machine — a physical box or a hand-made VM — into the farm is TeamCity's flow. Install the
OS by hand if needed, then run one line inside it:

```
iwr http://ctrl:8080/setup.ps1 | iex          # Windows
curl -fsSL http://ctrl:8080/setup.sh | sh     # Linux / macOS
```

The script installs bootstrap + autologon task + `bootstrap.json` and starts it. The agent appears on
the panel **unauthorized** — visible, never scheduled; *Authorize* turns it into an enrolled agent
with a persistent identity and token (D7). This is the complete answer to "how do I get the agent onto
a machine": after the one-liner, agent delivery and upgrades are central and automatic forever (D2).

An enrolled VM that lives on a managed hypervisor can additionally be **adopted as an image**: the
controller snapshots it and registers `Image v1`, making it cloneable and pristine-capable. Physical
machines skip this step — they stay persistent agents whose parameters describe their setup (D16).

## 9. Ad-hoc execution

Both are ordinary `BuildAssignment`s:

- `viv exec --image win10-19044-avx -- powershell -c "..."` — clone, run, stream output, revert.
  "Check this quickly on a clean 19044" as a one-liner.
- `viv exec --agent <name> -- ...` — same on a *live* machine (a physical box, a quarantined clone, a
  machine mid-provisioning), no revert. Line-based streaming first; a real interactive terminal
  (ConPTY + stdin channel over the same gRPC session) is a later feature — until then, the console
  button covers interactivity.

## 10. Platforms

| Guests | Runs on | Driver / provider | Pristine mechanism |
|---|---|---|---|
| Windows, Linux | Windows host | Hyper-V (first driver) | memory checkpoints |
| Windows, Linux | Linux host | QEMU/KVM (second) | savevm memory snapshots |
| macOS | Apple hardware only (EULA) | Tart on a Mac mini | none — instant APFS clone + ~20 s boot |
| any | the machine itself | physical / enrolled agent (D16) | none — clean policy `reboot` / `clean-workdir` / `none` |
| any | Azure and friends | cloud provider (later, D15) | fresh short-living instance per build |

The controller sees hosts as pools with capabilities; a Mac mini is just another node running the same
agent contract. Tart is driven as an external CLI, so its Fair Source license (free below 100 CPU
cores, possibly moving to permissive OSS) never links into Vivarium; Orchard — Cirrus' own Tart
orchestrator — is the reference for that driver.

## 11. Web panel

Blazor Server in the controller process (SignalR gives live updates for free; no JS toolchain).
Views: **Agents** (TeamCity-style, mandatory first screen: status axes, parameters, compatibility,
unauthorized newcomers awaiting authorization), **Fleet** (hosts + the D8 conveyor for managed
machines), **Images** (registry: lineage, versions, drift badges, snapshot chains,
build/promote/rollback/prune), **Queue & Builds** (TeamCity-shaped, with live service-message test
progress), **Matrix** (test × scenario — the product of the whole system), console links.

## 12. Prior art

Surveyed separately in [`docs/prior-art.md`](prior-art.md) — openQA, Cuckoo/CAPE, LAVA, syzkaller,
GitLab custom executors, Anka/Orchard/Tart, Packer-based image pipelines, ephemeral-runner managers,
and what Vivarium borrows from each.
