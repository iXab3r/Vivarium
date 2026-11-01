# First executable REST cohort

> Status: **First read/create cohort executed RED; P0 gates remain incomplete**
> Lead: Test Steward; co-held TeamCity, Vivarium REST and Testing Architect perspectives
> Tasks: [#33](https://github.com/iXab3r/Vivarium/issues/33), [#35](https://github.com/iXab3r/Vivarium/issues/35), [#36](https://github.com/iXab3r/Vivarium/issues/36), [#42](https://github.com/iXab3r/Vivarium/issues/42)

This is an incremental D38 cohort before structural replacement. The actual current controller,
authenticated through its real HTTP pipeline, returns 404 for two independently specified reads
and the project-creation request. The compatible model/routes remain unimplemented. The materialized
selection contains three cases; it is not the full known REST universe or completed P0.REST.1–3
acceptance. Required baseline checks also exposed a canonical-Git-byte defect, fixed under
[#77](https://github.com/iXab3r/Vivarium/issues/77) without changing its accepted contract.

## Independent reference and cases

Read-only acquisition on 2026-11-01 reconfirmed TeamCity **2026.1.3 (build 222742)**, API **2026.1**,
and rest-api plugin **222742**, loaded and enabled. The supported CLI 0.5.2 did not export original
entities/status/schema. A [bounded typed source increment](../../tools/teamcity-reference/README.md)
at CliFactory `b158fe03eab3c31982a21b36c278f690f6b541d9` acquired them through its existing profile,
credential/ReadOnly gate and transport. It was tested locally and remains unpublished.

| Stable case | Operation / owner | Exact request | Independent response |
|---|---|---|---|
| `TCREST-SERVER-API-VERSION-TEXT-001` | `TCREST:rootapi.getApiVersion`; SS-HOST | GET `/app/rest/apiVersion`; Accept `text/plain` | 200; `text/plain`; six bytes `2026.1` |
| `TCREST-PROJECT-ROOT-FIELDS-JSON-001` | `TCREST:projectapi.getProject`; SS-CFG | GET `/app/rest/projects/id:_Root?fields=id%2Cname`; Accept `application/json` | 200; `application/json`; `{"id":"_Root","name":"<Root project>"}` |
| `TCREST-PROJECT-CREATE-READBACK-JSON-001` | `TCREST:projectapi.addProject`; SS-CFG | POST `/app/rest/projects` with explicit id/name and `parentProject.locator: id:_Root`; then GET the supplied ID with `fields=id,name` | Declared POST 200 JSON object, then GET 200 JSON supplied id/name; source-established, **not an observed reference mutation** |

These support [AC-AUTO-01](../acceptance/automation/compatibility.feature),
[TCM-08](testing-model.md) and [TC-WIR-01/02/04/05/07/12](teamcity-parity.md).
They do not bind the whole business scenario. Preconditions are the installed authenticated
read-only reference profile and the built-in root project. The two captured reads have no generated
IDs, private names, cleanup mutations, environment mappings or expected side effects.

The create/readback case uses installed schema pointers
`/paths/~1app~1rest~1projects/post`, `/definitions/newProjectDescription/properties/id`, `name`,
`parentProject`, and the projected-project GET contract. The
[documented workflow](https://www.jetbrains.com/help/teamcity/rest/create-and-delete-projects.html)
supports explicit input identity/name and root-parent locator semantics; its rolling version alone
is not the pinned oracle. The installed schema declares 200/project JSON and the explicit property
types, without a required-property list or POST `fields` parameter. The test therefore asserts only
POST status/media/JSON-object, then observes supplied id/name through a real GET on success. It
does not invent POST default fields, archived state, permission denial, cleanup status or duplicate
semantics. The fixed synthetic ID is scoped to a fresh ephemeral Vivarium Host and removed with its
directory. No project was created on TeamCity. The small installed source excerpt is retained beside
the [safe fixtures](../../tests/Vivarium.Tests/TeamCity/Reference/).

Sanitized original capture records are retained with the tests:
[API version](../../tests/Vivarium.Tests/TeamCity/Reference/api-version.json) and
[root projection](../../tests/Vivarium.Tests/TeamCity/Reference/root-project.json).
Their entity SHA-256 values are respectively
`226fe4d6075c39f1125f08546d92c565a6f2dcee8557921e9a04d5d825860229` and
`bc53dbc4c89daf51c3f95c4dd665bd3ba6054d76eca30b44767f5ae22909f5bf`.
Fixture-binding controls verify both independent records against the C# expected status/media/body
and hash. The expected values never come from Vivarium. JSON object-key order is insignificant;
field presence, values/types and array order remain significant. Non-JSON entities compare exact bytes.

Installed Swagger 2.0 was also acquired: 562,948 bytes, SHA-256
`22e37bbaf8bdb48f5f67eade2cd3fe206a193eb8eb4de10178909386444104dc`, 268 paths / 449 HTTP operations.
It confirms these GET response media/shapes and optional `fields`, not defaults/permissions/effects
or a normalized variant denominator. Full schema and acquisition manifest stay in ignored
`artifacts/p0-rest-first/reference/`; host/base-path metadata is not committed. Vendor-build version
captures remain reference metadata; their required compatibility mapping is unresolved, not waived.

## Boundaries and controls

[Ordinary tests](../../tests/Vivarium.Tests/TeamCity/) use NUnit 4.3.2/Test SDK 17.14.0,
WireMock.Net **2.15.0**, and Microsoft.Extensions.TimeProvider.Testing **10.0.0**. The versions were
pinned after actual restore/build/integration. WireMock uses exact method, URL/query, Accept and
synthetic credential matching, with partial matches and admin interface disabled and a bounded
journal. The positive transcript verifies the journal's matched mapping; wrong method/path/query/
credential probes remain explicitly unmatched harness controls. Reset discards mappings and journal.
No live passthrough, catch-all success or domain simulator is installed.
A finite source-established POST→GET transcript also proves request body/media and sequencing;
resetting scenario state makes a premature read unmatched. Its two literal responses contain no
mock catalog or computed mutation policy. Replay is not proof that the real mutation happened.
The mutation's follow-up read is required once POST succeeds; current POST 404 correctly stops that
sequence, rather than counting the dependent read as another independent product defect.

Seven corruption controls reject wrong status/media/field/type/array order/byte and malformed JSON.
A positive control preserves JSON object-key neutrality. Malformed actual JSON is a response mismatch;
malformed independent expected JSON remains a fixture error. These are synthetic assertion controls,
not extra TeamCity requirements or product passes.

[CurrentController](../../tests/Vivarium.Tests/TeamCity/CurrentController.cs) reuses the existing
production Kestrel Host with real TLS pinning, auth, Git and **transitional SQLite**. Independent native
`/api/v1/system` probes prove valid bearer success and invalid bearer denial before compatible reads.
A setup/auth/startup failure is a harness failure, not raw product RED. This composition proves no
PostgreSQL, Chiral replacement, TestServer, separate Agent or executed workload behavior.

A supporting native composition test runs the actual `BuildQueueTimeoutMonitor` with two isolated
SQLite databases, matching fixture IDs and independent FakeTimeProvider clocks. A timer-registration
barrier precedes explicit advances; real-time watchdogs bound asynchronous database completion.
Only the advanced queue expires, through production logic. It does not call the one-pass sweep to
manufacture timer evidence. Its pre-action queue seed does not prove REST submission or TeamCity
queue semantics. Cleanup stops monitors/Host, drains only their SQLite connection pools, clears
owned Git read-only attributes and removes each fixture directory. No production hooks were added.

## Commands and reporting

Use exact SDK **10.0.303** (`global.json` is unchanged). This machine's verified private executable is
`artifacts/p0-rest-first/tooling/dotnet/dotnet.exe`; set process-local DOTNET_ROOT/DOTNET_ROOT_X64 and
prepend that directory to PATH so child builds use the same SDK. The SDK archive's SHA-512 was checked
against Microsoft's release metadata. No machine-wide SDK was replaced.

```powershell
# Ordinary current-product regressions and controls. Builds the raw project as a dependency.
dotnet build
dotnet test

# Focused strict replay/assertion controls; does not claim product compatibility.
dotnet test tests/Vivarium.Tests/Vivarium.Tests.csproj --filter TestCategory=TeamCityReplayControl

# Native HTTP/timer/isolation fixture controls.
dotnet test tests/Vivarium.Tests/Vivarium.Tests.csproj --filter TestCategory=TeamCityCompositionControl

# Full MATERIALIZED raw product cohort: currently three failures, process exit 1.
dotnet test tests/Vivarium.TeamCity.Conformance/Vivarium.TeamCity.Conformance.csproj --logger "trx;LogFileName=raw.trx" --results-directory artifacts/p0-rest-first/results/raw
```

The raw project is an ordinary NUnit/Test SDK project outside the solution's test selection. The
ordinary test project has a build-only project reference so root build compiles it. Neither ignored
cases, a default test filter nor an expected-failure allowance hides its assertions. An explicitly
invoked raw command executes every materialized case and stays nonzero while they fail. Discovery
and execution counts are checked from standard TRX. There are **zero delivered TeamCity GREEN cases**;
there is currently no nonempty delivered-compatibility gate. No empty filter or native readiness
substitute claims that fourth target. Establishing its first real GREEN cases remains a gate gap.

Run affected cases during TDD and these broader checks at coherent checkpoints. Status updates do
not execute tests. Preserve original receipts; later reuse needs the D39 input/code/environment impact
decision. The initial raw artifacts are `artifacts/p0-rest-first/results/raw/raw-initial.trx` and
`raw-initial.log`: two executed failures, zero skips, expected 200 / observed 404, after healthy native
readiness. The later full three-case run preserves that initial receipt and adds the independently
specified project-create failure. These exact known-RED IDs are not permission to absorb any future
exception or to label a setup/JSON parsing failure as an allowed missing route.
The [first-cohort receipt](../../.workspace/workstreams/coherent-refactor/rest-first-cohort-results.json)
records final selected commands, hashes, candidate/source identity, costs and limits. Earlier
development-control failures remain diagnostic artifacts, not current GREEN evidence.

Final local checkpoint on Windows with SDK 10.0.303:

| Selection | Observed result |
|---|---|
| Required root build | PASS; zero errors; existing dependency advisories retained in the receipt |
| Required root test | PASS, exit 0; TRX has 359 Passed and 12 NotExecuted with existing platform prerequisites; zero Failed; 4m50s |
| New replay/composition controls | All 16 Passed in that fresh ordinary run; no new skipped cases |
| Git legacy/newline regressions | All 15 focused cases Passed, including exact legacy bytes/hash, read/mutate/restart and nonuniform-ending rejection |
| Full materialized raw cohort | Three source-backed failures, exit 1, zero harness errors/skips; missing GET/POST routes returned 404 instead of 200 |

The console reports 359 Passed / 11 Skipped / 370 total while standard TRX retains 371 result rows
(359 Passed / 12 NotExecuted). Both original outputs are preserved; the receipt uses explicit raw
units and does not guess an adapter explanation. Earlier diagnostic runs retain their own artifact
hashes and are not promoted to current acceptance evidence. Final raw and ordinary runs are fresh
against the recorded source/assembly hashes; no main-revision transfer is claimed.

Maintained new fixture/case/control C# is 571 physical lines / 27,048 bytes, safe reference data
5,217 bytes, with zero product test hooks. This includes one-time assertion/journal/timer controls,
not 571 lines per future case. Standard raw discovery took 879ms; the three-case Host setup took
1,731ms and setup/run/cleanup 1,802ms. Its process working-set snapshot was 117,813,248 bytes.
The supporting two-database monitor proof advanced 20 virtual seconds in 298ms including lifecycle.
These are local representative costs and a process snapshot, not per-case allocations, memory peaks,
cross-platform performance or a completed bulk-scaling/maintenance proof.

## Remaining gates

P0 remains open and product API-complete is **false**; global variant count and compatibility percentage
are **unknown**. The broad corpus, full allocation/normalization, mechanical ledger/result/reuse
validator, structural/editability contracts and final independent aggregate/Security review are
unfinished. This cohort does not change the accepted D32–D40 gates.

| Cohort / gate | Exact missing prerequisite / owner |
|---|---|
| Observed mutation variants, denial and compatible/native effects | The narrow source-established create/readback case is bound; reference observation and additional variants still require an authorized disposable 2026.1.3 project/fixture, permitted actor and known denied-scope actor; #33/#35 and affected domain owners |
| Controlled Agent/lifecycle/fault composition | Disposable build + Agent and independent message/fault timeline and effects; #35/#42 plus Agent/Scheduling owners |
| Artifact/download bytes | Synthetic reference build/artifact and status/header/byte/hash plus unavailable/retention oracle; #33/#35 and Results/Artifacts |
| Target PG/Storage isolation and actual execution | Replacement resources and real Agent/workload; R0 and owning subsystem acceptance |
| Full representative cost/reset/scaling proof and delivered-compatible regression target | Broader verified representative cohort, repeatability/Agent/timer and maintenance results; #35/#42 |
| Mechanical scope/results/reuse controls | #36 implementation and #42 negative accounting/reuse tests |

Those cohorts are reference-blocked or unbound, not invented tests, skipped placeholders, executed
product failures or GREEN. This read-only acquisition authorizes no production TeamCity mutation.

The required root suite initially exposed #77: raw C# literals emitted checkout-dependent CRLF
canonical documents and commit messages, and the intentional CRLF-input fixture doubled CR bytes.
Four existing renderers and emitted commit messages now normalize to LF; legacy CRLF trailers retain
their provenance when read. An exact uniformly CRLF-encoded canonical document remains readable with
original bytes/hash/provenance; mixed/lone-CR and other noncanonical forms remain rejected. An actual
legacy committed-tree open/read/mutate/restart proof preserves its old revision and emits LF only for
new mutations. The affected fixture helpers now state the intended newline bytes.
Original repin/byte/provenance assertions remain, with a real legacy-commit regression. This follows
D29 and adds no global EOL policy, schema/authority change or architecture decision. The finding stays
open for P0's final review/main-delivery gate; local verification does not close the milestone.
