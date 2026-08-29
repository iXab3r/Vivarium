# End-to-end walkthrough: cross-OS integration tests

The normative UX document. Scenario: you have a program (`myapp`, any language) that must work on
Windows, Linux, and macOS, and an NUnit integration-test project that can verify it. You want to set
the check up once and run it on demand — locally or from CI.

This is the normative Phase 1 target UX for persistent machines; §7 shows how the same setup grows
into pristine snapshots at Phase 2. The durable queue, `vivarium.yaml`, `viv-cli run`, agent lifecycle,
explicit matrix cancellation, and raw build/artifact results exist now. Installer/Downloads flows,
central agent upgrade, parsed test occurrences, and `viv-cli exec` are marked below where they remain
work.
Decision references (D…) point into [`ARCHITECTURE.md`](ARCHITECTURE.md).

## 0. Install the controller — one machine, once

Any always-on box you own (your dev machine is fine to start):

```
viv-server.exe                 # self-contained; creates ./vivarium-data (SQLite + blobs)
```

First run prints the panel URL and an admin token:

```
Vivarium controller listening on https://192.168.1.10:8443
Panel: https://192.168.1.10:8443  (admin token: k3v9…)
Submit token: s7d2…
TLS:   self-signed, fingerprint SHA256:9F3A…
Enroll token (single-use): e5a1…
Data:  ./vivarium-data
```

On your workstation, point the CLI at it once — the fingerprint is confirmed on first contact and
pinned from then on:

```
viv-cli login https://192.168.1.10:8443
```

## 1. Connect three machines — five minutes each

You need one Windows, one Linux, one macOS machine. Anything counts: a spare laptop, a VM you already
have, a Mac mini in a drawer. The target installer UX is Panel → **Agents → Add Agent**, with two
equivalent routes. The TeamCity-style route downloads a preconfigured agent zip containing
`bootstrap.json`; the shell route will be a generated one-liner. It must authenticate the downloaded
installer bytes *before* executing them: use a trusted SPKI pin when the stock downloader supports it,
or verify an independently obtained package digest first, and send the single-use enroll token as
fetch authorization. A `curl -k ... | sh` command is intentionally not specified: validation inside
the downloaded script is too late if a MITM replaced that script (D21). The current Phase 1 binaries
require manual agent launch/configuration because the Downloads page and setup scripts are not
implemented yet.

Each Agent appears under **Agents** as *unauthorized* within seconds (D8). Click **Authorize** and
give it a name. The controller projects the small Phase-1 fact set into canonical `system.*` keys;
legacy `os.*` reports are translated during migration:

| Agent | Reported (excerpt) |
|---|---|
| `win10-box` | `system.os.family=windows system.os.version=<Environment.OSVersion> system.os.arch=x64 system.hostname=…` |
| `ubuntu-2204` | `system.os.family=linux system.os.version=<kernel-version> system.os.arch=x64 system.hostname=…` |
| `macbook` | `system.os.family=macos system.os.version=<Environment.OSVersion> system.os.arch=arm64 system.hostname=…` |

The Agents panel now keeps operator-owned custom parameters such as `software.browser=chrome`
separate from reported facts and merges both maps deterministically for requirement matching. Custom
parameter changes are assignment-fenced, and both maps are copied into the selected build's immutable
provenance. Platform-specific reported inventory (Windows build + UBR, `/etc/os-release`, macOS
product version) is still pending, so do not use the current raw `os.version` as an exact
distro/patch selector.

Two honest caveats. UI-test duty needs extras the setup script *offers* but never does silently:
autologon on Windows asks for credentials, and macOS TCC grants (Accessibility / Input Monitoring)
are clicks Apple reserves for a human (D10). And a headless box needs a display (dummy plug) before
UI results mean anything.

Once D2's authenticated manifest/launcher path ships, this is the last time you touch these machines
by hand: agents auto-upgrade centrally and everything else arrives through builds. Auto-upgrade is not
part of the current runnable slice.

## 2. Describe the check — `vivarium.yaml` in your repo

Build configurations are code, versioned next to the thing they test (D17):

```yaml
# vivarium.yaml
project: myapp

configurations:
  integration:
    matrix:
        windows: { agent: "system.os.family == windows", rid: win-x64 }
        linux:   { agent: "system.os.family == linux",   rid: linux-x64 }
        macos:   { agent: "system.os.family == macos",   rid: osx-arm64 }
    payload: out/{rid}/**
    steps:
      - program: IntegrationTests{exe}
        args: [--report-trx, --results-directory, "{results}"]
        cwd: .
        timeout: 30m
        policy: default
    collect:
      - "{results}/**"
      - logs/**
    queue_timeout: 30m
    clean: none          # Phase 1 fleet; `pristine` arrives with image-backed cells
    on_fail: none        # `keep` is parsed but its cleanup/provider semantics are still pending
```

- **Matrix cells** are named (`windows`, `linux`, `macos`) — the names become matrix columns and
  rerun targets. A cell selects agents with a requirement expression over their parameters (D8, D14).
- **Template variables** specialize one definition per cell: `{rid}` (declared per cell — payload
  paths must resolve at upload time, before any agent has matched), `{os}`, `{arch}`, `{exe}` (`.exe`
  on Windows, empty elsewhere), `{results}`, `{workdir}`.
- **Payload** is files-in / process / files-out (D3): whatever `out/{rid}/` holds is packed into an
  archive (executable bits and symlinks preserved — this matters the moment a Linux agent unpacks your
  tests), content-addressed and deduplicated — unchanged content never uploads twice.
- Collected TRX and logs come back as immutable artifacts and are downloadable from each cell's
  durable build-results page. The controller-side TRX adapter and per-test matrix are the next results
  layer; TeamCity service messages then add live progress without becoming authoritative results (D14).

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
$ viv-cli run integration
Submitted matrix build 867b6095-0e12-42e6-a4a8-299c128f21a4
Results: https://192.168.1.10:8443/builds/867b6095-0e12-42e6-a4a8-299c128f21a4
matrix: QUEUED
windows: QUEUED
linux: QUEUED
macos: QUEUED
matrix: RUNNING
windows: RUNNING on 73e7d9ea-…
linux: RUNNING on 91016bf1-…
windows: FINISHED/SUCCEEDED on 73e7d9ea-…
linux: FINISHED/FAILED on 91016bf1-…
macos: RUNNING on 73e7d9ea-…
macos: FINISHED/SUCCEEDED on 73e7d9ea-…
matrix: FINISHED/FAILED
$ echo $?   # → 1 (any red cell = nonzero; CI-friendly)
```

`viv-cli run integration --no-wait` just enqueues and prints the URL.

Stopping is explicit and durable:

```
$ viv-cli cancel 867b6095-0e12-42e6-a4a8-299c128f21a4 --reason "superseded by a newer commit"
Cancellation requested for matrix build 867b6095-0e12-42e6-a4a8-299c128f21a4
State: CANCEL_REQUESTED
Results: https://192.168.1.10:8443/builds/867b6095-0e12-42e6-a4a8-299c128f21a4
```

The parent results page has the same **Stop matrix build** action. Ctrl+C only detaches the local
`viv-cli run` watch; it deliberately does not cancel remote work.

Under the hood, per cell: queue → compatible agent (D8) → `BuildAssignment` → agent pulls blobs by
sha256 → steps run while status and heartbeats update centrally → artifacts are pushed → the durable
cell/matrix result is updated. Live log/service-message streaming joins later (D14). The current
results page exposes the raw TRX; controller-side parsing into test occurrences is the next slice.
Queue and lost-agent timeouts end as explicit `INFRASTRUCTURE_FAILED`, never as a test failure; full
TEST/CRASH classification and automatic INFRA retry remain D9 work.

## 5. When a cell is red

- The current **build-results view** lists cells with terminal outcomes, step results, and downloadable
  artifacts (`*.trx`, `logs/`). The next result-adapter slice turns those reports into the full
  rows = tests × columns = cells matrix and adds durable per-test details and logs.
- Planned ad-hoc access without leaving your desk:
  `viv-cli exec --agent ubuntu-2204 -- ./sut/myapp --version` (not implemented yet).
- Rerun one cell after a fix: `viv-cli run integration --only linux`.

## 6. Wire into CI

Vivarium can run jobs directly or be called by an existing CI/source-control pipeline:

```yaml
# GitHub Actions / TeamCity step, after publishing out/*
- run: viv-cli run integration      # waits by default
  env:
    VIVARIUM_URL: ${{ vars.VIVARIUM_URL }}
    VIVARIUM_TOKEN: ${{ secrets.VIVARIUM_TOKEN }}   # service credential with project Run permission (D26)
    VIVARIUM_CERT_FINGERPRINT: ${{ vars.VIVARIUM_CERT_FINGERPRINT }}
```

Exit code gates the pipeline; the CI log carries the matrix summary and the deep link.

## 7. Where the same setup goes next (Phase 2+)

The yaml is the only thing that changes — commands and habits stay identical:

```yaml
    matrix:
      win10-clean: { image: win10-19044-clean }      # pristine pool VM per build (D5, D15)
      win11-avx:   { image: win11-23h2-avx@v4 }      # "with product X installed" scenario
      linux:       { agent: "system.os.family == linux" }   # still a persistent machine
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
        windows: { agent: "system.os.family == windows" }
        linux:   { agent: "system.os.family == linux" }
      renderer: [dx11, vulkan]             # value axes multiply into scenarios
      locale:   [en-US, tr-TR]
    exclude:
      - { os: linux, renderer: dx11 }      # prune impossible combos
    steps:
      - program: IntegrationTests{exe}
        args: [--renderer, "{param.renderer}", --locale, "{param.locale}",
               --report-trx, --results-directory, "{results}"]
```

Six scenarios; the three that match `ubuntu-2204` simply queue on it one after another (TeamCity
semantics), while image-backed cells would fan out as parallel clones. Parameters reach the build as
`{param.*}` template variables and `VIVARIUM_PARAM_*` environment variables — running a subset of
tests per scenario is just an argument (`--filter {param.suite}`), not special machinery.

When combos are hand-picked rather than a cross product, name them explicitly:

```yaml
    scenarios:
      win-vulkan-turkish:
        agent: "system.os.family == windows"
        params: { renderer: vulkan, locale: tr-TR }
      linux-restart-storm:
        agent: "system.os.family == linux"
        params: { mode: restart-storm }
        repeat: 50                          # 50 ordinary builds, one matrix cell
```

`repeat` turns the cell into a pass rate — `47/50 (94%)` with drill-down into individual iterations —
and `viv-cli run integration --repeat 20` overrides it ad hoc. Repeats on pristine cells are truly
independent runs: that combination is the honest flakiness detector.

Rule of thumb for where a parameter belongs: values only the test process cares about stay in NUnit
`[TestCase]`; Vivarium parameterizes what the process cannot — the environment, the invocation, the
machine.

## UX decisions this walkthrough pins (D17, D18)

1. Configuration-as-code: `vivarium.yaml` in the tested repo; the panel manages the fleet and shows
   results, it does not author test configurations (v1).
2. Payload/steps specialization per cell via template variables (`{rid}`, `{exe}`, …), not per-cell
   copy-paste; `rid:` is declared per cell so payload resolves at upload time.
3. `viv-cli run` = upload (deduped) + enqueue + live matrix in the terminal; nonzero exit on any red cell.
4. Named matrix cells are the unit of rerun (`--only <cell>`) and of matrix columns.
5. Ad-hoc access will be `viv-cli exec --agent/--image`; console links and the Exec RPC remain planned.
6. The matrix generalizes past OS: the machine selector is one axis among parameter axes
   (cross-product with `exclude`, or an explicit named `scenarios:` list); parameters flow in as
   `{param.*}` and `VIVARIUM_PARAM_*`.
7. `repeat` is first-class; repeated cells aggregate into pass rates. Matrix *rows* are test cases
   (the payload framework's, with per-test history across scenarios) — which is why the columns are
   called *scenarios*, not cases.
