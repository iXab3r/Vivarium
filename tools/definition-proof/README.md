# Bounded C# representation experiment (US-013 / P0)

This is the RF-05/#34 representability experiment, not the public definition SDK, a catalog engine,
HTTP implementation or accepted Host activation. It uses the existing pinned .NET SDK, ordinary Git
and a short Python invocation; it introduces no test framework or scenario language.

From the repository root, with Python, Git and the already prepared private SDK 10.0.303 available:

```powershell
python tools/definition-proof/probe.py
```

The exact SDK executable is `artifacts/p0-rest-first/tooling/dotnet/dotnet.exe`. All experiment output
and two new owned Git repositories stay under a unique `artifacts/us013-p0/representation-run-*`.
The probe does not reset existing repositories, contact GitHub/TeamCity, use credentials or run T3.

`Control.g.cs` owns Project identity/metadata/binding. `Settings.g.cs` owns the product-scoped
declarations; `UserCode.cs` owns the explicitly named computation. Both managed declarations are real
C#, compiled and read in a separate process. The editor renders typed declarations, never rewrites
arbitrary user C#. Evaluator JSON is transient output, never an editable/persisted settings authority.
The probe checks canonical round-trip, inherited/local/computed parameter changes, ordered collection
replacement/reordering/omission, binding without duplicate ownership, invalid C#, duplicate identity
and reevaluation of the exact prior usable Git revisions in a fresh child. User-code bytes remain
unchanged and are selected from the owning Git revision together with managed settings.

The original `Settings.g.cs` three-definition seed represents a focused subset of the pinned T3 settings: identities, runners,
script references, requirements, dependencies, selection rules, timeouts and used trigger. It is not
the complete executable/adapted T3 recipe; Docker/process properties, runtime substitution, secret
resolution and complete template/mode behavior still need their owning proof. Its representative
computed parameter is an attributed experiment addition.

`T3Recipe.cs` is a separate literal translation of **all explicit product declarations** in the
vendored [historical Kotlin fixture](reference/NOTICE.md). The existing `RecipeSourceTests` NUnit
target compiles real committed C#, round-trips every declared field through the same managed editor,
edits source/Agent/cache/trigger inputs for an owned configuration candidate, and reevaluates the old
Git revision. Checkout mode, source name and quiet-period/rule fields are explicit; Docker and
PowerShell options remain ordinary typed runner properties. This does not resolve implicit TeamCity
defaults or execute runners. Its stable step IDs are attributed experiment IDs. The workload scripts
still require the separately recorded repository/tool-path/no-launch adaptations before a real T3
build. The original computed/inheritance and physical-recovery experiment continues to use its own
smaller seed.

Separate processes and deadlines are **not** an OS security sandbox. These are known trusted fixture
sources; do not use this probe to evaluate untrusted repositories. The prototype neither authorizes
mutations nor implements PG projection, Git-ref/PG atomicity, crash recovery, REST completion or a
cross-repository activation saga. Preparing then changing a binding demonstrates source ownership;
reevaluation demonstrates reproducibility, not production recovery. The active/applied authorities
must be proved through the real owning services before P0 acceptance. No business AC or product
conformance is made GREEN by this experiment. Keep this temporary proof separate from R2's engine.
