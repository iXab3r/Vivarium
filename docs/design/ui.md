# Depot-style Workbench UI

> Status: **Accepted — D35, 2026-10-31**
> Implementation: **Current panel is Blazor; replacement not implemented**
> Maintainers: UI, REST, Identity, TeamCity, AgentExplorer, Test Steward
> Related: [Architecture](../ARCHITECTURE.md) D24/D25/D32–D35

## Actual Depot reference

EyeAuras.Depot at 20238384013326cddb3296963a6d101a0970efc0 serves a production frontend from
apps/depot-mock (the historical name does not mean it is still a synthetic mock). It uses TypeScript,
Vite and @eyeauras/workbench 0.2.6; there is no React/React Router runtime dependency.

src/main.ts creates the Workbench shell, section rail and page lifetimes. repositories.ts, nodes.ts
and access.ts compose resizable outline/list plus inspector views with Workbench controls.
nodes-api.ts and node-settings.ts implement actual server schema/revision settings editing.
session.ts consumes server account/CSRF bootstrap; management-api.ts performs same-origin cookie
requests. These are app-owned integration modules, not a ready-made universal admin package.

The ASP.NET Host packages frontend dist into wwwroot. UI/WorkbenchShell.cs inserts only the current
account presentation and antiforgery token, then serves the actual shell. Identity Razor pages keep
stock credential handlers and share product styling/navigation. Vite/Node are development/build
tools, not services required in production.

Reference paths are in [the Depot study](../../.workspace/workstreams/coherent-refactor/depot-study.md).
Follow source and current server-backed scenarios where older Depot prose still describes mocks.

## Composition to transfer

Use one compact product header with sign-in/current account, My keys and sign-out; one shallow
Workbench rail; a resizable contextual outline/list; and the selected entity's inspector/page.
Reuse native tree disclosure, selection, keyboard behavior, focus, split layout and disposal.
Use a pinned npm package/lock and packaged fonts/assets, with license/provenance evidence.

Do not add React adapters, a second SPA framework, a frontend runtime server, duplicate headers or
a custom set of lookalike controls. Add an editor dependency only for an actual editing workflow;
honor the real Workbench peer dependencies. Build/release inputs are pinned and offline-preparable.

## Vivarium information architecture

Adapt the composition to CI rather than copying Depot's package entities:

- Projects/Builds: project/configuration outline, history and selected build inspector.
- Queue: actionable waiting/running work, requirements/compatibility reasons and cancellation.
- Agents: list and stable Agent details, status axes, safe facts, build history and lifecycle actions.
- Nodes: logical resource owners and schema-driven configuration/status.
- Access: users, groups, editable roles/included roles, permissions and global/project assignments.
- Account: standard sign-in/My keys/sign-out; privileged technical identity is clearly distinguishable.

Build-local views include overview, steps, durable logs, tests/test history and failures, artifacts,
parameters, dependencies/chain and statistics as their capabilities land. Stable entity URLs and
Back/Forward/direct navigation must survive renames and reloads. Long logs/results need bounded
loading, not complete histories in browser memory. Do not fill unavailable capabilities with fake data.

Workbench styling/layout does not relax TeamCity behavioral parity. Every operator workflow in
[the parity contract](teamcity-parity.md) still needs real services and acceptance evidence.
AgentExplorer process/network/files capabilities remain explicitly unimplemented until their owners
deliver them; a navigation label is not functionality.

## Nodes, configuration and storage boundaries

Nodes use Chiral schemas/UI schemas, read-only runtime facts and revisioned editing. Show saved
versus active revision, readiness and restart requirement; topology/list selection stays coherent.
Mask configured secrets, use blank replacement fields/explicit clear, reject stale InstanceId/revision,
preserve a typed draft on conflict, and make Refresh/Reset explicit. Render supported typed schema
fields only; unsupported shapes stay read-only instead of executing arbitrary schema code/HTML.
Do not create an unrestricted proxy to remote node URLs.

One Host initially co-hosts logical resource owners, not a remote service per class. Proposed Vivarium
owners are Host, Database, Identity, Builds, Agents and Storage; exact boundaries are fixed from
actual settings/lifecycle contracts in R0, not copied blindly from Depot's five package-service nodes.

Versioned desired build/fleet settings use Git validation/reconciliation and visible revision/diff.
Host/DB/OIDC/storage resource settings use Chiral's private managed store and apply/restart lifecycle.
Users, roles/groups/grants and runtime operations use PG services and immediate current checks, not
Git commits. Artifact/log bytes use Storage with PG-owned manifests/references. No UI shadow state
or independent JSON authority is introduced.

## API and authentication boundary

The Host serves the UI and /api/v1 on one origin; SSE updates resource projections. /app/rest is
the compatible client adapter over the same services, never a separate UI database or scheduler.
No browser component accesses EF, filesystem or in-process management services directly.

Server-supplied account/capability presentation guides controls but never authorizes an operation.
Each endpoint rechecks current user, permissions, resource scope and relevant concurrency token.
Cookie mutations use standard antiforgery. A hostile display name is encoded, and no bearer token
is embedded into the shell, browser storage, URL or ordinary diagnostics. One-time token reveal
stays response-only and guarded in acceptance artifacts.

Identity pages retain stock OIDC/Identity behavior and share the Workbench product presentation;
the UI does not implement password verification or OIDC callbacks in JavaScript. Access adapts
Depot's list/inspector pattern to TeamCity roles/groups/project scopes, not package grants or a
single isAdmin flag as the complete permission model.

## Delivery and evidence

R0 provides the real shell, Nodes and shared debug/Automation fixtures. R1 adds actual login,
account and TeamCity-style Access. R2–R4 supply usable run/queue/result/log/test/artifact journeys;
R5 verifies unchanged clients and R6 cumulative migration/cleanup. Each milestone may merge into
main separately after its own gate. Remove the Blazor surface when that workflow's replacement
passes its named checks; any temporary adapter has an owner/removal milestone and one authority.

Write independent English Gherkin cases with ordinary actors before implementation. Mutations
claimed as UI coverage use actual UI clicks/forms; API/EF may arrange fixtures but cannot replace
the asserted user action. Use EyeAuras.Automation/Reqnroll/Playwright with real Host/PG/storage,
pinned assets and debug/acceptance fixture reuse. Credential contexts disable raw DOM/HAR/traces;
guarded screenshots complement, not substitute for, assertions.

Cover keyboard/selection, narrow-width reflow, stable navigation, empty/error/denied states, node
CAS/draft preservation/masked secrets, same-session permission withdrawal, run/cancel, log tail/
download, individual test history and actual artifact bytes. Fake preview approval is not runtime
acceptance, and a platform/admin-only happy path cannot close the ordinary-user journey.
