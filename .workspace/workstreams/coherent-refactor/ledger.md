# Refactor ledger

Planning baseline: `cbb285a`; plan v1.8. All implementation gates are OPEN/unverified.
The existing TeamCity ledger and inventories remain the source universe; do not duplicate/waive them.

| Gate | Status | Evidence now | Required closure |
|---|---|---|---|
| Checkpoint | DONE | Pushed commit and annotated `checkpoint/pre-refactor-2026-10-31` tag | Already recorded; not a release/live-data backup |
| Current code census | DONE (inventory only) | `source-inventory.tsv`, reproducible `census.py` | P0 still needs per-path disposition/invariant ownership |
| Depot reference study | DONE (bounded study) | `depot-study.md`, frozen references and selected primary issues/code | Package/API and identity choices still need P0 decisions |
| Plan publication | VERIFIED historical planning snapshot, not delivery | `issues.json`: 8 milestones, 75 open issues, 67 native child links at planning publication; same-P0 debt #77 was subsequently added under #35 | GitHub owns current task state; no implementation issue closed |
| RF-10 delivery model | ACCEPTED | Owner selected separate milestone merges into main on 2026-10-31 | Per-milestone acceptance/review/cleanup; closure after that milestone reaches main; R6 cumulative verification |
| D34/D35 direction | ACCEPTED, NOT IMPLEMENTED | Depot authentication/installation user; TeamCity RBAC 1:1 in PG; Chiral resource settings; TypeScript/Vite Workbench; seven actual source role definitions captured read-only | Resolve concrete bindings/client profiles, implement and prove R0/R1; no runtime claim |
| D34/D35 issue synchronization | DONE | 24 affected issue bodies response-verified, including 19 explicit contract addenda; R0/R1 milestone descriptions updated | Keep future accepted refinements synchronized; no implementation issue closed |
| REST initial source discovery | DONE (not conformance) | Pinned same-version reference + installed runtime hash; 456 synthetic fixture rows from ten tables, safe receipt and generator | Full reference/runtime provenance, Swagger/inline/client union, normalized route/variant review and dispositions still open |
| D36 priority/UI clarification | ACCEPTED, NOT IMPLEMENTED | REST-driven model before implementation; shared UI technology preserves current UX; local Host+Agent/CLI gates in R0 | P0 structural contracts and R0 actual process/client evidence |
| D37 acceptance/conformance | ACCEPTED, NOT EXECUTED | 110 canonical business AC specified-unbound; 10 TCM invariants; 38 technical candidates; family-level trace map; fresh PoeBane Testing Architect study | #39 review/refine independent contracts/oracles; #40 bind real R0 evidence; cumulative per-milestone gates |
| D38 executable-first slice | PARTIAL EXECUTABLE COHORT, GATES OPEN | #35: fifteen known materialized cases (fourteen captured, one source-established); five ready product cases executed RED/nonzero, ten dependent variants product-prerequisite-blocked; thirty-one strict replay/composition controls passed; current evidence in rest-local-mutation-results.json, original rest-first-cohort-results.json retained historically; root build/test passed | Broader verified corpus/Agent/bytes/PG/cost proof, all P0.REST.1–3 scope, #36 accounting and #42 acceptance remain open |
| D39 task/ledger/closure | POLICY/SEED RECORDED, NOT EXECUTED | Eight milestone aggregates, now composed from D40 subsystem slices; 449 headings with variant/delivery allocation still open; API UNVERIFIED; debt NOT_ASSESSED; review NOT_REVIEWED | #36 mechanical ledger/result accounting, #42 baseline verification; each milestone final correctness/simplicity + Security/Adversarial PASS and zero debt before next implementation |
| D40 subsystem ownership | TASK GRAPH/CLASSIFICATION, NOT IMPLEMENTED | 16 logical owners/17 slices; all 449 discovery rows have one primary subsystem, implementation/REST task links and collaborators; native REST children under subsystem slices | #36 joins subsystem/milestone/case evidence; prove complete lifecycles/integrations and API separately; exact variants/allocation remain open |
| Cold-start documentation audit | CORE PLAN RECOVERED; PROSE CORRECTIONS RECORDED | Independent empty-context plan at 3001337; nine navigation/authority/gate findings in cold-start-audit.tsv; parent comparison in cold-start-audit.json | Corrected sources checked locally; no second independent post-fix run, exhaustive docs proof, runtime acceptance or Security/Adversarial PASS claimed |
| TCM-GAP-001 | OPEN, UNIMPLEMENTED/UNVERIFIED | Explicit suitable-agent custom run on a disabled Agent documented by TeamCity; absolute normal-queue prohibition clarified in AC-AG-02/06 and TC-AG-01 | #41 freezes reference request/permission variant and proves normal-block/explicit-target behavior; docs are not runtime PASS |
| P0 | OPEN | Plan v1.8, accepted D34–D40/RF-10, remaining choices open, S01…38 and business trace links | Execute P0.REST.1–3, #36 subsystem-aware ledger accounting and #42 verification; resolve structural/model/protocol/mutation and AC/oracle gates; linked rows are not coverage |
| R0 | OPEN | Local-lab contract and first wire subset; no new-stack run delivered | R0.1 Host+Agent; R0.2 unchanged CLI; R0.3 restart/regressions/cleanup |
| R1 | OPEN | Proposed contracts only | Approved Identity/current-access ordinary-user UI/client evidence |
| R2 | OPEN | Existing invariants and requirements, no target implementation | Source Compile + PG durable execution + real Agent/UI evidence |
| R3 | OPEN | Existing job/settings/edge census | Complete runner/chain/trigger/release semantics for all scoped families |
| R4 | OPEN | Existing parity requirements and selected producer evidence | Historical test/log/artifact/operator journeys and retention |
| R5 | OPEN | Compatibility contract, route census still pending | Exact unchanged-client/HTTP-variant conformance |
| R6 | OPEN | Metadata inventory only; 0/97 accepted | Full history/diff/restore/coexistence/final cleanup and integrated acceptance |

RF-10 and D34–D40 direction are accepted; no structural replacement, full business-AC binding or
independent review is claimed. GitHub issue links are maintained in `issues.json`.

Cycle-1 clarification: `subsystem-contributions.tsv` adds 11 unfrozen early R0/R2 contribution
links without adding owning slices, native tasks or cases. Known-discovery allocation versus exact
due freeze, evidence reuse/main transfer, broad-run triggers and finding classification are specified
in `docs/design/api-ledger-delivery.md`; #36/#42 still implement/prove them. Approved schema/raw HTTP
acquisition is an explicit #33/#35 deliverable. No execution/acceptance status changed.

Cycle-2 clarification: the contribution register now has 12 unfrozen links, adding step editing
SS-EXEC -> SS-CFG@R2 (#18/#47/#48) without changing 16 owners/17 slices, source IDs or coverage.
#34/#42 own the blank-install Project/BuildType/binding representation/diff proof; #32/#34/#36
retain exact collection-mode/concurrency/completion and due-variant contracts. Internal Git CAS
and immutable actual provenance remain; no universal queue-time freeze or compatible native-only
precondition/async rule is adopted. Format/SDK/path/reference unknowns stay future deliverables.

[Three-cycle planning record](planning-probe-cycles.json): C1/C2 material repairs are committed as
`c33ae7c`/`46a63a6`; C3 at `46a63a6` found no material defect in its bounded closure sample. The
record preserves exact revisions, local ignored raw-report paths/hashes, dispositions, cheap checks
and limits. It is provenance, not runtime/API/milestone/security acceptance or a fourth probe.
Actual P0 reference/representation/harness/RED and ledger/control deliverables remain open.

Current bounded cohort status (2026-11-01): `api-status.json` records seven headings with any bound
reference/replay case and three with product-ready raw cases; 442 discovery headings have no bound
case. This is partial first-variant binding, not complete allocation or a compatibility percentage.
The full operation/variant denominator and allocation remain unknown. The owner authorizes synthetic
fixtures and dedicated new test Agents on the separate local playground without per-fixture approval;
production and existing fleet Agents remain excluded. Owned mutation fixtures/PAT were removed,
and lab CLI write gates are restored off. No milestone acceptance, review PASS or closure is claimed.
