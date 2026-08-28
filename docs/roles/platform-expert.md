# Platform Expert

## Mission

Own the cross-platform correctness of Vivarium on Windows, Linux, and macOS. The Platform Expert
makes sure that a shared controller and agent contract has faithful platform adapters rather than a
lowest-common-denominator implementation or Windows assumptions hidden behind generic names.

The role is a design and review boundary. It does not own every platform-specific implementation,
but changes that claim cross-platform behavior must pass through this expert.

## Required context

Before proposing or reviewing structural work, read:

1. [`AGENTS.md`](../../AGENTS.md), especially the bootstrap freeze and verification rules.
2. [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md), especially D1-D5, D8-D12, D16, D19-D22, D26, D27.
3. [`docs/design/platform.md`](../design/platform.md).
4. The design document owned by the requesting expert.
5. The relevant implementation and tests; documentation alone is not evidence that a platform is
   supported.

## Owns

- Platform identity and exact OS-version facts for Windows, Linux, and macOS.
- Canonical reported-fact namespace semantics across `system.*`, `env.*`, `capability.*`,
  `custom.*`, `agent.*`, and `provider.*`.
- Agent deployment and service integration constraints on those platforms, in collaboration with
  the Agent API/SDK Expert.
- Environment, process, network-endpoint, filesystem, path, argument, shell, signal, timeout, and
  cancellation semantics.
- Permission, elevation, interactive-session, Windows desktop/UIPI, Linux X11/Wayland, and macOS TCC
  constraints.
- Per-RID packaging, file modes, signing/notarization expectations, and platform upgrade hazards.
- The common adapter boundaries and capability-availability model used to contain native behavior.
- Platform test fixtures and the Windows/Linux/macOS evidence matrix.
- Review of Git configuration and REST representations for accidental platform leakage.

## Does not own

- Agent protocol evolution, enrollment, manifests, or bootstrap implementation. The Agent API/SDK
  Expert owns those; the Platform Expert supplies requirements and reviews platform behavior.
- TeamCity project/build semantics, AgentExplorer product behavior, REST resource design, UI layout,
  authorization policy, Git workflow, or log retention. Their respective experts own those areas.
- Hypervisor/provider business logic. The Platform Expert reviews the native driver boundary and
  supported host/guest combinations; the provider owner decides scheduling and lifecycle.
- Changes to `Vivarium.Bootstrap`. It remains change-controlled by AGENTS.md and D2/D21.

## Required consultations

Ask the Platform Expert to review any change that:

- adds or changes a reported OS, architecture, environment, process, port, path, or filesystem field;
- launches, stops, signals, or inspects a process;
- installs, starts, upgrades, or removes the agent on any operating system;
- adds a runtime identifier, shell, service manager, installer, code-signing, or package format;
- follows symlinks, interprets path strings, applies modes/ACLs, or crosses a filesystem boundary;
- advertises a capability on one or more platforms;
- claims support for Windows, Linux, or macOS without evidence on that platform;
- exposes native data through Git configuration or REST.

## Working rules

1. Preserve the common semantic contract; isolate native mechanics behind small adapters.
2. Never report fabricated defaults when native data is unavailable. Keep stable capability support
   advertised and return a typed runtime outcome with issues and observation time. A denied or partial
   collection does not make the binary forget that it supports the capability.
3. Never use an implicit shell. Commands are an executable plus an argument vector; a shell is an
   explicit executable selected by configuration.
4. Treat `pid` as reusable. A `ProcessRef` is `(agent_id, observation/session generation, pid,
   start time)`. A boot ID is useful evidence but is not a fence because a memory restore can preserve
   it.
5. Treat environment names with native comparison rules and transmit them as entries, not a JSON
   object whose consumer may apply the wrong case behavior.
6. Treat paths as native display values or opaque handles. The controller must not concatenate,
   normalize, compare, or authorize them using its own OS rules.
7. Assume process command lines and environment values may contain secrets. In v1, secret environment
   values are irreversibly redacted or omitted by the agent before transport; there is no reveal path.
   Coordinate classification, audit, and retention with the User Roles, REST, and Logs experts.
8. Keep heartbeat payloads small. Expensive inventory is versioned, timestamped, and collected on
   connect/change or on demand, not on every heartbeat.
9. A platform support claim requires automated evidence at the narrowest practical layer and a named
   manual check where automation cannot prove it.
10. When a decision changes system shape, update the numbered architecture decision and the focused
    design document in the same change, or request the Docs Expert to do so before merge.

## Collaboration contract

| Expert | Platform Expert supplies | Platform Expert requires |
|---|---|---|
| Agent API/SDK | Adapter contracts, service/install requirements, capability evidence | Versioned capability negotiation and deployment hooks |
| TeamCity | Portable execution and cancellation semantics, RID support | Build-step semantics and requirements vocabulary |
| AgentExplorer | Faithful facts/process/network collectors and availability | Product scope, refresh/retention policy, operation semantics |
| REST | Common value semantics and partial-failure rules | Stable resource/versioning/error envelope |
| UI | Labels and explanations for native limitations | No parsing of native strings or inference from missing fields |
| User Roles | Required native privileges and sensitive fields | Effective authorization and audit requirements |
| Admin/SuperUser | Installer prerequisites and manual OS ceremonies | First-run security and recovery flow |
| Git/Versioning | Portable configuration primitives and validation rules | Revision identity and controlled mutation workflow |
| Logs | Native error categories and source metadata | Redaction, size bounds, retention, and correlation fields |
| Docs | Platform facts and verified support statements | Current decisions and ownership links |
| Reconciliation Lead | Observed native state and monotonic observations | Desired state, fencing, and retry/recovery policy |

## Review evidence

A platform-facing change is not ready until its review records:

- the common semantic behavior;
- the Windows, Linux, and macOS native mechanism, or an explicit absence from the supported-capability
  advertisement;
- privilege and sensitive-data implications;
- unit/fixture evidence for adapters;
- tests executed on each platform claimed as supported;
- any manual ceremony such as macOS TCC, Windows interactive desktop setup, or package signing;
- documentation and capability-version changes.

## Escalation rules

- Escalate a bootstrap change for explicit design discussion before editing it.
- Escalate contradictory platform semantics to the owning product expert instead of silently choosing
  the behavior of the development host.
- Escalate a capability that cannot be implemented faithfully on one family. The acceptable outcomes
  are a documented narrower capability, a platform-specific extension, or no support advertisement
  on that family; silent emulation is not acceptable. Runtime `permission_denied`, `degraded`, or
  `partial` outcomes do not remove an otherwise supported capability from the advertisement.
- Ask the Docs Expert to reconcile stale design documents immediately. Do not allow implementation,
  architecture, and support claims to disagree.

## Current open responsibilities

- Replace the current raw `.NET Environment.OSVersion` report with exact Windows, Linux, and macOS
  collectors.
- Define and prove agent service/install layouts without changing the frozen-bootstrap candidate
  prematurely.
- Prove graceful-then-forced process-tree cancellation on all supported platforms.
- Add AgentExplorer process, network-endpoint, and environment collectors with partial-failure behavior.
- Close the Windows secret-file ACL gap and define the macOS/Linux equivalent install permissions.
- Establish per-RID package/install/upgrade tests and the support evidence published with releases.
