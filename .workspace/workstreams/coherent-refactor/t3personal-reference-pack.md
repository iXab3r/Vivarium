# T3Personal reference pack: one bounded same-version cohort

**Subsequent independent evidence:** [server-input receipt #85](t3personal-server-inputs.md) now
confirms source `7ff65cada…`, separate settings `7495b685…` and the selected producer Build IDs.
The findings and missing-field handoffs below retain their original acquisition-time meaning;
use the new receipt for current availability and remaining gaps.

Status: **reference metadata available; runtime/model proofs open**. [Work card #84](https://github.com/iXab3r/Vivarium/issues/84)
is a P0 leaf under #13. This pack is not a frozen recipe/wire contract, conformance case, product
GREEN or milestone acceptance. The replacement product and all breadth levels remain NOT_AVAILABLE.

## Actual results to open now

Typed CLI 0.5.2 read-only selection verified canonical `https://build.eyeauras.net/`, TeamCity 2026.1.3 /
build 222742 / main_node, server clock 20261101T125428+0000. One query returned 18 finished SUCCESS
builds with no branch filter, hasMore=true / nextStart 18; no next page was read. The newest complete
three-configuration version group **in that bounded sample** was 0.1.6-nightly.20261101.16, not a claim
of globally latest work/history. All three reported queued 20261101T112327+0000 and finished/SUCCESS.

| Configuration | Build to open | Observed Agent / start → finish UTC |
|---|---|---|
| Prepare | [32724](https://build.eyeauras.net/buildConfiguration/T3Personal_Prepare/32724) | Cvat /10 /11:24:36 →11:29:37 |
| Windows | [32725](https://build.eyeauras.net/buildConfiguration/T3Personal_Windows/32725) | laptop-g15 15 /11:33:10 →11:42:01 |
| Release | [32726](https://build.eyeauras.net/buildConfiguration/T3Personal_Release/32726) | Cvat /10 /11:48:26 →11:49:17 |

These are reference observations, not assigned pilot fixtures. Matching version/queue time does not
prove the server's selected DAG. Use separate owned Linux/interactive Windows fixtures for PILOT.

## Three downloaded small manifests

Only these three original JSON artifacts were downloaded, with 65,536-byte per-file caps; total 2,147
artifact bytes. Local SHA256 recomputation matched the CLI summaries. No package, installer, YAML,
AppImage, screenshot, raw log, secret parameter or partial large binary was downloaded/inspected.

| Requested Build/artifact | Actual bytes | SHA256 of downloaded manifest bytes |
|---|---:|---|
| 32724 / prepare/manifest.json | 366 | `f826dbe1d2edd31eeca5512a4f0bac5fae662919f3bf2b0afb87dac59062729d` |
| 32725 / windows/manifest.json | 722 | `480b76714e4dc34584f7a35fb1e10287d30cd1934a16b47226ba09bc63ce8d9e` |
| 32726 / release/manifest.json | 1059 | `fda956c4653af7b015be48ff482d9caf57271a3b39aecd220cdd873a227665f3` |

All roots contain repository/revision/version/files. They **claim** repository iXab3r/t3code,
revision `7ff65cada80dd817aa5d7467b85e78ec30c578a9` and version `0.1.6-nightly.20261101.16`.
This is producer identity, not independent TC-server-reported resolved source/settings metadata.

| Manifest | Declared file inventory (not downloaded binary verification) |
|---|---|
| prepare (2) | `js-bundle.tar.gz`, `t3-0.1.6-nightly.20261101.16-linux-x64.tar.gz` |
| windows (5) | `T3-Code-0.1.6-nightly.20261101.16-x64.exe`, `T3-Code-0.1.6-nightly.20261101.16-x64.exe.blockmap`, `nightly.yml`, `t3-0.1.6-nightly.20261101.16-linux-x64.tar.gz`, `t3-0.1.6-nightly.20261101.16-win32-x64.zip` |
| release (8) | `T3-Code-0.1.6-nightly.20261101.16-amd64.deb`, `T3-Code-0.1.6-nightly.20261101.16-x64.exe`, `T3-Code-0.1.6-nightly.20261101.16-x64.exe.blockmap`, `T3-Code-0.1.6-nightly.20261101.16-x86_64.AppImage`, `nightly-linux.yml`, `nightly.yml`, `t3-0.1.6-nightly.20261101.16-linux-x64.tar.gz`, `t3-0.1.6-nightly.20261101.16-win32-x64.zip` |

All five Windows names/digests occur in Release unchanged. The runtime archive's declared digest
agrees in all three: `881829e53dc72b1282e52c9910fbf0f360b6d0bcc931379c959d8e539aa5c13e`. Shared JS bundle is not a Release asset. These comparisons
check declared strings, not file bytes/sizes. The additional Release Linux assets are observed there;
Prepare linux/manifest.json was not retrieved. SHA256SUMS is not in Release's manifest map.

## Public release and source metadata, independently attributed

A subsequent read-only GitHub metadata query found the public
[release tag v0.1.6-nightly.20261101.16](https://github.com/iXab3r/t3code/releases/tag/v0.1.6-nightly.20261101.16):
draft=false, prerelease=true, publishedAt 2026-11-01T11:49:10Z, 10 assets. Its Git tag resolves directly
to commit 7ff65cada80dd817aa5d7467b85e78ec30c578a9. All eight manifest-listed assets exist plus
manifest.json and SHA256SUMS. All eight GitHub provider-declared digest strings match the producer's
manifest strings; manifest.json has provider size 1,059 and digest equal to the actual downloaded
Release JSON hash above. No GitHub asset was downloaded in this metadata comparison.

Public release existence/tag/metadata is an actual openable reference result. Provider-reported
binary sizes/digests are not local binary-byte verification. This does not prove which TC Build
published the release, which mode ran, or the causal publisher path. No write/publication occurred.

At the **manifest-claimed** revision 7ff65cada, public source blobs are:

| Path | Git blob / comparison with 041345f2 snapshot |
|---|---|
| [.teamcity/settings.kts](https://github.com/iXab3r/t3code/blob/7ff65cada80dd817aa5d7467b85e78ec30c578a9/.teamcity/settings.kts) | `2eccef048973d5151c93aba0df0b96c913bfd20d`; differs from 041345f2's db0a1ed24a0b51c6a9bf726590c2c898ca2a776b |
| prepare.sh | `5b6bd0d6fcc05bd989d01c3b75e013f6b9a48d46`; identical |
| windows.ps1 | `0b2b626a4ca18cd7c7f67ead91f17d2b7c179e90`; identical |
| run.mjs | `bbf13d393be1cbf0041afd4e232caef01132ee03`; identical |
| manifest.mjs | `e04d915b540f47f8c246e8b1736ec5c1fd55d6ed`; identical |
| publish.sh | `27364356fef8179abac908680c79c8afa0a94f7c`; identical |

Script paths are under scripts/personal-release. The 7ff65 Kotlin retains default release mode,
actual DAG/runner/artifact rules and default-branch 60s VCS quiet period, but **no metadata-change
trigger exclusions**. Later 041345f2 adds those exclusions. Do not attribute later rules or a server
settings revision to this cohort without server evidence. Source blobs explain intended producer
behavior; they do not prove unmodified execution or complete/default-publication effects. This
is an evidence limit, not a new remote-attestation or whole-working-tree immutability requirement
beyond TeamCity semantics. #85 records actual available/absent fields and named handoffs rather
than inventing values or waiting indefinitely for an unavailable field.

## Immediate next proof and named handoff

| Gap / owner | Small concrete output | Current blocker |
|---|---|---|
| Independent server selection, [#85](https://github.com/iXab3r/Vivarium/issues/85) under #35, with #13/#33/#36/#82 | One safe typed receipt rooted at Release 32726 and its actual selected dependencies, comparing cohort candidates 32725/32724: server-reported VCS/settings fields and selected snapshot/artifact Build IDs/inputs. | Current typed-CLI projection lacks this independent receipt; #85 defines existing capability checks or a future bounded unpublished getter using existing profile/gates/auth/limits. No installed/conformance-client patch, generic harness, arbitrary raw HTTP or invented absent fields. |
| Exact wire/control cohort, #33/#35 | Literal due method/path/projection/status/header/entity/current-permission/readback/effect expectations with independent sources and cheapest controlled fixtures. | CLI entity summaries are not original wire or negative/cancel/native-consistency proof. No new conformance case is materialized by this pack. |
| Shared model, #32; C#/Git proof, #34 | Worked returned/related/reused Build identities and source/settings/selection trace; actual before/after source files, editor/evaluator invocation, expected effective values, revisions and failure/recovery outputs. | Neither the model nor source representation is proved by these manifests. |
| Due case freeze/accounting, #36/#42 | Join exact cases, principals, fixture/oracle/target provenance and freeze gate; retain #77 and all remaining P0 baseline/composition/allocation/debt/review/main obligations. | Pack availability does not finish P0 or accept a recipe/API denominator. |
| Binary/platform/publication acceptance, #78/#82/#83 + owning contributors | Minimal explicit byte/platform/default-publication proof and valid reuse/limits, separate from provider/producer declarations. | No local binary verification, launch execution or causal publication proof here; real effects need their later concrete authorized execution boundary. |

Original small manifests and safe receipts remain ignored under artifacts/t3personal-next-reference;
subsequent GitHub metadata/comparisons are in artifacts/t3personal-next-planning/public-release-receipt.json.
No existing 449 API headings, 15 cases, 110 canonical AC, raw results or generated status changed.
