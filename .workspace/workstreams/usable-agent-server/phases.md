# Usable physical-agent server: orchestration waves

Status: Waves 0-5 complete; Wave 6 is next against the accepted order in `docs/ROADMAP.md`. The earlier deployment-first phase order
is retired; packages, installers, and bootstrap changes do not jump ahead of Git, public management,
first-run/RBAC, or the React replacement.

Every worker stream below is at least eight hours and includes focused tests, documentation owned by its
domain, and evidence. The Reconciliation Lead/integrator resource-budgets concurrency and currently
executes serially after host memory pressure; future parallel workers require disjoint path ownership.
Workers do not rewrite Git history, reset/revert shared
files, edit another stream's paths, or claim an integration gate. The integrator exclusively owns root
composition points (`Program.cs`, `VivariumControllerHost.cs`, project files when shared), numbered
architecture/roadmap reconciliation, workstream state, root verification, and commits.

## Wave 0 — freeze the management-kernel baseline — completed

The current working tree already contains the verified management-kernel slice. No feature worker edits
its stores, host wiring, security, audit, migrations, or Blazor files until this gate closes.

### Integrator work

- Preserve and integrate the existing management-kernel changes; migration 1–2 definitions and checksums
  become immutable baselines for all later migrations.
- Graduate the accepted D25 UI contract: compact application bar, TeamCity/AgentExplorer/Administration
  switch rail, resizable expandable context pane, routed main page, dedicated Agent collection, and
  stable per-Agent pages with local tabs.
- Regenerate `inventory.tsv`, keep human classification in `feature-map.tsv`/`ledger.md`, and reconcile
  every planning artifact with the authoritative roadmap.
- Freeze Wave-1 path ownership and shared composition rules.

### Review and evidence

Persistence/Migrations, Security, Logs, Admin/Superuser, UI, Docs, and Test Steward review. Run
`dotnet build` and `dotnet test` at the solution root and record exact counts. Exit only when the baseline
is internally consistent and no active worker owns overlapping dirty files.

## Wave 1 — read-only `/api/v1` and deterministic OpenAPI — completed

The public HTTP conventions are published first. The fleet and TeamCity readers may build against a small
shared contract branch/diff, but root composition waits for the common layer.

### Stream 1A — REST contract core — 8–12 hours

Ownership: new `src/Vivarium.Controller/Rest/Common/**`, `Rest/System/**`, deterministic OpenAPI support,
and `tests/Vivarium.Tests/RestInfrastructureTests.cs`.

Deliver:

- versioned `/api/v1` conventions and system resource;
- RFC 9457 Problem Details with stable machine codes;
- actor/correlation/idempotency propagation and authorization-filtering seam;
- validated cursor envelope, deterministic ordering rules, ETag/conditional-read conventions;
- deterministic OpenAPI generation and byte-for-byte repeatability test.

Reviews: Vivarium REST lead; Security, User Roles, Logs, Docs, Test Steward.

### Stream 1B — Agent and audit reads — 10–14 hours

Ownership: new `Rest/Agents/**`, `Rest/Audit/**`, read-only query additions to `AgentStore` and
`AuditEventStore`, and `AgentRestApiTests.cs`. Any migration remains integrator-owned for the wave.

Deliver the Agents collection/detail representations and audit reads over the existing management kernel,
including all four Agent status axes, stable immutable ID, observed/custom parameter separation,
freshness, sorting, pagination, authorization filtering, and restart-persistent state.

Reviews: AgentExplorer lead; Agent API/SDK, Persistence, REST, Security.

### Stream 1C — TeamCity queue/build reads — 10–14 hours

Ownership: new `Rest/Builds/**`, `Rest/Queue/**`, read-only query additions to Build/Matrix/Queue stores,
and `BuildRestApiTests.cs`.

Deliver parent/child build, queue, step, result, artifact-manifest, immutable Agent provenance, deadline,
and cancellation-state reads with stable ordering and object-level authorization filtering.

Reviews: TeamCity lead; Scheduling, Results/Artifacts, Persistence, REST.

### Wave-1 gate

Tier-1 cursor/filter/ETag/ordering tests, real-Kestrel 401/403/404/Problem Details tests, restart-persistent
SQLite reads, OpenAPI generated twice with identical bytes, then root build/test. Exit means a browser can
render every currently proven farm view through public REST without Blazor services or ControlPlane.

## Wave 2 — Agent compatibility negotiation and typed static facts — completed

### Stream 2A — additive protocol and legacy negotiation — 8–12 hours

Ownership: `src/Vivarium.Contracts/protos/vivarium/v1/agent_hub.proto`, the handshake portion of
`AgentHubService`, and new compatibility tests. No unrelated scheduler or REST edits.

Add bounded protocol compatibility, advertised capability IDs, credential/connection generations,
reported package digest, and explicit legacy behavior without removing existing fields.

### Stream 2B — platform fact collectors — 8–12 hours

Ownership: new `src/Vivarium.Agent/Facts/**`, focused Agent collection wiring, and platform-specific
collector tests.

Report canonical OS family/version/build, architecture, hostname, Agent/package version, capabilities,
observation time, and partial quality on Windows, Linux, and macOS. Do not infer a capability from OS.

### Stream 2C — persistence and REST projection — 10–14 hours

Ownership: observed-fact models, the wave's new migration content under integrator coordination,
`AgentStore` projection changes, Agent REST representation, and focused tests.

Keep observed typed facts distinct from Git desired/custom properties and preserve legacy Agent reads.

### Wave-2 gate

Agent API/SDK lead review with Platform, Scheduling, Persistence, REST, Security, and Test Steward.
Evidence includes additive protobuf audit, empty-capability legacy Agent, current Agent, reconnect,
two simultaneous independent Agents, tier-2 session coverage, and root build/test.

## Wave 3 — managed-local Git gateway and first desired mutation — completed

### Stream 3A — control repository and validation — 12–16 hours

Ownership: new `src/Vivarium.Controller/Configuration/Git/**` and focused repository tests.

Create/adopt the managed-local repository, normalized document validation, commit authorship/correlation,
optimistic base revision, and no-secret boundary.

### Stream 3B — reconciler and last-known-good projection — 10–14 hours

Ownership: new `Configuration/Reconciliation/**`, the wave's migration with integrator coordination,
and restart/recovery tests.

Implement validate-before-activate, durable applied revision, last-known-good retention, invalid-revision
diagnostics, restart recovery, and audit linkage.

### Stream 3C — first Agent desired-setting mutation — 10–14 hours

Ownership: Agent desired-policy application service, new `Rest/Agents/Configuration/**`, and mutation
tests. Do not add hidden SQLite desired authority.

Move one Agent desired setting through commit-before-activate with `If-Match`, normalized diff, resulting
commit, applied-revision state, and distinct 412/409 behavior.

### Wave-3 gate

Git/Versioning lead review with AgentExplorer, Persistence, REST, Security, Logs, and Test Steward.
Evidence: invalid revision never activates, LKG survives restart, stale base preserves the draft,
audit-to-revision correlation is exact, and repository scans find no secret bytes.

## Wave 4 — build REST/SSE, object-scoped blobs, CLI, and TRX — completed

### Stream 4A — object-scoped blob staging and access — completed

Ownership: new `Blobs/Access/**`, REST blob endpoints/application services, and cross-object access tests.

Replace presence-leaking global discovery with principal/object-bound staging grants and authenticated
assignment/result/human-download paths.

### Stream 4B — build mutations and resumable events — completed

Ownership: new `Rest/Builds/Mutations/**`, `Rest/Events/**`, build application services, and HTTP/SSE
tests. Shared store changes are predeclared with the integrator.

Deliver idempotent submit/cancel, authoritative snapshots, resumable event IDs, retention-gap handling,
and controller-restart convergence. ControlPlane remains during parity.

### Stream 4C — CLI migration and result projection — completed

Ownership: `src/Vivarium.Cli/**`, new controller `Results/**`, TRX fixtures, and CLI/result tests.

Move login/run/watch/cancel/blob flow to REST/SSE and project TRX into durable test occurrences without
discarding raw artifacts.

### Wave-4 gate

REST, TeamCity, Results/Artifacts, Scheduling, Security, Logs, and Test Steward review. Evidence includes
idempotent retries, SSE resume/gap, cancellation across restart/reconnect, cross-object blob denial,
bounded TRX projection/restart catch-up, CLI parity, and root build/test. The closed implementation gate
is recorded in `evidence.md`; cross-platform TRX producer fixtures and public result presentation remain
later result-domain acceptance, not hidden Wave-4 completion claims.

## Wave 5 — first-run administration and TeamCity/fleet RBAC — completed

### Stream 5A — resumable first-run claim — completed

Ownership: setup operation/state, its migration, local claim boundary, REST resources, and recovery tests.

Delivered v9's purpose-bound token/session/request ledger, unclaimed rotation, one-time setup exchange,
durable restart resume, local access reissue, safe pre-commit abandon, and setup-only REST resources.

### Stream 5B — role schema and evaluator — completed

Ownership: TeamCity project permissions, independent fleet/pool permissions, built-in security floor,
service-identity attenuation, evaluator tests, and no UI authorization logic.

Delivered the product-owned permission catalog and five built-in role floors, canonical Git User and
direct built-in RoleBinding documents, v10 revision-linked projections, project/fleet scope isolation,
and the shared evaluator. Groups, service accounts, PATs, custom roles, project ancestry, and general
role-management APIs remain explicit later RBAC work.

### Stream 5C — legacy migration, audit, and Superuser recovery — completed

Ownership: legacy credential transition, secret receipts/hashes, bounded recovery operation, redaction,
and permission-matrix tests.

Delivered one atomic Git User + `SYSTEM_ADMIN` setup commit, exact-revision reconciliation before v11
private credential activation, named password/cookie login with credential-generation checking,
unchanged legacy adapter scopes, and host-explicit single-use restart-safe recovery issue/exchange/revoke.
Supported local CLI commands and final legacy-token removal remain later migration UX.

### Wave-5 gate

Admin/Superuser and User Roles leads review with Security, Git, Persistence, REST, and Logs. Prove token
reveal once, interrupted setup resume, abandoned-attempt fencing, project/fleet non-escalation, recovery
audit, and that legacy credentials gain no new authority.

Closed by focused migration/administration/authorization tests plus adjacent REST/panel/Git/kernel
regressions. The scope is the first built-in-role administration path, not the complete identity/RBAC
catalog described by D26.

## Wave 6 — domain expansion and deployment freeze gate

Three independent large streams follow public management, Git, and RBAC. Execute them serially under
the reduced-memory orchestration budget; their ownership boundaries remain separate so later hosts can
parallelize them without changing the contracts.

### Stream 6A — TeamCity Project/Build Configuration catalog — 12–18 hours

Ownership: Git schema/projection for Projects and Build Configurations, TeamCity REST reads/mutations,
requirements/compatibility explanations, immutable definition/source revision, and tests.

### Stream 6B — AgentExplorer observation/operation foundation — 12–18 hours

Ownership: observation epochs, operation store, shared Agent work/maintenance lease abstraction, bounded
refresh/cancel state machines, and tests. Environment/process/network collectors do not ship before this
foundation.

### Stream 6C — numbered D2/D21 deployment refinement — core complete as D30

Ownership spans Architecture, Agent API/SDK, Platform, Security, REST, Scheduling, Persistence, and
Development docs. D30 is adopted; the bootstrap implementation proceeded only after that decision.

Adopt:

- a verified seed Agent package before persistent credentials exist;
- reuse of the protected existing Agent credential for later package reads, with a narrow
  `AgentPackageRead` permission, rather than a redundant second local secret;
- package identity `(version, RID, SHA-256)` with digest driving activation;
- same-origin authenticated manifest/package URLs and no token in a URL;
- content-addressed installed packages, atomic active pointer, last-known-good retention, rollback/quarantine;
- a non-secret launch nonce/health marker written only after the controller accepts the new authenticated
  session, exact digest, compatibility, and reconciliation;
- enrollment-proof expiry, consumption, and removal from the installed configuration.

### Wave-6 gate

Each domain lead closes its own evidence. The bootstrap gate opens only when the numbered decision,
threat model, persistence/authorization rules, compatibility plan, and process-level test plan agree.

## Wave 7 — React/EyeAuras Workbench replacement

The Workbench intake can begin once Wave-1 browser contracts stabilize; data-backed page implementation
waits for its real REST resource. No mock/private management backend is permitted.

### Stream 7A — vendoring, build, shell, routing, and auth — 12–18 hours

Ownership: new controller-owned frontend `shell/**`, reproducible Workbench vendor intake, package lock,
licenses/notices/provenance, production build, workspace switches, application bar, resizable context pane,
canonical routes, REST client, auth/anti-forgery, Problem Details, and layout preferences.

### Stream 7B — TeamCity pages — 12–18 hours

Ownership: frontend `features/teamcity/**` and feature tests. Implement Projects, Build Configuration,
Queue, Build/Results, and Matrix using compact TeamCity headers, local tabs, dense tables, and real REST/SSE.

### Stream 7C — AgentExplorer pages — 12–18 hours

Ownership: frontend `features/agents/**` and feature tests. Implement the dedicated
`/agent-explorer/agents` collection and stable `/agent-explorer/agents/{agentId}/{tab?}` page. Summary,
Build History, Compatible Configurations, Environment, Processes, Network, Metrics, Logs, and Parameters
are Agent-local URL-backed tabs.

### Wave-7 gate

UI Expert review with REST and each domain owner. Prove REST-only browser traffic, direct refresh/deep
links, loading/empty/forbidden/stale/reconnecting/partial states, rail/pane keyboard behavior, accessible
local tabs, narrow/wide layouts, production-Kestrel Playwright, licenses/bundle report, and every current
Blazor flow in the parity ledger. Remove Blazor only after named parity.

## Wave 8 — immutable packages, enrollment bundles, and durable upgrade operation

### Stream 8A — deterministic four-RID package builder/catalog — storage/import core complete

Ownership: new `Controller/Deployment/AgentPackageStore`, package models/service, build orchestration,
controller publish content, package metadata migration, endpoints, and tests. The integrator owns host and
project-file composition.

Package identity is immutable version + RID + digest + size; same-version/different-digest packages remain
distinct. Serve authenticated manifest/package bytes using the existing Agent credential and narrow
package-read permission. Ordinary retryable GET is sufficient initially; range support is optional.

### Stream 8B — stamped enrollment bundles — 12–16 hours

Ownership: `Deployment/EnrollmentBundleService`, REST application service, explicit public-controller URL
validation, token/audit integration, layout fixtures, and tests.

Generate a non-replayable secret-bearing ZIP containing bootstrap, configuration, seed package,
content-addressed active pointer, controller fingerprint, and one-time enrollment proof. Do not persist or
replay plaintext bundle bytes; issue a replacement proof after a lost download.

### Stream 8C — durable upgrade operation/shared lease — per-Agent core complete

Ownership: new operation store, Agent upgrade service, shared Agent work/maintenance lease, scheduler
drain fence, REST operation resources, migration, and race/restart tests.

Persist target package, drain one Agent without interrupting active work, block new claims atomically,
acquire maintenance lease, request restart, survive controller restart, require a newer compatible session
with the exact digest, and release/record outcome. Upgrade creation is idempotent.

### Wave-8 gate

Prove deterministic archives and modes for all supported RIDs, immutable digests, unauthorized/cross-Agent
package denial, wrong-RID/tamper/replay/expiry rejection, no plaintext proof in SQLite/audit, build-claim
versus drain fencing, controller restart in every operation phase, and two-Agent isolation.

## Wave 9 — bootstrap activation/recovery and platform installation

### Stream 9A — bootstrap active/LKG package state machine — core complete; freeze evidence partial

Ownership: `src/Vivarium.Bootstrap/**`, Agent health-marker handling, controller upgrade-session acceptance,
and real child-process tests. This is the first stream allowed to edit the frozen component after Wave 6.

Run the seed package without manifest access before authorization; later read the protected Agent
credential from the data directory. Authenticate manifest/package GETs, validate version/RID/digest/size/
archive/same-origin URL, stage immutable content, atomically change the active pointer, retain LKG, require
the matching post-reconciliation health marker, roll back once, and quarantine a repeatedly bad release.

### Stream 9B — install/status/uninstall/diagnose contract — 14–22 hours

Ownership: Agent command parsing, new platform service-integration modules, bundle templates/metadata,
platform tests, and operator docs.

Implement idempotent foreground and persistent-host installation with explicit install directory, data
directory, native principal, service/logon mode, package version, one Agent per data directory, and
preserve/delete identity choices. Package Windows service, systemd, and launchd definitions; call only the
platforms with real evidence supported.

### Wave-9 gate

Process tests cover missing/revoked credential, bad digest, truncation, traversal, interruption at every
activation boundary, crash-before-health, successful promotion, rollback, and bad-release quarantine.
The first real `osx-arm64` host completes install/status/restart/uninstall/reinstall unless the physical
target is intentionally changed.

## Wave 10 — deployment/rollout UI and two-Agent dogfood

### Stream 10A — Administration deployment and rollout pages — 12–18 hours

Ownership: frontend `features/administration/deployment/**`, route/browser tests, and no Blazor screen.

Implement target RID/mode, stamped package creation, digest/fingerprint/expiry, download/install guidance,
pending authorization, authorize/enable, package catalog, drain/upgrade/reconnect/rollback health, and audit
links over real REST operations.

### Stream 10B — two-Agent automated acceptance — 10–16 hours

Ownership: tier-2 scenario fixtures and evidence. Launch two independent Agent/bootstrap stacks with
unique identities, enrollment proofs, credentials, data directories, session generations, package state,
leases, and audit correlation. Run a concurrent two-cell matrix; drain/upgrade/fail/rollback one Agent
while the other retains capacity; restart the controller while queued/draining/restarting/health-checking.

### Stream 10C — real-host walkthrough and release evidence — 10–16 hours

Ownership: operator walkthrough, real-host evidence, diagnostics, and release-gate ledger. Exercise start
server → deploy ZIP → install → unauthorized → authorize → enable → build → upgrade → healthy reconnect.

### Milestone gate

The server is usable with one tested physical Agent and remains correct for at least two. Root build/test,
production frontend build, Playwright, protocol compatibility evidence, four-RID package evidence, two-Agent
tier-2 acceptance, and the real-host record are all current. This is not called an end-user release until
every claimed platform's real installer/upgrade evidence is present.
