# Selected early C#/Git activation contract

Status: **Specified P0 contract; production implementation and acceptance remain due.**
Owners: SS-CFG with Git/Versioning, Persistence, IAM, Security and Agent API; early integration
#113/#114/#115 and #116/#117/#118, full authoring #122/#123/#124. D43/D44 govern this contract.
This freezes the early source/activation obligations, not the complete later language/field SDK.

The fresh Host implementation is in progress under R0, not accepted. Its native installation-wide
activation action is `POST /api/v1/configuration-changes/reconcile`: it journals the authenticated
operator and the already accepted control head, returns `202` with a status location, and lets the
Host coordinator perform the evaluation/apply work. It does not edit Git, advance refs or acknowledge
a compatible settings mutation. Current global `edit_project` and `create_sub_project` permissions
are required for this whole-installation action, including replay/activation. Losing those rights
cannot turn the pending managed operation into an external import. Individual compatible authoring
operations and externally delegated automatic imports remain with their own contracts.

Private repository locations are optional `DefinitionSources` in the existing Host Chiral node,
with an explicit control repository ID/path and local settings repository ID/path/trust-reference
registry. Desired declarations remain in Git. The fresh repository manifest retains
`apiVersion: vivarium.io/v1alpha1`, `kind: Repository` and `id`, with `spec.schemaVersion: "2"` and
one `documentKinds` entry (`Project` for control, `Settings` for a bound source). This selects the
server-owned definition contract v1 and pinned evaluator; it is separate from the legacy schema-1
Agent/User/RoleBinding manifest. Local binding uses the configured trust reference and an empty
credential reference; remote acquisition/credentials are not implemented by this local adapter.
The initial operational bounds are 16 Projects, 4096 reserved-tree files, 4 MiB reserved-tree bytes,
256 KiB per source input and 1 MiB materialized catalogue; these are resource bounds, not licensed
capacity. No acceptance of a full public SDK or complete native/compatible resource family follows.
Each evaluator also has a trusted 30-second PID-1 timeout inside its private PID namespace, so
coordinator loss cannot leave author code running indefinitely. Recovery reconciles only journalled
invocation identities, rejects interrupted evaluation and retains active/LKG; already materialized
evaluations retain their original compiler/contract identity and are not executed again.
The journal pins each selected settings member before evaluating its code. Restart cannot silently
resolve that member to a newer head or recompute a completed phase with changed compiler inputs.
Recovery owns journalled invocation identities even if the optional source resource is renamed or
removed. It cleans those containers and rejects pending operations for the removed owner rather
than adopting them under the new resource. The compiler contract fingerprint includes the Host
module build identity, covering trusted project/scaffold changes as well as embedded declarations.
The activation transaction also projects stable positive PostgreSQL Project/BuildType keys and
their exact owner relations. Per-BuildType definitions are immutable, foreign-key-backed members
of the materialized revision; rename updates the current index without replacing identity or old
definitions. Removal from active use retains identity/history. The selected R0/PILOT path does not
silently convert a retained configuration to a template or move it between Projects; those operations
retain their separately due reference contracts.

## Selected source boundary

The control manifest may additionally declare `Agent`. Canonical
`.vivarium/agents/{agentId}.yaml` documents retain the existing v1alpha1 `Agent`/`spec.enabled`
shape; they are controller-only desired policy, never evaluator inputs or build submission.
Their effective values are retained in the immutable catalogue snapshot and projected to registered
Agents in the same activation transaction. Changes additionally require current global
`enable_disable_agent`. Predeclared Agents receive the accepted projection when they enroll.
Removing these declarations is rejected until the owning removal contract is implemented.

R0 starts with a managed-local control repository on its configured authoritative ref. Its Project
and BuildType are real catalogue identities. The selected declarations use
`.vivarium/projects/{projectId}/Control.g.cs` for each Project's control ownership and
`.vivarium/unbound/{projectId}/Settings.g.cs` plus `UserCode.cs` for initially
unbound settings. A bound source selects `.vivarium/settings/Settings.g.cs` and its sibling
`UserCode.cs` at the exact selected settings revision. These paths are the proved early packaging;
the coordinator copies each selected control declaration to the evaluator's fixed `Control.g.cs`
input name, as in the single-Project study. This permits P and protected Q in one control repository
without duplicate evaluator entry types or a second desired authority. User project/MSBuild/NuGet
files are not the evaluator's build project. A manifest/schema version
selects the server-owned definition contract/compiler image; an unknown version rejects activation.

The early typed declaration shape is the one compiled in the
[source study](../../tools/definition-proof/Model.cs): Project ID/parent/name/description and optional
binding; Settings owner/source/project parameters; Configuration ID/name/ordered steps/parameters,
requirements, snapshot/artifact relations, timeout and triggers. R0 uses only its source-free
single process/parameter subset; PILOT uses the separately proved explicit T3 recipe fields.
Computed declarations and typed edit intent remain C#, read back by compilation. The production
package/namespace may differ from the study's explicitly experimental `DefinitionProof`; that does
not authorize different field semantics or another persisted desired-settings representation.
New field categories require their independently established contract before exposure.

Control Git owns Project parent/identity, source binding, trust/credential **references** and explicit
synchronization policy. Effective source ownership follows D44: inherited follows the nearest explicit
selection; disabled selects the Project's local control-Git C#; enabled binding selects that settings
source. A foreign parent declaration cannot seize an independently local child. Ordinary inherited
parameters/templates are resolved separately; disabling synchronization does not flatten them.
Preparing the destination source does not activate it or create another accepted field owner.

Both compilation and execution use D43's isolated Linux/Docker boundary. The trusted coordinator
selects exact committed bytes, compiler/dependency image and bounded context; it passes no Host
secrets, mounts, ambient environment, author build hooks or network. Nonzero/timeout/OOM/oversized/
malformed output is rejected, with bounded redacted diagnostics and owned-container reconciliation.
There is no Host execution fallback. Returned data is untrusted until Host validation completes.

## Current authority, identity and validation

For a managed command, record its actual actor and target in the durable operation before ref
mutation. Evaluate current PG authority for the complete proposed Project/source change, then again
at activation. Revocation or account disable before activation prevents the actor's pending proposal
from becoming active. A Git repository writer does not thereby acquire SYSTEM_ADMIN, cross-Project
mutation or access to private credential values. Externally accepted revisions reconcile under the
configured source/Project trust delegation, never a historical commit author's current permissions.
Changing that delegation is an authorized operation. This is one IAM authority, not Git RBAC.
Retain operation origin with accepted commits: retry/reconciliation cannot relabel a rejected or
pending managed proposal as an external import to evade its current-actor checks.

Validate the complete selected revision set: supported schema; bounded allowed paths and bytes;
unique stable Project/BuildType/item IDs; actual parent/source/producer/template references;
ownership boundaries; literal versus computed values; secret-reference fields; and the selected
execution plan. Reject duplicate IDs, conflicting owners and platform path/case collisions rather
than silently choosing a declaration. Never hash/truncate IDs or manufacture a Project from a run
request. Compatible name/ID errors retain their reference operation's response when that operation
is exposed; these internal validation outcomes are not a guessed compatible error envelope.

## Durability and completion fence

Keep these identities distinct and inspectable: candidate commit/revision set, accepted authoritative
head, active projection and last-known-good revision set. Record immutable evaluated declarations,
effective plan, source/context inputs actually used, compiler/image/schema identity and hashes with
the revision set. C# need not be deterministic: cold reevaluation is not permission to overwrite a
previously retained plan or Build input.

1. Prepare the validated candidate and journal the operation, expected old refs and exact intended
   revision set. A source compile or destination push is preparation, not activation.
2. Advance only the intended authoritative refs with expected-old CAS. Rediscover an already
   accepted operation/commit on retry; a divergent foreign ref is a conflict, never a force reset.
3. Under the affected catalogue activation transaction/fence, recheck ownership/delegation/current
   actor authority and the observed accepted refs. Atomically persist the complete projection,
   materialized plan, active and last-known-good pointers, operation outcome and application audit
   in PG. No partial Project/BuildType graph becomes schedulable.
4. Applied readback means that transaction committed. A new catalogue run may use only a verified
   active plan; its exact definition/input relation is retained independently of later changes.

For R0 and PILOT's C#/Git preparation there is **no compatible HTTP mutation** to acknowledge:
Git push succeeds as Git, while the operator separately observes activation. Native catalogue reads
expose the active revision and applied values; the configuration-change/operation read exposes the
candidate's pending/rejected/applied outcome under the existing native REST resource contract.
An unapplied source is never reported as the active configuration. A failed first candidate leaves
the catalogue unavailable, not an empty successful smoke definition.

Where a later native or compatible field mutation is exposed, its own frozen reference contract
determines the HTTP success/error and read-after-write fence. Do not translate internal `APPLIED`,
`CONFLICT` or a Git result directly into a universal compatible 200/409/202. The selected reference
mode observations include asynchronous initialization/read-only rejection and a control-only update
without a new source revision; each retains its own completion observation. No new mandatory
compatible-client header, unconditional enqueue-time settings snapshot or generic CAS policy follows.

## Failure, handoff and restart

Before accepted refs advance, rejection/failed CAS leaves active/LKG unchanged; an orphan candidate
commit is not active. After refs advance but before PG apply, retain a durable pending operation and
the old active/LKG projection. Restart inspects the journal and exact refs, revalidates authorization
and ownership, and either completes that same operation or records conflict/rejection. It neither
creates a second accepted owner nor rewinds foreign history. Record failed evaluation/handoff with
actionable redacted diagnostics; a runtime retry cannot bypass a now-invalid trust/permission check.

An unbound-to-bound handoff prepares the destination first, journals both selected revision members,
then accepts the control binding and atomically activates the complete selected set. Disabling
synchronization materializes local declarations with stable IDs while preserving inherited relations.
Failed transition retains the previous active set even if the newly accepted desired head is pending
or rejected. That distinction is not a writable DB override of Git. New valid edits can repair the
desired head; rollback is a new validated revision, not destructive reset of published history.

Every existing Build retains its immutable actually resolved plan/revisions/Agent workload relation.
Host restart, source rename/edit, destination failure, later nondeterministic evaluation or access
changes cannot rewrite them. Current access still governs reading the retained evidence.

## Independent evidence and due implementation

The [mode/context observations](../../.workspace/workstreams/coherent-refactor/us013-mode-concurrency.md)
and [child-owner observations](../../tests/Vivarium.Tests/TeamCity/Reference/child-owner-observations.json)
constrain reference processing. The [physical target](../../tests/Vivarium.DefinitionProof/README.md)
proves bounded source editing/readback, real Git CAS/PG handoff crash recovery, D43 isolation and
isolated evaluated plans in catalogue/Build relations. Their original receipts retain actual inputs,
execution identity, platform and single-worker/study limits. They do not prove production current
authorization, concurrent activation or any compatible HTTP mutation response.

The selected [R0 contract variants](../../.workspace/workstreams/coherent-refactor/r0-contract-variants.tsv)
join unbound activation, rejected candidates, handoff recovery and immutable history to the existing
R0 contribution. Actual owning services/Git/PG/Docker/Agent and negative/restart acceptance are due
in R0/PILOT before exposure. Full later SDK, language/mode/field and remote-source variants remain
with their explicit source-gap owners; they do not reopen these selected early ownership/fence rules.
