# Refactor ledger

Planning baseline: `cbb285a`; plan v1.6. All implementation gates are OPEN/unverified.
The existing TeamCity ledger and inventories remain the source universe; do not duplicate/waive them.

| Gate | Status | Evidence now | Required closure |
|---|---|---|---|
| Checkpoint | DONE | Pushed commit and annotated `checkpoint/pre-refactor-2026-10-31` tag | Already recorded; not a release/live-data backup |
| Current code census | DONE (inventory only) | `source-inventory.tsv`, reproducible `census.py` | P0 still needs per-path disposition/invariant ownership |
| Depot reference study | DONE (bounded study) | `depot-study.md`, frozen references and selected primary issues/code | Package/API and identity choices still need P0 decisions |
| Plan publication | VERIFIED publication, not delivery | `issues.json`: 8 milestones, 40 open issues, 32 native child links; D37/D38 synchronization receipts and #35 composition clarification; live review reconfirmed 40 open issues | Keep external issue gates synchronized; no implementation issue closed |
| RF-10 delivery model | ACCEPTED | Owner selected separate milestone merges into main on 2026-10-31 | Per-milestone acceptance/review/cleanup; closure after that milestone reaches main; R6 cumulative verification |
| D34/D35 direction | ACCEPTED, NOT IMPLEMENTED | Depot authentication/installation user; TeamCity RBAC 1:1 in PG; Chiral resource settings; TypeScript/Vite Workbench; seven actual source role definitions captured read-only | Resolve concrete bindings/client profiles, implement and prove R0/R1; no runtime claim |
| D34/D35 issue synchronization | DONE | 24 affected issue bodies response-verified, including 19 explicit contract addenda; R0/R1 milestone descriptions updated | Keep future accepted refinements synchronized; no implementation issue closed |
| REST initial source discovery | DONE (not conformance) | Pinned same-version reference + installed runtime hash; 456 synthetic fixture rows from ten tables, safe receipt and generator | Full reference/runtime provenance, Swagger/inline/client union, normalized route/variant review and dispositions still open |
| D36 priority/UI clarification | ACCEPTED, NOT IMPLEMENTED | REST-driven model before implementation; shared UI technology preserves current UX; local Host+Agent/CLI gates in R0 | P0 structural contracts and R0 actual process/client evidence |
| D37 acceptance/conformance | ACCEPTED, NOT EXECUTED | 110 canonical business AC specified-unbound; 10 TCM invariants; 38 technical candidates; family-level trace map; fresh PoeBane Testing Architect study | #39 review/refine independent contracts/oracles; #40 bind real R0 evidence; cumulative per-milestone gates |
| D38 executable-first slice | ACCEPTED, NOT EXECUTED | #35, test-first design and three planned-unexecuted checkpoints; bounded simulation/composition contract | Prepare exact SDK, freeze verified cohorts, prove replay/composition controls and run actual current-product RED before replacement; measure cost before bulk expansion |
| TCM-GAP-001 | OPEN, UNIMPLEMENTED/UNVERIFIED | Explicit suitable-agent custom run on a disabled Agent documented by TeamCity; absolute normal-queue prohibition clarified in AC-AG-02/06 and TC-AG-01 | #41 freezes reference request/permission variant and proves normal-block/explicit-target behavior; docs are not runtime PASS |
| P0 | OPEN | Plan v1.6, accepted D34–D38/RF-10, remaining choices open, S01…38 and business trace links | Execute P0.REST.1–3, including minimal deterministic composition/cost proof; resolve structural/model/protocol/mutation and AC/oracle gates; linked rows are not coverage |
| R0 | OPEN | Local-lab contract and first wire subset; no new-stack run delivered | R0.1 Host+Agent; R0.2 unchanged CLI; R0.3 restart/regressions/cleanup |
| R1 | OPEN | Proposed contracts only | Approved Identity/current-access ordinary-user UI/client evidence |
| R2 | OPEN | Existing invariants and requirements, no target implementation | Source Compile + PG durable execution + real Agent/UI evidence |
| R3 | OPEN | Existing job/settings/edge census | Complete runner/chain/trigger/release semantics for all scoped families |
| R4 | OPEN | Existing parity requirements and selected producer evidence | Historical test/log/artifact/operator journeys and retention |
| R5 | OPEN | Compatibility contract, route census still pending | Exact unchanged-client/HTTP-variant conformance |
| R6 | OPEN | Metadata inventory only; 0/97 accepted | Full history/diff/restore/coexistence/final cleanup and integrated acceptance |

RF-10 and D34–D38 direction are accepted; no product implementation, scenario execution or
independent review is claimed. GitHub issue links are maintained in `issues.json`.
