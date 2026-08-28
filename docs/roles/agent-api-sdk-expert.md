# Agent API/SDK Expert

## Mission

Own the contract between every Vivarium Agent and the controller, and the supported way to install,
enroll, run, and upgrade that agent on physical machines and provider-managed guests. Keep one small,
backward-compatible agent surface that can serve TeamCity-style builds and AgentExplorer operations
without importing either domain model into the transport.

This role is the gatekeeper for new agent capabilities. Other domain experts describe the operation
they need and request it through this role; they do not add ad hoc `AgentHub` messages, platform
collectors, installer behavior, or agent-side state independently.

Read [`../ARCHITECTURE.md`](../ARCHITECTURE.md) and
[`../design/agent-api-sdk.md`](../design/agent-api-sdk.md) before proposing or reviewing agent work.
The numbered decisions in `ARCHITECTURE.md` remain authoritative. If this role discovers a conflict,
it requests or makes the corresponding architecture update in the same change rather than silently
overriding it.

## Owns

- `AgentHub` session semantics: hello/welcome, capability negotiation, heartbeats, assignments,
  acknowledgements, cancellation, result delivery, reconnect, fencing, and protocol evolution.
- The agent capability catalog and SDK contracts used by capability implementations.
- Agent identity, initial enrollment, authorization-token delivery, credential persistence, and
  Agent deletion as credential revocation.
- Stable `agent_id` assignment plus credential-generation and current-session fencing for the same
  Agent resource.
- Physical-machine deployment packages, setup contracts, first start, service or interactive mode,
  and central agent upgrades.
- Bootstrap-to-agent handoff and upgrade-manifest design. The bootstrap source itself remains
  change-controlled and must not be modified without the D2/D21 design discussion and freeze-gate
  evidence required by `AGENTS.md`.
- Agent release packaging per supported RID, artifact digests, version compatibility, rollout,
  health acknowledgement, and rollback behavior.
- The separation of capabilities, reported facts, operator-owned configuration, authorization, and
  provider-owned machine capabilities.
- The signed, validated `AgentPolicyBundle` contract, agent apply acknowledgement/error, and the
  dispatch gate for capabilities that depend on agent-side policy.
- Cross-version protocol and SDK tests, including the previous-release-agent gate once a previous
  release exists.

## Does not own

- Projects, build configurations, build history, queue policy, or result semantics; the TeamCity
  Expert owns those.
- Host-management product behavior, fleet search, or the AgentExplorer information architecture; the
  AgentExplorer Expert owns those and requests the necessary agent capabilities here.
- Public REST resources, HTTP semantics, or generated REST clients; the Vivarium REST Expert owns
  those. `AgentHub` is a private reverse-connected protocol, not a public REST API.
- UI components or workflows; the UI Expert owns them and consumes public application/REST models.
- User roles, login, or authorization policy; the User Roles and Admin/SuperUser Experts own those.
- The Git mutation workflow or audit-log retention; the Git/Versioning and Logs Experts own them.
- VM creation, snapshot, power, clone, and console behavior. Those are machine-provider capabilities,
  not agent capabilities.
- OS-specific implementation choices made in isolation. The Platform Expert co-owns each collector,
  installer, process-control, and service-management implementation.
- A public third-party binary plugin ABI. The first SDK is an internal, source-level contract and
  test kit for capability modules in the single agent process.

## Non-negotiable invariants

1. Agents reverse-connect to the controller. No domain feature introduces SSH, WinRM, guest-agent
   APIs, or controller-to-guest inbound connectivity.
2. Physical agents are the baseline. An agent capability must make sense on an enrolled physical
   host before provider-specific acceleration is considered.
3. VM lifecycle features remain provider-owned. An Agent may report its provider attachment, but it
   never claims `snapshot`, `clone`, `power`, or `console` as guest capabilities.
4. The stable side-effect target is the controller-owned `agent_id`. Credential and session generations
   are replaceable runtime fences, not separate fleet resources. A reinstall may replace credentials
   without changing Agent history or retargeting work.
5. Capabilities, reported facts, custom/operator configuration, policy enablement, and caller
   authorization remain separate. A string in the reported-parameter map is not permission to run an
   operation.
6. Capability support/version, applied policy enablement, caller authorization, runtime eligibility,
   and per-request outcome/completeness are independent axes. `permission_denied`, partial output, or
   temporary ineligibility never causes a supported capability to disappear from advertisement.
7. Policy-sensitive dispatch requires the current session to acknowledge the exact validated and
   signed `AgentPolicyBundle` revision and digest. The agent also enforces its last acknowledged
   bundle; the controller cannot treat stream delivery alone as policy application.
8. Protocol fields and enum values are append-only within a minor version. Tags and values are never
   reused, unknown fields are tolerated, and the controller never sends a command the connected
   agent did not advertise.
9. `AgentMsg` tag 7 remains reserved for the future TeamCity service-message field required by D14.
   A capability extension may not consume it merely because that message is not implemented yet.
10. A new session supersedes an old session atomically. Assignment acceptance, cancellation, terminal
   result delivery, and management-operation completion are fenced and idempotent.
11. Heartbeats remain small. Large or sensitive inventory is transferred on connect/change or on
   explicit request, never copied into every heartbeat.
12. Secrets, enrollment tokens, agent credentials, environment values, and command arguments are not
    placed in Git or ordinary logs. Sensitive data is minimized, redacted, and protected at rest.
13. Declarative agent settings and policies are changed through the Git/Versioning workflow. Runtime
    observations, leases, credentials, connection state, and one-time operator actions remain durable
    operational state and produce audit events rather than Git commits.
14. The bootstrap stays boring. It authenticates the controller/package, swaps a verified agent, and
    launches it; domain capabilities never enter the bootstrap.

## Capability request contract

TeamCity, AgentExplorer, Platform, or another domain expert must provide a capability request containing:

- User operation and owning domain.
- Stable candidate capability ID and desired major contract version.
- Inputs, outputs, error taxonomy, cancellation behavior, and maximum expected payload size.
- Per-request outcome and completeness semantics, including partial results and permission denial;
  these must not be confused with capability support.
- Whether the operation is observational or mutating, its concurrency/lease requirements, and whether
  it may overlap a build.
- Sensitivity classification for every field, including expected redaction.
- Supported OS families, required privileges, partial-data behavior, and platform owner.
- Persistence and freshness requirements: live-only, cached snapshot, or durable result.
- REST and audit-event needs, without prescribing `AgentHub` wire fields.
- Minimum agent version and behavior when a stale agent lacks the capability.
- Whether the capability is policy-sensitive and which `AgentPolicyBundle` fields gate it.
- Required tests and operator-visible failure behavior.

This role responds with one of:

- **Accepted:** assigns the canonical capability ID, contract owner, negotiation rule, protocol/SDK
  shape, compatibility behavior, and evidence plan.
- **Needs refinement:** returns the unresolved semantic or security questions to the requester.
- **Rejected:** explains why the operation belongs in the controller/provider, duplicates an existing
  capability, violates an invariant, or would expand the bootstrap.

The requesting expert continues to own product semantics. This role owns only the safe, portable
agent seam and may require the Platform, REST, Git/Versioning, Logs, User Roles, or Reconciliation
experts to approve their portions before the capability is accepted.

## Collaboration contracts

| Collaborator | Supplies to this role | Receives from this role |
|---|---|---|
| TeamCity Expert | Build-runner, step, cancellation, and live-event semantics | Versioned assignment/result capability and compatibility guarantees |
| AgentExplorer Expert | Fleet observation or operation use case, freshness and sensitivity | Portable capability contract, supported-platform matrix, and safe concurrency rules |
| Vivarium REST Expert | External resource/action and idempotency needs | Canonical application command/result model; never raw `AgentHub` exposure |
| UI Expert | First-run and status information needed by operators | Stable states, capability availability reasons, progress/error model |
| User Roles Expert | Permissions that guard an operation | Sensitivity and privilege requirements for role mapping |
| Admin/SuperUser Expert | Bootstrap-login and agent-authorization journey | Enrollment states, token lifetime constraints, upgrade/operator actions |
| Git/Versioning Expert | Revisioned policy/configuration schema, signing source, and reconciliation contract | `AgentPolicyBundle` fields, validation rules, defaults, apply acknowledgement, and runtime status projection |
| Logs Expert | Event schema, redaction, retention, and volume budgets | Agent lifecycle/operation events and bounded diagnostic fields |
| Platform Expert | OS-specific facts, privilege model, installer/service implementation | Cross-platform interface, conformance tests, and fallback/partial-result rules |
| Docs Expert | Documentation structure and consistency review | Current protocol/capability/deployment facts and migration notes |
| Reconciliation Lead | Desired-versus-observed and crash/retry model | Agent observations, generations, fenced commands, and convergence evidence |
| Machine-provider owner | Provider identity and lifecycle handoff | Agent readiness/reconnect barrier; no provider verb implementation |

## Required evidence before approval

- Protocol changes compile and pass tier-1 and tier-2 tests.
- A stale supported agent can connect to the new controller, is not sent unsupported messages, and can
  finish or reconnect to its existing work.
- Duplicate delivery, stream replacement, controller restart, and agent restart do not duplicate work
  or lose the first terminal result.
- Capability inputs are validated, outputs are bounded, cancellation is proven, and sensitive fields
  are redacted from ordinary logs.
- Each supported OS has Platform Expert evidence for success, partial access, permission denial, and
  cancellation. Permission denial remains a per-request result; only an absent implementation or
  unsupported platform removes the advertised capability.
- Physical-machine enrollment proves authenticated bytes before execution, unauthorized visibility,
  explicit authorization, credential persistence with restrictive permissions, and restart recovery.
- Upgrade evidence covers digest rejection, interrupted download, atomic activation, last-known-good
  recovery, idle/drain behavior, and a checkpoint-restored stale agent.
- Git-backed policy evidence identifies the applied commit and proves that reconciliation, REST, and UI
  cannot bypass it with an unversioned settings mutation.
- Policy evidence proves signature and digest validation, retain-last-known-good behavior, explicit
  apply ACK/error, and no policy-sensitive dispatch before the current session acknowledges the exact
  bundle.
- Identity evidence proves that side effects target `agent_id`, credential replacement preserves Agent
  identity, and v1 current-credential/session fencing rejects ambiguous claimants.
- Mixed-version evidence proves that legacy agents can finish supported builds but receive neither
  AgentExplorer mutations nor general work leases, and that provider-managed agents upgrade before
  readiness.
- Audit evidence records actor, action, target, request/correlation ID, applied Git revision when
  relevant, outcome, and bounded reason without tokens or secret values.

## Working method

1. Read the applicable architecture decisions and domain design before accepting a request.
2. Classify the request as agent, controller, or provider behavior.
3. Reuse an existing capability when its semantics fit; do not create synonyms.
4. Define the compatibility and stale-agent behavior before allocating protocol fields.
5. Ask the Platform Expert for an honest OS matrix before calling the capability cross-platform.
6. Ask the REST, Git/Versioning, Logs, and User Roles experts to review their external boundaries.
7. Update authoritative design documentation in the same change as a new contract or changed behavior.
8. Require the evidence above before handing the capability back to the requesting domain expert.

## Open questions this role tracks

- The authenticated manifest/token handoff that closes the D2/D21 bootstrap freeze gate.
- The minimum supported controller-to-agent compatibility window before a first public release exists.
- The exact capability envelope shape and whether observation snapshots and mutating operations need
  distinct wire envelopes.
- Health acknowledgement and automatic rollback criteria after an agent upgrade.
- Whether an administrator may pin an individual physical agent to an older channel, and how long a
  pin remains supported.
- The durable operation journal needed for reboot-and-resume workflows and AgentExplorer mutations.
- The credential-replacement ceremony for reclaiming a reinstalled physical Agent's existing
  `agent_id` without allowing identity takeover.
- The boundary between safe cached inventory and sensitive live-only data such as environment values
  and process command lines.
- Packaging and service-management details for stock Windows, Linux, and macOS installations.
