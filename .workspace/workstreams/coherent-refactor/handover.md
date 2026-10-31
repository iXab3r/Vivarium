# Handover

- User requested checkpoint, a Depot-informed milestone/issue plan, then iteration before implementation.
- Frozen/pushed checkpoint: `cbb285a55701e0be0c726c57d9daccd8bfb529e7`, annotated tag
  `checkpoint/pre-refactor-2026-10-31`; runtime remains `09dd40b`.
- Read `plan.md`, `decisions.md`, `depot-study.md`, `scenarios.md`, `ledger.md` and `issues.json`.
  These are draft v1; no RF decision is accepted and no implementation milestone is complete.
- GitHub: P0/roadmap #2, implementation parents #3–#9, child issues #10–#31; eight milestones,
  30 open issues total, 22 native sub-issue relationships verified with matching milestones.
- One integration branch/one final main switch is a provisional interpretation, not inferred approval.
- Existing accepted D32/D33, 120 TC requirements and all 97 configurations remain in scope; 0/97
  migration accepted. Full retained history/log/artifact import and explicit one-way diff-import
  remain mandatory. No source production mutation/deletion or cutover occurred.
- Census is checkpoint-based, not a live size target: 48,402 product C# lines, 21,629 test C# lines.
  `python .workspace/workstreams/coherent-refactor/census.py` regenerates against current tracked HEAD;
  record a new census revision when comparing later, do not relabel it as the original baseline.
- Depot main `2023838` equals fetched origin/main. Chiral main is not Depot's package API; pinned
  versions/source gitlink are in `depot-study.md`. No Depot/Chiral files were changed.
- Local build/test execution is blocked before compilation by missing pinned SDK 10.0.303; this
  planning change does not alter the SDK pin or claim runtime validation. Docs validation is separate.
- Next: owner iteration on RF-01/RF-02/RF-10 and milestone/scenario shape; bounded P0 discovery for
  exact C# mutation representation, wire/client universe, retained-history volume and old-runtime data.
  Do not begin R0 by silently selecting unresolved alternatives.
