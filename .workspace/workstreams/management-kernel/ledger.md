# Management kernel ledger

| Inventory | Target | Phase | State | Evidence |
|---|---|---|---|---|
| `grpc:*` | Shared actor/correlation; application-layer command authorization; atomic target-aware mutation audit | 2–3 | closed | Permission matrix, target-aware gRPC denial, idempotency, enrollment, and cancellation tests |
| `http:PUT-/blobs/{sha256}` | Shared bearer authorization and one bounded request audit; retry is `NO_CHANGE` | 2–3 | closed | Blob success/failure/denial/retry/correlation/redaction test in `ManagementKernelTests` |
| `http:GET-/blobs/{sha256}` | Shared bearer authorization | 2 | closed | Existing HTTP credential matrix in `ControlPlaneTests` |
| `http:GET-/builds/*/artifacts/*` | Shared claims context, artifact permission, target-safe audit, and correlation | 2–3 | closed | Success/denial/not-found/invalid-target artifact tests in `PanelTests` |
| `http:POST-/login` | Shared legacy-admin principal; accepted/denied audit | 2–3 | closed | Accepted/denied login audit and panel authentication tests |
| `http:POST-/logout` | Shared claims context and successful logout audit | 2–3 | closed | Panel authentication suite and shared endpoint implementation |
| `startup:enrollment-token-create` | System actor and atomic non-secret audit | 3 | closed | Shared administration path and secret-redaction test |
| `panel:*:read` | Shared legacy-admin context and operation-specific permission | 2 | closed | Panel suite plus permission matrix |
| `panel:agent-*` | Shared legacy-admin context; atomic audit for mutations | 2–3 | closed | Mutation/audit rollback, lifecycle, and custom-parameter tests |
| `panel:enrollment-token-create` | Shared legacy-admin context; atomic non-secret audit | 2–3 | closed | Secret-redaction test |
| `panel:child-build-cancel` | Shared legacy-admin context; atomic cancellation audit | 2–3 | closed | Queue/running cancellation audit test |
| `panel:matrix-build-cancel` | Shared legacy-admin context; atomic first-intent audit | 2–3 | closed | Idempotency/restart tests |
| Phase-1 SQLite schema | Ordered checksummed migrations, exact schema/integrity validation, explicit legacy adoption, and v3 idempotency/audit guard | 1 | closed | Fresh/populated-upgrade/reopen/refusal/rollback/drift/FK/min-version/replace tests |

Burn-down: 25 inventoried boundaries; 25 closed; 0 unclassified; 0 unsupported; 0 deviations.

Baseline correction: the first 20-row census omitted the protected artifact read, three panel read
operations, and the startup enrollment-token mutation. The discovery expression now includes GET
handlers, permission demands, and the startup call; the corrected universe is 25 rows.
