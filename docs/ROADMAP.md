# Roadmap

Ordered so that every phase ends with something usable. Decision references (D1…D18) point into
[`ARCHITECTURE.md`](ARCHITECTURE.md).

## Phase 0 — Design & skeleton *(current)*

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
- [ ] Reconnect / ghost re-adoption and result-fencing scenarios exercised by tier-2 tests (D4).
- [ ] Payload portability smoke tests on real machines: NUnit/MTP self-contained exe + TRX on all
      three OSes, cross-published macOS ad-hoc signing, nextest archive + `--workspace-remap` (D3).
- [ ] Hyper-V checkpoint spike on the dev machine: Standard vs Production types, automatic
      checkpoints off, static memory; measure apply latency for 1 and 5 concurrent 4 GB reverts (D5).
      One afternoon that de-risks the core latency claim before any driver code exists.

## Phase 1 — TeamCity core: agents, queue, builds (no hypervisors yet)

The control host from day one — already useful with zero VMs: enroll the machines you have (physical
included) and run the matrix across them.

- Bootstrap + `setup.ps1` / `setup.sh` one-liners with enroll token and pinned certificate; enroll →
  **unauthorized** → authorize (§8.4, D4).
- Agent status axes, parameters, requirements/compatibility, central auto-upgrade (D2, D8, D14).
- `ControlPlane` API (§5) + `viv login` / `viv run` (fail-fast on unmatchable cells, queue-wait
  timeout, `--only <cell>` rerun) / `viv exec --agent <name>`; panel login: admin token → cookie (D4).
- An enroll one-liner that actually runs on stock machines — explicit fingerprint argument, `curl.exe
  -k` initial fetch, single-use tokens (§8.4).
- Build configurations with **named cells only** (`agent:` expressions, per-cell `rid:`), queue,
  builds with steps; clean policies `clean-workdir` / `none` + `on_fail: keep` for persistent
  machines; failure taxonomy + reconnect fencing (D9, D4).
- Archive payloads (modes/symlinks, traversal hardening) → self-contained NUnit → TRX adapter at
  build end (D3). No live service-message streaming yet — step status + heartbeats suffice.
- Panel: Agents / Queue & Builds / live log tail; a plain per-build results table (the full matrix
  view with history comes later).
- Normative UX: `vivarium.yaml` + `viv run` per [`walkthrough.md`](walkthrough.md) §0–§6 (D17).
- Panel Downloads page (portable agent/CLI zips from the controller's bundled store), `vivarium-agent
  enroll`, and the `viv agent push` dev flow (D19).
- Tagged releases via GitHub Actions: self-contained per-RID zips + SHA256SUMS (D19, DEVELOPMENT.md).

Deliberately deferred out of Phase 1 (recorded in D14/D18, not abandoned): parameter axes, `exclude`,
scenario lists, `repeat`/pass-rate cells, live service messages, `clean: reboot` (drags in autologon
credentials), drift canaries.

## Phase 2 — Pristine: the Hyper-V provider

- Hyper-V driver: pool-VM create / own checkpoint / revert / destroy / console endpoint (D5, D7).
- Pool provider with auto-authorized agents — TeamCity cloud-profile logic (D15); `viv exec --image`.
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
