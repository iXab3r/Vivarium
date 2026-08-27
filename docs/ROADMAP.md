# Roadmap

Ordered so that every phase ends with something usable. Decision references (D1…D13) point into
[`ARCHITECTURE.md`](ARCHITECTURE.md).

## Phase 0 — Design & skeleton *(current)*

- [x] Architecture doc, prior-art survey, this roadmap.
- [ ] Solution skeleton: `Vivarium.Contracts` (proto), `Vivarium.Controller` (gRPC AgentHub + blob
      store + SQLite + empty panel), `Vivarium.Agent`, `Vivarium.Bootstrap`, `Vivarium.Cli`.
- [ ] The `Session` loop alive end-to-end with an agent running on the same machine — protocol proven
      before any VM exists.

## Phase 1 — TeamCity core: agents, queue, builds (no hypervisors yet)

The control host from day one — already useful with zero VMs: enroll the machines you have (physical
included) and run the matrix across them.

- Bootstrap + `setup.ps1` / `setup.sh` one-liners; enroll → **unauthorized** → authorize (§8.4).
- Agent status axes, parameters, requirements/compatibility, central auto-upgrade (D2, D8, D14).
- Build configurations, queue, builds with steps; clean policies `reboot` / `clean-workdir` / `none`
  (D5); failure taxonomy in the data model (D9).
- Self-contained NUnit payload → TRX adapter; TeamCity service messages for live test progress (D14).
- Panel: Agents / Queue & Builds / live logs; matrix view over enrolled machines.
- `vivarium.yaml` + `viv run` matrix UX — normative walkthrough in
  [`walkthrough.md`](walkthrough.md) (D17).
- `viv run`, `viv exec --agent <name>`.

## Phase 2 — Pristine: the Hyper-V provider

- Hyper-V driver: clone / revert (memory checkpoints, D5) / start / stop / console endpoint / MAC (D7).
- Provider-spawned auto-authorized agents — TeamCity cloud-profile logic (D15); `viv exec --image`.
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
