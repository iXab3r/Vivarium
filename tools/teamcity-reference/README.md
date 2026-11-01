# Bounded TeamCity reference acquisition

## Fixed T3Personal input receipt

[`cli-t3-inputs-capture.patch`](cli-t3-inputs-capture.patch) is a four-file unpublished increment after
`cli-reference-capture.patch` on the same pinned source. `server t3-inputs-capture` has no arguments:
it requires ReadOnly, the exact `default` profile at `https://build.eyeauras.net`, and observed
2026.1.3/build 222742. Five GETs read the server, Builds 32726/32725/32724 and current effective
T3Personal Versioned Settings. It uses existing auth/transport, a 30-second total deadline, bounded
strict projections and at most eight collection items; no redirects, paging, arbitrary paths,
parameters, logs or package downloads. Non-200 bodies are omitted and stop the sequence explicitly.
Source/settings revisions and selected producer IDs are retained literally, separately from
current configuration, source declarations and per-file byte proof.

[Receipt, findings and remaining gaps](../../.workspace/workstreams/coherent-refactor/t3personal-server-inputs.md)
bind the final patch hash and original entities. TypeScript build and all 664 tests pass (eight new,
zero skipped); clean patch application and source identity are verified. This installs/publishes
nothing and changes no TeamCity state or Vivarium production/conformance code.

## Existing metadata acquisition

The installed `@eyeauras/teamcity-cli@0.5.2` has no original-entity/status/schema export.
[`cli-reference-capture.patch`](cli-reference-capture.patch) is a tested, unpublished source increment
against [CliFactory b158fe03eab3c31982a21b36c278f690f6b541d9](https://github.com/iXab3r/EyeAuras.CliFactory/tree/b158fe03eab3c31982a21b36c278f690f6b541d9).
Apply it at that repository's root with `git apply --check` and `git apply`, then use its ordinary
dependency installation, build and test workflow. It changes two existing files and adds one small
capture module plus its tests; no installed package or external checkout was changed here.

The fixed `server reference-capture <operation>` command accepts only `api-version`, `rest-version`,
`server-version`, `schema`, and `root-project`. It uses the existing profile, ReadOnly permission gate,
credential lookup and authenticated transport. It accepts no arbitrary path, method, body, locator or
header. It refuses redirects, bounds entity reads, omits error payloads and every response header
except Content-Type, and rejects credentials in exported payload/media headers. Schema bytes are
limited to 8 MiB; root projection to 1 KiB; version text to 512 bytes. Base64 preserves original
response entity bytes after fetch content decoding, without JSON/XML reserialization.

After building the patched CLI, invoke its normal entry point, for example:

```powershell
teamcity-cli --json server reference-capture api-version
teamcity-cli --json server reference-capture root-project
teamcity-cli --json server reference-capture schema
```

Those commands require a configured read-only profile. Keep credentials in the existing CLI store.
Retain full schema only in ignored local artifacts: host/base-path metadata is not safe to commit
without inspection. These acquisition commands never authorize TeamCity writes. Mutation/error/state
observations still require explicitly authorized disposable fixtures and known permission actors.

## Current mutation-fixture target

Owner direction, 2026-11-01: mutation/reference-effect tests must use a **separate TeamCity test
server** matching the pinned 2026.1.3 profile; the owner confirmed a local instance. A temporary
project on the owner's working TeamCity server does not satisfy this requirement. Select the
dedicated test CLI profile explicitly and verify its configured URL and observed server version/build
against the isolated instance before
state-changing commands. Never rely on the production/default profile or mutate the owner's server.
Record the selected profile, verified target and fixture actors with each new acquisition receipt.

The separate Docker container `vivarium-p0-rest-teamcity-2026-1-3` is running at
`http://127.0.0.1:18111` (loopback port 18111 to container 8111). Normal setup UI completed the new
data directory, internal HSQL evaluation database, license and private administrator/token bootstrap.
The explicit profile `vivarium-teamcity-lab-202613` is **authenticated**: CLI reads confirmed
2026.1.3 / build 222742, API 2026.1, REST version 222742 and canonical `webUrl` exactly
`http://127.0.0.1:18111`. The advertised URL was corrected through normal Administration → Global
Settings UI because supported typed CLI global-setting keys exclude a URL setting.

The lab account has server `SYSTEM_ADMIN` at global scope. The owner subsequently authorized
synthetic projects/configurations/users/roles and dedicated new test Agents on this separate local
sandbox; its isolated CLI write gates were enabled for the bounded actual mutation acquisition.
The [local mutation record](../../docs/design/teamcity-rest-local-mutations.md) describes twelve
observed workflows and the separately restricted PAT's raw permission provenance.
After acquisition, the owned project/configuration and restricted PAT/local alias were removed;
typed project/configuration reads confirmed absence. Current local gates are ReadOnly on and
Update/Admin/Credentials off. The lab container and original administrator credential remain.
Process-local APPDATA/LOCALAPPDATA stay under the
ignored lab's `cli-state`; the production profile/default was not read. CLI Agent inventory confirmed
zero Agents. Existing fleet Agent connections/execution still need separate explicit authorization.

Data/log/temp bind mounts and local evidence remain entirely under ignored
`artifacts/p0-rest-first/teamcity-lab/` (`container-receipt.json`, `setup-receipt.json`, `README.md`).
For only this named lab container, use:

```powershell
docker stop vivarium-p0-rest-teamcity-2026-1-3
docker start vivarium-p0-rest-teamcity-2026-1-3
```

After starting it, re-verify the expected target and authenticated profile before any fixture writes.
Historical 2026-11-01 read-only captures below remain unchanged evidence from their original reference
target; they are not test-server readiness or mutation evidence.

The actual local acquisition used a scratch build of the pinned source under
`artifacts/p0-rest-first/reference/cli-acquisition`, through `node dist/src/bin.js --json server
reference-capture <operation>`. Exact scratch dependencies were `@eyeauras/cli-factory@0.5.2`,
`typescript@5.9.3`, `@types/node@24.10.0`, and `msw@2.11.0`; its standalone tsconfig used the source's
strict NodeNext settings with ES2023/DOM libraries. `npm run build`, the five focused capture tests,
and all 656 pinned integration tests passed (zero skipped, approximately 10.9 seconds). The patch
passed clean application against pristine pinned files and the resulting LF-normalized sources
matched the tested inputs. This is local source-increment evidence, not publication or installation.

Patch SHA-256 in its tested LF representation:
`0ac38fa6a925472874870b8cc55844861438bcf3aec025279c9a03a22883b6c4`.
Normalize a checkout-converted patch back to LF before verifying that byte hash or applying it.
The [first-cohort record](../../docs/design/teamcity-rest-first-cohort.md) binds safe captures, profile,
product targets, standard results and remaining gates. Installed Swagger acquisition is documented by
[JetBrains](https://www.jetbrains.com/help/teamcity/rest/teamcity-rest-api-documentation.html);
the actual installed metadata supplies the pinned contract, rather than rolling help examples.

## Fixed local mutation capture increment

[`cli-lab-fixture-capture.patch`](cli-lab-fixture-capture.patch) applies **after** the original capture
patch against the same pinned CliFactory revision. It changes four files: one fixed command, a
170-line lab-only acquisition module, six guard/projection tests, and an optional existing typed token
metadata projection. It accepts no arbitrary mutation request. The command requires the exact lab
profile/URL, process-isolated lab APPDATA/LOCALAPPDATA, local ReadOnly/Update/Credentials gates,
observed server/API/REST version and global synthetic administrator; it refuses an existing owned
project before writing. All writes use existing typed client methods. Response entities are bounded
to 32 KiB; capture headers exclude credentials; issued PAT values remain in the existing secret store.

Invoke the patched source's normal entry point from this repository root only after preparing the
authorized isolated lab and its explicit profile:

```powershell
teamcity-cli -p vivarium-teamcity-lab-202613 --json server fixture-capture
```

This command creates only its fixed synthetic project/configuration and restricted view PAT. Its
captured role/permission reads, positive reads, rejected updates and actual readbacks are reference
evidence; they do not claim Vivarium compatibility. The capture keeps per-exchange actor labels,
omits absent GET bodies, rejects redirects and preserves original entities. Repeated-query/raw-order
variants are outside this fixed request set. Archive prior captures before a deliberate fresh run;
remove only the named owned fixture/PAT through typed commands between acquisitions.

Clean patch application and tested-source identity were verified locally. All 662 pinned CLI tests
passed, zero skipped (63.144 seconds); six focused acquisition controls took approximately 256ms.
The final actual acquisition took 663ms for twelve workflows / 28 exchanges, 9,692 entity bytes,
plus one raw permission-provenance exchange. Earlier incomplete guard/GET-metadata attempts remain
ignored diagnostic artifacts and are not current reference fixtures.

LF patch SHA-256: `9f855e9b2baf6d6eac71b4173463dfda5f48ec52c2d2d135de6364ea65098d84`.
The [local mutation record](../../docs/design/teamcity-rest-local-mutations.md) and its separate receipt
bind the safe fixture and product/replay claims. No external checkout or installed CLI was modified.
