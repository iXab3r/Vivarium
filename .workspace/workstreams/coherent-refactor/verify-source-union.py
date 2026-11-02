"""Read-only byte/declaration checks for the finite P0 source census, not semantic normalization."""
import argparse
import csv
import hashlib
import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PIN = 'b158fe03eab3c31982a21b36c278f690f6b541d9'
CLI = ROOT/'artifacts/depot-refactor-study/cli-reference'

def sha(data): return hashlib.sha256(data).hexdigest()
def require(condition, message):
    if not condition: raise ValueError(message)
def command(repository, *args):
    result = subprocess.run(['git','--no-optional-locks','-C',str(repository),*args],capture_output=True,timeout=10,check=True)
    require(len(result.stdout)<=4*1024*1024, 'Source read exceeded byte bound')
    return result.stdout
def blob(repository, revision, path):
    require(re.fullmatch('[0-9a-f]{40}',revision) is not None, 'Invalid source revision')
    require(not Path(path).is_absolute() and '\\' not in path and '..' not in path.split('/'), 'Invalid source path')
    return command(repository,'show',revision+':'+path)

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--workstream',type=Path,default=Path(__file__).resolve().parent)
    args=parser.parse_args();work=args.workstream
    def table(name):
        with (work/name).open(encoding='utf8',newline='') as stream:return list(csv.DictReader(stream,delimiter='\t'))
    manifest=json.loads((work/'source-union-checkpoint.json').read_text(encoding='utf8'))
    require(manifest['cli_source_revision']==PIN,'Changed CLI pin needs a baseline change')
    require((CLI/'.git').exists(),'Pinned source is not a Git checkout')
    require(Path(command(CLI,'rev-parse','--show-toplevel').decode().strip()).resolve()==CLI.resolve(),'Git resolved to a parent checkout')
    require(command(CLI,'rev-parse','HEAD').decode().strip()==PIN,'Pinned CLI checkout revision differs')
    inputs=[]
    for item in manifest['cli_fixture_source_hashes']+manifest['cli_implementation_files']:
        data=blob(CLI,PIN,item['path'])
        require(sha(data)==item['sha256'],'CLI source hash differs: '+item['path'])
        inputs.append({'repository':'pinned-cli','revision':PIN,**item})

    schema_path=ROOT/'artifacts/p0-rest-first/reference/captured/schema.body'
    data=schema_path.read_bytes();require(len(data)<4*1024*1024,'Schema exceeded byte bound')
    schema=json.loads(data);schema_hash=sha(data)
    occurrences=table('installed-http-occurrences.tsv');seen=set()
    for row in occurrences:
        key=(row['method'].lower(),row['path'])
        require(key not in seen,'Duplicate installed operation occurrence');seen.add(key)
        require(row['schema_sha256']==schema_hash,'Installed schema hash differs')
        pointer='/paths/'+row['path'].replace('~','~0').replace('/','~1')+'/'+row['method'].lower()
        require(row['json_pointer']==pointer,'Changed installed operation pointer')
        operation=schema['paths'][row['path']][row['method'].lower()]
        require(operation.get('operationId','')==row['declared_operation_id'],'Changed installed operationId')
    actual={(method,path) for path,methods in schema['paths'].items() for method in methods
            if method in {'get','put','post','delete','patch','head','options','trace'}}
    require(seen==actual,'Installed source census omitted or invented operations')

    cache={}
    def source(row):
        key=(row['source_revision'],row['source'])
        if key not in cache:cache[key]=blob(ROOT,*key)
        data=cache[key]
        require(sha(data)==row['source_sha256'],'Native source hash differs: '+row['source'])
        lines=data.decode('utf8').splitlines();line=int(row['line'])
        require(0<line<=len(lines),'Declaration line outside source')
        return lines,line,lines[line-1]
    native=table('native-http-occurrences.tsv')
    require(len({r['source_id'] for r in native})==len(native),'Duplicate native declaration')
    for row in native:
        lines,index,_=source(row)
        declaration='\n'.join(lines[index-1:index+2])
        match=re.match(r'\s*\w+\.Map(Get|Post|Put|Delete|Patch|Head|Options)\(\s*"([^"]*)"',declaration)
        require(match is not None and match[1].upper()==row['method'] and row['path'].endswith(match[2]),'Native declaration method/route/line differs: '+row['source_id'])
    messages=table('agent-message-occurrences.tsv')
    require(len({r['source_id'] for r in messages})==len(messages),'Duplicate Agent field declaration')
    for row in messages:
        lines,index,line=source(row)
        require(re.search(r'\b'+re.escape(row['message_type'])+r'\s+'+re.escape(row['field'])+r'\s*=\s*'+re.escape(row['tag'])+r'\s*;',line) is not None,'Agent field/type/tag/line differs')
        enclosing=[re.match(r'message\s+(\w+)\s*\{',text) for text in lines[:index]]
        enclosing=[m[1] for m in enclosing if m]
        require(enclosing and enclosing[-1]==row['envelope'],'Agent field envelope differs')
    syntax=subprocess.run(['node',str(ROOT/'tools/teamcity-reference/inventory-client.mjs'),'--check','--output',str(work.resolve())],
        cwd=ROOT,capture_output=True,text=True,timeout=20)
    require(syntax.returncode==0,'Pinned CLI syntax census differs: '+syntax.stderr[-1000:])
    require(len(syntax.stdout)<65536,'Syntax census diagnostic bound exceeded')
    syntax=json.loads(syntax.stdout)
    require(syntax['source_revision']==PIN,'Syntax census source revision differs')
    for (revision,path),data in sorted(cache.items()):inputs.append({'repository':'vivarium','revision':revision,'path':path,'sha256':sha(data)})
    print(json.dumps({'result':'PASS','installed_operations':len(occurrences),'cli_fixture_sources':len(manifest['cli_fixture_source_hashes']),
        'cli_implementation_sources':len(manifest['cli_implementation_files']),'cli_syntax_census':syntax,
        'native_literal_declarations':len(native),'agent_envelope_fields':len(messages),
        'schema':{'path':schema_path.relative_to(ROOT).as_posix(),'sha256':schema_hash},'inputs':inputs,
        'limits':['Byte/declaration provenance only; no source-dispatch or semantic alias proof, inline-branch completeness, actor/effect oracle, normalized denominator or product conformance.',
        'Native suffix check does not prove group-prefix dispatch; source census excludes computed routes. Agent declarations do not prove processing effects.']}))

if __name__=='__main__':main()
