# Spike: Hyper-V Standard-checkpoint revert latency

*2026-08-27 · script: [`spikes/hyperv-checkpoint-spike.ps1`](../../spikes/hyperv-checkpoint-spike.ps1) ·
host: Windows 11, 64 GB RAM, NVMe, a working dev machine (not idle) · de-risks D5 before any driver code.*

## Method

Gen2 VMs with **no OS** (firmware idles at "no boot device"), differencing disks off one parent — the
sealed-parent → pool-VM shape of D5/§8.3. Static memory, `CheckpointType Standard`, automatic
checkpoints off, no NIC. A Standard checkpoint is taken of each *running* VM, then the pool revert
cycle is measured: `TurnOff → Restore-VMCheckpoint → Start`.

## Results

**5 VMs × 2 GB static:**

| Operation | Time |
|---|---|
| Create 5 VMs + diff disks | 14.6 s (one-time pool creation) |
| First cold start, all 5 | 11.3 s |
| Checkpoint (CHECKPOINTING phase), per VM | 1.19–1.27 s |
| Single revert cycle (turnoff + apply + resume) | **0.92 / 1.04 / 1.36 s** |
| Concurrent revert, all 5 (wall clock) | **4.71 s** (turnoff 0.66 + apply 1.46 + resume 2.59) |

**1 VM × 4 GB static:** checkpoint 1.7 s; revert cycles 0.98 / 1.02 / 1.22 s, one outlier 2.53 s under
host load. Latency barely moved from 2 GB → 4 GB on this hardware.

## Findings

1. **The D5 claim holds with margin.** A full revert-to-live-agent cycle is ~1 s per VM here; five
   concurrent reverts complete in under 5 s. The docs keep 2–5 s as the planning number for real
   guests on shared hosts.
2. **The conveyor states are exactly as designed**: apply lands the VM in `Saved`, `Start-VM` resumes
   it to `Running`. The pool cycle is TurnOff → Apply → Resume, three fast operations.
3. **`.vmrs` = full assigned RAM, always** — 2048.0 MB and 4096.0 MB byte-exact, even for an OS-less
   guest that never touched its memory. The §13 pool disk budget (≈ RAM × pool size per image
   version) is confirmed, not hypothetical.
4. **The Production-checkpoint trap is real.** On a VM without guest VSS, `CheckpointType Production`
   *silently* created a **Standard** checkpoint (fallback); `ProductionOnly` failed outright. On a
   real guest with integration services the fallback never triggers — Production would "succeed" with
   **no memory state**, silently degrading every revert into a cold boot. The driver must pin
   `CheckpointType Standard` explicitly (D5).
5. **Caveats.** OS-less guests barely dirty RAM: resume (~0.5 s even at 4 GB) suggests lazy loading
   and/or sparse content, and real guests with working sets will resume slower — these numbers are
   the optimistic floor, to be re-measured in Phase 2 E2E with real images. Host load produces
   outliers (one 2.5 s cycle); expect jitter on hosts that double as workstations.

## Verdict

Pool-VM pristine (D5) is implementable on Hyper-V with the pinned settings, and the latency budget is
real. Next hypervisor question for Phase 2 is operational, not conceptual: checkpoint-chain hygiene
across thousands of revert cycles and the seal pipeline's merge-to-standalone-VHDX cost.
