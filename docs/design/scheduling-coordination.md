# Scheduling and Coordination

> Status: **Accepted**
> Implementation: **Partial**
> Maintainer role: [Scheduling/Coordination Expert](../roles/scheduling-coordination-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D4-D9, D13-D16, D18, D22-D28

## Purpose

Vivarium has two independent products using one physical agent:

- **TeamCity** schedules Projects, Build Configurations, Builds, and ordered steps.
- **AgentExplorer** observes and manages the fleet outside any Build.

They must not share domain history or pretend that an operator command is a Build. They must share one
coordination boundary, because a Build, a cleanup command, an agent upgrade, and a VM rollback cannot
safely mutate the same machine at once.

This document defines that boundary: durable queueing, per-Agent leases, delivery handshakes,
heartbeats, reconnect/restart adoption, cancellation, provider ordering, deadlines, idempotency,
fairness, Git-controlled policy, REST operations, and audit correlation.

## Scope and non-goals

In scope:

- TeamCity Build Queue selection, assignment, acknowledgement, result, cancellation, and recovery.
- AgentExplorer read-versus-mutate classification and mutation coordination.
- Shared occupancy of a physical box or VM.
- Provider and maintenance actions that make an agent temporarily unavailable.
- Safety under duplicate, late, reordered, missing, and contradictory messages.
- Durable policy provenance and externally visible async-operation behavior.

Not in scope:

- The contents of Build Configurations or AgentExplorer command/file/process APIs.
- Agent installation and protocol field design beyond the coordination requirements placed on them.
- Provider-specific CLI/API implementation.
- User-role definitions, REST representation style, Git workflow details, or log retention policy.
- Multi-controller consensus or high availability. The target remains one controller and one serialized
  SQLite writer.
- Multiple independent mutating slots inside one agent. A physical machine and a VM currently have
  mutation capacity one.
- Exactly-once network delivery. Vivarium uses durable intent, at-least-once delivery, idempotency,
  and fencing.

## Current implementation baseline

The Phase 1 build path already provides substantial TeamCity-style coordination:

| Area | Implemented baseline | Evidence |
|---|---|---|
| Queue | Durable global FIFO; scheduler skips incompatible blocked entries and selects the first runnable item | `BuildQueueStore`, `BuildScheduler`, `BuildSchedulerTests` |
| Queue wait | Persisted absolute deadline; expiry wins atomically at the boundary and produces `INFRASTRUCTURE_FAILED` | `BuildQueueTimeoutMonitor`, `BuildQueueTimeoutTests` |
| Eligibility | Connected, reconciled, authorized, enabled, idle, and compatible axes gate dispatch | `AgentRegistry`, `BuildSchedulerTests` |
| Dispatch | Durable owner/session and selected-agent provenance are recorded before send; exact-session acknowledgement completes dispatch | `BuildQueueStore`, `BuildTracker` |
| Reconnect | Lost sessions arm a durable reconnect grace; a matching re-hello can adopt; expiry is infrastructure failure | `BuildStore`, `BuildTracker`, `BuildLeaseTests` |
| Cancellation | Running cancellation is durable and redelivered; matrix cancellation is serialized and first reason wins | `BuildTracker`, `MatrixBuildCancellationService`, cancellation tests |
| Results | The first valid terminal result is durable; identical retries and lease-expired late results are acknowledged without rewriting history | `BuildStore`, `BuildTracker`, result/reconnect tests |
| Persistence | Queue, assignment, cancellation intent, ownership, provenance, and result live in SQLite; in-memory waiters are projections | `VivariumDatabase`, build stores |

The following are target work, not implemented capabilities:

- A generic per-Agent lease shared by TeamCity, AgentExplorer, providers, and maintenance.
- Explicit credential and connection generations, Agent-scoped fences, and observation epochs; current
  build ownership is already Agent/session-oriented.
- Caller/Project/Build Configuration authorization to target pools/trust classes before compatibility,
  with durable authorization-policy provenance.
- AgentExplorer operation records and agent operation envelopes.
- Machine providers, actual rollback/power/clone reconciliation, and host-capacity reservations.
- Immutable Agent/provider capability, trust-class, and actual image/checkpoint
  execution snapshots.
- General workload reporting in `Hello`; today recovery is expressed as `running_build_id`.
- The explicit Build `RELEASING` phase and epilogue-derived final outcome; Phase 1 currently accepts the
  agent result as the Build's terminal result.
- Agent-side durable journaling for operations beyond the current build terminal-result retry.
- Git-controlled fleet/scheduling policy reconciliation and policy provenance on every operation.
- A public REST management surface and generic async `Operation` resource.
- The minimal SQLite audit/outbox required below; current logs and domain rows are not yet a complete
  action journal.

This distinction must remain visible in plans and UI. Design prose is not evidence that a target path
runs.

## Vocabulary and boundaries

### Identities

- `agent_id` identifies the stable physical or virtual Agent on which work and guest-side effects
  happen. It is the unit of exclusive mutation, lease uniqueness, fencing, observation epochs, and
  execution provenance.
- `credential_generation` identifies the current revocable authentication generation for that Agent.
- `session_id` identifies one reverse-connected AgentHub session and changes on every reconnect.
- `connection_generation` is a controller-side monotonic generation for accepted sessions of an
  Agent. It survives the fact that a restored VM resumes the same OS boot.
- `provider_instance_id` identifies an optional provider-native VM/allocation for lifecycle operations;
  physical and hand-managed Agents normally have none.
- `lease_id` identifies one exclusive reservation of an Agent.
- `fence` is a monotonically increasing integer scoped to `agent_id`. Every new exclusive lease gets
  a value greater than all previous leases for that Agent.
- `correlation_id` follows one request through REST/UI/CLI, Git reconciliation, scheduling, agent or
  provider work, domain result, and audit logs.

Names are presentation, not identity. Provider operations must never choose a target from an Agent
display name. Fresh enrollment creates an Agent; reclaiming an existing `agent_id` requires controller-
issued proof. Provider-managed attachment additionally requires provider identity proof. Re-enrollment
replaces the credential generation transactionally, and reconnect replaces only the current session;
active leases prevent unsafe replacement unless a reconciliation/force workflow explicitly handles it.

### Work classes

| Class | Domain record | Exclusive Agent lease | Examples |
|---|---|---:|---|
| TeamCity Build | `Build` | Yes | checkout/build/test sequence, provisioning Build |
| AgentExplorer read probe | request plus optional inventory snapshot | No | OS facts, process list, TCP/UDP endpoints |
| AgentExplorer mutation | `AgentExplorerOperation` | Yes | remote command, cleanup, process stop, software mutation |
| Provider lifecycle | `ProviderOperation` | Yes for an attached Agent; host/image reservation as needed before one exists | restore checkpoint, start, stop, destroy, clone |
| Maintenance | `MaintenanceOperation` | Yes | agent upgrade, re-baseline, repair, planned reboot |

An arbitrary command is always a mutation for coordination purposes even when its author believes it
is read-only. File browsing and inventory probes are reads only while their contract forbids writes.

Read probes may run beside a Build, subject to per-Agent/session concurrency and rate limits. A platform or
collector that cannot guarantee safe concurrent observation must advertise that fact; the controller
then routes that probe through an exclusive AgentExplorer operation.

Every read probe is registered before delivery with `probe_id`, stable `agent_id`, credential generation,
session/connection generation, observation epoch, deadline, and correlation ID. Its response is accepted
only while all of those still identify the current observation boundary. Restore, hard power, destroy,
credential replacement, and any provider action that invalidates observations stop new probes, drain or
cancel registered probes, and durably increment the Agent's observation epoch before the side effect.
Late responses from an older epoch are discarded rather than published as current inventory.

## Shared per-Agent lease

Domain records remain separate. A small coordination record links one domain owner to one Agent:

```text
AgentLease
  agent_id                -- unique while nonterminal
  lease_id                -- globally unique
  fence                   -- monotonic per Agent
  owner_kind              -- BUILD | AGENT_EXPLORER | PROVIDER | MAINTENANCE
  owner_id                -- domain record ID
  credential_generation?  -- expected credential generation, where applicable
  session_id?             -- exact session currently allowed to execute
  connection_generation?  -- controller-side accepted-session generation
  provider_instance_id?   -- exact provider target, where applicable
  state                   -- RESERVED | DISPATCHING | ACTIVE | CANCEL_REQUESTED |
                             RELEASING | RELEASED | EXPIRED
  acquired_at
  assignment_deadline?
  reconnect_deadline?
  execution_deadline?
  cancellation_deadline?
  release_deadline?
  correlation_id
  policy_revision
```

This is a conceptual contract, not a mandatory single table layout. Its required properties are:

1. SQLite enforces at most one nonterminal lease for a stable `agent_id`; neither session nor
   ProviderInstance identity can substitute for that uniqueness key.
2. Allocating a lease and incrementing its Agent-scoped fence is one serialized transaction.
3. The lease points to an existing domain record. Domain-specific state and results never move into a
   generic Build-shaped table.
4. A send occurs only after `RESERVED`/`DISPATCHING` is durable.
5. Completion of the domain item moves the lease to `RELEASING`; it becomes `RELEASED` only after the
   agent and provider release barrier is satisfied.
6. Every acknowledgement, status, result, or provider observation updates state only when
   `agent_id`, `lease_id`, fence, owner, credential/connection generations, and allowed session match.
7. Provider work that is a Build's clean prelude or epilogue executes under that Build's existing
   lease/fence. A standalone provider action owns its own lease. When an ownership handoff is required,
   it is one durable compare-and-swap with no unleased schedulable gap.

The controller must not hold the SQLite writer across agent or provider I/O. It performs:

```text
commit intent and fence
        ↓
perform at-least-once external I/O
        ↓
commit observation under the expected fence
```

If the final commit loses a race to cancellation, expiry, or a newer fence, the response is late
evidence. It cannot revive or overwrite the current owner.

### Agent enforcement

The target agent workload envelope carries at least:

```text
work_kind, work_id, agent_id, credential_generation, session_id,
connection_generation, lease_id, fence, execution_deadline
```

The Agent API/SDK Expert owns the concrete backward-compatible protocol. The behavioral requirement is:

- The agent persists the highest accepted fence for its stable `agent_id`.
- A lower fence is rejected.
- A duplicate assignment with the same kind, work ID, lease, fence, and immutable payload hash is
  acknowledged without starting a second execution.
- The same lease/fence with different payload is a protocol conflict and must not execute.
- One mutation runs at a time. Read probes use a separate bounded path.
- The active workload and unacknowledged terminal result survive agent restart where the operation can
  survive it; otherwise the restart produces an explicit infrastructure/unknown outcome.

A stale restored snapshot can contain an older persisted fence. Controller-side connection generation
and readiness barriers remain mandatory; guest storage alone cannot fence memory rollback.

## Admission authorization and execution provenance

Compatibility is not authorization. Before matching requirements against any agent, the controller
evaluates whether the caller, Project, and Build Configuration may target the candidate pool and its
trust class. The input is the authenticated principal plus the effective user-role and Git-controlled
policy revisions. Unauthorized pools and machines are removed before compatibility evaluation, queue
admission, and “no compatible agents” diagnostics so those diagnostics do not become a fleet-discovery
side channel.

Admission persists:

- caller/principal identity and authentication method reference;
- Project and Build Configuration stable IDs and immutable definition revision;
- authorization policy revision and Git commit/content hash;
- permitted and selected pool/trust class;
- the decision reason/rule identifier without embedding secrets.

Once an actual Agent is selected and any clean prelude completes, the assignment or operation
captures an immutable execution-target snapshot:

```text
agent_id, Agent kind, pool and trust class,
credential generation and assigned session/connection generation,
reported/custom parameter snapshots,
Agent capability IDs/versions,
provider identity, optional provider_instance_id, lifecycle mode and provider capability snapshot,
image ID/version, recipe/content hash and checkpoint/generation identity when applicable,
clean policy and clean-completion observation,
selection, authorization, configuration and Git policy revisions
```

Physical Agents legitimately have no image/checkpoint fields. Provider-backed Agents must record the
actual image version and restore/clone provenance reported by the provider, not only the requested
image selector. A later capability, label, provider, image, credential/session, or policy change never
rewrites historical execution provenance.

## TeamCity Build lifecycle

The target visible domain states are TeamCity-shaped with an explicit safety phase: `QUEUED`,
`RUNNING`, `CANCEL_REQUESTED`, `RELEASING`, and `FINISHED`. Assignment phase is separately durable so
the UI may explain a running Build as starting, accepted, or reconnecting without weakening ownership.
`RELEASING` means agent execution evidence is durable but the clean epilogue and machine release have
not completed. The current Phase 1 implementation finishes the Build when it accepts the agent result;
the explicit releasing/finalization split is target work.

### Assignment sequence

1. Admission evaluates caller/Project/Build Configuration authorization to pools and trust classes,
   removes unauthorized targets, and only then evaluates compatibility.
2. Submission atomically persists every matrix child, its immutable resolved definition, authorized
   target scope, authorization and Git policy/config revisions, queue row, queue deadline, and
   requirements.
3. The scheduler scans in stable order for the first item that has a compatible eligible agent only
   among its authorized pools/trust classes and has all required capacity.
4. The scheduler/provider selects an actual Agent. In one serialized transaction the coordinator:
   - verifies the queue deadline is strictly in the future;
   - rechecks current authorization without expanding the admitted target scope; a revocation blocks
     dispatch and records both admitted and current policy revisions;
   - verifies the Agent and optional ProviderInstance are still eligible;
   - allocates the Agent lease and next fence;
   - starts the selected execution-target snapshot with credential/session and Agent/provider capabilities,
     currently observed image/checkpoint facts, trust class, and policy/config provenance;
   - stores the exact assignment and owner session;
   - marks the queue claim and Build ownership durable.
5. If the Build requires `pristine`, `reboot`, or another clean prelude, the provider/agent performs it
   as a child phase under the Build lease. The controller waits for the required newer reconciled
   session and then durably rebinds the assignment owner session; AgentExplorer cannot enter between clean
   and execution. Facts only knowable after the clean operation finalize the immutable target snapshot
   before assignment delivery.
6. After readiness and commit, the controller sends the assignment to that exact current session.
7. `AssignmentAccepted` must match Agent, credential/session generations, Build, lease, and fence. It marks
   assignment accepted and removes the dispatch claim from the active queue projection.
8. Status and logs are hints scoped by that ownership. They do not assign ownership and are ignored
   when stale.
9. The first matching agent terminal result and artifact manifest are persisted atomically as immutable
   execution evidence, the Build becomes `RELEASING`, and only then does the controller acknowledge the
   result.
10. The clean/keep/seal/revert epilogue runs under the same Agent lease/fence. No other mutation may
    enter while test evidence is being preserved or its ProviderInstance is being restored.
11. If the epilogue succeeds, one transaction records the final Build outcome, release provenance, and
    releases the lease/readiness gate. If rollback or cleanup fails, it records final `INFRA`, preserves
    the original execution outcome, tests, logs, artifacts, and dumps, marks the machine unavailable or
    quarantined, and then closes the lease without exposing capacity.

An agent may finish so quickly that its terminal result arrives before the explicit assignment
acknowledgement. A valid terminal result is also proof of acceptance and completes the assignment
handshake idempotently, as the current implementation already permits. It is not proof that the Build
epilogue or machine release completed.

### Queue behavior and fairness

The initial deterministic ordering is:

```text
priority band descending, enqueued_at ascending, stable work ID ascending
```

The scheduler selects the first **runnable** item, not simply the head, so an item requiring a busy or
missing platform does not block unrelated agents. Selection must consider:

- caller/Project/Build Configuration authorization to the pool/trust class, filtered before all
  compatibility diagnostics;
- static compatibility;
- connected/reconciled/authorized/enabled/healthy eligibility;
- current Agent lease;
- Build Configuration and Project concurrency caps;
- provider pool and host resource reservations;
- maintenance/drain state;
- queue and policy deadlines.

Within the same priority band, older runnable work wins. Priority overrides require an explicit
permission and audit event. The initial design does not preempt an active Build. Starvation controls
are per-configuration/project caps, bounded operator-work bursts, persisted queue deadlines, and a
background-only maintenance band. Priority aging may be added only with a numbered policy and
deterministic tests; it is not implicit.

AgentExplorer mutations and TeamCity Builds may keep domain-specific queues, but their lease requests
enter the same per-Agent arbitration order. The default bands are:

1. Explicit emergency action, authorized and audited.
2. Normal TeamCity and AgentExplorer mutations, ordered by configured priority and age.
3. Maintenance and pool housekeeping.

An emergency action still does not silently preempt active work. It requests cancellation/drain and
waits for release unless an explicit force operation is authorized and audited.

### Capacity

- Each physical host or VM Agent supplies one mutating slot.
- Read probes use a separate small concurrency budget, are registered against the current Agent
  observation epoch, and never imply mutating capacity.
- A provider host may impose CPU, RAM, disk, and VM-count reservations in addition to the machine
  lease. Creating/cloning consumes host capacity before waiting for the new agent.
- A Build Configuration cap constrains its running plus starting Builds across all machines.
- Capacity reservation and release are durable or reconstructable from provider facts after restart.
- A disconnected agent with an unexpired active lease consumes its slot during reconnect grace.

## Cancellation

Cancellation is a durable desired state, not a best-effort packet.

### Build cancellation

- A queued Build is made terminal `CANCELLED` and its queue row removed in one transaction; it never
  reaches an agent.
- A running Build becomes `CANCEL_REQUESTED`; the first non-empty reason wins.
- After commit, the controller sends cancellation to the owning session and resends on reconnect.
- The agent terminates the whole process tree, runs only the step-finalization policy explicitly
  allowed on cancellation, and reports an execution-terminal `CANCELLED` result; the Build still
  passes through `RELEASING`.
- Cancellation requested during `RELEASING` does not skip the safety epilogue or erase the stored
  execution result. A separately authorized force action is required to abandon cleanup.
- Disabling an agent is never cancellation.
- Cancelling a matrix parent serializes child transitions: queued children terminate, running children
  record cancellation intent, releasing children continue their safety epilogue, and finished children
  remain immutable.

### Operation cancellation

AgentExplorer, provider, and maintenance operations declare one of:

- `cancellable`: the agent/provider can stop and report a safe terminal state;
- `cancellable-before-commit-point`: cancellation is honored only before a named irreversible phase;
- `not-cancellable`: the request is recorded but the operation must finish reconciliation.

REST or UI cancellation means “request cancellation”, not “the side effect has already stopped.” The
operation retains its lease until its final state and actual Agent/provider state are reconciled. A timeout
during cancellation does not release the Agent as idle; it places it in `UNKNOWN`/maintenance and
requires provider or operator reconciliation.

## Heartbeats, reconnects, and controller restart

Transport keepalives detect a broken stream; application heartbeats renew observable liveness and
carry a compact workload assertion. Normal heartbeats do not extend execution or queue deadlines.

The target `Hello`/heartbeat workload report generalizes `running_build_id` to:

```text
none
or { agent_id, credential_generation, work_kind, work_id, lease_id, fence, phase }
```

The Agent API must add this backward-compatibly because a reverted pool checkpoint can contain an
older agent until its post-revert upgrade.

### Reconnect decision table

| Durable controller state | Agent report | Action |
|---|---|---|
| Same active owner/Agent, same lease/fence | Same workload | Atomically adopt the newer session, clear reconnect deadline, resend pending cancel if any |
| Assignment prepared, not accepted | No workload | Adopt the session and resend the same immutable assignment under the same lease/fence if its acknowledgement deadline permits |
| Active accepted workload | No workload | Keep ownership during grace; then fail/quarantine according to work kind rather than assigning new work |
| No active lease | Old/lower-fence workload | Reject as stale, issue fenced stop when safe, keep Agent reconciling until empty |
| Active lease for different owner | Conflicting workload | Mark Agent unavailable/unknown, emit a high-signal audit event, and require reconciliation; never schedule beside it |
| Execution result stored / Build releasing or final | Matching duplicate terminal | Acknowledge without rewriting execution evidence or final outcome |

Only the current `session_id` may send state for an adopted lease. A newer accepted session
atomically supersedes the old stream; messages already buffered on the old stream are stale.

On controller startup:

1. Load every nonterminal domain record, Agent lease, cancellation intent, deadline, and provider
   operation from SQLite.
2. Mark their Agents reconciling in the in-memory projection.
3. Arm persisted startup reconnect grace where required; never derive a fresh unbounded grace on each
   restart.
4. Reconcile provider actual state and wait for agent `Hello` reports.
5. Adopt, redeliver, expire, quarantine, or complete under the persisted fence.
6. Open Agents to new scheduling only after the durable and observed states agree.

## Duplicate and late messages

| Message | Duplicate/late rule |
|---|---|
| Assignment | Same immutable assignment and fence is acknowledged, not rerun; different content is a conflict |
| Assignment acceptance | Idempotent only for the exact owner/session/lease/fence; stale acceptance is ignored and audited at a rate-limited level |
| Status/log | Accepted only from current ownership; late chunks never change durable status |
| Agent terminal result | First valid execution result wins; byte-equivalent semantic retry is acknowledged; a different later result is retained as conflict evidence but never replaces execution evidence or epilogue-derived final outcome |
| Read-probe result | Accepted only for the registered Agent, current credential/session, observation epoch, and deadline; otherwise discarded as stale |
| Cancellation | Repeated requests return the same effective intent; first reason wins; delivery is repeated until terminal/reconciled |
| Heartbeat/Hello | Newer session may adopt only through the durable comparison-and-swap; an old session cannot renew a lease |
| Provider command | Reuse operation id and fence; driver reconciliation must tolerate the command having completed before the response was lost |

A late terminal result after reconnect-lease expiry may be acknowledged so the agent can clear its
journal, but it cannot replace the already durable infrastructure failure or release a newer lease.

## Deadline model

Each deadline answers a different question and is stored as an absolute controller timestamp:

| Deadline | Starts | Expiry consequence |
|---|---|---|
| Queue wait | Admission | Terminal infrastructure queue-timeout; never assigned |
| Capacity/provision | Provider request | Fail or retry provider acquisition under bounded policy |
| Assignment acknowledgement | Durable dispatch preparation | Reconcile/resend or fail/requeue only when side effects are proven absent |
| Reconnect grace | Current session loss | Matching adoption or domain-specific infrastructure/unknown terminal path |
| Execution | Accepted workload | Durable cancellation intent, then cancellation grace |
| Cancellation grace | Cancel delivery/acknowledgement | Escalate to platform kill/provider recovery; Agent stays unavailable |
| Provider action | Provider intent | Reconcile actual provider state; never assume failure means no side effect |
| Post-restore readiness | Provider reports restore/start complete | Quarantine/recycle because stale or missing agent is not ready |
| Release/cleanup | Durable agent terminal result and entry to `RELEASING` | Finalize Build as `INFRA`, preserve execution evidence, and mark Agent bad/maintenance if clean policy cannot prove readiness |

Configuration reloads do not recompute deadlines for admitted work. Legacy backfills occur once from
the original admission timestamp, as the current queue deadline implementation does. Tests use
`TimeProvider`/virtual time and cover exact-boundary races; no correctness test sleeps.

## Provider action ordering on an Agent's provider instance

Provider actions operate on provider-issued immutable `provider_instance_id`. The Agent registry
supplies readiness evidence and the verified attachment, but never selects a target by display name.

### Restore or rollback

1. Persist the provider operation and acquire that `agent_id`'s exclusive lease/fence. When restore
   is the clean prelude or epilogue of a Build, attach the provider operation to the already durable
   Build lease/fence instead of competing for or releasing a second lease.
2. Stop admitting read probes and mutations for the Agent, then drain/cancel its registered probes.
   Persistently increment the observation epoch before provider side effects so any late probe response
   is invalid.
3. Record the Agent ID, credential/session generations, exact ProviderInstance, Agent/provider
   capability snapshot, and actual image/checkpoint provenance.
4. Confirm no different active Build or AgentExplorer mutation owns the Agent. If one does, request its
   cancellation and wait; force requires a separate authorized action.
5. Fence the old session from new state changes.
6. Invoke provider restore/start with operation ID and fence; retries reconcile rather than assume the
   first call did nothing.
7. Wait for the provider's actual state and for the expected Agent to connect
   with a generation newer than the recorded generation.
8. Require the new `Hello` to report no workload and expected Agent/image/capability facts. Perform clock and
   network post-restore checks required by D4/D5.
9. Capture the post-operation provider, Agent-capability, image/checkpoint, credential/session, and
   generation provenance. Mark the Agent ready and release or atomically hand off the provider lease only after
   the reconciliation transaction.

### Power stop/restart

A normal power operation drains and waits exactly like restore. Losing the agent connection is not
proof the ProviderInstance stopped; provider actual state is. Restart completion requires a newer reconciled
agent generation. An emergency hard-power action records that it may have destroyed work, finalizes
the affected domain record according to policy, and quarantines until reconciliation.

### Clone/create

Clone/create reserves provider-host capacity and references an immutable image version. It has no
source-Agent mutation lease when the source is a sealed image, but it has a durable provider operation,
`provider_instance_id`, and intended `agent_id` from allocation. If creation serves an already queued
Build, the target Agent is assigned to that Build lease as soon as its identity exists;
otherwise provider-to-Build ownership is transferred atomically. Capacity is not exposed to unrelated
work until host-side identity checks, agent enrollment/authorization, and first reconciled idle
`Hello` complete. Partial clones are destroyed or quarantined by the same operation journal after a
controller restart.

## Maintenance and upgrades

Maintenance is coordinated work, not an out-of-band flag flip:

1. Desired long-lived maintenance/upgrade policy arrives from Git.
2. The Agent enters `draining`; no new mutation is assigned, but current work is not cancelled.
3. After idle, a maintenance operation acquires the shared Agent lease.
4. Agent upgrade sends the restart instruction only after the lease is durable.
5. The lease remains held until a newer session reports the required agent version and no workload.
6. A checkpoint-restored stale agent is upgraded again before readiness; this does not rebuild the
   image.
7. Deadline failure marks the Agent maintenance or bad and prevents scheduling.

Pool re-baseline, disk compaction, checkpoint pruning, and canaries use background priority and
provider/host capacity. Canary Builds are Builds; provider housekeeping and upgrades are maintenance
operations. Health `bad`, `maintenance`, or `retired` removes future eligibility without rewriting or
implicitly cancelling current work.

## Git-controlled scheduling policy

Long-lived desired state is Git-controlled from day one. The Git/Versioning Expert owns repository
layout and commit workflow; scheduling consumes a validated effective-policy object containing:

```text
repository identity, ref, commit SHA, source path, content hash, parsed policy version
```

Scheduling-related versioned settings include:

- default and per-configuration queue-wait limits;
- priority bands and permitted overrides;
- Project/Build Configuration concurrency caps;
- provider pool/host capacity policy;
- caller/Project/Build Configuration access to target pools and machine trust classes, with its policy
  revision consumed before compatibility matching;
- maintenance windows and desired agent versions;
- persistent drains/enablement and scheduling labels when those are modeled as desired state;
- AgentExplorer mutation queue/concurrency limits and read-probe budgets;
- reconnect, assignment, execution, cancellation, readiness, and release policy within safe bounds.

Every admitted Build or durable operation stores the Git commit/content hash or an immutable resolved
policy snapshot. Later commits affect future admission and desired reconciliation, not already
persisted deadlines or historical provenance unless an explicit migration/action says so. Security or
trust revocation is the deliberate exception for not-yet-started work: dispatch rechecks current
authorization and may narrow/reject the admitted scope, recording both revisions, but can never expand
that scope silently.

The configuration/action boundary is deliberate:

- A durable preference or property is changed through Git and reconciled.
- An immediate command such as cancel, retry, authorize, emergency drain, or force power is an
  operational action. It need not create a Git commit, but it is authorized and journaled.
- A temporary override has an explicit expiry and audit record; it cannot silently become desired
  configuration.

If Git is unavailable, the controller continues safely with the last successfully applied revision
and exposes the stale/error state. It does not accept an unversioned replacement as authoritative.

## REST async operation contract

The Vivarium REST Expert owns final URI and representation conventions. Scheduling requires the
following semantics from day one:

- A potentially long or mutating request returns `202 Accepted` with an operation resource and
  `Location` header. Provider actions, AgentExplorer mutations, maintenance, and asynchronous Build
  cancellation all follow this rule.
- Mutating requests require an `Idempotency-Key`. The key is scoped to principal and operation type;
  an identical replay returns the same resource, while reuse with a different canonical payload is a
  conflict.
- The operation resource exposes domain, domain resource ID, state, phase, timestamps, relevant
  deadlines, cancelability, progress summary, terminal outcome/error, machine/agent references,
  immutable target provenance, correlation ID, authorization decision revision, and policy/config Git
  revision. Secrets and raw command environment are excluded.
- `GET` polling is side-effect free and supports `ETag`; event streaming may supplement but cannot be
  the only way to recover state.
- Cancellation is an explicit idempotent command on the Build or operation resource. It records intent
  and returns current operation state; `202` never promises that the machine has stopped yet.
- The first cancellation reason wins. Repeated cancellation returns the same effective request.
- A busy machine normally leaves an accepted request queued with a visible queue deadline. A caller
  may explicitly request fail-fast admission, which returns a conflict without creating hidden work.
- A completed operation remains queryable according to retention policy; reconnecting clients never
  depend on the original HTTP connection.
- `correlation_id` is returned to the caller and propagated to all durable transitions, SQLite audit
  rows, and operational logs.

Illustrative resources, subject to REST Expert naming review:

```text
PUT  /api/v1/builds/{id}/cancellation
POST /api/v1/agents/{id}/operations
POST /api/v1/provider-instances/{id}/operations
GET  /api/v1/operations/{operationId}
PUT  /api/v1/operations/{operationId}/cancellation
```

Read-only AgentExplorer inventory endpoints return `200 OK` snapshots with `observed_at`, staleness, and
partial-access errors. They still emit a bounded audit event where policy requires it, but they do not
masquerade as durable mutating operations.

## Audit and correlation

Ordinary process logs are not the action journal. From the first REST/Git/UI mutation surface, the
controller maintains a minimal append-only SQLite `audit_events` table (or equivalently an audit
outbox feeding that table). A domain state transition and its audit row are written in the same
serialized transaction. Caller, security, and configuration actions without a domain transition are
persisted before success is returned. An exporter may tail an outbox cursor, but delivery failure
cannot erase or roll back the local audit row.

The minimal row contains bounded scalar fields and a small versioned metadata object:

```text
sequence/event_id, occurred_at, event name,
actor type/id, correlation_id, request/idempotency-key hash,
domain and resource ID, agent_id, credential_generation, session_id, connection_generation,
provider_instance_id where applicable,
lease_id, fence, observation_epoch,
from/to state, deadline,
authorization/policy revision, Git commit/content hash,
reason code, outcome, bounded redacted metadata
```

Emit one durable high-signal event for:

- authorization and admission/rejection, including selected pool/trust class and effective policy/Git
  revision;
- login/bootstrap/security changes and every accepted/rejected Git configuration reconciliation/apply;
- queue claim, assignment send/accept, lease acquisition/release/expiry;
- agent execution result, entry to `RELEASING`, epilogue result, final Build outcome, and cancellation;
- session supersession, adoption, reconnect expiry, and conflicting workload;
- read-probe admission/completion where security policy requires it, observation-epoch invalidation,
  stale fence/session/probe rejection, differing duplicate result, and provider ambiguity;
- provider/maintenance intent, phase change, completion, quarantine, or force action;
- priority override, emergency drain, and health/eligibility transition.

Do not put access tokens, full environment values, secrets, payload bytes, or unredacted command
arguments in the audit row or scheduling logs. Heartbeats are high volume: persist/audit only
connection or liveness state changes and aggregated missed-heartbeat diagnostics, not every heartbeat.
Payload stdout/stderr belongs to the Build/operation log stream, not the audit table. Operational logs
may mirror audit events for diagnostics, but SQLite remains the local source of action history.

## Failure and retry policy

- Agent execution outcome and final Build outcome are separate facts. Epilogue rollback/cleanup
  failure makes the final Build `INFRA` while retaining the execution result and all collected test
  evidence for diagnosis.
- A Build whose payload has begun does not automatically run twice. Infrastructure retry creates a
  new attempt/Build with explicit ancestry after the current Agent is fenced and reconciled.
- Managed disposable capacity may be recycled after infrastructure failure; a physical agent is
  marked bad/maintenance and requires a policy or operator decision.
- AgentExplorer mutations are not automatically retried unless the operation contract declares the side
  effect idempotent and supplies a reconciliation check. “The response was lost” is not evidence the
  command did not run.
- Provider calls are retried by operation ID/fence and actual-state reconciliation.
- Test failures are never scheduler-retried.
- Cancellation, duplicate delivery, and result acknowledgement are always idempotent.

## Required evidence and tests

All coordination code is covered at the lowest useful tier with virtual time. Required scenarios:

- First-runnable FIFO, stable tie breaking, priorities, concurrency caps, and blocked-head bypass.
- Exact queue-deadline boundary and one-time legacy backfill.
- Build and AgentExplorer mutation racing for one Agent: exactly one lease wins.
- Two sessions or credential generations claiming one `agent_id`: only the current durable generation
  can be scheduled, and the lease remains keyed to the Agent across safe session replacement.
- Authorization denies a pool/trust class before compatibility and does not disclose unauthorized
  machine compatibility; the admitted policy revision is preserved.
- Read probe beside a Build without acquiring a mutating lease; rollback drains probes, increments the
  observation epoch, and discards a late old-epoch response.
- Crash after durable claim but before send; crash after send but before acknowledgement; terminal
  result before acknowledgement.
- Duplicate assignment, cancellation, provider command, and identical terminal result.
- Conflicting duplicate payload/result and stale session/fence.
- Session replacement, reconnect adoption, reconnect expiry, and cancellation redelivery.
- Controller restart with prepared, active, cancelling, releasing, and provider-ambiguous work.
- Agent execution succeeds but epilogue cleanup fails: final outcome is `INFRA`, immutable tests/logs/
  artifacts remain available, and capacity is not exposed.
- Rollback completion followed by a stale connection; no new assignment until a newer empty `Hello`.
- Provider timeout after the side effect happened; reconciliation completes rather than repeats an
  unsafe action.
- Git policy update while work is queued/running; stored deadline and provenance remain unchanged.
- Historical target snapshot retains Agent/provider capabilities, actual image/checkpoint, session,
  trust class, and authorization/config revisions after live facts change.
- Async REST idempotency replay and cancellation replay.
- Atomic domain-transition plus SQLite audit/outbox write, including crash/replay behavior, correlated
  from request to terminal/release without secret-bearing values.

Tier-2 protocol tests must eventually run against the previous released agent version so new workload
fields remain backward compatible after pool checkpoint restore.

## Collaboration points

| Expert | Contract needed by scheduling |
|---|---|
| Agent API/SDK | Versioned Agent/session/workload envelope, Agent-scoped fence persistence, read-probe epoch, heartbeat/Hello assertion, ack/cancel/result retry, upgrade handshake |
| TeamCity | Visible Build states, queue priorities/caps, failure/retry semantics, matrix cancellation |
| AgentExplorer | Read/mutate classification, read-probe registry/epoch, operation cancelability/commit point, result contract |
| Vivarium REST | Async operation resources, idempotency, polling/events, cancellation, error mapping |
| Git/Versioning | Effective policy object, Git provenance, reconcile/apply semantics, stale-repo behavior |
| UI | Honest starting/reconnecting/cancelling/releasing/unknown phases and machine-busy explanations |
| User Roles / Admin | Pre-compatibility caller/Project/Configuration access to pools/trust classes; permissions for submit, cancel, priority override, force, emergency drain, provider and maintenance actions |
| Logs | Durable SQLite audit/outbox schema, operational-log projection, redaction, sampling/rate limits, retention/export |
| Platform | Process-tree kill, reboot, power, restore/clone identity, post-operation readiness on each OS/provider |
| Reconciliation Lead | Startup, reconnect, rollback, ambiguity, partial failure, and unknown-state review |
| Docs | Architecture decision reconciliation and current-versus-target accuracy |

## Open questions

1. Should the public Build state gain `STARTING`, or should starting remain an assignment phase under
   existing `RUNNING` as the current implementation does?
2. What are the first committed priority bands and per-domain burst limits? The invariants do not
   require arbitrary priority numbers.
3. Which AgentExplorer reads are safe beside a running test on each OS, and which must opt into the
   exclusive path?
4. Does default AgentExplorer mutation admission queue behind a busy Build, or should interactive callers
   default to fail-fast and opt into queueing?
5. Which provider drivers can accept an idempotency token natively, and what reconciliation probe is
   required for each command that cannot?
6. When a physical agent disappears during an arbitrary command, which operations can prove a safe
   terminal state and which must remain `UNKNOWN` until an operator inspects the host?
7. What is the explicit irreversible commit point for restore, hard power, destroy, software mutation,
   and future file writes?
8. What is the retention/export policy for the mandatory SQLite audit table, and which sensitive
   read-only AgentExplorer actions require individual rather than aggregated audit rows?
9. Which scheduling settings are farm-wide Git desired state versus repository-local
   `vivarium.yaml`, and how are conflicting scopes resolved?
10. How long may upgrade/maintenance drain wait before it remains pending, cancels work, or asks an
    administrator? There must be no implicit destructive answer.
