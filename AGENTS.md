# Vivarium Project Notes

Vivarium is an OSS TeamCity-style test farm plus AgentExplorer fleet manager. One cross-platform agent runs
jobs and safe remote operations on user-provided physical machines first and on snapshot-managed VMs
later, producing centralized test × scenario results across Windows, Linux, and macOS.

> **Docs map:** this file (AGENTS.md) holds the *rules*; [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
> holds the *shape* (all adopted decisions, numbered D1…); [`docs/design/`](docs/design/README.md)
> holds focused designs; [`docs/roles/`](docs/roles/README.md) routes expert context;
> [`docs/ROADMAP.md`](docs/ROADMAP.md) holds
> the order of work; [`docs/prior-art.md`](docs/prior-art.md) records what we borrowed from existing
> systems; [`docs/walkthrough.md`](docs/walkthrough.md) is the normative end-to-end UX;
> [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) covers building, test tiers, releases, and farm
> upgrades. Read ARCHITECTURE.md before proposing or implementing anything structural.

This repository is worked on by humans and multiple AI agents (Claude Code, Codex, and others). This
file is the shared contract for all of them; keep it harness-agnostic. Subdirectories may add their own
`AGENTS.md` with narrower rules once code exists.

## Project status

Phase 1 in progress; the Phase 0 pinned-TLS session loop is complete. Persistent TeamCity-style agent
registrations and status axes, heartbeats, separately managed reported/custom parameters,
restart-safe build ownership and cancellation, durable FIFO scheduling with queue-wait deadlines,
acknowledged assignments and terminal results, immutable assigned-agent provenance, the protected
Agents / Queue & Builds panels, the scoped ControlPlane API, and `viv login` / `viv run` / `viv cancel`
exist and have tier-2 coverage. Strict Phase-1 `vivarium.yaml` parsing and hardened deterministic
payload archives are also implemented. The transport-independent management kernel now provides an
ordered checksummed SQLite migration ledger, fail-closed schema validation, a minimal append-only audit
journal, shared actor/correlation context, and one authorization evaluator beneath the
ControlPlane, panel, and blob boundaries. The current Blazor panel is transitional: the accepted target
is a React panel built on the vendored EyeAuras Workbench and the public REST management API. The
read-only REST foundation now publishes system, Agent, audit, build, and queue resources plus a
deterministic OpenAPI document. AgentHub now negotiates an additive v1 capability contract, persists
durable credential/connection generations, and publishes bounded typed static host facts and
capabilities through Agent REST reads while explicitly draining legacy Agents from new work. The
managed-local system-Git foundation now validates and commits candidate trees before activation,
durably reconciles revision sets with last-known-good recovery, and exposes Git-backed Agent
`spec.enabled`, User declarations, and built-in RoleBindings. The Agent setting is managed through
`/api/v1/agents/{id}/settings` GET/PUT; the first-run ceremony atomically creates its User and
`SYSTEM_ADMIN` binding. Other Agent and controller desired settings remain future Git schemas.
Object-scoped upload staging, idempotent REST
build submit/cancel, durable resumable build SSE, and the live CLI build flow now use the public REST
boundary; the gRPC ControlPlane remains only as a transitional compatibility adapter. Migration v8
adds a bounded controller-side TRX projection with durable report/test/occurrence rows, explicit
no-report/partial/failure states, immutable raw-artifact provenance, and restart catch-up. First-run
administration now has a durable local claim/resume/abandon saga, setup-only REST sessions, an atomic
managed-local Git activation, private password verifiers, named panel login, product-owned built-in
TeamCity/fleet role floors, Git bindings, and explicit restart-safe Superuser recovery. Legacy
admin/submit tokens remain transitional adapters; groups, service accounts, PATs, custom roles,
project-tree inheritance, public user/role management, and the setup UI/local CLI are not complete.
The TeamCity catalog, dynamic AgentExplorer inventory, REST/UI test-result presentation, JUnit and
TEST/CRASH normalization, installers, fleet-wide upgrade orchestration/channels, and machine
providers are not complete, so there is no end-user release yet. The first central Agent-upgrade
slice is now implemented: immutable per-RID packages (including idempotent bundled-catalog import),
authenticated Agent-scoped delivery, durable maintenance drains and operations, busy-Agent drain,
restart-safe coordination, exact reconciled health confirmation, one-shot last-known-good rollback,
two-sided crash-recoverable finalization, strict local integrity and prior binding, durable failure
quarantine, supervised-bootstrap capability gating, bounded session outboxes, a skew-safe watchdog, and
`viv agent upgrade` / `viv agent upgrade-status` (D30); new operations always resolve the matching
Agent package from the running Server release and observed RID, while raw publication is hidden behind
an explicit development/test option. Real
bootstrap child-process success and rollback paths and two-Agent isolation have tier-2 evidence.
Installers/stamped enrollment archives, signing, previous-release compatibility CI, fleet rollout
orchestration/channels, and the remaining bootstrap bad-download/interrupted-activation evidence are
not complete, so the bootstrap stays change-controlled and is not yet declared frozen. The docs remain authoritative
for shape — when code and a decision disagree, fix one of them in the same
commit, never neither.

## Repository conventions

- All committed content — docs, code, comments, commit messages — is in **English**.
- Target stack: **.NET 10 / C#** for controller, agent, bootstrap, CLI; protocol in **proto3**
  (`Vivarium.Contracts`). React + EyeAuras Workbench for the panel, built to static assets and served
  by the controller. SQLite + a blob directory for operational storage — no external service
  dependencies.
- Layout: `src/Vivarium.{Contracts,Controller,Agent,Bootstrap,Cli}`, controller-owned React sources,
  `tests/Vivarium.Tests`, `docs/`, plus `images/` for image recipes (later).
- Dependencies must be MIT/Apache-2.0-compatible (the project is MIT).
- The bootstrap component is contractually **frozen once Phase 0 proves it** (ARCHITECTURE §7).
  Changes to it require an explicit design discussion first — it is the one piece baked into every VM
  image and installed on every physical machine.

## Design-change discipline

Architecture decisions live in ARCHITECTURE.md as numbered entries (D1…). When an implementation choice
contradicts, refines, or retires a decision, update the decision **in the same commit** — the doc must
never describe a system that no longer exists. New significant decisions get new numbers; reference
them from commit messages and PRs. Focused designs may add detail but cannot silently override a
numbered decision. Keep target design and implementation status separate.

## Expert-role routing

Canonical role packs and their focused designs are indexed in
[`docs/roles/README.md`](docs/roles/README.md). Thin project-agent adapters live in `.codex/agents/`
and `.claude/agents/`; never duplicate durable role instructions there.

- **Agent API/SDK Expert** owns AgentHub, capabilities, enrollment, deployment, upgrades, and SDK
  compatibility. Other experts request agent capabilities through this role.
- **TeamCity Expert** owns projects, build configurations, builds, steps, requirements, queue vocabulary/
  visible policy, and build chains; **Scheduling/Coordination Expert** owns durable queue algorithms,
  leases, fencing, and recovery. **AgentExplorer Expert** owns independent fleet observation and remote
  operation semantics.
- **Vivarium REST Expert** reviews every public management resource or mutation. **UI Expert** reviews
  every web UI change and the React/EyeAuras Workbench integration.
- **Git/Versioning Expert** reviews every desired setting/property change. **User Roles** and
  **Admin/SuperUser** experts own authorization and first-run administration; **Security Expert** reviews
  trust-boundary changes.
- **Scheduling/Coordination**, **Persistence/Migrations**, **Results/Artifacts**, **Logs**, and
  **Platform** experts own their cross-cutting contracts. **Machine Providers/Images Expert** owns
  provider hosts, images, pools, clone/revert/power/console, and sealing. **Docs Expert** keeps the
  document graph current.
- Adopt **Reconciliation Lead** for broad migrations/audits and **Test Steward** for test or verification
  changes. State the lead and co-held roles before acting.

## Configuration and management contracts

- Git is the source of truth for mutable **desired configuration** from its first implementation.
  Projects, build configurations, fleet policy, agent custom properties, RBAC policy, and non-secret
  server settings become effective only from a validated Git revision. REST, UI, and CLI writes use one
  Git mutation/reconciliation service; they never patch a hidden SQLite authority.
- Credentials, secret values, session/heartbeat state, observations, queue/build/operation state,
  results, and high-volume logs do **not** belong in Git. Runtime actions such as authorize, cancel,
  restart, rollback, or remote execution are authorized and written to the audit journal.
- `/api/v1` REST is the canonical public management surface for both TeamCity and AgentExplorer. The React
  UI and CLI consume it. AgentHub remains reverse-streaming gRPC, blob bytes remain on the authenticated
  HTTP data plane, and the existing gRPC ControlPlane is a transitional compatibility adapter only.
- Every state-changing request carries actor, correlation/idempotency identity, target, outcome, and,
  when configuration-derived, the Git revision. Logs and audit records must be bounded and redacted.

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
