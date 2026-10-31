# Acceptance-first testing and TeamCity superset conformance

> Status: **Accepted direction — D37/D38; execution/bindings are not implemented**
> Owners: Business Acceptance, Testing Architect, Test Steward, TeamCity, REST and affected domain owners

## Contract hierarchy

Highest-level requirements are system Acceptance Criteria, written in English business language:
actor, relevant initial situation, action and observable outcome/forbidden outcome. They describe
projects, configurations, builds, agents, users, tests and artifacts as product concepts. They do not
prescribe PostgreSQL, Chiral, classes, tables, fences, endpoint paths, serializers or process topology.

The canonical business scenarios live in [docs/acceptance](../acceptance/README.md). These Gherkin
files are specifications today: **specified-unbound**, not discovered/passing executable tests.
R0/R1 integration must include these exact files through the existing Automation/Reqnroll consumer
instead of copying requirements into a second feature set. Parsing, generated steps, ignored cases
or placeholder bindings do not satisfy acceptance.

The supporting hierarchy is:

1. Business AC: what the user can accomplish and what must never happen.
2. TeamCity model contracts: resource identity, lifecycle, scope and inherited semantics preserving
   that user outcome. Stable TCM-* invariants are defined below.
3. Public API/wire contracts: exact native/compatible requests, locator/fields/media/ID/status/body/
   side-effect behavior. TC-* and CLI fixture IDs remain technical evidence, not business phrasing.
4. Component/algorithm contracts: the smallest actual state transitions, parsing, persistence,
   transport/process and platform behavior needed to prove the higher contracts.

Changing internal architecture must not require rewriting a business expectation. A real product
policy change updates its AC explicitly; do not weaken it because a replacement cannot yet pass.
Business Gherkin is a specification language, not a requirement to run every example through UI
or launch a process. Use UI when the user interaction is itself the claim, an actual separate Agent
when machine execution/process behavior is the claim, and the cheapest sufficient composition otherwise.

## TeamCity base and extension invariants

D38 requires [executable REST contracts and the initial RED baseline](teamcity-rest-test-first.md)
as the first P0 execution slice. The corpus uses independent reference expectations, strict replay
controls and the real Vivarium boundary with separate result claims. This intentionally broad suite
can contain hundreds/thousands of focused variants; minimal composition does not mean minimal API
coverage. Mandatory TDD/acceptance, known-RED progression and honest partial-milestone reporting are
defined there. No actual new executable corpus or RED result is claimed by this specification.

Vivarium is a semantic superset for builds and agents: preserved TeamCity behavior plus explicitly
named extensions. More capabilities cannot redefine a base role, hide a state axis, reinterpret an
ID, change existing build outcomes or require old clients to understand new workflow objects.

| ID | Preserved contract |
|---|---|
| TCM-01 | Project/configuration/template identity and hierarchy, owned/inherited settings and historical revision meaning remain TeamCity-compatible |
| TCM-02 | A Build is a run with stable identity/provenance; queue/history, ordinary/composite/matrix and upstream/downstream relationships have the frozen compatible meaning |
| TCM-03 | Connected, authorized, enabled and idle/building are independent Agent axes; pools, requirements and compatibility reasons preserve their base meaning |
| TCM-04 | Queue/admission/run/cancel/terminal state and outcome remain distinct and truthful; ambiguous ownership cannot silently advertise safe free capacity or successful execution |
| TCM-05 | Definition/source/runtime parameters, ordered steps, inheritance/reset, triggering, dependency/reuse and artifact-selection semantics match their frozen base contracts |
| TCM-06 | Test/problem identity versus occurrence, historical outcomes, triage, logs, statistics and artifact provenance/retention retain their observable meaning |
| TCM-07 | Roles/permissions/editability/includes/groups/project scopes/delegation and current authorization remain TeamCity 1:1; extension permissions do not replace base grants |
| TCM-08 | Admitted REST/client contracts preserve types/IDs/locators/projections/media/paging/errors/success boundaries and real side effects, with no compatibility-only state or dummy success |
| TCM-09 | Imported retained history and original bytes remain readable independently of TeamCity; explicit diff choices preserve stable mappings and never delete history merely because source cleanup did |
| TCM-10 | Extensions use explicit concepts/settings/namespaces/capabilities and neutral defaults for existing configurations; existing base workflows and clients remain valid when unrelated extensions are used |

The baseline is the frozen owner reference/client profile and independently reviewed official
semantics. A source defect or unexplained HTTP error is not automatically the contract to copy.
Unknown/missing/unimplemented behavior is an open blocker, never a passing or silently waived variant.
Keep the architecture separate: native extensions can add concepts without copying JetBrains internals,
but cannot narrow the required base behavior under an unchanged TeamCity name.

One concrete drift risk is already recorded: disabling blocks ordinary automatic assignment, while
TeamCity documents explicitly assigned diagnostic/custom builds on a disabled agent. Do not turn
the ordinary scheduler predicate into a universal prohibition. AC-AG-02/06, S38 and the model-gap
ledger require the exact targeted-run/permission/reference variant to be frozen and tested. Current
public docs are newer than the pinned server; documentation is an expected-contract input, not a
claim that this behavior was executed against either server.

## Evidence pyramid and independent axes

### Reference behavior precedes implementation policy

For adopted TeamCity resources, derive exact positive/negative cases from the frozen reference
profile: actor/grants, starting state, request/defaults, result/errors and subsequent visible state.
Unknown results are reference-verification tasks, not choices for a more convenient Vivarium rule.
Use prepared isolated reference fixtures for mutation evidence; no production mutation is implied.

Replay corresponding scenarios through the compatible facade and native application surface. Verify
both response contracts and effects: effective settings, queue/run identity, permissions, history,
selected bytes and side effects. Native wire schemas may differ; semantic processing cannot. Allow
only declared environment/identity mappings in comparisons, such as local URLs and mapped allocated
IDs. Do not normalize away state, status/error differences, types, omitted/empty values, ordering,
permission outcomes or duplicate execution. Expected results must not come from the tested facade.

Explicit extension options need paired cases with the option absent and present. An optional native
version check may reject a stale edit only in that opted-in workflow; it must not become a mandatory
precondition or different default for a TeamCity operation. Apply this gate with each delivered
family and the cumulative base suite, without adding a new test framework or mandatory E2E topology.

### Evidence selection

Use many small focused contracts, fewer composed/integration cases and a small set of decisive real
client/UI/platform journeys. No fixed ratio, test-count quota or one-test-per-level requirement.

| Evidence boundary | What is real / controlled | Claim it can establish |
|---|---|---|
| Logic/public domain contract | Production logic, independently authored expected result, virtual time where relevant | Requirements, inheritance, selectors, state rules, parsing and deterministic transformations |
| Composed component/persistence | Actual owning services; real PG/Storage when transactions/bytes are claimed | CAS, atomic claims, current permission reads, idempotency, projection and reference consistency |
| Protocol/controlled external actor | Real production boundary plus scripted Agent/VCS/TeamCity/upstream failures | Request/response variants, drops/stale delivery/retry behavior and controller reactions |
| Causal whole-workflow composition | Real catalogue/queue/execution/results and actual consumers at required boundaries | A requested run causes actual execution, output/results and visible lifecycle rather than authored success |
| Real client/UI/process/platform | Unchanged CLI, actual UI interactions, separate Agent/processes and required OS/provider | Client compatibility, UI operation, containment/restart/cleanup and platform-specific behavior |

Classify functional boundary, environment/provider, in/out-of-process placement, runner and execution
selection separately. Gherkin does not choose a topology. An in-process integration can exercise a
complete public workflow; two processes can still prove only startup. A scripted Agent success result
proves ingestion behavior, not that an executable ran or its artifact bytes are correct.

The existing D20 logic/protocol/fake-provider/real-provider contours remain useful execution
boundaries. They are not the business-requirement hierarchy. Reuse EyeAuras.Automation/Reqnroll/
Playwright and existing .NET test/public contracts; do not create a second runner, scheduler, report
engine or generic simulator to give the pyramid a uniform appearance.

D38's [deterministic composition boundary](teamcity-rest-test-first.md#deterministic-behavior-without-deploying-a-complete-server)
uses production code in process where sufficient, controlled timers and small external-actor drivers.
WireMock is an HTTP transcript adapter; it need not run in every product test. Only real owning
services decide product state. Prove isolation and measure a representative P0.REST.2 cohort before
bulk expansion; reuse real PG where cheaper than maintaining fake persistence. Thousands of cases
should add reviewed data and assertions, not another model, scenario interpreter or fixture per case.

## Evidence selection and oracle

For each task record in its existing plan/issue: actual risk → required observable → existing evidence
→ smallest real composition → oracle → placement/provider/runner → execution selection/cost → exact
command/result → limits. A mechanical change may need inspection/compilation only. New tests protect
a plausible failure and never merely freeze private implementation structure or increase the count.

Expected outcomes come from accepted business/model/wire contracts and independent reference cases,
not the code being tested or facade-generated fixtures. Normalization may remove run-dependent time/
origin/identity only where contractual equivalence is preserved; it cannot erase wrong types, errors,
missing fields, ordering, effects or permission differences. Use cross-surface and extension-neutrality
checks for the same supported base case, with actual state/bytes assertions alongside HTTP success.

Setup/parser/compiler/missing-step failures are not product RED. An empty/all-skipped run, green
health probe, screenshot or generated binding is not GREEN for a business outcome. Preserve original
failure and partial-start cleanup diagnostics; do not mask them with retries or extra wall sleeps.

## Continuous conformance, not a final R5 ceremony

Every build/Agent change names affected AC/TCM/wire variants and existing/new evidence before
implementation. Every milestone candidate proves:

- its new business acceptance and affected base contracts;
- the small cumulative core suite already implemented in earlier milestones;
- relevant cross-surface and extension-neutrality cases;
- preserved assertions when code/tests are replaced, with no silently weakened baseline;
- exact candidate/reference/client/fixture versions, prerequisites, actual selection/results and gaps.

Later requirements stay visibly specified-unbound/open; they are not required to pass before they
are implemented, and their absence cannot become a full-parity claim. Each milestone keeps its own
gate; R5/R6 run cumulative admitted compatibility/migration acceptance. Targeted edits use focused
checks; broad platform/fault matrices run when the affected risk/release gate requires them.

Separate ordinary local/CI, extended integration, long correctness, platform/provider/manual and
performance diagnostics. Measure total preparation/build/run/cleanup as well as case cost. Do not
copy PoeBane's Rust-specific five-second/10–15-minute policies as Vivarium budgets without evidence.
Moving a required long check preserves its assertion, named command, reports and visible failures;
run that slice when the changed contract needs it. Benchmarks answer cost questions, not business correctness.

## Roles and traceability

Business Acceptance owns user-language AC and independent expected outcomes. TeamCity/REST owners
own base-model/wire semantics. Testing Architect selects minimal trustworthy composition/oracles and
coverage migration; Test Steward maintains bindings, fixture lifecycle, execution and honest evidence.
Roles can be co-held for work; self-adoption is not independent review. Use the existing independent
milestone/domain/security/simplicity review gates, without a new per-test approval ceremony.

The [acceptance map](../../.workspace/workstreams/coherent-refactor/acceptance-map.tsv) links business
AC to existing S* candidates, TC* requirements, TCM invariants and milestone. Relationship counts are
planning traceability only. Runtime evidence must add a real case/command, candidate revision and
result before a criterion is accepted. Do not duplicate the entire feature text in every issue.

## Reference practice

Adapted from PoEBane.Rust main 2984e1f84cd8e5042dfa976c23e75a26ad63cb1d, especially
[Testing architect](https://github.com/iXab3r/PoEBane.Rust/blob/2984e1f84cd8e5042dfa976c23e75a26ad63cb1d/docs/roles/testing-architect.md),
[verification taxonomy](https://github.com/iXab3r/PoEBane.Rust/blob/2984e1f84cd8e5042dfa976c23e75a26ad63cb1d/docs/practices/build-and-verification.md)
and simulator causal ownership. The role was revised by 48a4735f5ea684bdce73296a8ef7f8ae2934ca1b
(2026-10-27). Source-owner dirty/local older checkout was read only; fresh roles were inspected from
the pinned remote revision. This imports principles, not PoeBane permissions, numeric budgets or tooling.
