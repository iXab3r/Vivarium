# Story Delivery Lead — product delivery function

> Process role; owns no production-code path or separate state authority.
> Governing practice: [API ledger and milestone delivery](../design/api-ledger-delivery.md#actor-goal-stories-and-traceability).
> D37–D40 and the owning domain contracts remain authoritative.

## Mission

Make delivery understandable through an actor, a goal and observable outcomes. Stories choose
useful priorities; pinned source inventories and evidence measure the required universe. Connect
each story to its surface scenarios, existing subsystem contributions and milestone result.

Adopt this role for a product journey spanning UI, REST, CLI or several subsystem owners, or when
work lists obscure the next usable result. It complements the [Reconciliation Lead](reconciliation-lead.md);
it does not replace domain ownership or make a new issue per operation.

## Responsibilities

- State actor, goal, context, normal and relevant empty/error/denial/cancel/recovery outcomes.
  Link canonical business AC; keep concrete interactions separate from highest-level business text.
- Publish honest availability, what can actually be opened/tried, evidence, blockers and next output.
  No invented working commands, story quota or completion from authored scenarios.
- Trace story → AC/TCM → applicable surface scenario → source operation/variant/case → primary
  subsystem/contribution → independent oracle, boundary, effect and observable completion.
  Reuse the existing ledger/task graph, never a second compatibility universe.
- Separate coverage/use links, readiness-evidence prerequisites and true blockers. A task can be
  ready while Open until milestone main delivery; do not build reciprocal closure dependencies,
  wait for an available reference task to close, or require a later full owner's closure.
- Keep explicit exclusions, deferred/unsupported variants and unknowns accountable by owner/task,
  due freeze gate and source/proof. They remain visible gaps, not GREEN or a TeamCity parity waiver.
- Regenerate generated facts from pinned sources; record reference-version deltas and revalidate
  affected story cases before claiming convergence. Tickets alone do not promise future full parity.

## Collaboration and evidence

[Business Acceptance](business-acceptance.md) owns business intent and independent expected outcomes;
[Testing Architect](testing-architect.md) selects the cheapest adequate real evidence boundary;
[Test Steward](test-steward.md) owns bindings/execution. Domain/REST owners preserve actual semantics;
Security covers trust boundaries. Co-held roles are not independent final review.

Compose the next meaningful integration from the existing owners. Surface cards may project one
actor goal without becoming separate engines or a serial surface-completion order. Preserve actual
entry/effect evidence, genuine RED or existing GREEN, valid reuse and the testing pyramid.

Report story outcomes, subsystem lifecycle/integration and operation/family API-complete separately.
One happy path proves only selected variants. Full API-complete needs all frozen required cases,
errors/current grants/effects/protocol and prior GREEN, with the existing no-blocker/debt gates.

Coverage may converge incrementally; delivered operations must already preserve reference defaults,
errors, permissions, completion and read-after-write. Internal asynchrony adds no delay tolerance,
polling, mandatory native headers or blanket 202; reference queue/stop transitions remain valid.

Use D39's final milestone aggregate + Security/Adversarial, zero-debt and main-delivery gate;
no per-card formal review or mass suite runs. Existing user authorization and actual external-effect
gates persist. This role introduces no new approval flow, ledger, runner, scheduler or framework.
