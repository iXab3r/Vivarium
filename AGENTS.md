# Vivarium Project Notes

Vivarium is an OSS test farm that runs test corpora against snapshot-managed VMs (exact Windows patch
levels, machines with specific software preinstalled, Ubuntu, macOS) and produces a test × scenario matrix.

> **Docs map:** this file (AGENTS.md) holds the *rules*; [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
> holds the *shape* (all design decisions, numbered D1…D20); [`docs/ROADMAP.md`](docs/ROADMAP.md) holds
> the order of work; [`docs/prior-art.md`](docs/prior-art.md) records what we borrowed from existing
> systems; [`docs/walkthrough.md`](docs/walkthrough.md) is the normative end-to-end UX;
> [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) covers building, test tiers, releases, and farm
> upgrades. Read ARCHITECTURE.md before proposing or implementing anything structural.

This repository is worked on by humans and multiple AI agents (Claude Code, Codex, and others). This
file is the shared contract for all of them; keep it harness-agnostic. Subdirectories may add their own
`AGENTS.md` with narrower rules once code exists.

## Project status

Phase 0 in progress. The solution skeleton, the pinned-TLS session loop (enroll → authorize → build →
artifacts), and the first tier-2 protocol tests exist and pass; nothing is end-user usable yet. The
docs remain authoritative for shape — when code and a decision disagree, fix one of them in the same
commit, never neither.

## Repository conventions

- All committed content — docs, code, comments, commit messages — is in **English**.
- Target stack: **.NET 10 / C#** for controller, agent, bootstrap, CLI; protocol in **proto3**
  (`Vivarium.Contracts`). Blazor Server for the panel. SQLite + a blob directory for storage — no
  external service dependencies.
- Layout: `src/Vivarium.{Contracts,Controller,Agent,Bootstrap,Cli}`, `tests/Vivarium.Tests`, `docs/`,
  plus `images/` for image recipes (later).
- Dependencies must be MIT/Apache-2.0-compatible (the project is MIT).
- The bootstrap component is contractually **frozen once Phase 0 proves it** (ARCHITECTURE §7).
  Changes to it require an explicit design discussion first — it is the one piece baked into every VM
  image and installed on every physical machine.

## Design-change discipline

Architecture decisions live in ARCHITECTURE.md as numbered entries (D1…). When an implementation choice
contradicts, refines, or retires a decision, update the decision **in the same commit** — the doc must
never describe a system that no longer exists. New significant decisions get new numbers; reference
them from commit messages and PRs.

## Core implementation rules

### 1. Think before coding

Do not assume; do not hide confusion; surface tradeoffs. State assumptions when they matter. If
multiple interpretations exist, present them instead of picking silently. If a simpler approach exists,
say so. If a wrong guess would create churn, stop and ask.

### 2. Simplicity first

Minimum code that solves the problem; nothing speculative. No features beyond what was asked, no
abstractions for single-use code, no configurability nobody requested, no error handling for impossible
scenarios. The runner's power comes from a small number of sharp contracts (files-in/process/files-out,
clone/revert/start/stop, the frozen bootstrap) — guard their smallness.

### 3. Surgical changes

Touch only what you must; clean up only your own mess. Match existing style; remove imports/helpers
your change made unused; do not refactor or "improve" unrelated code in passing. Every changed line
should trace to the task at hand.

### 4. Verification

Once code exists: `dotnet build` and `dotnet test` at the solution root must pass before handoff
(test-tier definitions and CI mapping: [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md)), and
protocol changes must keep `Vivarium.Contracts` backward-compatible within a minor version (pool
checkpoints may carry a stale agent until its post-revert upgrade).
