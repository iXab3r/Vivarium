# TeamCity Domain Design

> Status: **Accepted**
> Implementation: **Partial**
> Maintainer role: [TeamCity Expert](../roles/teamcity-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D3-D9, D13, D14, D17, D18, D22-D28

Numbered architecture decisions remain authoritative.

## D32 parity refinement

[TeamCity behavioral parity](teamcity-parity.md) is the required build-product acceptance contract,
including human diagnostic workflows and historical import/diff-import. D32 adopts C# Git definitions,
in-process Chiral composition and PostgreSQL as the new target; the SQLite/YAML sections below retain
the implemented foundation and earlier design detail until their explicit migration. New catalog
authoring must not create a competing YAML authority. Reusable catalog queueing supports visible
waiting for compatible Agents even when none are currently known; the current payload-matrix
fail-fast rule remains a named transitional API behavior.

## Purpose

Vivarium uses the proven TeamCity work model for reusable automation that can run on heterogeneous
physical agents and, later, provider-managed machines:

```text
Project
  -> Build Configuration
       -> Build (queued -> running -> finished)
            -> ordered Step Runs
```

The model is intentionally useful for compilation, packaging, tests, benchmarks, and other repeatable
jobs. Test matrices and normalized test results are important Vivarium extensions, not restrictions on
what a build may execute.

This document defines TeamCity domain semantics and the invariants handed to implementation owners.
It does not define host inspection or remote fleet management (AgentExplorer), scheduler race/delivery
mechanics, result ingestion, persistence transaction implementation, agent wire messages or deployment
(Agent API/SDK), generic REST conventions, UI components, Git provider implementation, or global
authorization roles.

## Current state

Phase 1 already has:

- strict `vivarium.yaml` parsing and immutable UTF-8 definition snapshots;
- deterministic payload archives and a files-in/process/files-out agent contract;
- matrix expansion into a parent build and ordered child cells;
- a durable global FIFO queue with compatibility checks and queue-wait deadlines;
- acknowledged assignment, restart-safe ownership, immutable assigned-agent provenance, and
  idempotent terminal results;
- durable parent/child cancellation with reconnect delivery;
- streamed logs, object-authorized raw artifacts, step outcomes, build-result pages, REST/SSE-backed
  CLI submission/watch/cancel, and a transitional scoped management gRPC adapter;
- durable bounded TRX report/test/occurrence projections with raw-artifact provenance and restart
  catch-up.

The current submission identifies a build configuration by project/name strings carried in a
submitted YAML snapshot. There is not yet a durable, Git-reconciled catalog of projects, build
configurations, VCS roots, templates, triggers, or dependencies. Configuration edits are not yet a
Git-first REST workflow. Detailed test-result REST/UI, JUnit, TEST/CRASH classification, and live
TeamCity service messages remain target work.

## Goals

- Copy TeamCity's project, build configuration, build, step, parameter, requirement, queue,
  cancellation, dependency, and result semantics where they fit Vivarium.
- Make Git the only source of truth for mutable TeamCity configuration from the first catalog slice.
- Make REST a first-class interface used by the UI, CLI, and external automation from the first
  mutation slice.
- Preserve reproducibility by freezing both configuration and source revisions into every build.
- Keep the executor payload-agnostic and cross-platform.
- Let one configuration expand across operating systems, agent properties, parameters, and repeats.
- Keep history understandable when configuration, source, agents, and matrices change.

## Non-goals

- Reimplementing the entire TeamCity plugin ecosystem or every runner in the first release.
- Treating AgentExplorer remote commands, process control, cleanup, file browsing, or software management
  as builds.
- Allowing database-only settings, UI state, or an admin-only internal API to become a second configuration source.
- Storing Git, package, license, or signing credentials in versioned YAML.
- Inferring authoritative test results from process exit codes or TeamCity service messages.
- Requiring Git to record runtime actions. Runtime actions belong to the durable journal and audit log.

## Domain model

### Stable identity

Every definition has an explicit immutable ID suitable for Git references, REST paths, dependencies,
and historical joins. Display names are mutable. Renaming a display name does not change identity;
changing an ID is modeled as delete-and-create unless a future explicit migration operation is
defined.

Identifiers are unique within these scopes:

- project ID: globally unique and immutable; project name and description are mutable;
- build configuration ID: globally unique and immutable, even when displayed inside one project;
  configuration name and description are mutable;
- step, trigger, requirement, VCS root, template, and dependency IDs: stable within their owning
  project or build configuration as appropriate;
- configuration revision ID: immutable identity of one validated, fully resolved configuration graph;
- matrix parent build ID: controller-generated identity of one atomic matrix submission;
- declared cell key: stable scenario ID plus zero-based iteration, independent of display name and
  eventual agent selection;
- child build ID: controller-generated identity of one execution attempt for one declared cell key;
- execution-target history key: immutable grouping identity derived from physical-machine or image
  provenance after assignment.

The identities serve different purposes and are never substituted for each other:

```text
ProjectId
  -> BuildConfigurationId
       -> ConfigurationRevisionId
            -> MatrixParentBuildId
                 -> DeclaredCellKey = (ScenarioId, Iteration)
                      -> ChildBuildId
                           -> ExecutionTargetHistoryKey after assignment
```

An explicit scenario has an immutable `ScenarioId` and mutable display name. A cross-product matrix
derives `ScenarioId` deterministically from the normalized axis IDs and values, never from localized
display text. `Iteration` distinguishes repeated children inside one submission.

`ConfigurationRevisionId` covers the product-settings repository ID, commit SHA, definition path and
content hash, plus the hash of the fully resolved project/configuration graph. A Git commit can contain
multiple configurations, so its commit SHA alone is not a configuration revision ID.

`ExecutionTargetHistoryKey` groups comparable executions without pretending ephemeral machine
instances are different scenarios. For a physical/enrolled target it is based on stable agent/machine
identity plus the immutable scheduling-parameter snapshot hash. For an image-backed target it is based
on immutable `ImageVersionId`; provider and concrete clone IDs remain provenance but are not the
longitudinal history key. The cell-history grouping key is
`(BuildConfigurationId, ScenarioId, ExecutionTargetHistoryKey)`. Configuration revision and repeat
iteration are recorded dimensions and change badges, not part of that grouping key.

### Project

A `Project` groups related build configurations and may have a parent project. It owns or inherits:

- display name and description;
- project parameters;
- Git VCS roots;
- templates, when templates are enabled;
- permission scope references;
- build configurations and child projects.

Project hierarchy exists in the data model from the catalog slice, but the first UI may expose only
one root level. Inherited settings are materialized into the validated projection and frozen into each
build; historical reads never recompute inheritance from today's project tree.

### Build Configuration

A `BuildConfiguration` is the reusable definition of one job. It contains:

- stable identity, display name, description, and enabled/paused state;
- one or more attached VCS roots and checkout rules;
- ordered build steps;
- parameters and allowed run-time overrides;
- explicit and runner-implied agent requirements;
- artifact publication rules and result-adapter declarations;
- matrix scenarios/axes and repeat policy;
- clean policy, queue-wait timeout, run timeout, concurrency cap, and priority class;
- triggers, dependencies, templates, failure conditions, and optional build features as their staged
  slices become available.

A configuration is not an execution. A `BuildConfigurationRevision` identifies the validated
settings repository, commit SHA, configuration path, content hash, schema version, and fully resolved
definition used to create builds.

### Build and Step Run

A `Build` is one immutable execution request for one build-configuration revision and one resolved
parameter/scenario set. It records:

- project ID and mutable-name snapshot, build-configuration ID and mutable-name snapshot,
  configuration revision ID, and normalized definition snapshot;
- controller-control repository ID/commit/path/content hash for the project binding used at
  admission;
- product-settings repository ID/commit/path/content hash and resolved-graph hash;
- trigger type, triggering principal, request ID, and creation time;
- exact source revision set for every attached VCS root, kept distinct from the settings revision;
- source verification state and immutable payload/archive hashes;
- initial and resolved parameters, with secret references but never secret values;
- matrix parent build ID, declared cell key, child build ID, and build-chain identity where
  applicable;
- queue position facts, deadlines, selected agent, machine/provider/image identity, execution-target
  history key, and immutable reported/custom parameter and capability snapshots;
- ordered step runs, logs, artifacts, test occurrences, statistics, failure classification, and
  cancellation reason;
- all timestamps needed to explain queue, assignment, execution, and terminal state.

Steps execute in declared order on the same assigned agent. A step has a stable ID, name, runner type,
runner settings, enabled flag, execution conditions, timeout, and execution policy. The first runner
type remains a portable process/command runner. Typed runners are adapters that compile to the same
small agent execution contract unless they require a separately negotiated agent capability.

Step execution policies preserve D14's distinction:

- `default`: run only while the build is successful;
- `even-if-failed`: run after success or an ordinary previous-step failure, but not after cancellation;
- `always`: run after success, failure, or cancellation where the cancellation protocol still permits
  an epilogue;
- disabled steps remain in the revision but do not run.

`always` must not collapse into `even-if-failed`.

## Git as the configuration source of truth

### Three distinct Git concerns

Vivarium must not conflate:

1. **Controller control repository:** owns Project identity, hierarchy/metadata and settings-repository
   bindings, including allowed branches/paths, trust policy and private credential references (D23).
   Fleet desired policy also belongs to its owning domain here; private Host/resource configuration
   uses Chiral under D35.
2. **Product settings repository:** owns bound product-scoped C# build definitions, project parameters,
   templates, steps, requirements, matrices, triggers and dependencies (D32/D36). These do not
   re-declare control-owned Project identity/metadata or authorize their own repository binding.
3. **Source VCS roots:** Git repositories and immutable revisions checked out or packaged as build
   input.

The product settings repository and a source VCS root may be the same repository or separate.
Today's Phase-1 `vivarium.yaml` beside tested code is transitional payload-submission evidence,
not the target catalog schema. UI/native/compatible REST edits author supported C# settings through
the same validation/Git activation path; exact runnable syntax/editability requires P0 #34's proof.
The controller control repository remains separate so a product repository cannot register itself,
replace its trust policy, or redirect its own credential reference. A product binding is not a
prerequisite to creating its control-owned Project. P0 #34 must prove blank-install Project creation,
BuildType creation while unbound, and a subsequent authorized binding with exact ownership/diffs;
unbound C# placement and final file/schema/SDK choices remain that proof's deliverables. See
[the representation proof](teamcity-rest-domain.md#c-authoring-is-a-pre-implementation-gate).

The Git/Versioning Expert owns the controller control repository mechanism and repository bindings;
the relevant domain expert owns each entry's schema. The TeamCity Expert owns product settings and
consumes a validated `ProjectBinding` projection. Credentials referenced by either repository remain
operational secret state, never Git values.

The unavoidable bootstrap exception is the first-run location, trust material, and credential needed
to open or initialize the controller control repository itself. The Admin/SuperUser and Git/Versioning
Experts own that ceremony. Once the repository is established, project bindings and controller policy
changes are Git-first; the bootstrap values do not become an alternate mutable configuration store.

Every build records all three provenance layers separately:

- `ControlRevision`: control repository ID, commit SHA, binding path, and binding content hash;
- `ConfigurationRevision`: product settings repository ID, commit SHA, definition path, definition
  content hash, resolved-graph hash, and configuration revision ID;
- `SourceRevisionSet`: one entry per attached VCS root with root ID, repository identity, commit SHA,
  checkout-rules hash, and verification state.

Recording both control and product-settings commits/paths/hashes makes historical interpretation
possible even after bindings or names change. Neither revision is inferred from the source revision
set.

The first catalog slice supports Git only. The domain names remain `VCS root` and `source revision`
because they describe TeamCity semantics, but no abstraction work for other VCS implementations is
required.

### Desired state and projection

Versioned files are desired state. PostgreSQL holds last-known-good projections of controller control and
product settings for fast reads, compatibility calculation, scheduling, and history. Each projection
retains its repository coordinates and content hashes and can be rebuilt from Git plus runtime
history.

Configuration reconciliation is:

```text
observe Git commit
  -> fetch exact bytes
  -> parse and validate the complete affected project graph
  -> resolve IDs, inheritance, templates, references, and schemas
  -> transactionally publish a new projection revision
  -> emit an audit event
```

An invalid or unavailable candidate does not partly apply. The last-known-good revision remains
active, and the validation/reconciliation error is visible through REST and UI. Deletion follows the
same reconciliation path; existing build history retains its frozen snapshots.

The Git/Versioning Expert owns clone/fetch credentials, locking, commit/push mechanics, polling or
webhooks, conflict resolution, repository health, and recovery. The Reconciliation Lead reviews both
control and product-settings state machines and their failure behavior. The TeamCity Expert supplies
product-settings validation and the collection-mode contract that records requested selections
separately from the immutable control/settings/source revisions actually resolved for execution.

### UI and REST edits

There is no direct `UPDATE build_configurations` authoring path. A project/configuration UI or REST
mutation targets the repository owning the changed fields: Project identity/metadata/hierarchy and
bindings use controller control; bound product-scoped settings use the product repository. Unbound
definition placement follows #34's proved representation, not a new authority. In either case the
mutation:

1. reads the active definition/repository revision and any explicit native workflow precondition;
2. validates the requested domain change;
3. renders a deterministic patch to the versioned representation;
4. commits by internal Git ref compare-and-swap, with remote push/review only under configured policy;
5. re-reads and validates the committed bytes;
6. activates the resulting projection revision;
7. returns the operation's native or compatible response at its required success/activation fence.

Internal ref CAS and recovery protect committed state; a CAS race may require rebuilding/revalidating
a candidate according to the operation contract. Explicit native version-checked workflows such as
AC-CFG-03 reject stale caller revisions. Default/compatible edits keep actual TeamCity concurrency
processing and headers; internal CAS does not impose universal caller-visible stale rejection.
Only an explicitly declared native asynchronous workflow may return a pending operation resource.
Compatible calls retain their reference response and completion fence, including 200/201 where
required; an unapplied/rejected candidate is not successful activation. Exact defaults/variants
remain #32/#34/#36 contract work.

Direct commits made outside Vivarium travel through the same validation and projection path. The UI
does not claim success until the commit is durable. Failed pushes or validation errors never leave a
server-only setting behind.

### Versioned and non-versioned data

Control-owned Project identity, hierarchy, names/descriptions and binding policy are versioned desired
configuration. Product-scoped settings include configuration identity/names/descriptions, project and
configuration parameters, VCS root references, steps, requirements, matrices, artifact/result rules,
clean/queue policies, triggers, dependencies and templates. Exact representation remains #34 work.

The controller control repository versions Project declarations and project-to-settings-repository
bindings/trust policy, not a duplicate product step/parameter store. A change in either repository
yields a new recorded provenance revision even if the other repository is unchanged.

Git contains references to credentials and secrets, never their values. Repository credentials,
secret material, user sessions, agent credentials, runtime queue/build state, logs, artifacts, and
audit events are operational state. Their mutations are audited but are not configuration commits.
The owner experts must define how operational credentials and secret values are stored and rotated.

### Configuration activation and builds

Support the reference immediate settings capture and later collection modes. At enqueue preserve
requested branch/revision/settings mode and other explicit inputs separately from resolved execution
selections. Immediate mode captures its required revisions then; later mode collects at the
contract-defined assignment/start boundary. A queued request does not imply that unresolved settings
or a preferred Agent are already frozen/assigned. Exact 2026.1.3 defaults, collection timing and
branch-specific variants remain #32/#34/#36 work, not a universal admission-time rule.

At the selected collection boundary, retain the validated control/settings revisions, effective
definition/hash and source revisions actually used as immutable execution provenance; snapshot Agent
facts at actual assignment. Later edits never rewrite those actual selections or historical output.
Explicit settings/source selections must be authorized; an untrusted source branch does not gain
authority to change privileged steps, secret references or execution policy by opening a pull request.

### Source revision and verification state

A settings revision is always a controller-fetched, validated Git commit. Product configuration has
no dirty or unverified mode. Source input is a different fact and carries one of these states:

- `verified-clean`: the controller resolved the immutable commit and produced the payload, or verified
  that the submitted payload manifest/tree matches that commit under the recorded checkout rules;
- `dirty`: the payload differs from a known base commit; provenance records the base commit, payload
  archive hash, deterministic manifest/tree hash, and patch hash when available;
- `unverified`: the controller has the immutable payload/archive hash but cannot prove a Git commit or
  clean tree corresponding to it.

Automatic VCS and schedule triggers accept only `verified-clean` sources. `dirty` and `unverified`
submissions are explicit manual/REST modes guarded by permission and rendered with permanent history
badges. They are never labeled or grouped as executions of an asserted clean source revision; history
retains their distinct source-provenance state. The actual payload hash, not the asserted commit,
remains the execution truth.

## VCS roots and checkout

A Git VCS root defines:

- stable ID and display name;
- fetch URL and default branch;
- allowed branch specification;
- checkout and trigger rules;
- submodule and large-file policy;
- credential reference;
- server-observed repository identity.

Retain the immutable source selections actually executed for every attached root. P0 freezes the
reference timing for default branch selection, explicitly requested revisions, queued work and chain
synchronization; do not turn the old "always resolve before queueing" example into a different
TeamCity source-selection policy. Execution must not silently substitute a different revision.

The current CLI payload/YAML path is legacy evidence. R0 may use its declared seeded payload to prove
real execution. R2 must support the source-based C# Compile and the checkout modes/toolchains required
by the inventoried configurations. SDK/Git-equipped CI agents and pristine payload-only agents are
both valid machine roles. Agent-side checkout is not deferred merely because another machine lacks
Git; requirements and capability negotiation make that distinction explicit.

Multiple attached roots, checkout rules, submodules and large-file behavior follow the reference
profile. A bounded early slice may reject cases not delivered yet, but cannot claim a migrated family
accepted while required roots or checkout behavior remain unsupported. Never silently drop a root.

## Parameters

Parameters are typed name/value definitions with stable source and sensitivity metadata. The model
distinguishes:

- configuration parameters used while resolving settings;
- `env.*` parameters passed to the process environment;
- system/tool parameters consumed by typed runners;
- predefined immutable build parameters;
- agent reported/custom parameters used for matching and optionally exposed read-only to a build;
- secret references resolved only for an authorized execution.

The initial precedence, from lowest to highest, is:

1. predefined defaults;
2. inherited project parameters;
3. template parameters, once templates ship;
4. build-configuration parameters;
5. trigger overrides;
6. explicit run overrides;
7. resolved matrix/scenario parameters.

Enforced template values, when introduced, are a separately documented exception and override normal
user-editable layers. Duplicate keys within one layer are validation errors. Expansion cycles and
unknown required references fail validation or queueing before assignment. The build stores initial
and resolved values, redacting secret values from REST, logs, Git, and durable definition snapshots.

## Agent requirements and capabilities

A requirement is a stable-ID expression:

```text
agent parameter  operator  optional value
```

The first operators are `exists`, `does-not-exist`, `equals`, `not-equals`, `starts-with`,
`contains`, `matches`, and ordered numeric/version comparisons. Multiple requirements are ANDed.
OR is expressed through separate matrix scenarios/configurations until an explicit expression design
is accepted.

Compatibility is pure and explainable: the REST model returns every failed requirement with the
observed or missing parameter. Static compatibility is distinct from current eligibility such as
connected, authorized, enabled, healthy, reconciled, idle, or exclusively leased.

Runner and clean-policy needs may add implicit capability requirements, but these are shown alongside
explicit requirements. Capability IDs and protocol advertisement are negotiated with the Agent
API/SDK Expert. TeamCity never reaches into AgentExplorer to perform host mutation as a prerequisite for
a build.

## Matrices

The following D14 payload-matrix identity/admission/rerun mechanics describe the transitional native
workflow. They do not choose compatible catalog collection defaults or promise a new execution for
every enqueue under reference queue optimization; #32/#34/#36 own that mapping.

A matrix belongs to a build configuration revision and expands a run into:

- one durable composite parent;
- one ordinary child build per scenario/axis combination and repeat iteration.

Expansion is deterministic and atomic. Every child freezes the same configuration revision plus its
own resolved parameters, requirements, stable scenario ID and name snapshot, iteration, source
revision set and verification state, queue deadline, and idempotency identity.

D14's admission rule is explicit: before creating a matrix parent, children, or queue rows, the
controller evaluates every selected cell against durable parameter/capability snapshots for known
agents. Potential future provider capacity does not satisfy this current D14 check until a numbered
architecture refinement defines provider-backed static compatibility. If any cell has no statically
compatible known agent, the entire atomic submission is rejected with per-cell failed-requirement
explanations and no build IDs are created.

Connected, authorized, enabled, healthy, idle, and lease state are current eligibility rather than
static compatibility; a statically compatible cell with no currently eligible capacity is accepted and
waits in the queue.

The parent aggregates lifecycle, pass rate, and matrix presentation. Results, logs, artifacts, and
assigned-agent provenance remain owned by children. Each child is addressed by its declared cell key
inside the parent and by its globally unique child build ID. A later rerun links new IDs to the prior
parent/child but never alters original matrix history.

## Queue, execution, cancellation, and rerun

The existing durable global FIFO and first-runnable scan are transitional implementation evidence.
Adopted catalog operations follow reference queue processing/optimization; versioned policy includes:

- queue-wait deadline;
- per-configuration concurrency cap;
- priority class with starvation guardrails;
- clean policy and run timeout.

Queue and assignment state is operational, not Git configuration. Every transition is persisted
before it is projected to clients or agents. Explicit native keyed queue workflows retain identified
retry receipts. Default/compatible enqueue follows reference retry/optimization/reuse behavior;
intentional commands are not collapsed by body hash, and do not universally require distinct Build
IDs or executions. Freeze returned optimized/replaced identities and links in #32/#34/#36.

The transitional native matrix cancellation command is distinct from deletion. It records principal,
reason, timestamp, and first accepted request. Queued children finish `CANCELLED`; running children
persist cancel intent and receive retry-safe fenced delivery until a terminal result is accepted.
This internal delivery contract does not determine compatible repeated/after-finish HTTP outcomes.
POST cancellation, DELETE and cancel/requeue follow their actual frozen TeamCity contracts, including
retention, replacement identity and completion. The [reference findings and probes](../../.workspace/workstreams/coherent-refactor/reference-probes.md)
keep those unobserved effects and settings-capture defaults explicit. Scheduling, Persistence and
Agent API implement the claim/delivery/reconnect races required by the adopted contract.

### Rerun modes

There is no ambiguous `rerun` command. A client chooses one of two modes:

- `rerun-original` creates a new matrix parent build ID and new child build IDs from the exact frozen
  control revision, configuration revision, source revision set and verification state, payload hashes,
  declared cell definitions, and run parameters of the selected parent or child. It may select a new
  compatible execution target, but cannot silently fall back to current configuration/source. If a
  required retained payload or settings snapshot is unavailable, the request fails explicitly.
- `rerun-current` creates a new submission from the current validated control binding and current
  product-settings revision, then resolves current source revisions. It retains `rerunOf` links and
  requested stable scenario IDs, but performs fresh matrix expansion and D14 admission. A removed or
  renamed-with-new-ID scenario is a validation error rather than an accidental different run.

Both modes are audited, pass through normal authorization/admission, and record their new provenance.
The UI must label them as distinct actions, for example **Rerun same revisions** and **Run current
configuration**.

### Matrix parent state and outcome

While any child is nonterminal, the parent remains nonterminal and exposes the worst outcome observed
so far only as progress. Once every child is terminal, the parent outcome is deterministic:

1. `INFRASTRUCTURE_FAILED` if any child is infrastructure-failed;
2. otherwise `FAILED` if any child failed, retaining all child failure classes and summarizing
   `CRASH` before `TEST` before unclassified process failure;
3. otherwise `CANCELLED` if any child was cancelled;
4. otherwise `SUCCEEDED`.

Thus a cancellation request does not erase a test or infrastructure failure that already won for a
child, and a mixed failed/cancelled matrix remains failed. The parent's cancel intent and reason remain
visible metadata regardless of aggregate outcome. Empty matrices are rejected at validation and never
need an outcome.

## Results and historical truth

One Build Results resource accumulates information from queue through completion. Its stable sections
are:

- overview and status text;
- settings Git commit and source changes/revisions;
- queue and agent provenance;
- ordered step runs and bounded logs;
- parameters with secret redaction;
- artifacts;
- normalized tests and statistics when adapters exist;
- dependencies/build chain when enabled;
- audit-relevant trigger, cancel, and rerun information.

Raw artifacts are authoritative input to controller-side result adapters. TeamCity service messages
provide live progress only because payload output is untrusted and may be forged, interleaved, or
truncated. Failure classification follows D9 and remains distinct from cancellation.

The TeamCity domain defines which build/step owns each result, the accepted lifecycle states, aggregate
outcome rules, immutable provenance, and REST projection. The Results Expert implements ingestion,
artifact validation, service-message parsing, test adapters, statistics extraction, and their
idempotency/backpressure behavior. The Persistence Expert implements the transactions that make the
first accepted terminal result and its manifests durable.

Retention may remove configured historical payloads according to policy, but a retained build's
identity and provenance must never silently change. If a referenced blob has expired, REST reports an
explicit expired/unavailable state rather than returning another build's content.

## Triggers, dependencies, and templates

These features are staged to protect the small execution core.

| Stage | Feature | Required contract |
|---|---|---|
| Current | Manual/CLI submission | Frozen YAML and payload, matrix, queue, cancellation, raw results |
| Catalog foundation | Git-reconciled projects/configurations and REST run endpoint | Stable IDs, settings commits, optimistic edits, last-known-good projection |
| VCS integration | Git roots and VCS trigger | Immutable source revision selection, polling/webhook reconciliation, branch/trust policy |
| Scheduling | Schedule trigger | Time zone, missed-fire policy, deduplication, pause behavior, audit event |
| R2 catalog | Project inheritance and templates | Stable member IDs, reference merge/override rules, resolved snapshot |
| Chains | Snapshot dependencies | DAG validation, shared source revision set, cancellation and failure propagation |
| Chains | Artifact dependencies | Immutable producing build selection, explicit artifact rules, provenance |
| Advanced | Trigger/dependency customization and branch-specific settings | Trust model, parameter precedence, reproducible revision choice |

Snapshot dependency is the TeamCity source-revision relationship; it must always be labeled
`build snapshot dependency` in contexts where a VM snapshot rollback could otherwise be confused
with it. Artifact dependency transfers selected immutable outputs and does not imply ordering unless
paired with a snapshot dependency.

R2 catalog authoring includes the required template/inheritance behavior. P0 #34 proves the editable
representation; #47/#48 freeze and accept its reference variants. Transitional YAML anchors/copy-paste
do not substitute for those semantics. Actual resolved inheritance remains execution provenance.

## REST contract handed to the REST Expert

REST is not a later wrapper over gRPC. The TeamCity application service must support these resources
from the catalog foundation:

- projects and project hierarchy;
- build configurations and immutable configuration revisions;
- VCS roots and validation state;
- configuration-change operations that produce Git commits;
- configuration compatibility and compatible/incompatible agent explanations;
- builds, matrix children, queue state, step runs, results, logs, tests, artifacts, and source changes;
- explicit commands to queue, cancel, and rerun builds;
- triggers, templates, and dependencies when their domain slices ship.

Behavioral requirements:

- JSON representations with stable IDs and links;
- pagination/filtering for collections and resumable live updates where needed;
- ETag/`If-Match` for explicitly version-checked native configuration workflows;
- idempotency keys for explicitly keyed native commands, with reference retry/optimization handling
  for adopted TeamCity operations and no mandatory new compatible-client headers;
- consistent distinction among validation, conflict, authorization, not-found, and transient
  reconciliation failures;
- config mutations expose the operation's required response/success fence; only declared native
  asynchronous workflows return pending operations, while compatible responses keep reference shape;
- runtime mutations return the durable resulting state and audit correlation ID;
- no secret values in requests echoed to logs or responses;
- UI and CLI call the same contract as external clients.

The Vivarium REST Expert owns route spelling, version negotiation, common envelopes, problem details,
authentication mechanics, and OpenAPI. This document owns the TeamCity resource semantics those
routes expose.

## Audit events

The TeamCity domain emits bounded structured events for:

- settings revision observed, validated, rejected, activated, or superseded;
- UI/REST configuration edit requested, Git commit attempted/succeeded/failed, and reconciliation
  completed;
- build queued, dequeued, claimed, accepted, started, cancel-requested, canceled, finished, retried,
  or expired;
- agent compatibility/eligibility reason selected at assignment time;
- artifact publication and result-adapter success/failure;
- trigger fired/skipped and dependency promoted/resolved.

Events include timestamp, principal or service identity, correlation/request ID, entity IDs, old/new
settings commits where applicable, outcome, and a bounded reason. They do not duplicate stdout,
artifacts, complete YAML, environment maps, parameters, or secrets. The Logs Expert owns sinks,
retention, indexing, rate limits, and redaction implementation.

## Security and trust

- A user who can modify settings Git can change code executed by agents. Project modification is a
  privileged permission and untrusted pull-request branches cannot automatically supply privileged
  settings.
- VCS credentials and secret values are referenced, never committed or returned.
- Run permission does not imply configuration edit, agent administration, AgentExplorer operation, or
  secret-view permission.
- Configuration validation must enforce path, artifact, parameter, and runner constraints before a
  revision becomes active.
- Build definitions, source archives, logs, service messages, and artifacts are untrusted inputs even
  when their project is authorized.
- Every mutating REST request and Git-backed reconciliation result is attributable in the audit log.

The User Roles and Admin/SuperUser Experts define the exact TeamCity-compatible permission matrix and
first-login flow.

## Cross-domain handoffs

### Agent API/SDK Expert

Owns build-assignment wire contracts, capability advertisement, step execution, progress, cancellation,
result acknowledgement, reconnect behavior, deployment, and upgrade compatibility. New runner or
checkout needs are submitted as versioned capability requests; the TeamCity domain does not edit the
agent protocol by itself.

### Scheduling and Persistence Experts

The Scheduling Expert implements queue scans, compatibility/eligibility application, capacity
arbitration, claims, leases, expiry, assignment coordination, cancellation delivery, and race
resolution. The TeamCity domain supplies lifecycle semantics, priority/deadline policy, compatibility
inputs, and the atomic outcomes that implementation must preserve.

The Persistence Expert implements PostgreSQL schema, constraints, transaction boundaries,
migrations, and restart restoration. The TeamCity domain declares which facts must become durable
atomically; it does not prescribe repositories or SQL transaction layout.

### Results Expert

Owns ingestion and validation of terminal results, step records, logs, artifact manifests, service
messages, test formats, and statistics. The TeamCity domain owns entity relationships, historical
meaning, aggregate outcomes, and the public result projection.

### AgentExplorer Expert

Owns host inventory and out-of-build operations such as process/port inspection, files, remote
commands, cleanup, and software management. TeamCity consumes shared agent facts and the common
exclusive-capacity decision, but AgentExplorer operations never appear as builds.

### Git/Versioning Expert and Reconciliation Lead

Own repository mechanics and review the Git-to-projection state machine. The TeamCity domain supplies
the schema, validation rules, immutable revision requirements, and audit events.

### REST and UI Experts

REST exposes the canonical behavior. UI builds TeamCity-like project/configuration/queue/build-results
screens on that API and shows Git commit, pending reconciliation, conflict, and validation states. UI
must not call internal repositories or business storage directly.

### Platform and Logs Experts

Platform reviews checkout, shell/path/environment, process trees, signals, file modes/symlinks, and
artifact collection on Windows, Linux, and macOS. Logs defines bounded build/audit pipelines and
redaction.

## Invariants to test

- A configuration cannot become active without a durable, validated Git commit.
- Replaying reconciliation for the same commit is idempotent.
- An invalid commit leaves the prior projection and schedulable revision unchanged.
- Explicit native version-checked stale edits conflict; compatible/default edit races follow the
  frozen reference processing while internal ref CAS/recovery preserve coherent accepted revisions.
- Immediate and later collection modes preserve requested selections and freeze actual settings/source
  provenance at their respective contract-defined boundary, not universally at queue admission.
- Queueing records distinct control, configuration, and source provenance; dirty/unverified source
  state can never masquerade as `verified-clean`.
- Later edits, renames, deletes, agent changes, or reconciliation do not rewrite build history.
- Matrix expansion is deterministic and all-or-nothing.
- Transitional D14 payload-matrix submission rejects a statically impossible selected cell before IDs
  or queue rows exist; reusable catalog queueing retains D32's wait-for-compatible-capacity behavior.
- Stable IDs survive mutable-name changes; matrix parent IDs, declared cell keys, child build IDs, and
  execution-target history keys are never conflated.
- Compatibility explanations are deterministic for the same requirement and agent snapshots.
- Queue expiry, assignment, cancellation, reconnect, and terminal result races have one durable winner.
- A parent cancellation cannot alter an already terminal child.
- Parent outcome precedence is deterministic for every mix of success, cancellation, test/crash
  failure, and infrastructure failure.
- `rerun-original` never resolves current settings/source, and `rerun-current` never claims original
  provenance.
- REST and CLI retries with the same idempotency key do not duplicate configuration commits or builds.
- No secret value is serialized into Git, audit events, definition snapshots, or REST responses.

## Prior-art evidence

The design borrows these specific behaviors from current official TeamCity documentation:

- Projects own build configurations; a build configuration captures sequential build steps plus
  parameters, triggers, features, and agent requirements:
  [Creating and Editing Projects](https://www.jetbrains.com/help/teamcity/creating-and-editing-projects.html),
  [Creating and Editing Build Configurations](https://www.jetbrains.com/help/teamcity/creating-and-editing-build-configurations.html),
  and [Configuring Build Steps](https://www.jetbrains.com/help/teamcity/configuring-build-steps.html).
- Requirements use `parameter operator [value]` and expose compatible/incompatible agents:
  [Configuring Agent Requirements](https://www.jetbrains.com/help/teamcity/cloud/configuring-agent-requirements.html).
- Parameters have project/configuration/run/agent sources and explicit resolution precedence:
  [Configuring Build Parameters](https://www.jetbrains.com/help/teamcity/configuring-build-parameters.html).
- VCS roots describe repository, branch, authentication, and checkout behavior:
  [Configuring VCS Roots](https://www.jetbrains.com/help/teamcity/configuring-vcs-roots.html).
- Versioned settings may be one-way or two-way; UI changes can be committed as the acting user,
  incoming commits are validated before activation, and builds can freeze settings from VCS:
  [Storing Project Settings in Version Control](https://www.jetbrains.com/help/teamcity/storing-project-settings-in-version-control.html).
- Templates share settings across configurations and project hierarchy:
  [Build Configuration Template](https://www.jetbrains.com/help/teamcity/build-configuration-template.html).
- Snapshot dependencies order a chain around the same sources while artifact dependencies transfer
  outputs:
  [Common Dependency Concepts](https://www.jetbrains.com/help/teamcity/build-dependencies-setup.html).
- VCS triggers group detected changes, support a quiet period, and require an explicit trust model for
  untrusted contributors:
  [Configuring VCS Triggers](https://www.jetbrains.com/help/teamcity/configuring-vcs-triggers.html).
- Queueing/cancellation are explicit REST operations, and build results aggregate status, changes,
  logs, artifacts, parameters, tests, and dependencies:
  [Start and Cancel Builds](https://www.jetbrains.com/help/teamcity/rest/start-and-cancel-builds.html)
  and [Build Results Page](https://www.jetbrains.com/help/teamcity/build-results-page.html).
- TeamCity's REST API exposes projects, build configurations, builds, field selection, and paginated
  collections:
  [TeamCity REST API](https://www.jetbrains.com/help/teamcity/rest/teamcity-rest-api-documentation.html).

Internal Git persistence for mutable catalog configuration is an implementation choice. Externally
observable TeamCity Versioned Settings modes and handling remain reference-compatible; internal Git
storage does not require users to enable external repository synchronization. UI authoring uses the
same C#/Git validation and activation path, without a separate UI-only database settings authority.
Exact unresolved dialect and mode variants remain pinned-reference contract work.

## Open questions

1. Which proved C# file/managed-source representation supports the required UI/native/compatible
   mutations and user-authored/computed fields? P0 #34 owns the edit/read-after-write proof. C# is
   accepted; YAML-only catalog selection is superseded. Preserve transitional Phase-1 submission
   behavior until its explicit migration/replacement is verified; it is not a second catalog authority.
2. Which forge adapters, if any, implement the optional remote review-branch policy after the
   managed-local direct-commit baseline?
3. What author/committer identity and signing policy is required for controller-created commits?
4. Which settings changes are allowed from untrusted source branches, and which always use the trusted
   default settings revision?
5. Is controller-side Git checkout/archive sufficient for the first VCS slice, and what repository
   size/submodule/LFS limits are acceptable?
6. Which TeamCity parameter names and precedence rules are copied verbatim, and which are normalized
   to existing `VIVARIUM_*` environment variables?
7. What retention guarantee keeps controller-control and product-settings commits, source revisions,
   dirty-source evidence, and payload blobs fetchable for the lifetime of a retained build when
   upstream Git history is rewritten?
