# Focused design index

Focused designs refine one bounded subsystem or cross-cutting concern. They do not silently override
numbered decisions in [`ARCHITECTURE.md`](../ARCHITECTURE.md). Each design distinguishes target shape
from implemented state and names unresolved questions; [`ROADMAP.md`](../ROADMAP.md) remains the source
of truth for delivery order and status.

Start with the [business acceptance catalogue](../acceptance/README.md) for expected behavior and
its [lifecycle review](../acceptance/lifecycle-review.md) for outstanding variants. Focused contracts
below provide the technical details; legacy implementation sections describe evidence to replace.

| Design | Maintainer role | Scope |
|---|---|---|
| [Agent API/SDK](agent-api-sdk.md) | Agent API/SDK Expert | AgentHub, capability negotiation, enrollment, deployment, upgrades, and SDK boundary |
| [Agent lifecycle and recovery](agent-lifecycle-recovery.md) | Agent API/SDK + Scheduling/Coordination Experts | Failure catalogue, responsiveness, Build stop escalation, workload containment, restart, quarantine, and recovery evidence |
| [TeamCity domain](teamcity.md) | TeamCity Expert | Projects, configurations, builds, steps, requirements, matrices, and build chains |
| [TeamCity behavioral parity](teamcity-parity.md) | TeamCity + Results + Logs + Scheduling + REST/UI | Required configuration, execution, test history, requirements, logs, automation and historical-import parity for replacing TeamCity (D32) |
| [TeamCity REST compatibility](teamcity-rest-compatibility.md) | Vivarium REST + TeamCity + Test Steward | Unchanged-client `/app/rest` compatibility, locators, media types, paging, errors, mutations and conformance (D33) |
| [REST-driven shared domain](teamcity-rest-domain.md) | REST + TeamCity + Scheduling + Git/Persistence | Early resource/ID/state/command/authority contracts, C# mutation gate and first compatible local execution (D36) |
| [REST handling and implementation notes](teamcity-rest-recommendations.md) | Reconciliation + TeamCity/REST + Identity/Git/Results | **TeamCity handling required; implementation options proposed:** contract, tradeoffs and verification for all 36 REST-AC findings |
| [Testing and continuous conformance](testing-model.md) | Business Acceptance + Testing Architect + Test Steward + TeamCity/REST | Business AC, TCM superset invariants, independent evidence pyramid and per-milestone drift/extension checks (D37) |
| [Early REST test-first gate](teamcity-rest-test-first.md) | TeamCity/REST + Testing Architect + Test Steward | Executable P0 corpus, strict WireMock.Net replay, genuine RED baseline, mandatory TDD/acceptance and honest partial-milestone gates (D38) |
| [First executable REST cohort](teamcity-rest-first-cohort.md) | Test Steward + TeamCity/REST + Testing Architect | Captured reads/source-established create-readback, strict replay/assertion controls, healthy current-product RED, native timer/isolation proof and remaining P0 gates |
| [Local reference mutation cohort](teamcity-rest-local-mutations.md) | Test Steward + TeamCity/REST + User Roles + Testing Architect | Twelve observed project/configuration/scoped-permission workflows, exact effects/error JSON, two new ready raw cases and ten explicit product prerequisites |
| [API ledger and milestone delivery](api-ledger-delivery.md) | Reconciliation + REST + Test Steward + Security | Task blocks, mechanical slice/global API-complete, focused checks, final aggregate/adversarial review and zero-debt closure (D39) |
| [Subsystem ownership](subsystems.md) | Reconciliation + domain owners + REST/Test Steward | Coherent subsystem implementation units, primary/collaborating REST ownership, native child tasks and subsystem/milestone/global progress (D40) |
| [AgentExplorer](agent-explorer.md) | AgentExplorer Expert | Host inventory, observation, remote operations, and fleet management |
| [Machine providers/images](machine-providers-images.md) | Machine Providers/Images Expert | Provider hosts, pools, images, clone/revert/power/console, sealing, drift, and capacity |
| [REST API](rest-api.md) | Vivarium REST Expert | Canonical `/api/v1` management surface, events, compatibility, and Git-aware mutations |
| [Web UI](ui.md) | UI Expert | Depot-style TypeScript/Vite Workbench, REST/SSE, node schema forms and CI workspace (D35) |
| [Authorization model](authorization-model.md) | User Roles Expert | TeamCity roles/permissions/scopes/groups/inheritance 1:1 in PostgreSQL (D34) |
| [First-run administration](first-run-administration.md) | Admin/SuperUser Expert | Depot-style Identity/OpenIddict OIDC, installation user, sessions/tokens and keys (D34) |
| [Git/versioning](git-versioning.md) | Git/Versioning Expert | Desired-configuration source of truth, commits, review, reconciliation, drift, and rollback |
| [Logging](logging.md) | Logs Expert | Audit, diagnostics, workload output, redaction, correlation, bounds, and retention |
| [Platform](platform.md) | Platform Expert | Windows/Linux/macOS facts, inventory, execution, permissions, packaging, and evidence |
| [Documentation governance](documentation-governance.md) | Docs Expert | Authority, status, role packs, context loading, decisions, and same-change updates |
| [Security](security.md) | Security Expert | Trust boundaries, secrets, remote-management safety, abuse limits, and negative evidence |
| [Scheduling/coordination](scheduling-coordination.md) | Scheduling/Coordination Expert | Durable work, leases, fencing, cancellation, reconnect, rollback ordering, and capacity |
| [Persistence](persistence.md) | Persistence/Migrations Expert | PostgreSQL business/identity data, Git projections, Storage publication, migrations, recovery and complete backup; legacy SQLite inventory |
| [Results/artifacts](results-artifacts.md) | Results/Artifacts Expert | Terminal results, artifacts, test adapters, occurrences, build problems, and matrix projection |

Read [`documentation-governance.md`](documentation-governance.md) for precedence, status metadata,
decision promotion, and handoff rules.
