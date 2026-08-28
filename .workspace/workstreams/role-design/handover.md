# Role-design handover

Status: complete.

Baseline: sixteen domain roles, two function roles, sixteen accepted focused design documents, and two
thin harness adapters per role. Canonical role guides are the source of durable expert instructions;
adapters only route context.

Implementation should follow [`docs/ROADMAP.md`](../../../docs/ROADMAP.md). The post-review sequence is:
transport-independent migrations/audit/authorization kernel, read-only REST/OpenAPI, typed static Agent
facts, and then the first managed-local Git mutation with last-known-good reconciliation. Object-scoped
build REST/SSE, dynamic AgentExplorer operations, and the React panel build on those shared contracts
instead of creating alternate state or in-process APIs.
