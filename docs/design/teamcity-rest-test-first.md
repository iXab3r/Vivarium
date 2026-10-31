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
- Keep reference capture on the approved CLI; missing typed schema/contract export is named tooling
  work. Do not substitute the removed CLI or hidden raw production requests. Mutation capture uses
  prepared disposable reference fixtures, not the production farm during planning.
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

Every affected milestone/merge runs mandatory conformance checks. Previously GREEN cases cannot be
added back to the known-RED set to conceal regressions. Newly found required failing variants reopen
their family's acceptance. A new reference version is a reviewed separate profile, not a rewrite of
the old oracle. Use no automatic baseline expansion or wildcard permission to fail.

RF-10 still permits partial milestones in main. Use explicitly separated standard test targets:
ordinary solution checks, replay control, delivered product-conformance cases and the full raw
product-conformance report. A dedicated same-framework conformance entry point avoids putting
intentionally unfinished product cases in an ordinary test command that claims to be all GREEN.
It must still be built and executed by required verification; it must not disappear from CI because
it is outside the ordinary solution selection. Exact commands belong to the first harness delivery.

The full raw conformance run remains visibly RED/nonzero while required failures remain. A narrower
milestone regression gate may pass only its declared delivered set plus no unexpected regressions;
its name/report must state that scope and remaining RED. Preserve standard raw test results and the
existing reporting path, not a custom format or an `allow_failure` that labels parity successful.
CI must keep the failing full target/job and its raw artifacts distinguishable from a passing partial
gate. Changes to failure kinds/root causes within known-RED cases stay visible for triage; the case
ID is not permission to swallow any exception.
Harness/environment failures always fail the gate. Full API/family acceptance requires all required
cases GREEN and no unresolved reference/product/binding blockers; R5/R6 cannot waive the baseline.

## First executable checkpoints inside P0

| Checkpoint | Concrete output and exit evidence |
|---|---|
| P0.REST.1 | Freeze a broad, cross-family, independently sourced case corpus and reviewed applicability ledger; stable case IDs/provenance, exact first positive/negative expectations, missing-reference entries explicit |
| P0.REST.2 | Implement reusable same-framework conformance tests and strict WireMock.Net replay; unchanged-client/request controls GREEN, assertion-failure controls work, isolation/cleanup and target selection proven |
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
- **Reference inputs:** [REST inventory and gaps](../acceptance/rest-gap-review.md), [reference-constrained handling](teamcity-rest-recommendations.md), [testing hierarchy](testing-model.md).
