"""Generate a bounded, value-free census of versioned TeamCity source features."""
import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
OUT = ROOT / 'artifacts' / 'teamcity-migration'
SOURCES = [
    ('EyeAuras', r'D:\Work\EyeAuras', 'origin/master'),
    ('PoEBane.Rust', r'D:\Work\PoEBane.Rust', 'origin/main'),
    ('CLink', r'D:\Work\EyeAuras.CLink', 'origin/main'),
    ('Workbench', r'D:\Work\EyeAuras.UI.Workbench', 'origin/master'),
    ('CLI.Factory', r'D:\Work\EyeAuras.CliFactory', 'origin/main'),
    ('Rust.Sdk', r'D:\Work\EyeAuras.Rust.Sdk', 'origin/main'),
    ('XAC', r'D:\Work\XAC', 'GH/master'),
    ('Vivarium', str(ROOT), 'origin/main'),
]
PATTERNS = {
    'same-agent': r'runOnSameAgent\s*=\s*true',
    'reuse-disabled': r'reuseBuilds\s*=\s*ReuseBuilds.NO',
    'snapshot-dependency': r'\bsnapshot\s*\(',
    'artifact-dependency': r'\bartifacts\s*\{',
    'artifact-archive-extraction': r'[^\s"]+\.zip!\*',
    'artifact-archive-publication': r'=>[^\n]*\.zip',
    'clean-checkout': r'cleanCheckout\s*=\s*true',
    'checkout-agent': r'CheckoutMode.ON_AGENT',
    'checkout-rule': r'checkoutRules',
    'concurrency-limit': r'maxRunningBuilds\s*=',
    'execution-timeout': r'executionTimeoutMin\s*=',
    'step-conditions': r'\bconditions\s*\{',
    'step-failure-continuation': r'ExecutionMode.RUN_ON_FAILURE|ExecutionMode.ALWAYS',
    'prompted-parameter': r'display\s*=\s*ParameterDisplay.PROMPT',
    'protected-parameter': r'\bpassword\s*\(',
    'dependency-parameter': r'depParamRefs|%dep\.',
    'build-number': r'buildNumberPattern',
    'vcs-trigger': r'\bvcs\s*\{|VcsTrigger',
    'finish-trigger': r'finishBuildTrigger\s*\{',
    'schedule-trigger': r'\bschedule\s*\{',
    'timezone': r'timezone|timeZone',
    'shared-workspace': r'BuildAgentShared|ReleasesFolderPath|BuildReleasesFolderPath',
    'ssh-publish': r'SSHRelease|sshUpload|sshExec',
    'docker': r'dockerCommand|dockerSupport',
    'cake-entry': r'Cake|build\.csproj|Build\.cs|build\.dll',
    'linqpad-entry': r'LPRun|\.linq',
    'submodules': r'submodule',
    'service-message': r'##teamcity\[',
}

def git(path, *args):
    return subprocess.check_output(['git', '-C', path, '--no-optional-locks', *args], text=True, encoding='utf-8', errors='replace')

rows = []
revisions = []
for label, path, ref in SOURCES:
    sha = git(path, 'rev-parse', ref).strip()
    revisions.append({'repository': label, 'root': path, 'ref': ref, 'sha': sha})
    files = git(path, 'ls-tree', '-r', '--name-only', ref, '.teamcity').splitlines()
    for file in files:
        if not file.endswith(('.kt', '.kts', '.cs', '.xml', '.md')):
            continue
        value = git(path, 'show', f'{ref}:{file}')
        hits = {name: [i for i, line in enumerate(value.splitlines(), 1) if re.search(pattern, line)] for name, pattern in PATTERNS.items()}
        rows.append({'repository': label, 'sha': sha, 'path': file, 'lines': len(value.splitlines()), 'features': {k: v for k, v in hits.items() if v}})
(OUT / 'source-feature-census.json').write_text(json.dumps({'revisions': revisions, 'files': rows}, indent=2), encoding='utf-8')
counts = {name: sum(bool(row['features'].get(name)) for row in rows) for name in PATTERNS}
print(json.dumps({'repositories': revisions, 'files': len(rows), 'filesPerFeature': counts}, indent=2))
