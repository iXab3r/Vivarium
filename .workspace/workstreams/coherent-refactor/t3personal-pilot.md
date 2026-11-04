# First real product: the full T3Personal chain

## Current story delivery plan

### Current checkpoint — 2026-11-04 (not milestone closure)

D52 selected retained-dependency admission and actual T3 publication are checkpoint evidence on
`feature/us013-whole-chain`. Raw receipt: `artifacts/pilot-chain/release-recovery-20261104-203528/receipt.json`.
Root 8 is PASS; original Prepare 1 and Windows 2 are reused, and only builds 1/2/3/7/8 executed.
The initial unchanged CLI chain built Prepare/Windows successfully and failed publication. Later
native Release-only admission used a corrected, versioned Release definition. This composition
is not a single successful unchanged-CLI whole-chain journey; original failures remain evidence.

Package source remains `3ae8f481bc7c118747c8d01f0ec14f3f18e8b6ec`.
Release-definition Git is `893ab2d6aa109f200e3be5b0a5bdba0ca032c5fa`, helper SHA-256
`eb8ff213db945a4fe858798db0a05c7c5a2590e48415acec446207ee7f73c19e`, script SHA-256
`7e35344872e34a814b331defc3f2b8951bbe8510b20b9e1f7e99b56cb80ba51c`.
Controlled exit 75 after the first asset, same-draft resume, published repetition and actual remote
asset SHA-256 checks passed in the selected recovery proof. Release
[434102890](https://github.com/iXab3r/vivarium-us013-e2e/releases/tag/v0.1.6-nightly.20261104.918)
(version `0.1.6-nightly.20261104.918`) is published only in the owned test repository.
No built application was launched. Receipt and `ActualT3ReleaseRecovery.cs` assertions were inspected;
this documentation checkpoint performs no remote mutation, download or execution.

Existing candidate receipts report publisher 33 controls, tiny proof/preflight, clean root build and
root 700 Passed/11 skipped, with 891 source inputs/15 producer copies and zero drift. These are
qualified reuse of the preceding source candidate, not freshly executed checks on this commit.
Raw setup failures and earlier failed publication attempts remain separate. Full PILOT/main is open.

Remaining acceptance uses the existing contract and tasks, without another case universe:

| Remaining item / owner | Evidence and cheapest adequate boundary |
|---|---|
| [#117](https://github.com/iXab3r/Vivarium/issues/117) model/contract | Reconcile existing explicit-selection variants and AC-BLD-07/TCM bindings against captured literal observations. Ordinary authority, missing bytes, contradictory graph and changed-HEAD reference variants remain unknown/unaccepted; existing conservative controls are not wire GREEN. Use bounded source/reference fixtures before product assertions. Full API denominator remains unknown; no percentage. |
| [#118](https://github.com/iXab3r/Vivarium/issues/118) cumulative small-chain proof | Renew existing 33 story/SDK cohort (D50's 29 story rows plus SDK/owner-death case, then D51's four cases), preserving raw failures and setup classifications. Confirm actual test discovery/bindings before selecting the cohort; this count does not require 33 new tests or another heavy build. Cover independent UI/REST/unchanged-CLI entries, current rights/byte denial, waiting, upstream failure/cancel, restart/ownership and trigger/quiet-period/exclusion/single-publisher behavior. Use existing shared Host/owned Agents and tiny workloads, with browser evidence only for UI entry/effects. |
| [#78](https://github.com/iXab3r/Vivarium/issues/78) heavy journey composition | Assess the initial unchanged-CLI build/package plus corrected native Release-only recovery against the heavy-journey contract; explicitly resolve the remaining successful ordinary-entry/readback obligation. Reuse immutable producer bytes where valid. Do not infer full CLI success or rerun heavy work merely for this checkpoint. |
| Milestone delivery (#78/#118) | Root coordinates exact-revision evidence transfer/invalidation, due cumulative checks and mechanical ledger assessment only with current explicit source seals. Independent aggregate correctness/simplicity and Security/Adversarial review, zero due findings/debt, reviewed main delivery and task closure remain due. This checkpoint neither accepts nor merges the milestone. |

Historical planning and earlier checkpoints below remain provenance; their no-implementation/current
availability statements describe their dated sessions. Current selected evidence above supersedes
those status claims, without changing the completion contract below.


**Execution update, 2026-11-01:** the owner assigned this plan with “let's work”. The
[integrated P0 execution checkpoint](us013-p0-execution.md) records actual reference effects,
replay and C#/Git source experiments, their limits and remaining work. Planning readiness is now
being exercised; it is not P0 acceptance or permission to skip into R0/PILOT. Original planning
statements below describe that earlier session, not a continuing implementation prohibition.

Owner direction, 2026-11-01: work through actor-goal stories; when the route to a story is unclear,
resolve its plan. **US-013 / #78 is the active unit of delivery.** Lead: Story Delivery; co-held
TeamCity, Testing Architect, REST and Docs perspectives. Technical cards identify responsibilities
inside this story. Completing a receipt or model card is not a handoff or a reason to ask the owner
to choose the next leaf. Continue the connected work until a coherent story checkpoint or a real
unresolved decision/external execution boundary is reached.

The release operator must be able to sign in, inspect all three configurations and readiness,
start the ordinary Release configuration through UI, compatible REST or unchanged CLI, follow its
actual upstream work, retrieve coherent Linux/Windows build/package assets and identify the
publication result in the test repository. Denial, source/runner failure, waiting, cancellation and recovery
must remain truthful. The full promise includes default publication and used trigger/coexistence
behavior. Build-only/off is an intermediate result, not completion of US-013.

### Confirmed autonomous E2E boundaries

The owner resolved these choices on 2026-11-01:

- Publication in a separate test repository is sufficient. No final release in `iXab3r/t3code`.
- Frequent small-chain checks plus one actual T3 build/package E2E; no execution of the built
  T3 GUI or CLI, application smoke checks or interactive Windows requirement.
- Separate owned test Agent processes on this Windows machine and available Linux/Docker.
  No dedicated VM requirement, existing fleet Agent use or new machine-provider implementation.
- A fresh isolated Vivarium installation. Existing-data migration and TeamCity cutover stay outside
  this story; current-data/legacy-path handling still follows the relevant broader milestone contract.
- Autonomous milestone merges into main after all due checks, independent review and debt closure.
  Planning does not merge the current branch or waive any unpassed P0/R0/PILOT gate.
- The delivery agent prepares the three configurations through C#/Git. UI scope is chain/readiness
  inspection, run/cancel, logs and artifact downloads; UI configuration creation/editing belongs
  to its corresponding story. This does not remove due shared-model/configuration-API proofs.

The test recipe must explicitly map hardcoded repository/updater/credential-preflight identities
to the owned test repository and remove built-application launch checks. These are attributed fixture
deltas, not swallowed failures or claims that the original application smoke tests passed. Compile,
package, selected artifact transfer and publication remain real; remaining command failures stay
failures. Browser journeys test Vivarium's own UI, not the T3 application. Source/settings/recipe
fingerprints identify the adapted test inputs separately from historical #84/#85 observations.

All seven owner scope/environment/autonomy choices are resolved. The remaining definition/model/
reference questions are technical proof work assigned to the autonomous agent. Resolve them through
the bounded P0 experiments below; do not return them as unspecified policy questions or assume GREEN.

<a id="autonomous-execution-contract--planning-draft"></a>

### Autonomous delivery plan — ready for P0 execution

This plan prepares a future agent to deliver the whole story without technical-card handoffs.
**Plan readiness: READY_FOR_AUTONOMOUS_P0.** This is readiness to execute the integrated P0 proof
work, not acceptance of its still-unfrozen variants or readiness to skip into R0/PILOT. Implementation
and external effects are not started by this planning session. The agent freezes each dependent
contract from actual evidence before implementing against it; routine technical choices need no
further owner selection while they preserve the agreed behavior and authorities.

1. **Freeze and prepare.** Pin source/settings, tool/client versions and the explicit test-recipe
   deltas; record upstream versus adapted fixture Git identities. Resolve the exact owned test
   repository/branch/tag scope, private credential aliases, run-owned roots/ports/containers/volumes
   and concrete URLs/trust reachable from Windows and Linux. Use a fresh private PG/Git/Storage
   installation. Read-only inspection confirms an available Linux/amd64 Docker engine and MSVC
   x64 tools on this Windows machine; it does not yet prove the full pinned build environment.
   Prepare tools privately/per-process where possible rather than changing global PATH or installing
   unrelated host software. Concrete missing credentials use the existing private secret mechanism.
2. **Resolve the integrated P0 package.** Prove C#/Git definition/edit/evaluate/activate/recover,
   source/settings/Build/selection identity, independent entry/lifecycle/current-access/byte cases,
   representative production composition and the honest executable baseline. Preserve all other
   P0 structural/allocation/review/debt/main obligations. Reference gaps are evidence work, not
   opportunities to invent defaults. Ordinary local fixes and experiments continue autonomously.
3. **Deliver the real R0 composition.** Use production Host/Chiral/PG/Git/Storage/Identity/Agent
   services and a tiny real workload. Reuse Depot's pinned real OIDC fixture/protocol and ordinary
   cookie/key paths; no product-only test login, direct grants-as-proof or second auth engine.
   Prove start/readiness/current rights/run/bytes/failure/cancel/restart, then close R0's due gates.
4. **Implement and accept the small whole-chain journeys.** Each of UI, compatible REST and the
   unchanged pinned CLI makes its own real chain entry. All use the same production authorities and
   real Windows/Linux Agent processes. Use small deterministic source/build/package inputs for
   selection, reuse, waiting, upstream failure, cancellation, current-rights/byte denial and restart
   variants. Controlled reference fixtures provide independent expected effects; HTTP replay is
   transcript/fault control, not another build engine. Verify manifests and actual transferred,
   retained and downloaded bytes through user-visible paths.
5. **Run the single real T3 build/package E2E.** Use the ordinary unchanged CLI entry, the adapted
   pinned recipe, real checkout/Docker/PowerShell/build/package commands and the selected upstream
   artifact chain. Inspect the same run through UI/REST; these readbacks are not claimed as two
   further T3 submissions. No built T3 GUI/CLI smoke launch is required. The current default-publication
   path must actually publish the expected outputs in the owned test repository. Record actual
   identities, exits, version/manifest consistency, file inventories and byte hashes.
   This is one selected heavy acceptance journey, not a prohibition on necessary repair/reruns;
   retain failed attempts and renew the proof when relevant changes invalidate it.
6. **Review, merge and finish.** Run focused TDD during work and batch required root/boundary checks
   at coherent milestones. Obtain independent final correctness/simplicity and Security/Adversarial
   review on exact integrated revisions; repair all due findings and renew affected evidence. Merge
   only after that milestone's gates pass, then verify main delivery and close its due cards.
   P0 may deliver its explicitly declared honest RED baseline; this is not permission to regress
   implemented GREEN or accept harness/setup failures. Final story completion requires the frozen
   story cases and cumulative delivered behavior, not all 449 discovery headings being GREEN.

Resolve each phase's concrete prerequisites before its effects; do not make a late publication
credential or a heavy T3 environment a blocker for independent P0 work. Provision resources only
when their phase needs them. Verify the actual test repository, scoped credential, remaining recipe
commands and environment before starting the heavy run rather than discovering missing publication
access after hours of compilation. Normal private provisioning is not a new per-action owner gate.

The agent records resumable decisions, exact heads/PRs, evidence locations, owned resources and next
actions in the existing workstream/GitHub records. It re-reads actual build/release state after
interruptions before acting again. This uses the existing host/CLI/testing contours, not a new
orchestrator, business state authority or scenario language. Missing PR checks are not silently
treated as PASS: observe configured checks and run the required exact-revision evidence. Vivarium's
checked-in CI triggers Compile on the default branch; its Release/publishers have no trigger.
Test T3 publication and publishing Vivarium itself are separate effects.

### Concrete defaults and technical decisions delegated to the agent

| Area | Planned default and required proof |
|---|---|
| Test repository | Create a new private `iXab3r/vivarium-us013-e2e` if available, or a unique owned suffix. Reuse an existing repository only after verifying its fixture ownership/provenance. Names are routine setup choices, not a reason to ask the owner. Never modify the production T3 repository or overwrite unrelated tags/releases. |
| Source fixture | Keep small synthetic input on a distinct branch and the adapted pinned T3 snapshot on another, so semantic-trigger cases do not repeatedly rebuild T3. Preserve source/license provenance and record both upstream and fixture commits. Seed the adapted tree after applying test-repository/launch-check deltas; do not push upstream tags or enable copied upstream `.github/workflows`. Desired C# definitions live in the controller's Git authority, separately from the workload repository. |
| C# definitions | Prove RF-05's managed C# settings plus separate user code candidate with the real three definitions. Prefer typed deterministic edits to managed fields and a bounded separate evaluator over arbitrary C# AST rewriting. Demonstrate valid/invalid activation, immutable effective plans and restart recovery. Inspect the pinned APIs before choosing file/type names; adopt only a proved candidate. Git-first US-013 setup does not exempt configuration mutations due in P0 from their compatible behavior. |
| Reference fixtures | Use the explicitly isolated `vivarium-teamcity-lab-202613` profile/target, never default production writes. Verify version/origin before mutations, use owned small configurations/actors/Agent processes where needed, capture literal effects/bytes, and restore temporary lab gates/resources. Establish reuse, cancellation, source/settings timing and selected inputs independently; unobserved facts stay gaps until resolved. |
| Authentication | Reuse the pinned Depot real OIDC fixture and stock Identity/OpenIddict flow with owned ordinary users. Obtain real cookies/finite CLI credentials through normal product paths, grant/withdraw current rights through owning services and prove denial. No database grant seed or installation password used as a bearer substitute. Private values stay in existing stores; request a missing credential through the private mechanism once concrete need is established. |
| Local resources | Own Windows processes, Linux containers, private PG/Git/Storage and namespaced caches/volumes. Verify actual Windows↔Linux controller/data-plane URLs, TLS trust, tool versions, Docker access, disk and bounded cleanup; localhost reachability is not assumed across containers. Do not switch the host Docker mode, reboot, replace global tools or install unrelated host components silently. |
| Heavy workload | Use the source-confirmed T3 cohort as the initial pinned build/package baseline, with explicitly separate fixture Git identities after adaptation. Keep the used later trigger-exclusion delta in its due controlled cases. Prepare/Windows/Release timeout inputs remain 90/90/15 minutes for that recipe; derive supported CLI wait/queue budgets from the frozen critical path rather than the client's 10-minute default. Timeout does not imply cancellation. |
| Evidence and cleanup | Retain exact source/fixture/target identities, requests, outcomes, bytes and cleanup receipts in the existing contours. Final output includes the test release, required evidence and reproducible start/setup inputs. Reap test-owned processes/containers; preserve final evidence/release and never recursively clean paths or resources without checked ownership. No new orchestration framework or second task/status authority. |

If the definition candidate fails, the agent may revise its implementation approach and repeat the
bounded proof without asking about class names or libraries. Escalate only a concrete incompatible
observable contract or scope/authority change, presenting the failed proof and viable recommendation.
This differs from asking the owner to choose an unspecified abstraction before inspecting its APIs.

### Failure classification and autonomous repair

| Failure | Agent action and honest result |
|---|---|
| Vivarium differs from independently established behavior | Preserve real RED, fix the owning production implementation and required regression evidence. Do not change the oracle or hide the case. |
| Reference/default/identity behavior is unknown | Inspect pinned sources and execute the bounded isolated reference case. Resolve the fact, not a new owner preference; escalate only a concrete unresolved material contract conflict. |
| Test harness, tool, URL/TLS or private fixture setup fails | Diagnose and repair within the owned scope; classify setup failure separately from product RED. Missing external authority/secret uses its concrete supported resolution, not repeated blind runs. |
| T3 compile/package fails because of its own source or dependency environment | Verify the pinned inputs and distinguish reproducible workload failure from CI handling. Repair declared fixture/tool integration where justified; do not rewrite the T3 application, drop remaining build steps or turn their nonzero exit into success. A correct failed Build is not a successful heavy E2E; an unavailable independently working workload is a named prerequisite blocker. |
| Submit/cancel/publish response is lost | Inspect the known operation/Build/tag/release and actual effects, then follow the frozen retry/reuse semantics. Do not manufacture a new run or overwrite unrelated output merely to retry. |
| Independent review or main verification finds a defect | Fix due findings within the same milestone, renew affected checks/verdicts and verify the actual main result before closure. A review unavailable or issued for another revision is not PASS. |

### Exact milestone merge and resume rules

Read-only GitHub inspection on 2026-11-01 found main at `09dd40bb…`, `protected=false`, no effective
branch rules, and a branch-protection 404. These are observations, not permanent configuration.
The checked-in TeamCity definition has a default-branch Compile trigger and no automatic PR,
Release or publisher trigger. Re-read actual policy/check state at execution; this plan changes no
repository administration or CI settings and does not interpret missing checks as successful checks.

Before each milestone merge, the agent must have: the exact integrated candidate/base, all due
checks and explicit reuse/invalidation on that candidate, zero due findings/debt, independent final
correctness/simplicity and Security/Adversarial verdicts on that revision, and satisfied observed
repository checks/policies. Reviewers inspect raw evidence with independent context; author self-
review is not PASS. Create/link the milestone PR through the available host integration, merge only
that verified candidate, then inspect the actual main result. A changed merge tree invalidates
affected evidence/verdicts until renewed; an unchanged relevant tree still needs an explicit transfer
assessment. Close cards only after verified main delivery. Do not auto-merge this planning branch
merely because story-level autonomy has been agreed.

On resume, read the existing workstream/GitHub checkpoint, verify the current branch/head/PR and
owned resources, and inspect actual build/assignment/release state before repeating commands. Lost
submit or publish responses do not authorize blind resubmission; preserve the operation's frozen
retry/identity behavior and inspect the known object. Do not adopt a stale reviewer PASS, a stale
controller instance, unrelated process/container or unrelated same-named release. Fix ordinary
failures autonomously; if a required reviewer/tool/credential or agreed environment is unavailable,
record the concrete blocker rather than weakening the gate.

### Completion evidence for this story

| Observable | Required evidence |
|---|---|
| C#/Git setup and ordinary access | Real activated three-configuration definitions/revisions; ordinary login/key and current scoped allow/deny. No UI configuration editor required. |
| Three surfaces | Independent small whole-chain UI, compatible REST and unchanged-CLI entries, with real state/effect readbacks and selected input/output bytes. Viewing one run three ways is insufficient. |
| Execution and ownership | Actual Windows/Linux work, requirements and waiting reasons, ordered configured commands, upstream failure, queued/running cancellation and restart/reconnect without fabricated success or orphan ownership. |
| Artifacts and publication | Coherent common version, selected producer/consumer files and hashes, retained/downloaded bytes and actual complete publication in the owned test repository; partial/retry/current-access controls use small owned fixtures. |
| Real T3 build/package | One real three-configuration T3 CLI submission using the attributed adapted recipe, successful remaining compile/package commands, expected outputs/test publication and UI/REST inspection; no built-application GUI/CLI launch proof. |
| Automation and delivery | Used controlled trigger/quiet-period/exclusion/single-publisher cases, frozen due/cumulative case evidence, independent milestone verdicts, zero due debt and verified main revisions. |

Bind these observables to the existing AC/TCM/ledger and real independent cases during P0; this table
does not invent case IDs, expectations or a new test language. A plan can be ready to execute while
product availability remains NOT_AVAILABLE and exact variant scope remains UNFROZEN.

### Future agent assignment

Use this as the delivery brief after the owner assigns execution; it does not start work from this
planning session:

> Deliver US-013 / #78 end to end using this plan and the current repository contracts. Start with
> the integrated P0 proof package, close its gates/main delivery, then R0 and PILOT. Preserve the
> seven confirmed boundaries and use the existing subsystem owners rather than a leaf-task queue.
> Resolve routine technical choices through pinned APIs and independent bounded evidence, implement
> and repair continuously, and obtain independent milestone verdicts before autonomous main merges.
> Do not launch the built T3 application, require interactive Windows, migrate existing installations,
> use existing fleet Agents or publish to production T3. Finish with verified story/main/test-release
> evidence and cleanup, or report a concrete blocker outside the agreed scope with a recommendation.

**Publication oracle and recovery.** The original T3 publisher can exit zero for an already published
tag without verifying its assets. The E2E test therefore independently checks repository, tag target,
release state, expected asset set and hashes. After a lost response, inspect those facts before
resuming or retrying; do not overwrite an unrelated tag/release or delete evidence to obtain GREEN.
This is test/producer verification, not a GitHub-specific success policy added to Vivarium's runner.
Exercise partial/failure/retry variants with small owned test assets. The historical ten-asset
reference totals 905,365,355 provider-declared bytes; actual fixture sizes/resource budgets must be
measured rather than claimed locally verified from that metadata.

**Stop and cleanup.** Normal implementation/test failures are work for the agent to fix, not user
handoffs. Escalate a concrete unavailable secret/resource, unresolved material contract choice,
unavailable independent reviewer or action outside the agreed scope, with evidence and a recommended
resolution. Use only owned cleanup targets; preserve final release/evidence and historical source
discussions. Tests own and reap their processes/containers. Supply reproducible installation/start
inputs and usable evidence rather than leaving untracked background services. No migration,
production TeamCity cutover, production T3 publication or global interactive-desktop setup is due.

### Assessment and immediate work

**P0 proof work is the first autonomous execution block.** The current controller still
references SQLite and has the existing queue/Agent/Git/native-REST foundation, not the accepted
C#/Chiral/PostgreSQL catalog and compatible full-chain product. #84/#85 give usable independent
reference inputs; they do not supply a complete definition/editing/selection model or frozen
entry/effect cases. P0's baseline/composition/allocation and final review/main gates remain open.
These are assigned technical readiness obligations, not unresolved owner scope choices or a proposal
to restart the accepted architecture. Execute their bounded proof before dependent structural code.

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
| Real chain and bytes | Release-entered Prepare→Windows→Release build/package work, exact selected inputs, Docker/PowerShell runners, common version, retained logs/manifests and actual staged/downloaded bytes; no built-application launch | RUN/CFG/VCS/AGT/EXEC/CHAIN/LOG/BLOB contributions below. Small fixtures provide frequent checks; one actual T3 build/package E2E is due. |
| Default publication and used automation | A causally attributable default-publication result in the owned test repository, selected tag/assets, and controlled used trigger/quiet-period/exclusion/single-publisher behavior | SS-DEP #155 and SS-TRG #143 with #117/#118. Concrete test repository/credential preflight is due; no production publication or trigger activation is required. |
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
Use separately owned processes on this Windows machine and Linux/Docker, not Cvat/laptop-g15 or a
required interactive desktop. Prepare the concrete test-repository target, private credential
references and owned cleanup boundaries before execution. Resolve routine details autonomously
within that scope; there is no new product approval state or per-action confirmation requirement.

**Reporting:** state the operator outcome now available, the story checkpoint reached, the remaining
story blocker and verification. Put leaf IDs in supporting links. Current availability is still
NOT_AVAILABLE; no new executable proof, milestone acceptance or product implementation is claimed
by this planning reconciliation. The 25-story / 18-packet / 54-task graph is unchanged.

## Reference and preserved detailed scope

The detailed material below retains earlier scope/evidence context. Current work order and handoff
come from the story plan above; historical “next #85” and superseded task IDs are not a leaf-work queue.
Descriptions of the original T3 application launch checks, interactive Windows and publication to
`iXab3r/t3code` below are source/history facts, not current US-013 acceptance requirements. The
confirmed test-only/build-system boundaries above supersede the earlier pilot scope.

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
