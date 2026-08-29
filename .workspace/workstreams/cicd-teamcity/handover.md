# TeamCity CI/CD handover

Status: active; simplified TeamCity pipeline imported, Windows Compile evidence green.

## Current contract

Cake is the provider-neutral build driver. `Compile`/`CompileAll` stamp the selected product version and
produce runnable local trees. `Release` packages the exact TeamCity snapshot artifacts and runs one
host-native final-ZIP smoke. `Publish` only uploads the ready Release artifact to GitHub. All Cake build,
test, and publish subprocesses use one MSBuild worker.

The versioned TeamCity project contains exactly six configurations:

1. `Compile / Windows x64` — full solution build and test once, win-x64 compilation, native smoke;
2. `Compile / Linux x64` — linux-x64 compilation and native smoke;
3. `Compile / Linux arm64` — linux-arm64 compilation and native smoke;
4. `Compile / macOS arm64` — osx-arm64 compilation and native smoke;
5. `Release` — artifact-only deterministic packaging plus one host-native final-ZIP smoke;
6. `Publish` — paused GitHub deployment consuming only the Release artifact.

There are no TeamCity Verify, composite gate, or standalone release-smoke configurations. Release does
not produce bookkeeping manifests or checksum inventories; platform execution is internal to the
producing Compile build. GitHub Actions remains explicitly disabled by the project owner.

## Evidence and blockers

- TeamCity Windows Compile build `30848` (`0.1.0.9-1cceb736`) succeeded at revision `1cceb73`: 151
  tests passed, 1 was ignored, and the native product smoke passed. Its `win-x64` artifact contains
  only `agent/`, `cli/`, and `server/`, confirming that the final bookkeeping identity file is gone.
- Local macOS Cake CI succeeded: 142 passed, 9 platform skips.
- Local osx-arm64 Compile/native product smoke and deterministic release packaging have succeeded.
- The prerelease matrix passed for all four RIDs. Root tests pass 143 with 9 platform skips, and the
  matching final osx-arm64 release ZIP smoke is green.
- The final simplification removed Compile/release/package manifests, checksum inventories, duplicate
  archive verification, duplicate agent/CLI staging copies, and global NuGet locked mode. Deterministic
  packaging, final native smoke, prior D3 diagnostics, and guarded Publish behavior remain.
- A simulated TeamCity tag build proved that `v0.2.0` produces build number
  `0.2.0.99-dddddddd` and `viv-cli 0.2.0`. Sequential four-RID Compile and the exact twelve-ZIP Release
  both passed locally; root tests remain green at 143 passed and 9 platform skips.
- No compatible Linux x64, Linux arm64, or macOS arm64 TeamCity agent is currently available.
- D29 still requires the controller Git prerequisite to be proven on every controller RID.
- GitHub publication stays paused until protected tags, immutable releases, and a publish-only secret
  are configured and exercised without creating a public end-user release.

## Next steps

1. Add or rotate compatible SDK 10.0.303 agents for the other three RIDs and run their Compile builds.
2. Run `Release` from a protected SemVer tag and verify it reuses exact Compile artifacts.
3. Configure the publish-only GitHub secret, then explicitly unpause and exercise `Publish` only after
   the release activation gates are closed.
