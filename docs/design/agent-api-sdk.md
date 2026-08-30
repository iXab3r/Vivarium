# Agent API/SDK Design

> Status: **Accepted**
> Implementation: **Partial**
> Maintainer role: [Agent API/SDK Expert](../roles/agent-api-sdk-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D1, D2, D4, D7, D8, D14, D16,
> D19-D24, D26, D27, D29, D30

Numbered architecture decisions remain authoritative. This document specializes their Agent API/SDK
boundary; a contradiction requires an architecture update in the same change.

This document defines the shared agent seam used by TeamCity-style builds and AgentExplorer fleet
management. It covers the reverse-connected protocol, capability negotiation, internal capability
SDK, physical-agent enrollment, packaging, deployment, and upgrades. It also defines how Git-backed
configuration, REST, authorization, audit, platform implementations, providers, and reconciliation
meet that seam.

It does not define TeamCity projects/configurations, AgentExplorer product screens, public REST route
names, user-role membership, VM provider verbs, or a UI framework.

## Current state

The Phase 1 implementation already provides:

- One bidirectional proto3 `AgentHub.Session` stream per agent over pinned TLS.
- Persistent `agent_id`, pending enrollment, explicit authorization, and durable token storage with
  restrictive file permissions. Durable credential and connection generations fence replacement and
  reconnect, survive controller restart, and are returned authoritatively in the handshake.
- A per-connect `session_id`, application heartbeats, reconnect, newer-session replacement, and
  restart-safe controller ownership recovery.
- Additive protocol-range negotiation and bounded versioned capability IDs. Current Agents negotiate
  `teamcity.build-runner.v1` and `agent-explorer.host-facts.v1`; pre-negotiation Agents remain visible
  and may finish adopted work but are drained from new assignments.
- Bounded typed connect-time host observations for Windows, Linux, and macOS, including distinct
  product/build/kernel identity, OS/process architecture, hostname, Agent/package identity, collection
  outcome/issues, and observation provenance. Capabilities persist independently from observation
  success and are available through Agent REST reads.
- Build assignment acknowledgement, duplicate-assignment suppression, idempotent cancellation,
  process-tree termination, durable terminal-result retry, and controller acknowledgement.
- Basic reported parameters plus operator-owned custom parameters stored separately.
- Agent desired enablement now has one canonical Git document and a durable desired/applied
  projection. `/api/v1/agents/{id}/settings` GET/PUT commits `spec.enabled` before activation, and the
  resulting applied state feeds the existing scheduler-visible enabled axis across restart.
- Agent version/package/upgrade-operation reporting and a controller `RestartAgent` message.
- Immutable content-addressed per-RID package storage, exact Server-release catalog import, and
  Agent-scoped authenticated manifest/package reads over pinned TLS. New operations resolve the target
  solely from the running Server release and observed Agent RID.
- Durable per-Agent upgrade operations with an atomic maintenance drain, a distinct `HANDOFF_READY`
  commit point, bounded restart dispatch, exact newer-generation/RID/digest reconciliation, durable
  phase history, first cancellation reason, and restart recovery. Cancellation releases a drain only
  before handoff; afterward it is an audited rollback request and the drain remains held.
- Content-addressed bootstrap activation with portable archive validation, cached-content rehashing,
  an exact seed digest, strict persisted receipts, a singleton supervisor/child lease, a monotonic
  skew-safe deadline, bounded termination reporting, and retained LKG. Candidate acceptance uses a
  crash-recoverable ready → promoted → commit-accepted → committed → server-confirmed handshake;
  eligibility returns only after the Agent confirms the controller's durable receipt. Linux/macOS
  tier-2 evidence launches real bootstrap/Agent processes for success and failed-candidate rollback;
  another tier-2 scenario proves one busy Agent does not block its peer.
- `viv-cli agent upgrade`, `viv-cli agent upgrade-status`, and the synonymous phase-aware
  `viv-cli agent upgrade-cancel` / `upgrade-rollback` recovery commands use the public REST
  resources. Status prints the held-drain flag, retry generation/deadline, failure/cancellation reason,
  and bounded transition history. Starting a packaged Server imports its release `catalog.json`
  idempotently but never silently restarts the fleet. The raw publication endpoint exists only behind an
  explicit hidden development/test option and is not a product or CLI contract.

The current state is not yet an end-user agent platform:

- There is no signed `AgentPolicyBundle`, apply acknowledgement, or exact-policy dispatch gate.
- Dynamic processes, network endpoints, environment inspection, metrics, and refresh operations are
  not implemented; the current typed observation is deliberately static and connect-time only.
- The only executable workload is `BuildAssignment`; there is no generic operation envelope or
  AgentExplorer operation lifecycle.
- Preconfigured archives, installers/setup endpoints, and signing/notarization are not complete. The
  TeamCity release workflow now produces embedded catalogs and unstamped public Agent templates; the
  D30 manifest path and post-authorization handoff are implemented, while the D21 initial
  installer-authenticity gate remains open.
- Per-Agent central rollout, drain, health acknowledgement, and rollback are implemented. Fleet/group
  rollout orchestration, release channels/pins, automatic canary policy, and previous-release
  compatibility CI remain future work.
- Public Agent REST reads consume the typed observation/capability projection, and the first
  Git-backed REST mutation now controls only Agent `spec.enabled`. Display name, custom parameters,
  authorization policy, maintenance/drain policy, capability policy, and release/rollout settings are
  not yet wired through the desired-configuration lifecycle.

## Target model

One agent binary runs on every physical machine, long-lived VM, and provider-created guest. The
controller owns decisions; the agent exposes small, versioned capabilities and executes fenced work.

```text
Git desired configuration ----> Controller reconciler ----> Agent policy/status
                                      |                         ^
REST / UI / CLI ----------------------|                         |
                                      |                  AgentHub Session
                                      v                         |
                              durable work/operations ----------+

Machine provider ---------------- provider-instance lifecycle and snapshot/power/console
Agent ---------------------------- stable execution identity, in-guest facts and operations
```

TeamCity and AgentExplorer share identity, connection, authorization, capability negotiation,
heartbeats, fencing, leases, cancellation, progress, and audit correlation. They retain separate
domain entities and histories:

- A TeamCity build remains a `Build` with build steps, artifacts, logs, and results.
- An AgentExplorer action remains a management `Operation`, not a disguised build.
- Observational AgentExplorer requests may coexist with a build when their declared cost and platform
  behavior permit it.
- Mutating AgentExplorer operations use the shared exclusive-work arbiter and cannot race a build,
  restart, provider restore, or another mutation.

## Agent, credential, and session identity

The stable resource and its runtime fences have different lifetimes and must not be collapsed:

| Identity | Meaning | Lifetime and authority |
|---|---|---|
| `agent_id` | Stable ID of the Vivarium Agent and target of fleet policy, work, and side effects | Controller-owned; survives reinstall, credential rotation, rename, disconnect, and retained history |
| `credential_generation` | Current revocable authentication generation for the Agent | Controller-issued and monotonically replaced by an audited authorization/re-enrollment workflow |
| `session_id` | One reverse-connected process session | Agent-generated nonce per connect; superseded atomically and used for delivery fencing |
| `connection_generation` | Controller ordering of accepted sessions for the Agent | Monotonic per `agent_id`; protects against reconnect and restored-memory races |

All Agent-scoped configuration, AgentExplorer operations, provider handoffs, leases, and immutable
execution provenance target `agent_id`. The current credential generation authenticates that Agent;
self-reported hostname, MAC address, or a client-selected ID is never sufficient identity proof.

V1 permits one current credential generation and one accepted current session per `agent_id`.
Reinstall or credential rotation completes an explicit, audited replacement that revokes the prior
generation before the replacement can receive new work. Reconnect atomically supersedes only the old
session. Historical builds and operations retain `agent_id` plus the executing credential/session and
connection generations where required for provenance and fencing.

For physical enrollment, authorization creates a new Agent or explicitly reclaims an existing
`agent_id` through controller-issued proof. The controller never accepts a client-selected `agent_id`
as proof of that claim. For provider-managed capacity, the provider reserves the intended `agent_id`;
the D7 nonce plus provider-side facts validate attachment of the connecting guest to that Agent and
its optional `ProviderInstance`.

The current `Hello.agent_id` already carries the stable Agent identity. Migration is additive: new
fields carry credential and connection generations without changing `agent_id` semantics. Conflicting
self-reported or provider-attested identity is a reconciliation error and blocks work.

## Capability model

### Capability axes and related state

| Axis/state | Example | Source of truth | Persistence |
|---|---|---|---|
| Support/version | `agent-explorer.processes.v1` | Agent binary + SDK module | Advertised each session; catalogued by controller |
| Applied policy enablement | Capability enabled in acknowledged policy digest | Validated Git revision + applied `AgentPolicyBundle` | Desired revision and per-session apply ACK/error |
| Caller authorization | Caller has `fleet.inventory.view` | User-role/authz system | Evaluated for each request |
| Runtime eligibility | Connected, healthy, idle or safely concurrent, current credential/session fences | Controller reconciliation and lease state | Current operational projection |
| Request outcome/completeness | `partial`, 83 of 91 processes visible | Agent operation result | Per request or durable operation result |
| Reported fact | `system.os.family=windows` | Observation from agent | Snapshot with observation time |
| Custom parameter | `custom.lab=berlin` | Git desired configuration | Git revision plus reconciled projection |

Provider capabilities such as checkpoint, clone, power, or console form a sixth, separate catalog
owned by the machine provider. They may participate in scheduling requirements but are never
advertised as guest-agent capabilities.

An operation may be dispatched only when the first four capability axes permit it:

```text
advertised by agent
AND enabled by the exact policy bundle acknowledged by this session
AND permitted for the caller
AND allowed by current health/session/lease state
```

Outcome and completeness exist only after a request is attempted. The agent returns a typed outcome
such as `succeeded`, `partial`, `permission_denied`, `cancelled`, `timed_out`, or `failed`, plus bounded
completeness metadata appropriate to the capability. A permission-denied or partial request does not
remove the capability from subsequent advertisement; support describes implementation/version, not
the success of the last observation.

The UI and REST projections must preserve the reason when dispatch is false: `unsupported`,
`platform-unavailable`, `policy-disabled`, `forbidden`, `offline`, `busy`, or `unhealthy` are not the
same state.

### Stable identifiers

Capability IDs use lowercase dotted namespaces and include the contract major version:

```text
teamcity.build-runner.v1
agent-explorer.host-facts.v1
agent-explorer.environment.v1
agent-explorer.processes.v1
agent-explorer.network-endpoints.v1
agent-explorer.metrics.v1
agent-explorer.files.read.v1              # planned
agent-explorer.commands.exec.v1           # planned
agent-explorer.process.control.v1         # planned
agent-explorer.software.inventory.v1      # planned
agent-explorer.software.manage.v1         # planned
agent-explorer.state.manage.v1            # planned
```

Adding optional fields does not create a new capability major. Incompatible input, output, security,
or lifecycle semantics require a new major ID. A rename does not create an alias automatically; the
catalog declares migrations deliberately.

### Capability descriptor

The controller maintains metadata for every known capability:

- ID, owning domain, summary, and lifecycle state: experimental, supported, deprecated, removed.
- Minimum agent version and compatible controller range.
- Observation versus mutation classification.
- Concurrency class: heartbeat-safe, read-only concurrent, or exclusive.
- Input/output contract and size/time limits.
- Required caller permission and data sensitivity.
- Supported platform matrix and required privilege.
- Audit event names and default logging/redaction policy.
- SDK module and conformance-test owner.

The agent advertises the IDs and versions implemented for the current platform. Missing runtime
privilege does not erase support: a process collector remains advertised and returns a typed
`permission_denied` or partial result with completeness details. The agent omits a capability only
when its binary/platform has no conforming implementation, or advertises a lower version when that is
the actual contract it implements.

### Canonical parameter namespaces

Scheduling and inventory use canonical, writer-owned namespaces:

| Namespace | Writer and meaning |
|---|---|
| `system.*` | Agent-observed OS, architecture, hostname, runtime, hardware, and session facts |
| `env.*` | Explicitly published, policy-allowed environment parameters; never the raw environment by default |
| `capability.*` | Controller projection of negotiated support/version and effective availability; not a free-form agent map |
| `custom.*` | Operator-owned desired parameters from a validated Git revision |
| `agent.*` | Controller-owned stable Agent identity, kind, health, and clean-policy traits |
| `provider.*` | Provider-observed lifecycle, image, pool, snapshot, power, and host-side traits |

Writers cannot claim another namespace. In particular, an agent cannot self-assert `custom.*`,
`agent.*`, or `provider.*`, and provider traits never masquerade as in-guest `system.*` facts.

The current aliases migrate additively:

| Current key | Canonical key |
|---|---|
| `os.family` | `system.os.family` |
| `os.version` | `system.os.version` plus normalized platform-specific build fields |
| `arch` | `system.arch` |
| `hostname` | `system.hostname` |
| `machine.kind` | `agent.kind` (controller-owned projection; legacy key accepted during migration) |

During the supported legacy window, old agents may send the current keys and the controller
normalizes them before matching. Updated agents send canonical `system.*` keys and may temporarily
include legacy aliases for old controllers. If canonical and legacy values conflict, the controller
does not guess: it records a reconciliation error and blocks new work. New configuration is validated
against canonical names; legacy requirement names receive deprecation diagnostics and are removed only
after the published compatibility window. Immutable history keeps the raw received map and its
canonical normalized projection so migration cannot rewrite past provenance.

### Negotiation

The hello exchange eventually carries:

- Agent semantic version and protocol compatibility range.
- Supported capability IDs with any bounded feature flags.
- Exact host facts or a facts revision/digest when the unchanged snapshot already exists.
- Current fenced work identity: no work, build ID, or management operation ID.
- Stable `agent_id`, credential generation, session ID, provider identity proof when applicable, and
  interactive state.

The welcome response carries:

- Controller version and protocol selection.
- Authorization and enablement state.
- Desired policy revision/digest and the current session's acknowledged applied revision/digest.
- Server time and upgrade disposition.
- Accepted/rejected capabilities with reasons when policy or controller support differs.

Negotiation is additive. The controller schedules or dispatches only against the negotiated set for
the current session, never against the version string alone.

### AgentPolicyBundle application

Git-backed agent policy is delivered as a validated, authenticated `AgentPolicyBundle`, not as
untrusted loose settings on an operation. Its canonical signed payload contains at least:

- Bundle schema version, target `agent_id`, and exact current credential generation.
- Source Git revision and a controller-issued per-Agent bundle generation.
- Capability enablement and sensitivity policy, concurrency limits, and other agent-enforced desired
  settings; never credentials or secret values.
- Canonical payload digest, signing key ID/algorithm, and controller signature. The digest covers the
  canonical payload excluding the signature itself.

The Git/Versioning path validates schema and cross-field rules before the controller signs the
immutable bundle. The bundle generation is monotonic for replay protection while the Git revision
may intentionally move to an earlier commit during rollback.
Restored clocks are not trusted for correctness, so wall-clock expiry is not the only replay gate.

The agent validates signature, digest, schema compatibility, target `agent_id`, credential generation,
and bundle generation before atomically applying a bundle. On success it sends a fenced apply
acknowledgement containing agent ID, session ID, source revision, generation, and digest. On failure it
keeps its last-known-good policy and returns a typed, redacted apply error with those identities. The controller
persists the ACK/error and projects desired-versus-applied state.

A capability descriptor declares whether dispatch is policy-sensitive. The controller dispatches
such work only when the current session has acknowledged the exact desired bundle revision and digest;
every AgentExplorer mutation and general work lease is policy-sensitive. The agent independently rejects
a request that its applied bundle does not enable. This double gate prevents a restart, delayed ACK,
or stale stream from opening capabilities under the wrong policy.

## AgentHub evolution

`AgentHub` remains a private gRPC API optimized for one long-lived, reverse-connected stream. REST
clients never receive agent credentials and never connect directly to an agent.

Protocol rules:

1. Existing proto field tags and enum numbers are immutable and never reused.
2. New optional fields and new `oneof` cases are appended. Unknown fields and messages are tolerated
   according to proto3 behavior.
3. The next conceptual extension is designed before tags are allocated. `AgentMsg` tag 7 is already
   reserved for D14 TeamCity service messages.
4. The controller gates every outbound message on negotiated support. A stale agent is allowed to
   remain connected and finish/recover supported work.
5. Every executable unit carries a stable work/operation ID, accepting session ID, deadline,
   cancellation identity, and idempotency rule.
6. Terminal outcomes are durable on the agent until acknowledged durably by the controller. Progress
   and high-volume observations may be best-effort only when their contract says so.
7. Stream replacement is atomic. Messages from a superseded session cannot commit ownership,
   progress that changes authoritative state, or terminal results.
8. Heartbeat cadence and payload are bounded. Capability catalogues and inventories are sent only on
   hello/change or explicit request.
9. Bulk or resumable bytes use authenticated HTTP/blob transfer on the same Kestrel host rather than
   unbounded gRPC messages.
10. Once releases exist, CI runs the current controller against at least the previous supported agent
    package. The compatibility window is explicit release policy, not an assumption.

### Mixed-version mode

An agent that lacks typed capability negotiation, explicit credential generation, general
work assertions, or policy-bundle ACK runs in explicit legacy mode:

- It may reconnect, be re-adopted, and finish an already owned legacy-compatible `BuildAssignment`.
  New assignments are drained by default; a published compatibility policy may explicitly permit a
  Build only when every required feature is proven supported by that exact agent version.
- It never receives AgentExplorer mutations, a general work/operation lease, or any policy-sensitive
  capability command. Absence of negotiation cannot be interpreted as implicit support.
- A long-lived physical agent remains visible with an upgrade-required/legacy badge and can finish
  supported work according to drain policy.
- A provider-managed agent must upgrade, negotiate capabilities, bind to its `agent_id`, and
  acknowledge the current policy bundle before the provider readiness barrier can pass.

The current `Hello.running_build_id` assertion evolves additively. A new active-work assertion carries
typed work kind, stable work ID, attempt/fencing identity, local phase, and pending-terminal-result
state for Builds and future management Operations. During migration, a new agent sends both the new
assertion and `running_build_id` for a Build; values must agree. A new controller continues to honor
legacy `running_build_id` for build re-adoption, never reinterprets it as a general lease, and records
a reconciliation error on conflict. Existing field tags and semantics remain unchanged.

The protocol should use typed domain messages behind shared lifecycle envelopes rather than a generic
string command with arbitrary JSON. A generic envelope may share correlation, deadline, cancellation,
and result mechanics, but the capability payload remains typed and versioned.

## Internal Agent SDK

The first SDK is a source-level contract within `Vivarium.Agent`, plus shared test fixtures. It is not
a dynamic plugin loader or stable native ABI. Its purpose is to stop each capability from rebuilding
session, cancellation, logging, validation, and platform dispatch differently.

A capability module supplies:

- Its descriptor and platform probe.
- Typed request validation and bounded result production.
- An async handler receiving an operation context with agent ID, session generation, work ID,
  deadline, cancellation token, and a safe event/log sink.
- Sensitivity metadata and a redactor for diagnostic fields.
- Concurrency classification and any required exclusive-work lease.
- A cleanup hook that is safe after cancellation or process termination.
- Platform-specific implementations behind one portable contract.
- Conformance fixtures for success, partial access, permission denial, timeout, cancellation,
  duplicate request, and unsupported platform.

The SDK supplies:

- Fenced dispatch, deduplication, acknowledgement, cancellation, deadline, and terminal delivery.
- Bounded progress and diagnostic emission integrated with the Logs Expert's budgets.
- Typed platform/result errors; raw exception text is not a public contract.
- Temporary/work-directory allocation and safe path validation where required.
- Capability advertisement and controller negotiation.
- Test fakes for disconnect, reconnect, duplicate delivery, clock skew, and process restart.

Modules do not read controller desired configuration directly, mutate Agent identity or credentials, decide
caller authorization, write arbitrary audit records, or call provider APIs.

## Physical-first enrollment and deployment

An enrolled physical machine is the reference deployment. A hand-managed VM follows the same path.
Provider-managed guests add host-verified identity and lifecycle handoffs, but do not redefine setup.

### First-run journey

1. An authenticated administrator opens Downloads or requests an enrollment package through REST.
2. The controller creates a short-lived, single-use enroll token and stamps a per-RID package with
   `bootstrap.json`: controller URL, pinned certificate fingerprint, and machine kind. Persistent
   agent identity and credentials are never stamped into the package.
3. Before executing any downloaded byte, the operator authenticates it using the D21-approved SPKI
   pin or an independently obtained package digest. An enroll token alone is not package authenticity.
4. The platform installer places bootstrap/config in explicit install and data directories and starts
   it in service or interactive/logon mode. Autologon and elevated UI-test duties are separate,
   explicit platform setup choices.
5. Bootstrap uses pinned TLS, obtains a verified agent package, and launches the agent.
6. The agent creates a persistent GUID candidate and appears in the controller as connected but
   unauthorized. On fresh enrollment this becomes its stable `agent_id`; it receives no work yet.
7. An administrator authorizes the Agent. The controller delivers its persistent token over the live
   session, establishes the first credential generation, and the agent stores that credential with
   restrictive platform permissions. Re-enrollment of an existing `agent_id` requires a distinct,
   audited controller-issued reclaim proof rather than merely presenting the GUID again.
8. The agent reconnects/authenticates, advertises capabilities and facts, receives the applied Git
   policy bundle, acknowledges its exact revision/digest, and becomes schedulable only after
   authorization, enablement, health, credential/session fencing, reconciliation, compatibility, and
   idle checks pass.

The Admin/SuperUser Expert owns the surrounding first-login experience and authorization wording; the
UI and REST Experts own their surfaces; the Platform Expert owns stock-OS install/service mechanics;
this stream owns the state transitions and security contract connecting them.

### Provider-managed guests

Pool/cloud agents use the same binary and session. Auto-authorization additionally requires
host-verified provider facts and a per-instance nonce as defined by D7. `image_id` alone is never
identity. The provider reserves the controller-owned `agent_id`; the guest does not choose it.
Readiness after start/restore requires a newer connection generation, no ghost work, an idle Agent,
completed version negotiation, the current credential generation, verified ProviderInstance
attachment, and an ACK for the exact desired policy revision/digest.

Snapshot, clone, power, destroy, and console remain provider operations. A restored checkpoint may
contain an older agent; the post-restore handshake upgrades the process before the provider marks the
  Agent ready. That upgrade must not require a guest reboot.

## Agent packaging and upgrades

The controller is the distribution point for self-contained, single-file agent/bootstrap packages for
every supported RID. A release manifest identifies immutable package bytes by version, RID, SHA-256,
size, and URL. Release/channel policy is declarative Git-backed configuration; package bytes live in
the authenticated controller store and are referenced by digest rather than committed to Git.

The D19 public installation template has one portable tree, with `.exe` suffixes on Windows:

```text
viv-agent-update[.exe]
bootstrap.json.sample
agent/current/viv-agent[.exe]
agent/version
```

`bootstrap.json.sample` contains placeholders only; enrollment will stamp a separate
`bootstrap.json` without modifying release bytes. ZIP entries are sorted, carry one canonical
timestamp, reject ambiguous/traversal paths, and mark only known executables as executable. Every
Server release embeds these four public templates under `packages/agents/` for future Downloads and
installer flows. They are intentionally distinct from the child-only packages under
`agent-packages/`: D30 Bootstrap activation accepts an archive with `viv-agent[.exe]` at its root, and
the colocated schema-v1 catalog binds exactly one such package per RID to the Server version.

Implemented per-Agent upgrade lifecycle (fleet pacing/channel policy remains future work):

1. An authorized request selects an Agent or rollout scope. The controller resolves the only valid
   target: the Agent package for the observed RID from the currently running Server release. Future
   channel policy decides when and in what order Agents move, not which arbitrary package bytes to run.
2. The controller drains the agent: no new exclusive work is assigned. An active build is allowed to
   finish unless an explicit, separately authorized stop action says otherwise.
3. `RestartAgent` asks the agent process to exit. Bootstrap fetches the authenticated manifest over
   pinned TLS and downloads the matching RID package. Creation is rejected unless the Agent's current
   Hello negotiates `vivarium.bootstrap-supervisor.v1` from a live launcher lease.
4. Bootstrap verifies digest and package structure, stages it outside `current`, and activates it
   atomically while retaining a last-known-good package. It binds both manifest reads to the exact
   local prior digest and rejects an expired or changed directive before changing active state.
5. The new agent connects with a new session ID and exact operation/RID/digest, negotiates protocol and
   capabilities, completes reconciliation, and remains under a bounded probation interval.
6. The controller accepts that exact session; the Agent durably writes `ready`; bootstrap durably
   replies `promoted`; the Agent confirms health; the controller durably enters `COMMIT_PENDING`; the
   Agent durably writes `committed` and confirms it. The controller then durably enters `FINALIZING`,
   returns a recorded receipt, and the Agent durably writes and confirms `server-confirmed`. Only that
   final receipt releases the drain; a crash or reconnect in either commit phase resumes the exact
   phase rather than guessing that the other side persisted it.
7. Deadline, candidate exit, invalid candidate identity, or post-handoff cancellation requests an LKG
   rollback. Bootstrap positively terminates the candidate and launches the exact prior digest with the
   same operation ID; only controller observation of that prior generation records `ROLLED_BACK` and
   releases the drain. The prior digest and starting generation are rebound atomically from the exact
   live reconciled Hello at handoff. A retry is a new fenced operation after this terminal result.

Bootstrap converts the authenticated manifest's bounded remaining duration into a local deadline and
also measures it with a monotonic watchdog. It verifies every persisted schema-2 package against its
original receipt, fails closed on lost initialized state, keeps only active/fallback/pending package
directories, and reserves disk space before download/extraction. A child that cannot be positively
terminated within the bounded escalation window is reported through the Agent-scoped bootstrap failure
endpoint; that report is durable across bootstrap restart and blocks relaunch until an idempotent
controller acknowledgment. The same operation/failure in a candidate Hello independently prevents
health progression. The operation becomes visibly `FAILED` while its maintenance drain remains held.
Controller session outboxes are bounded; a non-reading Agent is fenced on overflow instead of
accumulating retry messages without limit.

Agent development rebuilds a complete Server release bundle and rolls its Agent component to a canary.
This preserves the Server/Agent release contract in development and production. A hidden, explicitly
enabled raw publication surface is reserved for integration fixtures; it is not exposed by `viv-cli`, REST
OpenAPI, or the panel.

The D30 implementation remains change-controlled. Do not declare it frozen until the remaining
bad-download/interrupted-activation and Windows process evidence, plus D21 authenticated installer
evidence, pass.

## Git-backed desired configuration

Declarative agent changes are commits, not mutable controller rows. The Git/Versioning Expert owns the
repository/worktree/commit workflow, conflict handling, and author identity. This stream defines the
agent-facing schema and reconciliation behavior for settings such as:

- Display name and custom scheduling parameters.
- Enabled/disabled intent and maintenance/drain policy.
- Allowed AgentExplorer capabilities and sensitive-data policy.
- Agent release channel/version pin and rollout group.
- Expected platform/machine-kind assertions and installer mode where declarative.

The controller validates a candidate revision before applying it and records the applied commit hash.
REST and UI mutations create Git changes through the shared versioning service; they do not patch the
runtime projection directly. The reconciler converges the agent toward the committed intent and
reports `desired_revision`, `applied_revision`, and a reason when they differ.

For agent-enforced settings, reconciliation compiles the revision into the canonical
`AgentPolicyBundle`, validates and signs it, and persists its revision, generation, digest, and
signature metadata before delivery. A policy-sensitive capability is not eligible until the current
session ACKs that exact bundle. An apply error leaves the last-known-good bundle active and makes the
machine visibly unreconciled; it never falls open to defaults.

Not every state belongs in Git. Credentials, enrollment tokens, connection/session state, heartbeat
timestamps, facts/inventory snapshots, active leases, work progress, terminal results, and one-time
authorize/cancel/restart actions are operational records. They are journaled and audited, never
committed with secrets or high-frequency churn.

## REST boundary

The Vivarium REST Expert owns public HTTP resource names, versioning, status codes, idempotency keys,
pagination, filtering, OpenAPI, and client generation. The Agent API/SDK stream supplies canonical
application commands and projections for:

- Agents, credential lifecycle, and effective status.
- Capabilities and availability reasons.
- Enrollment-package requests and enrollment progress.
- Desired-versus-applied configuration revision.
- Upgrade/drain/restart actions and progress.
- Capability operation submission, observation, cancellation, and terminal outcome.

REST calls application services; application services persist intent or work before sending an
`AgentHub` message. A successful HTTP response never means an unpersisted stream write happened. Live
updates may use a REST-compatible event surface chosen by the REST/UI experts, while durable reads
remain resumable from controller state.

The REST layer does not proxy raw gRPC messages, expose `auth_token`, accept arbitrary capability IDs
with untyped payloads, or create a direct browser-to-agent channel.

## Audit and logging boundary

Agent lifecycle and operations emit semantic events to the controller log/audit sink. The Logs Expert
owns event encoding, sinks, retention, rotation, and volume budgets. At minimum an audit event can
identify:

- Timestamp, actor or system principal, action, target agent/machine, and owning domain.
- Request/correlation ID, operation/build ID, accepting session generation, and outcome.
- Git desired/applied revision for settings-driven behavior.
- Agent/controller version and capability ID when relevant.
- Bounded reason/error class and whether sensitive fields were redacted.

Enrollment-token values, agent credentials, environment values, secret arguments, package bearer
tokens, and unrestricted process command lines never enter ordinary logs. Heartbeats do not produce
one durable audit record per tick. State transitions and anomalies are logged; repetitive healthy
signals are aggregated as metrics or sampled diagnostics.

## Concurrency and reconciliation

The controller is authoritative for assignment and operation ownership. The agent is authoritative for
what the current process can observe and for a durably retained terminal result until acknowledgement.

- Session generation fences every work transition.
- Side effects and leases target the stable `agent_id`; delivery is additionally fenced to its current
  credential generation and session.
- Builds and mutating management operations share a capacity-one exclusive arbiter per physical
  agent unless a future capability explicitly proves a different safe capacity.
- Read-only observations declare cost and overlap behavior; expensive snapshots can be throttled or
  rejected while a build is latency-sensitive.
- Disable/drain prevents new work but does not implicitly cancel current work.
- Cancel is an idempotent action whose intent is durable before delivery and is replayed after
  reconnect.
- A provider restore/start transition waits for a newer reconciled session and cannot mark the machine
  ready from a stale TCP connection or before capability negotiation and exact policy ACK.
- Disconnection retains ownership during the bounded reconnect lease. Expiry produces the domain's
  infrastructure-failure outcome rather than silent reassignment beside ghost work.

The Reconciliation Lead reviews desired/applied convergence, crash windows, retry rules, and provider
handoffs for every new mutating capability.

## Security boundaries

- TLS server identity is pinned because restored VM clocks may invalidate ordinary certificate date
  checks. Pin validation never means accepting any certificate.
- Agent credentials prove Agent identity; authorization, enablement, health, policy, and caller
  roles independently determine eligibility.
- Deleting an Agent revokes its credential. Unauthorizing it changes scheduling permission but
  does not break an in-flight authenticated artifact upload.
- Enrollment tokens are hashed server-side, short-lived, single-use, and insufficient to authenticate
  downloaded installer bytes. An enrollment-proof session stays unauthorized and may only receive its
  new bearer; the Agent durably stores it and immediately reconnects, and only that bearer Hello
  consumes the proof and enables scheduling.
- Agent secret files use restrictive platform permissions. Package digests and controller pins are
  compared in constant, canonical representations.
- Agent operations run with the minimum privilege consistent with their declared capability. Elevated
  interactive automation is an explicit machine setup, not an implicit property of all agents.
- Inputs, archive paths, file paths, process arguments, output sizes, and deadlines are validated at
  the capability boundary.

## Required evidence and release gates

### Every protocol or capability change

- Proto compatibility review: no reused tags/enum numbers and documented stale-peer behavior.
- Tier-1 tests for validation, negotiation, availability reasons, deduplication, cancellation, and
  error mapping.
- Tier-2 real-session tests for reconnect, stream replacement, controller restart, agent restart,
  duplicate delivery, durable terminal acknowledgement, and bounded logging.
- Platform conformance evidence for each advertised OS, including partial access and permission
  denial. Absence is advertised honestly on unsupported hosts.
- Security tests for malformed/oversized inputs and sensitive-field redaction.
- Git/REST/audit tests proving that declarative changes carry a revision and cannot bypass the shared
  mutation path.
- Capability-axis tests prove that support/version, policy enablement, caller authorization, runtime
  eligibility, and request outcome/completeness remain independent. `permission_denied` and partial
  results do not remove capability support.
- Identity tests enforce one current credential/session per `agent_id`, safe credential replacement,
  and stable Agent targeting across reconnect, reinstall, and retained history.
- Policy-bundle tests cover canonical digest/signature validation, wrong target/credential generation, replayed
  generation, exact ACK gating, apply error, and last-known-good retention.
- Mixed-version tests cover additive `running_build_id` evolution, supported Build completion, denial
  of AgentExplorer/general leases, and provider upgrade-before-readiness.

### Enrollment and upgrade slice

- Stock supported OS: authenticated package before execution, install, first contact, unauthorized
  visibility, explicit authorization, restrictive token persistence, restart, and uninstall guidance.
- Invalid/expired/reused enroll tokens and deleted Agents fail safely.
- Bad digest, truncated package, interrupted activation, crash before health acknowledgement, and
  last-known-good recovery are exercised.
- A busy physical agent drains without losing its build; a disabled agent remains disabled after
  restart; an offline agent converges when it reconnects.
- A provider checkpoint containing a stale agent reconnects with a newer generation, upgrades without
  reboot, negotiates/binds/ACKs policy before readiness, and cannot race a new assignment.
- Once a prior release exists, the current controller passes tier-2 against its supported previous
  agent package and the current agent passes against the supported previous controller if that
  direction is promised by release policy.

## Non-goals for the first implementation

- Dynamic third-party agent plugins or an out-of-process capability marketplace.
- A generic unrestricted remote shell, interactive terminal, remote file editor, or software manager.
  Their placeholder capabilities do not allocate wire contracts until their product/security designs
  are accepted.
- Provider snapshot/power/clone/console implementation.
- Making raw environment, process arguments, or filesystem content persistent searchable inventory.
- Multi-agent capacity on one physical host; baseline capacity is one exclusive work item.
- Silent autologon, security-control changes, or privilege escalation during enrollment.
- Storing package binaries, credentials, runtime state, or high-frequency observations in Git.
- Treating the D30 bootstrap as frozen before its remaining cross-platform and installer evidence exists.

## Open questions

1. **Resolved by D30 for post-authorization updates:** bootstrap launches the authenticated seed and
   makes no manifest request before `data/auth.token` exists; thereafter it reuses the Agent bearer
   only in same-origin pinned-TLS headers. D21 installer-byte authentication and the remaining
   cross-platform failure evidence still close the final freeze gate.
2. What controller/agent compatibility window will releases promise: current plus N-1, or a longer
   window for rarely connected physical machines?
3. Should capability negotiation use one general typed operation envelope or separate observation and
   mutation envelopes with different durability guarantees?
4. Which inventory is cached durably, for how long, and which sensitive fields remain live-only?
5. **Resolved for the first per-Agent operation by D30:** exact newer-generation reconciliation plus
   controller acceptance, atomic marker write, and Agent confirmation within the bounded operation
   deadline. Early exit/health timeout rolls back once; unexpected state fails closed and stays drained.
6. Can release pins be permanent, or must unsupported pins automatically disable scheduling after an
   announced compatibility horizon?
7. Which service/logon installation modes are supported on stock Windows, systemd Linux, and macOS,
   and how are interactive UI-test duties represented without weakening headless agents?
8. What durable operation cursor is required before expected-reboot, software management, and other
   reconnecting mutations can be accepted?
9. How are capability-level output budgets negotiated for large process lists, network endpoints,
   metrics streams, and future file transfers?
