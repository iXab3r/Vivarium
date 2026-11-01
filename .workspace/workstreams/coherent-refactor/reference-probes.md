# Queue, capture timing and authoring reference findings

Planning research at `ac609f5355f188080951015083114a3a3bce7625`, 2026-11-01.
This narrows existing #32/#33/#34/#35/#36 obligations; it adds no case, executed conformance,
implementation choice or review verdict. The [R0 manifest](r0-journey-manifest.md) stays UNFROZEN.

## Evidence boundary

The existing installed schema belongs to TeamCity **2026.1.3 / build 222742 / API 2026.1**:
`artifacts/p0-rest-first/reference/captured/schema.body`, bound by
`artifacts/p0-rest-first/reference/acquisition-manifest.json`, SHA-256
`22e37bbaf8bdb48f5f67eade2cd3fe206a193eb8eb4de10178909386444104dc` for the schema bytes.
JSON pointers below identify declarations, not captured endpoint effects. No build/Agent lifecycle
was executed in the existing catalog/permission reference cohort. Its fifteen materialized cases are unchanged.

Official `/2026.1/` help is release-family documentation, not an immutable patch-version observation.
Rolling tutorials, plugin documentation, historical support and older Java source are distinct leads;
none freezes an installed response, default, permission or completion fence. Pinned CliFactory
`b158fe03eab3c31982a21b36c278f690f6b541d9` supplies request/parsing facts, not server outcomes.

## What is established, and what is still a probe

| Topic | Source-backed finding | Remaining installed-reference evidence |
|---|---|---|
| Identity/reuse | Schema `/definitions/build/properties/id` is int64; `/definitions/build/properties/taskId` is also int64 without an equality rule. `replacementIds` and `related` are declared. The unchanged CLI parses a positive safe-integer id and polls `/builds/id:N` using its enqueue result. [2026.1 queue help](https://www.jetbrains.com/help/teamcity/2026.1/working-with-build-queue.html#Build+Queue+Optimization+by+TeamCity) describes queue suppression/replacement and reuse of started work. | Ordinary returned/queued/running/history identity and optimized/reused old-locator resolution. A [2019/2020 JetBrains support exchange](https://teamcity-support.jetbrains.com/hc/en-us/community/posts/360006101100-Build-Id-from-queued-build) supports normal continuity but also explains an optimization redirect to another execution ID; it does not freeze 2026.1.3 REST behavior. No universal same-ID or different-ID promise. |
| State/outcome | `/definitions/build/properties/state/enum` includes queued, running, finished, deleted and unknown. `/definitions/build/properties/status/description` describes unset queued status, SUCCESS for healthy running/success, FAILURE and canceled UNKNOWN; `failedToStart` and `canceledInfo` are separate. | Literal absent/null/value projections, errors and retained lookups for each due state. Running SUCCESS is not completion; do not reduce the state enum to three values or infer terminal outcome from status alone. |
| Cancel/delete/requeue | Installed POST cancel operations declare 200/build; DELETE operations have only generic default response metadata. [2026.1 cancellation model](https://www.jetbrains.com/help/teamcity/rest/2026.1/buildcancelrequest.html) and `/definitions/buildCancelRequest/properties/readdIntoQueue` distinguish explicit false from true, which requests new identical work after running-build cancellation. Queued work can only be aborted. | Repeat/after-finish errors and effects, queue-start/finish-stop races, permission denial, actual stop/capacity release and DELETE retention. The [start/cancel guide](https://www.jetbrains.com/help/teamcity/rest/2026.1/start-and-cancel-builds.html#Cancel+Queued+Build) distinguishes metadata-preserving cancellation from deletion. No omitted-flag default or uniform repeated HTTP outcome is established. |
| Capture/provenance | `/definitions/buildTriggeringOptions/properties/freezeSettings` and [2026.1 reference](https://www.jetbrains.com/help/teamcity/rest/2026.1/buildtriggeringoptions.html#freezeSettings) distinguish immediate collection from collection when assigned to an Agent and ready to start. No absent default is declared; the pinned CLI omits this option. `/definitions/build/properties/revisions` and `/definitions/build/properties/versionedSettingsRevision` separately identify source and settings revisions. | Absent/true/false timing, queued edits, source/settings-root changes and actual resolved provenance. `settingsHash`/`currentSettingsHash` existence does not define equality, ETags or a capture instant. Preserve requested selections separately from actually resolved immutable inputs. |

Installed operation pointers: `/paths/~1app~1rest~1buildQueue/post` and
`/paths/~1app~1rest~1buildQueue~1{queuedBuildLocator}/post` omit a `fields` parameter although
the client sends it. `/paths/~1app~1rest~1builds~1{buildLocator}/post` declares `fields`.
Their actual POST acceptance/projection is still a probe. DELETE at the two locator paths is a
different operation; a generic `default` response declaration supplies no numeric success code.

## Editing modes and Versioned Settings controls

[2026.1 Kotlin DSL](https://www.jetbrains.com/help/teamcity/2026.1/kotlin-dsl.html#Edit+Project+Settings+via+Web+UI)
documents direct UI edits to unchanged generated scripts and patch-based UI edits after manual code
changes. It also documents temporary read-only initialization and a project-settings UI restriction
when DSL uses context parameters; those parameters remain editable on their own tab and trigger DSL
reload. Manual or computed code is therefore not automatically read-only. This does not prescribe
a Vivarium patch subsystem, arbitrary-code AST editor, SDK, source path or second settings authority.

[2026.1 Versioned Settings](https://www.jetbrains.com/help/teamcity/2026.1/storing-project-settings-in-version-control.html)
distinguishes inherited/disabled/enabled synchronization, one-way read-only UI versus two-way edits,
import versus overwrite, and current/default/VCS build-settings selection. Invalid incoming settings
retain current settings; latest UI changes must be committed before a build uses them. Settings
root/path and branch selection have constraints and server-owned exceptions. Freeze actual UI and
REST behavior separately; UI read-only documentation alone does not establish an HTTP rejection.

Installed `/definitions/versionedSettingsConfig/properties/synchronizationMode/enum`,
`/definitions/versionedSettingsConfig/properties/buildSettingsMode/enum` and
`/definitions/versionedSettingsConfig/properties/importDecision/enum` declare these mode choices.
At `/paths/~1app~1rest~1projects~1{locator}~1versionedSettings~1contextParameters`, GET and PUT
declare 200/collection. The [rolling REST tutorial](https://www.jetbrains.com/help/teamcity/rest/manage-vcs-settings.html#Other+Tasks)
currently says POST for modifying context parameters: retain this discrepancy and use the pinned
schema's GET/PUT declaration as the acquisition target. `checkForChanges` and `commitCurrentSettings`
under the same path prefix declare POST with generic default responses; `loadSettings` declares
POST/200/projects and `status` GET/200/status. These do not prove apply timing or durable completion.
Pinned [client.ts](https://github.com/iXab3r/EyeAuras.CliFactory/blob/b158fe03eab3c31982a21b36c278f690f6b541d9/integrations/teamcity/src/client.ts)
corroborates PUT in `replaceVersionedContext` and POST in `checkVersionedSettings`/
`commitVersionedSettings`; these are client request facts, still not observed status/effect evidence.

## Bounded future probes in existing tasks

Every probe records pinned target/client, real ordinary/allowed/denied actors and effective grants,
owned Project/BuildType/Agent/workload setup, exact request and sanitized raw response, timeline,
source/effective-state or byte readback and cleanup. These are acquisition obligations, not actions
performed by this planning slice. No new case IDs are materialized here.

| Existing owner | Concrete probe/output |
|---|---|
| #32; #33/#35 acquisition; #36 freeze | Ordinary enqueue → queued poll → actual start → success/failure/history, then repeated/default/custom-parameter and optimized/reused controls. Record returned/resolved IDs and old-ID GET/cancel behavior, composite/matrix/native-import relations, all due state/outcome projections and POST-fields behavior. |
| #33/#35; #36; #38/#44 product effects | Separate explicit-false queued/running POST cancel, repeated/after-finish and queue-start/finish-stop races with current permission denial. Capture actual stop/no-start/capacity and retained metadata. Classify DELETE/requeue/optimized variants for their due owners; do not silently add them to the selected R0 CLI journey. |
| #32/#33/#35/#36; #34/#47 provenance | While work waits, vary source-only/settings-only changes, freezeSettings absent/true/false and due current/default/VCS modes. Record intended selection, collection time, resolved source/settings/control revisions, effective configuration and actual Agent assignment separately. |
| #33/#35; #34 proof; #47/#48 implementation acceptance | Mode × operation matrix: disabled/inherited sync, two-way generated/manual/computed DSL, one-way and context parameters, including initialization/reload. Exercise property, collection replace/reorder, inherited reset and binding collision through UI and REST independently; current actor denial is a separate control. |
| #34; #33/#35; #47/#48 | Observe edit return, source diff, commit/push/ref, reload/apply/status/readback and next-build eligibility. Probe invalid code/IDs/context values, unavailable Git/blocked push, commit accepted but apply failed, and restart/retry at handoffs. Preserve prior usable state and one field owner; no guessed 200/201/202 or native async policy. |
| Existing #41 / TCM-GAP-001; #56 fleet context | Independent Agent connection/authorization/enablement/update/workload facts, busy-disable continuity and explicit suitable disabled-Agent custom targeting with exact permissions. [2026.1 Agent help](https://www.jetbrains.com/help/teamcity/2026.1/install-and-start-teamcity-agents.html#Build+Agent+Statuses) separately describes new-Agent authorization, co-hosted/cloud exceptions and long-disconnection deauthorization; verify each due context instead of assuming universal manual approval or every disconnect means unauthorized. Existing owners remain; no extra R0 feature claim. |

#42 checks P0 source/freeze/proof accounting; #43 consumes the selected R0 contract and #44 proves
real effects. #34 still owes actual before/after source/evaluator/revision/recovery outputs;
#47/#48 own full R2 authoring. Unknown errors, defaults and completion remain explicit gaps.
