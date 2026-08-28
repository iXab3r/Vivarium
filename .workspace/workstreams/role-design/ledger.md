# Role-design ledger

| Stable ID | Canonical role | Focused design | Adapters | State |
|---|---|---|---|---|
| agent-api-sdk-expert | required | agent-api-sdk | Codex + Claude | verified |
| teamcity-expert | required | teamcity | Codex + Claude | verified |
| agent-explorer-expert | required | agent-explorer | Codex + Claude | verified |
| machine-providers-images-expert | required | machine-providers-images | Codex + Claude | verified |
| vivarium-rest-expert | required | rest-api | Codex + Claude | verified |
| ui-expert | required | ui | Codex + Claude | verified |
| user-roles-expert | required | authorization-model | Codex + Claude | verified |
| admin-superuser-expert | required | first-run-administration | Codex + Claude | verified |
| git-versioning-expert | required | git-versioning | Codex + Claude | verified |
| logs-expert | required | logging | Codex + Claude | verified |
| platform-expert | required | platform | Codex + Claude | verified |
| docs-expert | required | documentation-governance | Codex + Claude | verified |
| security-expert | required | security | Codex + Claude | verified |
| scheduling-coordination-expert | required | scheduling-coordination | Codex + Claude | verified |
| persistence-migrations-expert | required | persistence | Codex + Claude | verified |
| results-artifacts-expert | required | results-artifacts | Codex + Claude | verified |
| reconciliation-lead | required | n/a | Codex + Claude | verified |
| test-steward | required | n/a | Codex + Claude | verified |

Baseline correction: the user named eleven domain streams. Security, Scheduling/Coordination,
Persistence/Migrations, Results/Artifacts, and Machine Providers/Images were added because each owns a
load-bearing contract not fully covered by the named streams. The frozen baseline therefore contains
sixteen domain roles.
