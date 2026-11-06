# Selectable relational data providers

Status: **accepted feature direction; specification only, implementation and bindings pending**.
Owner: [feature #227](https://github.com/iXab3r/Vivarium/issues/227).
Decision: [D53](../ARCHITECTURE.md#d53-selectable-data-providers-preserve-one-headless-product).
Canonical business criteria: [AC-DP-01 through AC-DP-20](../acceptance/operations/data-providers.feature).

## Problem and useful outcome

The current Host requires PostgreSQL even for a small persistent developer installation. Existing
legacy SQLite code is a different application path, not a provider of the current Host. Simply
changing an EF registration would leave incompatible queries, schema and concurrency semantics.

An operator selects SQLite or PostgreSQL while initializing the same product. Both selections offer
the same accepted native and TeamCity-compatible API behavior, authorization, execution and recovery.
The developer can use an embedded database without provisioning PostgreSQL. This is not a local/server
mode switch, an API emulator or permission to ship an incomplete SQLite feature tier.

No Vivarium UI work is included. Management uses public APIs and CLI after supported .NET bootstrap.
Agent processes remain separate consumers of the normal protocols. D43's isolated C# evaluator and
container workloads still have their own Docker prerequisite; SQLite does not remove that boundary.

## Authority and scope

- One selected relational provider owns structured application data. Git still owns desired settings;
  Storage owns large bytes, and Chiral owns private resource settings. No dual writes or fallback store.
- Selection belongs to the existing Database node with saved/active restart semantics. An upgrade of
  existing configuration retains PostgreSQL explicitly. Do not infer the provider from a failed connection.
- Selection is fixed for an active Host. Reject a provider/data-identity mismatch before readiness;
  changing a setting is not data transfer. Cross-provider transfer is outside this first feature.
- Initial SQLite support requires a local filesystem and exclusive Host ownership of the installation.
  Reject conflicting ownership. This is a topology constraint, not a functional restriction. Network
  filesystems, multi-Host coordination and HA need separate evidence and are not claimed here.
- Preserve the existing PostgreSQL schema history. Give SQLite its own fresh baseline and subsequent
  versioned migrations. Do not repurpose legacy SQLite data as a new installation without an explicit
  legacy disposition. No `EnsureCreated` shortcut for persistent acceptance.
- Backup covers the selected database, desired settings, bytes and protected keys together. A live
  SQLite file copy is not a complete backup. Initial restore uses the same provider and a fenced target.
- No provider-specific credentials or file paths appear in ordinary REST representations or logs.

## Dependency inventory and decisions to close before broad implementation

Source baseline: `324faf9d355bd96e1517f54a9f5dec5c7a0b4d0b`. This inventory distinguishes the active
Host path from legacy SQLite stores. It is a reviewed search starting point, not proof that a keyword
scan finds every provider assumption. The inventory task must inspect call sites, migrations,
configuration, tests and packaging, and classify every active transaction and raw query.

| Area and current source | Required contract / decision | Evidence gate |
|---|---|---|
| `Hosting/VivariumRuntime.cs`, `VivariumConfigurationCommand.cs`, Database node/schema | Select one provider at composition; migrate/verify it consistently; preserve existing installations | Fresh, restart, wrong provider, unsupported schema |
| `Persistence/VivariumDbContext.cs`, `Builds/Runtime/RuntimeBuildModel.cs` | Map jsonb, collation C, identity, regex/octet-length checks, filtered/unique keys and foreign keys | Independent boundary values, invalid inserts and public readback |
| User/Agent TeamCity IDs, `RuntimeOutputRange.ReceiptOrder` | Generated non-key IDs and ordered receipts cannot rely on SQLite primary-key autoincrement alone | Concurrent allocation, deletion/restart, import high-water marks, no reuse that changes public identity |
| Runtime `DateTimeOffset`, decimal output ranges and unsigned protocol values | Preserve UTC precision, ordering, deadlines, range endpoints and keyset pagination | Equal timestamps, non-ASCII/case distinctions, large numeric edges and restart |
| `Identity/CurrentAccess.cs`, `TeamCityAccessService.cs`, `VivariumExternalUsers.cs` | Replace unnest/ANY and provider errors without splitting an atomic access observation or duplicating role semantics | Same-credential revoke; concurrent provisioning; grant/revoke versus admission |
| `RuntimeBuildAccess.cs`, `RuntimeBuildService.cs` | Permission fence, queue fence and first read snapshot order; preserve denied audit commits | Independent connections, competing admissions, rollback and current authorization |
| `Agents/AgentCredentials.cs`, runtime Agent callers | Enrollment use, Agent ownership, credential/session generations and result fencing | Racing enrollment, new versus stale session, restart |
| `Catalog/CatalogService.cs`, `CatalogAgentSettings.cs`, `RuntimeVcsTriggers.cs` | Session-level try-lock lifetime, Git commit/projection recovery, trigger event/counter consistency | Competing reconcilers, changed source, crash at activation and trigger admission |
| `RuntimeBuildRunInputs.cs`, secret services | Single consumption, expiry, protected persistence and audit in the same accepted operation | Competing requests, expiry clock, restart and no plaintext leakage |
| `RuntimeBuildOutputStore.cs`, `RuntimeAgentByteEndpoints.cs`, `RuntimeOutputRetention.cs` | Durable object intent, upload/delete exclusion, skip-busy cleanup and short database transactions | Paused PUT versus cleanup, crash before/after reference commit, byte hashes |
| `Persistence/PostgresSchema.cs` and 23 PostgreSQL migration classes at baseline | Migration ownership, checksum intent, introspection, readiness and recovery | Fresh/upgrade/interrupted/tampered/newer-schema cases per provider |
| `R0AcceptanceInstallation.cs`, `OwnedPostgres.cs`, local tooling and release packaging | Real provider setup, private state and native dependency availability on supported RIDs | Host+Agent journey, package startup, lifecycle and restore |

The broader source scan also finds raw SQLite in legacy Agent/build stores. Classify reachability;
do not count that code as an implementation of this feature or mechanically rewrite it into EF.

## Code boundary

EF remains the shared CRUD and query abstraction. Common entities, portable LINQ, state machines,
Identity/OpenIddict stores and API handlers are retained. No repository-per-table framework and no
second build, authorization or scheduler implementation are needed.

Provider implementations own options/model mapping, migrations and demonstrated query/transaction
differences. Proposed names, not frozen signatures:

```csharp
public interface IVivariumDataProvider
{
    void ConfigureOptions(DbContextOptionsBuilder options, DatabaseSettings settings);
    void ConfigureModel(ModelBuilder model);
}

public interface IWriteTransactions
{
    Task<IDbContextTransaction> BeginAdmissionAsync(
        VivariumDbContext db, AdmissionKind kind, CancellationToken ct);
    Task<IDbContextTransaction> BeginAccessMutationAsync(
        VivariumDbContext db, CancellationToken ct);
    Task<IDbContextTransaction> BeginAgentMutationAsync(
        VivariumDbContext db, string agentId, CancellationToken ct);
}
```

The inventory may require additional coherent operation scopes; do not replace missing analysis with
an arbitrary lock-flags API. The complete contracts specify acquisition before authoritative reads,
participating writers, connection/transaction ownership, rollback/disposal, deadline, cancellation and
commit ambiguity. PostgreSQL retains the proven fence order. SQLite reserves writing before its
authoritative read snapshot. Every conflicting writer must participate, including background workers.

An `IAccessSnapshotQuery` may isolate SQL differences while retaining one access contract. Prefer a
portable shared query where it preserves atomicity and cost. `ISchemaMaintenance` owns explicit
migrate/verify. Provider error classification must distinguish uniqueness, contention, corruption,
unavailability and unknown commit outcomes; arbitrary retry is not an application policy.

Model cache identity, design-time tooling, generated IDs, UTC/decimal conversions and migration
assembly selection must be tested with both providers in one process. Provider selection must not
leak into ordinary domain services as scattered `IsSqlite` branches.

## Object lifetime is a separate design gate

The current PostgreSQL implementation holds a row lock across a bounded Storage PUT and cleanup skips
locked rows. Translating that to SQLite's sole writer would delay unrelated writes during byte I/O.
Before the broad port, prove a small ownership protocol with durable staging/deletion intent, exclusive
object ownership and short database transitions. A process-local keyed gate is only a candidate under
verified exclusive Host ownership, with all ingress/cleanup paths participating and durable crash
recovery; a durable lease has additional fencing/expiry costs. Choose from measured proof, not taste.

Never remove protection or let an expired lease delete an active upload. Test crash windows around
intent, PUT, manifest/reference commit and cleanup. The existing bounded PostgreSQL row-lock behavior
remains implementation evidence until its replacement is proven. A general distributed lock service
is not a prerequisite.

## Test allocation and traceability

All fixtures are synthetic. Current state for every new AC and binding below: **specified-unbound,
not run**. The feature file is the canonical business text; do not copy it into a second feature set.
Provider choice is a Vivarium deployment capability; no fictional TeamCity endpoint is invented for it.

| AC | Minimal decisive evidence | Existing story context |
|---|---|---|
| AC-DP-01 | Real fresh schema, bootstrap and supported configuration readback on each provider | MIG-01 / #178 |
| AC-DP-02 | One bounded real Host+Agent journey per provider, native REST and unchanged compatible client; exact bytes | MIG-19 / #196, MIG-29 / #206, MIG-33 / #210 |
| AC-DP-03 | Real store + production access/admission services, one credential, ordinary actor, completed revoke barrier | MIG-03 through MIG-07 / #180–#184 |
| AC-DP-04 | Two independent contexts/connections and controlled contention; distinct unkeyed runs and counters | MIG-19 / #196, MIG-25 / #202 |
| AC-DP-05 | Production enrollment with two independent claimants and exact identity readback | MIG-15 / #192 |
| AC-DP-06 | Production prepared-input consumption; one winner, encrypted persistence and redacted reads | MIG-11 / #188 |
| AC-DP-07 | Actual Host interruption/reopen around durable acknowledgement; real separate Agent for ownership boundary | MIG-21 / #198 |
| AC-DP-08 | Controlled old/new Agent messages through production ownership services on real stores | MIG-21 / #198 |
| AC-DP-09 | Controlled Storage PUT barrier against real ingress/cleanup; independent expected bytes | MIG-33 / #210 |
| AC-DP-10 | Crash/reopen at object intent and reference boundaries; controlled clock for expiry | MIG-34 / #211 |
| AC-DP-11 | Real queries with independently seeded edge values, bounded pages and ordinary permission filtering | MIG-08 / #185, MIG-29 / #206, MIG-34 / #211 |
| AC-DP-12 | Real Git/catalogue and trigger owner with controlled time/source, interrupted activation | MIG-13 / #190, MIG-27 / #204 |
| AC-DP-13 | Supported schema fixtures and exact old/new data checks, interrupted migration/checksum checks | MIG-01 / #178 |
| AC-DP-14 | Newer/corrupt schema fixtures; no readiness or fallback and preserved data | MIG-01 / #178 |
| AC-DP-15 | Coordinated cold backup and isolated same-provider restore; key/grant/byte checks, missing-component negatives | MIG-46 / #223 |
| AC-DP-16 | Retained installation plus mismatched provider/resource configuration, no new active empty state | MIG-01 / #178 |
| AC-DP-17 | Real engine contention, bounded acquisition failure and recovery; no sleep-based ordering | MIG-19 / #196 |
| AC-DP-18 | Commit-window fault injection and selected physical storage-failure checks, truthful public outcome | MIG-21 / #198 |
| AC-DP-19 | Two isolated resources plus conflicting-owner attempt; independent identities and cleanup ownership | MIG-01 / #178 |
| AC-DP-20 | SQLite Host+Agent with PostgreSQL absent and network/process checks for hidden dependency | MIG-01 / #178, tooling #176 |

Layer A keeps the existing reference-backed fake-server API client stories: pure protocol/client
cases run once, independent of the SQL provider. Layer B binds these business scenarios to real
Vivarium code and real database engines. Reuse independent wire assertions against both product
providers for affected compatibility variants. Equality between two implementations is supporting
evidence only; two implementations can share the same bug.

Do not run every scenario through every process boundary. Parameterize persistence-sensitive tests;
retain a small full-process cohort and targeted real PostgreSQL concurrency proof. Test ID, source
revision, provider/version, schema baseline, contract/profile, oracle and outcome are evidence axes.
Unknown, unbound, RED, setup failure and GREEN remain distinct. No new API operations are counted
merely because the same obligation is exercised with another provider.

Later stories extend the common product on both providers. This prerequisite preserves current
accepted functionality and establishes provider-neutral extension rules; it cannot claim features
whose owning stories have not yet been implemented. No silently PostgreSQL-only core feature is allowed.

## Delivery gates

1. Inventory every active provider dependency and decide transaction/object ownership, mapping and
   public error contracts. Prove high-risk concurrency, generated identity and upload/cleanup behavior
   before a broad conversion. Unknowns are named blockers, not permission to improvise during the port.
2. Provider composition, shared model and independent schema lifecycles, preserving existing PG data.
3. Shared IAM/catalogue/Agent/build/trigger/byte services with correct atomicity and recovery.
4. .NET operator lifecycle, real-agent use, backup/restore and packaged provider availability.
5. Bind all prerequisite ACs, assess affected API variants per provider, run coherent checks and obtain
   independent aggregate plus Security/Adversarial review. Fix all due debt before delivery and M1.

Reference research and story refinement may continue during this prerequisite. Do not run expensive
whole-product suites for each edit; use focused RED/GREEN then coherent milestone checks. Do not
fabricate failing placeholder tests for missing bindings. Runtime acceptance remains pending.

## Primary references

- [EF SQLite limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)
- [Provider-specific migration sets](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers)
- [SQLite transactions and contention](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)
- [EF model caching](https://learn.microsoft.com/en-us/ef/core/modeling/dynamic-model)
- [SQLite isolation](https://sqlite.org/isolation.html)
