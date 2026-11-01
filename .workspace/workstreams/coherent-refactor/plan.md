# Coherent Vivarium refactor — plan v1.22

**Execution started for US-013.** The owner assigned the accepted autonomous delivery plan with
“let's work”. Continue the integrated P0 proof/model/contract/evidence block; its actual outputs are in
[the physical checkpoint](us013-p0-execution.md) and [source/story accounting](story-source-accounting.md).
The latter checks declared links/freshness, not semantic normalization or actor/effect/variant freeze.
The [literal T3 settings proof](us013-recipe-representation.md) now fills explicit recipe-field
omissions and checks source-preserving C#/Git edits; workload script adaptation/execution stays due.
The [computed-source candidate](us013-computed-source.md) adds typed edit readback and precedence
without rewriting user C#; source feasibility does not accept modes/auth/concurrency or P0.
Reference observations and a bounded C#/Git source
experiment and an eight-case real Git/PostgreSQL physical-recovery study are available. The latter
proves a single-worker prototype, not Host/authorization/concurrent-writer integration. Product
availability remains NOT_AVAILABLE, no milestone is accepted and
R0/PILOT must wait for P0 closure. Earlier planning-only assignments below retain their historical
scope and do not prohibit this newly assigned execution.

**Active discussion: full autonomous story delivery.** The owner confirmed test-repository publication,
small checks plus one real T3 build/package without built-app launch, this Windows machine plus
Linux/Docker with owned Agent processes, a fresh isolated installation without migration/cutover, and
autonomous main merges after all due gates and independent review. C#/Git prepares the configurations;
UI manages/inspects builds, logs and artifacts, with configuration authoring in its separate story.
See the [execution-ready delivery plan](t3personal-pilot.md#autonomous-delivery-plan--ready-for-p0-execution).
All seven owner choices are resolved. The future agent starts with bounded P0 technical proof work,
not another round of unspecified owner decisions; exact variant freeze and product acceptance remain
open. The preceding planning session started no implementation or external effect. Earlier
launch/interactive-Windows/production-publication scope is superseded for current US-013 acceptance.

**Current delivery unit: US-013 / #78.** Follow the
[whole-story delivery plan](t3personal-pilot.md#current-story-delivery-plan). The owner explicitly
replaced leaf-card handoffs with story work; when the route is unclear, resolve the connected plan.
The immediate block combines definition/model/reference cases and evidence design until it has
implementable, proved outputs. Do not finish an assignment merely by completing #32, #34 or another
supporting card. Existing P0/R0/PILOT gates, subsystem owners and full publication scope remain.

**Current planning graph:** [all 18 story-led task packets are active](story-task-migration.md).
Implementation/contract/acceptance #110–#163 now own the existing slices; all 25 goals have explicit
contribution links. [Source cleanup is complete](story-source-cleanup.md): 62 old cards are closed as
superseded with original text/discussion retained, leaving 100 active Open issues. Existing product
cases/results, unfrozen scope and
P0/R0/PILOT gates remain. The [verification](story-task-migration-verification.md) includes the
existing read-only accounting check with explicit historical-evidence reuse, not new runtime proof.

**Preceding planning increment:** [source-union continuation and first Stage 2 task packet](story-transition-stage2.md).
The additional finite source joins and 160 surface-scenario candidates do not freeze variants.
#110/#111/#112 are proposed same-P0 implementation/contract/acceptance work under US-013; partial
source transfer is recorded, old task/ledger ownership remains until reconciled activation, and no
cleanup or product execution has occurred. Other story-led packets remain to be prepared.

**Latest owner direction: story-driven transition in three stages.** Start from
[25 actor-goal stories](stories.md), [API inventory preparation](api-inventory-preparation.md)
and [stage-specific dependencies](story-dependencies.tsv). Stage 1 adds 24 proposed unscheduled
stories while retaining #78 and its #79–#81 surface tracks. Stage 2 creates coherent implementation
tasks from selected story scenarios and carries useful decisions/discussion into them with stable
source anchors and readback. Stage 3 cleans redundant active tasks only after preserving the original
discussions and checking transfer/deltas. No old issue is closed, deleted or reparented in Stage 1.
Stories drive priority; D37–D40 still govern semantics, subsystem ownership and evidence gates.
The 449 intent links are not a normalized API union or proof of coverage. See the
[Stage 1 verification](story-stage1-verification.md); the technical reference sequence below remains.

**Now:** [reference pack #84](t3personal-reference-pack.md) and the independent
[server-input receipt #85](t3personal-server-inputs.md) are available: one bounded three-build
cohort, source/settings/dependency selection, small-manifest hashes and public release metadata. The replacement product is NOT_AVAILABLE;
conformance remains fifteen known cases / five RED / ten blocked / zero GREEN, denominator unknown.
**Next coherent block:** complete the integrated US-013 preparation package: actual C#/Git
representation, worked source/settings/selection model, independent entry/effect cases and executable
baseline/composition/allocation evidence. #32/#34/#110–#112/#117 are responsibilities inside that
block, not separate user handoffs. All remaining P0 gates still apply.
**After that:** remaining P0/#77/final review/debt/main → real Host/Agent #113 and cheap Smoke #115 /
R0 closure → first all-configuration/all-surface PILOT breadth preview. These steps do not waive
remaining gates or accept the full chain. See the [goal/output/blocker sequence](t3personal-pilot.md#current-result-and-the-next-measurable-steps).
The owner-selected whole-chain/default-publication scope and shared surface projections remain.
No product output, recipe, exact new case or publication execution is claimed delivered/frozen.

Status: **owner iteration; D38 executable-first, D39 closure and D40 subsystem-led delivery accepted**.
P0 now includes actual conformance implementation and its RED baseline, not only documents. Other
structural product work still follows the accepted P0 exit and milestone gates below.
Current assignment (2026-11-01): work by actor-goal stories, planning when their delivery route is
unclear. US-013 is selected. Continue its connected readiness/planning and, when the route and
accepted predecessor gates permit, implementation and verification without leaf-card handoffs.
Necessary technical work stays inside the story and existing subsystem ownership. This update
reconciles the story plan; it executes no product/reference/fleet/publication work. Earlier narrow
planning/getter assignments retain their historical scope, not a permanent implementation ban or
a new approval procedure. Concrete external effects keep their actual authorization boundaries.
Captured 2026-10-31. Lead: Reconciliation; co-held TeamCity, Platform/Persistence, Identity,
Test Steward and Docs. Existing accepted decisions remain authoritative until explicitly amended.
Owner clarification on 2026-10-31: RF-10 is accepted; each verified milestone may merge separately
into main. D34/D35 now accept Depot authentication/superuser, TeamCity RBAC 1:1 in PG,
Chiral-owned resource settings, and Depot's TypeScript/Vite Workbench UI. Remaining choices are draft.
The owner clarified that UI technology is shared with Depot while current Vivarium/TeamCity UX is
preserved. D36 moves deep REST decomposition before implementation and local Host+Agent proof into R0.
D37 adds continuous TeamCity Build/Agent superset checks and a separate business-AC → model/wire →
component evidence hierarchy. See docs/acceptance and the focused testing model; all new AC are
specified-unbound, not runtime acceptance.

## Outcome and boundaries

Replace the transitional implementation with a smaller, understandable C#/Chiral/PostgreSQL
product that can replace the owner's TeamCity without losing the behavior in the
[120-requirement contract](../teamcity-migration/requirement-catalog.tsv). Preserve all 97 source
configurations until each has an accepted migrate/retire disposition. Full retained history,
tests, logs and artifact bytes must be usable inside Vivarium. Initial import and explicit repeated
one-way diff-import support coexistence; conflict choice is whole-entity keep-Vivarium/use-TeamCity.
Unchanged `teamcity-cli` and actual other clients are acceptance consumers.

Accepted delivery model (RF-10): each milestone may be reviewed and merged into main separately
after its own acceptance gate passes. A milestone may use its own integration branch and root PR,
with small dependent changes reviewed against that parent where useful; one global integration
branch and a final all-at-once main merge are not required. Each merged increment must keep main
coherent, buildable and covered by its declared regression/scenario checks. Document which new
capabilities are usable at that point; a foundation milestone does not claim complete TeamCity parity.
Remove replaced code in the milestone that completes its replacement. Any necessary temporary
adapter must have a named owner, invariant and removal milestone, with one effective authority.
Production cutover remains a later explicit action, separate from merging a milestone into main.

Do not turn the refactor into a new generic orchestration framework, distributed microservices,
provider/image implementation, universal workflow engine or comprehensive TeamCity plugin emulator.
Existing fleet/remote-operation and upgrade guarantees must be preserved or explicitly disposed of;
unimplemented optional fleet expansion stays outside the critical path unless a real build needs it.

## Frozen present state

This section preserves the original checkpoint. The [current handover](handover.md#current-state--2026-11-01)
records subsequent product corrections, executable P0 tooling and verification limits.

The pushed tag `checkpoint/pre-refactor-2026-10-31` points to `cbb285a`: runtime from `09dd40b`
plus the migration requirements. It is a recovery/reference point, not a release or backup of live
operational data. Raw TeamCity captures remain private/ignored. No source infrastructure was changed.

The [tracked census](source-inventory.tsv), generated by `census.py` at that commit, counts physical
lines including comments, blanks and SQL. It excludes binary files and target/bin/obj/node_modules.

| Category | Physical lines | C# subset |
|---|---:|---:|
| Product (`src`) | 50,497 | 48,402 |
| Tests | 21,661 | 21,629 |
| Build tooling | 2,239 | 1,785 |
| Documentation | 16,416 | — |
| Workstream material | 3,189 | — |

The earlier chat's +60k came largely from bringing an old checkout forward, but the current product
size is real. Large hotspots include DatabaseMigrator (4,335 lines), AdministrationBootstrapService
(2,034), ManagedGitRepository (1,639), Bootstrap Program (1,605), and multiple 900–1,300-line stores.
These counts identify inspection targets; splitting files or deleting tests does not demonstrate
simplification. The bootstrap remains change-controlled under D2/D21/D30.

Already working: reverse AgentHub sessions, enrollment and Agent status axes, durable ownership,
queueing/cancellation/reconnect fencing, Git configuration reconciliation foundations, immutable
provenance, blobs, transitional UI/CLI and tests. Missing: a complete source-based build product,
Chiral/PG target, C# catalog, chains, full persistent logs/test history, import and compatible REST.
See the [existing gap ledger](../teamcity-migration/ledger.md); none of the 97 jobs is migration-accepted.

Exact SDK 10.0.303 has now been acquired and verified privately without changing `global.json`.
The [first executable cohort](../../../docs/design/teamcity-rest-first-cohort.md) records current
build/control/raw evidence. The later [local mutation cohort](../../../docs/design/teamcity-rest-local-mutations.md)
and `rest-local-mutation-results.json` established fifteen known cases (fourteen captured,
one source-established), five ready product RED and ten product-prerequisite blockers, with 31 passing
controls and a passing root build/test at that checkpoint. The later accounting and missing-evidence
receipts add provenance validation, explicit reuse and 47 ledger controls; exact run boundaries are in
the current handover. Earlier SDK failures remain historical setup failures, not product RED.

## Immediate execution order — status review, 2026-10-31

The target direction is sufficiently settled to begin the D38 executable contract slice. Replacement
code is not delivered. The current five-case ready conformance selection has actual healthy product RED;
the compatible facade and broad P0 conformance gates remain open. All 110 business AC remain
specified-unbound, and all 36 REST decomposition findings remain open.
Do not treat the remaining reference investigation as 36 product-policy questions for the owner.
The owner authorizes synthetic project/configuration/user/role fixtures and dedicated new test
Agents on the separate verified local TeamCity playground without per-fixture approval. Production
and existing fleet Agents remain excluded. Current owned mutation fixtures/PAT are cleaned up and
the lab CLI write gates are restored off; subsequent authorized acquisition must re-verify the
explicit lab target/profile and enable only its local gates.

The next deliverable is executable REST evidence in #35, with #32–#34/#36 supplying the affected
contracts alongside it. P0.REST.1–3 may progress by verified cohorts; do not finish a prose-only
whole-API specification or design a simulator platform before executing the first sound cohort.

| Order | Concrete work | Observable result / dependency |
|---|---|---|
| 1 | Reuse the verified pinned SDK 10.0.303 and required build/test baseline | Current root checks passed; retain exact receipt inputs and rerun only at required code checkpoints; an SDK error is not product RED |
| 2 | Freeze first cross-family reference cases under the pinned server/client profile | Exact requests/responses/effects and per-assertion sources; uncertain values reference-blocked. Obtain missing installed-schema/raw-wire export through approved CLI tooling without blocking already grounded cases |
| 3 | Prove the small NUnit composition and strict replay/assertion controls | Real requests, deliberate bad-response detection, independent readiness and owned reset/cleanup; controlled actors/time only where required; measured cost, no new test language |
| 4 | Run that cohort against the actual current Vivarium HTTP boundary | Genuine RED/GREEN/product-blocked report and regression baseline before replacement; a healthy missing route is RED, repeated dependency failures are not independent semantic defects |
| 5 | Expand verified cases across the required families while closing #32–#34/#36 | Broad discoverable coverage, exact variant allocation and model/mutation proof. No guessed expectations or self-generated oracle; preserve earlier GREEN |
| 6 | After P0 exit, deliver the new-stack Host+Agent/unchanged-CLI loop in R0 | R0.1 local real farm, R0.2 actual compatible smoke, R0.3 restart/cleanup; not a prerequisite for first current-product RED |

Highest-risk contracts to settle early:

- Reference fidelity: installed 2026.1.3 versus rolling docs/older public source; raw wire versus CLI
  output; complete method/variant/client inventory. The current inventories are discovery, not proof.
- Shared run identity/state: one-cell/matrix/composite mapping, queue-to-history continuity,
  imported/native allocation and state versus outcome. Resolve before replacement schema/commands.
- Editable C# and mutation completion: owned/effective settings, reset/replace/reorder, computed
  fields and managed/user source boundary, Git commit versus activation and read-after-write.
  Prove the proposed representation with real edits before adopting it; no hidden override store.
- Actual credential/permission behavior: framework authentication is chosen, but exact compatible
  transports, token lifetimes, current rights and special build/installation identities need cases.
- Historical graph and byte ownership: retained relationships/IDs, replace conflict unit, immutable
  published history and Storage transfer/retention limits. Model constraints precede PG design;
  full inventory/download and migration execution do not block the first REST tests.

Real discoveries that change a product promise or legacy-data retirement need an explicit owner
decision. Other unknown TeamCity defaults/errors/effects require evidence, not a new Vivarium policy.
Detailed panel composition, extra provider implementations and fleet rollout are outside the first
test-slice critical path. Required server/plugin/cloud operations remain allocated/open, not waived.

## Target ownership and reuse

One ASP.NET Core Host owns in-process Chiral logical resource nodes. Proposed boundaries are Host,
Database, Identity, Builds, Agents and Storage; P0 must justify/merge these by actual settings and
lifecycle ownership. A node is not a project per class or a mandatory remote service. The agent is
still a separate cross-platform workload process connected through the existing reverse protocol.

PostgreSQL/EF Core owns business state: catalog projections, durable queue/build/chain transitions,
claims/fences, results, import lineage, authorization state if approved, and audit. Use explicit short
transactions and database constraints at actual race boundaries. A durable outbox is added only for
a proven commit-to-external-delivery boundary. Chiral InMemory is local messaging, never queue durability.
Do not introduce a distributed Chiral PostgreSQL transport just because business storage is PostgreSQL.

Git owns trusted build definitions and desired fleet configuration. Chiral's private file storage owns
resource settings and their restart/CAS contract. Credentials and runtime state stay out of Git.
D34 explicitly makes current users, groups, editable TeamCity roles and scoped grants PostgreSQL data;
the old Git-RBAC authority is retired by the replacement, not retained alongside it.
No parallel YAML/C# authoring authorities survive final acceptance.

Use real EyeAuras.Chiral, EyeAuras.Storage and Workbench contracts. D34 selects ASP.NET Core Identity,
OpenIddict and EF for Depot-style authentication; TeamCity supplies the exact authorization semantics.
D35 selects pinned Workbench + TypeScript/Vite, without requiring React or a frontend runtime server.
Depot is an example,
not a dependency that must run beside Vivarium, and it does not expose a ready-made shared Identity
package. Copying its complete Host or inventing a shared platform prematurely would retain complexity.
See [reuse evidence and limits](depot-study.md) and [pending decisions](decisions.md).

`/api/v1`, Workbench UI, CLI and `/app/rest` call the same domain services, authorization and appropriate
Git/PG/Chiral mutation path. No separate TeamCity database, alternate scheduler or permissive compatibility administrator.
Preserve old-agent protocol compatibility at the frozen protocol boundary; retire transitional
ControlPlane and SQLite only after named consumers/data have a verified replacement or disposition.

## Mechanisms that must be specified before implementation

| Mechanism | Required specification/output | Delivery owner |
|---|---|---|
| Host readiness/settings | Node IDs, dependencies, init/migrate/run order, masked schema/CAS, restart application, persisted key paths | R0 |
| Login/current authority | OIDC issuer+sub mapping, normal Identity login/logout, superuser lifecycle, current account/grants, token issuance/revoke, CSRF | P0 contracts; installation login plus minimal ordinary OIDC/My keys/current-grant R0 contribution; complete IAM R1 |
| C# catalog | Trusted settings revision vs source revision, pinned evaluator inputs, bounded separate process, immutable semantic plan, inheritance/provenance, last-good activation | R2 |
| Editing desired configuration | Exact REST/UI-editable C# representation, generated/user-owned boundaries, Git CAS/review/activation and unsupported expression behavior | P0 decision, R2 |
| VCS/workspaces | Exact checkout/submodules/rules, branch selection, private credentials, caches and OS isolation | R2 |
| Scheduling | Status transition table, atomic dequeue/claim, session/assignment fences, restart/re-adoption, cancellation acknowledgment and ambiguous-capacity quarantine | P0 model; minimal real loop R0, full catalogue-aware R2 |
| Agent policy | Reported vs custom facts, requirement operators, compatible/incompatible reasons, pools/auth/enabled/connected axes, maintenance vs stop | R2/R3 |
| Build graphs | Dependency selection/reuse key, exact revisions/artifacts, failure propagation, same-Agent policy, locks and release ordering | R3 |
| Runners | Process/C#/.NET/script/container mappings for observed jobs, environment/parameter/service-message semantics, termination and exit classification | R2/R3 |
| Triggers | VCS/finish/schedule state, deduplication, branch rules/timezones/quiet period, restart and coexistence ownership | R3 |
| Logs/results | Bounded durable segments and cursor/gap contract, redaction, service-message parser, TRX/JUnit normalization, test identities/occurrences/history | R2 basics, R4 completion |
| Artifacts/storage | Immutable manifest/hash/streaming, dependency mapping, retention/pins/GC, interrupted upload, package feed disposition via Depot | R2 basics, R3/R4 completion |
| Native/compatible API | Auth, IDs, locators/fields, JSON/XML/text, deterministic paging, files, mutations/idempotency, errors and events | Deep P0 breakdown, first real subset R0; each domain slice; cumulative R5 |
| Import | Authoritative bounded inventories, stable source IDs, unit-of-conflict/dependency rules, resume/checkpoints, history vs desired C# activation, final cumulative delta | P0 sizing, R6 |
| Recovery/delivery | Owned fixtures, repeatable backup/restore including keys/settings/blobs, canary/rollback/write boundary, supported OS evidence | R0 contour, R6 acceptance |

Every row needs inputs, state owner, outputs, failure semantics and a scenario; a class diagram alone
is insufficient. P0 closes unresolved choices in [decisions.md](decisions.md), not by guessing in code.

## Milestones and acceptance order

| ID | Observable increment | Exit evidence |
|---|---|---|
| P0 | Executable reference-backed REST contracts and a genuine RED baseline precede structural replacement | P0.REST.1–3 corpus/replay/product baseline executed with evidence and explicit blockers; resource/ID/state/command, protocol and C# mutation/authority proof reviewed; required variants allocated; no unknown mapping hidden behind a count |
| R0 | An operator runs a local Host plus real Agent and the first compatible smoke job | R0.1 real PG/Storage/Host/Agent admission/debug and ordinary credential readiness; R0.2 unchanged CLI success/nonzero/queued and running cancel/exact artifact bytes; R0.3 restart/reconnect/owned cleanup. Installation login plus minimal ordinary OIDC/My keys, SYSTEM_ADMIN and restricted project principals/current allow-deny for exposed actions; full IAM R1 |
| PILOT | Build the full actual T3Personal chain across UI, direct compatible REST and unchanged CLI | Breadth levels: full recipe/capability/AC+wire matrix; truthful all-config/surface discovery/auth/state; real DAG/Docker/PowerShell/interactive launch/artifact/publication; fault/recovery/access/cumulative acceptance. #78/#82/#83 and surface projections #79–#81; all NOT_AVAILABLE |
| R1 | Ordinary identity/access and compatible lifecycle work beyond the R0 subset is complete | Complete roles/groups/project inheritance, provider/account/token/PAT wire variants and legacy disposition; preserve R0 current-cookie/token grant, withdrawal, expiry/revoke and key/restart guarantees |
| R2 | A user authors a full C# catalogue and runs a genuine source-based Compile | Extend R0 catalogue/execution with full C# evaluation/editing/inheritance, exact checkout, requirements/reasons and report/artifact provenance; same existing PG scheduler, Agent and compatible/native run services |
| R3 | Existing build families can express and execute their complete chains | Parameters/inheritance, all observed runners/features, chain/artifact policies, counters/locks/triggers, paused jobs and publish gating; representative Windows/Linux workflows |
| R4 | A developer diagnoses a failure and compares historical tests/logs/artifacts | Persistent logs/tail/search/download/gaps, TRX+JUnit identities/history/duration, problems/investigations/mutes/statistics, permissions and retention/GC; real UI workflows |
| R5 | Existing TeamCity clients pass cumulative cross-family conformance | Finish agreed remaining operations and all method/path/variant/client/negative/files checks built since P0/R0; this is not the first REST analysis or adapter implementation |
| R6 | A local full migration rehearsal supports independent operation and final switch | All 97 dispositions and 120 requirements evidenced; full history+bytes/import replay+diff choices, coexistence ownership, final delta, encrypted restore, real family/OS tests; old implementation removed |

Near-term dependencies: accepted P0.REST.1–3 and shared structural/representation/allocation gates → accepted R0 → PILOT.
PILOT pulls only its required production contributions forward; full later-owner completion is not a
prerequisite. All actual Windows/Release/used trigger/default-publication behavior is due in PILOT.
Existing P0 gates, cumulative earlier GREEN, end-milestone review/zero-debt/main delivery remain.
Windows, coherent chain artifacts and default publication are breadth levels within PILOT, not
follow-up-only scope. After PILOT, plan other families and history/diff-import stories. R1–R6 below remain full-scope owner/completion backlogs and cumulative
gates, not blanket prerequisites before each next product story. Allocate its exact next milestone
and needed partial contributions before implementation, after predecessor closure; do not create all
later milestones now, accept broad owners early or move failing due/previously GREEN cases.
P0 starts with the [D38 executable REST suite and initial RED baseline](../../../docs/design/teamcity-rest-test-first.md).
P0.REST.1 freezes independent expectations, P0.REST.2 proves minimal deterministic production-code
composition/cost before bulk expansion and runs strict WireMock.Net/client controls,
and P0.REST.3 executes the same corpus against the healthy actual Vivarium boundary. Hundreds or
thousands of reviewed variants are expected as coverage expands; missing reference/setup is not
product RED. Preserve standard raw results and a monotonic known-RED/regression ledger; each main
milestone accepts its declared delivered set while the remaining full-suite RED stays visible.
The simulator controls time and external deliveries, not product decisions. Use cheap in-process
evidence where sufficient and real PG/Git/Storage/transport/Agent checks where required; no universal
WireMock-backed TeamCity engine or new scenario interpreter. Thousands of variants should primarily
add reviewed data, not framework logic. Composition/timer/isolation and cost proof belongs to #35.

P0 also includes resource/ID/state/authority schemas, protocol fixtures, C# mutation proof and per-operation
milestone allocation, not just counting routes. See [REST breakdown](rest-breakdown.md) and
[local lab](local-lab.md). Full historical byte transfer and every late source-property value do not
block local Host/Agent development; they retain explicit owners/gates before affected migration.
Storage/history identities, UI diagnostics and the compatible execution profile start in R0.
Domain-compatible REST and scenarios grow with each domain milestone. R5 is the final
conformance gate, not permission to discover client contracts late. R4 completes existing diagnostics,
not their first appearance. Work may overlap only where prerequisites are explicit and stable.

Current GitHub planning graph: **9 milestones, 9 closure parents and 75 native sub-issues**
(84 open issues, including #77, six PILOT issues and reference leaves #84/#85). These counts are task state, not product progress.
The refreshed issues.json comes from current metadata/native parents; older counts below are historical.
Subsystem slices parent their REST subtasks; #12 also owns the cross-cutting P0 foundation. Start at
[P0 / roadmap #2](https://github.com/iXab3r/Vivarium/issues/2); milestone parents are
[R0 #3](https://github.com/iXab3r/Vivarium/issues/3),
[R1 #4](https://github.com/iXab3r/Vivarium/issues/4),
[R2 #5](https://github.com/iXab3r/Vivarium/issues/5),
[R3 #6](https://github.com/iXab3r/Vivarium/issues/6),
[R4 #7](https://github.com/iXab3r/Vivarium/issues/7),
[R5 #8](https://github.com/iXab3r/Vivarium/issues/8) and
[R6 #9](https://github.com/iXab3r/Vivarium/issues/9).
All issues remain open. `issues.json` records exact links and hierarchy, including PILOT #78–#83.

Early concrete tasks: [REST breakdown #12](https://github.com/iXab3r/Vivarium/issues/12),
[resource/state model #32](https://github.com/iXab3r/Vivarium/issues/32),
[protocol #33](https://github.com/iXab3r/Vivarium/issues/33),
[mutation/C# proof #34](https://github.com/iXab3r/Vivarium/issues/34),
[executable conformance and RED baseline #35](https://github.com/iXab3r/Vivarium/issues/35),
[mechanical API-ledger/scope accounting #36](https://github.com/iXab3r/Vivarium/issues/36),
[local Host+Agent #37](https://github.com/iXab3r/Vivarium/issues/37) and
[first unchanged-CLI run #38](https://github.com/iXab3r/Vivarium/issues/38).

D37 work: [business AC/model baseline #39](https://github.com/iXab3r/Vivarium/issues/39),
[first canonical bindings/base suite #40](https://github.com/iXab3r/Vivarium/issues/40), and
[explicit disabled-Agent custom-run variant #41](https://github.com/iXab3r/Vivarium/issues/41).
The last finding corrects an over-broad normal-queue rule; TC-AG-01 retains its ID and the total
technical requirement count stays 120. Reference execution and replacement implementation remain open.

Each milestone has a parent issue and bounded child deliverables. D39 makes GitHub authoritative for
live task/closure state; structured ledger/results own compatibility evidence. Workstream summaries
must follow those authorities, not become another task board. Target counts are not coverage. Do not close implementation issues because this
plan was written or code was committed: close after their own gate passes and their milestone has
been accepted in main. Preserve verified evidence while that milestone remains integration-pending;
completed earlier milestones do not wait for R6 to close their issues.

D40 makes the [16 logical subsystems](subsystems.tsv) the units of implementation and completed
lifecycle/integration. [18 subsystem slices](subsystem-slices.tsv) bind owning code tasks to native
REST-contract/acceptance children, including separate P0/R5 protocol-foundation slices. Reuse the
existing code tasks; new owners #56/#57/#58 cover Agent fleet, required capacity and deployments.
For example VCS #19 owns REST #59/#60; queue/run #20 owns #65/#66; results #25 owns #71/#72.
[api-blocks.tsv](api-blocks.tsv) aggregates these slices by milestone; #2–#9 retain closure gates.
Do not implement endpoints as independent work units or equate this catalogue with extra Chiral nodes.
The [API ledger](api-ledger.tsv) preserves all 449 discovery headings and links each to one primary
subsystem, related owners and REST/code tasks. Exact methods/variants, case bindings and delivery
allocation remain open; the primary owner's milestone does not postpone earlier required cases.
[Status](api-status.json) separates subsystem lifecycle/integration, API and milestone/global status.
All remain UNVERIFIED/NOT_ACCEPTED, with no compatibility percentage or completed mechanical
report. Seven discovery headings now have any bound reference/replay case (three have ready product
cases); 442 have no binding. These partial first-variant bindings do not establish full allocation.
Task #36 implements the small mechanical validator/result binding; #42 verifies it and
the actual RED baseline. Current bounded tests are recorded above; no product API-complete,
final review PASS or debt clearance is claimed.

### Earliest hands-on checkpoints

The [R0 journey manifest](r0-journey-manifest.md) makes #36's existing freeze obligation concrete:
actual selected requests, ordinary/admin/restricted principals, activated P/C and protected Q/D seeds,
Agent/workload states, byte/readback/nonzero/cancel/restart effects and source-versus-reference gaps.
It remains UNFROZEN, consumed by #43/#14/#37/#38 and verified by #42/#44. The existing IAM→HOST
contribution pulls only the ordinary credential/current-grant subset forward from #16/#17; no full
R1 prerequisite or duplicate owner is added. Depot's installation account cannot issue My keys.

R0.1 is the first usable new-stack local lab: real server + Agent, PG/Storage, normal enrollment and
authorization, current-style UI and debugger attach. R0.2 adds unchanged teamcity-cli against a
seeded real configuration: enqueue/watch/cancel/download; R0.3 proves bounded restart/reconnect
and cleanup. These are concrete internal checkpoints, not runtime already delivered by the plan.
The lab can be exercised on its milestone branch before all R0 acceptance is complete; merge still
requires the declared regression/review gates and existing-feature disposition. PILOT is the first targeted real source product after R0; R2 completes general source/catalogue
capabilities. R0 checkpoints stay branch previews until its existing whole-milestone closure; no R0 split.

The current legacy process entry points/protocol tests and exact verified SDK exist today;
the new-stack reproducible lab is not delivered. Do not equate those facts with an end-user local
deployment claim or invent calendar estimates before the preparation and port work are measured.

## Development and test contract proposed for this workstream

Owner clarification: TeamCity determines the handling of adopted model operations, not a new
Vivarium policy choice. Freeze reference defaults, guards, permissions, transitions and effects;
dispatch native and compatible requests through that same semantic processing. Internal C#/PG/
Chiral/framework implementation options must satisfy the contract. Unknown results are verification
work; no blanket scope exceptions are adopted. Optional extension controls must be neutral when absent.

D37's [testing model](../../../docs/design/testing-model.md) is authoritative for evidence selection.
The canonical [business AC](../../../docs/acceptance/README.md) contain no implementation detail;
acceptance-map.tsv connects their 110 stable IDs across subsystem folders to TCM invariants, the original TC requirements and
S* supporting candidates. These initial family-level links are planning traceability, not coverage.
Highest-level words stay stable through architecture changes. Gherkin does not mandate E2E, and the
table below describes possible evidence boundaries rather than one mandatory test per pyramid level.

Write the English Gherkin/user examples and cheapest meaningful public-contract test before the
implementation. A product failure is RED; missing binding, selector error or absent SDK is a test/setup
failure. An already-passing regression is baseline evidence, not an invented RED. No fixed testing
percentage, arbitrary scenario count or assertion against private implementation shape.

| Layer | Purpose and actual dependencies | Typical checks |
|---|---|---|
| Logic (largest) | Pure plan/parser/policy transitions with virtual time | Requirements, inheritance, graph validation, selectors, import conflict classification, service-message escaping |
| Component/contract | Actual PG/EF transactions and constraints; actual storage and Kestrel/protocol boundaries | Concurrent claims, CAS, result fencing, retry/idempotency, revoke with same token/cookie, bounded streaming |
| Simulation | Real domain services plus controllable external actors | Agent disconnect/drop/duplicate/stale ack, VCS events, TeamCity pages/bytes/errors/deltas, fake OIDC upstream failure |
| Acceptance (small, independent journeys) | EyeAuras.Automation + English Gherkin/Reqnroll; real Host/PG/storage; Playwright for UI; stock clients | Login, grant, run, cancel, inspect test history/log/artifact, same-session revoke, unchanged CLI, import/replay/restore |
| Real platform/family (few) | Actual Agent processes, Windows/Linux initially; supported macOS evidence, actual source/toolchains | Checkout, process containment/kill, runner outputs and selected full chains, old-agent compatibility where a release exists |

Simulators implement external boundaries, not a second scheduler/database/identity/storage system.
Start with minimal scripted Agent/TeamCity/VCS behaviors and grow them only from named failure cases.
Pure fake-time tests establish timeout ordering; real PostgreSQL tests establish transactional behavior;
actual Agent/OS tests establish process containment. None substitutes for the others.

Use EyeAuras.Automation as a pinned development source dependency, its CLI/process ownership and
selection model; do not write a custom Gherkin runner or add Python orchestration. Consumer fixtures
own product setup/reset and bindings. One shared fixture composition supports prepare/start/attach/
run/reset/stop and automated acceptance from the first executable milestone. Package/tool/browser
downloads happen during explicit preparation; prepared runtime inputs have fingerprints and execution
does not silently restore or access production. Use an owned closed Linux contour where feasible;
Windows/macOS native checks declare their actual isolation separately, never relabel loopback as
network-none. Credential browser diagnostics stay disabled; safe receipts contain no tokens/raw DOM/HAR.

Independent user journeys are catalogued in [scenarios.md](scenarios.md). UI mutation acceptance uses
real UI actions; API/DB shortcuts may prepare fixtures but cannot prove that the UI action works.
Keep developer, viewer, project admin, fleet admin and unprivileged user distinct; an omnipotent admin
running every acceptance test would miss the most important access boundaries.

During TDD use focused changed-case/regression checks. At coherent handoff/milestone checkpoints
batch required root build/test and due real PG/Gherkin/client/OS/cumulative evidence; reuse valid
unchanged results. Status queries read results without running tests. No full matrix per edit/task.
At milestone end perform final independent aggregate correctness/domain/simplicity and explicit
Security/Adversarial review against the exact head/base/target. Fix all actionable findings and
introduced/exposed debts through same-milestone tasks, verify narrowly and renew affected verdicts.
No milestone closure or next-milestone implementation before zero debt, acceptance and verified
main delivery. R6 proves full cumulative acceptance. No review or runtime PASS is claimed here.

Every such merge includes affected business/model/wire cases plus a small cumulative implemented
TeamCity base suite and relevant extension-neutrality checks. A new native capability is not a waiver
for changing an existing Build/Agent meaning. Keep later unbound AC visibly open; preserve useful
assertions on test migration and an independent oracle. R5 remains cumulative conformance, not the
first checkpoint for model drift. Business Acceptance/Testing Architect/Test Steward have distinct
intent/selection/execution responsibilities and may be co-held without claiming independent review.

## Simplicity and replacement acceptance

P0 classifies every tracked product path as keep, adapt, replace/remove or deferred-preserve, with
owner, invariant/scenario and replacement. Initially inspect SQLite migration/store proliferation,
bootstrap administration saga, duplicated auth/audit/mutation adapters, Git layer, page/CLI orchestration
and custom blob lifecycle against real shared-library capabilities. No automatic deletion by filename.

At each milestone remove the stores, retired-engine migrations, unused DTOs, adapters, UI/routes and
internal-structure tests that its replacement makes obsolete; R6 verifies cumulative completion.
Retain behavior tests,
protocol/security guarantees and any required one-time legacy-data reader with an explicit owner and
retirement condition. No new general repository/unit-of-work facade over EF, generic workflow engine,
speculative plugin architecture or one project/interface per service.

Compare product LOC and dependencies to the checkpoint, with explanations per subsystem, but set no
unfounded “20k lines” target. Required simplification evidence is fewer independent authorities and
custom mechanisms, smaller owners and no parallel implementations. New parity features may add code;
report that separately from replacement savings. A final “cleanup later” backlog does not close R6.

## Next iteration

Superuser/authentication, TeamCity RBAC/PG authority and Depot UI composition are now accepted in
D34/D35. Review remaining choices, especially C# editing, exact wire variants, storage integration and
history conflict units; refine milestone scenarios and issue acceptance. Only after those choices
affecting the R0 structure are recorded as accepted contracts should the executable R0 branch begin;
late-family capture/variant work keeps its own explicit gate and must not silently expand that prerequisite.
The old [migration phases](../teamcity-migration/phases.md) are discovery scaffolding; this draft
proposes a coordinated replacement sequence without yet overriding the accepted roadmap.
