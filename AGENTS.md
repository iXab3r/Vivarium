# Vivarium Project Notes

Vivarium is an OSS test farm that runs test corpora against snapshot-managed VMs (exact Windows patch
levels, machines with specific software preinstalled, Ubuntu, macOS) and produces a test × scenario matrix.

> **Docs map:** this file (AGENTS.md) holds the *rules*; [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
> holds the *shape* (all design decisions, numbered D1…D13); [`docs/ROADMAP.md`](docs/ROADMAP.md) holds
> the order of work; [`docs/prior-art.md`](docs/prior-art.md) records what we borrowed from existing
> systems. Read ARCHITECTURE.md before proposing or implementing anything structural.

This repository is worked on by humans and multiple AI agents (Claude Code, Codex, and others). This
file is the shared contract for all of them; keep it harness-agnostic. Subdirectories may add their own
`AGENTS.md` with narrower rules once code exists.

## Project status

Design phase. There is no runnable code yet; the deliverables so far are the documents above. Until the
solution skeleton lands, "making a change" usually means changing a document — treat docs with the same
rigor as code.

## Repository conventions

- All committed content — docs, code, comments, commit messages — is in **English**.
- Target stack: **.NET 10 / C#** for controller, agent, bootstrap, CLI; protocol in **proto3**
  (`Vivarium.Contracts`). Blazor Server for the panel. SQLite + a blob directory for storage — no
  external service dependencies.
- Planned layout: `src/Vivarium.Contracts`, `src/Vivarium.Controller`, `src/Vivarium.Agent`,
  `src/Vivarium.Bootstrap`, `src/Vivarium.Cli`, plus `images/` for image recipes and `docs/`.
- Dependencies must be MIT/Apache-2.0-compatible (the project is MIT).
- The bootstrap component is contractually **frozen** (ARCHITECTURE §7). Changes to it require an
  explicit design discussion first — it is the one piece baked into every VM image.

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

Once code exists: `dotnet build` and `dotnet test` at the solution root must pass before handoff, and
protocol changes must keep `Vivarium.Contracts` backward-compatible within a minor version (agents in
sealed images may be one version behind until their next boot).
