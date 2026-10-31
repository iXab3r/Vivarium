# Handover

- User requested checkpoint, a Depot-informed milestone/issue plan, then iteration before implementation.
- Frozen/pushed checkpoint: `cbb285a55701e0be0c726c57d9daccd8bfb529e7`, annotated tag
  `checkpoint/pre-refactor-2026-10-31`; runtime remains `09dd40b`.
- Read `plan.md`, `decisions.md`, `depot-study.md`, `scenarios.md`, `ledger.md` and `issues.json`.
  Plan v1.2 remains draft for unresolved choices; RF-10 delivery and D34/D35 directions are accepted.
  No implementation milestone is complete.
- GitHub: P0/roadmap #2, implementation parents #3–#9, child issues #10–#31; eight milestones,
  30 open issues total, 22 native sub-issue relationships verified with matching milestones.
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
- Local build/test execution is blocked before compilation by missing pinned SDK 10.0.303; this
  planning change does not alter the SDK pin or claim runtime validation. Docs validation is separate.
- D34 accepts Depot Identity/OpenIddict/OIDC/installation account plus TeamCity roles/permissions,
  editable definitions/includes/groups/project scopes 1:1, as PG data. Legacy Git-RBAC and custom
  claim/recovery/password paths are replacement targets, not ongoing authorities.
- D35 accepts Chiral resource settings/readiness, PG structured state, Git versioned settings and
  Storage bytes, plus Depot's actual TypeScript/Vite Workbench frontend (no React dependency).
- Depot UI reference: apps/depot-mock is the production frontend at the frozen commit; main.ts,
  session.ts, management-api.ts, nodes-api.ts/node-settings.ts and Access list/inspector were inspected.
- Read-only source role census captured seven definitions and their permissions/includes, including
  RESTRICTEDBOT and TOOLS_INTEGRATION, under ignored artifacts/depot-refactor-study/teamcity-role-*.json.
- Next: refine accepted mechanisms and S01–S07/S35; bounded P0 discovery for
  exact C# mutation representation, wire/client universe, retained-history volume and old-runtime data.
  Do not begin R0 by silently selecting unresolved alternatives.
