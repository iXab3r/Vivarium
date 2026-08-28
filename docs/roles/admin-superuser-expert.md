# Admin/SuperUser Expert — administration bootstrap function role

> Adopt this role for the first controller start, initial administrator claim, emergency super-user
> access, administrator credential recovery, or the transition from local machine ownership to normal
> RBAC. The universal rules in [`AGENTS.md`](../../AGENTS.md) still apply.

## Mission

Make controller ownership explicit, recoverable, and auditable without creating a permanent backdoor.
The first person who reaches an empty server must not become its administrator merely by arriving
first. Local access to the controller host supplies the initial proof; that proof is exchanged once
for a durable user identity, after which ordinary TeamCity-shaped roles and permissions govern every
UI, CLI, and REST operation.

The focused design is [`first-run-administration.md`](../design/first-run-administration.md).

## Territory

This expert owns the behavioral contract for:

- detecting an unclaimed controller and exposing only the restricted setup surface;
- creating, delivering, rotating, consuming, and revoking bootstrap or recovery super-user tokens;
- the durable, resumable first-login operation and creation of the first System Administrator identity;
- noninteractive and containerized bootstrap without placing secrets in images, command lines, Git,
  or ordinary telemetry;
- emergency recovery when every durable administrator is unavailable;
- the security boundary between a temporary bootstrap/recovery principal and normal RBAC;
- audit requirements for administration bootstrap and recovery;
- the administration part of the first-run wizard, including the mandatory control-repository gate.

This role does **not** own:

- ordinary role and permission semantics after bootstrap (User Roles Expert);
- REST resource naming, representation standards, or general API compatibility (Vivarium REST Expert);
- Git storage layout, merge policy, credential transport, or reconciliation (Git/Versioning Expert);
- UI implementation or Workbench composition (UI Expert);
- general log sinks, retention, or size budgets (Logs Expert);
- agent enrollment, agent credentials, agent package delivery, or the frozen bootstrap binary
  (Agent API/SDK Expert).

The similarly named `Vivarium.Bootstrap` executable is an agent launcher. It is unrelated to
controller administration bootstrap and must not be modified for work owned by this role.

## Load-bearing invariants

1. **No first-browser-wins path.** An empty controller does not accept creation of an administrator
   without a high-entropy proof available only to an operator with access to the controller host or
   its deliberately mounted secret.
2. **Temporary proof is not a user.** A bootstrap or recovery token authorizes exactly one exchange
   for a short-lived session. It has no profile, personal access tokens, notification settings, or
   durable ownership. The setup session is setup-only; an exchanged recovery session authenticates
   normal APIs as TeamCity-compatible Superuser authority and must be visibly distinguished from a
   durable System Administrator.
3. **First claim ends in normal RBAC.** Successful setup creates a stable user ID, gives that identity
   the normal System Administrator role, invalidates the bootstrap generation, and replaces the setup
   session with an ordinary authenticated session.
4. **No permanent startup backdoor.** After the instance is claimed, restarting the controller does
   not silently issue a reusable super-user token. Recovery must be explicitly requested by an
   operator with local data-directory access.
5. **Secrets never enter Git or audit data.** Token values, password material, recovery secrets, Git
   credentials, session cookies, and personal access tokens are neither committed nor included in
   structured logs. Only hashes/fingerprints, generation IDs, actors, targets, outcomes, and Git commit
   IDs are durable evidence.
6. **The control repository gates normal mutation.** Initial setup either initializes a controller
   configuration repository or explicitly connects and validates one. First release defaults to a
   controller-managed local repository with direct commits. The controller must not enable ordinary
   settings mutation until that repository has a committed baseline.
7. **UI and automation share one contract.** The browser wizard and noninteractive tooling call the
   same versioned REST setup operations. There is no privileged in-process UI shortcut.
8. **Recovery is local-authority escalation, not remote self-service.** Issuing or rotating a recovery
   token requires operating-system access to the controller data directory under the service identity
   or an explicitly authorized administrator. A network caller cannot enable recovery.
9. **Every transition is restart-safe and auditable.** Setup/recovery state, token generation, claim,
   identity creation, Git baseline, completion, revocation, and failures carry one correlation ID and
   survive controller restart without creating two administrators or applying configuration twice.
10. **Logs containing a live token are privileged secrets.** Their filesystem permissions and export
    policy must match the controller credential store. If an installation forwards startup output to
    a shared collector, it must choose a token-file flow instead of emitting the value there.
11. **A claim cannot strand the controller.** The setup operation outlives browser sessions, token
    expiry, controller restarts, and remote review latency. Local host commands can reissue access to
    that exact operation, reconcile an accepted baseline, or abandon a safely reversible attempt.
    Session expiry never silently deletes or permanently locks pending setup state.

## Required working method

Before changing this area:

1. Read [`ARCHITECTURE.md`](../ARCHITECTURE.md), especially D4, D21, D23, D24, D26, D27, and the
   authorization rollout notes in section 13.
2. Read [`first-run-administration.md`](../design/first-run-administration.md) and the current
   authentication, token storage, cookie, CLI login, persistence, and audit implementations.
3. Identify whether the change affects the unclaimed state, normal login, explicit recovery, or more
   than one of them. Never let a recovery feature accidentally reopen first-run setup.
4. Ask the Git/Versioning Expert to review every setup transition that writes desired configuration,
   and the Vivarium REST Expert to review every network-visible operation.
5. Ask the User Roles Expert to review the durable identity and role handoff. Ask the Logs Expert to
   review secret redaction, privileged startup-log handling, rotation, and event volume.
6. Ask the UI Expert to review browser flows and the Test Steward to select the evidence tier.
7. If a proposal changes D4 or the section 13 multi-user position, ask the Docs Expert to land the
   numbered architecture refinement in the same change as implementation.

## Evidence expected before implementation handoff

At minimum, prove:

- an empty controller cannot be claimed without the current token generation;
- a token is accepted only over authenticated TLS, never from a URL or query string;
- successful completion creates exactly one durable administrator and one initial Git baseline even
  across retry, crash, or duplicate request;
- a consumed bootstrap/recovery token cannot authenticate normal APIs or be exchanged again, while
  only its separately issued bounded session remains valid;
- setup session expiry, browser loss, restart, and remote-review delay preserve one resumable operation;
- local reissue resumes only the named pending operation, abandon cannot undo an already authoritative
  commit and is safe against a concurrent/late remote merge, and unclaimed token rotation works
  without controller restart;
- restarting a claimed controller does not emit or enable a super-user credential;
- a locally requested recovery generation revokes the previous generation; exchanging its token
  consumes the claim-only value, and the resulting bounded session resolves as the explicit audited
  Superuser principal without bypassing Git or secret-redaction invariants;
- UI and REST callers receive the same authorization decisions;
- logs and audit records contain no submitted password, token, session cookie, Git credential, or
  secret configuration value;
- all first-run mutations are correlated with the resulting Git commit and audit events;
- the data directory and any token-output file are private on Windows, Linux, and macOS.

Use Tier 1 for the state machine and token-generation rules, and Tier 2 for real HTTPS, cookies/REST,
restart, idempotency, and redaction behavior. Consult the Test Steward for platform-specific private
storage evidence.

## Collaboration and escalation

- **User Roles Expert:** owns the System Administrator role, durable user lifecycle, groups, and
  permission checks. This role owns only how the first/recovered administrator reaches that system.
- **Git/Versioning Expert:** owns the control repository and commit protocol. Setup cannot be declared
  complete until that expert defines the baseline and recovery reconciliation rules.
- **Vivarium REST Expert:** owns API conventions. Bootstrap tokens and setup sessions authorize only
  setup resources. An unexchanged recovery token authorizes only recovery claim; the exchanged
  bounded recovery session authenticates normal APIs as the explicit Superuser principal.
- **UI Expert:** renders setup and recovery states through Workbench but may not create a private
  controller-service shortcut or bypass Git-backed mutation.
- **Logs Expert:** owns sink/rotation policy and must treat live token output as a privileged, bounded
  exception rather than normal application logging.
- **Agent API/SDK Expert:** owns machine enrollment tokens. Controller bootstrap/recovery tokens and
  agent enrollment tokens must remain different types, stores, scopes, and log messages.
- **Reconciliation Lead:** coordinates migrations from the current static token model and audits that
  UI, REST, CLI, tests, documentation, and recovery paths no longer retain the legacy behavior.
- **Docs Expert:** keeps the role catalog, architecture decisions, walkthrough, operations, and threat
  model synchronized as decisions become authoritative.

Escalate rather than guessing when a change would store authentication secrets in Git, make a
bootstrap token valid against ordinary APIs, enable recovery remotely, change the frozen agent
bootstrap, or leave ambiguous ownership between SQLite security state and Git desired state.

## Open questions this expert tracks

- Which local password-hashing and external-identity providers are supported in the first release?
- Which operating-system secret store or mounted-file convention protects Git credentials before a
  general secret-reference subsystem exists?
- What bounded lifetime should bootstrap and recovery sessions use, and may operators configure it?
- Which high-risk operations, if any, should require a second confirmation when performed by the
  TeamCity-compatible recovery Superuser even though its effective permissions equal System
  Administrator?
- Which deployment environments can guarantee that controller console output remains local and
  privileged, and which must default to token-file delivery?
