# Results and Artifacts Design

> Status: **Accepted**
> Implementation: **Partial**
> Maintainer role: [Results/Artifacts Expert](../roles/results-artifacts-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D3, D4, D9, D13, D14, D17, D18,
> D22-D28

This document is the focused design for centralized TeamCity-style build/test reporting in Vivarium.
If implementation requires a contradictory choice, the architecture decision must be updated in the
same change before code lands.

The goal is a durable chain of evidence:

```text
Git configuration revision
    -> immutable resolved build definition
    -> assigned child build + immutable machine provenance
    -> uploaded content-addressed blobs
    -> durably accepted terminal result + ordered artifact manifest
    -> versioned result-adapter projections
    -> REST resources
    -> TeamCity-shaped UI and external consumers
```

The raw result boundary is intentionally small. The controller, not the agent, understands TRX,
JUnit, test history, build problems, or matrix rows.

## Current state

Phase 1 already implements:

- an agent-side pending terminal result that survives reconnect and is retried until
  `BuildResultAccepted`;
- exact-session result fencing and first-result-wins durable finalization;
- `SUCCEEDED`, `FAILED`, `CANCELLED`, and `INFRASTRUCTURE_FAILED` raw outcomes;
- per-step exit code, timeout, and skipped fields;
- artifact collection after steps and upload to the SHA-256 content-addressed blob store;
- server-side hash verification for blob `PUT` and hash verification on agent download;
- an ordered `Artifact { path, sha256, size }` list inside the stored protobuf result;
- durable matrix/child snapshots that project child outcomes, steps, assigned-agent provenance, and
  artifact metadata;
- project/principal-owned upload plans, assignment-scoped payload reads, owned-build artifact writes,
  immutable build artifact references, and protected build-scoped downloads;
- a protected build-results page listing child outcomes, step results, and artifact downloads;
- migration v8 durable TRX projection state (`PENDING`, `NO_REPORT`, `SUCCEEDED`, `PARTIAL`, `FAILED`),
  versioned report rows, stable/fallback test definitions, and occurrences linked to immutable raw
  artifact identity/hash/path;
- a bounded deterministic TRX parser with typed safe failures and sequential restart catch-up for
  terminal Builds whose projection was absent or interrupted.

Important limitations remain:

- artifact/reference rows exist for object authorization, but retention references, policies, and
  garbage collection are not complete;
- result finalization does not yet define a complete manifest-validation and blob-pinning contract;
- only the first TRX adapter/store exists; there is no general configured adapter pipeline, JUnit,
  reprocessing/generation workflow, or cross-producer/cross-platform golden corpus yet;
- generic `FAILED` is not yet normalized into `TEST` versus `CRASH`;
- there is no durable `BuildProblem` model, test history, repeated-cell pass rate, or full test ×
  scenario projection;
- live service messages and semantic log anchors are not implemented;
- retention policies, pins, dependency references, quotas, and blob garbage collection are target
  behavior;
- current submitted definition bytes are durable, but complete Git remote/ref/commit verification is
  not yet part of result provenance;
- public REST resources for detailed projected tests/problems/history do not yet exist;
- terminal results do not carry final per-step/stdout/stderr log watermarks, so result acceptance is
  not yet a proof that every preceding log byte is durably queryable;
- controller result commit, ACK consumption by a persistent agent, and provider epilogue/reuse are
  not yet expressed as separate durable barriers.

## Core model

### Raw evidence versus projections

Vivarium stores two classes of data:

1. **Raw evidence:** terminal result envelope and `rawOutcome`, final log watermarks, ordered artifact
   manifest, raw report blobs, build log, resolved definition snapshot, Git provenance, and assigned
   machine provenance. Raw evidence is immutable.
2. **Derived projections:** normalized tests, occurrences, problems, summaries, trends, and matrix
   cells, including a versioned `failureClass`. Projections are deterministic products of raw
   evidence plus an exact configuration revision, adapter version, adapter-settings hash, and
   projection generation. They may be rebuilt without asking the agent to rerun while their raw
   inputs remain available.

An adapter bug must be repairable by re-projecting retained raw reports. It must never require
rewriting the original terminal result or pretending a newer interpretation was the one originally
stored.

### Proposed durable entities

| Entity | Identity and purpose |
|---|---|
| `BuildResultRecord` | One immutable accepted raw terminal result per child build. Stores `rawOutcome`, status text, accepting session, result digest, final per-stream log watermarks, and finalization time. |
| `ArtifactManifestEntry` | Ordered entry owned by one child build: stable build-scoped artifact id, ordinal, matched rule id, normalized display path, blob SHA-256, size, kind, media type, source step, visibility, and availability/tombstone state. |
| `BlobReference` | Durable reference from a retained owner to a verified blob. It is the unit that prevents garbage collection. |
| `ResultProjection` | Append-only adapter generation identified by build, input artifact, configuration revision, adapter id/version, adapter-settings hash, projection schema version, generation, state, and deterministic output digest. |
| `Test` | Stable logical test identity within a declared test-source namespace. It is not one execution. |
| `TestOccurrence` | One observed attempt of one test in one child build and one report/test run. Preserves normalized and native values and references the exact projection id/generation that produced it. |
| `BuildProblem` | One typed problem occurrence attached to a child build, step, adapter, artifact, or infrastructure phase. Adapter-derived problems reference the exact projection id/generation; raw lifecycle problems do not pretend to come from an adapter. |
| `BuildLogReference` | Immutable log stream identity plus optional start/end positions used by steps, tests, and problems. The Logs Expert owns storage and transport. |

The matrix build owns only aggregation metadata. It references child-build entities; it does not copy
them.

## Terminal-result finalization and ACK boundary

### Agent order

For the Phase 1 files-out contract, the agent performs this order:

1. Finish all eligible steps, including allowed diagnostic collection steps.
2. Finish both log streams for every step and persist a final exclusive byte watermark for each
   `(step_index, stdout|stderr)` stream. A stream with no bytes has watermark zero.
3. Resolve declared artifact rules under the build work directory.
4. Upload each content blob idempotently by SHA-256.
5. Persist the complete terminal result, final log watermarks, and ordered manifest locally.
6. Send every log chunk through those watermarks, then send the terminal result on the currently
   fenced session. The agent retains a bounded local spool until result acknowledgement.
7. Retain and retry the same pending result and any requested missing log ranges/content across
   disconnects until the matching
   `BuildResultAccepted` arrives.
8. Delete the local pending-result/log-spool record only after that acknowledgement, then report
   itself idle.

The agent does not parse reports or decide test outcomes.

### Controller validation

Before accepting a terminal result, the controller validates:

- current authenticated agent and session fencing;
- child-build ownership and a lifecycle state that may accept a terminal result;
- build id and accepting session consistency;
- supported enum values and structurally valid step indices;
- exactly one step result per assigned step, or a documented compatibility rule for an older agent;
- one final exclusive log watermark for every assigned step and stream, with contiguous durable log
  bytes from offset zero through that watermark;
- bounded status text, artifact count, path length, and total manifest bytes;
- normalized relative artifact paths with no root, traversal, platform alias, or duplicate path;
- non-negative sizes and valid lower-case SHA-256 identifiers;
- existence of every referenced blob and equality between manifest size and stored blob size;
- no conflicting manifest entries or impossible result combinations.

A known and verified blob may be referenced by many builds. Knowledge of its digest does not establish
permission to read it.

### Durable commit

A finalization attempt first waits for the Logs Expert's store to durably seal contiguous ranges
through every declared watermark. The returned immutable log-seal identity is then an input to one
serialized SQLite transaction, which must:

1. establish that no different terminal result has already won;
2. persist the verified log-seal identity, watermarks, and log references;
3. persist the immutable raw result, `rawOutcome`, and canonical result digest;
4. persist all ordered manifest rows;
5. create blob references that pin every manifest entry;
6. finalize child-build lifecycle/outcome and cancellation state as `result committed`, without
   prematurely declaring a persistent agent reusable;
7. complete or remove the queue claim according to the TeamCity lifecycle contract;
8. append the audit event for result acceptance;
9. enqueue idempotent projection work, without requiring adapters to finish.

Only a successful commit permits `BuildResultAccepted`. Adapter parsing and UI projection are not
part of the ACK boundary: making them synchronous would make a parser defect or large report keep the
agent occupied and retrying an already safe result.

If a result references a missing blob, the result is not acknowledged. The target protocol should
return a typed, bounded rejection/request for missing content or log ranges instead of relying on
silence; the Agent API/SDK Expert owns that additive protocol design and the Logs Expert owns durable
range validation.

### Agent release and epilogue barriers

Result durability, agent awareness, and machine reuse are separate facts:

1. `raw_result_committed` means the terminal envelope, manifest/blob references, and logs through all
   final watermarks are durable. It is the earliest point at which the controller may send
   `BuildResultAccepted`.
2. `agent_idle_confirmed` means an enrolled/persistent agent consumed that ACK, cleared its local
   pending result/spool, and reported no running or pending build on the current fenced session. Only
   this barrier makes that agent schedulable again.
3. `machine_ready` means any selected provider epilogue completed and D5's newer-generation idle
   readiness barrier passed. Only this barrier makes a managed machine reusable.

For a managed revert/destroy epilogue, the controller may deliberately begin the provider epilogue at
`raw_result_committed` without waiting to observe ACK consumption: the committed evidence is already
safe and revert may erase the agent's local pending record. This is the one explicit ACK gap. The
machine cannot return to capacity until the epilogue and D5 readiness complete. A keep/quarantine or
other epilogue whose decision depends on adapter-derived `failureClass` waits for the required
projection generation to reach `ready`; a policy based only on immutable `rawOutcome` may use the raw
commit barrier. The TeamCity and provider domains own the policy and transitions; this result domain
supplies these barriers.

### Retry and conflict behavior

- An equivalent retry for the already accepted build is acknowledged again, including after a
  controller restart.
- The canonical result digest covers `rawOutcome`, status, ordered step results, final log
  watermarks, and the full ordered artifact manifest. Session id is submission fencing metadata, not
  part of semantic result equality.
- A non-equivalent retry after finalization is rejected, logged as a security/data-integrity event,
  and never overwrites the first result.
- A result that arrives after lease-expiry INFRA finalization is acknowledged only under the explicit
  late-result reconciliation rule and cannot replace the durable INFRA result.
- Cancellation-result races retain the existing first durable terminal transition and first
  cancellation-reason rules. Reconciliation Lead review is required for changes here.

## Artifact contract

### V1 artifact rules, limits, and precedence

Artifact rules are an ordered, Git-versioned list evaluated against normalized forward-slash paths
relative to the build work directory. A v1 rule has only:

- required stable `id` matching `[A-Za-z][A-Za-z0-9_.-]{0,63}`;
- one `include` or `exclude` glob;
- for includes, optional `kind`, `visibility`, and `mediaType` metadata;
- for a test-report include, the configured result-adapter binding by stable adapter-rule id.

V1 deliberately has no arbitrary metadata map, user-supplied artifact id, mutable label, description,
or per-file ACL. Limits are part of the first wire/storage contract: at most 64 rules; at most 512
UTF-8 bytes per glob and 16 KiB across all rule patterns; media type is at most 127 printable ASCII
bytes; a normalized artifact path is at most 1,024 UTF-8 bytes; one child manifest contains at most
10,000 entries and at most 8 MiB of serialized manifest metadata. Content-size and retention quotas
are separate Git-versioned policy and do not change these metadata limits.

Evaluation is deterministic:

1. Normalize and validate the candidate path before matching.
2. Any matching exclude rule removes the candidate, regardless of rule position.
3. A candidate with no matching include is not collected.
4. If multiple include rules match, the last declared include supplies the captured rule id and all
   metadata; metadata is not merged field by field.
5. The path appears once in the manifest. Duplicate normalized candidate paths are an error.

Rule ids must be unique within the resolved build configuration. Adapters bind to captured rule ids,
not a second round of extension/path guessing. Legacy Phase 1 `collect` globs normalize as ordered
include rules with definition-scoped ids `legacy.collect.<zero-based-ordinal>`, `kind=user`,
`visibility=normal`, and default media type for backward compatibility; newly authored
configurations use explicit ids.

### Manifest

Artifacts belong to the child build that produced them. The ordered manifest is immutable after raw
result finalization. An entry contains at least:

- stable build-scoped `artifact_id` and ordinal;
- normalized forward-slash relative path for display and lookup;
- content SHA-256 and byte size;
- media type when confidently known, otherwise `application/octet-stream`;
- kind: `user`, `test-report`, `log`, `dump`, `screenshot`, or `internal`;
- producing step index when known;
- visibility classification: initially `normal` or `sensitive`;
- raw collector metadata needed for diagnostics.

The stable id is build-scoped and derived as
`art_ + unpadded-base64url(SHA-256(UTF8(build_id + NUL + normalized_path)))`, using all 32 digest
bytes. It does not depend on ordinal, content hash, or presentation metadata, so the same manifest
entry has the same id on result retry.
It is intentionally not stable across builds; cross-build identity belongs to tests or future
artifact-dependency coordinates. The controller computes and validates the id rather than trusting an
agent-supplied value.

Artifact kind and adapter selection come from the Git-versioned build configuration, not filename
guessing. The original report bytes remain a normal manifest artifact even after parsing.

Paths are presentation metadata, not filesystem authority. Downloads resolve by artifact id and
manifest membership, never by joining an untrusted path to the blob directory. Duplicate normalized
paths are rejected in v1 because they make REST lookup, ZIP export, and cross-platform display
ambiguous.

### Blob properties

- Blob identity is lower-case SHA-256 of exact bytes.
- The server hashes every upload before atomic publication under that identity.
- Blobs are immutable and deduplicated globally.
- A manifest reference is committed only after the blob exists and its recorded length matches.
- Metadata that varies by build, such as path, kind, source step, visibility, or original name, lives
  in the manifest rather than the blob.
- Blob garbage collection uses durable references plus an upload/grace fence; directory scanning is
  not the source of truth.

Build logs use the Logs Expert's chunked storage contract. A build may link its log and log ranges
from result resources, but log chunks are not silently inserted into the user artifact manifest.

Server/provider-generated evidence after agent finalization, such as a corpse snapshot, needs a
separate append-only attachment contract if it is admitted. It must not mutate the accepted agent
manifest. Whether that contract is in first-release scope remains open.

## Result adapters

### Adapter contract

A result adapter is a bounded, controller-side, deterministic transformation:

```text
(build id, input artifact id, configuration revision,
 adapter id, adapter version, adapter-settings hash, projection schema version,
 immutable raw report artifact)
    -> test runs + test occurrences + build problems + summary
```

`configuration revision` means the verified Git commit plus exact resolved-definition content hash;
`adapter-settings hash` is SHA-256 over canonical resolved adapter configuration, not raw map
iteration order. Those inputs form the immutable `projection_key`. Each processing attempt is an append-only
`generation` under that key; `(projection_key, generation)` identifies one `ResultProjection`.
Ordinary duplicate delivery reuses the existing successful generation. An operator-requested or
software-upgrade reprocess appends a generation and may atomically select it as the active projection
after success. It never deletes or rewrites the generation that historical occurrences previously
referenced.

Requirements:

- built-in adapters first; no in-process third-party plugin loading in the initial release;
- explicit selection by configuration (`type`, report paths/artifact rule, test-source identity), not
  extension sniffing;
- streaming or bounded parsing with configured file/count/text limits;
- no external entities, DTD processing, network access, or filesystem access outside the provided
  blob stream;
- invariant-culture parsing where the format specifies it;
- deterministic ids and output for identical input/settings/version;
- idempotent writes reuse or complete the same projection generation, never raw evidence;
- native values and unknown fields important for diagnostics are preserved alongside normalization;
- projection state is `pending`, `ready`, or `failed`, with a typed parse problem on failure;
- reprocessing records the new adapter version and keeps enough provenance to explain changed
  interpretation.

Every adapter-created `TestOccurrence`, test run, summary, and `BuildProblem` stores the exact
`projection_id` and generation. Consumers may request the active generation or an explicit historical
generation. Raw lifecycle problems have no projection id and declare their controller classifier
version instead.

The first adapter is TRX produced by the self-contained .NET/NUnit/Microsoft.Testing.Platform route.
JUnit follows when the Linux/nextest slice is scheduled.

### TRX-first normalized fields

A TRX projection should preserve:

- test run id/name, producer hints, start/finish times, counters, and run outcome;
- test definition identity data and execution ids;
- class/type, method, fully-qualified/native names, storage/source, and parameter display;
- each result attempt's native outcome, normalized outcome, duration, timestamps, machine/test host,
  stdout, stderr, error message, stack trace, and attachments;
- parent/child or data-driven relationships when present;
- raw XML artifact id and source location sufficient to diagnose parser behavior.

Normalized occurrence outcomes are:

`passed | failed | skipped | ignored | inconclusive | aborted | not_run | unknown`.

Never discard the native outcome string. Unknown producer values map to `unknown` and create an
adapter warning rather than being guessed as success.

### Stable Test identity

`Test` and `TestOccurrence` are separate:

- `Test` represents a logical test across builds, scenarios, agents, and repetitions.
- `TestOccurrence` represents one report result/attempt in exactly one child build.

The stable key is scoped by project and a Git-controlled `testSourceId`, then computed from the
strongest framework identity available. It must not include build id, scenario, agent, OS path,
working directory, localized display name, duration, or outcome.

Preferred identity inputs, in order, are:

1. a producer-defined stable identity proven stable across supported producer versions;
2. canonical framework/type/method identity plus a culture-invariant parameter identity;
3. canonical fully qualified native name as a marked fallback.

Every Test records `identity_quality = stable | fallback` and the adapter algorithm version. A
fallback identity is visible and may split history rather than risk silently merging unrelated tests.
The original display name is stored per occurrence, so a Turkish locale or different producer can
change presentation without changing stable history when stronger identity is available.

Multiple attempts in one report are never overwritten. Each gets its own occurrence id and attempt
ordinal; a build-level test summary may aggregate attempts but links back to all evidence.

## Failure taxonomy and build problems

Vivarium keeps three related concepts separate:

1. **`rawOutcome`:** the immutable agent/controller terminal value `succeeded`, `failed`,
   `cancelled`, or `infrastructure_failed` from the current wire model. Adapter processing never
   rewrites it.
2. **`failureClass`:** the derived D9 value `TEST`, `CRASH`, `INFRA`, `NONE`, or `UNKNOWN`, stored with
   `classifierId`, `classifierVersion`, and, when adapter-derived, exact projection id/generation.
   Cancellation remains an explicit raw terminal outcome rather than masquerading as failure.
3. **`projectionState`:** `not_configured`, `pending`, `ready`, or `failed`, separately reported for
   each configured projection and aggregated conservatively for build-list use.
4. **Build problems:** typed occurrences explaining why a build is red or incomplete.

REST and UI always expose `rawOutcome`, versioned `failureClass`, and `projectionState` as separate
fields. While adapter work is pending, a build can truthfully be terminal with a final `rawOutcome`
and `failureClass=UNKNOWN`; clients do not guess. Reprocessing can select a new active failure-class
projection, but its classifier/projection provenance remains visible and the raw value is unchanged.

Initial problem types should include:

- `PROCESS_EXIT_CODE` and `STEP_TIMEOUT`;
- `TEST_FAILURES_REPORTED`;
- `RESULT_REPORT_MISSING`, `RESULT_REPORT_INVALID`, and `RESULT_REPORT_UNSUPPORTED`;
- `PROCESS_CRASH_NO_REPORT` and `CRASH_DUMP_COLLECTED`;
- `ARTIFACT_UPLOAD_FAILED` and `ARTIFACT_MANIFEST_INVALID`;
- `QUEUE_WAIT_TIMEOUT`, `AGENT_CONNECTION_LOST`, `ASSIGNMENT_LEASE_EXPIRED`, and provider failures;
- `CANCELLED_BY_USER` as an informational terminal reason rather than a failure class.

A `BuildProblem` has a stable problem type/identity, occurrence id, severity, human summary, bounded
details, source phase/step, timestamps, and optional artifact and log anchors. Messages are display
content; automation keys on typed fields. Adapter-derived problems reference their exact
`projection_id` and generation. Controller/agent lifecycle problems instead record the responsible
classifier id/version and no fake adapter projection.

Target classification:

- a parsed report containing failed tests produces `TEST` plus occurrence-linked problems;
- a nonzero process exit with no valid configured report produces `CRASH`;
- the payload never starting, queue expiry, lost lease, provider failure, or harness-side artifact
  failure produces `INFRA`;
- report missing/invalid after the payload ran is explicit and governed by a Git-versioned failure
  condition. It is never silently treated as a passing empty test run.

The TeamCity Expert jointly owns the final failure-condition schema and build-status rollup.

## Matrix and composite projections

The ordinary child `Build` is always the result owner. The matrix/composite parent is also a `Build`,
not a parallel `MatrixBuild` resource type; it provides derived views:

- build summary across child lifecycle and failure classes;
- rows keyed by stable `Test` identity;
- columns keyed by declared scenario plus resolved machine/image provenance;
- iteration drill-down for repeated cells;
- child-owned problems, artifacts, log links, and raw report links;
- pass count/rate and duration summaries without losing individual occurrences.

A matrix cell must distinguish at least:

- passed;
- test failed;
- skipped/ignored/inconclusive;
- test absent from an otherwise valid report;
- no valid report;
- infrastructure failed before tests;
- crashed;
- cancelled;
- not scheduled/not run;
- projection pending or failed.

Absence is not success. A test omitted from one scenario is different from a child that produced no
report, and both differ from an INFRA child. Repeated cells compute rates from explicit iteration
states and show the denominator; `47/50` must not become `47/47` because three reports were missing.

The current composite outcome precedence is a coarse child-build projection. Test matrix summaries
must retain failure-class counts instead of collapsing all red states into one value.

## Logs and semantic links

Logs are streamed evidence owned by the Logs Expert. Result resources reference them using immutable
build/log identity and monotonic offsets or event ids, never mutable line numbers alone.

- A step result links to its log interval.
- A build problem links to the closest relevant interval and artifact/dump when available.
- A test occurrence may link to service-message-derived start/end anchors, but the parsed raw report
  remains authoritative.
- TRX-captured stdout/stderr remains occurrence data and links to its raw report; it is not silently
  merged into the process log.
- Service messages are untrusted progress hints and cannot create authoritative test occurrences.
- Redaction occurs before durable log publication. A result projection cannot restore secrets that
  the Logs Expert intentionally removed.

The UI must survive missing or expired log detail while retaining the typed result and explicit
retention state.

## Git configuration and provenance

The following are configuration, so their changes must flow through Git rather than mutable database
or UI-only settings:

- artifact include/exclude rules and artifact kinds;
- configured result adapters, report paths, and `testSourceId`;
- test/result failure conditions;
- artifact/result/log retention policy and quotas;
- identity algorithm overrides or migration declarations;
- matrix/scenario/repetition definitions.

Every child result retains:

- project and build-configuration stable ids;
- Git repository identity, commit id, requested ref, and verification state;
- exact submitted definition bytes and content hash;
- resolved cell definition and parameter snapshot;
- selected agent/image provenance;
- adapter id/version, projection schema version, and adapter-settings hash.

Operational actions such as cancel, rerun, pin, download, cleanup execution, or manual reprocess are
not configuration commits. They go to the append-only audit journal with actor, authorization,
target, timestamp, request/idempotency id, and result. The Git/Versioning Expert owns enforcement and
repository synchronization; this document owns the provenance that result readers require.

## REST resource contract

The Vivarium REST Expert owns final route conventions. The result domain requires equivalent
versioned resources from day one:

| Resource | Required behavior |
|---|---|
| `GET /api/v1/builds/{buildId}/result` | Immutable `rawOutcome`; separately versioned `failureClass`; per-projection `projectionState`, key/generation; configuration and machine provenance; links. |
| `GET /api/v1/builds/{buildId}/problems` | Paged typed problem occurrences with step/log/artifact links and locators. |
| `GET /api/v1/builds/{buildId}/tests` | Paged occurrences or summaries with outcome/test locators and explicit projection state. |
| `GET /api/v1/projects/{projectId}/tests/{testId}` | Stable test metadata and links to paged history. |
| `GET /api/v1/projects/{projectId}/tests/{testId}/occurrences` | Paged history filterable by configuration, scenario, outcome, revision, and time. |
| `GET /api/v1/builds/{buildId}/artifacts` | Ordered manifest metadata; no raw blob-directory exposure. |
| `GET /api/v1/builds/{buildId}/artifacts/{artifactId}` | One manifest entry and retention/download links. |
| `GET /api/v1/builds/{buildId}/artifacts/{artifactId}/content` | Build- and manifest-scoped authorized bytes with ETag, range support, safe disposition, and no hash-only authorization. |
| `GET /api/v1/builds/{buildId}/matrix` | For a composite/matrix `Build`, paged/sectioned stable-test × scenario projection with iteration summaries and child-build links. |
| `GET /api/v1/builds/{buildId}/log` | Logs Expert-owned stream/read model used by semantic links. |

Requirements common to these resources:

- browser UI and external clients consume the same REST application service; the UI does not read
  SQLite or internal singleton state directly;
- project/build authorization is checked before lookup details can reveal resource existence;
- stable opaque ids, additive schemas, consistent problem details, cursor pagination, sparse field or
  expansion support, and resumable/event resources where live data warrants them;
- terminal raw result may coexist with `projection_state=pending`; clients can distinguish durable
  completion from adapter readiness;
- `ETag`/conditional requests for immutable metadata and content;
- artifact digests may be returned as integrity metadata but never function as bearer credentials;
- a manifest reports `available`, `expired`, `quarantined`, or `corrupt`; an unavailable blob does not
  yield a misleading empty response, and `corrupt` is never relabeled as intentional expiry;
- list endpoints are bounded and never return unbounded test output, stack traces, or artifact trees;
- every mutating result operation, such as pin, cleanup, or reprocess, is separately authorized and
  audited. Configuration-changing mutations must instead produce a Git change through the
  Git/Versioning workflow.

There is no separate public `matrix-builds` resource: a matrix parent is a `Build`. There is also no
user-facing `/blobs/{sha256}` authority. Agent blob upload remains an authenticated Agent API/data
plane operation; every user-facing list, metadata read, and download starts from an authorized build
and resolves an artifact id through that build's manifest.

## UI handoff

All UI work goes through the UI Expert. The results domain supplies API resources and semantic view
states for a TeamCity-shaped experience:

- Build Overview: status, immutable raw outcome, versioned failure class/projection state, revision,
  agent/image provenance, steps, and problems.
- Tests: summary counters, paged occurrences, failure details, history, raw report, and log links.
- Artifacts: ordered/tree presentation, size/kind/availability, individual download, and later
  authorized archive download.
- Build Log: step and problem anchors supplied by the Logs Expert.
- Matrix: stable tests as rows, scenarios as columns, repetition rates, precise missing/infra/crash/
  cancellation states, and child-build drill-down.
- Composite: child status/provenance without flattening child evidence.

The API, not React components, owns status rollup and identity. The UI Expert owns Workbench parts,
routes, commands, accessibility, virtualized tables, colors, icons, and responsive behavior. Result
states must always have textual labels; color alone is insufficient.

## Authorization, retention, and downloads

### Authorization

Initial permissions should distinguish:

- view build metadata/results;
- view test details and output;
- list/download normal artifacts;
- download sensitive artifacts;
- pin/unpin builds;
- request projection reprocessing;
- configure or execute cleanup.

The User Roles Expert maps these permissions to TeamCity-shaped roles. Agent tokens upload only for
owned assignments under the Agent API contract; submit/CI tokens do not inherit AgentExplorer exec or
administration permissions.

All user-facing downloads are resolved through project -> build -> manifest-entry membership. Safe
filenames are emitted in `Content-Disposition`; content type is conservative; `nosniff` and range
handling apply. Future archive browsing must not extract attacker-controlled archives on the server.

### Retention

Retention configuration is Git-versioned and inherited through the TeamCity project/configuration
model. The store tracks why an object is retained:

- retained build metadata/result;
- artifact manifest reference;
- pinned build;
- build-chain/artifact dependency;
- quarantined failure evidence;
- active upload/projection grace window;
- explicit legal/operator hold when added later.

Cleanup is serialized, auditable, restart-safe, and dry-run/explainable before deletion. It removes a
blob only when no durable reference or grace fence remains. Shared blobs survive while any owner
retains them. A retained build cannot silently lose referenced bytes under the default policy.

Intentional cleanup has only two honest shapes:

1. **Expire bytes but retain artifact metadata.** One serialized transaction verifies the applicable
   Git policy, writes an `ArtifactTombstone` containing artifact id, digest, size, expiry time, policy
   revision, cleanup operation id, and actor, changes availability to `expired`, and removes that
   artifact's blob reference. Physical blob deletion occurs later only when the global reference count
   is zero and the grace fence has elapsed.
2. **Remove the retained build/result entirely.** Cleanup transactionally removes its manifest and
   projections/references as one domain aggregate. The append-only audit journal retains the cleanup
   operation and target identity, but REST does not leave a live manifest entry that points nowhere.

If a manifest/reference still says `available` but the blob is absent or fails hash/size validation,
that is storage corruption. The controller marks the entry `corrupt`, emits a high-severity typed
problem/audit event, prevents download and re-projection from that input, and starts the storage
recovery path. It must never manufacture an expiry tombstone to hide unexplained loss.

Each projection exposes `reprojectionAvailability` independently from `projectionState`:

- `available` when every immutable input artifact remains verified and readable;
- `unavailable_expired` when an audited tombstone intentionally removed an input;
- `blocked_corrupt` when a required referenced input is unexpectedly missing or invalid;
- `inputs_incomplete` for a projection definition whose required evidence was never accepted.

An already materialized projection may remain readable after audited raw-report expiry, but it is
explicitly no longer reproducible and cannot gain a new generation. Default policy keeps raw report
evidence for as long as its retained projection/build; shorter raw retention must be an explicit Git
choice visible in REST and UI.

Artifact count, per-file size, per-build total size, report parse size, captured output, and retention
age/count all need explicit limits before public release. On limit breach, preserve a typed problem
and as much safe evidence as policy allows; never truncate a report and then parse it as authoritative.

## Cross-platform fidelity

The same report must retain the same meaning on Windows, Linux, and macOS:

- store times as UTC instants with source offset/raw text when supplied;
- store duration in an integer precision sufficient for the source format, plus its raw value when
  conversion can lose fidelity;
- parse XML encodings according to the document and preserve invalid-input diagnostics without
  attempting permissive unsafe recovery;
- keep native line endings and exception/stack text in raw evidence; normalize only the UI view;
- normalize artifact display paths to forward slashes while retaining raw producer path metadata;
- never key a test on absolute work directory, drive letter, path separator, path case, localized
  display text, host name, or process id;
- do not lowercase case-sensitive test identifiers or collapse Unicode distinctions without a
  documented, versioned identity rule;
- map framework outcomes explicitly and preserve unknown native values;
- treat missing permission/attachment/path data as partial fidelity, not as failure of every result;
- test adapters with real producer fixtures from supported .NET/MTP/NUnit versions on all three OSes.

The Platform Expert approves platform-specific collection and fixture coverage before an adapter is
declared cross-platform.

## TeamCity prior art and deliberate differences

Vivarium borrows these TeamCity boundaries:

- A Build exposes lifecycle/state separately from tests, problem occurrences, and artifacts. A
  composite build aggregates child builds rather than becoming their execution owner.
- Tests are exposed as test occurrences associated with a build, while build problems are separate
  typed occurrences.
- Artifacts are build-owned, browsable/downloadable through build-scoped resources, and usable by
  later artifact dependencies.
- Retention protects pinned and dependency-referenced builds/artifacts rather than deleting purely by
  file age.

Primary references:

- [TeamCity Build REST model](https://www.jetbrains.com/help/teamcity/rest/build.html)
- [Manage Tests and Build Problems](https://www.jetbrains.com/help/teamcity/rest/manage-tests-and-build-problems.html)
- [Build Artifacts](https://www.jetbrains.com/help/teamcity/build-artifact.html)
- [Get Build Artifacts through REST](https://www.jetbrains.com/help/teamcity/rest/manage-finished-builds.html#get-build-artifacts)
- [Build and Artifact Dependencies](https://www.jetbrains.com/help/teamcity/build-dependencies-setup.html)
- [TeamCity Data Clean-Up](https://www.jetbrains.com/help/teamcity/teamcity-data-clean-up.html)

Vivarium deliberately differs where its matrix and immutable evidence require it:

- a matrix is a first-class test × scenario projection with immutable resolved-machine provenance;
- raw artifacts use globally content-addressed bytes but remain authorized through build ownership;
- result adapters are explicit, controller-side, versioned projections over immutable raw files;
- missing reports, infrastructure failures, crashes, cancellations, and absent tests remain distinct
  matrix states;
- Git revision and exact resolved definition provenance are mandatory parts of historical meaning.

## Staged delivery

### Stage 0 — existing Phase 1 base

Keep the current reconnect-safe result ACK, durable raw protobuf result, verified blob store, child
artifact projection, protected download, and assigned-agent provenance green while evolving the
model additively.

### Stage 1 — ingestion and REST foundation

- Normalize immutable result/manifest/blob-reference rows.
- Add final per-stream log watermarks, durable log seals, missing-range recovery, and the explicit
  `raw_result_committed` / `agent_idle_confirmed` / `machine_ready` barriers.
- Validate blob presence, size, paths, counts, and step coverage before durable acceptance.
- Define canonical result digest, exact retry equivalence, conflict audit, and typed missing-blob
  response.
- Add the bounded v1 artifact-rule model, deterministic precedence, stable rule/artifact ids, quotas,
  and reference-aware retention foundations.
- Add build-scoped REST result, problem, manifest, artifact metadata/content, and projection-state
  resources before replacing the UI.
- Preserve protocol backward compatibility with a stale previous-release agent.

### Stage 2 — TRX and build problems

- Add Git-configured result-adapter/failure rules and complete Git provenance.
- **Partially implemented:** bounded TRX adapter plus durable report, test-definition, occurrence, and
  build projection-state rows with restart catch-up. Cross-platform producer fixtures remain.
- Persist the remaining general `ResultProjection` generation model and `BuildProblem`.
- Establish stable/fallback test identity, full projection-key/generation identity, and adapter
  algorithm versioning; link every derived occurrence/problem to its generation.
- Normalize `TEST` versus `CRASH` without regressing existing `INFRA`/cancel semantics.
- Expose paged test/problem/history REST resources.

### Stage 3 — matrix and UI handoff

- Build the stable-test × scenario projection, including repetitions and precise missing states.
- Add semantic step/problem/test log anchors with the Logs Expert.
- Hand API-backed view models to the UI Expert for Workbench build/results/matrix screens.
- Add adapter reprocessing and projection-version diagnostics.

### Stage 4 — breadth and operations

- Add JUnit/nextest and other scheduled adapters without weakening the common contract.
- Add build-chain artifact dependencies and their retention references with the TeamCity Expert.
- Add pins, cleanup explain/dry-run, archive download, trends, mute/investigation projections, and
  operational quotas as roadmap slices justify them.

## Verification plan

### Logic and adapter tests

- TRX golden files from Windows, Linux, and macOS producers, including MTP/NUnit variants;
- parameterized tests under multiple cultures and path layouts;
- duplicate names, retries/attempts, skipped/inconclusive/unknown outcomes, attachments, stdout,
  stderr, errors, stacks, timestamps, and duration precision;
- empty, truncated, malformed, XXE/entity, deeply nested, oversized, and high-count reports;
- stable identity and fallback behavior across adapter versions;
- deterministic re-projection from the same evidence.

### Persistence and protocol tests

- crashes before upload, between uploads, before result send, before/after database commit, and
  before/after ACK delivery;
- delayed/out-of-order/missing log chunks, restart with an unsealed log range, and watermark retry;
- enrolled-agent ACK-loss keeps the agent occupied, while managed revert after durable commit follows
  the explicit ACK gap and still requires D5 readiness before reuse;
- controller restart followed by identical retry;
- conflicting retry, superseded session, cancellation race, and lease-expired late result;
- missing blob, incorrect size, duplicate/colliding path, invalid digest, quota breach, and partial
  manifest;
- transactionally pinned blobs surviving concurrent cleanup;
- first-result-wins and child-build ownership after restart.

### REST and authorization tests

- anonymous, wrong-project, submit-only, normal-reader, sensitive-reader, and administrator access;
- no existence leak before authorization;
- artifact ETag, range, safe filename, unavailable state, and manifest membership;
- bounded paging/filtering for occurrences and matrix rows;
- matrix projection addressed as a `Build`, never a parallel matrix resource;
- immutable `rawOutcome` independently visible from versioned `failureClass` and
  `projectionState=pending|failed`;
- every artifact download begins at build/manifest membership; a digest alone grants nothing;
- immutable response identity across restart and additive schema compatibility.

### Matrix and retention tests

- test absent versus report absent versus infra/crash/cancel/not-run;
- repeats with missing iterations retain the declared denominator;
- child artifacts and occurrences are not copied to the composite;
- shared blobs, pinned builds, dependency references, upload grace, expiration, and cleanup restart;
- audited expiry tombstone versus complete audited removal versus unexpected corruption;
- expired inputs make re-projection explicitly unavailable, while corruption blocks it and raises a
  problem;
- cleanup explains every keep/delete decision and leaves no dangling available link.

## Invariants

1. First valid durable terminal result wins; retries cannot rewrite it.
2. ACK follows durable logs through terminal watermarks plus raw result, manifest, and blob-reference
   commit, not adapter completion.
3. Persistent-agent reuse waits for ACK consumption and idle confirmation. Managed epilogue may use
   the explicit earlier raw-commit barrier, but reuse still waits for D5 readiness.
4. Raw evidence and `rawOutcome` are immutable; every `failureClass` and derived interpretation names
   its classifier/projection version, settings/configuration identity, and generation.
5. Every artifact belongs to one child manifest even when its bytes are globally deduplicated.
6. Every test occurrence belongs to one child build and one stable or explicitly fallback Test and
   references its exact projection generation.
7. A composite/matrix parent is a `Build`; its views reference children and never flatten away
   provenance.
8. Missing evidence is explicit and never normalized to success.
9. Configuration meaning and result provenance are tied to Git and exact definition bytes.
10. Blob identity never grants read authorization.
11. Retention/cleanup is reference-aware, auditable, and race-safe; intentional expiry is a
    tombstone/removal, while unexplained referenced-byte loss is corruption.

## Non-goals

- Parsing test reports on agents.
- Treating service messages as authoritative final test results.
- A general package registry or arbitrary artifact-repository implementation in the first slice.
- Mutable correction of historical raw results.
- A data warehouse, unlimited analytics, or cross-controller federation.
- First-release support for arbitrary third-party adapter plugins.
- Server-side extraction of untrusted archives for ordinary downloads.
- UI implementation, global REST conventions, general logging infrastructure, or Git synchronization
  inside this domain.

## Open questions

1. Which TRX identity fields are demonstrably stable across the selected MTP/NUnit producers and
   versions? Which configurations must declare `testSourceId` or an identity mapping?
2. Should report parse failure always fail the build, or can a Git-versioned failure condition retain
   process success while surfacing a red/amber build problem?
3. Are server/provider attachments after agent terminal finalization needed in the first release? If
   yes, what append-only owner and ACK do they use?
4. What are the first-release artifact/report/log quotas and inherited retention defaults?
5. Does sensitive-artifact classification require per-entry ACLs, or is a separate project-level
   permission sufficient initially?
6. Which fields are frozen in the public v1 REST representation before the React UI begins consuming
   it?
7. How long are raw reports retained after normalized occurrences, and may metadata survive deliberate
   artifact expiration?
