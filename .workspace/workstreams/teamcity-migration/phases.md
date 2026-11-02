# Proposed implementation and migration sequence

These phases now implement the owner direction adopted in D32, the updated roadmap and
`docs/design/teamcity-parity.md`. The 120 stable requirements and per-configuration candidate map
govern acceptance. This is implementation planning, not evidence that the new target is implemented.

1. **Small runnable build on the new foundation.** Reconcile Chiral/PostgreSQL/C# ownership decisions,
   adopt pinned Chiral packages, preserve the agent/session/queue invariants in PostgreSQL, add a small
   catalog/C# plan compiler and private Agent checkout. Run Vivarium's own Compile manually from one
   exact Git commit. Persist stdout, result and artifacts across server restart. No publisher yet.
   Establish imported-history identity/provenance requirements alongside the new data model and begin
   a complete retained-build/log/artifact census; the current three-build-per-job sample is insufficient.
   Plan for coexistence: stable source-to-target IDs and an explicit whole-entity keep-Vivarium or
   use-TeamCity import policy, with no field-level merging or bidirectional synchronization.
   Freeze the existing TeamCity clients' method/path/variant inventory for D33. Implement the
   compatible facade beside each native domain slice; both reads and writes need acceptance.
2. **Autonomous independent CI.** Add VCS triggering, branch/revision policy, parameter forms, the
   observed requirement operators and workspace/cache admission. Run CLI.Factory Verify on Cvat in
   its existing Linux container. Keep its current `build/teamcity.sh` initially as an ordinary script;
   renaming a valid provider adapter is not required for migration.
3. **Exact build chains.** Add graph creation/validation, per-edge revision/failure/reuse policy,
   dependency output parameters and artifact staging. Prove Vivarium Compile -> Release and CLink
   Compile -> SmokeTest before enabling any publish. Persist exact upstream build IDs/digests.
4. **First trusted publication.** Add protected secret bindings, shared resource locks, retained
   releases, durable operation/audit UI and permission boundaries. Move CLI.Factory/Workbench npm and
   a simple NuGet publisher with verify-only/dry-run proof followed by one explicitly owned real
   publication. Only one system owns each publisher. Preserve committed version/release counters.
   A publisher family is not accepted until its relevant test/history, log, parameter, artifact and
   operator workflows also satisfy the parity contract; command success alone is insufficient.
5. **Windows compile -> Linux Docker publication.** Port WebServer-linux and related services,
   transferring the exact Windows-produced artifacts to Cvat for image packaging. Implement Docker
   step wrappers and command adapters separately. Do not rebuild binaries during publishing.
6. **Desktop and release-heavy families.** Port EyeAuras App, protection/packaging/E2E/SDK/docs,
   MicSwitch/PoeEye and related libraries; prove same-Agent/shared-directory behavior or remove that
   coupling in the owning build scripts. Then port PoEBane Basic/Pro plus nightly long tests,
   benchmarks and docs, Rust SDK, ReClass and XAC. Preserve shared caches, interactive sessions,
   licensed native tools, disk budgets, parameter conditions, tags and release-channel safeguards.
7. **Import all retained history.** Implement resumable idempotent historical import into Vivarium,
   including archived/removed configuration identities still referenced by retained builds. Preserve
   original build numbers, timestamps, states, parameters subject to secret policy, source/configuration
   evidence, Agent provenance, chain edges, test results, logs, artifact trees and retention/pin state
   where available. Run one initial bulk copy and support explicitly requested repeatable diff-imports
   during coexistence: new entities are added, unchanged are skipped, and changed matches keep the
   Vivarium snapshot or use the TeamCity snapshot under the chosen policy. Preserve already imported
   history after source cleanup. Capture a final delta after the old writers have stopped. Close the
   acceptance manifest in `history-import.md` before shutdown.
8. **Retire TeamCity.** Reconcile all 97 IDs (migrated or explicitly retired), 17 roots and three
   authorized machines; dispose the 16 paused configurations and three archived projects. Prove no
   active trigger, release counter, secret, package consumer or publisher still requires TeamCity.
   Restore a backup on an isolated Vivarium instance; test one-family rollback; prove that imported
   historical logs/artifacts can be read without any request to TeamCity. Disable the old
   trigger/publisher only after its replacement is verified and owned. An external archive alone
   does not close this gate.
   Run unchanged `teamcity-cli` and other inventoried consumers against Vivarium using normal
   server/credential setup. Native OpenAPI parity and route registration do not replace conformance.

Every phase closes on real outcomes rather than a translated configuration file. For shadow runs,
compare exact source/settings revisions, effective parameters, declared outputs and test evidence;
do not require byte-identical artifacts from a nondeterministic upstream build without first defining
which fields vary. Use detached outputs/test registries and prevent production publication.

Existing failures are separate baseline facts: EyeAuras Compile 32598 has one failed integration test;
PoEBane LongTests 32592 and Rust SDK 32351 have nonzero process failures. Their causes were not inferred
from the short diagnosis. Migration must not turn those failures green or claim to have fixed them.

FakeMachineProvider, pristine VM pools, image factories, remote file/process management, multi-process
Chiral topology and full enterprise RBAC are not on the initial TeamCity-exit critical path unless a
specific inventoried build proves otherwise.
