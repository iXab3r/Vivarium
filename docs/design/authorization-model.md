# Authorization Model

> Status: **Accepted**
> Implementation: **Planned**
> Maintainer role: [User Roles Expert](../roles/user-roles-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D4, D8, D22-D28

This design specializes the TeamCity-compatible role model adopted in D26 and the separate
AgentExplorer fleet permissions. Numbered architecture decisions remain authoritative.

This document consumes D26's Git desired-revision, applied-revision, validation, and reconciliation
contract. It defines authorization semantics at that boundary; it does not redefine repository
layout, commit transport, or the reconciler's global atomicity rules.

## Goals

- Preserve TeamCity's recognizable roles, project hierarchy, additive permissions, group inheritance,
  and agent-pool safety rules.
- Give TeamCity build execution and AgentExplorer host management separate least-privilege boundaries.
- Make REST, UI, CLI, and internal workflows enforce one authorization decision.
- Require every durable setting/property change to become a Git revision before it becomes effective.
- Keep immediate incident-response suspension available without pretending that an emergency action
  is a Git configuration change.
- Audit security-relevant actions without writing credentials or unbounded sensitive payloads to logs.
- Support human users and narrowly scoped automation from day one.

## Prior art copied from TeamCity

Vivarium starts in per-project authorization mode; it does not implement TeamCity's simpler
guest/logged-in/admin mode. TeamCity defines a permission as an operation, a role as a permission set,
and permits role assignment globally or to a project. Project grants propagate to subprojects; group
grants propagate through nested groups. Vivarium copies these semantics and the built-in role names.
See TeamCity's [Managing Roles and Permissions](https://www.jetbrains.com/help/teamcity/managing-roles-and-permissions.html).

TeamCity also requires a project-level agent-management permission in every project associated with a
shared agent pool before that permission can operate on the pool's agents. Vivarium copies this
all-associated-projects rule for TeamCity-side agent management. See
[Configuring Agent Pools](https://www.jetbrains.com/help/teamcity/cloud/configuring-agent-pools.html).

TeamCity access tokens can inherit the user's authority or be attenuated to selected projects and
permissions, are shown once, and can expire. Vivarium copies attenuation, one-time display, expiry,
and revocation, and additionally makes non-human service accounts explicit. See
[Configuring Your User Profile](https://www.jetbrains.com/help/teamcity/configuring-your-user-profile.html).

TeamCity records user actions in an audit log and exposes configuration history/diffs. Vivarium copies
the security-event coverage, but Git commits are the authoritative configuration history. See
[Tracking User Actions](https://www.jetbrains.com/help/teamcity/tracking-user-actions.html).

TeamCity can commit UI edits to versioned settings with the initiating user as author and keeps the
previous settings active when a candidate revision fails validation. Vivarium makes versioned settings
mandatory rather than optional. See
[Storing Project Settings in Version Control](https://www.jetbrains.com/help/teamcity/storing-project-settings-in-version-control.html).

### Deliberate Vivarium divergences

1. TeamCity's `Project Developer` can review agent details. In Vivarium this means only an agent's
   scheduling-safe summary and compatibility facts. Process command lines, environment values,
   network connections, files, and remote command execution are AgentExplorer data with separate fleet
   permissions.
2. TeamCity can grant interactive agent terminals to project administrators. Vivarium grants no
   AgentExplorer remote execution permission to Project Administrator by default. A project configuration
   is reviewed, versioned code; an ad-hoc command on a persistent physical host bypasses that boundary.
3. TeamCity allows mutable server-side settings with optional VCS synchronization. Vivarium activates
   durable settings only from Git. UI and REST mutation endpoints propose Git changes; they do not
   patch live configuration directly.
4. Guest login is disabled and not part of the initial model. An authenticated principal with no
   grants sees no projects or fleet resources.

These differences follow TeamCity's own least-privilege and agent-isolation guidance in its
[Security Notes](https://www.jetbrains.com/help/teamcity/security-notes.html), while accounting for
AgentExplorer's substantially broader physical-host access.

## Concepts

### Principals

- **User**: a human identity authenticated through the configured local or external authentication
  provider.
- **Group**: a named set of users and/or child groups. A child group inherits all parent-group role
  assignments. Cycles are rejected.
- **Service account**: a non-human identity for CI, scripts, and integrations. It has role assignments
  but no interactive password or browser session.
- **Access token**: a revocable credential owned by one user or service account. It is not an identity
  by itself and cannot have authority its owner lacks.
- **Superuser**: the break-glass identity established by the first-start flow. It is equivalent to
  System Administrator for authorization, is not used for ordinary automation, and is governed by the
  Admin/SuperUser design.
- **Agent identity**: a machine credential used for AgentHub and build/blob operations. It is outside
  user RBAC and can never authenticate to user-management or AgentExplorer management REST endpoints.
- **Enrollment token**: a short-lived, single-purpose setup proof. It grants no user permission.

### Identity declarations and credentials

Git declares desired identities and authority; the controller's private store proves them. These are
joined by stable opaque IDs and never collapsed into one record:

| Git desired declaration | Private operational state |
|---|---|
| Stable user/service-account/agent ID, display metadata, desired active/authorized state, group and role bindings, target agent pool | Password verifier, access-token hash, agent credential hash and generation, delivery outbox, revocation/tombstone, session and last-used metadata |

Creating a declaration does not mint a credential. Credential issue, delivery, rotation, and
revocation are separately authorized and audited actions. Git stores neither the credential nor a
reversible encrypted form of it.

Removing an identity declaration makes it ineffective when that desired revision is applied and
atomically revokes all of its private credential generations. A retained tombstone prevents replay.
Rolling Git back to reintroduce the same stable ID restores only its declaration and bindings; it does
not clear a suspension overlay, reuse a credential hash, or resurrect a token. An administrator must
explicitly issue a new credential generation. Renames preserve the stable ID and credentials.

### Resources and scope trees

Authorization has two explicit, non-interchangeable scope trees.

```text
Project Root                         Fleet Root
└── Project                          └── Agent Pool
    ├── Subproject                       └── Agent / ProviderInstance
    ├── Build Configuration
    └── Build / artifacts / results
```

- A project role assignment applies to that project and all descendants. A global assignment applies
  to Project Root and therefore every project. A child project cannot subtract an inherited grant.
- A fleet role assignment applies at Fleet Root or an agent pool and its agents. Project visibility
  never implies fleet visibility.
- One agent belongs to one agent pool; a project may use several pools and a pool may serve several
  projects.
- Safe TeamCity agent operations may cross from a project permission to an agent only when the caller
  holds that permission in **every** project associated with the agent's pool. This exactly protects a
  shared pool from one project's administrator.
- Sensitive AgentExplorer observations and mutations never use that bridge. They require a fleet-scoped
  permission directly.

Permissions declare their legal scope kinds. A global-only permission cannot be granted at a project
or pool. A role binding containing no permissions applicable to its target scope is rejected.

### Effective permissions

Permissions are additive. Effective authority is the union of:

1. Roles assigned directly to the principal.
2. Roles assigned to every group containing the principal, including inherited parent groups.
3. Permissions included through other roles.
4. Ancestor project or fleet-scope assignments.

There are no explicit deny entries in the initial model. Absence is denial. This matches TeamCity,
keeps explanations deterministic, and avoids ambiguous deny precedence across nested groups and two
scope trees. Incident response removes grants, revokes tokens, disables accounts, or invokes the
separate break-glass path.

For a human or service account, effective authentication additionally requires a present identity
declaration in the applied desired revision, desired `active: true`, a valid non-revoked private
credential, and no active suspension overlay. For an agent, the equivalent predicate is defined in
"Agent authorization lifecycle" below. An applied Git grant never overrides an immediate suspension.

Role inclusion is a directed acyclic graph. Cycles are rejected during Git validation. Permission and
role identifiers are stable API contracts; display names are not identifiers.

## Built-in roles

The five built-in names and their broad semantics match TeamCity. Their stable IDs and **minimum
permission bundles live in the versioned product schema/code** and migrate with the controller. Git
holds user/group/service-account bindings, may define custom roles, and may add explicitly supported
permissions to a built-in role, but it cannot delete a built-in role or remove a minimum permission.
Validation rejects such a revision before application. This prevents a configuration typo or rollback
across a product upgrade from silently changing what a recognizable built-in role means. Custom roles
are planned; the first implementation may ship only the built-ins and Git bindings.

| Role | Scope | Vivarium semantics |
|---|---|---|
| **System Administrator** (`SYSTEM_ADMIN`) | Global only | Unrestricted controller administration, all project and fleet permissions, user/role/token administration, Git repository binding, reconciliation, and audit access. It does not bypass Git for durable settings or secret-redaction rules. |
| **Project Administrator** (`PROJECT_ADMIN`) | Project or global | Includes Project Developer. Manages the project tree, project role assignments, settings proposals and approvals, build configurations, parameters, and project/pool associations within its scope. It has TeamCity-safe project agent management only under the all-associated-projects rule. It has no sensitive AgentExplorer observation or mutation by default. |
| **Project Developer** (`PROJECT_DEVELOPER`) | Project or global | Includes Project Viewer. Runs and cancels builds, changes queue order/priority within policy, supplies allowed run-time parameters, views results/logs/artifacts, and sees scheduling-safe agent summaries. It cannot change durable configuration, reveal secrets, or execute AgentExplorer commands. |
| **Project Viewer** (`PROJECT_VIEWER`) | Project or global | Read-only project, build configuration, build result, ordinary log, and artifact access. It can see the ancestor project path needed for navigation. Like TeamCity, it does not grant agent details. |
| **Agent Manager** (`AGENT_MANAGER`) | Fleet Root or pool | Views detailed fleet inventory; authorizes, enables/disables, drains, renames, assigns, and removes eligible agents; manages pool policy and agent custom properties through Git; and pauses/resumes scheduling. It does not receive remote command, file-write, process-control, software-mutation, secret-value, user, project-configuration, or Git-repository-binding permissions by default. |

An administrator creates a custom role for remote operators rather than broadening Agent Manager or
Project Administrator. System Administrator remains the only built-in role with every high-risk
permission.

## Permission catalog

This is the minimum target catalog. New features add the narrowest permission that corresponds to a
meaningful user action, not one permission per HTTP route.

### TeamCity/project permissions

| Permission ID | Scope | Meaning |
|---|---|---|
| `project.view` | Project | See project identity and hierarchy. |
| `project.settings.view` | Project | See non-secret project/build-configuration settings and their Git revisions. |
| `project.settings.propose` | Project | Submit a validated settings diff for Git commit/change-request creation. |
| `project.settings.approve` | Project | Approve a settings proposal when the configured Git workflow requires Vivarium approval. Never bypasses repository branch policy. |
| `project.create` / `project.delete` | Project | Propose creation/deletion beneath the scoped project through Git. |
| `project.roles.manage` | Project | Propose role bindings within this project scope; cannot grant permissions the caller does not possess in that scope. |
| `build.run` | Project | Queue a build from an accepted configuration revision. |
| `build.cancel` | Project | Stop queued/running builds. |
| `build.queue.manage` | Project | Reorder or change priority within server policy. |
| `build.parameters.customize` | Project | Override explicitly overridable run parameters; never secret references or agent policy. |
| `build.log.view` | Project | View ordinary build logs. |
| `build.artifact.view` | Project | Browse/download build artifacts. |
| `build.runtime-sensitive.view` | Project | View protected runtime parameters or other explicitly classified sensitive build data. |
| `build.agent-summary.view` | Project | See assigned-agent provenance and scheduling-safe facts for compatible/used agents. |
| `project.agent.enable` | Project | Enable/disable an agent through TeamCity's shared-pool rule. |
| `project.agent.authorize` | Project | Authorize an enrolled agent into an eligible pool through the shared-pool rule. |
| `project.agent.remove` | Project | Remove an idle project Agent through the shared-pool rule. |
| `project.agent.policy.change` | Project | Change run-configuration/pool association policy through Git and the shared-pool rule. |

`PROJECT_VIEWER`, `PROJECT_DEVELOPER`, and `PROJECT_ADMIN` include the corresponding permissions above
following TeamCity's viewer → developer → administrator progression. Sensitive runtime data remains
an explicit grant rather than an accidental consequence of artifact or log access.

### AgentExplorer/fleet permissions

| Permission ID | Scope | Meaning |
|---|---|---|
| `fleet.summary.view` | Fleet/pool | List agents and see connection, authorization, enablement, activity, version, OS summary, capabilities, and stale age. |
| `fleet.inventory.view` | Fleet/pool | View detailed non-sensitive OS/hardware facts, process names/metrics, and listening endpoint ownership. |
| `fleet.process-commandline.view` | Fleet/pool | View process paths, command lines, users, and sessions. |
| `fleet.environment-names.view` | Fleet/pool | View environment variable names and redacted metadata. |
| `fleet.environment-values.view` | Fleet/pool | View policy-allowed environment values. Secret-pattern values remain redacted unless separately supported by a future secret permission. |
| `fleet.agent.authorize` | Fleet/pool | Authorize/unauthorize an enrolled agent. |
| `fleet.agent.enable` | Fleet/pool | Enable, disable, drain, or resume an agent. |
| `fleet.agent.suspend` | Fleet/pool | Immediately suspend/resume an agent and fence its credential/session; incident-response permission. |
| `fleet.agent.manage` | Fleet/pool | Rename/remove idle Agents and propose custom properties or pool assignment changes through Git. |
| `fleet.pool.manage` | Fleet/pool | Propose pool policy, project association, capacity, and scheduling changes through Git. |
| `fleet.command.execute` | Fleet/pool | Execute an ad-hoc command on a host. High risk; no built-in role except System Administrator receives it. |
| `fleet.process.control` | Fleet/pool | Start, stop, or terminate a process outside a build. |
| `fleet.files.read` | Fleet/pool | Browse/read permitted remote files. |
| `fleet.files.write` | Fleet/pool | Create, modify, move, or delete permitted remote files. |
| `fleet.software.manage` | Fleet/pool | Install, upgrade, or remove software outside a build. |
| `fleet.agent.power` | Fleet/pool | Reboot, start, stop, or power-cycle a physical or provider-backed Agent. |
| `fleet.agent.snapshot` | Fleet/pool | Create, restore, promote, or remove snapshots of the Agent's attached ProviderInstance according to policy. |

Read-only inventory operations may run alongside a build if the AgentExplorer design allows it. Mutation
authorization does not override the common execution lease, cancellation, or safety interlocks.

### Global security, Git, and audit permissions

| Permission ID | Meaning |
|---|---|
| `users.manage` | Propose creation/deletion and desired activation of users/service accounts and manage groups through Git. |
| `users.suspend` | Immediately suspend/resume a human or service account and invalidate its active sessions; persisted and audited outside Git. |
| `roles.define` | Propose custom role definitions and included-role relationships through Git. |
| `tokens.manage-all` | Revoke and inspect metadata for all access tokens; token values are never recoverable. |
| `git.repository.bind` | Bind controller/project/fleet settings to repositories, paths, branches, and bot credentials. |
| `git.change.reconcile` | Retry, pause, or force a normal reconciliation after fixing a rejected revision; does not bypass validation or branch policy. |
| `git.policy.manage` | Configure approval requirements and protected settings paths through Git. |
| `audit.view` | View the non-sensitive audit stream. |
| `audit.sensitive.view` | View protected audit details if such storage is implemented. |
| `server.manage` | Configure controller-wide operational settings through Git. |

## Git-controlled authorization and settings

Git is the source of truth for durable desired configuration from the first implementation. This
includes:

- Projects, build configurations, steps, requirements, non-secret parameters, triggers, and policies.
- Agent custom properties, durable enablement/maintenance policy, pools, provider policy, and
  project/pool associations.
- Identity declarations, group structure, role bindings, custom role definitions, and allowed
  additive built-in-role customization. Built-in minimum bundles remain product schema.
- REST exposure policy, retention/logging policy, and other non-secret server settings.

Git does **not** contain credentials or event state:

- Passwords, token hashes, agent credentials, enrollment proofs, Git credentials, and secret values.
- Sessions, heartbeats, reported facts, current processes/ports, queue leases, build results, and logs.
- One-time operational actions such as run/cancel build, refresh inventory, immediate emergency drain
  or suspension, reboot, remote command, credential issue/delivery/rotation/revocation, or enrollment
  proof consumption. These are authorized and audited actions; any durable policy they change must
  still be committed separately.

Every settings mutation follows one pipeline regardless of whether it starts in UI or REST:

```text
authenticate
  -> authorize proposal against current resource and expected Git revision
  -> validate and render deterministic diff
  -> commit to a change branch or configured direct-write branch
  -> satisfy Vivarium approval permission and repository branch policy
  -> observe accepted commit
  -> validate the complete candidate model
  -> atomically reconcile projections
  -> audit commit and applied revision
```

No live setting changes before the accepted commit is observed. A rejected or invalid revision leaves
the last accepted revision active and exposes bounded diagnostics. Every read model and build snapshot
that depends on configuration carries the applied Git commit. Concurrent writes use an expected
revision and fail with a conflict rather than silently rebasing a user's intent.

`project.settings.propose` and `project.settings.approve` are separate permissions. Whether one person
may hold and exercise both is a repository/policy choice; Vivarium must support a four-eyes rule. Git
provider approval and branch protection remain authoritative when configured. Vivarium cannot claim a
change is approved merely because its own database says so while the repository rejects it.

For UI-originated commits, the human is recorded as Git author and the controller service identity as
committer. Commit metadata includes stable actor ID, request ID, target resource, and Vivarium version;
it contains no token or secret values. Direct commits made outside Vivarium are attributed to their Git
author and to the reconciliation service as the applying actor.

### Desired policy and immediate safety overlays

Git owns the durable desired state:

- An agent's desired `authorized`, `enabled`, pool assignment, and maintenance policy.
- A user or service account's desired `active` state and role/group bindings.

The operational store owns immediate safety overlays:

- **Agent drain**: rejects new TeamCity assignments but allows the current build to finish. Authorized
  AgentExplorer observation and maintenance operations remain available subject to the common execution
  lease and their own permissions. The overlay has an actor, reason, created time, optional expiry,
  and generation.
- **Agent suspend**: an incident-response barrier that rejects new sessions/work and sensitive
  AgentExplorer access. It revokes or fences the current credential generation and triggers the active-
  build behavior described below.
- **User/service-account suspend**: rejects authentication and all subsequent requests, invalidates
  browser sessions, and makes every access token ineffective without deleting token history.

Overlays are serialized, persisted, restart-safe, immediately effective, and fully audited. They take
precedence over Git desired state and are not cleared by Git reconciliation, branch rewind, controller
restart, or declaration re-creation. Only an authorized explicit resume/clear action (or a recorded TTL
expiry, where policy allows one) removes an overlay. Clearing an overlay reveals the currently applied
desired state; it does not force-enable an identity or agent.

Ordinary UI toggles edit Git desired policy. Buttons named **Drain now**, **Suspend now**, and **Resume
overlay** are visibly separate operational actions. If an emergency action should become durable, the
operator follows it with a Git change; failure to do so is shown as desired/overlay drift, not silently
reconciled in either direction.

### Agent authorization lifecycle

An agent is effectively authorized only when all of these are true:

```text
registration is declared in the applied desired revision
AND desired authorized == true
AND presented credential matches the current non-revoked private generation
AND no agent-suspend overlay is active
```

Scheduling additionally requires desired `enabled`, no drain/maintenance barrier, connected and
reconciled session state, idle activity, health, compatibility, and an available execution lease.
Credential validity alone never authorizes an agent; a Git declaration alone never authenticates it.

#### Initial authorization and pool assignment

A pending enrolled agent is visible by provisional stable ID and verified enrollment proof but has no
effective authorization and no trusted pool. The authorizing request names a **target** pool. Authority
is evaluated against that target, never against a pool ID or labels self-reported by the pending agent:

- A caller with target-pool `fleet.agent.authorize` may claim it into that pool.
- Alternatively, `project.agent.authorize` is accepted only if held in every project already
  associated with the target pool, matching TeamCity's shared-pool rule.
- A pool with no associated project requires target-pool fleet authority.

The resulting Git change atomically declares the agent's target pool and desired authorization. This
removes the catch-22 in which authorization would require membership in a pool the pending agent cannot
join until authorized. Pool capacity and enrollment-proof validation are still mandatory.

#### Restart-safe credential-delivery saga

After the declaration is accepted and its revision is applied, a serialized controller transaction:

1. Allocates a monotonically increasing credential generation.
2. Stores its verifier plus a protected, bounded, one-time delivery envelope/outbox record and the
   applied revision that authorized issuance.
3. Delivers `AuthorizationGranted` only to the pending session that owns the enrollment proof.
4. Treats the next `Hello` that proves that credential generation as delivery confirmation.
5. Deletes the delivery envelope after confirmation while retaining verifier, generation, and audit
   metadata.

The effective-authorization predicate remains false until the agent proves the new credential. A
controller crash replays the durable outbox; it never generates multiple concurrently valid secrets.
If an envelope cannot be recovered, the controller atomically revokes that generation and issues a
new one. A stale delivery or `Hello` is rejected by generation fencing. Removing authorization or the
agent declaration while delivery is pending cancels the outbox and revokes the generation.

#### Unauthorize, suspend, and delete during a build

- **Apply desired `authorized: false`:** reject new assignments and all new mutating AgentExplorer
  operations immediately. Do not invalidate the credential or kill the current build. The exact
  fenced session/build keeps a narrow completion lane for heartbeat, logs, build-scoped blob transfer,
  cancellation acknowledgement, and the first terminal result. A controller restart or reconnect may
  re-adopt only that build into the same restricted lane. After terminal state or lease expiry, the
  agent remains credential-valid but unauthorized and can use only the enrollment/status channel.
- **Drain overlay or desired `enabled: false`:** reject new assignments; the current build finishes.
  Neither changes authorization or credential validity.
- **Suspend overlay / emergency credential revocation:** persist cancellation intent, best-effort send
  cancel to the exact fenced session, then revoke/fence the credential and close the session. Because
  the untrusted agent can no longer upload a result, the build finishes from a result already durably
  accepted or by the bounded cancellation/reconnect lease as `INFRASTRUCTURE_FAILED`. This deliberately
  lossy security action is distinct from normal unauthorization.
- **Delete:** ordinary UI/REST deletion of an agent with an active build or lease returns conflict and
  creates no Git commit; cancel the build, or drain and wait for it to finish, first. If a direct Git
  deletion arrives while a lease exists, reconciliation persists a deletion barrier that prevents new
  work and reports the revision as waiting for quiescence. Once the build is terminal (or its lease
  expires), one serialized transaction cancels pending delivery, revokes every credential generation,
  closes sessions, tombstones the registration, applies deletion, and leaves immutable build
  provenance intact. There is no force-delete shortcut; incident response uses suspend, then
  cancellation/lease expiry, then delete.

Re-adding a deleted agent ID by Git rollback creates a declared but credentialless registration. It
cannot authenticate until a new enrollment/credential generation is explicitly authorized.

## Tokens and automation

- Personal tokens and service-account tokens are random, high-entropy bearer credentials stored only
  as hashes and displayed exactly once.
- Each token has an ID, owner, created time, optional expiry, last-used metadata, and explicit project,
  fleet, and permission attenuation. Effective token authority is the intersection of its declared
  limits with its owner's current effective permissions.
- Revoking a role, group membership, account, or permission takes effect for the next request even if
  the token was issued earlier. Token claims are not a stale embedded ACL.
- Removing or suspending an identity immediately makes all of its tokens ineffective. Deletion and
  explicit revocation retain tombstones; Git rollback never restores token validity. Re-activation
  requires a new credential where deletion/revocation occurred, while clearing a suspension overlay
  may reveal still-unexpired non-revoked credentials according to explicit resume policy.
- A CI service account normally receives `project.view`, `project.settings.view`, `build.run`,
  `build.cancel`, log/result/artifact reads, and payload upload for selected projects. It receives no
  fleet inventory, agent mutation, Git approval, user administration, or remote execution.
- The browser uses an authenticated, secure, same-site cookie. REST/CLI use bearer tokens. Both resolve
  to the same principal and permission evaluator.
- Agent tokens remain protocol-scoped machine credentials and cannot be converted into service-account
  tokens.
- Token values are never accepted in URLs and never written to logs, audit metadata, Git commits, or
  build parameters.

## Enforcement and denial behavior

Authorization lives in application services. REST handlers, UI actions, CLI commands, gRPC methods,
blob endpoints, streams, and background continuations call those services; UI hiding is only a user
experience detail.

Each decision uses `(principal, credential_generation, permission, resource, applied_revision,
suspension_generation)` and returns allow/deny plus a non-sensitive reason code. Long-lived watches
and downloads authorize at establishment and again when opening each protected child resource.
Mutating workflows reauthorize immediately before their durable commit/dispatch so a revoked role or
new safety overlay cannot survive an approval wait.

HTTP behavior:

- `401 Unauthorized`: no valid credential. Include a standards-compatible authentication challenge.
- `403 Forbidden`: authenticated, resource is already visible, but the requested action is not allowed.
- `404 Not Found`: an individual project/build/agent/pool is absent **or not visible** to the caller.
  Do not reveal which case applies.
- `409 Conflict`: authorization succeeded but expected Git revision or resource state no longer
  matches.
- Collection endpoints return only visible objects and compute pagination/counts after authorization
  filtering.

The UI disables or hides actions using the same permission-introspection response, but the server
always repeats the check. Error text must not reveal hidden names, sensitive parameter values,
environment data, paths, command lines, or role assignments.

## Audit requirements

The initial audit implementation may use structured, bounded logs, as requested, but audit events are
security records rather than free-form diagnostic messages. Every event contains:

- Timestamp, event type/schema version, success/denied/error result.
- Stable actor type and ID; token ID when applicable, never token value.
- Authentication method, source address, request/correlation ID.
- Permission checked and resource type/ID/scope.
- Git before/candidate/applied commit for settings operations.
- Operation/build ID for runtime actions.
- A bounded, redacted summary and machine-readable reason code.

Mandatory events include login and failed authentication, token create/revoke/use anomalies, user/group/
role changes, Git proposal/approval/reconciliation/rejection, agent authorize/enable/remove, pool and
project association changes, build run/cancel/queue changes, AgentExplorer sensitive reads and all
mutations, Agent power/snapshot actions, permission denials for mutations, and audit access itself.
Agent credential generations and delivery state transitions, deletion barriers, and every drain/
suspend/resume overlay transition are also mandatory events.

Do not log bearer credentials, cookie values, secret parameters, raw environment values, entire files,
unbounded command output, or unrestricted command lines. Sensitive commands are represented in the
ordinary audit log by operation ID, executable/redacted summary, and content hash; access to any exact
protected payload is a separately authorized feature. The Logs Expert owns rotation, retention,
backpressure, and export while preserving these fields.

## Current state and migration

Today D4 implements three coarse credential classes: agent, submit, and admin. The panel exchanges the
admin token for a cookie; the CLI uses scoped bearer tokens. There is no user/group/service-account
catalog, project RBAC, fleet RBAC, Git-backed role policy, or shared fine-grained evaluator. The
implemented API must therefore be treated as a single-admin transitional surface, not evidence that
the target model exists.

Migration sequence:

1. Introduce stable principal, permission, role, role-binding, service-account, and token metadata
   models plus one authorization service; preserve agent credentials separately.
2. Map the legacy admin credential to a temporary System Administrator migration principal and the
   submit credential to a temporary build-only service principal. Emit deprecation/audit events.
3. Enforce the shared evaluator at existing ControlPlane, blob, and panel boundaries before adding new
   REST endpoints.
4. Add Git-backed policy/configuration reconciliation and require an applied revision for settings
   mutation. Remove direct SQLite settings edits.
5. Add private credential generations/outbox/tombstones plus persisted drain/suspension overlays before
   Git becomes authoritative for identity and agent authorization.
6. Add fleet scopes and the sensitive AgentExplorer permission split before shipping process, environment,
   file, command, or software APIs.
7. Remove legacy credentials after administrators create named users/service accounts and verify the
   recovery path.

No migration grants a legacy submit token AgentExplorer access or Git approval authority.

## Invariants

1. No credential implies no access; Guest has no grants and is disabled.
2. Project authority never implies sensitive AgentExplorer authority.
3. Build execution never implies remote command, file, process, software, power, or snapshot control.
4. A token never grants more than its current owner and declared attenuation both allow.
5. An agent credential never authenticates as a human/service principal.
6. Agent authorization requires both a valid current credential generation and desired authorization
   in the applied Git revision; either side alone is insufficient.
7. Durable settings are never effective without an accepted, validated Git revision.
8. Safety overlays take immediate precedence and survive restart and Git rollback until explicitly
   cleared or validly expired.
9. Credential deletion/revocation is monotonic; Git rollback never resurrects a credential.
10. Secret values never enter Git, permission explanations, or ordinary audit logs.
11. Shared-pool TeamCity agent actions require the permission in every associated project; initial
    assignment is evaluated against the requested target pool.
12. Built-in role minimum bundles are product schema and cannot be weakened through Git.
13. Authorization is enforced below REST/UI/CLI adapters and is identical across transports.
14. Hidden resource existence is not disclosed by individual lookups, errors, counts, or pagination.
15. Every successful mutation and every denied mutation attempt is attributable to a stable actor and
    request/operation ID.
16. System Administrator can administer policy but cannot bypass Git activation, safety overlays,
    credential generation fencing, deletion quiescence, immutable build history, or secret redaction.

## Non-goals for the first authorization slice

- Full identity-provider federation, SCIM, SAML, or organization tenancy.
- Explicit deny ACLs, conditional policy languages, or arbitrary ABAC expressions.
- Per-field custom permissions beyond the documented sensitive-data classes.
- Treating Git repository access as sufficient Vivarium runtime access, or vice versa.
- Allowing the UI to mutate live settings when Git is unavailable.
- Storing secrets in Git merely because encrypted text is available.
- Using audit logs as build logs, command-output storage, or a substitute for durable domain state.

## Required evidence

- Golden tests for every built-in role and permission bundle.
- Project and nested-group inheritance tests, including absence of downward-to-parent authority.
- Shared-pool tests proving one missing project grant denies the operation.
- Tests proving project roles cannot read sensitive AgentExplorer data or execute commands.
- Token attenuation, expiry, revocation, one-time-display, and owner-role-revocation tests.
- Agent effective-authorization truth-table tests for declaration, desired authorization, credential
  generation, and suspension overlay.
- Restart/crash tests for credential issue/delivery/confirmation, unauthorization while building,
  restricted reconnect completion, deletion barriers, and credential tombstones.
- Initial pool-assignment tests for target-pool fleet authority and the every-associated-project rule.
- Drain/suspend/resume tests proving immediate precedence, persistence, explicit clearing, and no Git
  rollback resurrection for both agents and users/service accounts.
- Product-schema tests proving Git cannot delete or weaken a built-in minimum role bundle.
- REST/UI/CLI contract tests that reach the same application authorization service.
- `401` / `403` / `404` / `409` tests, collection filtering tests, and hidden-object timing/error review.
- Git tests proving invalid/rejected commits leave the old revision active, expected-revision conflicts
  do not rebase silently, and actor/commit metadata is preserved.
- Audit tests proving required events exist, are bounded, and redact tokens/secrets.
- Restart tests proving role bindings, revocation, applied Git revision, and audit ordering survive.

## Collaboration

- **TeamCity Expert:** owns resource semantics; requests permission review for every project/build/
  queue/agent operation.
- **AgentExplorer Expert:** classifies host data and operations; never exposes a new capability before
  assigning a fleet permission and audit class.
- **Vivarium REST Expert:** applies the HTTP denial and filtering contract and publishes permission
  requirements in endpoint documentation.
- **UI Expert:** consumes effective-permission data for presentation and routes all actions through
  guarded services.
- **Agent API/SDK Expert:** keeps machine credentials separate, implements generation-fenced delivery,
  and ensures controller instructions are authorized before dispatch.
- **Git/Versioning Expert:** implements proposal, approval, commit attribution, validation, expected-
  revision concurrency, and reconciliation without bypasses.
- **Admin/SuperUser Expert:** owns first-login and recovery while preserving System Administrator and
  break-glass audit semantics.
- **Logs Expert:** implements bounded/redacted security events and retention.
- **Docs Expert and Reconciliation Lead:** keep this target aligned with numbered architecture
  decisions, roadmap state, walkthrough, and implementation evidence.

## Open questions

1. Do custom roles ship with the first RBAC slice, or do the five TeamCity built-ins precede the role
   editor and Git schema?
2. Is pool-scoped Agent Manager required immediately, or is global Agent Manager plus project-scoped
   TeamCity agent management sufficient for the first release?
3. What default TTL, if any, is safe for drain overlays? Suspend overlays should default to no expiry.
4. Should clearing a user suspension preserve still-valid credentials or require rotation by default?
5. How long must credential tombstones and completed delivery metadata be retained?
6. How are VCS identities bound to Vivarium users strongly enough to trust commit attribution?
7. What protected store and retention apply if exact remote-command payloads must be recoverable for
   incident response?
