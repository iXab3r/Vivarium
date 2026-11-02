"""Reproduce the P0 source-preservation register, never delete or rewrite product code."""
import argparse
import csv
import hashlib
import io
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
WORK = Path(__file__).resolve().parent

# These are explicit architectural dispositions, not inferred behavioral coverage.
# Each narrower boundary precedes its enclosing directory. New paths fail closed.
GROUPS = {
    'bootstrap': ('keep', 'SS-AGT', '128', 'Existing frozen supervised bootstrap', 'D2/D21/D30; no rewrite or contract change without explicit design; package integrity, prior binding, rollback and child fences survive', 'AgentDeploymentTests;SecretStorageTests'),
    'agent-facts': ('keep', 'SS-AGT', '128', 'Existing cross-platform static facts and negotiated capabilities', 'D8/D16; bounded typed facts and independent status axes; do not silently narrow platform evidence', 'AgentFactCollectorTests;AgentFactRestApiTests;AgentProtocolCompatibilityTests'),
    'agent-runtime': ('adapt', 'SS-EXEC', '137', 'Existing Agent process/transport with additive plan inputs', 'D4/D30/D31; preserve active journal, stop/process ownership, bounded outbox, payload safety, restart/quarantine and terminal ACK; no AgentHub rewrite', 'SessionLoopTests;BuildExecutorTests;AgentResponsivenessTests;PayloadArchiveTests'),
    'agent-bytes': ('adapt', 'SS-BLOB', '152', 'Existing pinned Agent HTTP client with assignment-scoped delivery', 'D4/D24; retain pinned TLS, streaming integrity and cancellation; enforce authorized object scope without a second byte store', 'AgentBlobClientTests;SecretStorageTests;BlobAccessServiceTests'),
    'private-files': ('keep', 'SS-HOST', '113', 'Existing platform private-storage and pinned-TLS primitives', 'D4/D21; owner-only secret storage and authenticated transport remain; no credentials enter Git/logs', 'SecretStorageTests;SessionLoopTests'),
    'cli-yaml': ('replace-remove', 'SS-CFG', '122', 'D41 real CSharp/Git configuration entry; early replacement in #113/#116', 'Owner retired the public YAML/payload DTO contract for fresh US-013. Keep current code until catalogue execution/matrix/cancel/log/artifact and security assertions have verified replacements; preserve AgentHub/Bootstrap, not old public aliases', 'VivariumDefinitionParserTests;CliTests;CliRestClientTests'),
    'cli-control': ('adapt', 'SS-PROTO', '110', 'Transitional ControlPlane upgrade adapter until verified public REST replacement', 'D33; preserve actual upgrade/status consumers and auth until replacement; no parallel business state; removal due R5 not by file cleanup', 'CliTests;ControlPlaneTests;AgentDeploymentTests'),
    'cli': ('adapt', 'SS-HOST', '113', 'Existing CLI over canonical REST and stock Identity credential flow', 'D41 replaces YAML/payload run with catalogue runs. Port useful run/cancel/watch/exit/cursor/retry/cleanup and security assertions; legacy public Build alias/DTO shapes are not required. Preserve retained upgrade/lifecycle commands and use ordinary Identity credentials', 'CliTests;CliRestClientTests;PrincipalIdempotencyTests;PayloadArchiveTests'),
    'agent-protocol': ('keep', 'SS-AGT', '128', 'Existing reverse AgentHub protocol with additive minor-compatible evolution', 'D2/D4/D30/D31; preserve session/generation/workload fences, stale-Agent drain and ACK/result compatibility', 'AgentProtocolCompatibilityTests;FencingTests;SessionLoopTests'),
    'control-protocol': ('deferred-preserve', 'SS-PROTO', '110', 'Transitional gRPC management adapter over shared services', 'D33; retain identified clients until REST replacement; no independent state/permission bypass; removal assessed before R5', 'ControlPlaneTests;CliTests'),
    'contracts': ('keep', 'SS-AGT', '128', 'Existing contract assembly and stamped product version', 'Minor-version backwards compatibility and release version identity; do not change protocol through packaging cleanup', 'AgentProtocolCompatibilityTests;AgentDeploymentTests'),
    'old-setup': ('replace-remove', 'SS-IAM', '113', 'D34 stock Identity/OpenIddict installation-user lifecycle and normal cookie', 'Retain claim/resume/recovery data as legacy input; never port a second verifier or Git SYSTEM_ADMIN authority; remove old entry only with verified replacement', 'AdministrationBootstrapTests;PanelTests;ApplicationAuthorizationTests'),
    'old-rbac': ('replace-remove', 'SS-IAM', '119', 'D34 PostgreSQL TeamCity roles/groups/scoped grants services; minimal R0 portion in #113', 'Exact editable TeamCity permission IDs/includes/scopes/current access replace immutable floors and Git-RBAC; old declarations are preserved migration input, not target authority', 'AuthorizationPolicyTests;ApplicationAuthorizationTests;AuditBoundaryTests'),
    'agent-credentials': ('adapt', 'SS-AGT', '128', 'PostgreSQL Agent enrollment/credential generations; ordinary management credentials owned by Identity', 'TokenStore mixes Agent enrollment with legacy admin/submit: preserve one-use enrollment, hashes and generations; retire only management-token branch after ordinary-client replacement', 'SessionLoopTests;FencingTests;AgentProtocolCompatibilityTests;SecretStorageTests'),
    'auth-boundary': ('adapt', 'SS-IAM', '113', 'Shared stock Identity authentication plus TeamCity current authorization', 'D34; preserve actor/correlation/audit/redaction and current access on every route/byte boundary; no second auth store or copied Depot package ACL', 'ApplicationAuthorizationTests;AuditBoundaryTests;BlobAccessRestApiTests'),
    'agent-lifecycle': ('adapt', 'SS-AGT', '128', 'Existing Agent lifecycle services with PostgreSQL persistence', 'D8/D30/D31; preserve authorization/enablement/connection/health/occupancy axes, exact fencing, restart/drain/ACK deadlines and quarantine; needed portion integrated in R0/PILOT', 'AgentLifecycleTests;AgentResponsivenessTests;FencingTests;AgentRestApiTests'),
    'agent-upgrade': ('deferred-preserve', 'SS-AGT', '128', 'Preserve D30 catalog/package/upgrade lifecycle through Host migration', 'Do not remove release/RID lookup, scoped bytes, maintenance drains, exact health confirmation, rollback/finalization or supervised capability checks', 'AgentDeploymentTests;ControlPlaneTests;CliTests'),
    'git-settings': ('adapt', 'SS-CFG', '122', 'Single Git validation/mutation/reconciliation authority with C# catalog schema', 'D23/D29; retain validated candidate/CAS/last-known-good/restart and canonical LF/legacy CRLF bytes; remove Git IAM ownership while retaining Agent desired settings', 'ManagedGitRepositoryTests;ConfigurationReconciliationTests;AgentDesiredConfigurationTests;AgentConfigurationReconciliationMonitorTests'),
    'sqlite': ('replace-remove', 'SS-HOST', '113', 'PostgreSQL business services and ordered startup migrations', 'D32/D35; preserve migration fail-closed/checksum/transaction/audit and restart invariants, not SQLite writer mechanics; never delete operational databases as source cleanup', 'DatabaseMigrationTests;ManagementKernelTests;ConfigurationReconciliationTests'),
    'build-runtime': ('adapt', 'SS-RUN', '134', 'Shared PostgreSQL Build/queue/lease services; early portions in #113/#116', 'D41 real Project/BuildType ownership for each Build and explicit catalogue-backed matrix/cell relations; preserve ownership/fences/queue deadlines/cancel/history/opt-in keyed retry, not arbitrary payload labels or old public aliases; no second scheduler', 'BuildLeaseTests;BuildQueueStoreTests;BuildQueueTimeoutTests;BuildSchedulerTests;BuildMutationRestApiTests;BuildRestApiTests;BuildEventStreamTests'),
    'artifacts': ('adapt', 'SS-BLOB', '152', 'Storage bytes and PostgreSQL object/build/project references', 'D24/D32; preserve immutable hash/length/provenance, staged upload, current scope authorization and cancellation; never authorize solely by a known content hash', 'BlobAccessServiceTests;BlobAccessRestApiTests;AgentBlobClientTests'),
    'audit': ('adapt', 'SS-HOST', '113', 'PostgreSQL append-only audit beneath shared management services', 'Actor/correlation/target/outcome/config revision remain attributable, bounded and redacted; denied and runtime actions are not Git settings', 'AuditBoundaryTests;ManagementKernelTests'),
    'results': ('adapt', 'SS-RES', '149', 'PostgreSQL result projections with immutable Storage artifact provenance', 'Keep no-report/partial/failure, individual occurrence identity and restart catch-up; preserve raw bytes and legacy useful TRX assertions; full result UI/history remains later', 'TrxResultProjectionTests;TrxProjectionPersistenceTests'),
    'ui': ('replace-remove', 'SS-HOST', '116', 'D35 TypeScript/Vite/EyeAuras Workbench over canonical REST', 'Preserve useful current Agent/Queue/Build navigation and actual actions/log/artifact/current-access behavior; remove Blazor only after verified replacement, not layout redesign', 'PanelTests;BuildRestApiTests;AgentRestApiTests;ApplicationAuthorizationTests'),
    'rest-protocol': ('adapt', 'SS-PROTO', '110', 'Shared native REST and compatible /app/rest projections over owning services', 'D33/D36/D41; preserve retained native resource/locator/media/cursor/ETag/problem and keyed-retry contracts. Only the retired YAML/payload Build entry and its old public ID/DTO shapes lose compatibility; Agent/lifecycle boundaries remain. No facade-only state or mandatory compatible headers', 'RestInfrastructureTests;BuildRestApiTests;BuildMutationRestApiTests;PrincipalIdempotencyTests'),
    'host': ('adapt', 'SS-HOST', '113', 'D35 one real Host with in-process Chiral nodes/private settings', 'Preserve graceful shutdown joining workers, readiness, TLS/private options and release catalog import; PG business state/Git desired/Chiral private settings/Storage bytes keep distinct authority', 'ManagementKernelTests;RestInfrastructureTests;SessionLoopTests;SecretStorageTests'),
}

def group(path):
    name = Path(path).name
    if path.startswith('src/Vivarium.Bootstrap/'): return 'bootstrap'
    if name in {'PrivateStorage.cs', 'PinnedTls.cs', 'ControllerCertificate.cs'}: return 'private-files'
    if path.startswith('src/Vivarium.Agent/Facts/'): return 'agent-facts'
    if path == 'src/Vivarium.Agent/BlobClient.cs': return 'agent-bytes'
    if path.startswith('src/Vivarium.Agent/'): return 'agent-runtime'
    if path.startswith('src/Vivarium.Cli/Configuration/'): return 'cli-yaml'
    if path == 'src/Vivarium.Cli/ControlPlaneEndpoint.cs': return 'cli-control'
    if path.startswith('src/Vivarium.Cli/'): return 'cli'
    if path.endswith('/agent_hub.proto'): return 'agent-protocol'
    if path.endswith('/control_plane.proto'): return 'control-protocol'
    if path.startswith('src/Vivarium.Contracts/'): return 'contracts'
    prefix = 'src/Vivarium.Controller/'
    if not path.startswith(prefix): raise ValueError('Unclassified source: ' + path)
    part = path[len(prefix):]
    if part.startswith(('Administration/', 'Rest/Administration/')) or name in {'UserCredentialService.cs', 'PanelLogin.cs'}: return 'old-setup'
    if name in {'AuthorizationPolicy.cs', 'AuthorizationPolicyProjection.cs'}: return 'old-rbac'
    if name == 'TokenStore.cs': return 'agent-credentials'
    if part.startswith('Security/') or name in {'RestAuthentication.cs', 'ControlPlaneAuthorizer.cs', 'PanelManagementContext.cs'}: return 'auth-boundary'
    if part.startswith('Agents/') or name == 'AgentHubService.cs': return 'agent-lifecycle'
    if part.startswith('Deployment/') or part.startswith('Rest/Deployment/'): return 'agent-upgrade'
    if part.startswith('Configuration/'): return 'git-settings'
    if part.startswith('Persistence/'): return 'sqlite'
    if part.startswith(('Builds/', 'Scheduling/', 'Management/MatrixBuild')) or name == 'DatabaseChangeNotifier.cs': return 'build-runtime'
    if part.startswith('Blobs/') or part.startswith('Rest/Blobs/'): return 'artifacts'
    if part.startswith('Auditing/'): return 'audit'
    if part.startswith('Results/'): return 'results'
    if part.startswith(('Components/', 'wwwroot/')): return 'ui'
    if part.startswith('Rest/') or name == 'ControlPlaneService.cs': return 'rest-protocol'
    if part in {'Program.cs', 'ControllerOptions.cs', 'VivariumControllerHost.cs', 'Vivarium.Controller.csproj', 'Properties/launchSettings.json', 'Properties/AssemblyInfo.cs', 'appsettings.Development.json', 'appsettings.json'}: return 'host'
    raise ValueError('Unclassified source: ' + path)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    names = sorted(filter(None, subprocess.check_output(['git', 'ls-files', '-z', 'src'], cwd=ROOT).decode().split('\0')))
    with (WORK/'source-inventory.tsv').open(encoding='utf8', newline='') as stream:
        census = {r['path'] for r in csv.DictReader(stream, delimiter='\t') if r['bucket']=='production'}
    if set(names) != census: raise ValueError('Product path roster differs from the pinned census; reconcile the census first.')
    with (WORK/'subsystems.tsv').open(encoding='utf8', newline='') as stream:
        owners = {r['subsystem_id'] for r in csv.DictReader(stream, delimiter='\t')}
    with (WORK/'task-packet-bodies.tsv').open(encoding='utf8', newline='') as stream:
        tasks = {r['task_issue'] for r in csv.DictReader(stream, delimiter='\t')}
    tests = {p.stem: p.relative_to(ROOT).as_posix() for p in (ROOT/'tests/Vivarium.Tests').rglob('*Tests.cs')}
    rows = []
    for path in names:
        kind = group(path)
        disposition, owner, task, replacement, invariant, anchors = GROUPS[kind]
        if owner not in owners or task not in tasks: raise ValueError('Unknown owner/task for ' + path)
        baseline = ';'.join(tests[name] for name in anchors.split(';'))
        rows.append(dict(path=path, sha256=hashlib.sha256((ROOT/path).read_bytes()).hexdigest(),
            disposition=disposition, subsystem_id=owner, replacement_task=task,
            replacement=replacement, preservation_invariant=invariant, baseline_test_files=baseline,
            boundary=kind, proof_state='SOURCE_DISPOSITION_ONLY_NOT_REPLACEMENT_ACCEPTANCE'))
    output = io.StringIO(newline='')
    writer = csv.DictWriter(output, fieldnames=list(rows[0]), delimiter='\t', lineterminator='\n')
    writer.writeheader(); writer.writerows(rows)
    target = WORK/'source-disposition.tsv'
    if args.check:
        if not target.exists() or target.read_text(encoding='utf8') != output.getvalue(): raise ValueError('Source disposition is missing or stale.')
    else: target.write_text(output.getvalue(), encoding='utf8', newline='\n')
    from collections import Counter
    print(json.dumps({'product_paths':len(rows), 'dispositions':dict(Counter(r['disposition'] for r in rows)),
        'all_paths_and_baseline_anchors_resolved':True, 'operational_data':'Preserve all existing data; source map neither migrates nor authorizes deletion. Fresh US-013 installation only.',
        'claim':'Per-path preservation/target disposition, not executable coverage or completed replacement.'}))

if __name__ == '__main__': main()
