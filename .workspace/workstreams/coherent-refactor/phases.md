# Phase state

Active: P0 owner iteration / bounded follow-up discovery.

1. Checkpoint and census — complete.
2. Depot/PoeBane study and plan v1.4 — recorded; RF-10 accepted (separate milestone merges to main).
   D34/D35 accept Depot authentication/superuser, exact TeamCity RBAC in PG, Chiral settings ownership
   and Depot-style TypeScript/Vite Workbench UI; remaining choices still require P0 work.
3. Deep REST resource/ID/state/protocol/mutation/harness/allocation — open #12/#32–#36 before
   structural implementation. Initial 456 synthetic fixture rows discovered, not reconciled coverage.
4. R0.1 local Host+real Agent (#37), R0.2 unchanged-CLI smoke (#38), R0.3 restart/cleanup — planned.
5. R1…R6 domain expansion and cumulative client/migration proof — not started or accepted.
6. D37 is continuous across all phases: business AC/model/wire links and a small cumulative
   implemented TeamCity base plus extension-neutrality checks at each milestone, not only R5.
   Business specs #39 are specified-unbound; R0 bindings #40 and targeted-run gap #41 remain open.

Acceptance dependencies and exact exit contracts live in `plan.md`; status/evidence lives in
`ledger.md`, TC requirement assignment in `requirement-routing.tsv`, external coordination in
GitHub milestones/issues. Closing P0 requires updating the authoritative architecture/designs for
chosen differences, not merely resolving conversations in GitHub.
