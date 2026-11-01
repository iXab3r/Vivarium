# REST-driven shared domain contract

> Status: **Accepted direction — D36; detailed P0 contracts remain open**
> Implementation: **TeamCity facade not implemented**
> Owners: TeamCity, REST, Scheduling, Git, Persistence, Identity, Results/Logs, Test Steward

## Purpose

D37 requires this model and its wire projection to be continuously proved as a TeamCity superset.
Link changes to [business AC](../acceptance/README.md), TCM invariants and exact variant tests;
validate real state effects as well as responses. Every milestone preserves its implemented base
and checks relevant extension neutrality. See [evidence hierarchy](testing-model.md).

D32/D36/D37 adopt TeamCity's Build/Agent model. P0 reconstructs and adopts the verified frozen
TeamCity reference semantics for identities, queue/running/history continuity, reuse, cancellation and
independent Agent axes, then maps transitional Vivarium concepts onto that model. These are reference
facts to establish, not independent domain-design choices. Internal C#/PostgreSQL/Chiral representation
is implementation freedom only where observable behavior is preserved; extensions must remain neutral
without explicit opt-in. Unknown reference behavior remains a verification gap, not invented policy.
Native /api/v1 and compatible /app/rest project the same adopted model and services. Do not mirror
every wire DTO as a table or put business rules in HTTP handlers.
D40 makes the [subsystem lifecycle](subsystems.md) the implementation unit: use REST resources and
subpaths to assign primary/collaborating owners, attach REST subtasks to the real subsystem work,
then roll up subsystem, milestone and global evidence. Early REST constraints do not prescribe an
endpoint-by-endpoint implementation order or another domain authority.

The [wire contract](teamcity-rest-compatibility.md) owns protocol acceptance. The
[workstream breakdown](../../.workspace/workstreams/coherent-refactor/rest-breakdown.md) owns current
discovery evidence, open mappings and milestone allocation; no source/fixture count is API coverage.

The [business gap review](../acceptance/rest-gap-review.md) additionally inspects 30 public resource
pages and records 36 open decomposition findings. P0 must explicitly settle settings-freeze timing,
manual status versus original evidence, Versioned Settings versus C# Git authority, required
authentication forms, Agent compatibility policy, approvals and externally reported build lifecycle.
API metadata is a discovery input, not proof of installed-version semantics or an exemption for
licensing/plugin/cloud/node operations without an agreed product mapping.

## Resource identities and relations

The shared model must represent the following observable concepts without facade-only shadow state:

- Project hierarchy with stable external ID, parent/root identity and visibility/delegation boundaries.
- BuildType/configuration versus reusable template, ordered steps and identified settings collections:
  parameters, requirements, VCS entries, triggers, features and snapshot/artifact dependencies.
- Declared/owned settings versus inherited/effective values; stable item identities and template links.
- Build with immutable effective definition/settings revision, selected VCS revisions, runtime overrides,
  Agent provenance, step runs, queue/running/terminal state and independent outcome/problem data.
- Queue membership/admission/position/reason, cancellation/approval and history as views of execution,
  not unrelated opaque objects whose IDs cannot be reconciled across queue/watch/cancel/history.
- Build-chain/snapshot edges and artifact selection, composites/matrix membership and retry/reuse lineage.
- Stable Agent identity, connection/authorized/enabled/activity axes, facts/custom parameters and pools.
- VCS root configuration versus resolved VCS root instance, changes/revisions and checkout selection.
- Test identity versus per-build occurrence, build problem identity versus occurrence, mute/investigation
  scope, statistics and associations to exact retained runs.
- Artifact/log/source references, path trees, metadata and streamed bytes; identity/roles/groups/scoped
  assignments; audit and supported server operations.

P0 explicitly resolves the current native MatrixBuild/child UUID model into compatible Build and
composite identities. It must define what a one-cell run returns, which ID a client polls/cancels,
and whether a composite parent is independently visible. Existing project/configuration strings are
not sufficient persisted catalogue identities. Do not defer either mapping until imported history.

## IDs, state and protocol facts that shape persistence

Different IDs have different wire types. The installed CLI's enqueue parser accepts a positive safe
integer JSON Build.id; some test IDs exceed JS integer precision and must remain exact strings.
Project/configuration external IDs, numeric internal IDs, mutable build numbers and occurrence locators
are distinct. Native and imported origins require collision-free persisted mappings and valid local
hrefs, with compatible JSON representation per resource. Never apply a universal stringify-all rule.

Queue/running/finished state is separate from SUCCESS/FAILURE/other outcome and cancellation/failure
details; the installed lifecycle enum also declares deleted and unknown. Literal absent/null/status
behavior and optimized/reused old-ID resolution remain observation gaps, not a universal numeric-ID
continuity rule. Native TEST/CRASH/INFRA taxonomy must project truthfully into those contracts, including
failed-to-start, muted test failures and incomplete reports. Resource identity and terminal provenance
remain stable after configuration rename/edit, reconnect, import and retention changes.

Locators need parsed nested structure and resource-specific resolvers, not split-on-comma strings.
The real client uses nested id dimensions and base64url-encoded branch value conditions. Fields
projection, collection wrappers, counts/empty pages and numeric continuation affect query/projection
contracts. Use shared bounded query primitives only where observed contracts require them; no generic
OData/GraphQL engine or dynamically generated business model is needed.

## Command and authority contract

TeamCity defines the semantics of each adopted command, including default arguments, guards,
permissions, replacement/reset, state transitions, repeat/reuse behavior and observable completion.
Authority classification below determines how Vivarium persists that command; it cannot redefine its
handling. Native and compatible adapters dispatch the same semantic operation. An implementation
option is valid only if the reference response and resulting state remain consistent.

Classify each endpoint before implementing it:

- Versioned desired build/fleet settings: validate/edit the approved C# representation, commit to Git,
  activate the exact revision and project to PG. Preserve owned versus inherited/reset semantics.
- Identity/roles/groups/grants: authorized/audited live PG transactions under D34.
- Queue/run/cancel/reorder/approve/Agent runtime actions/triage: durable domain transactions and current
  policy, with delivery/ack/fence behavior in their owning services.
- Host/resource settings: Chiral schema/CAS/apply lifecycle under D35; no second settings authority.
- Bytes: authenticated bounded Storage transfer with PG-owned references and retention/access checks.

HTTP method alone does not determine authority, sensitivity or retry safety. Freeze absent/null/empty,
single-field versus collection replacement, inherited override/reset, ordering, create/copy/move/delete,
permission denial and read-after-write behavior per operation. TeamCity property bags identify real
runner/feature settings; accepting arbitrary names is not implementation of their semantics.

Git commit and PG activation are not one SQL transaction. Define operation identity, durable outcome,
failure/retry observation and the exact success boundary. An endpoint expecting 200/201 cannot silently
become native 202. Do not report an unapplied/rejected candidate as success. Existing clients need not
send new mandatory headers, and repeated intentional run requests cannot be deduplicated by body hash.

This does not prohibit TeamCity's reference queue optimization or suitable-build reuse. Keep those
domain rules distinct from transport retry identity; an unkeyed repeated request does not establish
either exactly-once intent or an obligation to create a different Build every time. See the
[proposed resolution of all 36 findings](teamcity-rest-recommendations.md).

## C# authoring is a pre-implementation gate

The [US-013 execution checkpoint](../../.workspace/workstreams/coherent-refactor/us013-p0-execution.md)
now supplies bounded real reference editing/source effects and a compiled C#/Git representation
experiment. It distinguishes demonstrated source editing/reevaluation from still-unproved real
Host activation, authorization and recovery. The complete P0 gate below remains due; the experiment
does not establish the final public SDK or all mode/collection semantics.

The CLI can edit individual properties, replace/reorder collections, reset inherited entries and
clone/move definitions. An arbitrary hand-written C# expression is not generally editable that way.
P0 must prove the managed/user-owned representation, precedence and supported computed-field rules.
The [reference findings](../../.workspace/workstreams/coherent-refactor/reference-probes.md) record
that 2026.1 Kotlin help permits UI edits after manual changes via patches, while one-way/context
and initialization modes have distinct UI restrictions. This does not select a Vivarium patch
architecture or imply REST rejection from UI behavior. Installed contextParameters declares GET/PUT;
generic Versioned Settings POST response metadata does not establish a numeric status/apply fence.
For each reference Versioned Settings/DSL mode, freeze which mutations TeamCity permits, rejects or
makes read-only, with its failure and completion behavior. Required mutable fields must have a real
path to deterministic validated source changes where that mode permits edits; do not assume arbitrary
computed C# must be editable or invent a native restriction where TeamCity allows the edit. Unverified
mode behavior or an unrepresentable required edit remains an explicit blocker. Do not hide a DB/JSON override or introduce a
general C# AST rewriter to claim 1:1 behavior.

#34's concrete proof starts with a blank installation: create Project P before any product binding,
then create BuildType C while unbound, then authorize a product-repository binding and read/edit C
through both surfaces. Show independently expected before/after control and product diffs for Project
identity/hierarchy/name/description, project parameters/templates, configuration identity/steps and
binding/trust/credential references, with one owner per field. Follow D23 control versus product
authority; do not make Project creation depend on a repository binding that itself needs that Project.
Prove invalid/denied changes leave prior usable state, commit/apply crash recovery and correct
read-after-write/compatible completion. Declare which files own unbound definitions and how binding
preserves identity/history without duplicate state, but do not invent paths or an SDK API in this
planning seed. #42 verifies P0's actual representation/ownership evidence; #47/#48 freeze and accept
the R2 product authoring behavior. This is an assigned proof, not existing runnable C# or GREEN.

The future P0 proof pack must contain actual before/after source files and a field-authority table,
not only a proposed SDK. Identify managed versus user-owned source, exact editor/evaluator invocation,
independently expected diff/effective result and source/reference case links. Demonstrate individual
properties, collection replacement/reordering and inherited reset in their permitted modes, plus a
representative computed field and an existing destination-ID/binding collision. Show candidate,
accepted and active control/product revisions, returned error/completion/readback and recovery when
commit/ref/apply or a cross-repository binding handoff fails. Preserve one owner per field and the
previous usable state. This bounded representability gate precedes R2's full engine/live UI authoring;
exact source paths, SDK and implementation are outputs of #34, not choices made by this plan.

Freeze requested versus resolved source/settings revisions and capture timing separately: installed
freezeSettings describes immediate versus assigned-ready-to-start collection but declares no absent
default. Compatible cancel POST, DELETE and requeue also have distinct contracts; internal durable
retry/delivery guarantees do not prescribe repeated or after-finish HTTP outcomes. The linked probe
note assigns the exact actor/request/timeline/effect observations to #32/#33/#35/#36.

## Earliest implementation and verification

D38 places the executable REST expectation corpus/replay harness/actual RED baseline first in P0,
before structural product replacement. Its [test-first gate](teamcity-rest-test-first.md) does not
wait for the entire new persistence/catalog stack; it proves transport/target readiness separately
and reports product-prerequisite blockers honestly. Each domain implementation then follows the
corresponding pre-existing failing contracts and preserves the growing implemented suite.

R0 delivers the first thin compatible execution path together with the actual local Host/Agent lab.
R0's [unfrozen journey manifest](../../.workspace/workstreams/coherent-refactor/r0-journey-manifest.md)
includes the minimal ordinary OIDC/key/current-grant contribution required by the unchanged CLI and
denied-access controls. After accepted P0/R0, the [PILOT integration](../../.workspace/workstreams/coherent-refactor/t3personal-pilot.md)
brings the actual full T3 chain/default publication through breadth levels across UI/direct compatible REST/unchanged CLI before full IAM
and general catalogue/runner/log completion. Its same-milestone #78/#82/#83 own the integration;
full owners retain later scope and one authority. R1 then completes identity/access variants;
R2 catalogue/VCS/requirements; R3 chains/runners/triggers;
R4 tests/logs/history/files; R5 cumulative client/variant conformance and remaining agreed operations.
Provider, server-internal, licensing, Kotlin and plugin-specific calls need early explicit dispositions;
no false successful license/cluster/JVM response or silent omission can close full compatibility.

Use independently authored reference fixtures, actual unchanged CLI processes, real PG/Storage and
normal credentials. Simulator faults exercise transport/delivery boundaries; they do not validate a
second implementation against itself. Source API metadata, actual server observations, synthetic
client fixtures and Vivarium execution evidence are four different evidence classes.

P0 exit requires resource/identity/state/authority decisions and a reviewed endpoint/variant allocation,
including the exact R0 smoke profile. It does not require downloading all historical artifact bytes.
Every domain milestone refines and proves its wire cases before merge; R5 is not a late adapter project.
