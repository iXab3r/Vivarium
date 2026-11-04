from pathlib import Path
import argparse,subprocess,json,hashlib,re,os,copy

repository_root=Path(__file__).resolve().parents[2]
owned_root=(repository_root/'artifacts/us013-t3-recovery-v3').resolve()
if not owned_root.is_relative_to(repository_root):raise RuntimeError('Owned preparation root leaves the repository.')
base=owned_root/'source'
if base.resolve().parent!=owned_root:raise RuntimeError('Owned source is redirected outside its preparation root.')
parser=argparse.ArgumentParser(description="Prepare the owned package-only T3 acceptance source. Never launches a built application or publishes.")
parser.add_argument('--clone-from',default='https://github.com/iXab3r/t3code.git',help='Read-only source repository containing the pinned commit.')
args=parser.parse_args()
git_environment={k:v for k,v in os.environ.items() if not k.upper().startswith('GIT_')}
git_environment['GIT_CONFIG_NOSYSTEM']='1'
git_environment['GIT_CONFIG_GLOBAL']='NUL' if os.name=='nt' else '/dev/null'
git_environment['GIT_TERMINAL_PROMPT']='0'
git_environment['GIT_ALLOW_PROTOCOL']='https:file'
pin='7ff65cada80dd817aa5d7467b85e78ec30c578a9'
repository='iXab3r/vivarium-us013-e2e'
target='551ae1f5580629142f8289d360e21122c99c8e21'
def git(*arguments):
 return subprocess.check_output(['git','-C',str(base),'-c','core.hooksPath='+('NUL' if os.name=='nt' else '/dev/null'),'-c','core.autocrlf=false','-c','core.longpaths=true','-c','credential.helper=',*arguments],text=True,env=git_environment,timeout=180).strip()

if not base.exists():
 base.mkdir(parents=True)
 git('init','--initial-branch=main')
 git('fetch','--no-tags','--depth=1','--',args.clone_from,pin)
 git('checkout','--detach',pin)
if not (base/'.git').is_dir():raise RuntimeError('The owned source must have its own Git metadata.')
parent=base.parent
reuse=git('rev-parse','HEAD')!=pin
if reuse:
 prior=json.loads((parent/'adapter.json').read_text(encoding='utf-8'))
 if git('status','--porcelain') or git('rev-parse','HEAD')!=prior['adaptedSource'] or prior['originalSource']!=pin:
  raise RuntimeError('The existing owned adapted source identity differs.')
 for item in prior['changed']:
  if hashlib.sha256((base/item['path']).read_bytes()).hexdigest()!=item['sha256']:
   raise RuntimeError('The existing owned source bytes differ: '+item['path'])

if not reuse:
 if git('rev-parse','HEAD')!=pin or git('status','--porcelain'):raise RuntimeError('An unchanged owned pinned source copy is required.')
 changed=[]
 def edit(relative,transform):
  path=base/relative;original=path.read_bytes();source=original.decode('utf-8');updated=transform(source)
  if updated==source:raise RuntimeError('Expected adapter change missing: '+relative)
  path.write_text(updated,encoding='utf-8',newline='\n')
  changed.append({'path':relative,'originalSha256':hashlib.sha256(original).hexdigest(),'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
 def replace(source,old,new,count=1):
  if source.count(old)!=count:raise RuntimeError('Pinned recipe anchor differs: '+old[:80])
  return source.replace(old,new)

 def recipe(source):
  source=replace(source,'const repository = "iXab3r/t3code";','const repository = "'+repository+'";\nprocess.env.VIVARIUM_US013_PACKAGE_ONLY = "true";')
  source=re.sub(r'  node\(\[\n    "scripts/smoke-cli-archive.ts",.*?\n  \]\);\n','',source,flags=re.S)
  if 'scripts/smoke-cli-archive.ts' in source:raise RuntimeError('A built CLI launch remains in the recipe.')
  start=source.index('    run("dbus-run-session", [');end=source.index('\n    ]);',start)+len('\n    ]);')
  source=source[:start]+source[end:]
  start=source.index('  try {\n    node(\n      [\n        "scripts/smoke-desktop-app.ts"')
  end=source.index('  node(["apps/server/scripts/cli.ts", "build-exe", "--target", "win-x64"]);',start)
  source=source[:start]+'  message("publishArtifacts", "ci-out/launch-check/** => launch-check");\n'+source[end:]
  source=replace(source,'    run(appImage, ["--appimage-extract"], { cwd: temp, stdio: ["ignore", "ignore", "inherit"] });',
   '    await extractAppImageWithoutExecution(appImage, temp);')
  source=source.replace('repo:\\s*t3code\\s*','repo:\\s*vivarium-us013-e2e\\s*')
  source=source.replace('  const profile = NodePath.join(output, "smoke-profile");\n  await NodeFSP.mkdir(profile);\n','')
  source+=r"""

async function extractAppImageWithoutExecution(image, directory) {
  const input = await NodeFSP.open(image, "r");
  try {
    const size = (await input.stat()).size;
    const block = Buffer.alloc(65536);
    let overlap = Buffer.alloc(0);
    for (let position = 0; position < size;) {
      const { bytesRead } = await input.read(block, 0, block.length, position);
      if (!bytesRead) break;
      const bytes = Buffer.concat([overlap, block.subarray(0, bytesRead)]);
      for (let index = bytes.indexOf("hsqs"); index >= 0; index = bytes.indexOf("hsqs", index + 1)) {
        const offset = position - overlap.length + index;
        const header = Buffer.alloc(96);
        const read = await input.read(header, 0, header.length, offset);
        if (read.bytesRead !== 96 || header.readUInt16LE(28) !== 4) continue;
        const used = header.readBigUInt64LE(40);
        if (used <= 96n || used > BigInt(size - offset)) continue;
        run("unsquashfs", ["-o", String(offset), "-d", NodePath.join(directory, "squashfs-root"), image]);
        return;
      }
      overlap = Buffer.from(bytes.subarray(Math.max(0, bytes.length - 3)));
      position += bytesRead;
    }
    throw new Error("No bounded SquashFS payload found in the packaged AppImage.");
  } finally { await input.close(); }
}
"""
  if 'scripts/smoke-desktop-app.ts' in source or 'run(appImage,' in source:raise RuntimeError('A packaged application launch remains.')
  return source
 edit('scripts/personal-release/run.mjs',recipe)
 edit('scripts/personal-release/manifest.mjs',lambda s:s.replace('iXab3r/t3code',repository))
 edit('scripts/personal-release/manifest.test.mjs',lambda s:s.replace('iXab3r/t3code',repository))
 def windows(source):
  source=replace(source,"$env:VP_HOME = 'C:\\BuildAgent\\tools\\t3-vite-plus'","$env:VP_HOME = 'C:\\Users\\Xab3r\\AppData\\Local\\T3BuildTools\\vite-plus'")
  source=replace(source,"$env:PYTHON = 'C:\\Python311\\python.exe'","$env:PYTHON = 'C:\\Python313\\python.exe'")
  source=replace(source,"if (-not [Environment]::UserInteractive) { throw 'T3 desktop checks require an interactive agent.' }", "$env:VIVARIUM_US013_PACKAGE_ONLY = 'true'")
  return source
 edit('scripts/personal-release/windows.ps1',windows)
 def prepare(source):
  source=source.replace('xvfb xauth dbus-x11','squashfs-tools')
  source=replace(source,'node scripts/personal-release/run.mjs check-github','node scripts/personal-release/run.mjs check-github\nunset GITHUB_TOKEN\nexport VIVARIUM_US013_PACKAGE_ONLY=true')
  return source
 edit('scripts/personal-release/prepare.sh',prepare)
 def desktop(source):
  anchor='    yield* runCommand(\n      ChildProcess.make(\n        process.execPath,\n        // Clearing NODE_PATH'
  return replace(source,anchor,'    if (process.env.VIVARIUM_US013_PACKAGE_ONLY === "true") {\n      yield* Effect.log("Vivarium US-013 package-only: packaged server execution is excluded.");\n      return;\n    }\n\n'+anchor)
 edit('scripts/build-desktop-artifact.ts',desktop)
 def adapter_payload(name, expected):
  path=Path(__file__).resolve().parent/"publisher"/name;data=path.read_bytes()
  if hashlib.sha256(data).hexdigest()!=expected:raise RuntimeError("Publisher adapter input hash differs: "+name)
  return data.decode("utf-8")

 edit("scripts/personal-release/publish.sh",lambda source:adapter_payload("publish.sh",'0dfb54d704c57cdebfe25aaf2463059db04507310a429ff61ac6bc607049b91b'))
 relative="scripts/personal-release/publication-state.mjs";path=base/relative
 if path.exists():raise RuntimeError("Unexpected publisher helper exists.")
 path.write_text(adapter_payload("publication-state.mjs",'387569f1e9f7a777af355a3745fd3c58d6548ba5f582ead2e910d27682291faf'),encoding="utf-8",newline="\n")
 changed.append({"path":relative,"originalSha256":None,"sha256":hashlib.sha256(path.read_bytes()).hexdigest()})
 git("add","-N","--",relative)
 for relative in ['scripts/personal-release/run.mjs','scripts/personal-release/manifest.mjs']:
  subprocess.run(['node','--check',str(base/relative)],check=True)
 patch=subprocess.check_output(['git','-C',str(base),'-c','core.longpaths=true','diff','--binary'])
 parent=base.parent;(parent/'adapter.patch').write_bytes(patch)
 (parent/'adapter.json').write_text(json.dumps({'kind':'OWNED_T3_RECIPE_ADAPTER_NOT_BUILD_ACCEPTANCE','originalSource':pin,'repository':repository,'publicationTarget':target,'changed':changed,'patchSha256':hashlib.sha256(patch).hexdigest(),'excluded':['built GUI launches','built CLI/archive launches','packaged server execution probe','interactive Windows prerequisite'],'retained':['compile/package commands','NSIS/AppImage/CLI archives','static updater metadata','version/revision/hash manifest checks'],'remaining':['real GitHub publication/retry/recovery proof','source/trigger/counter integration','real build/package and publication']},indent=2)+'\n',encoding='utf-8')
 print(json.dumps({'adapter':str(parent/'adapter.json'),'changed':len(changed),'originalSource':pin}))

if not reuse:
 git('add','--',*[item['path'] for item in changed])
 git('-c','commit.gpgsign=false','-c','user.name=Owned Vivarium acceptance','-c','user.email=fixture@example.invalid','commit','-m','fixture: package T3 without app launch and prove owned publication recovery')
 git('checkout','-B','main','HEAD')
metadata=json.loads((parent/'adapter.json').read_text(encoding='utf-8'))
metadata['adaptedSource']=git('rev-parse','HEAD')
metadata['adaptedTree']=git('rev-parse','HEAD^{tree}')
metadata['sourceWorktreeClean']=not bool(git('status','--porcelain'))
metadata['preparationToolSha256']=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
patch=subprocess.check_output(['git','-C',str(base),'-c','core.longpaths=true','diff','--binary',pin,'HEAD'],env=git_environment)
(parent/'adapter.patch').write_bytes(patch)
metadata['patchSha256']=hashlib.sha256(patch).hexdigest()
metadata['publisherFaultBoundary']='Fixture-only exit75 after one verified real uploaded asset; actual Release wrapper resumes the partial draft and repeats published verification.'
(parent/'adapter.json').write_text(json.dumps(metadata,indent=2)+'\n',encoding='utf-8',newline='\n')
plan_file=Path(__file__).resolve().parent/'recipe-plan.json'
plan=json.loads(plan_file.read_text(encoding='utf-8'))
inputs={'kind':'OWNED_ACTUAL_T3_CONFIGURATION_INPUTS_NOT_EXECUTED_ACCEPTANCE','reference_plan_sha256':hashlib.sha256(plan_file.read_bytes()).hexdigest(),'original_source':pin,'adapted_source':metadata['adaptedSource'],'publisher_target':repository,'publication_target':target,'project_mapping':{'original':'T3Personal','fresh_fixture_identity':'P','fresh_fixture_display_name':'T3Personal (owned acceptance)'},'source':copy.deepcopy(plan['source']),'configurations':copy.deepcopy(plan['configurations'])}
inputs['source']['url']='OWNED_ANONYMOUS_SOURCE_URL'
inputs['source']['expectedRevision']=metadata['adaptedSource']
for config in inputs['configurations']:
 config['originalRequirements']=copy.deepcopy(config['requirements'])
 config['requirements']=[{'name':'system.os.family','value':'windows' if config['id'].endswith('_Windows') else 'linux'},{'name':'system.hostname','value':'OWNED_WINDOWS_HOSTNAME' if config['id'].endswith('_Windows') else 'OWNED_LINUX_HOSTNAME'}]
 for trigger in config['triggers']:
  trigger['rules']='-:README.md\n-:AGENTS.md\n-:docs/operations/**\n-:.teamcity/**'
  trigger['source_delta']='041345f2c201d68e718d5dbf0ba5ce9755f1704b'
(parent/'build-package-inputs.json').write_text(json.dumps(inputs,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps({'kind':'OWNED_FIXTURE_PREPARATION_NOT_EXECUTION','source':str(base),'adaptedSource':metadata['adaptedSource'],'changedFiles':len(metadata['changed']),'sourceClean':metadata['sourceWorktreeClean'],'reusedSource':reuse}))
