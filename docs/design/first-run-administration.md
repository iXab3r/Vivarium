# First-run administration and super-user recovery

> Status: **Accepted**
> Implementation: **Planned**
> Maintainer role: [Admin/SuperUser Expert](../roles/admin-superuser-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D4, D23, D24, D26, D27

This design specializes the first-run and recovery model adopted in D26. Numbered architecture
decisions remain authoritative.

## Purpose

Vivarium needs a secure answer to three different questions that are easy to conflate:

1. Who may claim a controller with no users?
2. How does that person become a durable administrator governed by ordinary RBAC?
3. How does an operator recover administration if every durable administrator is unavailable?

The answer borrows TeamCity's useful operational premise: someone who can read a deliberately
protected token from the server host can recover the server. It does not borrow an unsafe
first-browser-wins setup or keep a standing token in normal request paths. Initial ownership is a
one-time ceremony; emergency super-user access is an explicit, short-lived recovery ceremony.

All settings produced by the ceremony are committed through Git. Authentication secrets and runtime
security state are not settings and never enter Git. Every action is written to an audit log.

## Current state

The Phase 1 implementation is a useful prototype, not the target administration model:

| Area | Current evidence | Limitation |
|---|---|---|
| Controller credentials | `TokenStore` creates persistent `admin.token` and `submit.token` files; private modes are applied and tested on Unix. | The plaintext admin token is the durable administrator identity and never expires; Windows currently relies on inherited ACLs rather than explicit hardening. |
| Startup output | `Program.cs` prints the full admin, submit, and newly created enroll tokens on every start. | Ordinary process logs expose long-lived credentials and do not distinguish initial claim from recovery. |
| Panel login | `POST /login` compares the static admin token and creates a 12-hour sliding, secure, HTTP-only, SameSite=Strict cookie. | There is no user record, role membership, credential rotation, bootstrap state, or recovery generation. |
| Control plane | The gRPC management plane resolves `Admin`, `Submit`, and `Agent` bearer scopes. | The admin bearer is not attributable to a durable user and is valid against all admin operations. |
| CLI | `viv login` stores a supplied submit/admin token together with pinned controller trust. | It cannot represent a user session, personal access token, or restricted setup principal. |
| Persistence | SQLite stores agents, builds, enrollment tokens, and related durable state. | It has no durable user/RBAC/bootstrap/audit model. |
| Git | `vivarium.yaml` is versioned in the tested repository per D17. | Controller settings and the first-run baseline are not yet backed by a controller configuration repository. |
| REST | Authenticated blob HTTP endpoints exist; management is gRPC and the panel calls in-process services. | There is no versioned REST administration or setup surface. |
| Audit | Normal application/build logging exists. | Admin login, configuration mutation, and token lifecycle do not have a dedicated safe audit trail. |

Relevant executable evidence includes `SecretStorageTests`, `PanelTests`, `CliTests`, and
`ControlPlaneTests`. These tests should be preserved as migration baselines and expanded rather than
silently invalidated.

## TeamCity evidence and deliberate differences

Current TeamCity On-Premises behavior provides four useful reference points:

- A fresh TeamCity data set has no users; its ordinary quick-start UI asks the first visitor to create
  the administrator account. See [TeamCity First Start](https://www.jetbrains.com/help/teamcity/quick-setup-guide.html#TeamCity+First+Start).
- TeamCity generates a new super-user token on every server start, writes it to the console and
  `teamcity-server.log`, and accepts it through a special login. See
  [Super User Access](https://www.jetbrains.com/help/teamcity/super-user.html).
- The super user can also authenticate to TeamCity's REST API. See
  [REST API Quick Start](https://www.jetbrains.com/help/teamcity/rest/quick-start.html#Authentication).
- JetBrains warns that anyone who can read TeamCity server logs can elevate to administrator and
  recommends disabling super-user access when logs are exported. It also documents a startup setting
  that prevents an empty database from granting administrator creation to the first visitor. See
  [Security Notes](https://www.jetbrains.com/help/teamcity/security-notes.html#Protect+Data+from+Exposure)
  and [Configure Server Installation](https://www.jetbrains.com/help/teamcity/configure-server-installation.html).

Vivarium adopts local-host proof, log discoverability for a protected local installation, and an
emergency recovery mode. It deliberately differs in these respects:

- no unauthenticated visitor may create the first administrator;
- normal restarts of a claimed instance do not generate a standing super-user credential;
- bootstrap and recovery tokens are single-use exchanges with bounded sessions;
- the bootstrap principal cannot call ordinary TeamCity, AgentExplorer, or administration APIs;
- a token value is never reprinted because a remote browser requested the login page;
- token-file delivery replaces console/log delivery where logs are forwarded or broadly readable.

## Target state

An unclaimed controller exposes a read-only health/setup status plus a token-protected setup API. A
successful token exchange immediately consumes the presented value, creates one short setup session,
and durably creates one resumable setup operation. The operation—not the browser session—owns the
pending identity, Git candidate, review state, reconciliation, and activation. Session expiry,
browser loss, controller restart, or a remote pull request waiting for review cannot strand it. Only a
locally authorized resume, reconcile, or abandon command may resolve an otherwise unattended attempt.

First release defaults to a controller-managed local configuration repository with direct commits.
That path preserves the no-external-service goal and lets ordinary setup complete synchronously. A
remote repository or review workflow is used only when the operator explicitly configures it and
supplies private repository credentials plus authenticated host trust.

An active controller has no standing super-user token. A host operator can explicitly create a
single-use recovery generation through a local command. In TeamCity terms, the resulting ephemeral
Superuser has System Administrator-equivalent authorization, but it still cannot bypass Git-backed
desired state, audit, secret redaction, or concurrency rules. Its intended use is to repair or create
a durable administrator and hand control back promptly. Initial recommended limits are 24 hours for
an unclaimed bootstrap generation, 30 minutes for a setup session, 15 minutes for a recovery token,
and 30 minutes for a recovery session. These are policy defaults for security and usability review,
not wire-contract constants.

## State model

The controller persists an administration-bootstrap record independent of process lifetime:

```text
UNCLAIMED -> SETUP_IN_PROGRESS
SETUP_IN_PROGRESS -> SETUP_ACTIVATING -> ACTIVE
SETUP_IN_PROGRESS -> SETUP_WAITING_FOR_GIT -> SETUP_ACTIVATING

ACTIVE
  -> RECOVERY_AVAILABLE
  -> RECOVERY_IN_PROGRESS
  -> ACTIVE
```

`UNCLAIMED` means there is no durable System Administrator and no completed initial configuration
baseline. `SETUP_IN_PROGRESS` means one durable operation owns the claim and may be edited through a
valid setup session. `SETUP_WAITING_FOR_GIT` means its exact candidate is committed but is waiting for
the configured authoritative ref—for example a remote review—to accept it. `SETUP_ACTIVATING` means
the authoritative commit is accepted and reconciliation must finish identity/RBAC activation; this
state completes without a browser. `ACTIVE` means a durable administrator and committed
configuration baseline both exist. `RECOVERY_AVAILABLE` is an explicitly, locally enabled overlay on
`ACTIVE`; it does not reopen initial setup. `RECOVERY_IN_PROGRESS` means the one-time recovery value
was exchanged for a bounded, explicitly identified Superuser session.

The persisted record contains no plaintext token. It contains:

- instance ID;
- state and state version;
- current bootstrap or recovery generation ID;
- token hash using a purpose-specific keyed/password hashing construction;
- issued, expires, consumed, and revoked timestamps;
- setup correlation/idempotency ID;
- setup operation status, created/updated timestamps, and last durable checkpoint;
- pending durable user ID, if any;
- selected repository mode, identity, authoritative ref, and expected base;
- candidate tree hash, pending/resulting Git commit ID, change branch/review locator, if any;
- setup-session generation and expiry, without the session value;
- recovery mode and reason;
- last transition outcome.

All transitions go through the serialized SQLite writer. Token comparison is constant-time. A token
generation is never reused for a different purpose.

### Durable setup ownership and local control

Exchanging the bootstrap token atomically creates the operation before returning a setup session. A
request/response loss may leave a pending operation without a usable browser session, but never an
ambiguous unclaimed controller. The operation continues reconciliation and remote-ref observation in
the background; credential expiry only removes the caller's access.

The controller exposes local host commands with these semantic contracts. Exact spelling may change,
but REST cannot perform these authority-establishing actions:

- `setup status`: returns the non-secret operation ID, phase, repository/ref, candidate/review locator,
  and last error so a host operator can recover after losing every browser/client record.
- `setup token rotate`: valid only in `UNCLAIMED`. It immediately revokes the previous bootstrap
  generation, issues a new one through the configured private sink, and requires no restart.
- `setup access reissue <operation-id>`: valid only for the one non-terminal setup operation. It
  invalidates previous setup sessions, issues a single-use resume token bound to the same operation,
  delivers it through the explicitly configured private sink, and changes none of its proposed
  identity or Git state. Exchanging that token creates a fresh setup session.
- `setup recover <operation-id>`: re-runs deterministic Git/ref and SQLite reconciliation. If the
  accepted authoritative commit matches the recorded candidate, it completes activation without
  recreating the user or commit. It cannot force-apply an invalid or unaccepted revision and emits no
  new credential unless the operator separately requests access reissue.
- `setup abandon <operation-id> --reason ...`: revokes all setup credentials and tombstones the
  operation only while its candidate has not reached the authoritative ref. Pending credentials/user
  records are deleted or permanently disabled; an orphan local commit or remote review branch is
  recorded but not rewritten or hidden. The controller returns to `UNCLAIMED` and issues a new
  generation through the configured sink. If the candidate is already authoritative, abandon is
  rejected and reconciliation must finish or a later Superuser must create a normal Git revert.

An abandoned operation ID and its commit/change locator remain audit tombstones. If its remote change
is merged later, the reconciler refuses to activate it automatically and reports a blocked,
operator-visible head; a new locally authenticated setup operation must explicitly adopt or replace
that revision. This prevents a delayed pull request from resurrecting an abandoned administrator.
These pre-activation `setup recover` semantics are distinct from the active-instance Superuser
recovery ceremony later in this document; neither credential is accepted by the other claim path.

## Secret types must remain separate

| Secret | Purpose | Lifetime | Valid surface | Storage |
|---|---|---|---|---|
| Bootstrap token | Claim an unclaimed controller | One generation, single successful exchange, bounded expiry | Setup claim only | Hash in SQLite; value delivered locally once |
| Setup session | Finish the active first-run workflow | Short and bounded | Setup resources only | Protected cookie or hashed opaque bearer |
| Recovery token | Enter explicitly enabled recovery | One generation, single successful exchange, bounded expiry | Recovery claim only | Hash in SQLite; value delivered locally once |
| Recovery session | TeamCity-compatible break-glass administration | Short and bounded | Normal APIs as the explicit Superuser principal | Protected cookie or hashed opaque bearer |
| User password/external credential | Authenticate a durable user | Until changed/revoked | Normal login | Password hash or provider binding in security state |
| Personal access token | Automate as a durable user | Explicit expiry and permission restrictions | Normal REST/CLI under RBAC | Hash in security state; value shown once |
| Agent enrollment token | Admit one machine for later authorization | Short/single-agent per D7/D19 | Agent setup/session only | Existing separate enrollment store |

A token parser must reject using one secret type in another authentication scheme even if its random
format happens to match.

## Startup behavior

### Unclaimed interactive installation

On start, the controller determines state before mapping normal mutation endpoints.

1. In `UNCLAIMED`, if no valid bootstrap generation exists, generate at least 256 random bits,
   persist only its hash, and assign a generation ID and expiry. Pending setup states never generate a
   new unbound bootstrap token; they resume their recorded operation instead.
2. Write the value once to the local console and a dedicated private startup log. The log entry is
   intentionally easy to search and names its expiry, for example:

   ```text
   VIVARIUM FIRST-RUN TOKEN [generation 01J...; expires 2026-08-28T14:00:00Z]: <secret>
   Open https://127.0.0.1:8443/setup and paste this token. Do not send it in a URL.
   ```

3. Ordinary structured logging receives only the generation ID, expiry, delivery channel, and a
   short non-authenticating fingerprint. It never receives the value.
4. The startup log and any output file use the same private-directory/file permission helpers as
   controller keys. On Unix this means owner-only access; on Windows only the service identity and
   explicitly selected local administrators may read it.
5. A restart while still unclaimed rotates the generation and revokes the previous value. It does
   not produce multiple valid tokens. Restarting a pending setup operation preserves its operation,
   Git/review state, and still-valid setup session.
6. In `private-log` and `token-file` modes, a background expiry transition rotates an unclaimed token
   and delivers its replacement without a restart. The local `setup token rotate` command can do this
   at any time. In `provided-file` mode, expiry fails closed with no valid generation until the local
   operator supplies a replacement file and requests rotation; the controller never falls back to
   another output channel.

The token grants no access until it is presented to the setup claim operation over authenticated
HTTPS. Merely loading `/setup` returns public instructions and non-sensitive state.

### Pending setup restart

On startup in any `SETUP_*` state, the controller loads the one durable operation, resumes Git/ref
observation and reconciliation, and emits only its non-secret operation ID and phase. It neither
returns to `UNCLAIMED` nor emits an unbound token because the previous setup session expired. The host
operator uses `setup status`, `setup access reissue`, `setup recover`, or the narrowly permitted
`setup abandon` flow described above.

### Active installation

An `ACTIVE` controller starts without printing or enabling any super-user credential. Startup logs
identify the instance and normal login URL, never user or personal access tokens. If no enabled System
Administrator exists, the controller reports a high-severity local diagnostic but does not reopen
first-run setup automatically.

### Log delivery modes

The controller supports explicit delivery policy:

| Mode | Intended deployment | Behavior |
|---|---|---|
| `private-log` | Portable/local service with private data directory | Value goes to private startup log and local console. |
| `token-file` | Container, orchestrator, or centralized logging | Value is atomically written to an explicitly mounted private output file; stdout logs only metadata. |
| `provided-file` | Fully automated sealed deployment | Controller reads an operator-provided secret file once, stores only its hash, and never echoes the value. |

There is no command-line argument carrying the value and no recommended environment-variable form:
both are commonly exposed by process inspection, diagnostics, shell history, or deployment metadata.
The output path must be explicit and must not resolve inside the Git working tree.

## Interactive first-run experience

The Workbench-based UI is only a client of the versioned REST setup contract.

1. **Prove local ownership.** The login page asks for the first-run token. It never puts the token in a
   query string, fragment, referrer-bearing navigation, browser storage, or application log.
2. **Create the durable administrator.** The user selects a login/display name and a supported local
   credential or external identity binding. The server reserves an immutable user ID. Password policy
   and role semantics belong to User Roles Expert.
3. **Initialize or connect the control repository.** The wizard offers:
   - by default, initialize a local controller-owned Git repository and commit directly to its
     authoritative branch; or
   - when explicitly selected, connect an existing repository/branch, validate its format and
     ownership expectations, and propose the initial or adoption commit.
4. **Review the baseline.** The UI shows the non-secret files/diff that will be committed, target
   branch, author identity, and whether a remote push is required. Secrets are represented only by
   references.
5. **Commit and activate.** The server commits the baseline through the Git/Versioning mutation
   protocol and durably links the commit ID to the setup operation. Managed-local direct mode normally
   activates immediately. A remote review may leave the operation in `SETUP_WAITING_FOR_GIT`; the UI
   may close safely and later resume, while background reconciliation completes after acceptance.
   Activation assigns the normal System Administrator role, closes all setup sessions, and allows the
   durable user to sign in normally. The bootstrap token was already consumed at claim.
6. **Show recovery instructions.** The success page explains how a host operator can explicitly issue
   a recovery token and warns that ordinary restarts will not print one.

The setup session cannot list agents, inspect builds, execute commands, issue enrollment tokens,
download blobs, create personal access tokens, or edit arbitrary files. It can operate only on the
pending identity, the initial control-repository configuration, and setup completion.

## Git baseline and ownership boundary

The first-run ceremony establishes two different durable stores:

### Git-managed desired state

The initial commit contains, at minimum, schema/version metadata and the non-secret desired state
needed to run the controller. The exact paths belong to Git/Versioning Expert, but the baseline must
be capable of representing:

- instance-visible server settings;
- normal authentication provider settings without secrets;
- stable user/group/role references required by the initial System Administrator assignment;
- project and fleet configuration roots as they become supported;
- secret references, never values.

Every later UI or REST settings change goes through the same propose/validate/commit/reconcile flow.
The bootstrap wizard is not an exception to Git-backed mutation; it creates the first commit.

### First-release repository modes

**Managed-local direct** is the first-release default. The controller initializes a private non-bare
repository beneath its data directory, creates the schema manifest and baseline candidate, commits to
the locally authoritative branch, and reconciles that commit. A remote is optional and can be added
later through an ordinary Git-backed administrative change; adding one does not silently change
authority.

**Explicit remote adoption** requires all of the following before any fetch, push, or review request:

- a repository URL without userinfo, embedded password/token, or credential-bearing query values;
- an explicit authoritative branch and direct-push or review workflow;
- a controller-local credential reference backed by a private file/mounted secret or supported OS
  credential store; the setup API receives the reference, never the resolved value;
- authenticated host trust: normal system CA validation or an explicit CA/SPKI pin for HTTPS, or a
  private `known_hosts` entry/pinned host-key fingerprint for SSH;
- private permissions, no symlink/reparse escape, and an explicit separation from the Git working
  tree for every credential and trust file.

First release must not put credentials in Git remote URLs/config, use `GIT_SSL_NO_VERIFY`, disable SSH
host-key checking, accept trust-on-first-use from a remote response, inherit ambient interactive Git
credential helpers, or log a sanitized-looking URL without first removing userinfo and query data.
For initial remote setup, the host operator installs the credential and host-trust material locally
or mounts it into the container, then selects their stable references in the wizard/REST request.

Remote direct mode remains pending until a fast-forward push succeeds and the authoritative ref is
fetched back. Remote review mode remains `SETUP_WAITING_FOR_GIT` until the accepted commit reaches and
is fetched from the authoritative branch. Review-session expiry and browser closure do not cancel the
change request or the setup operation.

### Runtime security and operational state

SQLite/private storage contains data that must not be committed:

- password hashes and external-provider subject bindings;
- bootstrap/recovery/PAT/session token hashes and revocation state;
- Git credentials or references to their external secret source;
- cookie protection keys;
- login failures, lockouts, sessions, and one-time claims;
- the append-only audit stream and reconciliation checkpoints.

The durable user ID may be referenced by Git configuration, but its credential material may not.
Changing a password or revoking a session is a security operation: it is audited but does not create a
Git commit. Changing that user's role or a server setting is desired-configuration mutation and does.

### Crash consistency

SQLite and Git cannot share one atomic transaction. The durable setup operation therefore runs an
idempotent saga:

1. consume the bootstrap value, create the setup operation, and reserve its user ID in
   `SETUP_IN_PROGRESS`;
2. prepare and validate the exact baseline tree;
3. create exactly one candidate commit containing the operation/correlation ID in machine-readable
   metadata;
4. advance the managed-local ref directly, push in explicit remote-direct mode, or record the remote
   review locator and enter `SETUP_WAITING_FOR_GIT`;
5. observe the candidate on the configured authoritative ref and enter `SETUP_ACTIVATING`;
6. record the accepted commit ID, activate the identity/role binding, and close all setup credentials
   in one SQLite transaction. The durable administrator then uses normal login; activation does not
   depend on returning a browser response.

After a crash, reconciliation searches for the operation/correlation ID and verifies the recorded tree
hash, repository, and authoritative ref. If the accepted commit exists, it completes activation; if
only the candidate/review exists, it resumes observation; if neither exists, it returns to editable
`SETUP_IN_PROGRESS`. It never creates a second user or semantically duplicate commit. Expired setup
sessions do not alter these decisions. The Git/Versioning Expert and Reconciliation Lead own the final
repository-specific mechanics.

## REST bootstrap contract

Endpoint names below reserve the resource boundary; the Vivarium REST Expert owns final naming,
media types, error envelopes, and compatibility rules.

| Operation | Authentication | Result |
|---|---|---|
| `GET /api/v1/setup/status` | None | Minimal state: unclaimed/pending/active, setup API version, token delivery hint; no identities, paths, or secrets. |
| `POST /api/v1/setup/claims` | Bootstrap or operation-bound reissue token in a dedicated authorization scheme or body field | Atomically consumes the value, creates/resumes the durable operation, and returns a short setup session. |
| `GET /api/v1/setup/operations/{operationId}` | Setup session bound to that operation | Returns durable phase, redacted Git/change status, last error, and permissible next steps. |
| `PUT /api/v1/setup/administrator` | Setup session | Idempotently reserves the durable identity and credential binding. |
| `PUT /api/v1/setup/config-repository` | Setup session | Validates local initialization or remote connection using secret references. |
| `GET /api/v1/setup/changes` | Setup session | Returns the redacted proposed initial Git diff and validation result. |
| `POST /api/v1/setup/completion` | Setup session + idempotency key | Starts or resumes commit/reconciliation and returns the durable operation; activation may remain pending on remote review. |

Requirements:

- setup routes exist only in `UNCLAIMED` and the recorded `SETUP_*` states; after activation they
  return a stable non-disclosing terminal response and cannot be re-enabled over REST;
- all requests use the controller's authenticated HTTPS endpoint; self-signed deployments rely on the
  independently observed/pinned fingerprint already required by D4;
- claim failure responses do not reveal whether the generation ID, token, or expiry was wrong;
- claim attempts are rate-limited by instance and source, while local audit records retain enough data
  for investigation without recording credentials;
- state-changing operations require an idempotency key and optimistic state version;
- setup cookies are secure, HTTP-only, SameSite=Strict, narrowly pathed, short-lived, and invalidated
  when the token generation rotates or setup completes;
- setup-session expiry or loss never cancels, abandons, or rolls back the durable operation; only a
  local resume command can issue another operation-bound token and session;
- non-browser automation receives an equivalently scoped opaque setup credential; it never reuses the
  original bootstrap token for every request;
- normal bearer/PAT, submit, agent, and enrollment credentials are rejected by setup routes;
- the bootstrap/setup credential is rejected by every normal API route;
- request bodies containing passwords or Git credentials are globally marked sensitive and excluded
  from request tracing, error echoes, diagnostics, and audit diffs.

## Noninteractive and container deployment

Automation performs the same state transitions through REST:

1. Mount an input bootstrap-token file or a private output-token file and Git credential references.
2. Start the controller with stdout secret emission disabled.
3. Wait for the non-sensitive setup status/readiness response.
4. Exchange the bootstrap token once for a setup credential.
5. Reserve the initial administrator or bind a pre-established external subject.
6. Configure/validate the Git repository and inspect the proposed redacted baseline.
7. Complete with an idempotency key, persist the returned operation ID, and poll its durable state. A
   remote review can outlive the automation process and setup credential; background reconciliation
   still activates an accepted commit.
8. If automation loses its setup session before completion, a host-local operator reissues access to
   that operation; automation never creates a second claim to work around the loss.
9. After activation, record the resulting Git commit ID and discard every setup secret.
10. Create a named, expiring personal access token under the durable administrator only when automation
   genuinely needs ongoing access; do not keep using the setup credential.

The controller must fail closed when an explicitly configured token input/output file is missing,
world-readable, symlinked/reparse-pointed outside its allowed private directory, or located inside the
control repository. It must not silently fall back to logging a token.

For immutable deployments, pre-seeding the Git repository is allowed. Pre-seeding an already-active
administrator database image is not the normal path because it duplicates instance identity,
credentials, data-protection keys, and audit history.

## Explicit recovery

Recovery starts from operating-system authority over the controller, not from an anonymous HTTP
request.

The distinction is strict: the unexchanged recovery token is accepted only by the recovery-claim
endpoint and is rejected by every normal API. A successful exchange consumes that token and issues a
different bounded recovery-session credential. Only that session authenticates normal APIs, where it
resolves to the explicit `superuser` principal and every authorization, audit, Git, lease, fencing,
and concurrency rule still applies.

1. A local controller administration command requests recovery and supplies a human reason and output
   mode. The exact command name is owned by the CLI/operations surface.
2. The controller creates a new recovery generation, revokes any previous recovery generation and
   sessions, stores only the token hash, and writes the value through the selected private channel.
3. The operator visits a dedicated recovery URL and exchanges the value once. A normal login form must
   not ambiguously treat an empty username as super-user mode. The exchange consumes the value and
   enters `RECOVERY_IN_PROGRESS` with a bounded recovery session.
4. The recovery principal is an explicit `superuser` actor with System Administrator-equivalent
   authorization. Its normal path is to create/enable a durable administrator, replace a local
   credential, repair an external identity binding, restore System Administrator membership through
   the Git mutation protocol, or repair the control-repository connection.
5. Completion/revocation closes all recovery sessions. The operator then logs in as a durable user.

As in TeamCity, Superuser authorization is equivalent to System Administrator, but it is not a normal
user and cannot create authority outside that role. Every use is labeled `superuser` in UI/REST and
audit, the session cannot outlive its recovery generation, and durable settings still flow through
Git. The UI should foreground recovery tasks and require confirmation for unrelated high-risk actions
without inventing a second permission model.

Recovery of a Git-backed role assignment follows the same commit/reconcile protocol. Recovery of a
password hash or provider binding is audit-only because secrets and credential bindings do not belong
in Git. If the control repository is unavailable, recovery may restore connectivity or create a
clearly marked local emergency branch, but may not silently mutate desired state only in SQLite.

Token rotation rules:

- in `UNCLAIMED`, expiry or the local rotate command issues a replacement without restart in an output
  mode that can deliver it; only the newest generation remains valid;
- issuing a replacement bootstrap/recovery generation immediately revokes the previous generation
  and every session derived from it;
- a setup-access reissue is bound to the existing operation, revokes earlier setup sessions, and
  cannot start another operation or change Git state by itself;
- successful setup closes the already consumed bootstrap generation and invalidates its setup session;
- successful recovery exchange consumes the token while leaving only its bounded recovery session;
- a local revoke operation terminates the generation and all derived sessions without replacement;
- changing a durable administrator credential does not revive or rotate recovery automatically;
- controller restart does not enable recovery for an active instance.

## Audit trail

Logs are sufficient for the first implementation, provided there is a dedicated structured audit
category with bounded rotation and no secret values. Each event records:

- timestamp and monotonic sequence;
- event name and schema version;
- instance ID and setup/recovery generation ID;
- correlation, request, and idempotency IDs;
- actor kind (`local-operator`, `setup-principal`, `superuser`, or durable user ID);
- authentication method, never credential value;
- source address/user agent where applicable;
- target stable IDs;
- redacted before/after summary;
- Git repository identity and commit ID for desired-state changes;
- outcome, stable failure code, and duration.

Required event families include:

- `administration.bootstrap-issued|rotated|revoked|claim-failed|claim-succeeded`;
- `administration.identity-reserved|activated|failed`;
- `administration.git-validated|baseline-committed|review-waiting|reconciled|failed`;
- `administration.setup-access-reissued|resumed|recover-requested|completed|abandoned|blocked`;
- `administration.recovery-requested|issued|claim-failed|claim-succeeded|completed|revoked`;
- `authentication.login-succeeded|login-failed|credential-changed|session-revoked`;
- `authorization.role-change-proposed|committed|reconciled|failed`.

Never log token/password/cookie/PAT values, Authorization headers, request bodies, Git credential URLs,
private-key paths that reveal secret layout, or unredacted configuration diffs. Failed claim events
must be sampled or aggregated after a bounded threshold so an attack cannot inflate logs without
limit; the Logs Expert owns final rate and retention policy.

The one exceptional line containing the initial/recovery token is written only to the explicitly
selected privileged delivery sink. It is not an audit event and must not be copied by the structured
logger.

## Security invariants

1. Network reachability alone never establishes the first administrator.
2. The first-run/recovery token is high entropy, purpose-bound, generation-bound, expiring, and
   compared in constant time.
3. The full value appears at most once per configured private delivery sink and never in URLs.
4. No normal endpoint accepts a bootstrap token, setup session, or unexchanged recovery token. An
   exchanged recovery session is accepted only as the explicit audited Superuser principal.
5. No setup-claim or recovery-claim endpoint accepts normal agent, submit, CI, user, or PAT
   credentials in place of its purpose-bound token.
6. No administrator identity becomes active before the initial desired-state commit is durable under
   the configured repository policy.
7. Consuming a bootstrap/recovery token prevents another exchange but leaves only the newly issued
   bounded session. Rotating or revoking a generation, setup completion/abandonment, or recovery
   completion/revocation invalidates every relevant derived session.
8. User credentials, tokens, cookies, and Git secrets never enter Git.
9. Desired settings and role assignments never mutate only in SQLite.
10. Every privileged transition has a redacted audit event and, where applicable, a Git commit ID.
11. A claimed instance never returns to unclaimed state because a database query found no enabled
    administrators. Recovery is explicit.
12. Nothing in this design changes `Vivarium.Bootstrap` or its frozen agent-launcher contract.

## Non-goals

- Defining the complete TeamCity-compatible permission catalog and role inheritance.
- Choosing every local/external authentication provider.
- Designing a general-purpose secret manager.
- Defining the complete control-repository schema or Git merge/review policy.
- Defining all REST resources outside setup and recovery.
- Agent enrollment, agent authorization, or agent software installation.
- Controller license acceptance, database selection, or an extensible installer wizard.
- Allowing a remote support service, vendor, or Vivarium maintainer to recover an operator's server.

## Acceptance evidence

Before declaring the target implemented:

- Tier 1 exhaustively covers legal/illegal state transitions, token generation rotation, expiry,
  single-use exchange, session revocation, durable-operation resume/abandon rules, delayed-review
  acceptance, and crash-reconciliation decisions under virtual time.
- Tier 2 starts real HTTPS controllers and proves interactive and REST setup, duplicate completion,
  browser/session loss, host-local resume/reissue, restart at every saga boundary, online unclaimed
  token rotation, token/session invalidation, recovery token-versus-session isolation, normal-route
  Superuser use, secure cookies, rate limiting, redaction, and handoff to RBAC.
- Filesystem tests prove private token/log permissions and symlink/reparse-point rejection on Windows,
  Linux, and macOS.
- Git integration tests prove exactly one baseline commit, stable correlation metadata, remote-policy
  failure behavior, managed-local direct activation, remote host/credential validation, long-lived
  review waiting, late merge after abandonment, and recovery after a commit-before-SQLite crash.
- Audit golden tests assert required fields and absence of every submitted secret.
- A migration test starts from the current `admin.token` prototype and follows the documented one-time
  upgrade ceremony without leaving the old static token valid.
- UI tests prove the Workbench wizard calls REST, shows the proposed Git diff, handles restart/resume,
  and never places a token in browser storage or navigation URLs.
- The repository-wide `dotnet build` and `dotnet test` gates pass after implementation.

## Migration from the Phase 1 static token

Existing development data directories need an explicit upgrade path; silently treating
`admin.token` as a durable user credential would preserve the backdoor indefinitely.

Recommended migration:

1. Detect a legacy private `admin.token` and mark the instance `UNCLAIMED_LEGACY`.
2. Accept that token only on the setup-claim endpoint and only for one migration generation.
3. Require creation/binding of a durable administrator and control-repository baseline.
4. On successful completion, revoke the legacy value, remove or tombstone the file recoverably, and
   record its non-authenticating fingerprint in the migration audit event.
5. Reject the legacy value on panel login, normal REST, gRPC management, and CLI after completion.

The `submit.token` likewise needs later migration to an attributable service identity/PAT, owned by
User Roles and REST experts. It must not block the first administrator migration, but its continued
existence must remain visible as legacy security debt.

## Collaboration and open decisions

| Question | Required owner(s) |
|---|---|
| Exact control-repository baseline, branch/push policy, credential reference, and saga metadata | Git/Versioning Expert, Reconciliation Lead |
| Stable REST paths, media types, idempotency/error contract, setup auth scheme | Vivarium REST Expert |
| User schema, password hashing/provider binding, System Administrator role assignment | User Roles Expert |
| Workbench wizard, token-entry UX, recovery UI, Git diff presentation | UI Expert |
| Privileged startup sink, audit format, rate limiting, rotation, retention | Logs Expert |
| Private-file behavior and container secret conventions per OS | Platform Expert |
| State-machine, restart, security, and cross-platform evidence | Test Steward |
| Numbered architecture refinement and synchronized walkthrough/operations docs | Docs Expert |

The Admin/SuperUser Expert must actively request these reviews. A first-run implementation is not
complete when only its login form works: Git baseline, REST parity, audit, recovery, restart safety,
and normal-RBAC handoff are one contract.
