# Recommendations for the 36 REST acceptance findings

Status: **Proposed for owner discussion; not adopted architecture or executable acceptance**.
Baseline: [REST review](../acceptance/rest-gap-review.md), 110 canonical AC, installed reference
2026.1.3 build 222742. Every REST-AC finding remains open. Writing a recommendation does not settle
an unknown reference result, authorize production mutations or waive compatibility.

## Recommended direction

Use TeamCity's observable semantics for ordinary build, Agent, project, test and permission behavior.
Store that model through the accepted Git/PG/Chiral/Storage authorities. Keep one domain implementation
and two HTTP projections. Most questions need reference examples rather than a new product policy.
The actual design choices are authoring, native integration and scope boundaries described below.

### Editable C# settings and arbitrary C# work

Recommend a managed declarative C# definition for all settings that REST/UI must edit: stable resource
IDs, ordered typed settings and extensible property bags. Generate that owned file deterministically
as a whole from the validated candidate model. Keep handwritten C# work in separate script/step files.
The managed file remains the versioned authority; the database is its projection, not an override.

Do not promise automatic property editing of an arbitrary computed C# object graph. Required migrated
configurations must be fully expressible in the editable model. Additional handwritten graph generators
may generate managed definitions before commit; they cannot silently override fields edited through
REST. Prove this boundary with an actual template/parameter/step edit and unchanged-client read-back
before choosing the final authoring API. Arbitrary repository C# must not execute inside the privileged
Host merely to inspect settings; use constrained declarative reading or a proven restricted evaluation
boundary. This proposal trades unrestricted dynamic catalog authoring for predictable compatible edits;
it does not restrict ordinary C# workload code. It needs an explicit owner decision before adoption.

### Git authority and Versioned Settings

Keep every effective configuration tied to accepted Git revisions. Distinguish Vivarium's internal
versioning from project synchronization with the configured product/settings repository. Disabling
that synchronization can leave the accepted revision fixed without moving authority into PG.
Current-settings versus selected-VCS settings then chooses the appropriate accepted revision/plan.
Inherited synchronization, affected projects, load/commit/status and edit permissions remain real
operations. Prove the mapping for all three build-settings modes, not just the default.

Recommend direct, validated commit-and-activate for the standard compatible editing path. Return its
reference success only once the corresponding read/new-build behavior is observable. Native reviewed
proposals may remain pending explicitly; an old synchronous TeamCity edit must not secretly turn into
a pull request. Source-dialect-specific Kotlin/XML execution is a proposed explicit exception unless
the owner chooses to fund a real translator/runtime. Do not report that dialect as executed when the
actual source is C#. Basic project/configuration mutations remain compatibility requirements.

This proposed exception concerns repository authoring/evaluation, **not XML REST request/response
support**, which remains required. Prefer a lossless static settings translator where the actual
clients need it and the supported model permits it; do not promise arbitrary Kotlin execution or
round-tripping computed programs. The owner must decide the exact repository-format boundary.

### Identity, sessions and protected values

Retain Depot-style Identity/OpenIddict and OIDC for normal interactive login. Implement required
Basic/local automation accounts with the same framework password verifier; never assign an OIDC user
a password implicitly. The empty-username superuser form is an alias to the reserved installation
account's framework sign-in checks. Guest access requires explicit enabled policy. Temporary build
credentials belong to the exact run, include its detached phase, and never grant human management
rights. Freeze their actual lifetime and post-finish behavior against the reference.

Local logout, forced logout of all sessions, removal of remembered sign-in, token revoke and provider
logout are separate actions. For forced session termination, recommend the normal Identity security
stamp mechanism checked on every protected cookie request, with an explicit validation-interval choice
and measured request cost. The stock interval is 30 minutes [S10]; resetting a stamp alone cannot prove
next-request logout. This is a proposed option refinement: adopting per-request validation would also
make startup password reset retire old installation-account cookies sooner. Do not claim it already
follows from D34 or invent a custom session epoch. API token revocation remains a separate operation.

Server authentication-module settings map to the Identity Chiral node and supported adapters. A
source-compatible write must reach its reference-observable apply boundary. Restart-required native
resource settings cannot become a successful-but-not-effective compatible authentication change.
Either implement the required hot apply or retain an explicit unsupported-operation/scope decision;
unknown Java authentication modules cannot be accepted as fictitious working providers.

Resource connection secrets stay in private Chiral settings. For reversible build/project secrets,
recommend a small PG credential service using existing framework protection with an explicitly durable,
protected key ring and purpose separation; Git contains references only. Identity password hashes are
not reversible workload secrets. Data Protection can protect stored confidential data, but long-term
key custody/retention and restore need explicit proof [S11]; it is not an automatic external-vault
replacement. First verify existing reusable facilities before adding a provider. Never delete a key
while retained credentials/backups need it. Ordinary reads mask secret values; privileged secure-value
endpoints need their exact reference permission/assignment contract, not a blanket guess.

Choose an explicit key-at-rest protector and prove restore under the supported fresh-host identity;
filesystem persistence alone is not encryption, and machine-bound protection is not automatically
portable. Keep credential/key recovery independent from routine TLS certificate rotation [S11].

### Original evidence and mutable public state

Preserve original source/settings/agent provenance, raw outcomes and test occurrences. Allow the
reference administrative mutations of displayed build status, number, comment, tags and pin state.
Record actor/reason and expose the distinction. Re-evaluate dependent views and integrations only
according to their reference behavior; changing displayed status is not executing the build again.
Running/finished remains independent from success/failure. A running build with current SUCCESS
status is not a finished build or confirmed cancellation.

### Ownership, agentless work and queue optimization

Model an agentless/external executor as a real Build execution mode. A detached build can remain
running after its local finalization releases the Agent. Release capacity only after local work and
required cleanup are confirmed; keep external progress/finish and scoped credentials attached to the
same Build. Do not turn that work into an unrelated AgentExplorer operation [S7].

Distinguish HTTP retry identity, an intentional enqueue, queue optimization and dependency reuse.
TeamCity documents equivalent queued-build suppression and chain substitution [S3]. Reproduce the
installed profile's observable optimization rules; do not invent body-hash deduplication or promise
that every repeat always creates a new Build. Native explicit idempotency can recover a known command.
An unchanged unkeyed request cannot reveal whether the caller meant retry or another command: use the
reference behavior rather than promise exactly-once effects without sufficient identity.

### Backup and product-specific scope

Keep the accepted coordinated offline backup as the first recovery baseline. Full compatible REST
backup control additionally needs a real durable backup job while its management API remains usable:
recommend a maintenance/coordinated snapshot of PG, referenced Storage objects, retained Git revisions,
private settings and authentication keys. State a consistency boundary and verify restore. This is
additional work, not a ZIP-download alias for copying the Host directory. Before adoption, update the
offline-only-first contract and allocate the online/coordinated job explicitly.

Report real Vivarium capabilities and actual product identity through native discovery. Compatible
version fields may advertise the frozen compatibility profile, with real product/version separately
available; never imply a JVM plugin or JetBrains binary is installed. Recommend explicit exceptions
for evaluating repository Kotlin/XML settings, JetBrains license-key administration, JVM plugin execution and
multi-server responsibility mutations until real equivalents exist. These are **scope proposals**,
not accepted exclusions. A capability/error response is preferable to false success, but still leaves
full parity incomplete. Truthful singleton-node reads and native capacity information need no cluster.
Live read-only CLI discovery returned API version `2026.1` and REST plugin version `222742`; keep
these distinct from server product version `2026.1.3` and from the actual Vivarium release version.

On 2026-10-31, read-only CLI pages for cloud profiles and instances each returned zero visible entries
with `hasMore=false`. This is current-credential visibility, not proof about hidden/historical capacity.
Recommend prioritizing the observed physical-agent migration while separately tracking cloud write
compatibility. Deferring an unused adapter must be an explicit scope/delivery decision; it cannot
silently turn the complete REST goal into only the owner's currently observed subset.

## Recommendations for every finding

Each section resolves the design direction and states the remaining acceptance oracle. The original
[gap register](../../.workspace/workstreams/coherent-refactor/rest-business-gaps.tsv) retains all
questions, nearest AC, operation anchors and delivery gates. Source codes refer to the references below.

### REST-AC-01

**Projects: preserve identity on move; create new identities on copy.** Recompute inherited settings
and access under the new parent; retain actual historical provenance. Treat archive, pause and delete
separately. Keep custom ordering and external-ID mutation rules as reference data. Capture a nested
project move/copy with templates, VCS roots, grants and retained builds; do not invent an all-purpose
"copy everything including history" operation. Resolve ID regeneration/aliases from the actual mode.

### REST-AC-02

**Implement ordered template inheritance and owned/effective settings.** Higher-priority template
settings, local overrides, disabled inherited entries and project defaults must be distinguishable.
Honor the detach `inlineSettings` choice instead of always dropping or always copying settings.
Check a two-template conflict, reset override, default inheritance and both detach variants [S1].

### REST-AC-03

**Specify mutation semantics per collection.** Preserve item IDs and order; match append, replace,
delete, disable, empty/null/omitted and inherited-reset behavior per endpoint. Keep unknown property
metadata for lossless import/read/edit, but declare missing execution adapters as blockers rather
than running an unsupported feature successfully. Verify read-after-write and resulting execution,
including deletion of an overridden item that reveals an inherited value.

### REST-AC-04

**Keep declared, inherited, effective, overridden and produced parameter values distinct.** Support
type/specification/default/validation metadata and build-only overrides without changing the shared
configuration. Preserve secrets as protected references and apply the exact read/assignment policy.
Check inheritance removal, prompts/types, runtime replacement, output propagation and denied secret
reads. Password inputs must not become ordinary output parameters [S8].

### REST-AC-05

**Use the editable C# and revision mapping above.** Synchronization-disabled does not disable internal
Git history. Implement current/VCS/default choice, parent inheritance, load/commit/status, affected
projects and configured editability with real effects. Prove the full round trip before catalog code.
Keep Kotlin/XML execution as an explicit owner scope decision; do not return successful synchronization
for a source dialect that was never executed [S6].

### REST-AC-06

**Separate configured VCS roots from observed parameterized instances.** Support multi-root checkout,
per-entry rules, branches, hooks/polling, explicit checks, state reset and secret rotation through their
correct settings/runtime authorities. Validate submodule/LFS modes used by the inventory. Check a root
shared by two configurations, distinct parameterized instances, inaccessible source and delete-in-use;
no silently substituted revision or discarded extra root.

### REST-AC-07

**Add Changes as its own user journey and identity model.** Pending changes, included changes, parent
revisions, duplicates, first builds and issue links are different queries. Preserve source-origin IDs
on import and apply current project visibility. Test a merge and a shared root across configurations.
The TeamCity `history` flag for an out-of-order old-revision build is distinct from merely being a
finished retained build; its rollup/notification/investigation rules need cases [S9].

### REST-AC-08

**Support both settings-freeze modes and preserve explicit selections.** Requested revision/branch,
Agent preference, personal build, clean checkout and parameter overrides stay separate inputs. Capture
two waiting builds while source/settings change: one freezes immediately, the other collects at
assignment/start. Snapshot Agent facts at actual assignment, not at queue entry; a requested/planned
Agent is not an acknowledged ownership claim [S4].

### REST-AC-09

**Keep queue pause, configuration pause and Agent disablement independent.** Global pause prevents
new starts while current work remains accounted for. Match priority/manual order/optimization and
avoid head-of-line blocking by incompatible work. A paused configuration disables automatic triggers
but can still run manually or through dependencies; optional queued cancellation is separate [S2].
Check all three switches, queue reorder during concurrent arrivals and prediction versus assignment [S3].

### REST-AC-10

**Make approvals a durable admission gate.** Persist required users/groups/counts, decisions, actor,
reasons and reference timeout calculated from queueing. Approval alone does not grant an Agent or
skip other eligibility checks. Capture group/count rules, `approveAll`, invalid configuration, chain
scope and timeout/restart; map unavailable approvers and changed membership according to the reference.

### REST-AC-11

**Treat cancel-and-requeue as cancellation plus a separately identifiable replacement.** Preserve the
original cancellation reason and actual termination state; create the replacement at the reference
transition. Public docs reserve requeue for a running build, not a still-queued one [S5]. Test repeat,
disconnect and failure during the transition; do not expose a pause/resume of the same process or
release its capacity merely because the cancel request was accepted.

### REST-AC-12

**Represent the chain as actual selected runs and edges.** Keep snapshot synchronization separate
from artifact transfer, and honor reuse/force rebuild/failed-only/selected dependency options,
same-Agent affinity, composite membership and per-edge failure/cancel rules. Test reuse of running
and completed suitable builds and unavailable artifacts. Neither always rerun nor always reuse is
an acceptable simplification; reference substitutions must remain navigable [S3, S12].

Queue optimization may substitute another run before execution. Preserve request/promotion and
replacement lineage and make old locators resolve exactly as the reference does; do not force every
superseded queued item to appear as a separately executed build. This is distinct from changing the
identity/provenance of an accepted execution.

### REST-AC-13

**Include any/selected configuration run policy in Agent eligibility.** Combine it with requirements,
pool/project rights, authorization, connection, health, occupancy and enablement. Explicit disabled-Agent
targeting may bypass the enablement gate only where the reference permits; it is not authority to
bypass every other gate. Check a capability-matching but policy-excluded Agent, empty selection and
both automatic and explicit targeting. Freeze the actual intersection rather than assume it.

### REST-AC-14

**Keep one pool per Agent, multiple pools per project and the reference Default pool.** A move changes
membership, not machine identity; removal from a custom pool returns it to Default in current docs.
Honor pool limits, all-affected-project permission checks and append/replace membership behavior.
Capture delete/default constraints, running work during reassignment and automatic authorization
token scope/revoke behavior. A pool membership change must not silently create another credential [S13].

### REST-AC-15

**Persist AgentType identity separately from registration and connection.** Map imported type IDs and
historical facts; do not create a new public type on every heartbeat or treat it as a live Agent.
Use it for compatible capacity queries where the reference does, including not-yet-started images.
Capture when effective property changes create a different type and what disconnected/type queries
mean; a type record alone must never provide runnable machine capacity.

### REST-AC-16

**Preserve original evidence and allow reference metadata/status mutations.** Store an attributable
administrative status change separately from original execution/test evidence. Build number is a
mutable display value, not the Build ID; preserve its counter behavior. Check tags add/replace/remove,
comment deletion, pin/unpin and status/reason plus downstream rollups, investigations and publisher
effects. Do not infer completed execution from current SUCCESS status [S14].

### REST-AC-17

**Support a real external/agentless executor, including detachment.** Keep the same Build ID and
history; release the local Agent only after its required finalization. Process authorized external
log/service messages, problems, completion and configured timeout; keep run credentials valid through
the detached phase. Public docs establish this workflow [S7]. The public Java source also describes
queued-to-agentless start, but is from 2024 [S15]; freeze its 2026.1.3 state/permission/finish semantics
before binding. No fake terminal result for still-running local work.

### REST-AC-18

**Use per-operation bulk semantics, normally per-item results rather than a global rollback.** Resolve
the request's target set once for that invocation; check rights per item and preserve safe error
visibility. Native identified retries can reuse the recorded set; a new unkeyed compatible invocation
follows reference selection again. The older public implementation supports partial results [S15].
Verify permission failure, disappearing item, partial success and retry for each bulk family; do not
impose one atomicity rule on every endpoint.

### REST-AC-19

**Build tree/metadata/file/archive views over one authorized manifest and byte store.** Exact-build
source and latest source are separate selections. Preserve original bytes and hashes; validate paths,
archive selection, escaping, links and supported ranges without changing the client's URI semantics.
Check hostile/nested paths, inaccessible files, expired content, archives and non-REST log/feed routes.
A known hash or file name must not grant access.

### REST-AC-20

**Keep logical tests, occurrences and attempts separate.** Use source test IDs and verified mappings;
preserve parameterization/suites, exact long IDs, attachments and partial/ignored/running states.
Reconcile live messages and final reports without duplicate counting. Retain earlier failed attempts
without forcing every ultimately successful retry to make the build fail: aggregate outcome follows
the reference policy. Test cross-branch/configuration history, name changes and imported/native joins.

### REST-AC-21

**Keep build problems separate from test failures and triage separate from raw outcomes.** Implement
responsibility/reassignment/resolution, project/configuration scopes, expiry and when-fixed mute rules.
Apply current access to single and bulk actions. Test one problem recurring across builds, group/user
assignment changes, timed expiry and automatic resolution. A mute is not deletion of failure evidence
or a license to suppress unrelated failures.

### REST-AC-22

**Retain original statistic text plus validated numeric interpretation where needed.** Missing is not
zero; avoid binary floating-point precision loss and guessed units. Keep output values, actual inputs,
interpolation and configured defaults distinct, with the reference secret rules. Test custom values,
precision, unresolved references, live/final changes and downstream consumption. Counter/number changes
must not change stable build identity or earlier input snapshots.

### REST-AC-23

**Prioritize adapters by the actual 97-configuration baseline and verify their side effects.** The
inventory includes .NET, scripts/PowerShell/C#, Docker and NuGet runners, VCS/schedule/finish triggers,
plus AssemblyInfo, swabra, report import, shared resources, keep rules, labeling, perfmon, indexing and
free-space checks. Preserve their inputs and output/cleanup/publication effects; wrapper execution is
not equivalent to installing the feature name. Test denied credentials, failed publication and retry
with one coexistence owner. No migrated family passes with a used adapter unimplemented.

### REST-AC-24

**Use current PG authority and the exact TeamCity role/group graph.** Match append/replace assignments,
nested membership, included-role edits, built-in/custom deletion, All Users/guest and authorization
modes. Follow reference cycle/last-admin validation; do not add an immutable role floor or mandatory
last-admin protection absent there. Delete old authentication authority while retaining historical
actor provenance. Account re-creation must not recover retired grants implicitly; provider provisioning
still follows explicit current policy. Test self/other-user and sibling/project delegation cases.

### REST-AC-25

**Implement the required transports over the same framework authority.** Use the identity/session
proposal above for bearer, Basic/httpAuth, cookie reuse, guestAuth, superuser alias and build-scoped
credentials. Do not attempt to check an OIDC provider password locally. Capture positive/negative
requests, revoked/expired credentials and detached build access. Separate local logout, forced logout
and API revoke; forced logout must reject an old cookie at the specified next-request boundary rather
than rely unknowingly on the default stamp interval [S10, S16].

### REST-AC-26

**Implement profile/preferences and named-token metadata as ordinary owned resources.** Keep self
versus administrator rights, token expiry/scope/name behavior and avatar content/cache/removal explicit.
Preserve supported user/group property bags without treating arbitrary properties as permission
grants. Match remember-me removal independently from active-session logout and token revoke. Test
another user's profile, same-name token creation/deletion and cached avatar replacement.

### REST-AC-27

**Treat deployment instances and their history as first-class records.** A successful build does not
prove that it was deployed. Record the actual build/environment/instance event with actor/time and
retained lineage; expose dashboards and current state as projections. Capture repeated reporting,
replacement, delete/cascade/retained history and imported deployment links. Add this to release/history
delivery where used rather than encode it as tags with an invented meaning.

A deployment instance is a logical deployment target, not necessarily an Agent or VM. Release history
must not acquire an unnecessary machine-provider prerequisite.

### REST-AC-28

**Keep protocol coverage separate from required provider adapters.** Empty visible lists should stay
truthful; start/stop/force-stop need real operations, permissions, quotas and observed outcomes before
being supported. Current-credential cloud reads were empty, so recommend prioritizing physical-agent
cutover and explicitly scheduling unused adapters later if the owner accepts that scope distinction.
Check historical/hidden profiles and client usage first. No successful no-op or blanket cloud exemption.

### REST-AC-29

**Separate a real backup job, cleanup policy and privileged diagnostic files.** Use the backup proposal
above with a verified consistency manifest and restore; preserve offline fallback. Cleanup honors
pins, dependency retention and removal grace separately from explicit deletion [S17]. Put startup
installation passwords in a dedicated private sink outside every served diagnostic area; map required
server-file areas to actual safe product resources. Do not rewrite artifact bytes to sanitize them
or claim a native backup is a restorable TeamCity database archive.

### REST-AC-30

**Expose real durable audit, actionable health and correctly typed metrics.** Preserve actor/target/
outcome and allowed scope, categories, time/unit meanings and missing-data states. Health is not just
an open TCP port or an exception string. Test permission-filtered audit pagination, health item
creation/resolution, unavailable measurements and restart continuity. Workload output remains distinct
from the action journal; heartbeat noise does not require an audit row per message.

### REST-AC-31

**Advertise compatibility truthfully and keep unmapped operations explicit.** Use a truthful singleton
node and real native capability/version information, plus the frozen compatible discovery profile.
Recommend no JetBrains license enforcement/key installation, JVM plugin runtime or multi-server
responsibility changes in the initial C# Host; request an explicit scope decision rather than pretend
they exist. Report configured actual capacity with the profile's valid unlimited representation where
applicable. Test old-client discovery and error handling; unsupported effects remain parity gaps.

### REST-AC-32

**Treat locator/query defaults and continuation as business behavior.** Match nested/encoded locators,
default personal/cancelled/history/branch selection, fields, empty pages with continuation, limits,
current visibility and renamed/imported links. Never replace search-depth limits with total-count
claims or return hidden identities via errors/expansions. Use independent multi-page datasets where
the only visible match lies beyond an empty/intermediate page.

### REST-AC-33

**Distinguish retry receipts from actual queue optimization.** Use the enqueue/reuse rule above [S3].
Test an explicitly identified retry, two intentional requests, equivalent work already queued,
replacement by a newer automatic chain and reference-disabled optimization. Serialize accepted Git
edits and expose their correct read-after-write fence; native CAS must not introduce mandatory new
headers or universal stale-edit rejection on compatible calls where TeamCity provides neither.
Unknown outcome after an unkeyed call is not proof of exactly-once execution.

Include the IDs and links returned for optimized/replaced queue entries. "Stable run identity" does
not establish that every enqueue request survives optimization as an independently executed run.

### REST-AC-34

**Specify deletion per resource; do not apply one universal soft-delete or preserve-everything rule.**
Use immutable IDs/tombstones for retained provenance while removing live authority and active catalog
visibility according to reference semantics. Match dependency warnings/errors and grace/restore/
cleanup windows; current docs give configurable delayed cleanup for deleted projects/configurations
[S17]. Verify explicit deletion of pinned builds and inactive versus merely idle Agent deletion.
Source-absence during diff-import never becomes an explicit target-delete command.

### REST-AC-35

**Prove the migrated workload, not just its settings API.** Start with the real .NET/TRX/JUnit/script/
Docker/NuGet producers and required features from the baseline. Reuse existing Automation, fixtures,
process runner and platform adapters. Add targeted service-message/report/fault cases and actual
Host+Agent journeys where execution is the claim. Keep cheap logic/contract tests for rules; no extra
test framework, generic workflow engine or mandatory full E2E for every Gherkin criterion.

Supply the compatible execution environment and service-message variables used by existing drivers,
including `TEAMCITY_VERSION` and scoped build credentials. Otherwise the migrated driver may disable
its reporting path even though the process runs successfully. Requirement aliases must report actual
capabilities; a compatibility alias is not proof that Java or another toolchain is installed.

### REST-AC-36

**Import a verified reachable graph with stable lineage and original bytes.** Keep source IDs/kinds/
installation origin and relationships to changes, tests/problems/triage, dependencies, actors and
required deployment events. Use checkpointed stages and a final delta/integrity pass; do not pretend
multi-page reads were a source snapshot. Retain historical actor snapshots separately from live user
grants, and explicitly remap/reprovision unavailable secrets. Whole-entity keep/use-source stays the
only conflict choice; missing source objects do not delete target history. Prove source-independent
reads and restored reads before retirement.

Freeze wire-ID allocation against native/source collisions during coexistence; simply allocating
above today's largest imported ID is not sufficient while TeamCity continues creating builds.
Preserve original IDs/provenance and required old-client lookups with explicit mappings. Keep numeric
Build IDs and exact long test-ID strings in their resource-specific wire representations.

## What needs an owner decision, and what needs measurement

The proposed editable C# authoring boundary, dialect/runtime scope exceptions, per-request cookie
stamp validation, reversible build-secret storage, coordinated REST backup and any deferred unused
cloud adapter affect architecture or scope. Present them for adoption explicitly; do not silently
rewrite D32–D35 or declare all 449 operations covered. They can be decided independently.

Template precedence, status updates, queue selection/optimization, concrete replacement semantics,
pool permission intersections, delete behavior, session endpoints and all HTTP shapes are reference
facts to capture, not product choices to invent. Public help is newer than the installed server.
The public REST Java repository's pinned master is from **2024-03-04**, so it is supporting historical
evidence only; for example, its UserRequest does not establish the current logout endpoint.

P0 should add a bounded typed schema/contract export to the existing `@eyeauras/teamcity-cli` tooling
if needed to capture installed Swagger; its current CLI exposes no such command. Keep source reads
on the approved CLI. Execute mutation reference cases only against an explicitly disposable prepared
2026.1.3 instance/project, never the production farm merely to settle prose. Reuse independent
fixtures/oracles; a simulator generated from Vivarium's own responses cannot establish parity.

R0 still delivers the real local server and separate Agent with a seeded compatible run. R1 proves
identity/access; R2 catalog/source/queue/eligibility; R3 chains/integrations/approvals; R4 results and
deployment/history views; R5 cumulative wire/client behavior; R6 import, restore and cutover. Add the
external executor and backup jobs to concrete owning issues before their implementation; the model
decisions happen in P0, not when R5 testing discovers them.

## Primary references and evidence

- **S1:** [Template precedence, defaults and detachment](https://www.jetbrains.com/help/teamcity/build-configuration-template.html).
- **S2:** [Configuration pause](https://www.jetbrains.com/help/teamcity/changing-build-configuration-status.html).
- **S3:** [Queue ordering, optimization, reuse and assignment](https://www.jetbrains.com/help/teamcity/working-with-build-queue.html).
- **S4:** [Custom triggering options and freeze timing](https://www.jetbrains.com/help/teamcity/rest/buildtriggeringoptions.html).
- **S5:** [Cancel and requeue](https://www.jetbrains.com/help/teamcity/rest/buildcancelrequest.html).
- **S6:** [Versioned Settings modes](https://www.jetbrains.com/help/teamcity/rest/versionedsettingsconfig.html).
- **S7:** [Detached/agentless work and build-level reporting](https://www.jetbrains.com/help/teamcity/detaching-build-from-agent.html).
- **S8:** [Parameter reads/overrides](https://www.jetbrains.com/help/teamcity/rest/manage-typed-parameters.html), [output parameter restrictions](https://www.jetbrains.com/help/teamcity/use-parameters-in-build-chains.html).
- **S9:** [Out-of-order history builds](https://www.jetbrains.com/help/teamcity/history-build.html).
- **S10:** [Identity stamp validation interval](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.securitystampvalidatoroptions.validationinterval?view=aspnetcore-10.0).
- **S11:** [Data Protection scope](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/introduction?view=aspnetcore-10.0), [durable key retention](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0).
- **S12:** [Dependency synchronization, reuse and failure policy](https://www.jetbrains.com/help/teamcity/configuring-dependencies.html).
- **S13:** [Pools, capacity and shared-project permissions](https://www.jetbrains.com/help/teamcity/configuring-agent-pools.html).
- **S14:** [Manual build status](https://www.jetbrains.com/help/teamcity/rest/buildstatusupdate.html).
- **S15:** [Public REST source at fc730e6 (2024)](https://github.com/JetBrains/teamcity-rest/tree/fc730e618ccd4b57dbbaf03425bb79a9580d19d2); private analysis receipts contain hashes of six inspected files, not installed-version proof.
- **S16:** [REST auth forms](https://www.jetbrains.com/help/teamcity/rest/teamcity-rest-api-documentation.html), [current logout description](https://www.jetbrains.com/help/teamcity/rest/userapi.html#logoutUser).
- **S17:** [Retention, dependency protection and deleted-configuration grace](https://www.jetbrains.com/help/teamcity/teamcity-data-clean-up.html).
- **Local baseline:** [97 configurations and real feature/producer evidence](../../.workspace/workstreams/teamcity-migration/parity-evidence.md). No migration is accepted by these recommendations.
