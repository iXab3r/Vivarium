# TeamCity behavioral parity

> Status: **Accepted**
> Implementation: **Partial — execution foundations exist; full behavioral parity is planned**
> Maintainer roles: TeamCity, Results/Artifacts, Logs, Scheduling, Agent API/SDK, REST, UI, Test Steward
> Related architecture: [D14, D17, D23-D28, D31-D33](../ARCHITECTURE.md)

## Product contract

Vivarium must replace the owner's TeamCity build infrastructure without losing the behavior used to
configure, run, observe, investigate and retain builds. Executing the same commands is insufficient:
build history, individual test history, requirements explanations, live/historical logs, dependencies,
parameters, publication, permissions and automation are acceptance surfaces of the product.

The target implementation uses C# definitions, one initial Host with logical in-process Chiral nodes,
PostgreSQL business persistence, the existing reverse-connected Agent execution seam, and REST/SSE plus
React/Workbench. These are implementation choices beneath the behavioral contract, not substitutes
for it. Current code still uses SQLite and YAML; a requirement below is not a claim of implementation.

This catalog is the durable acceptance contract. The reproducible census, per-configuration mappings,
exact source revisions, verification state and migration decisions belong in
`.workspace/workstreams/teamcity-migration/`. New discoveries extend that census/ledger and this
catalog when they establish a durable requirement. No feature disappears merely because an exporter
cannot read it or a recent build did not exercise it.

## Baseline and evidence classes

The live reference is TeamCity **2026.1.3**, build **222742**. The initial census contains 97 build
configurations, including paused configurations, 17 VCS roots and three authorized connected Agents.
Current metadata, effective inherited settings, dependencies and recent runtime evidence were read
through `@eyeauras/teamcity-cli`; original Kotlin definitions and generated `.kts` patches were also
inspected. Official documentation currently describes 2026.2: it explains established semantics, but
newer features are not presumed present on the reference server.

Evidence labels below mean:

- **O:** observed in live metadata/runtime or the inspected source drivers.
- **C:** required core TeamCity behavior or an explicit owner requirement, even if not in a sample.
- **U:** requires additional source export, settings-value, producer or consumer verification.

These labels describe requirement provenance, not delivery or completion. Every requirement remains
open until its API, persistence, UI/CLI and applicable execution evidence pass. A known baseline failure
must remain visibly failed; a migration cannot manufacture green results by omitting tests or steps.

## 1. Projects, configurations and authoring

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-CAT-01 | Stable Project/Build Configuration IDs, hierarchy, names, descriptions, ordering and paused state. | Rename/move a configuration without losing historical URLs or dependency identity; retain paused entries. | O |
| TC-CAT-02 | Shared templates/defaults with explicit inheritance and local overrides. | Resolve inherited .NET, Squirrel, Docker and NuGet settings to the same effective values and ordered steps. | O |
| TC-CAT-03 | Browse project/configuration build history, branches, current problems and latest running/finished state. | Project -> configuration -> old build stays navigable after later settings edits. | C |
| TC-CAT-04 | Branch, outcome, date, number, tag, pin and Agent filters, deterministic paging and stable deep links. | Find a retained release or failed build without scanning every page in the browser. | C |
| TC-DEF-01 | C# is the new versioned definition language, producing a validated immutable effective plan. | The same pinned source/SDK/dependencies produce the same semantic plan; an invalid candidate retains the last good active definition. | C |
| TC-DEF-02 | UI/CLI can inspect and change the migrated configuration fields through attributable Git changes. | Editing requirements or a parameter produces a reviewable C# diff and a validated new revision, never a hidden DB override. | C |
| TC-DEF-03 | Expose source, effective inherited settings, validation errors and configuration revision/diff. | Explain which template/function/default supplied a step or parameter and which revision a run actually used. | O/C |
| TC-DEF-04 | Separate trusted settings revision, source revisions and runtime parameter overrides. | An untrusted source branch cannot change privileged steps or secret bindings; rerunning an old build does not silently use new settings. | C |
| TC-DEF-05 | Bound and isolate definition evaluation; normal C# helpers and existing Cake drivers remain usable. | A hanging/throwing definition cannot hang the controller; a supported command driver runs without rewriting its business logic. | C |

## 2. Source control and changes

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-VCS-01 | Multiple VCS roots, exact commits, branch/default-branch selection and recorded checkout rules. | Source selection made at admission remains fixed after new commits arrive while the build queues. | O |
| TC-VCS-02 | Agent checkout, private Git, submodules and the selected clean/incremental workspace policy. | Build EyeAuras/PoEBane with required private submodules on the intended Windows or Linux machine. | O |
| TC-VCS-03 | Pending/included changes with authors, messages, affected paths, revisions and dependency changes. | Build Changes identifies what changed since the appropriate prior run and links to exact source evidence. | C |
| TC-VCS-04 | Branch history and explicitly selected historical-revision/custom builds. | Run a chosen revision; historical runs do not falsely resolve a newer branch's current failure. | C |
| TC-VCS-05 | Git tags/labels and external change links preserve configured publication policy. | Existing release labeling selects the intended root/revision and cannot tag an unrelated current HEAD. | O/U |

### Trigger behavior

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-TRG-01 | VCS change detection, branch/path filters, quiet period and queue optimization preserve configured semantics. | A burst of changes produces the intended source-based run rather than silently changing the current trigger's batching behavior. | O |
| TC-TRG-02 | Schedules preserve timezone, pending-change policy and restart/DST/missed-fire behavior. | PoEBane nightly verification runs on Europe/Berlin time even with no new commit; restarting the server does not duplicate it. | O |
| TC-TRG-03 | Finish-build triggers preserve upstream identity, success-only and branch conditions independently of snapshot dependencies. | SDK publication fires from its configured producer, without creating a second accidental chain or publication. | O |
| TC-TRG-04 | Pausing/enabling configurations and triggers is visible; coexistence has one chosen active owner. | Importing or diff-importing an enabled source trigger does not start duplicate automation. | O/C |
| TC-TRG-05 | Persist trigger cause and deduplication identity with exact selected inputs. | A polling/webhook retry or controller crash cannot create an unaccounted duplicate run. | C |

## 3. Parameters, counters and step inputs

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-PAR-01 | Project/template/configuration/run/Agent/predefined parameters have explicit resolution precedence and origin. | Inspect configured, overridden and resolved values without exposing secrets. | O |
| TC-PAR-02 | Text, boolean, select, required/validated, hidden, read-only and prompted run inputs. | Preserve PoEBane check selections and alpha/stable choice; invalid manual input queues nothing. | O |
| TC-PAR-03 | Environment/system/predefined paths, build IDs, numbers, branch and step inputs reach existing drivers correctly. | A translated `%...%` reference supplies the intended value/path on both Windows and Linux. | O |
| TC-PAR-04 | Durable counters, formatted build numbers and authorized runtime build-number updates. | GenerateBuildNumber and CLink/Vivarium version stamping survive retries/restarts and retain the original release counter. | O |
| TC-PAR-05 | Output parameters and dependency parameter references become durable before consumers run. | An upstream AppVersion selects the downstream archive and published version from that exact chain. | O |
| TC-PAR-06 | Step conditions can inspect typed inputs and earlier step results, with default/even-if-failed/always policies. | Selected PoEBane checks and post-failure diagnostics retain their current conditions; force cancellation does not run forbidden cleanup. | O |

## 4. Agents, requirements and compatibility

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-AG-01 | Connected, authorized, enabled, activity, health and maintenance facts remain independently visible. | A disabled connected Agent completes current work but receives none; quarantine never appears idle. | O/C |
| TC-AG-02 | Preserve reported facts, custom parameters, environment/tool discovery and stable historical Agent identity. | Later rename/tool changes do not rewrite which Agent/environment executed an old build. | O |
| TC-AG-03 | Explicit and runner-implied requirements compose with inherited requirements using AND. | A .NET/Docker step cannot be assigned to an Agent lacking its tools even when explicit OS conditions pass. | O/C |
| TC-AG-04 | Preserve observed `exists`, `equals`, `does-not-equal` and `contains`, including missing/empty value semantics. | Missing property and empty property produce the same match decisions as TeamCity, especially negative operators. | O |
| TC-AG-05 | Core requirement families include absence, prefix/suffix, negative containment, numeric/version comparisons and regex matching. | Exported expressions are validated against compatibility cases; unsupported regex syntax is reported instead of silently changing meaning. | C/U |
| TC-AG-06 | Configuration -> compatible/incompatible Agents and Agent -> compatible/incompatible configurations, with per-condition reasons. | Show actual value, expected condition, source of requirement and why laptop-g15/SWIFT3/Cvat qualify or fail. | C |
| TC-AG-07 | Separate static compatibility, current availability, project/pool authorization and resource/lease eligibility. | A matching busy/offline Agent means waiting for capacity, not an incompatible expression; permissions are independently enforced. | C |
| TC-AG-08 | Pools/allowed-job policy, draining and shared machine/resource restrictions are inspectable and effective. | The current `any` Agent policy imports faithfully; later selected-job restrictions cannot be bypassed by a matching requirement. | O/C |
| TC-AG-09 | OS/session/native tools, free disk and real resource observations participate in readiness. | Enforce PoEBane's 60-GB requirement; desktop tests run under a real intended interactive session; missing metrics are unknown, not zero. | O |

Observed operators are the first migration gate, not permission to silently discard the remaining
core requirement vocabulary. TeamCity's AND semantics do not imply an invented OR extension. A regex
engine change from Java to .NET needs explicit compatibility handling for expressions actually used.

## 5. Queue, lifecycle and execution

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-QUE-01 | Manual/API/triggered runs enter a durable visible queue with actor, branch, parameters and reason. | Queued work and its exact inputs survive a controller restart. | O/C |
| TC-QUE-02 | Queue explains waiting for Agent, dependencies, lock, concurrency, disabled capacity or maintenance. | A reusable configuration with no currently matching Agent can wait with a reason and deadline rather than being silently dropped. | C |
| TC-QUE-03 | Authorized cancel, reorder/priority and per-configuration concurrency are durable and attributable. | A serialized publisher never overlaps its own release, even after controller restart. | O/C |
| TC-QUE-04 | Queue-wait deadline is separate from step/build timeout and cannot extend accidentally on reconnect. | Expiry does not launch work and has a distinct failure reason. | C |
| TC-QUE-05 | Running, releasing/finalizing, cancelled, success, failure and infrastructure problems preserve their meanings. | A received test result does not release a machine whose workload is still ambiguous. | C |
| TC-QUE-06 | Rerun-original, run-current, retry failed stages and custom-run choices are explicit where exposed. | Select exact original inputs versus current inputs; retries do not accidentally repeat publication. | C/U |
| TC-EXE-01 | Ordered process, shell/PowerShell, .NET, NuGet and C# steps preserve cwd, args, env, timeout and execution policy. | All seven observed runner types have a verified equivalent; path quoting and Unicode survive translation. | O |
| TC-EXE-02 | Container-wrapped ordinary steps and Docker build/tag/save/push preserve separate semantics. | Run CLI.Factory/Workbench containers and publish WebServer images with intended mounts, UID, IPC, env and output files. | O |
| TC-EXE-03 | Persistent workspace/cache policy, clean checkout, step cleanup and stale-artifact prevention are explicit. | A failed test/compile cannot publish reports or executables left by an earlier run. | O |
| TC-EXE-04 | Graceful and force stop, acknowledgements, bounded deadlines and process-tree/containment recovery preserve honest outcomes. | Stop a hung workload without claiming the machine is free until termination is proven; its peer stays usable. | O/C |
| TC-EXE-05 | Agent/controller reconnects, restart and lost acknowledgements cannot duplicate execution or lose accepted results. | Repeat assignment/result messages without running or publishing twice. | O/C |
| TC-EXE-06 | Predefined step conditions, nonzero exits, timeouts, test failures and configured build-problem rules affect final status correctly. | A process exit alone cannot hide an ingested test failure or a configured build problem. | O/C |
| TC-EXE-07 | Runner features such as assembly/version stamping and workspace/process cleanup retain their effects. | Replace AssemblyInfo/swabra behavior with an explicit equivalent and verify resulting binaries and workspace. | O |
| TC-EXE-08 | Configured failure conditions include relevant error output, report failures, run duration and statistics thresholds. | Preserve the effective source policy and explain which condition failed; do not treat every stderr line as a failure by default. | C/U |

## 6. Build chains and dependencies

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-CHN-01 | Validate a dependency DAG and create durable graph instances with exact upstream/downstream run IDs. | Reject cycles/missing references before partial queue creation; render the running and historical chain. | O/C |
| TC-CHN-02 | Source revision synchronization and per-edge failure/incomplete/cancel policy are preserved. | A failed required upstream cannot launch a supposedly verified publication. | O |
| TC-CHN-03 | Suitable-build reuse and explicitly disabled reuse remain per-edge policies. | CLink reuses a compatible verified result; EyeAuras/PoEBane NO-reuse edges create the required fresh work. | O |
| TC-CHN-04 | Same-Agent affinity and shared checkout/release-directory dependencies are preserved or explicitly migrated with evidence. | EyeAuras GenerateBuildNumber/Compile and Prepare release/Publish cannot race a different machine or deadlock. | O |
| TC-CHN-05 | Parameter overrides and exact output references flow through the selected chain. | Inspect which upstream version and artifact digest a downstream release actually consumed. | O |
| TC-CHN-06 | Composite/aggregate status, dependency browsing and failed-stage retry do not flatten child evidence or duplicate side effects. | Navigate from a failed release stage to its exact producer and retry only the authorized scope. | C/U |

## 7. Artifacts, release inputs and storage

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-ART-01 | Publication supports observed include/exclude, destination mapping, rename and archive creation rules. | EyeAuras SDK/E2E archives and excluded scratch reports have the same logical contents and paths. | O |
| TC-ART-02 | Dependencies support exact/same-chain and configured successful/pinned/branch selectors plus archive extraction. | `archive.zip!**` stages the intended upstream files with clean-destination behavior, without rebuilding them. | O/C |
| TC-ART-03 | Browse directories and archives, inspect metadata and download individual files or a selected archive. | Old screenshots, TRX/JUnit, symbols, native packages and release ZIPs remain discoverable through the build. | C |
| TC-ART-04 | Verified hashes/sizes, resumable bounded transfer and build-scoped authorization protect bytes. | A hash alone grants no download; incomplete upload cannot become a successful manifest entry. | O/C |
| TC-ART-05 | Failure/cancel artifact capture follows the configured policy and explains unavailable outputs. | Diagnostics from a failed stage survive; no report is shown as passed just because no file was produced. | C |
| TC-ART-06 | Pins, tags, release retention, dependency references and cleanup policy preserve retained evidence. | PoEBane keep-all releases and every referenced retained artifact survive GC and backup/restore. | O/C |
| TC-ART-07 | Resolve TeamCity package-feed/indexer consumers and preserve package/symbol availability through Depot or an accepted equivalent. | SDK consumers restore their required versions after TeamCity is unavailable. | O/U |
| TC-ART-08 | Report attachments and HTML/trace/video outputs remain accessible through appropriate protected viewers/downloads. | Open Playwright evidence and screenshots without executing uploaded HTML as trusted controller-origin code. | O/C |

## 8. Build logs

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-LOG-01 | Live follow and historical reads use the same durable sequenced stdout/stderr and lifecycle record. | Reopen an old failed build after server restart and see its retained output, not an empty in-memory buffer. | C |
| TC-LOG-02 | Step/block hierarchy, parallel flow IDs, severity and timestamps remain navigable. | Collapse a Cake block, isolate a failing step and follow interleaved output without mixing unrelated tests. | O/C |
| TC-LOG-03 | Search, severity/step filters, expand/collapse, wrap and absolute/relative timestamps are available. | Find an earlier error without downloading the entire log or loading it all into browser memory. | C |
| TC-LOG-04 | Full/raw download and stable line/offset anchors support external tools and shared failure links. | A test/problem link opens the relevant region of the exact retained log. | C |
| TC-LOG-05 | Service-message escaping, chunk splitting, flow lifecycle and progress/metadata effects are compatible. | PoEBane blocks, buildNumber, setParameter and importData work across chunk boundaries and reconnect. | O |
| TC-LOG-06 | Bounded buffers/spools and explicit durable gaps distinguish missing output, retention and corruption. | Flood output or interrupt a connection without starving heartbeat/cancel or silently pretending the log is complete. | C |
| TC-LOG-07 | Terminal output completeness is a durable barrier, separate from process exit and status events. | Final result cannot imply a complete log while final output is still unpersisted. | C |
| TC-LOG-08 | Redaction/access policy applies before persistence and to imported log views/downloads. | A publication secret cannot leak through a rendered log, raw export or diagnostic exception. | C |

## 9. Tests and individual test history

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-TST-01 | Ingest the actual .NET/TRX and JUnit producers used by the fleet, preserving their native evidence. | CLink/Vivarium TRX and PoEBane nextest, Node, Vitest and Playwright JUnit create visible occurrences. | O |
| TC-TST-02 | Stable logical test identity is distinct from a build occurrence, attempt, parameter case and suite. | Repeated names in different assemblies/frameworks do not merge; import preserves long TeamCity test IDs exactly. | O/C |
| TC-TST-03 | Build Tests lists totals, passed/failed/ignored/skipped/not-run and running/partial state with paging/search/filter/sort. | EyeAuras's many-thousand-test builds are usable without a giant unpaged UI payload; missing data is not success. | O/C |
| TC-TST-04 | Test details include native outcome, duration, failure message, stack trace, stdout/stderr and available attachments. | Navigate from a failed test to its screenshot/report and relevant build-log region. | C |
| TC-TST-05 | An individual test has a retained execution timeline/table and duration/outcome history. | Filter its runs by configuration, branch, period and Agent/environment; open any occurrence's build. | C |
| TC-TST-06 | Cross-run comparison exposes new failures, recurring/first failure, last success and already-fixed evidence. | A newer relevant run resolves a current failure; a historical/custom run does not falsely make it fixed. | C |
| TC-TST-07 | Flaky/rerun attempts remain visible and distinguish same-source instability from a changed environment. | A failed-then-passed attempt is retained rather than overwritten with a single green row. | C |
| TC-TST-08 | Live progress and final authoritative report reconciliation cannot double-count tests. | A streamed test event and its later report produce one explainable occurrence/attempt set. | C |
| TC-TST-09 | No-report, malformed, duplicate, truncated, cancelled and partially completed reports have explicit states. | A successful process with a missing required report cannot silently report zero successful tests. | O/C |
| TC-TST-10 | Imported and native test histories can be browsed together only under an explicit verified identity mapping. | Preserve the old timeline without guessing that two display names identify the same test; show unmapped history separately. | C |

JUnit is required for the current fleet, not a future optional Rust feature. PoEBane's driver already
generates distinct JUnit dialects and sends `importData`. Determine how each existing .NET runner reports
tests; a server-side runner may currently supply reporting not visible in a simple command translation.
Service-message-only test producers, if found, require an explicit retained ingestion contract or a
verified report-producing adapter before their configuration can migrate. Ignoring their events fails
parity; an exporter limitation does not waive this requirement.

History identity must include the source/framework/assembly or binary and parameterization dimensions
needed by the actual producer. Display name or culture alone is not identity. Store original IDs as
strings at JSON/UI/import boundaries: observed test IDs exceed JavaScript's exact integer range.

## 10. Failure investigation and triage

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-TRI-01 | Build problems have stable identities, occurrences, first/new/repeated state and log links separate from test failures. | Distinguish checkout failure, timeout, process exit and a failed assertion. | O/C |
| TC-TRI-02 | Project/configuration current-problems views include child projects and filter test/build problems. | Find currently broken work and compare it with an older build's historical raw outcome. | C |
| TC-TRI-03 | Assign/reassign/resolve investigations with responsible identity, reason, scope and history. | Import and operate the existing assigned test investigation without mapping source user ID directly to an unrelated Vivarium account. | O |
| TC-TRI-04 | Mute/unmute supports reason, scope and configured expiry/when-fixed policy with permissions/audit. | A muted test is still visibly failed in raw evidence; configured effective build policy and mute-at-run/current-mute are distinct. | C |
| TC-TRI-05 | Failure resolution/flaky/mute projections never rewrite original outcomes or erase attempts. | Current triage changes do not retroactively falsify an old build's evidence. | C |

No active mutes were present in the bounded live snapshot; an investigation was present. Zero current
mutes is not a reason to remove the core mute workflow or its import semantics.

## 11. Statistics, publications and operational actions

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-STA-01 | Retain build/queue/stage durations, artifact sizes, test counts/durations and relevant custom statistics. | Compare a build's time/size/test evidence with prior retained runs without losing numeric precision. | O/C |
| TC-STA-02 | Build/test history graphs and branch/configuration selection link every point to its run. | Explain whether a slower test or stage began after a source/environment change. | C |
| TC-STA-03 | Preserve configured Agent CPU/memory/performance monitoring and stage timing outputs. | Existing perfmon/cargo-timings/benchmark evidence remains available; unsupported metrics are explicit. | O |
| TC-STA-04 | Preserve native structured numeric values and their units separately from display formatting. | Large counters, byte sizes and subsecond durations survive export/import/API/UI round trips. | O/C |
| TC-REL-01 | Publication consumes the exact verified artifacts/source/version and preserves selected channel/dry-run mode. | Alpha/stable, npm, NuGet, SSH and Docker destinations do not quietly rebuild different bytes. | O |
| TC-REL-02 | Shared locks, concurrency limits and one active publisher owner prevent release races during coexistence. | CLI.Factory's release lock and PoEBane's serialized release remain effective across restarts. | O |
| TC-REL-03 | Publication retries require explicit external-effect reconciliation and retained receipts. | A lost response cannot silently repeat a package push, tag or SSH deployment under a new version. | C |
| TC-REL-04 | Preserve release tags, notes, symbols, retention and links to exact producer/verification runs. | Trace a published package back to source, settings, tests and hashes after TeamCity shutdown. | O/C |
| TC-OPS-01 | Build metadata actions include tags, pin/unpin, comments and permission-controlled status/number operations where used. | A retained release remains findable and protected; a mutation records its actor and original execution evidence. | C/U |
| TC-OPS-02 | Current credentials, project-tree roles and run/edit/publish/admin permissions are independently evaluated. | A build viewer cannot obtain secrets or activate a publisher; imported user references do not grant live roles. | C |
| TC-OPS-03 | Inventory notifications, status publishing, webhooks and external CI/API callers before retiring their endpoints. | Each discovered consumer is migrated or explicitly disposed, never assumed absent because no job feature listed it. | U |
| TC-OPS-04 | Backup/restore, retention, quotas and disk admission preserve runnable service and retained evidence. | Restore on an isolated Host and read old builds/logs/tests/artifacts with the correct permissions. | C |

## 12. API, UI, import and coexistence

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-API-01 | REST/CLI cover configuration, run/cancel, queue/Agent compatibility, tests/history/triage, logs, artifacts and import. | UI and automation perform the same authorized operation without direct DB access. | C |
| TC-API-02 | Every collection/long-running read is bounded, filterable and resumable where required. | Large tests/history/logs work with cursors and durable live reconnection rather than unbounded polling. | C |
| TC-API-03 | Build tabs expose Overview, Changes, Log, Tests, Artifacts, Parameters, Dependencies and Statistics when applicable. | Navigate among exact linked resources; empty/forbidden/partial/stale states are honest and deep-linkable. | C |
| TC-API-04 | Preserve compatible environment/service-message contracts or migrate each caller explicitly in the same family. | PoEBane's `TEAMCITY_VERSION` guard cannot silently disable test imports, output parameters or log blocks under Vivarium. | O |
| TC-API-05 | Existing TeamCity REST/file/feed clients use the required compatible facade over shared native application behavior. | Unmodified teamcity-cli and other inventoried clients work after normal server/credential setup; wire compatibility is independently verified under D33. | C/U |
| TC-IMP-01 | Import all retained build history, logs, tests and artifacts, including paused/archived/removed configurations. | Old test timelines and release downloads work while TeamCity is unavailable. | C |
| TC-IMP-02 | Stable installation/kind/source-ID mappings preserve original relationships, identities and provenance. | Repeated import cannot create duplicate builds or grant historical users/Agents live access. | C |
| TC-IMP-03 | One initial import supports explicit repeatable one-way diff-import throughout coexistence. | Add new, skip unchanged, and apply whole-entity keep-Vivarium/use-TeamCity policy without field merging. | C |
| TC-IMP-04 | Atomic verified snapshot replacement, checkpoints and source-absence handling preserve target history. | Interrupted replacement retains a complete prior snapshot; source cleanup never deletes imported target data. | C |
| TC-IMP-05 | Final delta, selected conflict dispositions, integrity manifests and restored reads gate shutdown. | Every retained source object is accounted for; intentional kept-target differences are not called source-identical. | C |
| TC-IMP-06 | Source-unavailable evidence, unmapped test identities and exporter limitations are visible acceptance items. | Never replace a missing source log with a successful empty file or invent an original C# revision. | C/U |

### TeamCity REST wire parity

The [REST compatibility contract](teamcity-rest-compatibility.md) is mandatory alongside native
`/api/v1` parity. It defines unchanged-client acceptance, the endpoint/variant census and shared-service
mutation rules. Both reads and writes are in scope.

| ID | Required observable behavior | Acceptance example | Basis |
|---|---|---|---|
| TC-WIR-01 | Compatible REST roots, context paths, versions and discovery. | Configure an existing client's server address and connect without rewriting its paths. | C |
| TC-WIR-02 | Compatible method/path/query/body contracts for inventoried read and mutation families. | Existing job/Agent/build/test/triage/file commands operate on Vivarium. | C |
| TC-WIR-03 | Locator nesting, escaping, `$base64`, branch and lookup semantics. | Non-simple branch names and nested selectors return the intended resources. | O/C |
| TC-WIR-04 | Compatible `fields`, DTO wrappers, scalar/collection shapes and exact IDs. | Existing models decode nested projections without losing long test IDs. | O/C |
| TC-WIR-05 | JSON/XML/plain-text request and response negotiation. | XML-only and scalar endpoints retain their expected media types and encoding. | O/C |
| TC-WIR-06 | Offset/count/lookup pagination and usable continuation links. | Existing clients enumerate all matching results without native-cursor changes or premature termination. | O/C |
| TC-WIR-07 | Compatible statuses, errors, empty results and mutation replies. | Clients distinguish rejection/not-found from empty success; no synthetic success for unsupported actions. | C |
| TC-WIR-08 | Compatible credential transport with the same current native authorization and masking. | A facade request cannot bypass project/fleet permissions or read a protected parameter. | C |
| TC-WIR-09 | Compatible file/log/artifact metadata, paths, archives and streamed bytes. | Follow returned links and retrieve the intended protected content with an unchanged consumer. | C |
| TC-WIR-10 | Compatible create/replace/patch/delete and immediately observable mutation semantics. | Legacy configuration writes commit/activate through Git and are reflected by native reads. | C |
| TC-WIR-11 | Shared identity, historical provenance and valid links across both APIs/import. | Native and compatible reads refer to one build; links work without the old server. | C |
| TC-WIR-12 | Executable unchanged-client conformance and route/variant accounting. | Run real existing CLI flows plus isolated mutation/download/error cases; route registration alone is not parity. | C |

## Workload acceptance profiles

Core navigation, permissions, history, logs and lifecycle apply across the product. Each configuration
also needs its actual execution/profile gates. A generated per-configuration map selects candidate
gates; human review confirms exact defaults, expressions, scripts, artifacts and producers.

| Profile | Concrete representative workflows | Additional gates |
|---|---|---|
| Basic source CI | Vivarium Compile, CLI.Factory Verify | C# catalog, private checkout, exact revision, VCS trigger, durable output, real test producer, native/container toolchain |
| Verified build chain | Vivarium Compile -> Release; CLink Compile -> SmokeTest -> PackageRelease | Source sync, suitable-build reuse, NO-reuse, exact artifacts/output values, failure and restart/cancel windows |
| Linux container / npm | CLI.Factory and Workbench | Ordinary Docker-wrapped steps, Node/.NET/Playwright, private dependencies, release lock, dry-run/publish and registry effects |
| Windows -> Linux Docker | WebServer-linux and service publishers | Cross-Agent exact-byte transfer, archive extraction, Linux Docker build/tag/push and release metadata |
| Desktop protected release | EyeAuras, MicSwitch, PoeEye, ReClass | Native/licensed tools, protection, packaging, same-Agent/shared paths, interactive tests, SSH/channel publication |
| Rust / mixed verification | PoEBane Basic/Pro/nightly, Rust SDK, XAC | Toolchains, caches, disk, typed conditions, native architectures, nextest/JUnit, benchmarks, scheduled/interactive evidence |
| Historical data | Retained runs from every family | Test identity/history, triage, logs/artifacts, imported provenance, diff-import, retention and restore |

A family migrates only when its configuration behavior **and** its human diagnostic workflow pass.
Record unsupported/incomplete behavior openly; a deliberate deviation requires an explicit owner
decision before claiming parity. Do not translate only the easy active jobs and lose paused history,
inherited settings or rare release paths.

## Required end-to-end evidence

1. Select a configuration and branch, inspect requirements, see per-Agent reasons and queue a custom
   run with the same parameter choices as its TeamCity counterpart.
2. Run the intended exact source on the intended machine/container; inspect ordered steps, live
   nested logs, test progress and dependency/artifact provenance.
3. Deliberately fail a test; view its error/output/attachments, historical statuses and duration,
   first failure, last success, flaky/attempt information, investigation and mute policy.
4. Restart controller/Agent or lose the connection while running, publishing output or acknowledging
   completion. Preserve one execution, honest status, logs or explicit gaps, and independent peer work.
5. Open the finished build after restart; filter/search/download logs, browse artifacts, inspect
   parameters/changes/dependencies/statistics and follow all test/log/source links.
6. Import historical evidence, repeat diff-import under both conflict choices, restore a backup, then
   perform the same history/test/log/artifact workflows without contacting TeamCity.
7. Compare each migrated family's effective definition and selected results against its frozen source
   baseline; retain known failures and explain intentional differences instead of treating fewer
   executed tests as success.

No implementation percentage is inferred from the number of translated files or passing parsers.
The parity ledger closes only with these consumer-visible and failure-path observations.

## Primary references and remaining discovery

- [Build Results](https://www.jetbrains.com/help/teamcity/build-results-page.html): build tabs,
  linked changes/dependencies, test details and retained history.
- [Build Log](https://www.jetbrains.com/help/teamcity/build-log.html): structured output, filters,
  relative time and downloads.
- [Agent Requirements](https://www.jetbrains.com/help/teamcity/configuring-agent-requirements.html)
  and [conditions](https://www.jetbrains.com/help/teamcity/requirement-conditions.html): explicit/
  implicit compatibility, operators and AND composition.
- [Test/problem triage](https://www.jetbrains.com/help/teamcity/investigating-and-muting-build-failures.html)
  and [test REST concepts](https://www.jetbrains.com/help/teamcity/rest/manage-tests-and-build-problems.html):
  stable tests versus occurrences, investigations, muting and history.
- [Service messages](https://www.jetbrains.com/help/teamcity/service-messages.html): escaping, flows,
  tests, output parameters, status and artifact/report declarations.

The live evidence snapshot is not a full history/byte export. Exact hidden settings values,
server-only templates, all .NET producer modes, historical test/log attachments, notification/API/feed
consumers and structured historical-log export remain explicit discovery work. Some typed CLI test
list/occurrence calls returned HTTP 400 while scoped test lists and string-ID lookup worked; record
the exporter gap rather than treating it as absent TeamCity functionality. Runtime-derived profiles
also cannot prove that a job with no tests in its latest run never reported tests historically.
