# TeamCity CI/CD handover

Status: active; TeamCity project exists, Windows evidence is green, simplified pipeline import pending.

## Current contract

Cake is the provider-neutral build driver. `Compile`/`CompileAll` produce runnable local trees for the
four supported RIDs, `Release` only packages those trees, and `Publish` only uploads the ready Release
artifact to GitHub. All Cake build, test, and publish subprocesses use one MSBuild worker.

The versioned TeamCity project contains exactly six configurations:

1. `Compile / Windows x64` — full solution build and test once, win-x64 compilation, native smoke;
2. `Compile / Linux x64` — linux-x64 compilation and native smoke;
3. `Compile / Linux arm64` — linux-arm64 compilation and native smoke;
4. `Compile / macOS arm64` — osx-arm64 compilation and native smoke;
5. `Release` — artifact-only deterministic packaging from all four Compile builds;
6. `Publish` — paused GitHub deployment consuming only the Release artifact.

There are no TeamCity Verify, composite gate, or standalone release-smoke configurations. Archive
validation is internal to Release; platform execution is internal to the producing Compile build.
GitHub Actions remains explicitly disabled by the project owner.

## Evidence and blockers

- TeamCity Windows build `30843` succeeded on `laptop-g15` with SDK 10.0.303: 152 passed, 2 ignored.
- Local macOS Cake CI succeeded: 142 passed, 9 platform skips.
- Local osx-arm64 Compile/native product smoke and deterministic release packaging have succeeded.
- No compatible Linux x64, Linux arm64, or macOS arm64 TeamCity agent is currently available.
- D29 still requires the controller Git prerequisite to be proven on every controller RID.
- GitHub publication stays paused until protected tags, immutable releases, and a publish-only secret
  are configured and exercised without creating a public end-user release.

## Next steps

1. Validate and import the simplified Kotlin DSL.
2. Run `Compile / Windows x64` and record the new build ID plus TRX/native-smoke evidence.
3. Add or rotate compatible SDK 10.0.303 agents for the other three RIDs and run their Compile builds.
4. Run `Release` from a protected SemVer tag and verify it reuses exact Compile artifacts.
5. Configure the publish-only GitHub secret, then explicitly unpause and exercise `Publish` only after
   the release activation gates are closed.
