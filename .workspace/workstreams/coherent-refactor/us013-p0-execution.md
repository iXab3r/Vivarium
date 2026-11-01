# US-013 autonomous P0 execution

The owner assigned execution with “let's work” on 2026-11-01 after resolving the seven boundaries in
the [delivery plan](t3personal-pilot.md#confirmed-autonomous-e2e-boundaries). Lead: Story Delivery;
co-held TeamCity, Git/Versioning, Persistence, REST, Testing Architect, Test Steward and Docs;
Security perspective also covers the known dependency fixes. This continues
#110/#111/#112 with #32/#34; no new task graph, public SDK or product state authority is introduced.

**State: P0 in progress.** These are real reference observations, strict replay controls and a bounded
C# source experiment, real Git/PG physical study and two legacy lifecycle repairs. The replacement
installation, UI and full T3 chain remain NOT_AVAILABLE.
No P0/R0/PILOT gate, independent review or main merge is accepted here. The original fifteen
materialized conformance cases retain their identities; observation labels are not new frozen cases.

The subsequent [literal T3 settings proof](us013-recipe-representation.md) fills explicit recipe-field
omissions while preserving this original smaller computed/recovery fixture and its historical receipts.
It does not resolve implicit defaults, all computed field categories or full workload adaptation.

## Independently observed reference behavior

The explicit `vivarium-teamcity-lab-202613` profile targets the separate loopback TeamCity
2026.1.3/build 222742. Acquisition uses the pinned CLI source and its ordinary credential store,
permission gates and transport. Fixed acquisition-only projections/options are identified separately
in the [literal observation corpus](../../../tests/Vivarium.Tests/TeamCity/Reference/us013-observations.json).
Neither installed nor acceptance-client code was changed. No production TeamCity mutation, existing
fleet Agent or production T3 repository/publication was used.

| Observed facet | Actual result and qualification |
|---|---|
| Blank/unbound authoring | A Project and BuildType can exist without a product/settings binding. Successful creation/readback is distinct from later Versioned Settings initialization. |
| Parameter ownership | A Project value is reported `inherited:true`; a local PUT produces the local value with `inherited:false`; DELETE returns 204 and exposes the inherited value again. |
| Ordered replacement | PUT of two steps, their reversed order and then one step changes the effective collection accordingly. In this pinned typed-client request, supplied local item IDs are not sent; the server allocates new runner IDs. This is not a universal claim about all replacement bodies. |
| Configuration collision | Creating the same BuildType ID returns 400 and preserves its original readback. |
| Ordinary execution | Real separate Linux Agent work reaches queued/running/finished under one numeric Build ID. Queued status is absent; healthy running SUCCESS is not completion. A real exit 7 produces finished/FAILURE. |
| Cancellation | Explicit-false queued and running cancellation produce retained finished/UNKNOWN with cancellation metadata. The running request can return while the Build is still running/UNKNOWN; observed terminal completion is a separate fact. Repeat, races and denied variants remain separate obligations. |
| Artifact bytes | Actual command-produced files are published and downloaded through the normal CLI. The saved bytes, lengths and SHA-256 agree with the captured HTTP entities; no package or T3 application was run. |
| Current definition versus source collection | In the controlled disabled-Agent fixture, all absent/true/false `freezeSettings` variants executed the earlier server-defined command. In the separate Git-source fixture all three consumed the later source commit and its actual file bytes. These distinct observations do not establish a universal flag default, assignment fence or Versioned Settings behavior. |
| Two-way generated Kotlin | Enabling synchronization commits generated source before the initial source has necessarily applied. REST can return 500 ReadOnly during that interval; subsequent readback remains unchanged. After initialization, an allowed parameter edit is committed to Git. |
| Editing disabled | In the same controlled context `allowUIEditing=false` also rejects the tested REST parameter PUT with 500 ReadOnly, preserving its prior value and Git source. This is an observed REST result, not an inference from UI restrictions or a current-permission denial. |
| Manual/computed Kotlin | A manually committed Kotlin expression yields `computed-mode`. Subsequent REST edit yields `managed-override` through a generated patch while preserving the manual source; DELETE yields inherited `base` and a removal patch. Source files/commits and effective readbacks are separate evidence. |
| Actual tiny chain | Release 35 uses Windows 34/Prepare 33 and publishes independently downloaded, compared bytes. Repeat Release 38 reuses the same producers under explicit successful-only reuse settings. Composite 42 selects Release 38 and has no Agent. |
| Queue optimization | Unkeyed enqueue returns 45, then 48; a later GET through locator 45 returns Build 48. The [worked model](us013-build-model.md) preserves requested/resolved/physical identity rather than assuming one unchanged returned ID or transport deduplication. |
| Disabled-Agent exception and historical cancel | Explicitly targeted suitable Prepare 49 succeeds while Agent reads remain disabled. A tested after-finish cancel returns 400 and the original success stays unchanged. No universal cancellation/idempotency rule follows. |

The first manual-code probe raced the server's own pending settings commit. The server explicitly
skipped the older external revision. A later probe waits for the observed commit/application outcome,
including the legitimate “repository is up-to-date” no-new-commit result, before the external edit.
Preserve those diagnostic attempts; they are not product RED or a reason to change the expected value.

The reference Agent is a newly owned container using the official
[minimal Agent image](https://github.com/JetBrains/teamcity-docker-images/blob/master/dockerhub/teamcity-minimal-agent/README.md),
pinned to digest `sha256:f711ef16fd62a46bc6e5d3e07f0fcd0eaf7c3c8f905fc4500135a58d5ad05765`.
The source/settings Git daemons run only on an owned Docker network with no published ports. One
serves an owned read-only source fixture; the settings fixture explicitly permits owned test pushes.
The owned Agent registration/containers/Git daemons/network have now been removed; the original lab
server remains ready with zero Agents and no queued/running work. CLI Update/Admin/Credentials gates
are off; retained Agent credential material is owner-private. Cleanup readbacks are retained with the
execution receipt. None is a Vivarium R0 Host or Agent claim.

## Bounded C#/Git representation result

Run the checked-in [experiment](../../../tools/definition-proof/README.md):

```powershell
python tools/definition-proof/probe.py
```

This compiles real C#, edits typed managed declarations, records actual Git commits and evaluates
their exact source bytes in separate bounded children. The expected parameter values follow the
independent reference workflow: computed `env.MODE=computed-mode`, local `managed-override`, then
inherited `base`. A C#-looking string remains literal data. The representative three-definition
seed also retains separate source-root metadata, ordered runners, requirements, snapshot/artifact
relations, selection strings, 90/90/15 timeouts and the used trigger shape.

| Field/source | Candidate owner and demonstrated change |
|---|---|
| Project ID, parent, name, description and binding/trust/credential **references** | `Control.g.cs` in the control repository. No credential values or identity/RBAC declarations. |
| Initially unbound settings and user code | `.vivarium/unbound/T3Personal/Settings.g.cs` and `UserCode.cs` in control. A blank Project needs neither a product binding nor a fictitious VCS root. |
| Bound product settings and user code | `.vivarium/settings/Settings.g.cs` and `UserCode.cs` in the product repository. Prepare these first, then change the control binding and remove the unbound copies. IDs and represented configuration values remain unchanged. |
| Managed fields | Deterministically rendered C# literals and explicit named expression references. Read declarations by compilation; no arbitrary-code AST rewrite or persisted JSON/DB override. |
| User computation | Separate C# source selected from the same owning Git revision. The tested managed edits leave its committed bytes unchanged. |
| Evaluated output | Immutable experiment output for the selected revision pair; never another editable desired-settings store. |

The experiment demonstrates source representability and reevaluation of a prior usable revision,
including invalid C# and duplicate-identity rejection. It **does not** implement native/compatible
HTTP editing, real current authorization, PG projection, Host activation, Git-ref/PG recovery or a
cross-repository activation saga. Separate child processes are not an OS sandbox. Its files/types
are candidate proof outputs, not adopted public paths or an already frozen SDK. Templates, all
computed-field categories, full recipe translation and actual activation/recovery remain due.

## Physical revision-set recovery experiment

The separately invoked [NUnit target](../../../tests/Vivarium.DefinitionProof/README.md) now compiles
the actual control source before choosing its settings owner, then compiles those exact committed
settings/user-code bytes. It uses new bare Git copies and real owned PostgreSQL **17.10**, not a
fabricated validation descriptor passed into the legacy control-only SQLite reconciler.

All **eight** physical tests pass with zero skips. Four kill an actual worker process at durable
intent/before-product-ref, between product/control refs, before projection, and after uncommitted
projection changes. Real Git lock files and PostgreSQL row/table locks establish the boundaries;
fresh worker processes resume the same intent. The prior projection remains usable until the final
transaction; selected members, derived plan, history and application audit commit together, once.
The other tests verify ownership transfer without configuration-ID drift, invalid external C#/duplicate
identity rejection with the old projection retained, ref-writer interference, literal edit/inherited
reset, cold successful retry and PostgreSQL restart on its durable volume. The freshly regenerated
source fixture retains its own exact input hashes and effective-plan expectations.

This is a **single-worker physical feasibility result**, not production Host activation, compatible
mutation semantics, authorization, Chiral integration, concurrent-writer fencing or normal-checkout
dirty-state recovery. Its schema is experimental and has no migrations/public authority contract.
PostgreSQL stores immutable derived plans and actual selected members, not editable desired settings.
The first diagnostic run exposed invalid multi-command positional SQL; the next passed seven tests
but exposed Docker's dynamic host-port change after restart. Both harness defects are fixed; their
failed logs remain distinct from the final eight-pass result and from product REST RED.

All owned PostgreSQL containers, volumes and networks are removed after the final run. No credential
is saved in the resource descriptors or receipts. Separate trusted-code processes are not a sandbox.

The known dependency debt is also addressed in this checkpoint: explicit patched transitive pins
select Microsoft.OpenApi **2.7.5** for the controller and System.Security.Cryptography.Pkcs **10.0.13**
for Cake/NuGet. These follow the publisher's
[OpenAPI advisory](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc) and
[CMS advisories](https://github.com/advisories/GHSA-2fmv-rv2c-59x7),
[signature-processing advisory](https://github.com/advisories/GHSA-846f-f764-76mx).
Root restore/build now report zero warnings/errors; this is dependency repair, not an independent
Security/Adversarial milestone verdict.

A subsequent root run exposed a legacy upgrade-cancellation test timeout (458 passed, one failed).
Its isolated retry passed, but inspection identified an invalid busy fixture: direct registry-only
`TryBeginBuild` contradicted the real idle Agent's heartbeat and could trigger D31 quarantine. Both
affected upgrade fixtures now run actual bounded, file-gated workloads and require a matching busy
heartbeat before exercising drain/cancel. Real terminal results release capacity; no production
heartbeat/quarantine or Bootstrap code changes. The two focused tests pass. The cancellation test
also uses the existing 60-second evidence window for the already bounded three-dispatch restart
contract, rather than a shorter 20-second window. The failed run is retained as a diagnostic, not
silently promoted by the focused retry.

The next full run exposed a separate in-process restart race: the shared five-second Host shutdown
budget could expire on a reverse stream before a canceled configuration-monitor task released its
Git gateway lock. `VivariumControllerHost.DisposeAsync` now joins background execution tasks with a
separate bounded drain before disposing services/database. All five existing fencing/restart tests
pass afterward. This fixes the current owning production lifecycle, not the future Host/PG stack;
the earlier failed full run is retained separately.

## Verification and evidence accounting

The existing NUnit/Test SDK and finite WireMock replay contour consumes the literal observations.
It preserves response media/bytes, including empty 204 entities with a JSON Content-Type and requests
with no Content-Type. An extra request Content-Type must be unmatched. JSON object key order remains
non-contractual; array order, fields/types, status, literal media and non-JSON bytes remain checked.
Replay proves the transcript/assertion contour, not Vivarium's runtime behavior. The current corpus
contains 35 observation groups and 505 selected exchanges after omitting only consecutive
byte-identical GETs. All 53 focused replay/control tests pass, zero skipped. Raw entities and source
Git blobs retain independent byte hashes; no semantic expected value is generated from Vivarium.

The current [physical/recovery receipt](us013-p0-physical-results.json) binds **263** unchanged
source/fixture inputs across the completed checks: root build **zero warnings/errors**, root tests
**459 Passed**, eleven existing console skips/twelve literal TRX NotExecuted, zero failures;
**116** focused TeamCity replay/composition/accounting controls, **eight** separate physical tests,
the two real-busy upgrade cases and five fencing/restart cases pass. Fresh raw again executes five
genuine HTTP mismatches, zero passes/skips. This current receipt supplies fresh source/assembly/raw/
control provenance after the dependency and Host changes; it does not transfer old production-tree
evidence across those changes by calling them documentation-only. Original captures and prior
receipts remain immutable. #78/#110/#111/#112 have response-verified checkpoint headers with every
previous body retained verbatim, unchanged state/labels/assignees/milestones/comments and no closure.

The prior reference/source [execution receipt](us013-p0-execution-results.json) binds 255 unchanged input-file hashes
across the final root build/test: zero build errors, **459 Passed**, eleven console-reported existing
skips (twelve literal TRX NotExecuted records), zero failures. All **53** focused replay/control tests
also pass. The earlier 443-pass run is retained separately, not relabelled as this final selection.
The separately invoked current raw target again executes five genuine response mismatches, zero
passes/skips: four expected 200/observed 404 and malformed-ID expected 500/observed 404. The ten
prior prerequisite-blocked cases remain blocked. There is still no product GREEN/API-complete claim.

New observations do not silently expand the original frozen-case requirement, normalized operation
denominator or historical receipts. #111/#112 still must join exact due variants, actors/grants,
seeds, independent oracle, effect/completion and owning contributions before product acceptance.
The global denominator remains UNKNOWN. Due baseline/composition/allocation, aggregate correctness/
simplicity, explicit independent Security/Adversarial, zero debt and verified main delivery remain.

## Next connected output

The existing `ConfigurationReconciler` is not a ready target adapter: it hardcodes SQLite connection/
transaction types and `UpsertControlMember`, so its public revision-set list does not prove actual
multi-repository persistence. Do not wrap two repositories into a fictitious single-control revision.
The bounded feasibility target now uses the existing NUnit/Test SDK contour, real owned PostgreSQL,
actual Git ref CAS and the checked-in C# evaluator. Its eight passing cases cover intent/ref/projection
interruptions, restart/retry, source interference and last-known-good retention. Bare repositories isolate
the ref boundary; they do not replace D29's normal-checkout/dirty-state evidence. Missing Docker is a
setup failure in that explicit target, not a skipped/passing product case. This remains a P0 source/
persistence experiment, not Host/Chiral/Identity/HTTP acceptance or another test framework.

Consume the [worked small-chain identity/input model](us013-build-model.md), its explicit native-matrix
preservation boundary and the physical study's stated limits. Finish the due source/model/concurrency/
mode and completion decisions; do not relabel the prototype as an implemented Host. Extend the existing
ledger with the due story/source/variant/contribution/evidence joins and their failure controls.
Use these outputs to close the integrated P0 pack and remaining P0 gates; do not start R0/PILOT merely
because a source experiment or replay passed. The [story plan](t3personal-pilot.md) remains the driver.
