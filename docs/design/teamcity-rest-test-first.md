# Test-first TeamCity REST compatibility gate

> Status: **Accepted direction — D38; corpus, harness and RED baseline not implemented yet**
> Owners: TeamCity/REST, Testing Architect, Test Steward, Business Acceptance, affected domain owners

## Outcome and timing

REST compatibility is mandatory acceptance and TDD evidence, not a final R5 wrapper check. The first
P0 execution slice builds the reference-backed case corpus and test harness, then records genuine
Vivarium failures **before structural product replacement**. R0 still delivers the first reproducible
new-stack Host plus separate Agent; it does not become a prerequisite for writing the whole corpus.

The 30 public API groups/449 operation headings and 456 synthetic CLI fixture requests are discovery
inputs with different units, not executable coverage. Required variants should naturally produce
hundreds and potentially thousands of focused cases. Breadth is intentional. Do not inflate it with
an unverified Cartesian product, duplicate assertions or deliberately failing placeholder tests.

This specification establishes delivery gates. It does not claim that those cases exist or are RED
today. The existing 110 business AC remain the canonical higher-level requirements, independently of
the technical HTTP cases. Consume their existing files when binding them; do not duplicate Gherkin.

## One expected contract, distinct evidence targets

Use independently established case expectations with stable IDs across these targets:

| Target | What actually runs | What a successful result proves |
|---|---|---|
| Reference evidence | Approved reference metadata/docs/version-matched source/client assertions and, where needed, isolated real TeamCity interactions | The expected behavior has a defensible source; confidence and unresolved variants remain explicit |
| Strict reference replay | WireMock.Net serves the independently authored responses/sequences; real HTTP requests or unchanged clients drive it | Request construction, replay plumbing, client interpretation and the test assertions work for that evidence |
| Vivarium conformance | The real Vivarium HTTP/application boundary receives the corresponding requests; real owning services process mutations | Compatible responses and actual observable effects match the expected contract |

WireMock.Net is the preferred .NET replay library, not another test framework. Reuse the existing
NUnit/Test SDK stack. It supports matching method/path/query/headers/cookies/body and explicit scenario
states [W1, W2]. Pin a compatible package version after a small real integration proof; no dependency
has been added by this document. Prefer local in-process replay where enough; no Java/Docker service
is required merely to replay HTTP. A dedicated conformance test target/project may use the same NUnit
stack and standard test results; it is not a new runner or a second requirement corpus.

Do not put WireMock in place of Vivarium while claiming product conformance. A stub built from the
same expected response is self-consistent by construction: its GREEN result does not validate the
oracle or establish TeamCity equivalence. Likewise, do not implement a second scheduler, role engine,
catalog or provider in the mock. Replay bounded observed sequences and faults; real Vivarium must
produce its own settings, authorization, queue/history transitions and byte references.

## Deterministic behavior without deploying a complete server

Owner clarification, 2026-10-31: thousands of cases must be simple and cheap, and the test
infrastructure must help reduce total maintained code. Use a small deterministic test composition
around **production Vivarium code**, with WireMock as its HTTP replay adapter. Do not make a stateful
TeamCity emulator the foundation of every test. This refines D38, not the accepted product semantics.

WireMock scenario states are sufficient for finite request/response transcripts [W2]. They do not
provide the product scheduler, virtual time for all components, database isolation or gRPC Agent
execution. Do not force those boundaries through HTTP or write them inside response callbacks.
ASP.NET Core TestServer can exercise an actual application's HTTP pipeline in process [H1]; this
avoids a deployed Server executable without replacing its routes, binding, auth or serialization.
The current Host is not yet composed that way: this is a proposed delivery seam, not existing proof.

| Question | Smallest sufficient composition | Deliberate limit |
|---|---|---|
| Does a client send/interpret the frozen contract? | Real client plus strict WireMock transcript | Proves client/replay controls, not Vivarium behavior |
| Are locator/media/projection/error variants compatible? | Actual production parser or HTTP pipeline, small immutable fixtures and independent expected data | Direct parser tests do not prove routing; HTTP cases keep the real pipeline |
| How does Vivarium react to Agent messages, deadlines or failures? | Actual owning services and relevant REST boundary, controlled time and scripted external actors | Driver emits specified messages; Vivarium decides permission, compatibility, ownership and transitions |
| Are queries, grants, claims, revisions and bytes correct? | Same production services with real PostgreSQL, Git or Storage for the affected claim | Fake storage or SQLite cannot prove PostgreSQL behavior; a transcript cannot prove activation or durable effects |
| Does a client/build/process really work? | Small decisive real Kestrel/client/Agent/workload journeys, plus focused real-platform/fault gates | TestServer has no real TLS/socket/process evidence; scripted completion is not executed work |

These are evidence choices within the D37 pyramid, not five new test tiers or a new runner. A case
records its cheapest sufficient boundary; do not run every case against every target. Cover required
variants at their actual boundary, with a selected overlap between cheap compositions and real-host
journeys to detect composition drift. WireMock replay need not be on the product execution path or
repeated per case when no client, upstream HTTP behavior or assertion-control claim requires it.

### Permitted simulator state and causal ownership

The shared fixture may own a transcript cursor, named input bindings, an external connection state,
queued scripted deliveries, captured requests, controlled clock and isolated resource lifetimes.
Keep driver actions explicit: send the specified Hello/ACK/result, withhold a message, disconnect,
release a barrier or advance time. AgentHub drivers use its actual protocol/accepted seam, not a new
HTTP Agent API. Build-specific test input/output is allowed; a generic build executor is not.

It must not choose eligible agents, resolve roles/inheritance/locators, allocate authoritative runs,
implement queue optimization or compute expected outcomes. Reusing production logic inside the
tested composition is intended; using it to produce the expected result is not. A stubbed external
Agent can exercise result ingestion while the real controller owns its transition, but that case
cannot claim an executable ran. Never rewrite the product database to the expected post-action state.
Pre-action seed data may use the real owning schema for an unrelated precondition; creation/auth/
execution claims must exercise their actual boundary. No privileged test route or auth bypass.

For example, arrange a source-backed queued-build fixture and two reported Agent parameter sets;
submit the request to the actual service/REST route and observe its assignment. The driver can then
send the specified ACK or disconnect. The expected assignment/state is frozen reference evidence;
neither the driver nor the assertion helper runs a second compatibility/scheduling algorithm.

Control timer scheduling as well as the current date: FakeTimeProvider supports explicit advances
and timers [H2]. Merely overriding GetUtcNow does not virtualize PeriodicTimer or Task.Delay. Inject
one clock through the participating production timers; use existing one-pass operations when the
claim is that operation, and a smaller timer-wiring test when the background trigger is the claim.
Advance in explicit steps and await an observable completion/barrier. Advancing the clock does not
drain arbitrary asynchronous work or serialize PostgreSQL transactions. Bound waits with a separate
real-time watchdog so a stalled virtual clock cannot hang CI. Exercise concurrency using explicit
barriers and real competing operations; virtual time alone does not prove race freedom.

### Keep thousands of cases boring

- Use stable NUnit data cases: fixture/precondition, request or short action sequence, literal
  expected response/effects and source. Small C# builders or shared immutable fixture files are
  enough; no scenario language, interpreter, generic workflow engine or second Gherkin corpus.
- Share one request driver/comparator and only demonstrated setup. Preserve useful per-case
  assertions and failure diffs; helpers must not contain product decisions or calculate the oracle.
  Do not snapshot huge generated trees when a small explicit contractual projection is sufficient.
- Reuse expensive immutable preparation and an owned PG instance per worker/run where isolation
  permits; give each case isolated mutable data, clock, actor state, Git/Storage roots and journal.
  Choose/prove database reset or independent database boundaries against actual connection/background
  behavior. An outer transaction is not assumed to cover separately opened application connections.
  Do not start a container, migrate a fresh cluster or launch an Agent per locator/media variant.
- Compare costs before adding fakes. Real local PG may be simpler than maintaining another query/
  transaction model [H3]. No speculative repository layer or universal in-memory database for tests.
- Record fixture C# size, hand-maintained fixture data, new product hooks, setup/run/cleanup time,
  discovery overhead, memory and failure artifact size on representative batches. Growth in case
  data is distinct from growth in executable framework logic; both have maintenance cost. Repeated
  transcripts need shared immutable inputs, not a more powerful emulator. Make no speed/LOC claim
  without measurements; do not invent a fixed quota or a new metrics service.

If a helper starts deciding business policy or a new ordinary variant needs new framework logic,
stop and simplify its boundary. If the cheap composition needs substantial production-only-for-tests
plumbing, retain the small existing real-host fixture until a measured refactor is justified.
Deleting old tests requires preserving their unique assertions and evidence boundary, not just count.

### P0.REST.2 composition proof before bulk expansion

First prove a small representative cohort on the existing stack: a read/projection variant, a
mutation with real read-back and denial, a scripted Agent delivery/fault, and a timer boundary.
Use only reference-established expectations; missing prerequisites stay explicitly blocked. Compare
the same applicable cases through the existing real Host and the minimal in-process composition.
Include a real PG concurrency/durability check when that target storage is available; transitional
SQLite evidence remains labelled as such and does not close the PG gate. R0 still supplies the real
new-stack Host+Agent journey; this spike does not defer the first genuine current-product RED to R0.

Publish the composition/evidence matrix, startup/reset isolation controls, repeatability results,
bounded failure/cleanup behavior and measured maintenance/runtime cost. Select the cheaper adequate
composition from that evidence before materializing thousands of variants. This is part of the
existing P0.REST.2 gate, not a new milestone or authorization to build a simulator platform.

## Establish expectations without guessing

For each case record the source reference/profile, operation and exact variant, business/TCM links,
preconditions, request, expected response, expected effects, permitted environment/ID mappings and
fixture/cleanup requirements. Attach evidence at the level that supports the asserted value.

- Freeze the installed reference profile: server 2026.1.3, API 2026.1 and plugin 222742, plus actual
  client versions and controlled fixture inputs. Public help currently rolls to a newer version;
  public REST Java master fc730e6 is from 2024. Neither silently establishes installed-version behavior.
- Use official method/model descriptions, installed Swagger when exported through approved tooling,
  version-matched implementation and actual `@eyeauras/teamcity-cli` decoders/assertions. Client
  synthetic fixtures are useful seeds, not captured server responses. Schema proves shape, not every
  default, permission, failure or side effect. An unexplained CLI 400 is not an oracle.
- Distinguish raw HTTP evidence from a CLI's transformed JSON report. Capture status, relevant
  headers and original body shape through approved tooling; a decoded/formatted client value is
  evidence for that decoder, not automatically the server's wire representation.
- Where the result is unspecified or evidence conflicts, obtain an isolated reference observation
  or leave the case blocked on reference evidence. Do not select a convenient expected value.
- #33 owns the pinned reference acquisition contract; #35 delivers and demonstrates the supported
  `@eyeauras/teamcity-cli` acquisition capability, and #42 verifies its controls. Required outputs are
  exact installed schema bytes/profile/hash and sanitized raw HTTP status/relevant headers/original
  body, command/tool version and fixture/permission provenance. Inspect actual supported capabilities;
  if missing, deliver a bounded typed CLI export/capture increment under this same P0 work, rather
  than inventing a command or bypass. #36 binds outputs and unresolved per-case reference gaps.
  Keep capture on approved tooling; no removed CLI, hidden raw production requests or new authority.
  Mutation observations require authorized disposable reference fixtures; if access is absent record
  the exact missing prerequisite and continue grounded cases. No production capture mutation follows
  from this planning repair; the commands, capability and fixtures remain implementation deliverables.
- Keep real credentials, session cookies and production payloads out of committed fixtures. Use
  safe synthetic data and typed placeholders while preserving relevant formats and permission scope.

Expected values must not be generated from Vivarium handlers, DTOs, serializers, OpenAPI or responses.
Generation may expand reviewed independent data into parameterized NUnit cases [N1]; it cannot invent
unsupported variants or use the tested implementation to compute its own oracle.

## Variant coverage

Applicability is reviewed per operation, not multiplied blindly:

| Dimension | Cases to specify where applicable |
|---|---|
| Routing and requests | Method, route/context/version aliases, encoding, content type, required/optional body and malformed input |
| Response shape | JSON/XML/plain text, declared headers/status, scalar/collection wrappers, omitted/null/empty, exact ID and numeric representations |
| Selection | Nested/encoded locators, fields/default projection, name/ID lookup, defaults, sort/order, paging/lookup limits and empty pages with continuation |
| Authority | Real supported credential forms, anonymous/guest, user/build scope, revoked/expired credentials, self/other/sibling access and inherited roles |
| Mutations | Create/edit/delete/copy/move, append/replace/reset/order, inheritance, duplicate IDs/names, conflict handling and reference apply boundary |
| Lifecycle | Queued/running/finished, status versus state, cancel/requeue, approvals, settings freeze, optimization/reuse/replacement, detached execution |
| Repetition and concurrency | Identified retry versus unkeyed invocation, simultaneous edits/admission, partial bulk results, late/stale input and restart where required |
| Bytes and history | Metadata/tree/archive/file/log behavior, path/range handling, exact bytes/hashes, retention/unavailability, current access and imported links |

Verify mutations by follow-up observations through the actual product API and corresponding native
surface. Assert durable/restart properties at real PG/Storage/process boundaries when that is the
claim. Actual execution needs a real Agent/workload; thousands of media/locator cases do not each
need a machine process. API status GREEN alone does not prove the requested action happened.

## Replay controls and false-GREEN protection

Use strict mappings and request-journal verification [W3]. Reject unexpected requests and reset
scenario state between cases. No broad catch-all 200, automatic passthrough to live TeamCity or
unbounded logs. Match only declared variable fields; do not normalize away wrong types, missing
fields, statuses, order, permissions, state or duplicate work. JSON object-key order and header-name
casing can be ignored where their protocols make them insignificant; arrays are not sorted blindly.

A default unmatched WireMock 404 is a harness/replay failure, not evidence for a TeamCity not-found
case. Product negative cases also need sufficient assertions and positive family/control probes so a
server that returns 404 for every path cannot pass a family. Add a small set of assertion controls
that deliberately change a recorded status/type/field/order/byte and prove the comparator fails.
These controls test the shared harness, not a parallel model of product behavior.

Establish Vivarium process/transport/authentication readiness independently of the compatible route
under test. If a required route is absent on a healthy correctly targeted server, that is meaningful
product RED. If the server never started, the profile/credential is wrong or replay matching failed,
that is not product RED. Never use the missing compatible route itself as the sole readiness probe.

## Honest result categories and the initial RED baseline

| Result | Meaning and handling |
|---|---|
| Product GREEN | A real bound case passes its assertions against Vivarium; keep it in the permanent regression set |
| Product RED | A source-backed bound case reaches the intended healthy product boundary and observes wrong/missing behavior |
| Product-blocked | A known missing product prerequisite prevents a dependent case; retain its root cause and do not count every cascade as an independent defect |
| Reference-blocked / unbound | Expected behavior or execution binding is not established; specification/discovery is neither RED execution nor GREEN |
| Harness/environment failure | SDK/build/startup/auth fixture/replay/cleanup/selection failure; repair it, never absorb it into an expected product-RED allowance |

NUnit assertions must exercise actual requests and effects. No `Assert.Fail("not implemented")` test
placeholders, Ignore/Explicit/xfail used to hide future contracts, empty sources or swallowed setup
exceptions. Product missing-route/operation failures may be legitimate RED; a failure in the test
scaffolding is not. Report raw executed failures and blocked/unbound counts separately.

Each materialized variant is an individually discoverable case with a stable name/ID, not one giant
test loop that obscures its failures. Verify discovered/executed counts against the selected ready
case manifest. The same case replayed on two targets is not two different compatibility requirements.

P0 records stable case IDs, exact inputs/evidence hashes, product revision, actual target identity,
assertion differences, root causes, cleanup and owning milestone. Only executed product-RED cases
can enter its known-RED baseline. Do not call the existing discovery inventories that baseline.

## Mandatory TDD and milestone acceptance

1. Establish/review the relevant reference case before changing its product behavior.
2. Run it against Vivarium and preserve the real product RED. If it already passes, preserve that
   existing GREEN as baseline evidence; do not break it merely to manufacture a RED demonstration.
3. Implement the smallest shared semantic change and get the affected cases GREEN.
4. Refactor with the implemented compatibility set, reference replay controls and affected native/
   business checks still GREEN. Changing expected results to fit the implementation is not TDD.

Every affected milestone runs mandatory conformance acceptance. During TDD use focused changed-case
checks; D39 batches broader checks and formal review at coherent milestone gates rather than each
task/edit/child merge. Previously GREEN cases cannot be
added back to the known-RED set to conceal regressions. Newly found required failing variants reopen
their family's acceptance. A new reference version is a reviewed separate profile, not a rewrite of
the old oracle. Use no automatic baseline expansion or wildcard permission to fail.

RF-10 still permits partial milestones in main. Use explicitly separated standard test targets:
ordinary solution checks, replay control, delivered product-conformance cases and the full raw
product-conformance report. A dedicated same-framework conformance entry point avoids putting
intentionally unfinished product cases in an ordinary test command that claims to be all GREEN.
All targets must remain available/buildable in required verification; execute the full raw target
for the first baseline and later when due under [checkpoint/reuse rules](api-ledger-delivery.md#valid-evidence-reuse-including-delivery-to-main).
Due/cumulative acceptance always needs actual valid boundary evidence; wiring a target is insufficient.
Do not infer a full future-known-RED rerun per ordinary handoff/task/status query. Exact commands,
selections and triggers belong to #35's first harness delivery and do not exist in this seed.

The full raw conformance run remains visibly RED/nonzero while required failures remain. A narrower
milestone regression gate may pass only its declared delivered set plus no unexpected regressions;
its name/report must state that scope and remaining RED. Preserve standard raw test results and the
existing reporting path, not a custom format or an `allow_failure` that labels parity successful.
When the full target is run, keep its failure and raw artifacts distinguishable from a passing partial
gate. D39's ledger reports the full known case set while separating new execution, valid unchanged
evidence and stale/unknown cases; generating status must not launch suites or claim a fresh full run.
Changes to failure kinds/root causes within known-RED cases stay visible for triage; the case
ID is not permission to swallow any exception.
Harness/environment failures always fail the gate. Full API/family acceptance requires all required
cases GREEN and no unresolved reference/product/binding blockers; R5/R6 cannot waive the baseline.
The [D39 delivery contract](api-ledger-delivery.md) binds each milestone to contract/code/REST-test
tasks, mechanical API-complete status, final aggregate and Security/Adversarial review, and no debt
carryover. P0 baseline acceptance is not product API-complete.

## First executable checkpoints inside P0

### Which gaps block which work

Keep these gates distinct; a visible blocker is not automatically a whole-project stop:

| Gate | Required before proceeding | Other unknowns |
|---|---|---|
| First verified cohort | Independent expectations and real readiness/bindings for that cohort, with honest control/product results | Other reference/product gaps remain visible; do not wait for whole-API prose or the replacement stack |
| P0 exit / R0 structural start | Executable P0 baseline/ledger controls and existing P0 reconciliation/allocation gates; canonical R0 resource/ID/state/authority/mutation/protocol contracts resolved | Explicit later-domain reference gaps retain owners/due gates and block their affected claims; they cannot conceal unfinished P0 census gates or an R0 structural dependency |
| Owning subsystem acceptance | Frozen due scope, real required GREEN and current prior-base evidence, no required reference/binding/product/harness/stale blockers or milestone debt | Planned later functionality remains separately allocated; no scope relabel or automatic waiver |

When a gap may affect shared identity/schema/state/authority or the R0 journey, establish that
dependency before treating it as later-domain detail. This clarification does not reduce the P0
exit scope or permit a missing required contract to pass; it prevents unrelated late unknowns from
blocking the first sound executable cases. P0 acceptance still does not mean product API-complete.
The [allocation/freeze rule](api-ledger-delivery.md#p0-allocation-and-due-variant-freeze) distinguishes
full known-discovery allocation from exact due-case freeze, including machine-readable later gaps
and the mandatory structural/R0 impact assessment. It preserves all existing P0 gates.

| Checkpoint | Concrete output and exit evidence |
|---|---|
| P0.REST.1 | Freeze a broad, cross-family, independently sourced case corpus and reviewed applicability ledger; stable case IDs/provenance, exact first positive/negative expectations, missing-reference entries explicit |
| P0.REST.2 | Prove the minimal production-code composition and bounded external actors on representative cases before bulk expansion; implement same-framework conformance and strict WireMock.Net replay; client/assertion controls, timer/reset/isolation/cleanup, target selection and measured cost documented |
| P0.REST.3 | Execute that verified corpus against the actual healthy Vivarium boundary; publish genuine RED/GREEN/product-blocked results and exact known-RED baseline; prove that a wrong endpoint/status/type/effect is detected |

These checkpoints are the first P0 implementation slice, not merely a harness design deferred to R0.
Prepare the exact SDK/tooling first. Extend the corpus toward the full required operation/variant set
before and alongside each owning domain slice; do not wait for R5 or write thousands of guesses to
reach a number. First RED must cover meaningful reads/writes, protocol/error/permission branches,
state effects and bytes where prerequisites exist, not only two discovery GETs. Product-blocked cases
remain visible and grow into execution as their real prerequisites land.

Current execution evidence: none for this replacement suite. Existing tests use NUnit 4.3.2 and
Microsoft.NET.Test.Sdk 17.14.0; no WireMock package is currently referenced. The declared SDK 10.0.303
is not installed in the inspected environment (10.0.103 is present; inspected 2026-10-31). No package
was added or product code changed; no build, runtime test, release or fleet operation was run.

## Sources

- **W1:** [WireMock.Net request matching](https://wiremock.org/dotnet/request-matching/).
- **W2:** [Bounded scenario states](https://wiremock.org/dotnet/scenarios-and-states/).
- **W3:** [Request journal and unmatched-request controls](https://wiremock.org/dotnet/admin-api-reference/), [stubbing and reset](https://wiremock.org/dotnet/stubbing/).
- **W4:** [WireMock.Net project/license](https://github.com/wiremock/WireMock.Net): Apache-2.0, consistent with repository dependency policy.
- **N1:** [NUnit data-driven cases](https://docs.nunit.org/articles/nunit/writing-tests/attributes/testcasesource.html).
- **H1:** [ASP.NET Core in-process integration testing](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0). TestServer exercises the application pipeline; real transport/process claims require separate evidence.
- **H2:** [FakeTimeProvider and explicit timer control](https://learn.microsoft.com/en-us/dotnet/core/extensions/timeprovider-testing). It does not replace async/DB completion synchronization.
- **H3:** [Database strategy trade-offs](https://learn.microsoft.com/en-us/ef/core/testing/choosing-a-testing-strategy). This is evidence about fake-provider limits and real-database cost, not a decision to adopt EF or its test architecture.
- **Reference inputs:** [REST inventory and gaps](../acceptance/rest-gap-review.md), [reference-constrained handling](teamcity-rest-recommendations.md), [testing hierarchy](testing-model.md).
