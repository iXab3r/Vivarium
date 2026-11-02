# C#/Git/PostgreSQL physical feasibility study

This explicitly invoked P0 target uses the existing NUnit/Test SDK stack. It is not a product
acceptance target or a second reference model. The source declarations, evaluator and activation
algorithm are experimental candidates; replacement Host, Chiral and ordinary OIDC are absent.
Passing this target cannot accept a milestone or turn a raw REST case GREEN.

`IsolatedCompilerTests` proves D43's newly accepted Linux/Docker boundary separately from the
historical trusted-source studies. It compiles actual Git-selected C# and runs ordinary computations
inside fresh non-root containers with no network/Host mounts/credentials, a read-only root, dropped
capabilities, no-new-privileges and private bounded tmpfs. Fixed study budgets are one CPU, 512 MiB
memory with no extra swap, 128 processes, 128 MiB work plus 32 MiB `/tmp`, 256 KiB source and 64 KiB
combined output; the usual invocation deadline is 30 seconds. These are measured study inputs,
not a frozen public SDK or independently proven production admission policy.

The study pins the available official SDK **10.0.401** image by immutable digest in
`IsolatedCompilerStudy.cs`. That compiler pin is separate from the repository's Windows SDK
10.0.303; do not relabel the older source studies as executions under this image. It uses a trusted
SDK-only project and an empty package-source list, with compilation and execution both inside the
container. Repository MSBuild files, paths and oversized input are rejected before container creation.
No runtime image pull, external package feed, bind mount, Docker socket or Host environment forwarding
is available to the author code.

Negative evidence includes real Host-file/environment/privilege attempts with intact positive
controls, an independently reachable TCP receiver blocked by network-none, CPU deadline, output
flood, unmanaged-memory cgroup OOM and process fanout. Separate cases reject compilation/nonzero/
malformed-output failure and reconcile a still-running container after killing the actual coordinator
worker. The latter uses a pre-launch owned descriptor; it is not the production PG operation journal.
Output and containers are bounded/removed even on rejection. Container cleanup verifies immutable
identity and invocation ownership before deletion; a stalled attach is closed before management stop.

Select `--filter FullyQualifiedName~IsolatedCompilerTests` for this boundary. Docker Desktop Linux
containers on the current Windows coordinator are the proved platform; no native Windows/macOS
sandbox, hostile-kernel protection, catalogue activation, public evaluator endpoint or production
global concurrency/restart integration is accepted by these tests. A separate same-user process
remains insufficient. The other source experiments below continue to use known trusted fixtures.

`BuildIdentityPhysicalTests` additionally uses the real legacy Host, pinned TLS, bounded AgentHub
enrollment/reconnect and unchanged native `viv-cli` cancellation. Its isolated PostgreSQL identity
study checks numeric/native matrix aliases, ordinary/composite separation, standalone Agent/type
identity, collision rollback and restart. The old Host still rejects candidate numeric Build URLs;
the study is not a compatible facade or a replacement persistence implementation. It does not prove
real Agent execution, imported identities, optimized queue replacement, SSE or artifact mapping.

Under D41, that public legacy alias adapter is historical feasibility evidence, not a replacement
requirement. `CatalogIdentityPhysicalTests` instead compiles real committed C# into Project and
BuildType catalogue identities and immutable selected definitions in an owned PostgreSQL database.
It rejects missing owners/definitions and definition rewrites, allocates concurrent Build identities,
retains old selected input after a real Git edit and PostgreSQL restart, and separately maps an opaque
Agent workload identity. This proves the bounded structural relationship, not production catalogue
activation, HTTP, authorization, source timing, concurrent activation or matrix execution.

`TemplateSourceTests` checks the independently observed parameter precedence: local configuration,
project, then templates in attachment order. It retains declaration readback, managed edits, cold
revision evaluation and rejected missing/duplicate references. Only template parameters are proven;
other template fields, hierarchy, modes, grants and production activation remain separate obligations.

From the repository root, with its pinned .NET SDK on PATH:

```powershell
dotnet test tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj -m:1
```

Python, Git and Docker Linux containers must work. Setup failure is a failure, never a skip.
The same target also runs `RecipeSourceTests`: a separate non-Docker C#/Git round-trip of all
explicit declarations from the pinned T3 Kotlin fixture. Select `--filter FullyQualifiedName~RecipeSourceTests`
for only that source-representation check. Its owned configuration candidate does not create a GitHub
repository, modify workload scripts, launch an application, build T3 or publish anything.
`ComputedSourceTests` separately checks typed C# edit readback and precedence over an untouched
computed declaration; select `--filter FullyQualifiedName~ComputedSourceTests` for that bounded
source study. Both source fixtures own fresh Git repositories and use the existing compiler.
The root test project builds this assembly but does not execute these physical cases implicitly.
The source fixture is created by `tools/definition-proof/probe.py`; its receipt records literal Git
revisions and expected effective-plan hashes. The same exact committed C# is compiled by separate
workers, with a control-only read before selecting the authoritative settings repository.

Each run owns unique bare Git copies and a labeled PostgreSQL container/network/volume. PostgreSQL
17 uses the immutable image digest in `OwnedPostgres.cs`, a random credential, a loopback-only random
port, and no existing database. Cleanup verifies resource ownership, including after a lost creation
response. A restart uses that run's durable volume. Artifacts retain source history and receipts;
the private database connection is never an argument or a saved resource descriptor. Source fixtures
are trusted: process separation and environment filtering are not an OS security sandbox.

The study schema journals intent before changing refs. Real Git compare-and-swap advances product
then control; one PostgreSQL transaction commits the selected members, derived plan, history and
application audit. JSON is immutable evaluator output, not an editable desired-settings authority.
Workers are ordinary separate child processes. Tests use actual Git lock files, PostgreSQL row/table
locks and observed journal/server state to kill a worker at four durability boundaries, then resume
the same operation in a fresh process. They also check source rejection, ownership transfer, cold
retry, PostgreSQL restart and an interfering ref writer.

This is a single-worker feasibility experiment over trusted bare repositories. It does not prove
production concurrency fencing, remote credentials, normal-checkout dirty-state recovery, mode and
permission behavior, all computed C# field categories or the complete executable/adapted T3 recipe. Those obligations
remain explicit in the US-013 P0 pack. The production legacy reconciler remains SQLite/control-only.

Dependencies: [Npgsql 10.0.3](https://www.nuget.org/packages/Npgsql/10.0.3), using its
[documented transactions and positional parameters](https://www.npgsql.org/doc/basic-usage.html).
