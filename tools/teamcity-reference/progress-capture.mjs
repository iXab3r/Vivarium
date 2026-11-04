import assert from 'node:assert/strict';
import {createHash, randomBytes, randomUUID} from 'node:crypto';
import {execFileSync} from 'node:child_process';
import {mkdir, readFile, readdir, writeFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {setTimeout as pause} from 'node:timers/promises';

// Additional R0 progress oracle: literal running-info snapshots, request/response times and terminal stage statistics.
// Completion: actual successful processes and identical P/Q admin bytes, P actor
// bytes, literal denials with unchanged state, token revoke, owned cleanup/gates.
const area=resolve('artifacts/us013-r0-progress-20261104');
const run=resolve(area,'run-'+randomUUID().replaceAll('-',''));
await mkdir(run,{recursive:true});
await writeFile(resolve(area,'latest-run.txt'),run+'\n');
const suffix=run.split('run-').at(-1).slice(0,10),prefix='VivUs013Progress_20261104_'+suffix;
const project=prefix+'_P',protectedProject=prefix+'_Q',job=project+'_Smoke',protectedJob=protectedProject+'_Smoke';
const username=(prefix+'_User').toLowerCase(),tokenName=prefix+'_Token';
const target='http://127.0.0.1:18111',profile='vivarium-teamcity-lab-202613';
const server='vivarium-p0-rest-teamcity-2026-1-3';
const serverId='a98f4443f2a5cddbe614fb442e4e8d644ce5a92ebc7bf085c40df51f1cb89cce';
const serverImage='sha256:e62c922b94edbef8536bb0d514a193a38bd480fd3855fdac7a649e0a5f8a550a';
const agentImage='jetbrains/teamcity-minimal-agent@sha256:f711ef16fd62a46bc6e5d3e07f0fcd0eaf7c3c8f905fc4500135a58d5ad05765';
const owner='us013-r0-progress-20261104',network='vivarium-us013-progress-net-'+suffix;
const container='vivarium-us013-progress-agent-'+suffix,agentName=prefix+'_Agent';
const state=resolve('artifacts/p0-rest-first/teamcity-lab/cli-state');
for(const [key,leaf] of [['APPDATA','roaming'],['LOCALAPPDATA','local']])
 assert.equal(resolve(process.env[key]??'').toLowerCase(),resolve(state,leaf).toLowerCase());
const clientRoot=resolve('artifacts/us013-r0-access-20261102/client/node_modules/@eyeauras/teamcity-cli');
const {createTeamCityCli}=await import(pathToFileURL(resolve(clientRoot,'dist/src/index.js')));
const {TeamCityClient}=await import(pathToFileURL(resolve(clientRoot,'dist/src/client.js')));
const {issuedTokenKey}=await import(pathToFileURL(resolve(clientRoot,'dist/src/admin-models.js')));
const sha=bytes=>createHash('sha256').update(bytes).digest('hex');
const script=await readFile(new URL(import.meta.url));
await writeFile(resolve(run,'probe.mjs'),script);
const receipt={kind:'BOUNDED_R0_EXECUTION_PROGRESS_REFERENCE_NOT_PRODUCT_CONFORMANCE',
 profile,target,prefix,project,protectedProject,job,protectedJob,username,tokenName,
 observedAtUtc:new Date().toISOString(),cliVersion:'0.5.6',cliSource:'c0159333508e8cbd417107cf60d2bb2efee882af',
 sourceSha256:sha(script),runtimeInputs:[],setup:[],privateSetup:[],cases:[],cleanup:[],observations:[],
 resources:{owner,network,container,agentName,agentImage,serverId},
 acquisitionVariant:'Published CLI 0.5.6 acquires only the private lab administrator context. Published TeamCityClient performs owned requests. Private Basic bootstrap only issues one scoped PAT; credential entities are omitted. Fixed byte GET follows the same pinned file path/query/Accept as client download. Not an unchanged CLI story acceptance.',
 limits:['All Users inherited rights are observed, not changed. Scoped PAT restrictions bound P and Agent controls; this is not proof of target OIDC/JWT, permission withdrawal or a new authorization policy.','One owned Linux Agent and simpleRunner process, not Windows/T3 or production Vivarium.']};
for(const name of ['teamcity-cli','cli-factory']){
 const dir=resolve('artifacts/us013-r0-access-20261102/client/node_modules/@eyeauras',name);
 const pkg=JSON.parse(await readFile(resolve(dir,'package.json'),'utf8'));assert.equal(pkg.version,'0.5.6');
 for(const path of ['package.json',...(await readdir(resolve(dir,'dist/src'),{recursive:true})).filter(p=>p.endsWith('.js')).map(p=>'dist/src/'+p)].sort())
  receipt.runtimeInputs.push({package:name,path,sha256:sha(await readFile(resolve(dir,path)))});
}
const docker=(...args)=>execFileSync('docker',args,{encoding:'utf8',timeout:120000,windowsHide:true,stdio:['ignore','pipe','pipe']}).trim();
const secrets=[],secretStore=new Map(),scopedSecrets={get:async k=>secretStore.get(k),set:async(k,v)=>{secretStore.set(k,v);secrets.push(v);},delete:async k=>secretStore.delete(k)};
const password=randomBytes(32).toString('base64url');secrets.push(password);
const builds=new Map();let active,adminToken,admin,actor,userId,agentId,initial,verified=false,cleanup=false;
let networkCreated=false,serverConnected=false,agentCreated=false;
const signal=AbortSignal.timeout(600000);
const safeError=error=>typeof error?.status==='number'?'HTTP '+error.status:error?.name+': '+String(error?.message??error).replace(/Bearer\s+\S+/g,'Bearer [omitted]');
async function save(){const data=JSON.stringify(receipt,null,2)+'\n';if(secrets.some(s=>s.length>8&&data.includes(s)))throw Error('Secret export guard.');await writeFile(resolve(run,'receipt.json'),data);}
async function group(kind,id,action){active={id,exchanges:[]};receipt[kind].push(active);try{return await action();}catch(e){active.error=safeError(e);throw e;}finally{await save();}}
function allowed(path,method,body){
 if(method==='GET'&&['/app/rest/server','/app/rest/users/current','/app/rest/projects','/app/rest/agents','/app/rest/buildQueue','/app/rest/users','/app/rest/userGroups/key:ALL_USERS_GROUP/roles'].includes(path))return true;
 if(path==='/app/rest/users'&&method==='POST')return JSON.parse(body).username===username;
 if(path==='/app/rest/users/current/tokens'&&method==='POST')return JSON.parse(body).name===tokenName;
 if(path==='/app/rest/users/current/tokens/'+tokenName&&method==='DELETE')return true;
 if(userId&&path==='/app/rest/users/id:'+userId+'/tokens/'+tokenName&&method==='DELETE')return true;
 if(userId&&path==='/app/rest/users/id:'+userId&&['GET','DELETE'].includes(method))return true;
 if(userId&&path==='/app/rest/users/id:'+userId+'/roles/PROJECT_ADMIN/p:'+project&&['PUT','DELETE'].includes(method))return true;
 if(path==='/app/rest/projects'&&method==='POST')return [project,protectedProject].includes(JSON.parse(body).id);
 if([project,protectedProject].some(id=>path==='/app/rest/projects/id:'+id)&&['GET','DELETE'].includes(method))return true;
 if(path==='/app/rest/buildTypes'&&method==='POST'){const b=JSON.parse(body);return b.id===job&&b.project?.id===project||b.id===protectedJob&&b.project?.id===protectedProject;}
 if([job,protectedJob].some(id=>path==='/app/rest/buildTypes/id:'+id)&&method==='GET')return true;
 if([job,protectedJob].some(id=>path==='/app/rest/buildTypes/id:'+id+'/steps')&&method==='PUT')return true;
 if(path==='/app/rest/buildQueue'&&method==='POST')return [job,protectedJob].includes(JSON.parse(body).buildType?.id);
 if([...builds.keys()].some(id=>['/app/rest/builds/id:'+id,'/app/rest/buildQueue/id:'+id].some(base=>path===base||path.startsWith(base+'/')))&&['GET','POST'].includes(method))return true;
 if(agentId&&['','/enabledInfo','/authorizedInfo'].some(end=>path==='/app/rest/agents/id:'+agentId+end)&&['GET','PUT','DELETE'].includes(method))return true;
 return false;
}
const transport=actorName=>async(input,init)=>{
 let request=new Request(input,init),url=new URL(request.url),body=['GET','HEAD'].includes(request.method)?undefined:await request.clone().text();
 assert.equal(url.origin,target);assert.ok(!url.username&&!url.password&&!url.hash);
 const path=decodeURIComponent(url.pathname);
 if(!allowed(path,request.method,body)||request.method!=='GET'&&!verified)throw Error('Unowned request rejected: '+request.method+' '+path);
 const privateExchange=request.method==='POST'&&(path==='/app/rest/users'||path==='/app/rest/users/current/tokens');
 if(path==='/app/rest/users'&&request.method==='POST'){body=JSON.stringify({...JSON.parse(body),password});request=new Request(request,{body});}
 if(actorName==='owned-basic-bootstrap'){
  assert.equal(path,'/app/rest/users/current/tokens');assert.equal(request.method,'POST');
  const headers=new Headers(request.headers);headers.set('Authorization','Basic '+Buffer.from(username+':'+password).toString('base64'));request=new Request(request,{headers});
 }
 const requestStartedAtUtc=new Date().toISOString();
 const response=await fetch(request,{redirect:'error',signal:cleanup?AbortSignal.timeout(120000):signal});
 if(privateExchange){receipt.privateSetup.push({actor:actorName,method:request.method,path:url.pathname,status:response.status,entityExport:'OMITTED_PRIVATE_CREDENTIAL_SETUP'});return response;}
 const bytes=Buffer.from(await response.clone().arrayBuffer());assert.ok(bytes.length<=262144);
 if(secrets.some(s=>s.length>8&&(bytes.toString().includes(s)||body?.includes(s))))throw Error('Secret entity rejected.');
 const selected=headers=>Object.fromEntries(['accept','content-type'].flatMap(k=>headers.has(k)?[[k,headers.get(k)]]:[]));
 active?.exchanges.push({requestStartedAtUtc,responseCompletedAtUtc:new Date().toISOString(),actor:actorName,request:{method:request.method,path:url.pathname,query:Object.fromEntries(url.searchParams),headers:selected(request.headers),...(body===undefined?{}:{body}),bodyBase64:Buffer.from(body??'').toString('base64'),bodyBytes:Buffer.byteLength(body??''),bodySha256:sha(body??'')},response:{status:response.status,headers:{'content-type':response.headers.get('content-type')??''},bodyBase64:bytes.toString('base64'),bodyBytes:bytes.length,bodySha256:sha(bytes)}});
 return response;
};
const app=createTeamCityCli({fetch:async(input,init)=>{
 const req=new Request(input,init);assert.equal(new URL(req.url).origin,target);
 if(req.headers.has('authorization')){const value=req.headers.get('authorization');assert.ok(value.startsWith('Bearer '));adminToken=value.slice(7);if(!secrets.includes(adminToken))secrets.push(adminToken);}
 return transport('admin')(req);
}});
const invoke=args=>app.execute(['-p',profile,'--json',...args],cleanup?AbortSignal.timeout(120000):signal);
async function attempt(action){try{return {returned:true,result:await action()};}catch(e){if(typeof e?.status!=='number')throw e;return {returned:false,status:e.status};}}
async function absent(id){const found=await attempt(()=>admin.getProject(id));assert.equal(found.status,404);}
const status=(kind,value)=>admin.setAgentStatus(agentId,kind,value,'Owned R0 reference fixture');
async function finished(id){for(let i=0;i<160;i++){
 const b=await admin.getBuild(id);assert.equal(b.buildTypeId,builds.get(id));
 if(!cleanup&&b.state==='running'){
  const url=new URL(target+'/app/rest/builds/id:'+id);
  url.searchParams.set('fields','id,state,status,statusText,startDate,canceledInfo(timestamp),percentageComplete,running-info(*)');
  const r=await transport('admin')(url,{headers:{Authorization:'Bearer '+adminToken,Accept:'application/json'}});assert.equal(r.status,200);
 }
 if(b.state==='finished'){
  if(!cleanup){const r=await transport('admin')(target+'/app/rest/builds/id:'+id+'/statistics',{headers:{Authorization:'Bearer '+adminToken,Accept:'application/json'}});assert.equal(r.status,200);}
  return b;
 }
 await pause(500);
}throw Error('Owned Build completion deadline.');}
async function queue(client,id){const b=await client.runJob(id);assert.ok(Number.isSafeInteger(b.id)&&b.id>0);assert.equal(b.buildTypeId,id);builds.set(b.id,id);return b.id;}
async function bytes(clientName,token,id){const url=new URL(target+'/app/rest/builds/id:'+id+'/artifacts/files/smoke.txt');url.searchParams.set('resolveParameters','false');url.searchParams.set('logBuildUsage','false');const r=await transport(clientName)(url,{headers:{Authorization:'Bearer '+token,Accept:'*/*'}});return {status:r.status,bytes:Buffer.from(await r.arrayBuffer())};}
try{
 await group('setup','fixed-server-empty-agent-queue-and-catalog-preflight',async()=>{
  initial=await invoke(['permissions','list']);assert.ok(initial.find(p=>p.name==='ReadOnly')?.enabled);assert.ok(!initial.some(p=>['Update','Admin','Credentials'].includes(p.name)&&p.enabled));
  const s=await invoke(['server','status']);assert.equal(s.version,'2026.1.3 (build 222742)');assert.equal(String(s.buildNumber),'222742');
  for(const args of [['agents','list','--limit','1'],['queue','list','--limit','1']]){const r=await invoke(args);assert.equal(r.items.length,0);assert.ok(!r.hasMore);}
  const projects=await invoke(['projects','list','--limit','10']);assert.ok(!projects.hasMore&&projects.items.every(p=>p.id==='_Root'));
  const found=JSON.parse(docker('inspect',server))[0];assert.equal(found.Id,serverId);assert.equal(found.Image,serverImage);
  assert.ok(docker('exec',server,'sha256sum','/opt/teamcity/webapps/ROOT/WEB-INF/lib/server-core.jar').startsWith('24e812063c7a4ed3c14723f4e53a2d1277baaf05c8506d91a6db540d8711a665 '));
  assert.ok(JSON.parse(docker('image','inspect',agentImage))[0].RepoDigests.includes(agentImage));
  admin=new TeamCityClient({baseUrl:target,token:adminToken,fetch:transport('admin'),signal});await absent(project);await absent(protectedProject);
  verified=true;for(const gate of ['Update','Admin','Credentials'])await invoke(['permissions','grant',gate]);
 });
 await group('setup','owned-projects-native-process-recipes-and-ordinary-scoped-pat',async()=>{
  receipt.inheritedAllUsersRoles=await admin.listAccountRoles('groups','ALL_USERS_GROUP');
  for(const [p,j]of[[project,job],[protectedProject,protectedJob]]){
   await admin.createProject(p,{name:'Owned R0 access '+(p===project?'P':'Q')});await admin.createJob(j,{name:'LocalSmoke',project:p});
   await admin.replaceAllSettings('steps',j,[{name:'Owned byte producer',type:'simpleRunner',properties:[{name:'script.content',value:'#!/bin/sh\nset -eu\necho "owned-r0-process-start:${MODE:-success}"\ncase "${MODE:-success}" in fail) exit 7;; long) sleep 30;; esac\nprintf "owned-r0-smoke\\n" > smoke.txt\necho "##teamcity[publishArtifacts \'smoke.txt\']"\necho owned-r0-process\n'},{name:'use.custom.script',value:'true'}]}]);
  }
  const user=await admin.createAccountUser(username,'Owned R0 scoped run actor');userId=String(user.id);assert.match(userId,/^\d+$/);receipt.userId=userId;
  await admin.grantAccountRole('users',userId,{roleId:'PROJECT_ADMIN',project});
  const bootstrap=new TeamCityClient({baseUrl:target,token:'private-bootstrap-placeholder',fetch:transport('owned-basic-bootstrap'),signal});
  await bootstrap.createCurrentUserToken({name:tokenName,alias:tokenName,expires:new Date(Date.now()+3600000).toISOString().replace(/[-:]/g,'').replace(/\.\d{3}Z$/,'+0000'),restrictions:['view_project','run_build','view_file_content','view_build_runtime_data','customize_build_parameters','cancel_build'].map(permission=>({permission,project}))},scopedSecrets);
  const stored=JSON.parse(secretStore.get(issuedTokenKey(tokenName)));assert.equal(stored.name,tokenName);secrets.push(stored.value);
  actor=new TeamCityClient({baseUrl:target,token:stored.value,fetch:transport('owned-scoped-pat'),signal});receipt.actorCredentialFingerprint=sha(stored.value);
 });
 await group('setup','owned-real-linux-agent',async()=>{
  docker('network','create','--internal','--label','vivarium.owner='+owner,network);networkCreated=true;
  docker('network','connect',network,server);serverConnected=true;
  docker('run','--detach','--name',container,'--label','vivarium.owner='+owner,'--network',network,'--cpus','1','--memory','1g','--env','SERVER_URL=http://'+server+':8111','--env','AGENT_NAME='+agentName,agentImage);agentCreated=true;
  for(let i=0;i<180;i++){const a=await invoke(['agents','list','--connected','any','--enabled','any','--authorized','any','--limit','10']);assert.ok(!a.hasMore&&a.items.every(a=>a.name===agentName));const found=a.items.find(a=>a.name===agentName&&a.connected);if(found){agentId=found.id;receipt.agentId=agentId;break;}await pause(500);}
  assert.ok(agentId);await status('enabled',true);await status('authorized',true);
 });
 let pBuild,qBuild;
 await group('cases','scoped-actor-runs-real-build-and-downloads-produced-bytes',async()=>{
  const current=await actor.currentUser();assert.equal(String(current.id),userId);assert.equal(current.username,username);await actor.getJob(job);
  pBuild=await queue(actor,job);receipt.allowedBuildId=pBuild;const b=await finished(pBuild);assert.equal(b.status,'SUCCESS');
  await actor.getBuild(pBuild);await actor.listFiles('builds',String(pBuild),undefined,10);
  const credential=JSON.parse(secretStore.get(issuedTokenKey(tokenName))).value;
  const own=await bytes('owned-scoped-pat',credential,pBuild),control=await bytes('admin',adminToken,pBuild);
  assert.equal(own.status,200);assert.equal(control.status,200);assert.equal(own.bytes.toString(),'owned-r0-smoke\n');assert.deepEqual(own.bytes,control.bytes);
  receipt.allowedArtifact={bytes:own.bytes.length,sha256:sha(own.bytes)};
 });
 await group('cases','protected-sibling-has-real-bytes-but-denies-same-actor',async()=>{
  qBuild=await queue(admin,protectedJob);receipt.protectedBuildId=qBuild;assert.equal((await finished(qBuild)).status,'SUCCESS');
  const control=await bytes('admin',adminToken,qBuild);assert.equal(control.status,200);assert.equal(control.bytes.toString(),'owned-r0-smoke\n');
  receipt.protectedJobRead=await attempt(()=>actor.getJob(protectedJob));receipt.protectedRun=await attempt(()=>actor.runJob(protectedJob));receipt.protectedBuildRead=await attempt(()=>actor.getBuild(qBuild));
  const denied=await bytes('owned-scoped-pat',JSON.parse(secretStore.get(issuedTokenKey(tokenName))).value,qBuild);receipt.protectedDownloadStatus=denied.status;
  assert.ok(denied.status>=400);assert.ok(!denied.bytes.includes(control.bytes));
  assert.ok(!receipt.protectedJobRead.returned&&!receipt.protectedRun.returned&&!receipt.protectedBuildRead.returned);
  const still=await bytes('admin',adminToken,qBuild);assert.deepEqual(still.bytes,control.bytes);
  await actor.getJob(job);assert.equal((await bytes('owned-scoped-pat',JSON.parse(secretStore.get(issuedTokenKey(tokenName))).value,pBuild)).status,200);
 });
 await group('cases','project-actor-cannot-disable-owned-agent',async()=>{
  const before=await admin.getAgentStatus(agentId,'enabled');assert.equal(before.status,true);
  receipt.agentControl=await attempt(()=>actor.setAgentStatus(agentId,'enabled',false,'Denied project actor control'));
  assert.ok(!receipt.agentControl.returned);const after=await admin.getAgentStatus(agentId,'enabled');assert.equal(after.status,true);
 });
 await group('cases','same-scoped-actor-real-nonzero-exit',async()=>{
  const build=await actor.runJob(job,{properties:[{name:'env.MODE',value:'fail'}]});builds.set(build.id,job);receipt.failureBuildId=build.id;
  const terminal=await finished(build.id);assert.equal(terminal.status,'FAILURE');await actor.getBuild(build.id);
 });
 await group('cases','same-scoped-actor-queued-cancel-and-retained-history',async()=>{
  await status('enabled',false);const id=await queue(actor,job);receipt.queuedCancelBuildId=id;
  assert.equal((await actor.getBuild(id)).state,'queued');await actor.cancelQueuedBuild(id);
  const terminal=await finished(id);assert.equal(terminal.status,'UNKNOWN');await actor.getBuild(id);await status('enabled',true);
 });
 await group('cases','same-scoped-actor-running-cancel-and-retained-history',async()=>{
  const build=await actor.runJob(job,{properties:[{name:'env.MODE',value:'long'}]});builds.set(build.id,job);receipt.runningCancelBuildId=build.id;
  let running=false;for(let i=0;i<120;i++){const b=await actor.getBuild(build.id);if(b.state==='running'){running=true;break;}if(b.state==='finished')throw Error('Long process finished before running cancellation.');await pause(500);}assert.ok(running);
  await actor.cancelBuild(build.id);const terminal=await finished(build.id);assert.equal(terminal.status,'UNKNOWN');await actor.getBuild(build.id);
 });
 await group('cases','revoked-same-pat-cannot-read-build-or-download-existing-bytes',async()=>{
  const credential=JSON.parse(secretStore.get(issuedTokenKey(tokenName))).value;
  receipt.selfRevoke=await attempt(()=>actor.deleteCurrentUserToken(tokenName,scopedSecrets,tokenName));
  assert.equal(receipt.selfRevoke.status,403);
  const revoked=await transport('admin')(target+'/app/rest/users/id:'+userId+'/tokens/'+tokenName,{method:'DELETE',headers:{Authorization:'Bearer '+adminToken,Accept:'application/json'}});
  assert.ok(revoked.ok);secretStore.delete(issuedTokenKey(tokenName));
  receipt.revokedBuild=await attempt(()=>actor.getBuild(pBuild));const denied=await bytes('owned-scoped-pat',credential,pBuild);receipt.revokedDownloadStatus=denied.status;
  assert.ok(!receipt.revokedBuild.returned&&denied.status>=400);assert.ok(!denied.bytes.includes(Buffer.from('owned-r0-smoke\n')));
  assert.equal((await bytes('admin',adminToken,pBuild)).status,200);
 });
}catch(e){receipt.failure=safeError(e);}
finally{
 cleanup=true;
 try{if(verified&&admin)await group('cleanup','remove-only-owned-reference-objects',async()=>{
  for(const id of builds.keys()){const b=await admin.getBuild(id);if(b.state==='queued')await admin.cancelQueuedBuild(id);else if(b.state==='running')await admin.cancelBuild(id);await finished(id);}
  if(agentId){await status('enabled',false);await status('authorized',false);}
  if(!userId){const users=await admin.listAccountUsers({limit:100});assert.ok(!users.hasMore);userId=String(users.items.find(u=>u.username===username)?.id??'');}
  if(userId)await admin.deleteAccountUser(userId);
  secretStore.clear();
  for(const id of[project,protectedProject]){const found=await attempt(()=>admin.getProject(id));if(found.returned)await admin.deleteProject(id);else assert.equal(found.status,404);await absent(id);}
 });}catch(e){receipt.cleanupFailure=safeError(e);}
 try{
  if(agentCreated){assert.equal(JSON.parse(docker('inspect',container))[0].Config.Labels['vivarium.owner'],owner);docker('stop','--time','20',container);docker('rm','--volumes',container);}
  if(agentId){for(let i=0;i<120;i++){const a=await invoke(['agents','show',String(agentId)]);assert.equal(a.name,agentName);if(!a.connected)break;if(i===119)throw Error('Agent disconnect deadline.');await pause(500);}await invoke(['agents','delete',String(agentId)]);}
  if(serverConnected){assert.equal(JSON.parse(docker('inspect',server))[0].Id,serverId);docker('network','disconnect',network,server);}
  if(networkCreated){assert.equal(JSON.parse(docker('network','inspect',network))[0].Labels['vivarium.owner'],owner);docker('network','rm',network);}
 }catch(e){receipt.resourceCleanupFailure=safeError(e);}
 if(initial)try{for(const g of['Update','Admin','Credentials'])await invoke(['permissions',initial.find(p=>p.name===g)?.enabled?'grant':'revoke',g]);receipt.finalPermissions=await invoke(['permissions','list']);assert.deepEqual(receipt.finalPermissions,initial);}catch(e){receipt.restoreFailure=safeError(e);}
 await app.dispose();receipt.localSecretsCleared=secretStore.size===0;receipt.complete=!receipt.failure&&!receipt.cleanupFailure&&!receipt.resourceCleanupFailure&&!receipt.restoreFailure&&receipt.localSecretsCleared;await save();
}
console.log(JSON.stringify({receipt:resolve(run,'receipt.json'),complete:receipt.complete,failure:receipt.failure,cleanupFailure:receipt.cleanupFailure,resourceCleanupFailure:receipt.resourceCleanupFailure,restoreFailure:receipt.restoreFailure,cases:receipt.cases.map(c=>({id:c.id,exchanges:c.exchanges.length}))}));
process.exitCode=receipt.complete?0:1;
