"""Execute the pinned vendor plugin, without starting a server/Agent or claiming Windows E2E."""
from pathlib import Path
import base64
import hashlib
import io
import json
import shutil
import subprocess
import uuid
import zipfile

server_image = 'sha256:e62c922b94edbef8536bb0d514a193a38bd480fd3855fdac7a649e0a5f8a550a'
agent_image = 'jetbrains/teamcity-minimal-agent@sha256:f711ef16fd62a46bc6e5d3e07f0fcd0eaf7c3c8f905fc4500135a58d5ad05765'
suffix = uuid.uuid4().hex[:12]
owner = 'us013-powershell-profile-' + suffix
directory = Path('artifacts/pilot-chain/powershell-profile-' + suffix).resolve()
directory.mkdir()
receipt = {'kind': 'PINNED_VENDOR_PLUGIN_CONTROL_NOT_REST_OR_WINDOWS_EXECUTION', 'serverImage': server_image,
           'agentImage': agent_image, 'pluginBuild': '222742', 'complete': False,
           'scope': 'Actual pinned PowerShell selection, Windows default execution-policy method and command provider. Synthetic installed Desktop5.1/Core7.5 x64 facts; empty ordinary configuration. Only API/context wiring is supplied; no replacement selection or command model. No Windows process/REST/auth/cancel/whole-runner completeness claim.',
           'inputs': {}, 'cleanup': []}
created = []


def sha(value):
    return hashlib.sha256(value).hexdigest()


def run(args):
    result = subprocess.run(args, capture_output=True, timeout=60)
    if len(result.stdout) + len(result.stderr) > 65536:
        raise RuntimeError('Reference control output exceeded its bound.')
    if result.returncode:
        raise RuntimeError('Reference control failed: ' + result.stderr.decode(errors='replace')[:2048])
    return result.stdout


def copy_image_file(image, path, filename):
    name = owner + '-' + str(len(created))
    identifier = run(['docker', 'create', '--pull', 'never', '--name', name, '--label', 'vivarium.owner=' + owner,
                      '--network', 'none', '--entrypoint', '/bin/true', image]).decode().strip()
    created.append((name, identifier))
    run(['docker', 'cp', name + ':' + path, str(directory / filename)])


try:
    copy_image_file(server_image, '/opt/teamcity/webapps/ROOT/WEB-INF/plugins/powershell.zip', 'powershell.zip')
    with zipfile.ZipFile(directory / 'powershell.zip') as plugin:
        if '<version>222742</version>' not in plugin.read('teamcity-plugin.xml').decode():
            raise RuntimeError('Pinned plugin build differs.')
        with zipfile.ZipFile(io.BytesIO(plugin.read('agent/plugin-agent.zip'))) as agent:
            for name in ['powershell-agent-222742.jar', 'powershell-common-222742.jar']:
                (directory / name).write_bytes(agent.read('lib/' + name))
    copy_image_file(agent_image, '/opt/buildagent/lib/agent-openapi.jar', 'agent-openapi.jar')
    with zipfile.ZipFile(directory / 'agent-openapi.jar') as api:
        if 'Implementation-Version: 2026.1-222742' not in api.read('META-INF/MANIFEST.MF').decode():
            raise RuntimeError('Pinned Agent API build differs.')
    shutil.copyfile(Path(__file__).with_name('PowerShellProbe.java'), directory / 'PowerShellProbe.java')
    for name in ['PowerShellProbe.java', 'powershell.zip', 'powershell-agent-222742.jar', 'powershell-common-222742.jar', 'agent-openapi.jar']:
        receipt['inputs'][name] = sha((directory / name).read_bytes())
    receipt['producerSha256'] = sha(Path(__file__).read_bytes())
    output = run(['docker', 'run', '--rm', '--pull', 'never', '--hostname', 'localhost', '--label', 'vivarium.owner=' + owner,
                  '--network', 'none', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
                  '--user', '1000:1000', '--memory', '512m', '--cpus', '1', '--pids-limit', '128',
                  '--tmpfs', '/tmp:rw,nosuid,size=64m', '--mount', 'type=bind,source=' + str(directory) + ',target=/probe,readonly',
                  '--entrypoint', 'java', server_image, '-Duser.home=/tmp', '-cp',
                  '/opt/teamcity/lib/*:/opt/teamcity/webapps/ROOT/WEB-INF/lib/*:/probe/*', '/probe/PowerShellProbe.java'])
    receipt['stdoutBase64'] = base64.b64encode(output).decode()
    receipt['stdoutSha256'] = sha(output)
    lines = [line[len('CONTROL_JSON '):] for line in output.decode().splitlines() if line.startswith('CONTROL_JSON ')]
    if len(lines) != 1:
        raise RuntimeError('Ambiguous reference control result.')
    receipt['observation'] = json.loads(lines[0])
    receipt['complete'] = True
except Exception as error:
    receipt['failure'] = str(error)[:2048]
finally:
    for name, identifier in reversed(created):
        try:
            metadata = json.loads(run(['docker', 'inspect', name]))[0]
            if metadata['Id'] != identifier or metadata['Config']['Labels'].get('vivarium.owner') != owner:
                raise RuntimeError('Reference container ownership differs.')
            run(['docker', 'rm', name])
            receipt['cleanup'].append({'name': name, 'removed': True})
        except Exception as error:
            receipt['cleanup'].append({'name': name, 'removed': False, 'failure': str(error)[:2048]})
    receipt['ownedResourcesRemoved'] = len(receipt['cleanup']) == len(created) and all(x['removed'] for x in receipt['cleanup'])
    (directory / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'directory': str(directory), 'complete': receipt['complete'], 'cleanup': receipt['ownedResourcesRemoved'], 'observation': receipt.get('observation'), 'failure': receipt.get('failure')}))
raise SystemExit(not (receipt['complete'] and receipt['ownedResourcesRemoved']))
