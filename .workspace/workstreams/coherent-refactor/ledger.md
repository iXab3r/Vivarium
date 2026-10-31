# Refactor ledger

Planning baseline: `cbb285a`; draft v1. All implementation gates are OPEN/unverified.
The existing TeamCity ledger and inventories remain the source universe; do not duplicate/waive them.

| Gate | Status | Evidence now | Required closure |
|---|---|---|---|
| Checkpoint | DONE | Pushed commit and annotated `checkpoint/pre-refactor-2026-10-31` tag | Already recorded; not a release/live-data backup |
| Current code census | DONE (inventory only) | `source-inventory.tsv`, reproducible `census.py` | P0 still needs per-path disposition/invariant ownership |
| Depot reference study | DONE (bounded study) | `depot-study.md`, frozen references and selected primary issues/code | Package/API and identity choices still need P0 decisions |
| Plan publication | DONE (draft only) | `issues.json`: 8 milestones, 30 open issues, 22 verified native child relationships | Keep external issues synchronized during owner iteration |
| P0 | OPEN | Plan v1, RF-01…11, S01…34, complete TC routing | Owner iteration; resolve decisions, source/client/data inventories; update ADRs |
| R0 | OPEN | Proposed contracts only | Actual prepared Host/debug/Automation/PG/storage/settings evidence |
| R1 | OPEN | Proposed contracts only | Approved Identity/current-access ordinary-user UI/client evidence |
| R2 | OPEN | Existing invariants and requirements, no target implementation | Source Compile + PG durable execution + real Agent/UI evidence |
| R3 | OPEN | Existing job/settings/edge census | Complete runner/chain/trigger/release semantics for all scoped families |
| R4 | OPEN | Existing parity requirements and selected producer evidence | Historical test/log/artifact/operator journeys and retention |
| R5 | OPEN | Compatibility contract, route census still pending | Exact unchanged-client/HTTP-variant conformance |
| R6 | OPEN | Metadata inventory only; 0/97 accepted | Full history/diff/restore/coexistence/final cleanup and integrated acceptance |

No product implementation, architecture decision, scenario execution or independent review is claimed
by publication of this plan. GitHub issue links are maintained in `issues.json` after creation.
