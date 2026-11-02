import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile, readdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const { createTeamCityCli } = await import(pathToFileURL(resolve('artifacts/p0-rest-first/reference/cli-acquisition/dist/src/cli.js')));

const area=resolve('artifacts/us013-templates-20261102');
const run=resolve(area,'run-'+randomUUID().replaceAll('-',''));await mkdir(run,{recursive:true});
const prefix='VivUs013Templates_20261102_'+run.split('run-').at(-1).slice(0,10);
const project=prefix+'_Project',job=project+'_Build',a=project+'_A',b=project+'_B';
const profile='vivarium-teamcity-lab-202613',target='http://127.0.0.1:18111';
const privateState=resolve('artifacts/p0-rest-first/teamcity-lab/cli-state');
if(resolve(process.env.APPDATA??'').toLowerCase()!==resolve(privateState,'roaming').toLowerCase()||
 resolve(process.env.LOCALAPPDATA??'').toLowerCase()!==resolve(privateState,'local').toLowerCase())throw Error('Private lab profile required.');
const sha=b=>createHash('sha256').update(b).digest('hex');
const script=await readFile(new URL(import.meta.url));await writeFile(resolve(run,'probe.mjs'),script);
const receipt={kind:'BOUNDED_TEMPLATE_REFERENCE_NOT_PRODUCT_CONFORMANCE',profile,target,prefix,project,job,templates:[a,b],
 sourcePin:'b158fe03eab3c31982a21b36c278f690f6b541d9',scriptSha256:sha(script),setup:[],cases:[],cleanup:[],observations:[],runtimeInputs:[]};
const acquisition=resolve('artifacts/p0-rest-first/reference/cli-acquisition');
for(const directory of ['src','dist/src','node_modules/@eyeauras/cli-factory/dist/src'])
 for(const path of (await readdir(resolve(acquisition,directory),{recursive:true})).filter(p=>/\.(?:js|ts)$/.test(p)).sort()){
  const full=resolve(acquisition,directory,path);receipt.runtimeInputs.push({path:full,sha256:sha(await readFile(full))});
 }
for(const path of ['package.json','package-lock.json','node_modules/@eyeauras/cli-factory/package.json']){
 const full=resolve(acquisition,path);receipt.runtimeInputs.push({path:full,sha256:sha(await readFile(full))});
}
let active,verified=false,createAttempted=false,cleanup=false;
const normalSignal=AbortSignal.timeout(240000);
const save=()=>writeFile(resolve(run,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');
const app=createTeamCityCli({fetch:async(input,init)=>{
 const request=new Request(input,init),url=new URL(request.url),method=request.method;
 if(url.origin!==target||url.username||url.password||url.hash)throw Error('Wrong target.');
 const body=['GET','HEAD'].includes(method)?undefined:await request.clone().text();
 const path=decodeURIComponent(url.pathname);
 const allowed=(method==='GET'&&['/app/rest/server','/app/rest/users/current','/app/rest/projects/id:'+project].includes(path))||
  (path==='/app/rest/projects'&&method==='POST'&&JSON.parse(body).id===project)||
  (path==='/app/rest/projects/id:'+project&&method==='DELETE')||
  (path==='/app/rest/buildTypes'&&method==='POST'&&JSON.parse(body).project?.id===project&&JSON.parse(body).id===job)||
  (path==='/app/rest/projects/id:'+project+'/templates'&&method==='POST'&&[a,b].includes(JSON.parse(body).id))||
  path.startsWith('/app/rest/projects/id:'+project+'/parameters')||
  [job,a,b].some(id=>path==='/app/rest/buildTypes/id:'+id||path.startsWith('/app/rest/buildTypes/id:'+id+'/'));
 if(!allowed||(method!=='GET'&&!verified))throw Error('Unowned request: '+method+' '+path);
 const headers={};for(const name of ['accept','content-type'])if(request.headers.has(name))headers[name]=request.headers.get(name);
 const exchange={actor:'admin',request:{method,path:url.pathname,query:Object.fromEntries(url.searchParams),headers,...(body===undefined?{}:{body})}};
 active?.exchanges.push(exchange);
 const response=await fetch(request,{redirect:'error',signal:cleanup?AbortSignal.timeout(120000):normalSignal});
 const bytes=Buffer.from(await response.clone().arrayBuffer());if(bytes.length>65536)throw Error('Entity bound exceeded.');
 const authorization=request.headers.get('authorization')?.replace(/^(?:Bearer|Basic) /,'');
 if(authorization&&authorization.length>8&&(bytes.toString().includes(authorization)||body?.includes(authorization)))throw Error('Credential entity export refused.');
 exchange.response={status:response.status,headers:{'content-type':response.headers.get('content-type')??''},bodyBase64:bytes.toString('base64'),bodyBytes:bytes.length,bodySha256:sha(bytes)};
 return response;
}});
const invoke=args=>app.execute(['-p',profile,'--json',...args],cleanup?AbortSignal.timeout(120000):normalSignal);
async function group(kind,id,action){active={id,exchanges:[]};receipt[kind].push(active);try{return await action();}catch(error){active.error=String(error);throw error;}finally{await save();}}
const param=(action,id,value)=>['jobs','parameters',action,id,'env.SHARED',...(value===undefined?[]:['--value',value])];
const observe=async label=>{
 const value=await invoke(param('show',job));const templates=await invoke(['jobs','templates','list',job]);
 receipt.observations.push({label,value,templates});
};
let initial, failure,restoreFailure;
try{
 await group('setup','verify-owned-absence-and-reference',async()=>{
  initial=await invoke(['permissions','list']);
  if(initial.some(p=>['Update','Admin','Credentials'].includes(p.name)&&p.enabled)||!initial.find(p=>p.name==='ReadOnly')?.enabled)throw Error('Unexpected gates.');
  const server=await invoke(['server','status']);if(server.version!=='2026.1.3 (build 222742)'||String(server.buildNumber)!=='222742')throw Error('Wrong reference version.');
  try{await invoke(['projects','show',project]);throw Error('Owned target already exists.');}catch(error){if(error.status!==404)throw error;}
  verified=true;await invoke(['permissions','grant','Update']);
 });
 await group('setup','create-owned-templates-and-consumer',async()=>{
  createAttempted=true;await invoke(['projects','create',project,'--name','Owned template proof']);
  await invoke(['jobs','create',job,'--name','Owned template consumer','--project',project]);
  for(const id of [a,b])await invoke(['projects','templates','create',project,id,'--name',id]);
  await invoke(param('create',a,'A'));await invoke(param('create',b,'B'));
 });
 await group('cases','template-a-before-b',async()=>{
  await invoke(['jobs','templates','replace-all',job,'--template',a,'--template',b]);await observe('A then B');
 });
 await group('cases','template-b-before-a',async()=>{
  await invoke(['jobs','templates','replace-all',job,'--template',b,'--template',a]);await observe('B then A');
 });
 await group('cases','local-override-and-reset',async()=>{
  await invoke(param('set',job,'LOCAL'));await observe('Local override');
  await invoke(param('delete',job));await observe('Local reset');
 });
 await group('cases','template-parameter-change-propagates',async()=>{
  await invoke(param('set',b,'B2'));await observe('Changed first-priority template');
 });
 await group('cases','project-default-overrides-templates',async()=>{
  await invoke(['projects','parameters','create',project,'env.SHARED','--value','PROJECT']);await observe('Project default');
 });
 await group('cases','project-default-survives-template-reorder',async()=>{
  await invoke(['jobs','templates','replace-all',job,'--template',a,'--template',b]);await observe('Project before reordered templates');
 });
 await group('cases','local-override-and-project-reset',async()=>{
  await invoke(param('set',job,'LOCAL'));await observe('Local before project and templates');
  await invoke(param('delete',job));await observe('Reset restores project');
 });
 receipt.complete=true;
}catch(error){failure=String(error);receipt.failure=failure;process.exitCode=1;}
finally{
 cleanup=true;
 try{if(verified&&createAttempted)await group('cleanup','delete-owned-project',async()=>{
  await invoke(['permissions','grant','Update']);await invoke(['projects','delete',project]);
  try{await invoke(['projects','show',project]);throw Error('Owned project remains.');}catch(error){if(error.status!==404)throw error;}
 });}catch(error){receipt.cleanupFailure=String(error);process.exitCode=1;}
 if(initial)for(const gate of ['Update','Admin','Credentials'])try{
  await invoke(['permissions',initial.find(p=>p.name===gate)?.enabled?'grant':'revoke',gate]);
 }catch(error){restoreFailure=String(error);process.exitCode=1;}
 try{receipt.initialPermissions=initial;receipt.finalPermissions=await invoke(['permissions','list']);
  if(JSON.stringify(initial)!==JSON.stringify(receipt.finalPermissions))throw Error('Gate restoration differs.');
 }catch(error){restoreFailure=String(error);process.exitCode=1;}
 if(restoreFailure)receipt.restoreFailure=restoreFailure;
 await save();await app.dispose();
}
console.log(JSON.stringify({receipt:resolve(run,'receipt.json'),complete:receipt.complete??false,failure,cleanupFailure:receipt.cleanupFailure,restoreFailure,observations:receipt.observations}));
process.exitCode=receipt.complete&&!receipt.failure&&!receipt.cleanupFailure&&!receipt.restoreFailure?0:1;
