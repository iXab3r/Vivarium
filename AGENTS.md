# Vivarium Project Notes

Vivarium is an OSS TeamCity-style CI build server and test farm plus AgentExplorer fleet manager. One cross-platform agent runs
jobs and safe remote operations on user-provided physical machines first and on snapshot-managed VMs
later, producing centralized test × scenario results across Windows, Linux, and macOS.

> **Docs map:** this file (AGENTS.md) holds the *rules*;
> [`docs/acceptance/`](docs/acceptance/README.md) holds expected business behavior and lifecycle AC;
> [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
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

The selected fresh D34/D35 Host passed US-013/R0 and is delivered on main (PR #165); the full
Prepare → Windows → Release integration remains in progress under #116–#118.
The active PILOT branch has selected real source-free Linux → Windows → Linux chain evidence through
independent compatible REST, unchanged CLI 0.5.6 and Workbench entries, including exact selected
artifact bytes, dependency failure/cancellation, native graph reads and same-cookie withdrawal.
Explicit successful snapshot reuse and durable queued-request resolution also have selected evidence.
The branch now adds anonymous ON_AGENT Git checkout on Windows/Linux, separate immutable selected
and consumed workload revisions, same-chain revision pinning, private generated script inputs and
source-collection/missing-Git failure evidence. These are selected cohorts, not PILOT/main acceptance;
the selected recipe now also runs actual Docker Prepare/Release and Windows Desktop PowerShell
FILE/PS1/x64, with immutable step/container receipts, scoped cache and exact bytes. D47 adds private
Engine ownership and conservative restart recovery; selected actual owner-death and live synthetic
secret metadata/output checks have evidence. These runner cohorts do not accept their complete API
families. The selected recipe artifact rules now map literal/leaf wildcard/recursive paths and
stream verified private bytes, with exact 257 KiB transfer and missing-required-input evidence plus
R0 upload/current-authority regressions. This does not accept the complete artifact API or prove
the 2 GiB transfer ceiling. Selected real steps now read their own Git HEAD from root and nested
directories through relative gitfiles, including long-path Windows and read-only container metadata.
D49 now collects a queued source selection before physical ownership and proves successful producer
reuse on the same source SHA, three new executions after a changed SHA and Host restart durability.
The cumulative selected story/SDK cohort now passes 30/30 (29 story rows and the seven-variant
SDK/owner-death test), with root 698/0. D50 adds negotiated literal build-number metadata, live
projection, terminal recovery and secret masking. Full PILOT/main acceptance still requires
remaining fault/authority/UI profiles, triggers/single publisher
and actual T3 build/package/publication. Targeted temporary CLI chain input has D48 evidence below.

Explicit local `init`, `nodes`, `migrate` and `run` commands now compose managed Chiral Host/Database/Identity/Storage settings, PostgreSQL
Identity/OpenIddict migrations with checksum intent, ordinary account handlers and private object
HTTP. Source-free C#/Git Builds now dispatch through PG-owned leases to a real Windows Agent with
independent result/output receipts and a bounded literal-artifact publisher. Two-build Agent reuse
and retained bytes/cursors after a Host restart have selected integration evidence. An ordinary key
now has selected native P/Q isolation, project delegation, grant/eligibility/revocation, failure and
queue/running-cancel evidence. Selected output-expiry/epoch/private-byte deletion and deletion-retry
evidence now survives a cold Host restart. D45 adds native per-run secret environments with encrypted
input/assignment recovery and existing Agent output masking; permanent Project secret storage is deferred.
D48 adds actor/root/catalogue-bound one-use encrypted chain input, letting ordinary clients carry
only an opaque parameter while each named Build receives its own D45 environment. Selected real
CLI/process/masking, restart, expiry, quota, current-rights and concurrent-consumption evidence exists.
The selected fresh `/app/rest` adapter now runs the published unchanged CLI 0.5.6 through ordinary
login, C#/Git discovery, successful/failed runs, queued/running cancellation and exact artifact
download. Its bounded integration fixture uses a real Windows Agent and a dedicated Linux/Docker
Agent, plus shared native reads. Agent enablement is a journalled Git edit through the catalogue
coordinator; source-file modification times are additive immutable artifact observations.
The selected R0 gate has 28 bound obligations, independent aggregate/Security PASS and verified main
delivery; broader compatibility and full story acceptance remain open. Exact transient progress wire
cases remain unaccepted under D46/#166. Ordinary OIDC/current
TeamCity grants, catalogue/Agent/build integration and Workbench remain in progress. The legacy
controller below remains available until its actual replacement gates pass.

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
- Dependencies must be MIT/Apache-2.0-compatible (the project is MIT). Owner-approved exception
  (2026-11-02): the closed `EyeAuras.Storage` and `EyeAuras.Storage.Contracts` libraries may be
  consumed as dependencies; this does not declare those libraries or BankaStore MIT-licensed.
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
- Adopt [Story Delivery Lead](docs/roles/story-delivery-lead.md) to connect actor goals, surface
  scenarios and API evidence to useful increments; follow the existing [delivery practice](docs/design/api-ledger-delivery.md#actor-goal-stories-and-traceability).

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
- D37 requires a TeamCity semantic superset for builds/agents throughout development. Every affected
  change/milestone names its business AC, TCM model invariants and wire cases, preserves the base
  behavior and proves relevant extension neutrality. Do not wait until R5 to check model drift.
- Highest-level AC live in `docs/acceptance/**/*.feature` in business language, independent of internal
  architecture. Business Acceptance owns intent; Testing Architect selects minimal evidence/oracles;
  Test Steward owns bindings/execution. Use the D37 hierarchy/pyramid; parsed/unbound features are
  not GREEN, Gherkin does not force E2E, and no new test framework is implied.
- D38 makes executable, reference-backed REST conformance and a genuine RED baseline the first P0
  execution slice before structural replacement. Prefer WireMock.Net replay on the existing NUnit/
  Test SDK stack; mock GREEN does not prove Vivarium behavior. Hundreds/thousands of real variants
  are expected as coverage expands. Preserve implemented GREEN cases, execute/report remaining raw
  RED explicitly, and distinguish missing reference/product prerequisites from harness/setup errors.
  No guessed oracle, skipped placeholder, automatic RED allowance or product-derived expectation.
  Deterministic fixtures run production logic with controlled time and bounded external actors;
  WireMock replays HTTP rather than implementing a second model. P0.REST.2 proves the smallest
  composition, isolation/timer control and maintenance/runtime cost before bulk case expansion.
- D39 requires task blocks (REST contract → implementation → REST acceptance → milestone closure)
  and a mechanically checked API ledger/status at each milestone. GitHub owns live task state;
  ledger/results own compatibility evidence. Unknown denominators are unknown, not percentages.
  Follow [API-ledger delivery](docs/design/api-ledger-delivery.md): focused TDD during work, batch
  required checks at coherent checkpoints, formal independent aggregate correctness/simplicity and
  explicit Security/Adversarial review at milestone end. Fix all milestone findings/debts there;
  no carryover, no next-milestone implementation before closure, no per-task review/full-suite tax.
- D40 makes coherent subsystems the implementation units. Maintain the full REST ledger with one
  primary subsystem/variant owner and related-owner links; attach REST-contract/acceptance subtasks
  to same-milestone subsystem implementation tasks. Track subsystem lifecycle/integration and API
  completeness separately, with milestone/global rollups. See [subsystems](docs/design/subsystems.md).
  REST discovery/TDD still comes early; do not implement route by route or multiply state authorities.
- D41 replaces the transitional YAML/native payload submission entry with real C#/Git Project/BuildType
  runs for the fresh US-013 installation. Preserve execution/matrix/cancel/log/artifact capabilities,
  AgentHub and Bootstrap; retire old code only with verified replacement. Legacy public alias/DTO
  preservation is not a target requirement.
- D42 adopts free OSS capacity without license-imposed limits and explicitly unsupported commercial
  TeamCity key operations. Keep that owner-approved exception visible in source allocation and negative
  acceptance; unsupported is not positive conformance. Other compatibility obligations remain.
- D43 isolates compilation and execution of project C# inside Linux/Docker, without network, Host
  data or credentials and with resource/time/output bounds. The coordinator owns pinned compiler/
  dependency inputs and validates untrusted output under current authority; no same-user Host fallback.
  Existing trusted-source studies are not isolation or production-evaluator acceptance.
- D44 selects managed typed C# declarations/edits with untouched user computations and one effective
  Git owner per Project/field. Disabled synchronization uses managed-local Git, not PG desired state;
  an ancestor source cannot seize an independently owned child. Reference modes/completion and
  XML/Kotlin obligations remain; the source studies are not full SDK or product acceptance.
- D45 scopes US-013 secrets to one run's Agent environment, with private per-Build encrypted recovery
  input/assignments and Agent-side exact-value output masking. No raw values in Git, collected plans,
  payload scripts, public representations or audit; permanent Project secret storage is a separate story.
- D48 permits one-use native prepared chain input for ordinary clients: actor/root/exact-catalogue
  binding, fifteen-minute expiry, bounded encrypted input and targeted per-Build D45 environments.
  The explicit opaque run parameter is not bearer authority or a reusable Project secret reference.
- D49 collects immutable source selections outside queue/Agent locks before physical ownership,
  with four bounded pending discoveries and a fenced queued/unassigned commit. Successful-only
  suitability compares effective inputs, root/checkout, source SHA and selected upstreams; it never
  compares encrypted run secrets or replaces an assigned execution.
- D50 negotiates literal stdout Build-number metadata at the D14-planned AgentMsg tag 7, preserving
  numeric counters/outcomes and legacy assignments. Mask before metadata transport; retain immutable
  sequenced PG observations and complete terminal recovery, rejecting conflicts and raw secrets.
- D46 accepts US-013 by complete actor/process/outcome/stop/recovery/byte behavior. Exact background
  progress estimates and dynamic cancellation timestamp cadence are separately owned by #166 under
  SS-RUN@R2. Four full literal R0 workflows remain required; two distinct behavioral obligations
  cannot discharge the retained complete nonzero/running-cancel wire cases. Other compatibility and
  independent-review/zero-debt/main gates apply within that owner-approved scope.
- Credentials, secret values, session/heartbeat state, observations, queue/build/operation state,
  results, and high-volume logs do **not** belong in Git. Runtime actions such as authorize, cancel,
  restart, rollback, or remote execution are authorized and written to the audit journal.
- `/api/v1` REST is the canonical public management surface for both TeamCity and AgentExplorer. The TypeScript
  UI and CLI consume it. AgentHub remains reverse-streaming gRPC, blob bytes remain on the authenticated
  HTTP data plane, and the existing gRPC ControlPlane is a transitional compatibility adapter only.
  D33 additionally requires a TeamCity-compatible `/app/rest` facade for existing clients. Both HTTP
  contracts use the same application services, authorization, appropriate Git/PG/Chiral authority and audit; compatibility
  never grants a separate state authority or permission bypass.
- For the adopted TeamCity model, operation handling comes from the reference TeamCity contract:
  defaults, validation, states, permissions, side effects, replacement/merge, retry, cancellation and
  completion behavior are not independent Vivarium policy choices. Native and compatible surfaces
  share that processing. Unknown reference variants remain verification gaps; internal technology
  choices, framework defaults or generic hardening do not justify a different observable result.
- Every state-changing request has actor, internal correlation/operation identity, target, outcome,
  and, when configuration-derived, the Git revision. Idempotency/preconditions follow the frozen
  operation contract or explicit native opt-in workflow; do not require new compatible-client headers
  or deduplicate intentional unkeyed runs. Logs and audit records must be bounded and redacted.

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
