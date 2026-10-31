# Business acceptance review driven by TeamCity REST

Status: **36 open decomposition findings against the 110-AC baseline `f4c8c97`**.
This is an audit and proposed business outcomes, not another feature corpus, new scope exclusions,
bound acceptance tests or evidence that a documented operation works in Vivarium.

## Evidence and limits

On 2026-10-31, the public JetBrains REST documentation sitemap yielded **30 API resource pages and
449 documented operation headings**. All 30 were reviewed against the canonical business criteria.
The [operation inventory](../../.workspace/workstreams/coherent-refactor/teamcity-official-rest-operations.tsv)
records operation IDs, example endpoint templates and source URLs. The
[source receipt](../../.workspace/workstreams/coherent-refactor/rest-business-review-census.json)
records page hashes and discovery limits. These are operation descriptions, not normalized HTTP
method/path/variant counts; the matching number in the CLI README does not prove identical scope.

Read-only `@eyeauras/teamcity-cli` 0.5.2 `server status` and `server rest-info` reconfirmed the owner
reference as **2026.1.3 build 222742**, REST root `/app/rest`. Public help currently presents 2026.2
(some cached/model pages still show 2026.1). Public descriptions expose missing categories; they do
not replace the frozen installed Swagger, enabled plugins, reference responses or mutation effects.
The installed CLI exposes no Swagger-export/raw-request command. No alternate TeamCity CLI, raw
source-server HTTP calls, source mutations or real build executions were used for this review.

## How to read the findings

The [gap register](../../.workspace/workstreams/coherent-refactor/rest-business-gaps.tsv) has stable
REST-AC-* IDs, nearest existing AC, operation anchors, a proposed business outcome, unresolved
variants, owners and delivery gates. The
[resource review](../../.workspace/workstreams/coherent-refactor/rest-resource-acceptance-review.tsv)
accounts for every discovered API resource. Neither table measures runtime coverage.

- **missing**: no focused business scenario specifies this workflow. AC-AUTO-01's general
  compatibility promise and a technical requirement-family link do not substitute for it.
- **broad**: the business intention exists, but relevant lifecycle branches or distinct effects are
  absent. This does not invalidate useful existing criteria.
- **ambiguous**: the result depends on an unspecified choice or vague wording, so two incompatible
  implementations could both claim to pass.
- **tension**: a reference capability can conflict with an absolute reading of our target wording.
  Resolve the mapping explicitly; do not silently drop the reference operation or retire an ADR.
- **pending-profile**: the resource exists, but its installed-version behavior and product mapping
  are unresolved. This is not permission to classify it as optional or return fake success.

All 36 findings remain open. A nearby AC or a proposed business sentence is not an exact oracle.
Implementation acceptance still needs the frozen variants and independent expected state effects.

The [recommendations for all 36 findings](../design/teamcity-rest-recommendations.md) now separate
reference-required behavior from proposed authoring, framework and scope decisions. These are owner
discussion proposals, not adopted exceptions or newly passing AC. They also correct the earlier
overly broad repeat-enqueue assumption: the reference can optimize equivalent queued work.

## Missing or underspecified categories

| Business category | What the existing AC do not settle | Findings |
|---|---|---|
| Projects and settings editing | Copy/move/reparent/archive; inherited scope after moving; multiple/default templates; detach and keep effective settings; replace versus merge/reorder/reset | 01–05 |
| Source and Changes | VCS root versus resolved instance lifecycle; forced change check/hooks/state repair; pending/included changes, ancestry, first builds and issue links | 06–08 |
| Queue and approvals | Global pause/resume/order; required reviewers and timeout; cancellation versus new replacement run; custom options, chain reuse/selective rebuild | 08–12 |
| Agents and pools | Run policy restricted to selected configurations; pool creation/move/delete/project replacement; authorization tokens; machine type versus machine/session | 13–15 |
| Build administration | Mutable number/tags/comments/pin/status; external progress/log/problem/completion reporting; bulk effects and per-object failures | 16–18 |
| Files and results | File tree/archive/exact-build versus latest source; test identity versus occurrence; build problems; triage expiry; measurements/output/resolved values | 19–22 |
| Integrations and deployments | Real behavior behind feature/trigger property bags; publication and VCS labels; environments/instances and retained deployment history | 23, 27, 35 |
| Identity and access | Actual create/replace/delete/group/include semantics; Basic/guest/superuser/build credentials; forced logout; profiles/avatars/preferences and named tokens | 24–26 |
| Capacity and server operations | Required cloud start/stop/force-stop; backup job lifecycle, cleanup configuration and privileged diagnostics; health, audit, metrics, discovery, licenses/plugins/nodes | 28–31 |
| Cross-cutting user outcomes | Complete visible search across pages; intentional repetition versus retry; destructive effects; complete imported relationship graph | 32–36 |

The detailed register gives an English business outcome for each finding, for example: an operator
restricts a suitable machine to selected configurations; a reviewer approves a protected release;
a developer finds the builds containing a pending change; a release operator sees what is deployed
to each environment. Route names stay in the supporting technical columns.

## Five decisions with immediate architectural consequences

### 1. Source and settings may be fixed at different lifecycle points

The public [BuildTriggeringOptions](https://www.jetbrains.com/help/teamcity/rest/buildtriggeringoptions.html)
model describes `freezeSettings`: immediate collection versus collection when the build is assigned
and ready to start. The same object carries clean-checkout and selective dependency rebuild choices.
Thus "run the selected version" alone is insufficient. Add separate business examples where source
and settings change while the build waits, covering both requested modes, and verify them against
the installed reference. Do not restore a universal resolve-before-queue rule.

### 2. A result can have original evidence and an administrative status change

[BuildStatusUpdate](https://www.jetbrains.com/help/teamcity/rest/buildstatusupdate.html) exposes manual
success/failure changes with a reason. Our immutable-history statements protect original execution
facts; they must not prohibit this supported administrative workflow. Specify displayed status,
original test/step evidence, actor/reason and downstream effects separately. It is not a license for
an unacknowledged running process to become "safely stopped".

### 3. Git-only C# settings need a real mapping for Versioned Settings

[VersionedSettingsApi](https://www.jetbrains.com/help/teamcity/rest/versionedsettingsapi.html) exposes
load, commit, effective configuration, affected projects and synchronization status. Its
[configuration model](https://www.jetbrains.com/help/teamcity/rest/versionedsettingsconfig.html)
also describes current-versus-VCS build settings modes, disabled/inherited synchronization, editable
settings and Kotlin/XML-specific fields. D32's C# direction is explicit, but "a Git commit exists"
does not define these workflows. P0 must settle supported semantics and representation without a
hidden database override, a fictitious Kotlin runtime or a successful no-op facade.

### 4. Authentication reuse does not establish all compatible credential forms

The [REST overview](https://www.jetbrains.com/help/teamcity/rest/teamcity-rest-api-documentation.html)
describes bearer, Basic/`httpAuth`, guest, browser-session, superuser and temporary build credentials.
Depot-style OIDC/Identity remains the accepted implementation direction. Specify how required
client forms use that same identity authority, including current rights and forced logout; no second
credential store or database-independent superuser verifier is implied. Source credentials that
cannot be exported need explicit provisioning/rotation, not a silent compatibility exception.

### 5. Eligibility is more than requirements and enabled state

[CompatibilityPolicy](https://www.jetbrains.com/help/teamcity/rest/compatibilitypolicy.html) can
restrict an otherwise suitable agent to selected configurations. Combine it with requirements,
project/pool access, authorization, enablement, occupancy and explicit custom targeting. The business
scenario needs a case where capabilities match but policy refuses the run. Global queue pause,
configuration pause and agent disablement are different operations. Approvals add another gate;
[ApprovalInfo](https://www.jetbrains.com/help/teamcity/rest/approvalinfo.html) describes required
reviewers, timeout and chain relationships.

## Other wording that needs an exact outcome

- AC-CFG-08/09 say "supported removal" and "agreed removal policy". Replace these placeholders with
  object-specific reference cases. [AgentApi](https://www.jetbrains.com/help/teamcity/rest/agentapi.html)
  says deletion is for an inactive agent; our idle-agent wording alone does not prove that condition.
  Pinned retention protection also does not automatically define explicit-delete permissions.
- AC-BLD-03/04 and AC-RUN-03 do not specify cancel-and-requeue. Public
  [BuildCancelRequest](https://www.jetbrains.com/help/teamcity/rest/buildcancelrequest.html) distinguishes
  a new identical queued replacement for a running build from aborting a still-queued build.
- AC-CFG-03's stale-edit protection and native idempotency rules cannot become mandatory new headers
  or body-hash deduplication for unchanged clients. Repeated intentional enqueue is not necessarily
  a transport retry. Capture the observable success/failure and read-after-write boundary.
- AC-SU-08 and private resource secrecy must be reconciled with required privileged server-file and
  secure-value operations, using the actual reference authorization/content contract. Do not assume
  every server log is exportable or every privileged endpoint is forbidden merely from its name.
- License/plugin/node APIs and existing cloud workflows require explicit product mapping. Additional
  Vivarium VM extensions may be later work; a required TeamCity cloud operation cannot be dismissed
  as optional under that label. Method presence does not require a JVM/plugin system to be copied,
  but a missing mapping remains a compatibility gap.

## Next decomposition and evidence order

P0 first resolves profile/version and the five model decisions above, then allocates every reviewed
resource and its required variants to the existing milestones. Freeze contracts early; implement
each family alongside its domain. R5 remains cumulative proof, not the first occasion to discover
these semantics. REST-AC-28/31 need explicit delivery decisions, not an automatic R6 waiver.

For each selected operation family, derive: actor and intended outcome; starting states; normal,
empty, denied, invalid, concurrent and repeated cases; resulting domain state and history; cross-
resource effects; current permissions; actual client observation. Bind the outcome to an existing
AC or add a canonical scenario only once its expected behavior is precise. Link the exact wire
variants below it. Prefer the smallest real composition that proves that claim with an independent
oracle; actual machine execution still needs the real Agent and workload.

REST is the discovery spine, not the complete behavioral oracle. Runner/feature property bags do
not describe their implementations. Supplement the API review with the 97-configuration inventory,
service-message/report producers, Agent protocol, workload environment and non-REST file/log feeds.
Startup/OIDC/provider flows also need their existing business AC even where no REST method describes
them. The original 120 TC requirements and all current 110 AC remain in force; none were removed or
marked accepted by this review.
