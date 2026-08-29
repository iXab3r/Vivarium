# Prior art

A survey of systems that solve pieces of Vivarium's problem, and what Vivarium borrows from each.
Two families: **test orchestration in VMs/farms** and **image factories & fleet management**.
Borrowed ideas reference the numbered decisions in [`ARCHITECTURE.md`](ARCHITECTURE.md).

The niche check first: nothing OSS combines *snapshot-revert-per-job VM farm* + *versioned image
registry with provisioning pipeline* + *test-result matrix* for desktop OSes. The nearest neighbors are
openQA (QEMU-centric, screen-needle-driven, no pristine-machine/no-runtime contract), Ludus (builds
cyber *ranges*, not test matrices), and Anka Build Cloud (commercial, macOS-only). The niche is open.

## Test orchestration systems

### openQA + os-autoinst (SUSE) — [open.qa](https://open.qa/docs/)
Full-OS UI testing that gates openSUSE releases: web controller + workers driving QEMU (and svirt
backends). No in-guest agent — outside-in control via screen capture matched against "needles"
(PNG + regions) and VNC input. GPL, very active.
**Borrowed:** *developer mode* — pause on failure → live takeover in the browser → resume (roadmap);
video of every job from the screenshot stream; *bug carry-over* — a failure label auto-carries to the
next run of the same test × scenario, killing re-triage (roadmap); job dependencies (`START_AFTER_TEST`
= our base → provision → seal chain, D6).
**Lesson:** needle (screenshot-matching) maintenance is a permanent tax — Vivarium deliberately runs
*real test code inside the guest* instead of driving pixels from outside; also "what changed since last
good?" tooling dominated their triage — our results are tagged with `ImageVersion` for the same reason (§6).

### Cuckoo → CAPEv2 / Cuckoo3 — [capev2.readthedocs.io](https://capev2.readthedocs.io/en/latest/installation/guest/saving.html)
Malware sandboxes are snapshot-revert farms: host + "machinery" drivers (start/stop/revert) + a tiny
stdlib-Python in-guest agent; per-job logic ships as a payload zip. Original Cuckoo died (monolith +
maintainer burnout); CAPEv2/Cuckoo3 active.
**Borrowed (15 years of proof for our core loop):** memory snapshot taken *with the OS logged in and
the agent already running* → revert = instantly-ready guest (D5, §8.3); dumb versionless agent, all
logic in the payload (D2, D3); host explicitly passes wall-clock to the guest after every revert
because restored snapshots wake with stale clocks (D4); the guest-prep checklist doctrine — updates,
Defender, UAC, screensaver, time-sync all handled at image-sealing time (§8.1).
**Lesson:** drivers must *validate* snapshot capability (their KVM-raw-disk trap); keep the agent minimal.

### LAVA (Linaro) — [docs.lavasoftware.org](https://docs.lavasoftware.org/lava/healthchecks.html)
Embedded board-farm scheduler, GPL, active.
**Borrowed:** *health-check canary jobs* per device type that run periodically, **auto-offline** the
device on failure, and preempt user jobs (D13); *health as a separate axis from state* —
Good/Unknown/Bad/Maintenance/Retired vs Idle/Reserved/Running (D8); an explicit `infrastructure error`
result class born from misattribution pain (D9).

### syzkaller (Google) — [pkg.go.dev/github.com/google/syzkaller/vm](https://pkg.go.dev/github.com/google/syzkaller/vm)
Kernel fuzzer with the best minimal hypervisor abstraction in OSS, field-proven at ~10k VMs. Apache-2.0.
**Borrowed:** the tiny per-backend driver contract (Copy/Forward/Run/Diagnose ≈ our small driver verb
set, D1); outcome classification over a merged console+process stream into
*crash / lost-connection / no-output*, with typed infra errors retried N times and never reported as
crashes (D9); the "isolated" backend (pool of physical machines, "revert" = reboot) as the cheap path
to bare-metal scenarios (roadmap).

### GitLab Runner custom executor — [docs.gitlab.com/runner/executors/custom](https://docs.gitlab.com/runner/executors/custom/)
Driver = four executables (`config`/`prepare`/`run`/`cleanup`); the libvirt example is exactly
clone-VM-per-job. MIT.
**Borrowed:** the cleanest minimal INFRA/TEST contract in production — driver exits
`BUILD_FAILURE_EXIT_CODE` vs `SYSTEM_FAILURE_EXIT_CODE` and system failures auto-retry (D9);
cleanup-always-runs semantics.

### Microsoft HLK — [learn.microsoft.com](https://learn.microsoft.com/en-us/windows-hardware/test/hlk/)
Controller + client-machine pools for Windows driver certification.
**Borrowed:** machines gate through a Ready state before becoming schedulable (D8).
**Lesson (cautionary):** heavyweight smart agents with controller/client version lock-step refuse to
talk across versions — Vivarium's agent stays dumb and the protocol is versioned and
backward-compatible within a minor (AGENTS.md → Verification).

### labgrid (Pengutronix) — [labgrid.readthedocs.io](https://labgrid.readthedocs.io/en/latest/overview.html)
Embedded lab control: coordinator/exporter/place model with acquire-lease semantics. LGPL.
**Borrowed:** lease/reservation semantics for grabbing a live VM for manual debugging; they *migrated
their coordinator protocol to gRPC* after their message bus rotted — validates D4.

### DeviceFarmer / OpenSTF — [github.com/DeviceFarmer/stf](https://github.com/DeviceFarmer/stf)
Android device farm with live screen + remote input in the browser. Apache-2.0.
**Borrowed:** live screen as a JPEG frame stream over websocket + input channel — the pragmatic design
for an embedded web console (roadmap); *booking* — reserve a unit from the pool via REST for manual
debugging with auto-release on timeout (pairs with our `QUARANTINE`).
**Lesson:** their ZeroMQ+protobuf microservice mesh was their main regret — one controller process (§3).

### Anka Build Cloud (Veertu) — [docs.veertu.com](https://docs.veertu.com/anka/anka-build-cloud/)
Commercial macOS-CI VM cloud; Vivarium's closest commercial twin (controller + registry + nodes pulling
templates by tag).
**Borrowed:** registry = templates + immutable version tags with CoW layer reuse, nodes pull only
missing layers (§8, roadmap); suspended-state VM distribution ("boot" ≈ instant).

### Tart + Orchard (Cirrus Labs) — [github.com/cirruslabs/orchard](https://github.com/cirruslabs/orchard)
macOS/Linux VMs on Apple Silicon + a controller/worker orchestrator; workers dial out to the controller
(matches D1). VM images distributed as **OCI artifacts** — registry, auth, dedup for free (roadmap).
**Note:** licensing is Fair Source (free under 100 CPU cores) with a pending move toward permissive
OSS after the 2026 Cirrus Labs acquisition — verify before *bundling*; driving the `tart` CLI as an
external tool (our plan, §10) sidesteps the question.

### Avocado / avocado-vt — [avocado-vt.readthedocs.io](https://avocado-vt.readthedocs.io/en/latest/Introduction.html)
kvm-autotest lineage; its Cartesian config generates test × variant matrices.
**Lesson:** the matrix language became write-only from accreted complexity — keep scenario selectors
small and declarative.

### BrowserStack / Sauce Labs (UX reference only)
**Borrowed:** the per-cell artifact bundle — video + logs time-synced to test steps, one click from the
red matrix cell — is the bar users expect from a matrix UI (§11, roadmap).

### Also relevant
[Ubicloud](https://github.com/ubicloud/ubicloud) (AGPL) — ephemeral-VM-per-job GitHub Actions runners,
good plumbing reference. [tmt + Testing Farm](https://docs.testing-farm.io) (Red Hat) — hardware/env
requirements declared in the test plan, prior art for scenario-requirement schemas.
[Firecracker snapshot-restore](https://github.com/firecracker-microvm/firecracker/blob/main/docs/snapshotting/snapshot-support.md)
— ~30 ms restores; future Linux fast path (roadmap), and their "restoring uniqueness" caveat
(clock/RNG/MAC must re-seed after restore) applies to any memory-revert design.

## Image factories & fleet management

### HashiCorp Packer — [developer.hashicorp.com/packer](https://developer.hashicorp.com/packer/integrations/hashicorp/hyperv)
The standard image builder (`hyperv-iso`, `hyperv-vmcx`, qemu, tart…); community
[`windows-update` provisioner](https://github.com/rgl/packer-plugin-windows-update) patches to a level
in a search→install→reboot loop.
**Borrowed:** the pipeline shape (child image = parent + steps ≈ D6); hard-won Windows rules — enable
the remote channel *last* during unattended install, run Windows Update via a dedicated
reboot-aware step with KB filters, never as inline scripts (§8.2 step semantics).
**Lesson:** most Packer-on-Windows pain is WinRM bootstrapping — which Vivarium structurally avoids by
having no inbound guest channel at all (D1).

### Unattended-install tooling
[schneegans Unattend Generator](https://schneegans.de/windows/unattend-generator/) — web generator
**backed by an MIT .NET library** ([cschneegans/unattend-generator](https://github.com/cschneegans/unattend-generator/)),
Win10/11 through current builds: accounts, autologon, bloatware removal, first-logon scripts.
**Borrowed:** embed the library in the controller to render `autounattend.xml` from recipe fields (§8.1).
Ubuntu: subiquity **autoinstall** YAML via NoCloud seed — the Linux equivalent.

### UUP dump — [uupdump.net](https://uupdump.net)
Downloads official packages from Microsoft's update servers and assembles an ISO of an **exact build**
(e.g. 19044 at a chosen LCU) — the only practical source for "Windows 10 at patch level X" media;
[rgl/uup-dump-get-windows-iso](https://github.com/rgl/uup-dump-get-windows-iso) automates it end-to-end.
**Borrowed:** the automation model for base-media acquisition (§8.1). Keep it an operator-run fetch
step; never redistribute ISOs.

### Ludus — [github.com/badsectorlabs/ludus](https://github.com/badsectorlabs/ludus)
AGPL cyber-range builder on Proxmox: Packer templates by name + one range YAML + Ansible roles; the
closest OSS project in spirit.
**Borrowed:** **testing mode** — snapshot everything and switch the network to deny-all-with-allowlist
for the duration, which kills Windows Update drift *during* jobs, not just between rebuilds (§8.2
network profiles); template/recipe registration by name.

### Azure DevTest Labs — [learn.microsoft.com](https://learn.microsoft.com/en-us/azure/devtest-labs/devtest-lab-artifact-author)
**Borrowed:** *artifacts* — catalog items defined by a manifest (`Artifactfile.json`: title, target OS,
typed parameters, run command) pulled from a git repo — the model for Vivarium's provisioning-step
catalog with parameter forms in the panel (§8.2); *formulas* (image + ordered artifacts + parameter
values) ≈ our recipe object; auto-shutdown/quota/expiry policies as fleet-maintenance ideas (D13).

### Test Kitchen / Molecule — [kitchen.ci lineage](https://docs.ansible.com/projects/molecule/)
`platforms × suites` matrix files and the create/**converge**/**verify**/destroy lifecycle.
**Borrowed:** keeping *converge* (provision) separate from *verify* (test) so verify can re-run without
re-provisioning — Vivarium gets this structurally from image sealing (D6); `kitchen login` (drop into a
live instance) as beloved debug UX ≈ `viv-cli exec --agent` + console (§9).

### DetectionLab / GOAD
[DetectionLab](https://github.com/clong/DetectionLab/issues/885) (archived): one maintainer × N
hypervisors × Windows churn = unsustainable; its weekly CI rebuilds were what kept images honest —
**borrowed** as scheduled canary rebuilds (D13). [GOAD](https://github.com/Orange-Cyberdefense/GOAD)
survived by separating *content recipes* from *provider drivers* early — same split as recipes vs
drivers (§3, §8.2).

### Proxmox VE — [pve.proxmox.com](https://pve.proxmox.com/wiki/VM_Templates_and_Clones)
Excellent REST API; linked clones **only from immutable templates**; snapshots with RAM.
**Borrowed:** the immutability invariant — sealed `ImageVersion`s are frozen, clones derive only from
sealed versions (§8.3). Also a candidate host platform for a future driver; no existing Proxmox panel
is a test farm (niche confirmed).

### Ephemeral-runner managers — garm / GitLab fleeting / BuildKite / ARC
[garm](https://github.com/cloudbase/garm/blob/main/doc/external_provider.md):
**providers are external executables** speaking a tiny verb protocol via env — the sanctioned escape
hatch if community hypervisor drivers ever outgrow in-process ones (§3). GitLab
[fleeting](https://docs.gitlab.com/runner/fleet_scaling/fleeting) validates the scaler-vs-provider
two-layer split. BuildKite Elastic Stack: scale-in blocked by lifecycle hooks until the agent finishes
its job; warm pools with `min_idle` + anti-flap delays (scheduler, D8/D13).

### Silent installs on Windows
The ladder: winget → Chocolatey (community packages already encode the right silent switches — the
biggest practical win for recipe steps) → [Boxstarter](https://boxstarter.org) — **borrowed:**
reboot-and-resume step semantics for multi-reboot installs (§8.2). For no-silent-mode installers the
community wraps them in AutoHotkey scripts — validates our `manual` step, with recorded automation as
an optional later upgrade.

### Hyper-V implementation cribs
[AutomatedLab](https://github.com/AutomatedLab/AutomatedLab) (MIT) — the richest existing Hyper-V
provisioning codebase (ISO handling, unattend generation, domain labs);
[fdcastel/Hyper-V-Automation](https://github.com/fdcastel/Hyper-V-Automation) — curated PowerShell for
exactly our driver verbs. Reference material for the Phase 2 Hyper-V driver.

## Consolidated: the ideas Vivarium adopts

1. Memory snapshot taken with agent running & user logged in; revert = ready guest (Cuckoo/CAPE → D5, §8.3).
2. Dumb versionless agent + per-job payload; protocol versioned anyway (CAPE + HLK's cautionary tale → D2, D3).
3. Explicit clock injection after every revert (Cuckoo → D4).
4. INFRA/TEST split as a typed, auto-retried contract (GitLab custom executor + syzkaller + LAVA → D9).
5. Health-check canaries that auto-offline images/hosts; health ≠ state (LAVA → D8, D13).
6. Testing-mode firewall: deny-all + allowlist during jobs (Ludus → §8.2).
7. Provisioning-step catalog with typed parameter manifests (Azure DevTest Labs → §8.2).
8. Exact-build media via UUP dump automation + embedded MIT unattend generator (→ §8.1).
9. Immutable sealed versions; clones only from sealed (Proxmox → §8.3); OCI/delta distribution for multi-host fleets later (Tart/Anka → roadmap).
10. Debug UX to aspire to: pause-on-failure live takeover, per-job video, bug carry-over, per-cell artifact bundle (openQA + BrowserStack → roadmap).
