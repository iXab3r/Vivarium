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
