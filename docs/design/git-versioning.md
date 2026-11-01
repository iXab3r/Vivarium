# Git-backed desired settings

> Status: **Accepted target; partial legacy implementation**
> Maintainer: Git/Versioning Expert
> Decisions: D17, D23, D29, D32–D37 in [ARCHITECTURE](../ARCHITECTURE.md)

## Scope and authority

Git owns versioned **build and fleet desired settings**, not all mutable application data. This
document replaces the former Git-RBAC layout and first-administrator Git saga as target contracts.
Historical implementation evidence is retained below; it does not define new authorities.

| Data | Authority | Mutation path |
|---|---|---|
| Projects, C# build definitions/templates/parameters/requirements/triggers, versioned fleet properties and policy, retention policy | Validated Git revision | Shared Git mutation/reconciliation service |
| Users, external bindings, editable roles/permissions, groups, scoped assignments, framework credentials | PostgreSQL application data | Shared identity/authorization service and audit |
| Connections, queue/build/lease/operation state, observations, imports, tests, manifests and audit | PostgreSQL business data | Owning application service |
| Host/database/Identity/Storage resource configuration and private connection secrets | Private Chiral managed settings | Node validation, CAS and saved/active restart lifecycle |
| Large log/artifact/payload bytes | Storage, referenced by PostgreSQL | Authorized data plane and owning service |

Database projections of Git-owned settings are rebuildable indexes. Identity, history and audit are
not rebuildable from Git. Credential issuance and agent authorization are audited security actions;
do not turn them into synthetic settings commits. Agent enablement/custom properties remain
versioned desired settings. Stopping a build is a separate action, not an enablement edit.

## Repository ownership and initialization

A controller settings repository owns Project identity/hierarchy/metadata, settings-repository
bindings/trust policy and fleet desired policy (D23). A bound product repository owns product-scoped
C# build definitions, project parameters and templates, without re-declaring Project control identity
or authorizing its own binding. They may share physical storage, but repository
identity and revision provenance remain distinct. An operation spanning repositories cannot promise
one atomic Git commit. Neither repository contains identity users/groups/roles/grants, external-login
bindings or credential bytes. Settings-repository bindings and private credential references remain
control-owned declarations; identity/RBAC authority remains PostgreSQL.

Managed-local direct Git on `main` is the deterministic default. It requires no external Git host;
this does **not** mean the new server has no PostgreSQL/Storage prerequisites. Initialize only the
reserved configuration tree; never overwrite unrelated content or silently adopt incompatible data.
Adding a remote alone does not change authority. Remote authority, branch and direct/review policy
are explicit operator choices stored with the Chiral-owned resource settings needed to locate Git.

The settings tree reserves `.vivarium/repository.yaml`, project declarations, agent declarations,
fleet policies and provider/image declarations. C# definition paths and their editable representation
are finalized by the early [REST/domain contract](teamcity-rest-domain.md). Existing YAML parsing is
legacy support; arbitrary C# cannot be represented as editable forms by wishful round-tripping.
No target `.vivarium/rbac` tree is defined. Non-secret versioned policy is distinct from resource
connections and their private settings. Final per-kind schemas remain a P0 implementation input.
A Project can be declared before it has a product/settings binding. #34 must prove the blank-install
Project → unbound BuildType → authorized binding sequence and independently expected control/product
diffs; exact unbound C# placement, file schema and SDK API stay unresolved until that proof.

## Mutation and activation

REST, UI, CLI and imports use the same application mutation service for Git-owned fields. It:

1. Authorizes the actual actor against current PostgreSQL permissions and records a durable intent.
2. Reads the internal repository/base revision and any explicit native caller precondition, then
   builds an isolated complete candidate tree.
3. Validates paths, schema/definition versions, stable IDs, references, domain rules, secret-reference
   fields, platform claims and the actor's authority for the complete change.
4. Creates one commit and advances the authoritative ref by compare-and-swap. Reviewed mode waits
   for the authorized merge/fetch; local branch creation alone is not activation.
5. Reconciles only the accepted authoritative revision and records its apply outcome/projection.

No consumer sees a half-written tree. A project and its versioned policy can share one commit;
access grants cannot be included as a falsely atomic Git+identity transaction. Proposals, accepted
heads and active/last-known-good revisions are distinguishable. Invalid external revisions retain
the verified last-known-good settings with actionable diagnostics; they do not rewrite the branch.
Caller-visible success must follow the relevant native or TeamCity-compatible activation fence.
The compatible facade must not require new headers or asynchronous behavior from unchanged clients.
Internal CAS/recovery does not imply universal stale-caller rejection: explicit native version-checked
workflows enforce their preconditions; default/compatible edits retain reference concurrency handling.

Every build pins the control and tested repository identities/revisions, definition path/hash and
resolved settings actually used. Settings changes and renames do not rewrite historical runs. Template
and parameter resolution must preserve the TeamCity model, not add a parallel settings authority.

Git ref updates and PostgreSQL cannot form one physical transaction. Recoverable intent, stable
operation/commit correlation, ref CAS and idempotent reconciliation close that gap. Retry must
rediscover an accepted commit rather than create another effect. A committed but unapplied revision
is visible as such; a failed ref update never activates an orphan commit. Do not port SQLite's single
writer loop as a required PostgreSQL architecture.

## Trust and secrets

External author text is not proof of identity. Security-sensitive repository changes require the
configured attestation/authorized-actor policy, or the explicit policy that protected branch writers
are administrator-equivalent **for those repository-owned settings**. This never makes a Git writer
the reserved installation user or lets Git grant PostgreSQL roles. Code-execution policy is still a
security boundary. Pin repository identity and host trust; use normal HTTPS validation or provisioned
SSH host keys. Credentials never enter URLs, shell command text, commits, diffs or diagnostics.

Secret references may be versioned; values, password hashes, tokens and private keys may not.
Git validation cannot authorize a secret merely because its reference parses. Resolution checks
current access and the workload's intended scope. Native and compatible mutations share these rules.

## Reconciliation, rollback and degraded behavior

Use stable resource IDs, separate configuration revisions from runtime row versions, and keep the
active projection coupled to its accepted revision. Rollback creates a new validated commit; it
does not force-reset published history. Re-check current authorization on writes/replays, and record
the resulting Git revision only for configuration-derived actions. PostgreSQL role changes do not
depend on Git availability or require a settings revision to take effect.

Git failure with verified settings may preserve already-authorized runtime behavior under that
revision, subject to current account/permission and resource readiness checks. No verified catalog
means no catalog scheduling; diagnostics and resource repair remain distinct. Never invent a hidden
database-only settings override. Resource or account-storage failures follow their own readiness
contracts; a cached Git tree is not an authentication fallback.

Agent `enabled: false` blocks **ordinary automatic assignment** once active and lets current work
finish. Explicit suitable-agent custom runs retain TeamCity's disabled-agent behavior; see AC-AG-06
and TCM-GAP-001. Agent authorization, compatibility, health, ownership and target permissions remain
independent checks. Agent acknowledgements and delivery belong to the Agent API contract.

Audit records link actor, target, outcome, correlation/operation and applicable revisions without
secret values. High-volume output is not Git history. Retain references needed by builds, imports,
last-known-good settings and backups; garbage collection must not break their provenance.

## Evidence and unresolved details

Business acceptance lives in [configuration](../acceptance/configuration/catalog.feature),
[agents](../acceptance/agents/admission.feature) and [migration](../acceptance/migration/import.feature).
Use independent expected trees and real Git/PG boundary evidence where the claim crosses them.
Exercise stale edits, invalid revisions, dirty/case-colliding paths, denied changes, interrupted
commit/ref/audit/apply boundaries, replay, external trust, read-only degradation and source-independent
restore. Family links in the acceptance map are not execution coverage.

P0 must settle C# editable representation, per-kind schemas, supported Git packaging, remote trust
adapter, secret-reference shape, schema compatibility and retention of intents/tombstones/revisions.
These details cannot silently reintroduce Git RBAC or resource connections as Git authority.

## Legacy checkpoint evidence — not replacement requirements

The following records the pre-refactor implementation, including mechanisms D34/D35 retire.
It is retained for source/data/consumer disposition, not as an alternative target:

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
  protect the repository boundary. Repository-local line-ending policy is pinned to
  `core.autocrlf=false` and `core.eol=lf`; adoption rematerializes only a checkout proven clean under
  its previous effective policy.
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
