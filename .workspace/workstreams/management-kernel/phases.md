# Phases

1. **Closed.** Establish the green baseline and versioned migration ledger. Gate: fresh and legacy databases reach the exact current schema; drift and newer schemas fail closed.
2. **Closed.** Introduce stable legacy principals, request/correlation context, and one evaluator. Gate: every inventoried boundary preserves its existing authority and no submit/agent credential gains administration rights.
3. **Closed.** Add the minimal append-only audit journal to caller/security mutations. Gate: success plus audit is atomic, retries do not invent duplicate successes, secrets are absent, and automatic lifecycle work stays out.
4. **Closed.** Reconcile documentation and run repository-wide verification. Gate: `dotnet build` and `dotnet test` pass at the solution root after the final migration-manifest review.
