# Handover

- User requested checkpoint, a Depot-informed milestone/issue plan, then iteration before implementation.
- Frozen/pushed checkpoint: `cbb285a55701e0be0c726c57d9daccd8bfb529e7`, annotated tag
  `checkpoint/pre-refactor-2026-10-31`; runtime remains `09dd40b`.
- Read `plan.md`, `decisions.md`, `depot-study.md`, `scenarios.md`, `ledger.md` and `issues.json`.
  Plan v1.8 remains draft for unresolved choices; RF-10 delivery and D34–D40 directions are accepted.
  No implementation milestone is complete.
- Current owner direction (2026-11-01): mutation/reference-effect tests use a separate local TeamCity
  test server pinned to 2026.1.3, never a temporary project on the owner's working server. Select
  its dedicated CLI profile explicitly and verify target URL plus observed version/build before
  writes; do not use the production/default profile. Container `vivarium-p0-rest-teamcity-2026-1-3`
  is running on loopback `http://127.0.0.1:18111`; normal UI confirmed 2026.1.3 / build 222742 and
  configured a new data directory, internal HSQL evaluation database, license and private
  administrator/token bootstrap. Dedicated CLI profile `vivarium-teamcity-lab-202613` is
  authenticated: actual CLI confirmed 2026.1.3 / build 222742, API 2026.1, REST 222742 and canonical
  `webUrl=http://127.0.0.1:18111` after its normal Global Settings UI correction. Process-local
  APPDATA/LOCALAPPDATA stay under ignored `artifacts/p0-rest-first/teamcity-lab/cli-state`;
  no production/default profile read. Server/account/profile authentication is ready; local CLI
  gates are restored to ReadOnly on and Update/Admin/Credentials off after twelve actual bounded
  mutation/permission reference workflows. The owner authorizes synthetic projects/configurations,
  users/roles and dedicated new test Agents on this separate local playground without per-fixture
  approval; production and existing fleet Agents remain excluded. The owned restricted PAT/local
  alias and project/configuration were removed, with typed absence readbacks and a cleanup receipt.
  Data/log/temp bind mounts and setup/container receipts stay under ignored
  `artifacts/p0-rest-first/teamcity-lab/`. CLI inventory confirmed zero Agents; existing fleet Agent
  connections/execution need separate authorization.
  Preserve historical read-only captures/receipts with their original target identity; they are not
  evidence that the new test instance is ready. Current fixture
  contracts and current evidence are synchronized in #33/#35/#42 and tools/teamcity-reference/README.md.
- GitHub: #2–#9 remain milestone closure parents. D40 reuses existing subsystem code tasks, moves
  REST contract/acceptance tasks under their same-milestone subsystem owner and adds #56–#76:
  eight milestones, 75 open issues and 67 native child links in the historical planning snapshot.
  Same-P0 debt #77 was later added under #35; GitHub owns current task state. The planning graph is
  in issues.json and subsystem-slices.tsv; do not restore the older flat REST execution chain.
- Owner accepted RF-10 on 2026-10-31: each milestone may merge separately into main after its own
  acceptance/review, and its issues close after that delivery. Use milestone integration branches where
  useful; remove obsolete code with its replacement. R6 is cumulative acceptance, not the first main merge.
- Existing accepted D32/D33, 120 TC requirements and all 97 configurations remain in scope; 0/97
  migration accepted. Full retained history/log/artifact import and explicit one-way diff-import
  remain mandatory. No source production mutation/deletion or cutover occurred.
- Census is checkpoint-based, not a live size target: 48,402 product C# lines, 21,629 test C# lines.
  `python .workspace/workstreams/coherent-refactor/census.py` regenerates against current tracked HEAD;
  record a new census revision when comparing later, do not relabel it as the original baseline.
- Depot main `2023838` equals fetched origin/main. Chiral main is not Depot's package API; pinned
  versions/source gitlink are in `depot-study.md`. No Depot/Chiral files were changed.
- The exact pinned SDK 10.0.303 is now verified in ignored private tooling. The first executable
  [REST cohort](../../../docs/design/teamcity-rest-first-cohort.md) records real build/control/raw
  evidence; the SDK pin is unchanged. It does not deliver the replacement stack or close P0.
- D34 accepts Depot Identity/OpenIddict/OIDC/installation account plus TeamCity roles/permissions,
  editable definitions/includes/groups/project scopes 1:1, as PG data. Legacy Git-RBAC and custom
  claim/recovery/password paths are replacement targets, not ongoing authorities.
- D35 accepts Chiral resource settings/readiness, PG structured state, Git versioned settings and
  Storage bytes, plus Depot's actual TypeScript/Vite Workbench frontend (no React dependency).
- UI clarification: share technology/Host/Identity/schema integration, preserve useful current
  Vivarium/TeamCity composition and workflows; no forced Depot split-view redesign.
- D36 makes deep REST contracts precede structural implementation. Read rest-breakdown.md,
  rest-census.json, rest-fixture-inventory.tsv and local-lab.md. R0.1 is a real Host+Agent lab,
  R0.2 the unchanged-CLI smoke, R0.3 restart/cleanup. R2 remains full source-based C# Compile.
- REST discovery: reference repo b158fe03eab3c31982a21b36c278f690f6b541d9 is version 0.5.2;
  source-owner checkout is older 0.4.1 and was not changed. Separate source copy is ignored/private.
  Generator extracted 456 synthetic fixture rows; not normalized route count or executed conformance.
  npm metadata lacks gitHead; full Swagger/inline/other-client union and binary provenance are open.
- D37 requires a continuous TeamCity semantic superset, with highest-level business-language AC.
  Read docs/acceptance/*.feature, docs/design/testing-model.md and the Business Acceptance/Testing
  Architect roles. All 110 AC are specified-unbound, not parsed/executed passes. S01–S38 are
  supporting technical candidates; acceptance-map.tsv and requirement-routing.tsv are initial
  family-level traceability, not coverage. Reuse exact feature files in future bindings.
- Fresh PoeBane source 2984e1f (Testing Architect change 48a4735, 2026-10-27) was read remotely;
  its older dirty source-owner checkout was untouched. poebane-testing-study.md records transferred
  evidence/selection/oracle principles; no Rust-specific budgets/permissions/new runner imported.
- D38/#35 is the first executable P0 slice: independent corpus, verified replay/composition, actual
  current-Vivarium RED baseline before replacement. P0.REST.2 first proves cheap production-code
  composition, controlled timers/external actors, isolation and measured code/data/runtime cost.
  WireMock is an HTTP adapter, not a model simulator. Read teamcity-rest-test-first.md and
  rest-conformance-checkpoints.tsv. Two installed-reference reads now have strict replay controls
  and actual current-product RED (404 versus 200), with native HTTP/controlled-timer/isolation proof.
  A third source-established project create/readback case now reaches real POST RED; its expected
  semantics come from installed metadata and documented explicit inputs, not a live mutation capture.
  Required checks exposed canonical Git byte/provenance defects tracked in same-P0 #77.
  The later [local mutation cohort](../../../docs/design/teamcity-rest-local-mutations.md) now records
  twelve actual project/configuration/permission workflows with readbacks and exact captured error
  JSON (including observed malformed-ID 500). Total known materialized cases: fifteen, fourteen
  captured and one source-established. Five ready product cases execute genuine RED; ten dependent
  catalog/scoped-PAT variants remain product-prerequisite-blocked. Current receipt is
  `rest-local-mutation-results.json`; the original receipt remains historical. Root build passed,
  root ordinary tests passed (374 Passed/twelve existing NotExecuted rows), and focused controls
  passed 31/31. `api-status.json` distinguishes seven headings with any bound reference/replay case,
  three with ready product cases, and 442 with no binding; full variant allocation/denominator stay unknown.
  The broader checkpoints remain incomplete; no production TeamCity write/farm operation occurred.
  Inspection: current VivariumControllerHost.StartAsync directly constructs storage/TLS/services and
  wires Kestrel/background workers; no TestServer composition seam was found there. BuildScheduler
  has DispatchAvailableAsync, queue timeout monitor has SweepOnceAsync and a TimeProvider-backed
  PeriodicTimer. Its test's GetUtcNow-only clock does not virtualize timer scheduling. Depot's
  AccessPolicyTests/DepotFixture separate real-PG policy inputs from real Host/OIDC/process claims.
- TCM-GAP-001/#41: disable blocks ordinary automatic queue work, not the documented explicitly
  targeted suitable-agent diagnostic/custom run. TC-AG-01 and AC-AG-02/06 clarified; reference
  2026.1.3 variant and actual replacement behavior still open. No source TeamCity write performed.
- Depot UI reference: apps/depot-mock is the production frontend at the frozen commit; main.ts,
  session.ts, management-api.ts, nodes-api.ts/node-settings.ts and Access list/inspector were inspected.
- Read-only source role census captured seven definitions and their permissions/includes, including
  RESTRICTEDBOT and TOOLS_INTEGRATION, under ignored artifacts/depot-refactor-study/teamcity-role-*.json.
- Status review: corrected stale 31-AC entries in scope/ledger to canonical 110; no new AC or runtime
  evidence from that historical review. All 36 reference decomposition findings remain open; the
  current executable RED cohort is recorded above, while the compatible facade remains absent.
- D39 (2026-11-01): 14 task issues #42–#55 added; all 54 issues/8 milestones synchronized and native
  links verified. api-blocks.tsv is the task graph, api-ledger.tsv seeds 449 source headings, and
  api-status.json is an explicitly unverified seed snapshot. #36 delivers mechanical accounting;
  #42 verifies baseline/ledger. Formal reviews only at milestone end, including explicit independent
  Security/Adversarial; resolve all milestone debts before next implementation. No current review,
  debt clearance or API-complete claim. Current bounded test evidence is recorded above; GitHub
  owns task state and the result ledger owns evidence.
- D40: 16 logical subsystems, 17 delivery slices, one primary owner/task plus related owners for
  every one of the same 449 discovery headings. subsystem-slices.tsv is the implementation/REST
  task graph; api-blocks.tsv is milestone aggregation. Proto has no exclusive top-level resource;
  native/non-REST requirements still apply. Classification is planning ownership, not verified
  reference semantics, frozen allocation or API acceptance. The original classification performed
  no runtime tests/reviews; subsequent bounded execution is recorded above.
- Cold-start documentation exercise: /root/cold_start_plan independently reconstructed a start-to-
  delivery plan from baseline 3001337 with fork_turns=none. Core order matched: executable-first
  P0, coherent subsystems, honest ledgers, focused tests and final zero-debt milestone/security
  review. Nine wording/routing/gate findings were corrected; cold-start-audit.tsv/json record the
  parent comparison and limits. Original report is ignored/local at
  artifacts/depot-refactor-study/cold-start-audit/agent-plan-3001337.md, SHA256
  259b6679ecafeeac1696c663c03e62061881ea8daba473b920e2f46976be537f.
  No product tests/implementation, new authorization, milestone sign-off or second blind recheck.
- Next: execute #35's verified cohorts immediately, with #12/#32–#34/#36/#39 supplying the affected
  model/protocol/mutation/reference evidence alongside tests; see plan.md's immediate execution order.
  Do not require whole-API prose completion before the first sound test cohort. #40 grows
  the real R0 base suite and #41 the targeted-run case. Continue bounded P0 discovery for
  exact C# mutation representation, wire/client universe, retained-history volume and old-runtime data.
  Do not begin R0 by silently selecting unresolved alternatives.

## Cycle-1 planning repairs (2026-11-01)

The 17 owning slices and 16 domains are unchanged. `subsystem-contributions.tsv` records 11 partial
R0/R2 integration obligations under existing #14/#43/#44 and #20/#65/#66; exact variants remain
unfrozen/unallocated in #36. AC-AG-07/AC-ART-01/03 now include their promised early partial gates.
`api-ledger-delivery.md` defines evidence reuse/main transfer, discovery allocation versus due freeze,
execution triggers and bounded finding classification. #33/#35/#36/#42 own concrete approved
reference acquisition and controls. This is guidance/task repair, not delivered commands, fixtures,
checker, SDK preparation, raw baseline, API completeness or milestone/security acceptance.
Next implementation stays #35 with affected #33/#36 contracts, then actual P0 gates before R0.
Run documents/task/ledger checks for these prose changes; no build/test/runtime check was run.

## Cycle-2 authoring repairs (2026-11-01)

Current contribution register: 12 unfrozen links (the cycle-1 count above records its historical
11-row handoff). Added SS-EXEC -> SS-CFG@R2 under #18/#47/#48 for already-promised R2 step editing;
#22 retains primary step semantics/full R3 scope, #34 proves representation and #36 allocates exact
variants. No new owning slice, task or case. D23 control-owned Project identity/metadata/bindings
are distinct from bound product-scoped C# settings; blank-install/unbound placement/diff proof is
#34/#42 work, not a new schema. Immediate/later settings collection and compatible concurrency/
completion follow reference contracts; exact defaults/timing remain #32/#34/#36. No runtime result,
normalized case, milestone acceptance or new product policy is claimed. Next execution remains #35
alongside affected P0 contracts; these repairs require only cheap documents/task/accounting checks.

## Three-cycle planning provenance (2026-11-01)

[Structured cycle record](planning-probe-cycles.json) retains source/fix revisions, bounded probe angles,
raw-report hashes/local ignored paths, dispositions, actual cheap checks and limits. C1 repairs are
`c33ae7c`; C2 repairs are `46a63a6`. C3 at `46a63a6` found no material defect in its sampled closure
path; no semantic fix followed. This is not exhaustive consistency or runtime/API/milestone/security
PASS. Raw reports remain unchanged local artifacts. #35 executable-first cohorts/real RED, #32–#34
model/reference/C# proofs and #36/#42 accounting/controls remain actual P0 deliverables; no commands
or oracles are invented by this record, and no task state/graph or acceptance gate changed.
