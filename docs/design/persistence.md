# Persistence, Migrations, and Recovery

> Status: **Accepted**
> Implementation: **Partial**
> Maintainer role: [Persistence/Migrations Expert](../roles/persistence-migrations-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D4, D7-D9, D14, D18, D22-D30

Numbered architecture decisions remain authoritative; any conflict requires an architecture update in
the same change.

## Purpose

Vivarium must recover a build farm after a controller restart without duplicating work, losing an
accepted result, or silently changing historical meaning. It must also support Git-backed configuration
and a day-one REST surface without turning SQLite into a second configuration authority.

This document defines the storage boundaries, durability rules, migration discipline, REST concurrency
implications, retention, and operator recovery contract. It does not define domain entities owned by
TeamCity, AgentExplorer, Agent API, Git/Versioning, or User Roles experts.

## Storage authority

| Store | Authoritative for | Explicitly not authoritative for |
|---|---|---|
| Git repositories | Mutable desired configuration and its human-reviewable history | Live status, queues, credentials, observations, execution results |
| SQLite | Active configuration projection, runtime/operational state, idempotency, immutable execution provenance, references to blobs and logs | Editing history of mutable configuration, large content bodies |
| Blob directory | Immutable payloads, artifacts, result files, and durable log chunks addressed by SHA-256 | Mutable metadata, lifecycle state, authorization |
| Process memory | Connections, process handles, caches, waiters, subscriptions, wake-ups | Any fact that must survive restart |

### Git-backed configuration

Every deliberate change to settings or properties originates as a Git commit, whether requested by
CLI, REST, or UI. This includes projects, build configurations, steps, requirements, agent custom
properties and policies, provider/image definitions, roles and permission assignments, and retention
policies once those domains adopt them.

The following are operational commands or observations and do not create a configuration commit:

- agent hello facts, heartbeats, connection state, and live inventory;
- enrollment proofs, credential hashes, and short-lived tokens;
- queueing, assigning, acknowledging, cancelling, and completing work;
- authorizing the first contact, invoking a run, or requesting a one-off operation;
- logs, artifacts, results, health observations, and audit events.

An owning design may deliberately promote an operational toggle into Git-backed desired state. For
example, a persistent maintenance policy can live in Git while an emergency drain remains an audited
operation. The domain expert must name that distinction; persistence code must not guess it.

Secret values never enter Git. Git stores secret references or policy only. SQLite stores hashes or
protected material only where the security design requires it; plaintext bootstrap or superuser tokens
may be emitted once by the owning flow but are never written to ordinary logs or audit details.

### Revision sets and materialization

Vivarium does not assume that all configuration lives at one Git commit. A **configuration revision
set** is the immutable set of Git inputs used for one materialized scope:

- the control-repository commit that defines central fleet, access, policy, and project registration;
- zero or more product-repository commits that define product-owned projects and
  `vivarium.yaml` build configurations;
- each member's stable repository identity, selected ref if relevant, commit, tree/content hash, and
  role in the set.

A submitted build normally captures at least the controlling control-repository revision and the
product-repository revision containing the tested definition. A centralized projection may include
multiple registered product repositories. The Git/Versioning design owns how repositories and refs
are selected; persistence stores the exact resolved set rather than inferring it later.

There is no singleton active revision for the whole farm. Revision sets become active for an explicit
materialization scope, such as central control configuration or one registered product/project. A
scope has at most one active set, while different scopes can validly use different product commits.

The Git reconciler validates a complete candidate revision set before changing its active scope.
Applying a set is one serialized SQLite transaction:

1. Record the candidate set and every repository member, content hash, validation result, and
   parent/base set supplied by the writer.
2. Replace or update the affected domain projections, each row carrying `source_revision_set_id`, the
   supplying member where relevant, and a normalized `content_hash`.
3. Advance that materialization scope's active revision-set pointer only after all constraints pass.
4. Commit, then publish a best-effort wake-up to schedulers and UI projections.

Invalid sets are recorded as reconciliation failures and leave the last known good active set for the
scope untouched. No endpoint may update a projected configuration row directly.

Projection tables are domain-specific, not a generic entity-attribute-value store. The shared schema
needs only revision-set/application metadata; the TeamCity, AgentExplorer, roles, and provider designs
own the typed projection tables they query.

An active projection must be rebuildable from the repositories in its revision set. Rebuildability
does not make build history disposable: every submitted build records its exact revision set plus the
canonical resolved definition snapshot and its hash. This snapshot is immutable evidence even if a
Git history is later garbage-collected, force-rewritten, or temporarily unavailable.

## Durability invariants

1. **Commit before acknowledgement.** A controller reports success only after the transaction that
   establishes the fact has committed.
2. **Projection after persistence.** Live sessions, streams, and UI notifications are updated only
   after commit. A crash between commit and delivery is repaired by replay/reconciliation.
3. **One logical writer.** All SQLite mutations pass through the serialized writer. Reads use short
   independent connections. Business invariants still use transactions, constraints, and conditional
   updates.
4. **Immutable evidence.** Accepted assignments, resolved definitions, selected-agent facts, selected
   machine/image identity, terminal results, artifact manifests, and their hashes never change.
5. **Fenced ownership.** An active work item has one durable owner. A stale session or operation
   generation cannot acknowledge, cancel, or complete newer ownership.
6. **First terminal result wins.** Duplicate identical results are acknowledged; conflicting later
   terminal results are rejected and logged.
7. **First cancellation intent wins.** Cancellation is idempotent and retains its original actor,
   reason, and timestamp.
8. **Atomic aggregates.** Creating or cancelling a matrix and its affected child/queue rows is one
   transaction.
9. **Absolute deadlines.** Queue, reconnect, operation, and retention deadlines are persisted absolute
   instants. Restart or configuration drift never extends them.
10. **Blob before reference.** A verified blob is committed before a SQLite transaction references it.
    An orphan is collectable; missing bytes behind an active reference are a diagnosed integrity
    failure. Intentionally expired bytes have an explicit tombstone and no active reference.
11. **Git apply is all-or-nothing.** A candidate revision set never becomes partially active in its
    materialization scope.
12. **Memory is disposable.** Startup can reconstruct every non-terminal controller responsibility
    from SQLite, Git, and blob metadata.
13. **Watermark before release.** An agent or other producer releases a result or log prefix only after
    SQLite commits the terminal record or contiguous watermark and all required blob references.
14. **Required audit is atomic.** A successful caller, security, or configuration mutation that
    requires durable audit writes its append-only audit row in the same transaction. Automatic build
    transitions remain domain state plus diagnostics and do not flood the audit table.

## Current implementation evidence

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

## Target logical schema

Exact columns land with their owning slice. These logical records are the minimum shared substrate:

### `schema_migrations`

- monotonically increasing migration number;
- immutable migration name and checksum;
- controller version and UTC application time.

The database also exposes one metadata row containing the current schema version and the minimum
controller version that may open it. A controller must refuse a schema newer than it supports.

### `configuration_revision_sets` and `configuration_revision_members`

- stable revision-set ID and materialization scope;
- base/parent revision-set ID supplied by the writer;
- state: validating, rejected, active, or superseded within that scope;
- validation/reconciliation operation ID and bounded error summary;
- requested, validated, and applied timestamps;
- actor and request correlation ID.

Each member records repository ID, role (`control` or `product`), commit, tree/content hash, and any
stable project binding. One set has one control member and the product members required by that scope.
Only one set is active per materialization scope. Domain projection rows point to their source set;
where needed they also identify the member that supplied the object. Rejected candidate details may
be retained for an operator-defined window; Git remains their full history.

### `audit_events`

The day-one audit ledger is a minimal append-only SQLite table:

- stable audit event ID and UTC timestamp;
- authenticated actor/credential identity and request or operation ID;
- action and target type/ID;
- outcome plus bounded redacted reason/details;
- base/result revision-set IDs where configuration changed.

The table has no update path. A caller, security, or configuration mutation whose success contract
requires audit inserts the event in the same SQLite transaction as the domain mutation or active-set
change. A transaction that cannot write its required audit row does not succeed.

Ordinary scheduler progress, heartbeats, step transitions, log chunks, and automatic build lifecycle
transitions remain in domain tables and bounded diagnostic logs. They do not generate audit rows.

### `idempotency_requests`

- authenticated actor or credential identity;
- operation/endpoint namespace and idempotency key;
- canonical request hash;
- state and durable result/resource reference;
- creation, completion, and expiry timestamps.

The uniqueness boundary is `(actor, operation, key)`. The same request returns the saved result;
different content conflicts. Rows outlive client retry windows and are removed only by an explicit
retention policy that cannot invalidate a still-running operation.

Build submission may retain its existing domain-specific request record; the common mechanism is for
other mutating REST operations, not a reason to rewrite working code prematurely.

### `blob_references`

- SHA-256;
- immutable owner type and owner ID;
- purpose/path/ordinal within the owner;
- size and committed timestamp;
- state (`active` or `released`) and optional retention class or expiry derived from the owner.

References, not directory enumeration, define reachability. A blob can have many references. Blob
metadata must not weaken the rule that the filename hash is the content identity.

### `blob_expiry_tombstones`

- owner type/ID and purpose/path/ordinal formerly referencing the blob;
- SHA-256 and size, preserving immutable provenance;
- expiry time, retention-policy revision set, and GC operation ID;
- actor for explicit deletion, or system retention reason for scheduled expiry.

Intentional artifact or log expiry atomically creates the tombstone and releases the active reference
before bytes become collectable. An owner-specific download then returns an explicit expired response
(REST `410 Gone`), not not-found and not corruption. A tombstone does not claim the bytes still exist.

### Durable operation records

AgentExplorer commands, provider lifecycle transitions, agent upgrades, configuration reconciliation,
and other restart-spanning actions use domain-specific operation tables with:

- operation ID and kind;
- requested actor, target, and request correlation;
- desired state, current state, deadline, and cancellation intent;
- owner/fencing generation when an agent or provider is involved;
- result or failure summary and log stream reference;
- created, updated, and terminal timestamps.

Do not force builds and AgentExplorer operations into one table merely because both need leases. Share
the invariants and primitives while preserving domain semantics.

## Transactions and external side effects

SQLite cannot transact with Git, the filesystem, a live agent, or a machine provider. Use an
intent/effect/observation pattern:

1. Persist a uniquely identified intent and commit.
2. Perform or request the idempotent external effect.
3. Persist the observed outcome with the same operation ID and fencing generation.
4. A reconciler retries non-terminal intents after restart until success, cancellation, or a bounded
   terminal failure.

Never hold a SQLite transaction open across network I/O, Git commands, blob upload, process execution,
or provider calls.

### SQLite and blobs

A blob commit is:

1. stream to a unique temporary file in the destination filesystem while hashing;
2. flush and close the file;
3. reject and remove it if its digest differs from the requested lowercase SHA-256;
4. atomically rename it to the content path, accepting a race only when the existing content verifies;
5. in a later SQLite transaction, create the owner reference and acknowledge the owning mutation.

A crash after step 4 and before step 5 creates an orphan that GC may remove after its grace period. A
crash after the reference transaction must not find an uncommitted blob. Platform-specific durability
of file and directory flushes must be proved by the Platform Expert before backup/recovery is declared
release-ready.

### Git and SQLite

A configuration-changing REST/UI request first performs optimistic concurrency against its base
revision set. Before invoking Git, one SQLite transaction persists the idempotent operation intent and
the required caller-audit event; only then may the endpoint acknowledge an asynchronous acceptance.
Once the Git expert has produced the required control/product repository commit or commits,
reconciliation resolves, validates, and atomically applies the resulting set. That apply transaction
also appends the successful configuration-audit event. The REST response includes the created commits,
candidate revision-set ID, and active set for the affected scope:

- return success when that set is already active for the scope;
- return an accepted operation when the commits exist but set materialization is still pending;
- return a durable rejected reconciliation result without altering the prior active set.

If Git commits succeed but the controller crashes before recording their revision set in SQLite, the
durable operation intent and startup reconciliation discover the configured repository refs, resolve
the set, and resume. If SQLite commits an apply before the runtime wake-up, startup and periodic
reconciliation re-publish the active state. Git and SQLite must therefore converge without a
distributed transaction. A Git commit by itself is an intermediate external effect, not the
success/active acknowledgement point.

## REST concurrency and pagination

REST correctness is a storage concern as well as an API concern.

### Configuration resources

- A representation exposes an ETag derived from its revision-set ID plus normalized content hash.
- A mutation requires `If-Match` or an equivalent explicit base revision.
- A stale base fails without creating a merge commit or changing SQLite projection state.
- The Git/Versioning Expert owns whether a non-overlapping edit may be rebased; persistence still
  records the actual base set and resulting set and members.

### Operational commands

- Every retryable POST accepts an idempotency key and hashes the canonical body and target.
- Accepted async commands return a stable operation ID.
- Conditional state transitions use a SQL predicate such as `WHERE state IN (...) AND generation = ?`;
  serialization by the writer is not a substitute for that predicate.
- Cancellation and terminal-result rules preserve the first committed decision.
- When the bounded writer queue is saturated, REST returns an explicit retryable overload result and
  does not accept work that exists only in memory.

### Collections

- Public collections use a documented stable order with a unique tie-breaker, for example
  `(created_unix_ms DESC, build_id DESC)`.
- Use opaque keyset cursors, not offset pagination, for growing queues, builds, logs, audit events, and
  operations.
- Page sizes have conservative defaults and hard maximums.
- Filter/sort combinations supported by REST have matching indexes; unsupported arbitrary sorting is
  rejected rather than implemented as an unbounded scan.
- A cursor encodes filter identity and sort position so it cannot be reused against a different query.

Suggested first indexes extend the existing queue/deadline indexes with actual endpoint needs:

- builds by updated time plus ID, and by active owner/deadline;
- matrix builds by created time plus ID and by project/configuration;
- operations by target/state/deadline and by created time plus ID;
- agents by status/name and selectively indexed normalized facts used in fleet search;
- blob references by owner and by SHA-256;
- blob expiry tombstones by owner and expired time plus ID;
- configuration revision sets by scope/state/applied time and members by repository/commit;
- audit events by time plus ID and, when required by an actual query, actor or target;
- idempotency requests by expiry.

Add indexes from concrete queries and verify plans in tests. Do not pre-index arbitrary reported or
custom parameter keys; use a deliberate searchable projection when AgentExplorer defines those filters.

## Migrations

### Rules

1. Migrations are ordered, immutable files or classes committed with the code. An applied migration's
   checksum must match the binary's manifest.
2. Startup applies pending migrations before starting the writer, scheduler, REST mutations, agent
   assignment, or reconciliation loops.
3. Each purely SQLite migration runs inside `BEGIN IMMEDIATE` and one transaction. Constraint/data
   validation runs before commit.
4. Forward-only migrations are the supported path. Rollback means restoring the pre-upgrade backup and
   old binary, not guessing at a destructive down migration.
5. Destructive table rebuilds require a pre-upgrade backup, row-count/content checks, and an upgrade
   test from the oldest supported released schema.
6. Filesystem migrations are rare. When unavoidable, they use a persisted phase journal and
   idempotent steps because SQLite and filesystem changes cannot be atomic together.
7. Backfills are bounded. Large backfills are resumable maintenance operations rather than a single
   startup transaction.
8. A failed migration leaves the old schema/data intact when transactional, prevents the controller
   from scheduling work, and emits an actionable diagnostic.

`CREATE TABLE IF NOT EXISTS` remains appropriate inside a first-version migration, not as a substitute
for a migration history. `EnsureColumn` must be retired after the ledger lands; it cannot distinguish
an expected prior schema from unknown drift.

### Compatibility policy

Until the first release, preserve current Phase 1 databases through explicit baseline migrations.
After release, the project must publish:

- the oldest schema directly upgradeable by the current controller;
- whether downgrade is unsupported;
- the required backup step;
- the relation between schema version, controller version, and stale agent protocol compatibility.

Agent protocol compatibility and database schema compatibility are independent. Supporting a previous
agent does not mean an old controller may safely open a new database.

## Restart and reconciliation

Startup order is:

1. acquire exclusive ownership of the data directory;
2. open SQLite and perform a fast integrity check;
3. verify supported schema and apply migrations;
4. verify every repository in the desired revision sets and reconcile each materialization scope to a
   valid active projection;
5. clean expired upload temporary files and inspect referenced blob availability without blocking on
   a full-store rehash;
6. reconstruct queued, claimed, running, cancel-requested, and non-terminal operation state;
7. expire deadlines already in the past through the ordinary durable transition path;
8. start agent acceptance, schedulers, reconcilers, REST mutations, and live projections.

WAL recovery is delegated to SQLite, but application recovery remains explicit. A connected agent's
new session reports its believed active work. The controller re-adopts only a matching durable owner;
otherwise it sends abort/cancel according to the owning protocol. A restored snapshot or stale stream
cannot create capacity until its newer generation crosses the readiness barrier defined by D4/D5.

There is no automatic replay of a mutating external action without its persisted operation ID and
idempotency/fencing contract.

## Logs and action journal

The Logs Expert owns event shape, severity, redaction, chunk limits, and quotas. Persistence owns when
an event is durable and how its owner references it.

Day-one action journaling uses the minimal append-only SQLite `audit_events` table. It covers successful
caller-initiated mutations, security changes, and configuration changes for which durable audit is
part of the success contract. Each event records:

- stable event and request/operation IDs;
- authenticated actor and credential kind, never credential value;
- action, target type/ID, and Git base/result revision-set IDs where applicable;
- accepted/rejected/outcome and bounded redacted reason;
- controller time and relevant agent/session generation;
- resulting resource/version ID.

The event is inserted in the same transaction as the mutation. Failure to insert it fails the
mutation; there is no post-commit window in which a successful audited change has no journal row.
Duplicate retries return the existing mutation and audit identity instead of appending a misleading
second success event.

Rejected attempts, transport failures, and detailed diagnostics use structured rolling application
logs unless a security design explicitly requires a durable rejection audit. Automatic build/queue
transitions, heartbeats, step progress, and log chunks are high-volume domain activity, not caller
audit. They remain reconstructable state and bounded diagnostic logs rather than one `audit_events`
row each.

The table is queryable but is not yet tamper-evident. Signed export or a tamper-evident audit chain
requires a later numbered design decision.

### Result and log watermarks

Build and operation output uses bounded immutable blob chunks plus a SQLite manifest ordered by
sequence. The manifest records stream, byte size, content hash, first/last timestamp, and truncation or
dropped-byte counters. Never append forever to one file and never retain an unbounded in-memory
`StringBuilder`.

Each producer stream has a durable **highest contiguous watermark**; a sparse received chunk above a
gap does not advance it. Committing a new watermark and every newly covered chunk manifest/reference
is one SQLite transaction after the chunk blobs are verified. Only after that commit may the
controller acknowledge the watermark and the producer release its local outbox through that sequence.
On restart, both sides resume from the committed watermark and duplicate chunks are idempotent by
stream ID, sequence, and hash; the same sequence with different content is a protocol/integrity error.

Terminal result acceptance is a release boundary too. The acceptance transaction records the first
terminal result, its immutable artifact/result references, and the declared final watermark for every
required output stream. The controller acknowledges the terminal result only when those blobs exist
and the transaction commits. A deliberately truncated log records its final watermark and truncation
counter explicitly; it is not silently treated as complete.

Any protocol or implementation change that affects result acknowledgement, chunk acknowledgement,
watermark advancement, or producer outbox deletion requires joint review by the Agent API/SDK, Logs,
and Persistence and Migrations experts. The first release cannot declare durable streamed logs until
restart tests prove this release boundary.

## Retention and garbage collection

Retention policies are Git-backed configuration. Applying a shorter policy does not synchronously
delete data in the configuration transaction; it changes what a later maintenance operation may
collect.

GC uses mark and sweep:

1. Select an immutable retention/GC operation ID and cutoff time.
2. Mark hashes reachable from retained builds, artifacts, results, logs, image/package records, active
   uploads, and non-terminal operations.
3. For intentionally expired retained artifacts or logs, transactionally insert immutable expiry
   tombstones and release their active references. Only then are their bytes unreferenced.
4. Treat blobs newer than the grace cutoff as reachable even if no reference is visible yet.
5. Move unreferenced candidates to a same-filesystem quarantine/trash area.
6. Recheck reachability before final deletion after a second grace interval.
7. Record counts and bytes, not every hash, in ordinary logs unless diagnostics are requested.

Referenced immutable provenance is retained with its owning build. Deleting a build history record is
an explicit audited retention action, never a side effect of blob pressure. Quarantined failed-machine
snapshots and image chains have provider-specific retention but follow the same owner/reference rule.

Retention may intentionally expire artifact or log bytes while keeping build history. In that case,
the owner manifest retains path, hash, size, and an expiry tombstone; the active blob reference is
released. Missing bytes are expected only after this committed release. Deleting bytes first and
adding a tombstone later is corruption, not expiry.

Stale upload temporary files have their own short retention. SQLite WAL/SHM files and active controller
files are never blob-GC candidates.

## Backup and restore

### First supported contract: offline backup

The first release should support a deliberately boring, reliable procedure:

1. drain or stop new work and shut down the controller cleanly;
2. checkpoint/close SQLite;
3. copy the complete data directory, including `vivarium.db`, blob content, every configured control
   and product Git repository and ref needed by retained revision sets, TLS private key/certificate,
   and any protected controller key material;
4. create a manifest with controller/schema version, revision sets and Git refs, file sizes, and
   hashes;
5. verify the copy before declaring success.

Backing up only SQLite or only Git is not a Vivarium backup. Token hashes without controller trust
material and blob references without blob bytes are incomplete.

### Restore

Restore into an empty data directory, never over a live partial state:

1. verify the backup manifest and blob filename hashes;
2. start a compatible controller in maintenance mode;
3. run SQLite integrity and foreign-key checks;
4. apply supported forward migrations;
5. verify every member of retained/active revision sets and reconcile projections;
6. verify all retained SQLite blob references exist;
7. enable scheduling only after checks pass.

Agents may reconnect during an outage, but scheduling remains closed until restore validation and
durable ownership reconciliation complete.

### Future online backup

An online backup may use SQLite's backup API plus immutable content-addressed blobs. It still needs a
recorded consistency boundary: snapshot the database, capture Git refs, then copy every blob reachable
from that snapshot. Blobs created concurrently can be harmless extras; a blob referenced by the
database snapshot may not be missing. This is later work, not a reason to delay the offline contract.

## Corruption handling

- On an SQLite open, migration, quick-check, or foreign-key failure, stop scheduling and reject
  mutations. Preserve the database, WAL, and SHM files for diagnosis. Never rename the database away
  and initialize an empty farm automatically.
- Offer an explicit operator path to export diagnostics, restore a verified backup, or run documented
  SQLite recovery tooling against a copy.
- Missing or hash-invalid bytes behind an **active** blob reference mark the owning
  artifact/log/result unavailable and the store degraded. Historical metadata remains immutable; do
  not rewrite a build as though the artifact never existed.
- Missing bytes behind a committed expiry tombstone and released reference are intentional. The owner
  remains visible as expired and returns REST `410 Gone`; it is not a degraded-store signal.
- New uploads of the correct content may repair a missing content hash. Such a repair is logged and
  does not change provenance.
- Run bounded incremental blob scrubs as maintenance and a full scrub on explicit request. Hashing the
  entire store on every startup is not acceptable.
- Disk-full and I/O errors are persistence failures. Do not acknowledge the mutation, and do not
  convert them into a test failure.

## Cross-platform filesystem rules

- Keep blob temporary and final paths on the same filesystem so rename is atomic.
- Use lowercase hexadecimal hashes and never rely on filesystem case sensitivity.
- Do not use symlinks or junctions inside controller-owned data directories without an explicit
  platform/security review.
- Hold a data-directory ownership lock so two controller processes cannot open the same farm.
- Document antivirus/indexer interference and file-lock behavior on Windows; prove rename, backup, and
  restore on Windows, Linux, and macOS.

## Non-goals

- Multi-controller high availability or shared-network-filesystem SQLite.
- A generic event-sourced architecture.
- Storing large payloads, artifacts, or logs inside SQLite.
- Treating Git as a queue, operation journal, credential store, or results database.
- Treating SQLite as the editable source of project, fleet, role, or policy configuration.
- Arbitrary REST sorting and filtering without bounded, indexed query contracts.
- Automatic destructive repair after corruption.
- Immediate online backup, point-in-time recovery, or remote object storage in the first release.

## Collaboration and review gates

The Persistence and Migrations Expert must review a domain design before implementation when it adds:

- a mutable property or Git-backed configuration object;
- a state machine, lease, deadline, or external side effect;
- a REST mutation, ETag, idempotency key, list filter, or pagination cursor;
- a new blob owner, log stream, artifact type, or retention rule;
- a table, index, migration, or rebuildable projection;
- a new startup/recovery responsibility.

The owning domain expert defines meaning, authorization, and policy. Persistence reviews schema,
transaction, acknowledgement/release, restart, provenance representation, retention mechanics, and
query behavior; it does not take ownership of the domain. Git/Versioning and Reconciliation experts
jointly approve revision-set materialization. Agent API/SDK, Logs, and Persistence jointly approve
result/log watermarks and producer release. Logs and Platform experts jointly approve durable
log/file behavior. Docs are updated in the same change as an accepted decision.

## Open questions

1. What is the first-release retention default for builds, logs, artifacts, idempotency records,
   rejected revision sets, and audit rows?
2. What bounded writer capacity and overload status are appropriate before fleet inventory and live
   logs increase write pressure?
3. Which controller key material is portable in backup, and which must be reissued on restore?
4. What is the first released database baseline and supported direct-upgrade window?
5. What platform-specific durability guarantees are required around file and directory flush before a
   blob or backup is acknowledged?
6. How should a restored controller reconcile agents that completed work while it was unavailable but
   whose terminal result is still only in the agent's durable outbox?
