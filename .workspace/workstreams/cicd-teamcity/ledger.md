# TeamCity CI/CD migration ledger

| Stable ID | Target | Phase | Owner/review | State | Required evidence |
|---|---|---|---|---|---|
| gha-job:test | Cake `CI` in `Compile / Windows x64` | build-driver / TeamCity | Test Steward, Platform | Windows server evidence green | full suite runs exactly once per revision; deterministic TRX imported |
| gha-job:payload-smoke | Cake `PayloadSmoke` retained as an explicit local diagnostic | build-driver | Test Steward, Platform | implemented; not a separate TeamCity stage | native diagnostic remains runnable without multiplying the full suite |
| gha-job:payload-cross-macos-publish | Cake cross-publish target retained as an explicit diagnostic | build-driver | Test Steward, Platform | implemented; not in the simplified TeamCity chain | exact transferred-artifact diagnostic can be run when required |
| gha-job:payload-cross-macos-run | Cake transferred-payload target retained as an explicit diagnostic | build-driver | Test Steward, Platform | implemented; not in the simplified TeamCity chain | native macOS diagnostic can be run when required |
| gha-job:payload-nextest | pinned nextest archive diagnostic | build-driver | Test Steward, Security | implemented; not in the simplified TeamCity chain | archive/remap smoke with verified tool bytes when explicitly requested |
| local-build:rid-matrix | Cake `Compile` / `CompileAll` runnable trees | build-driver | Platform, Test Steward | local evidence complete; target-native TeamCity evidence pending | all four binary formats; native `viv-server`/`viv-cli`/`viv-agent`/`viv-agent-update` smoke per Compile configuration |
| release-rid:win-x64 | Compile artifact plus native product smoke | compile / release | Agent API/SDK, Platform | simplified Windows Compile evidence green | win-x64 Compile artifact is natively runnable and packaged unchanged by Release |
| release-rid:linux-x64 | Compile artifact plus native product smoke | compile / release | Agent API/SDK, Platform | pending | linux-x64 Compile artifact is natively runnable and packaged unchanged by Release |
| release-rid:linux-arm64 | Compile artifact plus native product smoke | compile / release | Agent API/SDK, Platform | blocked | real Linux arm64 agent/evidence; cross-compile alone is insufficient |
| release-rid:osx-arm64 | Compile artifact plus native product smoke | compile / release | Agent API/SDK, Platform | local evidence; server evidence pending | osx-arm64 Compile artifact is natively runnable and packaged unchanged by Release |
| release-contract:layout | controller/agent/CLI package layout | release | Agent API/SDK, Platform, Security | local verification complete; integration review pending | D19 naming-only bootstrap change, exact 12-ZIP output, and native smoke |
| release-contract:d29-git | bundle or verify system Git on every controller RID | release | Platform, Security, Docs | activation gate | accepted D29 commit and per-RID evidence before stable release |
| release-contract:github | draft-first immutable/idempotent publication | publication | Security | implemented paused; activation evidence pending | negative API tests, draft resume, remote digest verification |
| teamcity:trusted-source | default-branch DSL and trusted PR policy | TeamCity | Security, Git/Versioning | implemented; server evidence pending | fork PR cannot alter settings or receive credentials |
| teamcity:pipeline-shape | four Compile configurations -> Release -> Publish | TeamCity | TeamCity Expert, Test Steward | six simplified configurations imported | six visible configurations; Release contains no compile/test step; Publish contains no packaging step |
| teamcity:test-reporting | import deterministic TRX from Windows Compile | TeamCity | Test Steward | simplified Windows server evidence green | passing/failing/cancelled results visible without running the full suite per RID |
| cutover:github-actions | remove automatic GitHub Actions workflow | cutover | Reconciliation Lead, Docs | closed by owner decision | remote `ci` workflow `343961597` is `disabled_manually`, YAML removed; automatic CI gap documented pending TeamCity activation |

States distinguish `pending`, `in progress`, `blocked`, `activation gate`, `unclassified`, and eventually `closed`.
