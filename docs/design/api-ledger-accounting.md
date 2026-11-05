# Offline API ledger accounting

## Current Host status (#172)

After building the tool once, run this offline projection from the repository root:

```powershell
dotnet tools/Vivarium.ApiLedger/bin/Debug/net10.0/Vivarium.ApiLedger.dll . --current 1> artifacts/rest-status172/current.json 2> artifacts/rest-status172/current.txt
```

Create the output directory first if saving files. Without redirection, stdout is JSON and stderr is
the readable report. The command itself writes no files, builds nothing, runs no tests and starts no
network hosts. Exit 0 means the bounded registry projection is valid, **including UNKNOWN evidence**;
exit 2 means unreadable/invalid accounting. Inspect `evidence_state`, never infer compatibility from
the exit code. Original normal/`--r0`/`--pilot` exit semantics remain unchanged.

The normal reader's second argument is a named assessment identity: the default assessment names
`ledger-accounting-working-tree`. Passing an arbitrary HEAD SHA there legitimately yields “Unknown
candidate”; this is invocation misuse, not proof of an API regression. Using the correct name still
does not renew its old expected HEAD or missing producer provenance. `--current` observes HEAD
without weakening that strict gate. It does not manufacture an assessment or edit retained hashes.

The projection joins the canonical subsystem, discovery, materialized-case, R0-binding, PILOT
declaration, native-source and source-disposition tables. JSON retains source paths/current hashes,
missing source observations, historical outcome IDs, assessment/receipt identity and hashes of every
consumed register plus the selected final delivery record. Current adapter source observations are
bounded to existing Controller adapter directories. They are neither exhaustive route discovery nor
wire proof. Read the surface descriptions with their source observations; source presence alone
does not qualify implementation behavior. Unmatched routes and unknown owners remain open under
#161/#162/#163. Each subsystem links its owner and existing contract/acceptance work.

Validated example at HEAD `5fe8bf4292a309bc0d228c040996265e902a7125` (dirty implementation tree,
2026-11-05; report executes no tests):

```text
Accounting: VALID; evidence: UNQUALIFIED_CURRENT_HEAD
Global denominator/percentage: UNKNOWN; API complete: false
449 discovery headings; 25 materialized historical cases (5 RED, 20 PRODUCT_BLOCKED)
28 selected R0 bindings; 7 declared PILOT wire variants (unmaterialized)
SS-CFG: 94 headings, 10 historical cases, 4 R0 bindings
SS-RUN: 51 headings, 4 historical cases, 5 R0 bindings, 7 declarations
SS-AGT: 42 headings, 0 historical cases, 2 R0 bindings
SS-IAM: 58 headings, 10 historical cases, 8 R0 bindings
SS-LOG: 1 heading, 0 historical cases, 7 R0 bindings
```

These are different populations. Zero materialized historical cases does not mean an unsupported
native API. The selected Chiral/PG R0 delivery (28 obligations) and PILOT delivery (34 business/SDK
rows) remain accepted at their recorded identities in the
[final delivery record](../../.workspace/workstreams/coherent-refactor/pilot-final-delivery-20261105.md).
They are not current full-wire GREEN. This increment deliberately grants no transfer: the current
tree has additional producer inputs and no default `r0-api-evidence.json` or `pilot-api-evidence.json`.
The existing `--r0`/`--pilot` readers remain the qualification entry points with explicit assessments;
the parent must supply input-specific reviewed reuse or request the smallest affected renewal.
No blanket rerun is needed just to display status. Missing reference variants stay with the owning
contract tasks; exact progress/cancel behavior stays #166. The private farm smoke cannot fill these
wire gaps. No previously GREEN result is deleted or relabeled by this projection.

Focused controls: `dotnet test tests/Vivarium.Tests/Vivarium.Tests.csproj --filter FullyQualifiedName~ApiLedgerCurrentTests`.
Local report/JSON and standard TRX are under `artifacts/rest-status172/`; full root verification and
independent aggregate/Security review belong to the parent integration checkpoint.

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

### PILOT declarations

`api-ledger.tsv` keeps materialized wire bindings in `variant_case_ids`. Its optional
`declared_variant_ids` relation joins the bounded `pilot-contract-variants.tsv` inventory and
`pilot-contract-scope.json` declaration roster. These retain existing IDs, owners, tasks,
source pointers and unresolved reference variants. The reader checks both API join directions,
unique identity, PILOT slice/task ownership and source existence. Declarations increase only
`declared_pilot_variants`; selected story/product receipts remain separate and do not establish
materialized, executed or GREEN wire cases. R0 retains its original inventory and scope.

`accounting_state` distinguishes structurally valid registries from execution evidence status.
`VALID` with `STALE_OR_INVALID_EVIDENCE` still exits nonzero and grants no GREEN or acceptance.
An invalidation assessment retains original receipts; new required metadata absent from those
receipts remains an explicit stale-evidence error, even after the registry itself is repaired.
Source-disposition hashes observe current source bytes only and do not renew replacement proof.


The existing offline reader also accepts `--pilot <assessment-file>`. It joins the
normative `pilot-contract-scope.json` business IDs to exact discovered test names and
raw TRX class/name/outcome rows, with hash-bound cohort receipt, binding table,
discovery log and seal. These are business/SDK evidence, never additional literal
TCREST cases. Seven declared variants remain declarations in the global join.
The external assessment binds `candidate_id`, `receipt`, `seal`, `bindings`,
`discovery` (each path/sha256), and `trx`. Optional `heavy_results` use the existing
raw-result descriptor (artifact/sha256/counters/exit_code) plus a role. Original
observer failure and subsequent read-only continuation have separate outcomes.
Input and producer drift is reported individually. Missing candidate reuse decisions
or input-specific impact support withholds current GREEN while preserving raw
outcomes. Heavy composition has separate hash-bound CLI, graph/source/definition,
remote metadata and exact downloaded-file checks. Actor/readback assertions remain
bounded by the raw continuation and source review; summary booleans alone are insufficient.
Legacy SQLite baseline currency, selected Chiral/PG evidence, unknown reference
variants and review/debt/main acceptance remain separate. No runtime is executed.


## PILOT input-specific transfer

`--pilot` reports currency for the existing 33 business bindings plus required SDK separately
from heavy composition, reference gaps, historical baseline and milestone acceptance. See the
[bounded reuse assessment](pilot-reuse-assessment.md). `current_green` is selected evidence
currency only. `heavy_gaps` must also be inspected; a successful exit does not accept a milestone.
Each changed/additional input needs its old hash (or ADDED), current hash, affected binding,
category, observed diff, assessor, rationale, hash-bound supporting evidence and explicit independent
review requirement. Producer decisions additionally identify metadata or test-only cause; unknown
runtime impact stays unqualified. These fields attribute human reasoning; they cannot prove it correct.
