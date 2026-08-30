# Focused design index

Focused designs refine one bounded subsystem or cross-cutting concern. They do not silently override
numbered decisions in [`ARCHITECTURE.md`](../ARCHITECTURE.md). Each design distinguishes target shape
from implemented state and names unresolved questions; [`ROADMAP.md`](../ROADMAP.md) remains the source
of truth for delivery order and status.

| Design | Maintainer role | Scope |
|---|---|---|
| [Agent API/SDK](agent-api-sdk.md) | Agent API/SDK Expert | AgentHub, capability negotiation, enrollment, deployment, upgrades, and SDK boundary |
| [Agent lifecycle and recovery](agent-lifecycle-recovery.md) | Agent API/SDK + Scheduling/Coordination Experts | Failure catalogue, responsiveness, Build stop escalation, workload containment, restart, quarantine, and recovery evidence |
| [TeamCity domain](teamcity.md) | TeamCity Expert | Projects, configurations, builds, steps, requirements, matrices, and build chains |
| [AgentExplorer](agent-explorer.md) | AgentExplorer Expert | Host inventory, observation, remote operations, and fleet management |
| [Machine providers/images](machine-providers-images.md) | Machine Providers/Images Expert | Provider hosts, pools, images, clone/revert/power/console, sealing, drift, and capacity |
| [REST API](rest-api.md) | Vivarium REST Expert | Canonical `/api/v1` management surface, events, compatibility, and Git-aware mutations |
| [Web UI](ui.md) | UI Expert | React, EyeAuras Workbench, information architecture, REST/SSE consumption, and Git diff UX |
| [Authorization model](authorization-model.md) | User Roles Expert | TeamCity-compatible roles plus separate fleet/AgentExplorer permissions |
| [First-run administration](first-run-administration.md) | Admin/SuperUser Expert | Initial administrator claim, recovery Superuser, Git bootstrap, and noninteractive setup |
| [Git/versioning](git-versioning.md) | Git/Versioning Expert | Desired-configuration source of truth, commits, review, reconciliation, drift, and rollback |
| [Logging](logging.md) | Logs Expert | Audit, diagnostics, workload output, redaction, correlation, bounds, and retention |
| [Platform](platform.md) | Platform Expert | Windows/Linux/macOS facts, inventory, execution, permissions, packaging, and evidence |
| [Documentation governance](documentation-governance.md) | Docs Expert | Authority, status, role packs, context loading, decisions, and same-change updates |
| [Security](security.md) | Security Expert | Trust boundaries, secrets, remote-management safety, abuse limits, and negative evidence |
| [Scheduling/coordination](scheduling-coordination.md) | Scheduling/Coordination Expert | Durable work, leases, fencing, cancellation, reconnect, rollback ordering, and capacity |
| [Persistence](persistence.md) | Persistence/Migrations Expert | Git projections, SQLite/blob durability, migrations, recovery, retention, and backup |
| [Results/artifacts](results-artifacts.md) | Results/Artifacts Expert | Terminal results, artifacts, test adapters, occurrences, build problems, and matrix projection |

Read [`documentation-governance.md`](documentation-governance.md) for precedence, status metadata,
decision promotion, and handoff rules.
