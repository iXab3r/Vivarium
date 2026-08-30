# Agent Lifecycle and Recovery

> Status: **Accepted**
> Implementation: **Partial**
> Maintainer roles: [Agent API/SDK Expert](../roles/agent-api-sdk-expert.md),
> [Scheduling/Coordination Expert](../roles/scheduling-coordination-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D4, D8, D14, D22, D27, D30, D31
> Evidence: `src/Vivarium.Agent`, `src/Vivarium.Controller/Agents`,
> `src/Vivarium.Controller/Builds`, `tests/Vivarium.Tests/AgentLifecycleTests.cs`,
> `tests/Vivarium.Tests/AgentResponsivenessTests.cs`, `tests/Vivarium.Tests/BuildLeaseTests.cs`,
> `tests/Vivarium.Tests/FencingTests.cs`

## Purpose

This document is the durable failure catalogue for Agent lifecycle work. It records why apparently
redundant deadlines, acknowledgements, containment boundaries, health axes, and quarantine states
exist. A future implementation may replace a mechanism, but it must preserve the invariant and the
negative evidence attached to the corresponding finding.

The primary goal is responsiveness: submitted workload code must not be able to make the controller
believe an Agent is usable when its control loop, executor, or prior workload is stuck. Recovery is
convergent, not optimistic. Absence of evidence is never evidence that a machine is clean.

## Terminology and independent axes

- **Connected** means the current authenticated AgentHub transport has a fresh application heartbeat.
- **Control-responsive** means the current Agent process has acknowledged a fenced controller probe or
  command within its deadline. A heartbeat alone does not prove this.
- **Reconciled** means the controller's durable workload owner agrees with the Agent's current workload
  assertion under the current connection generation.
- **Health** is currently `HEALTHY | UNHEALTHY | UNKNOWN`; it is an eligibility input, not a synonym for
  connection state.
- **Lifecycle** is `STARTING | READY | DRAINING | RESTARTING | UPGRADING | QUARANTINED | UNREACHABLE`.
- **Occupancy** identifies the actual exclusive owner: no work, Build, AgentExplorer operation,
  maintenance, or provider operation.

Scheduling requires all relevant axes to be positive. The target UI and REST expose them independently
so an operator can distinguish an offline machine, a live but wedged Agent, a known unhealthy machine,
and a healthy Agent intentionally drained for maintenance. The current REST slice adds operational
health, quarantine, and reason to the existing connection/activity view; the complete lifecycle and
occupancy projection, React presentation, and actions remain target work.

## Build stop contract

The first stop request is durable and graceful. It asks the active native containment to terminate,
waits only for a persisted grace deadline, and then permits only steps explicitly marked to run after
cancellation. A force request is a separate authorized and audited escalation. It skips remaining
cleanup, force-terminates the entire containment, and waits only for a force deadline.

```text
RUNNING
  -> CANCEL_REQUESTED -> GRACEFUL_STOPPING -> CANCELLATION_CLEANUP -> CANCELLED
  -> graceful deadline -> GRACE_EXPIRED + QUARANTINED
  -> explicit FORCE_STOP_REQUESTED -> FORCE_STOPPING -> FORCE_TERMINATED
  -> force deadline without positive empty-workload proof -> UNKNOWN + QUARANTINED
```

Repeated graceful requests preserve the first reason and grace deadline. Grace expiry never grants
force authority: it quarantines the Agent while leaving the control session available for recovery.
An explicitly authorized force request reopens the same operation with one new bounded force-result
deadline; duplicates cannot weaken or extend that phase. A terminal result is accepted only under the
current workload fence. Quarantine alone never releases scheduler capacity or erases ambiguous runtime
occupancy.

On Windows, `CloseMainWindow` is necessarily best-effort. A service-hosted console workload normally
has no window, so it remains in graceful stopping until its deadline and then quarantine; the operator
must issue the separately authorized force stop. Vivarium does not synthesize a successful graceful
stop or silently escalate. A native console-process adapter is part of the remaining cross-platform
containment evidence.

The TeamCity behavior borrowed here is the distinction between the first stop, cancellation cleanup,
and a repeated/force stop. Vivarium deliberately adds controller-owned deadlines and quarantine rather
than allowing a disconnected or uncooperative Agent to remain ambiguously running forever.

## Recovery ladder

For every server-requested stop or Agent-process restart, recovery advances through bounded layers:

1. Persist intent, operation identity, fence, reason, and absolute deadline.
2. Deliver on the current priority control lane and require an exact acknowledgement.
3. On missed acknowledgement, fence the session and retain Agent occupancy.
4. Ask the out-of-process Bootstrap supervisor to terminate/restart the exact recorded Agent child.
5. Reconcile a newer Agent session and its persisted workload journal before restoring readiness.
6. If Bootstrap or the host is unreachable, mark the Agent `UNREACHABLE` or `QUARANTINED`; never infer
   `READY`.
7. A provider-managed host may then use a separately authorized provider reset/revert. A physical
   host without an out-of-band provider requires operator intervention.

Step 4 is target behavior, not current capability. Today Bootstrap relaunches an Agent that consumed an
in-band restart command and exited, but the controller cannot yet reach Bootstrap when the Agent control
loop itself is wedged. The remaining extension is intentionally narrow: an authenticated, durable
`restart-current-child` directive with an operation ID and deadline. Bootstrap must not learn Build,
AgentExplorer, or health policy. This is the explicit D2/D21 design discussion authorizing that bounded
lifecycle addition; the freeze gate still requires platform process evidence before the new contract
is declared frozen.

## Expected-failure catalogue

| ID | Expected failure and unsafe shortcut | Required solution / invariant | Implementation evidence and remaining gate |
|---|---|---|---|
| `ALR-001` | Agent crashes or is OOM-killed while a Build descendant survives. Reconnect expiry then makes the machine look idle and a second Build overlaps the orphan. | Create native containment at launch, persist the active-workload journal and process identity, kill/reconcile it before readiness, and quarantine on ambiguity. Reconnect expiry may finish Build history but cannot prove machine cleanliness. | **Partial.** `active-build.json` records accepted ownership and exact PID/start identity; startup kills a matching process tree, reports recovery in `Hello`, and quarantines ambiguity. A durable native Windows Job Object / Linux cgroup / macOS containment handle and detached-descendant evidence remain open. |
| `ALR-002` | One Stop command is treated as both graceful cancellation and hard termination. Cleanup is either skipped or allowed to hang forever. | Separate graceful and force modes, persist their per-phase deadlines, preserve the first reason, and expose honest intermediate states. | **Implemented for the current process contract.** Graceful sends `SIGTERM` or best-effort `CloseMainWindow`, runs only `ALWAYS` cleanup, and never grants hard-kill authority; explicit force skips cleanup and kills the process tree. Headless Windows console jobs require explicit force until a native console adapter exists. Native containment evidence remains under `ALR-001`. |
| `ALR-003` | Agent receives cancellation but its executor ignores it or hangs while killing/waiting. Heartbeats continue and the Build remains `CANCEL_REQUESTED` forever. | Acknowledge exact stop intent, carry one immutable controller-owned deadline per phase, then quarantine without releasing occupancy when evidence is absent. Force is a distinct authorized recovery request. | **Implemented.** Durable stop operations carry exact IDs, modes, ACKs, first reason, and grace/force deadlines. The Agent preserves the accepted deadline while the controller enforces expiry; grace and force expiry quarantine, and neither makes the Agent idle. |
| `ALR-004` | Hello or heartbeat says an empty or different workload while the controller owns a Build, but the controller refreshes liveness without checking the assertion. | Validate every workload assertion against durable ownership and the current session; conflict immediately makes the Agent non-eligible and starts reconciliation. | **Implemented for Build ownership.** Reconnect and heartbeat workload mismatch plus heartbeat sequence regression quarantine the Agent; only an empty assertion for a still-unacknowledged assignment may be retried. An unknown reported Build receives a fenced containment request. Other occupancy kinds remain future schemas. |
| `ALR-005` | Log or status traffic holds the single gRPC writer while heartbeat, cancel acknowledgement, or terminal result waits behind it. | Use byte-bounded priority lanes. Control and terminal traffic preempts or sheds workload output; output never blocks the child indefinitely. | **Implemented.** `SessionWriter` uses a bounded control lane and byte-reserved output lane; control is selected before queued logs, and controller outbox overflow fences only that Agent session. |
| `ALR-006` | A noisy Build grows Agent/controller memory without bound and can OOM the control plane or its peer Agents. | Byte-bound Agent spools and controller ingestion, persist explicit gap/truncation evidence, and keep per-Agent failure isolated. | **Partial.** Agent queued logs are capped at 1 MiB and shed without blocking; controller live logs are capped at 1 MiB characters with a truncation marker. Dropped Agent bytes are counted but not yet sent/persisted as a durable gap, and disconnected output is not spooled. |
| `ALR-007` | Assignment delivery is accepted by neither side, but the session remains connected. The prepared queue claim or direct dispatcher waits forever. | Persist an assignment-ack deadline, redeliver idempotently only while valid, then fence/reconcile/quarantine. Never silently requeue ambiguous work. | **Implemented.** Assignment attempts and exact-session ACK/deadline are durable. Matching reconnect adoption is stronger positive evidence; expiry quarantines, retains ownership, and requests force containment. |
| `ALR-008` | Controller commits a terminal result but loses `ResultAccepted`. It releases capacity while the Agent retains the pending result and rejects the next assignment. | Result acknowledgement uses the bounded/fencing send path; capacity waits for a result-ack or durable output-gap barrier, and retries remain idempotent. | **Implemented.** Terminal results remain durable and retried; controller ACK uses the bounded session outbox and releases runtime ownership only after the ACK is queued. |
| `ALR-009` | Operator needs to restart the Agent process without changing package version, or the Agent control loop is wedged and cannot consume `RestartAgent`. | General restart is a durable operation with `after-current-work`, `cancel-then-restart`, and `force` modes; Bootstrap provides the out-of-process fallback. Host reboot remains a different operation. | **Partial.** REST creates idempotent, audited, draining operations with exact ACKs; success requires both a newer connection generation and a different Bootstrap child/process-instance fence, so a network reconnect cannot fake restart. Modes require the negotiated Bootstrap capability. Controller-to-Bootstrap recovery for a wedged Agent, CLI/UI actions, and native platform evidence remain open. |
| `ALR-010` | A malformed workload exhausts RAM, CPU, process count, disk, or pipes, starving/killing the Agent. A same-principal workload may also signal or overwrite Agent state. | Put workloads in resource-governed containment with reserved control-plane capacity, byte/disk quotas, and an explicit privilege boundary. Unsupported isolation is reported honestly and may restrict a machine to trusted workloads. | **Open.** Process launch has no resource governance; service/user split remains a Platform/Security gate. |
| `ALR-011` | A powered-off physical host, kernel hang, or network partition is described as remotely recoverable through the in-band Agent. | Mark it `UNREACHABLE`; retain/quarantine ambiguous occupancy. Only a provider/BMC or operator can restore the host. Never promise in-band recovery from loss of the in-band path. | **Accepted limitation.** Provider recovery arrives with provider implementations. |
| `ALR-012` | Controller restart, reconnect, stale heartbeat, late result, or lost ACK causes a lower-generation observation to overwrite the current owner. | Persist owner/fence/deadlines; accept state only from the current credential/session generation; make duplicates idempotent and contradictions quarantine-worthy. | **Implemented for Build and Agent restart state.** Durable ownership, stop/assignment/restart deadlines, exact session/generation checks, idempotent late results, and persistent health quarantine exist. Generalized fences for future AgentExplorer/provider occupancy remain open. |

## Release-blocking evidence

The lifecycle contract is not complete until automated evidence covers:

- graceful stop, cleanup, repeated force stop, and non-extending deadlines;
- a process that ignores graceful termination and a descendant that detaches from its parent;
- Agent `SIGKILL`/forced termination during a Build followed by Bootstrap/new-Agent reconciliation;
- heartbeat with empty, matching, stale, and conflicting workload assertions;
- blocked controller reads plus a log flood without heartbeat/control/result starvation;
- controller and Agent restart at every assignment, cancel, result, and result-ack crash window;
- full output/ACK queues with explicit gap/fencing behavior;
- resource pressure and disk-full behavior without corrupting Agent identity or pending evidence;
- two-Agent isolation: every failure above on Agent A leaves Agent B responsive and schedulable;
- native Windows, Linux, and macOS process-containment evidence for every advertised behavior;
- mixed-version behavior in which a stale Agent may finish supported work but is never sent an
  unsupported force/restart command.

## Non-goals

- Claiming to sandbox hostile code before a proven OS-principal boundary exists.
- Treating Agent process restart as machine reboot or provider reset.
- Releasing a machine merely to keep queue throughput high when its workload state is ambiguous.
- Putting detailed metrics, process lists, logs, or sensitive host data into heartbeats.
