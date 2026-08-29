# TeamCity CI/CD handover

Status: active; simplified TeamCity pipeline imported, Windows Compile evidence green.

## Current contract

Cake is the provider-neutral build driver. `Compile`/`CompileAll` produce runnable local trees and
checksummed provenance manifests for the four supported RIDs. `Release` accepts only trees whose RID,
version, source SHA, file inventory, and digests match the candidate, then packages them and runs one
host-native final-ZIP smoke. `Publish` only uploads the ready Release artifact to GitHub. All Cake
build, test, and publish subprocesses use one MSBuild worker.

The versioned TeamCity project contains exactly six configurations:

1. `Compile / Windows x64` — full solution build and test once, win-x64 compilation, native smoke;
2. `Compile / Linux x64` — linux-x64 compilation and native smoke;
3. `Compile / Linux arm64` — linux-arm64 compilation and native smoke;
4. `Compile / macOS arm64` — osx-arm64 compilation and native smoke;
5. `Release` — verified artifact-only deterministic packaging plus one host-native final-ZIP smoke;
6. `Publish` — paused GitHub deployment consuming only the Release artifact.

There are no TeamCity Verify, composite gate, or standalone release-smoke configurations. Manifest and
archive validation is internal to Release; platform execution is internal to the producing Compile
build. GitHub Actions remains explicitly disabled by the project owner.

## Evidence and blockers

- TeamCity Windows Compile build `30845` succeeded on `laptop-g15` with SDK 10.0.303: 150 passed,
  1 ignored, native product smoke green, 22 Compile files plus TRX published.
- Local macOS Cake CI succeeded: 142 passed, 9 platform skips.
- Local osx-arm64 Compile/native product smoke and deterministic release packaging have succeeded.
- The hardened prerelease matrix passed for all four RIDs; wrong version, wrong source SHA, and a
  deliberately modified Compile file were all rejected before packaging. Root tests now pass 143 with
  9 platform skips, and the matching final osx-arm64 release ZIP smoke is green.
- No compatible Linux x64, Linux arm64, or macOS arm64 TeamCity agent is currently available.
- D29 still requires the controller Git prerequisite to be proven on every controller RID.
- GitHub publication stays paused until protected tags, immutable releases, and a publish-only secret
  are configured and exercised without creating a public end-user release.

## Next steps

1. Add or rotate compatible SDK 10.0.303 agents for the other three RIDs and run their Compile builds.
2. Run `Release` from a protected SemVer tag and verify it reuses exact Compile artifacts.
3. Configure the publish-only GitHub secret, then explicitly unpause and exercise `Publish` only after
   the release activation gates are closed.
