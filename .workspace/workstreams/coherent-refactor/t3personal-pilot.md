# First real product: T3Personal Prepare

**Now:** NOT_AVAILABLE in the replacement product. The owner selected real T3Personal and three
stories: UI → direct compatible REST → unchanged CLI. **Prepare-first is the recommended draft
boundary; the optional Prepare/full-chain choice is unanswered, not assumed approved.**
**Next:** #13/#35 freeze the Prepare recipe/expected outputs and due reference cases, #36 binds
them, then close accepted P0 and R0. **Later:** Windows → explicit full-chain build-only validation
→ separately scoped default publication, plus the remaining full subsystem lifecycles.

## What to open, and when

Today only the [reference project](https://build.eyeauras.net/project/T3Personal?mode=builds) and
planning/evidence below are available. No working replacement login/build URL or start command is
invented here. [PILOT #78](https://github.com/iXab3r/Vivarium/issues/78) is the integration/closure
owner, with [contract #82](https://github.com/iXab3r/Vivarium/issues/82) and
[acceptance #83](https://github.com/iXab3r/Vivarium/issues/83) in the same milestone.

| Openable checkpoint | User action and observable result | Task / now |
|---|---|---|
| Local prerequisite | Start real Host/PG/Storage/Agent, sign in, see identity/heartbeat/readiness and use owned stop/reset/attach. | #37, NOT_AVAILABLE; R0 branch preview |
| Cheap client prerequisite | Run/watch/cancel LocalSmoke with unchanged CLI and verify retained output/bytes/recovery. This is not T3 success. | #38, NOT_AVAILABLE; R0 branch preview |
| UI: sign in and see Prepare | Open real Project/BuildType and eligible separately owned Linux Agent, or a truthful named incompatibility. | [#79](https://github.com/iXab3r/Vivarium/issues/79), NOT_AVAILABLE |
| UI: watch real work | Submit through normal UI/native services; actual source checkout and Docker Prepare progress through queued/running/finished with resolved revision/settings/counter/Agent. | #79, NOT_AVAILABLE |
| UI: inspect and download | Actual SUCCESS, source-emitted version/build number, AppImage, runtime/shared bundle, manifests, retained logs and launch diagnostics; download/hash bytes. | #79, NOT_AVAILABLE |
| Direct REST | A real /app/rest-entered Prepare execution, normal auth/current grants, intermediate status, logs/bytes and UI/native cross-readbacks. | [#80](https://github.com/iXab3r/Vivarium/issues/80), NOT_AVAILABLE |
| Unchanged CLI | A real pinned teamcity-cli-entered Prepare execution; exact commands/exits, current status and hash-verified download, cross-readable through UI/REST. | [#81](https://github.com/iXab3r/Vivarium/issues/81), NOT_AVAILABLE |

The future CLI contract uses supported options and a dedicated Vivarium profile, never the default
production profile. No explicit revision flag is currently supported; a branch/main invocation must
record its actual resolved source/settings and reconcile recipe changes against this source snapshot.
Select an adequate supported wait budget for Prepare's 90-minute limit. A local wait timeout (exit124)
leaves work running and is not cancellation. Deliver verified commands/download locations when ready;
artifact --output is a name inside the profile download directory, not an arbitrary destination path.

Each surface has actual execution/evidence; one submission viewed three ways does not prove three
entry stories. Implement shared services/API behavior when first needed, not artificial delays for
a later story. Use cheap controlled workloads for nonzero failure, queued/running cancellation,
denial/revoke and restart/ownership variants; do not rebuild T3 for thousands of protocol assertions.
Instantiate the existing canonical AC/TCM at those boundaries; no new business-AC count/framework.

## Targeted source/reference addendum — not a refreshed global census

The historical inventory contains 97 configurations and no T3Personal. Read-only reference metadata
on 2026-11-01 (CLI 0.5.2, verified canonical build.eyeauras.net, TeamCity 2026.1.3/build 222742)
adds these three observed IDs. This is a targeted addendum, not a claim of 100 current configurations
or migration acceptance. No existing fleet Agent was assigned or modified.

| Installed ID | Current pinned source recipe / dependency |
|---|---|
| T3Personal_Prepare | Linux Docker simpleRunner; source checkout +:. and -:.repos; positive build counter; shared bundle, Linux runtime/CLI/AppImage and launch checks; requirement names Cvat in reference. |
| T3Personal_Windows | PowerShell file runner; Prepare snapshot dependency and prepare/** artifact input; Windows packaging and real interactive desktop/CLI launch checks; reference requirement laptop-g15. |
| T3Personal_Release | Linux Docker asset staging/validation; snapshot Windows, artifacts from Windows and Prepare; combined manifest/SHA256SUMS, then publication by default. |

All use source root T3Personal_Git. Current source is pinned at
`041345f2c201d68e718d5dbf0ba5ce9755f1704b`:
[settings.kts](https://github.com/iXab3r/t3code/blob/041345f2c201d68e718d5dbf0ba5ce9755f1704b/.teamcity/settings.kts),
[prepare.sh](https://github.com/iXab3r/t3code/blob/041345f2c201d68e718d5dbf0ba5ce9755f1704b/scripts/personal-release/prepare.sh),
[run.mjs](https://github.com/iXab3r/t3code/blob/041345f2c201d68e718d5dbf0ba5ce9755f1704b/scripts/personal-release/run.mjs),
[publish.sh](https://github.com/iXab3r/t3code/blob/041345f2c201d68e718d5dbf0ba5ce9755f1704b/scripts/personal-release/publish.sh).
Settings blob is `db0a1ed24a0b51c6a9bf726590c2c898ca2a776b`; source is intended behavior,
not a successful executed recipe. Prepare 32698 / Windows 32699 / Release 32700 were observed
successful with displayed version 0.1.6-nightly.20261101.15; only root artifact listings were read,
without hashes/bytes. Change f48df77f6f0e1347d5bf4185e2f6bf2d11414b22 is change metadata, not
resolved source/settings provenance. Current source's Linux artifact folders differ from the older
Prepare root listing. #13/#35 must align exact effective source/settings/recipe before GREEN, or
deliberately freeze the new source and prove its actual outputs. Do not combine these evidence classes.

Prepare has one actual reference runner step, RUNNER_1; internal script stages are observed output/
progress, not invented per-command StepRuns. Prepare uses a pinned Node Docker digest, Node 26.12.1, Rust 1.96.1, Vite+ 1.4.0 and pnpm 12.14.0,
a 90-minute timeout, owned caches and actual dbus/xvfb Electron plus CLI checks. Preserve its
`buildNumber` and `publishArtifacts` effects: diagnostics/prepare/Linux/launch outputs and manifests,
not stdout alone. Provide a separate owned Agent with explicit requirement mapping; do not commandeer
Cvat/laptop-g15 or silently remove requirements. Record exact permitted mapping in the pilot recipe.

Prepare's GITHUB_TOKEN preflight makes an authenticated repository GET and requires push permission;
it does not publish. Supply a private credential reference, never a token in public planning/logs.
Release source sets T3_PUBLISH=release and invokes publish.sh; off/draft/release are supported,
with no dry-run or repository-target override. Draft also writes externally. An eventual off-mode
chain is explicitly build-only validation, not default-publication parity or full T3 migration.
Publication and trigger ownership during coexistence need their later deliberate effect scope.

## One bounded integration, existing owners

Fixed near-term order is accepted P0 → accepted R0 → PILOT. Next product stories are Windows →
chain/build-only → explicit publication → other families → history/diff-import. Existing R1–R6 are
full-scope owner/completion backlogs and cumulative gates, not a wall before the next product story.
Allocate each later story's exact milestone/partial contributions before implementation and after
predecessor closure; no new later milestone or debt waiver is invented now.
P0 shared structural/representation, honest reference/RED, allocation and composition gates remain;
known independent later variants stay later only after their early-path impact is assessed. Full IAM,
general catalog editor, all runners and historical import do not become prerequisites to PILOT.

SS-RUN@PILOT uses #78/#82/#83; primary full owner SS-RUN/#20 remains R2. Early contributions
reuse #16/#17 current credentials/grants, #18 approved recipe activation, #19 exact checkout,
#56 Agent compatibility, #22 Docker/step execution, #23 counter, #24 logs/service messages and #26
artifact bytes. #14 supplies the accepted R0 Host. Later owners retain their full scope and
regressions; no second engine, node, queue or authority is introduced. All variants stay UNFROZEN
until actual contract/case evidence exists; global 449 headings and fifteen known cases are unchanged.

Cards may expose usable branch checkpoints as soon as ready. They close after PILOT's frozen due
scope/cumulative evidence, independent aggregate correctness/simplicity and Security/Adversarial
review, zero unresolved milestone debt and verified main delivery. No per-card formal review/full
suite tax or debt waiver. Later full-chain/publication/family/history work remains explicitly due.
