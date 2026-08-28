# AgentExplorer Design

> Status: **Accepted**
> Implementation: **Planned**
> Maintainer role: [AgentExplorer Expert](../roles/agent-explorer-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D4, D7, D8, D13, D16, D22-D28

This document adds AgentExplorer detail without overriding numbered architecture decisions.

## 1. Summary

AgentExplorer is Vivarium's independent view and management surface for the fleet. It lists stable
Agents, exposes trustworthy host inventory for each one, and will later support operator actions
outside any project or build configuration.

The physical Agent is the reference deployment. A physical host, a hand-managed VM, and a provider-
created VM all run the same agent and expose the same AgentExplorer
contract. Provider-only abilities such as power, console, clone, or checkpoint rollback attach to the
optional ProviderInstance and its provider; they are not invented as guest agent abilities.

```text
Vivarium Controller
  +-- AgentExplorer: Agent -> facts / processes / network / metrics / operations
  +-- TeamCity:   Project -> Build Configuration -> Build -> Step Runs
                         \                           /
                          +-- shared stable Agent identity
                          +-- one current Agent session (v1)
                          +-- shared AgentLease arbiter

Vivarium Agent
  +-- observational AgentExplorer capabilities
  +-- TeamCity build-runner capability
  +-- future mutating AgentExplorer capabilities
```

Sharing transport does not merge the domains. AgentExplorer operations have their own resource model,
permissions, states, audit records, and retention. They are not TeamCity `Build`s.

## 2. Goals

- List and search all known Agents, including disconnected Agents with clearly stale facts.
- Show precise cross-platform OS and host facts, not only a display-name OS string.
- Inspect a safe, explicitly redacted view of the agent process's effective environment.
- Inspect processes and their parameters, preserving partial results when OS permissions deny fields.
- Inspect TCP/UDP endpoints and correlate them to stable-enough process identities.
- Show bounded host/process metrics with explicit observation time.
- Establish the resource, security, audit, lease, Git, and REST contracts needed before files,
  commands, process control, and software/state management are implemented.
- Preserve physical-agent usefulness without requiring a hypervisor or pristine capability.

## 3. Non-goals

- Replacing TeamCity projects, build configurations, builds, build results, or scheduling.
- Treating a remote cleanup command as a build for implementation convenience.
- Becoming a general hypervisor or cloud control plane. Provider operations remain provider-owned.
- Providing SSH, WinRM, PowerShell Direct, or another controller-to-host inbound channel; D1 remains.
- Streaming every process, socket, metric, or environment value through heartbeats.
- Persisting secrets or unlimited high-cardinality telemetry.
- Shipping dummy file or command RPCs before their security and operation contracts are ready.
- Promising identical field availability across Windows, Linux, and macOS when their APIs and
  permissions differ.

## 4. Domain boundary and identity

### 4.1 Agent projection

The AgentExplorer resource is the stable `Agent`; its public `agentId` is the immutable `agent_id`.
It survives upgrade, reinstall, credential rotation, rename, disconnect, and session replacement.
Physical enrollment creates an Agent. Re-enrollment may reclaim the identity only with controller-
issued proof; presenting a client-selected `agent_id`, hostname, or MAC address is not proof.

Credential generation and connection session are runtime aspects of that Agent. V1 permits exactly
one current credential generation and one accepted current session. Replacement fences the old
credential/session before the new one can publish observations or accept work. A provider-created VM
may have a separate `ProviderInstance`, attached to the intended `agent_id` after provider identity
verification; physical and hand-managed Agents normally have no such record.

An Agent reuses the authoritative TeamCity-style status axes from D8:

- connected / disconnected;
- authorized / unauthorized;
- enabled / disabled;
- idle / building / upgrading;
- health and provider-instance lifecycle where applicable.

AgentExplorer adds inventory freshness, effective capabilities, active AgentExplorer operation, Agent
observation epoch, and shared lease state. It must not create competing connectivity or authorization
facts.

Authorization resolves the target `agent_id` through its applied fleet/pool membership and provider
projection. A caller who can inspect one pool does not gain access to sibling pools because the same
agent binary or provider type is used there. Agent authorization is necessary runtime eligibility,
not the user's fleet authorization boundary.

### 4.2 Four separate namespaces

These concepts must never collapse into one string map:

| Concept | Example | Owner |
|---|---|---|
| Agent capability | `agent-explorer.processes.v1` | Agent binary / Agent API |
| Reported fact | `system.os.build=26100.3915` | Agent observation |
| Custom setting/label | `custom.lab=berlin` | Git-backed operator configuration |
| Policy and authorization | environment values disabled for this Agent/caller | Controller policy and roles |
| Provider capability | `provider.snapshot.rollback.v1` | Machine provider |

A feature is effective only when it is supported by the connected agent, enabled by applied policy,
permitted for the caller, and available under current OS privileges. REST and UI must preserve the
difference between `unsupported`, `policy-disabled`, `forbidden`, `offline`, and `temporarily-failed`.

Observed host facts use the canonical `system.*` namespace. Legacy `os.*` keys are transitional and
must not enter the public REST/Git contract. The separately defined `env.*` namespace contains only
explicitly published safe scheduling parameters; it is not a copy of the host environment.

## 5. Agent capability catalogue

The following names describe AgentExplorer requirements. The Agent API/SDK Expert owns their final wire
representation, negotiation, compatibility rules, packaging, and implementation seam.

| Proposed capability | Semantics | Initial state |
|---|---|---|
| `agent-explorer.host-facts.v1` | Typed OS, runtime, hardware summary, boot, time-zone, and network-address facts | First slice |
| `agent-explorer.environment.v1` | Refreshable allow-listed safe environment with irreversible agent-side redaction | First slice |
| `agent-explorer.processes.v1` | Bounded process refresh probe with partial fields/errors | First slice |
| `agent-explorer.network-endpoints.v1` | Bounded TCP/UDP refresh probe and owning-process references | First slice |
| `agent-explorer.metrics.v1` | Bounded host/disk/network and optional process measurements | Next slice |
| `agent-explorer.files.read.v1` | Policy-rooted browse/stat/read/download | Planned |
| `agent-explorer.commands.exec.v1` | Non-interactive command operation with bounded output | Planned |
| `agent-explorer.process.control.v1` | Start/stop/terminate with identity fencing | Planned |
| `agent-explorer.software.inventory.v1` | Cross-platform installed-software/services inventory | Planned |
| `agent-explorer.software.manage.v1` | Install/update/uninstall operations | Planned |
| `agent-explorer.state.manage.v1` | Apply declared host state outside TeamCity builds | Planned |
| `agent-explorer.environment.reveal.v1` | Distinct non-cacheable reveal flow, only if later approved | Not accepted for v1 |

Registration, authentication, heartbeats, reconnect fencing, and upgrade negotiation are mandatory
Agent API behavior rather than optional AgentExplorer capabilities. `provider.power`, `provider.console`,
`provider.clone`, and `provider.snapshot.rollback` belong to the ProviderInstance surface.

AgentExplorer capability requests to the Agent API/SDK Expert must require request IDs, deadlines,
cancellation where meaningful, maximum response sizes, backpressure or paging/chunking, per-source
errors, sensitivity metadata, and exact Agent observation-epoch plus connection-generation fencing.
This document does not allocate proto field tags.

## 6. Inventory contracts

### 6.1 Common snapshot envelope

Every inventory dataset has a controller-issued snapshot identity and metadata:

- `agentId`, credential generation, session ID, and connection generation;
- controller-owned Agent observation epoch and snapshot ID/generation;
- capability name/version;
- `observedAt` when the agent sampled the OS;
- `receivedAt` on the controller's trusted clock;
- `status`: `complete`, `partial`, `unavailable`, or `unsupported`;
- structured source errors with platform error codes but no secret-bearing exception dump;
- item count, truncation flag, and continuation token where applicable;
- requested and effective maximum age.

The validity fence is `(agent_id, observation_epoch, connection_generation)`. A refresh operation
captures that tuple before dispatch, and the controller accepts its result only while all three still
match the current Agent projection. Provider lifecycle increments `observation_epoch` before a
rollback, restore, reprovision, or other state discontinuity and stops, drains, or invalidates probes
from the old epoch. An Agent rebind or replacement also advances the epoch. A reconnect advances
connection generation. A crossing probe terminates as stale/invalidated; its payload is discarded and
cannot become the Agent's latest cached snapshot.

The observation epoch is controller/provider state, not an OS boot identifier. A memory rollback can
resume the same boot, so boot ID is useful descriptive evidence but never a validity fence.

The controller computes user-facing age from `receivedAt`, because checkpoint restore can leave guest
clocks wrong (D4). `observedAt` remains evidence but is not the sole freshness authority. An empty
complete snapshot means “observed none”; an unavailable or partial snapshot never masquerades as an
empty list.

Heartbeats remain small: liveness, session/lease identity, current work, capability digest, and a
host-facts generation/digest. Process, endpoint, environment, and metrics payloads do not ride on each
heartbeat.

Static safe facts may be persisted when the agent connects and whenever their generation changes.
Dynamic inventory is collected only by an explicit bounded refresh operation and cached under an
explicit TTL and retention policy. Snapshot `GET`s never contact the Agent. They return only the
latest cached snapshot that matches the current Agent epoch and credential/session generations, applied policy and
redaction contract, and caller authorization. If none exists, REST reports that no current authorized
snapshot exists and tells the caller to start a refresh; it does not return an empty inventory.

A disconnected Agent can show its last persistent safe facts with a stale badge. Process, endpoint,
environment, and metrics snapshots from a superseded session or prior observation epoch are never
presented as current.

### 6.2 Agent host facts

The typed host-facts contract includes:

- hostname and a platform machine/boot identifier where safely available;
- OS family;
- Windows product/edition, version, build and UBR; Linux distribution fields from `os-release` plus
  a separate kernel version; macOS product version/build plus kernel version;
- CPU architecture, logical processor count, and optional model summary;
- total physical memory;
- agent version, runtime version, service account, and interactive-session truth;
- boot time/uptime and time zone;
- local interface/address summary without pretending it is stable machine identity.

Fields use typed structures with an extension facts map only for platform-specific additions. Values
used for TeamCity compatibility are projected deliberately into stable `system.*` reported
parameters; the raw inventory structure is not itself the scheduler's unbounded parameter map.

### 6.3 Environment

Environment v1 means a safe view derived from the effective environment inherited by a process
launched by the agent, not every system/user environment source in the OS. Each entry can report an
allow-listed name, safe value or irreversible redaction marker, availability, and a source only when
the platform can determine it reliably.

Safety rules:

- v1 has no raw-secret reveal path, including for administrators or Superuser;
- only allow-listed names are emitted, and secret-like names/values are irreversibly replaced on the
  agent before transport, then defensively redacted again at controller/log boundaries;
- the controller caches only this safe snapshot under its bounded TTL; it never receives the removed
  raw value and cannot reverse the marker;
- safe scheduling values are explicitly published as `env.*` reported parameters; the controller
  never promotes every environment variable automatically;
- REST responses and logs must never include bearer tokens, enrollment tokens, private keys,
  passwords, or unbounded raw exception state.

Any future raw-value reveal requires a separately reviewed, distinctly named Agent capability and
permission. It must be non-cacheable, non-replayable, audited, and unable to flow through ordinary
snapshot, log, SSE, or idempotency storage. That future is not part of v1.

### 6.4 Processes

A process record should include, when the platform and permissions allow:

- `pid`, parent PID, name, and start time;
- executable path, command line/arguments, user, session, and working directory;
- CPU time, working/private memory, and thread count;
- per-field availability/redaction indicators.

PID alone is unsafe because it is reused. A `ProcessRef` contains `(agent_id, observation_epoch,
snapshot_generation, connection_generation, pid, start_time)`. A future stop/terminate operation must
compare the complete reference immediately before acting. Boot ID may be included as descriptive
evidence but is not a fence because memory rollback can restore the same boot. A process that exits
during collection produces a partial/raced item, not failure of the whole snapshot. Command lines,
usernames, and paths follow the sensitive-read and redaction policy.

### 6.5 TCP/UDP endpoints

An endpoint record includes:

- protocol (`tcp` or `udp`) and address family;
- local address/port;
- TCP state and remote address/port where applicable;
- UDP binding state presented as `bound`, not a fictitious TCP-style `listen` state;
- optional owning `ProcessRef`, process name/path projection, and ownership error.

“Open ports” in the UI defaults to listening TCP sockets plus bound UDP endpoints. A separate view may
show all TCP connections. Ownership can legitimately be absent because a process exited, the OS API
did not expose it, or privileges were insufficient. Those causes stay distinguishable.

### 6.6 Metrics

The first metrics surface is an inventory aid, not a time-series monitoring product:

- host CPU utilization, memory used/available, load where meaningful;
- filesystem capacity/free space by mounted/local volume;
- network counters by interface;
- optional process CPU/memory values attached to a process snapshot.

Every sample records interval and observation time. The controller bounds sampling cadence, number of
series, history, and label cardinality. Long-term dashboards, alert-rule engines, and arbitrary
Prometheus-style labels are non-goals until retention and operating cost are proven.

## 7. Agent listing and search

`GET /api/v1/agents` is a paginated, server-side search over durable AgentExplorer projections. Initial
filters and sorts cover:

- Agent ID, display name, hostname, current credential generation, and session diagnostics;
- connected, authorized, enabled, activity, health, and current lease kind;
- OS family/version/build/architecture and agent version;
- custom labels/properties;
- effective capability, Agent kind, and optional provider kind;
- last communication and inventory age.

Text search covers stable identity, names, and configured searchable labels. It must not synchronously
fan out to the fleet. Searching current process names or ports is outside the first slice; if later
added, it searches explicitly retained/indexed snapshots and exposes their age.

List results include only summary facts. Detailed inventories require Agent-scoped REST resources
and authorization against the target fleet/pool and provider projection.

## 8. Git-backed configuration and observed state

All durable AgentExplorer settings are desired state in Git, including:

- host/group display settings and custom labels;
- AgentExplorer enablement and capability policy;
- environment allow-list/redaction rules;
- file roots, command allow-lists, software policy, and desired host state when those features land;
- inventory cadence/TTL and retention policy within validated system bounds.

The exact repository layout, branch/approval flow, commit signing, UI commit workflow, rollback, and
conflict strategy belong to the Git/Versioning Expert. AgentExplorer requires these invariants:

1. SQLite stores an indexed applied projection and observed state, never an independent editable copy.
2. Every applied desired-state projection records repository identity, commit SHA, path/key, schema
   version, validation result, and reconciliation time.
3. UI and REST changes produce Git commits through the sanctioned versioning workflow; there is no
   hidden direct-write endpoint.
4. A failed validation or reconciliation preserves the last known-good applied revision and exposes
   the failure. It does not partially apply a commit.
5. Rollback means applying another Git revision/commit; history is not rewritten.

Live observations and runtime actions do not become Git commits. A refresh, command, process stop, or
reboot is an event in the operation/audit journal. If an operator wants durable host state, they edit
the Git-backed desired state and reconciliation records the resulting actions.

## 9. REST surface

REST is designed from the first AgentExplorer slice and is the contract used by UI and external tools.
Controller-to-agent traffic remains AgentHub gRPC. The Vivarium REST Expert owns shared conventions;
AgentExplorer requires at least these resources:

```text
GET  /api/v1/agents
GET  /api/v1/agents/{agentId}
GET  /api/v1/agents/{agentId}/facts
GET  /api/v1/agents/{agentId}/environment
GET  /api/v1/agents/{agentId}/processes
GET  /api/v1/agents/{agentId}/network-endpoints
GET  /api/v1/agents/{agentId}/metrics
POST /api/v1/agents/{agentId}/inventory-refreshes
GET  /api/v1/operations/{operationId}
PUT  /api/v1/operations/{operationId}/cancellation
```

`{agentId}` is always `agent_id`. The environment, processes, network-endpoints, and metrics `GET`s
are side-effect free: after checking fleet/pool authorization, they return only the latest cached
snapshot valid for the current observation epoch, credential/session generation, applied
policy, and requested view. They never trigger an Agent call, wait for a live host, or silently
refresh.

`POST .../inventory-refreshes` accepts a bounded set of requested datasets, validates authorization
for every dataset before dispatch, and starts a bounded probe. It returns `202 Accepted` with a
`Location: /api/v1/operations/{operationId}` locator and operation representation. The operation
captures `agent_id`, observation epoch, credential/session generation, probe IDs,
deadlines, policy revision/digest, and caller identity. Success atomically publishes the accepted
snapshots; a rollback, credential replacement, superseding connection, timeout, or cancellation prevents stale
publication.

Planned resource families, not initial dummy endpoints:

```text
/api/v1/agents/{agentId}/files
/api/v1/agents/{agentId}/commands
/api/v1/agents/{agentId}/process-actions
/api/v1/agents/{agentId}/software
/api/v1/agents/{agentId}/desired-state
```

Requirements for the REST Expert:

- explicit `/api/v1` compatibility and deprecation policy;
- cursor pagination, bounded page sizes, stable sort, filter syntax, and field projection;
- standard problem responses preserving unsupported/disabled/forbidden/offline/partial distinctions;
- ETags/conditional GET for snapshots and applied Git projections;
- caller-generated idempotency keys for all mutating requests;
- asynchronous operation resources instead of keeping an HTTP request open for long work;
- correlation IDs linking REST requests, agent requests, operations, audit, and bounded logs;
- cookie plus anti-forgery protection for UI writes and bearer-token scopes for API clients;
- no requirement to load React in order to test or use the API.

## 10. Permissions, audit, and logs

The User Roles Expert will map final permissions into the TeamCity-derived model. AgentExplorer needs
distinct actions equivalent to:

- Agent list/basic facts read;
- process/network/metrics read;
- safe environment snapshot read;
- sensitive process fields read;
- inventory refresh;
- command/process/software/state operation create and cancel;
- Git-backed AgentExplorer policy read and propose/change;
- audit read.

Normal list/facts reads use bounded access logs. Sensitive reads and every mutation produce a durable
audit event with timestamp, principal, auth method, request/correlation/idempotency IDs, target
`agent_id`, operation type, redacted parameter summary or digest, applied policy commit SHA, lease
and connection generation, result, duration, and cancellation/failure reason. Audit never records
secret values, tokens, unrestricted command output, or raw environment values.

Operational logs explain behavior but are not the audit source of truth. The Logs Expert owns sinks,
rotation, retention, cardinality limits, and redaction infrastructure. AgentExplorer supplies stable event
names and avoids per-metric/per-process information logs that would scale with fleet size.

Every permission check resolves `agent_id` to the current applied fleet/pool path. Provider-backed
Agents inherit the provider/pool projection selected by applied Git policy; they do not become
globally visible merely because they auto-authorize. Provider and pool membership changes are
fenced configuration transitions, and an operation retains the authorized target projection and
policy revision used at creation for audit while rechecking runtime eligibility before dispatch.

## 11. Shared lease and operation model

An Agent has capacity one for exclusive work in v1. TeamCity builds and mutating AgentExplorer
operations use one controller-owned `AgentLease` arbiter:

```text
AgentLease
  agentId
  leaseId / fence
  kind: teamcity-build | agent-explorer-operation | provider | maintenance
  ownerId
  agentId / credentialGeneration / sessionId / connectionGeneration
  acquiredAt / deadline / reconnectDeadline
  cancellationRequestedAt / reason
```

Lease acquisition and release target `agent_id` and are durable and serialized with eligibility,
credential generation, provider lifecycle, and pool-projection changes. A stale epoch, credential,
or connection cannot accept or complete work after a newer fence owns the Agent. Disconnect preserves
ownership for a bounded reconnect window; recovery re-adopts or aborts the exact operation. Disabling an Agent prevents
new work but does not silently cancel existing work.

Read-only probes may run without the exclusive lease only when their capability classification is
observational, their resource/time/output limits are enforced, and they cannot invalidate build
results. The controller applies per-host concurrency and rate limits. Resource-intensive inventory can
be promoted to a shared or exclusive lease class if measurements show unacceptable build impact.

Future mutating work is a `AgentExplorerOperation`, not a build:

```text
queued -> dispatched -> accepted -> running -> succeeded
                                      |       -> failed
                                      |       -> cancelled
                                      +-> cancel-requested
queued/dispatched ---------------------------> expired
```

An operation stores actor, target `agent_id`, type, redacted input/digest, Git policy revision and
authorized pool/provider projection, lease, observation epoch, credential/connection generation,
deadlines, progress summary, terminal status, and bounded output/artifact references.
Cancellation is idempotent and the first terminal result wins. AgentExplorer may have its own small queue,
but it must not enter the TeamCity Build Queue or Build history.

## 12. Future feature boundaries

### Files

The initial UI shows a `Files — planned` surface only. The later contract must use policy-defined roots,
path-traversal and symlink/reparse-point defenses, bounded listing/read sizes, range downloads, clear
text/binary handling, and audit for sensitive reads. Arbitrary filesystem root access is not implied.

### Commands and process control

The initial UI shows `Commands — planned`. Later command execution is non-interactive first, with
explicit executable/arguments/working directory/environment, timeout, output limits, process-tree
cancellation, and an allow/deny policy. Shell strings are not the canonical API. Secrets use
references, never Git values or logged plaintext. Interactive terminal support is a distinct future
capability.

Process control uses `ProcessRef`, not PID alone. Start/stop/terminate actions are separately
authorized and audited.

### Software and host state

Software inventory needs platform-specific normalized identities without claiming that MSI, package
managers, Homebrew, applications, services, drivers, and portable programs are identical. Mutations
are durable AgentExplorer operations. Declarative management is Git-backed desired state plus observed
reconciliation; immediate operator actions remain journaled exceptions.

## 13. Current and target state

### Current evidence

- `AgentRegistry` has persistent identity projections, connected/reconciled/auth/enabled/activity
  axes, heartbeat time, connection generation, and a single `CurrentBuildId`.
- `AgentStore` persists reported and operator custom parameter maps separately plus basic OS/version,
  architecture, interactivity, and agent version.
- `ControlPlane.ListAgents` exposes a flat unfiltered list with basic status and merged parameters.
- Agent heartbeats report only the running build identity; there is no AgentExplorer inventory exchange.
- Build assignment, cancellation, reconnect ownership, and session fencing provide proven patterns,
  but they are build-specific rather than a shared general lease.
- There are no process, TCP/UDP, environment, or metrics collectors; no typed capability negotiation,
  observation epoch, AgentExplorer REST resources, operation store, or AgentExplorer audit journal.

Relevant implementation evidence:

- `src/Vivarium.Controller/Agents/AgentRegistry.cs`
- `src/Vivarium.Controller/Agents/AgentStore.cs`
- `src/Vivarium.Controller/Agents/AgentAdministration.cs`
- `src/Vivarium.Controller/Management/ControlPlaneService.cs`
- `src/Vivarium.Contracts/protos/vivarium/v1/control_plane.proto`
- `src/Vivarium.Contracts/protos/vivarium/v1/agent_hub.proto`
- `src/Vivarium.Agent/AgentRunner.cs`

Relevant architecture evidence: D1, D4, D8, D11, D13, D15, D16, D22-D28, and sections 5, 6, 9,
and 10.

### Target slices

1. Make the implemented stable `agent_id` authoritative across AgentExplorer, REST, Git policy,
   authorization, provider attachment, session fencing, observation epochs, and persistence.
2. Request typed capability negotiation and host-facts support from the Agent API/SDK Expert.
3. Add the read-only REST Agent list/detail and applied-policy projection; keep current gRPC management
   compatibility as needed.
4. Add explicit bounded environment, process, and network refresh operations plus cached snapshot
   GETs with cross-platform evidence, rollback invalidation, partial-result tests, irreversible
   redaction, limits, and freshness UI contracts.
5. Add bounded metrics and prove storage/log cardinality.
6. Generalize build-specific occupancy into the shared durable per-Agent lease without changing
   Build history.
7. Add the AgentExplorer operation store before implementing commands, process control, files, or
   software/state mutations.

## 14. Design invariants

- One agent connection can serve both products; one domain entity cannot silently serve both products.
- Offline/stale/partial/unsupported/forbidden are first-class states, never empty-data shortcuts.
- Agent identity, lease targets, operations, authorization, and observation epochs key on stable
  `agent_id`; credential and session generations are replaceable runtime fences.
- Snapshot GETs are side-effect free; only explicit refresh operations contact an Agent.
- A stale observation epoch, credential generation, or connection generation cannot publish a current snapshot.
- Heartbeat load is independent of host process/socket counts.
- Environment v1 is allow-listed and irreversibly redacted before transport; no raw value reaches its
  cache, REST representation, logs, or UI.
- Git is authoritative for durable settings; audit is authoritative for runtime actions.
- REST and agent contracts are versioned before UI dependence forms around them.
- Physical agents need no provider capability to be useful.
- Provider snapshot/power actions target the exact ProviderInstance attached to the Agent and
  coordinate through the Agent's lease arbiter.
- No AgentExplorer mutation can overlap a TeamCity build on a capacity-one Agent.
- No future capability is implied by a placeholder screen.

## 15. Open questions

1. Which host facts and dynamic snapshots are persisted, for how long, and under which configurable
   upper bounds?
2. Which Windows/Linux/macOS privilege level is required for complete process and endpoint ownership,
   and what reduced-mode contract is acceptable for non-elevated agents?
3. May observational inventory run concurrently with timing-sensitive tests, or must a build
   configuration be able to suppress all probes except heartbeats?
4. Which audit records are retained indefinitely, and which operation output becomes a bounded blob
   artifact rather than a log field?
5. Which TeamCity-derived roles receive safe inventory read access by default on each fleet/pool
   scope?
6. What fleet size and largest-host process/socket counts define pagination, latency, payload, and
   retention acceptance budgets for the first release?
