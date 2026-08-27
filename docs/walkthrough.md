# End-to-end walkthrough: cross-OS integration tests

The normative UX document. Scenario: you have a program (`myapp`, any language) that must work on
Windows, Linux, and macOS, and an NUnit integration-test project that can verify it. You want to set
the check up once and run it on demand — locally or from CI.

Written against Phase 1 capabilities (persistent machines, no hypervisors); §7 shows how the same
setup grows into pristine snapshots at Phase 2. Decision references (D…) point into
[`ARCHITECTURE.md`](ARCHITECTURE.md).

## 0. Install the controller — one machine, once

Any always-on box you own (your dev machine is fine to start):

```
vivarium-controller.exe        # self-contained; creates ./vivarium-data (SQLite + blobs)
```

First run prints the panel URL and an admin token:

```
Vivarium controller listening on https://192.168.1.10:8443
Panel: https://192.168.1.10:8443  (admin token: k3v9…)
TLS:   self-signed, fingerprint SHA256:9F3A…  (embedded in enroll commands automatically)
```

On your workstation, point the CLI at it once — the fingerprint is confirmed on first contact and
pinned from then on:

```
viv login https://192.168.1.10:8443
```

## 1. Connect three machines — five minutes each

You need one Windows, one Linux, one macOS machine. Anything counts: a spare laptop, a VM you already
have, a Mac mini in a drawer. Panel → **Agents → Add machine** offers two equivalent routes. The lazy
one, TeamCity-style: download the **preconfigured agent zip** right on the machine — it already
contains `bootstrap.json` pointing at this controller — unzip, run, done. Or, for a shell you are
already in, the one-liner:

```
# Windows (elevated; curl.exe ships with Windows)
curl.exe -k https://192.168.1.10:8443/setup.ps1 -o setup.ps1; powershell -ep bypass .\setup.ps1 -Fp SHA256:9F3A... -Token <enroll-token>

# Linux / macOS
curl -fsSLk https://192.168.1.10:8443/setup.sh | sh -s -- --fp SHA256:9F3A... --token <token>
```

The script re-checks the live certificate against the fingerprint argument before doing anything else;
the panel shows this exact command pre-filled — token, fingerprint and all (§8.4 explains the trust
handshake).

Each machine appears under **Agents** as *unauthorized* within seconds (D8). Click **Authorize**, give
it a name. The agent has already reported its parameters — you add tags only if you want them:

| Agent | Reported (excerpt) |
|---|---|
| `win10-box` | `os.family=windows os.build=19045 arch=x64 interactive=true` |
| `ubuntu-2204` | `os.family=linux os.version=22.04 arch=x64` |
| `macbook` | `os.family=macos os.version=14.5 arch=arm64` |

Two honest caveats. UI-test duty needs extras the setup script *offers* but never does silently:
autologon on Windows asks for credentials, and macOS TCC grants (Accessibility / Input Monitoring)
are clicks Apple reserves for a human (D10). And a headless box needs a display (dummy plug) before
UI results mean anything.

That aside, this is the last time you touch these machines by hand: the agent auto-upgrades centrally
from now on (D2), and everything else arrives through builds.

## 2. Describe the check — `vivarium.yaml` in your repo

Build configurations are code, versioned next to the thing they test (D17):

```yaml
# vivarium.yaml
project: myapp

configurations:
  integration:
    matrix:
      windows: { agent: "os.family == windows", rid: win-x64 }
      linux:   { agent: "os.family == linux",   rid: linux-x64 }
      macos:   { agent: "os.family == macos",   rid: osx-arm64 }
    payload: out/{rid}/**
    steps:
      - run: IntegrationTests{exe} --report-trx --results-directory {results}
    collect:
      - "{results}/**"
      - logs/**
    timeout: 30m
    clean: none          # Phase 1 fleet; `pristine` arrives with image-backed cells
    on_fail: keep        # optional: leave the machine/workdir alone for inspection
```

- **Matrix cells** are named (`windows`, `linux`, `macos`) — the names become matrix columns and
  rerun targets. A cell selects agents with a requirement expression over their parameters (D8, D14).
- **Template variables** specialize one definition per cell: `{rid}` (declared per cell — payload
  paths must resolve at upload time, before any agent has matched), `{os}`, `{arch}`, `{exe}` (`.exe`
  on Windows, empty elsewhere), `{results}`, `{workdir}`.
- **Payload** is files-in / process / files-out (D3): whatever `out/{rid}/` holds is packed into an
  archive (executable bits and symlinks preserved — this matters the moment a Linux agent unpacks your
  tests), content-addressed and deduplicated — unchanged content never uploads twice.
- Results come back as TRX (parsed by the controller's adapter); if the runner also emits TeamCity
  service messages, tests stream live while the build runs (D14 — lands after the Phase 1 core, which
  shows step status and delivers full results at build end).

## 3. Build the payloads

Tests are published **self-contained** — the target machines have no SDKs, no runtimes, and never will
(D3). The SUT rides along inside the same folder:

```
dotnet publish tests/IntegrationTests -c Release -r win-x64   --self-contained -o out/win-x64
dotnet publish tests/IntegrationTests -c Release -r linux-x64 --self-contained -o out/linux-x64
dotnet publish tests/IntegrationTests -c Release -r osx-arm64 --self-contained -o out/osx-arm64
# SUT in any language — e.g. Rust:
cargo build --release --target x86_64-pc-windows-msvc   # …then copy into out/win-x64/sut/, etc.
```

Test code locates the SUT relatively (`AppContext.BaseDirectory/sut/myapp{exe}`) and can read
`VIVARIUM_RESULTS_DIR`, `VIVARIUM_CELL`, `VIVARIUM_BUILD_ID` from the environment when it cares.

Pristine machines are unforgiving (D3's portability doctrine): Rust binaries want `crt-static` — there
is no VC++ redist out there; .NET wants `InvariantGlobalization` for minimal Linux; cross-published
macOS binaries must carry at least an ad-hoc signature; and TRX needs the
`Microsoft.Testing.Extensions.TrxReport` package.

## 4. Run

```
$ viv run integration
Uploading payload… 3 cells, 214 MB → 38 MB new (dedup)
Matrix build #12 queued → https://192.168.1.10:8443/builds/12

  windows  win10-box      ▶ running   step 1/1
  linux    ubuntu-2204    ▶ running
  macos    macbook        ⏳ queued (agent busy)

  windows  win10-box      ✓ passed    148/148   2m 11s
  linux    ubuntu-2204    ✗ failed    146/148   1m 58s
  macos    macbook        ✓ passed    148/148   3m 40s

FAILED on linux: PortBindingTest, UnixSocketPermissionsTest
Details: https://192.168.1.10:8443/builds/12
$ echo $?   # → 1 (any red cell = nonzero; CI-friendly)
```

`viv run integration --no-wait` just enqueues and prints the URL.

Under the hood, per cell: queue → compatible agent (D8) → `BuildAssignment` → agent pulls blobs by
sha256 → steps run with logs and heartbeats streaming (live service messages join post-Phase 1, D14) →
artifacts pushed →
TRX parsed → matrix updated. Infra hiccups (agent lost mid-build) retry silently on the taxonomy's
INFRA branch and never masquerade as test failures (D9).

## 5. When a cell is red

- The **matrix view** is rows = tests × columns = cells; the red cell links to the build page: full
  log, per-test outcomes, collected artifacts (`*.trx`, `logs/`), and — because `on_fail: keep` — a
  note that `ubuntu-2204` still holds the workdir.
- Poke the machine without leaving your desk:
  `viv exec --agent ubuntu-2204 -- ./sut/myapp --version`
- Rerun one cell after a fix: `viv run integration --only linux`.

## 6. Wire into CI

Vivarium is not a CI server (non-goal); your CI calls it like any other tool:

```yaml
# GitHub Actions / TeamCity step, after publishing out/*
- run: viv run integration          # waits by default
  env:
    VIVARIUM_URL: ${{ vars.VIVARIUM_URL }}
    VIVARIUM_TOKEN: ${{ secrets.VIVARIUM_TOKEN }}   # submit-scoped token, not admin (D4)
```

Exit code gates the pipeline; the CI log carries the matrix summary and the deep link.

## 7. Where the same setup goes next (Phase 2+)

The yaml is the only thing that changes — commands and habits stay identical:

```yaml
    matrix:
      win10-clean: { image: win10-19044-clean }      # pristine pool VM per build (D5, D15)
      win11-avx:   { image: win11-23h2-avx@v4 }      # "with product X installed" scenario
      linux:       { agent: "os.family == linux" }   # still a persistent machine
      macos:       { agent: "name == macbook" }
    clean: pristine                                   # image-backed cells revert every build
```

Image-backed cells run on the provider's pool of pristine VMs, each reverted to its own checkpoint
before the build — while physical cells keep behaving like classic TeamCity agents. One matrix, both
worlds (D15, D16).

## 8. Beyond OS: parameter axes and repeats

*(These land after the Phase 1 core — recorded here so the yaml's final shape is visible.)*

The machine is just one axis (D18). The same configuration can sweep parameters — including several
scenarios on the *same* machine — and repeat cells for flake hunting:

```yaml
    matrix:
      os:                                  # the machine axis is one axis among many
        windows: { agent: "os.family == windows" }
        linux:   { agent: "os.family == linux" }
      renderer: [dx11, vulkan]             # value axes multiply into scenarios
      locale:   [en-US, tr-TR]
    exclude:
      - { os: linux, renderer: dx11 }      # prune impossible combos
    steps:
      - run: IntegrationTests{exe} --renderer {param.renderer} --locale {param.locale}
             --report-trx --results-directory {results}
```

Six scenarios; the three that match `ubuntu-2204` simply queue on it one after another (TeamCity
semantics), while image-backed cells would fan out as parallel clones. Parameters reach the build as
`{param.*}` template variables and `VIVARIUM_PARAM_*` environment variables — running a subset of
tests per scenario is just an argument (`--filter {param.suite}`), not special machinery.

When combos are hand-picked rather than a cross product, name them explicitly:

```yaml
    scenarios:
      win-vulkan-turkish:
        agent: "os.family == windows"
        params: { renderer: vulkan, locale: tr-TR }
      linux-restart-storm:
        agent: "os.family == linux"
        params: { mode: restart-storm }
        repeat: 50                          # 50 ordinary builds, one matrix cell
```

`repeat` turns the cell into a pass rate — `47/50 (94%)` with drill-down into individual iterations —
and `viv run integration --repeat 20` overrides it ad hoc. Repeats on pristine cells are truly
independent runs: that combination is the honest flakiness detector.

Rule of thumb for where a parameter belongs: values only the test process cares about stay in NUnit
`[TestCase]`; Vivarium parameterizes what the process cannot — the environment, the invocation, the
machine.

## UX decisions this walkthrough pins (D17, D18)

1. Configuration-as-code: `vivarium.yaml` in the tested repo; the panel manages the fleet and shows
   results, it does not author test configurations (v1).
2. Payload/steps specialization per cell via template variables (`{rid}`, `{exe}`, …), not per-cell
   copy-paste; `rid:` is declared per cell so payload resolves at upload time.
3. `viv run` = upload (deduped) + enqueue + live matrix in the terminal; nonzero exit on any red cell.
4. Named matrix cells are the unit of rerun (`--only <cell>`) and of matrix columns.
5. Ad-hoc access is `viv exec --agent/--image`, console links live in the panel.
6. The matrix generalizes past OS: the machine selector is one axis among parameter axes
   (cross-product with `exclude`, or an explicit named `scenarios:` list); parameters flow in as
   `{param.*}` and `VIVARIUM_PARAM_*`.
7. `repeat` is first-class; repeated cells aggregate into pass rates. Matrix *rows* are test cases
   (the payload framework's, with per-test history across scenarios) — which is why the columns are
   called *scenarios*, not cases.
