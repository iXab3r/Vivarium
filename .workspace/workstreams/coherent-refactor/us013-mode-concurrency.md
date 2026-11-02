# US-013 P0: settings modes, context and concurrent edits

Lead: Story Delivery; co-held TeamCity, Git/Versioning, REST, Testing Architect and Test Steward.
This continues #78 through the existing #110/#111/#112 owners. It records reference constraints
for preparing the three configurations; it does not accept the replacement configuration subsystem.

## Reference and acquisition

Two owned-fixture captures used the isolated TeamCity 2026.1.3 / build 222742 / API 2026.1 server
and profile `vivarium-teamcity-lab-202613`. The pinned CliFactory upstream is
`b158fe03eab3c31982a21b36c278f690f6b541d9`; the acquisition directory is an extracted archive,
not a Git checkout. Each successful receipt binds its actual script and 131 source/runtime inputs.
Existing CLI authentication, update gates and transport were used, with capture-only request and
projection handling. This is qualified acquisition, not unchanged acceptance-client success.

- Modes, enabled synchronization, source commits and context:
  `artifacts/us013-modes-20261102/run-b2540b0c19e24289b35975249a1cf617/receipt.json`.
- Repeated races with synchronization disabled:
  `artifacts/us013-modes-20261102/concurrency-run-888357366f07490090e98f5ce3f20fb9/receipt.json`.

The checked-in [literal corpus](../../../tests/Vivarium.Tests/TeamCity/Reference/versioned-mode-observations.json)
retains 30 observation groups and 323 selected exchanges from 625 original exchanges. Only
intermediate consecutive byte-identical GETs are omitted, retaining first and last; concurrent
groups are not deduplicated. Entity bytes, hashes, client labels and dispatch/completion sequences
remain literal. Git source snapshots have independent blob IDs as well as entity SHA-256 hashes.
Two CLI instances use the same administrator credential; this is not a two-user/current-grant proof.
No Agent, build, built T3 application or release operation ran. Owned catalog and transport resources
were removed, and private Update/Admin/Credentials gates were restored to disabled.

## What the actual writes establish

| Observed context | Actual response and subsequent state | Boundary |
|---|---|---|
| Each of `alwaysUseCurrent`, `useCurrentByDefault`, `useFromVCS`, with `allowUIEditing=true` | The tested `env.MODE` PUT returns 200; readback and a real Git source commit contain its value. | Editability facet only; not build-time selection, omitted defaults or `freezeSettings`. |
| The same three modes with `allowUIEditing=false` | The tested PUT returns 500 with `ReadOnlyEntityException`; prior value and source revision remain. | No universal oracle for other writes or failure variants. |
| Immediately after enabling generated Kotlin settings | Config PUT succeeds; the immediate parameter PUT is temporarily ReadOnly. Applied revision and a successful readiness write are observed later. | Config response is not activation completion. |
| A no-op control change | Status can say it was not committed because the repository is up-to-date. | The capture accepts this fence only for an unchanged HEAD previously observed applied; not a general completion rule. |
| Unused configured context parameter | Ordinary parameter editing succeeds. | Merely configuring context does not prove read-only behavior. |
| Kotlin actually calls `DslContext.getParameter`, including its default | Ordinary parameter editing returns 500 ReadOnly even with UI editing enabled. Changing context changes the effective value without changing the settings Git commit. | Usage and resolved context are effective inputs; the product Git SHA alone is insufficient provenance. |

The used-context snapshots all retain product/settings commit
`0894867a7a83f09ca1dfff108ea7983cb7567e83`, while effective `env.MODE` changes from `ctx-a`
to `ctx-b` to `ctx-default`. The existing control/product separation can represent this distinction,
but the actual context schema, binding, reevaluation and provenance path still need proof.

An unconfigured project without a configured ancestor returns **204 with an empty entity** from
the effective Versioned Settings config GET. The typed CLI tries to parse JSON and fails. Acquisition
catches only that exact empty-204 projection failure and preserves the response; it does not turn
the response into a successful unchanged-client result or synthesize an effective configuration.

## Concurrent edits: preserve evidence without inventing an oracle

The dispatch barrier releases both prepared requests and proves both were in flight before either
response completed. It is outside the server. Selected requests contain neither `If-Match` nor an
idempotency key.

| Probe | Observation |
|---|---|
| Two reads, then sequential delayed scalar writes A and B | Both writes succeed; B becomes visible and is committed. A stale client read is not sufficient grounds for imposing a new mandatory 409/precondition. |
| Concurrent different parameters | Both writes succeed and both values survive in readback and source. |
| Concurrent same parameter, synchronization enabled | Both responses contain B's value, including A's response; source/readback also contain B. Mutation responses need not echo each request. |
| Concurrent whole step-list replacements, synchronization enabled | Both two-step PUTs return 200; responses, readback and Git source contain four mixed steps. |
| Ten repeated step-list races, synchronization disabled | Nine readbacks contain four mixed steps and one contains two. All twenty PUTs return 200. |
| Five same-parameter races, synchronization disabled | Both writes succeed in each pair; observed final values are A's. Response values remain in the transcript. |
| Sequential whole-list controls | A then B each produces exactly the requested two steps in the requested order. |

These observations disprove a single inferred winner/response-echo rule and show that the mixed
step result is not explained solely by Git synchronization. They do **not** establish an exhaustive
allowed-outcome set, frequency, a required mixed result for every race, or a universal atomic HTTP
replacement guarantee. Literal WireMock replay checks the transcript; it does not execute concurrent
TeamCity processing or constitute the future product race oracle. Do not copy a possible reference
defect into a deterministic product requirement without resolving its contract.

Keep atomic Git publication and internal compare-and-swap separate from compatible HTTP policy.
The earlier physical study's internal `CONFLICT` is not evidence for returning 409 to these
unconditional compatible writes. AC-CFG-03 is an explicitly version-checked extension. Its stronger
precondition belongs only to an opted-in workflow and must remain neutral to ordinary clients.
The [selected early activation contract](../../../docs/design/csharp-activation.md) now fixes P0's
unbound R0 and C#/Git PILOT-preparation ownership/current-authority/activation/retry/readback fence.
Those selected entries expose no compatible field-edit HTTP operation. The full unconditional
HTTP mutation/concurrency oracles remain due with SS-CFG #122/#123/#124 before exposing those edits;
their observed races are not an automatic 409 policy or a deterministic mixed-result oracle.
This study introduces no second model or state authority.

## Diagnostics that are not accepted positive cases

Earlier attempts are retained under `artifacts/us013-modes-20261102`, separately from the successful
captures. A child ID outside its parent's prefix was accepted on creation, but enabling portable
Kotlin settings rejected the nonuniform IDs. This is a binding-validation lead, not a global REST
hierarchy-ID restriction. A later hierarchy attempt failed a whole-config change after first applied
status with a currently-used-format error; there was no successful post-initialization readiness
write before it. Hierarchy versus initialization causality remains unresolved. The successful flat
fixture does not close inheritance/activation behavior. Other attempts exposed Git-daemon ownership
and an overly strict no-op polling fence; those were acquisition/setup failures, not product RED.

## Story traceability and remaining gate

AC-CFG-01/02/04 and TCM-01/05/07/08/10 remain the governing identity, editability, activation,
authorization and compatible-client contracts. This reference work supports their future bindings;
none becomes business GREEN. It does not prove actual build selections for AC-BLD-09.

The [checkpoint receipt](us013-mode-results.json) records fresh root/control/raw results and explicitly
reuses the unchanged computed-source, full-recipe and physical-recovery inputs from the preceding
checkpoint. Root build has zero warnings/errors; 524 root tests pass with zero failures and the
existing platform skips, and all 181 TeamCity controls pass. The ten source/physical checks are
reused, not relabelled as fresh execution. The 34 new controls comprise 30 literal replays and four integrity/fact controls. They
add no materialized product case: the original fifteen remain five genuine HTTP RED, ten blocked
by product prerequisites and zero GREEN, with an unknown normalized denominator.

Continue the integrated P0 package: full schema/template and context-binding proof, current-grant
and hierarchy variants, reference-backed shared mutation/completion processing, exact source/effect
allocation and case freeze, then independent aggregate correctness/simplicity and Security/Adversarial
reviews with all debts fixed. R0/PILOT, actual T3 build/package and test-repository release follow
only after their predecessor gates. This checkpoint is not P0 closure or a main merge.
