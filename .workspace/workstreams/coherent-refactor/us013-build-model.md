# US-013 worked Build identity and input model

P0 working contract for #32/#111, based on the [executed reference proof](us013-p0-execution.md).
Observed facts below are independently captured. The proposed internal representation remains a P0
structural candidate, not an implemented schema, public SDK or milestone acceptance. Resolve its
remaining checks and final independent review before R0 implementation.

## Reference traces

All small-chain work below uses the owned Linux reference Agent. The Windows-named configuration is
a small Linux command fixture: these results make no Windows/T3 build claim. Snapshot reuse is
explicitly enabled with successful-only selection in this fixture, not inferred as every API default.

| Entry | Issued/resulting identity | Actual selected work and bytes |
|---|---|---|
| First ordinary Release | 35, queued → running → finished/SUCCESS | Snapshot Windows 34; artifacts Prepare 33 and Windows 34. Real commands transfer and compare identical inputs; downloaded Release bytes are 27 bytes, SHA-256 `97dc31e36da90b592250f4e30e01d0364a3abcafa36d57fccaccbaedbd6629c8`. |
| Repeat ordinary Release | 38, finished/SUCCESS | Reuses Windows 34 and Prepare 33. A new root run does not imply every upstream runs again. |
| Explicit composite | 42, finished/SUCCESS, no Agent | Selects Release 38; the composite is its own Build. An ordinary Release does not acquire an implicit composite parent. |
| Two unkeyed queued Release requests | First returns 45, second 48 | With the Agent disabled, later GET `/builds/id:45` returns a queued Build whose `id` is 48. Cancellation through the old locator also acts on the resolved Build. Intentional unkeyed requests and domain queue optimization are different from transport deduplication. |
| Explicit suitable disabled-Agent run | Prepare 49, finished/SUCCESS | Request names owned Agent 1; Agent reads before/after still report enabled=false. This has no unmet snapshot dependency. Normal queue work remains waiting while disabled. |
| Cancel an already finished success | POST `/builds/id:35` returns 400 | Subsequent GET still reports 35, finished/SUCCESS. This observed case does not establish all repeat/race/cancel outcomes. |

The earlier `take-started-build-with-same-revisions=false` fixture did not reuse upstreams. Its
diagnostic attempt targeted Release on the disabled Agent while its unbuilt dependencies still
needed ordinary capacity: the truthful reason was `Build dependencies have not been built yet`.
That attempt does not prove a targeting failure. The independent Prepare case isolates targeting.

The snapshot collection in the selected projection reports the direct upstream in both this small
fixture and the historical Release 32726 receipt. Preserve the literal projection; do not manufacture
an expanded closure. The domain still retains the real DAG, so explicit closure queries can be
specified independently. Documentation/projection variants remain accountable gaps.

## Shared concepts forced by those traces

| Concept | Required distinction / candidate representation |
|---|---|
| Configuration | Stable external string ID versus mutable display name, and a regular/composite kind. Current desired settings, an evaluated immutable plan and a historical Build snapshot are different objects. |
| Build identity | Positive int64 compatible Build ID allocated durably, separate from display build number/counter. The pinned CLI additionally requires a positive JS-safe integer; that is not the server's entire int64 domain. |
| Requested versus resolved identity | Preserve the admission identity and the reference-defined replacement/resolution relation. Reads/actions through an old locator must resolve consistently with the shared service; no facade-only alias state or body-hash dedupe. An actual completed execution is not rewritten as a different historical execution. |
| Native IDs | Preserve existing native opaque IDs through durable identity mapping on the same Build, not duplicated native/compatible execution state. Candidate: canonical numeric Build identity plus stable native UUID alias. Existing native payload/matrix adapters need explicit mapping/evidence before this is adopted. |
| Queue | Queue membership/position/reason are views of Build admission/lifecycle. A returned queue href is not a second execution identity. Retain requested target, resolved identity and eventual physical execution separately. |
| Composite and native matrix | Explicit composite is a Build without an Agent execution. Normal catalogue submission returns its ordinary Build. Candidate native payload-matrix mapping retains its explicitly requested aggregate and ordinary cell Builds; do not introduce that aggregate into ordinary compatible runs. Preserve existing cell keys/outcome assertions. |
| State and outcome | Queued/running/finished is independent of SUCCESS/FAILURE/UNKNOWN and cancellation/failure detail. Running SUCCESS is not finished; running FAILURE/UNKNOWN is observable before completion. Declared deleted/unknown lifecycle variants remain separately allocated. |
| Agent facts | Requested Agent may already appear on a queued targeted Build. That does not prove assignment acknowledgement, workload ownership or immutable executed-host facts. Keep requested target, accepted assignment/session fence and actual execution provenance distinct. |
| Eligibility | Connection, authorization, enablement, compatibility, health and occupancy stay independent. Explicit suitable-Agent targeting is a distinct admitted workflow, not a permanent enablement change or bypass of all permissions/dependencies. |
| Dependency definition versus selection | Settings declare snapshot edges and artifact selectors. A Build records actual selected upstream Build IDs, settings/source selections and consumed files/hashes. Reuse does not create fake new executions. |
| Original execution versus operator metadata | Original step/result/input evidence remains immutable. Administrative display status/comments/number and their audit do not overwrite it. Current public problem resources are mutable: a manual SUCCESS can remove a previously visible occurrence. Preserve original reporting evidence separately; do not expose every historical report as a current problem. |

These identity mappings are projections of one owning domain, not separate schedulers or copied
facade state. The target PG transaction owns admission, alias/replacement relations, queue and leases;
the Agent protocol retains its own stable workload/session fences. Neither Git nor Chiral messaging
is the runtime transaction authority.

### Reported results and mutable public status

The [literal reported-build corpus](../../../tests/Vivarium.Tests/TeamCity/Reference/reported-build-observations.json)
contains eight bounded groups / 29 HTTP exchanges against 2026.1.3/build 222742, using one uniquely
owned Linux Agent and an administrator. It is reference/replay evidence, not product conformance.

| Observed operation and readback | Shared-model consequence |
|---|---|
| Change running Build 52's number to `owned-display-label`; GET retains id 52. | Identity and display number are different fields. A number edit cannot move result/input ownership. Counter/default/duplicate-number variants remain separate. |
| Submit a plain log and a problem occurrence to actual Agent-backed running Build 52; the problem is readable and running status becomes FAILURE. | Public reporting commands can affect an Agent-backed Build. Route both reports and Agent results through the same owning Build services; an external report is not a new execution or fabricated Agent result. |
| `PUT finish` acknowledges, then GET first reports running/FAILURE and later finished/FAILURE. | Accepted finish and terminal completion are separate facts. Preserve the observed response and real lifecycle transition; no blanket synchronous-completion or 202 rule follows. |
| Manual SUCCESS on finished Build 52 succeeds; subsequent Build status is SUCCESS, current problem list is empty and direct GET of the previously readable occurrence returns 404. | Preserve original process/report/audit evidence independently from mutable visible status and current problem membership. The HTTP observation does not establish physical database deletion or every later-report policy. |
| The follow-up Build 53 is observed cancelled with UNKNOWN; a SUCCESS request returns success/errorCount=0, but subsequent status remains UNKNOWN. | A successful status request does not prove that every Build state adopts that status. Preserve cancellation and effective status independently; do not infer natural process failure from this follow-up. |
| `PUT runningData` for ordinary queued Build 52 returns 400, explicitly not agent-less; the Build stays queued. | Reporting does not grant arbitrary queued Builds a new execution kind. Genuine agent-less admission, executor eligibility and permissions need their own reference cases before exposure. |

The acquisition intended a later natural nonzero exit, but the follow-up was already cancelled;
`natural_failure_proven=false` explicitly retains that limit. Original acquisition labels are kept
as provenance, with corrected case IDs rather than a false natural-failure oracle. Existing independent
nonzero lifecycle evidence is separate. No restricted principal, duplicate/late report, conflicting
report/Agent-result race or general Agent-less contract is proved here. Those variants stay with
SS-RUN/#134/#135/#136 and contributing result/log owners before their affected implementation.
R0's owned-Agent path preserves original facts, report provenance, effective status, cancellation and
completion separately even though its selected entry does not invoke external producer commands.

The capture removed its project, Agent registration/container and private transport network and
restored the initial local gates. An earlier capture with failed Agent-disconnect cleanup is retained
with a separate recovery receipt; it is not substituted for this fully cleaned capture.

### Approval is a separate admission condition

The [literal approval corpus](../../../tests/Vivarium.Tests/TeamCity/Reference/approval-observations.json)
contains three groups / 83 exchanges against the same pinned server, one administrator and zero Agents.
The owned configuration has a harmless process step, an explicit individual-user rule, a one-minute
timeout and `manualStartIsApproval=false`; none is an absent-default observation.

The Build first reports `waitingForApproval`, eligible for the current user. Approving it changes
approval status to `approved`; it remains queued with no idle compatible Agent. Repeating approval
returns 400. A nonexistent-user rule instead reports `configurationValid=true` with a deleted-user
sentinel and cannot be treated as the malformed-rule `configurationValid=false` variant. Its approval
request returns 200 while the effective approval remains waiting; the Build fails to start.

The valid-user timeout reads as approval `timedOut`, Build finished/FAILURE with `failedToStart=true`,
a preparation problem and no `canceledInfo` in the literal selected projection. This actual response
is more specific than the generic cancellation wording in the 2026.1 help. Keep approval rules/votes,
current-user eligibility and timeout separately from queue membership, execution state, cancellation
and Agent eligibility. No approval request may manufacture capacity or report a successfully executed
Build. Group/composite voting, whole-chain approval, untrusted Builds, malformed rules, other actors
and repeat/race variants remain with SS-RUN/#134/#135/#136 before their affected exposure. R0 has no
approval feature; its neutral absence must not block the ordinary owned-Agent path.

### Imported origin and retained sizing

TC-IMP-02 already requires stable installation/kind/source-ID mappings. Keep a future imported
object's original ID qualified by stable source-installation identity and resource kind, separate
from the globally allocated local Build key, mutable source URL and native alias. Resolve relations
through that origin mapping; original test IDs remain exact strings. A historical source actor or
Agent is provenance and never implicitly gains current credentials, grants or workload ownership.
This applies to retained tombstones/archived parents as well as visible current catalogue rows.
It refines the existing import contract; no importer, migration or old-ID public adapter is proved.

The read-only retained metadata assessment finds 38 projects, 97 configurations and 104 reported
Agent registrations. All 97 retained Build-page snapshots together contain only 251 sampled rows;
76 pages explicitly have more data. The 1,084 collection declarations include 38 failures, followed
by 38 declared repairs, which are not independent full-export acceptance. Total retained Builds,
test occurrences and log/artifact bytes remain unknown. Local JSON size is not a Storage estimate.
Full paging/export, privileged originals, source-change consistency, one-way conflict/import and
source-unavailable restore proof remain SS-MIG/#158/#159/#160 (R6). US-013's fresh installation
does not transfer old bytes or perform cutover. Its Host still follows the already adopted coordinated
PG/Storage/Git/private-settings/key backup boundary; no compatible backup job can be exposed as a
successful no-op. Backup wire jobs and source-specific retained originals require their own cases.

### Native matrix preservation boundary

The existing implementation allocates a 32-character opaque aggregate ID and a different opaque
ID for each cell in one `MatrixBuildStore.SubmitScopedAsync` transaction. The stored Agent assignment
contains the cell ID. `BuildRestProjection` exposes the aggregate as `/api/v1/builds/{id}`, cell IDs
inside `children`, queue entries with cell `buildId` and an aggregate `buildUrl`, and artifact URLs
containing both IDs. The CLI treats IDs as strings and rejects caller-supplied child IDs; its watcher
also requires an event's resource ID to match the requested native aggregate ID. These are actual
current contracts, not evidence that a numeric alias adapter already exists.

| Existing observable | Required candidate mapping and R0 preservation evidence |
|---|---|
| Native submission result and idempotent retry | One explicit native-matrix aggregate identity plus one identity per declared cell, each with a stable native alias and canonical numeric ID. Keep actor/operation/request-key scoping and exact prior response semantics. Native keyed retry must not change ordinary compatible unkeyed enqueue behavior. |
| Ordinary compatible configuration run | Its ordinary Build, without an automatically created native matrix or composite wrapper. A native-matrix kind is not a claim that TeamCity Composite settings reproduce payload-matrix semantics. |
| Assignment, acknowledgement, heartbeat and terminal result | Resolve the stable cell alias to the same owning Build. Preserve the string workload ID and its lease/session fences through the Agent protocol; no independent facade queue or recreated execution. |
| Native GET, cancellation, events and replay cursor | Resolve the aggregate alias once in the shared service; emit the expected native resource ID, child IDs, links, runtime revision and cursor ordering. A canonical numeric key does not justify rewriting an accepted native response/event ID. |
| Queue, results and artifact bytes | Preserve the cell-to-aggregate relation, cell name/order, RID, requirement, immutable assigned-host facts, exit/timeout/skip results and byte hash/length. Old native download URLs continue to resolve the same authorized stored bytes. |
| Native cancellation and outcomes | Preserve queued/running/cancel-requested/finished and native cancelled/failure/success distinctions. Compatible state/status/cancellation projections follow the reference cases; do not turn an in-progress native cancellation into a finished compatible Build. |
| Historical IDs and restart | Durable alias uniqueness and kind/parent relations survive process restart. US-013's fresh installation does not authorize a second identity authority or require legacy database migration. |

Existing preservation evidence is in `BuildRestApiTests`, `BuildMutationRestApiTests`,
`BuildEventStreamTests`, `BuildLeaseTests`, `CliRestClientTests` and `CliTests` (exact due tests
must be selected in the ledger). The current tests run the legacy implementation, not the candidate
mapping. R0 must reuse those assertions against the replacement shared domain and add native/compatible
cross-reads, independent ordinary-run neutrality, alias-collision and restart controls. A model table
alone does not close that proof or adopt a public identity schema.

The [physical identity study](../../../tests/Vivarium.DefinitionProof/BuildIdentityPhysicalTests.cs)
now combines actual legacy native submission/HTTP/TLS/AgentHub and unchanged native `viv-cli`
cancellation with an isolated PostgreSQL identity candidate. Numeric keys retain native aggregate/cell
aliases under concurrent registration, rollback and restart; independent ordinary/composite rows get
no implicit matrix. Standalone Agent/type keys remain stable when the real protocol connection
generation advances. The legacy Host still returns 404 for candidate numeric Build URLs. This proves
selected representation and existing-client overlap, not the replacement adapter, real Agent execution,
import/queue-replacement semantics or artifact/log/SSE preservation. Those exact R0 obligations remain.

## Definition, settings and source selections

The historical independent server receipt distinguishes source `7ff65cada…` from settings
`7495b685…`, even though the settings file bytes match. The same VCS root has different source/settings
root instances and checkout rules. Never use a manifest-claimed source revision as settings identity.

Represent the requested branch/revision/settings mode and explicit triggering options separately
from the resolved control/settings revision set, immutable effective plan and resolved source-root
instances/revisions/checkout rules. Definition collection and source collection are not one instant.
The small source-free/server-settings fixture executes its earlier command after a queued edit;
the Git-source fixture resolves later source bytes. Even explicit true/false `freezeSettings` in that
context did not establish a universal capture rule. Store the actual resolution state and evidence,
not an invented admission-time source snapshot or guessed absent default.

Resolve Agent parameter/fact provenance at actual assignment, not merely at enqueue. Once an input
has been resolved and used, later desired-setting edits, source changes, Agent reconnect/rename or
credential changes cannot rewrite it. Failed source/settings collection is a truthful waiting/failure
state with its reference behavior, not an empty successful plan.

## C#/Git authority and completion

The [source experiment](../../../tools/definition-proof/README.md) separates control metadata/binding,
product managed declarations and named user computations. The reference demonstrates generated,
initializing, manual/computed and editing-disabled contexts. Required supported edits must survive
those distinctions without arbitrary C# AST rewriting or a hidden DB/JSON override.

Project creation must precede an optional product binding. Prepare a destination without making it
an active second owner; switch the accepted control binding and retain exact active/last-known-good
revision sets. Git commits/refs and PG activation are separate durability boundaries. A successful
source compile or remote Git push alone is not an applied configuration. Internal CAS, retries and
restart recovery must preserve the reference's caller-visible success/readback fence; they do not
require new headers or blanket 202 responses from compatible clients.

The source experiment proves typed edits and deterministic reevaluation, not that activation saga.
Exact mode/error/completion cases, current grants, collisions, accepted-but-unapplied/interrupted
handoffs and real recovery remain P0 work. Kotlin/XML-specific public operations still need explicit
language/source mappings and due ownership; reporting fictitious Kotlin success is not a C# mapping.

The [child-owner reference corpus](../../../tests/Vivarium.Tests/TeamCity/Reference/child-owner-observations.json)
now supplies seven groups / 297 exchanges and exact source revisions. Under an editing-disabled
ancestor, an inherited child edit returns 500/ReadOnlyEntityException; explicitly disabling child
synchronization admits the local edit. A later real parent export excludes the child's declaration.
Parent-only source import preserves the child value; even explicit source reintroduction of that
child reports applied at the parent while retaining the child's disabled mode and local value.
Ancestor source presence is therefore not sufficient ownership. Preserve the independently selected
child owner and ordinary inherited project parameters rather than allowing two accepted field owners.

The [compiled child-owner study](../../../tests/Vivarium.DefinitionProof/ChildOwnerSourceTests.cs)
demonstrates a bounded candidate: the parent selects committed product C#, the child selects committed
control C# and retains its parent identity. A parent edit/foreign child declaration cannot overwrite
that selection; an owned child edit and cold old-revision reads remain independent. It follows an
executed internal RED for the formerly single-project-only compiler. This is source-owner selection
feasibility, not a production hierarchy importer, cross-project effective-parameter evaluator,
language/mode HTTP adapter or public SDK. Those explicit limits remain due before affected exposure.
Control-only mode readback also has its own fence: the reference can report configuration updated
without a new source revision/application, so waiting for a nonexistent new import is not completion.

## Acceptance and unresolved work

Supporting business intent: AC-BLD-01/02/03/04/05/07/09/13, AC-CFG-01/02/03/04/05/06,
AC-AG-02/06, AC-ART-01/03 and affected access/identity AC. TCM-01/02/03/04/05/07/08/10 and their
exact wire variants are joined per due case, not all declared GREEN by this document.

Before P0 closure: prove the native-ID/matrix mapping against existing clients/fixtures, complete the
real representation/activation/recovery boundary, freeze exact R0 actor/request/effect/byte cases,
classify remaining alias/requeue/deleted/permission/race/settings-mode variants by earliest due
integration, and finish the existing source/ledger/control/debt/review/main gates. Unknown shared or
R0 impact cannot be marked later merely because the happy path is now observed.
