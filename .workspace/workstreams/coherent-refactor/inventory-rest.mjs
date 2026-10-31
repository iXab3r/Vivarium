/** Read-only analysis of pinned CLI fixtures; not a test runner or coverage claim. */
import { readFile, readdir, writeFile, mkdir } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { stripTypeScriptTypes } from 'node:module';

const root = process.cwd();
const work = join(root, '.workspace/workstreams/coherent-refactor');
const output = join(root, 'artifacts/depot-refactor-study');
const source = resolve(process.argv[2] ?? join(output, 'cli-reference'));
const expectedRevision = 'b158fe03eab3c31982a21b36c278f690f6b541d9';
const revision = execFileSync('git', ['--no-optional-locks','rev-parse','HEAD'], { cwd: source, encoding:'utf8' }).trim();
if (revision !== expectedRevision) throw new Error('Reference revision differs; record a baseline change first.');
const integration = join(source, 'integrations/teamcity');
const pkg = JSON.parse(await readFile(join(integration, 'package.json'), 'utf8'));
if (pkg.version !== '0.5.2') throw new Error('Unexpected reference CLI version.');
const tests = join(integration, 'tests');
const files = (await readdir(tests)).filter(name => name.endsWith('-cases.ts')).sort();
const dataUrl = code => 'data:text/javascript;base64,' + Buffer.from(code).toString('base64');
const authoring = stripTypeScriptTypes(await readFile(join(tests, 'authoring-cases.ts'), 'utf8'));
const authoringUrl = dataUrl(authoring);
const counts = {}; const rows = [];
const categories = {
  projects:['Catalog','R2'],buildTypes:['Definitions','R2'],'vcs-roots':['VCS','R2'],'vcs-root-instances':['VCS','R2'],
  builds:['Builds/results','R2/R4'],buildQueue:['Queue','R0/R2'],agents:['Agents','R0/R2'],agentPools:['Agent pools','R2/R3'],
  tests:['Tests/triage','R4'],testOccurrences:['Tests/triage','R4'],problems:['Tests/triage','R4'],problemOccurrences:['Tests/triage','R4'],
  investigations:['Tests/triage','R4'],mutes:['Tests/triage','R4'],changes:['VCS','R2'],
  users:['Identity/access','R1'],userGroups:['Identity/access','R1'],roles:['Identity/access','R1'],avatars:['Identity/access','R1'],agentTypes:['Agents','R0/R2'],
  cloud:['Provider/cloud','R6 disposition'],server:['Server/admin','R0/R6'],audit:['Audit','R1/R4'],
  health:['Server/admin','R4/R6'],deploymentDashboards:['Deployments','R3/R6']
};
const fullPath = value => value.startsWith('/app/rest') ? value : '/app/rest' + value;
function authorityHint(path,method) {
  if (/\/artifacts\/|\/sources\/|\/files\//.test(path)) return 'Storage bytes + PG references/access; review sensitive paths';
  const resource=path.replace(/^\/app\/rest\/?/,'').split('/')[0];
  if (['users','userGroups','roles','avatars'].includes(resource)) return 'PG Identity/RBAC + framework credential lifecycle';
  if (['buildTypes','projects','vcs-roots'].includes(resource)) return 'Git/C# desired + PG projection; review runtime subresources';
  if (['agents','agentPools','agentTypes'].includes(resource)) return 'PG runtime or Git fleet desired; per-operation decision required';
  if (['server','cloud'].includes(resource)) return 'Chiral resource or PG operational/provider service; per-operation decision required';
  return method==='GET'?'Read owning PG/domain projection; review sensitive/action-like reads':'PG owning domain transaction/action; review required';
}
function classify(path) {
  const resource=path.replace(/^\/app\/rest\/?/,'').split('/')[0];
  if (/\/artifacts\/|\/sources\/|\/files\/|\/buildLog/.test(path)) return ['Files/logs','R2/R4'];
  return categories[resource] ?? ['Protocol/discovery','R0/P0'];
}
function add(file, key, index, value, preflight=false) {
  const path=fullPath(preflight ? value.preflight : value.path);
  const method=preflight ? 'GET' : value.method;
  const [family,milestone]=classify(path);
  const row={id:`CLI-FIX-${String(rows.length+1).padStart(4,'0')}`,source:`integrations/teamcity/tests/${file}`,
    table:key,case_index:index,kind:preflight?'preflight':'request',method,path,family,
    candidate_milestone:milestone,candidate_owner:family,authority_hint:authorityHint(path,method),
    command:value.argv?.slice(0,3).join(' ') ?? '',
    query_keys:Object.keys(value.query??{}).sort().join(','),
    fields:preflight?(value.preflightFields??''):(value.query?.fields??''),
    body_kind:preflight?'none':value.body===undefined?'none':typeof value.body==='string'?'text':'json',
    body_keys:preflight||!value.body||typeof value.body!=='object'?'':Object.keys(value.body).sort().join(','),
    declared_status:preflight?'fixture-default':String(value.responseStatus??'fixture-default'),
    declared_media:value.accept??value.media??(value.text?'text/plain':'fixture-default'),
    state:'discovered-unverified'};
  rows.push(row);counts[family]=(counts[family]??0)+1;
}
for (const file of files) {
  let code=stripTypeScriptTypes(await readFile(join(tests,file),'utf8'));
  code=code.replaceAll('"./authoring-cases.js"',JSON.stringify(authoringUrl));
  // Fixture modules are inspected constant tables/loops and import only the authoring helper.
  if (/^\s*import\s+[^\n]*?\bfrom\s+["'](?!data:)/m.test(code)) throw new Error(`Unexpected fixture dependency in ${file}`);
  const module=await import(dataUrl(code));
  for (const [key, values] of Object.entries(module)) {
    if (!key.endsWith('Cases')||!Array.isArray(values))continue;
    for (const [index,value] of values.entries()) {
      if (!value.method||typeof value.path!=='string')throw new Error(`Missing wire fields: ${file}/${index}`);
      add(file,key,index,value);
      if(value.preflight)add(file,key,index,value,true);
    }
  }
}
const columns=Object.keys(rows[0]);
const escape=value=>String(value).replaceAll('\t',' ').replaceAll('\r',' ').replaceAll('\n',' ');
await writeFile(join(work,'rest-fixture-inventory.tsv'),[columns.join('\t'),...rows.map(r=>columns.map(c=>escape(r[c])).join('\t'))].join('\n')+'\n');
const hash=content=>createHash('sha256').update(content).digest('hex');
const installedRoot=join(process.env.APPDATA,'npm/node_modules/@eyeauras/teamcity-cli');
const installedVersion=JSON.parse(await readFile(join(installedRoot,'package.json'),'utf8')).version;
const installedHash=hash(await readFile(join(installedRoot,'dist/src/client.js')));
if (installedVersion!=='0.5.2'||installedHash!=='83a06b5d92ad2409bbdaac6f0df45c9765a0ae42f5256d05c56072d886f4cf07') {
  throw new Error('Installed runtime differs; record a baseline change first.');
}
const receipt={reference_revision:revision,reference_cli_version:pkg.version,
  installed_cli_version:installedVersion,installed_client_sha256:installedHash,
  installed_readme_claimed_method_paths:449,fixture_tables:files.length,fixture_rows:rows.length,
  request_rows:rows.filter(r=>r.kind==='request').length,preflight_rows:rows.filter(r=>r.kind==='preflight').length,
  unique_concrete_method_paths:new Set(rows.map(r=>r.method+' '+r.path)).size,
  counts_by_family:counts,implemented_teamcity_facade_routes:0,
  limitations:['Synthetic fixtures, not live reference results or executed tests.',
    'Published npm metadata has no gitHead; matching source/package version is not installed-binary provenance proof. Runtime file hash is recorded separately.',
    'Concrete example paths are not normalized API templates; do not compare this count to the README route claim.',
    'Inline test handlers, all client variants and full server OpenAPI remain separate reconciliation inputs.',
    'Fixture-default media/status requires harness inspection before a protocol contract is frozen.'],
  input_hashes:await Promise.all(files.map(async name=>({path:`integrations/teamcity/tests/${name}`,sha256:hash(await readFile(join(tests,name)))})))};
await mkdir(output,{recursive:true});
await writeFile(join(output,'rest-fixture-receipt.json'),JSON.stringify(receipt,null,2)+'\n');
console.log(JSON.stringify(receipt));
