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
from `.teamcity` with **use settings from the default branch**. Do not enable fork pull requests. Configure
the GitHub App and commit-status publisher only after the server connection exists and the `CI gate`
status is proven on all three operating systems.

Agent activation requirements:

- Windows x64 with an SDK from the feature band selected by `global.json`;
- Linux x64 with that SDK feature band, Cargo, and at least 4 GiB free for self-contained publishes;
- macOS arm64 with that SDK feature band;
- Linux arm64 with that SDK feature band before the stable `Release gate` can complete.

The current three-agent license has no macOS agent and no Linux arm64 agent. Capacity must therefore be
rotated or the license/capacity changed before stable release activation. `Release / Assemble candidate`
has no trigger, is serialized, accepts only a SemVer `v*` tag through the Cake validation, and publishes
candidate artifacts without GitHub credentials. GitHub publication remains disabled until draft/resume,
remote-digest, protected-tag, D29 Git-prerequisite, and native four-RID evidence are all closed.

`Release / Publish GitHub` is committed paused. Before enabling it, create a TeamCity password parameter
named `github.release.token` whose fine-grained GitHub credential is restricted to this repository and to
release publication plus the read-only administration permission needed to verify immutable-release policy.
Enable GitHub release immutability and protected `v*` tag rules first. The publisher never clobbers an asset:
it resumes only a compatible draft, checks GitHub's remote `sha256:` digests, publishes last, and treats an
already-published exact immutable release as a successful idempotent rerun.
