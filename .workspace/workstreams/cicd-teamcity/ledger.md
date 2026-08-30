# TeamCity CI/CD migration ledger

| Stable ID | Target | Phase | Owner/review | State | Required evidence |
|---|---|---|---|---|---|
| gha-job:test | Cake `CI` in `Compile` | build-driver / TeamCity | Test Steward, Platform | Windows server evidence green | full suite runs exactly once per revision; deterministic TRX imported |
| gha-job:payload-smoke | Cake `PayloadSmoke` retained as an explicit local diagnostic | build-driver | Test Steward, Platform | implemented; not a separate TeamCity stage | native diagnostic remains runnable without multiplying the full suite |
| gha-job:payload-cross-macos-publish | Cake cross-publish target retained as an explicit diagnostic | build-driver | Test Steward, Platform | implemented; not in the simplified TeamCity chain | exact transferred-artifact diagnostic can be run when required |
| gha-job:payload-cross-macos-run | Cake transferred-payload target retained as an explicit diagnostic | build-driver | Test Steward, Platform | implemented; not in the simplified TeamCity chain | native macOS diagnostic can be run when required |
| gha-job:payload-nextest | pinned nextest archive diagnostic | build-driver | Test Steward, Security | implemented; not in the simplified TeamCity chain | archive/remap smoke with verified tool bytes when explicitly requested |
| local-build:rid-matrix | Cake `Compile` / `CompileAll` runnable trees | build-driver | Platform, Test Steward | local evidence complete | all four binary formats from one cross-platform Compile; host-native smoke |
| release-rid:win-x64 | Compile artifact plus native product smoke | compile / release | Agent API/SDK, Platform | simplified Windows Compile evidence green | win-x64 Compile artifact is natively runnable and packaged unchanged by Release |
| release-rid:linux-x64 | Cross-published Compile artifact | compile / release | Agent API/SDK, Platform | local cross-publish evidence green | linux-x64 artifact is included unchanged by Release; native execution is optional follow-up evidence |
| release-rid:linux-arm64 | Cross-published Compile artifact | compile / release | Agent API/SDK, Platform | local cross-publish evidence green | linux-arm64 artifact is included unchanged by Release; native execution is optional follow-up evidence |
| release-rid:osx-arm64 | Cross-published Compile artifact plus native smoke | compile / release | Agent API/SDK, Platform | local evidence green | osx-arm64 artifact is natively runnable and packaged unchanged by Release |
| release-contract:layout | controller/agent/CLI package layout | release | Agent API/SDK, Platform, Security | local verification complete; integration review pending | D19 naming-only bootstrap change, exact 12-ZIP output, and native smoke |
| release-contract:docker | server container distribution and update | release | TeamCity, Platform, Security | local DSL/build validation complete; first registry publish pending | exact linux-x64 release bytes, non-root runtime, persistent data volume, versioned + latest tags |
| release-contract:d29-git | bundle or verify system Git on every controller RID | release | Platform, Security, Docs | activation gate | accepted D29 commit and per-RID evidence before stable release |
| release-contract:github | token-gated GitHub REST publication | publication | Security | implemented; TeamCity token missing | with `github.release.token`, create/resume draft, upload expected assets, and publish |
| teamcity:trusted-source | default-branch DSL and trusted PR policy | TeamCity | Security, Git/Versioning | implemented; server evidence pending | fork PR cannot alter settings or receive credentials |
| teamcity:pipeline-shape | Compile -> Release -> Publish / GitHub -> Publish / Docker | TeamCity | TeamCity Expert, Test Steward | four configurations validate locally; server import pending | one Compile counter and source revision stamp every RID; Release contains no compile/test step; publishers consume only Release output |
| teamcity:test-reporting | import deterministic TRX from Compile | TeamCity | Test Steward | Windows server evidence green | passing/failing/cancelled results visible from the single test run |
| cutover:github-actions | remove automatic GitHub Actions workflow | cutover | Reconciliation Lead, Docs | closed by owner decision | remote `ci` workflow `343961597` is `disabled_manually`, YAML removed; automatic CI gap documented pending TeamCity activation |

States distinguish `pending`, `in progress`, `blocked`, `activation gate`, `unclassified`, and eventually `closed`.
