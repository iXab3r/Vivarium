# Git-backed configuration and versioning

> Status: **Accepted**
> Implementation: **Partial — managed-local foundation and Agent enablement implemented; remote/review modes planned**
> Maintainer role: [Git/Versioning Expert](../roles/git-versioning-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D7, D8, D14, D17, D23-D29

## 1. Decision

Git is Vivarium's day-one source of truth for all mutable **desired configuration and properties**.
A configuration edit made through REST, the UI, or the CLI is a Git mutation with the same schema,
validation, concurrency, commit, and reconciliation behavior as a human-authored repository change.
The controller does not keep a second authoritative copy in SQLite.

Operational actions are deliberately different. Running or cancelling a build, refreshing inventory,
authorizing a one-time login, restoring a VM snapshot, or eventually executing an AgentExplorer command
is an action, not desired configuration. Actions are written to a structured audit journal and refer
to the exact applied configuration revision; they do not create noisy synthetic Git commits unless
they also change desired state.

Two Git authorities are expected:

1. A **controller configuration repository** owns fleet-wide desired state: project catalog,
   AgentExplorer policies, agent custom properties and enablement, RBAC, providers, image catalog and
   recipes, and controller policy.
2. A **tested product repository** owns the build configurations that test that product, preserving
   D17's `vivarium.yaml`-next-to-code model. A submitted build pins that repository's commit, config
   path, config blob hash, and resolved immutable definition.

The repositories may physically be the same repository for a small installation. Their authority and
revision provenance remain distinct in the model.

### Repository ownership

| Resource | Controller configuration repository | Tested product repository |
|---|---|---|
| Controller settings, retention, feature and distribution policy | Owns | Does not own |
| Users, groups, roles, bindings, project access policy | Owns | Does not own credentials or RBAC |
| Project catalog entry, display metadata, repository identity and allowed config paths | Owns | Is referenced by the catalog |
| Build configurations, ordered steps, requirements, parameters, triggers, matrix, artifact and failure rules | References and materializes | Owns in committed `vivarium.yaml` |
| Agent desired name, authorization/enablement, custom parameters and operation/deployment policy | Owns | Does not own |
| AgentExplorer policy, providers, image catalog and fleet-owned image recipes | Owns | May reference stable IDs only |
| Product source and product-specific test assets | Does not shadow | Owns |
| Secrets, live observations, queues, logs, artifacts and results | Neither Git repository owns | Neither Git repository owns |

Project-scoped settings that affect how code is built or tested live with the product build definition.
Project catalog, access, and fleet policy live with controller configuration. A future UI write must
target the repository that owns the field; a cross-repository UI operation is two independently
versioned mutations and must not claim false atomicity across Git repositories.

## 2. Current and target state

### Current state

- D17 makes a submitted `vivarium.yaml` authoritative and the controller persists the exact UTF-8
  snapshot, hash, and resolved assignment with the build.
- Agent-reported and operator-owned custom parameters are stored separately. Agent enablement, User
  declarations, and built-in RoleBindings are now desired Git state; names, custom parameters, groups,
  custom roles, and the remaining registration/authorization policy have not yet moved through this
  gateway.
- Authentication tokens and credential hashes live in runtime storage, as they should.
- The implemented ControlPlane is gRPC. The current Blazor panel calls controller services in process;
  public ordinary configuration mutation remains limited to Agent `spec.enabled`, while the bounded
  setup REST saga owns the exceptional initial User + RoleBinding baseline mutation.
- SQLite and the blob store contain queue, ownership, logs, artifacts, and results. This is operational
  history and remains outside Git.
- The controller now creates or adopts a normal non-bare managed-local repository on `main`. The D29
  adapter invokes system Git without a shell, builds candidates through isolated index/object state,
  validates the complete canonical tree with bounded size/path/schema and secret checks, and advances
  the authoritative ref through expected-old compare-and-swap. Bounded process execution, stable
  commit provenance, a private checkout recovery marker, and fail-closed human dirty-state handling
  protect the repository boundary.
- SQLite migration v5 durably records revision sets/members, per-scope active and last-known-good
  pointers, idempotent mutation operations, and the Agent desired projection. Migration v6 adds bounded
  affected-target metadata, exact conflict revision/diff replay, and retryable repository-attempt
  evidence. Reconciliation validates committed bytes, applies projections atomically with their
  pointers/audit, preserves the last-known-good projection when a revision is invalid, blocked, or
  removes a currently materialized Agent document, and recovers pending committed work after restart.
  Desired activation and scheduler admission share an Agent lifecycle lease; a bounded hosted monitor
  converges external managed-local commits into the durable and live projections.
- The canonical v1alpha1 tree currently supports `.vivarium/agents/{id}.yaml`,
  `.vivarium/rbac/users/{id}.yaml`, and `.vivarium/rbac/bindings/{id}.yaml`. The authorization cut is
  deliberately restricted to direct User bindings to product-owned built-in roles. Setup uses the
  atomic multi-document mutation primitive so User and `SYSTEM_ADMIN` never tear; reconciliation
  materializes both with the exact revision before the private credential activates.
  `/api/v1/agents/{id}/settings` GET/PUT remains the only general desired-state management resource.
- Remote authority, review workflows, repository credentials, host-trust integrations, and broader
  desired-state schemas remain planned.

### Target state

- Every desired-state read identifies its source repository and commit.
- Every desired-state write enters one mutation gateway used by REST, UI, and CLI.
- A mutation builds and validates a complete candidate tree, creates one commit, atomically advances
  the authoritative ref, and then reconciles that commit.
- The active runtime snapshot is immutable and keyed by commit SHA. SQLite may cache indexes and
  materialized projections, but they can be discarded and rebuilt.
- Every build records both its control-repository revision and tested-repository revision.
- Every privileged operation emits a structured, bounded, secret-free audit record; configuration
  records link to the resulting commit.

## 3. Classification boundary

The following classification is normative. New data must be classified before its storage is chosen.

| Class | Examples | Authority |
|---|---|---|
| Desired configuration | project metadata; build definitions; agent display name, enabled state, custom labels; AgentExplorer operation policy; roles and bindings; providers; images; retention policy | Git |
| Runtime observation | connection state, heartbeat, reported capabilities/facts, OS inventory, processes, ports, health measurements | Agent/controller runtime store |
| Operational action | run/cancel build, authorize enrollment, refresh inventory, restart agent, rollback VM snapshot, AgentExplorer command | Audit journal plus domain state |
| Secret material | bearer token, credential hash, private key, license value, repository credential | Secret/credential store, referenced from Git |
| Immutable execution history | queue claims, assignments, logs, artifacts, results, agent provenance | SQLite/blob store |

Some commands cross the boundary. For example, approving an enrolled agent is an audited action that
creates or updates its Git-owned desired agent declaration, while issuing its bearer credential is a
runtime secret operation. Disabling an agent is a Git mutation; stopping its current build is a
separate audited action. Deleting an agent removes desired configuration in Git and independently
revokes its credential after authorization checks.

Live facts never write themselves back to Git. An operator may explicitly **accept** a reported value
as desired state, which creates an ordinary reviewed mutation. This prevents feedback loops and noisy
commits every time a host changes.

## 4. Repository bootstrap and authority

The first-release bootstrap is deterministic: unless an external repository is explicitly configured,
the controller initializes **managed-local direct Git** on branch `main` in its data directory and
creates the canonical minimal initial commit. No interactive choice or network service is required.
Supplying explicit external repository settings selects remote authority and its configured direct or
review workflow; merely adding an `origin` to the managed-local repository does not change authority.

### Managed local repository

The controller initializes a normal non-bare repository in its data directory with a minimal schema
manifest and initial commit on `main`. Its branch is locally authoritative and the mutation gateway
writes it directly with compare-and-swap. This is the first-release default and preserves Vivarium's
no-external-service requirement. Adding a remote later is optional and does not silently change
authority or enable review mode.

### Existing remote repository

The administrator supplies a repository URL, authoritative branch, pinned host trust, and a reference
to repository credentials. The remote branch is authoritative. The controller clones it and applies
only fetched commits from that branch. In this mode a UI/REST change is not active merely because it
exists in a local branch: direct mode must push successfully, while review mode waits for the change
to be merged and fetched.

### Private repository credentials and host trust

External-repository bootstrap information must be available before that repository can be read, so it
lives in protected controller startup configuration, not inside the selected Git repository. It
contains the repository URL, branch, workflow/authority mode, a credential reference, and host-trust
policy.

- HTTPS uses normal certificate validation, optionally augmented by an explicitly installed private
  CA or pinned server public key. Disabling certificate validation is forbidden.
- SSH uses pre-provisioned `known_hosts` keys or pinned host-key fingerprints. `accept-new`, blind
  trust-on-first-use, and disabled host-key checking are forbidden for unattended bootstrap.
- Credentials are resolved from the startup secret/credential store. Passwords, deploy keys, and
  access tokens are never embedded in repository URLs, command lines, Git config committed with the
  repository, logs, or audit fields.
- A read-only credential is sufficient when humans/automation update the authoritative branch. Direct
  push or controller-created review branches require the narrow corresponding write permission.
- Missing credentials, an unknown host key, or a pin mismatch fails closed and leaves any verified
  last-known-good configuration visible but read-only.

Credential rotation does not change repository identity or create a configuration commit. Changing
repository URL, host-trust roots, or authority mode is an audited bootstrap migration.

### Trusting externally authored security changes

Git commit author text is not authenticated. In remote-authority mode, reaching the configured branch
is therefore insufficient by itself to activate an external commit that changes RBAC, agent
authorization, secret references, repository trust, build-execution policy, AgentExplorer operation
policy, or another schema-designated security-sensitive resource. Trust policy is selected per
authoritative repository. Controller-repository trust is bootstrap configuration; product-repository
trust is declared by the already-trusted controller project catalog. Each must select exactly one
policy:

1. **Attested identities.** Each security-sensitive commit or forge merge attestation is
   cryptographically verified against bootstrap-pinned trust roots, mapped to a Vivarium subject, and
   authorized using the previously accepted configuration revision. A change cannot grant itself the
   permission needed to authorize that same change. The controller validates every commit in order
   from the last accepted head; an untrusted link blocks later descendants.
2. **Repository writers are administrators for repository-owned resources.** Advancing the protected
   authoritative branch is explicitly declared equivalent to the highest Vivarium permission over
   resources that repository owns. For the controller repository this is SuperUser authority; for a
   product repository it is Project Administrator/build-definition authority for the mapped project,
   including the ability to cause code to run on matching agents. Branch protection, review rules, and
   forge administrator access become part of Vivarium's security boundary, and setup must state that
   consequence. This is a deliberate policy, not an inference from a successful fetch.

Controller-originated commits remain tied to an authenticated mutation intent and audit event. If
they are merged externally, the selected external policy still governs the merge/branch transition.
The initial external repository head is accepted only under the configured bootstrap trust policy.
Commit signatures supplement but never replace schema validation, authorization, or audit.

The bootstrap choice, repository identity, and authoritative branch are controller startup
configuration because they are needed to locate the Git source of truth. They are not mutable through
the ordinary repository that they select. Changing repository authority is an explicit administrative
migration with a maintenance window and audit event.

The first-login token is printed to the controller's protected startup log by the Admin / SuperUser
flow. It is short-lived runtime secret material and is never written to Git or commit metadata. After
first login, the administrator creates Git-owned users/role bindings through the normal mutation path.

If an existing repository contains no Vivarium manifest, initialization proposes a first commit; it
does not overwrite unrelated content. A non-empty incompatible repository is rejected with a precise
diagnostic.

## 5. Canonical repository layout

The controller configuration tree uses one resource per file under a reserved directory:

```text
.vivarium/
  repository.yaml
  controller/settings.yaml
  projects/<project-id>.yaml
  agents/<agent-id>.yaml
  agent-explorer/policies/<policy-id>.yaml
  providers/<provider-id>.yaml
  images/<image-id>/image.yaml
  images/<image-id>/recipes/<recipe-id>.yaml
  rbac/users/<user-id>.yaml
  rbac/groups/<group-id>.yaml
  rbac/roles/<role-id>.yaml
  rbac/bindings/<binding-id>.yaml
```

`repository.yaml` declares the repository schema version and enabled document kinds. Domain designs
may add subtrees only after review by the Git / Versioning Expert and Docs Expert. Runtime-generated
files, locks, caches, logs, tokens, and resolved secret values are forbidden under `.vivarium/`.

Product repositories keep one or more `vivarium.yaml` files next to tested code. The controller
project declaration records the repository identity and permitted config paths; it does not copy a
mutable shadow build configuration into the controller database. If a future UI authors a product
build configuration, it commits to the product repository through the same mutation protocol.

### Stable identity

- Every resource document contains `apiVersion`, `kind`, `id`, and `spec`.
- `id` is immutable, unique within its kind and repository, and independent of display name and path.
- Human-selected IDs use a case-sensitive restricted ASCII form such as
  `windows-release-tests`; agent IDs retain their enrolled stable GUID.
- References use `(kind, id)`, never display names or filesystem-relative inference.
- Renaming or moving a file does not change identity. Reusing a retired ID for a different logical
  resource is forbidden.
- Cross-repository references include repository identity plus commit or an explicit moving-ref
  policy. Builds always resolve them to commits before execution.

## 6. Canonical serialization

YAML is the human-facing format. The accepted canonical subset is intentionally narrow:

- UTF-8 without BOM and LF line endings on every platform;
- one resource document per file;
- lowercase, forward-slash repository paths with case-collision validation;
- fixed top-level key order: `apiVersion`, `kind`, `id`, `metadata`, `spec`;
- deterministic field order defined by each versioned schema;
- map keys sorted ordinally where the schema does not define semantic order;
- list order preserved only where order is semantic, such as build steps; set-like lists are sorted;
- explicit booleans, strings, and numbers with no implicit YAML type surprises;
- no duplicate keys, aliases, anchors, merge keys, custom tags, or environment interpolation;
- defaults omitted or emitted consistently per field; `null`, missing, and empty are never silently
  treated as the same value unless the schema says so;
- exactly one trailing newline.

`viv config format` and the controller canonical writer implement the same formatter. A controller
mutation rewrites affected documents canonically; it does not reformat unrelated files. Comments are
not part of the configuration model and may be lost in a document changed through the UI. Durable
rationale belongs in explicit `metadata.description` or documentation, not only in YAML comments.

Canonical document bytes are hashed for diagnostics, but the authoritative revision is the Git commit
SHA and repository identity. The design does not assume SHA-1 specifically; it accepts the object
format of the configured repository.

## 7. Mutation and commit protocol

REST, UI, and CLI call one controller-side `ConfigurationMutationService`. Domain services submit
typed intent; they do not edit files. A request includes:

- operation and idempotency key;
- repository identity and expected base commit;
- actor subject and authenticated credential ID;
- affected resource IDs and typed patch/create/delete intent;
- human reason when policy requires it;
- review mode selection permitted by repository policy.

The service performs the following state machine:

1. Durably record a mutation intent keyed by idempotency key.
2. Read the expected base tree without mutating the active checkout.
3. Apply all typed changes to an isolated candidate tree.
4. Canonically serialize changed documents.
5. Validate the complete candidate repository.
6. Create one commit with all affected files.
7. Advance the target ref with compare-and-swap against the expected base.
8. Append or recover the audit event linking operation ID and commit SHA.
9. Reconcile the authoritative commit and expose apply status.

No consumer observes a half-written multi-file change. A project, its role binding, and its policy can
therefore be created in one commit or not at all. A temporary worktree/index or direct Git tree
construction is an implementation choice; editing the active checkout file by file is not acceptable.

Git ref update and SQLite audit/intention state cannot share one physical transaction. Recovery closes
that gap:

- a commit created without a successful ref update is unapplied and may later be garbage-collected;
- a ref updated before the audit row is finalized is discovered from the commit trailers and completed
  idempotently on restart;
- an idempotency retry returns the original operation/commit rather than creating a second commit;
- materialization begins only from the authoritative ref, never from an orphan commit.

### Authorship and commit metadata

The authenticated human or service principal is the Git author. The controller is the committer. A
stable subject ID is mandatory; display name and verified email are informative and may change.
Controller-created commits use a concise domain message and machine-readable trailers:

```text
Update agent policy for windows-lab-01

Vivarium-Operation-ID: <uuid>
Vivarium-Request-ID: <idempotency-key>
Vivarium-Actor-ID: <stable-subject-id>
Vivarium-Actor-Type: user|service
```

Tokens, source IPs, raw user-agent strings, and secret values do not belong in commit metadata. The
audit event may retain policy-approved request context. Impersonation, when eventually supported,
records both authenticated and effective subjects in audit and never disguises the author.

## 8. Direct, branch, and review workflows

Repository policy selects one of two write workflows:

### Direct workflow

The validated commit advances the configured authoritative branch with optimistic compare-and-swap.
For a remote-authority repository, push must succeed as a fast-forward before the commit becomes
eligible for apply. Protected-branch policy may disable direct writes.

### Review workflow

The controller creates `vivarium/change/<operation-id>` at the expected base and returns the branch
and commit. An optional Git-host adapter may push the branch and open a pull request, but Vivarium does
not require or embed a Git hosting product. The active configuration changes only when the reviewed
commit reaches the authoritative branch and is fetched.

The UI shows `pending review`, `merged but not applied`, `active`, or `rejected/invalid` rather than
pretending a submitted form is immediately active. REST returns the same operation and revision
state.

Human-authored commits and merges are first-class. The controller validates every new authoritative
head independently and applies the external security-commit trust policy from §4. A Git hook or CI
check improves feedback but is not a trust boundary.

## 9. Concurrency and conflicts

Revision classes are explicit and never interchangeable:

- `configurationRevision = { repositoryId, commit }` identifies a committed desired tree. A
  configuration endpoint exposes a strong configuration ETag derived from this pair and requires it
  through `If-Match` (or an equivalent explicit base field) for mutation.
- `appliedConfigurationRevision` identifies the validated commit currently materialized by the
  controller. It may lag `configurationRevision` while a new head is pending, invalid, or blocked.
- `observationRevision` identifies an agent inventory/facts snapshot and its observation ETag. It is
  for cache validation and refresh semantics, never repository compare-and-swap.
- `runtimeVersion` is a domain-specific monotonic version/fencing token for a queue row, lease,
  registration session, or operation. An action that must interlock with live state supplies an
  explicit expected runtime version; it does not reuse `If-Match` from configuration.

Every configuration write carries the `configurationRevision` observed by the caller. UI forms retain
the repository identity and base commit from load through submit. REST routes must not expose one
ambiguous `revision` field or accept an observation/runtime ETag as a configuration precondition.

If the authoritative ref moved, the mutation returns a conflict with:

- expected and current commit;
- resource IDs and paths changed since the base;
- whether the candidate can be regenerated without semantic overlap.

The controller never silently force-pushes, resets, or chooses a merge winner. It may regenerate a
candidate only after an explicit retry against the new base; semantic validation runs again. Conflicts
are reported even when textual changes appear mergeable if both edits touch the same typed property.

Multi-file changes have one expected base and one commit. Per-file optimistic concurrency would allow
cross-resource invariants to tear and is forbidden.

## 10. Validation before commit and apply

Candidate validation runs before commit for controller-originated mutations and again before
materialization for every authoritative commit. It includes:

1. repository and document syntax;
2. supported schema/api versions;
3. canonical path and case-collision rules;
4. stable-ID uniqueness and tombstone/reuse rules;
5. cross-document referential integrity;
6. domain semantic validation from TeamCity, AgentExplorer, RBAC, providers, and images;
7. authorization for the complete mutation, including privilege-escalation checks;
8. secret scanning and enforcement of reference-only secret fields;
9. platform validation for commands, paths, and policies that claim cross-platform support;
10. migration compatibility with the running controller version.

Controller-originated invalid mutations create no commit. An invalid human-authored authoritative
commit is not applied: reconciliation retains the last-known-good snapshot, marks the head `INVALID`,
and exposes actionable diagnostics in REST/UI/logs. It does not rewrite the user's branch.

Schema upgrades are explicit repository migrations that create ordinary commits. A controller may
read older supported schemas, but must not silently rewrite a repository merely because it started.

## 11. Secrets and credentials

Git documents contain opaque references such as a typed `SecretRef`; they never contain resolved
values. The initial secret backend may be local, but its API must permit later external providers
without changing domain schemas.

Required behavior:

- validate that secret-bearing schema fields accept references only;
- resolve a reference only at the narrow execution boundary that needs it;
- never place resolved values in candidate trees, commits, diffs, audit events, logs, REST responses,
  materialized caches, or agent parameter snapshots;
- pass only the required value to the required agent/build/operation and apply log redaction;
- authorize secret-reference use separately from secret-value read;
- rotate a value without a Git commit when the reference is unchanged;
- fail closed when a required reference cannot be resolved.

Enrollment tokens, agent credentials, session tokens, repository credentials, and the initial
superuser token are runtime credentials, not Git configuration. Git may contain policy and a reference
to a credential, never the credential itself.

## 12. Materialization and caching

The reconciler reads an immutable authoritative commit, validates it, compiles domain models, and
atomically swaps one `ActiveConfiguration` pointer only after the whole tree succeeds. The active
snapshot records repository identity, commit SHA, schema version, apply time, and diagnostics.

SQLite projections support indexed REST reads, scheduling, compatibility queries, and UI performance.
They are tagged by source commit and are disposable. A projection transaction must either represent
one complete commit or remain on the previous commit. Startup can rebuild projections from Git.

Agents do not clone configuration repositories and do not receive repository credentials. The
controller resolves the active policy and sends the smallest typed assignment or policy snapshot the
agent needs. Agent-reported facts flow in the opposite direction and remain runtime observations.

Build execution pins:

- `controlConfigurationRevision = { controllerRepositoryId, appliedCommit }`;
- `productConfigurationRevision = { productRepositoryId, productCommit }`;
- `vivarium.yaml` path and canonical blob hash;
- fully resolved build definition and parameter/agent provenance already required by D14/D17.

Both revision fields are persisted before submission returns and copied into child builds/assignments.
They remain separate even when both authorities happen to use the same physical repository and commit,
so history remains unambiguous after repositories split or move.

The controller rejects a build configuration loaded from an uncommitted working-tree edit. A local
development payload may be dirty only if its configuration still resolves from a committed blob and
the build records a separate dirty-payload marker; allowing or rejecting dirty payloads is a TeamCity
policy decision, not a loophole for unversioned configuration.

REST configuration reads return `configurationRevision`, `appliedConfigurationRevision`, and the
configuration ETag. Operational reads return the applied configuration revision plus their separate
`observationRevision` or `runtimeVersion` and observation timestamps.

## 13. Reconciliation and drift

The Reconciliation Lead owns the loop; this document defines its revision contract. The controller
watches the local authoritative ref and periodically fetches in remote-authority mode. For each head
it reports:

- `PENDING`: discovered but not yet validated;
- `INVALID`: validation failed; last-known-good remains active;
- `APPLYING`: projections/effects are being prepared;
- `ACTIVE`: all required controller projections use this commit;
- `BLOCKED`: valid desired state cannot currently be realized;
- `SUPERSEDED`: a newer authoritative commit became the target.

Drift is reported separately at three boundaries:

1. **Repository drift:** authoritative head differs from active commit.
2. **Projection drift:** cached/controller state claims a different source commit or cannot be rebuilt.
3. **Environment drift:** actual agent, provider, image, or host state differs from desired policy.

The controller never auto-commits observed environment drift. Safe idempotent reconciliation may
bring runtime state toward desired state; destructive or availability-affecting corrections require
the owning domain's policy and audit event. `Accept observed state` is an explicit Git mutation.

A manually modified dirty controller checkout is not an alternate desired state. The controller
refuses new writes and apply from that checkout, reports the paths, and requires the operator to
commit, discard, or move the changes through Git deliberately.

### Agent property application and interlocks

Activating a controller configuration commit atomically changes controller-side desired state. Effects
that require an agent handshake are tracked per agent as `PENDING_DELIVERY`, `PENDING_IDLE`, `APPLIED`,
`BLOCKED`, or `SUPERSEDED`; one slow agent does not roll back unrelated desired resources. The
scheduler and agent registry serialize activation, assignment claims, and occupancy changes through
the existing controller writer/interlock boundary.

| Desired agent property | Controller-side effect | Existing work | Completion condition |
|---|---|---|---|
| `enabled: false` | Immediately rejects new assignment claims after the commit becomes active | Current build continues, matching D8 | Controller projection active; no agent ack required |
| `authorized: false` | Immediately rejects new builds/operations | Current build and authorized artifact upload may finish; credential is not implicitly revoked, matching D4 | Controller projection active |
| `authorized: true` | Remains ineligible until enrollment credential delivery is complete | None | Exact live session acknowledges authorization |
| display name or custom scheduling parameters | New claims use the new values | Running build retains immutable assigned-agent snapshot | Controller projection active |
| removal of agent declaration | Immediately drains and rejects new work | Busy registration remains visible until work ends; credential revocation/removal then runs as audited epilogue | Idle plus credential revocation complete |
| AgentExplorer/build execution policy | Blocks newly forbidden operations immediately | In-flight operation follows its domain cancellation contract; it is never silently reinterpreted | Required policy revision delivered and acknowledged where agent enforcement is needed |
| agent version/channel/deployment policy | Drains when restart/swap is required | Never restarts through an active build | Agent becomes idle, restarts safely, and reports/acknowledges the required version |

The race rule is deterministic: if the serialized assignment claim commits first, that work owns the
agent and later disable/unauthorize changes do not cancel it; if configuration activation commits
first, the claim observes the new policy and fails. A policy that requires agent-side enforcement also
makes the agent ineligible for affected new work until that session acknowledges the exact policy
revision.

The Agent API / SDK Expert owns wire messages, durable delivery, acknowledgement, reconnect behavior,
drain/restart mechanics, and per-capability cancellation details. This design owns Git classification,
desired/effective revision reporting, and the controller-side scheduling interlock those mechanisms
must satisfy.

## 14. Rollback

Configuration rollback is a forward-moving Git operation: compute the inverse change needed to make
the current tree match a selected earlier revision, validate it against current schemas, and create a
new commit through the normal optimistic-concurrency path. Do not reset the branch, rewrite published
history, or merely point the runtime cache at an old commit.

The rollback operation records the source revision, new commit, actor, reason, and validation result.
It may conflict with changes made after the selected revision and must present those conflicts rather
than erasing them. Secret values are not restored by Git rollback; secret references may be.

Git configuration rollback is unrelated to provider/VM snapshot rollback. The latter is an audited
runtime action coordinated with agent leases and machine lifecycle. UI and APIs must use distinct
names and identifiers for the two operations.

## 15. Audit linkage and logging

Git history answers what desired configuration changed. It does not by itself answer who attempted a
denied action, read sensitive inventory, ran a build, cancelled work, restored a machine, or observed
an apply failure. Vivarium therefore emits a structured `Vivarium.Audit` event for at least:

- login, logout, token issuance/revocation, and failed authentication policy events;
- agent enrollment, authorization, enable/disable request, rename, and deletion;
- every configuration mutation request, rejection, commit, merge observation, validation, apply, and
  rollback;
- project/build start, retry, cancel, and administrative queue changes;
- AgentExplorer inventory refresh and sensitive environment/process access;
- command/process/software/file operations when those capabilities arrive;
- provider power, clone, snapshot, restore, quarantine, and destructive lifecycle actions;
- role/binding and secret-reference use or secret-value access.

The initial journal is D27's minimal append-only SQLite `audit_events` table, with bounded retention,
access control, and export defined by the Logs and Persistence Experts. When caller-visible success
depends on accepted intent, the audit row commits in the same serialized transaction. Each event has a
stable event ID, UTC time, operation/correlation ID, authenticated and effective actor IDs, action,
target kind/ID, outcome, reason/error code, and applied configuration revision. Configuration events
additionally contain base and resulting commit. Values likely to contain secrets are excluded rather
than merely formatted.

Git commits and audit events link through `Vivarium-Operation-ID`. Recovery can recreate a missing
commit-success event from commit trailers, but the Git commit is not a replacement for the action
journal.

Build stdout/stderr, agent diagnostic logs, metrics, process inventories, artifacts, test results, and
audit records have different schemas and retention. They must not be concatenated into one unbounded
log or committed to Git. High-volume operational data remains in SQLite/blob/metrics storage and is
governed by the Logs Expert.

## 16. Failure and degraded behavior

The last-known-good configuration keeps the system observable during Git failures, but failure never
creates an alternate authority.

| Failure | Required behavior |
|---|---|
| Repository unavailable at cold start and no verified cache | Start diagnostics/login surfaces only; do not schedule work or accept configuration writes |
| Repository unavailable with verified last-known-good snapshot | Continue reads and already-authorized runtime behavior under that revision; reject mutations explicitly |
| Local disk full/read-only/lock failure | Reject mutation before claiming success; keep active snapshot; raise health alert |
| Remote unavailable in local-authority mode | Local commits may apply; remote sync is visibly degraded and never described as authoritative |
| Remote unavailable in remote-authority mode | Existing active config continues; writes may create review branches locally but cannot apply until push/merge/fetch succeeds |
| Private-repository credential or host-trust failure | Fail fetch/push closed; retain verified last-known-good read-only; never downgrade TLS/SSH verification |
| Untrusted external security-sensitive commit | Do not apply that commit or descendants; report trust diagnostics while retaining last-known-good |
| New head invalid | Keep last-known-good; mark head invalid with diagnostics; do not auto-revert user history |
| Audit sink temporarily unavailable | Follow Logs policy: privileged mutation/action must fail closed once bounded durable buffering is exhausted |
| Crash during mutation | Recover from intent, authoritative ref, commit trailers, and idempotency key; never infer success from a candidate file |

REST returns a stable error code such as `CONFIG_REPOSITORY_UNAVAILABLE`, the active revision, and
retryability. The UI shows degraded/read-only status prominently. No endpoint writes desired state to
SQLite as an emergency fallback.

## 17. Invariants

1. One committed tree is authoritative for each configuration repository at a time.
2. Every active configuration has an exact repository identity and commit.
3. Every configuration change is a complete, validated commit or has no effect.
4. Every writer uses optimistic concurrency against the authoritative ref.
5. Configuration revisions/ETags are never interchangeable with observation revisions or runtime
   fencing versions.
6. Stable IDs survive display-name and path changes.
7. Runtime projections never become a competing source of truth.
8. Git stores references to secrets, never secret values or credentials.
9. External security-sensitive commits satisfy attested-identity authorization or an explicit
   repository-writers-are-SuperUsers policy.
10. Operational actions are audited and configuration events link to commits.
11. Invalid or unreachable Git state retains a visible last-known-good configuration; it never causes
   silent fallback or partial apply.
12. Rollback preserves history by creating a new commit.
13. Builds pin separate controller and product configuration revisions.
14. High-volume logs, observations, artifacts, and results stay out of Git.

## 18. Non-goals

- Building a Git server, pull-request product, merge UI, or forge-specific workflow into Vivarium.
- Using Git as the build queue, event bus, lock service, metrics store, audit-log sink, blob store, or
  secret store.
- Giving agents Git credentials or making them reconcile controller configuration.
- Automatically committing every observed agent fact or environmental drift.
- Versioning process lists, port inventories, heartbeats, logs, artifacts, or test results in Git.
- Solving source-code review policy for every organization; Vivarium supplies direct and review modes.
- Rewriting a user's published branch to perform rollback or repair.
- Treating a commit signature as a substitute for schema validation, authorization, or audit; it is
  only one input to the configured external-authority trust policy.

## 19. Evidence and acceptance criteria

The design follows existing Vivarium evidence and decisions:

- D17 already establishes `vivarium.yaml` next to tested code and immutable submission snapshots.
- D14 requires immutable build-definition and assigned-agent provenance.
- D8 separates agent-reported facts from operator-owned desired parameters.
- D4 separates authentication scopes and requires protected management surfaces.
- Section 6 makes in-memory state a projection and uses durable SQLite/blob storage for operational
  history; the Git design extends the same source-of-truth discipline to desired configuration.
- D6 and the image recipes already require declarative files in Git.

The complete design is accepted only with evidence for:

- repository initialization and existing-repository attachment on Windows, Linux, and macOS;
- deterministic managed-local/direct startup when no remote is configured, plus private HTTPS/SSH
  credential and pinned-host-trust failure cases;
- identical canonical output from REST, UI, CLI, and `viv config format`;
- atomic two-resource mutations and stale-base conflicts;
- rejection of observation/runtime ETags as configuration CAS values;
- secret-reference validation and representative secret leak rejection;
- direct and review workflows, including remote outage behavior;
- external RBAC/security commits under both attested-identity and repository-writer-as-SuperUser
  policies, including prevention of self-authorizing commits;
- crash recovery at intent, commit, ref-update, audit, and materialization boundaries;
- invalid-head last-known-good behavior and projection rebuild from a commit;
- immutable build provenance containing both relevant Git revisions;
- disable/unauthorize/delete/rename/custom-parameter/upgrade races against assignment and reconnect;
- structured audit events linked to their Git commits without leaking request credentials;
- proof that the controller rejects database-only desired-state mutation paths.

## 20. Collaboration contract

- Domain experts define schemas and semantic validation; the Git / Versioning Expert approves identity,
  layout, canonicalization, migration, and mutation behavior.
- The REST Expert defines resource routes using the separate configuration revision, observation
  revision, runtime version, idempotency, conflict, and operation contracts. UI uses those routes and
  displays pending/active revision state without mixing their ETags.
- User Roles and Admin / SuperUser experts define authorization and first-run semantics without putting
  credentials in Git.
- Logs Expert defines audit durability, redaction, rotation, retention, and export.
- Platform Expert verifies filesystem, locking, Git, credential, and line-ending behavior on all
  supported hosts.
- Agent API / SDK Expert consumes materialized policy and deployment references and owns delivery,
  exact-session acknowledgement, drain/restart, and capability cancellation semantics required by
  the agent-property interlock table; agents never become Git writers.
- Reconciliation Lead owns apply ordering, last-known-good snapshots, drift reporting, and safe runtime
  effects while preserving this document's revision boundaries.
- Docs Expert updates architecture decisions and the document map whenever an accepted implementation
  refines this proposal.

No expert may add a domain-specific write shortcut. Cross-domain changes are one candidate tree and
one commit, reviewed by every affected owner.

## 21. Open questions

1. D29 selects a narrow system-`git` adapter for the first managed-local implementation. Platform
   release evidence must still decide whether controller packages bundle Git or verify it as an
   explicit prerequisite; remote credentials/host trust remain a later adapter concern.
2. Which local secret backend is the cross-platform default, and which secret-reference URI shape can
   remain stable across later external backends?
3. Which Git forge adapters, if any, are included for creating review requests? The core contract must
   remain forge-neutral.
4. What repository schema compatibility window and migration tooling are required across controller
   upgrades?
5. Which portable signature/forge-attestation formats and identity mappings implement the external
   attested-identity policy first?
6. How long are mutation intents, audit events, ID tombstones, and verified last-known-good snapshots
   retained, and how are they backed up together?
7. How should a project repository identity survive URL changes and mirrors without accidentally
   treating a different repository as the same authority?
