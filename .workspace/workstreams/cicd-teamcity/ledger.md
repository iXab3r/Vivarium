# TeamCity CI/CD migration ledger

| Stable ID | Target | Phase | Owner/review | State | Required evidence |
|---|---|---|---|---|---|
| gha-job:test | Cake `CI` plus Windows/Linux/macOS TeamCity jobs | build-driver / TeamCity | Test Steward, Platform | implemented; server evidence pending | root build/test and native TeamCity runs on all three OS families |
| gha-job:payload-smoke | Cake `PayloadSmoke` per native host RID | build-driver / TeamCity | Test Steward, Platform | implemented; server evidence pending | TRX passes on win-x64, linux-x64, osx-arm64 |
| gha-job:payload-cross-macos-publish | Cake cross-publish output from Linux | TeamCity | Test Steward, Platform | pending | exact artifact dependency into macOS job |
| gha-job:payload-cross-macos-run | macOS execution of Linux-produced payload | TeamCity | Test Steward, Platform | pending | native macOS run passes |
| gha-job:payload-nextest | pinned nextest archive smoke | build-driver / TeamCity | Test Steward, Security | pending | archive/remap smoke with verified tool bytes |
| local-build:rid-matrix | Cake `Publish` / `PublishAll` runnable trees | build-driver | Platform, Test Steward | local evidence complete; target-native evidence pending | all four binary formats; host-native `viv-server`/`viv-cli`/`viv-agent`/`viv-agent-update` execution; native target execution in TeamCity |
| release-rid:win-x64 | package plus native release smoke | release | Agent API/SDK, Platform | pending | extracted final ZIP runs on Windows x64 |
| release-rid:linux-x64 | package plus native release smoke | release | Agent API/SDK, Platform | pending | extracted final ZIP runs on Linux x64 |
| release-rid:linux-arm64 | package plus native release smoke | release | Agent API/SDK, Platform | blocked | real Linux arm64 agent/evidence; cross-publish alone is insufficient |
| release-rid:osx-arm64 | package plus native release smoke | release | Agent API/SDK, Platform | local evidence; server evidence pending | extracted final ZIP runs on macOS arm64 |
| release-contract:layout | exact controller/agent/CLI trees and nested package identity | release | Agent API/SDK, Platform, Security | local verification complete; integration review pending | D19 naming-only bootstrap change, exact layout verification, native smoke, and two-run SHA-256 equality |
| release-contract:d29-git | bundle or verify system Git on every controller RID | release | Platform, Security, Docs | activation gate | accepted D29 commit and per-RID evidence before stable release |
| release-contract:github | draft-first immutable/idempotent publication | publication | Security | implemented paused; activation evidence pending | negative API tests, draft resume, remote digest verification |
| teamcity:trusted-source | default-branch DSL and trusted PR policy | TeamCity | Security, Git/Versioning | implemented; server evidence pending | fork PR cannot alter settings or receive credentials |
| teamcity:test-reporting | import deterministic TRX output | TeamCity | Test Steward | implemented; server evidence pending | passing/failing/cancelled results visible in TeamCity |
| cutover:github-actions | remove automatic GitHub Actions workflow | cutover | Reconciliation Lead, Docs | closed by owner decision | remote `ci` workflow `343961597` is `disabled_manually`, YAML removed; automatic CI gap documented pending TeamCity activation |

States distinguish `pending`, `in progress`, `blocked`, `activation gate`, `unclassified`, and eventually `closed`.
