# Depot study and transfer decisions

Reference: [EyeAuras.Depot](https://github.com/iXab3r/EyeAuras.Depot) at
`20238384013326cddb3296963a6d101a0970efc0` (2026-10-31 fetch: local main equals origin/main).
All 101 issues and 16 milestones were read as a bounded inventory, including the closed older
planning/identity issues requested by the owner; selected bodies, designs and code were examined.
Raw issue bodies are ignored under `artifacts/depot-refactor-study/`, not duplicated into Git.
Two issues remain open in optional M8; “Depot is done” is not a blanket claim about every proposal.

## What its plan actually does

Depot milestones name user-visible increments: P0 contracts/plan; M0 locally running persisted Host;
M1 identity/current access; M2 generic artifacts; M3 NuGet; M4 Cargo; M5 npm; M6 Workbench/recovery;
M7 migration rehearsal; optional M8 role synchronization; M9 full-SDK/cutover readiness.
Later M6.1–M6.5 refinements added sign-in/UI/native lifecycles and QA-owned independent journeys.
Reuse the task shape, not its milestone numbers, package-domain features or 101-item backlog.

- [#4](https://github.com/iXab3r/EyeAuras.Depot/issues/4),
  [#6](https://github.com/iXab3r/EyeAuras.Depot/issues/6),
  [#7](https://github.com/iXab3r/EyeAuras.Depot/issues/7): concrete contracts before implementation,
  native milestones/sub-issues, explicit simplification; a written baseline is not an implemented backend.
- [#10](https://github.com/iXab3r/EyeAuras.Depot/issues/10),
  [#11](https://github.com/iXab3r/EyeAuras.Depot/issues/11),
  [#12](https://github.com/iXab3r/EyeAuras.Depot/issues/12): Automation, shared local-debug and closed
  execution contours, owned process/container cleanup from the first executable increment.
- [#15](https://github.com/iXab3r/EyeAuras.Depot/issues/15),
  [#16](https://github.com/iXab3r/EyeAuras.Depot/issues/16),
  [#17](https://github.com/iXab3r/EyeAuras.Depot/issues/17): standard Identity/OpenIddict/EF, local
  issuer+subject identity, current local permissions rather than trusting stale token roles.
- [#62](https://github.com/iXab3r/EyeAuras.Depot/issues/62): unchanged-browser-cookie revocation
  matters independently of JWT revocation; testing only the bearer path left a real gap.
- [#165](https://github.com/iXab3r/EyeAuras.Depot/issues/165),
  [#167](https://github.com/iXab3r/EyeAuras.Depot/issues/167): QA defines independent user journeys
  first. Replace a coupled all-in-one scenario and custom Python orchestration with stock Automation,
  real UI mutations and native clients. UI tests must not mutate through API/EF and claim UI coverage.
- [#179](https://github.com/iXab3r/EyeAuras.Depot/issues/179),
  [#180](https://github.com/iXab3r/EyeAuras.Depot/issues/180): inventory real retained originals and
  checksums early; missing data is a separately decided gap, never silently synthesized or omitted.
- [#183](https://github.com/iXab3r/EyeAuras.Depot/issues/183),
  [#186](https://github.com/iXab3r/EyeAuras.Depot/issues/186),
  [#195](https://github.com/iXab3r/EyeAuras.Depot/issues/195): actual cold consumers, encrypted
  recovery and cumulative final delta; even first login/token issuance crosses a rollback write
  boundary. Copying a backup file does not establish an operational rollback.

## Verified reusable building blocks

| Capability | Reference files at the frozen revision | Transfer / limit |
|---|---|---|
| One Host / nodes | `src/EyeAuras.Depot.Host/Program.cs`, `Nodes/DepotNodeRegistration.cs`, `docs/architecture/configuration.md` | Explicit logical nodes, managed private settings/CAS/restart, readiness before Identity settings are read. No distributed topology requirement |
| Chiral | Host pins `EyeAuras.Chiral`, `.AspNetCore`, `.FileStorage` to `0.1.0-depot.48.1` | Reuse actual package API; local Chiral main `41137a4` is not equivalent to the Depot package. InMemory bus does not persist business transitions |
| Object storage | Host pins `EyeAuras.Storage` to `0.1.0-depot.73.1`; `Storage/*`, configuration modules | Use streaming/object contracts where fit; CI manifests, dependency mapping, retention and GC policy remain Vivarium responsibilities |
| Identity | `Identity/DepotIdentityRegistration.cs`, `DepotExternalUsers.cs`, `DepotSignInManager.cs`; `docs/architecture/authentication.md` | Stock Identity/EF + OpenIddict code/PKCE and persistent keys. These are app-owned composition, not an existing reusable Depot Identity NuGet |
| Current access | `Access/DepotCurrentAccess.cs`, resource handlers; `docs/architecture/roles.md` | Reuse policy pattern and same-cookie/JWT tests. Package Read/Write/Delete and default-public repositories are not appropriate CI defaults |
| Superuser | `tests/scenarios/Superuser.feature`, authentication/roles designs | Normal Identity installation account with rotated startup password; accepted as Vivarium target in D34, replacing legacy D26 claim/recovery |
| Tokens | `Tokens/*`, `tests/scenarios/Tokens.feature` | Framework issuance/list/revoke, no custom verifier/epochs. Confirm TeamCity-client bearer behavior and service-account needs before selecting profile |
| Workbench | `UI/WorkbenchShell.cs`, node configuration schemas, `tests/scenarios/Workbench.feature` | Shared workbench/node forms plus Vivarium-specific projects/agents/builds/results UI. Workbench alone is not the CI product |
| Automation | Gitlink `submodules/EyeAuras.Automation` at `c09facd10b413e6a3ebfd117379009e11c00b4b4` | Pinned development source dependency; do not assume published integration packages |
| Acceptance wiring | `tools/EyeAuras.Depot.Automation`, `tests/EyeAuras.Depot.Testing`, `tests/EyeAuras.Depot.AcceptanceTests`, `tests/scenarios`, `automation.json`, `tools/Prepare.cs` | Shared fixtures for debug/acceptance, Reqnroll/NUnit/Playwright and stock Automation CLI; product-specific bindings remain local |

Pinned reference framework versions are OpenIddict 7.5.0, Identity EF 10.0.9 and Npgsql EF 10.0.3;
they are reference evidence, not a mandate to change Vivarium dependencies without compatibility review.
Keep development-only automation out of production runtime dependencies.

## Practices to adopt, with evidence

Read source: `docs/practices/issue-development.md`, `build-and-verification.md`,
`component-boundaries.md`, `docs/architecture/test-contours.md`, `automation-integration.md`,
`docs/testing/ui-scenarios.md` at the frozen Depot revision.

1. Milestone = user result + boundaries + dependencies + acceptance evidence; issue = a bounded
   contract with owners and focused checks. Native sub-issues retain the same milestone. No fixed
   issue count, no workflow-bot infrastructure just to administer the plan.
2. Practical TDD at the cheapest meaningful public contract; Gherkin business language before
   bindings; distinguish product RED, broken test setup and already-green regression.
3. Real PG for transactional claims, real storage/native clients for bytes/protocol claims, actual
   OIDC issuer compatibility smoke plus fake upstream only for controlled failures. No EF InMemory
   or disabled TLS validation as substitute evidence.
4. Prepare pinned inputs separately from isolated execution. Debug and acceptance share fixture
   composition. Existing Automation owns selection/lifetime; consumer owns setup/reset/bindings.
5. Small independently selectable UI journeys, ordinary-user operations and unchanged-credential
   permission withdrawal. Safe artifacts, owned cleanup, no raw credential browser dumps.
6. Review affected contracts plus security/adversarial behavior, then simplify at exact integrated
   revision. Do not carry introduced cleanup tails into a promised future milestone.

Depot's existing autonomous merge/deployment authority applies to Depot, not to this task. This study
does not import its permissions, its business grants, optional role sync or an entire infrastructure
copy. Standard framework composition is transferable; domain policy requires a Vivarium contract.

## Follow-up: actual UI and storage integration (2026-10-31)

Owner accepted D34/D35 after the initial study. The frontend dependency/source inspection corrects
the earlier generic React assumption: Depot uses TypeScript/Vite, Workbench 0.2.6, codicons and the
required Monaco peer, with no React runtime. apps/depot-mock is now production source despite its
historical name; no parallel synthetic mode is present. Some older UI prose still describes mock
states, so actual modules and server-backed acceptance are the implementation evidence.

- main.ts: createWorkbench, compact account header, shallow section rail, page registration/disposal
  and local navigation; Workbench provides native controls/split/tree behavior.
- UI/WorkbenchShell.cs: same-origin static shell with encoded current account and antiforgery token;
  no browser credential engine or EF access. Host csproj packages Vite dist into wwwroot.
- Identity/Pages: standard Razor login/external-login/logout handlers, styled consistently with
  Workbench. Identity owns authentication; browser UI owns presentation.
- session.ts/management-api.ts: typed minimal account bootstrap, same-origin cookie requests and
  antiforgery headers, safe error presentation. The server remains current authority.
- nodes.ts/nodes-api.ts/node-settings.ts: node list/inspector, actual schemas, InstanceId/revision CAS,
  saved/active/restart state, masked configured secrets, typed drafts and explicit Refresh/Reset.
- access.ts: list/inspector composition useful for Vivarium Users/Groups/Roles/assignments; Depot's
  package Read/Write/Delete and isAdmin gating are not the complete TeamCity permission model.
- Nodes/DepotNodeRegistration.cs: one Host, explicit logical resource owners, local Chiral messaging,
  shared managed FileStorage settings and Storage integration. Chiral is not business durability;
  Depot roles/users/grants are PG data and package bytes use object storage.

Vivarium adaptation: Git owns versioned build/fleet configuration; Chiral owns private resource
settings/lifecycle; PG owns structured business/runtime state and identity/RBAC; Storage owns large
bytes. Use common services and minimal app bindings, not a second settings service, generic admin
framework, microservice topology or copy of Depot's package domain.

The role baseline was also read from the real source with teamcity-cli roles list/show: seven role
definitions, including RESTRICTEDBOT and TOOLS_INTEGRATION, with actual permission/included-role
responses saved privately. Exact editable IDs/scopes/inheritance and both standard/custom definitions
are now the D34 target. No TeamCity roles, users, memberships or permissions were changed.

Owner UI clarification: the shared technology/integration does not require copying Depot's layout.
Current Vivarium and TeamCity already provide the desired CI workflow baseline. Workbench controls,
Host packaging, Identity integration and schema settings are reused while useful pages/navigation/
tables/build tabs remain familiar. A forced outline/inspector redesign is not part of this migration.
