# Local reference mutation cohort

> Status: **Twelve observed reference workflows; five ready product cases remain RED**
> Lead: Test Steward; co-held TeamCity, Vivarium REST, User Roles and Testing Architect
> Tasks: [#33](https://github.com/iXab3r/Vivarium/issues/33), [#35](https://github.com/iXab3r/Vivarium/issues/35), [#42](https://github.com/iXab3r/Vivarium/issues/42)

The later [current-grant cohort](../../tests/Vivarium.Tests/TeamCity/Reference/current-grant-observations.json)
adds three separately identified cases to the live [contract task #111](https://github.com/iXab3r/Vivarium/issues/111).
One ordinary user's unchanged scoped PAT receives 403 before a direct project grant, 200 after
the grant, and 403 after revocation. Readbacks retain the accepted value; a protected second
project has denied ordinary-user and successful administrator controls. Credential setup entities
are excluded, while all fifteen policy exchanges retain literal request/response bytes and hashes.
These cases remain product-prerequisite-blocked pending real compatible objects, credentials and
current-role services. They do not establish target OIDC behavior or global permission coverage.
The acquisition patch is [retained separately](../../tools/teamcity-reference/cli-current-grant-capture.patch).

Replay matches the transport's original request target and Host. WireMock.Net 2.15 rebuilds its URL
from a decoded ASP.NET path, so matching that URL would lose the captured `%3A` in role scopes.
The transport middleware overwrites the internal raw-target header before matching; an encoded-path
control also rejects a caller's attempt to spoof it. This changes no reference bytes or product route.
Counts and execution results in the original checkpoint below remain historical; GitHub holds live status.

This bounded D38 increment asks whether automation can create a project and build configuration,
change their descriptions, and observe the actual stored values; rejected changes must leave the
objects unchanged. Permission cases additionally require a known restricted actor and a successful
read of the same object before a denied write. These refine AC-AUTO-01, TCM-08 and the existing
TeamCity wire contract without implementing a compatible facade or another configuration authority.

The independently observed oracle comes only from the disposable TeamCity 2026.1.3 / build 222742
instance at loopback port 18111, explicit profile `vivarium-teamcity-lab-202613`. The owner authorizes
synthetic projects, configurations, users, roles and dedicated new test Agents on this separate local
instance. Existing servers and existing fleet Agents remain excluded. The acquisition profile and
process-local credential store must be verified before each mutation session. Credentials and token
values do not enter committed captures.
After final acquisition, typed commands revoked the owned view PAT, removed its local alias and
deleted the owned project/configuration. Subsequent project/configuration reads both returned 404;
the original laboratory administrator credential remains. Local gates are restored to ReadOnly on,
Update/Admin/Credentials off. The container remains running; cleanup took 12.598 seconds. No build
or Agent action occurred. The ignored `artifacts/p0-rest-second/cleanup-receipt.json` records this
cleanup separately from the immutable reference captures.

The [literal captures](../../tests/Vivarium.Tests/TeamCity/Reference/local-mutations.json) retain
original response entity bytes, lengths and SHA-256, exact request body/media, actor labels and
source/profile/version metadata. Acquisition IDs beginning `TC-` map explicitly to the following
conformance IDs by replacing that prefix with `TCREST-`. All twelve workflows were actually observed;
their [case register](../../.workspace/workstreams/coherent-refactor/rest-local-mutation-cases.tsv)
separates replay bindings from product prerequisites.

| Case suffix after `TCREST-` | Actual reference sequence | Product binding |
|---|---|---|
| `PROJECT-CREATE-READBACK-002` | POST 200; GET 200, initial description and root parent | Ready; actual product POST 404 |
| `PROJECT-EDIT-READBACK-001` | PUT 200 text; GET 200 updated description | Blocked: existing compatible project/state |
| `PROJECT-DUPLICATE-ID-001` | POST 400 JSON; GET 200 unchanged | Blocked: existing compatible project/state |
| `PROJECT-MALFORMED-ID-001` | POST **500** JSON; root GET 200; absent-project GET 404 JSON | Ready; actual product POST 404 |
| `BUILDTYPE-CREATE-READBACK-001` | POST 200; GET 200, initial description and supplied project | Blocked: existing compatible project |
| `BUILDTYPE-EDIT-READBACK-001` | PUT 200 text; GET 200 updated description | Blocked: existing compatible configuration/state |
| `BUILDTYPE-DUPLICATE-ID-001` | POST 400 JSON; GET 200 unchanged | Blocked: existing compatible configuration/state |
| `BUILDTYPE-MALFORMED-ID-001` | POST **500** JSON; absent-configuration GET 404 JSON | Blocked: existing project and positive configuration-family control |
| `PERMISSION-VIEW-PROJECT-GRANTED-001` | Restricted project GET 200; configuration GET 200 | Blocked: real compatible scoped PAT/objects |
| `PERMISSION-EDIT-PROJECT-DENIED-001` | Restricted GET 200; PUT 403; admin GET 200 unchanged | Blocked: real compatible scoped PAT/objects |
| `PERMISSION-EDIT-BUILDTYPE-DENIED-001` | Restricted GET 200; PUT 403; admin GET 200 unchanged | Blocked: real compatible scoped PAT/objects |
| `PERMISSION-CREATE-PROJECT-DENIED-001` | Restricted known-project GET 200; POST 403; admin absence GET 404 | Blocked: real compatible scoped PAT/objects |

The malformed identifiers contain a hyphen. Their observed 500 is the pinned reference behavior;
it is not replaced with a guessed validation 400. Exact captured error JSON, including error-message
strings, is compared. The creation variant sends explicit `parentProject.id: _Root`, description,
and a POST `fields` query. TeamCity's project POST still returns its full object despite that query;
the test compares that captured response, then the selected GET fields. This remains distinct from
the original schema-backed `parentProject.locator`/no-POST-fields case and does not retroactively
make that original request an observed reference mutation.

The restricted credential belongs to the synthetic administrator but is a separately issued PAT
with only project view access. Raw token-metadata readback records exactly one restriction:
permission `view_project`, the owned project ID, and `isGlobalScope: false`; the typed creation input
used `VIEW_PROJECT`. Preserve that observed casing difference. The positive reads and denied writes
use that PAT; final unchanged/absent reads use the administrator credential. Token values are omitted.

Finite recorded HTTP transcripts run through the existing NUnit/Test SDK and strict WireMock.Net
boundary. Each mapping matches method, full path/query, Accept, credential label, content type and
body, with bounded journal and ordered scenario states. Literal recorded responses have no catalog,
role engine, computed defaults or policy. The same requests/assertions target the healthy production
Kestrel fixture where real prerequisites can be established. A prerequisite failure does not earn
another independent mutation/permission defect or compatibility pass.

Current Vivarium has no TeamCity project/configuration model or project-scoped PAT authority. The
native legacy admin token cannot represent a restricted TeamCity actor. Permission transcripts may
therefore be replay-ready while product execution remains explicitly prerequisite-blocked. No
synthetic authentication handler, database rewrite or administrator-as-viewer substitution is allowed.

The product-ready raw selection is the original three cases plus the two ready workflows above,
not fifteen executable product variants. The other ten new workflows are replay-executable and
product-prerequisite-blocked. They have no ignored/placeholder product tests. The missing compatible
catalog/routes remain the shared diagnosed cause of the five raw failures; five cases are not five
independent defects. A future negative GREEN must reach every positive control and exact unchanged/
absent readback, and dependent workflows need real production fixture/actor bindings before admission.

All JSON responses compare exact fields, values, types and array order; non-JSON entities compare
exact bytes. The only environment mapping replaces the lab origin in each public `webUrl` field,
preserving its path/query. No IDs, errors, fields or arrays are normalized. The capture's fixed
query objects have no repeated parameters; duplicate-query/raw-query-order variants remain unknown.
Every exchange must match its strict replay mapping. Scenario reset makes a late read unmatched,
including a recorded 404; a default unmatched 404 cannot count as observed reference behavior.
Additional controls reject successful creation followed by an incorrect readback, bind twelve unique
workflow IDs and exactly two ready IDs, and verify the raw actor-restriction entity hash/values.

Keep the original first-cohort records immutable. Record the new acquisition/tool/source inputs and
new candidate/result hashes separately. Focused replay and raw checks precede a single required
solution build/test checkpoint. The [offline ledger foundation](api-ledger-accounting.md) now checks
partial case/task/result/reuse accounting; full future API denominator/allocation/due freeze,
P0 composition/Agent/PG gates and final independent milestone review remain incomplete. Exact commands
and current results are recorded in the [new receipt](../../.workspace/workstreams/coherent-refactor/rest-local-mutation-results.json).

Final Windows / SDK 10.0.303 checkpoint: root build passed (zero errors); root ordinary tests passed,
374 Passed with twelve existing NotExecuted result rows in TRX (console eleven Skipped), zero Failed.
The focused replay/composition selection passed all 31 controls, zero skips. The full five-case ready
raw target returned exit 1 with five genuine request mismatches and zero harness errors/skips:
four expected 200/observed 404, one observed-reference 500/actual-product 404. Follow-up observations
are not credited after an initial failed mutation. The ten blocked reference workflows remain outside
that executable product count.

The two new maintained C# files contain 252 physical lines / 12,948 bytes; the raw binding adds six
lines, with zero product hooks. The safe fixture is 40,663 bytes. Final raw Host setup took 1,495ms,
and setup/run/cleanup 1,567ms, with a 118,013,952-byte process working-set snapshot. The focused
two-database timer control advanced twenty virtual seconds in 256ms. Root ordinary execution took
5m16s. These are local observations; no memory-peak, per-case allocation, cross-platform or completed
bulk/PG/Agent composition claim follows. Diagnostic harness failures remain in their own artifacts;
the final controls and ordinary/raw records use separately hashed current source/assembly inputs.
