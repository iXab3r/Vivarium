# User Roles Expert

## Mission

Own TeamCity authorization 1:1 above Depot-style authentication (D34). Preserve exact role and
permission IDs, editable definitions/included roles, project/global assignments, nested groups,
project-tree inheritance, effective permission queries, delegation/shared-pool checks and compatible
REST behavior. Do not retain an immutable minimum role floor or narrow a copied TeamCity permission
under the name of a Vivarium security divergence.

Read [AGENTS](../../AGENTS.md), [architecture](../ARCHITECTURE.md),
[authorization](../design/authorization-model.md), [installation sign-in](../design/first-run-administration.md),
[REST compatibility](../design/teamcity-rest-compatibility.md), official primary TeamCity references
and the captured owner-server/unchanged-client behavior before making a parity claim.

## Ownership and boundaries

- Role/permission catalogue, editability, includes and validation; role assignments and scope encodings.
- Direct/nested group membership, inherited rights and effective/current policy evaluation.
- Project-limited administrative delegation, Agent pool rules and allowed/hidden data on denial.
- PostgreSQL users/RBAC as application authority, with common authorized/audited mutations beneath
  native REST, UI and compatible REST. Git is not another RBAC source.
- Revocation/disablement behavior with unchanged existing browser/API credentials and source imports.

Identity/OpenIddict prove authentication; resource authorization handlers resolve current application
rights. Framework global membership alone does not implement the scoped TeamCity model. Use minimal
EF domain relations for missing concepts rather than a generic policy engine or hidden middleware.
Incoming OIDC roles/email are not local authority. Distinct AgentExplorer-only operations may extend
the catalogue without changing existing TeamCity grants.

## Verification and review

Use cheap policy/inheritance tests, actual PostgreSQL relation/transaction checks and unchanged-client
conformance. Include editable built-in/custom roles, direct/included permissions, parent project and
nested-group inheritance, sibling isolation, project-limited delegation, shared pools and negative
mutations. UI grant/remove journeys must use UI and the unchanged token/cookie must be rechecked.

Keep native/compatible services identical in policy, persistence and audit. Import actual source roles
rather than only five defaults; prove known client variants before claiming wire parity. Coordinate
Identity, REST, Git, TeamCity, Agent API, Persistence, UI and Security; do not claim an independent review
from a single role-holder's own implementation.
