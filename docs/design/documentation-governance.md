# Documentation Governance

> Status: **Accepted**
> Implementation: **Implemented**
> Maintainer role: [Docs Expert](../roles/docs-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D1-D28

## Purpose

Vivarium is developed by humans and multiple AI agents. Documentation is therefore part of the control
plane for development: it must tell a contributor which statements are rules, which are adopted design,
which describe current implementation, and which are proposals. All documentation changes are ordinary
Git changes, reviewed and versioned alongside the code they describe.

This convention keeps that system reliable without requiring every contributor to load the entire
repository or maintain process metadata that quickly becomes stale.

## Current and target state

The current repository has an authoritative root contract and an architecture document with numbered
decisions, plus separate roadmap, prior-art, walkthrough, and development guides. This is the correct
foundation. The main scaling risk is concentrating every subsystem detail in `ARCHITECTURE.md` and
relying on conversational context to decide who should review a change.

The target adds two lightweight layers:

- **Focused designs** under `docs/design/` hold bounded subsystem details, alternatives, current/target
  state, invariants, open questions, and evidence.
- **Role packs** under `docs/roles/` tell a contributor what context to load, which boundaries to protect,
  and when to request another expert's review.

Neither layer replaces the existing authoritative spine.

## Source map and precedence

| Source | Authoritative for | Not authoritative for |
|---|---|---|
| Root and scoped `AGENTS.md` files | Contribution rules and context routing within their filesystem scope | Product architecture or implementation status |
| `docs/ARCHITECTURE.md` numbered decisions | Adopted target shape and structural invariants | Proof that a target is implemented |
| Accepted focused designs in `docs/design/` | Detail within the boundaries of cited decisions | Overriding a numbered decision |
| `docs/ROADMAP.md` | Work order, phase boundaries, and implementation status | Structural design rationale |
| `docs/walkthrough.md` | Normative end-to-end user experience for the described flow | Hidden backend contracts not exposed by the flow |
| `docs/DEVELOPMENT.md` | Build, test tiers, release, upgrade, and contributor procedures | Product behavior outside those procedures |
| `docs/prior-art.md` | Evidence about systems studied and what Vivarium borrows | A decision merely because another system made it |
| `docs/roles/` | Review responsibility, context-loading routes, and collaboration triggers | Product requirements or exclusive code ownership |
| Contracts, code, schemas, and tests | Evidence of current implementation | Permission to contradict adopted design silently |

Precedence is scoped, not a single universal ranking. When two sources appear to conflict:

1. Apply the most specific repository rule from `AGENTS.md` to how work is performed.
2. Use the numbered architecture decision for intended structural behavior.
3. Use contracts, code, persisted schemas, and tests to determine what is actually implemented now.
4. Use the roadmap to determine whether the difference is planned, partial, or an unexpected drift.
5. Reconcile the contradiction in the same change: fix the implementation, refine/retire the decision,
   or clearly label a known gap. Never leave both sides silently false.

`walkthrough.md` is normative for UX, but an apparent conflict with architecture requires both owners to
review it; neither document silently wins outside its scope. Prior art and draft designs never override
an accepted decision.

## Focused design contract

A focused design should cover one subsystem or one cross-cutting concern. It should contain only the
sections needed from this set:

- purpose and scope;
- current state, with evidence;
- target state;
- invariants and non-goals;
- contracts, flows, and failure behavior;
- security, compatibility, or platform constraints when relevant;
- alternatives and rationale;
- rollout and verification;
- open questions with responsible roles;
- collaboration and review triggers.

Use a small header:

```markdown
> Status: **Draft | Accepted | Superseded**
> Implementation: **Planned | Partial | Implemented**
> Maintainer roles: Role A, Role B
> Related architecture: D<n>, D<m>
> Evidence: optional paths, tests, schema, or verified commit
```

Only fields that add information are required. Git already records authors and modification times, so
do not maintain duplicate owner names, approval tables, changelogs, or review dates. Add `Evidence` when
the document makes current-state claims; omit it for a proposal with no implementation.

`Status` describes the design's authority. `Implementation` describes delivery. They must not be
collapsed: an accepted design may be planned, and implemented code may expose a disagreement that must
be reconciled.

## Role pack contract

Role packs are short, harness-neutral files. They should define:

- mission and scope;
- current and target context;
- invariants and non-goals;
- evidence expected before handoff;
- files or focused designs normally loaded;
- conditions for requesting another role's review;
- unresolved questions owned by that role.

A role is a perspective any contributor can assume, not a daemon, account, prompt template, or private
queue. The role that maintains a document is its first reviewer and routing contact, not its sole editor
or unilateral approver. The author of a code or design change remains responsible for updating affected
documentation in the same change.

Cross-cutting roles remain independent:

- The **Reconciliation Lead** detects conflicts between concurrent streams, current code, and adopted
  design, and coordinates a single coherent resolution.
- The **Test Steward** checks that verification claims correspond to real test tiers, fixtures, and
  release gates.
- The **Docs Expert** maintains the document graph, authority labels, and clarity of the written result.

These roles complement domain experts; they do not replace them.

## Loading context without loading everything

Contributors use progressive context loading:

1. Read the applicable `AGENTS.md` files. For a structural proposal or implementation, read
   `docs/ARCHITECTURE.md` completely as required by the root contract.
2. Load the role pack for the active stream and the focused design named by it.
3. Read the relevant roadmap slice to distinguish current from target state.
4. Load only the applicable contract, schema, source paths, tests, walkthrough section, development
   procedure, and prior-art section.
5. Follow direct links when an invariant or unresolved question requires more context; do not crawl all
   adjacent documents speculatively.

An agent must not rely on a previous conversation summary as the sole source for a durable decision.
If the decision matters to future work, it belongs in the repository. Role packs should link to shared
documents instead of copying their contents, keeping both token use and contradiction risk bounded.

## Same-change update rules

The change author updates documentation together with the implementation when any of these statements
would otherwise become false:

- A structural invariant or public behavior changes: update or retire the numbered architecture decision
  and update the focused design.
- A public REST/gRPC contract, agent protocol, configuration schema, persisted model, or compatibility
  promise changes: update its contract documentation and affected architecture/design references.
- A phase or feature moves between planned, partial, and implemented: update the roadmap and focused
  design implementation marker.
- A normative user journey changes: update the walkthrough.
- Build, test, release, packaging, or upgrade procedures change: update `DEVELOPMENT.md`.
- A borrowed semantic or rejected alternative materially changes: update prior art and the rationale that
  cites it.
- A review boundary moves between roles: update the affected role packs.

Pure refactors require no prose update when contracts and documented behavior remain true. Mechanical
documentation fixes do not require a decision entry. Significant design changes do.

System-managed settings and properties are subject to the Git/versioning design: documentation should
name their source of truth and history semantics, but must not invent those mechanics locally. Runtime
operator actions belong in the audit/log design even when they do not modify versioned configuration.

## Decision promotion, refinement, and retirement

1. Explore a bounded question in a focused design marked `Draft`. Record alternatives, evidence, and the
   role that owns each unresolved point.
2. Request review from affected domain and cross-cutting experts. A decision involving management
   mutations normally needs Git/Versioning and REST review; security or first-run behavior also needs
   User Roles and Admin/SuperUser review; platform-specific behavior needs Platform review.
3. Once the structural conclusion is accepted, add or refine a numbered decision in
   `ARCHITECTURE.md`. Keep the decision concise and link the focused design for detail.
4. Mark the focused design `Accepted` and track delivery separately in the roadmap and its
   `Implementation` field.
5. When a decision is replaced, do not delete or renumber it. Mark it superseded, name the replacing
   decision, update inbound references, and preserve enough rationale to understand historical code and
   migrations.

A refinement that changes observable semantics is a decision change even if the implementation diff is
small. An implementation detail that remains within an existing invariant does not need a new number.

## Ownership and review requests

Maintainer roles keep their documents coherent, but Git changes remain collaborative. Review is requested
by impact:

- Domain experts review behavior in their subsystem.
- Agent API/SDK reviews any capability or agent transport request from another stream.
- REST reviews every externally manageable resource and mutation from the start of its design.
- Git/Versioning reviews mutable settings, configuration provenance, revisions, rollback, and merge
  behavior.
- User Roles and Admin/SuperUser review authorization, bootstrap administration, tokens, and first-run
  flows.
- Logs reviews audit events, retention, cardinality, sensitive values, and diagnostic volume.
- Platform reviews Windows, Linux, and macOS collection/execution semantics.
- UI reviews user-visible flows and Workbench integration.
- Reconciliation Lead reviews cross-stream seams and contradictions.
- Test Steward reviews evidence and verification coverage.
- Docs Expert reviews authority, routing, terminology, and same-change completeness.

Request only reviewers affected by the change. Missing review is an explicit handoff item, not an excuse
to manufacture an answer outside the active role.

## Freshness and contradiction checks

Freshness comes primarily from same-change updates and evidence, not calendar reminders. Before handoff:

- Resolve every repository-relative link touched by the change.
- Confirm each cited `D<number>` exists and that decision numbers are not duplicated.
- Search affected documents for retired terminology and contradictory status claims.
- Check that current-state statements cite implementation evidence and target statements cite an adopted
  decision or remain labeled as proposals.
- Compare public contract names and status enums with their source definitions.
- Confirm the roadmap, walkthrough, and development guide were updated when their scoped truth changed.
- Ask the Reconciliation Lead for semantic conflicts that a link checker cannot find.
- Ask the Test Steward before stating that a behavior or release gate is covered.

These checks should be automated where practical: local-link resolution, duplicate/missing decision IDs,
required focused-design headers, and stale contract identifiers are good CI candidates. CI cannot decide
whether prose and code mean the same thing; that remains a focused review responsibility.

## Handoffs and open questions

Every material design or documentation handoff records:

- outcome and scope;
- decisions relied on, added, refined, or superseded;
- files changed;
- evidence and verification performed;
- affected documents intentionally not changed, with the reason;
- known contradictions or migration gaps;
- open questions, responsible roles, and whether each blocks implementation.

Open questions stay in the most focused owning design, not scattered through role files or chat logs.
When resolved, promote the durable conclusion to the appropriate authority and remove or close the open
question in the same change.

## Invariants and non-goals

The governance system must preserve a single discoverable source for each kind of truth, Git history for
every change, stable decision references, and enough evidence to distinguish implementation from intent.

It must not create a second issue tracker, require every expert to approve every change, mirror generated
API reference by hand, promote incidental implementation choices to architecture, or force contributors
to read every design document before making a local change.
