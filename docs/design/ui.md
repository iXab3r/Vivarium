# Vivarium Web UI Design

> Status: **Accepted**
> Implementation: **Planned**
> Maintainer role: [UI Expert](../roles/ui-expert.md)
> Related architecture: [`ARCHITECTURE.md`](../ARCHITECTURE.md) D4, D8, D14, D22-D28

This design specializes the React and EyeAuras Workbench target adopted in D25. Numbered architecture
decisions remain authoritative.

## Purpose

Vivarium has two user-facing products over one agent fleet:

- **TeamCity** manages projects, build configurations, build queues, builds, tests, and results.
- **AgentExplorer** discovers, observes, and later operates physical and virtual hosts outside a build.

The web UI must make both products coherent without merging their concepts. It also makes Git-backed
configuration and the audit journal visible instead of hiding change history behind mutable forms.

## Current and target state

| Concern | Current state | Target state |
|---|---|---|
| Rendering | Blazor Server components in `Vivarium.Controller` | React application using EyeAuras UI Workbench |
| Browser data | In-process panel access; the architecture says the panel needs no API | Published same-origin REST API for every query and action |
| Live updates | Blazor/SignalR projection | Resumable same-origin SSE projection over REST resources |
| Deployment | One Kestrel process | Still one Kestrel process; compiled static assets are included in the controller distribution |
| Information architecture | Agents and Queue/Builds first slices | TeamCity-shaped project/build workspace plus an independent AgentExplorer workspace |
| Configuration edits | Some operator state is mutable controller data | Every domain/admin setting change is a Git-backed revision with a visible diff |
| Audit | Incomplete | Searchable action journal linked from affected objects and mutation results |
| Workbench | Not consumed | Pinned, reproducibly vendored package output with provenance and license evidence |

The repository is pre-release, so the target is a clean replacement rather than a permanent dual UI.
Blazor remains until React reaches named parity; then its routes, components, service wiring, tests, and
unused dependencies are removed in one reviewable migration. New screens are not implemented twice.

## Product-level invariants

1. **One origin and one deployment.** The browser downloads the application and calls `/api/v1/...` on
   the same Kestrel origin. Node, Vite, and Playwright are build/test tools, not runtime services. The
   released controller does not fetch JavaScript, fonts, or UI packages from a CDN.
2. **REST is the browser boundary.** Queries and commands use documented REST resources. A live-event
   stream invalidates or advances those resource projections but is not a hidden command channel or a
   second source of truth.
3. **Git owns configuration.** Project definitions, build configurations, agent custom properties,
   fleet policy, role policy, and other domain/admin settings are edited as versioned documents. The UI
   sends repository identity plus an expected base commit/ETag, presents the diff, and returns a commit or
   an explicit conflict. A commit is not called active until it is the applied authoritative revision.
4. **Actions own audit records.** Cancel build, authorize agent, enable/disable host, refresh inventory,
   and future remote operations are runtime actions. They do not create meaningless Git commits; they
   produce durable audit records. If the same workflow also changes desired configuration, that part
   additionally follows the Git path.
5. **Secrets are neither.** Credentials and secret values never enter Git, diffs, URLs, browser logs, or
   audit payloads. Versioned configuration stores references to an expert-owned secret mechanism.
6. **View preferences stay local.** Theme, column width, collapsed panes, and a user's temporary filters
   may use Workbench/browser storage. They are not represented as shared system configuration.
7. **The server is authoritative.** The browser may optimistically render but never decides permissions,
   compatibility, lifecycle transitions, or build outcomes.
8. **Stable URLs exist for stable objects.** Project, build configuration, build, matrix, host, agent,
   and audit record routes are bookmarkable and refresh-safe.
9. **Staleness is explicit.** Host inventories, connection state, queues, and log tails show observation
   time and reconnecting/stale state. A cached snapshot is never made to look live.
10. **TeamCity vocabulary is preserved.** Visual simplification cannot rename the domain or collapse
    independent status axes.

## State-authority table

The UI labels the source and lifetime of state instead of presenting every change as the same toggle:

| State or operation | Authority | Required UI behavior |
|---|---|---|
| Projects, build configurations, fleet policy, role policy | Git desired configuration | Show repository, base commit/ETag, diff, resulting commit, and applied state |
| Agent durable display name, custom parameters, desired enabled state | Git desired configuration | Edit through the Git workflow; show desired and effective values separately when an overlay exists |
| Agent authorize/unauthorize; credential issue, rotation, and revocation | Audited security action and credential store | Never encode credentials in Git; confirm scope, reveal secrets only under the security contract, and link the audit record |
| Immediate drain, suspend, and resume | Audited runtime overlay | Show as temporary operational state distinct from desired enabled state; do not imply that it changed Git or stopped the current build |
| Connected state, activity, reported facts, inventory freshness | Agent/controller observation | Read-only projection with observation time and stale/partial indicators |
| Build run, cancel, retry, and future remote operations | Audited runtime action/operation | Show accepted versus completed state, idempotency/correlation identifiers, and audit link |
| Credentials and secret values | Expert-owned secret/credential store | Never place values in Git, diffs, URLs, browser logs, or audit payloads |
| Theme, pane sizes, temporary filters | Browser/Workbench preference | Keep local and never imply shared configuration |

The server computes effective eligibility from desired configuration, runtime overlays, authorization, and
observed status. The browser displays those inputs and the resulting effective state; it does not duplicate
the decision logic.

## Runtime and repository shape

The target frontend is a React/TypeScript application built to deterministic static output. ASP.NET Core
serves its hashed assets, the SPA fallback, REST resources, event stream, authenticated downloads, and
existing agent/data-plane protocols from one Kestrel host.

Development may run a Vite server with an explicit proxy to Kestrel. A production test must always cover
the Kestrel-served build because proxy-only behavior is not release behavior.

The precise frontend directory is selected by the integration change, but it must stay within the
controller source tree and produce assets included by the controller publish. A separate deployable web
service and a runtime dependency on the neighboring Workbench checkout are prohibited.

## EyeAuras UI Workbench consumption

EyeAuras UI Workbench is MIT-licensed, host-neutral, and explicitly supports vendored built output. It is
the shell/control foundation, not the owner of Vivarium domain behavior and not a reason to make the
product look like a code editor.

### Initial package set

Runtime packages start with the minimum needed surface:

- `@eyeauras/workbench` for shell, commands, context keys, menus, notifications, output, layouts, and
  framework-neutral controls.
- `@eyeauras/workbench-react` for React lifecycle and React-hosted pages/controls.
- `@eyeauras/workbench-react-router` and its `react-router` peer for the accepted Workbench navigation
  model. The host still publishes and restores canonical browser URLs; per-window memory history is not
  allowed to make durable objects unshareable.
- `react` and `react-dom`.
- `monaco-editor`, although no editor is planned initially, because it is a required peer of the core and
  supplies the VS Code base/context-key layers.

`@vscode/codicons` is an optional Workbench peer but is not part of Vivarium's dependency set: its
CC-BY-4.0 license does not satisfy the repository's current MIT/Apache-2.0-compatible dependency policy.
Initial surfaces use clear text and icon-free Workbench contributions where practical, or a separately
reviewed MIT/Apache-2.0-compatible icon source. The policy must be changed explicitly before Codicons can
be reconsidered. `@eyeauras/workbench-testing` remains a development-only option for shared Playwright
conventions.

Do not initially vendor `workbench-tauri`, `workbench-monaco`, the Tauri demo, or unused public packages.
If a real configuration-file editor later justifies Monaco, add `workbench-monaco` as a separate reviewed
dependency change.

### Vendoring contract

Until a complete reviewed Workbench version is published to npm, Vivarium uses Workbench's supported
"vendored built output" model:

1. Select an exact clean source commit. A dirty neighboring checkout is never a source artifact.
2. Build all selected packages using the Workbench-required build command; `tsc` alone is insufficient
   because the VS Code grid slice requires an additional copy step.
3. A Vivarium-owned sync script copies only selected packages' `dist`, `package.json`, README, LICENSE,
   and NOTICE where present.
4. A committed provenance manifest records repository URL, source commit, package names and versions,
   build command, sync-script version, and content hashes.
5. The frontend uses exact local `file:` dependencies and commits its lockfile. Runtime packages are never
   mixed across different Workbench commits or registry versions.
6. Updating Workbench runs the sync into a clean Vivarium tree, presents the generated diff, verifies the
   package boundary and production bundle, and records the new commit. Hand edits inside the vendored
   output are prohibited.
7. Workbench's MIT license and its Microsoft/VS Code notice travel with the distributed controller.

The checkout inspected during this design pass had HEAD
`8a098614b793a9e375b24e3ac9edce0be62340b6`, but also had uncommitted changes. That hash documents the
inspection baseline only; it is not an approved vendoring source. The migration must choose and record a
clean commit.

### Application boundary

- Workbench owns reusable shell mechanics and generic controls.
- Vivarium owns routes, resources, permissions, terminology, status rendering, and workflows.
- Product-specific components stay in Vivarium. A missing reusable primitive may be proposed upstream,
  but Vivarium does not patch vendored output.
- Prefer the ordinary Workbench page, view, command, context-key, notification, and output contribution
  points. Do not adopt dockable editor groups merely because they exist.
- Initial design is dense and operational, close to TeamCity: durable navigation, tables, breadcrumbs,
  compact status summaries, and progressive detail. Avoid a dashboard made of decorative cards.

## Browser data boundary

The REST Expert owns exact paths and schemas. The UI requires these properties from day one:

- Versioned `/api/v1` resources and a committed OpenAPI document.
- Cookie authentication for the browser, anti-forgery protection for state-changing requests, and
  server-enforced role checks.
- Stable resource identifiers, canonical links, UTC timestamps, correlation/request identifiers, and
  machine-readable error codes with safe user-facing messages.
- Pagination, sorting, and server-side filtering for fleet, builds, queue, projects, and audit data.
- Durable `Idempotency-Key` semantics for side-effecting POSTs and optimistic concurrency through
  `ETag`/`If-Match`; a missing required precondition is `428 Precondition Required`, and a stale
  `If-Match` is consistently `412 Precondition Failed`. `409 Conflict` is reserved for a request based on
  the current revision that violates a domain-state constraint.
- Long operations represented as resources with status, deadline, cancellation, and audit linkage rather
  than an HTTP request held open indefinitely.
- A resumable same-origin SSE stream carrying event id/cursor, resource identity, and revision. The browser
  resumes with `Last-Event-ID`; on a gap, restart, or retention-expiry `410`, it reloads the affected REST
  resource.

The UI cache holds projections only. Events may invalidate or patch an object if the revision sequence is
continuous; the next REST response remains authoritative. Reconnecting never overwrites an unsaved Git
draft. Browser console output, telemetry, and error reporting must follow the Logs Expert's redaction and
volume rules.

## Information architecture

The application has three top-level workspaces. Routes below describe stable concepts; exact URL spelling
is finalized with the REST and UI implementations.

### TeamCity workspace

Start from TeamCity's hierarchy and navigation density:

- **Projects:** project tree, project overview, build configurations, parameters, VCS/revision state, and
  permissions.
- **Build Configuration:** overview, current status, Run action, ordered steps, requirements, parameters,
  triggers/dependencies when implemented, compatible/incompatible agents with reasons, build history, and
  configuration revision/diff.
- **Build Queue:** durable global queue with wait reason, compatibility summary, deadline, priority when
  implemented, and cancel action.
- **Build:** overview, step progress, build log, tests, artifacts, parameters, assigned-agent provenance,
  changes/revision, audit links, and cancellation state.
- **Matrix:** scenario columns, test rows, iteration/pass-rate aggregation, stable cell links, and explicit
  machine/provenance changes.

`Project -> Build Configuration -> Build` appears consistently in breadcrumbs, page titles, URLs, search,
and action labels. A matrix parent and child build remain distinct. The UI does not flatten child artifacts
or infer test results from exit codes.

### AgentExplorer workspace

- **Agents:** searchable, pageable table of all registered Agents, including offline records. Default
  columns cover name, connection/authorization/enablement/activity axes, OS, agent version, capabilities,
  current lease/build, last seen, and inventory age.
- **Host detail / Overview:** identity, status axes, host facts, capability availability/policy, health,
  current activity, and immutable links to builds that ran there.
- **Environment:** on-demand effective environment with observation time, redaction, partial-access
  indicators, and no implicit durable persistence of secret-bearing values.
- **Processes:** on-demand process snapshot with process identity, parent, executable/arguments when
  permitted, owner/session, start time, and resource columns.
- **Network:** TCP/UDP endpoints, local/remote addresses, state, owning process, observation time, and
  platform/permission limitations.
- **Files — Planned**, **Commands — Planned**, and **Software — Planned:** honest placeholders that explain
  the future capability and permission boundary. They do not expose dead buttons or reserve fake REST or
  AgentHub contracts.

The detail page distinguishes `unsupported`, `disabled by host policy`, `forbidden to this user`,
`temporarily unavailable`, and `stale`. OS family alone is never used as proof of capability.

### Administration workspace

- Agent enrollment/download and pending authorization.
- Users, roles, and tokens when their owning designs land.
- Git repository/revision health and configuration conflicts.
- Searchable audit journal.
- Controller and package versions, including the Workbench provenance record.
- System diagnostics allowed by the Logs Expert.

Admin visibility is permission-filtered, but deep links return a real `403` presentation when the user is
authenticated and forbidden. They do not masquerade as `404` unless resource-disclosure policy requires
it.

## Git-backed editing experience

Every versioned edit follows one visible flow:

1. Load repository identity, document, expected base commit/ETag, authoritative applied revision, and
   effective-source information.
2. Edit a typed form or text representation without mutating server state.
3. Validate locally for immediacy and through REST for authority.
4. Review a normalized diff that excludes secrets and generated noise.
5. Supply or accept a meaningful commit message and submit against the expected base commit/ETag.
6. Receive the resulting commit, validation/audit links, and one explicit state: `pending review`,
   `merged/not applied`, `active`, or `invalid`.

If the base moved, the stale `If-Match` response is `412 Precondition Failed` with the current revision and
diff material. The UI preserves the draft and offers reload/reapply; it never silently rebases or
overwrites. `409 Conflict` is a separate presentation for a domain-state conflict after the precondition
has passed. A review-branch commit is never presented as effective configuration before it reaches the
authoritative branch and becomes the applied revision.

Read-only pages show the effective value, source file/key, and revision so operators can answer "why is
this setting active?" Build history always links to the immutable configuration revision/snapshot actually
used, not merely the current definition.

Operational action dialogs show scope and consequences, then link success or failure to the audit record.
Dangerous actions use an accessible two-step confirmation where the risk justifies it; confirmations are
not applied indiscriminately to reversible actions.

## Audit visibility

Audit is a first-class navigation destination and a contextual panel/link on affected objects. A row shows:

- time, actor, originating role/token, action, target, and outcome;
- correlation/request and idempotency identifiers;
- source IP/client metadata allowed by policy;
- Git revision and diff link for configuration changes;
- operation/build identifier for runtime actions;
- redaction/truncation markers where details were intentionally omitted.

Filters include time, actor, target kind/id, action, outcome, and correlation identifier. Audit pages never
render raw secrets or unbounded process/build logs inline; they link to bounded log views governed by the
Logs Expert.

## Live interaction states

Every live screen defines at least:

- initial loading, empty result, and permission-denied states;
- live, reconnecting, stale, and explicitly refreshed states;
- event-gap recovery through authoritative REST reload;
- terminal and partial-failure states;
- action pending, accepted, completed, failed, cancelled, and timed-out states when applicable.

Build cancellation reflects controller intent (`cancel requested`) separately from the eventual terminal
result. Disabling an agent never appears as stopping its current build. An offline host remains navigable
using its last durable facts with an obvious timestamp.

## Accessibility and responsive behavior

- Target WCAG 2.2 AA for supported workflows.
- Use semantic headings, landmarks, forms, tables, links, and buttons before ARIA repair work.
- All navigation, menus, dialogs, grids/tables, tabs, confirmations, filters, and log controls are keyboard
  operable with visible focus.
- Route changes and dialogs put focus predictably; background live updates do not move focus or reset
  selection, scroll, filters, or drafts.
- Status uses text/icon plus color and remains understandable in forced-colors and reduced-motion modes.
- Live announcements are reserved for user-relevant transitions; rapid log/process updates are not poured
  into an ARIA live region.
- Dense tables remain usable at laptop widths. Narrow screens may collapse secondary columns into details,
  but destructive or primary actions remain discoverable.
- Virtualized datasets retain accessible names, row counts where known, and stable keyboard navigation.

## Testing strategy

- **Unit tests:** formatters, reducers, permission-to-presentation mapping, event sequencing, stale/conflict
  behavior, and redaction-safe rendering.
- **Component tests:** forms, diff review, status axes, filters, destructive confirmations, focus restoration,
  and Workbench contribution lifetimes.
- **REST contract tests:** generated or validated against the committed OpenAPI contract; fixtures include
  every documented error and partial-data shape.
- **Browser tests:** Playwright against the production React build served by a real Kestrel test host.
  Cover login, direct deep links, Projects -> Build Configuration -> Build, queue cancellation, Agents ->
  Process/Network snapshots, Git edit/diff/conflict/revision, audit linkage, reconnect/event-gap recovery,
  and forbidden actions.
- **Accessibility checks:** automated axe-style checks plus explicit keyboard/focus/forced-colors coverage
  for critical workflows.
- **Migration parity:** the Reconciliation Lead maintains the finite Blazor route/action/state inventory.
  React must close it or record an approved retirement before removal.

Tests synchronize on visible state, REST completion, or event cursors, never arbitrary sleeps. Unexpected
browser errors and `console.error` fail the relevant browser test. Passing runs retain minimal media;
failures retain enough trace/screenshot evidence to diagnose the UI.

## Migration sequence

1. Reconcile numbered architecture decisions and repository instructions for React, REST-only browser
   access, Git-backed edits, and one-Kestrel static deployment.
2. Establish OpenAPI, browser auth/anti-forgery, error shapes, and a minimal event stream before building
   domain pages.
3. Choose a clean Workbench commit; add the reproducible vendoring script, provenance, licenses, exact
   dependencies, lockfile, and production frontend build.
4. Build the shell, canonical URL routing, login, permission handling, global search/navigation, and audit
   entry point.
5. Port the current Agents and Queue/Builds flows while the parity ledger proves statuses, actions, and
   failure states.
6. Add the TeamCity project/configuration IA and AgentExplorer Agents/detail surfaces through their REST
   contracts.
7. Run production-host browser, accessibility, security, and publish/package checks; remove Blazor cleanly.

This sequence is a dependency order, not permission to implement placeholder backends in the UI.

## Non-goals

- A second web service, Electron/Tauri client, or runtime Node dependency.
- A generic IDE, arbitrary dockable editor experience, or a fork of EyeAuras UI Workbench.
- Monaco editing before a concrete configuration-file workflow needs it.
- UI-owned domain rules, scheduling, capability inference, authorization, Git operations, or audit storage.
- Direct browser access to gRPC AgentHub, SQLite, provider APIs, or host operating-system APIs.
- Fake REST endpoints or active controls for future Files, Commands, or Software features.
- Keeping Blazor indefinitely as a fallback after React parity.
- Versioning secrets, session state, telemetry buffers, or personal pane layout in Git.

## Required evidence before adoption

- A numbered architecture reconciliation removes the Blazor/in-process-panel contradiction and records
  React, REST, Git-backed edits, and the one-Kestrel production boundary.
- A clean Workbench source commit and reproducible vendor manifest; all required MIT licenses/notices are
  present in source and published output.
- A clean clone can build the frontend and `dotnet publish` produces a self-contained controller serving
  the SPA without the Workbench repository or a network fetch.
- Bundle report establishes an initial budget, proves `workbench-monaco`/editor payload is absent until
  intentionally adopted, and proves `@vscode/codicons` is not included.
- Direct navigation and refresh work for representative project, build, host, and audit URLs.
- OpenAPI validation and production-Kestrel browser tests cover authentication, anti-forgery, permissions,
  REST errors, live reconnect/gap recovery, and stale snapshots.
- A configuration edit demonstrably presents repository and expected base commit/ETag, handles a stale
  `If-Match` as `412` without data loss, presents a current-revision domain conflict as `409`, returns the
  resulting commit separately from applied-revision state, and links the audit record.
- Critical workflows pass keyboard, focus, status-without-color, and automated accessibility checks.
- The reconciliation ledger proves every retained Blazor workflow and state in React before Blazor removal.

## Collaboration and open questions

The UI Expert owns resolution of presentation questions, but these require joint decisions:

- **REST Expert:** exact resource paths, resumable SSE protocol, OpenAPI generation, concurrency/error
  envelopes, and browser authentication mechanics.
- **Git/Versioning Expert:** repository topology, branch/commit policy, authorship, merge/conflict mechanics,
  and whether some edits create direct commits or review requests.
- **TeamCity Expert:** initial Project/Build Configuration authoring scope versus D17's tested-repository
  YAML authority, and the exact navigation parity target.
- **AgentExplorer and Agent API/SDK Experts:** inventory refresh costs, sensitive-field policy, capability
  states, partial platform data, and future operation lifecycle.
- **User Roles and Admin/SuperUser Experts:** role-to-action matrix, first-login flow, token presentation,
  and recovery paths.
- **Logs Expert:** live-log transport, chunking/search, client buffer limits, retention markers, and safe
  browser diagnostics.
- **Platform Expert:** platform-specific labels and unavailable states for process, environment, and network
  data.
- **Docs Expert and Reconciliation Lead:** architecture promotion and the Blazor parity/removal ledger.

Open design questions at this stage:

1. Which clean Workbench commit becomes the first vendor baseline?
2. What is the controller-managed Git repository topology, and which settings remain in tested repositories?
3. Are UI-originated changes committed directly for authorized administrators in v1, or always staged for
   review?
4. Which AgentExplorer inventory fields may be retained server-side, and which must remain on-demand only?
5. What exact TeamCity pages constitute the first-release parity boundary beyond the implemented Agents and
   Queue/Builds surfaces?
