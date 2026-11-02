"""Generate coverage candidates and the ID catalog; never mark behavior verified."""
import csv
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
WORK = Path(__file__).resolve().parent
BASE = ROOT / 'artifacts' / 'teamcity-migration'
RUNTIME = ROOT / 'artifacts' / 'teamcity-parity'

def read(root, name):
    return json.loads((root / f'{name}.json').read_text(encoding='utf-8-sig'))

def array(value):
    return [] if value is None else value if isinstance(value, list) else [value]

catalog = []
for line in (ROOT / 'docs/design/teamcity-parity.md').read_text(encoding='utf-8').splitlines():
    if not re.match(r'^\| TC-[A-Z]+-\d+ \|', line):
        continue
    values = [v.strip() for v in line.strip('|').split('|')]
    assert len(values) == 4, line
    catalog.append(dict(zip(('id', 'behavior', 'acceptance', 'basis'), values)))
ids = {row['id'] for row in catalog}
assert len(ids) == len(catalog)
runtime = {row['job']: row for row in read(RUNTIME, 'runtime-summary')}
jobs = read(BASE, 'jobs')['items']
rows = []
for job in jobs:
    key = job['id']
    values = {c: array(read(BASE, f'{key}--{c}')) for c in ('steps', 'agent-requirements', 'snapshot-dependencies', 'artifact-dependencies', 'triggers', 'features', 'templates')}
    enabled_steps = [v for v in values['steps'] if not v.get('disabled', False)]
    features = {v['type'] for v in values['features'] if not v.get('disabled', False)}
    triggers = {v['type'] for v in values['triggers'] if not v.get('disabled', False)}
    needs = {'TC-CAT-01', 'TC-CAT-03', 'TC-DEF-01', 'TC-DEF-02', 'TC-DEF-03', 'TC-DEF-04',
             'TC-VCS-01', 'TC-VCS-02', 'TC-PAR-01', 'TC-PAR-03', 'TC-AG-01', 'TC-AG-06',
             'TC-AG-07', 'TC-QUE-01', 'TC-QUE-02', 'TC-EXE-01', 'TC-EXE-05', 'TC-LOG-01',
             'TC-LOG-03', 'TC-LOG-04', 'TC-LOG-06', 'TC-ART-03', 'TC-API-01', 'TC-API-03',
             'TC-OPS-02', 'TC-IMP-01', 'TC-IMP-02', 'TC-IMP-03', 'TC-IMP-04'}
    if values['templates']: needs.add('TC-CAT-02')
    needs.add('TC-API-05')
    needs.update(f'TC-WIR-{i:02}' for i in range(1, 13))
    if values['agent-requirements']: needs.update(('TC-AG-03', 'TC-AG-04'))
    if values['snapshot-dependencies']: needs.update(('TC-CHN-01', 'TC-CHN-02', 'TC-CHN-03', 'TC-CHN-04', 'TC-CHN-05'))
    if values['artifact-dependencies']: needs.add('TC-ART-02')
    if any(v['type'] == 'DockerCommand' or any(p['name'] == 'plugin.docker.imageId' for p in v.get('properties', [])) for v in enabled_steps): needs.add('TC-EXE-02')
    if 'JetBrains.AssemblyInfo' in features or 'swabra' in features: needs.add('TC-EXE-07')
    if 'JetBrains.SharedResources' in features: needs.add('TC-REL-02')
    if 'jetbrains.agent.free.space' in features: needs.add('TC-AG-09')
    if 'NuGetPackagesIndexer' in features: needs.add('TC-ART-07')
    if 'VcsLabeling' in features: needs.add('TC-VCS-05')
    if 'keepRules' in features: needs.add('TC-ART-06')
    if 'perfmon' in features: needs.add('TC-STA-03')
    if triggers: needs.update(('TC-TRG-04', 'TC-TRG-05'))
    if 'vcsTrigger' in triggers: needs.add('TC-TRG-01')
    if 'schedulingTrigger' in triggers: needs.add('TC-TRG-02')
    if 'buildDependencyTrigger' in triggers: needs.add('TC-TRG-03')
    sample = runtime.get(key, {})
    if sample.get('tests'):
        needs.update(f'TC-TST-{i:02}' for i in range(1, 11))
        needs.update(('TC-TRI-01', 'TC-STA-01', 'TC-STA-02'))
    assert needs <= ids, needs - ids
    rows.append({
        'id': key, 'project': job['projectName'], 'name': job['name'], 'paused': job['paused'],
        'migration_state': 'open-unproven', 'enabled_runners': ','.join(sorted({v['type'] for v in enabled_steps})),
        'requirement_operators': ','.join(sorted({v['type'] for v in values['agent-requirements']})),
        'enabled_features': ','.join(sorted(features)), 'enabled_triggers': ','.join(sorted(triggers)),
        'latest_sample_build': sample.get('build'), 'latest_sample_tests': sample.get('tests'),
        'test_policy_review': 'required-even-when-sample-has-no-tests',
        'exact_settings_review': 'required-values-redacted',
        'candidate_acceptance_ids': ','.join(sorted(needs)), 'source_url': job['webUrl']})

for name, values in (('requirement-catalog.tsv', catalog), ('configuration-parity.tsv', rows)):
    with (WORK / name).open('w', encoding='utf-8', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=list(values[0]), delimiter='\t')
        writer.writeheader()
        writer.writerows(values)
assert len(rows) == 97
print(json.dumps({'requirements': len(catalog), 'unique_configuration_rows': len(rows), 'accepted_configurations': 0}))
