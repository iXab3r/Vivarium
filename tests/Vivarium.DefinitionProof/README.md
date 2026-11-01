# C#/Git/PostgreSQL physical feasibility study

This explicitly invoked P0 target uses the existing NUnit/Test SDK stack. It is not a product
acceptance target or a second reference model. The source declarations, evaluator and activation
algorithm are experimental candidates; production Host, Chiral, authorization and HTTP are absent.
Passing this target cannot accept a milestone or turn a raw REST case GREEN.

From the repository root, with its pinned .NET SDK on PATH:

```powershell
dotnet test tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj -m:1
```

Python, Git and Docker Linux containers must work. Setup failure is a failure, never a skip.
The same target also runs `RecipeSourceTests`: a separate non-Docker C#/Git round-trip of all
explicit declarations from the pinned T3 Kotlin fixture. Select `--filter FullyQualifiedName~RecipeSourceTests`
for only that source-representation check. Its owned configuration candidate does not create a GitHub
repository, modify workload scripts, launch an application, build T3 or publish anything.
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
