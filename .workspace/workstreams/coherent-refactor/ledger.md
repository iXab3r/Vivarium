# Refactor ledger

Planning baseline: `cbb285a`; plan v1.2. All implementation gates are OPEN/unverified.
The existing TeamCity ledger and inventories remain the source universe; do not duplicate/waive them.

| Gate | Status | Evidence now | Required closure |
|---|---|---|---|
| Checkpoint | DONE | Pushed commit and annotated `checkpoint/pre-refactor-2026-10-31` tag | Already recorded; not a release/live-data backup |
| Current code census | DONE (inventory only) | `source-inventory.tsv`, reproducible `census.py` | P0 still needs per-path disposition/invariant ownership |
| Depot reference study | DONE (bounded study) | `depot-study.md`, frozen references and selected primary issues/code | Package/API and identity choices still need P0 decisions |
| Plan publication | DONE (draft with accepted RF-10) | `issues.json`: 8 milestones, 30 open issues, 22 verified native child relationships; all 30 issue bodies synchronized and response-verified for RF-10 | Keep external issues synchronized during owner iteration |
| RF-10 delivery model | ACCEPTED | Owner selected separate milestone merges into main on 2026-10-31 | Per-milestone acceptance/review/cleanup; closure after that milestone reaches main; R6 cumulative verification |
| D34/D35 direction | ACCEPTED, NOT IMPLEMENTED | Depot authentication/installation user; TeamCity RBAC 1:1 in PG; Chiral resource settings; TypeScript/Vite Workbench; seven actual source role definitions captured read-only | Resolve concrete bindings/client profiles, implement and prove R0/R1; no runtime claim |
| D34/D35 issue synchronization | DONE | 24 affected issue bodies response-verified, including 19 explicit contract addenda; R0/R1 milestone descriptions updated | Keep future accepted refinements synchronized; no implementation issue closed |
| P0 | OPEN | Plan v1.2, accepted D34/D35/RF-10, remaining choices open, S01…35, complete TC routing | Owner iteration; resolve remaining decisions, source/client/data inventories; update ADRs |
| R0 | OPEN | Proposed contracts only | Actual prepared Host/debug/Automation/PG/storage/settings evidence |
| R1 | OPEN | Proposed contracts only | Approved Identity/current-access ordinary-user UI/client evidence |
| R2 | OPEN | Existing invariants and requirements, no target implementation | Source Compile + PG durable execution + real Agent/UI evidence |
| R3 | OPEN | Existing job/settings/edge census | Complete runner/chain/trigger/release semantics for all scoped families |
| R4 | OPEN | Existing parity requirements and selected producer evidence | Historical test/log/artifact/operator journeys and retention |
| R5 | OPEN | Compatibility contract, route census still pending | Exact unchanged-client/HTTP-variant conformance |
| R6 | OPEN | Metadata inventory only; 0/97 accepted | Full history/diff/restore/coexistence/final cleanup and integrated acceptance |

RF-10 and D34/D35 direction are accepted; no product implementation, scenario execution or
independent review is claimed. GitHub issue links are maintained in `issues.json`.
