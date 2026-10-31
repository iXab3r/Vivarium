# Vivarium Project Notes

Vivarium is an OSS TeamCity-style CI build server and test farm plus AgentExplorer fleet manager. One cross-platform agent runs
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

The immediate product priority is replacing the owner's TeamCity infrastructure without functional
loss, including individual test history, Agent requirements/compatibility, durable logs and operator
workflows. [Architecture D32](docs/ARCHITECTURE.md#d32-teamcity-behavioral-parity-is-the-immediate-build-product-contract)
and [the behavioral parity contract](docs/design/teamcity-parity.md) adopt C# definitions, in-process
Chiral nodes and PostgreSQL as the new target. This migration is not implemented; the current
SQLite/YAML foundation described below remains real implementation evidence.

Phase 1 in progress; the Phase 0 pinned-TLS session loop is complete. Persistent TeamCity-style agent
registrations and status axes, heartbeats, separately managed reported/custom parameters,
restart-safe build ownership and cancellation, durable FIFO scheduling with queue-wait deadlines,
acknowledged assignments and terminal results, immutable assigned-agent provenance, the protected
Agents / Queue & Builds panels, the scoped ControlPlane API, and `viv-cli login` / `viv-cli run` / `viv-cli cancel`
exist and have tier-2 coverage. Strict Phase-1 `vivarium.yaml` parsing and hardened deterministic
payload archives are also implemented. The transport-independent management kernel now provides an
ordered checksummed SQLite migration ledger, fail-closed schema validation, a minimal append-only audit
journal, shared actor/correlation context, and one authorization evaluator beneath the
ControlPlane, panel, and blob boundaries. The current Blazor panel is transitional: the accepted target
  follows Depot's TypeScript/Vite panel on pinned EyeAuras Workbench and the public REST management API (D35). The
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
`viv-cli agent upgrade` / `viv-cli agent upgrade-status` (D30); new operations always resolve the matching
Agent package from the running Server release and observed RID, while raw publication is hidden behind
an explicit development/test option. Real
bootstrap child-process success and rollback paths and two-Agent isolation have tier-2 evidence.
Installers/stamped enrollment archives, signing, previous-release compatibility CI, fleet rollout
orchestration/channels, and the remaining bootstrap bad-download/interrupted-activation evidence are
not complete, so the bootstrap stays change-controlled and is not yet declared frozen. The first D31
responsiveness slice now adds an active-Build journal and startup reconciliation, explicit graceful and
separately authorized force stop, durable stop/assignment acknowledgements and deadlines, fenced
heartbeat workload assertions, persistent health quarantine, priority/byte-bounded Agent messaging,
bounded controller live logs, and durable general Agent restart confirmed only by a newer connection
generation and a different Bootstrap child process-instance fence. Native process
containment/resource governance, durable output-gap evidence, an
out-of-process controller-to-Bootstrap recovery path for a wedged Agent, CLI/UI lifecycle actions, and
cross-platform fault evidence remain open. The docs remain authoritative
for shape — when code and a decision disagree, fix one of them in the same
commit, never neither.

Vivarium itself now builds through a provider-neutral Cake.Frosting application and a TeamCity
Compile → Release → Publish chain. Compile tests once and cross-publishes every supported RID with one
version; Release consumes those exact artifacts and emits deterministic `viv-server`, `viv-agent`, and
`viv-cli` archives; each Server archive includes both public Agent templates and the exact D30
`agent-packages/catalog.json`; guarded Publish creates or resumes the matching GitHub Release. GitHub
Actions is intentionally disabled. Signing, previous-release compatibility CI, and additional native
platform evidence remain release-quality gates.

## Repository conventions

- All committed content — docs, code, comments, commit messages — is in **English**.
- Target stack: **.NET 10 / C#** for controller, agent, bootstrap, CLI; protocol in **proto3**
  (`Vivarium.Contracts`). TypeScript/Vite + EyeAuras Workbench for the panel (D35), built to static assets and served
  by the controller. The new D32 target uses one initial Host with in-process EyeAuras.Chiral nodes,
  PostgreSQL business storage and object/blob bytes. SQLite is the current transitional persistence.
- Layout: `src/Vivarium.{Contracts,Controller,Agent,Bootstrap,Cli}`, controller-owned TypeScript sources,
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
[`docs/roles/README.md`](docs/roles/README.md). Markdown guides are the shared role contract. Native
Claude discovery adapters retain their tool/model metadata and link to those guides; never duplicate
durable instructions in harness-specific prompt files. Follow the index's independent-delegation rules.

- **Agent API/SDK Expert** owns AgentHub, capabilities, enrollment, deployment, upgrades, and SDK
  compatibility. Other experts request agent capabilities through this role.
- **TeamCity Expert** owns projects, build configurations, builds, steps, requirements, queue vocabulary/
  visible policy, and build chains; **Scheduling/Coordination Expert** owns durable queue algorithms,
  leases, fencing, and recovery. **AgentExplorer Expert** owns independent fleet observation and remote
  operation semantics.
- **Vivarium REST Expert** reviews every public management resource or mutation. **UI Expert** reviews
  every web UI change and the TypeScript/EyeAuras Workbench integration.
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

- Git is the source of truth for versioned **desired configuration**: projects/build definitions,
  fleet policy and agent desired properties use one Git validation/mutation/reconciliation path.
  D34 explicitly replaces Git identity/RBAC authority with PostgreSQL application data: users,
  groups, editable TeamCity roles/permissions and scoped grants use common authorized/audited services.
  D35 uses Chiral's managed settings/restart lifecycle for private Host/DB/OIDC/storage configuration;
  do not create a second settings authority. Other structured business/runtime data lives in PostgreSQL;
  Storage owns large bytes with PostgreSQL references. Current SQLite/Git-RBAC remains transitional.
- D34 adopts Depot's stock Identity/OpenIddict OIDC, normal cookie and installation-user lifecycle.
  TeamCity role/permission IDs, editability, scopes, groups and inheritance must match 1:1; do not
  retain the superseded immutable role floor or reinterpret TeamCity grants as narrower fleet grants.
- D35 shares Depot's UI technologies/integration, not a mandatory layout redesign. Preserve useful
  current Vivarium/TeamCity navigation, pages and workflows while porting to TypeScript/Workbench.
- D36 makes REST resource/identity/state/mutation/protocol decomposition a P0 structural gate.
  R0 must provide a real local Host+Agent and first unchanged-CLI smoke path; R5 is cumulative
  conformance. Do not defer compatibility model decisions until after domain implementation.
- Credentials, secret values, session/heartbeat state, observations, queue/build/operation state,
  results, and high-volume logs do **not** belong in Git. Runtime actions such as authorize, cancel,
  restart, rollback, or remote execution are authorized and written to the audit journal.
- `/api/v1` REST is the canonical public management surface for both TeamCity and AgentExplorer. The TypeScript
  UI and CLI consume it. AgentHub remains reverse-streaming gRPC, blob bytes remain on the authenticated
  HTTP data plane, and the existing gRPC ControlPlane is a transitional compatibility adapter only.
  D33 additionally requires a TeamCity-compatible `/app/rest` facade for existing clients. Both HTTP
  contracts use the same application services, authorization, appropriate Git/PG/Chiral authority and audit; compatibility
  never grants a separate state authority or permission bypass.
- Every state-changing request carries actor, correlation/idempotency identity, target, outcome, and,
  when configuration-derived, the Git revision. Logs and audit records must be bounded and redacted.

## Core implementation rules

### 1. Think before coding

Inspect relevant evidence and state material assumptions. Resolve routine choices from the existing
contracts and user intent. Ask only when an unresolved decision materially changes behavior, scope, or
a contract; continue independent work while awaiting a necessary answer.

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

For code changes: `dotnet build` and `dotnet test` at the solution root must pass before handoff
(test-tier definitions and CI mapping: [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md)), and
protocol changes must keep `Vivarium.Contracts` backward-compatible within a minor version (pool
checkpoints may carry a stale agent until its post-revert upgrade).

Documentation-only changes require factual consistency, local-link, native-adapter metadata, and diff checks. Report unrun code/runtime checks; do not trigger release or fleet operations merely to validate prose.
