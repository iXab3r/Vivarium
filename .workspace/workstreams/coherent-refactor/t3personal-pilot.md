# First real product: the full T3Personal chain

## Current story delivery plan

Owner direction, 2026-11-01: work through actor-goal stories; when the route to a story is unclear,
resolve its plan. **US-013 / #78 is the active unit of delivery.** Lead: Story Delivery; co-held
TeamCity, Testing Architect, REST and Docs perspectives. Technical cards identify responsibilities
inside this story. Completing a receipt or model card is not a handoff or a reason to ask the owner
to choose the next leaf. Continue the connected work until a coherent story checkpoint or a real
unresolved decision/external execution boundary is reached.

The release operator must be able to sign in, inspect all three configurations and readiness,
start the ordinary Release configuration through UI, compatible REST or unchanged CLI, follow its
actual upstream work, retrieve launch-checked coherent Linux/Windows assets and identify the
configured publication result. Denial, source/runner failure, waiting, cancellation and recovery
must remain truthful. The full promise includes default publication and used trigger/coexistence
behavior. Build-only/off is an intermediate result, not completion of US-013.

### Assessment and immediate work

**Planning is currently necessary before structural implementation.** The current controller still
references SQLite and has the existing queue/Agent/Git/native-REST foundation, not the accepted
C#/Chiral/PostgreSQL catalog and compatible full-chain product. #84/#85 give usable independent
reference inputs; they do not supply a complete definition/editing/selection model or frozen
entry/effect cases. P0's baseline/composition/allocation and final review/main gates remain open.
This is an identified readiness gap, not a proposal to restart the already accepted architecture.

**The current work block is the integrated US-013 preparation package**, spanning #110/#111/#112,
#32/#34 and the existing story contract #117. Finish its definition, model, reference cases and
evidence design together; do not return after each technical card. Its concrete exit output is an
implementable full-chain recipe with a proved C#/Git representation, exact independent due cases,
an executable honest baseline and a bound implementation/acceptance sequence. A prose plan alone
does not satisfy this exit. Broader P0 obligations must also close before R0 implementation.

| Story checkpoint | Concrete output the operator or implementer can inspect | Existing owning work and readiness |
|---|---|---|
| Ready to implement the whole chain | Three attributed definitions, selected-source/settings/Build model, due UI/REST/CLI scenarios and independent assertions; real definition/edit/evaluate/activate/recover proof and representative baseline/composition results | Current integrated preparation block; #110–#112 with #32/#34 and #117. P0 remains unaccepted. |
| A runnable installation and cheap real run | Actual Host URL, login/current grants, Git catalog, PG/Storage and separate Agent; unchanged CLI can run, observe bytes, fail, cancel and recover LocalSmoke | Thin US-001/US-004/US-006/US-009/US-010 contributions through #113–#115. Accepted P0 precedes R0; full later stories are not prerequisites. Smoke does not accept T3. |
| Whole-chain visibility | All three real configurations and their dependencies, Agent requirements/readiness, queue/build identities and diagnostics are visible across UI/REST/CLI | #116–#118 plus ten existing PILOT contributors. No serial UI-then-REST-then-CLI work order or fake runnable buttons. |
| Real chain and bytes | Release-entered Prepare→Windows→Release work, exact selected inputs, Docker/PowerShell runners, real Linux/interactive Windows launch checks, common version, retained logs/manifests and actual staged/downloaded bytes | RUN/CFG/VCS/AGT/EXEC/CHAIN/LOG/BLOB contributions below. Off-mode evidence is labelled; publication remains due. |
| Default publication and used automation | A causally attributable default-publication result, selected tag/assets, and controlled used trigger/quiet-period/exclusion/single-publisher behavior | SS-DEP #155 and SS-TRG #143 with #117/#118. Concrete external execution scope must be ready at execution; no production trigger activation follows from planning. |
| Accepted story | Current-access denial and byte controls, upstream failure/cancel, selection/retry/restart and publication/recovery evidence; cumulative prior GREEN, zero due debt, independent milestone verdicts and verified main delivery | #118 and existing milestone gates. Report story availability separately from full subsystem/API completeness. |

These are checkpoints within the existing P0 → R0 → PILOT and breadth levels, not new milestones,
new stories, a second task graph or waivers of D37–D40. Once a checkpoint's implementation route is
clear and its predecessors accepted, execute it continuously under this story. Do not wait for
unrelated full IAM, general editing, every runner, providers or global history implementation.

### Decisions grounded now

The worked reference model starts from the independent [#85 receipt](t3personal-server-inputs.md):

| Identity or relationship | Concrete reference and consequence for the story |
|---|---|
| Configuration versus Build | Release is `T3Personal_Release`; the observed run is 32726. Windows 32725 and Prepare 32724 are separate Builds. No composite parent, task ID or shared version is invented as their identity. |
| Requested versus resolved run | The receipt observes completed runs, not the request that created them. Preserve the returned-request/reuse relation as a due controlled-reference case; do not infer a universal new-ID or reuse policy from this cohort. |
| Source versus settings | All three source revisions are `7ff65cada…`; settings revisions are `7495b685…`. Keep distinct immutable historical fields and their root-instance/checkout provenance. Matching settings file bytes do not collapse these identities. |
| Dependency selection | Windows reports snapshot/artifact Prepare 32724. Release reports snapshot Windows 32725 and artifact producers Windows 32725/Prepare 32724. Retain these literal results; the documented snapshot closure discrepancy is an independently testable wire/model gap. |
| Produced versus consumed bytes | #84 proves the downloaded producer manifests, not every consumed file. Freeze the selected producer/rule/destination and compare actual producer→consumer bytes in the controlled and real-chain evidence. |
| Historical versus current settings | Current effective mode is `alwaysUseCurrent`; the receipt does not establish the mode at execution or a settings-hash-to-revision mapping. Freeze the due timing/default variants from controlled reference effects. |

Use the server-attributed `7495b685…` settings/source cohort as the known reference example.
The later `041345f2…` trigger exclusions remain a named used-behavior delta due before total story
acceptance; never attribute them retroactively to that older run or silently drop them. A future
normal entry records its actual resolved versions rather than assuming the CLI has a revision flag.

The three-definition representation must express the source checkout, literal Agent requirements
with explicit owned-fixture mapping, actual ordered runner steps, timeouts, snapshot/artifact rules,
counter/version/message effects, default `T3_PUBLISH=release`, and used trigger settings. Git owns
these desired definitions; PG owns runtime identities/selection/queue/history/access; Chiral owns
private Host connections/settings; Storage owns bytes with PG references. Both HTTP projections
and UI use the same owning services. Exact C# API/file/editor/evaluator mechanics remain a required
real #34 proof, not an assumed SDK hidden in a plan.

### Remaining planning and proof inside the current block

| Unclear part | Resolve it as one connected story output | Sufficient evidence and next implementation decision |
|---|---|---|
| Definition and input model | Work the three definitions through source/settings/Build identity, selected artifact mapping and edits into one effective immutable execution plan | Actual before/after C# files, evaluator invocation, Git revision/activation, invalid edit and recovery outputs; independently expected effective values. Then choose the smallest proved catalog/editor implementation. |
| Entry, defaults and lifecycle | Establish selected UI/REST/unchanged-CLI entry, returned/resolved identities, permission checks, queue/start/terminal/cancel/reuse and source/settings collection boundaries | A bounded three-configuration owned reference fixture on the isolated TeamCity lab, current allowed/denied actors and exact request/response/effect/byte receipts. Use tiny workloads for semantics; no repeated T3 rebuild per negative case. Replay records HTTP and does not implement another scheduler/model. |
| Test and delivery composition | Bind those due cases, real boundaries, fixtures/cleanup and prerequisite slices to the current ledger and owners; include the remaining P0 breadth/allocation obligations | Production-code composition with controlled time/external faults where sufficient; real Git/PG/Storage/Agent/OS where claimed. Prove isolation/cost and record real product RED or retained GREEN. Unknown cases remain gaps, never guessed expectations. |

Missing historical per-file consumption does not force an endless search of the old run: independent
controlled producer/consumer evidence can establish the due semantics, and actual PILOT bytes must
still be proved. Unrelated full operation variants stay with their due owners; shared structural/R0
unknowns and required cumulative behavior cannot be deferred to make this story appear ready.

The evidence plan uses real entries from all three surfaces and the smallest sufficient causally
attributed default-publication proof through the shared producer boundary, with explicit reuse and
off-mode differences. Freeze the exact mapping before execution; do not assume that viewing one run
three ways proves three entries, or that the three entries require three identical live publications.
Use separately owned Linux and interactive Windows Agents, not Cvat/laptop-g15. Prepare private
credential references and the exact external target/effect before any necessary operator approval;
there is no new product approval state or hypothetical confirmation step in ordinary local work.

**Reporting:** state the operator outcome now available, the story checkpoint reached, the remaining
story blocker and verification. Put leaf IDs in supporting links. Current availability is still
NOT_AVAILABLE; no new executable proof, milestone acceptance or product implementation is claimed
by this planning reconciliation. The 25-story / 18-packet / 54-task graph is unchanged.

## Reference and preserved detailed scope

The detailed material below retains earlier scope/evidence context. Current work order and handoff
come from the story plan above; historical “next #85” and superseded task IDs are not a leaf-work queue.

**Now:** NOT_AVAILABLE in the replacement product. The owner chose **breadth-first**: preserve the
whole actual Prepare → Windows → Release configuration and default-publication behavior across UI,
direct compatible /app/rest and unchanged CLI. The earlier Prepare-first recommendation is superseded.
**Reference available now:** [pack #84](t3personal-reference-pack.md) pins one bounded same-version
cohort, actual small-manifest bytes and public release metadata without claiming server-selected inputs.
**Independent inputs now available:** [#85 receipt](t3personal-server-inputs.md) records actual
source/settings revisions and selected producer Build IDs. Per-file consumption and historical
settings-mode proof remain gaps. **Next:** #111/#32/#34/#112 establish due wire/control,
worked-model and actual C#/Git proof outputs. Accepted P0/R0 still precede product implementation.
**Later:** other families and broader full subsystem/history scope. Used Windows, artifact handoff,
Release publication and trigger behavior cannot be deferred to those broader owners.

## Current result and the next measurable steps

| Goal | Output / where to open | Blocker or owner |
|---|---|---|
| 1. Pin the existing reference result | [#84 evidence pack](t3personal-reference-pack.md): Build 32724/32725/32726, actual three small-manifest byte hashes and public release/tag metadata. | Metadata available; server inputs/actual DAG/binary bytes/causal publisher proof remain open. |
| 2. Consume independent inputs and establish exact contract proof | [#85 receipt](t3personal-server-inputs.md) supplies source/settings and selected producer IDs. Then #111 literal wire/control cases; #32 returned/related/reused identity trace; #34 actual source diff/editor/evaluator/effective-plan/recovery pack; #112 exact case/principal/oracle freeze. | Receipt available; per-file consumption, historical settings mode and the snapshot-list documentation discrepancy remain explicit gaps. |
| 3. Close P0, with all its remaining obligations | #42/#2 baseline/controls/composition/allocation/disposition evidence, #77 debt disposition, final independent aggregate+Security, zero debt and verified main delivery. | Steps 1–2 alone do not finish P0. Existing remaining gates stay due. |
| 4. Open the real local technical prerequisites | #37 actual Host/PG/Storage/login/Agent URL and owned lifecycle receipt; #38 actual LocalSmoke CLI/run/log/byte/failure/cancel evidence, plus #14/#44 recovery and R0 main closure. | All replacement outputs remain NOT_AVAILABLE; cheap fixture is not T3 success. |
| 5. Try the first product breadth checkpoint | #78/#79/#80/#81/#82: truthful auth/discovery/readiness/chain-state across all three configurations and supported UI/REST/CLI projections; explicit applicability/gaps. | Real DAG/runners/launch/bytes/default publication and cumulative fault levels remain due before full PILOT acceptance. |

## What to open at each breadth-first level

Today only the [reference project](https://build.eyeauras.net/project/T3Personal?mode=builds) and
planning/evidence are available; no working replacement URL/command is invented. Integration
[#78](https://github.com/iXab3r/Vivarium/issues/78), [contract #82](https://github.com/iXab3r/Vivarium/issues/82)
and [acceptance #83](https://github.com/iXab3r/Vivarium/issues/83) retain one milestone. UI
[#79](https://github.com/iXab3r/Vivarium/issues/79), direct REST
[#80](https://github.com/iXab3r/Vivarium/issues/80) and unchanged CLI
[#81](https://github.com/iXab3r/Vivarium/issues/81) are acceptance projections worked across each
level, not a serial implementation order. Shared backend/contracts are delivered when first needed.

| Level / visible evidence to open | Whole-chain and surface applicability | Now / existing owners |
|---|---|---|
| 0. Frozen recipe/capability/AC + wire matrix | Three configurations, exact source/settings/runner/Agent/dependency/credential/counter/artifact/trigger/publication facts; UI, /app/rest and CLI actions plus actual unsupported/unknown dimensions explicitly mapped. Source versus installed alignment is resolved or a named blocker. A parsed matrix is not execution GREEN. | NOT_AVAILABLE as frozen proof; #13/#35/#36/#82 with shared #32/#34 gates |
| 1. Truthful discovery, auth, readiness and chain state | Open all three configurations/edges and Linux + interactive Windows Agent readiness through UI/REST/CLI; current ordinary/restricted access, queued/running/terminal and independent outcome/reasons. Each supported projection has actual readback; missing surfaces/capabilities remain gaps, not stub success. | NOT_AVAILABLE; #79/#80/#81 with IAM/CFG/VCS/AGT/RUN owners |
| 2. Real DAG, bytes, launch checks and publication | Each surface enters the same real chain through its frozen entry, with all three configurations observable: exact checkouts, Docker and PowerShell FILE/PS1, interactive Windows launch checks, snapshot + sameChainOrLastFinished artifact selection, source-emitted version/messages, manifests/bytes, Release staging and actual default publication. Off is only an intermediate checkpoint. The real publication execution gate must be satisfied before default-publication acceptance. | NOT_AVAILABLE; #78/#79/#80/#81, CHAIN/EXEC/TRG/LOG/BLOB/DEP contributors |
| 3. Fault/recovery/access and cumulative acceptance | Cover due failures/cancel/reuse/retry/restart/revocation/byte denial and used Release trigger/quiet-period/exclusions/single-publisher behavior with controlled fixtures where sufficient, plus actual chain/publication evidence. All three configurations and all surfaces have explicit applicability/evidence/gaps. No placeholder, replay-only or effect-free GREEN. | NOT_AVAILABLE; #83 and final PILOT aggregate + Security/zero-debt/main gate |

#37 actual Host/Agent/login lifecycle and #38 cheap LocalSmoke remain R0 technical previews; neither
accepts T3. The normal terminal entry is T3Personal_Release with the actual Prepare/Windows dependencies,
not a new composite/parent. Freeze POST-returned Release Build ID versus dependency Build IDs,
lookup and reuse relationships. UI/direct REST inspect the DAG; CLI uses only supported start/wait/
read views with cross-readbacks in UI/API, no invented CLI graph command.

Each surface needs its own actual chain-entry/effect evidence and cross-readbacks, not
one submission viewed three ways. Respect reference reuse/identity rather than invent universal
new-ID or no-reuse rules. Cheap controlled workloads test broad variants without rebuilding the
whole product for thousands of assertions; they do not replace actual Windows/Release/publication.
Existing 110 canonical AC/TCM are instantiated, not replaced by three new requirements/frameworks.

The actor goal for #78 is an authorized release operator obtaining the coherent, launch-checked
T3Personal product and its configured publication result. #79–#81 keep the requested UI/direct
compatible REST/unchanged CLI interaction projections of that goal. Trace existing AC-BLD-01/02/
03/04/05/07/13, AC-ART-01/03, AC-CFG-07, AC-UI-01/02 and AC-MIG-04 with applicable identity/access
criteria and their applicable TCM obligations, including base identity and extension neutrality.
#82 freezes the exact per-case subset with source variants/cases, due owners, independent oracles,
effects and reference completion timing; whole import/history remains later. This is a join over
existing IDs, not a new matrix universe.
Follow [the story practice](../../../docs/design/api-ledger-delivery.md#actor-goal-stories-and-traceability):
coverage links and ready evidence do not imply reciprocal issue-closure blockers; #83 reports story,
subsystem integration and whole-operation API-complete separately. Selected story success cannot
claim all required operation variants. Current next work remains #85; #84's pack is already usable.

#82/#83 must freeze the smallest sufficient real publication proof through the shared producer
boundary, together with actual UI/REST/CLI chain entries and controlled request/default/effect tests.
This does not silently require three identical live GitHub publications or a full rebuild per negative
case. Record explicit valid evidence reuse and its limits; do not declare unknown proof sufficient.
An off-mode entry is labelled with that parameter difference and never described as having published.
Actual default-publication behavior remains due for the total PILOT, not waived by this cost choice.


Future CLI examples require an explicit Vivarium profile, supported options and measured client
fingerprints. No explicit revision flag is currently supported: record actual resolved source/settings
and reconcile a changed recipe. Choose a supported full-chain wait budget considering source limits
Prepare 90m, Windows 90m, Release 15m plus queue/coordination; the current 10m wait default is not a
job deadline or a chosen future timeout policy. Wait timeout exit 124 leaves work running, not
cancelled. Artifact --output names a file within profile downloads, not an arbitrary destination.

## Targeted source/reference addendum — not a refreshed global census

The historical inventory contains 97 configurations and no T3Personal. Read-only reference metadata
on 2026-11-01 (CLI 0.5.2, verified canonical build.eyeauras.net, TeamCity 2026.1.3/build 222742)
adds these three observed IDs. This is a targeted addendum, not a claim of 100 current configurations
or migration acceptance. No existing fleet Agent was assigned or modified.

| Installed ID | Current pinned source recipe / dependency |
|---|---|
| T3Personal_Prepare | Linux Docker simpleRunner; source checkout +:. and -:.repos; positive build counter; shared bundle, Linux runtime/CLI/AppImage and launch checks; requirement names Cvat in reference. |
| T3Personal_Windows | PowerShell file runner; Prepare snapshot dependency and prepare/** artifact input; Windows packaging and real interactive desktop/CLI launch checks; reference requirement laptop-g15. |
| T3Personal_Release | Linux Docker asset staging/validation; snapshot Windows, artifacts from Windows and Prepare; combined manifest/SHA256SUMS, then publication by default. |

All use source root T3Personal_Git. Current source is pinned at
`041345f2c201d68e718d5dbf0ba5ce9755f1704b`:
[settings.kts](https://github.com/iXab3r/t3code/blob/041345f2c201d68e718d5dbf0ba5ce9755f1704b/.teamcity/settings.kts),
[prepare.sh](https://github.com/iXab3r/t3code/blob/041345f2c201d68e718d5dbf0ba5ce9755f1704b/scripts/personal-release/prepare.sh),
[run.mjs](https://github.com/iXab3r/t3code/blob/041345f2c201d68e718d5dbf0ba5ce9755f1704b/scripts/personal-release/run.mjs),
[publish.sh](https://github.com/iXab3r/t3code/blob/041345f2c201d68e718d5dbf0ba5ce9755f1704b/scripts/personal-release/publish.sh).
Settings blob db0a1ed24a0b51c6a9bf726590c2c898ca2a776b describes that 041345f2 source snapshot.
Earlier 32698/32699/32700 metadata was not source-aligned proof. The [new bounded pack](t3personal-reference-pack.md)
now records 32724/32725/32726, version 0.1.6-nightly.20261101.16, producer-claimed revision 7ff65cada,
actual small-manifest bytes and public release metadata. That is stronger reference input, still not
independent server-resolved source/settings, selected DAG/inputs or locally verified binary bytes.
#85 owns that immediate server receipt; #13/#35/#82 retain recipe/wire/effect freeze and execution gaps.

Prepare has one actual reference runner step, RUNNER_1; internal script stages are observed output/
progress, not invented per-command StepRuns. Prepare uses a pinned Node Docker digest, Node 26.12.1, Rust 1.96.1, Vite+ 1.4.0 and pnpm 12.14.0,
a 90-minute timeout, owned caches and actual dbus/xvfb Electron plus CLI checks. Preserve its
`buildNumber` and `publishArtifacts` effects: diagnostics/prepare/Linux/launch outputs and manifests,
not stdout alone. Provide a separate owned Agent with explicit requirement mapping; do not commandeer
Cvat/laptop-g15 or silently remove requirements. Record exact permitted mapping in the pilot recipe.

Prepare's GITHUB_TOKEN preflight makes an authenticated repository GET and requires push permission;
it does not publish. Supply a private credential reference, never a token in public planning/logs.
Release source sets T3_PUBLISH=release and invokes publish.sh; off/draft/release are supported,
with no dry-run or repository-target override. Draft also writes externally. An off-mode chain is a useful intermediate build-only checkpoint, not default-publication parity.
Default publication and the used Release VCS trigger/coexistence ownership are due in this PILOT;
the eventual real external effect needs a concrete execution scope/authorization gate. This planning
slice executes nothing and does not request or grant publication authority. This operator gate is
outside the product model, not a new awaiting-permission Build state or approval feature. No target override or
dry-run exists; an unavailable execution gate remains a blocker, not permission to omit the behavior.

## One bounded integration, existing owners

Near-term order remains accepted P0 → accepted R0 → PILOT. Full IAM/general editor/all-runner/
provider/history completeness is not a prerequisite, but every feature actually used by this chain
is now early due. No machine provider or deployment dashboard/type/API is invented: Release's name
does not establish a special BuildType category. SS-DEP contributes its actual publication behavior.

SS-RUN@PILOT #78/#82/#83 uses one production authority with #79/#80/#81 as surface projections.
Full owners remain #20/R2 run, #16/#17 IAM, #18 CFG, #19 VCS, #56 Agents, #22 runners, #21 chain,
#23 counter/used VCS trigger/publisher coordination, #24 logs/service messages, #26 bytes and #58
publication. The ten contributions stay bounded to the actual chain; broader full-owner scope stays
visible. Use separately owned Linux and interactive Windows fixtures with explicit mapping from
reference Cvat/laptop-g15 requirements, never those existing fleet Agents or a silent requirement drop.
The common version originates in Prepare and is validated through Windows/Release manifests;
per-configuration counters/identities and reference DAG reuse remain distinct and must be frozen.

The 041345f2 source snapshot has default-branch VCS/60s quiet period and metadata-path exclusions;
manifest-claimed 7ff65cada Kotlin retains the branch/quiet period but has no metadata exclusions.
Freeze the applicable server settings version, exact rules and coexistence/single-publisher ownership
at level 0; do not mix the two sources. Prove their used
behavior with controlled fixtures before full configuration parity closes. Manual surface stories do
not require a separate trigger project per surface or authorize activating production triggers now.

All variants remain UNFROZEN and all levels NOT_AVAILABLE until evidence exists. Sixteen owners,
eighteen slices/nine blocks remain; ten PILOT contributions raise the total to 22. Global 449 primary
API headings and fifteen cases/results/receipts are unchanged. Cards can expose honest branch
checkpoints; closure follows frozen due/cumulative evidence, independent final aggregate correctness/
simplicity + Security/Adversarial, zero due debt and verified main delivery. No per-card formal review,
three engines, scope waiver or closure from parsed/stub/effect-free evidence.
