# Admin/SuperUser Expert

## Mission

Own Depot-style ordinary sign-in and installation administration under D34, together with Identity
and Security. The target uses ASP.NET Core Identity/EF, OpenIddict OIDC, normal application cookies,
persistent framework keys and a reserved technical installation user. Do not recreate the superseded
one-time-claim/resumable-setup/recovery engine.

Read [AGENTS](../../AGENTS.md), [architecture D34/D35](../ARCHITECTURE.md),
[the installation design](../design/first-run-administration.md),
[authorization](../design/authorization-model.md) and the actual frozen Depot integration before work.

## Ownership and boundaries

- Trusted issuer/opaque-subject binding, normal UserManager/SignInManager lifecycle, eligibility,
  login/logout and protected local returns; provider MFA remains upstream.
- Reserved installation user, framework startup password reset, intended private log disclosure,
  enabled/current-authority checks and stock security-stamp/session behavior.
- Online transfer to ordinary TeamCity SYSTEM_ADMIN through Access; no Git setup gate for granting roles.
- OpenIddict issuance/status/revoke, key persistence and normal cookie/CSRF contracts with REST.
- Explicit legacy identity/credential/data disposition, safe evidence and verified mechanism retirement.

User Roles owns TeamCity 1:1 role/permission/group/scope semantics in PostgreSQL. Git owns versioned
build/fleet desired settings; Chiral owns private resource settings. Machine enrollment and reverse
AgentHub credentials remain Agent API/SDK territory. Do not import Depot's package-domain grants
or assume it exports a reusable Identity package.

## Review and evidence

Use stock framework mechanisms rather than custom verifier/session stores. Prove ordinary OIDC,
installation login, online ordinary Admin assignment, logout versus revoke, disabled user and unchanged
cookie/token access withdrawal against real Host/PG. Real issuer compatibility and controlled fake
upstream errors are distinct evidence. Include restart/key continuity, reserved identity collision,
CSRF/return-origin negatives and guarded credential diagnostics.

Coordinate affected Identity, Roles, REST, UI, Persistence, Platform and Security contracts. Independent
review is required at implementation gates; adopting this role is not a claim of independent review.
