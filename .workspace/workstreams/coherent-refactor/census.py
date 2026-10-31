"""Physical tracked-source census; counts are evidence, never a deletion target."""
import csv
import json
import subprocess
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
WORK = Path(__file__).resolve().parent
OUT = ROOT / 'artifacts/depot-refactor-study'
OUT.mkdir(parents=True, exist_ok=True)
names = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode().split('\0')
rows = []
for name in filter(None, names):
    path = Path(name)
    if any(p in {'target', 'bin', 'obj', 'node_modules'} for p in path.parts):
        continue
    raw = (ROOT / path).read_bytes()
    if b'\0' in raw:
        continue
    try:
        content = raw.decode('utf-8-sig')
    except UnicodeDecodeError:
        continue
    bucket = ('production' if name.startswith('src/') else 'tests' if name.startswith('tests/')
              else 'build-tooling' if name.startswith(('build/', '.teamcity/', '.github/'))
              else 'docs' if name.startswith('docs/') or name in {'AGENTS.md', 'README.md'}
              else 'workstream' if name.startswith('.workspace/') else 'other')
    rows.append({'path': name, 'bucket': bucket, 'extension': path.suffix, 'lines': len(content.splitlines()),
                 'nonblank_lines': sum(bool(line.strip()) for line in content.splitlines()),
                 'bytes': len(raw)})

with (WORK / 'source-inventory.tsv').open('w', encoding='utf-8', newline='') as f:
    writer = csv.DictWriter(f, fieldnames=list(rows[0]), delimiter='\t')
    writer.writeheader()
    writer.writerows(rows)
totals = Counter()
cs = Counter()
for row in rows:
    totals[row['bucket']] += row['lines']
    if row['extension'] == '.cs': cs[row['bucket']] += row['lines']
summary = {'revision': subprocess.check_output(['git','rev-parse','HEAD'], cwd=ROOT,text=True).strip(),
           'physical_lines': dict(totals), 'csharp_physical_lines': dict(cs),
           'top_production_files': sorted((r for r in rows if r['bucket']=='production'), key=lambda r:r['lines'], reverse=True)[:15],
           'top_test_files': sorted((r for r in rows if r['bucket']=='tests'), key=lambda r:r['lines'], reverse=True)[:5]}
(OUT / 'vivarium-code-census.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
print(json.dumps(summary, indent=2))
