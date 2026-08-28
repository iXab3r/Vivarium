# Roadmap

Ordered so that every phase ends with something usable. Decision references (D1…D28) point into
[`ARCHITECTURE.md`](ARCHITECTURE.md).

## Phase 0 — Design & skeleton *(complete)*

- [x] Architecture doc, prior-art survey, this roadmap.
- [x] Solution skeleton: `Vivarium.Contracts` (proto), `Vivarium.Controller` (gRPC AgentHub + blob
      store + empty panel; SQLite lands with Phase 1 persistence), `Vivarium.Agent`,
      `Vivarium.Bootstrap`, `Vivarium.Cli` (stub).
- [x] The `Session` loop alive end-to-end with an agent running on the same machine — enroll →
      authorize → payload → step → logs → artifacts → result, as the first members of the tier-2
      in-process protocol suite (D20).
- [x] GitHub Actions CI: build + tests on ubuntu/windows/macos — hosted runners bootstrap the farm
      that will replace them (DEVELOPMENT.md).
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
      session reconnects and controller restarts, kill the agent-side process tree, and finish as
      `CANCELLED`; assignments, cancellation intent, and terminal results are durable, while
      disabling does not interrupt current work (D4, D14).
- [x] Durable TeamCity-style FIFO Build Queue with requirement matching, independent eligibility
      axes, incompatible-head bypass, exact-session assignment acknowledgements, result
      acknowledgements, restart recovery, and the protected Queue & Builds panel (D4, D8, D14).
- [x] Bounded durable reconnect leases: a lost owning session has one non-extending grace window;
      matching re-adoption clears it, while expiry finishes the build as `INFRA` and atomically
      releases agent and queue capacity (D4, D9, D20).
- [x] The provider-facing post-rollback readiness barrier: an Agent is not reusable until a newer
      idle session reports no running build (D5).
- [x] Atomic, request-idempotent `ControlPlane` submission plus durable snapshot watching, blob
      discovery, agent listing/authorization, and separate agent / submit / admin token scopes (D4).
- [x] Strict Phase-1 `vivarium.yaml` parsing with named cells, per-cell RID and queue timeout,
      deterministic template expansion, selection via `--only`, and fail-fast payload and static
      compatibility validation (D14, D17).
- [x] `viv login` with TOFU confirmation followed by exact certificate pinning, and `viv run` with
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
- [x] Atomic, idempotent matrix cancellation through `ControlPlane.CancelBuild`, `viv cancel`, and
      the protected parent build page: queued children and claims finish together, running children
      retain durable ownership as `CANCEL_REQUESTED`, and the first reason survives retries/restarts
      (D4, D14).

Still to complete in Phase 1:

The accepted immediate delivery sequence is:

1. Establish the transport-independent management kernel: versioned SQLite migrations, the minimal
   append-only `audit_events` journal, request actor/correlation context, and one authorization
   evaluator beneath the existing ControlPlane, panel, and blob boundaries. Preserve legacy token
   scope without widening it (D26, D27).
2. Add the read-only `/api/v1` and deterministic OpenAPI surface for system health, Agents, builds,
   and queue state, with shared Problem Details, authorization filtering, and cursor semantics (D24,
   D28).
3. Add capability/version negotiation and canonical typed connect-time `system.*` host facts, then
   expose them through Agent REST reads with explicit freshness and legacy-agent behavior (D22, D28).
4. Initialize the managed-local Git control repository and last-known-good reconciler, then move one
   Agent desired-setting mutation through commit-before-activate, optimistic concurrency, and the
   same audit path (D23).
5. Move object-scoped blob staging and build submit/watch/cancel to REST/SSE, migrate the CLI, and add
   TRX result projection. Retire the transitional ControlPlane only after parity (D3, D24).
6. Complete first-run administration and TeamCity/fleet RBAC, then expand the TeamCity catalog and
   durable AgentExplorer operations. Dynamic process/network/environment refresh waits for policy,
   fleet authorization, operation persistence, and observation fencing (D22, D26-D28).
7. Port the proven flows to React/EyeAuras Workbench, then complete installers, central Agent upgrades,
   and release security/compatibility gates (D2, D19-D21, D25).

Static typed facts deliberately precede the first Git-backed mutation because they are observed state,
not desired configuration. Remote commands and file operations do not move forward with the read-only
inventory slice; they require the later durable-operation, lease, cancellation, RBAC, output, and audit
contracts. The checklist below records the remaining acceptance scope; it does not override this order.

- Git control repository and one mutation/reconciliation gateway for all desired settings and
  properties, with validated commit-before-activate, last-known-good projection, optimistic
  concurrency, audit linkage, and no secret values in Git (D23).
- Canonical `/api/v1` REST management with `/agents` as the stable fleet collection, OpenAPI, RBAC,
  idempotency, configuration/observation
  revisions, cursor pagination, async operations/cancellation, SSE, object-scoped blob access, and
  REST equivalents of the existing build/blob-discovery flows. The gRPC ControlPlane remains
  transitional while the CLI migrates (D24, D28).
- React + EyeAuras Workbench panel as a clean replacement for the current Blazor prototype. Vendor the
  reviewed core/React/router built packages with exact source commit, license, notice, and reproducible
  sync metadata; serve static assets from Kestrel and consume REST/SSE only (D25).
- Agent capability/version negotiation and AgentExplorer read-only inventory: searchable Agents,
  platform-accurate host facts, safe on-demand environment, processes, TCP/UDP endpoints with owning
  process, freshness/partial-error semantics, and shared lease visibility. Files and Commands remain
  explicit placeholders until their capabilities ship (D22, D28).
- TeamCity Project and Build Configuration catalog over Git-backed definitions, including ordered
  steps, requirements, parameters, VCS bindings, immutable definition/source revisions, and compatible/
  incompatible-agent explanations (D14, D17, D23).
- TeamCity-compatible RBAC plus separate fleet/AgentExplorer permissions; one-time local first-admin claim,
  managed-local Git baseline, durable resumable setup, and explicit bounded Superuser recovery. Migrate
  coarse admin/submit tokens without widening their authority (D26).
- Durable structured audit journal and bounded/redacted diagnostic, build, and AgentExplorer output with
  correlation/idempotency/Git revision linkage (D27).
- Bootstrap + `setup.ps1` / `setup.sh` one-liners with enroll token and pinned certificate; enroll →
  **unauthorized** → authorize (§8.4, D4).
- Central launcher-driven auto-upgrade (D2).
- `viv exec --agent <name>` as a durable AgentExplorer operation over REST plus AgentHub, with authorization,
  lease/fencing, cancellation, bounded output, and audit. It is not a Build or `ControlPlane.Exec`
  extension (D22, D24, §9).
- An enroll one-liner that actually runs on stock machines and authenticates installer bytes *before*
  execution using a trusted SPKI pin or independently verified package digest; the enroll token also
  authenticates the fetch. The rejected `curl -k ... | sh` shape cannot be repaired by validation
  inside an already replaced script (§8.4, D21).
- Complete persistent-machine clean-policy execution (`clean-workdir` / `none`, `on_fail: keep`) and
  build-end result adapters from self-contained NUnit TRX. Reconnect and queue expiry already produce
  durable `INFRASTRUCTURE_FAILED`; TEST/CRASH normalization, dumps, and automatic INFRA retries remain
  to implement (D3, D4, D9).
- Distinguish step `always` from `even-if-failed` during cancellation so post-stop diagnostics can run;
  `on_fail: keep` is parsed and transported but still needs controller/provider cleanup semantics.
- Resolve the frozen-bootstrap upgrade contract before D2 implementation: the current bootstrap has
  no authenticated manifest request and the controller maps no manifest endpoint. This requires an
  explicit numbered design refinement before changing the frozen component.
- Add a capability/version handshake and real previous-release compatibility suite before promising
  rolling upgrades. Current protobuf evolution is additive, but old outcomes, cancellation/assignment
  ACKs, and terminal-result ACKs are not operationally bidirectional across arbitrary versions (D2,
  D4, D20).
- Move custom-parameter desired state behind the Git/REST mutation path and collect platform-accurate
  reported inventory (Windows
  build + UBR, Linux distro + kernel, macOS product version) for exact configuration matching (D8,
  D16). Persistence, matching, central panel editing, and immutable build snapshots already exist.
- Live service-message parsing/streaming remains deferred; step status + heartbeats suffice for the
  Phase-1 core (D14).
- React panel: port the implemented agent/build/result views, then add live log tail and TRX-derived
  per-test results (plain durable build/cell details and artifact downloads exist in the transitional
  Blazor panel; the full test × scenario view with history comes later).
- Finish the normative UX in [`walkthrough.md`](walkthrough.md) §0–§6: install/enroll one-liners and
  parsed TRX presentation remain; `vivarium.yaml`, `viv run`, and raw result/artifact presentation are
  implemented (D17).
- Panel Downloads page (portable agent/CLI zips from the controller's bundled store), `vivarium-agent
  enroll`, and the `viv agent push` dev flow (D19).
- Tagged releases via GitHub Actions: self-contained per-RID zips + SHA256SUMS (D19, DEVELOPMENT.md).

Deliberately deferred out of Phase 1 (recorded in D14/D18, not abandoned): parameter axes, `exclude`,
scenario lists, `repeat`/pass-rate cells, live service messages, `clean: reboot` (drags in autologon
credentials), provisioning `expected-reboot` resume (requires a durable step cursor), drift canaries.

## Phase 2 — Pristine: the Hyper-V provider

- Hyper-V driver: pool-VM create / own checkpoint / revert / destroy / console endpoint (D5, D7).
- Pool provider with auto-authorized agents — TeamCity cloud-profile logic (D15); AgentExplorer
  `viv exec --image` under the same Agent lease (D22, D28).
- Image adoption of enrolled VMs (§8.4); the `pristine` clean policy end-to-end.
- Crash dumps, screenshot-on-fail, keep-on-fail / snapshot-the-corpse (D12).
- Hyper-V implementation references: AutomatedLab, fdcastel/Hyper-V-Automation (see prior-art).

## Phase 3 — Image factory

- Declarative recipes in git (§8.2) with `manual` steps.
- Provision jobs with the **seal** epilogue (D6); `ImageVersion` registry with lineage.
- Drift detection (D11) + health-check canaries and maintenance jobs (D13).

## Phase 4 — More platforms

- Linux guests: same agent, `linux-x64` publish; JUnit adapter for `cargo nextest` payloads.
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
