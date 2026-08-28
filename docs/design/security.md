# Vivarium Security Design

> Status: **Accepted**
> Implementation: **Partial**
> Maintainer role: [Security Expert](../roles/security-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D2, D4, D7, D8, D21-D27

This document distinguishes current evidence from target requirements. Numbered architecture
decisions remain authoritative.

## 1. Purpose and security posture

Vivarium deliberately executes supplied code and manages long-lived physical hosts. Its agent may run
elevated to support UI automation and machine maintenance. A successful build submission or
AgentExplorer mutation is therefore remote code execution by design, not merely a data-plane request.
Security must control **who can execute what, on which machine, from which immutable configuration
revision, with which secrets, under which lease, and with what evidence afterward**.

The controller is the policy authority. Agents report facts and capabilities and execute fenced work;
they do not decide caller permissions. Physical agents receive stronger protection than disposable
VMs because rollback cannot recover a physical host, erase external side effects, or undo secret
exfiltration.

## 2. Assets and adversaries

### Assets

- Controller authority: database, signing/pinning material, data-protection keys, tokens, Git
  credentials, secret references, and configuration history.
- Agent identity, enrollment proof, persistent credentials, update channel, and active lease.
- Physical host integrity, user data, installed software, network position, and interactive session.
- Managed image integrity, provider credentials, snapshot lineage, and verified ProviderInstance-to-Agent attachment.
- Source revisions, build definitions, result provenance, logs, artifacts, and audit records.
- Sensitive host inventory: environment values, command lines, usernames, paths, network endpoints,
  and software/license data.

### Adversaries and failure actors

- An unauthenticated network peer probing REST, UI, gRPC, blob, setup, or enrollment endpoints.
- An authenticated user or service token exceeding its project, agent-pool, or operation authority.
- A malicious or compromised Git repository, contributor, build payload, test process, or result file.
- A compromised or spoofed agent, including a restored stale VM session.
- A malicious browser origin, CSRF request, stored-output injection, or object-reference attack.
- Accidental operators: wrong target, stale UI, duplicate request, broad glob, leaked token, or unsafe
  cleanup command.
- Resource abuse: unbounded logs, ZIP bombs, huge inventories, artifact floods, reconnect storms,
  expensive live fan-out, and repeated command execution.

The controller host administrator, hypervisor administrator, and root/Administrator on an agent are
trusted infrastructure operators for v1. Vivarium cannot protect secrets from an already compromised
controller or hypervisor.

## 3. Trust boundaries

| Boundary | Untrusted input crossing it | Required controls |
|---|---|---|
| Network → controller | REST/UI/gRPC/blob/setup requests | TLS, authentication, authorization, request bounds, validation, audit |
| Agent → controller | hello facts, inventory, logs, status, results, artifacts | agent identity, session fencing, schemas, size bounds, output escaping, provenance |
| Controller → agent | build and AgentExplorer operations | pinned TLS, authenticated session, durable lease, operation policy, idempotency, deadline |
| Git remote → controller | refs, commits, trees, metadata, hooks/config | safe invocation, credential isolation, immutable revision, size/path limits, optional signature policy |
| Build submitter → agent OS | payloads, commands, environment, globs | target authorization, trust class, path hardening, timeout/quota, secret minimization |
| Agent OS → filesystem/process manager | files, exec, terminate, software changes | allowed roots/targets, canonical identity, policy, privilege gate, audit |
| Browser → controller | cookies, mutations, rendered logs/results | HttpOnly/Secure cookies, CSRF, restrictive CORS, CSP, escaping, object authorization |
| Controller → blob/artifact store | content-addressed bytes and metadata | hash verification, authorization by ownership, quotas, atomic writes, retention |
| Provider → managed machine | create/revert/power/snapshot | provider credential isolation, machine binding, readiness generation, exclusive lifecycle lease |

The reverse AgentHub connection in D1 removes inbound guest administration ports, but it does not
make the agent trustworthy. Its messages remain authenticated, bounded, and fenced untrusted input.

## 4. Required security invariants

### S1. Controller-owned authority

- Every user, service, build, AgentExplorer, Git-apply, agent-administration, and provider operation is
  authorized by the controller before dispatch.
- Agent-reported fields never grant permissions or enable policy. A caller cannot create a custom
  parameter that turns into authority.
- The UI, CLI, gRPC, and REST adapters call the same application authorization layer. No adapter gets
  a private bypass.

### S2. Capability, policy, permission, and lease remain separate

An operation is dispatchable only when all four checks succeed:

1. `capability`: the exact agent/platform operation and protocol version are advertised;
2. `policy`: the target agent, pool, or machine class has it enabled;
3. `permission`: the principal may invoke it for the project and target;
4. `lease`: lifecycle, health, connection generation, and occupancy allow it now.

Unknown capability versions, absent policies, stale inventory, and ambiguous targets fail closed.
Read-only AgentExplorer operations may coexist with a build only when their declared consistency and
cost allow it. Mutating operations require an exclusive durable lease and prevent build assignment.

TeamCity target selection has a mandatory order. The controller first evaluates the authenticated
principal and project against agent-pool and machine-trust-class policy from one governing Git
revision. Only agents inside that authorized target set enter TeamCity compatibility matching;
requirements can narrow the set but can never widen it or authorize a target. The build persists the
governing revision as provenance before queue admission. A later policy revision does not silently
change the authorization basis of queued, running, or historical work.

### S3. Identity, credentials, and fencing

- Users and service tokens are named principals. Tokens have explicit scopes/permissions, optional
  project and agent-pool restrictions, creation metadata, expiry, last-used time, and revocation.
- Persistent random tokens are stored as one-way hashes. Plaintext exists only at issuance or while a
  pending agent must receive its newly issued credential, and that interval is bounded.
- Credentials never appear in URLs, Git remotes, command arguments where avoidable, ordinary logs,
  build parameters, or definition snapshots.
- Agent authorization and agent authentication remain distinct as in D7/D8. Deleting/revoking a
  registration invalidates its credential; disabling it only affects future scheduling.
- Every dispatched operation carries a unique operation/build ID, target agent ID, accepting
  `session_id`, durable lease/fencing token, deadline, and idempotency semantics. Messages and results
  from superseded sessions cannot mutate current state.

### S4. Transport and initial trust

- Controller-to-agent and CLI-to-controller traffic uses TLS. Elevated agents validate the pinned
  controller certificate fingerprint despite restored clocks, as required by D4.
- Enrollment and setup authenticate installer bytes **before execution** as required by D21. An
  enroll token authorizes enrollment; it does not authenticate downloaded code.
- Agent packages and manifests are hash-verified and atomically installed. Package source, version,
  digest, and rollout actor are audited.
- Certificate rotation needs an authenticated overlap/re-pin ceremony. Silently accepting a new
  certificate after pin failure is prohibited.
- Bootstrap changes remain behind D2/D21's explicit design and freeze process.

### S5. Submitted code is privileged remote execution

- Permission to submit a build is not automatically permission to target every agent. Before any
  TeamCity compatibility selection, authorization evaluates the project and the pool/machine trust
  classes allowed by the governing Git revision. Compatibility is never authorization.
- Persistent physical agents and `clean_policy: none` are protected targets. Only trusted projects and
  principals may run code on them. A compatibility requirement is not an authorization rule.
- A snapshot rollback is cleanup, not a confidentiality or network boundary. It cannot undo data
  already sent elsewhere or side effects against external services.
- Builds receive no controller, Git, agent, provider, or AgentExplorer credentials by default. Secrets
  are resolved just in time from references, scoped to one build/step, masked in output, and omitted
  from immutable YAML and provenance snapshots.
- Elevated interactive test agents are explicitly classified. Until a split-service or privilege
  broker exists, arbitrary submitted code inherits that elevation; policy and target authorization
  must state this honestly.
- On disposable image-backed agents, compromise is contained only after the provider completes the
  correct epilogue. On physical agents, agent self-protection and post-build integrity remain open
  problems; the system must not market `clean-workdir` or `reboot` as strong isolation.

### S6. AgentExplorer disclosure and mutation

AgentExplorer reads are sensitive even when they do not change state:

- Host facts expose only the documented inventory schema.
- `agent-explorer.environment.v1` never transports raw secret values. The agent applies the operator's
  safe-value allowlist, case-insensitive secret-name rules, and irreversible masking before
  serialization; non-allowlisted or sensitive values are omitted or replaced with a non-reversible
  marker, so the controller cannot later reveal them from a snapshot. Environment data is live/on
  demand, bounded, and not an ordinary searchable fact.
- Any future raw-value reveal is a separate versioned capability, such as
  `agent-explorer.environment-reveal.v1`, with its own disabled-by-default policy, high-risk permission,
  live request, audit event, and transport response. Reveal responses are non-cacheable,
  non-persistent, non-indexed, and excluded from logs; adding that capability requires a new security
  review rather than weakening environment v1.
- Process command lines remain live sensitive data and undergo agent-side secret-pattern masking
  before transport. A general environment reveal permission does not imply command-line reveal.
- Process identity is `pid + observed start time`; PID alone is unsafe for later terminate/control.
- Network endpoints, usernames, executable paths, and installed software are permission-protected and
  carry `observed_at`, completeness, and access-denied metadata.
- Inventory snapshots have row, byte, duration, refresh-rate, and retention bounds. Offline data is
  visibly stale.

Mutations start disabled by default:

- Command execution uses structured program/argument/environment fields. Shell interpretation is an
  explicit capability and policy, never an accidental concatenation. Requests have working-directory
  rules, deadlines, output caps, cancellation, and an exclusive lease where needed.
- File browsing starts with controller-configured roots and read-only operations. Every access
  canonicalizes the path beneath an allowed root and defends against symlink, junction, reparse-point,
  hard-link, case-alias, and check/use races. Downloads have byte bounds and audit records.
- Process control revalidates `pid + start time + executable identity` immediately before mutation and
  protects the agent/bootstrap/controller-related process tree.
- Software and machine-state changes require higher-risk permissions, explicit policy, exclusive
  leases, before/after inventory evidence, and recoverable or documented failure behavior.

### S7. Git-backed configuration and secret references

- Configuration mutations create Git commits through the Git/Versioning application service. The
  REST/UI caller supplies intent, not arbitrary Git command text.
- An applied configuration is identified by repository identity and immutable commit/tree ID. A build
  records that revision and the exact resolved definition; moving branches or tags never rewrites
  history.
- Mutations use compare-and-swap against the base revision. Conflicts are explicit and never silently
  overwrite concurrent work.
- Git is invoked without a shell, with bounded time/output, controlled environment, no inherited
  hooks, no repository-local executable filters, and isolated credential material. Repository paths,
  refs, URLs, author fields, and filenames are untrusted input.
- Git credentials are secret references. They do not enter repository config, commit content, remote
  URLs, audit bodies, build logs, or agent parameters. Askpass/credential-helper state is ephemeral
  and controller-local.
- Configuration files contain secret references, never secret values. The security design does not
  prescribe the future secret backend, but resolution must be permissioned, audited, masked, and
  unavailable to code that does not need it.
- Commit-signature verification is an explicit repository policy. When enabled, the controller records
  verification status and allowed signer identity for the applied revision. A signature does not
  replace Vivarium authorization or branch/revision policy.
- A commit created outside Vivarium that changes security-sensitive desired configuration (including
  RBAC, agent-pool/trust policy, AgentExplorer policy, repository trust, secret references, or server
  security settings) is not applied merely because it is reachable from an allowed branch. The
  revision requires an attestation that maps its immutable commit ID to an authorized Vivarium actor
  or review decision. As an explicit alternative, a repository policy may declare every repository
  writer administrator-equivalent for those paths; enabling that policy is itself an audited admin
  action and must present the privilege consequence clearly. Without attestation or this explicit
  admin-equivalence policy, reconciliation refuses the security-sensitive revision. A cryptographic
  Git signature counts only when its signer is mapped to the required Vivarium authority.

### S8. REST and browser security from day one

- REST uses versioned resource paths and the same authorization decisions as gRPC. Authentication is
  not enough: every request checks access to each addressed project, agent, build, artifact, Git
  repository, or operation.
- Mutations accept caller idempotency keys where retry can duplicate effects and optimistic revision
  tokens where lost updates are possible. High-risk operations require an explicit target summary,
  not a wildcard inferred by the server.
- Browser sessions use `Secure`, `HttpOnly`, restrictive `SameSite` cookies, bounded lifetime, login
  throttling, and logout/revocation behavior. Cookie-authenticated mutations require CSRF protection.
- CORS is denied by default. If enabled later, origins, methods, and credentials are explicit; `*`
  with credentials is forbidden. A restrictive CSP and output escaping protect React/Workbench and
  any legacy server-rendered surface.
- Bearer tokens are accepted in the Authorization header, never query strings. Responses and errors
  omit secrets and do not reveal whether an inaccessible object exists.
- Collection endpoints paginate and cap page size. Live watch/stream endpoints have resumable cursors,
  bounded buffers, cancellation, and per-principal concurrency limits.

### S9. RBAC, SuperUser bootstrap, and service accounts

- The User Roles Expert owns the TeamCity-compatible permission catalog and role templates. Security
  requires deny-by-default evaluation, global vs project scope, explicit agent-pool/AgentExplorer target
  scope, and no privilege derived from display names or Git authorship.
- A SuperUser/bootstrap token may be printed to the controller log only while no administrator account
  exists. It is short-lived or claim-once, grants only the first-admin ceremony, is rate-limited, and
  is revoked after successful claim. It is not the permanent admin REST/gRPC token.
- Service tokens are first-class principals, not anonymous shared strings. Creation reveals plaintext
  once; the controller stores a verifier and metadata. Rotation can overlap briefly and revocation is
  immediate for new operations.
- Long-lived build/CI credentials cannot authorize AgentExplorer exec, file access, software management,
  agent authorization, Git credential changes, role changes, or secret reveal unless those permissions
  are separately and explicitly granted.

### S10. Logs, auditing, and untrusted output

Vivarium has three separate streams:

1. **Build/command output:** attacker-controlled bytes, bounded and rendered as text.
2. **Operational logs:** diagnostics for controller/agent health, structured and redacted.
3. **Security audit events:** durable actor/action/target/result evidence.

Audit events include a controller sequence, UTC time, authenticated principal, credential/service-token
ID (not its value), origin, request/correlation ID, action, object IDs, before/after revision or hashes,
result, error category, accepted agent/session/lease where applicable, and linked Git commit. They
cover login and throttling, token lifecycle, role changes, Git mutations/apply, agent enrollment and
authorization, policy changes, build submit/cancel, artifact reads, secret reveal/use, AgentExplorer
reads of sensitive fields, all mutations, provider lifecycle, and agent upgrades.

Audit records never contain raw tokens, passwords, secret values, environment values, full command
output, arbitrary file content, or credential-bearing URLs. Ordinary text logs are sufficient for the
first implementation but are not claimed to be tamper-evident. Rotation, retention, disk budget, and
optional export are explicit. A compromised controller administrator can alter local logs in v1.

Untrusted output is UTF-8-decoded defensively, strips or escapes terminal/browser control sequences,
preserves stream/step attribution, and has byte/line/rate/retention limits. Truncation is recorded as a
structured event so silence is never mistaken for complete output. Service messages remain progress
hints and cannot become authoritative results or audit events.

### S11. Blob, archive, artifact, and path safety

- A blob PUT is committed only when bytes hash to the requested SHA-256. Writes are atomic.
- Possession of a hash is not authorization and raw blob hashes are not public object identifiers.
- An agent GET is authorized only for a hash in the exact build/operation assignment accepted by that
  agent's current fenced session. An agent credential never grants farm-wide blob read access.
- A PUT requires scoped upload authority bound to the owned pending submission, build, or AgentExplorer
  operation. For agent-produced artifacts, authority is also bound to agent identity, accepting
  session/lease, declared artifact context, and configured size limits. A successful hash-verified
  upload yields a receipt; a result may reference only receipts owned by that same work and session.
  A result containing an unowned hash or mismatched receipt is rejected rather than attaching an
  existing farm blob by hash.
- Human and service-principal downloads are authorized through project/build/artifact resources and
  their object permissions. They do not download through a bearer-authenticated raw
  `/blobs/{sha256}` authority, even when they know the digest.
- Uploads and stored data have content-length/streamed-byte, per-object, per-build, per-principal, and
  farm quota controls. Unknown-length streams stop at the configured maximum.
- Archive validation occurs before extraction where possible and limits entry count, path length,
  compressed size, expanded size, compression ratio, special file types, and link target size. Every
  target stays beneath the work directory under platform-specific alias and link semantics.
- Artifact collection revalidates canonical paths after the workload has run; workload-created
  symlinks/reparse points/hard links cannot escape the work directory. Artifact count and total bytes
  are bounded. Download filenames are derived safely and never control server paths or headers.
- Result adapters parse immutable raw artifacts in a bounded, non-privileged context and treat XML,
  JSON, TRX/JUnit, filenames, and test names as untrusted input.

### S12. Abuse limits and availability

Every externally supplied collection or stream has a documented upper bound. At minimum:

- gRPC/REST request bytes, string lengths, map/list counts, and nesting;
- concurrent sessions per agent identity and newer-session replacement behavior;
- enrollment/login/token attempts per origin and identity;
- log bytes per second, total output per operation/build, and controller queue depth;
- blob/archive/artifact object and aggregate sizes;
- inventory rows/bytes, refresh frequency, process/port scan duration, and live fan-out concurrency;
- command duration, output, cancellation grace, process-tree cleanup, and queued-operation deadline;
- Git fetch depth/objects/bytes/time/output and concurrent repository mutations;
- REST page size, watch duration/buffer, and per-principal concurrent operations.

Backpressure is preferred to unbounded buffering. Bounds fail with a typed result and audit event;
security-significant truncation is visible to the caller.

## 5. Current implementation evidence

The following is evidence in the repository at the date above, not a claim about future branches:

| Area | Present evidence | Important gap |
|---|---|---|
| TLS | Agent and CLI pin the controller certificate; the controller serves HTTPS | Safe certificate rotation/recovery and authenticated setup endpoints are not implemented |
| Enrollment | Random enroll tokens are hashed, expire, and are claimed by one agent; persistent agent tokens are hash-checked | Pending auth token is temporarily plaintext in SQLite; installer freeze gate is unproven |
| Session safety | Per-connect `session_id`, durable ownership, accepted assignments, reconnect adoption, and result acknowledgement are implemented/tested | Future AgentExplorer operations need the same general lease/fence contract |
| API scopes | `agent`, `submit`, and `admin` bearer scopes protect current gRPC calls | No users, project RBAC, service-principal metadata, token expiry/rotation, or REST API |
| Panel | Secure/HttpOnly/SameSite-strict cookie auth and ASP.NET authorization are enabled | The permanent admin token is printed at every start; first-admin bootstrap separation and login throttling are absent |
| CSRF | ASP.NET antiforgery middleware protects the component surface | Login/logout explicitly disable antiforgery; the future React REST surface needs a deliberate CSRF contract |
| Blobs | SHA-256 names are validated and PUT bodies are hash-verified before atomic commit | Any valid bearer can GET/PUT any known hash; there are no body/quota/rate bounds |
| Payloads | Extraction rejects traversal, rooted paths, duplicate/type conflicts, Windows aliases/devices, and link pivots | No archive entry/expanded-byte/compression-ratio bound; post-execution artifact link escape needs a separate guarantee |
| Execution | Structured program/args, relative cwd checks, timeouts, cancellation, and whole-process-tree kill exist | Submitted code inherits the agent account; no physical-agent trust-class authorization exists yet |
| Logs | Agent chunks stdout/stderr and controller checks current agent/build ownership | Current in-memory build log grows without a total bound; no durable security audit journal or systematic redaction |
| Private storage | Unix secret files/directories are restricted by mode and tested | Current Windows implementations write files without explicit ACL hardening; platform evidence is incomplete |
| Git | Build definition snapshots are persisted for provenance | There is no controller Git mutation/apply service, credential model, signature policy, or audit correlation |
| AgentExplorer | Agent enrollment, heartbeats, parameters, status axes, and cancellation foundation exist | Environment/process/network/file/exec/software inventory and their permission/policy model do not exist |

Primary code evidence includes:

- `src/Vivarium.Controller/Security/TokenStore.cs`
- `src/Vivarium.Controller/Management/ControlPlaneAuthorizer.cs`
- `src/Vivarium.Controller/VivariumControllerHost.cs`
- `src/Vivarium.Controller/Blobs/BlobStore.cs`
- `src/Vivarium.Controller/AgentHubService.cs`
- `src/Vivarium.Controller/Builds/BuildTracker.cs`
- `src/Vivarium.Agent/PinnedTls.cs`
- `src/Vivarium.Agent/PayloadArchiveExtractor.cs`
- `src/Vivarium.Agent/BuildExecutor.cs`
- `src/Vivarium.Contracts/protos/vivarium/v1/agent_hub.proto`
- `src/Vivarium.Contracts/protos/vivarium/v1/control_plane.proto`

## 6. Target verification evidence

Security-sensitive slices are incomplete without automated negative evidence appropriate to risk:

- permission matrices across UI, REST, and gRPC, including object-level cross-project/agent denial;
- service-token expiry, rotation, revocation, and scope-escalation denial;
- CSRF, restrictive CORS, cookie flags, login throttling, and stored-output escaping;
- spoofed agent, stale session, replayed enrollment, duplicate operation, lease-loss, and reconnect races;
- malformed/oversized protobuf and REST bodies, log floods, inventory floods, and bounded backpressure;
- traversal, symlink/junction/reparse/hard-link races, Windows device aliases, ZIP bombs, and artifact
  escape after workload execution;
- Git ref races, hook/filter suppression, credential leakage, conflict/CAS behavior, malicious names,
  external security-sensitive revision attestation/admin-equivalence, and optional bad/untrusted
  signatures;
- agent-side environment allowlisting and irreversible pre-transport masking, command-line masking,
  and secret redaction in logs, errors, snapshots, audit events, and Git diffs;
- cross-platform secret-file ACLs and service-account boundaries on Windows, Linux, and macOS;
- privileged physical-agent targeting denial for unauthorized projects and principals;
- blob/object IDOR, assignment-scoped agent GET, owned-work upload receipts, result receipt mismatch,
  content hash mismatch, quota exhaustion, and project/build/artifact-scoped downloads.

Security tests belong in the same tier as the mechanism they protect. Protocol compatibility tests
must include the previous released agent once that release gate exists (D20).

## 7. Non-goals

- Providing a secure sandbox for hostile code on the same elevated physical OS. That requires a
  stronger process/VM boundary than the current architecture supplies.
- Protecting against a compromised controller host, hypervisor administrator, OS root, or stolen
  endpoint with access to already-decrypted secrets.
- Replacing Git hosting authorization, protected branches, review policy, backups, or signing-key
  governance.
- Claiming rollback reverses network exfiltration or external side effects.
- Building a tamper-proof audit ledger or SIEM in the first implementation. Structured bounded logs
  are the initial evidence, with their limits stated honestly.
- Exposing arbitrary remote shell/file/system-management functionality merely because the agent runs
  elevated. Each operation remains a separately reviewed capability.

## 8. Open questions requiring decisions

1. What trust classes and project-to-agent-pool grants protect persistent physical and elevated UI
   agents from ordinary build submitters?
2. Should the long-term agent split into a low-privilege runner plus a narrow elevated platform broker,
   and how can that evolve without violating the bootstrap freeze contract?
3. How are controller certificate rotation and disaster recovery performed without teaching agents to
   accept an unauthenticated new pin?
4. Is commit signing optional per repository or required for protected projects, and which key types
   and trust store are in scope?
5. Which local secret store ships without adding an external service dependency, and how are secret
   values encrypted, backed up, rotated, and scoped to a step?
6. What initial file roots, command allow/deny policy, process-protection list, and software-management
   transaction model are acceptable on physical agents?
7. What default log, inventory, Git, blob, artifact, and command quotas fit a small single-node farm,
     and which are hard safety ceilings versus administrator-tunable policy?
8. When does audit evidence need external append-only export or signatures, rather than honest local
     rotating logs?
9. Which concrete scoped-grant representation will implement D24's object-authorized blob flows without
   breaking content-addressed deduplication and offline operation?
