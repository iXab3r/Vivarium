# TeamCity REST compatibility

> Status: **Accepted**
> Implementation: **Planned**
> Maintainer roles: Vivarium REST, TeamCity, Git/Versioning, User Roles, Results/Artifacts, Logs, Test Steward
> Related architecture: [D24, D32-D36](../ARCHITECTURE.md)

## Goal and acceptance

D37 requires ongoing model plus wire conformance, not just a final client run. Highest-level
[business AC](../acceptance/README.md) remain implementation-independent; TCM/public-domain and
exact REST/side-effect tests support them under the [testing model](testing-model.md). Every affected
milestone proves its current base subset and relevant extension neutrality; later variants remain open.

Existing `@eyeauras/teamcity-cli` and other existing TeamCity API consumers must be able to manage
Vivarium through a compatible HTTP surface. Ordinary connection configuration may change the server
URL and provision a Vivarium credential. Rewriting the client, changing its TeamCity command vocabulary
or requiring a new Vivarium SDK is not the compatibility acceptance path.

Behavioral parity in the build engine and wire compatibility are separate gates. A native `/api/v1`
resource with similar fields does not satisfy a request addressed to `/app/rest`. Matching routes
alone also does not prove request/response variants, authorization, side effects or file transfers.

Freeze the observed TeamCity 2026.1.3 reference profile, installed CLI 0.5.2 and its exact source/test
revision. Inventory other consumers and their versions rather than assuming one client exhausts the
server API. The installed CLI README reports 449 unique REST method/path pairs, including reads and
mutations. That is a source-reported discovery baseline, not Vivarium coverage, a complete TeamCity
API count or proof that every payload variant was exercised live. Generate and reconcile the exact
route/variant census before implementing or estimating the facade.

D36 requires the [shared resource/state/command model](teamcity-rest-domain.md) and
[early operation breakdown](../../.workspace/workstreams/coherent-refactor/rest-breakdown.md) before
structural implementation. A reproducible initial census now extracts 456 synthetic fixture request
rows from same-version reference source b158fe03eab3c31982a21b36c278f690f6b541d9, plus a separate hash
of the installed runtime. It is neither full route reconciliation nor executed/live conformance;
npm metadata lacks release gitHead and inline tests/full Swagger/other clients remain inputs.
R0 delivers the first real compatible local run; R1–R4 grow matching endpoints with their domains.
R5 is cumulative conformance and agreed remaining operations, not the first facade implementation.

## One application model, two HTTP projections

```text
Workbench / viv-cli --> /api/v1 -----------+
                                         +--> shared application services
teamcity-cli / clients -> /app/rest ------+     authorization / Git / audit / execution / results
```

The TeamCity facade owns route/locator parsing, DTO/media-type translation and compatible status/error
responses. It does not own another scheduler, catalog, credential authority, result history or database
write path. Both APIs read and mutate the same Agent/build/test identities and current policy.

Native `/api/v1` remains the canonical Vivarium API with its own OpenAPI and SSE contracts. TeamCity
compatibility is an additional public management facade, not the old gRPC ControlPlane, and is not
limited to read-only endpoints. Its contracts and evidence remain separately versioned and tested.
No dependency on an available TeamCity instance is allowed after migration.

## Required protocol families

| ID | Required behavior | Acceptance |
|---|---|---|
| TC-WIR-01 | TeamCity route roots, context-path handling, REST version selection and discovery. | Unmodified clients connect using a base URL; support used `/app/rest` and documented version aliases without requiring client URL rewriting. |
| TC-WIR-02 | Method/path/query/body compatibility for reads and mutations across inventoried resource families. | Existing CLI commands for projects, jobs, parameters, steps, VCS, requirements, dependencies, Agents, queue, builds, tests/triage and files operate against Vivarium. |
| TC-WIR-03 | TeamCity locators, nesting, quoting/escaping, `$base64`, branch selection and supported lookup semantics. | Non-simple IDs and branch names containing separators select the same objects as the reference; invalid locators fail visibly. |
| TC-WIR-04 | `fields` projection and per-resource default DTO shape, collection names, counts, optional/null fields and scalar representations. | Nested projections decode with existing client models; large test IDs remain exact strings and numeric text statistics remain precise. |
| TC-WIR-05 | JSON, XML and plain-text negotiation, including endpoint-specific formats and request encodings. | Existing XML-only and text endpoints work alongside JSON; request Content-Type/Accept is honored rather than returning native DTOs unconditionally. |
| TC-WIR-06 | TeamCity paging and continuation semantics. | Preserve `start`, `count`, relevant `lookupLimit`, count meaning and usable `nextHref`/`prevHref`; do not expose native opaque cursors where a legacy client expects numeric continuation. |
| TC-WIR-07 | Compatible status codes, validation/not-found/conflict/permission errors and mutation response shapes. | Clients distinguish error, empty collection and successful action; an unsupported or rejected operation never returns a synthetic success. |
| TC-WIR-08 | Supported credential transport, current user/project authorization and protected-value handling. | Existing client authentication works with provisioned Vivarium credentials; secret reads stay masked and native/facade access decisions agree. |
| TC-WIR-09 | Artifact/source/log paths, directory metadata, archives and streamed download response contracts. | Follow returned links and obtain actual bytes with the expected media/disposition semantics, not a login page or a JSON wrapper. |
| TC-WIR-10 | Compatible mutation semantics, full replacement versus patch, inheritance and immediately observable outcomes. | Updating a job/parameter/requirement affects the same effective resource through both APIs after validated activation; omitted fields behave as specified. |
| TC-WIR-11 | Stable identity/provenance and links across native, imported and TeamCity-shaped resources. | Native and facade reads resolve to one build; diff-import preserves source mappings and links never accidentally send credentials to the old server. |
| TC-WIR-12 | Executable conformance using unchanged clients and isolated mutation fixtures. | Record method/path plus request/response variants and run real CLI lifecycle flows against Vivarium, including negative cases and downloaded-byte checks. |

The stable IDs above also appear in the [behavioral parity catalog](teamcity-parity.md). That catalog
and the workstream ledger carry acceptance; no API family is silently dropped when export is awkward.

## Requests and response details to freeze

The actual CLI already constructs nested `id:` locators, explicit branch conditions with `$base64`,
`start`/`count` dimensions, and parses top-level continuation dimensions from `nextHref`. Preserve the
meaning of a valid empty page with a deeper lookup continuation; do not terminate a client scan early
merely because the page has no returned items. Field projection must retain the requested nested
collection wrapper, scalar versus object/array representation and safe optional fields.

Inventory at least list/detail/field reads, filters/sorts, create/update/delete, configuration collection
replacement, build queue/cancel, file metadata/content/archive, test/problem occurrences, triage,
statistics and administrative routes exposed by the actual clients. Distinguish a method/path pair
from its locator dimensions, media types, payload schemas and important boundary/error variants.

Version discovery must distinguish the actual Vivarium product from its advertised compatibility
profile. Do not claim to run JetBrains software, copy its installation identity or synthesize successful
license/cluster/admin operations. Source-specific server files, Kotlin settings, licensing, JVM paths
and similar internals need an explicit semantic mapping or recorded incompatibility decision. They
remain open compatibility items; an implementation cannot call the entire client surface compatible
while silently excluding commands that are still used.

The installed CLI's occasional HTTP 400 responses are diagnostic findings, not behavior to clone
blindly. Validate a request variant against the reference/API contract before making an unexpected
failure part of the supported profile.

## Mutations, Git and security

TeamCity-shaped settings writes translate to the same validated Git mutation/activation services as
native edits. The facade must satisfy the response semantics an existing client expects without
returning success for a still-rejected or unapplied change. Decide synchronous completion or a truly
compatible operation response per source endpoint; silently changing a 200/201 resource response to a
native 202 operation breaks clients. C# is still the definition authority; accepting a legacy property
does not authorize a hidden DB setting or a second Kotlin execution engine.

Runtime actions use the existing durable execution/lease/audit path. Preserve exact replacement and
omission semantics, current authorization, inherited versus owned settings and secrets as protected
references. Existing clients cannot be required to add Vivarium-only actor/idempotency headers: derive
the actor from their authenticated identity and create internal correlation/operation context.
Retain compatible request identities when the source client supplies them. Do not deduplicate two
intentional identical run requests using a body hash or promise exactly-once external publication from
an ordinary legacy HTTP retry. Failed/ambiguous external effects remain observable and reconciled.

Changing the configured server and credential is normal client setup; silently forwarding an old
TeamCity token is not. Preserve any authentication forms actually used by inventoried consumers through
the host's credential policy. Serve the management origin with ordinary certificate trust supported by
the existing HTTP clients; do not require their code to implement Agent-specific certificate pinning
or disable TLS verification. A compatibility credential never bypasses project/fleet scopes, secret
masking, Git validation or audit. No live TeamCity mutation is needed for conformance development;
use isolated disposable projects/feeds/Agents and never send recording-test mutations to production.

## Files, IDs and historical import

Build numbers/names, stable configuration IDs, source numeric IDs and native run IDs are different
identities. A mapping must not collide when native Vivarium and imported TeamCity builds coexist.
Retain string IDs where precision requires them and make returned hrefs/web URLs valid on the chosen
Vivarium origin/context. Imported source URLs are historical metadata, not automatic redirect targets.

Bulk bytes remain streamed and bounded in memory. Preserve compatible file not-found, unsafe-path,
archive and interruption behavior, range/content headers where used, and build/project access checks.
Compatibility path syntax does not grant arbitrary filesystem reads. Historical log/test/artifact
reads use the imported selected snapshot under the same keep-target/use-source diff-import policy;
they must work with TeamCity unavailable.

## Conformance and delivery

1. Generate a method/path/variant inventory from the pinned CLI source/tests, the reference API
   metadata and other consumer calls. Keep generated census separate from human dispositions.
2. Define golden semantic request/response fixtures. Normalize only true run-dependent IDs, origins
   and times; do not normalize away missing fields, incorrect statuses or changed side effects.
3. Implement native domain behavior and facade mappings together. Existing GET/read/report/log
   workflows are an early slice; configuration/runtime mutations are also required before full parity.
4. Run the unchanged installed CLI with a separate Vivarium profile: authenticate/discover, list and
   change isolated configuration, inspect compatibility, queue/watch/cancel a build, view tests/triage,
   read statistics and retrieve artifacts/log bytes through supported paths.
5. Compare equivalent operations through native and compatible APIs, including current authorization,
   Git activation, mutation omission semantics, paging, escaping, XML/text, errors and restart/retry.
6. Validate other actual consumers individually. Close every used method/path/variant or obtain an
   explicit owner disposition; a count of registered routes is not a compatible-client result.

Keep native and facade coverage separate. This task records the requirement; no `/app/rest` facade,
client conformance result or implemented coverage is claimed yet.

## Primary sources

- Installed `@eyeauras/teamcity-cli` 0.5.2: README, `server-url.js`, `locator.js`, `paging.js`, command/
  model modules and the integration's source tests. Exact source revision belongs to the workstream.
- [TeamCity REST overview](https://www.jetbrains.com/help/teamcity/rest/teamcity-rest-api-documentation.html):
  authentication, version roots, media types and field projection.
- [Locators](https://www.jetbrains.com/help/teamcity/rest/locators.html) and
  [build reads](https://www.jetbrains.com/help/teamcity/rest/get-build-details.html): selection and
  continuation semantics. Current docs may be newer than the frozen 2026.1.3 live reference.
