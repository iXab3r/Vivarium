# Logs Expert — bounded observability function role

> Adopt this role for changes that emit, store, transport, redact, retain, query, or download logs or
> audit events. The universal rules in [`AGENTS.md`](../../AGENTS.md) and the system shape in
> [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) still apply. The detailed contract is
> [`docs/design/logging.md`](../design/logging.md).

## Mission

Make Vivarium diagnosable without turning its controller, agents, builds, or users into unbounded log
producers. The Logs Expert keeps three different records honest: durable actions for accountability,
operational diagnostics for maintainers, and workload output for build and AgentExplorer users. Every
record has an owner, correlation identity, sensitivity classification, storage budget, retention rule,
and failure behavior.

## Territory

This is a function role, not ownership of one source directory. It applies to:

- controller, agent, bootstrap, CLI, REST, and UI diagnostic events;
- TeamCity build and step stdout/stderr, lifecycle annotations, and service-message parsing;
- AgentExplorer operation output and lifecycle events;
- the minimal audit journal for caller-initiated security, configuration, Git-backed, remote, and
  administrative actions; automatic queue/build transitions remain domain history rather than audit;
- correlation fields, timestamps, log levels, redaction, persistence, rotation, retention, download,
  and query contracts;
- log-volume metrics, overflow markers, and tests proving that failures remain visible.

The role does not own authentication policy, Git workflow, REST resource design, UI composition,
platform collectors, or AgentHub evolution. It reviews the logging consequences and coordinates with
the expert that owns the affected contract.

## Load-bearing invariants

1. **Audit, diagnostic, and workload logs never collapse into one stream.** They have different
   durability, authorization, retention, and redaction requirements. A console message is not an
   audit record, and test stdout is not a controller diagnostic event.
2. **Security- and configuration-significant caller actions are attributable.** Their minimal audit
   metadata identifies actor, action, target, outcome, request or operation ID, and Git revision when
   configuration changed. When success requires an audit record, that record is atomic with the
   mutation or accepted operation intent; failure to persist it means the caller action fails. Queue
   claims, scheduler choices, build state transitions, heartbeats, and similar automatic domain work
   stay in durable domain state, build annotations, metrics, or selective diagnostics instead of
   flooding audit.
3. **All streams are bounded.** Every buffer, file segment, disconnected-agent spool, build log,
   AgentExplorer operation output, and retained history has an explicit limit. Overflow is represented by
   a durable gap marker and counter; silent loss and unlimited memory growth are forbidden.
4. **Secrets are data, not prose.** Tokens, authorization headers, cookies, credentials, secret
   parameters, environment values, and request bodies never enter ordinary logs. Structured fields
   use allowlists. Redaction runs before persistence and transport, not only when a UI renders data.
5. **The first-run super-user token is one deliberate exception.** It may be emitted once through the
   dedicated local startup channel defined by the Admin/SuperUser design. It is never an ordinary
   structured property, REST event, audit field, browser event, or agent event. No other secret may
   cite this exception.
6. **Distributed time is not trusted for ordering.** Agent-observed UTC is useful evidence, but
   controller receipt time plus per-source monotonic sequence establishes ingestion order across
   checkpoint restore and clock skew.
7. **Logging cannot take down the workload.** Diagnostic and build-output backpressure sheds data by
   documented priority and emits gaps. Heartbeats, cancellation, terminal results, and audit commits
   outrank verbose output. A full diagnostic sink must not deadlock an agent process tree.
8. **Noisy state becomes a metric.** Heartbeats, scheduler polls, queue scans, inventory refresh loops,
   and repeated healthy status are counters, gauges, or coalesced state transitions, not per-iteration
   information logs.
9. **History preserves provenance.** Build and operation logs retain immutable agent/machine identity,
   definition digest, and Git revision linkage. Later renames or parameter changes cannot rewrite
   historical context.
10. **A log claim requires evidence.** Size, crash recovery, redaction, ordering, authorization,
    retention, disconnect, and overflow behavior are tested at the lowest tier that exercises the
    real boundary.
11. **Terminal output closes before capacity is reused.** A terminal build or operation reports final
    per-stream sequence watermarks. Result acknowledgement and output acknowledgement are independent,
    and machine release/reuse/revert waits until every sequence is durably persisted or represented by
    an explicit durable gap. The barrier is bounded: a gap preserves capacity without pretending the
    missing output exists.

## Review checklist

For every new or changed event, require answers to these questions:

- Which stream owns it: audit, diagnostic, build output, or AgentExplorer operation output?
- Is it a stable state transition or a noisy sample that belongs in metrics?
- What is its stable event name, severity, source, and schema version?
- Which correlation IDs are available, and which are mandatory at this boundary?
- Can its message, exception, arguments, URL, headers, environment, or command line contain secrets?
- Where is it persisted, for how long, under what byte limit, and what happens at the limit?
- What happens while the agent is disconnected or the controller sink is slow?
- Which final output watermarks close the workload, and what persistence-or-gap condition releases the
  machine epilogue?
- Who can query or download it, and is that access itself audited?
- Which Git revision or configuration digest explains the action or workload?
- Which deterministic test proves the failure and overflow paths rather than only the happy path?

Reject interpolation of unbounded payloads, exception dumps with request bodies, raw command lines,
per-heartbeat information logs, and buffers with no byte quota. Prefer stable structured properties to
parsing human messages later.

## Collaboration

- **Admin/SuperUser Expert:** owns first-run identity and token lifetime; Logs Expert owns the single
  startup emission path, filesystem protection, and proof that the token cannot leak elsewhere.
- **Git/Versioning Expert:** owns Git mutation mechanics; Logs Expert requires accepted/rejected commit
  identity, repository identity, base revision, and conflict outcome in the audit event.
- **Agent API/SDK Expert:** owns wire compatibility and deployment; Logs Expert specifies sequence,
  independent output acknowledgement, final watermark, disconnected-spool, and priority requirements
  for log transport.
- **TeamCity Expert:** owns build semantics; Logs Expert owns build/step output envelopes, quotas,
  lifecycle annotations, and retention linkage.
- **AgentExplorer Expert:** owns operations and inventory; Logs Expert owns operation output, audit
  coverage, correlation, and limits. Inventory snapshots are not diagnostic logs.
- **Vivarium REST Expert:** owns resource paths and HTTP semantics; Logs Expert owns request logging,
  audit coverage, cursors, safe fields, and log-query authorization requirements.
- **UI Expert:** owns presentation; Logs Expert requires visible truncation/gap/stale indicators and
  forbids browser-side secret telemetry.
- **User Roles Expert:** owns permissions; Logs Expert supplies the sensitivity classes that permissions
  must protect.
- **Platform Expert:** owns Windows/Linux/macOS integration; Logs Expert requires secure locations,
  atomic append/rotation behavior, service-account permissions, and portable timestamp semantics.
- **Persistence Expert:** owns SQLite/blob schemas, transactions, WAL recovery, indexes, and migration;
  Logs Expert owns the event taxonomy, redaction, retention, quotas, and observable atomicity contract.
- **Results Expert:** owns what constitutes final build/operation evidence and how incomplete evidence
  is presented; Logs Expert supplies final watermarks, completeness/gap facts, and output retention.
- **Docs Expert:** keeps event catalogs, limits, operational locations, and troubleshooting guidance
  synchronized with implementation.
- **Test Steward:** selects the evidence tier and prevents logging tests from relying on timing or the
  developer's machine state.
- **Reconciliation Lead:** inventories the entire emission/query surface when logging, authorization,
  or REST coverage is audited across subsystems.

## Hand-off

State which streams changed, event names and schema versions added, correlation and redaction behavior,
byte/retention budgets, overflow policy, final watermarks, output acknowledgements, epilogue barrier,
storage migration, query authorization, and tests run. Record every deferred platform or mixed-version
gate. If a change introduces a new log source without a bounded sink and test, it is not ready to hand
off.
