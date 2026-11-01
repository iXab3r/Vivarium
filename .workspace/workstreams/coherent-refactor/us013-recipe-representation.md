# US-013 P0: explicit T3 settings in C#/Git

Lead: Story Delivery; co-held TeamCity, Git/Versioning, Testing Architect, Test Steward and Docs.
This continues #78 and #110/#111/#112. No new story, task graph, SDK or product authority is created.

## Result and boundary

`tools/definition-proof/T3Recipe.cs` translates all **explicit product declarations** in the pinned
T3 Kotlin file. It fills omissions in the original smaller representability seed: Agent checkout,
the source display name, complete Prepare shell/error handling, Docker image/platform/options/cache
mounts, all five PowerShell properties, and explicit custom VCS quiet-period mode. The original seed
remains the computed-parameter/inheritance and physical-recovery fixture.

The new NUnit source proof uses actual separate Git repositories and compilation children. It
compiles committed C#, passes the complete declared settings through the existing managed editor,
compares the effective plan byte-for-byte, commits owned source/Agent/cache/trigger edits, and
reevaluates the historical C# commit. User-code bytes remain unchanged. JSON is transient editor input
or immutable evaluator output; only C# is desired source. Step IDs are attributed proof IDs because
the Kotlin source does not explicitly assign them.

This proves representation and editing of the literal fields, **not** execution, complete effective
TeamCity defaults, a public SDK or accepted configuration model. The candidate has one source and
common checkout rules for these three configurations; it does not generalize that into a catalog
restriction. Arbitrary computed fields, templates, mutation modes, concurrency, current grants,
secret resolution and the actual Host activation/completion contract remain P0 obligations.

## Independent source identity

- Workload commit: `7ff65cada80dd817aa5d7467b85e78ec30c578a9`.
- Historical settings commit, independently obtained from the server:
  `7495b6859c91952ad91478a2428be805a4e48f1a`.
- Both `.teamcity/settings.kts` blobs: `2eccef048973d5151c93aba0df0b96c913bfd20d`, 5,046 LF bytes.
- The checked-in reference fixture retains the original source and MIT notice. The test validates
  its original Git blob after explicit CRLF-checkout normalization; that is not a claim that arbitrary
  input byte changes are harmless.
- Fresh read-only GitHub acquisition also matched the retained Prepare, Windows, orchestration and
  publisher script blob identities. Those original bytes remain ignored under
  `artifacts/us013-recipe/reference`; they were read, not executed.

The Release recipe has default publication `release`, the two literal artifact inputs and Windows
snapshot dependency, 15-minute timeout and custom 60-second default-branch trigger. Prepare/Windows
retain 90-minute timeouts. The historical cohort has **no** later metadata exclusion rules; the owned
configuration candidate attributes those rules as a separate edit. Product/settings/source identities
remain distinct. This is not independent attestation of every byte historically consumed by an Agent.

## Concrete workload adaptations still due

| Pinned input | Required owned-fixture change and retained behavior |
|---|---|
| `run.mjs`: repository constant, updater destination and two updater assertions | Point all three at the verified owned test repository. Preserve compile/package, manifest version/revision/digest checks and archive/asset validation. |
| `publish.sh`: token-preflight description and repository | Use the same verified test repository and appropriately scoped private credential. Preserve default publication, tag conflict checks and draft/resume behavior; later acceptance verifies real release bytes/effects. |
| `windows.ps1`: `UserInteractive` guard, `C:\BuildAgent`/Python/VP paths | Remove the launch-only guard and bind verified private tool locations for the current Windows Agent. Keep build tool versions and actual command failures visible. |
| `run.mjs`: two CLI archive smoke calls, Linux and Windows desktop smoke calls | Remove built-application launch checks under the owner's explicit build/package scope. Record that delta; do not call those checks passed. Retain package/manifest/archive checks. Linux AppImage extraction is a separate packaging step to assess explicitly before execution. |
| Prepare Docker cache volumes and Agent-name requirements | Use verified run-owned caches and separately owned Linux/Windows processes, preserving paths/options needed by the real build. Do not reuse current fleet Agents or global host-tool locations implicitly. |
| Prepare credential preflight | It currently requires publication access before compilation. Resolve the concrete owned repository/credential before the heavy run; this is not a prerequisite for independent P0 work. |

The current C# owned candidate changes only configuration inputs. It does not silently patch these
scripts or establish the repository/credentials/tools. The later adapted workload must have its own
Git identity and exact deltas before execution. No T3 build/package, application launch, release,
R0/PILOT implementation, P0 acceptance or main merge occurred in this proof.

## Verification

The root build passes with zero warnings/errors. The fresh full root run has 490 passed and zero
failed, with the existing platform skips (11 console skips; 12 literal TRX NotExecuted rows). All
147 TeamCity controls pass separately. The explicitly invoked definition target has 9 passed and
zero skipped: the new literal recipe proof plus all eight Git/PostgreSQL recovery controls.
Owned proof containers, networks and volumes are absent after cleanup.

The fresh raw product baseline remains five genuine HTTP-response mismatches. The original
fifteen-case cohort still has five RED, ten product-prerequisite-blocked and zero GREEN. Source
registers retain their unknown normalized denominator and all 78 declared gaps. These distinct
counts are not combined into a compatibility percentage.

Exact commands, current-source fingerprints, retained binaries, TRX files, original Git revisions and
limits are in [the checkpoint receipt](us013-recipe-results.json). Original source/accounting receipts
remain immutable historical evidence. The offline accounting command must bind a separate assessment
to the exact commit; it executes no tests and cannot promote this P0 result into acceptance.
