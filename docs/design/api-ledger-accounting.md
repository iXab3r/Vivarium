# Offline API ledger accounting

> Implemented partial P0 foundation under #110/#111/#112; former #36/#42 are retained source context.
> Source normalization, model decisions, allocation and
> exact due-scope freeze remain open. No product API-complete or milestone acceptance is claimed.

After the ordinary solution build, read the local ledger and execution evidence:

```powershell
dotnet tools/Vivarium.ApiLedger/bin/Debug/net10.0/Vivarium.ApiLedger.dll . ledger-accounting-working-tree
```

The command prints JSON to standard output. It reads files and bounded local Git identity/tree/diff
information only. It does not build, run tests, contact TeamCity/GitHub, repair inputs or write status.

## Selected fresh-Host R0 evidence

The retained P0 baseline command above keeps its original cohort and historical execution identity.
For the current fresh Host, use the same tool's selected-scope reader:

```powershell
dotnet tools/Vivarium.ApiLedger/bin/Debug/net10.0/Vivarium.ApiLedger.dll . <candidate> --r0 <assessment.json>
```

It validates the [R0 scope](../../.workspace/workstreams/coherent-refactor/r0-contract-scope.json),
[unique product bindings](../../.workspace/workstreams/coherent-refactor/r0-product-bindings.tsv),
primary/contributing owners, exact TRX class/display-name rows, target boundaries, result/assembly
hashes, required working-source/oracle/metadata bytes and explicit execution-to-candidate transfer.
The receipt uses the existing `candidate`, `results`, source-fingerprint, retained-assembly and
assessment conventions. Each result set additionally names its real target, command and harness
assembly role. One execution row cannot discharge multiple obligations. Replay/component rows
cannot stand in for a fresh-Host or ordinary browser boundary. Additional cumulative/supporting
rows are checked for failures but are not counted as new compatibility cases.

D46's owner-approved scope is four complete literal workflows plus 24 other selected obligations.
The two distinct nonzero/running-cancel behavioral passes cannot become GREEN for the retained
full-wire cases; #166 / SS-RUN@R2 owns their exact background estimation/cancellation-time behavior.
All original entities remain intact. Supporting source observations are retained in
[`progress-observations.json`](../../tests/Vivarium.Tests/TeamCity/Reference/progress-observations.json).
This is selected R0 accounting, with no global denominator, percentage or global API-complete claim.
`all_selected_cases_green` covers execution evidence only; independent aggregate/Security review,
all findings, main delivery and tracker closure remain separate milestone gates. Exit zero means
valid accounting, so callers must inspect that explicit evidence state as well.
To save a snapshot, redirect stdout to an artifact, inspect the exit code, then replace the tracked
[api-status.json](../../.workspace/workstreams/coherent-refactor/api-status.json) from that output.
Exit 0 means accounting/evidence validation succeeded, even when product results are RED. Exit 1
means invalid/stale accounting or a harness failure; exit 2 means invalid invocation/unreadable input.
No exit code establishes compatibility or closes an issue.

After a commit/merge, the checked-in assessment and snapshot keep their historical candidate. An
explicit transfer can live in an ordinary ignored JSON file, avoiding a self-referential commit SHA:

```powershell
dotnet tools/Vivarium.ApiLedger/bin/Debug/net10.0/Vivarium.ApiLedger.dll . ledger-accounting-working-tree artifacts/p0-ledger/assessments/current.json
```

Use the same assessment schema, retain the original execution receipt, and record the exact new
`expected_head`, from/to candidate, assessed input/environment impacts and rationale. Hash/tree checks
still apply; passing a new candidate ID or Git label alone grants no transfer. The command never
creates or updates that file. An unassessed head remains stale and does not force an automatic rerun.

The [ledger](../../.workspace/workstreams/coherent-refactor/api-ledger.tsv) retains 449 discovery
headings. The [original three bindings](../../.workspace/workstreams/coherent-refactor/rest-first-cohort-cases.tsv)
and [local bindings](../../.workspace/workstreams/coherent-refactor/rest-local-mutation-cases.tsv)
jointly name the known cases. These small tables bind existing case IDs to existing NUnit methods,
owners, tasks and effects; they do not redefine requests, expected values or fixture semantics.
Exact class plus test display name joins standard TRX rows. A replay row cannot supply product GREEN;
a single row cannot discharge multiple cases. Blocked workflows do not become placeholder tests.

Checks cover discovery/case duplicates and orphans, both directions of API/case lineage, one primary
owner, related owners/tasks, subsystem slices, same-milestone contributing tasks and milestone task
rollups. Subsystem known-case counts use the case's primary owner, including IAM-owned permission
variants over CFG-owned operation headings. The source-union allocation and normalized denominator
remain unknown. Future blocks have unknown discovery allocation; their zero known cases do not
establish empty frozen scope or completion. The protocol owner is never vacuous GREEN.

## Source and story links

The reader now joins the existing stories, surface-scenario candidates, source relations, installed
schema occurrences, CLI fixture inventory/relations, native declarations, Agent envelope fields,
source gaps, story dependencies and implementation contributions. For the present snapshot this is
25 stories, 160 scenario candidates, 52 dependency declarations, 449 schema occurrences, 456 CLI
fixture occurrences, 40 native HTTP declarations, 20 Agent envelope fields and 78 open source gaps.
These are different source registers, not a normalized operation denominator or coverage fraction.

The pinned CLI census also preserves all 41 TypeScript test sources and 233 inline MSW handler
occurrences outside the constant request tables. `cli_test_source_files` and
`cli_inline_handler_source_occurrences` report source counts, never executed requests or server
expectations. The reader validates unique source identity, revision/hash/occurrence joins, per-file
handler counts and source-intake owner/gap; missing rows or a claimed server-oracle/GREEN state fail.
The separate pinned-source generator verifies all five generated syntax tables byte for byte. The
global variant denominator and runtime-branch qualification remain unresolved.

Checks preserve primary source/owner identity, reverse scenario/source and case/API links, exact
fixture occurrence fields, declared schema pointers/profile hashes, known task/slice roles and
retained case identity. One documentation identity may have multiple source occurrences; repeated
operation names do not deduplicate distinct paths. Related scenarios do not double count a case.
Acceptance task links must point to REST-acceptance children, not implementation owners.

Dependency declarations name existing stories or known technical evidence/task references and keep
their bounded output/readiness/owner basis. They do not become reciprocal issue-closure blockers.
Declared current availability and proof descriptions do not establish actual readiness. The command
does not inspect GitHub state or interpret these strings as an execution workflow.

`story_source_join_state` reports referential validation only. Source candidates stay UNFROZEN;
schema/client/native acquisition bytes, semantic aliases, exact actors/current grants, effects,
completion and independent UI/CLI entry evidence need their own proofs. No scenario, story, API or
milestone becomes accepted because those links are valid. The full normalized denominator remains
unknown, and all due gap/impact/freeze/review/debt/main gates remain open.

The [evidence decision](../../.workspace/workstreams/coherent-refactor/api-evidence.json) identifies
the candidate and expected HEAD, original execution receipt/hash/base/source fingerprint, exact known
required case set, reference profile and explicit reuse/invalidate assessment. The command verifies
actual current HEAD, committed production tree, dirty/untracked production files and all recorded
working-file hashes. Required producer/harness paths include tracked and nonignored added files in
the current source/test/tool/build roots plus the root SDK/build inputs, excluding Markdown. Every
accounting table actually consumed by the reader is also required. `required_input_paths` exposes
that input roster; a self-consistent receipt cannot silently omit a consumed table or newly added
producer source. Metadata hashes are execution inputs, not another task-state authority.
Every required file must have execution-time byte provenance; Git
tree equality alone cannot prove working-file/EOL equality. Relevant input changes or unknown impact
are stale, including a merge without an explicit assessed transfer. Unrelated prose/tool edits need
no runtime rerun when the relevant inputs and environment remain valid.

Original receipts keep their execution identity. The older mutation receipt omitted full production
working-file byte provenance, so it cannot independently establish current freshness. The accounting
checkpoint retains that limitation and a bounded fresh five-case raw receipt; it does not rewrite the
old receipt or call reuse a fresh run. Reused replay/control artifacts retain their original receipt
and explicit separate transfer. Retained original DLL hashes describe execution bytes, never mutable
current `bin/` output. Missing/mismatched required evidence fails validation. No binary dependency
graph, PDB engine, scheduler or new test-result format is introduced.

Product RED requires an explicit response-assertion HTTP-mismatch classification matching the actual
TRX message/status evidence. Unknown assertions, setup/cleanup failures and unexecuted results are
harness failures, not allowed RED. Any failed required replay/control row, including a noncase
comparator control, blocks current GREEN and makes validation fail. The report preserves observed raw
rows separately from current reused/stale case accounting. Provenance validation cannot determine
whether arbitrary test assertions constitute the correct independent TeamCity oracle.

This foundation deliberately rejects frozen/global-complete/percentage/review/debt-closure claims.
The known cohort remains unfrozen; global denominator/percentage are null and API-complete false.
No general later-gap impact classifier, complete semantic source-union allocation, exact frozen variant
manifest/history, milestone debt/review evidence or closure workflow is implemented here. Those
remaining #110/#111/#112 obligations stay open under [D39 delivery](api-ledger-delivery.md).

Run the isolated pure-file accounting controls during development:

```powershell
dotnet test tests/Vivarium.Tests/Vivarium.Tests.csproj --filter TestCategory=ApiLedgerControl
```

These use the existing NUnit stack and synthetic standard TRX/files, require no farm or local ignored
artifacts, and exercise positive product GREEN before attempting corrupt/stale/false-complete inputs.
Root `dotnet build`/`dotnet test` remain the coherent handoff checks. Status queries run none of them.

## Historical executable checkpoints

The following receipts retain their original counts, dependency state and execution identities.
The [accounting checkpoint receipt](../../.workspace/workstreams/coherent-refactor/rest-ledger-results.json)
records a passing root build and ordinary baseline (420 Passed, twelve existing TRX NotExecuted rows,
zero Failed; 293.27 seconds including the test host). Only optional assessment-file plumbing changed
after that full run: final root build and all affected accounting controls were run again, while the
unchanged ordinary groups are explicitly reused. The final focused selection passed 77/77 (46 ledger
controls plus 31 replay/composition controls, 3.17 seconds including the test host). The fresh raw
selection remained five HTTP-mismatch failures/exit 1 (2.13 seconds; fixture's own total 1700 ms),
with ten dependent product workflows still blocked. No fresh full ordinary execution on the final
plumbing sources is claimed.

All 164 tracked production files plus nineteen fixture/harness inputs are byte-fingerprinted in that
receipt. The generated snapshot validates 449 headings, fifteen known cases, five product RED and ten
product-blocked cases, with zero product GREEN. Seven headings have a bound case; three have ready
raw cases. Default and external assessment entry points returned identical output; one default read
took 276 ms on this Windows host. These are local measurements, not bulk-scaling or cross-platform
evidence. Existing package advisories and platform prerequisites remain; no Security PASS is claimed.

The [missing-evidence follow-up receipt](../../.workspace/workstreams/coherent-refactor/rest-ledger-missing-evidence-results.json)
records `API-LEDGER-MISSING-SENTINEL-001`: an absent oracle could match a malformed declared hash of
`MISSING`. Absence now returns null, which cannot match a required hash string. A control that begins
with valid product GREEN reproduced the defect before the fix; all 47 current accounting controls
and root build then passed. The 183 product/reference inputs and prior execution receipts remain
unchanged. Product raw, reference/composition and unaffected ordinary evidence are explicitly reused;
no new full ordinary/raw run or final milestone review is claimed for this one-line error-path fix.
