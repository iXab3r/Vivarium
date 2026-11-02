# PoeBane testing-role study — 2026-10-31

Reference main: 2984e1f84cd8e5042dfa976c23e75a26ad63cb1d. The local source-owner checkout is older
and has unrelated dirty generated/Workbench files; it was not pulled, reset, built or edited. Fresh
roles/practices were read through GitHub at the frozen revision.

Inspected Testing Architect (202 lines, changed by 48a4735f5ea684bdce73296a8ef7f8ae2934ca1b,
2026-10-27, POE-1374), verification taxonomy, capability/decision examples, simulator causal ownership
and relevant role routing. Raw reference copies remain ignored under artifacts/depot-refactor-study.

Transferred principles:

- Start from the actual question/observable and existing evidence; select the smallest real composition
  and independent oracle before choosing runner/process placement or adding tests.
- Functional boundary, controlled/live environment/provider, in/out-of-process placement, runner and
  ordinary/extended/long/performance selection are different axes. Gherkin does not force EXE/E2E.
- Composed production components suffice where world/process behavior is outside the claim; use a
  causal real-consumer chain when requested decisions must cause real outcomes. A scripted success
  result cannot prove actual build execution/artifact production.
- Preserve independently defined expectations and useful original assertions when coverage moves;
  no product-derived oracle, fake behavioral RED, all-skipped GREEN or retries hiding the failure.
- Inspect existing Automation/test entry points rather than inventing another harness/scheduler.
  Own partial-start/stop/cleanup and distinguish the real boundaries actually exercised.
- Account for preparation/build/run/cleanup and aggregate group cost; use focused checks and named
  cumulative checkpoints. Long correctness stays visible and runs when the changed contract needs it.
- Planning/docs use static inspection. A new role does not imply a new test per edit, fixed pyramid
  ratio, extra agent for every routine case or another user approval ceremony.

Not imported: Rust/Cargo selectors, gameplay/memory/renderer rules, five-second ordinary-case and
10–15-minute release orientations, source-repo tracker/merge permissions or a new runtime framework.
Existing Vivarium independent milestone reviews remain; none was performed by this study.

The owner separately required highest-level system AC in business language and continuous TeamCity
superset checking. D37 combines that direction with these evidence principles. Business intent now
lives in canonical docs/acceptance/*.feature, technical S* candidates remain in scenarios.md, and
acceptance-map.tsv links them to TCM and the original TC contracts. All 31 new business criteria
are specified-unbound; initial family-level mapping is not executable coverage or acceptance.
