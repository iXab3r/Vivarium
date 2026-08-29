# TeamCity CI/CD handover

Status: active; simplified TeamCity pipeline imported, Windows Compile evidence green.

## Current contract

Cake is the provider-neutral build driver. `Compile`/`CompileAll` produce runnable local trees and a
small `build-info.json` containing RID, version, and source SHA. `Release` checks that identity, then
packages the trees and runs one host-native final-ZIP smoke. `Publish` only uploads the ready Release
artifact to GitHub. All Cake build, test, and publish subprocesses use one MSBuild worker.

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

- TeamCity Windows Compile build `30847` (`0.1.0.8-a09f9fcc`) succeeded on `laptop-g15` at revision
  `a09f9fc`: 151 passed, 1 ignored, and the isolated native product smoke returned `viv-cli 0.1.0`.
  Its 23-file Compile artifact contains the simplified 108-byte `build-info.json` and no checksummed
  Compile manifest.
- Local macOS Cake CI succeeded: 142 passed, 9 platform skips.
- Local osx-arm64 Compile/native product smoke and deterministic release packaging have succeeded.
- The prerelease matrix passed for all four RIDs, and wrong version or wrong source SHA was rejected
  before packaging. Root tests pass 143 with 9 platform skips, and the matching final osx-arm64 release
  ZIP smoke is green.
- The simplification pass removed per-file Compile hashes, deep duplicate archive-layout verification,
  duplicate agent/CLI staging copies, and global NuGet locked mode while retaining top-level release
  checksums, deterministic ZIPs, final native smoke, prior D3 diagnostics, and guarded Publish behavior.
- No compatible Linux x64, Linux arm64, or macOS arm64 TeamCity agent is currently available.
- D29 still requires the controller Git prerequisite to be proven on every controller RID.
- GitHub publication stays paused until protected tags, immutable releases, and a publish-only secret
  are configured and exercised without creating a public end-user release.

## Next steps

1. Add or rotate compatible SDK 10.0.303 agents for the other three RIDs and run their Compile builds.
2. Run `Release` from a protected SemVer tag and verify it reuses exact Compile artifacts.
3. Configure the publish-only GitHub secret, then explicitly unpause and exercise `Publish` only after
   the release activation gates are closed.
