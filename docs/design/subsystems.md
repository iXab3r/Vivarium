# Subsystem ownership and REST traceability

> Status: **Accepted direction — D40; planning/ownership only, no implementation acceptance**
> Owners: Reconciliation, affected domain owners, TeamCity/REST and Test Steward

## Implementation unit and independent tracking axes

Implement and connect a coherent subsystem lifecycle, not a sequence of unrelated REST handlers.
REST is an early external constraint and acceptance view over the real subsystem, not its state
authority or unit of implementation. Keep business AC, native/UI/Agent behavior, real persistence,
failure/recovery, ownership and integrations in that subsystem's definition of done.

Track three independent axes: subsystem (responsibility), milestone (delivery slice), REST
operation/variant/case (compatibility evidence). A subsystem can have more than one milestone slice;
its first thin working slice does not close its full required lifecycle. A subsystem name does not
imply a Chiral node, process, project, database or interface. Keep the existing small in-process Host.

## Catalogue

| Stable ID | Subsystem | Natural REST evidence and related non-REST behavior |
|---|---|---|
| SS-HOST | Host, administration and observability | Discovery, server/nodes/global settings/health/audit, protected diagnostics and local Host composition |
| SS-IAM | Identity, accounts and access | Users/groups/roles/avatars/auth settings, credential lifecycle, first login and current scoped rights |
| SS-CFG | Projects and versioned C# catalog | Projects/buildTypes/templates/parameters/versioned settings, inheritance and the managed C#/Git apply lifecycle |
| SS-VCS | VCS, changes and source checkout | VCS roots/instances/entries, branches/changes/source browsing, exact revision and real checkout |
| SS-AGT | Agent fleet, pools and compatibility | Agents/types/pools/requirements/project associations, reported/custom facts, status/ownership and preserved AgentExplorer/upgrade behavior |
| SS-CAP | Provider and cloud capacity | Cloud instances/profiles and required start/stop capacity ownership; no speculative provider platform |
| SS-RUN | Queue, run lifecycle and build history | buildQueue and Build identity/state/outcome/custom run/cancel/approval/recovery/history metadata |
| SS-EXEC | Steps, runners, features and execution parameters | buildTypes steps/features and actual execution/parameter/output effects |
| SS-CHAIN | Build chains and dependency selection | Snapshot/artifact dependencies and selected run/input relationships, reuse and failure propagation |
| SS-TRG | Triggers and release coordination | Triggers, locks/counters, restart and single publication ownership |
| SS-LOG | Build logs and service messages | Build log ingestion/download/tail and producer message behavior, including paths outside /app/rest |
| SS-RES | Tests, problems, triage and statistics | Tests/occurrences/problems/investigations/mutes/statistics and raw-result provenance |
| SS-BLOB | Artifacts, byte storage and retention | Artifact trees/files/archives/ranges, upload references and cleanup/retention |
| SS-DEP | Deployments and publication | Deployment dashboards/instances/history, VCS labels and actual publisher outcomes |
| SS-MIG | Import, coexistence and recovery | Import/diff/retained graph and bytes, backup/restore/final delta across the existing subsystem APIs |
| SS-PROTO | REST protocol and conformance foundation | Shared locator/fields/media/error/projection adapters and independent conformance infrastructure; no separate business engine |

Workbench UI is a surface of the user-facing subsystems, not a replacement authority or a reason to
split them by endpoint. AgentExplorer retains its independent work model within shared Agent/fleet
identity. Native-only and non-REST obligations remain in AC/requirement/task links even when a
subsystem has few or no exclusively owned documented REST operations.

## Ownership rules

Top-level paths suggest boundaries; nested paths and actual responsibility settle ownership.
`/buildTypes/.../steps`, `/triggers`, `/agent-requirements`, `/vcs-root-entries` and
`/snapshot-dependencies` belong to different owners. `/builds` includes run state, logs, artifacts,
results and source views. Generic project/build feature or field variants may need finer ownership
once normalized. The initial classification is an accountable planning assignment, not proof of
installed-version semantics or a frozen scope.
Related-owner links name collaboration, not an execution dependency or a demand to finish every
related subsystem first. Freeze actual prerequisites and interface examples per delivered slice.

For project `/secure/tokens` and `/secure/values`, assign CFG as primary with IAM/Host collaboration:
the public [ProjectApi description](https://www.jetbrains.com/help/teamcity/rest/projectapi.html)
describes sensitive configuration-value references, not user login tokens. This is an ownership
inference; exact permissions/handling still need the pinned installed-profile contract. Secret bytes
do not become Git configuration merely because CFG owns that workflow.

Every known operation has one primary subsystem and related-subsystem links. At variant granularity
retain one accountable owner and explicit collaborators; do not copy the same operation into each
owner or double-count its tests. Settings edits still use the one CFG/Git validation/activation path;
the feature owner supplies its semantics. Artifact selection belongs to CHAIN, actual bytes to BLOB;
source browsing belongs to VCS and uses the shared byte boundary. Authentication/authorization and
transport/serialization mechanisms are reused, not rebuilt in every owner.

The authoritative full discovered universe stays in `teamcity-official-rest-operations.tsv`, with
stable source IDs in [api-ledger.tsv](../../.workspace/workstreams/coherent-refactor/api-ledger.tsv).
All 449 known headings retain their identity/provenance. Add primary subsystem, owning implementation
task and REST contract/acceptance task links there; do not shrink the global universe into per-owner
copies. Normalized methods/variants, additional clients/non-REST surfaces and delivery allocation
remain open work. Ownership-linked is not API-complete.

## Task structure and completion

Reuse existing subsystem implementation tasks. Attach a REST-contract subtask and a REST-acceptance
subtask **under the owning subsystem slice task**, with the same milestone. The code task owns the
finished subsystem behavior and integration; REST subtasks fix the external contract/test evidence.
Contract-ready and focused real RED still precede changed behavior (D38), while implementation is
organized by subsystem lifecycle. Do not create an implementation issue per route or a domain model
inside the compatibility facade.

[subsystems.tsv](../../.workspace/workstreams/coherent-refactor/subsystems.tsv) lists logical owners;
[subsystem-slices.tsv](../../.workspace/workstreams/coherent-refactor/subsystem-slices.tsv) links their
milestone-specific implementation/REST tasks. Milestone [api-blocks.tsv](../../.workspace/workstreams/coherent-refactor/api-blocks.tsv)
is now an aggregate of those slices, not a competing execution hierarchy. Cross-milestone integration
or contributing code tasks use explicit links; native descendants keep the owning slice's milestone.
The primary full-owner task's milestone does not postpone an already required earlier R0/R2 variant:
bind that variant to the earlier real integration slice and the same shared subsystem logic.

Use [subsystem-contributions.tsv](../../.workspace/workstreams/coherent-refactor/subsystem-contributions.tsv)
for a partial contribution whose domain full-owner task is later. It links the primary domain owner
to an existing accountable integration slice, its same-milestone implementation/contract/acceptance
tasks and bounded business outcome. It does not create another slice, owner, service or duplicate
case. R0 uses SS-HOST@R0 (#14/#43/#44); R2 Compile output uses SS-RUN@R2 (#20/#65/#66),
with source checkout/Compile code and evidence from #19/#59/#60 and catalog inputs from #18.
R2 step editing has a separate bounded SS-EXEC contribution to SS-CFG@R2 (#18/#47/#48): EXEC
supplies ordered step/property/reset semantics and CFG uses its shared C#/Git validation/activation.
For example `TCREST:buildtypeapi.replaceAllBuildSteps` keeps SS-EXEC/#22 as primary owner while
its R2 authoring variants join that CFG contribution; full runner/feature execution remains R3.
The P0 #34 representation proof and #36/#47 exact allocation still precede changed behavior;
the discovered heading is not a frozen method/variant/case or execution claim.
#19 and #20 develop their real integration incrementally; a related later full-owner task is not
a finish-to-start prerequisite. The R2 contract names usable R0 runner/queue/Agent/Storage inputs
and the additional exact checkout, parameters, report and output interfaces before changed behavior.

Concrete lookup example (discovery, **not frozen wire cases**): resolve
`TCREST:agentapi.getAgent` → primary SS-AGT/#56, `TCREST:buildqueueapi.getQueuedBuild` →
SS-RUN/#20, and `TCREST:buildapi.downloadFileOfBuild` → SS-BLOB/#26. Their required R0
variants join the corresponding contribution → SS-HOST@R0 → contract #43/implementation #14/
acceptance #44, retaining source IDs and the later #61/#62, #65/#66 and #73/#74 full-owner links.
#36 must materialize exact normalized variants/cases and delivery joins; blank seed delivery/case
fields still mean unallocated, not that these example headings or all their variants are due in R0.
Count each required case once by primary owner, with its earliest due slice and cumulative reuse;
do not require full Agent/queue/artifact lifecycles to accept the thin R0 contribution.

Publish implementation/lifecycle acceptance and API-complete separately for each subsystem slice,
then the subsystem overall, milestone and global union. Scope, bindings, results, blockers/staleness,
integration readiness and debts must stay visible. A closed issue or full ownership mapping is not
GREEN. A zero-exclusive-route subsystem needs its actual cross-cutting/native evidence; no vacuous
100% result. Unknown denominators remain unknown. Full subsystem completion includes its complete
required lifecycle and integrations, not just HTTP cases. P0 still reports baseline acceptance
separately from product compatibility.

Task #36 extends ledger validation/result binding to these joins: exactly one owner, valid task and
same-milestone native links, no missing/duplicate/unallocated required variants, no double counting,
and global/subsystem/milestone consistency. Task #42 verifies controls. The runtime checker is not
implemented by this planning change. D39's focused-test cadence, final aggregate and explicit
Security/Adversarial review, zero-debt gate and verified main-delivery closure remain unchanged.
