# Stage 1 planning verification — 2026-11-01

Source HEAD: `9f5f36859f6a90309b117168db343abf14ae44df`, branch
`bugfix/teamcity-behavior-parity`. This record describes the story-catalogue working tree based on
that revision; it is not a runtime receipt or an independent milestone verdict.

## Verified planning joins

- 25 distinct story IDs and issue links: retained #78 plus 24 new issues #86–#109. New stories are
  Open, have `type:story`, no milestone and matching actor-goal titles and canonical AC in their bodies.
- All 110 canonical AC in the acceptance map have story links, with no invented AC ID. Canonical
  feature files exist. All 120 retained technical requirements remain reachable through those AC.
- All 449 existing API-ledger IDs have exactly one primary story/scenario intent link. Existing
  subsystem/legacy owner and case joins match the ledger. Fifteen distinct known case IDs survive;
  no new variant/case or result is created. 149 intent links explicitly carry source/mapping gaps.
- All 449 installed schema occurrences have unique profile/method/path/pointer keys; their path,
  method and declared operationId match the retained schema bytes. All occurrence keys are retained
  in the heading-to-schema candidate register. There are 447 endpoint/name candidates, two explicit
  name discrepancies and nine duplicated operationId groups. This is not semantic normalization.
- All 456 original CLI fixture rows remain: 432 requests and 24 preflights. The 36 prior REST business
  decomposition findings remain among 78 unique open source/mapping gap records. Every referenced
  story/gap ID resolves. The CLI/source union remains explicitly unknown.
- 52 dependency records cover every story, retain bounded stage/output/owner/unblock text and
  distinguish traceability, contributions and ready evidence from issue closure. No native blocked-by
  edge is added. Local link, JSON/TOML/TSV shape and whitespace/diff checks pass; native role-adapter
  metadata is unchanged. These bounded checks do not prove full scenario semantics or minimality.

## GitHub preservation readback

GitHub has 108 Open issues, with the existing nine milestone definitions retained. Compared with
the Stage 1 baseline, all 84 original issue titles, full bodies, states, milestone objects, labels,
assignees and comment counts are identical. Body SHA256 values match `legacy-crosswalk.tsv`.

Readback of all 28 existing parent issues confirms the same 75 native child links and nine original
roots. New stories have no children and no milestone. The seven comments on #2, two on #35 and three
on #36 have identical bodies and stable anchors; all other original issues still have zero comments.
No old issue/comment is edited, closed, deleted or reparented in this stage.

The connected GitHub issue-search tool capped the search at 100 and omitted some state/label
metadata. The complete issue readback therefore used paginated `gh api`:
`repos/iXab3r/Vivarium/issues?state=all&per_page=100`. Native child lists used paginated
`repos/iXab3r/Vivarium/issues/{number}/sub_issues?per_page=100`; comment bodies were read through the
GitHub connector. The comparison did not mistake missing search fields for changed task state.

Local ignored evidence is under `artifacts/story-migration-stage1/`: `baseline/`,
`readback-issues-raw.json`, `readback-native-children.json` and `readback-comments-{2,35,36}.json`.
The tracked crosswalk stores source anchors/hashes. These partial snapshots are not a complete
timeline/attachment/external-evidence archive and are insufficient to authorize destructive history
removal. Stage 2 still says NOT_TRANSFERRED and must reconcile later discussion edits/additions.

## Scope and evidence limits

Changes are planning registers, catalogue/preparation/verification prose and navigation/snapshot
updates. No product code, test binding, architecture decision, runtime schema, generated API status
or raw conformance receipt changes. Product evidence remains historical: fifteen known cases,
five RED, ten blocked, zero GREEN; all 110 business AC remain specified-unbound. Normalized API and
variant denominators remain UNKNOWN, and API-complete is false at its existing evidence boundary.

Root `dotnet build` / `dotnet test`, runtime checks and the existing offline accounting executable
were not run for this documentation-only change. No current-head runtime evidence reuse or new
accounting-checker support for story joins is claimed. The checks above are bounded document/data
checks, not new product or harness implementation. No independent final aggregate or Security review,
milestone closure, production/reference mutation, build/publication or service lifecycle occurred.

Stage 1 supplies a reviewable catalogue, preserved source lineage, dependency basis and an explicit
inventory work list. Next refine the source union and exact story scenarios before assigning Stage 2
implementation tasks and transferring useful discussion. Full T3Personal remains first priority;
#84 is usable bounded reference material and #85 still owns missing server-selection evidence.
