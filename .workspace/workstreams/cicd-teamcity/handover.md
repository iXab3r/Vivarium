# TeamCity CI/CD handover

Status: active; cross-platform binary pipeline proven, Docker publisher imported, first image pending.

## Current contract

Cake is the provider-neutral build driver. TeamCity contains four explicit configurations:

1. `Compile` — build and test once, cross-publish all four RIDs with its own counter as the shared
   patch version, then smoke only the current host binary;
2. `Release` — package the exact Compile artifact into twelve deterministic ZIPs and smoke the final
   host-native ZIP;
3. `Publish / GitHub` — upload only the Release artifact to GitHub;
4. `Publish / Docker` — build the released linux-x64 server as a non-root container, probe its version,
   and push `<version>` plus `latest` to the EyeAuras registry.

There are no per-OS Compile configurations, Build Number helper, Verify stage, composite gate,
standalone release-smoke configuration, manifests, or checksum inventories. Separate Linux and macOS
agents are not required to produce their binaries. GitHub Actions remains disabled by owner decision.

GitHub publication uses the REST API directly. It does not require GitHub CLI, immutable-release
settings, a specific agent OS, or a pre-existing tag. Docker publication uses the existing Cvat Docker
engine only for image packaging and depends on successful GitHub publication. The only missing
TeamCity configuration that prevents the final chain from running is `github.release.token`.

## Evidence

- Local macOS Cake CI is green: 143 tests passed and 9 platform tests were skipped.
- Sequential `CompileAll --build-counter 4242` stamped `0.1.4242` into all 16 final executables for
  `win-x64`, `linux-x64`, `linux-arm64`, and `osx-arm64`.
- `Release --build-version 0.1.4242` produced exactly twelve valid ZIPs. Each server ZIP embeds all
  four agent packages, and the final osx-arm64 smoke returned `viv-cli 0.1.4242`.
- The earlier Windows-only TeamCity configuration proved the native Windows path: builds `30852`,
  `30854`, and `30856` succeeded with versions `0.1.1`, `0.1.2`, and `0.1.3`; each passed 151 tests
  with 1 ignored.
- The Kotlin DSL validates on JDK 21 as one project, four build configurations, and one
  VCS root.

## Next steps

1. Add `github.release.token` as a TeamCity password parameter with GitHub Contents write access.
2. Run `Publish / Docker`; its dependency publishes GitHub first, then Cvat builds, smokes, and pushes
   the versioned container image.
