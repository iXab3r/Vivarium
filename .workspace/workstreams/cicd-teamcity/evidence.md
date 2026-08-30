# TeamCity CI/CD migration evidence

## Baseline — 2026-08-29

- Source revision: `09997927c45344ee8512655e6998a80dccf4a818` (`main`, worktree branch `feature/build`).
- Worktree was clean before implementation.
- Baseline GitHub Actions inventory: five jobs recorded in `inventory.tsv`.
- Current TeamCity census: 30 projects, 75 build configurations, three connected and authorized agents.
- CLink reference chain: `Compile -> SmokeTest -> PackageRelease`, using exact snapshot and artifact
  dependencies; successful GitHub publication build `30212` ran on `laptop-g15`.
- Root TeamCity connection inventory contains legacy GitHub OAuth and no GitHub App connection.
- GitHub repository has no tags and no releases.
- The project owner explicitly disabled GitHub Actions before TeamCity parity. Remote workflow `ci`
  (`343961597`) was verified as `disabled_manually`, and `.github/workflows/ci.yml` was removed from the
  implementation branch. Until TeamCity activation, pushes and pull requests have no automatic CI.
- Independent plan review verdict: changes required. All blocker corrections are represented in the
  ledger and phases before implementation began.

## Pending evidence

Do not convert a pending platform or release gate into a passing claim based on local macOS evidence.

## Build driver and release candidate — 2026-08-29

- `dotnet build Vivarium.slnx -m:1 --nologo -p:ContinuousIntegrationBuild=true`: succeeded with zero
  warnings and zero errors using committed NuGet lock files.
- Cake `CI`: succeeded on macOS arm64; 142 passed, 9 platform skips, 0 failed. Deterministic TRX was
  verified at `out/test-results/macos/vivarium-tests.trx`.
- Cake `PayloadSmoke`: native osx-arm64 self-contained payload succeeded; 2 passed, 1 expected skip.
- Cake `PayloadCrossMacPublish` plus `PayloadCrossMacRun`: succeeded locally. This proves target
  mechanics, not the required exact Linux-produced artifact transfer evidence.
- Cake `PayloadNextest`: intentionally not claimed on macOS; Linux x64 evidence remains pending.
- Cake `ReleasePackage` published `viv-server`, `viv-agent`, `viv-agent-update`, and `viv-cli` for
  all four supported RIDs, assembled twelve ZIPs plus manifest/checksums, and passed exact static
  verification.
- Before the portable-controller correction, two complete post-lock `ReleasePackage` runs with source
  `09997927c45344ee8512655e6998a80dccf4a818` and version `0.1.0` produced identical SHA-256 for all
  fourteen release files. The current controller ZIP contract now also contains its settings/static
  assets and embeds SQLite native bytes in the single-file executable. After the public binary/archive
  rename, two complete sequential `ReleasePackage` runs again produced identical SHA-256 values for all
  fourteen corrected release files, and both passed `ReleaseVerify`.
- The packager now publishes and stages one component/RID at a time, removes only its generated
  intermediates immediately, fails early unless the output volume has at least 4 GiB free, and all
  Cake `dotnet build`, `dotnet test`, and `dotnet publish` calls use one MSBuild worker.
- Cake `ReleaseSmoke --rid osx-arm64` succeeded from the final ZIPs: the controller started with an
  isolated data directory and served packaged `app.css` over HTTPS, `viv-cli --version` returned 0, and the
  `viv-agent`/`viv-agent-update` fail-closed probes returned their expected code 2.
- No GitHub Release API mutation was executed.

## Local RID builds — 2026-08-29

- The implementation follows the existing PoEBane/CLink Cake pattern: host build/test remains separate
  from explicit RID publication, and the provider wrapper contains no build logic.
- Cake `PublishAll` completed sequentially with one MSBuild worker and produced runnable trees for
  `win-x64`, `linux-x64`, `linux-arm64`, and `osx-arm64` under `out/build/<rid>/`.
- Representative controller and CLI binaries were identified as PE32+ x64, ELF x64, ELF AArch64, and
  Mach-O arm64 respectively. Every controller tree contains its static web assets and settings.
- On the native osx-arm64 host, `viv-server` startup, SQLite initialization, and packaged `app.css`
  HTTP 200 were proven; `viv-cli` returned version `0.1.0`; `viv-agent`/`viv-agent-update` failed closed
  with the expected code 2.
- Cake `Test --rid win-x64` on osx-arm64 refused to execute tests for the foreign RID. The native test
  suite remained green: 142 passed, 9 platform skips, 0 failed.
- The first controller smoke exposed missing `e_sqlite3.dylib`. `IncludeNativeLibrariesForSelfExtract`
  now embeds SQLite native bytes while preserving the single-file executable contract, and the
  controller uses its executable directory as the portable content root.

## TeamCity configuration — 2026-08-29

- Homebrew TeamCity CLI `1.4.0` and Maven `3.9.16` were used with JDK 21; JDK 26 was rejected by the
  TeamCity security agent and is not a supported validator runtime.
- `teamcity project settings validate .teamcity --no-color`: `Configuration valid`, server
  `https://build.eyeauras.net`, one project, eleven build configurations, one VCS root.
- DSL defines default-branch-only automatic triggering, server checkout, no pull-request feature,
  deterministic TRX import, exact Linux-to-macOS artifact dependency, tri-OS CI composite, serialized
  candidate assembly, four native release smokes, and a paused GitHub deployment.
- The TeamCity project was not created and settings were not uploaded. Native agent compatibility and
  credential isolation are activation evidence, not local validation claims.

## Simplified Compile -> Release -> Publish pipeline — 2026-08-29

- The project owner replaced the earlier Verify/gate/release-smoke layout with four platform Compile
  configurations followed by Release and Publish. The complete test suite runs only in Windows Compile;
  each platform Compile retains a short native product smoke.
- Kotlin DSL validation succeeded with JDK 21: one project, six build configurations, one VCS root.
- Root `dotnet build` succeeded with zero warnings and errors; root `dotnet test` passed 142 tests and
  skipped 9 platform-specific tests on macOS arm64.
- Cake `Compile --rid osx-arm64` and `CompileSmoke --rid osx-arm64` succeeded. The native server served
  packaged static assets, CLI reported version 0.1.0, and agent/updater failed closed as expected.
- Cake `Release` consumed the four existing `out/build/<rid>` trees without invoking `dotnet publish`,
  produced and internally verified the deterministic asset set, and the final osx-arm64 ZIPs passed
  the optional local `ReleaseSmoke`.
- TeamCity imported exactly the six simplified configurations. Windows Compile build `30845`
  (`0.1.0.6-5205f60f`) succeeded on `laptop-g15`: 150 passed, 1 ignored, win-x64 compilation and
  native product smoke succeeded, and TeamCity published 22 Compile files plus the TRX artifact.

## Release identity hardening — 2026-08-29

- Compile initially wrote a checksummed file inventory. The simplification pass replaced it with a
  small `build-info.json` containing only RID, product SemVer, and source SHA; Release checks all four
  identities before deleting or writing any release output.
- A sequential four-RID `CompileAll` using product version `0.1.0-rc.1` completed successfully, and
  the native osx-arm64 Compile smoke proved that `viv-cli --version` preserves the prerelease identity.
- Release rejected Compile inputs when either the requested version or source SHA differed. The earlier
  per-file tamper check was deliberately removed as duplicate integrity bookkeeping.
- A matching prerelease Release completed deterministic packaging and automatically ran the final
  host-native ZIP smoke: controller/static asset, exact CLI version, agent, and updater probes passed.
- Root `dotnet build` completed with zero warnings and errors. Root `dotnet test` passed 143 tests and
  skipped 9 platform-specific tests on macOS arm64.
- TeamCity Windows Compile build `30846` (`0.1.0.7-ce7090f7`) succeeded on `laptop-g15` from source and
  settings revision `ce7090f7949a44379621019e805b1d214f28c209`: 151 tests passed, 1 was ignored,
  Compile and the isolated native product smoke passed, and TeamCity published 23 Compile files plus
  TRX. This build records the superseded checksummed Compile manifest; simplified evidence follows.

## Simplification pass — 2026-08-29

- Removed the per-file Compile inventory and hashes, deep re-validation of package trees created by the
  same Release process, duplicate Compile staging copies, and global RuntimeIdentifier/NuGet locked-mode
  settings. Eight generated NuGet lock files and unused duplicate tool-version entries were removed.
- Compile now writes only `build-info.json` with RID, product SemVer, and source SHA. The four files in
  the `0.1.0-rc.2` matrix were 109–113 bytes; sequential `CompileAll` completed for all four RIDs.
- Release rejected a mismatched version and a mismatched source SHA from build identity alone. A matching
  Release completed, verified the top-level assets/checksums, and passed the final osx-arm64 ZIP smoke.
- Root `dotnet build` succeeded with zero warnings and errors; root `dotnet test` passed 143 tests and
  skipped 9 platform-specific tests. TeamCity DSL validation still reports one project, six build
  configurations, and one VCS root.
- TeamCity Windows Compile build `30847` (`0.1.0.8-a09f9fcc`) succeeded on `laptop-g15` from source
  revision `a09f9fccd83d6967dfe9cfe0b972e460e1876ed2`: 151 tests passed, 1 was ignored, win-x64 Compile
  succeeded, and `CompileSmoke` completed in 3.9 seconds with `viv-cli 0.1.0` plus the expected
  fail-closed agent/updater probes.
- TeamCity published the TRX result and 23 win-x64 Compile files. The artifact root contains
  `agent/`, `cli/`, `server/`, and the 108-byte `build-info.json`; the superseded
  `compile-manifest.json` is absent.

## Owner-directed minimal release contract — 2026-08-29

- Removed the remaining CI bookkeeping files: Compile `build-info.json`, public
  `release-manifest.json`, embedded `packages/manifest.json`, `SHA256SUMS`, and the standalone
  `ReleaseVerify` target. Release now produces exactly the twelve D19 ZIP files and runs one native
  final-ZIP smoke.
- A simulated TeamCity Compile for tag `v0.2.0`, counter `99`, and source prefix `dddddddd` emitted
  `##teamcity[buildNumber '0.2.0.99-dddddddd']`; every `dotnet publish` received `-p:Version=0.2.0`,
  and native `viv-cli --version` returned `viv-cli 0.2.0`.
- Sequential `CompileAll --build-version 0.2.0` completed all four RIDs in 23.4 seconds. None of the
  Compile roots contained an identity, manifest, or checksum file.
- `Release --build-version 0.2.0` completed in 49.1 seconds. The output contained exactly twelve ZIPs;
  each server ZIP embedded the four conventional `packages/agents/viv-agent-<rid>.zip` files and no
  package manifest. The final osx-arm64 controller, CLI, agent, and updater smoke passed.
- An isolated reflection harness ran `BuildProcess.RunAsync` against `/bin/sleep 30` with a one-second
  timeout. It killed and reaped the process, drained redirected output, and returned the expected
  timeout in 1.05 seconds.
- Root `dotnet build` succeeded with zero warnings and errors. Root `dotnet test` passed 143 tests and
  skipped 9 platform-specific tests.
- TeamCity Windows Compile build `30848` succeeded from revision
  `1cceb7364739ce74d6a9a85d3a7253da3fe7508b` with build number `0.1.0.9-1cceb736`: 151 tests passed,
  1 was ignored, and the native product smoke passed. The published `win-x64` artifact contains only
  `agent/`, `cli/`, and `server/`; the removed `build-info.json` is absent.

## Shared code version — 2026-08-30

- `VivariumVersionBase` now owns human-selected `major.minor`; the default local product version is
  `0.1.0`, and the TeamCity counter supplies only the patch component.
- Kotlin DSL validation on JDK 21 succeeded with one project, seven build configurations, and one VCS
  root. The generated configuration resolves the same `Build Number` dependency into every Compile
  build; Release forces all four Compile dependencies to start fresh and inherits their exact version.
- A sequential four-RID `CompileAll --build-counter 4242` emitted TeamCity build number `0.1.4242` and
  passed `-p:Version=0.1.4242` to all 16 publishes. All 16 final executables contain informational
  version `0.1.4242+78a18c7f6f6a61b276c42fea1a46ae2d6cc6d0ce`, and every `agent/version` is
  `0.1.4242`.
- `Release --build-version 0.1.4242` produced exactly twelve valid ZIPs. Each server ZIP embeds all
  four agent packages, and the native final-ZIP smoke returned `viv-cli 0.1.4242` with the expected
  controller, agent, and updater behavior.
- Root `dotnet build` succeeded without warnings or errors. Root `dotnet test` passed 143 tests and
  skipped 9 platform-specific tests.
- TeamCity applied revision `e46aecdbdfa331fcf86eae69a780c56d2f268f8e` and imported all seven
  configurations. Windows Compile build `30852` depended on Build Number build `30851` (`1`) and
  finished as `0.1.1`; a second build of the same revision, `30854`, depended on Build Number build
  `30853` (`2`) and finished as `0.1.2`. Both passed 151 tests with 1 ignored. Build `30854` passed
  `-p:Version=0.1.2` to controller, agent, updater, and CLI publishes, and its native smoke returned
  `viv-cli 0.1.2`.

## Single cross-platform Compile — 2026-08-30

- The Kotlin DSL validates on JDK 21 as one project, exactly three build configurations, and one VCS
  root. The chain is `Compile -> Release -> Publish`; Compile has no operating-system requirement and
  runs CI once before cross-publishing all four RIDs.
- Local Cake `CI --build-counter 5000` succeeded with 143 tests passed and 9 platform tests skipped on
  macOS arm64. `CompileAll --build-counter 5000` then published all four RIDs sequentially with one
  MSBuild worker.
- All 16 final executables contain informational version
  `0.1.5000+c1262cd2c19d576b40ef226700576df0f27d3f42`, and all four `agent/version` files contain
  `0.1.5000`. The native Compile smoke returned `viv-cli 0.1.5000` and passed the controller, agent,
  and updater probes.
- `Release --build-version 0.1.5000` produced exactly twelve valid ZIPs. Every server ZIP embeds all
  four agent packages, and its automatic native final-ZIP smoke passed.
- Publish no longer depends on a specific operating system, GitHub CLI, or repository release-policy
  setting. A safe local invocation without `GH_TOKEN` stopped immediately with
  `Publish requires a resolved GH_TOKEN environment secret.` and made no GitHub API mutation.
- TeamCity applied revision `c7c8493d0ff4e7db2f364f986745d7ec299b1b85` and replaced the seven old
  configurations with exactly `Compile`, `Release`, and `Publish`. Publish is not paused and has no
  operating-system requirement; `env.GH_TOKEN` remains the unresolved `%github.release.token%` link.
- TeamCity Compile build `30857` succeeded on `laptop-g15` as version `0.1.15`: 151 tests passed, 1
  was ignored on Windows, and the published `build/` artifact contains `win-x64`, `linux-x64`,
  `linux-arm64`, and `osx-arm64` from the one build.
- TeamCity Release build `30859` reused Compile `30857`, preserved version `0.1.15`, passed its final
  Windows-native server/CLI/agent/updater smoke, and published exactly the twelve expected ZIPs.
  Publish was not executed and no GitHub release was created.

## Docker server distribution — 2026-08-30

- The old EyeAuras.Web pipeline was inspected from source and from successful TeamCity publish build
  `30500`: Windows produced the Linux application payload, Cvat only assembled and pushed the image,
  and Portainer rollout was separate and disabled. Vivarium keeps those useful boundaries without the
  legacy image-tar artifact or an in-CI deployment webhook.
- The official Microsoft registry contains `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble`.
  Vivarium's image copies the exact released `linux-x64` server tree, restores the executable bit,
  installs system Git, runs as the unprivileged `app` user, and makes only `/var/lib/vivarium`
  writable and persistent.
- Cake's new `DockerImage` target compiles successfully. It builds from an existing Compile tree and
  verifies the container reports the exact stamped product version; Docker is not installed on the
  local macOS host, so the image itself remains to be proven by the first Cvat publish.
- TeamCity DSL validation on JDK 21 reported one project, exactly four build configurations, and one
  VCS root. The initial Docker import chained GitHub before Docker; the Docker publisher also took the
  exact `viv-server-linux-x64.zip` artifact directly from Release and pushed
  `registry.eyeauras.net:5000/ixab3r/viv-server:<version>` plus `latest`.
- Root `dotnet build` succeeded with zero warnings or errors; root `dotnet test` passed 143 tests and
  skipped 9 Windows-only cases. Native `CompileSmoke` proved that both server and CLI `--version`
  probes report the exact stamped `0.1.6000` test version.
- TeamCity applied source/settings revision `399a24d90dc387b2e954592db6fa54e3a8076238` and now shows
  exactly `Compile`, `Release`, `Publish / GitHub`, and `Publish / Docker`. The Docker publisher has no
  configuration-health findings and has one compatible agent; no Docker build or registry push has
  run yet.

## Independent publication destinations — 2026-08-30

- The project owner required GitHub Releases and Docker registry publication to be independent because
  either destination may be used alone.
- The Kotlin DSL now gives both publishers exactly one snapshot dependency: `Release`. The generated
  XML confirms that Docker has no dependency on GitHub; the missing `github.release.token` therefore
  affects only `Publish / GitHub`.
- TeamCity applied revision `d362c0cc5bac165ef60078d890f26170a9be2c78`. The live dependency pages
  confirm that each publisher has exactly one snapshot dependency on Release and its own direct
  Release artifact dependency; neither publisher references the other.
