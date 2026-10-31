# Handover

- Requirements recorded; implementation not started. Start with `docs/design/teamcity-parity.md`,
  then `parity-evidence.md`, `report.md`, `ledger.md` and `phases.md`.
- Frozen baseline: Vivarium `09dd40b`. Local main was fast-forwarded from `0999792` to that revision;
  requirements edits are on `bugfix/teamcity-behavior-parity`. The owner requested a pre-refactor
  checkpoint, identified by tag `checkpoint/pre-refactor-2026-10-31`; subsequent planning is separate.
- Universe: 97 job IDs, 81 unpaused + 16 paused; 38 projects including 3 archived; 17 VCS roots;
  104 Agent records but only Cvat/laptop-g15/SWIFT3 authorized and connected.
- Migration acceptance: 0/97; all 97 remain migration-open/unproven. No metadata row is called parity.
- Collected 1084 job/project surface tasks; repaired all 38 versioned-settings command failures.
  PowerShell's empty-pipeline serialization initially omitted successful empty-array files; these
  were restored from successful collection receipts, and the collector now preserves arrays.
- Integrity checks passed: 97 unique IDs, 970 parseable job-surface files, 114 parseable project-surface
  files, 111 total edges (107 enabled, 4 disabled), no dangling upstream references.
- D32 and the durable parity contract adopt the owner direction: preserve TeamCity behavior on C#,
  in-process Chiral and PostgreSQL. The contract has 120 stable acceptance IDs; generated candidate
  coverage for all 97 configurations is in `configuration-parity.tsv`. All rows remain open/unproven.
- D33 additionally requires a TeamCity-compatible REST facade for unchanged existing clients, including
  read and mutation behavior. See `docs/design/teamcity-rest-compatibility.md` and G17/TC-WIR-*.
  Installed CLI 0.5.2 reports 449 method/path pairs; mechanical route/variant census and actual target
  conformance remain open. No compatible API implementation is claimed.
- Supplemental investigation: 322 successful reads, 88 latest retained build diagnoses, statistics,
  branches, Agent allowed-job policy, representative test/artifact/change/tag/pin reads, zero active
  mutes and one current investigation. Scoped test lists and a long string test-ID lookup worked;
  two typed list/occurrence routes returned HTTP 400 and remain an exporter gap.
- JUnit is mandatory now: PoEBane already produces nextest/Node/Vitest/Playwright reports. Preserve
  individual test history/durations/details, triage and logs alongside command execution. Audit
  `TEAMCITY_VERSION`-guarded helpers so reporting/metadata do not silently turn off under Vivarium.
- Generated facts are `inventory.tsv`, `dependencies.tsv` and ignored safe JSON under
  `artifacts/teamcity-migration/`. Do not hand-classify generated tables. Use the human gap ledger.
- Refresh: run `collect-teamcity.ps1`, `summarize-teamcity.py`, `analyze-sources.py` from this directory
  through the root-relative commands in scope.toml. CLI collection uses a bounded four-call throttle.
- Depot and Chiral default branches were already current. Depot's actual Chiral package is
  `0.1.0-depot.48.1` from `ca5e4582`, ahead of parts of the library main API; do not guess from main.
- Reference-repository remote refs were refreshed; XAC uses remote `GH`, not `origin`.
- Product implementation and TeamCity configuration were not edited. Documentation/routing changed;
  no TeamCity writes/builds/imports, secret-value exports, publications, deployments or commits occurred.
- Root `dotnet build` and `dotnet test` were attempted after updating the base. Both stopped before
  build/test execution: the pinned .NET SDK 10.0.303 is absent; installed .NET 10 is 10.0.103. The SDK
  pin was preserved. Logs are under `artifacts/teamcity-parity/`. Latest recorded Vivarium Windows CI
  is green (337 passed, 12 skipped); it does not validate the proposed implementation migration.
- Owner selected full history/log/artifact import into Vivarium as a mandatory TeamCity shutdown gate.
  An external archive alone is insufficient. See `history-import.md` and G16. Recent-build metadata is
  not a complete history/size census; that census remains the next historical-data investigation.
  This selection does not authorize deletion of the source or production cutover.
- Coexistence is expected. Support an initial full import and repeatable explicit one-way diff-imports:
  add new, skip unchanged, and resolve changed matches by keeping the whole Vivarium snapshot or using
  the whole TeamCity snapshot. No field merge or continuous/bidirectional sync. Preserve imported data
  when TeamCity cleanup removes its source; importing never activates a duplicate trigger/publisher.
- Next concrete delivery: bring the clean Vivarium checkout to the reviewed integration head, then
  update conflicting architecture decisions and implement the small source-based Compile slice on
  Chiral/PostgreSQL with C# definition evaluation, private checkout and durable logs/artifacts.
- Before every family cutover, recover exact live script/settings values through a supported safe
  export/source path and prove observed semantics; current CLI intentionally redacts many values.
- Large chat diff explanation: fast-forward 0999792 -> 09dd40b brought 263 pre-existing files,
  +59,966/-1,949 lines. The chat view includes that repository refresh. Uncommitted own changes are
  documentation and analysis materials; `git diff --name-only -- src tests build` is empty. Do not
  revert the repository refresh or discard new documents just to change the chat's diff display.
