# US-013 P0: preserving computed C# through managed edits

Lead: Story Delivery; co-held TeamCity, Git/Versioning, Testing Architect and Test Steward.
This is a bounded continuation of #78/#110/#111/#112, not a public definition SDK or accepted Host.

## The gap and candidate

The first source experiment preserves `UserCode.cs` bytes but replaces an expression reference in
the managed declaration when a literal override is written. That is insufficient to prove later
editing of an entirely computed configuration while preserving unrelated computations. A writer
also needs the existing edit declarations: reconstructing them from effective values loses both
field provenance and explicit local-removal intent.

The alternative experiment keeps one user-owned C# declaration and one typed managed C# edit list
in the same selected product/settings commit:

1. `UserCode.Declare()` computes the base `Settings` using ordinary C#.
2. `Managed.Edits` declares typed `ConfigurationEdit` / `ParameterEdit` values in C#.
3. `Managed.Declare()` applies those edits through the bounded `ProofEdits` helper.
4. The evaluator validates ownership and resolves the resulting inherited parameters as before.
5. The transient `declared` projection reads the actual compiled edit list. A subsequent editor
   changes that list and emits C#; it does not infer old edits from effective settings.

There is no persisted JSON override, arbitrary C# AST rewrite, new catalog, HTTP handler or second
state authority. JSON files under ignored artifacts are transient editor input or immutable output.
The typed declaration is the sole edit input used by evaluation; it is not duplicated in a separate
generated apply function. User code and edit declarations are read from their exact Git commit.
Trusted source plus process bounds remain **not** an OS sandbox for untrusted repositories.

## Precedence and scope

| Edit | Candidate result and evidence boundary |
|---|---|
| No edit for a field | User computation remains active, including later author changes. |
| Literal parameter set | It replaces that local computed parameter; unrelated user computations remain active. This represents the independently observed Kotlin override facet. |
| Explicit local parameter removal | It suppresses the computed local entry; normal parent inheritance supplies `base`. It does not remove the edit and resurrect `computed-mode`. This represents the observed Kotlin removal facet. |
| Name / timeout | Typed scalar overrides; representation evidence, not an independently observed TeamCity computed-field mutation case. |
| Steps | An explicit ordered replacement, including an empty list. Omission from that replacement removes an item. Step IDs here are attributed proof IDs; this is not an oracle for unobserved reference ID/default behavior. |
| Author removes the edit declaration in Git | The user computation returns. This is a source-author action, distinct from compatible REST DELETE of a local parameter. No new public reset operation is implied. |

The renderer accepts only these named field categories. Unknown fields, duplicate target/parameter
edits, ambiguous parameter actions and malformed timeout values fail rather than becoming arbitrary
code paths. These bounds define the experiment, not a new restriction on compatible clients. Other
required field categories and permitted modes must have their own proven path before adoption.

## Verification intent and remaining gate

`ComputedSourceTests` compiles actual committed C# in separate children. It checks initial computed
values, override, a later author change, local removal/inheritance, an unrelated edit read from the
compiled C# declarations, collection/scalar edits, item omission, author clearing of edit declarations
and cold evaluation of the original commit. It checks exact user-source hashes across editor calls.
The missing edit-readback check was executed RED before adding the typed declaration/readback path.

The original literal T3 translation and single-worker Git/PostgreSQL recovery controls remain
separate obligations. This study does not establish authorization, compatible mode/error/completion
handling, concurrent mutation policy, full schemas/templates or production activation. Existing
reference `allowUIEditing=false` / initialization ReadOnly responses still need their own future
shared mutation path; source representability alone cannot accept them. All exact actor/effect/variant,
source-gap, independent aggregate/Security, debt and main-delivery gates remain due.

## Current checkpoint

The fresh root build has zero warnings/errors; root tests have 490 passed and zero failed with the
existing platform skips. All 147 TeamCity controls pass. The separate definition target has ten
passed and zero skipped: this strengthened computed-source proof, the complete explicit T3 recipe
round-trip and all eight Git/PostgreSQL recovery controls. The raw product target retains five
genuine HTTP-response mismatches; the same fifteen-case cohort still has five RED, ten
product-prerequisite-blocked and zero GREEN. No business acceptance or new product case is claimed.

The [receipt](us013-computed-results.json) binds literal TRX, the RED control, current source hashes,
retained assemblies and exact source Git revisions. Historical receipts are unchanged. Final offline
accounting is assessed separately against the exact commit and executes no suites. P0 remains open;
there is no milestone/main acceptance, replacement Host or T3 execution/publication in this study.
