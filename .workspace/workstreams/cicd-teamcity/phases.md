# TeamCity CI/CD migration phases

## Phase 1 — provider-neutral build driver

Add the Cake.Frosting application, preserve the root `dotnet build` and `dotnet test` baseline, and
reproduce each baseline GitHub Actions command through a named Cake target. The project owner later
disabled GitHub Actions before TeamCity activation, accepting a temporary automatic-CI gap.

Gate: local root build/test plus Cake build, test, native payload smoke, cross-publish, and nextest
preflight where the host supports them.

## Phase 2 — TeamCity parity

Add versioned Kotlin DSL that invokes only Cake targets. Define one Compile configuration that builds
and tests once, cross-publishes every RID with its own counter as the shared code version, and smokes
only the current host. Settings remain trusted-default-branch configuration; fork pull requests are
not automatic on persistent agents.

Gate: Kotlin DSL generation, all-RID cross-publish, and a green host-native Compile smoke.

## Phase 3 — release contract and candidate assembly

Freeze the per-RID package layout with Agent API/SDK, Platform, and Security review. Make Release consume
only the exact Compile artifacts and perform deterministic packaging plus one native final-ZIP smoke.
Keep native execution in Compile and collect D29 Git prerequisite evidence.

Gate: the all-RID Compile artifact packages into the exact release layout and the host-native final ZIP
passes smoke. Additional native platform evidence and D29 remain follow-up product-quality work.

## Phase 4 — publication

Publish only the exact release-gated TeamCity artifact. GitHub and Docker are independent destinations
that both consume Release directly. GitHub uses the computed code version to create the release tag at
the source commit, plus a serialized manual deployment, a publish-only credential, draft-first upload,
and publish-last behavior through the GitHub REST API. Docker builds the Linux server image from the
same Release artifact and pushes its versioned and `latest` tags to the existing EyeAuras registry. CI
publishes artifacts and images; it does not deploy a controller.

Gate: each selected destination publishes successfully. GitHub alone requires `github.release.token`;
Docker uses its overridable repository and image-name defaults independently.

## Phase 5 — cutover

Publish the appropriate TeamCity Compile status to GitHub, reconcile authoritative documentation, and
rerun the census. GitHub Actions was removed earlier by explicit project-owner decision; do not restore
it without another explicit decision.

Gate: every inventory row is closed or has an explicit non-release activation condition; TeamCity is
the active automatic CI authority.
