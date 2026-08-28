# Machine Providers and Images

> Status: **Accepted**
> Implementation: **Planned**
> Maintainer role: [Machine Providers/Images Expert](../roles/machine-providers-images-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D1, D2, D4-D8, D11-D16, D19-D28

## Purpose and scope

This design defines how Vivarium discovers static capacity, creates and recycles managed VM capacity,
and produces immutable machine images. It covers:

- `MachineProvider`, `ProviderHost`, `ProviderInstance`, Agent attachment, pool, provider-operation,
  and image-registry models;
- static enrolled Agents, Hyper-V/QEMU revert pools, and Tart clone-per-build capacity;
- provider capabilities, host reservations, pool growth/drain, readiness, cleanup, quarantine, and
  canaries;
- Git-backed recipes/policy, sealed image lineage, artifact replicas/distribution, REST resources,
  audit, failure classification, and required evidence.

The provider boundary stops at the guest boundary. Shared Agent leases and fencing belong to
Scheduling; AgentHub and managed-agent identity belong to Agent API/SDK; OS and native driver adapters
belong to Platform; fleet UI belongs to AgentExplorer/UI. This document states what those seams must
provide but does not take them over.

## Current state

The architecture already accepts the target shape:

- D5 specifies `pristine` as an optional clean policy, persistent per-image pool VMs, per-VM memory
  checkpoints, and a post-restore newer-session readiness barrier.
- D6 specifies provisioning as a Build whose `seal` epilogue produces an immutable standalone disk
  parent and records recipe lineage.
- D7 specifies provider-managed identity using host-side facts plus a per-instance nonce rather than a
  guest-asserted image ID.
- D8 defines the managed-machine conveyor and independent health state.
- D11-D13 define OS drift, quarantine/debug snapshots, maintenance, and canary Builds.
- D15-D16 define provider acquisition, static physical capacity, revert-pool and clone-per-build
  lifecycle modes, and capacity one per Agent.
- D19 defines controller-distributed agent/bootstrap packages; D20 names FakeMachineProvider and real-
  hypervisor test tiers. D22-D28 add shared coordination, Git, REST, React UI, RBAC, audit, and the
  stable Agent/provider-instance identity split.

Implementation evidence is intentionally smaller:

- `agent_hub.proto` contains the future `pool_nonce` hello field comment, but no provider protocol.
- Build scheduling, reconnect ownership, cancellation, and immutable selected-Agent provenance exist,
  but they are build-specific and do not yet use a general Agent lease/provider lifecycle.
- `docs/DEVELOPMENT.md` defines FakeMachineProvider as tier 3, but no implementation exists.
- `docs/ROADMAP.md` places provisioning Builds and the `ImageVersion` registry in Phase 3.
- There are no `MachineProvider`, `ProviderHost`, `ImageVersion`, pool, provider-operation, image-
  replica, Hyper-V, QEMU/KVM, or Tart runtime types in `src/` or `tests/` today.

No target state described below should be presented as implemented until its evidence lands.

## Target model

```text
Git desired configuration
  Provider / ProviderHost / Pool policy / ImageRecipe / aliases / retention
                           |
                           v
                    reconciliation
                           |
                           v
ProviderHost ---- MachineProvider ---- ProviderInstance ---- Agent ---- current session
     |                   |                    |
 host capacity      provider operations      +-- persistent revert-pool VM
     |                   |                    +-- ephemeral clone-per-build VM
     |                   |
     |                   +-- static physical/hand-managed Agent has no ProviderInstance
     |                   |
     +---- ImageStore replica <---- ImageVersion <---- ImageRecipe + parent lineage

Scheduling owns AgentLease/fence around provider prelude -> workload -> epilogue.
```

### Identities and records

| Record | Meaning | Authority |
|---|---|---|
| `MachineProvider` | One configured capacity source and driver instance | Git desired configuration plus applied projection |
| `ProviderHost` | A physical hypervisor node with driver endpoint, observed health, and host resources | Git identity/policy plus provider observation |
| `ProviderInstance` | Provider-native VM/allocation targeted by lifecycle operations; optional for an Agent | Provider registry and observation |
| `Agent` | Stable schedulable and AgentExplorer identity to which an optional ProviderInstance attaches | Agent registry, enrollment, and AgentHub |
| `AgentSession` | Current replaceable reverse-connected process session for an Agent | AgentHub runtime state |
| `Pool` | Desired and observed capacity for an image version/provider/host selection | Git policy plus runtime projection |
| `ProviderOperation` | Durable create/clone/start/stop/restore/destroy/seal/import/export action | Operational store and audit |
| `Image` | Stable logical scenario/image family, such as `win11-26100-clean` | Git desired configuration |
| `ImageRecipe` | Versioned declarative instructions that produce a new image version | Exact Git repository/revision/path/content digest |
| `ImageVersion` | Immutable sealed disk plus complete build/provider/OS lineage | Runtime image registry |
| `ImageReplica` | One verified physical copy/cache of an image artifact | Image store/provider observation |

Names and aliases are presentation and selection aids, never native target identity. A Build records
the immutable `agentId`, Agent snapshot, optional `providerInstanceId`, and `imageVersionId` actually used.

## Provider kinds and lifecycle modes

### Static enrolled capacity

The static provider projects enrolled physical Agents and hand-managed VM Agents into schedulable
capacity:

- It does not create, clone, destroy, snapshot, restore, or implicitly power them.
- Each Agent exposes one mutating slot and the capabilities/facts it actually reports.
- Supported clean policies are `none`, `clean-workdir`, and `reboot` when the Agent/Platform contract
  can perform and reconcile the reboot. Reboot is not fabricated as a provider capability.
- Membership, labels, enablement, maintenance intent, and policy come from Git. Connectivity, health,
  current work, and observations stay operational.
- Release returns the existing Agent to normal eligibility after agent-side cleanup/readiness. An
  infrastructure failure marks it bad/maintenance; there is no automatic replacement to create.

A hand-managed VM is static until it is explicitly adopted by a managed provider. Merely discovering
that it runs under a hypervisor does not grant provider control.

### Revert-pool managed capacity

Hyper-V and QEMU/KVM use persistent pool ProviderInstances per sealed `ImageVersion`:

1. Create a differencing disk from the immutable sealed parent.
2. Allocate a stable `agentId`, immutable `providerInstanceId`, MAC/host identity, unique hostname
   intent, and one-time provider nonce before first boot.
3. Boot once as that ProviderInstance, complete autologon/bootstrap/Agent reconciliation, and verify image and
   OS facts.
4. At idle, create that ProviderInstance's own warm memory checkpoint.
5. Before each pristine workload, restore that same checkpoint.
6. Wait for the exact Agent/ProviderInstance attachment to reconnect with a newer generation and become ready.
7. After workload completion, run the configured epilogue and restore again before releasing healthy
   capacity.

The memory checkpoint is instance-local runtime state. It is not part of `ImageVersion`, is not cloned
to another ProviderInstance, and may be rebuilt without changing image identity.

### Clone-per-build managed capacity

Tart, and later cloud providers, use an ephemeral lifecycle:

```text
reserve host capacity -> clone/create -> boot -> reconcile ready -> run -> collect -> destroy
```

There is no fake `pristine checkpoint` capability. Fresh immutable clone creation supplies pristine
state. A keep-on-failure request retains/quarantines the clone under a retention budget and causes the
pool/provider to create replacement capacity if policy permits.

## Provider capability catalogue

Provider capabilities use their own versioned namespace. Candidate v1 contracts are:

| Capability | Semantics |
|---|---|
| `provider.capacity.observe.v1` | Report host resource totals, reservations, pressure, storage, and health |
| `provider.instance.create.v1` | Create a managed ProviderInstance from an immutable image artifact |
| `provider.instance.clone.v1` | Cheaply derive an independent ProviderInstance/disk from an image version |
| `provider.instance.destroy.v1` | Reconcile then remove a ProviderInstance and its private runtime artifacts |
| `provider.power.v1` | Observe and request start, graceful stop, hard stop, or restart with distinct semantics |
| `provider.snapshot.create.v1` | Create a named instance-local checkpoint with declared disk/memory semantics |
| `provider.snapshot.rollback.v1` | Restore a known checkpoint on the exact ProviderInstance |
| `provider.console.v1` | Create a bounded, authorized console-access descriptor/ticket |
| `provider.image.import.v1` | Verify and materialize an image artifact into a provider-host cache |
| `provider.image.export.v1` | Export a sealed provider artifact for authoritative storage/replication |

A descriptor records provider/driver version, supported host and guest platforms, lifecycle mode,
checkpoint kind, cancellation/commit-point behavior, limits, and actual-state probe. A configured
provider advertises only capabilities that work on its current host. Capability support, Git
enablement, caller authorization, health, and capacity remain separate.

Initial driver matrix:

| Provider | Host | Guests | Lifecycle | Important capability truth |
|---|---|---|---|---|
| Static | Any supported host | Host itself/hand-managed VM | Persistent | Acquire/release only; no invented VM verbs |
| Hyper-V | Windows | Windows, Linux | Revert pool | Standard memory checkpoints, differencing VHDX, power, console |
| QEMU/KVM | Linux with KVM | Windows, Linux | Revert pool | qcow2 backing, `savevm`-class memory checkpoints, power, VNC; slower restore |
| Tart | Apple hardware/macOS | macOS | Clone per build | APFS clone, boot/power/console/destroy; no checkpoint rollback today |

## Provider host and capacity

A `ProviderHost` has stable identity and separate desired and observed state.

Desired configuration includes:

- provider/driver binding and allowed lifecycle/capabilities;
- admission caps for VM count, vCPU, memory, image-cache bytes, runtime/checkpoint disk bytes, and
  optional per-image caps;
- reserved headroom for the host/controller and maintenance;
- storage roots and secret references, never plaintext credentials;
- drain/maintenance windows, placement labels, and allowed image families.

Observed state includes:

- driver and hypervisor version, host OS, health, and last successful probe;
- total/free/committed CPU, physical memory, storage bytes/inodes where meaningful, VM count, and
  image/checkpoint/cache usage;
- active and reconstructable capacity reservations;
- native ProviderInstance inventory and unmatched/orphaned resources;
- pressure, degraded storage, and provider-specific issues.

The first policy is conservative: memory and disk reservations are hard limits; VM count is hard;
vCPU may use an explicit bounded overcommit ratio but never an implicit provider default. A ProviderInstance's
declared memory is part of scenario identity for checkpointed pools.

Scheduling owns durable capacity reservation and fairness. The provider supplies dimensions,
availability, and reconciliation. A create/clone operation receives an already-durable reservation
identity; after restart, reservations are reconstructed from provider actual state before new capacity
is admitted. The SQLite writer is never held across driver I/O.

## Pool growth, drain, and replacement

A pool is keyed by provider, compatible host selection, exact `ImageVersion`, lifecycle mode, and
resource shape. Git policy supplies `minWarm`, `maxSize`, optional target spare, placement constraints,
and maintenance/retention limits. Runtime demand may raise the observed target within those bounds.

Growth is background work:

1. Confirm image health and an available verified artifact replica.
2. Reserve host resources plus operation, ProviderInstance, and intended Agent identities.
3. Import/cache the artifact if needed.
4. Create the ProviderInstance and provider identity proof.
5. Boot and satisfy the readiness barrier.
6. For revert pools, create and verify the instance-local warm checkpoint.
7. Expose capacity only after every prior state is durable and reconciled.

Drain has explicit intent and reason:

- no new lease is assigned to a draining Agent/ProviderInstance or pool;
- active work finishes unless a separately authorized cancellation/force action exists;
- idle ProviderInstances run cleanup, then are destroyed or retained according to policy;
- controller restart resumes drain from desired intent plus provider actual state;
- reducing a pool target never silently cancels a Build;
- replacing an ImageVersion builds the new pool beside the old, shifts new admission, then drains the
  old pool. Capacity and disk budgets must include this temporary double footprint.

Pool ProviderInstances also drain for Agent upgrade, checkpoint re-baseline, host maintenance, repeated infra
failure, image deprecation, and provider-host pressure. An orphaned native VM is quarantined until
identity and ownership are reconciled; it is never adopted by name.

## Image, recipe, version, and lineage

### Image and recipe

An `Image` is a stable logical selector. Its Git-backed `ImageRecipe` declares:

- a parent pinned to an exact `ImageVersion`, never a moving alias during execution;
- target guest OS/architecture and compatible provider artifact formats;
- ordered provisioning steps, including explicit `manual` and future `expected-reboot` steps;
- payload digests/references and stable secret references;
- expected exact OS facts and other verification assertions;
- network profile, resource shape, interactive/session requirements, and clean-policy support;
- canary, retention, promotion, and allowed-provider policy.

The TeamCity Expert owns how provisioning steps execute as a Build. This role validates that the
recipe can produce a provider-compatible image and owns the sealing handoff.

### ImageVersion

An `ImageVersion` is immutable and contains or references:

- stable image/version IDs and lifecycle state: `BUILDING`, `SEALING`, `VERIFYING`, `AVAILABLE`,
  `DEGRADED`, `BAD`, `DEPRECATED`, or `DELETING`;
- recipe repository identity, commit SHA, path, schema version, and content digest;
- exact parent version and full transitive lineage;
- provisioning Build ID and immutable Build Configuration/parameter snapshot;
- source Agent, ProviderInstance, provider host, provider/driver versions, and seal operation;
- declared and observed OS/product/build/UBR/kernel/architecture facts;
- one or more artifact descriptors: format, byte size, cryptographic digest, sparse/allocation
  metadata where needed, and verified replicas;
- seal actor/time, verification/canary status, deprecation reason, and retention references.

Recipe digest and disk digest are different facts. VM disk formats may contain nondeterministic native
metadata; Vivarium promises immutable byte identity and traceable construction, not reproducible
bit-for-bit output unless a driver proves it.

### Sealing

Provisioning runs on a dedicated fresh Agent/ProviderInstance, never on serving pool capacity. The `seal` epilogue:

1. Requires the provisioning Build to reach its allowed seal condition and hold the same Agent
   lease/fence.
2. Quiesces guest work. An adopted enrolled VM first scrubs persistent Agent identity/token, writes
   the managed image identity, and later re-enrolls or retires the source Agent.
3. Deletes/merges checkpoints as required, shuts down cleanly, and produces a standalone immutable
   disk artifact rather than a diff-on-diff chain.
4. Computes/verifies artifact size and digest, registers an authoritative replica, and records complete
   lineage before exposing the version.
5. Verifies expected OS facts by booting a derived disposable ProviderInstance when the sealing path itself
   cannot supply trustworthy post-seal evidence.
6. Runs the required canary, then promotes to `AVAILABLE`; failures leave a visible non-schedulable
   version and preserve diagnostic evidence within retention bounds.

Changing bytes in place is forbidden. A repair is another provisioning Build and version.

## Image artifact storage and distribution

Git stores recipes, policy, aliases, and digests/references; it never stores multi-gigabyte VM disks.
SQLite stores registry metadata, lineage, operation state, and replica observations. Image bytes live
in an `ImageStore` abstraction rooted in explicitly configured controller/provider-host storage.

The first implementation may use one controller-owned local image directory as the authoritative
store and provider-local caches as replicas. The contract must already support:

- immutable keys by artifact digest plus format, never mutable filenames as identity;
- temporary/resumable copy, size and digest verification, atomic publication, and safe cleanup after
  interruption;
- sparse-file awareness so a logical disk size is not mistaken for transfer or physical allocation;
- per-provider formats (`vhdx`, `qcow2`, Tart-native/export form) without claiming automatic cross-
  format portability;
- explicit replica states: `COPYING`, `VERIFYING`, `AVAILABLE`, `STALE`, `CORRUPT`, `DELETING`;
- at least one policy-satisfying authoritative verified replica before an image version is available;
- provider-host cache eviction only when no active ProviderInstance, pool target, Build provenance, quarantine,
  or transfer references the artifact;
- bounded bandwidth/concurrency and disk-reservation accounting for copy plus temporary double space.

The existing small-object blob API is not automatically the image-transfer API. Reuse its digest and
GC principles, but do not force sparse multi-gigabyte disks through it until resumable transfer,
authorization, range/chunk integrity, and disk budgets are proven. The Results/Artifacts Expert owns
shared storage primitives; this role owns image replica and lineage correctness.

## Provider operations and failure taxonomy

A provider mutation is a durable `ProviderOperation`, even when it is a phase of a Build lease:

```text
REQUESTED -> RESERVED -> EXECUTING -> RECONCILING -> SUCCEEDED
                 |            |             +-----> FAILED
                 |            +--------------------> UNKNOWN
                 +---------------------------------> CANCELLED (only before commit point)
```

Each operation records provider/host/ProviderInstance/Agent/image identities, driver capability/version, owning
domain and lease reference, actor/correlation/idempotency identity, applied Git revision, phase,
deadlines, commit point, redacted input digest, actual-state observations, and terminal classification.
Scheduling owns the lease/fence fields and transition arbitration.

Stable failure classes include:

| Class | Meaning and default response |
|---|---|
| `INVALID_CONFIGURATION` / `UNSUPPORTED` | Deterministic; reject, no retry |
| `NOT_FOUND` / `IDENTITY_MISMATCH` | Reconcile registry/native inventory; quarantine ambiguous resources |
| `CAPACITY_EXHAUSTED` / `HOST_PRESSURE` | Wait, choose another host, or fail at capacity deadline |
| `ACCESS_DENIED` / `DRIVER_UNAVAILABLE` | Host/provider unhealthy until configuration/operator repair |
| `IMAGE_MISSING` / `IMAGE_CORRUPT` | Mark replica bad; use another verified replica or mark version unavailable |
| `CREATE_FAILED` / `CLONE_FAILED` / `CHECKPOINT_FAILED` | Reconcile and clean partial target before bounded retry |
| `RESTORE_FAILED` / `POWER_FAILED` | ProviderInstance and attached Agent bad/unknown; never release as ready |
| `TIMEOUT_AMBIGUOUS` | Side effect may have happened; actual-state reconciliation before any retry |
| `READINESS_TIMEOUT` / `IDENTITY_DRIFT` | Quarantine/recycle; do not schedule |
| `CLEANUP_FAILED` / `DESTROY_FAILED` | Capacity remains consumed/unknown until reconciled |
| `CONSOLE_UNAVAILABLE` | Does not mutate workload state; report capability-specific failure |
| `INTERNAL_DRIVER_ERROR` | High-signal bounded diagnostic, provider degraded, no blind retry |

Provider failures map into D9 `INFRA` when they prevent or invalidate a TeamCity Build. They do not
become `TEST` failures. A lost response is never proof that a mutating provider call did nothing.
Drivers use stable operation IDs where native APIs support them and always provide an actual-state
probe.

Cancellation is capability-specific: create/copy may be cancellable before publication; restore and
destroy often cross a non-cancellable commit point. A cancellation request never releases the lease
until the provider reconciles the real ProviderInstance state.

## Readiness and identity barrier

Provider-native `running` is necessary but insufficient. A newly created, cloned, started, or restored
ProviderInstance and its attached Agent become `READY` only when all of the following agree:

1. The provider reports the expected immutable `providerInstanceId`, intended `agentId`, image
   artifact/version, power state, and host-side identity facts.
2. Scheduling still owns the intended lease/fence and has not accepted a conflicting terminal path.
3. The exact expected agent proves the provider nonce/host-side identity through the Agent API.
4. Its accepted connection generation is newer than the generation recorded before start/restore.
5. Hello/heartbeat reports no ghost workload and no unacknowledged conflicting terminal result.
6. Agent/controller version and capability negotiation has completed; required applied Git policy is
   reconciled.
7. Clock/network post-resume checks and lightweight platform readiness checks pass.
8. Actual OS/image facts match the sealed version's declared facts and allowed drift policy.

The Agent API/SDK Expert owns the handshake and protocol. Scheduling owns the durable barrier. This
role supplies expected identities and provider observations. A stale TCP connection, same boot ID, or
native power state can never satisfy the barrier alone.

## Workload prelude, cleanup, and epilogue

The Build or AgentExplorer operation holds one Scheduling-owned Agent lease across provider prelude,
guest execution, collection, and epilogue.

- `pristine`: restore the pool ProviderInstance's own checkpoint, pass readiness, then dispatch guest work.
- `reboot`: persistent/static Agents use the Agent/Platform reboot contract and the same newer-
  generation readiness principle; this is not silently converted to provider power cycle.
- `clean-workdir`: agent-side cleanup proves the declared work root is ready.
- `none`: no reset, but current workload/agent state must still reconcile.
- `revert`: after terminal result/artifact durability, restore and pass readiness before healthy
  release.
- `keep`: managed ProviderInstances enter `QUARANTINE`; capacity is backfilled within policy. Physical Agents
  preserve workdir evidence but are not automatically held hostage—operator disablement is explicit.
- `seal`: provisioning-only path described above.
- `snapshot-corpse`: creates a separately retained debug checkpoint/artifact tied to the failed Build;
  it is not an `ImageVersion` and cannot be scheduled as one without an explicit promotion workflow.

Release never precedes epilogue reconciliation. Cleanup failure marks the Agent/ProviderInstance
bad/maintenance or destroys the instance after actual-state reconciliation. Artifact/result durability is checked before destructive
cleanup. The Results/Artifacts Expert owns result completeness; this role consumes the release gate.

## Drift, health, canaries, and maintenance

Health is orthogonal at provider host, image version, ProviderInstance, and Agent levels:

- Every ready handshake compares actual OS product/build/UBR/kernel/architecture and image identity
  with the sealed metadata. Mismatch is drift, not an editable label.
- A single ProviderInstance mismatch quarantines that instance and its Agent. Evidence that the sealed
  parent or multiple derived instances are wrong marks the `ImageVersion` bad and drains its pools.
- Canary Builds are ordinary TeamCity Builds with explicit image/version provenance. A cadence and
  required checks come from Git; failure changes health through reconciliation and audit, not by
  rewriting the recipe.
- Pool checkpoints are periodically re-baselined from the same sealed disk to limit diff/checkpoint
  growth and refresh the agent package. Re-baselining changes pool runtime state, not `ImageVersion`.
- Provider hosts report disk/memory pressure, driver health, orphaned VMs, and cache integrity.
  Maintenance drains capacity before compaction, checkpoint pruning, driver upgrades, or host reboot.
- A bad image, provider host, ProviderInstance, or Agent is removed from future admission without cancelling active work.
  Force evacuation is a separate permissioned and audited action.

Health recovery records evidence and actor/system cause. An operator override has an expiry and audit
record; durable policy changes go through Git.

## Git-backed desired configuration

The following are desired state from their first implementation:

- provider instances, provider-host identities, allowed drivers/capabilities, placement, and hard
  resource caps;
- static-pool membership/selectors, managed pool min/max/spare/resource policy, drain intent, and
  maintenance windows;
- image recipes, parent pins, network/resource shape, expected facts, secret references, canary,
  promotion aliases, deprecation, and retention policy;
- image-store locations/policy, replica count/placement, cache budgets, and transfer limits;
- console policy and provider action enablement.

Exact paths and commit workflow belong to Git/Versioning. Required semantics are:

1. Candidate commits validate all references, capability/platform combinations, resource bounds,
   cycles, recipe parent pins, and secret-reference syntax before application.
2. Application is atomic at a Git revision. Invalid/unavailable revisions leave the last-known-good
   provider projection active and visibly stale/degraded.
3. REST/UI/CLI configuration writes create or propose Git commits through the common mutation service
   with an expected base revision; no provider table has an independent settings mutation API.
4. Reconciliation converges provider actual state gradually and exposes desired/applied revision plus
   per-resource progress/error. Applying Git never holds a database transaction over driver I/O.
5. Removing a pool/image/provider from desired state starts a safe drain/deprecation workflow; it does
   not immediately destroy active ProviderInstances or retained lineage.
6. Rollback is a forward revert commit. Existing operations and Builds retain the revision/policy
   snapshot admitted with them.

Image disk bytes, ProviderInstance observations, operation state, health samples, capacities, credentials,
console tickets, leases, Builds, and audit events remain outside Git. Produced immutable
`ImageVersion` records are operational outputs of Git-pinned recipes; promotion aliases and retention
intent are Git-backed.

## REST surface

REST is designed before UI/provider implementation. The Vivarium REST Expert owns final naming and
HTTP conventions; this stream requires public projections equivalent to:

```text
GET  /api/v1/providers
GET  /api/v1/provider-hosts
GET  /api/v1/provider-hosts/{providerHostId}
GET  /api/v1/provider-instances
GET  /api/v1/provider-instances/{providerInstanceId}
GET  /api/v1/pools
GET  /api/v1/images
GET  /api/v1/images/{imageId}
GET  /api/v1/image-versions/{imageVersionId}
GET  /api/v1/image-versions/{imageVersionId}/replicas
POST /api/v1/provider-instances/{providerInstanceId}/operations
GET  /api/v1/operations/{operationId}
PUT  /api/v1/operations/{operationId}/cancellation
```

Runtime actions such as pool reconcile, create, restore, power, seal, replicate, drain-now, destroy,
canary-now, or console-access creation return `202 Accepted` operation resources, require idempotency
keys, and expose commit-point/cancelability plus durable progress. Configuration changes use the Git
mutation/change resource and return the candidate/applied revision; `PATCH /providers/{id}` must not
silently mutate SQLite.

Resources expose stable IDs, capability/availability reasons, desired/applied Git revisions, health,
observed-at timestamps, capacity/reservations, image lineage/digests, operation correlation, and stale/
partial states. Large image bytes use an authenticated bounded transfer surface, not base64 JSON.
Console access uses a short-lived separately authorized ticket/descriptor; credentials are neither
replayable idempotency bodies nor logged resource fields.

REST errors distinguish unsupported capability, invalid desired configuration, policy disabled,
forbidden, busy/leased, insufficient capacity, unhealthy, ambiguous provider state, stale Git base,
and missing/corrupt image. SSE may project progress; durable GET remains authoritative.

## Audit and logging

Emit one structured high-signal audit event for:

- provider/host/pool/image desired revision proposed, validated, applied, rejected, or rolled back;
- ProviderInstance/host capacity reservation, lifecycle transition, quarantine, health change, and release;
- create/clone/start/stop/restore/checkpoint/destroy/seal/import/export/replicate requests and terminal
  outcomes;
- image version registration, verification, promotion/deprecation intent, drift, canary result, and
  deletion eligibility;
- console ticket creation/use/revocation and every force/emergency action;
- stale/duplicate/ambiguous operation response, identity mismatch, and cleanup/readiness failure.

Events include actor/system identity, correlation and idempotency-key hash, provider/host/
ProviderInstance/Agent/image/operation IDs, owning Build/AgentExplorer resource, lease/fence supplied by Scheduling, from/to
state, deadlines, driver/capability version, Git revision, bounded reason, and outcome.

Do not log provider credentials, enrollment/agent tokens, console secrets, secret recipe values, full
native command lines, image bytes, or repeated capacity samples. Metrics carry repeated healthy
capacity/latency/cache state. Logs record transitions and anomalies. Native stderr is bounded,
redacted, and stored as diagnostic evidence only when policy permits.

## Platform and driver boundaries

The provider common layer defines semantic operations, state, errors, and evidence. The Platform
Expert owns native adapters and support claims.

### Windows / Hyper-V

- Windows host; Windows and Linux guests.
- Standard checkpoints are mandatory for warm memory restore; Production and automatic checkpoints
  cannot silently substitute.
- Static guest memory, static MAC, disabled automatic checkpoints, differencing VHDX from a sealed
  standalone parent, and per-ProviderInstance checkpoint identity are validated.
- Host-side VM ID/MAC/switch facts plus injected KVP nonce support D7 identity.
- Hyper-V TimeSync remains enabled; post-restore clock/network/session checks still run.
- Basic console mode is the safe default for observing the existing interactive session; enhanced/RDP
  mode can create a different session and must be labeled.

### Linux / QEMU-KVM

- Linux KVM host; Windows and Linux guests.
- qcow2 backing artifacts are immutable; each ProviderInstance owns its writable overlay and memory snapshot.
- `savevm`-style memory restore is semantically supported only with measured latency and disk budget;
  it is not described as Hyper-V-speed.
- QMP/native calls need actual-state probes; fw_cfg supplies the provider nonce; VNC supplies console.
- KVM/device/bridge permissions and durable flush behavior are tested on a real host.

### macOS / Tart

- Apple hardware and macOS host/guest only, respecting Apple's virtualization/licensing boundary.
- Tart is driven through its external CLI; its license is not linked into Vivarium.
- APFS clone plus boot implements clone-per-build. No checkpoint/rollback capability is advertised
  until Tart supplies and Vivarium proves the required semantics.
- Console/display, clone deletion, TCC, signing, and host storage pressure remain explicit platform
  concerns.

Common code never branches on localized CLI output. Adapters normalize native state and error codes.
No provider uses SSH, WinRM, PowerShell Direct, or guest-exec as a substitute for the reverse agent.

## Design invariants

- One stable `Agent` identifies the guest-side side-effect target; credential generations, sessions,
  and display names may change. Provider lifecycle operations additionally target an immutable
  `ProviderInstance` attached to that Agent.
- Static physical capacity remains valid without any provider lifecycle capability.
- Provider and Agent capabilities remain separate and independently versioned.
- Sealed disk artifacts and `ImageVersion` lineage are immutable; pool checkpoints are disposable
  instance runtime.
- Managed capacity is never schedulable before identity, agent, policy, readiness, and drift agree.
- Provider actual state is reconciled after timeout/restart; unsafe mutations are never blindly
  retried.
- Pool growth cannot exceed host reservations; drain cannot silently terminate work.
- Prelude, workload, and epilogue remain inside Scheduling's one lease/fence.
- Cleanup or readiness failure consumes/quarantines capacity rather than creating a false idle state.
- Recipes and durable policy are Git-backed; produced disks and observations are not Git content.
- REST and audit exist from the first provider slice; the UI is never the only management path.
- Image bytes, console credentials, and high-volume native output remain outside ordinary JSON/logs.

## Non-goals

- A general infrastructure-as-code platform or arbitrary cloud resource manager.
- Controller-to-guest exec, file copy, SSH, WinRM, or hypervisor guest tools.
- Multi-controller/HA provider coordination.
- Live migration, cross-provider checkpoint portability, or automatic VHDX/qcow2/Tart conversion.
- Oversubscription/autoscaling heuristics beyond explicit conservative Git policy in the first slice.
- Treating a memory checkpoint as a portable sealed image artifact.
- Building a separate image-builder product; provisioning remains a TeamCity Build with a seal
  epilogue.
- Hiding manual recipe steps, macOS TCC, Windows licensing, or interactive-session constraints.
- Silently adopting pre-existing native VMs, disks, or snapshots by name.
- Placing disk images, credentials, live capacity, health, or operation state in Git.
- Owning shared leases/fencing, AgentHub, OS adapters, AgentExplorer fleet UI, or public REST conventions.

## Required evidence

### Semantic and fake-provider evidence

- Deterministic tests with virtual time for all provider operation states, deadlines, cancellation
  commit points, duplicate requests, ambiguous completion, reconciliation, and controller restart.
- Pool min/max/grow/drain/replacement tests with host CPU/RAM/disk/VM caps and temporary double-space
  accounting.
- Static physical capacity tests proving no fabricated provider verbs and capacity one.
- Readiness tests rejecting stale sessions, wrong nonce/MAC/machine/image, old agent version, ghost
  workload, drifted OS facts, and unreconciled policy.
- Prelude/epilogue tests proving no unleased schedulable gap and no release after cleanup failure.
- Seal tests for dedicated provision machine, identity scrub on adoption, standalone parent, digest,
  lineage, replica verification, canary, and immutable version behavior.
- Artifact transfer tests for interruption/resume, corrupt digest, sparse allocation, insufficient
  temporary disk, atomic publication, and reference-safe GC.
- Git validation/reconciliation, REST idempotency/ETag/async action, authorization, audit correlation,
  secret redaction, and bounded-log tests.

### Real-provider evidence

- Hyper-V: create five concurrent pool machines within caps; prove unique identity; Standard own-memory
  checkpoint; repeated restore; newer agent generation; expected idle/image facts; bounded latency;
  cleanup, drain, restart recovery, full-disk and failed-restore behavior.
- QEMU/KVM: real KVM create/overlay/savevm/restore/QMP reconciliation/VNC smoke with measured restore
  and storage costs before `revert-pool` is advertised.
- Tart: Apple-hardware clone/boot/identity/readiness/destroy/keep/console smoke before clone-per-build
  support is advertised; checkpoint remains absent.
- Previous supported agent package in a restored checkpoint upgrades/reconciles without guest reboot
  before the pool becomes ready.
- Drift/canary evidence proves a bad machine/version/host is removed from future admission without
  rewriting in-flight Build history.

## Collaboration and approval flow

1. Machine Providers and Images Expert defines provider/image semantics, state, commit point, and
   evidence.
2. Scheduling/Coordination supplies the shared lease/fence, reservation ordering, deadlines, and
   durable owner handoff.
3. Agent API/SDK supplies managed identity, version/capability reconciliation, and post-operation
   session barrier.
4. Platform supplies native Hyper-V/QEMU/Tart adapters and verified host/guest support matrix.
5. TeamCity approves clean policies, provisioning Build, seal eligibility, and Build failure mapping.
6. Git/Versioning and Persistence/Migrations approve desired schema, applied projection, runtime
   records, atomicity, and migrations.
7. REST, User Roles, Security, UI, Results/Artifacts, and Logs approve their public, permission,
   storage, console, retention, and audit boundaries.
8. Reconciliation Lead reviews every restart, ambiguous native result, drift, drain, partial seal, and
   deletion/GC path; Test Steward reviews evidence and tier placement.
9. Docs Expert updates architecture, roadmap, walkthrough, design/role indexes, and current-state
   claims in the same accepted change.

## Open questions

1. What is the first authoritative image-store topology: controller-local only, provider-host-local
   with required replication, or controller authoritative plus provider caches?
2. Which resumable transfer/chunk-integrity scheme and sparse-file representation are required before
   multi-host image distribution ships?
3. Are image artifacts scoped to one provider/format, or may one `ImageVersion` contain independently
   built VHDX, qcow2, and Tart artifacts under shared recipe lineage?
4. Which exact host capacity dimensions and vCPU overcommit default are accepted for the first
   Hyper-V provider?
5. How does demand convert into pool target size without starving maintenance or exceeding temporary
   image-replacement disk budgets?
6. Which provider operations are cancellable, where is each irreversible commit point, and what native
   evidence reconciles a timeout?
7. What verification Build/canary is mandatory before a sealed version becomes `AVAILABLE`, and how
   many machine failures mark the whole version bad?
8. What is the retention/GC rule for deprecated image versions referenced by retained Builds,
   quarantined machines, corpse snapshots, and external exports?
9. How are short-lived console tickets transported securely for local Hyper-V, remote QEMU VNC, and
   Tart without logging credentials or creating a general inbound guest channel?
10. What exact Hyper-V host versions, QEMU/libvirt/QMP stack, and Tart versions form the first supported
    provider matrix?
11. Does adopting an enrolled VM require a separate source-machine retirement workflow, and how is
    failed identity scrub proven before any derived pool boots?
12. Which provider/image health overrides are temporary operational actions versus durable Git policy,
    and what expiry is mandatory for an override?
