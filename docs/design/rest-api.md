# Vivarium REST Management API

> Status: **Accepted**
> Implementation: **Partial — reads, Agent enablement/deployment, build/blob mutations, build SSE, and CLI flows implemented**
> Maintainer role: [Vivarium REST Expert](../roles/vivarium-rest-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D4, D7, D8, D14, D22-D30

## 1. Purpose

Vivarium needs one automation-grade management surface from day one. The React panel, `viv-cli`,
CI integrations, operators, and third-party clients should manage both product domains through the
same REST API:

- **TeamCity:** projects, build configurations, builds, queue, cancellation, artifacts, and results.
- **AgentExplorer:** agents/hosts, inventory, status, processes, network endpoints, and later remote
  files, commands, software, and state management.

REST is not a replacement for the existing execution and bulk-data transports:

```text
Browser / viv-cli / CI / integrations
             |
             v
         REST /api/v1
             |
             v
   Controller application services
       |                  |
       v                  v
Git desired state   SQLite runtime state
       |                  |
       +--------+---------+
                |
                v
        AgentHub gRPC session  <--- reverse-connected agents

Payloads and artifacts <----> authenticated /blobs/{sha256}
```

The API must make retries, concurrent edits, Git revisions, long-running work, cancellation, audit,
and backward compatibility explicit rather than adding them after clients already depend on
ambiguous behavior.

## 2. Current and target state

### Current

- Agents use one bidirectional gRPC `AgentHub.Session` stream for hello, heartbeat, assignment,
  cancellation, log chunks, and terminal result handshakes.
- Payload and artifact bytes use authenticated `GET/PUT /blobs/{sha256}` with server-side hash
  verification and staging/assignment/artifact-reference authorization.
- The live `viv-cli login` / `viv-cli run` / `viv-cli cancel` build flow uses REST/SSE: it creates a project-owned
  upload plan, uploads only required blobs, submits idempotently, resumes build events by event ID, and
  reads the authoritative build resource. The gRPC `ControlPlane` remains a frozen compatibility
  adapter and still carries legacy list/authorize methods pending their REST equivalents.
- Build submission already has a client request ID and durable idempotency behavior.
- A transport-independent kernel now supplies stable named/legacy principals, correlation/request
  context, one built-in-role permission evaluator, versioned SQLite migrations, and an append-only
  audit journal beneath ControlPlane, REST, panel, and blob handlers.
- The Blazor panel calls in-process services and therefore does not prove that external clients can
  perform the same management operations.
- Scoped legacy tokens still distinguish agent, submit, and admin concerns. The initial Git-backed User
  and direct built-in RoleBinding schema is implemented for first-run activation; groups, service
  accounts, PATs, custom roles, project hierarchy, and general administration resources are not.
- `/api/v1/system`, `/agents`, `/agents/{id}`, `/audit-events`, `/builds`, `/builds/{id}`, and
  `/queue` are implemented with shared authentication, Problem Details, bounded keyset cursors,
  conditional ETags, explicit filters, and deterministic OpenAPI at `/openapi/v1.json`.
- Agent collection/detail reads now project current negotiated capabilities, typed static operating
  system/package facts, observation quality/freshness, and distinct current-versus-observation
  credential/connection generations. `/api/v1/agents/{id}/facts` preserves legacy Agents as explicit
  unknown observations rather than fabricating typed values.
- `/api/v1/agents/{id}/settings` GET/PUT implements the first desired-configuration subresource for
  `spec.enabled`. Reads return desired/applied state and a strong configuration ETag. Writes require
  `If-Match`, an `Idempotency-Key`, and an explicit boolean; they commit validated Git bytes before
  activation and distinguish missing preconditions, stale bases, validation/reconciliation conflicts,
  and repository unavailability. Exact idempotent replay returns the original semantic result without
  restoring superseded live state.
- `/api/v1/setup/status`, setup claim/operation/administrator/repository/changes/completion resources,
  and `/api/v1/recovery/claims` implement purpose-separated first-run and break-glass exchanges. Setup
  sessions cannot authenticate normal resources, and unexchanged recovery values cannot either.
  Completion atomically commits User + `SYSTEM_ADMIN`, reconciles that exact revision, and activates
  the private credential. A `Vivarium-Recovery` session authenticates normal APIs only while the
  host-explicit recovery state remains active.
- `POST /api/v1/blob-upload-plans`, staged blob PUT, `POST /api/v1/builds`, idempotent cancellation, and
  `/api/v1/events` now implement object-scoped upload authority and resumable durable build events with
  explicit retention-gap recovery. Build definitions continue to arrive as exact `vivarium.yaml`
  snapshots from the tested repository.
- Agent deployment now exposes immutable package collection/detail/publication resources and durable
  per-Agent upgrade operation create/list/detail/cancel resources. Operation reads include the durable
  phase history, drain ownership, dispatch generation/backoff, first cancellation reason, failure, and
  exact result digest. Cancellation means cancel-and-release only in `draining`; from `handoff-ready`
  onward the same idempotent resource requests rollback and retains the drain. A retry is a new POST
  after `rolled-back`, with a new fence and idempotency key. Publication requires exact digest plus
  principal-scoped idempotency and rehashes cached content before serving/reusing it. Bootstrap
  manifest/package routes are deliberately outside OpenAPI and accept only the matching Agent
  credential and operation; a manifest exposes no package during drain and returns an explicit
  `activate` or `rollback` directive after handoff. `viv-cli` consumes the public management resources for
  package publication and upgrade/status commands.
- General identity/RBAC management, all other desired-configuration mutations, generic AgentExplorer
  runtime operations, detailed result resources, and the React client remain planned.

### Target

- `/api/v1` is the canonical public management API for UI, CLI, CI, and integrations.
- Both TeamCity and AgentExplorer call the same controller application services from REST; transport
  handlers contain no domain logic.
- Desired settings and properties are represented in a configured Git repository and reconciled into
  runtime projections. REST writes create a commit or change branch/pull request and return that
  change and Git revision.
- Runtime commands, builds, authorization ceremonies, secrets, observed facts, and audit records are
  durable and audited but are not committed to Git.
- Every mutating endpoint has defined authorization, idempotency, concurrency, audit, and cancellation
  behavior.
- OpenAPI is published and treated as a reviewed compatibility artifact.
- The existing gRPC ControlPlane is a transitional implementation-era adapter, not a supported public
  target. The CLI build flow has migrated; the adapter remains frozen except for compatibility fixes
  until its remaining legacy consumers have REST parity, then is removed before the first supported
  public release. AgentHub gRPC remains.

This target specializes the REST-first management plane adopted in D24. Numbered architecture
decisions remain authoritative.

## 3. Invariants

1. **One domain implementation.** REST, a transitional gRPC ControlPlane, background reconciliation,
   and controller UI hosting call the same application services and enforce the same invariants.
2. **No direct storage access.** Clients and REST handlers never access SQLite, the Git working tree,
   blob files, or live session objects directly.
3. **Reverse connection stays.** Agent-backed requests are dispatched over the already established
   AgentHub stream. A REST request never makes the controller dial into a host.
4. **Bulk bytes stay out of JSON.** REST describes payloads and artifacts by hash, size, media type,
   and authenticated link; bytes travel through `/blobs/{sha256}`.
5. **Configuration is Git-backed.** A successful desired-state write identifies the resulting commit
   or pending change. Applying a write only to SQLite is a defect.
6. **Secrets are not configuration files.** Token plaintext, password plaintext, private keys, and
   secret values never enter Git. Git may contain stable secret references and non-secret metadata.
7. **Runtime history is immutable.** Builds retain exact definition and assigned-agent snapshots.
   Later Git or agent changes do not rewrite them.
8. **Retries are safe.** A client timeout and retry cannot create duplicate side effects.
   Authentication and object-level authorization are evaluated again on every replay; a stored
   response is not an authorization grant. Secret plaintext is never persisted for replay.
9. **Conflicts are visible.** A stale mutation fails; last-writer-wins is not accepted for settings.
10. **Every action is attributable.** Mutations, denials, Git changes, dispatch, cancellation, and
    terminal outcomes share correlation and actor information.
11. **Collections and streams are bounded.** Pagination, retention, and cursor expiry are part of the
    contract.
12. **Unavailable is not unsupported.** Agent capability, administrator policy, caller permission,
    live connectivity, and OS access failures remain distinguishable in REST representations/errors.
13. **A blob hash is not authority.** Logical staging, assignment, build ownership, or artifact
    visibility grants access to shared content-addressed bytes.

## 4. Protocol and representation conventions

### 4.1 Base URL and media types

- Public resources live under `/api/v1`.
- JSON requests use `Content-Type: application/json`.
- JSON responses use `application/json`; errors use `application/problem+json`.
- UTF-8 is mandatory.
- Property names are `camelCase`.
- Timestamps are RFC 3339 UTC strings with a `Z` suffix.
- Durations are ISO 8601 strings unless a domain field has an established integer unit in its name.
- IDs are opaque case-sensitive strings. Clients must not parse UUIDs or derive hierarchy from IDs.
- Enum strings are lowercase kebab-case. Clients must preserve or tolerate unknown future values.
- Optional means absent, not an undocumented sentinel. `null` is used only when it has a distinct,
  documented meaning.

Successful responses return the resource directly. Collection responses use a common envelope:

```json
{
  "items": [],
  "page": {
    "nextCursor": "opaque-or-null",
    "limit": 50
  }
}
```

Resources should include their own `id`, `url`, relevant relationship URLs, and the applicable
`configurationRevision`, `observationRevision`, or `runtimeRevision`. Those fields are not aliases
for one generic revision. Do not force clients to construct undocumented paths.

### 4.2 Resource naming

- Use plural lowercase nouns and kebab-case path segments: `/build-configurations`.
- Prefer shallow globally addressable resources after discovery. Nest only to express creation or a
  collection scoped by a parent.
- Use subresources for stateful requests such as cancellation; do not add `/doThing` RPC endpoints.
- Names and display names are mutable data, never URL identity.
- Do not expose database table names or protobuf message layout.

Representative resource map:

| Area | Resources |
|---|---|
| Common | `/api/v1/system`, `/operations`, `/events`, `/audit-events`, `/configuration-changes`, `/blob-upload-plans` |
| TeamCity | `/projects`, `/build-configurations`, `/builds`, `/queue`, `/test-occurrences` |
| AgentExplorer | `/agents`, `/agents/{id}/facts`, `/agents/{id}/environment`, `/agents/{id}/processes`, `/agents/{id}/network-endpoints` |
| Providers / images | `/providers`, `/provider-hosts`, `/provider-instances`, `/pools`, `/images`, `/image-versions` |
| Administration | `/users`, `/groups`, `/roles`, `/tokens`, `/enrollment-tokens` |
| Bulk data | Existing authenticated `/blobs/{sha256}` outside JSON API versioning |

Illustrative routes, subject to owning-expert review:

```text
GET    /api/v1/projects
GET    /api/v1/projects/{projectId}
GET    /api/v1/projects/{projectId}/build-configurations
GET    /api/v1/build-configurations/{configurationId}
POST   /api/v1/builds
POST   /api/v1/blob-upload-plans
GET    /api/v1/builds/{buildId}
GET    /api/v1/builds/{buildId}/children
PUT    /api/v1/builds/{buildId}/cancellation
GET    /api/v1/builds/{buildId}/artifacts
GET    /api/v1/queue

GET    /api/v1/agents
GET    /api/v1/agents/{agentId}
GET    /api/v1/agents/{agentId}/facts
POST   /api/v1/agents/{agentId}/inventory-refreshes
GET    /api/v1/agents/{agentId}/processes
GET    /api/v1/agents/{agentId}/network-endpoints
GET    /api/v1/agents/{agentId}/environment
PUT    /api/v1/agents/{agentId}/authorization

GET    /api/v1/provider-instances
GET    /api/v1/provider-instances/{providerInstanceId}

GET    /api/v1/operations/{operationId}
PUT    /api/v1/operations/{operationId}/cancellation
GET    /api/v1/events
GET    /api/v1/audit-events
```

AgentExplorer and agent administration use the same stable `agentId` identity. Enrollment creates or
securely reclaims an Agent; authorization, credential lifecycle, desired settings, observations, and
operations are subresources or actions of that Agent. A connection session is diagnostic runtime state,
not a second public fleet resource.

`Files`, `command-executions`, software inventory/mutation, and process control are not exposed as
dummy endpoints before the corresponding Agent API capabilities and safety contracts exist. The UI
may display planned sections from static product metadata; an endpoint that always returns “not
implemented” is not a useful contract.

### 4.3 Errors

Errors follow RFC 9457 Problem Details and add stable machine-readable Vivarium fields:

```json
{
  "type": "https://vivarium.dev/problems/revision-conflict",
  "title": "The resource revision is stale",
  "status": 412,
  "detail": "The agent settings changed after they were loaded.",
  "instance": "/api/v1/agents/agent-17/settings",
  "code": "revision_conflict",
  "correlationId": "01K...",
  "retryable": false,
  "currentConfigurationRevision": "git:8ac21f...",
  "errors": [
    { "path": "customParameters.lab", "code": "changed", "message": "Value changed." }
  ]
}
```

Rules:

- `code` is stable within `/api/v1`; `title` and `detail` are human-facing and not control flow.
- Validation errors use `422`; malformed JSON uses `400`.
- Missing/invalid authentication uses `401`; insufficient permission uses `403`.
- Missing resources use `404`. A resource hidden by policy may also return `404`.
- State conflict or idempotency-key/body mismatch uses `409`.
- Missing required `If-Match` uses `428`; stale `If-Match` uses `412`.
- Rate/capacity limits use `429` with `Retry-After`; temporary service failures use `503`.
- `retryable` states whether the same semantic request may be retried after delay. It does not waive
  idempotency requirements.
- Problem details never contain stack traces, tokens, environment values, command-line secrets, or
  raw request bodies.

### 4.4 Idempotency

`Idempotency-Key` is required for non-idempotent requests that create side effects, including build
submission, inventory refresh, future remote command execution, token creation, and configuration
change creation.

- The uniqueness scope is authenticated principal + method + canonical path + key.
- The authenticated principal is a stable user/service-account subject, not a replaceable token ID,
  so credential rotation does not create a second operation.
- Authentication, endpoint permission, and object-level authorization are re-evaluated before the
  idempotency record is read or replayed. A caller whose access was revoked receives the current
  `401`, `403`, or hiding `404`, not the old successful response.
- Except for one-time secrets, the controller stores a hash of the effective request and the terminal
  semantic response durably before acknowledging success. Volatile headers such as `Date` and the new
  request's correlation ID are regenerated.
- Repeating the same key and same effective request returns the original semantic status and body only
  after the current authorization check succeeds.
- Reusing a key for different content returns `409 idempotency_key_reused`.
- Keys survive controller restart and are retained at least as long as a client may safely retry; the
  exact minimum retention is an open operational decision and must be published in `/api/v1/system`.
- Build submission uses a durable composite key `(requestScope, clientRequestId)`, where
  `requestScope` is the stable authenticated principal ID and `clientRequestId` is the
  `Idempotency-Key`. Different principals may safely use the same key. Existing unscoped build rows
  migrate to the reserved `legacy-control-plane` scope; transitional gRPC lookups remain in that
  scope, and REST never claims or replays them. Existing builds remain addressable by build ID, but
  idempotency replay deliberately does not cross the old gRPC/new REST transport boundary.
- Naturally idempotent `GET`, `HEAD`, `PUT`, and `DELETE` semantics still apply. A cancellation
  subresource is `PUT` so duplicate cancel requests preserve the first accepted reason and do not
  create multiple actions.

An idempotency key prevents duplicate execution. It is not an optimistic-concurrency token and does
not replace `If-Match`.

Secret-producing endpoints are a documented exception to response replay. Token and enrollment-token
creation commits the credential hash, resource metadata, request hash, and idempotency receipt in one
transaction, then includes plaintext only in the first response attempt. Plaintext is
never stored in the idempotency record, database, Git, audit, or ordinary logs. A retry returns the
same resource ID and metadata with `secretDelivery: "not-repeatable"` and no secret; it never creates
or rotates another credential. If the first response was lost, the safe recovery is to revoke that
resource and create a replacement with a new idempotency key. OpenAPI marks the plaintext field as
one-time and documents this recovery path.

### 4.5 Revisions and optimistic concurrency

Vivarium has three independent revision domains:

- `configurationRevision` identifies Git-backed desired state and has the form `git:<commit>` in JSON.
- `observationRevision` identifies an agent-reported inventory/status snapshot. A heartbeat or refresh
  may advance it without changing configuration.
- `runtimeRevision` identifies controller-owned workflow state such as a build or operation transition.

Each mutable subresource returns an opaque `ETag` for exactly one documented domain and the matching
named JSON field. Desired settings are read and edited through configuration-specific subresources,
for example `/agents/{id}/settings`; their `ETag` compares only `configurationRevision`. Inventory
subresources return an observation ETag. Builds and operations return a runtime ETag. Aggregate
resources may contain all three named revisions, but they are read-only aggregation points and do not
offer an ambiguous aggregate `If-Match` mutation.

Clients send `If-Match` for desired-state updates and destructive operations whose meaning depends on
observed state. Missing preconditions return `428`; a stale precondition returns `412` with the
current revision from that same domain but not a sensitive current representation. A new heartbeat
therefore cannot conflict with an agent settings edit, and a Git settings commit cannot pretend that
a process snapshot is still current.

Runtime actions also carry domain-specific fencing. Future process control identifies a process by
PID plus observed start time and requires the `observationRevision`/ETag of the process snapshot, so
PID reuse cannot terminate a different process. Build and operation cancellation uses the owning
resource's `runtimeRevision` when a precondition is required. Immutable resources, such as a finished
build snapshot or blob, may use a content-derived ETag.

### 4.6 Pagination, filtering, sorting, and projection

All collections use cursor pagination:

- `limit` defaults to 50 and is capped at 200.
- `cursor` is opaque, signed or integrity-protected, and bound to principal, filters, sort order, and
  snapshot semantics.
- Default sort is stable and documented per resource, always ending in ID as a tie-breaker.
- A missing `nextCursor` means the end. An expired cursor returns `410 cursor_expired`.
- Offset pagination is not exposed because live queues and fleets make it duplicate or skip rows.

MVP filtering uses explicit documented query parameters, not arbitrary SQL and not a prematurely
general expression language. Examples include:

```text
GET /api/v1/agents?search=berlin&connected=true&osFamily=windows&capability=agent-explorer.processes.v1
GET /api/v1/builds?projectId=core&status=running&agentId=agent-17
GET /api/v1/audit-events?actorId=user-1&targetType=agent&from=...&to=...
```

Repeated parameters mean OR within one field; different fields mean AND. Unsupported filters return
`400 unsupported_filter`, never silently fall back to an unfiltered scan. `sort` uses an allowlisted
comma-separated field list with `-` for descending. Field projection or expansion, if added, must be
allowlisted and bounded; it may not turn one request into unbounded joins.

### 4.7 Blob discovery, staging, and object authorization

Content addressing deduplicates bytes; it does not grant access to them. The target keeps physical
bytes global by SHA-256 while recording logical grants and references separately.

The REST equivalent of `MissingBlobs` is:

```text
POST /api/v1/blob-upload-plans
Idempotency-Key: <key>

{
  "projectId": "project-1",
  "blobs": [
    { "sha256": "<64 lowercase hex characters>", "size": 12345 }
  ]
}
```

It creates a bounded, expiring staging resource owned by the authenticated principal and project:

```json
{
  "id": "stage-01K...",
  "expiresAt": "2026-08-28T12:30:00Z",
  "items": [
    {
      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "size": 12345,
      "uploadRequired": true,
      "uploadUrl": "/blobs/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    }
  ]
}
```

`uploadRequired: false` is returned only when this principal/project already has a logical grant and
the bytes are present. If the same hash exists for another project but the caller has no grant, the
response still requires upload; the server verifies and drains the bytes without storing a duplicate,
then grants the staging reference. This prevents hash probing from revealing another project's
content while retaining physical deduplication.

A submit/CLI `PUT /blobs/{sha256}` supplies the non-secret staging ID in
`X-Vivarium-Blob-Staging-Id`. The controller requires current project upload permission, matching
principal ownership, an unexpired plan entry, declared size limits, and a body whose hash matches the
path. A build submission may reference only hashes granted to its staging resource; accepting the
build atomically consumes or attaches those grants and creates immutable build payload references.
Supplying a raw hash without a grant is rejected even when bytes exist.

Agent transfers use a different grant:

- Payload `GET /blobs/{sha256}` requires an agent credential plus `X-Vivarium-Build-Id` and the current
  fenced `X-Vivarium-Session-Id`. The build must be durably assigned/re-adopted to that agent/session,
  and the hash must occur in its immutable assignment.
- Artifact `PUT /blobs/{sha256}` requires the same current build ownership and an artifact-upload
  staging record for that build. A reconnect may continue only after D4 re-adoption establishes the
  new fenced session. Cancellation does not prematurely block the owning agent from uploading the
  terminal result/artifacts during the bounded result-acknowledgement window.
- Accepting the terminal artifact manifest creates immutable build/artifact references. Unattached
  staged uploads expire and become eligible for garbage collection after the grace window.

Human artifact metadata returns a content link of the form
`GET /build-content/{buildId}/artifacts/{artifactId}` rather than granting generic access by SHA-256.
This data-plane route resolves current build visibility and immutable artifact ownership before
streaming the underlying blob; it carries no credential in its URL and uses the caller's normal cookie
or bearer authentication. Raw management-principal `GET /blobs/{sha256}` by guessed hash is not
supported.

The implemented data plane now uses these logical staging, build-reference, assignment, and artifact
grants. Their changes use the controller's serialized durable writer so submission, cancellation, and
result acceptance cannot race authority. Retention reference counting and garbage collection remain
future work; current object isolation does not claim multi-user/RBAC completion.

## 5. Git-backed desired-state mutations

### 5.1 Boundary

The following are desired configuration and must be Git-backed from their first implementation:

- Projects and build-configuration definitions owned by the controller.
- Agent display names, enabled/disabled desired state, custom parameters, tags, and policy.
- AgentExplorer collection and operation policy, including what sensitive inventory may be read.
- Machine providers, pools, image recipes/references, scheduling policy, and retention settings.
- Users, groups, project role assignments, and non-secret token policy metadata.
- System configuration that is safe and meaningful to review as text.

The following are not committed to Git:

- Token plaintext or hashes, passwords, private keys, enrollment proofs, and secret values.
- Connection, heartbeat, health observation, queue occupancy, build progress, agent inventory, and
  metrics.
- Builds, runtime operations, results, logs, audit records, and idempotency records.
- One-time authorization/enrollment ceremonies and emergency SuperUser bootstrap events.

Authorization is deliberately split: Git may state that a known Agent is desired/authorized
and which policy applies, but credential issuance/revocation remains a security operation in the
durable credential store and audit journal. The User Roles, Admin/SuperUser, Git/Versioning, and
Reconciliation experts must ratify the exact atomic transition before implementation.

`vivarium.yaml` in the tested source repository remains the authoritative submitted build snapshot
under D17. A future controller catalog may index repository-backed configurations, but REST must not
silently rewrite a product repository without an explicit configured Git workflow.

### 5.2 Public change model

A desired-state REST edit creates a `configuration-change` rather than mutating a SQLite projection:

```json
{
  "id": "chg-01K...",
  "state": "awaiting-merge",
  "mode": "pull-request",
  "baseConfigurationRevision": "git:12ab34...",
  "headConfigurationRevision": "git:78cd90...",
  "branch": "vivarium/change/chg-01K...",
  "pullRequestUrl": "https://example.invalid/changes/42",
  "targets": [
    { "type": "agent-settings", "id": "agent-17" }
  ],
  "createdBy": { "type": "user", "id": "user-1" },
  "createdAt": "2026-08-28T12:00:00Z",
  "appliedConfigurationRevision": null,
  "validation": { "state": "succeeded", "errors": [] },
  "url": "/api/v1/configuration-changes/chg-01K..."
}
```

Repository policy selects one of two workflows:

- **Direct commit:** validate, commit to the configured branch, then reconcile that commit.
- **Reviewed change:** validate and commit to a dedicated branch, then create a pull request when a
  provider integration exists. In offline/local installations, the branch itself is the reviewable
  change and can be merged with ordinary Git tooling.

Resource-specific `PATCH`/`PUT` endpoints may provide convenient editing, but they must accept
`If-Match`, commit metadata, and change mode, and return `202 Accepted` with the resulting
`configuration-change`. They are adapters over the same Git transaction, not direct mutations.
Batch changes use `POST /api/v1/configuration-changes` with typed patch operations so related edits
produce one reviewable commit.

The commit records stable actor identity, correlation ID, and change ID in structured trailers. A
client-supplied display name or email is not trusted as identity. Commit messages are required,
bounded, and sanitized; commit signing policy belongs to the Git/Versioning Expert.

### 5.3 Apply and conflict semantics

1. Resolve and validate the caller's `baseConfigurationRevision`/`If-Match`.
2. Materialize the proposed change without editing the active projection.
3. Validate schema and cross-resource invariants using the same application services that reconcile
   externally authored commits.
4. Commit to the configured branch or change branch.
5. Return the change and its `headConfigurationRevision` after the commit is durable.
6. Reconcile only merged/configured-head commits into the runtime projection.
7. Mark the change `applied` only after projection succeeds; expose
   `appliedConfigurationRevision` and any reconciliation failure without rewriting Git history.

If HEAD moved, return `412` before commit or create an explicit conflict state; never auto-merge a
semantic configuration conflict silently. External commits are first-class: the reconciler validates
and applies them through the same pipeline, attributes the Git author/committer plus system actor,
and emits the same audit/event records.

Rollback is a new revert commit or reviewed change. REST never force-resets or rewrites shared Git
history.

## 6. Asynchronous work and cancellation

Long-running or agent-backed requests return `202 Accepted`, a `Location` header, and an operation:

```json
{
  "id": "op-01K...",
  "kind": "agent-inventory-refresh",
  "state": "running",
  "target": { "type": "agent", "id": "agent-17" },
  "createdBy": { "type": "user", "id": "user-1" },
  "createdAt": "2026-08-28T12:00:00Z",
  "startedAt": "2026-08-28T12:00:01Z",
  "finishedAt": null,
  "cancellable": true,
  "progress": { "message": "Waiting for agent response" },
  "result": null,
  "error": null,
  "correlationId": "01K...",
  "runtimeRevision": "runtime:7",
  "url": "/api/v1/operations/op-01K..."
}
```

Operation states are `queued`, `running`, `cancel-requested`, `succeeded`, `failed`, `cancelled`, and
`expired`. Terminal operations are immutable except for retention metadata.

`PUT /api/v1/operations/{id}/cancellation` is idempotent. Cancellation is a durable request, not proof
that work has stopped. The operation first becomes `cancel-requested`; only a terminal agent/domain
result makes it `cancelled` or otherwise terminal. Unsupported or too-late cancellation returns a
stable conflict problem. Restart restores pending operations and re-dispatches or reconciles them
under fencing rules.

Builds remain TeamCity resources rather than generic operations. Build submission returns the build
or matrix parent; `PUT /builds/{id}/cancellation` records the same durable cancellation intent as the
existing controller path. A matrix-parent cancellation atomically applies TeamCity semantics to its
children. Generic operations may link to a build but do not replace its lifecycle.

Agent-wide mutation cancellation and exclusivity must be designed with AgentExplorer and Agent API/SDK
experts before remote exec/process/software endpoints ship.

## 7. Events, watching, and logs

REST polling is always sufficient to recover current durable state. Server-Sent Events provide an
efficient resumable projection for browsers, CLI watches, and integrations:

```text
GET /api/v1/events?topic=build&resourceId=build-123&cursor=...
Accept: text/event-stream
Last-Event-ID: ...
```

Event fields include `id`, `sequence`, `occurredAt`, `type`, `resource`, `correlationId`, a bounded
`data` projection, and whichever of `configurationRevision`, `observationRevision`, or
`runtimeRevision` the event advances. Delivery is at least once; clients deduplicate by event ID and
fetch the referenced REST resource when exact current state matters.

- The cursor is durable within a published retention window.
- Reconnect uses `Last-Event-ID` or `cursor`.
- An expired cursor returns `410 event_cursor_expired` with a recovery link to current state.
- Authorization is evaluated at connection and for each emitted resource so permission changes do
  not leak later events.
- Keepalives contain no state and are not audit events.
- Slow clients are disconnected with a resumable cursor rather than consuming unbounded memory.
- Events are projections, not the source of truth and not a promise of permanent event sourcing.

Build logs are append-only, chunked, and retention-bounded. The REST contract exposes metadata,
range/cursor reads, and a resumable tail. Large completed logs belong in the blob store; SSE does not
retain or replay unlimited stdout. Audit events use their own collection and permission because they
may reveal sensitive operational metadata.

WebSockets are reserved for future genuinely bidirectional interactive features, such as a terminal.
They are not required for inventory, build watch, queue, or ordinary command output. SignalR may be
an internal UI convenience only if it does not become a private management contract or diverge from
REST/SSE semantics.

## 8. Authentication, roles, and scopes

Transport is TLS. Pinned TLS and enrollment rules for elevated agents remain governed by D4 and D21.

Supported client forms:

- Browser: secure, HTTP-only, same-site authentication cookie established by the login flow. All
  state-changing cookie-authenticated requests require anti-forgery protection.
- CLI/automation: bearer personal access token or service token. Tokens are sent only in the
  `Authorization` header, never query strings.
- Agent credentials: accepted only for AgentHub and explicitly agent-scoped blob operations, not the
  human management API unless a future reviewed use case says otherwise.

The User Roles Expert owns TeamCity-like role definitions and permission inheritance. The REST API
must map every operation to a canonical permission and publish/test that matrix. Representative
canonical permissions include:

```text
project.view
project.settings.propose
build.run
build.cancel
fleet.summary.view
fleet.inventory.view
fleet.environment-names.view
fleet.environment-values.view
fleet.command.execute
fleet.agent.authorize
fleet.agent.manage
git.repository.bind
git.change.reconcile
audit.view
tokens.manage-all
server.manage
```

These names are placeholders until User Roles review. They must not accidentally imply that a build
submission token can obtain an interactive shell or read environment secrets.

Authorization is resource-scoped where TeamCity semantics require project boundaries. Listing
returns only visible resources and pagination remains correct after filtering by permission. The API
does not reveal whether a hidden resource exists. The same current authorization and object-visibility
checks run before an idempotency replay is disclosed. Token creation attempts plaintext delivery
exactly once; only a hash, non-secret metadata, and the secret-free idempotency receipt remain durable.
SuperUser first-login and recovery behavior is owned by the Admin/SuperUser Expert and must use the
same audit/correlation boundary.

## 9. Audit and correlation

Every request receives a server-generated correlation ID. A caller may supply `X-Correlation-ID`
within strict length and character limits; the value is returned in the response. Distributed trace
context may also be accepted, but audit identity comes from authenticated server state, not headers.

Mutating requests and security-relevant reads emit structured audit records containing:

- timestamp, correlation ID, request ID, and idempotency key hash;
- actor type/ID, effective roles, authentication method, and source address;
- HTTP operation ID plus domain action;
- target resource type/ID and owning project when applicable;
- outcome, status, error code, and duration;
- before/after representation hashes or changed field paths, not secret values;
- configuration change ID, base/head/applied Git revisions, branch, and commit identity;
- runtime operation/build ID and agent/session identity where applicable.

Authorization denials, login/token events, agent authorization, remote inspection of sensitive
environment/process data, command execution, cancellation, and configuration reconciliation are
always audited. Ordinary high-volume polling is sampled or summarized according to Logs policy; it
must not make the journal unusable.

For the first implementation, structured append-only audit logs with rotation and retention are
sufficient, provided they are queryable through bounded `/audit-events` projection and tested for
redaction. Tamper-evident storage is a future decision. Application logs and the audit journal may
share infrastructure but are distinct records with different retention and access expectations.

## 10. OpenAPI and client consumption

- Publish OpenAPI at `/openapi/v1.json` and produce the same deterministic artifact during build.
- Check the generated artifact or a normalized contract snapshot into source control so code review
  sees API changes. CI rejects unexplained drift and breaking changes.
- Every operation has a stable `operationId`, tags, permission requirement, success/error responses,
  idempotency and precondition requirements, examples, and enum descriptions.
- Schemas document sensitivity and redaction. Secret plaintext response fields carry an explicit
  one-time-delivery extension, are absent from replay schemas, and never appear in examples.
- CLI and React UI consume generated types/clients from the reviewed OpenAPI contract. Handwritten
  wrappers may add UX behavior but may not invent endpoints or response shapes.
- UI and CLI integration tests run against a real Kestrel REST host. In-process method calls do not
  count as public contract coverage.
- External integrations may use plain HTTP without the Vivarium CLI. `curl` examples remain a design
  acceptance test for non-streaming operations.

The React/Workbench UI uses REST for data and mutations and SSE for live projections. The CLI build
flow now implements the following coherent path:

1. `viv-cli login` stores REST trust/credentials using the same pinned-controller identity rules; no CLI
   command needs a management gRPC channel afterward.
2. `viv-cli run` creates a principal/project-scoped blob upload plan, uploads required bytes through the
   staged `/blobs/{sha256}` data plane, then submits `POST /api/v1/builds` with one
   principal-scoped `Idempotency-Key`.
3. The default wait follows build SSE from its last event ID and periodically/finally reads
   `GET /api/v1/builds/{id}` as authoritative state. `--no-wait` returns after durable submission.
   Ctrl+C closes only the local SSE/poll watch and never changes the remote build.
4. `viv-cli cancel <build-id>` sends `PUT /api/v1/builds/{id}/cancellation`. Success means the first
   cancellation intent is durably recorded, matching current semantics; terminal cancellation is
   observed through the build resource/events rather than inferred from the HTTP connection.
5. Agent list/authorization and every remaining legacy management call still move to their REST
   equivalent. Only then is the gRPC ControlPlane removed.

During migration, gRPC and REST adapters call the same application commands, but idempotency replay
does not cross their separately scoped request IDs. The transitional gRPC ControlPlane is frozen and
must not gain features unavailable through REST.

## 11. Backward compatibility

`/api/v1` follows these rules:

- Adding optional response fields, endpoints, filters, event types, and problem codes is compatible.
- Existing clients must ignore unknown response fields and unknown enum values where possible.
- Required request fields cannot be added to an existing operation without a defaultable transition.
- Existing field meaning, type, units, nullability, default, authorization scope, and side effects do
  not change incompatibly.
- Fields and operations are deprecated in OpenAPI and response metadata before removal. Removal or a
  semantic break requires `/api/v2` and a documented migration path.
- Pagination cursor formats are opaque and may change, but retained cursors must honor their published
  lifetime or return the documented `410` recovery error.
- Event delivery remains at least once; adding events cannot change authoritative resource state.
- AgentHub protobuf compatibility remains governed separately by D4 and the previous-agent CI gate in
  D20. A REST version bump does not justify breaking enrolled agents.
- Blob identities and integrity semantics remain stable independently of REST versions.
- The management gRPC ControlPlane is not part of the target public compatibility promise. It remains
  frozen only long enough to migrate the current in-repository CLI and is removed before the first
  supported public release; AgentHub is unaffected.

Once releases exist, CI compares OpenAPI to the previous release and runs representative old clients
against the new controller. Security fixes may narrow access immediately, but require an explicit
release note rather than being hidden as a compatibility change.

## 12. Security and operational limits

- Reject request bodies over endpoint-specific limits before buffering them fully.
- Apply rate and concurrency limits per principal and expensive operation class.
- Never accept arbitrary filesystem paths, SQL, Git refspecs, shell strings, or filter expressions
  without domain validation. Remote command design requires a separate security review.
- Environment values, process command lines, file contents, logs, and artifacts may contain secrets;
  their endpoints require explicit permissions, redaction policy, and audit.
- Links returned by REST carry no bearer token in the URL. Blob access uses the caller's credential
  plus a server-side staging, assignment/build-ownership, or visible-artifact grant. SHA-256 knowledge
  alone never authorizes a transfer.
- The controller's self-signed deployment mode does not permit disabling TLS verification in clients.
- OpenAPI documentation UI, if hosted, follows the same authentication rules and may be disabled by
  policy; the machine-readable contract remains available to authorized callers.
- Health/readiness endpoints disclose only coarse process state anonymously. Detailed system state is
  authenticated.

## 13. Required implementation evidence

Before declaring the first REST slice complete, provide:

1. A numbered architecture update reconciling D4, component descriptions, protocol sketch, and the
   React/public-API panel model.
2. OpenAPI snapshot validation and a breaking-change check in CI.
3. Tier-2 Kestrel tests for authentication, anti-forgery, authorization, validation, and problem
   details.
4. Restart-persistent idempotency tests proving one build/operation/change for repeated requests,
   principal-scoped build request IDs, current-authorization rechecks, and secret-producing replay
   without stored or repeated plaintext.
5. Concurrent update tests proving stale `If-Match` cannot overwrite Git or runtime state and that
   independent configuration, observation, and runtime revisions do not conflict accidentally.
6. Git tests for direct commits, reviewed branches, validation failure, external commits,
   reconciliation failure, and revert commits.
7. Pagination tests under concurrent inserts, stable filters/sorts, cursor tamper, and cursor expiry.
8. SSE reconnect tests for duplicate delivery, resume, slow clients, permission changes, and expired
   cursors.
9. Operation tests for queued/running cancellation, duplicate cancellation, disconnect/reconnect,
   controller restart, and terminal result fencing.
10. Endpoint/permission matrix tests supplied with User Roles review.
11. Audit tests correlating HTTP request, Git change or runtime operation, agent dispatch, and terminal
    outcome while proving secret redaction and bounded log volume.
12. CLI and React smoke tests that use REST rather than database or private in-process shortcuts,
    including `viv-cli run` upload/submit/watch, local Ctrl+C, and explicit `viv-cli cancel` semantics.
13. Blob discovery/staging tests proving cross-principal non-disclosure, hash verification, immutable
    build references, fenced assignment download, owned artifact upload, reconnect, cancellation
    result grace, and unauthorized hash denial.
14. A check that AgentHub sessions remain backward-compatible and blob byte transfer remains on its
    authenticated data plane while object authorization is tightened.

## 14. Non-goals

- Replacing AgentHub gRPC with polling, inbound SSH, WinRM, or REST calls into agents.
- Moving payload, artifact, or completed large-log bytes into JSON.
- Exposing SQLite tables, generic CRUD, arbitrary SQL, or repository filesystem paths.
- Inventing a generic workflow language in the REST layer.
- Defining TeamCity, AgentExplorer, platform, user-role, or agent capability semantics without their
  owning experts.
- Storing runtime state, observations, audit records, generated status, or secrets in Git merely to
  say that everything is versioned.
- Requiring GitHub, GitLab, or any other external service. A local repository and direct/change-branch
  workflow must remain functional offline.
- Promising multi-controller clustering or cross-tenant isolation in the first release.
- Shipping placeholder remote-file or remote-command APIs before their safety and agent contracts.
- Treating SSE as permanent event sourcing or an exactly-once transport.

## 15. Initial delivery sequence

1. Establish versioned SQLite migrations, the minimal D27 `audit_events` journal, request actor and
   correlation context, and one authorization evaluator beneath the existing ControlPlane, panel, and
   blob boundaries. Map legacy credentials without widening their authority.
2. Establish shared HTTP conventions, Problem Details, authentication, deterministic OpenAPI, cursor
   validation, and a thin application-service boundary. Add read-only `system`, `agents`, `builds`, and
   `queue` resources first.
3. Add capability/version negotiation and typed static connect-time host facts, then expose
   `/agents/{agentId}/facts` with canonical `system.*` fields, freshness metadata, and explicit
   legacy-agent behavior.
4. **Completed:** managed-local Git plus last-known-good reconciliation and the first Git-backed Agent
   setting, `spec.enabled`, including `If-Match`, idempotency, commit/change response, audit, conflicts,
   and commit-before-activate behavior.
5. **Completed:** object-scoped blob discovery/staging and the complete CLI build
   submit/watch/cancel flow over REST/SSE, preserving local Ctrl+C versus remote cancellation.
6. Complete first-run administration and RBAC, then implement restart-safe runtime actions such as
   Agent inventory refresh with idempotency, durable operation state, AgentHub dispatch, fencing, and
   audit. Dynamic sensitive inventory does not ride on the static-facts shortcut.
7. Expand TeamCity and AgentExplorer surfaces and port proven views to React only after their experts
   approve each lifecycle and the Agent API/SDK Expert supplies the required capabilities.
8. Remove the transitional gRPC ControlPlane after CLI migration and before the first supported public
   release; keep AgentHub and the object-authorized blob data plane.

## 16. Open questions requiring explicit decisions

1. Which forge-specific adapters, if any, ship for the optional remote reviewed-change mode after the
   managed-local direct-commit baseline?
2. What is the minimum idempotency-record retention and how is it related to build/operation
   retention?
3. What are the event and audit retention windows, and when is tamper-evident audit storage required?
4. Which external identity provider, if any, follows the TeamCity-like local user/role baseline?
5. Which future file and log reads are sensitive by default, and how may policy narrow them per pool?
6. What stable API maturity signal distinguishes experimental future AgentExplorer capabilities from
   supported `/api/v1` resources without weakening `/api/v1` compatibility?

Open questions may narrow later slices but do not reopen D23-D28 or authorize placeholder endpoints.
