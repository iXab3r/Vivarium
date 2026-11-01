"""One P0 representability experiment. Not product activation or a conformance runner."""
import copy
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import time
import uuid

sys.dont_write_bytecode = True
import render

source = Path(__file__).resolve().parent
repository = Path(subprocess.check_output(['git','rev-parse','--show-toplevel'],cwd=source,text=True).strip())
source_head = subprocess.check_output(['git','rev-parse','HEAD'],cwd=source,text=True).strip()
source_paths = [*sorted(source.glob('*.cs')), source / 'Evaluator.csproj', source / 'probe.py',
                source / 'render.py', repository / 'global.json', repository / 'Directory.Build.props']
source_inputs = [{'path': path.relative_to(repository).as_posix(),
                  'sha256': hashlib.sha256(path.read_bytes()).hexdigest()} for path in source_paths]
started_utc = datetime.now(timezone.utc).isoformat()
dotnet = repository / 'artifacts/p0-rest-first/tooling/dotnet/dotnet.exe'
run = repository / 'artifacts/us013-p0' / ('representation-run-' + uuid.uuid4().hex)
run.mkdir()
control = run / 'control'
product = run / 'product'
evaluation = run / 'evaluation'
for directory in (control, product, evaluation):
    directory.mkdir()
for filename in ('Evaluator.csproj', 'Model.cs', 'Program.cs', 'UserCode.cs'):
    (evaluation / filename).write_bytes((source / filename).read_bytes())
(evaluation / 'global.json').write_bytes((repository / 'global.json').read_bytes())
user_hash = hashlib.sha256((evaluation / 'UserCode.cs').read_bytes()).hexdigest()
events = []
started = time.monotonic()


def execute(command, cwd, timeout=30):
    # Fixtures are trusted code; a separate bounded process is not a security sandbox.
    process = subprocess.run(command, cwd=cwd, capture_output=True, timeout=timeout)
    if len(process.stdout) + len(process.stderr) > 131072:
        raise RuntimeError('Diagnostic budget exceeded')
    return process


def git(directory, *args):
    result = execute(['git', *args], directory)
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors='replace'))
    return result.stdout.decode().strip()


for directory in (control, product):
    git(directory, 'init', '-b', 'main')
    git(directory, 'config', 'core.autocrlf', 'false')
    git(directory, 'config', 'user.name', 'Vivarium P0 proof')
    git(directory, 'config', 'user.email', 'proof@example.invalid')
    (directory / 'README').write_text('Owned P0 representation experiment.\n')
    git(directory, 'add', 'README')
    git(directory, 'commit', '-m', 'proof: initialize owned repository')


def commit(directory, name):
    git(directory, 'add', '--all')
    git(directory, 'commit', '-m', name)
    return git(directory, 'rev-parse', 'HEAD')


def evaluate(control_head, settings_head, settings_repo, settings_path, declared=False):
    def committed_bytes(directory, revision, path):
        result = execute(['git', 'show', revision + ':' + path], directory)
        assert result.returncode == 0
        return result.stdout
    (evaluation / 'Control.g.cs').write_bytes(committed_bytes(control, control_head, 'Control.g.cs'))
    (evaluation / 'Settings.g.cs').write_bytes(committed_bytes(settings_repo, settings_head, settings_path))
    user_path = str(Path(settings_path).parent / 'UserCode.cs').replace('\\', '/')
    (evaluation / 'UserCode.cs').write_bytes(committed_bytes(settings_repo, settings_head, user_path))
    build = execute([str(dotnet), 'build', 'Evaluator.csproj', '-m:1', '-p:SettingsSource=Settings.g.cs', '-p:UseSharedCompilation=false', '--disable-build-servers', '--nologo'], evaluation)
    if build.returncode:
        return build.returncode, build.stdout + build.stderr
    result = execute([str(dotnet), str(evaluation / 'bin/Debug/net10.0/Evaluator.dll'), *(['declared'] if declared else [])], evaluation, 10)
    assert hashlib.sha256((evaluation / 'UserCode.cs').read_bytes()).hexdigest() == user_hash
    return result.returncode, result.stdout + result.stderr


# The only seed authority is the checked-in C# source. JSON is evaluator output in memory.
for filename in ('Control.g.cs','Settings.g.cs'):
    (evaluation / filename).write_bytes((source / filename).read_bytes())
bootstrap = execute([str(dotnet), 'build', 'Evaluator.csproj', '-m:1', '-p:SettingsSource=Settings.g.cs', '-p:UseSharedCompilation=false', '--disable-build-servers', '--nologo'], evaluation)
assert bootstrap.returncode == 0, bootstrap.stdout.decode(errors='replace')
bootstrap = execute([str(dotnet), str(evaluation / 'bin/Debug/net10.0/Evaluator.dll'), 'declared'], evaluation)
assert bootstrap.returncode == 0, bootstrap.stderr.decode(errors='replace')
seed = json.loads(bootstrap.stdout)
project = seed['project']
settings = copy.deepcopy(seed['settings'])
unbound = '.vivarium/unbound/T3Personal/Settings.g.cs'
bound = '.vivarium/settings/Settings.g.cs'
(control / unbound).parent.mkdir(parents=True)
(product / bound).parent.mkdir(parents=True)


def record(name, ch, sh, owner, path):
    code, result = evaluate(ch, sh, owner, path)
    assert code == 0, result.decode(errors='replace')
    entity = json.loads(result)
    (run / (name + '.json')).write_bytes(result)
    events.append({'name': name, 'control': ch, 'settings': sh, 'settingsRepository': owner.name,
                   'settingsPath': path, 'effectiveSha256': hashlib.sha256(result).hexdigest()})
    return entity


try:
    empty = copy.deepcopy(settings)
    empty['configurations'] = []
    empty['source'] = None
    (control / 'Control.g.cs').write_text(render.control(project), encoding='utf-8', newline='\n')
    (control / unbound).write_text(render.settings(empty), encoding='utf-8', newline='\n')
    (control / unbound).with_name('UserCode.cs').write_bytes((source / 'UserCode.cs').read_bytes())
    blank = commit(control, 'proof: Project before product binding')
    blank_plan = record('blank-project', blank, blank, control, unbound)
    assert blank_plan['configurations'] == [] and blank_plan['source'] is None
    only = copy.deepcopy(empty)
    only['configurations'] = [settings['configurations'][0]]
    (control / unbound).write_text(render.settings(only), encoding='utf-8', newline='\n')
    unbound_head = commit(control, 'proof: unbound BuildType')
    assert record('unbound-configuration', unbound_head, unbound_head, control, unbound)['configurations'][0]['id'] == 'T3Personal_Prepare'
    (control / unbound).write_text(render.settings(settings), encoding='utf-8', newline='\n')
    three = commit(control, 'proof: three related definitions')
    before = record('three-unbound', three, three, control, unbound)
    assert [c['timeoutMinutes'] for c in before['configurations']] == [90, 90, 15]
    assert before['configurations'][2]['snapshots'][0]['configuration'] == 'T3Personal_Windows'
    assert before['configurations'][0]['parameters'] == [
        {'name': 'env.MODE', 'value': 'computed-mode', 'inherited': False}]
    # An evaluator read supplies the editor's data. No persisted JSON overrides C#.
    code, declared = evaluate(three, three, control, unbound, declared=True)
    assert code == 0
    declared = json.loads(declared)
    assert render.settings(declared['settings']) == (control / unbound).read_text()
    assert render.control(declared['project']) == (control / 'Control.g.cs').read_text()
    (product / bound).write_bytes((control / unbound).read_bytes())
    (product / bound).with_name('UserCode.cs').write_bytes((control / unbound).with_name('UserCode.cs').read_bytes())
    product_head = commit(product, 'proof: prepare product settings before binding')
    # Interrupted handoff before the control commit still reads the old authoritative settings.
    assert record('prepared-not-bound', three, three, control, unbound) == before
    project['binding'] = {'repository': 'product-proof', 'path': bound, 'branch': 'main',
                          'trustReference': 'local-owned', 'credentialReference': 'credential-ref-only'}
    (control / 'Control.g.cs').write_text(render.control(project), encoding='utf-8', newline='\n')
    (control / unbound).unlink()
    (control / unbound).with_name('UserCode.cs').unlink()
    bound_control = commit(control, 'proof: bind prepared settings and remove unbound owner')
    after = record('bound-three', bound_control, product_head, product, bound)
    assert after['configurations'] == before['configurations']
    # Property override of an expression does not edit user code; reset exposes inheritance.
    edit = declared['settings']
    config = edit['configurations'][0]
    config['parameters'] = [{'name': 'env.MODE', 'literal': 'managed-override', 'expression': None}]
    (product / bound).write_text(render.settings(edit), encoding='utf-8', newline='\n')
    override = commit(product, 'proof: literal overrides of inherited and computed parameters')
    observed = record('overrides', bound_control, override, product, bound)
    assert observed['configurations'][0]['parameters'] == [
        {'name': 'env.MODE', 'value': 'managed-override', 'inherited': False}]
    literal = '\"; throw new InvalidOperationException(\"data\"); //\\path\nnext\u2028line'
    config['parameters'][0]['literal'] = literal
    (product / bound).write_text(render.settings(edit), encoding='utf-8', newline='\n')
    escaped = commit(product, 'proof: C# syntax in a parameter remains literal data')
    observed = record('escaped-literal', bound_control, escaped, product, bound)
    assert observed['configurations'][0]['parameters'][0]['value'] == literal
    config['parameters'] = []
    (product / bound).write_text(render.settings(edit), encoding='utf-8', newline='\n')
    reset = commit(product, 'proof: reset to inherited source')
    observed = record('reset', bound_control, reset, product, bound)
    assert observed['configurations'][0]['parameters'] == [
        {'name': 'env.MODE', 'value': 'base', 'inherited': True}]
    first = config['steps'][0]
    second = copy.deepcopy(first)
    second['id'], second['name'] = 'second', 'Second'
    config['steps'] = [first, second]
    (product / bound).write_text(render.settings(edit), encoding='utf-8', newline='\n')
    ordered = commit(product, 'proof: replace ordered collection')
    assert [s['name'] for s in record('ordered', bound_control, ordered, product, bound)['configurations'][0]['steps']] == ['Prepare', 'Second']
    config['steps'].reverse()
    (product / bound).write_text(render.settings(edit), encoding='utf-8', newline='\n')
    reordered = commit(product, 'proof: reorder managed collection')
    assert [s['name'] for s in record('reordered', bound_control, reordered, product, bound)['configurations'][0]['steps']] == ['Second', 'Prepare']
    config['steps'] = [first]
    (product / bound).write_text(render.settings(edit), encoding='utf-8', newline='\n')
    omitted = commit(product, 'proof: omission removes managed item')
    good = record('omitted', bound_control, omitted, product, bound)
    # Invalid incoming Git revisions remain visible but cannot replace the known usable plan.
    (product / bound).write_text('this is not valid C#\n', encoding='utf-8', newline='\n')
    invalid = commit(product, 'proof: deliberately invalid external revision')
    code, diagnostic = evaluate(bound_control, invalid, product, bound)
    assert code != 0
    (run / 'invalid-compile.txt').write_bytes(diagnostic)
    assert record('known-good-restart', bound_control, omitted, product, bound) == good
    # Candidate duplication is a semantic evaluator failure, not a compiler error.
    edit['configurations'].append(copy.deepcopy(config))
    (product / bound).write_text(render.settings(edit), encoding='utf-8', newline='\n')
    collision = commit(product, 'proof: conflicting destination identity')
    code, diagnostic = evaluate(bound_control, collision, product, bound)
    assert code != 0 and b'Duplicate configuration identity' in diagnostic
    (run / 'collision.txt').write_bytes(diagnostic)
    report = {'status':'REPRESENTABILITY_PROVED_BOUNDED', 'events': events,
              'invalidRevision':invalid, 'collisionRevision':collision, 'userCodeSha256':user_hash,
              'seconds':round(time.monotonic()-started,3), 'productActivation':'NOT_PROVED',
              'startedUtc':started_utc, 'completedUtc':datetime.now(timezone.utc).isoformat(),
              'sourceHead':source_head, 'sourceInputs':source_inputs,
              'authorization':'NOT_PROVED', 'securitySandbox':'NOT_CLAIMED',
              'sourceRecipe':'Focused representation seed; not the final executable T3 recipe',
              'limits':'No HTTP/OIDC/PG/Chiral activation, mode mutability or cross-repository product transaction proof.'}
    (run / 'receipt.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({'status':report['status'],'receipt':str(run/'receipt.json'),'seconds':report['seconds']}))
except Exception:
    (run / 'incomplete-events.json').write_text(json.dumps(events,indent=2))
    raise
