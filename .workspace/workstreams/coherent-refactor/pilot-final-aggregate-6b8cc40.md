# Independent aggregate review

Candidate: `6b8cc40bc03645242776570b97967032ed69bd1e`
Base: `2f053e608637e04628334c797ad33609a9c29e61` (PR167 base)
Date: 2026-11-05

Verdict: PASS for the selected PILOT code/evidence gate. No concrete blocking correctness or simplicity finding identified in the reviewed paths. This is an independent, risk-directed aggregate review, not an assertion that every changed line or every reference capture was exhaustively inspected. Milestone acceptance and main delivery are not granted by this verdict.

## Scope and method

Reconciliation Lead and Test Steward perspectives co-held. Read-only Git/source/document/raw-result inspection; no builds, tests, runtime activity, network, TeamCity/GitHub calls, credentials, environment inspection or delegation. HEAD matched the candidate and Git status was clean. The main-to-candidate inventory has 179 changed files; review considered the whole delta, rather than only the latest selection correction.

Inspected architecture/owner scope and review discipline, model/state/schema relations, admission/dependency resolution and source collection, coordinator task ownership, worker shutdown, runner compilation, Agent Git and Docker execution, number metadata, prepared inputs/current access, protocol additions, native/compatible projections, Workbench behavior and physical fixture composition. Examined the selected runtime qualification, raw four-case TRX, accounting implementation and report, remediation report and heavy observer/readback source. Reviewed current source for each of the four earlier aggregate concerns. Existing D43/D44 evaluator/ownership contracts are retained; this delta does not introduce another evaluator or desired-settings authority. No Bootstrap modification is in the delta.

## Evidence assessment

The current report names this exact candidate and deliberately remains NOT_ACCEPTED, global API-complete false and denominator unknown. Its 34 selected business/SDK outcomes do not materialize seven declared wire variants or renew the separate five stale/twenty blocked legacy probes. This distinction is appropriate; requiring all 449 discovered headings for this selected gate would expand the contract.

The final-binary TRX contains exactly four PASS results: ordinary source reuse; artifact success; missing-required-artifact failure; explicit selection. Its SHA256 is ae16dd894fb9ccab2d18895b52c7031866f491298491a8783d08475d22efd703. The qualification lists 31 qualified-reuse and three renewed normative cases; explicit selection is additional branch evidence. All eight listed qualification input/producer hashes matched local files, including Controller b52ef06a1020b8e1cda4e906b4859ae878e137d06cc1d7f0b20ddfd2142cb9d6 in both producer and fixture locations.

The reuse judgment is reasonable for this bounded change: explicit admission identities affect requests containing those selections, ordinary admissions record an empty selection list, and ordinary source pinning/reuse plus the changed failed-artifact projection have current targeted execution. Agent, container, metadata, UI and trigger paths retain prior evidence rather than being represented as freshly run. The accounting code checks normative identities/bindings, proof hashes/results and attributed impact decisions; its human impact rationale remains review judgment, not a behavioral oracle.

The heavy composition is one fresh unchanged CLI0.5.6 successful Prepare1/Windows2/Release3 chain, version1297/release434492192. Its post-success observer invocation remains raw FAIL. The corrected continuation reopens the same installation with delivery worker removed, uses the existing actor identity, checks retained execution count and performs ordinary REST/Workbench/byte observation. Composing these receipts is adequate for the selected outcome; it does not turn the failed NUnit invocation into PASS. Root8/version918 remains separate. No built T3 GUI/CLI launch is required by owner scope.

## Earlier findings independently reassessed

- Ordinary CLI journey/readbacks: fresh chain and separate continuation address the previous composition gap; current fixture checks actual IDs, source, assigned Agents, exact assets, logs and configuration history.
- Clean-checkout publisher dependency: committed manifest.mjs and publication-state.mjs remove the ignored-helper dependency. The publication validator imports that committed helper.
- Awaited worker lifetime: recovery fixture awaits StopAsync in finally under a 30-second bound; coordinator RunAsync joins preparation/source tasks on exit.
- Mandatory checksum: publication-state.mjs requires SHA256SUMS and exact canonical bytes for every manifest payload, rejecting its absence and alternate/missing entries.

No new engine or speculative abstraction was identified that warrants a blocking simplification request.

## Explicit limits and remaining delivery conditions

This review did not rerun any checks or independently download remote assets; raw receipts are retained evidence. Not every large reference JSON capture or migration designer line was individually audited. Existing main R0 acceptance and D46 scope were treated as authoritative. The four bounded explicit-selection observations support the selected behavior, not full literal wire conformance or physical deleted-byte behavior, other settings/branches/graphs, secure-token or absent-field variants. Those broader profiles remain unaccepted.

Parent still owns independent Security/Adversarial review at this candidate, resolving any resulting due findings, exact-revision evidence preservation, zero due debt and reviewed main delivery/task closure. This artifact does not close issues or declare milestone/main acceptance. No additional full suite, heavy rebuild or publication is requested by this review.
