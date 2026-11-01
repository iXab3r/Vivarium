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
