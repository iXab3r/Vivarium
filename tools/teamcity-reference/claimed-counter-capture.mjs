import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as pause } from 'node:timers/promises';
import { execFileSync } from 'node:child_process';
const acquisition=resolve('artifacts/p0-rest-first/reference/cli-acquisition');
const {createTeamCityCli}=await import(pathToFileURL(resolve(acquisition,'dist/src/cli.js')));
const {TeamCityClient}=await import(pathToFileURL(resolve(acquisition,'dist/src/client.js')));
const {readIssuedToken}=await import(pathToFileURL(resolve(acquisition,'dist/src/admin-models.js')));
const profile='vivarium-teamcity-lab-202613',target='http://127.0.0.1:18111';
const privateState=resolve('artifacts/p0-rest-first/teamcity-lab/cli-state');
if(resolve(process.env.APPDATA??'').toLowerCase()!==resolve(privateState,'roaming').toLowerCase()||resolve(process.env.LOCALAPPDATA??'').toLowerCase()!==resolve(privateState,'local').toLowerCase())throw Error('Private lab profile required.');
const suffix=randomUUID().replaceAll('-','').slice(0,12),prefix='VivUs013ClaimedCounter_20261103_'+suffix;
const project=prefix+'_Project',job=project+'_Build',username=prefix.toLowerCase()+'_user',tokenName='QueuedAuth_'+suffix;
const run=resolve('artifacts/r0-host/claimed-counter-'+suffix);await mkdir(run,{recursive:true});
const owner='us013-claimed-counter-20261103',network='vivarium-us013-claimed-net-'+suffix,agentContainer='vivarium-us013-claimed-agent-'+suffix,agentName=prefix+'_Agent';
const server='vivarium-p0-rest-teamcity-2026-1-3',serverId='a98f4443f2a5cddbe614fb442e4e8d644ce5a92ebc7bf085c40df51f1cb89cce';
const serverImage='sha256:e62c922b94edbef8536bb0d514a193a38bd480fd3855fdac7a649e0a5f8a550a';
const agentImage='jetbrains/teamcity-minimal-agent@sha256:f711ef16fd62a46bc6e5d3e07f0fcd0eaf7c3c8f905fc4500135a58d5ad05765';
const docker=(...args)=>execFileSync('docker',args,{encoding:'utf8',timeout:120000,windowsHide:true,stdio:['ignore','pipe','pipe']}).trim();
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const source=await readFile(new URL(import.meta.url));await writeFile(resolve(run,'probe.mjs'),source);
const receipt={kind:'BOUNDED_PAUSED_OWNED_AGENT_COUNTER_REFERENCE_NOT_PRODUCT_CONFORMANCE',profile,target,prefix,project,job,username,sourceSha256:hash(source),setup:[],cases:[],cleanup:[],observations:[],resources:{owner,network,agentContainer,agentName,serverId,serverImage,agentImage},limits:'One newly owned Linux Agent is deliberately paused for a bounded observation window. Only its own project/builds are cancelled by the administrator; no role withdrawal. Ordinary account admission, actual state/counter observations, then one successful control. No universal assignment/ack timing inference when the captured state remains inconclusive.'};
let initial,active,verified=false,adminAuthorization,actorToken,userId,agentId,buildId,createdProject=false,createdUser=false,networkAttempted=false,connected=false,agentAttempted=false,cleanup=false,allUsersWithdrawn=false,adminUserId,initialAuthentication,authenticationChanged=false,agentPaused=false;
const password=randomUUID()+randomUUID(),secretValues=[password],deadline=AbortSignal.timeout(240000);
const save=()=>writeFile(resolve(run,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');
const memory=new Map(),secrets={get:async key=>memory.get(key),set:async(key,value)=>memory.set(key,value),delete:async key=>memory.delete(key)};
function allowed(path,method,body){
 if(path==='/app/rest/server/authSettings'&&method==='GET')return true;
 if(path==='/app/rest/server/authSettings'&&method==='PUT'&&initialAuthentication){const value=JSON.parse(body);return JSON.stringify(value)===JSON.stringify(initialAuthentication)||JSON.stringify(value)===JSON.stringify({...initialAuthentication,perProjectPermissions:true});}
 if(adminUserId&&path==='/app/rest/users/id:'+adminUserId+'/roles'&&method==='GET')return true;
 if(path==='/app/rest/userGroups/key:ALL_USERS_GROUP/roles'&&method==='GET')return true;
 if(path==='/app/rest/userGroups/key:ALL_USERS_GROUP/roles/PROJECT_DEVELOPER/g'&&['POST','DELETE'].includes(method))return true;
 if(method==='GET'&&['/app/rest/server','/app/rest/users/current','/app/rest/users','/app/rest/projects','/app/rest/buildQueue','/app/rest/agents'].includes(path))return true;
 if(path==='/app/rest/projects'&&method==='POST')return JSON.parse(body).id===project;
 if(path==='/app/rest/projects/id:'+project&&['GET','DELETE'].includes(method))return true;
 if(path==='/app/rest/buildTypes'&&method==='POST')return JSON.parse(body).id===job&&JSON.parse(body).project?.id===project;
 if(path==='/app/rest/buildTypes/id:'+job||path.startsWith('/app/rest/buildTypes/id:'+job+'/'))return true;
 if(path==='/app/rest/users'&&method==='POST')return JSON.parse(body).username===username;
 if(userId&&(path==='/app/rest/users/id:'+userId||path==='/app/rest/users/id:'+userId+'/roles'||path.startsWith('/app/rest/users/id:'+userId+'/roles/')))return true;
 if(path==='/app/rest/users/current/tokens'&&method==='POST')return JSON.parse(body).name===tokenName;
 if(agentId&&(path==='/app/rest/agents/id:'+agentId||path.startsWith('/app/rest/agents/id:'+agentId+'/')))return true;
 if(path==='/app/rest/buildQueue'&&method==='POST')return JSON.parse(body).buildType?.id===job;
 if(buildId&&['/app/rest/builds/id:'+buildId,'/app/rest/buildQueue/id:'+buildId].some(p=>path===p||path.startsWith(p+'/')))return true;
 return false;
}
function transport(actor){return async(input,init)=>{
 let request=new Request(input,init);const url=new URL(request.url),method=request.method,path=decodeURIComponent(url.pathname);
 let body=['GET','HEAD'].includes(method)?'':await request.clone().text();
 if(url.origin!==target||url.username||url.password||url.hash||!allowed(path,method,body)||(method!=='GET'&&!verified))throw Error('Unowned reference request.');
 if(actor==='admin'&&!adminAuthorization){adminAuthorization=request.headers.get('authorization');if(!adminAuthorization?.startsWith('Bearer '))throw Error('Private administrator credential unavailable.');secretValues.push(adminAuthorization.slice(7));}
 const privateExchange=path==='/app/rest/users'&&method==='POST'||path==='/app/rest/users/current/tokens'&&method==='POST';
 if(path==='/app/rest/users'&&method==='POST'){body=JSON.stringify({...JSON.parse(body),password});request=new Request(request,{body});}
 if(actor==='owned-basic-bootstrap'){
  if(path!=='/app/rest/users/current/tokens'||method!=='POST')throw Error('Basic setup scope differs.');
  const headers=new Headers(request.headers);headers.set('Authorization','Basic '+Buffer.from(username+':'+password).toString('base64'));request=new Request(request,{headers});
 }
 if(actor==='owned-account'){
  if(!(path==='/app/rest/buildQueue'&&method==='POST')&&!(buildId&&path==='/app/rest/builds/id:'+buildId+'/artifacts/files/proof.txt'&&method==='GET'))throw Error('Ordinary account proof scope differs.');
  const headers=new Headers(request.headers);headers.set('Authorization','Basic '+Buffer.from(username+':'+password).toString('base64'));request=new Request(request,{headers});
 }
 const response=await fetch(request,{redirect:'error',signal:cleanup?AbortSignal.timeout(120000):deadline});
 if(privateExchange){
  const metadata={privateCredentialExchange:true,actor,method,path,status:response.status,entities:'OMITTED_PRIVATE_SETUP'};
  if(response.status>=400){const text=await response.clone().text();if(text.length<=4096&&!secretValues.some(v=>v.length>8&&text.includes(v))&&!/(?:"value"\s*:|<value>)/i.test(text))metadata.errorHint=text.slice(0,512);}
  receipt.setup.push(metadata);await save();return response;
 }
 const bytes=Buffer.from(await response.clone().arrayBuffer());if(bytes.length>65536)throw Error('Reference entity exceeds its bound.');
 if(secretValues.some(value=>value.length>8&&(bytes.toString().includes(value)||body.includes(value))))throw Error('Secret entity export refused.');
 const headers=h=>Object.fromEntries(['accept','content-type','location','etag'].filter(k=>h.has(k)).map(k=>[k,h.get(k)]));
 active?.exchanges.push({actor,request:{method,path:url.pathname,query:Object.fromEntries(url.searchParams),headers:headers(request.headers),bodyBase64:Buffer.from(body).toString('base64'),bodyBytes:Buffer.byteLength(body),bodySha256:hash(Buffer.from(body))},response:{status:response.status,headers:headers(response.headers),bodyBase64:bytes.toString('base64'),bodyBytes:bytes.length,bodySha256:hash(bytes)}});
 await save();return response;
};}
const app=createTeamCityCli({fetch:transport('admin')});
const invoke=args=>app.execute(['-p',profile,'--json',...args],cleanup?AbortSignal.timeout(120000):deadline);
async function group(kind,id,action){const previous=active;active={id,exchanges:[]};receipt[kind].push(active);try{return await action();}finally{await save();active=previous;}}
const agentStatus=(kind,value)=>invoke(['agents',kind,'set',String(agentId),String(value),'--comment','Owned queued-authority proof']);
async function observe(label){const value=await invoke(['builds','show',String(buildId)]);if(value.buildTypeId!==job)throw Error('Build ownership differs.');receipt.observations.push({label,build:value});return value;}
let admin;
try{
 await group('setup','verify-private-reference-and-empty-fixture',async()=>{
  initial=await invoke(['permissions','list']);const status=await invoke(['server','status']);if(status.version!=='2026.1.3 (build 222742)'||String(status.buildNumber)!=='222742')throw Error('Wrong reference version.');
  const meta=JSON.parse(docker('inspect',server))[0];if(meta.Id!==serverId||meta.Image!==serverImage)throw Error('Reference container identity differs.');
  if(!JSON.parse(docker('image','inspect',agentImage))[0].RepoDigests.includes(agentImage))throw Error('Agent image identity differs.');
  const agents=await invoke(['agents','list','--connected','any','--enabled','any','--authorized','any','--limit','10']);if(agents.items.length||agents.hasMore)throw Error('Reference contains an unowned Agent.');
  const queue=await invoke(['queue','list','--limit','1']);if(queue.items.length||queue.hasMore)throw Error('Reference queue is not empty.');
  const projects=await invoke(['projects','list','--limit','10']);if(projects.hasMore||projects.items.some(p=>p.id!=='_Root'))throw Error('Reference contains another Project.');
  verified=true;for(const name of ['Update','Admin','Credentials'])await invoke(['permissions','grant',name]);
  admin=new TeamCityClient({baseUrl:target,token:adminAuthorization.slice(7),fetch:transport('admin'),signal:deadline});
  receipt.authenticationSettings=await admin.getAuthenticationSettings();
  if(!receipt.authenticationSettings.perProjectPermissions){
   const current=await transport('admin')(new Request(target+'/app/rest/users/current',{headers:{Authorization:adminAuthorization,Accept:'application/json'}}));adminUserId=String((await current.json()).id);
   receipt.administratorRoles=await admin.listAccountRoles('users',adminUserId);
   if(!receipt.administratorRoles.some(r=>r.roleId==='SYSTEM_ADMIN'&&r.scope==='g'))throw Error('Administrator does not retain an explicit global grant.');
   const currentAuth=await transport('admin')(new Request(target+'/app/rest/server/authSettings',{headers:{Authorization:adminAuthorization,Accept:'application/json'}}));initialAuthentication=await currentAuth.json();
   if(initialAuthentication.modules?.module?.some(m=>!['Default','Token-Auth','HTTP-Basic'].includes(m.name)||(m.properties?.property??[]).some(p=>m.name!=='Default'||!['usersCanResetOwnPasswords','usersCanChangeOwnPasswords','freeRegistrationAllowed'].includes(p.name)||!['true','false'].includes(p.value))))throw Error('Unexpected authentication module configuration.');
   authenticationChanged=true;
   const enabled=await transport('admin')(new Request(target+'/app/rest/server/authSettings',{method:'PUT',headers:{Authorization:adminAuthorization,Accept:'application/json','Content-Type':'application/json'},body:JSON.stringify({...initialAuthentication,perProjectPermissions:true})}));
   if(enabled.status!==200)throw Error('Could not enable scoped authorization in the isolated reference.');
   receipt.scopedAuthenticationSettings=await admin.getAuthenticationSettings();
   if(!receipt.scopedAuthenticationSettings.perProjectPermissions)throw Error('Scoped authorization did not become active.');
  }
  receipt.allUsersRolesBefore=await admin.listAccountRoles('groups','ALL_USERS_GROUP');
 });
 await group('setup','create-owned-project-user-and-finite-pat',async()=>{
  createdProject=true;await invoke(['projects','create',project,'--name','Owned queued authority']);await invoke(['jobs','create',job,'--name','Owned actor withdrawal','--project',project]);
  await invoke(['jobs','steps','replace-all',job,'--item',JSON.stringify({name:'Owned literal producer',type:'simpleRunner',properties:[{name:'script.content',value:'#!/bin/sh\nset -eu\nprintf "owned-queued-authority\\n" > proof.txt\necho "##teamcity[publishArtifacts \'proof.txt\']"\necho owned-claimed-counter-executed\n'},{name:'use.custom.script',value:'true'}]})]);
  const user=await admin.createAccountUser(username,'Owned queued actor');userId=String(user.id);createdUser=true;receipt.userId=userId;
  if(!/^\d+$/.test(userId)||user.username!==username)throw Error('Owned user identity differs.');
  await admin.grantAccountRole('users',userId,{roleId:'PROJECT_ADMIN',project});
  const bootstrap=new TeamCityClient({baseUrl:target,token:'owned-private-setup-placeholder',fetch:transport('owned-basic-bootstrap'),signal:deadline});
  await bootstrap.createCurrentUserToken({name:tokenName,alias:tokenName,restrictions:['view_project','run_build','view_file_content'].map(permission=>({permission,project})),expires:new Date(Date.now()+3600000).toISOString().replace(/[-:]/g,'').replace(/\.\d{3}Z$/,'+0000')},secrets);
  actorToken=JSON.parse(await readIssuedToken(secrets,tokenName)).value;secretValues.push(actorToken);
  await admin.revokeAccountRole('users',userId,{roleId:'PROJECT_ADMIN',project});
  await admin.grantAccountRole('users',userId,{roleId:'PROJECT_DEVELOPER',project});
 });
 await group('setup','create-new-owned-disabled-agent',async()=>{
  networkAttempted=true;docker('network','create','--internal','--label','vivarium.owner='+owner,network);connected=true;docker('network','connect',network,server);
  agentAttempted=true;docker('run','--detach','--name',agentContainer,'--label','vivarium.owner='+owner,'--network',network,'--cpus','1','--memory','1g','--env','SERVER_URL=http://'+server+':8111','--env','AGENT_NAME='+agentName,agentImage);
  for(let i=0;i<120;i++){const agents=await invoke(['agents','list','--connected','any','--enabled','any','--authorized','any','--limit','10']);if(agents.hasMore||agents.items.some(a=>a.name!==agentName))throw Error('Unexpected Agent.');const found=agents.items.find(a=>a.name===agentName&&a.connected);if(found){agentId=found.id;receipt.agentId=agentId;break;}await pause(500);}
  if(!agentId)throw Error('Owned Agent did not register.');await agentStatus('enabled',false);await agentStatus('authorized',true);
 });
 await group('cases','pause-new-owned-agent-and-observe-counter-before-delivery',async()=>{
  await agentStatus('enabled',true);
  for(let i=0;i<120;i++){
   const value=await invoke(['agents','show',String(agentId)]);
   receipt.readyAgent=value;
   if(value.connected&&value.enabled&&value.authorized&&value.uptodate&&!value.build)break;
   if(i===119)throw Error('Owned Agent did not become eligible and idle.');await pause(500);
  }
  async function counter(label){
   const response=await transport('admin')(new Request(target+'/app/rest/buildTypes/id:'+job+'/settings/buildNumberCounter',{headers:{Authorization:adminAuthorization,Accept:'text/plain'}}));
   const value=await response.text();if(response.status!==200||!/^\d+$/.test(value))throw Error('Owned counter response differs.');
   receipt.observations.push({label,counter:value});await save();return value;
  }
  receipt.counterBefore=await counter('Before owned Agent pause');
  const meta=JSON.parse(docker('inspect',agentContainer))[0];if(meta.Config.Labels['vivarium.owner']!==owner)throw Error('Pause target ownership differs.');
  docker('pause',agentContainer);agentPaused=true;
  const admit=async()=>{
   const response=await transport('owned-account')(new Request(target+'/app/rest/buildQueue',{method:'POST',headers:{Authorization:adminAuthorization,Accept:'application/json','Content-Type':'application/json'},body:JSON.stringify({buildType:{id:job}})}));
   if(response.status!==200)throw Error('Owned ordinary admission did not succeed.');const value=await response.json();
   if(!Number.isSafeInteger(value.id)||value.id<=0||value.buildTypeId!==job)throw Error('Owned admission identity differs.');buildId=value.id;return value;
  };
  const first=await admit();receipt.firstBuildId=first.id;
  for(let i=0;i<20;i++){await observe('Owned Agent deliberately paused before delivery');await pause(1000);}
  receipt.pausedAgent=await invoke(['agents','show',String(agentId)]);
  receipt.counterWhilePaused=await counter('Before cancellation while owned Agent is paused');
  const state=await observe('State before paused cancellation');receipt.stateBeforeUnpause=state;
  if(state.state==='queued')await invoke(['queue','cancel',String(buildId),'--comment','Owned paused counter reference']);
  else if(state.state==='running')await invoke(['builds','cancel',String(buildId),'--comment','Owned paused counter reference']);
  else throw Error('Owned paused Build unexpectedly completed.');
  docker('unpause',agentContainer);agentPaused=false;
  for(let i=0;i<160;i++){const value=await observe('After owned paused cancellation and Agent resume');if(value.state==='finished'){receipt.firstTerminal=value;break;}await pause(500);}
  if(!receipt.firstTerminal)throw Error('Owned first Build did not finish.');
  receipt.counterAfterCancellation=await counter('After first terminal cancellation');
  for(let i=0;i<120;i++){const value=await invoke(['agents','show',String(agentId)]);if(value.connected&&value.enabled&&value.authorized&&!value.build)break;if(i===119)throw Error('Owned Agent stayed occupied.');await pause(500);}
  receipt.secondAdmission=await admit();
  for(let i=0;i<160;i++){const value=await observe('Actual next owned ordinary control run');if(value.state==='finished'){receipt.finalBuild=value;break;}await pause(500);}
  if(!receipt.finalBuild||receipt.finalBuild.status!=='SUCCESS')throw Error('Owned control Build did not succeed.');
  receipt.counterAfterSuccess=await counter('After actual successful control');
 });
 receipt.complete=true;
}catch(error){receipt.failure={type:error?.name??'Error',message:secretValues.reduce((text,value)=>text.split(value).join('[REDACTED]'),String(error?.message??error))};}
finally{
 cleanup=true;
 if(agentPaused)try{const meta=JSON.parse(docker('inspect',agentContainer))[0];if(meta.Config.Labels['vivarium.owner']!==owner)throw Error('Resume ownership differs.');docker('unpause',agentContainer);agentPaused=false;}catch{receipt.unpauseCleanupFailure=true;}
 try{if(allUsersWithdrawn)await group('cleanup','restore-exact-all-users-lab-binding',async()=>{
  await admin.grantAccountRole('groups','ALL_USERS_GROUP',{roleId:'PROJECT_DEVELOPER',global:true});
  receipt.allUsersRolesAfter=await admin.listAccountRoles('groups','ALL_USERS_GROUP');
  if(JSON.stringify(receipt.allUsersRolesBefore)!==JSON.stringify(receipt.allUsersRolesAfter))throw Error('All Users roles differ after restoration.');
 });}catch{receipt.allUsersRestoreFailure=true;}
 try{if(verified&&createdProject)await group('cleanup','stop-owned-work-and-remove-project',async()=>{
  if(buildId){const current=await observe('Cleanup state');if(current.state==='queued')await invoke(['queue','cancel',String(buildId),'--comment','Owned reference cleanup']);else if(current.state==='running')await invoke(['builds','cancel',String(buildId),'--comment','Owned reference cleanup']);}
  await invoke(['projects','delete',project]);if(agentId){await agentStatus('enabled',false);await agentStatus('authorized',false);}
 });}catch(error){receipt.cleanupFailure=String(error?.name??'Error')+': owned state cleanup failed';}
 try{if(createdUser)await admin.deleteAccountUser(userId);}catch{receipt.userCleanupFailure=true;}
 try{
  if(agentAttempted){const meta=JSON.parse(docker('inspect',agentContainer))[0];if(meta.Config.Labels['vivarium.owner']!==owner)throw Error('Agent ownership differs.');docker('stop','--time','20',agentContainer);docker('rm','--volumes',agentContainer);}
  if(agentId){for(let i=0;i<120;i++){const agent=await invoke(['agents','show',String(agentId)]);if(agent.name!==agentName)throw Error('Agent identity differs.');if(!agent.connected)break;if(i===119)throw Error('Agent did not disconnect.');await pause(500);}await invoke(['agents','delete',String(agentId)]);}
  if(connected){const meta=JSON.parse(docker('inspect',server))[0];if(meta.Id!==serverId)throw Error('Reference identity changed.');if(meta.NetworkSettings.Networks[network])docker('network','disconnect',network,server);}
  if(networkAttempted){const meta=JSON.parse(docker('network','inspect',network))[0];if(meta.Labels['vivarium.owner']!==owner)throw Error('Network ownership differs.');docker('network','rm',network);}
 }catch(error){receipt.resourceCleanupFailure=String(error?.name??'Error')+': owned resource cleanup failed';}
 if(authenticationChanged)try{
  const restored=await transport('admin')(new Request(target+'/app/rest/server/authSettings',{method:'PUT',headers:{Authorization:adminAuthorization,Accept:'application/json','Content-Type':'application/json'},body:JSON.stringify(initialAuthentication)}));
  receipt.authenticationSettingsAfter=await admin.getAuthenticationSettings();
  if(restored.status!==200||JSON.stringify(receipt.authenticationSettings)!==JSON.stringify(receipt.authenticationSettingsAfter))receipt.authenticationRestoreFailure=true;
 }catch{receipt.authenticationRestoreFailure=true;}
 if(initial)for(const name of ['Update','Admin','Credentials'])try{await invoke(['permissions',initial.find(p=>p.name===name)?.enabled?'grant':'revoke',name]);}catch{receipt.gateRestoreFailure=true;}
 try{receipt.initialPermissions=initial;receipt.finalPermissions=await invoke(['permissions','list']);if(JSON.stringify(initial)!==JSON.stringify(receipt.finalPermissions))receipt.gateRestoreFailure=true;}catch{receipt.gateRestoreFailure=true;}
 await save();await app.dispose();
}
const good=receipt.complete&&!receipt.unpauseCleanupFailure&&!receipt.failure&&!receipt.cleanupFailure&&!receipt.userCleanupFailure&&!receipt.resourceCleanupFailure&&!receipt.gateRestoreFailure&&!receipt.allUsersRestoreFailure&&!receipt.authenticationRestoreFailure;
console.log(JSON.stringify({receipt:resolve(run,'receipt.json'),complete:good,finalState:receipt.finalBuild?.state,finalStatus:receipt.finalBuild?.status,counterBefore:receipt.counterBefore,counterWhilePaused:receipt.counterWhilePaused,counterAfterCancellation:receipt.counterAfterCancellation,counterAfterSuccess:receipt.counterAfterSuccess,pausedState:receipt.stateBeforeUnpause?.state,pausedNumber:receipt.stateBeforeUnpause?.number,firstNumber:receipt.firstTerminal?.number,nextNumber:receipt.finalBuild?.number,unpauseCleanupFailure:receipt.unpauseCleanupFailure,failure:receipt.failure,cleanupFailure:receipt.cleanupFailure,userCleanupFailure:receipt.userCleanupFailure,resourceCleanupFailure:receipt.resourceCleanupFailure,gateRestoreFailure:receipt.gateRestoreFailure}));process.exitCode=good?0:1;
