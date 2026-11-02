# T3Personal server-resolved inputs — reference #85

Reference acquisition on 2026-11-01, based on `01aa708dc99623b0a7cfccc7b6354ff501588221`.
Lead: TeamCity; co-held Story Delivery, REST, Security and Test Steward perspectives. The owner's
continuation from the named #85 next step executes this bounded read-only getter; the earlier
planning-only getter statements are historical. No Vivarium product/harness implementation occurs.

## Actual result

The [openable receipt](t3personal-server-inputs.json) retains five successful HTTP exchanges and
3,590 original response-entity bytes: the pinned server, Release 32726, Windows 32725, Prepare 32724
and the project's current effective Versioned Settings configuration. Each exchange has the exact
GET path/fields, status, Content-Type, byte count, SHA256 and base64 entity without JSON reserialization.
The save timestamp is not a claimed server response timestamp. All three builds remain finished/SUCCESS.

| Server Build field | Release 32726 | Windows 32725 | Prepare 32724 |
|---|---|---|---|
| `revisions.revision[0].version` | `7ff65cada80dd817aa5d7467b85e78ec30c578a9` | same | same |
| `versionedSettingsRevision.version` | `7495b6859c91952ad91478a2428be805a4e48f1a` | same | same |
| `snapshot-dependencies.build[].id` | 32725 | 32724 | empty |
| `artifact-dependencies.build[].id` | 32725, 32724 | 32724 | empty |

Every source revision reports branch `refs/heads/main`, root instance `67`, root `T3Personal_Git`,
and checkout rules `+:.` followed by `-:.repos`. Every settings revision reports the same branch/root,
instance `66` and empty checkout rules. Internal revision strings equal their respective version
strings in this sample; root-instance IDs are not Build IDs or independent repositories.

The three producer manifest revisions in [pack #84](t3personal-reference-pack.md) now agree with an
independent **server-reported source revision**. They do not identify the settings revision. Source
and settings must remain separate identities even when their relevant files happen to match.

The public [.teamcity/settings.kts at the server settings revision](https://github.com/iXab3r/t3code/blob/7495b6859c91952ad91478a2428be805a4e48f1a/.teamcity/settings.kts)
has exactly the same LF bytes as that file at source revision `7ff65cada…`: 5,046 bytes, Git blob
`2eccef048973d5151c93aba0df0b96c913bfd20d`, SHA256
`6d7ab6a1dbbdf2a465e4c26f2adcab305f9160b4a1c8ec0286037f5cb0921b7f`.
That file declares default publication, Windows→Prepare and Release→Windows snapshot dependencies,
the three artifact rules, default-branch VCS triggering with a 60-second quiet period and no later
metadata-change exclusions. It is attributable settings source, not a complete effective-definition
or execution attestation. The stored scripts at the source revision remain separately attributed.

## Exact boundaries and gaps

- Release returns only Windows in `snapshot-dependencies`, whereas the installed schema and
  [2026.1 Build reference](https://www.jetbrains.com/help/teamcity/rest/2026.1/build.html#snapshot-dependencies)
  describe an upstream closure. Preserve the literal response; do not insert Prepare or infer why
  this projection differs. Windows→Prepare is separately observed. #111/#32 own the semantic delta.
- `artifact-dependencies` supplies selected producer Build IDs. It does not supply individual
  downloaded file names, consumed-byte hashes or a reason for `sameChainOrLastFinished` selection.
  The settings source declares rules; the old small manifests supply producer bytes. Neither is
  independent proof that each declared file was consumed unchanged. That remains #111/#140/#152
  input/byte evidence, with #112/PILOT acceptance. No logs or packages were fetched here.
- The **current** effective config returns `synchronizationMode=enabled`,
  `buildSettingsMode=alwaysUseCurrent`, `allowUIEditing=false`,
  `applyChangesInDependenciesAndVcsSettings=true`, `vcsRootId=T3Personal_Git`, `format=kotlin`.
  `settingsPath` is absent, not a guessed default. This is not the configuration mode at execution.
- `settingsHash` and `currentSettingsHash` differ for Release and Windows, and match for Prepare.
  Their literal values are retained; no hash-to-revision mapping, cause, capture instant or replay
  identity is inferred. Build source and settings versions are independently returned fields, as
  distinguished by the installed schema and [Revision reference](https://www.jetbrains.com/help/teamcity/rest/2026.1/revision.html).

## Getter and verification

The installed CLI 0.5.2 has no required Build input projection. The four-file unpublished
[getter increment](../../../tools/teamcity-reference/cli-t3-inputs-capture.patch) applies after the
existing metadata-capture patch to pinned CliFactory `b158fe03…`; installed packages and external
checkouts are unchanged. Use the existing source build's normal entry point:

```powershell
node artifacts/p0-rest-first/reference/cli-acquisition/dist/src/bin.js -p default --json server t3-inputs-capture
```

This fixed command requires ReadOnly and the exact existing default profile/HTTPS origin. It uses
existing credential lookup and transport, verifies server 2026.1.3/build 222742 before Build reads,
refuses redirects and arbitrary inputs, and has a 30-second total deadline. Response caps are
1 KiB server, 16 KiB per Build, 4 KiB config; collections cap at eight with count consistency.
No pagination or followed dependency URLs. Unknown fields, credentials (including JSON escapes),
oversize, invalid projections or version/identity mismatches fail closed. Non-200 error bodies
are omitted and stop the fixed sequence with `complete=false`. Only Content-Type is retained.

TypeScript build and all 664 existing-plus-focused CLI tests pass, zero skipped. Eight new focused
tests cover target/gate/version, exact requests/raw bytes, redirects/errors, limits, missing fields,
different selected IDs and escaped credentials. Clean patch application/source identity is checked.
The receipt binds the final LF patch hash; original artifacts/logs remain under ignored
`artifacts/t3-inputs-reference/`. Windows x64 / pinned .NET SDK 10.0.303 root checks pass:
`dotnet build -m:1` (zero errors; existing unmodified dependency NU1902/NU1903 warnings) and
`dotnet test -m:1` (421 passed / 11 skipped / zero failed, 5m25s). The skips remain visible in the
ordinary root log; this is not a Tier-2 opt-in or cross-platform/fleet claim. The separate raw
conformance target is not rerun; its immutable prior evidence is reused only through explicit
unchanged-input accounting. Local-link/TOML/JSON/TSV/diff checks and #85 body readback pass; original
body, comments, state and native parent are retained. Only its current body hash and bounded
readiness dependency row change in the existing registers. All API/case/result/owner and raw
receipt registers remain unchanged.

No new conformance case, expected product value, API-complete denominator or GREEN is created.
The fifteen prior cases remain five RED / ten product-prerequisite-blocked / zero GREEN. #85's
bounded receipt is available while its issue remains Open until P0 delivery. Next consume it in
#32's worked identity/selection model, #34's C#/Git representation proof and #111's exact wire/control
cohort; preserve all remaining P0 baseline/composition/review/debt/main gates before R0/PILOT.
