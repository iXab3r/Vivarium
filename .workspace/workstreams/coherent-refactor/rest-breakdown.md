# Early REST breakdown — P0 input, not implemented compatibility

Captured 2026-10-31. Owners: Reconciliation, REST/TeamCity, Scheduling, Git/Persistence, Identity,
Results/Logs and Test Steward. D36 makes this work precede structural implementation.

## Evidence actually obtained

- Installed @eyeauras/teamcity-cli 0.5.2; client.js SHA-256
  83a06b5d92ad2409bbdaac6f0df45c9765a0ae42f5256d05c56072d886f4cf07.
- Local EyeAuras.CliFactory checkout is 0.4.1 on an unrelated branch, so it is not the reference.
  An isolated read-only source copy is pinned to b158fe03eab3c31982a21b36c278f690f6b541d9, whose
  integration package is 0.5.2. npm metadata provides no gitHead; version agreement alone is not a
  published-binary provenance proof. Runtime JS was inspected separately and its hash is preserved.
- inventory-rest.mjs evaluates ten inspected constant synthetic fixture tables with Node's TS
  stripping. It does not execute CLI commands, tests or HTTP requests. Result: 456 discovered request
  rows, comprising 432 principal requests and 24 preflights. rest-fixture-inventory.tsv retains source
  table/index, concrete example path/method, command, query/fields/body shape hints and candidate owner.
- 436 unique concrete method/example paths are not normalized API templates and must not be compared
  to the README's 449 method/path claim. Inline handlers, other consumers and full source-server
  OpenAPI remain reconciliation inputs. No implemented Vivarium facade coverage is claimed.
- Source fixtures are synthetic contract examples, not captured server results. The existing private
  TeamCity captures and role definitions are a distinct observed-evidence class.

Reproduce from the workspace root with Node 24.4.1 and the frozen source copy:

```text
node .workspace/workstreams/coherent-refactor/inventory-rest.mjs <pinned-cli-source-directory>
```

The full receipt is private under artifacts/depot-refactor-study/rest-fixture-receipt.json;
rest-census.json records safe provenance and limitations. Do not run a source-copy test suite or
silently refresh the pin when regenerating. This is analysis tooling, not a new product/test runner.

## Resource families and structural implications

Subsequent [business acceptance review](../../../docs/acceptance/rest-gap-review.md) inspects all 30
API pages in the public documentation sitemap. teamcity-official-rest-operations.tsv records 449
documented operation headings, rest-resource-acceptance-review.tsv routes every resource, and
rest-business-gaps.tsv contains 36 open findings. Counts are not normalized routes or coverage.
See rest-business-review-census.json for source hashes/version limits. This supplements the pinned
CLI fixtures; full installed Swagger/schema/variant reconciliation and reference effects remain open.

| Family / representative routes | Shared concepts required | Authority / earliest useful slice |
|---|---|---|
| Root, apiVersion, info/version, server | Compatibility profile distinct from actual product identity, discovery, context roots, endpoint media | Chiral/runtime observations; R0 thin profile |
| projects, project hierarchy/parameters/features | Stable Project/root IDs, parent relations, project visibility, versioned owned/effective settings | Git desired + PG projection; seeded R0, full R2 |
| buildTypes, templates, steps, parameters, requirements, features/triggers | BuildType/template identity, ordered collections, stable child IDs, typed property bags, inheritance/override/reset | Git/C# activation + PG projection; seed R0, editable R2, runner/trigger semantics R3 |
| vcs-roots, vcs-root-instances, entries/rules, changes | Desired root vs resolved instance, bindings, exact revisions/branches/checkout rules and change metadata | Git desired, PG observations; R2 |
| buildQueue, order/pausedState, approval/cancel | Build identity across queue/run/history, position/reason/admission, explicit state transitions and replay semantics | PG scheduler/commands; thin R0, full R2/R3 |
| builds, fields, status, tags/pins/comments, resulting parameters | Immutable run plan/revisions/Agent facts, state vs outcome, durable history and current authorization | PG run/results; thin R0, full R2/R4 |
| snapshot/artifact dependencies and build relations | Config edges vs actual selected run edges, DAG/reuse/affinity, exact artifact inputs and composite membership | Git definitions + PG execution; R3 |
| agents, agentTypes, authorized/enabled/run policy | Stable machine ID vs type/session, independent axes, eligibility reasons, current work and fact provenance | PG runtime + versioned desired fleet settings; R0/R2 |
| agentPools/project associations/authorization tokens | Pool identity/capacity, project access intersection and enrollment proof, not self-reported membership | Git desired + PG enrollment/observations; R2/R3 |
| users/current, userGroups, roles/includes/permissions, tokens/sessions/avatars | Framework identity plus TeamCity scoped role/group graph, exact public IDs and mutable definitions | PG Identity/OpenIddict/RBAC; technical account R0, full R1 |
| tests/testOccurrences, problems/problemOccurrences, investigations, mutes | Stable definition vs occurrence identity, parameterized tests, current/historical triage scope, exact long IDs | PG results/triage; R4 |
| statistics, resulting properties, logs/artifacts/sources/file trees/archives | Precise statistic text, metadata/reference model, bounded bytes, path authorization and native links | PG metadata + Storage bytes; artifact/raw output R0, full R4 |
| audit/health/deployment dashboards/history | Attributed actions, health observations and deployment concepts with real semantics | PG/domain projections; R1/R3/R4, disposition remaining in P0 |
| cloud/server backup/cleanup/auth modules/licenses/plugins/server files | Provider/admin effects, sensitive reads, native-resource mappings and real incompatibility risks | Early P0 disposition; accepted operations assigned R3–R6; no fabricated success |

This allocation is preliminary discovery, not permission to defer all compatible endpoints to R5.
Each family gets wire DTO/command tests with its domain implementation. Storage/identity authority is
per operation/field, not inferred just from its URL prefix or HTTP method.

## Decisions that REST forces before R0

1. **Canonical run identity.** Current native submission returns a matrix/child structure; TeamCity
   enqueue returns a Build with a numeric safe-integer id that the CLI immediately polls. Specify the
   one-cell and composite mapping, queue/history ID continuity and imported/native ID allocation.
   Do not serialize every ID as text: large test IDs and queued Build.id have different requirements.
2. **Durable catalogue.** Existing project/configuration strings cannot answer template/parent/move/
   inheritance APIs. Define Project, BuildType, template and child identities/projections first.
3. **Immutable run provenance.** Definition revision, selected source revision, runtime parameters,
   selected upstream runs and Agent snapshot are independent and immutable once bound to execution.
4. **State/outcome split.** Queued/running/finished and success/failure/cancellation/infrastructure or
   test detail must project independently, without turning missing reports into successful tests.
5. **C# mutation model.** CLI property edits, reset inherited value, replace-all, reorder, copy/move
   require writable source representation. Freeze managed/user-owned files, precedence/computed values
   and a concrete edit/read-after-write example before the evaluator/catalog is built.
6. **Authority and success boundary.** Git settings, live PG rights/execution and Chiral resource
   settings use different owning services. Define commit/activation/failure observation and 200/201
   behavior; no hidden DB overrides or blanket replacement with 202 native operations.
7. **Query/projection protocol.** Locators are nested grammar; fields have per-resource defaults;
   paging may have an empty page plus nextHref. IDs/hrefs/context roots and media types are contractual.
8. **Current scope.** Map all discovered methods/variants and non-REST download paths to an owner and
   milestone. JVM/Kotlin/license/provider-only surfaces need explicit mappings or an open blocker;
   an unimplemented endpoint cannot return a dummy object to make the CLI green.

See the [durable contract](../../../docs/design/teamcity-rest-domain.md). These decisions require
reviewable schemas/examples and state/command tables, not merely a resource class list.

## Wire contract work packages

| Key | Concrete P0 deliverable | Required review evidence |
|---|---|---|
| REST-CENSUS | Reconcile pinned runtime, source fixtures/inline handlers, reference Swagger and other consumers; include external download/context paths | Reproducible inputs, complete/unknown classification, route template versus example/variant distinction |
| REST-DOMAIN | Resource/identity/relationship schema and state/command tables, native matrix mapping and import lineage | Enqueue/poll/cancel/history plus composite and inherited-settings worked examples |
| REST-PROTOCOL | Locators/fields/media/paging/hrefs/status/error/auth profile | Nested/base64 branch, escaped paths, empty continuation page, long test ID and safe-integer Build.id fixtures |
| REST-MUTATIONS | Per-operation authority, body omission/replacement/reset/order, Git/C# edit representation and activation/retry boundary | CLI create/change/read/reset/reorder example produces the expected revision/effective response; PG rights change visible with unchanged token |
| REST-HARNESS | First executable P0 slice: independent corpus, strict WireMock.Net/client replay controls and actual Vivarium RED baseline (P0.REST.1–3) | Real requests/effects, source provenance, control GREEN versus product RED/blockers; no guessed oracle or skipped placeholders; prior GREEN cannot regress |
| REST-ALLOCATION | Operation/variant ledger by milestone, exact R0 smoke subset and explicit unsupported/admin/provider blockers | No orphan discovered operation; critical model choices resolved before R0; detailed variants proved before their owning slice |

P0 entry does not mean all 449 claims or whole TeamCity API are frozen. The current generator's
discovered rows stay unverified. P0 exit requires reconciliation/disposition and the structural
contracts plus the [executable D38 gate](../../../docs/design/teamcity-rest-test-first.md); a design-only
REST-HARNESS does not satisfy it. Ordinary, replay-control, delivered-case and full raw RED targets
remain distinguishable in required verification. Exact commands/results are the harness deliverable.

R0 does not wait for downloading the full retained artifact archive. The current
CLI has no dedicated Swagger export command; obtain metadata through an approved supported export
path as a named follow-up, rather than silently substituting newer public docs or raw hidden requests.

## Initial compatible R0 execution profile

Freeze exact selectors, defaults/statuses/media from the pinned client before implementation:

- Authenticate/current user and server/profile discovery under normal locally trusted HTTPS.
- List/show the real enrolled Agent and its authorization/enablement state.
- List/show a seeded Project/BuildType LocalSmoke using the final approved catalogue representation.
- POST buildQueue with buildType/branch/parameter shape used by runJob; return a real compatible
  Build that unchanged CLI can parse. Allocation, polling and cancellation use the same underlying run.
- Read queue/build state through finish; cancel a deliberately long queued/running smoke job truthfully.
- List/download the produced artifact through returned compatible paths and verify its bytes/hash.
- Native UI/API show the same Agent/run/result. Retained raw output is available; full indexed
  searchable log/test-history UX remains R4, not a false R0 completeness claim.

R0 is a declared subset with real effects, not a full compatible server and not a fake API demo.
R1/R2/R3/R4 grow this profile as their domain behavior lands; R5 is cumulative conformance.

## Explicit remaining gaps

Full source Swagger/client-union normalization and release-to-source provenance are not closed.
Other clients, non-REST log downloads, plugin IDs, server internals, token/user lifecycle variants,
C# editability and matrix mapping require the named P0 work. No current source configuration is
migration-accepted, and no /app/rest route has been implemented by this analysis.
