# Cross-Platform Agent and Host Semantics

> Status: **Accepted**
> Implementation: **Partial**
> Maintainer role: [Platform Expert](../roles/platform-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D1-D5, D8-D12, D16, D19-D30

## Purpose

Vivarium runs the same agent on physical machines and provider-managed machines across Windows,
Linux, and macOS. TeamCity builds and AgentExplorer inspection must share one agent contract without
pretending that the underlying operating systems have identical process, network, filesystem, or
service behavior.

This document defines the common semantics and the adapter boundary. It is intentionally not a list
of native APIs: platform-specific APIs are implementation details unless their limits change the
observable contract.

## Scope

- Windows, Linux, and macOS agent deployment and service integration.
- Exact OS and architecture inventory.
- Environment, process, TCP/UDP endpoint, execution, cancellation, and filesystem semantics.
- Permissions, elevation, interactive sessions, packaging, signing, and upgrades.
- Capability availability and partial failures.
- Portable Git configuration and REST representations.
- Cross-platform tests and evidence required to advertise support.

Hypervisor lifecycle, project/build semantics, product authorization, UI composition, and log
retention are owned elsewhere. This document specifies the platform constraints those designs must
honor.

## Current state

The repository has useful cross-platform foundations, but does not yet have a complete platform
contract:

- `Vivarium.Agent` now collects bounded typed connect-time facts on Windows, Linux, and macOS: product
  name/version/build, kernel, native OS and process architecture, hostname, Agent/package identity,
  observation time, outcome/completeness, and redacted structured issues. Windows build+UBR, Linux
  `os-release` plus `uname`, and macOS `sw_vers` plus `uname` remain distinct semantic fields.
- `agent-explorer.host-facts.v1` capability support is advertised independently from the latest
  collection outcome and persisted with credential/connection-generation provenance. Fixture coverage
  exists for all three platforms and native macOS; native Windows/Linux release evidence remains.
- `BuildExecutor` launches an executable with `ProcessStartInfo.ArgumentList`, no implicit shell,
  inherited environment plus explicit overrides, and a bounded timeout. It currently hard-kills with
  `.Kill(entireProcessTree: true)` for timeout and cancellation; a proven graceful phase and native
  process containment are not implemented.
- Payload archives normalize names, preserve Unix modes and symlink targets, reject traversal and
  link pivots, and apply platform-aware alias checks. This is a strong D3 base.
- Artifact glob matching is currently ordinal-ignore-case on every platform. That differs from normal
  Linux/macOS case-sensitive semantics and needs an explicit contract and tests before it is called
  portable.
- Supported definition RIDs are currently `win-x64`, `linux-x64`, `linux-arm64`, and `osx-arm64`.
- Unix secret files are restricted to the user. Agent-side Windows secret files currently rely on
  inherited directory ACLs; an explicit private ACL contract is still missing.
- The first central upgrade path is implemented across the common RID contract: bounded immutable ZIPs,
  exact digest/RID checks, content-addressed activation, retained prior package, health-gated success,
  and one-shot rollback. Linux/macOS run the real bootstrap/Agent process evidence; Windows process
  evidence remains. Installers, service-manager integration, package signing, fleet rollout policy,
  dynamic AgentExplorer process/network/environment inventory, and complete on-platform release
  evidence are not complete.

The current code is evidence only for the behavior it tests. It is not evidence that every advertised
platform behaves identically.

## Target state

One agent package exposes the same versioned semantic capabilities on every supported platform and
routes them through small native adapters. The controller, Git schema, REST API, and UI consume those
semantics without branching on native APIs or parsing native strings. Every supported capability is
advertised independently from the outcome of any collection attempt, and every release publishes the platform/RID evidence
behind its support claims. Deployment, upgrades, build execution, and AgentExplorer inventory therefore
share platform primitives while retaining separate product permissions and histories.

## Invariants

1. The controller stores and evaluates common semantic data; native collection and mutation remain
   inside an agent-side platform adapter.
2. A missing native value is never replaced with a plausible-looking empty string, zero, or controller
   default. Availability and collection errors are data.
3. Every observation carries `agent_id`, observation/session generation, collection time, and
   collector version so restored or stale state cannot masquerade as current state. A boot ID is
   informational and never a fence because restoring VM memory may preserve it.
4. Capability support advertisement, policy enablement, caller authorization, and one collection
   attempt's runtime outcome are separate facts. `permission_denied`, `degraded`, and `partial` do not
   withdraw a supported capability.
5. Builds and AgentExplorer operations use the same execution/cancellation primitives but distinct
   workload and authorization models.
6. Commands are executable plus ordered arguments. The controller never reconstructs a command line
   and never assumes a shell's quoting rules.
7. A path reported by an agent is not interpreted using the controller's filesystem rules.
8. Secrets can occur in environment values, command arguments, paths, native errors, and package
   manager output. In v1, secret environment values are irreversibly redacted or omitted agent-side
   before transport and can never be revealed by REST, UI, CLI, or a later authorization decision.
9. Capability identifiers do not encode operating systems. A capability advertisement says the agent
   implements the contract; runtime outcomes explain what happened during a particular attempt.
10. No bootstrap behavior changes through this design alone. D2/D21 and AGENTS.md govern that change.

## Common platform boundary

The agent composes narrow adapters behind semantic interfaces. Initial interfaces should remain
small enough to test with fixtures:

```text
IPlatformFactsCollector
IEnvironmentCollector
IProcessInventory
INetworkEndpointInventory
IProcessExecutor
IFileSystemSemantics
IServiceIntegration       (installer/diagnostics, not ordinary build execution)
```

The common layer owns validation, timeouts, result envelopes, redaction hooks, and protocol mapping.
The adapter owns native calls and translation. A new platform feature begins with a common semantic
contract and may add a namespaced native extension only when the common model cannot express a
material distinction.

### Capability advertisement and runtime outcomes

Each capability has a stable ID and contract version, for example
`agent-explorer.network-endpoints.v1`, projected to a scheduling fact under `capability.*`. An agent
advertises the capability while it implements that contract for the current platform. Host policy can
separately enable or disable invocation. Neither policy nor a collection result rewrites the support
advertisement.

Each invocation or collection has its own runtime outcome:

| Outcome | Meaning |
|---|---|
| `succeeded` | The requested contract completed without known loss |
| `partial` | Valid items or fields were returned, with identified omissions |
| `degraded` | A documented lower-fidelity native mechanism was used |
| `permission_denied` | Native permissions prevented the attempt or all useful results |
| `temporarily_unavailable` | A transient native/resource failure prevented useful collection |
| `failed` | A non-transient or unclassified failure prevented useful collection |

Collection results contain the outcome, `complete`, structured issues, and successful items. One
inaccessible process must not discard the rest of the process list. A denied collection must not be
encoded as an empty successful list or as an absent capability. A platform for which the agent does
not implement the contract simply does not advertise that capability; a diagnostic catalog may
explain the unsupported platform, but it is not part of the supported-capability set.

Issues use stable semantic codes such as `access_denied`, `process_exited`, `not_supported`,
`resource_exhausted`, and `native_failure`; native error code/message may be attached for diagnostics.
Consumers branch on the semantic code, never on localized native text.

## Platform and OS facts

Canonical parameter namespaces are flat, stable, and authority-specific:

| Namespace | Authority and purpose |
|---|---|
| `system.*` | Agent-observed OS, hardware, runtime, and session facts |
| `env.*` | Explicitly published, non-secret environment-derived scheduling facts |
| `capability.*` | Versioned capability support advertised by the agent |
| `custom.*` | Git-managed operator labels and overrides; never agent-owned |
| `agent.*` | Stable Agent identity/kind and Agent-level controller facts |
| `provider.*` | Provider and optional ProviderInstance identity, lifecycle, and capabilities/facts |

The canonical OS scheduling keys are:

```text
system.os.family            windows | linux | macos
system.os.arch              x64 | arm64
system.os.version           product/distribution version
system.os.build             platform build identity where applicable
system.os.kernel.version    kernel version, independent from product version
```

The currently implemented `os.*` keys are transitional read-only migration aliases. New Git
configuration, REST representations, UI code, and capability matching use `system.os.*`; writers do
not create or persist new `os.*` authority. The current standalone `arch` parameter likewise migrates
to `system.os.arch`. Compatibility code may read the aliases only for stale agents during the agreed
protocol compatibility window.

Additional typed facts may be displayed and audited but are not automatically safe as compatibility
keys. The agent reports the OS architecture, not merely the architecture of a potentially emulated
agent process; process architecture is reported separately when it differs.

### Windows

Collect product name/edition, product version, major/minor/build, UBR/revision, installation type,
OS architecture, and kernel build. The stable patch identity used for drift detection includes the
build and UBR. Version-helper behavior affected by application manifests is not an acceptable source
by itself.

### Linux

Collect distribution identity from `/etc/os-release`: ID, VERSION_ID, PRETTY_NAME, and optional
variant. Collect kernel release/version and architecture separately. Do not call a kernel version the
distribution version. Missing optional keys produce absent fields, not an unknown distribution
invented by the controller.

### macOS

Collect product name, product version, product build version, kernel version, and architecture.
Rosetta/emulation state is a separate fact from host architecture. macOS virtualization and TCC
constraints remain capability/permission facts, not OS-version strings.

Facts are refreshed on connect, after an upgrade, after resume/revert, and when a cheap identity
digest changes. They are not retransmitted on every heartbeat. Historical build provenance retains
the exact selected-agent fact snapshot as required by D14/D16.

## Environment semantics

"Environment" means the effective environment of the agent process and therefore the baseline that a
child process inherits. It is not a complete inventory of machine-, user-, service-, login-shell-, or
registry-defined variables.

Each entry contains a name, optional safe value, irreversible redaction/omission state, and optional
origin when the origin can be determined reliably. Names use native comparison:

- Windows environment names are compared case-insensitively.
- Linux and macOS environment names are case-sensitive.

The wire/REST form is an ordered array of entries, not a JSON object. This preserves native spelling
and prevents a controller or JavaScript client from silently applying different key semantics.

Environment values are sensitive by default. Scheduling parameters under `env.*` are a separate,
explicitly published allowlist; they are not synthesized from a captured environment. In v1, the
agent applies the configured allowlist and secret-name/value classification before serialization.
Secret values are either replaced by an irreversible `redacted` marker or the entire entry is omitted;
the original bytes never cross AgentHub and are never retained in a hidden controller field. There is
no reveal endpoint, privileged reveal scope, or delayed unredaction. Changing policy affects only a
new collection. Collection reports the agent principal and observation time.

## Process inventory

A `ProcessRef` is `(agent_id, observation_generation, pid, start_time)`. PID alone is not a stable
reference, and boot ID is only informational: restoring a memory snapshot can preserve the boot ID,
PID, and start-time view while the controller must treat the restored agent session as a new
generation. Common fields are:

- PID, parent PID, start time, name, executable path, and ordered argument vector when available;
- user/principal, session, interactive-session association, and elevation/integrity when available;
- state, CPU time, working/private memory, and thread count;
- per-field availability/issues rather than a failed whole snapshot.

Executable paths and arguments are native strings. The common API never parses an OS-formatted
command-line string to reconstruct arguments. When the platform exposes only a flattened command
line, it is labeled `command_line` and is not presented as an authoritative `arguments` array.

Processes may exit or PIDs may be reused during enumeration. Adapters must tolerate races and attach
item-level `process_exited` or `access_denied` issues instead of retrying an unbounded scan. Any later
operation revalidates the complete `ProcessRef` against the current Agent/session generation and
native start time immediately before acting.

## TCP and UDP endpoint inventory

The common entity is a network endpoint, not an "open port":

- protocol (`tcp` or `udp`) and address family (`ipv4` or `ipv6`);
- local address/port and wildcard-binding flag;
- TCP state and optional remote endpoint where applicable;
- owning `ProcessRef` (`agent_id`, observation/session generation, PID, and start time) when it can
  be resolved;
- native namespace/compartment/interface metadata only as an optional namespaced extension.

UDP has no TCP-style `LISTEN` state; a local UDP endpoint is represented as `bound`. An unresolved
owner is a valid partial result, not PID zero. Platform permissions and enumeration races can make
ownership incomplete, and this is surfaced in the snapshot.

Addresses are serialized canonically with a separate family and optional IPv6 scope ID. Consumers do
not parse presentation strings such as `address:port`.

## Execution, arguments, shells, and cancellation

### Launch

The portable launch request is:

```text
executable + arguments[] + working-directory + environment-overrides[] + timeout + output limits
```

There is no implicit `cmd.exe`, PowerShell, `/bin/sh`, Bash, or zsh. A user who needs a shell declares
it as the executable and supplies shell-specific arguments. Git configuration may select a
platform-specific executable, but quoting is never performed by the controller.

Relative payload-local executables resolve below the work directory. Bare executable names may use
the agent process's native `PATH`. The launch result records the resolved executable when the OS can
provide it.

### Containment

Every workload must have a native containment boundary created at launch, not discovered after a
timeout:

- Windows: a Job Object or an explicitly proven equivalent that includes descendants.
- Linux: a process group/session, with cgroup integration when resource governance requires it.
- macOS: a process group/session; platform tests must prove descendant cleanup limitations.

No process is killed by an unverified PID after the original process identity has disappeared.

### Stop contract

Cancellation has two separately authorized bounded modes:

1. Graceful stop requests termination using the platform adapter when the step policy allows it.
   Missing termination evidence at the grace deadline quarantines the Agent; it never grants force
   authority.
2. An explicit force-stop request terminates the entire containment group and has its own bounded
   result deadline.

The common terminal reason is `cancelled`, `timed_out`, or `force_terminated`; native mechanisms are
diagnostic metadata. Windows console-control delivery, Unix `SIGTERM`/`SIGKILL`, GUI close requests,
and service stop are not interchangeable. A step may declare the supported graceful mechanism;
otherwise the adapter reports graceful stop as unsupported; it does not silently acquire force-stop
authority. The controller and REST API must not expose Unix signals as the universal contract.

Output streams remain byte streams until decoded by an explicitly selected encoding. Backpressure,
chunk limits, secret redaction, and retention belong to the Logs Expert; the platform adapter must
not buffer unbounded process output.

## Filesystem semantics

Archive entry names use `/` as a protocol separator and are relative to a declared root. Host paths
remain native. The following rules are common:

- Reject rooted paths, traversal, duplicate/type-conflicting entries, and symlink/reparse pivots.
- Use native case and alias rules when materializing or querying a filesystem; do not assume that all
  Windows volumes are case-insensitive or all macOS volumes are case-insensitive.
- Preserve Unix executable/mode bits and symlink targets in portable payload archives.
- Do not follow symlinks/reparse points by default for future AgentExplorer file browsing or mutation.
- Represent a remote browse location with an opaque agent-issued handle plus a native display path.
  REST clients must not construct child paths by concatenating separators.
- Treat path display strings as untrusted data and potential secret-bearing values.
- Expose capabilities for modes, ACLs, symlinks, hard links, alternate streams, and case sensitivity
  rather than guessing them from `system.os.family`.

Artifact matching needs a documented case rule. The target default is the target filesystem's actual
comparison behavior, with an optional portable strict mode if TeamCity configuration requires stable
cross-platform matching. The current unconditional ignore-case matcher must not silently define the
contract.

## Permissions and elevation

The agent reports its principal, elevation/integrity, interactive session, and the runtime outcome of
each privileged operation. A supported capability remains advertised when the current principal gets
`permission_denied` or only a `partial`/`degraded` result. Installation policy decides the principal;
an operation never attempts an interactive `sudo`, UAC prompt, or macOS authorization dialog.

- Windows process ownership, protected process details, network ownership, clock correction, and UI
  automation may require elevation. UIPI and session isolation remain explicit constraints.
- Linux `/proc`, network ownership, signals across users, core dumps, input devices, and system changes
  depend on UID, capabilities, LSM policy, namespaces, and Wayland/X11 session.
- macOS process inspection, automation, screen/input access, protected files, and system changes depend
  on UID/root, SIP, sandboxing, and manual TCC grants.

Read-only inventory is best effort and returns partial results. Mutating AgentExplorer operations require
an explicit policy and user authorization even when the agent principal is technically able to run
them. Native privilege is not application authorization.

## Deployment and service integration

The controller distributes self-contained per-RID packages as required by D19. The Agent API/SDK
Expert owns the manifest, enrollment, and upgrade protocol; the Platform Expert owns these native
constraints:

| Platform | Persistent host mode | Interactive/UI mode | Native concerns |
|---|---|---|---|
| Windows | Windows Service/SCM | Logon task in the target desktop session | service/session isolation, UAC/UIPI, ACLs, Job Objects, MOTW, Authenticode |
| Linux | systemd service where available, documented foreground fallback | user service in the selected X11/Wayland session | UID/groups, capabilities, modes, cgroups, distro/service-manager variance |
| macOS | launch daemon where headless operation is sufficient | launch agent in the logged-in user session | TCC grants, launchd domains, quarantine, code signing/notarization |

The installer must prevent two active agents from sharing one identity/data directory. Install,
upgrade, restart, uninstall, and diagnostic commands must be idempotent and report the effective
install location, data location, principal, service mode, and version.

Interactive mode is not inferred from the process being attached to a terminal. The adapter verifies
the native session and reports it. A persistent service may need to coordinate with a user-session
runner for UI workloads; that split is an open design question and must not be hidden inside the
bootstrap contract.

## Packaging, signing, and upgrades

- Packages are immutable per RID and identified by version plus SHA-256.
- Public ZIP candidates use sorted entries and one fixed timestamp; Unix executable entries carry 0755
  and data/sample/version entries carry 0644. Native release smoke extracts the final ZIP rather than a
  pre-package publish directory.
- The controller serves exact package bytes and an authenticated manifest; D21 governs trust before
  installer execution.
- Archive formats preserve executable bits and symlinks where the target needs them.
- Upgrade uses verified temporary content, content-addressed extraction, and an atomic small state-file
  switch. Exact operation/digest/session reconciliation and a durable marker-confirmation handshake
  gate success; early exit or health timeout launches the retained prior package once.
- Windows release packages need an Authenticode/MOTW strategy; macOS needs signing/notarization and
  quarantine handling; Linux packages need verified modes and service-unit permissions. D19 currently
  defers public code signing, so UI/install docs must state the resulting warnings honestly.
- Secrets and identity data live outside the versioned executable directory and survive upgrade but
  are removed or rotated by an explicit unenroll/uninstall choice.

An upgrade support claim requires tests from the previous supported agent version to the candidate,
including reconnect, build ownership, pending cancellation, and a failed/aborted swap. Restoring an
old VM snapshot is equivalent to starting an old package and must exercise the same path.

## Git-controlled configuration without platform leakage

Git is the source of truth for desired configuration. Portable configuration expresses intent and
requirements, not native implementation commands chosen by the controller:

- Select agents with canonical facts such as `system.os.family`, `system.os.arch`,
  `capability.*`, and explicit `custom.*` parameters. `agent.*` and `provider.*` selectors retain
  their distinct authorities; new configuration never writes the transitional `os.*` aliases.
- Represent process launches as executable plus argument arrays. Never commit one flattened command
  string and ask a controller on another OS to quote it.
- Put deliberate platform variants under explicit selectors with stable IDs. Do not infer a shell,
  extension, path separator, or executable suffix from the controller's OS.
- Use repository-relative `/` paths for configuration and payload identity; resolve and validate them
  against the checked-out root before submission.
- Store secret references, never secret environment values or credentials.
- Normalize line endings and text encoding only where the schema declares it; binary/package hashes
  always cover exact bytes.
- Validate every declared platform variant at commit/import time. A typo in a non-host variant must
  not remain invisible because the controller happens to run on Windows.

Runtime observations such as process lists and endpoint snapshots do not belong in Git. Desired
labels, policies, package/state declarations, and build definitions do. Every applied revision and
result records the Git repository identity, commit, path, and content hash supplied by the
Git/Versioning design; the platform adapter never performs its own hidden Git mutation.

The TeamCity and Git/Versioning experts own the final schema. The Platform Expert reviews it using at
least one example for each supported family and rejects controller-host-dependent behavior.

## REST representation without platform leakage

The REST Expert owns routes and API versioning. Platform resources follow these representation rules:

- Use canonical enums for OS family, architecture, protocol, address family, capability support,
  runtime outcome, and semantic error code; allow unknown future values.
- Use RFC 3339 UTC timestamps and explicit byte counts/durations; do not expose native time structs.
- Use arrays for arguments and environment entries.
- Return native paths as display values or opaque handles, not as controller-normalized identifiers.
- Return IP address and port as separate fields.
- Return a `ProcessRef` as `agent_id`, observation/session generation, PID, and start time. A boot ID
  may be returned as diagnostics but never replaces the generation fence.
- Use nullable/omitted optional fields plus structured issues. Do not overload `0`, `""`, or `false`
  to mean unavailable.
- A partially successful inventory request returns its snapshot and issues. Request/authentication
  failures use the API's standard problem envelope.
- Keep native diagnostic data in a namespaced extension such as `native.windows`, and never make the
  common UI or automation depend on it.
- Include the stable capability advertisement/contract version independently from operation outcome,
  observation time, generation, freshness/staleness, and completeness.

REST must not accept a raw native path, PID, or shell command as sufficient authorization for a
mutation. Future file/process operations resolve an opaque resource reference against the current
agent generation and revalidate identity immediately before action.

## Test and evidence matrix

Cross-platform support is proven in layers:

| Layer | Required evidence |
|---|---|
| Semantic unit tests | Shared contract, mapping, validation, redaction hooks, partial-result merge, cancellation state machine |
| Adapter fixture tests | Golden native inputs/errors for exact OS facts, processes, and endpoints on every family |
| Tier-2 process tests | Real agent child process: launch/arguments/environment/output/timeout/cancel/tree cleanup/reconnect |
| Platform CI | Build and test on each supported OS/RID that hosted runners can cover |
| Package tests | Fresh install, permissions, service start/stop, upgrade, failed swap, uninstall, identity preservation |
| Manual/hardware checks | Windows interactive desktop/UIPI, Linux X11/Wayland limitations, macOS TCC and Apple-hardware-only workflows |
| Previous-version compatibility | Current controller against previous agent package, including restored-snapshot upgrade |

The minimum platform matrix begins with the currently declared RIDs:

```text
Windows x64
Linux x64
Linux arm64
macOS arm64
```

An architecture may use emulated compile/test coverage only for pure mapping logic. Native process,
service, permission, signing, filesystem, and endpoint support require evidence on the real family;
architecture-sensitive claims require the real architecture. Unsupported cells are explicit in the
release support matrix rather than silently skipped.

Every capability implementation supplies:

1. semantic contract tests;
2. at least one success fixture and representative permission/race/unsupported fixtures per platform;
3. a native smoke test for every platform on which the capability is advertised;
4. bounded output/time/resource tests;
5. documentation of required privilege and known information loss.

## Collaboration and change flow

1. The requesting expert defines the product behavior without naming native APIs.
2. The Platform Expert maps it to common semantics, native adapters, availability, security impact,
   and evidence.
3. The Agent API/SDK or REST Expert versions the transport representation.
4. The User Roles and Logs experts approve sensitive fields, authorization, audit, bounds, and
   retention.
5. The Git/Versioning Expert verifies that desired changes originate from a recorded revision and
   that runtime observations stay outside Git.
6. The Reconciliation Lead reviews any desired-state mutation, retry, fencing, or rollback behavior.
7. The Docs Expert reconciles this document, ARCHITECTURE, roadmap, walkthrough, and support matrix in
   the same change.

A platform-specific implementation may be merged without advertising that capability while the other
adapters are being built. Once advertised for a platform, runtime denial or degradation is reported
per attempt and does not erase support. It may not advertise support on another platform until that
platform's evidence matrix passes.

## Non-goals

- Hiding meaningful OS differences behind unreliable emulation.
- Providing a universal shell language or translating scripts between shells.
- Exposing every native process, network, ACL, service, or filesystem field in the common API.
- Automatic privilege escalation, UAC interaction, `sudo` prompting, TCC bypass, or SIP bypass.
- Treating the full machine/user environment as stable Git configuration.
- Supporting arbitrary Linux distributions, service managers, filesystems, or CPU architectures
  before they are named and tested in the release support matrix.
- Replacing provider contracts with SSH, WinRM, or hypervisor guest-exec APIs; D1 remains authoritative.

## Open questions

1. Should an interactive machine run one elevated user-session agent, or a privileged service plus a
   user-session companion? This affects identity, one-build capacity, upgrades, and the bootstrap
   contract and therefore needs a numbered architecture decision.
2. Which Windows API is the authoritative product edition/build/UBR source across supported Windows
   versions, and which Linux/macOS command/API fallbacks are permitted?
3. What is the exact graceful-cancellation default and which step types may opt into GUI/service stop
   mechanisms before force termination?
4. Should artifact glob matching follow the target filesystem or offer a configuration-level portable
   strict mode? The current unconditional ignore-case behavior must be resolved.
5. Which filesystem path-handle lifetime and generation rules will the future AgentExplorer file browser
   use?
6. Which host/RID combinations are release-supported first, and where will macOS arm64 and Linux
   arm64 service/package tests run continuously?
7. What signing/notarization threshold changes the current D19 unsigned-package policy for public
   releases?
8. What Windows ACL, Linux owner/mode, and macOS owner/mode contract protects agent identity and token
   files for each installation mode?
9. Which already agent-redacted inventory names, safe values, and issues may be persisted, and for how
   long? AgentExplorer, User Roles, Logs, and REST experts must agree before collectors ship; secret
   environment values never enter that decision because v1 never transports them.
10. How are native adapter and capability contract versions coordinated with stale agents restored
    from snapshots without violating protocol backward compatibility?
