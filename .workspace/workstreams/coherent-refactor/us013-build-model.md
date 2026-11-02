# US-013 worked Build identity and input model

P0 working contract for #32/#111, based on the [executed reference proof](us013-p0-execution.md).
Observed facts below are independently captured. D41/D43/D44 adopt catalogue ownership, isolation
and the shared source/edit mechanism. The [early activation contract](../../../docs/design/csharp-activation.md)
specifies the selected R0/PILOT declaration and completion subset; production integration and later
full SDK/field schemas remain due before their exposure. Complete P0 checks and independent review
before R0 implementation.

## Reference traces

All small-chain work below uses the owned Linux reference Agent. The Windows-named configuration is
a small Linux command fixture: these results make no Windows/T3 build claim. Snapshot reuse is
explicitly enabled with successful-only selection in this fixture, not inferred as every API default.

| Entry | Issued/resulting identity | Actual selected work and bytes |
|---|---|---|
| First ordinary Release | 35, queued â†’ running â†’ finished/SUCCESS | Snapshot Windows 34; artifacts Prepare 33 and Windows 34. Real commands transfer and compare identical inputs; downloaded Release bytes are 27 bytes, SHA-256 `97dc31e36da90b592250f4e30e01d0364a3abcafa36d57fccaccbaedbd6629c8`. |
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
| Native IDs | D41 selects the same real catalogue-backed numeric Build identity for native and compatible management in the fresh installation, with one execution state. The old public UUID alias adapter is no longer required. Preserve the distinct opaque in-flight Agent workload/session fence and its durable association with that Build. |
| Queue | Queue membership/position/reason are views of Build admission/lifecycle. A returned queue href is not a second execution identity. Retain requested target, resolved identity and eventual physical execution separately. |
| Composite and native matrix | Explicit composite is a Build without an Agent execution. Normal catalogue submission returns its ordinary Build. D41 retains matrix/scenario capabilities through real Git-owned configurations, with explicit aggregate/cell relations and resolved input provenance. Do not add an aggregate to ordinary compatible runs or manufacture catalogue objects from arbitrary payload labels. |
| State and outcome | Queued/running/finished is independent of SUCCESS/FAILURE/UNKNOWN and cancellation/failure detail. Running SUCCESS is not finished; running FAILURE/UNKNOWN is observable before completion. Declared deleted/unknown lifecycle variants remain separately allocated. |
| Agent facts | Requested Agent may already appear on a queued targeted Build. That does not prove assignment acknowledgement, workload ownership or immutable executed-host facts. Keep requested target, accepted assignment/session fence and actual execution provenance distinct. |
| User and authenticated subject | Stock Identity keeps its Guid account key and external issuer/opaque-subject association. Compatible User.id is an int64 facet allocated durably on that same account; native/compatible lookup reaches the same current eligibility/grants. It is not a second user store or a replacement JWT subject. Agent.id is independently int32; never impose a universal ID conversion. See `r0-identity-source-facts.json`. |
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

### D41: catalogue ownership replaces legacy public submission compatibility

The owner selected C#/Git replacement on 2026-11-02. Every admitted Build must refer to a real
Project and BuildType with an immutable evaluated definition/revision. No synthetic generic
configuration, caller-label import or parallel YAML authority is needed to preserve the retired
entry. Keep stable internal catalogue identity separate from mutable external IDs/display names,
numeric Build ID, display number and opaque Agent registration/workload/session fences.

| Retained capability | Replacement obligation |
|---|---|
| Configuration run | Native and compatible entry use the same real Git-owned configuration and owning services. Ordinary submission returns its ordinary Build, with no implicit matrix/composite wrapper. |
| Explicit matrices/scenarios | C#/Git declares the real configuration and scenario/parameter expansion. Retain aggregate/cell relations, selected definition, cell keys/order/RID/requirements/results; freeze compatible matrix variants before their affected exposure. |
| Agent execution/ACK/recovery | Preserve opaque assignment identity for an accepted workload, credential/connection generations, lease/session fences, heartbeats, terminal ACK, stop/restart and current-release upgrade behavior. The public submission format does not own AgentHub/Bootstrap compatibility. |
| Run/cancel/retry/watch | Preserve authorized actor/operation identity, durable queued/running/terminal/cancel behavior, bounded log/event replay and meaningful CLI exits. Explicit native keyed retry remains distinct from ordinary unkeyed compatible admission/reuse. |
| Results/logs/artifacts | Preserve original process/report evidence, current status semantics, bytes/hash/length, immutable assigned-host provenance and current project/build authorization. |
| Retired format | No requirement to preserve `viv-cli run vivarium.yaml`, arbitrary payload/matrix DTOs, old public aggregate/cell IDs or download URL shapes in the fresh target. Existing operational data are not deleted or migrated by this decision. |

Existing `BuildRestApiTests`, `BuildMutationRestApiTests`, `BuildEventStreamTests`, `BuildLeaseTests`,
`CliRestClientTests` and `CliTests` remain useful baseline evidence. Port their behavioral/security
assertions to the catalogue-based replacement; retire format-only assertions with verified removal.
#113/#114/#115 and #116/#117/#118 own the early delivery and due contributions, with full configuration,
execution/matrix owners retaining other variants. D39 still forbids removing a working capability
without its due replacement proof or requiring a later owner's blanket closure before useful integration.

The [earlier physical identity study](../../../tests/Vivarium.DefinitionProof/BuildIdentityPhysicalTests.cs)
is retained historical feasibility evidence: actual legacy native submit/HTTP/TLS/AgentHub/native
`viv-cli` plus isolated PG aliases/concurrency/rollback/restart, with legacy numeric URL404. Its
public alias adapter is no longer a D41 target obligation. It does not prove the required real
Project/BuildType relation or the new catalogue entry. Standalone Agent/type and in-flight workload
identity/fencing remain relevant; no original result/receipt is rewritten.

The [D41 catalogue study](../../../tests/Vivarium.DefinitionProof/CatalogIdentityPhysicalTests.cs)
now compiles the actual committed C# for T3Personal and its three configurations into real catalogue
and immutable-definition relations in owned PostgreSQL. Concurrent Build allocations remain distinct;
missing Project/configuration/definition and definition rewrites are rejected. A real source edit
selects a new plan for a new Build while old input and opaque workload mapping survive PostgreSQL
restart. This is structural feasibility, not the production catalogue, activation/authentication,
source timing, concurrent activation, matrix execution or HTTP acceptance. No old result is rewritten.

### D42: truthful OSS entitlement and discovery

The owner selected free OSS capacity with no license-imposed limits. Configured concurrency,
physical capacity, compatibility/current authorization and runtime budgets still constrain actual
work. Native discovery identifies Vivarium and its real release; the compatible API profile identifies
its supported TeamCity REST contract, not a fictitious JetBrains installation. Source server product
version, REST implementation version and API schema version remain distinct.

Commercial TeamCity key operations have an explicit unsupported/non-success contract, no entitlement
mutation and no paid-license engine. Keep their discovered rows, source identity, Host ownership and
negative acceptance in the ledger. This is a D42 compatibility exception, not positive TeamCity key
conformance or a waiver for other Host/plugin/cloud operations. Exact DTO/unlimited representation,
current grants and unsupported status/media/body are due before affected exposure. Installed schema
already declares `licensingData.unlimitedAgents` separately from numeric capacity fields; do not
invent a numeric unlimited sentinel or copy source entitlement as Vivarium's current state.

The installed `licensingData.serverLicenseType` enum includes `open_source`; use that compatible
classification with `unlimitedAgents=true`, `unlimitedBuildTypes=true` and `licenseUseExceeded=false`.
Omit optional finite maxima/remaining-capacity fields rather than inventing a sentinel. Native
discovery separately names Vivarium and its release. An unlimited license flag is not evidence of
unlimited physical capacity or support for a separately unimplemented resource such as Pipelines.
The pinned CLI's summary projection preserves these booleans without requiring numeric maxima.
Before exposure, freeze current grants and literal JSON/XML/fields/negative responses. These selected
representation rules do not constitute a reference capture or an implemented endpoint.

## Definition, settings and source selections

The historical independent server receipt distinguishes source `7ff65cadaâ€¦` from settings
`7495b685â€¦`, even though the settings file bytes match. The same VCS root has different source/settings
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

D44 adopts the mechanism established by the
[source experiment](../../../tools/definition-proof/README.md): control metadata/binding, product
managed declarations and untouched named user computations, with typed C# edit declarations after
computed definitions. Subsequent mutations read the actual compiled declarations, preserving prior
removals/overrides rather than reconstructing intent from effective values. No arbitrary C# AST
rewriting or hidden DB/JSON override is an authority. Full field SDK/editor support remains due
before exposing each edit category; the bounded scalar/parameter/step study does not accept all fields.

Source selection follows the closest explicit synchronization policy: inherited shares that source;
an explicitly disabled Project selects its managed-local control-Git C#; an enabled binding selects
its declared settings repository/revision. Desired settings stay in Git even while synchronization
is disabled. Parent source presence cannot replace a child's independently selected owner. Ordinary
project/template inheritance remains separate. R0 uses a real unbound Git-owned smoke; PILOT prepares
the three configurations through C#/Git. Broader mode/language/API editing remains with SS-CFG/VCS
before exposure, not a blanket prerequisite to implementing those selected integration contributions.

Project creation must precede an optional product binding. Prepare a destination without making it
an active second owner; switch the accepted control binding and retain exact active/last-known-good
revision sets. Git commits/refs and PG activation are separate durability boundaries. A successful
source compile or remote Git push alone is not an applied configuration. Internal CAS, retries and
restart recovery must preserve the reference's caller-visible success/readback fence; they do not
require new headers or blanket 202 responses from compatible clients.

The source experiments prove bounded typed edits and cold reevaluation of fixed fixtures; arbitrary
C# need not be deterministic (D43). Separate Git/PG worker-crash studies prove bounded recovery,
not a production activation saga. The [selected early activation contract](../../../docs/design/csharp-activation.md) now specifies
completion/current-authority/collision and failed-handoff fences for unbound R0 and C#/Git PILOT
preparation. Broader exact mode/error/edit cases remain due to their owners before exposure. Kotlin/XML-specific public operations still need explicit
language/source mappings and due ownership; reporting fictitious Kotlin success is not a C# mapping.

The separate CLI format blocker is now resolved by the published, locally installed **0.5.6** fix in
CliFactory [#79](https://github.com/iXab3r/EyeAuras.CliFactory/issues/79). The
[selected R0 consumer](r0-client-selection.json) independently verifies both unchanged-string writes,
remote errors, Update denial, ordinary JSON and JSON-RPC against the installed package. This allows
the C# format to reach a server; it does not prove that server's source compilation or activation.
No XML/Kotlin compatibility exception is accepted. Keep other language/mode/ownership/completion
variants accountable and the original 0.5.2 reference observations immutable. CliFactory's ongoing
[#80 audit](https://github.com/iXab3r/EyeAuras.CliFactory/issues/80) is not a blanket story dependency.

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

### D43: project C# evaluation boundary

The owner accepted isolated Linux/Docker evaluation, without network, Host data or secrets and with
time, memory and output bounds. Both compilation and execution are inside that boundary; ordinary
project authors need no Host-admin trust. The coordinator owns the pinned compiler/dependency image,
trusted build project and immutable source selection, then validates returned data under current
ownership/permission rules before activation. No Host-execution fallback is allowed. Exact resource
bounds, negative evidence and descriptor-based restart cleanup passed fourteen actual boundary
checks, retained in `us013-isolation-results.json`. This is not production adoption or closure of
the remaining source/mode/ownership contract. Historical same-user trusted-source
experiments keep their original limits. The concrete owner question is resolved by D43.

### Producer and output boundaries selected for early delivery

The value-free pinned interface assessment at
`artifacts/us013-producer-source-20261102/interface-assessment.json` supplements the earlier
172-file settings census. Fixed literal searches across eight pinned repositories and both separately
attributed T3 revisions find 66 source-file occurrences, including tests/documentation. Thirteen files
contain service-message text and twenty contain TeamCity environment names. These are source facts,
not an exhaustive runtime denominator or independently observed server effects.

The actual pinned T3 `scripts/personal-release/run.mjs` emits `buildNumber` and `publishArtifacts`;
its Kotlin wrapper also publishes the actual Prepare log. Preserve the escape rules, display number
versus identity, on-Agent path containment, real file collection and selected downstream bytes.
Prepare/Windows/Release keep the attributed no-application-launch and test-repository deltas. The
other retained drivers additionally emit `importData` JUnit, `setParameter` and block progress;
`TEAMCITY_VERSION` guards must not silently disable their behavior. SS-EXEC owns execution/environment
and parsing integration, with SS-RUN display/parameter state, SS-BLOB collected bytes, SS-RES report
imports and SS-LOG retained raw output. D14/D32 keep messages untrusted and raw reports authoritative;
receiving message text is not proof of the requested effect. Exact early variants remain due in P0
before the affected R0/PILOT integration; other drivers remain due before their owner exposes them.

For R0 lifecycle observation, preserve the existing native durable event capability:
`GET /api/v1/events?topic=build&resourceId=<Build ID>`, with cursor/Last-Event-ID reconnect and
current Build access checks. **This is not a workload-output capability.** Actual
`BuildEventData` contains a recovery resource link and an authoritative-GET flag; the event store
persists matrix transitions and no stdout/stderr bytes. `BuildTracker.GetLog` retains bounded text
only in memory. Neither proves retained output after restart. The earlier proposed use of the event
route for output was incorrect and is withdrawn before contract freeze.

SS-LOG's early R0 contribution must instead deliver real bounded durable output bytes and an
authorized native read/resume resource under the same Build identity. The selected
[REST resource](../../../docs/design/rest-api.md#71-selected-r0-durable-build-output) and seven
[planned native effect variants](r0-journey-manifest.md#selected-native-output-contract-and-planned-effect-cases)
now specify metadata/content reads, cursor/gap/error/current-access behavior. These remain unbound;
their common IAM/protocol and source/case/contribution obligations are specified in the selected R0 contract. Actual storage/Agent receipt/restart/readback
remain R0 implementation/acceptance work. Port the actual bounded-output assertions and preserve
D31's control-path priority; byte provenance and durable gaps cannot be replaced by status events.
The unchanged TeamCity CLI's inspected byte methods use REST file/source/archive/icon resources;
they do not supply an ordinary Build-log download command. Do not invent one to prove the CLI
journey. Native output and compatible artifact download must inspect the same actual Build.
Range/archive/source/server-file/avatar and non-REST clients remain separately accountable under
their full owners. Neither these specifications nor the independent installed log-access observations
declare native output GREEN or close the complete R0 manifest.

Supporting business intent: AC-BLD-01/02/03/04/05/07/09/13, AC-CFG-01/02/03/04/05/06,
AC-AG-02/06, AC-ART-01/03 and affected access/identity AC. TCM-01/02/03/04/05/07/08/10 and their
exact wire variants are joined per due case, not all declared GREEN by this document.

Before P0 closure: verify the adopted catalogue Project/BuildType/Build and D44 source ownership
against their bounded studies, retain Agent/workload fences and matrix capability obligations,
complete the selected representation/activation/recovery contract, freeze exact R0 actor/request/effect/byte cases,
classify remaining alias/requeue/deleted/permission/race/settings-mode variants by earliest due
integration, and finish the existing source/ledger/control/debt/review/main gates. Unknown shared or
R0 impact cannot be marked later merely because the happy path is now observed.
