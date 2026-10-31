# Handover

- User requested checkpoint, a Depot-informed milestone/issue plan, then iteration before implementation.
- Frozen/pushed checkpoint: `cbb285a55701e0be0c726c57d9daccd8bfb529e7`, annotated tag
  `checkpoint/pre-refactor-2026-10-31`; runtime remains `09dd40b`.
- Read `plan.md`, `decisions.md`, `depot-study.md`, `scenarios.md`, `ledger.md` and `issues.json`.
  Plan v1.3 remains draft for unresolved choices; RF-10 delivery and D34/D35/D36 directions are accepted.
  No implementation milestone is complete.
- GitHub: P0/roadmap #2, implementation parents #3–#9, original children #10–#31, REST sub-children
  #32–#36 under #12 and local-lab tasks #37/#38 under #3: eight milestones, 37 open issues,
  29 native child relationships with matching milestones.
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
- UI clarification: share technology/Host/Identity/schema integration, preserve useful current
  Vivarium/TeamCity composition and workflows; no forced Depot split-view redesign.
- D36 makes deep REST contracts precede structural implementation. Read rest-breakdown.md,
  rest-census.json, rest-fixture-inventory.tsv and local-lab.md. R0.1 is a real Host+Agent lab,
  R0.2 the unchanged-CLI smoke, R0.3 restart/cleanup. R2 remains full source-based C# Compile.
- REST discovery: reference repo b158fe03eab3c31982a21b36c278f690f6b541d9 is version 0.5.2;
  source-owner checkout is older 0.4.1 and was not changed. Separate source copy is ignored/private.
  Generator extracted 456 synthetic fixture rows; not normalized route count or executed conformance.
  npm metadata lacks gitHead; full Swagger/inline/other-client union and binary provenance are open.
- Depot UI reference: apps/depot-mock is the production frontend at the frozen commit; main.ts,
  session.ts, management-api.ts, nodes-api.ts/node-settings.ts and Access list/inspector were inspected.
- Read-only source role census captured seven definitions and their permissions/includes, including
  RESTRICTEDBOT and TOOLS_INTEGRATION, under ignored artifacts/depot-refactor-study/teamcity-role-*.json.
- Next: execute the concrete #12/#32–#36 contract work and refine S01–S07/S35–S37; bounded P0 discovery for
  exact C# mutation representation, wire/client universe, retained-history volume and old-runtime data.
  Do not begin R0 by silently selecting unresolved alternatives.
