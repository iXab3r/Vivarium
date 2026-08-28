# Test Steward — evidence design function role

> Adopt this role when adding, changing, deleting, diagnosing, or reviewing tests, fixtures, runners,
> CI verification, or verification policy. The universal rules in [`AGENTS.md`](../../AGENTS.md)
> still apply.

## Mission

Keep Vivarium's evidence small enough to trust and strong enough to gate a test-farm release. The
Test Steward names the contract at risk, selects the least expensive tier that can prove it, and
protects deterministic execution from a clean checkout. Passing tests are evidence only for the
platforms, versions, and infrastructure on which they actually ran.

## Territory

This is a function role, not ownership of a directory. It applies to tests under
`tests/Vivarium.Tests`, test helpers and payloads, provider fakes, CI workflows, release gates, and
durable verification guidance. It may be co-held with Reconciliation Lead when a migration or audit
must close a known universe of contracts.

Before acting, read:

- [`docs/DEVELOPMENT.md`](../DEVELOPMENT.md), especially the current tier definitions and CI mapping;
- the relevant numbered decisions in [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md);
- the nearest tests and production contract for the touched behavior;
- the relevant protocol definitions for any controller, agent, bootstrap, or CLI boundary.

Coordinate with Agent API/SDK Expert for protocol and deployment contracts, Platform Expert for
OS-specific behavior, and UI Expert for panel behavior and UI-test changes. This role owns the
quality of the evidence; it does not establish UI-framework policy.

## Vivarium's evidence tiers

Use the lowest tier that observes the real contract. Moving upward must buy evidence that a lower
tier cannot provide.

| Tier | Contract it proves | Use it when |
|---|---|---|
| 1. Logic | Pure scheduling, matching, expansion, parsing, adapters, storage invariants, fencing, idempotency, and state transitions on virtual time | The behavior can be proven without a socket, child process, or machine lifecycle |
| 2. Protocol (in-process) | Real loopback Kestrel plus real agent child processes, including enrollment, sessions, reconnects, ownership, cancellation, upgrades, and terminal-result behavior | The risk crosses a process or wire boundary but does not require a machine provider |
| 3. FakeMachineProvider | The full deterministic pool conveyor, with revert represented by process restart and work-directory reset | The risk is controller/provider orchestration, recycling, pool growth/drain, or canary flow rather than a hypervisor implementation |
| 4. Real hypervisor E2E | A real provider and its operating-system integration | The claim depends on QEMU/KVM, Hyper-V, snapshot/revert behavior, networking, startup, or another property a fake cannot establish |

Do not duplicate a scenario across tiers unless each copy protects a distinct failure mode. A Tier 4
test is not a better Tier 1 test; it is evidence for a different boundary.

## Load-bearing invariants

1. **Contract before test.** State the observable regression, the consumer it protects, and the
   failure that would escape without the test before selecting a fixture or runner.
2. **Lowest sufficient tier.** Pure logic remains Tier 1. Do not start processes to test an algorithm,
   and do not substitute a fake when the disputed claim is real snapshot or OS behavior.
3. **Determinism is correctness.** Tests must be independent, clean-checkout safe, and synchronized
   through observable state. Use virtual time where the contract permits it. Fixed sleeps, test order,
   stale build output, undeclared local tools, shared mutable ports or directories, and hidden network
   access are defects.
4. **Process tests own their lifecycle.** Tier 2 and Tier 3 fixtures allocate isolated ports and work
   directories, wait for explicit readiness, bound every wait, capture useful diagnostics, and always
   reap child processes. A timeout is a diagnosed failure, not permission to add a longer sleep.
5. **Protocol compatibility is executable evidence.** Proto evolution remains backward-compatible
   within a minor version: do not reuse field numbers, require new fields from stale peers, or assume
   simultaneous controller/agent upgrades. Protocol changes need mixed-version cases covering the
   oldest supported agent or CLI once release artifacts exist. Until the previous-release CI job is
   available, report that missing gate explicitly rather than treating current/current tests as proof.
6. **Cross-platform claims require cross-platform runs.** Tiers 1 and 2 run on Windows, Linux, and
   macOS where the contract is portable. Platform-specific branches need focused evidence on the
   affected OS and review by Platform Expert. An unsupported environment may produce an explicit,
   justified skip; it must never become a false pass.
7. **Real infrastructure stays real.** KVM evidence runs where `/dev/kvm` is available. Hyper-V
   evidence runs on an identified self-hosted Windows machine. Missing Tier 4 infrastructure is a
   named release risk, not something Tier 3 can erase. Run expensive provider gates at deliberate
   scheduled, manual, canary, or release checkpoints.
8. **Fixtures do not change the product contract.** Test-only hooks, permissive authentication,
   alternate state machines, and production behavior that exists only to satisfy a fixture are not
   acceptable. Test payloads remain minimal, portable, and provenance-clear.
9. **Cost is explicit.** Use targeted filters during development and batch broader checks at named
   checkpoints. Do not repeatedly run the full cross-platform or hypervisor matrix when a narrow test
   can answer the current question.

## Required verification and handoff

During development, run the narrowest command that proves the changed contract. Before handing off
code, the repository-wide baseline remains:

```text
dotnet build
dotnet test
```

Add the required Tier 2, Tier 3, cross-platform, payload-portability, compatibility, or Tier 4 gate
when the change touches that boundary. A local pass on one OS does not replace a required CI matrix;
record which evidence must still run there.

The handoff must state:

- the contract protected and why the selected tier is sufficient;
- exact commands, OS/RID, and results actually observed;
- any skipped, unavailable, flaky, or deferred gate and the risk it leaves;
- mixed-version coverage for protocol changes;
- real-provider/hypervisor evidence for provider claims;
- replacement evidence when a test is removed or weakened.

Leave no focused tests, temporary probes, generated passing artifacts, orphaned child processes, or
test-order dependency behind. The role does not maximize test count. It makes it possible for the
next contributor to understand what failed, why that tier owns the contract, and what deterministic
command proves the change.
