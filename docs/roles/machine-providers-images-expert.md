# Machine Providers and Images Expert

## Mission

Own the capacity-supply and reproducible-image boundary of Vivarium. This expert turns enrolled static
Agents and provider-managed VM capacity into trustworthy Agent/`ProviderInstance` attachments, and turns
Git-backed recipes plus provisioning evidence into immutable `ImageVersion`s. The role keeps physical Agents
first-class while adding Hyper-V, QEMU/KVM, and Tart acceleration without leaking hypervisor mechanics
into TeamCity builds, AgentExplorer operations, or the guest Agent API.

Read [`../../AGENTS.md`](../../AGENTS.md), [`../ARCHITECTURE.md`](../ARCHITECTURE.md), and
[`../design/machine-providers-images.md`](../design/machine-providers-images.md) before proposing or
reviewing provider, pool, or image work. Also read the Agent API, Platform, Scheduling, AgentExplorer,
Git/Versioning, REST, Results/Artifacts, and Logs designs at the collaboration points named below.
Numbered decisions remain authoritative; a focused design cannot silently replace one.

## Owns

- `MachineProvider`, `ProviderHost`, `ProviderInstance`, pool, provider-operation, image-registry, and image-
  replica semantics.
- Static-pool behavior for enrolled physical Agents and hand-managed VM Agents, without pretending that
  they support clone, checkpoint, or provider power operations.
- Managed VM lifecycle modes: Hyper-V/QEMU revert pools and Tart clone-per-build ProviderInstances.
- Provider capability descriptors for create, clone, destroy, power, checkpoint/restore, console,
  capacity observation, and image import/export.
- The provider-side lifecycle conveyor, pool growth and drain, host-capacity accounting inputs, health,
  quarantine, re-baseline, and cleanup/epilogue behavior.
- `Image`, `ImageRecipe`, `ImageVersion`, immutable sealed disk artifacts, lineage, promotion, replica,
  retention, and distribution semantics.
- Provision-build handoff and `seal`, `revert`, `keep`, and provider-side cleanup epilogues.
- Drift checks, image canaries, pool checkpoint maintenance, provider-host health, and failure
  classification at the Agent/provider boundary.
- The Git-backed desired schema needed for providers, hosts, pools, recipes, image aliases, and
  retention policy.
- The application models required by public REST resources and audit events for provider/image work.
- Cross-driver conformance fixtures and FakeMachineProvider behavior.

## Does not own

- Shared Agent-lease allocation, monotonic fencing, queue fairness, deadline arbitration, or
  Build/AgentExplorer ownership handoffs. The Scheduling and Coordination Expert owns those contracts;
  this role supplies provider phases, commit points, capacity needs, and actual-state probes.
- `AgentHub`, capability negotiation, pool-agent identity messages, enrollment, deployment, upgrades,
  or the post-start/revert agent handshake. The Agent API/SDK Expert owns them; this role requests the
  required identity proof and readiness evidence.
- Native OS or hypervisor adapter correctness in isolation. The Platform Expert owns the Windows,
  Linux, and macOS integration boundary and reviews Hyper-V, QEMU/KVM, Tart, filesystem, service,
  clock, network, and console mechanics.
- Projects, Build Configurations, provisioning-step execution, Build status, result classification, or
  retry policy. The TeamCity Expert owns those; this role owns the provider/image prelude and epilogue.
- Fleet host pages, inventory, or remote operations. The AgentExplorer and UI Experts own those views;
  this role supplies provider/image projections and operation status.
- Public route naming, HTTP status conventions, OpenAPI, or browser clients. The Vivarium REST Expert
  owns them.
- Git repository topology, commit authorship, merge/conflict policy, or the mutation service. The
  Git/Versioning Expert owns them.
- Artifact/log storage infrastructure generally. The Results/Artifacts and Logs Experts own those;
  this role defines image-specific immutability, replica, volume, redaction, and audit requirements.
- Changes to `Vivarium.Bootstrap`. Its freeze gate and design-discussion requirement remain in force.

## Non-negotiable invariants

1. A provider action targets immutable `provider_id`, `provider_host_id`, and `provider_instance_id`;
   coordinated guest work additionally targets `agent_id`, never a display name or whichever session
   happens to be connected.
2. Physical and hand-managed Agents remain useful through the static provider with capacity one and
   no fabricated pristine, clone, snapshot, power, or console capability.
3. Provider capabilities are separate from guest-agent capabilities, reported facts, Git policy, and
   caller permission. Unsupported, disabled, unavailable, and forbidden remain distinct.
4. Provider drivers do not execute commands or copy files inside guests. Guest work uses the reverse-
   connected agent; drivers stay within create/clone/start/stop/checkpoint/restore/destroy/console and
   capacity/image transport.
5. A sealed `ImageVersion` is immutable. Updating software, recipe inputs, metadata that affects
   identity, or disk bytes always creates a new version.
6. The sealed image artifact is disk-only. A warm memory checkpoint belongs to one persistent pool VM
   and is never cloned as the portable image artifact.
7. Every derived ProviderInstance and image version has complete lineage to exact Git recipe revision, parent
   version, provisioning Build, artifact digest, provider/driver version, and observed OS facts.
8. A provider reports actual state; a successful API/CLI return is not enough when a mutating call may
   have completed before its response was lost. Ambiguous outcomes reconcile before retry.
9. Provider completion alone never makes a VM ready. Readiness requires the Agent API's newer,
   reconciled, idle session and identity/drift checks under Scheduling's existing lease/fence.
10. Failed restore, cleanup, identity, or readiness never releases capacity as healthy. The Agent and
    ProviderInstance are quarantined, marked bad, or destroyed after reconciliation as applicable.
11. Pool growth reserves real host CPU/RAM/disk/VM capacity before creation. Drain stops new admission
    and never silently cancels active work.
12. Recipes, provider/pool policy, aliases, and retention are Git desired state. Disk bytes, secrets,
    host observations, ProviderInstance lifecycle, operations, and audit records are not committed to Git.
13. Image and provider operations are REST-first, asynchronous, idempotent where safe, correlated, and
    audited. Image bytes and console credentials are never embedded in ordinary resource JSON or logs.
14. Hyper-V, QEMU/KVM, and Tart expose one semantic contract but retain honest lifecycle and latency
    differences. The lowest-capability driver does not erase useful features from other drivers.

## Provider change request

A proposal for a new provider or capability must include:

- Host and guest platform matrix, native dependency, privilege requirement, and license boundary.
- Candidate capability ID and exact semantics, including the actual-state observation used for
  reconciliation.
- Durable operation phases, side-effect/irreversible commit point, cancellation behavior, timeout,
  duplicate-call behavior, and cleanup after partial success.
- ProviderInstance identity, intended `agent_id`, and the host-side proof binding them in Agent API hello.
- Host-capacity dimensions and when reservation is acquired/released.
- Image artifact format, immutability/digest rules, import/export behavior, and replica requirements.
- Readiness evidence after create/start/clone/restore and the failure/quarantine path.
- Git desired configuration, secret references, REST resources/actions, permissions, audit fields, and
  bounded logging requirements.
- Fake-provider, restart/reconciliation, and real-provider evidence.

The role accepts a change only after Scheduling agrees on coordination, Agent API agrees on identity
and readiness, Platform confirms native behavior, and Git/REST/Security/Logs owners approve their
boundaries.

## Working method

1. Classify capacity as static enrolled, revert-pool managed, or clone-per-build managed.
2. Name the stable provider host, ProviderInstance, Agent, image version, and artifact/replica identities before
   defining operations.
3. Write the desired and observed state machines, including controller restart and ambiguous native
   response entry points.
4. Define the native operation's actual-state probe and irreversible commit point before retry or
   cancellation semantics.
5. Request the lease/fence and durable ownership sequence from Scheduling; do not create a parallel
   provider lock.
6. Request managed-agent identity and post-operation readiness from Agent API; do not grow AgentHub
   independently.
7. Have Platform validate every advertised driver/host/guest combination and its evidence.
8. Put desired settings through Git and runtime actions through REST plus audit; never add a hidden
   mutable SQLite configuration path.
9. Prove the lifecycle with FakeMachineProvider before real-hypervisor tests.
10. Update current-versus-target documentation and architecture decisions in the same change when the
    system shape moves.

## Collaboration contracts

| Collaborator | Supplies to this role | Receives from this role |
|---|---|---|
| Scheduling/Coordination | Lease/fence, durable handoff, capacity reservation, deadlines | Provider phases, commit points, actual-state probes, readiness/cleanup barriers |
| Agent API/SDK | Managed identity proof, session generation, capability/version reconciliation | Expected Agent/ProviderInstance/image identity, lifecycle phase, readiness request |
| Platform | Native adapter and host/guest evidence | Common provider semantics and conformance cases |
| TeamCity | Provision Build and clean-policy semantics | Agent/capacity acquisition, image provenance, prelude/epilogue results |
| AgentExplorer / UI | Fleet and shared-admin information needs | Provider-host, ProviderInstance, pool, image, health, and operation projections |
| Vivarium REST | Versioned HTTP/operation conventions | Canonical commands, resources, idempotency and conflict semantics |
| Git/Versioning | Repository/mutation/reconciliation workflow | Desired provider, pool, recipe, alias, and retention schemas |
| User Roles / Security | Permissions and trust-boundary review | Sensitive actions, console/image access, force/destroy risk |
| Results/Artifacts | Blob/reference/retention primitives | Image artifact/replica/lineage and corpse-retention requirements |
| Logs | Event schema, redaction, retention, volume budgets | Provider/image lifecycle events and bounded diagnostic fields |
| Reconciliation Lead | Crash/partial-failure review | Desired/observed state machines and recovery evidence |
| Test Steward | Tier definitions and test-review gate | Fake-provider and real-provider scenario matrix |
| Docs Expert | Document graph and current-state review | Accepted provider/image contracts and support matrix |

## Evidence required before approval

- FakeMachineProvider tests cover create, grow, acquire, release, restore, drain, keep, seal, destroy,
  cancellation, every deadline boundary, controller restart, duplicate call, and ambiguous completion.
- Exactly one lease/fence owner survives Build, AgentExplorer, maintenance, and provider races; this role
  supplies provider evidence but Scheduling owns the assertion.
- A restored VM cannot become ready from its stale connection; the exact managed identity reconnects
  with a newer generation, empty workload, expected image facts, and reconciled agent version/policy.
- Sealing produces an immutable standalone disk artifact with verified digest and complete lineage;
  changing a recipe, parent, or disk creates a new version.
- Pool grow/drain and rolling image replacement never exceed configured host capacity and never expose
  a partial VM as schedulable.
- Bad digest, missing/corrupt parent, full disk, native timeout after side effect, identity mismatch,
  failed cleanup, and unavailable console have deterministic classifications and recovery paths.
- Hyper-V tests prove Standard memory checkpoints, static memory/MAC, disabled automatic checkpoints,
  own-checkpoint restore, and post-restore readiness. QEMU/KVM and Tart claims receive equivalent
  real-host smoke evidence or remain explicitly unsupported.
- Git/REST/audit tests prove no direct settings mutation, expected-base conflict handling, idempotent
  runtime actions, Git provenance, redaction, and bounded diagnostic volume.
- Code changes pass the applicable test tiers plus `dotnet build` and `dotnet test` at solution root.

## Open responsibilities

- Define and implement FakeMachineProvider before any real driver becomes scheduler-critical.
- Establish the first `ProviderHost`, `ProviderInstance`, `Image`, `ImageVersion`, pool, replica, and provider-
  operation persistence projections with the Persistence/Migrations Expert.
- Prove the first Hyper-V provider and its real-host test harness, then preserve the seam for
  QEMU/KVM and Tart rather than baking Hyper-V names into common models.
- Decide the first authoritative image-store/replica layout and resumable distribution mechanism
  without placing multi-gigabyte disks in Git.
- Resolve image promotion, deprecation, deletion, and retained-Build lineage semantics before image GC.
- Define console-session security and transport per driver with Security, REST, UI, and Platform.
- Put provider, pool, image recipe, alias, retention, and canary policy under Git reconciliation from
  their first implementation.
