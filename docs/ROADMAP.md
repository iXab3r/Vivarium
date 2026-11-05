# Roadmap

Current delivery — 2026-11-05: selected PILOT is accepted and delivered on main by [PR #167](https://github.com/iXab3r/Vivarium/pull/167), merge `a2aa5b309963eec0d20cb7ca00d161b1753c38b0` (2026-11-04T23:57:04Z; November 5 in Europe/Berlin). See the [canonical final delivery record](../.workspace/workstreams/coherent-refactor/pilot-final-delivery-20261105.md). Selected 34/34 uses four current targeted PASS results (three normative renewals plus explicit-selection evidence) and 31 qualified-reuse normative/SDK rows. Root checks retained: 728 passed/11 skipped, build zero warnings/errors. One unchanged CLI0.5.6 heavy chain completed Prepare1/Windows2/Release3, version1297/release434492192; observer raw FAIL and read-only continuation PASS remain separate. No full API denominator or parity claim; seven wire declarations remain unmaterialized and legacy five stale/twenty blocked remain separate. Independent aggregate and Security/Adversarial PASS at `6b8cc40bc03645242776570b97967032ed69bd1e` against prior main base `2f053e608637e04628334c797ad33609a9c29e61`. Zero unresolved due PILOT implementation/review debt. After verified main delivery, #117/#116/#79/#80/#81/#118/#78 were closed; milestone 9 is CLOSED with zero open and nine closed issues (#82/#83 remain unchanged legacy closures). Global API-complete remains false; SS-RUN R2 #134/#135/#136 and D46/#166 remain unaccepted. The merged tree equals delivery candidate `452f0770c0feed663c7f0fd1a0c43f47db7acc6a`; reviews transfer by unchanged src/tests/tools identity, not a fresh review of this documentation commit.


Historical delivery evidence: [2026-11-04 D52 PILOT checkpoint](../.workspace/workstreams/coherent-refactor/t3personal-pilot.md#current-checkpoint--2026-11-04-not-milestone-closure) records fresh unchanged CLI 0.5.6 Prepare 1 / Windows 2 / Release root 3 SUCCESS, version1297/release434492192 and subsequent read-only acceptance. The original EF observer FAIL remains raw; root8/version918 remains historical provenance. Selected PILOT/main delivery is now complete as recorded above. D34/D35 R0 was delivered by PR #165; the planning status below is historical.

Ordered so that every phase ends with something usable. Decision references (D1…D40) point into
[`ARCHITECTURE.md`](ARCHITECTURE.md).

**Refactor planning, 2026-10-31:** the owner requested a frozen current-state checkpoint and an
iterated, Depot-informed replacement plan before implementation. The [draft coordinated plan](../.workspace/workstreams/coherent-refactor/plan.md)
contains milestone gates, pending decisions, scenarios and issue routing. It does not yet supersede
accepted decisions or claim that the new implementation exists. Resolve P0 before starting it.

Owner clarifications: D34 accepts Depot authentication/superuser and TeamCity authorization 1:1;
D35 accepts Chiral configuration ownership, PostgreSQL business state and Depot's actual
TypeScript/Vite Workbench UI. RF-10 permits each verified milestone to merge separately into main.
These target decisions are recorded; their runtime replacement is not implemented.
The [current workstream handover](../.workspace/workstreams/coherent-refactor/handover.md#current-state--2026-11-04)
separates the existing product, accepted replacement, delivered P0 tooling and remaining gates.

D36 brings resource/identity/state/mutation/protocol REST decomposition into P0 before implementation.
The first new-stack local server + real Agent lab is R0.1; unchanged CLI smoke execution is R0.2,
with restart/cleanup proof at R0 exit. The first real product is then the
[T3Personal PILOT](../.workspace/workstreams/coherent-refactor/t3personal-pilot.md): full
Prepare/Windows/Release and default publication through UI, direct /app/rest and unchanged CLI.
The owner chose breadth-first levels across all configurations/surfaces, not Prepare-first or serial
UI/REST/CLI completion. Near-term order remains accepted P0 → R0 → PILOT. Windows, coherent chain
artifacts, used Release trigger/coexistence and default publication are early due here. Other families
and history/diff-import get later milestone allocation before implementation/after predecessor closure.
R1–R6 retain broader full-scope owners and cumulative gates, not unrelated blanket prerequisites.
Pull only the needed shared production contributions forward; full IAM/editor/all-runners/history do
not block this targeted product. R2 completes general source/catalogue behavior; R5 is cumulative
conformance. See the [concrete local gates](../.workspace/workstreams/coherent-refactor/local-lab.md)
and [early REST breakdown](../.workspace/workstreams/coherent-refactor/rest-breakdown.md).

D38 makes executable REST compatibility tests the first P0 execution slice: P0.REST.1 freezes
reference-backed cases, P0.REST.2 implements strict WireMock.Net replay and the shared conformance
tests, and P0.REST.3 publishes actual Vivarium RED/GREEN/blockers before structural replacement.
Before bulk expansion, P0.REST.2 proves a minimal deterministic production-code composition with
scripted external actors/timers, isolation and measured maintenance/runtime cost. WireMock handles
HTTP replay; it does not become a second scheduler or a required server for every test.
See [the mandatory TDD/acceptance gate](design/teamcity-rest-test-first.md). Broad coverage should
produce hundreds/thousands of meaningful variants; no mock-only GREEN or guessed failing oracle.
UI technology aligns with Depot while useful TeamCity/current-Vivarium composition remains.

D39 binds every milestone to contract → implementation → REST-acceptance tasks and its parent
closure gate. [Block register](../.workspace/workstreams/coherent-refactor/api-blocks.tsv) and
[API-ledger delivery](design/api-ledger-delivery.md) define mechanical API-complete, focused tests,
final independent aggregate and Security/Adversarial review and zero debt before the next milestone.
The [offline accounting foundation](design/api-ledger-accounting.md) now validates ledger/TRX joins,
provenance and explicit reuse, with 47 accounting controls. Historical P0 assessment: 449 headings,
15 known cases, five product RED, ten product-blocked and zero GREEN; variant denominator and
compatibility percentage remain unknown, API-complete false. Full source allocation/due-scope freeze
and #42 baseline acceptance remain open. No implementation milestone or final review PASS is claimed.

D40 makes [coherent subsystems](design/subsystems.md) the implementation units. REST-contract and
acceptance subtasks belong to their subsystem slice; milestone blocks aggregate their progress.
The full discovery ledger retains one primary owner and collaborator links per operation. Track
subsystem lifecycle/integration and API-complete separately, then milestone/global status. These
logical owners do not imply extra services/nodes or delayed early REST obligations.

D37 makes Build/Agent semantics a continuously tested TeamCity superset. The
[business AC](acceptance/README.md) are stable implementation-independent requirements;
[model/wire/component evidence](design/testing-model.md) supports them. Every milestone preserves
its implemented base suite and relevant extension neutrality; this check does not wait until R5.

## Immediate priority — replace TeamCity without functional loss (D32)

The accepted build-product contract is [TeamCity behavioral parity](design/teamcity-parity.md).
The [REST compatibility contract](design/teamcity-rest-compatibility.md) is also required: existing
`teamcity-cli` and other clients must work through the TeamCity-shaped API without client rewrites.
C# catalog definitions, in-process Chiral composition and PostgreSQL are the new target; current
SQLite/YAML execution remains transitional. This priority governs the next unimplemented slices;
the older Phase-1 sequence below records foundation progress and remaining scope, not a competing
instruction to finish AgentExplorer/provider work first.

1. Establish the Chiral/PostgreSQL/C# build foundation while preserving queue, identity, ownership,
   cancellation, audit and result guarantees. Prove a real source-checkout Compile with durable logs,
   reports and artifacts.
2. Deliver reusable projects/configurations, inherited/typed run parameters, VCS branches/revisions,
   requirements and per-Agent compatibility reasons, trigger/queue policy and operational build UI.
3. Deliver dependency graphs, exact upstream artifact/output selection, build counters/numbers,
   resource locks and verified publication for the existing real build families.
4. Deliver .NET/TRX and the currently used JUnit producers, individual test history/duration/details,
   failure comparison, investigation/mute workflows, structured log search/tail/download and retained
   statistics. These are migration gates, not optional polish after command execution.
5. Migrate each family with its API/UI/operator workflow and known failure behavior proven. Preserve
   paused configurations and rare release paths until explicit disposition.
6. Import all retained TeamCity build/test/log/artifact history; support repeatable one-way diff-import
   with whole-entity keep-Vivarium/use-TeamCity choices during coexistence. Close final-delta,
   standalone history-read and backup/restore gates before shutting TeamCity down.

The checklist in [the parity contract](design/teamcity-parity.md) owns stable acceptance IDs; the
workstream inventory/ledger owns per-configuration verification. No stage is closed by translating
files alone. Complete slices should supply usable diagnostics early; the order above is not permission
to postpone all logs/tests/UI until the final migration. VM/image and expanded AgentExplorer work
remain later priorities unless an inventoried build requires them.

Implement compatible read and mutation endpoints alongside the corresponding native domain slices.
Freeze client method/path/variant coverage, then run unchanged-client conformance on isolated fixtures;
neither matching route names nor native OpenAPI coverage alone closes D33.

## Phase 0 — Design & skeleton *(complete)*

- [x] Architecture doc, prior-art survey, this roadmap.
- [x] Solution skeleton: `Vivarium.Contracts` (proto), `Vivarium.Controller` (gRPC AgentHub + blob
      store + empty panel; SQLite lands with Phase 1 persistence), `Vivarium.Agent`,
      `Vivarium.Bootstrap`, `Vivarium.Cli` (stub).
- [x] The `Session` loop alive end-to-end with an agent running on the same machine — enroll →
      authorize → payload → step → logs → artifacts → result, as the first members of the tier-2
      in-process protocol suite (D20).
- [x] Hosted GitHub Actions originally proved the three-OS baseline; CI/CD now lives in the TeamCity
      Compile → Release → Publish chain with deterministic portable artifacts (DEVELOPMENT.md).
- [x] Pinned-TLS + `Welcome` handshake proven in the local loop (D4, §5); blob endpoints reject
      anonymous callers and lying hashes.
- [x] Reconnect / re-adoption and result-fencing scenarios exercised by tier-2 tests (D4): a build
      survives a kicked connection, its result and logs arrive via the new session, duplicate results
      are idempotent. (Session-supersede fencing — discarding results after an INFRA re-dispatch —
      arrives with the Phase 1 scheduler.)
- [x] Payload portability smokes in CI (D3): the NUnit/MTP self-contained exe runs bare and emits TRX
      on all three OSes; a **Linux-published** `osx-arm64` binary runs on macOS (the SDK ad-hoc signs
      cross-platform — the review-flagged `Killed: 9` risk did not materialize); a nextest archive
      runs on a simulated target with only the nextest binary + source tree + `--workspace-remap`.
      Reference payloads live in `samples/`.
- [x] Hyper-V checkpoint spike (D5): ~1 s per revert cycle, 4.7 s for five concurrent; apply lands in
      `Saved`, resume → `Running`; `.vmrs` = full assigned RAM; Production silently falls back to
      Standard on OS-less guests — the pin is confirmed necessary.
      Results: [`docs/spikes/hyperv-checkpoints.md`](spikes/hyperv-checkpoints.md).

## Phase 1 — physical-agent control plane: TeamCity + AgentExplorer foundations *(current)*

The control host from day one — already useful with zero VMs: enroll the machines you have (physical
included) and run the matrix across them.

Foundation now implemented:

- [x] SQLite-backed Agent records and hashed enrollment/Agent credentials; authorization,
      enablement, names, last-seen facts, and reported parameters survive controller restarts (D4,
      D7, D8).
- [x] TeamCity's independent connected / authorized / enabled / idle-or-building states, atomic
      reconnect replacement, heartbeat expiry, and a protected live Agents panel with central
      authorize, unauthorize, enable, disable, rename, delete, and enrollment-token actions (D8).
- [x] Agent-reported facts and operator-owned custom parameters are persisted separately, merged
      deterministically for compatibility matching, and centrally editable without racing assignment;
      the selected name and both parameter maps are copied into immutable build history (D8, D14).
- [x] One-build-per-agent ownership and explicit, idempotent cancellation: stop requests survive
      session reconnects and controller restarts; graceful stop and separately authorized force stop
      are durable and finish as `CANCELLED` when termination is proven. Assignments, cancellation
      intent, and terminal results are durable, while disabling does not interrupt current work (D4,
      D14, D31).
- [ ] Complete the D31 responsiveness hardening tracked in
      [`design/agent-lifecycle-recovery.md`](design/agent-lifecycle-recovery.md): native workload
      containment, durable output-gap evidence, controller-to-Bootstrap recovery for a wedged Agent,
      resource governance, and native cross-platform/two-Agent fault evidence. The active-work
      journal, bounded graceful/force stop, acknowledgements/deadlines, health/quarantine, priority
      messaging, bounded memory, and in-band durable Agent restart are implemented.
- [x] Durable TeamCity-style FIFO Build Queue with requirement matching, independent eligibility
      axes, incompatible-head bypass, exact-session assignment acknowledgements, result
      acknowledgements, restart recovery, and the protected Queue & Builds panel (D4, D8, D14).
- [x] Bounded durable reconnect leases: a lost owning session has one non-extending grace window;
      matching re-adoption clears it, while expiry finishes Build history as `INFRA` but quarantines
      and retains ambiguous runtime occupancy until positive recovery evidence arrives (D4, D9, D20,
      D31).
- [x] The provider-facing post-rollback readiness barrier: an Agent is not reusable until a newer
      idle session reports no running build (D5).
- [x] Atomic, request-idempotent `ControlPlane` submission plus durable snapshot watching, blob
      discovery, agent listing/authorization, and separate agent / submit / admin token scopes (D4).
- [x] Strict Phase-1 `vivarium.yaml` parsing with named cells, per-cell RID and queue timeout,
      deterministic template expansion, selection via `--only`, and fail-fast payload and static
      compatibility validation (D14, D17).
- [x] `viv-cli login` with TOFU confirmation followed by exact certificate pinning, and `viv-cli run` with
      deterministic payload dedup/upload, atomic matrix submission, reconnecting transition-only
      status watching, `--no-wait`, and CI-friendly exit codes (D3, D4, D17).
- [x] Durable queue-wait deadlines: a 30-minute controller default or per-configuration override is
      persisted as an absolute deadline; claim/dispatch are fenced at the boundary and pre-execution
      expiry is an atomic `INFRA` result visible through the API and panel (D9, D14).
- [x] Deterministic payload ZIPs preserve Unix modes and symlinks; Windows-created Unix payloads mark
      only declared payload-local step programs executable. Creation and agent extraction reject root
      escapes, traversal, duplicates, type conflicts, pivots, DOS device names, and platform aliases
      (D3).
- [x] Agent step environments expose stable build, workdir, results, and matrix-cell values; the
      results directory is ensured before each step (D3, D14).
- [x] Centralized durable result details: recent matrix builds link to per-cell outcomes, step results,
      and ordered artifact manifests; protected build-scoped downloads verify matrix/cell ownership,
      and the same artifact metadata is exposed additively through `WatchBuild` (D3, D14).
- [x] Atomic, idempotent matrix cancellation through `ControlPlane.CancelBuild`, `viv-cli cancel`, and
      the protected parent build page: queued children and claims finish together, running children
      retain durable ownership as `CANCEL_REQUESTED`, and the first reason survives retries/restarts
      (D4, D14).
- [x] The first public read-only `/api/v1` slice: authenticated system discovery, searchable and
      cursor-paged Agent and audit resources, matrix-build detail/history, durable FIFO queue reads,
      RFC 9457 Problem Details, conditional ETags, principal/filter/sort-bound cursors, and a
      deterministic OpenAPI document that excludes transitional panel/blob/gRPC surfaces (D24, D28).
- [x] Additive AgentHub v1 negotiation, independent capability advertisement, durable credential and
      connection generations, bounded cross-platform static host facts, restart-safe observation
      provenance, and typed `/api/v1/agents/{id}/facts` reads. Legacy Agents remain visible and may
      finish adopted work but are drained from new assignments (D22, D28).

Phase 1 delivery sequence (completed and remaining):

1. [x] Establish the transport-independent management kernel: versioned SQLite migrations, the minimal
   append-only `audit_events` journal, request actor/correlation context, and one authorization
   evaluator beneath the existing ControlPlane, panel, and blob boundaries. Preserve legacy token
   scope without widening it (D26, D27). The kernel now records legacy login/logout, enrollment,
   Agent administration, build submission, and cancellation while keeping automatic lifecycle work
   out of the journal.
2. [x] Add the read-only `/api/v1` and deterministic OpenAPI surface for system health, Agents, builds,
   and queue state, with shared Problem Details, authorization filtering, and cursor semantics (D24,
   D28).
3. [x] Add capability/version negotiation and canonical typed connect-time `system.*` host facts, then
   expose them through Agent REST reads with explicit freshness and legacy-agent behavior (D22, D28).
4. [x] Initialize the managed-local Git control repository and last-known-good reconciler, then move
   one Agent desired-setting mutation through commit-before-activate, optimistic concurrency, and the
   same audit path (D23, D29). The first schema is intentionally narrow:
   `.vivarium/agents/{id}.yaml` owns only `spec.enabled`, exposed through
   `/api/v1/agents/{id}/settings` GET/PUT; other desired settings remain later work.
5. [x] Move object-scoped blob staging and build submit/watch/cancel to REST/SSE, migrate the live CLI
   build flow, and add the first durable bounded TRX result projection. The transitional ControlPlane
   remains frozen as a compatibility adapter until its removal gate (D3, D24).
6. Complete first-run administration and TeamCity/fleet RBAC, then expand the TeamCity catalog and
   durable AgentExplorer operations (D22, D26-D28).
   - [x] Durable local claim/resume/abandon, setup-only REST, atomic managed-local User +
     `SYSTEM_ADMIN` activation, private password credential, named panel login, built-in role floors,
     Git RoleBindings, and explicit restart-safe Superuser recovery.
   - [ ] Groups, service accounts, PATs, custom roles, project-tree inheritance, general user/role REST,
     setup/recovery UI and local CLI, and removal of legacy credential adapters.
   Dynamic process/network/environment refresh waits for policy, fleet authorization, operation
   persistence, and observation fencing.
7. **Partial:** complete deployment and release UX, then port the proven flows to TypeScript/EyeAuras Workbench.
   The D30 per-Agent central upgrade core is implemented: exact Server-release package import, authenticated
   delivery, pre/post-handoff cancellation semantics, durable drain/restart/probation/two-sided
   commit/rollback history, bounded singleton child supervision, strict local package evidence,
   exact prior/deadline binding, durable failure quarantine, bounded session outboxes, supervised-
   bootstrap capability gating, skew-safe watchdogs, CLI, restart recovery, two-Agent isolation, and real bootstrap success/rollback
   process evidence. Remaining work is installer/stamped archives,
   fleet rollout/channels, release signing/packaging, previous-version/Windows gates, and the TypeScript UI
   (D2, D19-D21, D25, D30).

Static typed facts deliberately precede the first Git-backed mutation because they are observed state,
not desired configuration. Remote commands and file operations do not move forward with the read-only
inventory slice; they require the later durable-operation, lease, cancellation, RBAC, output, and audit
contracts. The checklist below records the remaining acceptance scope; it does not override this order.

- Extend the implemented managed-local Git mutation/reconciliation gateway beyond Agent
  `spec.enabled` to the remaining desired settings and properties. The existing foundation already
  provides validated commit-before-activate, last-known-good projection, optimistic concurrency,
  audit linkage, and secret rejection; remote/review repository modes remain planned (D23, D29).
- Extend the canonical `/api/v1` management surface beyond its implemented Agent/build/queue/audit
  reads, Agent enablement mutation, object-scoped blob staging, build submit/cancel, and resumable build
  SSE. Setup/recovery REST and the initial Git-backed User/built-in RoleBinding evaluator are also
  implemented. General identity/RBAC management, runtime operations, remaining configuration mutations,
  and detailed result resources remain. The live CLI build flow now consumes REST/SSE; the gRPC ControlPlane is frozen
  transitional compatibility only (D24, D28).
- TypeScript + EyeAuras Workbench panel as a clean replacement for the current Blazor prototype. Vendor the
  reviewed core/TypeScript/router built packages with exact source commit, license, notice, and reproducible
  sync metadata; serve static assets from Kestrel and consume REST/SSE only. The shell uses a narrow
  TeamCity/AgentExplorer/Administration switch rail plus one expandable context pane; Agents is a
  dedicated collection route and every Agent has a separate detail route with Agent-local tabs (D25).
- Extend the negotiated capability and typed static-facts foundation with AgentExplorer dynamic
  inventory: safe on-demand environment, processes, TCP/UDP endpoints with owning process,
  freshness/partial-error semantics, and shared lease visibility. Files and Commands remain explicit
  placeholders until their capabilities ship (D22, D28).
- TeamCity Project and Build Configuration catalog over Git-backed definitions, including ordered
  steps, requirements, parameters, VCS bindings, immutable definition/source revisions, and compatible/
  incompatible-agent explanations (D14, D17, D23).
- Depot-style Identity/OpenIddict OIDC and normal installation-user login; TeamCity role/permission
  IDs, editability, project/group inheritance and REST behavior 1:1, persisted in PostgreSQL (D34).
  Replace custom setup/recovery/password/Git-RBAC paths with verified framework composition; migrate
  existing users and coarse admin/submit consumers under an explicit disposition. Native AgentExplorer
  extension actions keep distinct permission IDs without changing TeamCity grants.
- Durable structured audit journal and bounded/redacted diagnostic, build, and AgentExplorer output with
  correlation/idempotency/Git revision linkage (D27).
- Bootstrap + `setup.ps1` / `setup.sh` one-liners with enroll token and pinned certificate; enroll →
  **unauthorized** → authorize (§8.4, D4).
- [x] Per-Agent central launcher-driven upgrade core: immutable/rehash-verified packages,
  authenticated delivery, durable busy-Agent drain and handoff fence, multi-phase health/two-sided
  commit confirmation, post-handoff rollback with exact-prior proof, bounded status history, REST/CLI,
  restart recovery, bounded singleton child supervision, and peer isolation (D2, D30). Fleet rollout policy/channels
  remain below.
- Fleet/group canary and rolling orchestration over the same D30 operations; controller startup may
  import its exact release catalog but must never silently restart the fleet. Rollout policy selects
  scope and pace; it does not select arbitrary package bytes independently from the Server release.
- `viv-cli exec --agent <name>` as a durable AgentExplorer operation over REST plus AgentHub, with authorization,
  lease/fencing, cancellation, bounded output, and audit. It is not a Build or `ControlPlane.Exec`
  extension (D22, D24, §9).
- An enroll one-liner that actually runs on stock machines and authenticates installer bytes *before*
  execution using a trusted SPKI pin or independently verified package digest; the enroll token also
  authenticates the fetch. The rejected `curl -k ... | sh` shape cannot be repaired by validation
  inside an already replaced script (§8.4, D21).
- Complete persistent-machine clean-policy execution (`clean-workdir` / `none`, `on_fail: keep`) and
  expand the implemented bounded NUnit/MTP TRX projection with REST/UI result presentation, JUnit,
  build problems, and cross-platform golden evidence. Reconnect and queue expiry already produce
  durable `INFRASTRUCTURE_FAILED`; TEST/CRASH normalization, dumps, and automatic INFRA retries remain
  to implement (D3, D4, D9).
- Distinguish step `always` from `even-if-failed` during cancellation so post-stop diagnostics can run;
  `on_fail: keep` is parsed and transported but still needs controller/provider cleanup semantics.
- Close the remaining D30 bootstrap freeze evidence: bad/truncated download, interrupted activation,
  Windows real-process success/rollback, and D21 authenticated installer bytes. The numbered design
  and authenticated post-authorization manifest/package path are implemented.
- Add a capability/version handshake and real previous-release compatibility suite before promising
  rolling upgrades. Current protobuf evolution is additive, but old outcomes, cancellation/assignment
  ACKs, and terminal-result ACKs are not operationally bidirectional across arbitrary versions (D2,
  D4, D20).
- Move Agent names and custom-parameter desired state behind the Git/REST mutation path. Platform-
  accurate typed static inventory is implemented, while persistence, matching, central panel editing,
  and immutable build snapshots for the earlier parameter model already exist (D8, D16, D23).
- Live service-message parsing/streaming remains deferred; step status + heartbeats suffice for the
  Phase-1 core (D14).
- TypeScript panel: port the implemented agent/build/result views, then expose the durable TRX-derived
  per-test projection through public REST and add live log tail (plain durable build/cell details and
  artifact downloads exist in the transitional Blazor panel; the full test × scenario view with
  history comes later).
- Finish the normative UX in [`walkthrough.md`](walkthrough.md) §0–§6: install/enroll one-liners and
  public TRX presentation remain; `vivarium.yaml`, the REST/SSE-backed `viv-cli run`, raw result/artifact
  presentation, and durable internal TRX projection are implemented (D17).
- Panel Downloads page (portable Agent/CLI zips from the controller's bundled store), `viv-agent
  enroll`, and fleet rollout UI. `viv-cli agent upgrade <agent-id>` always deploys the Agent component of
  the running Server release; there is no production package-publication or package-selection command
  (D19, D30).
- [x] Deterministic portable release candidates and guarded GitHub Release publication through
  TeamCity Compile → Release → Publish. Signing, previous-release compatibility, and additional native
  platform evidence remain release-quality gates (D19, DEVELOPMENT.md).

Deliberately deferred out of Phase 1 (recorded in D14/D18, not abandoned): parameter axes, `exclude`,
scenario lists, `repeat`/pass-rate cells, live service messages, `clean: reboot` (drags in autologon
credentials), provisioning `expected-reboot` resume (requires a durable step cursor), drift canaries.

## Phase 2 — Pristine: the Hyper-V provider

- Hyper-V driver: pool-VM create / own checkpoint / revert / destroy / console endpoint (D5, D7).
- Pool provider with auto-authorized agents — TeamCity cloud-profile logic (D15); AgentExplorer
  `viv-cli exec --image` under the same Agent lease (D22, D28).
- Image adoption of enrolled VMs (§8.4); the `pristine` clean policy end-to-end.
- Crash dumps, screenshot-on-fail, keep-on-fail / snapshot-the-corpse (D12).
- Hyper-V implementation references: AutomatedLab, fdcastel/Hyper-V-Automation (see prior-art).

## Phase 3 — Image factory

- Declarative recipes in git (§8.2) with `manual` steps.
- Provision jobs with the **seal** epilogue (D6); `ImageVersion` registry with lineage.
- Drift detection (D11) + health-check canaries and maintenance jobs (D13).

## Phase 4 — More platforms

- Linux guests: same agent, `linux-x64` publish; extend native payload coverage over the JUnit
  normalization already required for the current nextest/Node/Vitest/Playwright CI migration (D32).
- QEMU/KVM driver for a dedicated Linux host.

## Phase 5 — macOS

- Mac mini node with Tart driver (clone-per-run instead of memory snapshots).

## Later / nice-to-have

- Embedded web console (noVNC-style) instead of external RDP/VNC clients.
- Interactive terminal into guests (ConPTY + stdin channel over the agent session).
- Screen video recording of jobs; per-cell artifact bundle in the matrix (video + step-synced logs).
- Pause-on-failure with live takeover in the browser (openQA "developer mode"); failure-label
  carry-over across runs of the same test × scenario.
- Trend/history views over the matrix.
- Unattended base-image builds: embedded autounattend generation + UUP-dump media automation.
- Multi-host image distribution as OCI artifacts with delta pulls (Tart/Anka model).
- Cloud machine provider for short-living instances (Azure first) — same Acquire/Release seam (D15).
- Pristine for physical machines: PXE re-imaging or disk-restore integrations; WoL/IPMI power
  management (D16).
- Firecracker-style microVM fast path for Linux-only corpora.
