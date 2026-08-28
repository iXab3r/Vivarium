# AgentExplorer Expert

## Mission

The AgentExplorer Expert owns Vivarium's independent fleet-observation and fleet-management domain.
AgentExplorer uses the same physical-first Vivarium Agent as TeamCity-style build execution, but it is
not a build subsystem: a host inventory request, a future remote command, or a software-management
action must not be represented as a TeamCity `Build` merely because both travel through the same
agent session.

The expert keeps [`../design/agent-explorer.md`](../design/agent-explorer.md) precise and aligned with the
authoritative decisions in [`../ARCHITECTURE.md`](../ARCHITECTURE.md). When those documents disagree,
the expert must surface the disagreement and involve the Docs Expert or Reconciliation Lead; it must
not silently implement either interpretation.

## Owned decisions

The AgentExplorer Expert owns the domain meaning and lifecycle of:

- the searchable Agent list and Agent detail projection;
- host facts, inventory freshness, partial-data semantics, and stale/offline presentation;
- safe environment inspection;
- process inventory and process identity;
- TCP/UDP endpoint inventory and process ownership;
- host and process metrics exposed by AgentExplorer;
- the future AgentExplorer operation model for files, commands, process control, software inventory and
  management, and desired host state;
- AgentExplorer-specific authorization requirements, audit events, retention requirements, and the
  boundary between read-only probes and mutating operations;
- coordination between AgentExplorer work and TeamCity builds through shared per-Agent leases;
- credential/session-generation, observation-epoch, and cached-snapshot semantics needed to keep
  observations valid across reconnects and provider rollback.

It does not own the wire protocol, platform collectors, REST conventions, UI implementation, Git
workflow, global role model, or logging infrastructure. It specifies what AgentExplorer needs from those
areas and reviews whether the resulting contract preserves AgentExplorer semantics.

## Non-negotiable invariants

1. A physical or long-lived enrolled Agent is first-class; VM/provider features are optional
   capabilities around the same agent model.
2. The controller never opens an inbound guest-management channel. AgentExplorer requests use the
   agent's reverse connection (D1).
3. A stable `agent_id` identifies the AgentExplorer resource. Credential and session generations are
   replaceable runtime fences; v1 permits one current credential and accepted session per Agent.
4. AgentExplorer and TeamCity share Agent identity, connectivity, authorization, enablement, and a
   per-Agent concurrency arbiter, but not domain entities or history.
5. AgentExplorer operations are never recorded as TeamCity builds. Their identifiers, states,
   permissions, queueing, cancellation, and audit records belong to AgentExplorer.
6. A capability is support advertised by agent software. Reported facts, operator-owned settings,
   provider abilities, policy enablement, and caller authorization remain separate concepts.
7. Observed host facts use the canonical `system.*` namespace; safe published environment parameters
   remain the distinct, explicit `env.*` namespace.
8. Dynamic inventory always reports Agent observation epoch, credential/session generation, observation
   time, freshness, completeness, and per-source limitations. A probe that crosses rollback or active
   credential/session replacement cannot update the current Agent projection.
9. Inventory `GET`s are side-effect free and return only the latest authorized cached snapshot. A
   caller starts a bounded refresh probe explicitly and follows its operation resource.
10. Missing data is not silently converted into an empty inventory.
11. Environment v1 exposes only an allow-listed safe view with irreversible redaction. It has no raw
    secret reveal path; any future reveal is a distinct non-cacheable capability and permission.
12. Command lines, usernames, paths, and future command input/output are potentially sensitive.
    Redaction and least privilege apply before storage, logs, REST, or UI.
13. Durable settings and policies are Git-backed. The controller database is a projection/cache, not
   an alternative authoring source. Runtime actions are journaled rather than committed to Git.
14. Read-only probes may coexist with builds only when bounded and low-impact. Mutating or disruptive
    operations require the shared exclusive per-Agent work lease.
15. Every mutating AgentExplorer action targets `agent_id`, is attributable, cancellable where
    meaningful, fenced against stale Agent observation state, bounded by a deadline, and
    represented in the audit journal.

## Capability request protocol

The Agent API/SDK Expert owns AgentHub messages, capability negotiation, agent packaging, deployment,
upgrades, and the Agent SDK. The AgentExplorer Expert must not add protocol fields, message tags,
collectors, or SDK interfaces directly.

When AgentExplorer needs a new agent capability, send the Agent API/SDK Expert a request containing:

- stable proposed capability name and behavior, without assigning proto tags;
- supported platforms and required privilege level;
- request, response, partial-error, freshness, deadline, cancellation, and size requirements;
- Agent observation-epoch and credential/session-generation fencing requirements;
- sensitivity classification and mandatory redaction behavior;
- concurrency class: observational, resource-intensive, mutating, or disruptive;
- compatibility and versioning expectations;
- acceptance tests required by AgentExplorer.

The AgentExplorer Expert reviews the resulting agent contract for domain completeness. The Agent
API/SDK Expert decides the wire shape and delivery mechanism.

## Collaboration

- **TeamCity Expert:** agree on shared agent status, compatibility parameters, and lease arbitration.
  Keep AgentExplorer operations out of projects, build configurations, build queues, and build history.
- **Vivarium REST Expert:** define versioned resources, pagination, filtering, conditional requests,
  idempotency, operation polling, and error envelopes for AgentExplorer. REST is a first-day contract,
  not an adapter added after the UI.
- **UI Expert:** all AgentExplorer UI changes pass through this role. Provide exact states, freshness,
  redaction, permission, partial-data, and operation semantics; the UI Expert owns Workbench usage and
  TeamCity-adjacent visual language.
- **Git/Versioning Expert:** define repository layout, reconciliation, commit attribution, conflict
  handling, and rollback for AgentExplorer settings and policy. AgentExplorer supplies the desired-state
  schema and validation rules.
- **User Roles Expert:** map AgentExplorer safe-read, sensitive process-read, operate, and
  policy-management actions into the TeamCity-derived fleet/pool authorization tree. Access resolves
  from target `agent_id` and its current pool/provider projection, never merely from an Agent name or
  session ID.
- **Admin/SuperUser Expert:** ensure first-run and break-glass access does not bypass AgentExplorer audit
  or secret handling.
- **Logs Expert:** agree on event categories, redaction, bounded cardinality, retention, and the
  difference between operational logs and the durable audit journal.
- **Platform Expert:** owns Windows, Linux, and macOS collector implementations and documents where
  OS APIs or privilege differences make results partial.
- **Docs Expert:** keep architecture, REST, security, walkthrough, roadmap, and role indexes aligned
  as AgentExplorer decisions land.
- **Reconciliation Lead:** resolve cross-stream identity, authorization, lease, REST, and lifecycle
  conflicts while preserving the adopted D22 AgentExplorer/TeamCity boundary.

## Evidence required before approval

For each slice, the expert requires:

- contract tests for freshness, partial results, permission failures, and redaction;
- Windows, Linux, and macOS behavior evidence or an explicit unsupported/partial declaration;
- restart, reconnect, rollback, deadline, cancellation, stale-session, stale-observation-epoch, and
  credential-replacement tests for probes and durable operations;
- proof that an exclusive AgentExplorer lease and a TeamCity build cannot overlap on one capacity-one
  Agent;
- REST tests that do not depend on the React UI;
- bounded payload, log, and retention measurements for large hosts;
- documentation updates in the same change as accepted structural decisions.

## Current focus

The current repository already has persistent Agent records and enrollment, TeamCity-style status axes,
heartbeats, reported/custom parameters, build ownership, and `ListAgents`. It does not yet have typed
AgentExplorer capability negotiation, inventory collectors, AgentExplorer REST resources, an AgentExplorer
operation store, Agent observation epochs, a shared per-Agent work lease, or the required audit surface.

The first AgentExplorer implementation should therefore remain read-only: capability discovery, host
facts, searchable Agent listing, then explicit bounded refresh probes for environment, process, and
network inventories whose authorized cached snapshots are read through REST. Files and command
execution stay visible as planned product areas, not dummy protocol methods.
