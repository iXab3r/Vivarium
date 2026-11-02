# Required TeamCity historical-data import

Owner decision, 2026-10-31: import retained build history, logs and artifacts into Vivarium. Keeping
only an external archive does not meet the TeamCity shutdown requirement. This is a workstream scope
decision; production changes, source deletion and final cutover are not performed by this document.

TeamCity and Vivarium are expected to coexist during migration. Perform one initial full import and
support explicitly requested repeatable diff-imports afterward. This is a one-way import capability,
not continuous or bidirectional synchronization. The conflict policy below also applies when importing
configuration/catalog entities; importing history never activates a live definition or schedule.

## Inventory before estimating

The current census includes at most three recent builds per current configuration. Enumerate all
retained builds with complete pagination, including archived projects and historical configurations
that are no longer in the current catalog. Separate current configuration identity from historical
identity. Inventory available log streams, artifact trees (including necessary hidden artifacts), test
evidence, sizes, retention/pins and build/chain references. Determine the exact source export paths and
permissions. Do not infer total storage or migration duration from job counts or recent-build samples.

Verify which historical properties can actually be exported by the installed CLI. Its redacted
settings summaries are not a complete export; a missing CLI capability needs a supported extension or
an explicitly selected source export path. Never bypass secret redaction to recover credentials.

## Target meaning and identity

- Assign Vivarium identities with a unique `(source installation, TeamCity build ID)` mapping; keep
  original IDs, numbers, project/configuration identities, names and old links as provenance.
- Preserve states/outcomes, original timestamps, branch/source revisions, available definition
  evidence, triggering identity, Agent snapshots, dependency-build references, test results and
  statistics where present. Identify unavailable fields explicitly. Never invent an original C#
  definition or a verifiable source revision for an old run without evidence.
- Preserve logical artifact paths, names and bytes; physical storage may deduplicate by content hash.
  Logs retain stream/order/encoding evidence and are accessible through ordinary historical build
  views. Readability must not require rerunning old scripts or contacting the old server.
- Historical Agents/users/configurations may be immutable imported references; importing them must
  not enroll machines, create live credentials or activate retired build definitions.
- Mark origin as imported TeamCity history. Record the importer and current import time separately
  from original run/actor timestamps. Do not fabricate native execution or backdate audit events.
- Preserve historical relationships and identifiers separately from any current C# definition.
  Importing a completed build never schedules work, publishes packages or replays external effects.

## Transfer and recovery

Use durable per-object checkpoints and bounded streaming. Retry imports idempotently without duplicate
builds, test rows or artifact links. Verify downloaded size and a strong content hash; an exporter-owned
hash is transfer evidence and must not be called a source-authenticated hash unless supplied/proven by
the source. Reconcile manifests and object references before marking a run imported-complete.

## Diff-import and simple conflict policy

Identify an imported entity by `(source installation, entity kind, source ID)`, retaining its stable
Vivarium mapping. A repeat import adds new entities, skips unchanged content, and reports changed
entities before applying the selected conflict policy. Names and build numbers are not identity keys;
native Vivarium runs remain separate even when their names or numbers match a TeamCity run.

Every import selects one of two policies for matching entities with different content:

- **Keep Vivarium:** preserve the target entity's complete existing imported snapshot.
- **Use TeamCity:** replace the target entity's complete imported snapshot with the source snapshot.

The unit is a complete entity snapshot, not individual fields. For a historical build this includes its
imported metadata, log manifest and artifact manifest/references. Replacing that snapshot does not
delete native Vivarium runs or their relationships. Stage and verify required bytes before publishing
the new snapshot; an interrupted copy must not expose a half-replaced build. Reuse unchanged verified
content-addressed bytes instead of downloading them again where source identity/integrity evidence
permits it.

Record the selected policy and outcomes: added, unchanged, replaced, kept, failed and source-unavailable.
An intentional keep-target decision is an explicit accepted difference, not a failed import or proof
that target content equals TeamCity. No field merging, three-way merge, conflict-resolution engine,
background synchronization or reverse writes to TeamCity are required.

Source cleanup/deletion does not delete already imported target history. Preserve it and report source
absence. A later diff-import must detect changed older builds as well as new IDs; do not rely only on
a monotonically increasing build-ID cursor. Per-entity snapshots and a bounded comparison manifest are
sufficient; choose the simplest supported export/scan mechanism during implementation.

During coexistence, each configuration's active trigger/publisher has one chosen system owner.
Importing metadata or history does not enable duplicate triggers or execute historical side effects.

## Coexistence and final delta

Initial bulk transfer can run while TeamCity remains active. Subsequent reconciliation must detect new
builds and changed retained metadata/pins/artifacts; a highest-build-ID watermark alone does not detect
edits or cleanup of older runs. Stop old writers at the selected cutover point, finish in-flight work,
capture and verify a final manifest/delta under the same keep-target/use-source policy, then close
import acceptance. Repeat diff-imports must remain available throughout coexistence, not just once at
the final shutdown.

Keep source data until acceptance. Missing/purged logs or artifacts are recorded as source-unavailable
with evidence, never replaced by empty successful objects. A transfer failure remains retryable and
visible; it must not look like deliberate source absence. Set target retention so the importer/GC
cannot discard imported releases while reconciliation is in progress. Apply project visibility and
secret handling to historical views and importer diagnostics; do not dump contents into evidence logs.

## Shutdown acceptance

1. Source and target manifests account for every retained build and all required relationships,
   including explicitly selected keep-target differences.
2. Each selected log/artifact snapshot has matching verified bytes/size and an accessible target
   reference; unavailable source objects and kept target versions have explicit dispositions. Nothing
   is silently dropped or reported as source-identical when it intentionally differs.
3. Original build numbers, timestamps, outcomes, Agent/source provenance and historical test details
   are shown faithfully, with imported-origin and missing-evidence indicators.
4. Restart an interrupted import and repeat a completed import without duplicating history or effects.
   Prove diff-import for a new run, a changed older run, unchanged bytes, both conflict policies,
   interrupted replacement and source cleanup without target-history deletion.
5. Browse historical chains, view logs/tests and download artifacts through Vivarium while TeamCity
   is unavailable; old links can resolve through an explicit old-to-new mapping if needed.
6. Restore a Vivarium backup into an isolated instance and repeat those reads with permissions intact.
7. Complete final-delta reconciliation and record the verified cutoff, counts, bytes, unresolved
   source-unavailable dispositions and acceptance evidence before shutting TeamCity down.
