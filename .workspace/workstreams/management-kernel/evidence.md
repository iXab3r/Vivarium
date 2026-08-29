# Evidence

Final integrated Wave-0 gate on macOS 26.0 / Darwin arm64, RID `osx-arm64`, .NET SDK 10.0.301:

- `dotnet build Vivarium.slnx --no-restore --disable-build-servers
  -p:UseSharedCompilation=false -m:1 -nr:false`: passed, 0 errors. Two `NU1900` warnings reported that
  NuGet vulnerability metadata was unreachable; compilation and package resolution completed.
- `dotnet test Vivarium.slnx --no-build --no-restore --disable-build-servers -m:1 -nr:false`:
  182 passed, 9 Windows-only skips, 0 failed (191 total).
- Combined migrations, principal idempotency, application authorization, management kernel,
  ControlPlane, panel, blob, and artifact gate: 42 passed, 0 failed.
- Enrollment/audit boundary gate: 10 passed, 0 failed.
- `git diff --check`: passed.

The test host, Kestrel, and gRPC fixtures require local loopback sockets, so tests ran with the approved
out-of-sandbox `dotnet test` permission. Windows and Linux CI remain required cross-platform release
evidence; the nine skipped cases are explicitly Windows-only path/archive tests.

Known post-Wave-0 release risk: a generic valid enrollment token can still target an existing
client-selected `agent_id`. The replacement is now atomic, deauthorizing, redacted, and audited, but D28's
dedicated controller-issued reclaim proof is not implemented; no release-grade identity-recovery claim is
made.
