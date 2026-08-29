# Usable-agent-server planning ledger

Status: active. These entries guide the workstream but do not amend numbered architecture decisions.
Any adopted structural refinement is copied into `docs/ARCHITECTURE.md` in the same implementation
commit.

## Adopted W1 — authoritative sequence wins

The workstream follows the accepted Phase-1 dependency order: management kernel → read-only REST/OpenAPI
→ typed Agent facts → managed-local Git → build REST/SSE/CLI → first-run/RBAC → domain expansion →
React parity → packages/installers/upgrades. Deployment design and read-only investigation may proceed
earlier, but production bootstrap/package work does not silently bypass these gates. `phases.md` is the
executable orchestration plan.

## Proposed P1 — trust before convenience

The first usable installer is a stamped ZIP. The browser supplies the expected SHA-256 digest and the
bundle embeds the pinned controller identity. `setup.sh` / `setup.ps1` one-liners ship only after the
script bytes are authenticated before execution. A convenient command is not evidence of a trust path.

## Proposed P2 — seed the first Agent locally

The stamped bundle includes the bootstrap, `bootstrap.json`, and a seed copy of the selected Agent
package. First enrollment therefore does not depend on an unauthenticated bootstrap manifest request.
The seed launches, enrolls, and remains unauthorized until explicit approval.

## Proposed P3 — reuse the protected Agent credential for package reads

The enrollment credential is short-lived, single-purpose, consumed, and scrubbed from the installed
configuration. Before authorization the bootstrap never fetches a manifest; it starts the verified seed
package. After authorization, bootstrap and Agent run under the same installation principal and share the
protected data directory, so bootstrap reuses the existing Agent credential from `data/auth.token` for
same-origin manifest/package reads. The HTTP routes still require a narrow `AgentPackageRead` permission;
the bearer credential never appears in a URL, Git, logs, or audit payloads.

A redundant second package credential would add delivery, rotation, revocation, and partial-failure state
without a meaningful local isolation boundary. Reconsider it only if bootstrap and Agent later run under
genuinely isolated OS principals. This proposed answer to the D2/D21 gap still requires a new numbered
architecture decision, persistence/redaction rules, and security review before bootstrap code changes.

## Proposed P4 — bounded deployment fast path

The first usable milestone has exactly one desired release: the Agent version bundled with the running
controller. It does not expose a mutable channel or version picker, so it does not bypass Git-backed
desired configuration. Release/channel policy is added only through the Git mutation gateway.

## Proposed P5 — two-Agent correctness, one-Agent hardware proof

Acceptance uses two independently enrolled logical Agents at once and one actual installed physical
Agent. No global current-Agent state, shared enrollment token, singleton package directory, or scheduler
shortcut is permitted. The real target is assumed to be the local macOS arm64 host; changing the first
real target to Windows or Linux changes only the platform-evidence slice.

## Proposed P6 — no duplicate UI

The deployment backend is usable through REST/CLI and the existing authorization surface before its
React screen arrives. Deploy, packages, and rollout screens are implemented only in the accepted
React/EyeAuras Workbench target. The Blazor prototype receives no new deployment feature.

## Adopted W3 — administration precedes the deployment milestone

The usable deployment milestone follows D26 first-admin claim, TeamCity/fleet RBAC, and bounded
Superuser recovery. Legacy development credentials migrate without gaining authority; they are not the
accepted administration contract for deployment. Real multi-platform install/upgrade evidence still
determines which platforms may be called release-supported.

## Adopted W9 — D30 central upgrades are explicit runtime operations

The controller may import a release-bundled immutable package catalog without restarting Agents.
An authorized caller creates one per-Agent durable operation; its maintenance drain precedes restart,
active work finishes, and package access is limited to that credential-derived Agent and operation.
Success requires exact newer-generation reconciliation followed by controller health acceptance,
atomic Agent marker write, and Agent confirmation. Bootstrap retains the prior content-addressed slot,
rolls back once, and must report that prior digest before fetching another manifest. This decision has
graduated to Architecture D30 and the owning focused designs.

## Adopted W2 — Workbench navigation and routed Agent pages

The shell uses a narrow activity rail whose switches select TeamCity, AgentExplorer, or Administration.
The adjacent context pane is expandable/auto-hideable and changes its tree/navigation for the selected
workspace. Breadcrumbs, page headers, actions, and local tabs live in the main routed page, following
modern TeamCity rather than creating dashboard tiles.

AgentExplorer has one dedicated `/agent-explorer/agents` collection page. Selecting an Agent navigates to
its own stable `/agent-explorer/agents/{agentId}/{tab?}` page. Summary, build history, compatible configurations, environment,
processes, network, metrics, logs, and parameters are Agent-local tabs on that page; they are not global sidebar
destinations and the navigation pane does not duplicate the Agent list.

This contract has graduated to D25, architecture §11, `docs/design/ui.md`, the roadmap, and the normative
walkthrough. The workstream ledger retains the decision only to route implementation/evidence.

## Adopted W4 — read-only REST foundation is the browser boundary

The real controller host now exposes protected system, Agent, audit, matrix-build, and FIFO queue reads
under `/api/v1`, with deterministic OpenAPI, bounded opaque cursors, conditional ETags, and correlated
RFC 9457 errors. The React replacement consumes these resources; it does not regain in-process access to
controller stores. The anonymous OpenAPI document contains only `/api/v1` operations and deliberately
excludes transitional Blazor, blob-byte, login, and gRPC surfaces.

This closes only the read foundation. Build mutations/SSE, object-scoped blob staging, desired-state
writes beyond the later Wave-3 Agent `spec.enabled` slice, and first-run/RBAC remain gated by their
later waves.

## Adopted W5 — negotiated capabilities and observation provenance are explicit

Current Agents negotiate a bounded additive v1 range and advertise versioned capability support
independently from one collection attempt's outcome. Legacy Agents are visible and can complete adopted
work, but absence of negotiation never implies build-runner support for new assignments.

Credential replacement and accepted connections have separate durable monotonic generations. Typed
static facts carry their accepted credential/connection provenance, while REST exposes both observation
and current generations so stale or superseded evidence cannot masquerade as current. Capabilities are
stored independently from observations, including a negotiated Agent whose fact collector is unavailable.

## Adopted W6 — managed-local Git is active for Agent enablement only

The controller now creates or adopts a normal non-bare managed-local repository on `main` and uses the
D29 system-Git adapter to validate complete candidate trees, create bounded attributable commits, and
advance the authoritative ref by compare-and-swap before reconciliation. Migration v5 durably tracks
mutation operations, revision sets/members, materialization scope, active and last-known-good state,
and the first Agent desired projection. Migration v6 adds affected mutation targets, exact conflict
replay evidence, and retryable repository-attempt failures. Invalid, blocked, or removal revisions remain
visible while the prior valid projection stays active. Scheduler admission and desired activation share
the Agent lifecycle lease, and a bounded hosted monitor converges external local-Git heads into both the
durable and live projections.

The first schema is intentionally only `.vivarium/agents/{id}.yaml` with explicit boolean
`spec.enabled`. `/api/v1/agents/{id}/settings` GET/PUT is the only Git-backed desired mutation closed by
Wave 3. This does not declare Agent names/custom properties, RBAC, Projects/Build Configurations,
provider/image policy, server settings, remote authority, review branches, or general REST mutations
implemented. Wave 4 therefore begins at build REST/SSE, object-scoped blobs, CLI parity, and TRX rather
than broadening desired schemas opportunistically.

## Adopted W7 — Wave 4 closes at the public build boundary and first TRX projection

Object-scoped upload plans and immutable build/blob references now separate physical deduplication from
authorization. Build submit/cancel is principal-idempotent, durable build events are resumable with an
explicit retention-gap recovery contract, and the live CLI build flow consumes REST/SSE. The gRPC
ControlPlane remains frozen only for compatibility and legacy list/authorize parity; it is not extended.

The first controller-side TRX adapter is deliberately bounded and sequential. Migration v8 persists
build/report projection states, typed failures, stable/fallback tests, occurrences, adapter/schema
identity, and immutable raw-artifact provenance. Raw TRX remains authoritative, and startup catches up
missing/PENDING terminal builds. This does not declare result REST/UI, JUnit, build problems,
TEST/CRASH classification, cross-platform producer goldens, or test history implemented.

The Wave-4 topology gate uses two independent Agent runners and observes two matrix cells running
concurrently on distinct Agents. Work proceeds serially at the orchestration layer after the host OOM;
feature correctness does not depend on orchestration parallelism.

## Adopted W8 — first administration activation is Git-first and recovery is host-explicit

Wave 5 extends the control-repository schema only with canonical User and direct built-in RoleBinding
documents. The five recognizable built-in role minimums remain product code; Git cannot weaken them.
The first administrator's User and global `SYSTEM_ADMIN` binding land in one atomic Git commit, and the
private password verifier does not become active until reconciliation proves that exact commit is the
active revision. Setup values and sessions never authenticate normal management APIs.

An active restart never creates recovery authority. A host-local action issues one purpose-bound,
single-use recovery value, its dedicated exchange creates one bounded Superuser session, and local
revocation immediately returns the instance to `ACTIVE` and closes the session. Legacy admin/submit
tokens remain narrowly mapped compatibility adapters until named automation/PAT migration exists; this
decision does not call groups, service accounts, custom roles, project ancestry, general user/role REST,
or legacy-token removal complete.
