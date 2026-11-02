# Behavioral parity evidence

## Scope and repeatability

Baseline configuration census remains the 97 stable IDs in `inventory.tsv`; 0 migrations accepted.
`docs/design/teamcity-parity.md` contains 120 durable behavioral requirements. `map-parity.py` derives
`requirement-catalog.tsv` and `configuration-parity.tsv`; generated candidate gates are not human
classifications or completion claims. Exact settings/producer review remains required for every row.

`collect-parity.ps1` performed 322 successful additional typed CLI reads with a four-call throttle.
Safe snapshots are ignored under `artifacts/teamcity-parity/`. They include:

- branch samples for all 97 configurations, explicitly bounded at 25;
- the latest retained build diagnosis and statistics for 88 configurations with sampled history;
- allowed-job and incompatible-configuration policies for the three authorized Agents;
- global mute/investigation pages (complete: zero active mutes, one investigation);
- bounded test/artifact/change samples and tag/pin reads for eight representative configurations;
- reference server identity: TeamCity 2026.1.3, build 222742.

All 322 planned reads succeeded. Two separate exploratory routes, global `tests list` and a typed
occurrence lookup, returned HTTP 400; they are not included in that success count. A scoped build test
list and `tests show 235031264379903299` worked. A CLI capability/locator problem is not proof that
test history/details are absent in TeamCity. Full source export still needs its own acceptance.

Recent runtime samples are not a full retained-history census. Branch/test/artifact/change sample
pages are not silently treated as complete collections. Of the 88 latest retained builds, 12 expose
test counts in the diagnosis; absence of counts in another sample does not prove a test-free job.

## Representative observations

| Evidence | Observation and implication |
|---|---|
| EyeAuras Compile 32598 | 19,264 test occurrences, including one failure and 279 ignored; history/tests must support large paginated suites and preserve real failures. |
| PoEBane Compile 32546 | Test totals and ignored cases are reported; source driver supports JUnit from several runtimes, so a TRX-only target loses used functionality. |
| Vivarium Compile 32194 | Test totals, per-step durations and artifact-size statistics are present; statistics/test/log views are part of the CI workflow. |
| Current investigation | An assigned test investigation exists under EyeAuras; preserve source test/user identity, scope and history without granting imported users live permissions. |
| Long test ID lookup | String ID 235031264379903299 resolves to a parameterized Frida SDK test; JSON/UI numeric coercion would lose identity precision. |
| Agent allowed-job policy | Cvat, laptop-g15 and SWIFT3 currently use `any`; support the policy explicitly, along with static compatibility and current availability. |
| Existing build features | AssemblyInfo, swabra, NuGet indexer, VCS labeling, perfmon, shared resources, free-disk checks and keep rules have observable effects to preserve. |
| Docker ordinary-step properties | CLI.Factory/Workbench use a wrapper around a script, in addition to DockerCommand publishers elsewhere; both execution forms need acceptance. |

## Source-driver evidence

The original source census pins repository revisions in
`artifacts/teamcity-migration/source-feature-census.json`. Additional driver inspection found:

- PoEBane `build/TeamCity.cs`: `TEAMCITY_VERSION` controls report import, output parameters, build
  number and nested blocks. A plain runner relocation without adapter/env handling disables these.
- PoEBane `build/TestReport.cs` and `BuildCommands.cs`: nextest, Node, Vitest and Playwright JUnit,
  multiple report paths, ignored/skipped results and normalization for the source importer.
- CLink `.teamcity/build`: TRX creation plus dynamic build numbers; retain exact verified producer
  artifacts and avoid double execution at publication.
- Vivarium `build`: TRX, dynamic build number and provider-neutral packaging behavior.
- EyeAuras `.teamcity` and generated patch files: inherited requirements/templates, same-Agent
  chains, archive mappings, source version counters, containers, tagging, cleanup and release paths.

Some additional driver reads used existing local source files for discovery, rather than claiming
they exactly match the TeamCity-applied revision. Exact applied settings/driver provenance is still a
per-family gate; the durable requirement does not depend on pretending that discovery proves parity.

## Documentation reconciliation

- Adopted D32 for the owner's TeamCity behavior/C#/Chiral/PostgreSQL direction and coexistence/import.
- Linked the parity contract from architecture, roadmap, the design index and TeamCity/UI/results/
  logs/persistence/Git routing. Kept current SQLite/YAML implementation status explicit.
- Refined the future catalog queue rule: zero currently matching Agents means an explainable queue
  wait, while the existing payload-matrix fail-fast API remains a named transitional behavior.
- JUnit, test history/triage, compatible/incompatible reasons, durable log workflows and statistics
  are acceptance gates, not optional UI polish.
- Fast-forwarded the clean tracked main from 0999792 to 09dd40b and created
  `bugfix/teamcity-behavior-parity`; preserved all untracked analysis materials. No product source,
  TeamCity setting, release, deployment, secret value, commit or remote branch was changed.

## Verification

The REST compatibility follow-up adopts D33 and TC-WIR-01..12. Source inspection of installed CLI
0.5.2 confirms base-URL/context handling, nested/base64 locators and numeric continuation parsing.
Its README reports 449 method/path pairs; no complete executable Vivarium route coverage is implied.
The endpoint/variant census and unchanged-client isolated conformance are new explicit gates.

The chat's large diff includes the repository fast-forward: `git diff --shortstat 0999792..HEAD`
reports 263 files, +59,966/-1,949 lines. Own tracked edits are documents; own new files are requirements
and workstream materials. No uncommitted changes exist in src/tests/build. No product implementation
was added to explain or hide this display issue.

- Generated 120 unique requirement IDs and 97 unique configuration candidate rows; no row is closed.
- Root `dotnet build` and `dotnet test` were attempted; both stopped before compilation/execution
  because SDK 10.0.303 from `global.json` is not installed. Installed .NET 10 SDK is 10.0.103.
  Do not weaken the exact SDK pin for a documentation change. Full outputs are ignored local files
  `artifacts/teamcity-parity/dotnet-build.log` and `dotnet-test.log`.
- Documentation checks passed: 120 unique requirement IDs, 97 unique open candidate rows, all
  candidate references resolve, scope.toml parses, local links/anchors in the 12 affected documents
  resolve, Architecture has exactly one D32, and `git diff --check` passes. No product tests were
  added or changed; these edits specify behavior rather than implement it.
