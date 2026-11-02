# Identity and TeamCity authorization

> Status: **Accepted — D34, 2026-10-31**
> Implementation: **Legacy foundation exists; fresh D34 Host/IAM delivery is in progress**
> Maintainers: User Roles, Admin/SuperUser, REST, Security
> Related: [Architecture](../ARCHITECTURE.md) D23, D26, D33–D35

## Two separate responsibilities

Depot supplies the demonstrated authentication composition: Identity/EF, stock OpenIddict OIDC,
normal cookies, framework token lifecycle and the installation user. TeamCity supplies authorization:
roles, permissions, scopes, groups, inheritance and their REST behavior. Depot's package grants are
not Vivarium's permission model. The [installation design](first-run-administration.md) owns login.

Local users, external-login bindings, eligibility, roles, included-role relations, groups, memberships,
role assignments and framework token state are PostgreSQL application data. All entry points use one
authorized/audited mutation service and current permission evaluator. Git owns versioned desired
configuration, not a second live RBAC authority. This explicitly replaces the old D26/Git-RBAC target.

## Current implementation

The legacy entry still uses SQLite, custom local password verification, panel cookies, Git User
and built-in RoleBinding projections, legacy admin/submit tokens, and a setup/recovery saga. A common
evaluator and audit boundary already exist. General role/group CRUD, full project inheritance and
the D34 Identity/OIDC model do not. Preserve boundary/invariant tests while replacing these mechanisms;
do not describe the accepted target as working functionality.

The fresh R0 Host composes stock `IdentityCore`, Identity authentication cookies and
OpenIddict over PostgreSQL. Its scoped TeamCity roles, permission and included-role relations,
direct grants and All Users contributions are separate application tables; unused framework
global-role tables are removed only when empty. One PostgreSQL statement snapshot reads eligibility,
direct/All Users grants and transitive included-role permissions, avoiding a mixture of old eligibility
and newer grants. Current catalogue metadata and scope checks use one repeatable-read snapshot;
the short activation commit fences concurrent eligibility/grant writes. Current access rereads this data,
without an immutable SYSTEM_ADMIN floor. This is component-level implementation, not completed
authorized role management, nested-group support, REST conformance or story acceptance.

The common R0 service can edit direct role permissions, grant/withdraw global user roles and
enable/disable ordinary accounts with current checks and the same PostgreSQL audit transaction.
Its model tests include delegation, immediate loss of removed administrator permissions and a
failed audit that rolls back a grant. The Access UI has selected real browser evidence; project
delegation remains due. The native current-account read resolves one framework credential and
rereads eligibility, without falling back from an explicit bearer to a browser cookie.

Native My keys issuance wraps framework entry creation/metadata and audit in one EF transaction
before disclosure. Stock authorization-code consumption remains before that transaction, including
on a later failed issuance. Revocation shares its mutation/audit transaction. OpenIddict entity
caching is disabled so rollback cannot leave a committed-looking credential status in that cache.

Initial migration data supplies the five standard TeamCity role definitions captured from 2026.1.3,
including permission IDs and role inclusions, plus the All Users group without assignments. It does
not import the owner's custom roles or infer a default group grant. This is a one-time schema-data
migration: restart never restores removed permissions or recreates a deleted role. The original
embedded migration data remains immutable; future reference changes need a new migration.

The fresh Workbench Access view uses native paged user/role/grant reads and current global user-role
and eligibility mutations. Role-definition reads follow the observed global `view_user_profile` or
`change_user` alternatives; `manage_roles` alone is not a read grant. Own assignment reads remain
available to the eligible subject. Full compatible wire, nested groups and project delegation retain
their due owners. Native My keys has a passing focused real-issuer/browser lifecycle case; this does
not accept the complete IAM family or US-013.

## TeamCity parity contract

Use the owner server (captured TeamCity 2026.1.3), pinned unchanged teamcity-cli and official REST
reference as the behavioral baseline. Documentation currently defaults to newer versions; resolve
version differences against captured responses, not guesses. The required model includes:

- Stable external role IDs and permission IDs, names, global/project applicability and projections.
- Role definitions separate from user/group assignments. Included roles contribute their permissions.
- Editable built-in and custom role definitions, permission add/remove, included-role add/remove,
  create/delete and the reference server's validation behavior. No invented immutable permission floor.
- Global and project assignments, including TeamCity scope strings such as `g` and `p:_Root`.
  Agent Manager assignments must not be reinterpreted as Vivarium-only fleet scope assignments.
- Parent-project inheritance, direct and nested-group membership, inherited grants and effective
  permission queries. Preserve project-specific visibility, ancestor navigation and sibling isolation.
- Project-limited role/group delegation and TeamCity's all-associated-projects checks for shared
  Agent pools. Match actual TeamCity agent permissions, including machine administration where granted.
- Configured All Users/guest policy and simple/per-project authorization mode semantics. Fresh OIDC
  provisioning adds no implicit privilege; effective rights may include explicitly configured group
  policy. Importing users does not silently enable guest access or invent default Admin membership.
- Scoped token behavior and the corresponding user/group/role/permission mutations through both
  native REST and compatible REST. Authentication provider selection remains D34's separate concern.

The read-only census on 2026-10-31 captured seven role definitions: SYSTEM_ADMIN, PROJECT_ADMIN,
PROJECT_DEVELOPER, PROJECT_VIEWER, AGENT_MANAGER, RESTRICTEDBOT and TOOLS_INTEGRATION. Full definitions
and permission/included-role responses are private under artifacts/depot-refactor-study. Import
the actual definitions, not only five hard-coded defaults; classification of plugin/custom roles
and permission applicability is an explicit P0 task.

Primary references:
[Roles and permissions](https://www.jetbrains.com/help/teamcity/managing-roles-and-permissions.html),
[REST roles](https://www.jetbrains.com/help/teamcity/rest/manage-roles-and-permissions.html),
[REST users](https://www.jetbrains.com/help/teamcity/rest/manage-users.html),
[REST groups](https://www.jetbrains.com/help/teamcity/rest/manage-user-groups.html).

## Current authority, not stale credential claims

OIDC proves trusted issuer plus opaque subject and binds it to a local Identity user. Do not merge
by email or treat incoming provider roles as local permissions. Provider-role synchronization, if
later requested, is a separately specified adapter and is not needed to sign in.

Identity/OpenIddict validate credentials. ASP.NET authorization handlers then resolve the current
eligible local user, group memberships, role definitions, assignments and relevant scope hierarchy.
The unchanged browser cookie and unchanged bearer token lose removed rights on subsequent protected
operations. A disabled user is denied. Do not replace this with a positive permission cache, custom
session epoch, token blacklist or role claims baked into the session.

Identity's global role mechanism alone does not implement scoped TeamCity assignments, role
inclusion or nested groups. Use framework users/role stores where suitable and a small domain model
for those missing relations, with explicit EF constraints/transactions and authorization handlers.
Do not hide the domain in custom authentication middleware or build a generic policy framework.

## REST compatibility

Required families include /app/rest/roles and permission/included relations; users/current and user
roles/groups/effective permissions/tokens; userGroups and membership/role relations. Preserve role
IDs, locator/field behavior, scope encodings, JSON/XML shapes, statuses/errors, paging and read-after-write.
The native API and UI use the same services and current checks; there is no compatibility Admin bypass.

Use stock OpenIddict token infrastructure, but specify the actual TeamCity token/profile operations
from client evidence. Depot's package-specific scope, deferred user deletion/linking or external-only
account UX must not silently remove required compatible user/token operations. Required adaptations
stay thin framework/domain adapters, never another credential engine. Do not claim parity from DTO
shape or role count alone.

## Agent identity and Vivarium extensions

Machine credentials remain separate from users, cookies and role assignments. Preserve enrollment
proof, credential generation, session/assignment fences, idempotent delivery, restart recovery and
immutable build provenance. Scheduling requires authorization, enablement, connection, health,
compatibility, no maintenance barrier and an available lease; authentication alone is insufficient.

Normal unauthorization prevents new work while preserving the exact owned build's fenced completion
lane. Drain/disable permits current work to finish. Emergency suspension/revocation fences credentials
and persists cancellation; unresolved work terminates truthfully through bounded lease recovery.
Busy deletion conflicts or waits for quiescence, then revokes/tombstones identity without changing
historical provenance. Restoring an old Git declaration never restores an old credential.

AgentExplorer-only observations/commands can add clearly named permissions without reusing a
TeamCity ID for different behavior. Preserve TeamCity's permissions as copied; extra remote filesystem,
process/environment or provider capabilities are separate, explicitly granted extension actions.
The installation account's enabled maximum authority follows D34, not a new role-fallback mechanism.

## Required evidence

Canonical [authentication](../acceptance/authentication/sign-in.feature) and
[access](../acceptance/access/roles.feature) business AC, supported by S01–S05/S35 and focused
logic/PG/API checks, must cover ordinary login, online Admin
assignment, same-cookie and same-token withdrawal, disabled users, included-role mutation, parent
project and nested-group inheritance, no-grant/sibling isolation, project-limited delegation,
shared-Agent pools, actual source role import, and positive/negative unchanged CLI role/group/user
operations. Use real Identity/OpenIddict credentials and actual PG, not asserted principals alone.

R1 establishes the model and independently tested policy semantics; project/Agent resources exercise
their real scope checks as R2/R3 expose them. R5 closes wire/client conformance. Each milestone has
its own accepted main delivery; no P0 task is complete merely because this contract is written.
