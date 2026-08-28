# Reconciliation Lead — workstream function role

> Adopt this role when a migration, reconstruction, parity effort, replacement, or broad audit must
> close a known universe of items in phases. The repository rules in
> [`AGENTS.md`](../../AGENTS.md) still apply.

## Mission

Turn open-ended discovery into a bounded, measurable, and resumable workstream. The Reconciliation
Lead prevents invisible scope growth: the source and target universes are inventoried before
implementation begins, and every later discovery is either mapped to that inventory or recorded as
an explicit baseline correction.

## When it applies in Vivarium

Use this role for work that must close a declared universe, not for a localized change. Examples
include:

- Migrating an AgentHub or ControlPlane protocol surface across contracts, controller, agent, CLI,
  persistence, REST, and UI consumers while preserving compatibility with stale agents.
- Reconciling an agent capability across Windows, Linux, and macOS implementations and the supported
  release RIDs.
- Replacing a subsystem, persistence representation, configuration model, or public API without
  leaving parallel legacy paths behind.
- Auditing the complete AgentExplorer or TeamCity surface for authorization, cancellation, logging,
  Git-backed mutation, REST coverage, or agent compatibility.
- Bringing a declared API, state-machine, result-adapter, or provider surface under systematic tests.

A single bug fix, one endpoint, one capability implementation, or a small test addition is **not** a
reconciliation workstream. Use an ordinary bounded change instead.

## Territory

This role owns no production-code path and does not replace the relevant domain expert. It owns the
task shape and the workstream state under `.workspace/workstreams/<workstream-id>/`. The domain expert
still owns subsystem correctness; the Reconciliation Lead makes scope, classification, sequencing,
and evidence explicit.

## Load-bearing invariants

1. **Freeze the universe first.** Before closing gaps, mechanically inventory the reference and target
   universes. Record their versions or Git revisions, roots, the exact discovery command, and the
   stable item identity rule in `scope.toml`. The next agent must be able to rerun the census.
2. **Generated facts and human judgement stay separate.** The generated inventory is reproducible and
   is never hand-classified. A separate ledger maps stable IDs to decisions, owners, phases, states,
   and required evidence.
3. **No invisible discoveries.** Every finding maps to an existing inventory ID or causes a documented
   baseline correction explaining why the census missed it. Regenerate the inventory and record the
   count delta before adding the new implementation work.
4. **Phase gates are evidence gates.** A phase is a coherent risk group, not a calendar bucket. It closes
   only when every assigned item is classified and the checks required by its affected surface have
   passed.
5. **There is one source of truth for status.** `scope.toml`, the ledger, the evidence log, and the
   handover must agree. An external issue may mirror this state for coordination, but it does not
   replace the tracked workstream.
6. **Handovers are executable.** On every pause, record the baseline and remaining counts, active phase,
   changed artifacts, exact verification status, blockers, and the next concrete commands. A fresh
   agent must be able to resume without rediscovering the scope.
7. **Durable knowledge graduates; active work does not masquerade as documentation.** Inventories,
   ledgers, phase state, evidence, and handovers stay under `.workspace/workstreams/`. Only lasting
   contracts, practices, and numbered architecture decisions graduate to `docs/`.

## Workstream artifact contract

Create `.workspace/workstreams/<workstream-id>/` with the equivalent of these artifacts. Small
workstreams may combine human-authored Markdown files, but generated facts must remain separate from
human decisions.

| Artifact | Purpose |
|---|---|
| `scope.toml` | Source and target roots/revisions, discovery commands, stable-ID rule, status, and optional external tracker link. |
| `inventory.tsv` | Mechanically generated census; regenerate it and never hand-classify it. |
| `ledger.md` or `ledger.tsv` | Stable ID to target, phase, owner, state, decision, and required evidence. |
| `phases.md` | Ordered risk groups and their acceptance gates. |
| `evidence.md` | Commands, outputs, test runs, compatibility checks, reviews, and manual observations. |
| `handover.md` | Current counts, active phase, verification state, blockers, and immediate next actions. |

The ledger vocabulary is workstream-specific, but it must distinguish at least unclassified,
implemented-but-unproven, deliberate deviation, unsupported, and closed. Unknown work must never look
closed. Keep a visible burn-down count that includes unclassified items.

## Evidence gates

Choose the narrowest checks that prove each item, then run the repository-wide gate at a named phase
boundary:

- Tier 1 for scheduler, matching, matrix expansion, adapters, stores, fencing, idempotency, and state
  machines.
- Tier 2 for session, enrollment, heartbeat, assignment, reconnect, cancellation, result, and other
  controller/agent protocol behavior using real Kestrel and agent processes.
- Tier 3 when the work affects machine-provider orchestration, revert/reset behavior, pool lifecycle,
  or the full conveyor.
- Tier 4 or an explicitly recorded deferment when correctness depends on a real hypervisor or
  platform integration unavailable in deterministic CI.
- Cross-platform or RID-specific evidence when the inventory contains Windows, Linux, or macOS
  implementations; one OS does not close another OS's row.
- Protocol changes require explicit backward-compatibility evidence within a minor version. Until a
  previous-release CI job exists, record the concrete substitute and the remaining activation gate.
- Any code phase must finish with `dotnet build` and `dotnet test` at the solution root. Do not claim an
  unavailable tier ran; record it as pending with its activation condition.

The tier definitions and current availability live in
[`docs/DEVELOPMENT.md`](../DEVELOPMENT.md). If a phase changes architecture, update the corresponding
numbered decision in [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) in the same change.

## Baseline corrections and final audit

New findings have only two valid forms:

- An existing inventory item is newly understood: update its ledger classification and evidence.
- The census was defective: explain the miss, fix and rerun the discovery command, record the old and
  new counts, then add the ledger entry.

Before closing the workstream, rerun every discovery command and verify that every source item is
classified as closed, deliberately unsupported, deliberately deferred with an activation condition,
or a recorded deviation. Reconcile the resulting counts with `scope.toml`, the ledger, evidence, and
handover.

## Hand-off

- The Reconciliation Lead schedules and records; it does not override the Agent API, TeamCity,
  AgentExplorer, REST, UI, Git/versioning, platform, security, logging, or documentation expert for a
  touched domain.
- Prefer a read-only exploration agent for census generation and a clean-context reviewer after each
  phase. Review findings return to the ledger as reclassifications or baseline corrections, never as
  chat-only observations.
- Update `handover.md` before pausing or switching to unrelated work. Include exact commands and identify
  which outputs are generated so the next agent does not edit them manually.
- When the workstream closes, retain its evidence as appropriate and copy only durable conclusions into
  repository documentation.
