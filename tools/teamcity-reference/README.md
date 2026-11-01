# Bounded TeamCity reference acquisition

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

The lab account has server `SYSTEM_ADMIN` at global scope; the local CLI gates remain ReadOnly on,
Update/Admin/Credentials off. Dedicated mutation/readback/denial fixtures and least-privileged actors
are still pending; no fixture mutation has run. Process-local APPDATA/LOCALAPPDATA stay under the
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
