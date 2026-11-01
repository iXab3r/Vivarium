# Story-led task ownership — active planning graph

Planning checkpoint, 2026-11-01, based on `1f0421357334be4330e7440b08b23fc12f56a598`.
Lead: Story Delivery; co-held Reconciliation, REST, Docs and Test Steward perspectives.

## Current result

The 18 existing subsystem/milestone slices now have 54 implementation/REST-contract/REST-acceptance
tasks under the 25 actor goals. This completes the task-packet expansion and activates the new
planning ownership. It is not product implementation, exact variant freeze or milestone acceptance.

Stories select useful outcomes; one coherent subsystem contribution serves every related story and
surface. [story-implementation-links.tsv](story-implementation-links.tsv) records 61 primary/related
links covering all 25 goals. Every story page links its actual contributions. Some goals consume
related contributions rather than requiring a duplicate subsystem implementation.

Full T3Personal Prepare → Windows → Release, used triggers, selected input bytes, launch checks and
configured default publication remain first product priority after accepted P0 and R0. Later full
owners still owe their early used contributions; the old R1–R6 labels are full-scope completion
backlog, not blanket prerequisites to PILOT or a serial UI/REST/CLI work order.

## Active packets

| Slice | Primary story | Implementation | REST contract | REST acceptance |
|---|---|---|---|---|
| SS-PROTO@P0 | US-013 | [#110](https://github.com/iXab3r/Vivarium/issues/110) | [#111](https://github.com/iXab3r/Vivarium/issues/111) | [#112](https://github.com/iXab3r/Vivarium/issues/112) |
| SS-HOST@R0 | US-001 | [#113](https://github.com/iXab3r/Vivarium/issues/113) | [#114](https://github.com/iXab3r/Vivarium/issues/114) | [#115](https://github.com/iXab3r/Vivarium/issues/115) |
| SS-RUN@PILOT | US-013 | [#116](https://github.com/iXab3r/Vivarium/issues/116) | [#117](https://github.com/iXab3r/Vivarium/issues/117) | [#118](https://github.com/iXab3r/Vivarium/issues/118) |
| SS-IAM@R1 | US-004 | [#119](https://github.com/iXab3r/Vivarium/issues/119) | [#120](https://github.com/iXab3r/Vivarium/issues/120) | [#121](https://github.com/iXab3r/Vivarium/issues/121) |
| SS-CFG@R2 | US-006 | [#122](https://github.com/iXab3r/Vivarium/issues/122) | [#123](https://github.com/iXab3r/Vivarium/issues/123) | [#124](https://github.com/iXab3r/Vivarium/issues/124) |
| SS-VCS@R2 | US-007 | [#125](https://github.com/iXab3r/Vivarium/issues/125) | [#126](https://github.com/iXab3r/Vivarium/issues/126) | [#127](https://github.com/iXab3r/Vivarium/issues/127) |
| SS-AGT@R2 | US-009 | [#128](https://github.com/iXab3r/Vivarium/issues/128) | [#129](https://github.com/iXab3r/Vivarium/issues/129) | [#130](https://github.com/iXab3r/Vivarium/issues/130) |
| SS-RUN@R2 | US-010 | [#134](https://github.com/iXab3r/Vivarium/issues/134) | [#135](https://github.com/iXab3r/Vivarium/issues/135) | [#136](https://github.com/iXab3r/Vivarium/issues/136) |
| SS-CAP@R3 | US-025 | [#131](https://github.com/iXab3r/Vivarium/issues/131) | [#132](https://github.com/iXab3r/Vivarium/issues/132) | [#133](https://github.com/iXab3r/Vivarium/issues/133) |
| SS-EXEC@R3 | US-012 | [#137](https://github.com/iXab3r/Vivarium/issues/137) | [#138](https://github.com/iXab3r/Vivarium/issues/138) | [#139](https://github.com/iXab3r/Vivarium/issues/139) |
| SS-CHAIN@R3 | US-013 | [#140](https://github.com/iXab3r/Vivarium/issues/140) | [#141](https://github.com/iXab3r/Vivarium/issues/141) | [#142](https://github.com/iXab3r/Vivarium/issues/142) |
| SS-TRG@R3 | US-014 | [#143](https://github.com/iXab3r/Vivarium/issues/143) | [#144](https://github.com/iXab3r/Vivarium/issues/144) | [#145](https://github.com/iXab3r/Vivarium/issues/145) |
| SS-DEP@R3 | US-020 | [#155](https://github.com/iXab3r/Vivarium/issues/155) | [#156](https://github.com/iXab3r/Vivarium/issues/156) | [#157](https://github.com/iXab3r/Vivarium/issues/157) |
| SS-LOG@R4 | US-015 | [#146](https://github.com/iXab3r/Vivarium/issues/146) | [#147](https://github.com/iXab3r/Vivarium/issues/147) | [#148](https://github.com/iXab3r/Vivarium/issues/148) |
| SS-RES@R4 | US-016 | [#149](https://github.com/iXab3r/Vivarium/issues/149) | [#150](https://github.com/iXab3r/Vivarium/issues/150) | [#151](https://github.com/iXab3r/Vivarium/issues/151) |
| SS-BLOB@R4 | US-018 | [#152](https://github.com/iXab3r/Vivarium/issues/152) | [#153](https://github.com/iXab3r/Vivarium/issues/153) | [#154](https://github.com/iXab3r/Vivarium/issues/154) |
| SS-PROTO@R5 | US-010 | [#161](https://github.com/iXab3r/Vivarium/issues/161) | [#162](https://github.com/iXab3r/Vivarium/issues/162) | [#163](https://github.com/iXab3r/Vivarium/issues/163) |
| SS-MIG@R6 | US-021 | [#158](https://github.com/iXab3r/Vivarium/issues/158) | [#159](https://github.com/iXab3r/Vivarium/issues/159) | [#160](https://github.com/iXab3r/Vivarium/issues/160) |

Each implementation is a native child of its primary story; its contract/acceptance children share
its milestone. The common [packet register](implementation-task-plan.tsv), full [API ledger](api-ledger.tsv),
[subsystem owners](subsystems.tsv), [slices](subsystem-slices.tsv), [contributions](subsystem-contributions.tsv)
and [milestone blocks](api-blocks.tsv) now point to these active tasks. Known-case metadata changes
only task pointers; case IDs, requests, expected values, effects, prerequisites and test bindings stay.
Legacy owner columns retain historical provenance alongside explicit current owner columns.

Native ancestry organizes the work; it does not require whole later-owner issue closure before an
earlier story stage can accept its exact used contribution. Apply same-milestone delivery/closure
gates to the due tasks, and keep later full-owner scope and operation completeness separately visible.

## Preserved source and discussion

Sixty-six original issue bodies supplied 1,497 source blocks to the new packets. Every block is
present in its destination after newline normalization. Exact duplicate prose is stored once within
that same task with an explicit source/hash reference; fenced code blocks and headings retain their
structure. No unique source block is dropped. [source-block-transfer.tsv](source-block-transfer.tsv)
records the source issue/block ordinal/hash and verified destination.
[task-packet-bodies.tsv](task-packet-bodies.tsv) binds all 54 actual GitHub body hashes to their
story/slice/source set. This is a text-preservation proof, not acceptance of every historical claim.

The original bodies remain verbatim inside their GitHub cards. Sixty-two superseded implementation/
contract/acceptance sources receive `state:legacy-source` and a short current-destination banner;
the exact old body follows it. The separate [administrative cleanup](story-source-cleanup.md) now
closes these 62 sources as superseded (`not_planned`), preserving history. #78 remains the priority
actor-goal story, and #79–#81
remain its original UI/REST/CLI acceptance tracks. They are not relabelled legacy or replaced by
three separate backend implementations.

All twelve old comments retain exact IDs, URLs, authors, creation/edit times and bodies. A readable
[retained discussion copy](retained-source-discussion.md) preserves them with original hashes and
historical execution limits. The original conversations and attachment/external evidence links remain
on GitHub; no card/comment/attachment is deleted or hidden. Source closure preserves access. This is not a complete downloaded
timeline/attachment archive, and permanent deletion is not cleared by text routing.

[legacy-crosswalk.tsv](legacy-crosswalk.tsv) retains all 84 original sources, original body hashes/
parentage and explicit current disposition/parent/body identity. Earlier eleven partial-topic
transfers in [task-transfer.tsv](task-transfer.tsv) keep their historical readback hashes; separate
current hashes bind the expanded destinations without rewriting earlier proof identity.

## Retained live gates and readiness work

The eight original milestone closure gates #2–#9 remain live technical acceptance gates, with current
story-task indices above their original text. They do not select product priorities or turn source
supersession into completed implementation. Nine bounded evidence/verification helpers are moved to
their actual consuming tasks; their original bodies remain verbatim:

| Retained task | Current parent | Required output |
|---|---|---|
| #10 | #110 | State ownership and unresolved structural decisions |
| #11 | #110 | Legacy-path/invariant preservation disposition |
| #13 | #110 | Source/history/build-semantics inventory and bounded references |
| #32 | #111 | Worked Build/Agent/queue/reuse/source-settings identity model |
| #34 | #111 | Actual C#/Git editing/evaluation/activation/recovery and editability proof |
| #39 | #112 | Business AC and semantic-superset evidence review |
| #77 | #110 | Already implemented Git-byte correction; final P0 disposition/review/main gate |
| #85 | #111 | Independent server-resolved source/settings/dependency/artifact input receipt |
| #31 | #160 | Final integrated replacement/coexistence/removal proof |

#84 stays under #13 and is already usable within its recorded reference bounds while Open. It is not
server-selection, binary/platform or causal-publication proof. Ready evidence can be consumed without
waiting for a source issue to close. Exact unresolved outputs block only their dependent freeze/proof;
sound independent cohorts may continue. Shared-structure/R0 unknowns still block P0 exit until assessed.

## Verification and next action

[Verification](story-task-migration-verification.md) records readback, full source-block coverage,
graph/owner/case/link checks and the existing read-only accounting command. That command validates
its supported ownership/case/result joins and explicitly reused historical evidence. It does not
implement the missing full story/source/variant/later-gap validator. Product accounting remains
15 known cases / five RED / ten product-prerequisite-blocked / zero GREEN, global denominator UNKNOWN.
No build, test, new API conformance, independent final review or milestone acceptance is claimed.

The separate [source cleanup](story-source-cleanup.md) is complete after final delta/lineage
reconciliation: 62 superseded sources are closed, 100 active cards remain Open, and all twelve original
comments and 1,349 prior source timeline entries are retained. Closure grants no product acceptance.
No product implementation follows automatically from a task-graph migration; the current assignment
at that checkpoint remained planning. The subsequent [#85 reference proof](t3personal-server-inputs.md)
is now available; #32/#34/#111 consume its source/settings/selection facts and named remaining gaps.
