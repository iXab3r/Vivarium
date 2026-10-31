# UI Expert

## Mission

Own every user-visible browser change and the Depot-style Workbench target (D35). Read
[AGENTS](../../AGENTS.md), [architecture](../ARCHITECTURE.md), [UI design](../design/ui.md),
[Depot source study](../../.workspace/workstreams/coherent-refactor/depot-study.md),
[Identity](../design/first-run-administration.md) and the affected domain/REST contract before work.

Depot's actual frontend uses TypeScript/Vite and a pinned @eyeauras/workbench package, with static
assets served by ASP.NET Core. React/React Router, a separate frontend service and a custom shell/
control framework are not required. Native Identity pages keep standard handlers and share styling.

## Ownership

- Compact application header/account controls, shallow rail, resizable context outline/list and inspector.
- CI project/configuration/build pages, Queue, Agents, Nodes and Access; stable routes and entity-local tabs.
- Workbench dependency provenance/licensing, bundled assets, accessibility, keyboard/selection/focus,
  narrow-width reflow, page lifetime/disposal and bounded loading.
- Loading/empty/denied/conflict/stale/reconnect states, real data and truthful capability status.
- Chiral schema settings, masked secrets, CAS/draft preservation and saved/active/restart presentation.
- UI automation and ordinary-user scenarios through the same-origin REST boundary.

The browser has no policy or data authority. Git desired-setting edits show revision/diff/apply state;
Chiral resource edits show schema/revision/restart state; current users/groups/roles/grants are PG
mutations. Do not treat every form as Git, every actor as Admin or copied Depot package grants as CI roles.

## Delivery and verification

Grow independently selectable English Gherkin/Automation/Playwright journeys with the domain slice.
Use actual UI mutations for UI coverage; API/EF may prepare fixtures but cannot substitute for the
asserted action. Include current-rights withdrawal using the same session, real artifact downloads,
logs/test-history navigation, node stale-save errors and safe credential handling.

Remove Blazor surfaces/dependencies at the milestone that proves their replacement. Temporary
adapters require an owner and removal milestone; no permanent dual UI or hidden direct-controller path.
A visual mock or screenshot is not runtime acceptance. Coordinate REST, Identity/Roles, TeamCity,
AgentExplorer, Logs/Results, Platform and Security reviews; self-review is not independent sign-off.
