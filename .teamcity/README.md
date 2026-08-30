# TeamCity bootstrap

The Kotlin DSL is the desired CI/CD configuration. It deliberately does not contain server credentials,
GitHub tokens, or a pull-request feature. Manual builds may select any branch from the public Vivarium
VCS root; the automatic VCS trigger runs only the default branch.

Validate before import:

```text
teamcity project settings validate .teamcity
```

Run validation with JDK 21 active. The current TeamCity DSL security agent does not support Java 26
and fails before compiling the Kotlin configuration.

Create the TeamCity project from `https://github.com/iXab3r/Vivarium.git`, then enable versioned settings
from `.teamcity` with **use settings from the default branch**. Do not enable fork pull requests.

The project deliberately contains exactly three configurations:

- `Compile` builds and tests once, cross-publishes every supported RID with one version, and runs a
  native product smoke only for the current host;
- `Release` packages the Compile output without compiling or testing and runs one host-native
  smoke from the final ZIP;
- `Publish` downloads the Release artifact and uploads it to GitHub.

Compile is automatically triggered on the default branch. Any agent with the exact SDK from
`global.json` can run the complete cross-platform build; separate Linux and macOS agents are not
required. Configure commit-status publication after the chain is proven.

Agent requirement:

- any supported host with the exact SDK from `global.json`.

`Release` has no trigger, is serialized, and starts one fresh Compile. `VivariumVersionBase` `0.1` and
Compile counter `123` stamp the exact code version `0.1.123` into every RID. Release and Publish inherit
that exact version from their dependencies.

Publish has no trigger. Its only missing configuration is a TeamCity password parameter named
`github.release.token` with GitHub Contents write access for this repository. The publisher uses the
GitHub REST API directly, creates `v<version>` at the build source SHA, resumes a compatible draft, and
treats an already-published release with the expected assets as a successful rerun.
