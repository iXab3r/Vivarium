# Vivarium

**A test farm for real operating systems.** Vivarium runs your test corpus against a fleet of snapshot-managed virtual machines — Windows at an exact patch level, Windows with a specific third-party product installed, Ubuntu, macOS — and turns the results into a single *test × OS-configuration* matrix.

A vivarium is an enclosure that keeps organisms under controlled conditions for observation. This one keeps operating systems.

> **Status: design phase.** There is no runnable code yet. The design is documented in
> [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) (the shape), [`docs/ROADMAP.md`](docs/ROADMAP.md) (the order),
> [`docs/prior-art.md`](docs/prior-art.md) (what we learned from the systems that came before), and
> [`docs/walkthrough.md`](docs/walkthrough.md) (what using it will feel like, end to end).

## Why

CI matrixes answer *"does it pass on windows-latest and ubuntu-latest?"*. They cannot answer the questions that actually bite desktop software:

- Does it work on Windows 10 **19044 specifically**, before patch X landed?
- Does it survive on a machine where **product Y v1.2** is already installed?
- Does it behave with **no network at all**? On a pristine machine with **no runtimes installed**?
- Does the installer/overlay/input path work in a **real interactive desktop session**?

Answering those requires real OS installations with versioned, restorable state — virtual machines reverted to a pristine snapshot before every run. Vivarium is the controller, the guest agent, and the image registry that make that loop boring.

## How it works

```mermaid
flowchart LR
    CLI["viv CLI / your CI"] --> C
    UI["Web panel (Blazor)"] --- C
    C["Controller<br/>queue + image registry + results<br/>gRPC + blob store + SQLite"]
    C -- "checkpoint / revert / start / stop" --> D["Host driver<br/>Hyper-V · QEMU/KVM · Tart"]
    D --> VM["Pool VM / enrolled physical machine"]
    VM -- "reverse-connect gRPC<br/>hello / builds / logs / results" --> C
```

- **Controller** — one process: build queue, versioned image registry, scheduler, result store, web panel. TeamCity's model adopted wholesale — projects, build configurations, builds, agent authorization and statuses, parameters/requirements, even the `##teamcity[…]` service-message protocol — with an automation-first spin.
- **Host drivers** — a handful of verbs per hypervisor: create pool VM, checkpoint, revert, start, stop, destroy. Everything else happens over the agent channel, which is why adding a hypervisor is cheap.
- **Agents** — deliberately dumb, reverse-connect to the controller over gRPC; they live in pooled pristine VMs *and* on enrolled physical machines. Only a tiny frozen *bootstrap* is installed once (baked into images, or via a one-liner on a physical box); the agent itself is pulled and auto-upgraded centrally, TeamCity-style — snapshots never get rebuilt for an agent update.
- **Builds** — files in → steps → exit codes + files out. NUnit (self-contained, TRX) is the default payload; anything that produces JUnit XML (e.g. `cargo nextest`) or speaks TeamCity service messages plugs into the same pipe. Guests stay pristine: no SDKs, no runtimes. *Pristine* itself is a per-configuration clean policy — revert-to-snapshot where the machine supports it, plain reboot or nothing where it doesn't.
- **Images** — built as *base → declarative provisioning recipe → sealed disk*, versioned, with drift detection. Provisioning runs through the same build machinery; pooled VMs with per-VM memory checkpoints make revert-to-pristine a matter of seconds.

## Planned features

- Test matrix across Windows / Linux / macOS guests with per-scenario network profiles (NAT / offline / full).
- Physical machines as first-class agents: enroll → authorize → they are scenarios too.
- Checkpoint-with-memory revert per build; pooled pristine VMs for parallelism; machine-provider seam for short-living cloud instances (Azure) later.
- Image registry UI: lineage, versions, actual-vs-declared OS build (drift detection), snapshot chains.
- Declarative image recipes in git, including honest `manual` steps for software with no silent installer.
- Interactive-desktop guests by default (autologon, unlocked session) — input, overlay, and UI tests are first-class.
- Debugging affordances: keep-VM-on-fail, snapshot-the-corpse, console access, crash dumps, failure screenshots.
- `viv exec --image win10-19044 -- <cmd>` — ad-hoc commands on a pristine clone of any image.
- Portable everything: self-contained single-file binaries, xcopy deploy, air-gap friendly — the controller bundles and serves the agent packages itself, and agents auto-update from it.

## Non-goals

- Not a CI server — your CI calls Vivarium through the CLI/API and gets a matrix back.
- Not container-based — the whole point is real OS installations: patch levels, drivers, desktop sessions, macOS.
- Not a general-purpose VM manager — it manages exactly what the test loop needs.

## License

[MIT](LICENSE)
