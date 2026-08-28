# Expert roles — index and routing

Vivarium is developed by humans and multiple AI coding agents. A role is a harness-neutral context
pack: it defines one bounded territory, its load-bearing invariants, the evidence expected at handoff,
and the other experts that must be consulted. Roles are perspectives, not exclusive ownership or a
permission system.

Canonical guides live in this directory. `.codex/agents/` and `.claude/agents/` contain thin adapters
that route each harness back to the same guide; adapters never duplicate domain rules.

## Product and domain experts

| Role | Canonical guide | Focused design | Route work here when |
|---|---|---|---|
| Agent API/SDK Expert | [agent-api-sdk-expert.md](agent-api-sdk-expert.md) | [agent-api-sdk.md](../design/agent-api-sdk.md) | AgentHub, capabilities, enrollment, packaging, deployment, upgrades, or SDK contracts change. Other experts request agent capabilities through this role. |
| TeamCity Expert | [teamcity-expert.md](teamcity-expert.md) | [teamcity.md](../design/teamcity.md) | Projects, build configurations, steps, requirements, queues, builds, chains, or triggers change. |
| Results/Artifacts Expert | [results-artifacts-expert.md](results-artifacts-expert.md) | [results-artifacts.md](../design/results-artifacts.md) | Result finalization, artifacts, adapters, tests, build problems, matrices, or result retention changes. |
| AgentExplorer Expert | [agent-explorer-expert.md](agent-explorer-expert.md) | [agent-explorer.md](../design/agent-explorer.md) | Fleet inventory, host observation, remote operations, or software/state management changes. |
| Machine Providers/Images Expert | [machine-providers-images-expert.md](machine-providers-images-expert.md) | [machine-providers-images.md](../design/machine-providers-images.md) | Provider hosts, static/managed capacity, images, pools, clone/revert/power/console, sealing, or drift changes. |

## Public surfaces

| Role | Canonical guide | Focused design | Route work here when |
|---|---|---|---|
| Vivarium REST Expert | [vivarium-rest-expert.md](vivarium-rest-expert.md) | [rest-api.md](../design/rest-api.md) | Any public management resource, representation, mutation, event, or compatibility rule changes. |
| UI Expert | [ui-expert.md](ui-expert.md) | [ui.md](../design/ui.md) | Any user-visible web UI or EyeAuras Workbench integration changes. |

## Identity and desired configuration

| Role | Canonical guide | Focused design | Route work here when |
|---|---|---|---|
| User Roles Expert | [user-roles-expert.md](user-roles-expert.md) | [authorization-model.md](../design/authorization-model.md) | Roles, permissions, inheritance, service identities, or authorization decisions change. |
| Admin/SuperUser Expert | [admin-superuser-expert.md](admin-superuser-expert.md) | [first-run-administration.md](../design/first-run-administration.md) | First run, initial admin claim, Superuser recovery, or bootstrap administration changes. |
| Git/Versioning Expert | [git-versioning-expert.md](git-versioning-expert.md) | [git-versioning.md](../design/git-versioning.md) | Desired configuration, repository layout, revisions, validation, reconciliation, conflicts, or rollback change. |

## Runtime correctness and operations

| Role | Canonical guide | Focused design | Route work here when |
|---|---|---|---|
| Logs Expert | [logs-expert.md](logs-expert.md) | [logging.md](../design/logging.md) | Audit, diagnostic, build, agent, AgentExplorer, REST, retention, redaction, or support-export logging changes. |
| Platform Expert | [platform-expert.md](platform-expert.md) | [platform.md](../design/platform.md) | Windows, Linux, or macOS behavior, packaging, permissions, process/network/file semantics, or platform evidence changes. |
| Security Expert | [security-expert.md](security-expert.md) | [security.md](../design/security.md) | Trust boundaries, secrets, elevated-agent attack surface, remote management, or security review changes. |
| Scheduling/Coordination Expert | [scheduling-coordination-expert.md](scheduling-coordination-expert.md) | [scheduling-coordination.md](../design/scheduling-coordination.md) | Leases, fencing, queueing, cancellation, reconnect, rollback ordering, maintenance, or capacity changes. |
| Persistence/Migrations Expert | [persistence-migrations-expert.md](persistence-migrations-expert.md) | [persistence.md](../design/persistence.md) | SQLite/blob schemas, transactions, migrations, recovery, retention, backup, or projections change. |

## Governance and function roles

| Role | Guide | Adopt when |
|---|---|---|
| Docs Expert | [docs-expert.md](docs-expert.md) | Decisions, status, document authority, context routing, or durable handoff guidance changes. |
| Reconciliation Lead | [reconciliation-lead.md](reconciliation-lead.md) | A migration, replacement, parity effort, or broad audit must close a known universe in evidence-gated phases. |
| Test Steward | [test-steward.md](test-steward.md) | Tests, fixtures, runners, CI gates, compatibility evidence, or verification policy change. |

## Routing rules

1. Adopt the smallest set of roles that covers the change and state the lead role before acting.
2. Read `AGENTS.md` and all of `docs/ARCHITECTURE.md` before structural work, then read the selected
   role guide and focused design completely.
3. A domain expert may propose an agent capability, but only Agent API/SDK Expert changes the shared
   Agent API/capability contract.
4. Every user-visible web change routes through UI Expert; every public management contract routes
   through Vivarium REST Expert.
5. Every desired-setting/property mutation routes through Git/Versioning Expert. Runtime actions are
   authorized and audited, not disguised as configuration commits.
6. Cross-platform claims route through Platform Expert; security-sensitive changes route through
   Security Expert; tests and evidence route through Test Steward.
7. When a change crosses several streams, use Reconciliation Lead to freeze the affected universe and
   resolve contradictions. Docs Expert keeps the accepted result discoverable and current.

## Automatic co-review matrix

| Change touches | Lead role | Required co-review |
|---|---|---|
| AgentHub, capability, enrollment, deployment | Agent API/SDK | Platform, Security; Scheduling for work-bearing capabilities |
| Project, build configuration, queue-visible policy | TeamCity | Git/Versioning, REST; Results or Scheduling when their contracts change |
| Host inventory or remote operation | AgentExplorer | Agent API/SDK, Platform, REST, Security; Scheduling for mutating work |
| Provider, image, pool, ProviderInstance lifecycle | Machine Providers/Images | Scheduling, Platform, Agent API/SDK, Security |
| Public resource or mutation | Owning domain | REST, User Roles, Logs |
| Desired setting or property | Owning domain | Git/Versioning, Persistence, Reconciliation Lead for migrations |
| Browser-visible behavior | Owning domain | UI, REST |
| Durable runtime transition | Owning domain | Persistence, Scheduling/Coordination |
| Result, artifact, or finalization | Results/Artifacts | TeamCity, Persistence, Logs |
| Broad audit, migration, or replacement | Reconciliation Lead | Test Steward plus every affected owning role |
