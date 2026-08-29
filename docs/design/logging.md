# Logging, audit, and workload-output design

> Status: **Accepted**
> Implementation: **Partial**
> Maintainer role: [Logs Expert](../roles/logs-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D4, D9, D13, D14, D22-D28

This design specializes observability behavior without replacing numbered architecture decisions.

## 1. Purpose and scope

Vivarium coordinates long-running builds and remote operations across physical agents, managed VMs,
and unreliable connections. Logs must explain what happened after controller restart, agent reconnect,
checkpoint restore, user action, Git conflict, or a noisy test, while remaining safe and finite on a
developer workstation.

This design covers controller, agent, bootstrap, CLI, UI, REST, TeamCity builds, and AgentExplorer
operations. It defines event shape, correlation, observable durability, redaction, limits, retention,
transport, and evidence. Persistence owns schemas and transaction mechanics; authentication details,
REST URL naming, Git commit mechanics, final result semantics, and product UI composition remain with
their respective experts.

## 2. Current state

Phase 1 has useful fragments but no complete logging contract:

- controller services use `Microsoft.Extensions.Logging` for selected agent, heartbeat, scheduler,
  and queue-timeout events;
- the controller startup process writes controller URLs and raw admin, submit, and enroll tokens to
  the console;
- agent and bootstrap diagnostics are unstructured console text;
- the AgentHub protocol has `LogChunk { build_id, step_index, stream, data }`, but no sequence,
  acknowledgement, timestamp, resume cursor, compression, or explicit gap marker;
- `BuildTracker` concatenates received output into an in-memory string; build-log durability, retention,
  and quotas described by the architecture are not implemented yet;
- terminal build results and ownership are durable, but diagnostic and build logs are not part of the
  same crash-safe story;
- the minimal append-only SQLite audit journal records current legacy authentication/authorization,
  enrollment, Agent administration, and caller-requested build submit/cancel actions. Required success
  records share the domain transaction, fields are bounded/redacted, retries avoid duplicate success,
  and automatic lifecycle activity remains outside the journal;
- there is no public audit query/retention/export contract, REST request event policy, centralized
  agent diagnostic surface, or AgentExplorer operation-output contract.

Consequently, current console output is development evidence, not the target audit or historical-log
system. Existing raw submit/enroll token printing must not be carried into the target design. The one
permitted secret emission is the purpose-built first-run super-user token described in §8.

## 3. Invariants

1. Audit events, component diagnostics, build output, and AgentExplorer operation output are distinct
   streams with distinct schemas and authorization.
2. Caller-initiated security and configuration mutations that require audit do not succeed without
   atomically persisted minimal audit metadata; automatic domain transitions do not become audit noise.
3. All queues, files, blobs, downloads, and retained histories are byte-bounded.
4. Overflow and partial history are explicit facts, never silent truncation.
5. Redaction happens before a record crosses a process boundary or reaches durable storage.
6. Controller receipt time and per-producer sequence order distributed events; agent wall clocks do not.
7. Logging failure cannot starve heartbeat, cancellation, ownership, or terminal-result traffic.
8. Repeating healthy observations are metrics, not information events.
9. Every configuration-derived operation records its immutable definition digest and Git revision.
10. Historical output cannot be rewritten by later agent, user, configuration, or Git changes.
11. A terminal result closes output with final per-stream sequence watermarks; capacity is not released,
    reused, or reverted until each range is durably persisted or replaced by an explicit durable gap.

## 4. Four streams

### 4.1 Audit journal

The audit journal is **minimal append-only SQLite metadata** answering **which caller attempted which
security- or configuration-significant action against which target, from which revision, and with what
immediate outcome**. It covers authentication and authorization decisions, permission and token
changes, agent authorization/enablement/registration administration, Git-backed configuration
mutation, caller-requested build submission/cancellation, AgentExplorer mutating-operation acceptance,
retention-policy changes, and sensitive log exports.

Audit records are structured, compact, append-only rows and are not controlled by the diagnostic log
level. When a caller-visible success requires audit, Persistence writes the audit row atomically with
the domain mutation or durable external-operation intent through the serialized SQLite writer. If that
transaction cannot commit, the caller action fails. Denied security actions record a bounded failure
row without fabricating a successful domain mutation.

Audit is deliberately not a duplicate event-sourced history. Scheduler scans and choices, queue
claims/expiry, assignment delivery, automatic build/step state transitions, heartbeats, reconnects,
machine conveyor transitions, retention sweeps, and result projection remain in their authoritative
domain tables, build/operation lifecycle annotations, bounded metrics, or selective diagnostic events.
An accepted long-running action has one caller audit record; its progress and terminal result live in
the operation/build model unless a later security- or configuration-significant caller action itself
requires another audit row. This prevents an active farm from flooding the journal with automatic work.

Audit data is a log, not yet a compliance ledger: v1 does not promise cryptographic non-repudiation,
WORM storage, signatures, or resistance to an administrator who can edit the controller data
directory.

### 4.2 Diagnostic events

Diagnostic events explain Vivarium components themselves: controller lifecycle, session replacement,
reconciliation, scheduler failures, persistence failures, provider failures, agent reconnects,
bootstrap update failures, CLI protocol errors, and unexpected UI/API failures. They are structured
JSON records in rotating local files, with a concise human console renderer for interactive use.

Routine success, heartbeats, queue scans, polling, per-process inventory rows, and individual log
chunks are not diagnostic information events. A state transition may emit once; its repetition becomes
a metric or a coalesced summary.

### 4.3 TeamCity build output

Build output contains ordered stdout/stderr bytes per step plus controller-generated lifecycle
annotations. It belongs to the immutable child Build, not its matrix parent. Parsed TeamCity service
messages are separate progress projections; the original output remains authoritative as output and
TRX/JUnit artifacts remain authoritative as test results.

Build output is payload-controlled and may be malformed, binary, huge, forge service messages, or
contain secrets. The UI renders it as untrusted text. Registered secret values are masked before
persistence, but Vivarium cannot promise to discover secrets that were never registered.

### 4.4 AgentExplorer operation output

Read-only inventory snapshots are typed AgentExplorer data, not logs. A remote command, process-control,
software-management, file action, reboot, or state change is an Operation with bounded stdout/stderr,
domain lifecycle events, one minimal caller audit acceptance record when required, and a terminal
result. Its output uses the same chunk envelope and bounded spool machinery as build output but has
`operation_id` rather than `build_id` and independent authorization and retention.

## 5. Event envelope and taxonomy

Structured audit and diagnostic records use a common envelope. Workload chunks use the correlation,
time, source, and sequencing subset plus their byte payload.

| Field | Contract |
|---|---|
| `schema_version` | Integer version of this envelope/event payload. Additive changes do not require a new event name. |
| `event_id` | Controller-generated sortable unique ID for durable events. |
| `event_name` | Stable dotted identifier; never derive behavior by parsing `message`. |
| `stream` | `audit`, `diagnostic`, `build`, or `agent-explorer-operation`. |
| `level` | `trace`, `debug`, `information`, `warning`, `error`, or `critical`; audit is additionally outcome-classified. |
| `observed_at_utc` | Producer wall-clock observation in RFC 3339 UTC. May be skewed. |
| `received_at_utc` | Controller receipt/persistence time in RFC 3339 UTC; authoritative for cross-source display. |
| `source` | `controller`, `agent`, `bootstrap`, `cli`, `ui`, or `rest`. |
| `source_instance_id` | Controller process instance or agent session ID. |
| `source_sequence` | Monotonic unsigned sequence within the source instance or workload stream. |
| `trace_id`, `span_id` | W3C trace correlation when a request or operation has a trace. |
| `request_id` | Client-supplied or controller-generated idempotency/correlation identity. |
| `actor_type`, `actor_id` | User, token, agent, system scheduler, or provider identity; never the credential. |
| `operation_id` | Durable AgentExplorer/admin operation identity. |
| `project_id`, `build_configuration_id`, `matrix_build_id`, `build_id`, `step_id` | TeamCity correlation when applicable. |
| `agent_id`, `session_id`, `connection_generation`, `provider_id`, `provider_instance_id` | Fleet correlation when applicable. |
| `repository_id`, `git_revision`, `definition_digest` | Immutable configuration provenance when applicable. Never include credential-bearing repository URLs. |
| `outcome`, `reason_code` | Stable machine-readable result; free text is supplementary. |
| `message` | Bounded human summary with no secrets or required machine semantics. |
| `properties` | Event-specific allowlisted scalar fields with per-field size limits. |

Stable namespaces group events without requiring a universal enum:

- `security.authentication.*`, `security.authorization.*`, `security.token.*`;
- `git.change.*`, `configuration.change.*`;
- `agent.registration.*`, `agent.session.*`, `agent.reconciliation.*`;
- `teamcity.build.*`, `teamcity.step.*`, `teamcity.queue.*`;
- `agent-explorer.operation.*`, `agent-explorer.inventory.*`;
- `rest.request.*`, `controller.storage.*`, `provider.agent.*`;
- `logging.gap`, `logging.retention.*`, `logging.sink.*`.

Event names describe facts in past tense, for example `agent.session.connected`,
`teamcity.build.cancel_requested`, `agent-explorer.operation.completed`, and `git.change.rejected`.
Messages may improve without breaking consumers; names and stable fields require compatibility review.

## 6. Correlation and causality

- An inbound REST request receives or validates `traceparent` and receives a server `request_id`. The
  response returns the request ID. Untrusted incoming IDs are length- and character-bounded.
- One accepted long-running action receives an `operation_id`. Retries with the same idempotency key
  resolve to the same durable operation rather than creating duplicate audit histories.
- Matrix submission carries `request_id → matrix_build_id → child build_id → step_id`. Assignment adds
  `agent_id`, credential/connection generations, and the accepting `session_id`.
- AgentExplorer carries `request_id → operation_id → agent_id → session_id`; provider work additionally
  carries `provider_instance_id`.
- Git-backed changes carry `request_id → proposed base revision → resulting git_revision`; rejected
  conflicts retain the base and current revision without logging diff contents that may contain secrets.
- Agent output uses a per-build-step or per-operation-stream sequence. A new agent session does not
  reset that durable workload sequence. Duplicate sequences are idempotently ignored; missing ranges
  produce one `logging.gap` record.

Do not infer causality from timestamps. Snapshot-restored machines can report the past. For agent
records, retain `observed_at_utc`, `received_at_utc`, source sequence, and the known clock-skew estimate.
UI sorting defaults to controller receipt order while showing the agent-observed time where useful.
Durations use a monotonic local clock and are stored as elapsed values, not by subtracting wall clocks.

## 7. Levels

| Level | Use | Do not use for |
|---|---|---|
| `Trace` | Temporary per-message or per-state-machine detail; disabled by default | Credentials, payload bytes, every heartbeat in normal operation |
| `Debug` | Bounded troubleshooting detail, retries, matching explanations | Expected high-rate loops without sampling |
| `Information` | Startup/shutdown and durable state transitions | Polling, repeated healthy state, every REST read |
| `Warning` | Recovered degradation, retry exhaustion approaching, dropped output, stale inventory | User-caused validation errors that the API already returns |
| `Error` | An operation/request failed or state needs intervention | A failed test payload, ordinary build cancellation |
| `Critical` | Controller cannot preserve data/invariants, audit unavailable, corruption/security boundary failure | A single offline agent or failed build |

Audit events are emitted independently of level. An expected denied action is an audit outcome
`denied`; it does not automatically become a diagnostic error.

## 8. Redaction and the first-run token

Redaction is field-aware and applied at the source. The following never enter ordinary log records:

- bearer tokens, enroll tokens, submit tokens, agent credentials, cookies, authorization headers;
- passwords, private keys, secret parameter values, and configured sensitive environment values;
- request/response bodies by default;
- raw repository URLs containing userinfo or query credentials;
- complete process environments and raw command lines unless an explicit product surface is returning
  them to an authorized caller; that response is not diagnostic logging.

REST logging uses route templates rather than raw URLs and omits query strings. Exception logging uses
type, safe message, stable error code, and stack trace only after sanitization. Structured property
names are allowlisted per event; arbitrary object destructuring is forbidden. Messages and properties
have byte limits, with a visible truncation flag.

Known build secrets are exact-value masked in stdout/stderr using the resolved secret registry. Values
shorter than a safe minimum are not accepted as mask patterns because they corrupt ordinary output.
Encoding, transformed, split-across-chunk, or previously unknown secrets cannot be guaranteed masked;
the product documentation must say so. Chunk-boundary masking retains a small overlap without retaining
unbounded text.

### First-run super-user token exception

The Admin/SuperUser design may require a generated token to bootstrap the first login, following the
TeamCity experience. It is the only allowed raw-secret log emission and must satisfy all of these:

1. generated only while no durable administrator identity exists;
2. emitted once per token issuance to a dedicated local startup sink, with an unmistakable warning;
3. written neither as a structured property nor into audit, REST, browser, agent, or exported support
   logs;
4. protected by controller data-directory permissions and never printed by normal status endpoints;
5. independently revocable and bounded by the lifetime/one-time-use policy owned by Admin/SuperUser;
6. covered by a negative test scanning every other sink and endpoint for the token bytes.

The current controller also prints long-lived submit and admin tokens and a single-use enroll token.
That behavior is development-only debt: the target startup path replaces it with the single narrowly
defined super-user exception and secure token-management surfaces.

## 9. Persistence, limits, and retention

Defaults are conservative and configurable downward or upward by an administrator. Configuration
changes are Git-backed and audited. Limits apply to uncompressed bytes so compression cannot hide
unbounded ingestion.

| Stream/store | Default segment/quota | Default retention | Overflow behavior |
|---|---:|---:|---|
| Controller diagnostics | 32 MiB × 10 segments, 320 MiB total | 14 days, whichever limit comes first | Rotate; delete oldest complete segment; count deletion |
| Agent diagnostics | 16 MiB × 8 segments, 128 MiB total | 7 days, whichever limit comes first | Rotate locally; delete oldest; retain a counter/summary |
| Audit journal | 1 GiB SQLite budget with 32 MiB export segments | 365 days by default | Alert at 80/90%; reject caller actions whose success requires audit rather than silently discard |
| Build output | 256 MiB per child build and 64 MiB per step | Same lifecycle as retained build | Preserve bounded head and tail plus a gap record; never grow further |
| AgentExplorer operation output | 16 MiB per operation | 30 days by default | Preserve bounded head and tail plus a gap record; audit summary remains |
| Disconnected agent workload spool | 128 MiB global and no more than the workload's server quota | Until acknowledged or terminal retention expires | Drop oldest unacknowledged output by priority, record exact sequence gap |
| Agent diagnostic forward spool | 16 MiB | 24 hours | Drop oldest `Debug`, then `Information`; retain warning summary |

The controller exposes total and per-stream disk use and refuses quota configurations that exceed the
explicit controller data budget. Build log blobs referenced by retained builds follow D13: retention
and blob GC cannot remove a referenced log silently. Changing audit-retention policy is audited;
routine retention batches are summarized by bounded diagnostics and metrics rather than creating an
audit row per deletion. Legal/compliance retention is a future policy, not implied by these defaults.

### Crash safety

- Persistence owns the audit table, schema, migration, transaction, indexes, and WAL recovery. It
  inserts required minimal audit metadata in the same transaction as the controller mutation or
  durable operation intent. Logs owns the taxonomy, required safe fields, redaction, retention, and
  the observable rule that caller success is impossible when that atomic write fails.
- Diagnostic files have one writer per process, append newline-delimited JSON to an `.active` segment,
  and rotate through flush + close + atomic rename. Startup discards only a malformed trailing record
  and emits a recovery warning; earlier complete records remain readable.
- Build and operation chunks are idempotently keyed by workload, stream, and source sequence. The
  controller appends bounded active segments, persists the acknowledged sequence, and atomically seals
  completed segments into the blob store. A crash can replay a chunk but cannot duplicate it in the
  logical stream.
- Results owns terminal evidence semantics and persists the terminal domain result before output
  sealing. Log loss does not silently change a result outcome; Logs supplies an explicit completeness
  or gap fact for Results to present.

### Terminal watermarks and the release barrier

Every terminal build or AgentExplorer operation result carries immutable final watermarks for all logical
output streams. A watermark entry identifies workload, step or operation scope, stdout/stderr stream,
`final_sequence_exclusive`, and total produced bytes. `0` closes an empty stream. All sequence values
below the exclusive watermark must resolve to either a durably persisted output chunk or a durably
persisted gap record. The first accepted terminal result freezes these watermarks; a retry must match,
and a conflicting retry is a fenced protocol error rather than a new history.

Terminal-result acknowledgement and output acknowledgement are independent contracts. Persisting and
acknowledging a result does not acknowledge output. The agent retains each unacknowledged output range
in its bounded workload spool until the controller acknowledges that range independently. Conversely,
output acknowledgement says nothing about terminal-result acceptance. This separation lets terminal
results retain transport priority without erasing a disconnected agent's pending evidence.

Agent release, physical-Agent reuse, and provider epilogue—including checkpoint revert—cross an
explicit output-finalization barrier:

1. Results durably accepts the terminal result and its frozen final watermarks.
2. Persistence reconciles every sequence below each watermark with stored chunks and stored gaps, then
   seals/indexes the logical log and records `complete` or `complete_with_gaps`.
3. The controller independently acknowledges the resolved output ranges to the agent.
4. Only then may TeamCity/AgentExplorer release the execution lease and may the provider reuse, clean, or
   revert the machine.

The barrier is bounded so an unreachable agent or failed sink cannot hold scarce capacity forever. On
its deadline, the controller atomically persists explicit gaps for every unresolved range and marks the
log `complete_with_gaps`; only that durable fact opens the barrier. Revert must never erase the only
copy of unacknowledged output before persistence-or-gap resolution. Exact barrier deadlines and whether
an evidence gap affects a result badge are owned by TeamCity/AgentExplorer and Results respectively.

## 10. Backpressure and disconnected agents

Each producer uses byte-bounded channels, not item-count-only queues. Transport priority is:

1. cancellation, fencing, heartbeat, assignment acknowledgement, and terminal result;
2. audit/operation state acknowledgement;
3. warning/error diagnostic events and lifecycle annotations;
4. stdout/stderr workload chunks;
5. information/debug/trace diagnostics.

Agent output is numbered before enqueue. The controller acknowledges contiguous durable output ranges
independently from terminal-result acknowledgement. On disconnect, the agent persists unacknowledged
build or operation chunks in its data directory and resumes from the last output acknowledgement after
re-hello. Pool snapshot restore can rewind the local spool, so controller deduplication, immutable
terminal watermarks, and session fencing remain mandatory.

When a workload or spool quota is reached, execution continues. The spool preserves a bounded head and
rolling tail, advances sequence over omitted bytes, and later transmits an exact gap record containing
the omitted byte and sequence counts. It never blocks a child process indefinitely and never lets log
traffic delay cancellation or terminal result delivery. Repeated disconnect messages are coalesced;
reconnect count and duration are metrics.

Diagnostic channels drop `Trace`, then `Debug`, then repeated `Information`. Warnings and errors use a
small reserved capacity and a synchronous local fallback, but even that fallback has a deadline. Drop
counters and one rate-limited warning make degradation visible without creating a recursive log storm.

## 11. Component contracts

### Controller

The controller orchestrates receipt timestamps, correlation propagation, query authorization, output
finalization, and the release barrier. Persistence owns audit/log schemas, atomic transactions, WAL
recovery, indexes, and blob sealing. Logs owns taxonomy, redaction, quotas, retention, gap projection,
and completeness contracts. Results owns final evidence semantics. The controller uses structured
logging and never stores an unbounded in-memory build string. Scheduler loops expose counts/durations
as metrics and emit events only for transitions or failures.

### Agent and bootstrap

The agent owns source sequence, observation time, local redaction, bounded spool, reconnect replay, and
secure local file permissions. It never forwards full environment inventories as diagnostics. The
bootstrap remains deliberately small and frozen only after D2's gate; its diagnostics cover manifest
verification, swap, launch, exit, and backoff without printing tokens or full configuration. Any change
to bootstrap logging still requires the explicit bootstrap design discussion.

### CLI

CLI stdout is the command's stable user/result surface; diagnostics go to stderr. `--verbose` enables
information detail and `--debug` enables bounded debug detail for that invocation. The CLI does not
persist logs by default, echo raw argv, or print stored credentials. A future support-bundle option
must require an explicit destination and use the same redaction rules.

### UI

Browser console logging is development-only and has no automatic server upload. User actions are
audited at the controller after authentication, never inferred from browser telemetry. The UI carries
the REST request ID, shows gaps/truncation/staleness and source/receipt time distinctions, and treats
all workload text as untrusted. UI failures sent to the server use a tiny allowlisted schema with no
DOM snapshot, form values, URL query, local storage, or cookies.

### REST

One completion event per request records route template, method, status class/code, elapsed time,
request/response byte counts, authenticated actor ID/type, request/trace IDs, and stable error code.
It does not record authorization/cookie headers, body, query string, raw URL, or arbitrary headers.
Health checks and successful high-rate polling are metrics or sampled debug events. Authentication
failures and the caller/security/configuration actions enumerated by the audit catalog additionally
emit minimal audit metadata; automatic domain progress does not. The REST Expert owns exact routes and
status semantics; this contract applies from day one.

### TeamCity builds and AgentExplorer operations

Build steps and remote operations share a chunking primitive but never share identities, permissions,
history pages, or retention assumptions. Every stream records stdout/stderr separately and injects
controller lifecycle annotations without pretending those annotations came from the payload. Remote
command text and arguments are sensitive: audit stores an operation kind and a safe command digest or
administrator-approved summary, not the raw command by default.

## 12. Query, live view, and download surfaces

The REST contract must support, subject to the User Roles design:

- cursor-paginated audit queries by receipt time, actor, action, target, outcome, request ID, operation
  ID, build ID, agent ID, and Git revision;
- cursor-paginated controller/agent diagnostic queries by time, source, instance, level, event name,
  agent, request, and operation correlation;
- resumable build and operation output reads by logical byte/sequence cursor, including explicit gaps;
- live tail via a bounded streaming surface that resumes from a cursor and disconnects slow consumers
  without retaining unlimited server memory;
- immutable downloads of a build/operation log or filtered audit/diagnostic export, with content type,
  generation time, applied filters, completeness/gap metadata, and source revision in a manifest.

The exact REST paths belong to the REST Expert. Queries are controller-side and paginated; the browser
does not download an entire farm history to filter locally. Sensitive diagnostic/audit downloads are
themselves audited. Build-log access follows project/build permissions; AgentExplorer output follows host
operation permissions; controller and agent diagnostics require an administrative troubleshooting
permission.

## 13. Metrics instead of noisy logs

Vivarium needs bounded in-process metrics even before an external metrics backend exists. The panel
may read controller snapshots; export formats can follow later. Required counters/gauges/histograms
include:

- connected/authorized/enabled/idle agents and session reconnects;
- heartbeat age and missed-heartbeat transitions;
- queue depth, queue wait, scheduler pass duration, compatible-agent count;
- builds/operations by state and outcome;
- log bytes received, persisted, downloaded, redacted, truncated, and dropped by stream/reason;
- current spool, segment, blob, and audit disk bytes versus quota;
- REST request count, duration, response class, and active streams by route template;
- audit persistence failures and rejected mutations;
- Git mutation latency, conflicts, and accepted/rejected counts.

Metrics use bounded labels. Build ID, agent ID, user ID, request ID, raw route, repository URL, command,
and exception message are forbidden metric labels. Those correlations belong in logs.

## 14. Git and configuration linkage

Every action derived from Git-backed configuration records:

- stable repository identity without credentials;
- resolved commit SHA, never only a branch name;
- configuration path and content/definition digest;
- base revision for a proposed mutation;
- resulting commit SHA for an accepted mutation;
- conflict/rejection reason code for a rejected mutation;
- actor, request ID, and server-side operation ID.

The audit event does not duplicate a complete diff or file contents. Git remains the source of truth
for configuration history; the audit journal explains who asked Vivarium to create or apply the commit
and what the system did. Reads that merely inspect public configuration need no audit event; secret or
administrative exports do.

## 15. Cross-platform storage locations and permissions

All paths derive from the explicitly selected component data directory (D19), never the current working
directory once installed:

```text
<controller-data>/logs/diagnostic/
<controller-data>/logs/exports/
<controller-data>/blobs/                 # sealed build/operation log segments
<controller-data>/vivarium.db            # audit journal and log indexes

<agent-data>/logs/diagnostic/
<agent-data>/spool/workloads/
<agent-data>/spool/diagnostic/
```

Default installed roots are decided by Platform Expert and installer design: `%ProgramData%` on
Windows system installs, `/var/lib` plus `/var/log` policy on Linux system installs, and
`/Library/Application Support` plus the platform log policy on macOS system installs; per-user and
portable modes use their explicit data root. Code never assumes `/tmp`, path casing, rename behavior
across volumes, or POSIX permissions on Windows.

Controller and agent service accounts receive the minimum directory access; ordinary local users do
not receive audit, token-bearing startup, diagnostic, or spool read access. Creation verifies or
repairs ownership and rejects a dangerously broad directory when safe repair is impossible. Rotation
and sealing use same-volume atomic rename. Windows sharing flags permit the active writer while
downloads read only closed segments. Linux/macOS set restrictive modes and do not follow symlinks.

## 16. Required evidence

At minimum, implementation needs deterministic tests for:

### Tier 1

- envelope serialization and additive schema compatibility;
- event-name and required-correlation validation;
- field allowlists, token/header/cookie/URL/exception redaction, message/property limits;
- chunk-boundary registered-secret masking;
- level filtering, metric substitution, coalescing, and rate limiting;
- byte quotas, head/tail preservation, gap accounting, rotation, age/size retention, and disk-budget
  thresholds;
- minimal audit inclusion/exclusion: caller/security/config actions are recorded while automatic queue,
  build, heartbeat, scheduler, and conveyor transitions do not flood audit;
- atomic caller mutation plus audit metadata behavior, using the transaction contract supplied by
  Persistence;
- cursor pagination, stable ordering, authorization filters, and export manifests;
- Git revision/digest linkage and absence of diff/credential material;
- metric label-cardinality restrictions.

### Tier 2

- controller crash before/after the atomic caller mutation plus audit commit;
- controller crash during active log append/seal and recovery of only a malformed trailing record;
- agent disconnect/reconnect replay, duplicate chunk idempotency, missing-range gap, and session fencing;
- terminal final-watermark freezing, empty streams, matching retries, and rejection of conflicting
  terminal watermarks;
- independent result and output acknowledgements, including a result accepted before its output;
- release/reuse/revert blocked until all final ranges are persisted, then opened by complete logs or a
  durable explicit-gap decision at the bounded barrier deadline;
- restart after terminal-result acceptance but before output finalization, proving the recovered machine
  lease cannot cross the epilogue barrier early;
- snapshot-style agent time reversal with stable controller ordering;
- slow/unavailable controller sink without heartbeat, cancellation, or terminal-result starvation;
- process stdout exceeding step/build quotas without deadlock or outcome corruption;
- REST request correlation propagation and negative proof that body, query, auth, cookie, and secret
  bytes appear in no sink;
- first-run super-user token present only in its dedicated startup sink and absent from audit,
  diagnostics, REST, UI payloads, agent output, and support exports;
- restart-safe retention and audit-full behavior rejecting new mutations;
- mixed-version protocol behavior when sequence/ack/gap fields are introduced: stale agents must remain
  accepted within the supported minor version and degrade explicitly.

### Platform and operational evidence

- file permissions, symlink/reparse-point defense, atomic rotation, open-file sharing, service restart,
  and disk-full behavior on Windows, Linux, and macOS;
- concurrent output and UTF-8 fragmentation on every supported RID;
- a real installed-service smoke test proving the documented paths and identities;
- load evidence showing bounded memory and disk at configured rates and a slow live-tail consumer;
- repository-wide `dotnet build` and `dotnet test` before code hand-off, plus the applicable tier and
  cross-platform gates from [`DEVELOPMENT.md`](../DEVELOPMENT.md).

Tests inspect emitted bytes and durable records, not only logger mock calls. Every intentional drop is
asserted through both a gap marker and a metric.

## 17. Non-goals

- Requiring Elasticsearch, Loki, OpenTelemetry Collector, or any external service.
- Shipping a general observability platform or arbitrary log-query language.
- Recording every inventory row, heartbeat, scheduler pass, successful polling request, or UI click.
- Guaranteeing redaction of secrets Vivarium was never told were secrets.
- Treating service messages, logs, or operation output as authoritative test results or machine state.
- Duplicating automatic queue/build/machine state transitions into the audit journal.
- Full-text indexing all workload output in SQLite.
- Tamper-proof compliance logging, distributed tracing storage, or cross-controller aggregation in v1.
- Logging raw remote commands, request bodies, environments, or Git diffs for convenience.

## 18. Evidence behind the design

- D4 requires bounded log chunks, reconnect fencing, durable ownership/result handshakes, and prioritizes
  reliable behavior across checkpoint restore; logging must compose with those guarantees rather than
  compete with them.
- D13 makes blob retention reference-counted and disk budgets visible; build-log segments therefore
  belong to retained builds and the same GC contract.
- D14 makes child builds, immutable assigned-agent provenance, and TeamCity service messages the work
  model; build output follows the child and service messages remain non-authoritative progress.
- D17 makes submitted Git-tracked configuration authoritative; audit records link to resolved revision
  and definition digest instead of duplicating version history.
- D19 gives every component an explicit data directory and portable deployment; log paths derive from
  that root rather than assuming one operating system.
- The current code demonstrates the immediate gaps: unstructured token-bearing startup output,
  unstructured agent/bootstrap console diagnostics, and non-durable in-memory concatenation of
  sequence-free `LogChunk` messages.

## 19. Collaboration boundaries

- Agent API/SDK Expert must review any AgentHub sequence, acknowledgement, gap, or diagnostic-forwarding
  change and preserve backward compatibility.
- TeamCity and AgentExplorer Experts define workload lifecycle semantics; this design defines their output
  and audit envelope, not their domain state machines.
- REST Expert defines endpoints and media types while preserving safe request logging, cursors, and
  authorization described here.
- Git/Versioning Expert defines repository layout, branch/commit flow, and conflicts while supplying
  immutable revision fields to audit.
- Admin/SuperUser and User Roles Experts define identity, token lifetime, and access policy; Logs Expert
  prevents leakage and requires denied/accepted actions to remain attributable.
- Persistence Expert owns audit/log schemas, SQLite transactions, indexes, migrations, WAL recovery,
  and blob atomicity; Logs Expert owns taxonomy, redaction, quotas, retention, and observable failure
  behavior rather than prescribing the storage implementation.
- Results Expert owns terminal evidence semantics, result status/badges, and what incomplete output
  means to a consumer; Logs Expert supplies frozen final watermarks and complete/gapped facts.
- UI Expert presents output, gaps, time skew, and exports without inventing client-side truth.
- Platform Expert proves secure storage and process/output collection on Windows, Linux, and macOS.
- Docs Expert keeps operational limits, locations, troubleshooting, and event catalog current.
- Test Steward chooses the lowest sufficient tier; Reconciliation Lead audits coverage across all
  emitters and consumers when the implementation spans the system.

## 20. Open questions

1. Does v1 require audit retention beyond 365 days, or is an administrator-configured policy enough?
2. Should audit records gain a per-segment hash chain before multi-user deployment, or remain explicitly
   non-tamper-evident until a compliance requirement exists?
3. What build-log quotas are appropriate after real corpus measurements, and should a project be able
   to lower but not raise the controller-wide ceiling?
4. Which registered secret encodings, beyond exact UTF-8 values, should be masked without creating false
   confidence or corrupting normal build output?
5. Should disconnected agent diagnostics be uploaded automatically at warning-or-higher, or only via an
   explicit support-bundle operation?
6. Are audit exports allowed to include source IP addresses, given privacy and deployment needs, or is
   authenticated actor plus request correlation sufficient?
7. What bounded output-finalization deadline should TeamCity builds and AgentExplorer operations use before
   unresolved ranges become durable gaps, and may configurations lower it?

Until these are decided, implementations must keep the narrower safe behavior: no extra secret
emission, no unbounded retention, no automatic full diagnostic upload, no raw command/request logging,
and no claim of tamper-proof audit.
