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
fleet policies and provider/image declarations. D44 selects managed typed C# declarations and typed
edit declarations after untouched user computations; compiled declarations supply edit readback.
The control owner selects explicit validated source paths/bindings and owns the managed-local
unbound namespace. Arbitrary AST rewriting and editable database/JSON overrides are not source
authorities. The exact public SDK/file schema for each exposed field remains with the early
[REST/domain contract](teamcity-rest-domain.md). Existing YAML parsing is legacy support; arbitrary
C# cannot be represented as editable forms by wishful round-tripping.
No target `.vivarium/rbac` tree is defined. Non-secret versioned policy is distinct from resource
connections and their private settings. The selected early declaration/activation subset is specified
below; later per-kind schemas stay with their owners before affected exposure.
A Project can be declared before it has a product/settings binding. The real source/catalogue
studies prove bounded unbound C# ownership and destination preparation/binding; product activation
and authorized blank-install Project → unbound BuildType → binding remain separate implementation
gates. Their independently expected control/product diffs, public file schema and field SDK are not
accepted merely by source compilation.

D44 keeps synchronization policy separate from Git authority. An inherited Project follows the
nearest explicit source selection; explicitly disabled synchronization uses its own managed-local
control-Git C#. A bound Project uses the selected settings repository and revision. Parent source
presence cannot overwrite a child that independently selected a local owner. Parameter/template
inheritance is independent of this source-owner selection. Preparing another repository is not
activation or a second accepted owner. One-way, initialization/context restrictions and Build-settings
selection retain the frozen reference operation's handling; no generic read-only or success rule is
inferred from the source mechanism.
An explicit disable materializes the validated local declarations and stable identities into real
managed-local C#, retaining inherited relationships rather than flattening effective values. Later
local edits do not invoke an unselected product computation. Re-enabling/inheriting uses the selected
reference import/overwrite behavior. Desired accepted binding and currently applied/last-known-good
revision sets remain distinct during a failed or pending transition; that is not two writable owners.

For the selected R0 unbound smoke and PILOT C#/Git preparation, the
[early activation contract](csharp-activation.md) fixes source packaging, ownership, current
authority, candidate/accepted/active/LKG identities, commit/apply/readback fence and failed-handoff
recovery. The study namespace is experimental; the selected field semantics and one-owner/fence
rules are fixed. Full later SDK/field/remote/language variants remain due before their exposure.

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

D43 runs both compilation and evaluation of project-authored C# inside an isolated Linux/Docker
evaluator. Git acquisition and credentials stay with the trusted coordinator. Supply only bounded
committed source and typed non-secret context; no repository-controlled MSBuild/restore instructions,
network, Host mounts or secret values. Pin compiler/dependency image provenance with the selected
revision set and immutable validated plan. Evaluation is not activation; resource/timeout/output or
coordinator failure retains the previous active plan and reconciles the owned container. A private
temporary process under the Host user is not this boundary. Existing trusted source studies retain
their narrower claims; numerical budgets, actual hostile-input evidence and production integration
have their own gates.

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

The selected early contract settles C# ownership/typed edits, proved packaging, current trust/secret
references, schema rejection and retained intents/revisions for R0/PILOT preparation. Actual production
integration remains due there. Later field schemas, remote adapters, language/mode mutations and
tombstone variants retain explicit source-gap/full-owner obligations before their exposure. None may
silently reintroduce Git RBAC or resource connections as Git authority.

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
  New rendered documents/commit messages use LF independently of source checkout EOL. The exact
  uniformly CRLF-encoded form of the same canonical document remains readable for legacy Windows
  trees, with original byte hashes/provenance retained and no ref/history rewrite. Mixed endings,
  lone CR and noncanonical spacing/fields remain invalid; new mutations emit LF. Same-P0 #77 binds
  old-tree open/read/mutate/restart, original hash/byte and negative-ending evidence.
- SQLite migration v5 durably records revision sets/members, per-scope active and last-known-good
  pointers, idempotent mutation operations, and the Agent desired projection. Migration v6 adds bounded
  affected-target metadata, exact conflict revision/diff replay, and retryable repository-attempt
  evidence. Reconciliation validates committed bytes, applies projections atomically with their
  pointers/audit, preserves the last-known-good projection when a revision is invalid, blocked, or
  removes a currently materialized Agent document, and recovers pending committed work after restart.
  Desired activation and scheduler admission share an Agent lifecycle lease; a bounded hosted monitor
  converges external managed-local commits into the durable and live projections.
  Controller disposal joins canceled background workers before disposing their services/database,
  with a separate bounded drain after the shared Host stop budget. A completed in-process shutdown
  therefore releases the monitor's repository lock before the same data directory is reopened.
- The canonical v1alpha1 tree currently supports `.vivarium/agents/{id}.yaml`,
  `.vivarium/rbac/users/{id}.yaml`, and `.vivarium/rbac/bindings/{id}.yaml`. The authorization cut is
  deliberately restricted to direct User bindings to product-owned built-in roles. Setup uses the
  atomic multi-document mutation primitive so User and `SYSTEM_ADMIN` never tear; reconciliation
  materializes both with the exact revision before the private credential activates.
  `/api/v1/agents/{id}/settings` GET/PUT remains the only general desired-state management resource.
- Remote authority, review workflows, repository credentials, host-trust integrations, and broader
  desired-state schemas remain planned.
