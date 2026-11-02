# Persistence, migrations and recovery

> Status: **Accepted target; partial legacy implementation**
> Maintainer: Persistence/Migrations Expert
> Decisions: D23, D27, D32–D37 in [ARCHITECTURE](../ARCHITECTURE.md)

## Storage authority

PostgreSQL is the structured business/runtime store. Git owns only versioned desired build/fleet
settings. Private resource connections/secrets use Chiral's managed settings. Large bytes use Storage
with PostgreSQL manifests/references. Identity/OpenIddict/roles/groups/assignments are PostgreSQL
application data, not rebuildable Git projections. See [Git/versioning](git-versioning.md) for the
classification boundary and [first-run administration](first-run-administration.md) for keys/secrets.

The former SQLite schema and writer channel are implementation evidence below. Preserve their
observable guarantees, not their table layout or serialized-writer mechanism. Chiral nodes give
ownership, settings and readiness; InMemory nodes are not durable business persistence. Do not add
another queue or scheduler as a side effect of the storage port.

## Durability and ownership

Accepted actions, assignments, cancellations, results and audit intent survive ordinary restart.
Enforce at most one active exclusive owner for an agent; use transactions/constraints and fencing
appropriate to PostgreSQL. Connection generations, retry receipts and immutable assigned-agent
provenance prevent old sessions/results from taking over current work. Unknown work is not success
or safely free capacity. Never hold a database transaction across agent/provider/network I/O.

Build identity continues from queue to running/history under the compatible contract. Persist tested
source/settings snapshots, actor, agent identity, selected upstream builds, effective parameters and
original import lineage. Later account/settings changes do not rewrite historical execution meaning.
Native and compatible IDs must map to the same objects, including imported IDs and large test IDs;
do not infer storage keys from a convenient wire DTO.

Domain transitions and their mandatory audit/outbox intent commit together when caller-visible
success depends on them. Retried requests re-check current rights and recover the original effect.
Secret plaintext is not persisted for replay. Long-running operation state and terminal outcomes are
durable; bounded process logs are not an action journal. Progress messages do not require an audit
row per chunk/heartbeat. Public contract fences determine when a result can be acknowledged.

## Logical records and byte publication

The schema must support catalog/revision projections; accounts/external bindings/framework grants;
editable roles/groups/scoped assignments; agent registration/credentials/observations; queue/build/
lease/operation state; test identities/occurrences/attempts/problems/triage/statistics; log sequence
and gap records; artifact/payload manifests/references/tombstones; import mappings/checkpoints;
audit/idempotency and migration metadata. This is a responsibility list, not a required one-table-
per-item design. Resolve exact aggregates/constraints against the early REST/domain profile.

Publish bytes and references in a recoverable order. Verify staged content before making a complete
manifest visible. Acknowledged bytes must remain recoverable after restart. Recover orphan staging
and incomplete uploads without presenting an incomplete artifact as complete. A hash identifies
content, not authorization; downloads check the caller's current build/project/assignment rights.
Public blobs cannot share access policy with private settings, signing or cookie keys.

Result and log ingestion need stable identities, sequence/watermarks and idempotent retry. Retain
raw reports alongside parsed provenance and explicit no-report/partial/invalid states. An unreadable
report or missing adapter is not a passing suite. Durable gaps and expiry tombstones distinguish
removed, never-present and unavailable content. Do not fabricate an empty successful download.

## Transactions beyond PostgreSQL

Git, Storage, PostgreSQL and remote machines do not share a physical transaction. Use durable intent,
stable operation IDs, authoritative ref CAS, verified publication and idempotent recovery. Avoid a
general orchestration framework when the bounded domain transition suffices. Reconcile committed
Git revisions into projections; never rebuild Identity or historical results from Git.

Concurrency versions for resource settings, Git revisions and runtime objects are different values.
Native ETags/cursors cannot be imposed as new requirements on TeamCity clients. Compatible resource
filters, locators, pagination, error/media shapes and mutation success use the same underlying model
but retain their own frozen wire semantics. Cursor expiry and permission filtering remain explicit.

## Startup, migration and compatibility

Run ordered, versioned, checksummed PostgreSQL/framework migrations before declaring affected nodes
ready. Prevent concurrent migration runners. Refuse an unsupported newer schema before mutation;
failed migration never starts partially compatible scheduling. State compatibility windows and
backup requirements per migration. Do not port SQLite PRAGMA/WAL/checkpoint commands as PostgreSQL
procedures, silently reset a database or create an unrelated empty installation after failure.

The pre-refactor SQLite-to-PostgreSQL data/consumer disposition is explicit P0 work, separate from
the required TeamCity import. A checkpoint/tag alone is not an import path. Every legacy schema,
credential, settings authority and consumer needs retain/translate/retire evidence before removal.
Milestones may merge independently, but replaced code leaves only after its declared acceptance and
existing-feature disposition. No permanent dual business-store authority is accepted.

After restart, recover intents, reconcile active settings, re-establish current agent ownership and
resume or terminate work truthfully before offering conflicting capacity. Database readiness, Git
catalog readiness, identity initialization and byte storage readiness are separate observations.
Never use account-storage failure as permission to grant an emergency database-independent login.

## Retention and garbage collection

Respect TeamCity pin/dependency/cleanup semantics and imported-retention commitments. Cleanup must
not remove bytes referenced by retained builds, active assignments, pending publication, in-progress
imports or the supported backup boundary. Delete logical references and collect unreachable content
with recoverable progress; interrupted collection cannot remove still-referenced data. Imports do
not mirror source cleanup as deletion. Limits and removal remain visible, not silent data loss.

Bound audit/idempotency/log/observation data separately from user retention of build artifacts.
Record which data was deliberately removed and why where the public history contract requires it.
Do not turn workload output into full-text PostgreSQL rows by default or concatenate all sinks into
one unbounded stream. Exact budgets/retention and dependency variants remain explicit design inputs.

## Backup and restore

The first supported procedure is coordinated offline backup: drain/stop scheduling, stop the Host,
take a consistent PostgreSQL backup and capture every referenced byte object, retained settings Git
revision/ref, private Chiral setting, TLS material, Data Protection/OpenIddict key and other required
private dependency. PostgreSQL may be an external service: copying the Host directory does not copy
the database or external Storage. Use provider-appropriate consistency/export tools, not SQLite file
copy/checkpoint instructions. Protect the backup according to its included secrets.

Record server/schema/configuration versions, consistency boundary, repository identities/revisions,
object inventory and verification hashes. Verify completeness before claiming backup success. Restore
into an isolated empty target with compatible software, validate database integrity and supported
migrations, restore private dependencies and byte references, reconcile settings/agent ownership,
and enable work only after validation. Missing keys/secrets or corrupt content are explicit failures,
not a reason to silently replace identity or fabricate retained history.

Restored session/token usability follows framework validity/revocation rules; startup still rotates
the enabled installation account password. Agent connectivity alone cannot reopen scheduling before
restore/ownership checks. Prove retained builds/tests/logs/artifact bytes and current access with the
old TeamCity unavailable. Online backup is later work requiring its own proven consistency boundary.

## Acceptance and open decisions

Use [installation](../acceptance/installation/server.feature),
[operations](../acceptance/operations/backup-restore.feature),
[resources/audit](../acceptance/operations/resources-audit.feature) and
[migration](../acceptance/migration/import.feature) criteria, plus the technical result/agent/REST
contracts. Prefer small real PG/Storage boundary tests for transaction/publication claims; real
process/agent recovery and restore need their actual consumer chain. Inject failures at acknowledged
intent, assignment, cancellation, byte publication, watermark, Git activation, migration and cleanup
boundaries. No implementation-generated expected trace proves its own correctness.

P0 must allocate exact aggregate/ID constraints, version support, migration/data handling, limits,
private key custody, complete backup tooling, corruption repair and import checkpoints. These remain
open until reference/fixture-backed decisions and evidence exist; documentation is not a passed run.

## Legacy checkpoint evidence — not replacement requirements

The following is the source/data inventory of the old SQLite implementation, not a target schema
or a requirement to preserve its writer architecture:

The Phase 1 code already establishes several parts of this contract:

- `src/Vivarium.Controller/Persistence/VivariumDatabase.cs` opens `vivarium.db`, applies a five-second
  busy timeout, and funnels mutations through one writer channel. `DatabaseMigrator.cs` enables WAL and
  foreign keys and applies an ordered, checksummed migration manifest with explicit adoption of the
  known Phase-1 schema, exact table/column checks, required-index/trigger checks, metadata consistency,
  and refusal of drift or a newer schema.
- The current v12 schema defines durable agents, enrollment tokens, builds, queue entries, matrix builds,
  matrix cells, migration metadata, audit events, configuration revision sets/members, materialization
  scopes, idempotent configuration mutations, Agent desired configuration, object-scoped blob grants
  and references, build mutation/events, and bounded TRX report/test/occurrence projections. Constraints
  and partial unique indexes enforce one active build and one queue claim per agent; update/delete
  triggers make audit rows append-only. V9 adds the resumable administration claim/session saga, v10
  adds Git User/RoleBinding projections, v11 adds the private password verifier and credential
  generation record, and v12 adds immutable Agent-package metadata/publication receipts plus durable
  upgrade operations, append-only phase events, and fenced maintenance drains. Upgrade rows persist
  handoff/health/commit/rollback state, exact connection generations, bounded dispatch backoff, the
  first cancellation reason, outcome digest, and absolute deadline. Package bytes remain
  content-addressed files, not SQLite bodies and are rehashed at the serve/reuse boundary; upgrade
  state, history, and drain ownership recover together after restart.
- `ManagementAuthorization.cs` supplies stable named and legacy principals, request/correlation context,
  product-owned built-in role floors, and one permission evaluator used by ControlPlane, REST, panel,
  and blob endpoints without widening legacy admin/submit/agent scope.
- `AuditEventStore.cs` writes bounded, redacted action rows. Agent administration, enrollment-token
  issuance, matrix submit/cancel, and queued/running build cancellation insert their success audit in
  the same SQLite transaction; exact retries do not append duplicate success rows. Panel
  authentication/logout and denied authorization decisions also use the journal, while heartbeats and
  automatic scheduling/build transitions do not.
- `src/Vivarium.Controller/Management/MatrixBuildStore.cs` persists a matrix, every child build, and
  every queue row in one transaction. Its request ID plus canonical request payload implements
  idempotent submission and changed-request conflict detection.
- Build ownership, reconnect deadlines, cancellation intent, terminal protobuf results, exact
  definition snapshots, and separate reported/custom agent snapshots are stored durably.
- `src/Vivarium.Controller/Blobs/BlobStore.cs` streams an upload into a temporary file, verifies the
  SHA-256 name, then uses an atomic same-directory move. A concurrent identical writer is harmless.
- `tests/Vivarium.Tests/BuildQueueStoreTests.cs` covers FIFO/claim recovery, uniqueness, prepared lease
  recovery, cancellation persistence, and the existing Phase 1 table rebuild.
- `tests/Vivarium.Tests/ControlPlaneTests.cs` covers atomic submission rollback, idempotent retry,
  restart snapshots, atomic restart-safe cancellation, and immutable assigned-agent provenance.
- `tests/Vivarium.Tests/FencingTests.cs` covers reconnect ownership, duplicate results, first-reason
  cancellation, and agent/controller restart before result acknowledgement.
- `tests/Vivarium.Tests/BuildQueueTimeoutTests.cs` covers persisted absolute deadlines and restart-safe
  legacy backfill.
- `tests/Vivarium.Tests/SessionLoopTests.cs` covers server-side blob hash verification and preservation
  of an existing blob when an idempotent PUT body is wrong.
- `tests/Vivarium.Tests/DatabaseMigrationTests.cs` covers fresh application, explicit rollback,
  checksum drift, unknown tables, interrupted metadata, and newer-schema refusal.
- `tests/Vivarium.Tests/ManagementKernelTests.cs` covers the legacy permission matrix, append-only
  restart persistence, mutation/audit rollback, token redaction, idempotent submission audit, and
  accepted/denied login audit.
- `tests/Vivarium.Tests/ConfigurationReconciliationTests.cs` covers commit-before-activate,
  revision-set materialization, invalid/blocked last-known-good retention, exact principal-scoped
  idempotency and conflict replay, affected-target audit/revision linkage, pending-operation and
  repository-failure recovery, no-op reconciliation, Agent-document removal rejection, bounded moving-
  head convergence, and restart recovery from an invalid authoritative head. Migration v6 carries the
  additional mutation evidence without changing the v1-v5 migration bytes/checksums.
- `tests/Vivarium.Tests/AgentDesiredConfigurationTests.cs` covers the first `spec.enabled` projection,
  stale-base and validation conflicts, idempotent replay, concurrent head movement, restart behavior,
  and the GET/PUT REST precondition shape.
- `tests/Vivarium.Tests/AgentConfigurationReconciliationMonitorTests.cs` covers external valid-head
  convergence into durable/live state and LKG behavior for invalid, removal, and repository-failure
  attempts under the shared scheduling lifecycle fence.

The current gaps are equally important:

- the writer channel is unbounded and has no overload contract;
- build log text is an in-memory buffer and is evicted on terminal acknowledgement rather than stored
  as durable bounded chunks;
- the blob directory has no reference table, retention policy, garbage collector, scrubber, or quota;
- Git materialization currently projects Agent `spec.enabled`, User declarations, and direct built-in
  RoleBindings; Project/Build Configuration, custom Agent properties, groups/custom roles,
  provider/image, retention, and other desired-state projections remain;
- configuration and build mutations have durable principal-scoped idempotency, and build SSE has a
  durable resumable cursor/retention-gap contract; there is no general runtime-operation store for
  AgentExplorer, deployment, provider, or other asynchronous actions;
- the minimal audit journal has no retention/GC or tamper-evident export; configuration/setup mutation
  audit links to exact operations/revisions, while full identity/RBAC management, runtime-operation, and
  diagnostic/build-output streams remain;
- backup, restore, integrity verification, and corruption response are acknowledged but unimplemented.
