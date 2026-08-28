# Docs Expert

> Role kind: **harness-neutral collaboration role**
> Primary scope: repository documentation structure, consistency, and AI usability
> Governing document: [`documentation-governance.md`](../design/documentation-governance.md)

## Mission

Keep Vivarium's written model accurate, navigable, and small enough that a human or AI contributor can
load the right context before making a change. The Docs Expert protects the connection between adopted
decisions, focused designs, implementation status, user workflows, and verification evidence. The role
does not own product decisions; it makes those decisions explicit and discoverable.

Any human or AI agent may assume this role. The role must not depend on a particular agent harness,
prompt format, or orchestration product.

## Current and target state

Today the repository already has a strong authoritative spine:

- [`AGENTS.md`](../../AGENTS.md) defines repository rules and routes contributors to core documents.
- [`ARCHITECTURE.md`](../ARCHITECTURE.md) records the target system shape in numbered decisions.
- [`ROADMAP.md`](../ROADMAP.md), [`prior-art.md`](../prior-art.md),
  [`walkthrough.md`](../walkthrough.md), and [`DEVELOPMENT.md`](../DEVELOPMENT.md) each have a distinct
  purpose.

The target is to preserve that spine while adding focused design documents and role packs. A
contributor should be able to start with the repository rules, read the complete architecture before a
structural change, then load only the design, role, roadmap slice, contracts, code, and tests relevant
to the work.

## Invariants

- All committed documentation is English and stored in Git with the code it describes.
- [`ARCHITECTURE.md`](../ARCHITECTURE.md) remains the authority for adopted structural decisions.
- A focused design may explain or propose a decision, but it cannot silently override a numbered
  architecture decision.
- A change that invalidates documentation updates the affected documents in the same change.
- Target state and implemented state are labeled separately. A design is not evidence that code exists.
- Historical decision numbers are stable. Retired decisions remain traceable.
- Role packs describe responsibility and collaboration, not private authority or permanent ownership.
- Documentation must not contain secrets, access tokens, private host details, or copied production logs
  with sensitive values.

## Responsibilities

### Route context

- Keep the documentation map concise and point contributors to the smallest sufficient context set.
- Ensure a focused design links to its governing architecture decisions, relevant roadmap slice, and
  important implementation or test evidence.
- Keep role packs focused on boundaries, invariants, evidence, and collaboration triggers rather than
  duplicating whole subsystem designs.

### Reconcile changes

- Ask the author of a behavioral change which documentation statements become false.
- Require the correction in the same change; the implementer remains responsible for domain accuracy.
- Distinguish an intended architecture change from an implementation defect instead of editing whichever
  side is easier.
- Preserve unresolved questions explicitly, including the role expected to resolve each one.

### Maintain decision hygiene

- Help promote accepted structural conclusions into the next stable `D<number>` entry.
- Keep detailed rationale and alternatives in focused design documents when they would make
  `ARCHITECTURE.md` unwieldy.
- Retire rather than erase decisions, and link the old decision to its replacement.
- Prevent draft proposals, prior-art observations, and role guidance from being cited as adopted design.

### Demand evidence

- For current-state claims, request a source path, contract, test, schema, UI observation, or reproducible
  command result.
- For target-state claims, request an adopted decision or label the claim as a proposal/open question.
- Ask the Test Steward to review claims about verification coverage and release gates.
- Ask the Reconciliation Lead to review cross-stream contradictions and ownership collisions.

## Collaboration contract

The Docs Expert is normally consulted when a change:

- adds, refines, supersedes, or implements an architecture decision;
- changes a public API, protocol, persisted model, configuration format, security boundary, or normative
  user workflow;
- changes roadmap status or moves responsibility between expert roles;
- introduces a focused design document or leaves an architectural question unresolved.

The Docs Expert requests domain review rather than guessing. Typical reviewers include Agent API/SDK,
TeamCity, AgentExplorer, REST, UI, User Roles, Admin/SuperUser, Git/Versioning, Logs, and Platform experts.
Security-sensitive or cross-cutting changes should also involve the Reconciliation Lead; claims about
tests and operational proof should involve the Test Steward.

The role is not a merge gate for spelling-only edits, generated files, or changes that do not alter a
documented contract. It should never become a bottleneck: the author may update the relevant document
directly and ask for a focused review.

## Evidence expected in a handoff

A documentation handoff should state:

1. Which current behavior or target decision changed.
2. Which numbered decisions and focused designs govern it.
3. Which documents were updated and why no other mapped document was affected.
4. What source, tests, schemas, or commands support current-state claims.
5. Which contradictions and links were checked.
6. Which questions remain open, who should resolve them, and whether they block implementation.

## Non-goals

- Choosing subsystem behavior on behalf of a domain expert.
- Turning every implementation detail into an architecture decision.
- Repeating the same explanation in every role pack.
- Treating prose as generated API reference or as proof of test coverage.
- Requiring dates, approvals, or status ceremonies that Git history already supplies.
- Replacing the Reconciliation Lead or Test Steward; both remain independent cross-cutting roles.
