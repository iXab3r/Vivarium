# API ledger and milestone delivery

> Status: **Accepted direction — D39; task graph/ledger seed only, no runtime parity claimed**
> Owners: Reconciliation, TeamCity/REST, Test Steward, affected domains and Security

## Subsystem task ownership and milestone aggregates

GitHub owns live task state. D40 makes a coherent subsystem slice the implementation unit: its
owning implementation task parents REST-contract and REST-acceptance subtasks in the same milestone.
The [subsystem catalogue](subsystems.md) defines ownership and completed lifecycles; the
[slice register](../../.workspace/workstreams/coherent-refactor/subsystem-slices.tsv) contains exact
task IDs. The milestone [block register](../../.workspace/workstreams/coherent-refactor/api-blocks.tsv)
aggregates those subsystem slices and retains the parent closure gate. It is not an execution plan
for unrelated endpoints. Do not create one implementation issue per route or another workflow bot.

Work progresses contract-ready → focused real RED → implementation → REST verification → final
milestone review/fixes → main delivery → bottom-up issue/milestone closure. Contract/code readiness
can be recorded while issues stay Open until verified main delivery (RF-10). Completed earlier
milestones do not wait for R6. Planning, reference reads and reviews never authorize production
mutation, release, fleet action, cutover or a merge outside the existing user scope.

Freeze the affected operation/variant set before its implementation; reference discovery and tests
can advance in verified cohorts. A contract task states expected responses **and effects**, source
profile, exact case IDs, owning services and implementation/verification links. Implementations may
not choose different TeamCity defaults or hide required variants behind an unsupported label.

## Mechanical ledger and API-complete

Reuse the discovered source universe and preserve stable source IDs when normalizing/splitting rows.
[api-ledger.tsv](../../.workspace/workstreams/coherent-refactor/api-ledger.tsv) initially indexes the
449 documented operation headings; these are **discovery rows**, not method/path variants or tests.
The 456 CLI fixture requests remain a separate input. Installed schemas, actual clients, non-REST
downloads and domain/Agent behavior supplement discovery; no denominator is frozen from a count.

P0 task #36 owns the small ledger validator/report binding. It must use ordinary structured files and
existing test results, not add a scheduler, dashboard service, runner or recurring full-suite job.
The initial report is explicitly UNVERIFIED; no conformance runner/report ingestion exists yet.
The validator checks accounting, bindings and evidence provenance; it cannot infer that an arbitrary
assertion is a correct TeamCity oracle. Reference contracts and actual state/byte checks still own
that meaning. A populated receipt or a renamed test cannot turn self-consistency into parity.

The delivered ledger must mechanically enforce:

- Stable operation and variant/case IDs; exact method/path/request/media/auth/state/effect dimensions,
  source/profile and applicability. Source operation IDs survive splits; duplicates/orphans fail.
- One primary subsystem/variant owner and its implementation/REST tasks per milestone slice, with
  collaborators and cross-slice links; no duplicate counting. Global/subsystem/milestone rollups agree.
  P0 classifies/allocates every known discovery input; exact due variants freeze before their
  implementation under the granularity rule below. Unknown source behavior remains visible and
  blocks its affected contract/acceptance gate. Use the [P0 gate distinctions](teamcity-rest-test-first.md#which-gaps-block-which-work), not an automatic stop of independent sound cohorts.
- Independently frozen scope per slice and cumulative delivered scope. Scope changes have explicit
  reason/history; a failing required case cannot move to a later milestone or disappear to pass.
- Case-to-test binding and ordinary raw result provenance: candidate/base, fixture/reference/client
  inputs, command/selection, assertions/effects, result and safe artifact reference. Expected values
  never come from the product. Stale evidence or an unexecuted case is not current GREEN.
- Discovered, specified, reference-blocked, unbound, product-blocked, implemented-unverified, GREEN,
  RED and harness-failure remain distinct. Issue closure, response schemas and replay GREEN do not
  establish product compatibility. Discovery/input rows and executed cases have separate counts.

Every subsystem and milestone acceptance publishes its **API-complete status**, not merely an issue
completion percentage; subsystem lifecycle/integration readiness is a separate claim. Report
frozen/unfrozen scope; required/bound/executed/GREEN/RED/blocked/unbound/stale
cases; implemented-but-unverified items; unallocated discovery; tested candidate/profile; open debts
and review verdicts. If normalization/scope is incomplete, global compatibility percentage is
**unknown**. Never present GREEN / discovered headings as an API compatibility percentage.

`API-complete(slice)` is true only when its scope is frozen, all required slice cases and previously
delivered compatible behavior have valid GREEN evidence, no required reference/binding/product/
harness/stale blockers remain, and no unresolved milestone debt exists. Report the slice name and
remaining global gaps beside it. Slice-complete does not mean whole-TeamCity-complete. Final global
completion additionally needs reconciled full required scope and zero unallocated required variants.

P0 delivers an executable contract/RED baseline, not a compatible product slice. Its baseline gate
may pass with accurately recorded expected product RED, while **product API-complete stays false**.
Planned future functionality outside a frozen slice is not milestone debt; a missing behavior
promised by that slice, a regression or its introduced/exposed technical tail is debt and blocks it.

## P0 allocation and due-variant freeze

P0 reconciliation covers the full known discovery union (449 headings, 456 client requests and
supplemental schema/client/non-REST/native inputs), preserving lineage and explicit unresolved scope.
Every known input needs a primary owner, authority, intended delivery/integration slice and task links,
or a machine-readable gap with those accountable links. This is allocation completeness, not a
claim that the global normalized denominator is known or that all late variants are frozen.
P0 structural and exact R0 resource/identity/state/authority/mutation/protocol decisions remain blockers.
An unknown possible shared-structure/R0 dependency is a P0 blocker until assessed, never an assumed
late gap. Existing P0 census, executable baseline and model/editability gates are unchanged.

#36 delivers later-gap records in ordinary structured files: stable gap ID, source IDs/profile,
unknown dimensions/expected-value source, primary owner, authority, intended integration slice,
owning contract/acceptance tasks, due freeze gate, shared-structure/R0 impact assessment with rationale
and assessor, and unresolved/resolved state/evidence. This is ordinary classification during work;
independent final milestone review judges it in aggregate, without a formal review per gap/cohort.
Validate required fields and links; reject a
later classification with unknown/positive structural or R0 impact. Each due slice normalizes and
freezes its exact required variants/cases before implementation. Later gaps block their own due
contracts/acceptance and remain in global unknowns; no missing due behavior is waived by allocation.
This defines #36's deliverable, not an assertion that gap normalization/validation exists today.

## Affordable verification

During TDD run the affected named cases and necessary regression neighbors. Reuse unchanged valid
evidence; do not restart full REST/E2E/platform matrices per edit, subtask, reviewer or status query.
Status generation reads ledger/results without executing tests or probing TeamCity. At coherent
handoff/milestone checkpoints batch the required solution baseline and due/cumulative compatibility
checks. Expand only for changed shared contracts, new failures or unresolved risk. Every required
case still needs real evidence at its proper boundary; do not reduce scope for speed.

D38's first raw RED run remains mandatory. Later reports preserve the full known raw result set and
separate newly executed selection from reused evidence and stale/unknown cases; do not claim a fresh
full run from an aggregate report. A required full run stays visibly RED/nonzero while cases fail.
Schedule a broad rerun for the first baseline, milestone closure or material cross-cutting changes
when evidence requires it, not as automatic per-task ceremony. No auto-expanded RED allowlist.
Normal root build/test obligations remain; a prose-only update needs document/ledger/task checks.

### Valid evidence reuse, including delivery to main

Retain each original receipt: executed revision/base/target identity, command/selection, result and
artifact, reference/client/profile versions and fixture/oracle/environment inputs (hashes or immutable
references where available). Never rewrite its revision to the new head or label reuse a fresh run.
For each new candidate and verified main revision, record an explicit reuse/invalidate decision for
affected case groups: from/to revisions, relevant production-code and shared-service changes,
fixture/oracle/reference/client/environment impact, rationale, assessor and linked affected reruns.
An identical tested tree with unchanged relevant inputs can transfer with that recorded rationale;
a merge/cherry-pick requires checking its actual resulting tree and relevant environment, not its label.
Unrelated prose need not invalidate runtime evidence. Changed auth/locator/state/serialization/storage,
fixture, oracle or relevant environment invalidates affected evidence. Unknown impact is stale;
rerun the affected selection and real boundary before counting it GREEN. Independent final reviewers
judge sufficiency, retain exact reviewed revisions and renew affected verdicts; an old PASS is not
automatically a new-head PASS. #36 validates records and stale accounting, not a dependency engine.
#42 proves unchanged reuse, invalidation on shared semantics/fixture/oracle changes and unknown-impact
rejection. Current seed receipts/checker remain unimplemented.

All four verification targets must be available and buildable from first harness delivery. Execute
the full raw target for the first D38 baseline; later execute the due/cumulative cases at coherent
handoff/milestone gates, reusing explicitly valid unaffected evidence. A new full raw execution is due
when required by the declared milestone's acceptance (including R5/R6 cumulative conformance), or
material cross-cutting change/unknown impact makes the full selection necessary. Milestone closure
records that decision and current evidence for every due/prior-delivered case; it is not an automatic
full future-known-RED rerun. Ordinary code handoff still runs required root build/test plus affected
checks. Status/prose-only updates execute no suites. #35 publishes exact commands and selections/
triggers; wiring the raw target never excuses a required real run, and availability does not impose
a full-suite/status/per-task tax.

## Review and zero-debt closure

Formal independent aggregate review is at the **end of each milestone**, after implementation and
focused evidence. Cover correctness, affected domains/REST and simplicity plus an explicit
**Security/Adversarial review**. One independent reviewer may co-hold competent roles, with explicit
verdicts; an author's self-review is not sign-off. Do not import Depot's every-PR review cadence or
run a review agent for each case/task. Consult an owner during implementation when needed without
turning routine work into another mandatory formal review ceremony.

Bind sign-off to exact head/base/target, evidence and limits. The adversarial pass traces reachable
anonymous, least-privileged, cross-project and stale-credential paths, hostile producer/byte input,
resource amplification and restart/retry/ownership failures relevant to the milestone. Use the
shortest synthetic isolated proof; no live farm/security scan or compulsory exploit matrix. Docs-only
scope can conclude no runtime boundary changed, but cannot claim runtime security verification.

All actionable findings and introduced/exposed technical debts become same-milestone subtasks under
their owner (or the parent for cross-cutting findings). Fix, run the smallest relevant regression,
then renew final aggregate verdicts for the changed revision, reusing unaffected evidence. No open
findings, TODO tails, suppressed required tests, legacy authority leftovers or security follow-ups
may be carried to the next milestone. Unrelated pre-existing work/new features need an explicit
classification, not a renamed debt exemption. Record each disputed finding in the same-milestone
owning task (ordinary task text/structured record, no new platform): stable finding ID, observation/
evidence and revision, affected due AC/contract or reachable changed path, origin (introduced,
pre-existing or unknown), relevance rationale, proposed classification, accountable owner/task,
disposition/evidence and independent final reviewer judgment. A due-contract failure, regression,
reachable vulnerability in the delivered flow, or a tail exposed by integration is milestone debt
regardless of age. Unknown relevance stays unresolved and blocks closure until assessed. Only work
with no due-contract or reachable delivered-path impact can be classified unrelated pre-existing
work/new scope, with explicit rationale and final reviewer agreement. A reachable current
vulnerability cannot be renamed old debt. No arbitrary cleanup/LOC target.
A still-required tested compatibility path is current supported behavior; leftover code after its
consumer/replacement is retired is milestone debt. An owner/removal label cannot excuse that debt.

Only after API acceptance, required business/native evidence, final correctness/simplicity and
Security/Adversarial PASS, zero debt and verified main delivery may tasks and milestone close.
Do not begin the next milestone's implementation before that gate. Unrelated read-only discovery
may continue. A not-yet-performed review is NOT REVIEWED, never PASS.

## Depot precedent and explicit differences

Read Depot at `20238384013326cddb3296963a6d101a0970efc0`: issue-development.md and
build-and-verification.md, [API task #42](https://github.com/iXab3r/EyeAuras.Depot/issues/42),
[same-milestone findings #80](https://github.com/iXab3r/EyeAuras.Depot/issues/80) and
[aggregate acceptance #188](https://github.com/iXab3r/EyeAuras.Depot/issues/188).
Borrow explicit contracts, owned cheap evidence, real-boundary claims and no carried tails. Its
per-PR review rule, autonomous merge/deploy permissions and auth approval rules are not imported.
