# TeamCity CI/CD migration phases

## Phase 1 — provider-neutral build driver

Add the Cake.Frosting application, preserve the root `dotnet build` and `dotnet test` baseline, and
reproduce each baseline GitHub Actions command through a named Cake target. The project owner later
disabled GitHub Actions before TeamCity activation, accepting a temporary automatic-CI gap.

Gate: local root build/test plus Cake build, test, native payload smoke, cross-publish, and nextest
preflight where the host supports them.

## Phase 2 — TeamCity parity

Add versioned Kotlin DSL that invokes only Cake targets. Define one Compile configuration per RID,
allocate one shared build counter for the cross-platform chain, import the complete test suite's TRX
only from Windows x64, and run a native product smoke in every Compile configuration. Settings remain
trusted-default-branch configuration; fork pull requests are not automatic on persistent agents.

Gate: Kotlin DSL generation and green native Compile builds for every supported RID.

## Phase 3 — release contract and candidate assembly

Freeze the per-RID package layout with Agent API/SDK, Platform, and Security review. Make Release consume
only the exact Compile artifacts and perform deterministic packaging plus one native final-ZIP smoke.
Keep native execution in Compile and collect D29 Git prerequisite evidence.

Gate: all four RID rows have native package evidence. Linux arm64 and D29 are explicit stable-release
activation gates.

## Phase 4 — guarded publication

Publish only the exact release-gated TeamCity artifact. Use the computed code version to create the
release tag at the source commit, plus a serialized manual deployment, a publish-only credential,
draft-first upload, remote digest verification, publish-last, and immutable/idempotent reruns.

Gate: draft/staging interruption and mismatch tests pass without creating a public end-user release.

## Phase 5 — cutover

Publish the appropriate TeamCity Compile status to GitHub, reconcile authoritative documentation, and
rerun the census. GitHub Actions was removed earlier by explicit project-owner decision; do not restore
it without another explicit decision.

Gate: every inventory row is closed or has an explicit non-release activation condition; TeamCity is
the active automatic CI authority.
