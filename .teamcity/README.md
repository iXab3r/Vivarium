# TeamCity bootstrap

The Kotlin DSL is the desired CI/CD configuration. It deliberately does not contain server credentials,
GitHub tokens, or a pull-request feature. Persistent build agents execute only `main`, repository-owned
`feature/*` branches, and protected `v*` tags from the public Vivarium VCS root. The automatic VCS trigger
runs only the default branch.

Validate before import:

```text
teamcity project settings validate .teamcity
```

Run validation with JDK 21 active. The current TeamCity DSL security agent does not support Java 26
and fails before compiling the Kotlin configuration.

Create the TeamCity project from `https://github.com/iXab3r/Vivarium.git`, then enable versioned settings
from `.teamcity` with **use settings from the default branch**. Do not enable fork pull requests.

The project deliberately contains only six configurations:

- `Compile / Windows x64` builds, runs the complete test suite once, compiles win-x64 products, and
  runs their native product smoke;
- the Linux x64, Linux arm64, and macOS arm64 Compile configurations compile only their RID and run
  the same short native product smoke without repeating the full test suite;
- `Release` verifies all four Compile manifests, packages without compiling or testing, and runs one
  host-native smoke from the final ZIP;
- `Publish` downloads the Release artifact and uploads it to GitHub.

Only Windows Compile is automatically triggered on the default branch while it is the only compatible
active agent. The other Compile configurations and the downstream chain become runnable as matching
agents are added. Configure commit-status publication after the Compile matrix is proven.

Agent activation requirements:

- Windows x64 with the exact SDK from `global.json`;
- Linux x64 with that SDK;
- macOS arm64 with that SDK;
- Linux arm64 with that SDK before Release can complete.

The current three-agent license has no macOS agent and no Linux arm64 agent. Capacity must therefore be
rotated or the license/capacity changed before stable release activation. `Release` has no trigger, is
serialized, accepts only a SemVer `v*` tag through the Cake validation, and packages candidate artifacts
without GitHub credentials. Tag Compile builds bake that SemVer into the binaries, and Release rejects
any input whose RID, source SHA, version, file inventory, or digests disagree. GitHub publication remains
disabled until draft/resume,
remote-digest, protected-tag, D29 Git-prerequisite, and native four-RID evidence are all closed.

`Publish` is committed paused. Before enabling it, create a TeamCity password parameter
named `github.release.token` whose fine-grained GitHub credential is restricted to this repository and to
release publication plus the read-only administration permission needed to verify immutable-release policy.
Enable GitHub release immutability and protected `v*` tag rules first. The publisher never clobbers an asset:
it resumes only a compatible draft, checks GitHub's remote `sha256:` digests, publishes last, and treats an
already-published exact immutable release as a successful idempotent rerun.
