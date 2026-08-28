# Role-design evidence

Final evidence:

- Frozen inventory: 18 canonical role guides, 16 focused designs, 18 Codex adapters, and 18 Claude
  adapters; every inventory item is classified in `inventory.tsv`.
- Local-link validation: all links in 42 scoped routing/design/workstream Markdown files resolve.
- Adapter validation: role basenames match both harness sets exactly; all 18 Codex TOML files parse.
- Metadata validation: every focused design declares separate Status, Implementation, Maintainer role,
  and Related architecture fields.
- Architecture reconciliation: D22-D27 adopt the two product planes, Git desired state, REST-first
  management, React/Workbench UI, TeamCity-style authorization/first run, and bounded audit/logging.
- `git diff --check`: passed.
- `dotnet build -m:1 --no-restore`: passed with only the sandboxed NuGet vulnerability-feed warning.
- `dotnet test -m:1 --no-build --no-restore`: 150 passed, 0 failed (run outside the sandbox because
  Windows denied temporary PFX import inside it).
