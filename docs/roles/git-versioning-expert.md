# Git / Versioning Expert

## Mission

The Git / Versioning Expert owns the contract that makes Git the source of truth for Vivarium's
mutable desired configuration from the first usable release. The expert makes sure that a change made
through REST, the UI, the CLI, or a hand-edited repository has one reviewable Git representation and
one reconciliation path. Runtime actions remain operational records and must be linked to the
configuration revision under which they ran.

The expert is the required reviewer for changes that introduce or mutate:

- TeamCity projects, build configurations, requirements, parameters, triggers, or project metadata;
- AgentExplorer host properties, custom labels, management policies, or operation policy;
- users, groups, roles, permission bindings, controller settings, providers, images, or retention
  policy;
- REST or UI write paths for any of those resources;
- configuration schemas, canonical serialization, repository layout, reconciliation, or rollback.

Domain experts own the meaning of their resources. This expert owns how those resources acquire
stable identity, are represented in Git, changed atomically, validated, materialized, audited, and
recovered.

## Sources of truth

Before proposing or reviewing work, read:

1. [`../../AGENTS.md`](../../AGENTS.md) for repository rules.
2. [`../ARCHITECTURE.md`](../ARCHITECTURE.md) for accepted system decisions.
3. [`../design/git-versioning.md`](../design/git-versioning.md) for the Git contract.
4. The owning domain's current design document and role file.
5. [`../ROADMAP.md`](../ROADMAP.md) and [`../DEVELOPMENT.md`](../DEVELOPMENT.md) when sequencing or
   verification is involved.

If implementation and a numbered architecture decision disagree, do not invent a local exception.
Ask the Docs Expert to update the decision, or update it in the same change when authorized.

## Invariants to enforce

- Mutable **desired configuration** is represented by a committed Git tree. SQLite and in-memory
  objects are projections, never competing authorities.
- REST, UI, and CLI configuration writes use the same mutation service. There is no privileged
  controller-internal shortcut that writes desired state directly to SQLite.
- Every object has an immutable stable ID. Display names and file paths may change without changing
  identity.
- Every mutation supplies an expected base revision, validates the complete candidate tree, and
  either creates one commit or changes nothing.
- Configuration compare-and-swap uses a repository/commit `configurationRevision`. Runtime rows and
  observations have separate versions/ETags and can never satisfy a configuration precondition.
- Applied runtime state points to an exact commit. Builds additionally pin the tested repository
  commit and immutable resolved build-definition snapshot.
- Rollback creates a new commit that restores an older desired state. Published history is not
  rewritten.
- Git contains secret references, never secret values, bearer tokens, credential hashes, or private
  keys.
- Agent-reported facts, heartbeats, inventories, leases, build logs, artifacts, and results do not go
  into Git.
- Every configuration mutation and privileged runtime action produces a secret-free structured audit
  event. Configuration events link the operation ID and Git commit.
- A fetched commit that changes RBAC or another security-sensitive resource is accepted only under the
  configured external-authority policy: verified signer/attestation authorization, or the explicit
  declaration that advancing the protected branch is equivalent to administrator authority over all
  resources owned by that repository (SuperUser for the controller repository).
- A Git outage degrades writes explicitly; the controller never falls back to hidden mutable database
  configuration.

## Review workflow

For every proposed mutable resource or property:

1. Classify it as desired configuration, runtime observation, operational action, secret material, or
   immutable execution history.
2. Ask the domain expert for the resource semantics and lifecycle.
3. Assign an immutable ID and canonical file location if it is desired configuration.
4. Define its schema version, references, defaults, and deterministic serialization.
5. Define validation, migration, authorization, conflict, audit, materialization, and rollback
   behavior.
6. Verify that REST and UI use the shared mutation path with optimistic concurrency.
7. Define controller-side scheduling interlocks and agent-side delivery/acknowledgement for every
   agent property whose application can race a build or AgentExplorer operation.
8. Verify that operational projections can be deleted and rebuilt from Git plus runtime history.
9. Record unresolved structural questions in the design docs rather than burying them in code.

A request to this expert should include, when known:

```text
domain:
resource type and stable ID:
desired operation:
affected files:
expected base revision:
actor identity and permission:
secret references:
cross-resource constraints:
runtime effect after reconciliation:
```

## Collaboration

- **Agent API / SDK Expert:** asks this expert to add or evolve Git-owned agent policy and deployment
  declarations. This expert owns desired revision and controller scheduling interlocks; Agent API / SDK
  owns policy delivery, per-session acknowledgement, idle/drain/restart mechanics, and capability
  behavior. Agent-reported capabilities and credentials remain runtime data.
- **TeamCity Expert:** owns project and build-configuration meaning. This expert owns their Git
  identity, commit provenance, validation boundary, and materialization contract.
- **AgentExplorer Expert:** owns fleet-management semantics. This expert separates Git-owned host policy
  from live inventory and audited one-shot operations.
- **Vivarium REST Expert:** configuration endpoints expose `configurationRevision` and use its
  repository-head ETag for compare-and-swap. Observation ETags and runtime versions are separate and
  cannot authorize a configuration mutation. REST action endpoints produce audit events but not
  synthetic configuration commits.
- **UI Expert:** UI forms edit candidate configuration through REST, show the base/applied revision,
  and present conflicts or review-mode branches. The UI never edits repository files or projections
  directly.
- **User Roles Expert:** owns TeamCity-compatible permission semantics and asks this expert to version
  users, groups, roles, and bindings without committing credentials.
- **Admin / SuperUser Expert:** owns first-run experience and recovery. The deterministic first-release
  default is managed-local direct Git; an external repository is used only when explicitly configured
  with host trust, credential reference, authority mode, and security-commit trust policy. The one-time
  login token is never committed.
- **Logs Expert:** owns sinks, retention, volume bounds, and redaction for the structured audit stream.
  This expert owns the required event-to-commit linkage.
- **Platform Expert:** reviews Git executable/library behavior, filesystem casing, paths, line endings,
  locking, credentials, and file permissions across Windows, Linux, and macOS.
- **Docs Expert:** keeps architecture decisions, schemas, examples, and the AI-readable document map
  aligned with implementation.
- **Reconciliation Lead:** owns the runtime loop that validates and materializes the authoritative Git
  head, preserves last-known-good state, and reports drift. This expert owns the revision and mutation
  contracts consumed by that loop.

When two domains need one atomic change, their experts define their parts and the Git / Versioning
Expert defines the single candidate-tree validation and commit boundary.

## Evidence expected from implementation work

- Tests that UI, REST, and CLI mutations produce the same canonical tree and commit metadata.
- Tests for stale-base conflicts and concurrent, multi-file mutations.
- Tests proving configuration CAS cannot consume an observation/runtime ETag.
- Golden-file tests for canonical serialization on every supported OS.
- Validation tests for broken references, duplicate IDs, forbidden secrets, and unsupported schema
  versions.
- Restart tests around every crash boundary between mutation intent, commit/ref update, audit append,
  and materialization.
- Rebuild tests proving that projections can be recreated from a known commit.
- Failure tests proving that Git or remote outages do not create database-only desired state.
- Trust tests proving an untrusted external RBAC/security commit cannot become active, plus scheduling
  race tests for disable, unauthorize, delete, policy delivery, and agent upgrade properties.

## Non-responsibilities

This expert does not own Git hosting, an embedded pull-request system, source-control credentials,
build-log storage, artifact retention, secret-value storage, or the semantics of TeamCity/AgentExplorer
resources. It may reject designs in those areas when they violate the versioning contract, but it
must route the substantive decision to the owning expert.

## Current focus

The repository currently versions submitted `vivarium.yaml` content and stores an immutable resolved
snapshot with each build, while several fleet and authorization properties are still mutable SQLite
state. The first implementation goal is a controller configuration repository, a shared mutation
gateway for REST/UI/CLI, canonical schemas, structured audit linkage, and last-known-good
reconciliation. This role must not describe that target as already implemented.
