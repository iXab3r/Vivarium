# TeamCity exit assessment

Date: 2026-10-31. Scope: the owner's build infrastructure at https://build.eyeauras.net.
Target direction supplied by the owner: C# instead of Kotlin, EyeAuras.Chiral logical nodes in one
process initially, PostgreSQL, and the newer composition approach used by EyeAuras.Depot.

Subsequent owner requirements are now adopted by Architecture D32 and
`docs/design/teamcity-parity.md`: 120 stable acceptance requirements, with candidate coverage for all
97 configurations in `configuration-parity.tsv`. This original assessment remains baseline evidence;
the durable contract and current handover govern later scope and acceptance.

## Conclusion

Vivarium has a useful durable execution foundation, but it is not yet an autonomous replacement for
this TeamCity installation. The central gaps are source checkout, a versioned reusable build catalog,
chain orchestration, artifact/parameter propagation, triggers, execution secrets, durable logs and
operational UI. Chiral/PostgreSQL is a foundational migration; changing the DSL language alone does
not implement these build behaviors.

The smallest useful delivery is a real source-based build, on the new foundation, operated through
Vivarium without a client preparing and uploading the workspace. Grow that into the existing chains
and publication paths. The proposed ordered work is in `phases.md`; the closure gates are in
`ledger.md`. This report does not implement or adopt new numbered architecture decisions.

## Evidence and limits

- Live inventory came through installed `@eyeauras/teamcity-cli` 0.5.2. All top-level pages were
  checked for `hasMore`; none required another page at limit 250. Ten surfaces were read for every
  job, plus project parameters/features/versioned-settings configuration. The first 38 settings
  reads used the wrong subcommand and were all retried successfully with `config show`.
- Effective steps/parameters/requirements/dependencies include inherited metadata. Property values
  that the CLI redacts remained redacted. No credential values were requested or exported.
- Recent history is at most three builds per configuration, not a historical activity or artifact
  size audit. The collection is sequentially consistent per response, not an atomic server snapshot.
- C# / Kotlin source evidence was inspected at pinned revisions of eight repositories. The generated
  source-feature census covers 172 files. Regex hits are discovery evidence, not an authoritative
  Kotlin evaluator or a count of live configured objects. Generated TeamCity `.kts` patches must be
  included; several current jobs are defined there rather than ordinary BuildType source files.
- A complete census is not complete executable parity. Hidden inline script/property values,
  server-only templates, exact applied settings revisions, cleanup policy, credentials and consumers
  of package endpoints need per-family verification before cutover.
- No build, release, production mutation, deployment, credential extraction or product-code change
  was performed. Root build/test was not rerun for this analysis.

Raw safe CLI snapshots are local ignored files under `artifacts/teamcity-migration/`. Reproducible
scripts, `inventory.tsv`, `dependencies.tsv`, this report and the human decisions live in this
workstream. Do not commit raw snapshots or private configuration into public documentation.

## Current installation

| Fact | Observed count |
|---|---:|
| Projects, including archived | 38 |
| Archived projects | 3 |
| Build configurations | 97 |
| Unpaused / paused configurations | 81 / 16 |
| Enabled steps, including paused configurations | 352 |
| Snapshot dependency edges | 62 |
| Artifact dependency edges | 45 |
| Configurations with snapshot dependencies | 51 |
| Configurations with artifact dependencies | 39 |
| Configurations using templates | 40 |
| VCS roots | 17 |
| Registered Agent records / authorized connected Agents | 104 / 3 |

The edge counts above include enabled edges only. The generated dependency table also retains four
disabled edges: 111 rows in total, 107 enabled. All referenced upstream IDs resolve within the census.

The three real authorized connected Agents are Cvat (10), laptop-g15 (15) and SWIFT3 (2284).
Do not interpret the other 101 historical/unauthorized records as paid or usable build capacity.
Current compatibility lists have 21, 66 and 31 entries respectively; these overlap and are not
capacity allocations. Recent successful work/source requirements establish the relevant Linux Docker
and Windows workloads. Desktop/native behavior still needs execution proof under Vivarium.

Enabled runner types on unpaused jobs: command/script 132, PowerShell 41, .NET 83, Docker commands 30,
NuGet pack 1, NuGet publish 10 and C# script 1 (298 enabled steps total). Three additional ordinary
script steps have Docker-wrapper properties; they are already included in the 132 script count and
must not be double-counted as DockerCommand steps. Across paused and unpaused jobs there are 352
enabled steps. Typed adapters can lower most of these to the existing process contract.

Enabled triggers on unpaused jobs: 7 VCS, 1 finish-build and 3 schedules. Two further VCS triggers
belong to paused jobs. Source evidence includes branch filters, quiet periods, queue optimization,
nightly Europe/Berlin timezone and execution without pending changes. Preserve the actual policies;
do not activate every observed configuration automatically.

Observed requirement operators are `exists`, `equals`, `does-not-equal` and `contains`. Current
Vivarium matching accepts only equality clauses joined by AND. There are nine distinct attached
template identities across 40 configurations. Shared settings are therefore a migration requirement,
although C# functions/composition can replace template mechanics without recreating Kotlin inheritance.

## Workload families and hidden coupling

| Family | Existing behavior that the migration must preserve |
|---|---|
| Vivarium | Compile -> Release -> independent GitHub/Docker destinations; exact artifact/version reuse and four-RID packaging. A small self-hosting pilot. |
| CLI.Factory / Workbench | Linux container-wrapped Node/.NET/Playwright steps; verify-only/dry-run/publish modes; private source/tool dependencies; npm/GitHub publication. CLI.Factory release has shared write lock `cli-factory-release`. |
| CLink | C#/Cake build driver; Compile -> SmokeTest -> PackageRelease and separate benchmarks; suitable-build reuse and exact verified artifact/source selection. |
| EyeAuras App / SDK / E2E | Build-number generator; inherited .NET settings; multiple ordered publish/transform steps; LINQPad tooling; Enigma/protection and release packaging; desktop UI tests; SDK/docs/NuGet publication. |
| WebServer-linux and service publishers | Build sources/binaries on Windows; transfer ZIP contents to Cvat; Docker build/tag/save/push versioned and latest images. Names do not mean compilation already runs on Linux. |
| PoEBane | Basic/Pro compile and release; selectable test/format/static-check stages; C#/Cake owning Rust/toolchain/protection/package logic; nightly long tests, benchmarks and docs; explicit checkout directories and preserved caches; 60-GB free-disk admission. |
| Native / other desktop | Rust SDK source-free consumers, ReClass protected package, XAC native i686/x64 + C# + Companion/Automation, MicSwitch/PoeEye and shared library publishers. Preserve actual toolchains, local tools, sessions and secrets. |

Concrete coupling examples:

- EyeAuras Compile has a same-Agent snapshot dependency on GenerateBuildNumber. Publish also has
  same-Agent coupling to Prepare release, plus files under shared release directories. Current agent
  workspaces must not be replaced blindly with a fresh payload directory for every stage.
- CLink uses successful upstream reuse, while many EyeAuras/PoEBane edges explicitly forbid reuse.
  One universal "latest successful" rule would change released source and artifacts.
- Artifact rules use include/exclude, path remapping, ZIP creation and `archive.zip!**` extraction.
  A manifest of hashes alone does not implement these rules or select the upstream run.
- PoEBane publishing selects alpha/stable, is serialized, and retains all published release data.
  Its nightly jobs deliberately run even when no source changes are pending.
- Three active SDK/docs jobs have TeamCity NuGet indexer features. This is an endpoint/consumer
  dependency to investigate, not proof that every package client currently consumes that feed.
- Root-level secrets are inherited by the job inventory. Preserve required bindings with narrower
  explicit scopes rather than copying every inherited credential into every new build.

The source baseline is not entirely green. EyeAuras Compile 32598 has one failed integration test;
PoEBane LongTests 32592 and Rust SDK 32351 report process exit failures. Diagnosis was bounded and did
not infer root causes from those summaries. These are pre-existing facts, not migration defects and
not a reason to suppress failure reporting.

## Reusable Vivarium foundation

Reviewed baseline is `origin/main` at `09dd40bb8449261d54cf3bd6261b3b1d4227fee0`. The local main remains
`0999792`, 33 commits behind and clean apart from these newly created analysis materials. Product
files were read from Git objects rather than evaluating the old local tree as the current product.

Reuse the stable Agent identity/capability negotiation, reverse AgentHub stream, heartbeat and current
session fences, durable FIFO queue, acknowledged assignment/result, cancellation/recovery, raw artifact
integrity and scoped transfers, REST/SSE/CLI, TRX projection, initial administration and per-Agent
upgrade/rollback. Preserve their failure semantics while replacing persistence/composition.

Important implementation limits:

- Build submission still supplies a definition snapshot and resolved cells/steps. Project and
  configuration are strings; there is no persistent reusable project/configuration/VCS/trigger/DAG
  catalog.
- Current execution consumes payloads prepared by the caller. It is possible to run `git` as a
  process, but that is not a supported server-owned checkout/revision/credential/workspace contract.
- Matrix fan-out is not build-chain scheduling. No existing API pins dependency build outputs and
  implements the observed reuse/failure/affinity policies.
- `BuildTracker.OnLog` appends to an in-memory buffer capped at 1 MiB characters. This does not provide
  restart-safe log history or a complete production log tail. Streamed state events and raw artifacts
  must not be described as durable stdout.
- SQLite connections/types/serialized-write callbacks occur across roughly two dozen controller
  source/project files. PostgreSQL adoption is a transactional-store migration, not a package swap.
- Current equality-only matching, static step inputs and TRX projection are useful beginnings, but do
  not cover the observed requirement operators, version/output propagation or whole failure policy.

## Chiral / Depot findings

Depot `2023838` and Chiral main `41137a4` were already current with their default remote branches.
Only remote refs were refreshed; no checkout was pulled or switched. Depot uses package
`0.1.0-depot.48.1` from source `ca5e4582be6b68c4e8bb61ac97a116d30b480eb5`, not simply whatever is on
Chiral main. Its private candidate packages include restart-bound saving/CAS and validation fixes;
select an actual reviewed package API before adoption.

Depot demonstrates a single ASP.NET Host with logical nodes, Chiral InMemory, EF Core/Npgsql business
storage, private FileStorage settings, startup readiness and settings that become active after Host
restart. Its Host consumes the separately packaged Storage object API. These boundaries transfer well
to Vivarium; Depot's application identity/policy is still owned by Depot, not by Chiral.

Recommended initial ownership:

1. **Host / node configuration:** Chiral registration, schemas, private masked settings, connections,
   readiness and restart lifecycle. Candidate logical nodes are Host, Database, Identity, Build
   Automation, Agents and Storage, only where they own real configuration/resources.
2. **Build definitions:** trusted Git-owned C# and pinned SDK/dependencies. Compile/evaluate outside
   the controller process into a bounded validated immutable plan. Freeze configuration/source
   revisions and the normalized plan/hash with each run. Keep the last good activated definition.
3. **Runtime business state:** PostgreSQL owns runs, graph instances, queue, attempts, leases/fences,
   counters, output bindings, idempotency, retained events and audit. Use business transactions; a
   MassTransit message acknowledgement is not evidence that an external publication occurred once.
4. **Agent execution:** keep remote Agents distinct from in-process modules. Existing gRPC/HTTP is a
   useful secure execution/data-plane seam. Chiral discovery is not permanent Agent registration and
   must not make a machine eligible based only on a node heartbeat. Agents do not receive business-DB
   credentials merely to become Chiral nodes.
5. **Bytes and packages:** object storage serves build logs/intermediate/final artifacts with run-owned
   access and retention. Depot receives actual published NuGet/npm/Cargo/generic releases. Using
   Depot for every ephemeral log line is unnecessary; duplicating its package registry is unnecessary.

InMemory messages do not cross processes, survive restart or provide a durable scheduler. Keep one
controller process initially and use explicit application services for local operations; do not put
every method or data blob onto a bus. PostgreSQL business storage is independent of the optional
PostgreSQL messaging transport. Privileged provisioning/runtime grants remain separate if distributed
messaging is introduced later.

These recommendations intentionally refine existing decisions: SQLite/no external service, all desired
server settings/RBAC in Git, and YAML-first definitions cannot remain literally unchanged if Depot's
configuration boundaries and C# are adopted. Update those numbered decisions together with the first
implementation. C# build definitions in Git and private resource/secret settings in Chiral need one
clear ownership boundary, not two competing authoring stores. Decide RBAC's own authority explicitly.

## C# authoring and execution

Separate two contracts:

- A versioned C# **configuration program** declares projects, builds, parameters, source roots,
  dependencies, outputs, requirements and triggers. Its emitted normalized plan is data consumed by
  the engine. Ordinary functions/composition can replace template duplication.
- An optional C# **build step** executes on the selected Agent, like an existing Cake command. C#
  authoring does not require all Rust/PowerShell/Node/Docker commands to be rewritten.

Use ordinary .NET compilation and versioned packages/projects; a `.cs` file-based entry is available
on the selected .NET 10 SDK. A custom script language/interpreter is not a prerequisite. A typed DSL,
plan format, compatibility policy and constrained evaluator still have to be built. An out-of-process
evaluator protects the controller's process lifetime but is not automatically a security sandbox;
only trusted settings can execute with privileged references. Evaluation must not receive build
publication secrets, and build steps must not execute inside the controller.

Do not compile C# to today's YAML, parse it back and call that catalog support. Retain YAML submission
only as an explicit bounded compatibility path if needed; C# is the requested new authoring authority.
Keep existing provider-neutral Cake drivers. Model typed prompts/booleans/selects separately from
arbitrary C# expressions so the UI can show the same manual-run choices.

Minimum engine semantics include per-configuration counters, output parameters, declared step
conditions, AND-composed observed requirement operators, same-chain artifact selection, exact source
revision sets, failure/reuse policies, per-Agent/workspace/resource locks and idempotent trigger/run
creation. Add TeamCity stdout compatibility only for the observed useful messages and constrain its
effects. It must not grant permissions or override authenticated execution provenance.

## Acceptance and unresolved decisions

All 97 inventoried configurations must end as either executed-and-accepted on Vivarium or explicitly
retired, with paused configuration state preserved until disposition. For each migrated family:

- run from the intended Git/settings/source revisions with the expected parameter defaults/prompts;
- verify toolchains, native principal/session, checkout/cache and affinity/lock behavior;
- prove failed steps/tests and cancellation/restart remain honest and do not duplicate publication;
- retain accessible logs, tests, artifacts, exact upstream references and release identity;
- prove the replacement trigger/publisher is the sole owner and demonstrate family-level rollback.

Before complete shutdown, resolve all package/feed consumers, old API/callback users, secret binding,
retention/backup and history access. The owner explicitly selected **full import of retained TeamCity
history, logs and artifacts into Vivarium**. An independently accessible external archive alone is
insufficient. Imported history must be browsable and downloadable through Vivarium without TeamCity
being available. The complete historical/byte census, importer, initial bulk transfer, final delta and
integrity/restore gates are additional required work, tracked as G16 and in `history-import.md`.
Recent-build metadata does not establish its size, duration or completeness. Data already deleted by
TeamCity cleanup must be reported as unavailable, not reconstructed or silently counted as imported.

TeamCity and Vivarium will coexist. Perform one initial full import and support explicitly requested
repeatable one-way diff-imports: add new entities, skip unchanged, and resolve changed matches with
either the complete existing Vivarium snapshot or the complete TeamCity snapshot. No field-level or
three-way merge and no continuous/bidirectional synchronization are required. Keep stable source IDs,
record intentional keep-target differences, and preserve imported history when its source is cleaned
up. Configuration import must not implicitly enable duplicate triggers or publishers. The final delta
uses the same simple conflict policy and precedes TeamCity shutdown.

The first implementation should be a thin real Compile vertical slice. Full enterprise TeamCity
plugin parity, VM/pristine infrastructure, dynamic AgentExplorer and distributed Chiral deployments
are not required before getting financial value from moving these builds.

## Selected primary references

- [TeamCity dependency semantics](https://www.jetbrains.com/help/teamcity/configuring-dependencies.html):
  source synchronization, reuse, same-Agent and failure policy; artifact transfer is a separate edge.
- [TeamCity artifact transfer](https://www.jetbrains.com/help/teamcity/artifact-dependencies.html):
  upstream selection and same-chain consistency.
- [TeamCity service messages](https://www.jetbrains.com/help/teamcity/service-messages.html):
  escaping, output parameters, status/test/artifact metadata. Compatibility is scoped, not blanket.
- [.NET file-based applications](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps):
  standard C# compilation and package/project references on .NET 10.
- Depot: `docs/architecture/nodes.md`, `configuration.md`, `chiral-integration.md`,
  `src/EyeAuras.Depot.Host/Nodes/DepotNodeRegistration.cs`, `Program.cs` at `2023838`.
- Chiral: `docs/architecture/postgresql-transport.md`, `library-boundary.md`, `options-readiness.md`.
- Vivarium: architecture D3/D14/D17/D23/D27, `docs/design/teamcity.md`, `VivariumDatabase.cs`,
  `AgentCompatibilityMatcher.cs`, `BuildTracker.cs`, build submission REST models at `09dd40b`.
- Source-specific line evidence and full repository SHAs: ignored `source-feature-census.json`;
  live identities/edges: tracked `inventory.tsv` and `dependencies.tsv`.
