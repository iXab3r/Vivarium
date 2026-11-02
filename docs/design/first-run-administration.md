# Depot-style sign-in and installation administration

> Status: **Accepted — D34, 2026-10-31**
> Implementation: **Legacy setup/login exists; the new model is not implemented**
> Maintainers: Identity, Admin/SuperUser, Security, Platform
> Related: [Authorization](authorization-model.md), [UI](ui.md), [Architecture](../ARCHITECTURE.md) D34/D35

## Accepted replacement

Reuse Depot's demonstrated ASP.NET Core Identity/EF and OpenIddict composition, normal sign-in
pages, persistent framework keys and technical installation user. Do not depend on the Depot service
or create a shared Identity package without a demonstrated reuse boundary.

This supersedes the one-time claim/resumable setup/recovery saga as the target. Initializing Chiral
resource settings and Git configuration is a separate operator concern; granting an ordinary user
Admin does not require stopping Host/PG or initializing a Git authorization repository.

## Current implementation

Current source still has custom SQLite password verification, a named panel login/cookie, Git User
and SYSTEM_ADMIN binding activation, one-time setup claims and durable setup sessions. Explicit
host-local recovery issues a bounded recovery session; normal claimed-instance restarts do not
generate a standing superuser token. Legacy admin/submit token files remain accepted adapters.

These mechanisms and tests are implementation evidence, not the new target. R1 replaces the custom
authentication/setup paths after the P0 existing-data/consumer disposition and real scenario proof.
Preserve actor attribution, audit, revocation and recovery guarantees; do not delete operational
identities or credentials by assumption.

## Ordinary OIDC sign-in

Use stock OpenIddict.Client authorization-code flow with PKCE, trusted issuer/signature/audience,
nonce/correlation and validated local return navigation. The provider owns credential verification
and MFA. Map trusted issuer plus opaque subject to a local Identity user through its external-login
store; never merge by email, assume EyeAuras-specific GUIDs or import provider Admin claims.

A first valid login provisions one unprivileged local user, respecting the current enabled policy
and configured TeamCity group/authorization mode. Repeated/concurrent login resolves the same
binding; a disabled binding cannot reprovision itself or reactivate the account. Use UserManager/
SignInManager and normal eligibility extension points, not a naked custom cookie/token generator.

Ordinary browser sign-in is OIDC-first; the installation account uses Identity password sign-in.
Required TeamCity user/account REST operations are separately inventoried and implemented using
framework mechanisms; Depot's deferred account deletion/linking is not a blanket Vivarium restriction.

## Installation superuser

Use one reserved technical Identity user in PostgreSQL and the normal login form/application cookie.
Ordinary OIDC provisioning cannot claim its reserved login or link an external identity to it.

When installation access is enabled, each Host startup creates a fresh random password through
UserManager's normal create/reset flow before accepting sign-ins. Persist only the framework password
hash/security stamp. After successful persistence disclose the new value only in the intended
private server-log entry. There is no second raw-token verifier, special recovery API or database-
independent authentication fallback. Failed initialization must not silently admit the old password.

The Identity Chiral node owns Superuser.Enabled with normal apply/restart semantics. Disabled mode
grants no authority, including for an existing cookie. Re-enabling regenerates the credential at startup.
The technical user has maximum administration, while ordinary administrators use TeamCity roles.
Use this account to grant an already-provisioned ordinary OIDC user SYSTEM_ADMIN through the normal
Access UI/service, without a custom first-admin saga or provider role mapping.

Startup password reset updates Identity's security stamp. Follow the stock cookie validator/profile:
this does not promise instantaneous invalidation of every previously issued browser cookie.
Current account/superuser-enabled/permission checks are separate and apply to protected requests.
Do not build a custom epoch or rotate shared cookie keys to simulate immediate stamp retirement.

## Sessions, API credentials and keys

Use one normal Identity application cookie and framework CSRF protections for browser mutations;
common styled login/logout and My keys navigation live on the Host origin. Port Depot's reviewed
cookie/Remember me behavior through Identity-node options; do not copy its application name,
issuer, audiences, fixed account ID or production key material.

OpenIddict owns finite token issuance/validation/status/revocation using EF stores. The native UI/API
and compatible TeamCity token endpoints resolve the same local user/current permissions. Logout
clears the local browser session; it does not automatically revoke API tokens or end the upstream
provider session. Explicit revoke must deny the original token.

Persist Data Protection keys and OpenIddict credentials in owned private durable storage, separately
from public blobs. Ordinary restarts preserve ordinary session/token continuity within framework
validity rules; losing keys is not repaired by silently generating another identity. Include keys,
private Chiral settings and PG in the recovery contract. No secret is placed in Git, a URL or a
browser diagnostic artifact. Only the intended startup superuser disclosure is a log exception.

## Configuration and operator recovery

Chiral owns Host/Database/Identity settings, schemas, masked secret replacement, CAS and saved/active
restart state. Operator-local initialization/repair uses the same modules and settings store.
A stopped-Host local settings repair concerns exclusive file-store access, not online role assignment.
A broken PG is an operational dependency failure, not justification for a parallel superuser store.

Versioned build/fleet desired configuration uses Git. Identity/RBAC state uses PG and current
authorized/audited services. The existing Git User/RoleBinding projections require explicit
migration/disposition and retirement, never permanent dual authority.

## Acceptance

Use real Host/PG/Identity credentials, the actual local issuer compatibility fixture and controlled
fake upstream failures. EyeAuras.Automation/Reqnroll/Playwright exercises independent ordinary OIDC,
installation-to-ordinary-admin, My keys, logout/revoke, same-cookie/token rights removal, disabled
user, settings CAS and restart/key continuity journeys. Negative checks cover issuer/subject
confusion, reserved-login collision, unsafe return URL and CSRF.

Root build/test, affected PG/protocol checks and exact-revision UI/security/simplicity review gate R1.
Legacy authentication/setup code is removed with the verified replacement; existing machine
enrollment and AgentHub credentials are not replaced by human OIDC.
