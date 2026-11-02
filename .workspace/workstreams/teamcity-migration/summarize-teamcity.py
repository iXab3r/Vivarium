"""Regenerate inventory facts; interpretation belongs in the migration ledger."""
import csv
import json
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SNAP = ROOT / 'artifacts' / 'teamcity-migration'
DEST = Path(__file__).resolve().parent

def read(name):
    path = SNAP / f'{name}.json'
    if not path.exists():
        raise RuntimeError(f'Missing snapshot: {name}')
    return json.loads(path.read_text(encoding='utf-8-sig'))

def array(value):
    if value is None:
        return []
    return value if isinstance(value, list) else [value]

jobs = read('jobs')['items']
projects = read('projects')['items']
agents = read('agents')['items']
categories = ('steps', 'parameters', 'agent-requirements', 'snapshot-dependencies', 'artifact-dependencies', 'vcs', 'triggers', 'features', 'templates')
counts = {key: Counter() for key in ('runners', 'activeJobRunners', 'triggers', 'activeJobTriggers', 'features', 'activeJobFeatures', 'requirements', 'templates')}
edges = []
rows = []
parameters = []
for job in jobs:
    values = {category: array(read(f'{job["id"]}--{category}')) for category in categories}
    record = {key: job.get(key) for key in ('id', 'name', 'projectId', 'projectName', 'paused', 'webUrl')}
    record.update({category: len(value) for category, value in values.items()})
    history = read(f'{job["id"]}--builds').get('items', [])
    last = history[0] if history else {}
    record.update({'latestBuildId': last.get('id'), 'latestState': last.get('state'), 'latestStatus': last.get('status'), 'latestStarted': last.get('startDate'), 'latestAgent': last.get('agent', {}).get('name')})
    record['enabledSteps'] = sum(not value.get('disabled', False) for value in values['steps'])
    record['secretParameters'] = sum(value.get('type') == 'protected' for value in values['parameters'])
    record['inheritedParameters'] = sum(value.get('inherited', False) for value in values['parameters'])
    record['enabledTriggers'] = sum(not value.get('disabled', False) for value in values['triggers'])
    rows.append(record)
    for category, counter in (('steps', 'runners'), ('triggers', 'triggers'), ('features', 'features'), ('agent-requirements', 'requirements')):
        for value in values[category]:
            if not value.get('disabled', False):
                counts[counter][value.get('type', 'unknown')] += 1
                active = {'runners': 'activeJobRunners', 'triggers': 'activeJobTriggers', 'features': 'activeJobFeatures'}.get(counter)
                if not job['paused'] and active:
                    counts[active][value.get('type', 'unknown')] += 1
    for value in values['templates']:
        counts['templates'][value['id']] += 1
    for category in ('snapshot-dependencies', 'artifact-dependencies'):
        for value in values[category]:
            edges.append({'job': job['id'], 'upstream': value.get('source', {}).get('id', value.get('id')), 'kind': category, 'disabled': value.get('disabled', False), 'pausedJob': job['paused']})
    for value in values['parameters']:
        parameters.append({'job': job['id'], 'name': value['name'], 'inherited': value.get('inherited', False), 'redacted': value.get('redacted', False), 'type': value.get('type')})

for filename, items in (('inventory.tsv', rows), ('dependencies.tsv', edges)):
    with (DEST / filename).open('w', encoding='utf-8', newline='') as file:
        writer = csv.DictWriter(file, fieldnames=list(items[0]), delimiter='\t')
        writer.writeheader()
        writer.writerows(items)

project_settings = [{'id': p['id'], 'settings': read(f'{p["id"]}--versioned-settings')} for p in projects]
summary = {
    'projects': len(projects), 'archivedProjects': sum(p.get('archived', False) for p in projects),
    'jobs': len(jobs), 'pausedJobs': sum(j['paused'] for j in jobs), 'activeJobs': sum(not j['paused'] for j in jobs),
    'registeredAgents': len(agents), 'authorizedAgents': [a for a in agents if a['authorized']],
    'vcsRoots': len(read('vcs-roots')['items']),
    'enabledSteps': sum(row['enabledSteps'] for row in rows),
    'jobsWithHistory': sum(bool(row['latestBuildId']) for row in rows),
    'jobsWithSnapshots': sum(bool(row['snapshot-dependencies']) for row in rows),
    'jobsWithArtifacts': sum(bool(row['artifact-dependencies']) for row in rows),
    'jobsWithTemplates': sum(bool(row['templates']) for row in rows),
    'jobsWithTriggers': sum(bool(row['enabledTriggers']) for row in rows),
    'jobsWithSecrets': sum(bool(row['secretParameters']) for row in rows),
    'uniqueSecretNames': sorted({p['name'] for p in parameters if p['type'] == 'protected'}),
    'edgeCounts': dict(Counter(e['kind'] for e in edges if not e['disabled'])),
    'activeEdgeCounts': dict(Counter(e['kind'] for e in edges if not e['disabled'] and not e['pausedJob'])),
    'types': {name: dict(counter) for name, counter in counts.items()},
    'perProject': dict(Counter(j['projectName'] for j in jobs)),
    'settings': project_settings,
}
(SNAP / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
(SNAP / 'parameter-metadata.json').write_text(json.dumps(parameters, indent=2), encoding='utf-8')
print(json.dumps({k: v for k, v in summary.items() if k not in ('settings', 'uniqueSecretNames', 'perProject')}, indent=2))
