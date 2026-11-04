import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';
import { setTimeout as pause } from 'node:timers/promises';

const target = 'http://127.0.0.1:18111', profile = 'vivarium-teamcity-lab-202613';
const state = resolve('artifacts/p0-rest-first/teamcity-lab/cli-state');
if (resolve(process.env.APPDATA ?? '').toLowerCase() !== resolve(state, 'roaming').toLowerCase()
    || resolve(process.env.LOCALAPPDATA ?? '').toLowerCase() !== resolve(state, 'local').toLowerCase()) throw Error('Private lab profile required.');
const docker = (...args) => execFileSync('docker', args, { encoding: 'utf8', windowsHide: true, timeout: 180000 }).trim();
const server = 'vivarium-p0-rest-teamcity-2026-1-3';
const serverId = 'a98f4443f2a5cddbe614fb442e4e8d644ce5a92ebc7bf085c40df51f1cb89cce';
const image = 'sha256:e62c922b94edbef8536bb0d514a193a38bd480fd3855fdac7a649e0a5f8a550a';
const agentBase = 'jetbrains/teamcity-minimal-agent@sha256:f711ef16fd62a46bc6e5d3e07f0fcd0eaf7c3c8f905fc4500135a58d5ad05765';
let agentImage;
const metadata = JSON.parse(docker('inspect', server))[0];
if (metadata.Id !== serverId || metadata.Image !== image) throw Error('Pinned reference required.');
const suffix = randomUUID().replaceAll('-', '').slice(0, 12), project = 'VivUs013Docker_' + suffix;
const owner = 'us013-pilot-docker-reference', network = 'vivarium-docker-net-' + suffix;
const container = 'vivarium-docker-agent-' + suffix, agentName = project + '_Agent';
const directory = resolve('artifacts/pilot-chain/docker-reference-' + suffix); await mkdir(directory, { recursive: true });
const hash = value => createHash('sha256').update(value).digest('hex');
const source = await readFile(new URL(import.meta.url)); await writeFile(resolve(directory, 'producer.mjs'), source);
const receipt = { kind: 'PINNED_DOCKER_RUNNER_REFERENCE_NOT_PRODUCT_ACCEPTANCE', target, profile, image,
    project, sourceSha256: hash(source), agentBase, cases: [], setup: [], cleanup: [], complete: false,
    limits: 'Actual source-free Docker simpleRunner on a uniquely owned Linux reference Agent and dedicated mutual-TLS Docker engine. Success, exit7, missing pinned image and running cancellation; actual terminal/counter/artifact/container observations. No Windows, private image credentials, general Docker parameter or full runner-family conformance claim.' };

const daemon=network+'-daemon', graph=network+'-graph', certs=network+'-certs', work=network+'-work', temp=network+'-temp';
const dind='docker:29.3.1-dind@sha256:686d2c5464787b0ca42420e0aa03a94fc9781ff9bf8b5ff947614b9fa4c3c3e0';
const cliImage='docker:29.3.1-cli@sha256:18f5ab0fab739ea822819b342357947dfba235cdef438cce345ebc0c143c5b34';
const sdk='mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317';
const nodeImage='node:26.12.1-bookworm@sha256:3b764117159bda01d2396391e7789b6a9106af9729cfd001ef55e98557868310';
const volumes=[];let daemonAttempted=false, imageAttempted=false;
const imageTag='vivarium-us013-reference-docker:'+suffix;
function nested(...args){return docker('exec',daemon,'docker','--host','unix:///var/run/docker.sock',...args);}

const jobs = new Set(), builds = new Set();
let authorization, initial, verified = false, projectAttempted = false, networkAttempted = false, connected = false, agentAttempted = false, agentId;
let active = receipt.setup, cleaning = false;
const save = () => writeFile(resolve(directory, 'receipt.json'), JSON.stringify(receipt, null, 2) + '\n');
function allowed(method, path, body) {
    if (method === 'GET' && ['/app/rest/server', '/app/rest/projects', '/app/rest/buildQueue', '/app/rest/agents'].includes(path)) return true;
    if (path === '/app/rest/projects' && method === 'POST') return JSON.parse(body).id === project;
    if (path === '/app/rest/projects/id:' + project && ['DELETE', 'GET'].includes(method)) return true;
    if (path === '/app/rest/buildTypes' && method === 'POST') { const value = JSON.parse(body); return jobs.has(value.id) && value.project?.id === project; }
    for (const job of jobs) if (path === '/app/rest/buildTypes/id:' + job || path.startsWith('/app/rest/buildTypes/id:' + job + '/')) return true;
    if (method === 'POST' && path === '/app/rest/buildQueue') return jobs.has(JSON.parse(body).buildType?.id);
    for (const id of builds) if (['/app/rest/builds/id:' + id, '/app/rest/buildQueue/id:' + id].includes(path)) return ['GET', 'POST'].includes(method);
    for (const id of builds) if (path === '/app/rest/builds/id:' + id + '/artifacts/content/proof.txt') return method === 'GET';
    if (agentId && ["/app/rest/agents/id:" + agentId, "/app/rest/agents/id:" + agentId + '/enabledInfo',
        "/app/rest/agents/id:" + agentId + '/authorizedInfo'].includes(path)) return true;
    return false;
}
async function transport(input, init) {
    const request = new Request(input, init), url = new URL(request.url);
    const body = ['GET', 'HEAD'].includes(request.method) ? '' : await request.clone().text();
    if (url.origin !== target || url.username || url.password || url.hash || !allowed(request.method, decodeURIComponent(url.pathname), body)
        || request.method !== 'GET' && !verified) throw Error('Unowned reference request refused.');
    authorization ??= request.headers.get('authorization');
    if (!authorization?.startsWith('Bearer ')) throw Error('Private reference credential unavailable.');
    const response = await fetch(request, { redirect: 'error', signal: AbortSignal.timeout(30000) });
    const bytes = Buffer.from(await response.clone().arrayBuffer());
    if (bytes.length > 65536 || bytes.toString().includes(authorization.slice(7)) || body.includes(authorization.slice(7))) throw Error('Private or oversized entity refused.');
    const headers = value => Object.fromEntries(['accept', 'content-type', 'location'].filter(key => value.has(key)).map(key => [key, value.get(key)]));
    active.push({ request: { method: request.method, path: url.pathname, query: Object.fromEntries(url.searchParams), headers: headers(request.headers),
        bodyBase64: Buffer.from(body).toString('base64'), bodyBytes: Buffer.byteLength(body), bodySha256: hash(Buffer.from(body)) },
    response: { status: response.status, headers: headers(response.headers), bodyBase64: bytes.toString('base64'), bodyBytes: bytes.length, bodySha256: hash(bytes) } });
    await save(); return response;
}
const { createTeamCityCli } = await import(pathToFileURL(resolve('artifacts/p0-rest-first/reference/cli-acquisition/dist/src/cli.js')));
const app = createTeamCityCli({ fetch: transport });
const invoke = args => app.execute(['-p', profile, '--json', ...args], AbortSignal.timeout(30000));
async function call(method, path, body, expected = 200) {
    const response = await transport(target + path, { method,
        headers: { Authorization: authorization, Accept: 'application/json', ...(body ? { 'Content-Type': 'application/json' } : {}) },
        ...(body ? { body: JSON.stringify(body) } : {}) });
    if (response.status !== expected) throw Error('Unexpected reference response: ' + response.status);
    return expected === 204 ? null : response.json();
}
async function enable(value) { await invoke(['agents', 'enabled', 'set', String(agentId), String(value), '--comment', 'Owned lifecycle reference']); }
async function observe(id) {
    const value = await call('GET', '/app/rest/builds/id:' + id + '?fields=id,buildTypeId,number,state,status,statusText,failedToStart,personal,queuedDate,startDate,finishDate,canceledInfo,agent(id),problemOccurrences(count,problemOccurrence(type,identity,details)),snapshot-dependencies(build(id,buildTypeId,state,status),count)');
    if (!jobs.has(value.buildTypeId)) throw Error('Build ownership differs.');
    for (const dependency of value['snapshot-dependencies']?.build ?? []) builds.add(dependency.id);
    return value;
}
async function cancel(id) {
    const value = await observe(id);
    if (value.state !== 'finished') await call('POST', '/app/rest/' + (value.state === 'queued' ? 'buildQueue' : 'builds') + '/id:' + id,
        { comment: cleaning ? 'Owned lifecycle cleanup' : 'Owned lifecycle cancel', readdIntoQueue: false });
}
try {
    initial = await invoke(['permissions', 'list']); const version = await invoke(['server', 'status']);
    if (version.version !== '2026.1.3 (build 222742)') throw Error('Wrong reference version.');
    const agents = await invoke(['agents', 'list', '--connected', 'any', '--enabled', 'any', '--authorized', 'any', '--limit', '10']);
    const queue = await invoke(['queue', 'list', '--limit', '1']);
    if (agents.items.length || agents.hasMore || queue.items.length || queue.hasMore) throw Error('Reference is occupied.');
    verified = true; for (const gate of ['Update', 'Admin']) await invoke(['permissions', 'grant', gate]);
    projectAttempted = true; await call('POST', '/app/rest/projects', { id: project, name: 'Owned lifecycle reference', parentProject: { id: '_Root' } });
    networkAttempted = true; docker('network', 'create', '--label', 'vivarium.owner=' + owner, network);
    connected = true; docker('network', 'connect', network, server);

    const recipe='FROM '+cliImage+' AS cli\nFROM '+agentBase+'\nUSER root\nCOPY --from=cli /usr/local/bin/docker /usr/local/bin/docker\nUSER buildagent\n';
    const imageContext=resolve(directory,'agent-image');await mkdir(imageContext);await writeFile(resolve(imageContext,'Dockerfile'),recipe);
    receipt.agentImageRecipe={sha256:hash(Buffer.from(recipe)),recipe,base:agentBase,cliImage};
    imageAttempted=true;docker('build','--network','none','--pull=false','--label','vivarium.owner='+owner,'--label','vivarium.fixture='+suffix,'--tag',imageTag,imageContext);
    agentImage=docker('image','inspect','--format','{{.Id}}',imageTag);receipt.agentImage=agentImage;
    receipt.actualClientVersion=docker('run','--rm','--pull','never','--label','vivarium.owner='+owner,'--network','none','--read-only','--cap-drop','ALL','--entrypoint','docker',agentImage,'--version');
    if(!receipt.actualClientVersion.startsWith('Docker version 29.3.1,'))throw Error('Pinned Docker client version differs.');
    for(const name of [graph,certs,work,temp]){volumes.push(name);docker('volume','create','--label','vivarium.owner='+owner,name);}
    daemonAttempted=true;docker('run','-d','--pull','never','--name',daemon,'--label','vivarium.owner='+owner,'--hostname','docker','--network',network,'--network-alias','docker','--privileged','--memory','2g','--cpus','2','--pids-limit','512','--env','DOCKER_TLS_CERTDIR=/certs',
        '--mount','type=volume,source='+graph+',target=/var/lib/docker','--mount','type=volume,source='+certs+',target=/certs/client',
        '--mount','type=volume,source='+work+',target=/opt/buildagent/work','--mount','type=volume,source='+temp+',target=/opt/buildagent/temp',dind);
    let engineReady=false;for(let i=0;i<80;i++){try{receipt.engineId=nested('info','--format','{{.ID}}');engineReady=true;break;}catch{await pause(250);}}
    if(!engineReady||receipt.engineId===docker('info','--format','{{.ID}}'))throw Error('Separate engine unavailable.');
    docker('run','--rm','--pull','never','--label','vivarium.owner='+owner,'--network','none','--read-only','--cap-drop','ALL','--cap-add','CHOWN','--user','0:0',
        '--mount','type=volume,source='+certs+',target=/cert-client','--mount','type=volume,source='+work+',target=/work','--mount','type=volume,source='+temp+',target=/temp',sdk,
        'sh','-c','chmod 600 /cert-client/ca.pem /cert-client/cert.pem /cert-client/key.pem && chmod 700 /cert-client && chown 1000:1000 /cert-client/ca.pem /cert-client/cert.pem /cert-client/key.pem /cert-client /work /temp');
    nested('pull',nodeImage);receipt.runtime={dind,cliImage,nodeImage,engineId:receipt.engineId,network,workspace:'/opt/buildagent/work',temporary:'/opt/buildagent/temp',tls:true};await save();

    agentAttempted = true; docker('run', '-d', '--name', container, '--label', 'vivarium.owner=' + owner, '--network', network,
        '--cpus', '1', '--memory', '1g', '--env', 'SERVER_URL=http://' + server + ':8111', '--env', 'AGENT_NAME=' + agentName,'--env','DOCKER_HOST=tcp://docker:2376','--env','DOCKER_TLS_VERIFY=1','--env','DOCKER_CERT_PATH=/cert-client',
        '--mount','type=volume,source='+certs+',target=/cert-client,readonly','--mount','type=volume,source='+work+',target=/opt/buildagent/work','--mount','type=volume,source='+temp+',target=/opt/buildagent/temp',agentImage);
    for (let i = 0; i < 120; i++) {
        const page = await invoke(['agents', 'list', '--connected', 'any', '--enabled', 'any', '--authorized', 'any', '--limit', '10']);
        if (page.hasMore || page.items.some(a => a.name !== agentName)) throw Error('Unexpected Agent.');
        const agent = page.items.find(a => a.name === agentName && a.connected); if (agent) { agentId = agent.id; break; } await pause(500);
    }
    if (!agentId) throw Error('Owned Agent did not register.');
    await enable(false); await invoke(['agents', 'authorized', 'set', String(agentId), 'true', '--comment', 'Owned lifecycle reference']);

    for(const mode of ['success','exit7','missing-image','cancel']){
        active=receipt.setup;const id=project+'_'+mode.replaceAll('-','_');jobs.add(id);
        await call('POST','/app/rest/buildTypes',{id,name:'Owned Docker '+mode,project:{id:project}});
        const script=mode==='exit7'?'#!/bin/sh\nexit 7\n':mode==='cancel'?'#!/bin/sh\nnode -e "setTimeout(()=>{},600000)"\n':
            '#!/bin/sh\nset -eu\nnode --version\nprintf "coherent-reference-version\\n" > proof.txt\necho "##teamcity[publishArtifacts \'proof.txt\']"\n';
        await invoke(['jobs','steps','replace-all',id,'--item',JSON.stringify({name:'Owned Docker step',type:'simpleRunner',properties:[
            {name:'script.content',value:script},{name:'use.custom.script',value:'true'},
            {name:'plugin.docker.imageId',value:mode==='missing-image'?'node@sha256:'+''.padStart(64,'0'):nodeImage},
            {name:'plugin.docker.imagePlatform',value:'linux'},{name:'plugin.docker.run.parameters',value:'--user 0 --init --platform linux/amd64'}]})]);
        const counter=async()=>{const r=await transport(target+'/app/rest/buildTypes/id:'+id+'/settings/buildNumberCounter',{headers:{Authorization:authorization,Accept:'text/plain'}});if(r.status!==200)throw Error('Counter unavailable.');return r.text();};
        const scenario={id:'docker:'+mode,exchanges:[],observations:[],counterBefore:await counter()};receipt.cases.push(scenario);active=scenario.exchanges;
        await enable(true);const admitted=await call('POST','/app/rest/buildQueue',{buildType:{id}});builds.add(admitted.id);
        let cancelSent=false;
        for(let i=0;i<240;i++){
            const value=await observe(admitted.id);scenario.observations.push(value);
            if(mode==='cancel'&&!cancelSent&&value.state==='running'){
                const running=nested('ps','--no-trunc','--format','{{.ID}}');
                if(running){
                    const names=running.split('\n').flatMap(cid=>nested('top',cid,'-eo','pid,comm').split('\n').slice(1).map(s=>s.trim().split(/\s+/).slice(1).join(' ')));
                    const executables=running.split('\n').flatMap(cid=>nested('exec',cid,'sh','-c','for p in /proc/[0-9]*/exe; do readlink "$p" 2>/dev/null || :; done').split('\n'));
                    scenario.lastRunningProcessNames=names;
                    if(executables.includes('/usr/local/bin/node')){scenario.runningContainers=running.split('\n');scenario.runningProcessNames=names;scenario.runningProcessExecutables=executables;await cancel(admitted.id);cancelSent=true;}
                }
            }
            if(value.state==='finished'){scenario.terminalObservation=value;break;}await pause(500);
        }
        scenario.terminalProven=!!scenario.terminalObservation;
        if(!scenario.terminalProven)throw Error('Docker observation did not reach a terminal state.');
        scenario.counterAfter=await counter();scenario.remainingContainers=nested('ps','-a','--format','{{.ID}} {{.State}}');
        if(mode==='success'){
            const path='/app/rest/builds/id:'+admitted.id+'/artifacts/content/proof.txt';const r=await transport(target+path,{headers:{Authorization:authorization,Accept:'application/octet-stream'}});
            const bytes=Buffer.from(await r.arrayBuffer());scenario.download={status:r.status,bytes:bytes.length,sha256:hash(bytes)};
        }
        await enable(false);await save();
    }
    receipt.complete = true;
} catch (error) { receipt.failure = { type: error?.name ?? 'Error', message: authorization ? String(error?.message).split(authorization.slice(7)).join('[REDACTED]') : 'Reference setup failed' }; }
finally {
    cleaning = true; active = receipt.cleanup;
    try { for (const id of builds) await cancel(id); } catch { receipt.buildCleanupFailure = true; }
    try { if (projectAttempted) { await call('DELETE', '/app/rest/projects/id:' + project, undefined, 204); await call('GET', '/app/rest/projects/id:' + project, undefined, 404); receipt.ownedProjectRemoved = true; } } catch { receipt.projectCleanupFailure = true; }
    try {
        if (agentAttempted) { const meta = JSON.parse(docker('inspect', container))[0]; if (meta.Config.Labels['vivarium.owner'] !== owner) throw Error('Agent ownership differs.'); docker('stop', '--time', '10', container); docker('rm', '--volumes', container); }
        if(daemonAttempted){const meta=JSON.parse(docker('inspect',daemon))[0];if(meta.Config.Labels['vivarium.owner']!==owner)throw Error('Daemon owner differs.');docker('rm','--force',daemon);}
        if (agentId) {
            for (let i = 0; i < 120; i++) {
                const agent = await invoke(['agents', 'show', String(agentId)]);
                if (agent.name !== agentName) throw Error('Agent identity differs.');
                if (!agent.connected) break; if (i === 119) throw Error('Agent stayed connected.'); await pause(500);
            }
            await invoke(['agents', 'delete', String(agentId)]);
        }

        for(const name of volumes.reverse()){const meta=JSON.parse(docker('volume','inspect',name))[0];if(meta.Labels['vivarium.owner']!==owner)throw Error('Volume owner differs.');docker('volume','rm',name);}
        if(imageAttempted){const meta=JSON.parse(docker('image','inspect',imageTag))[0];if(meta.Config.Labels['vivarium.owner']!==owner||meta.Config.Labels['vivarium.fixture']!==suffix)throw Error('Image owner differs.');docker('image','rm',imageTag);}
        if (connected) { const meta = JSON.parse(docker('inspect', server))[0]; if (meta.Id !== serverId) throw Error('Server identity differs.'); docker('network', 'disconnect', network, server); }
        if (networkAttempted) { const meta = JSON.parse(docker('network', 'inspect', network))[0]; if (meta.Labels['vivarium.owner'] !== owner) throw Error('Network ownership differs.'); docker('network', 'rm', network); }
        receipt.ownedResourcesRemoved = true;
    } catch { receipt.resourceCleanupFailure = true; }
    if (initial) try { for (const gate of ['Update', 'Admin']) await invoke(['permissions', initial.find(p => p.name === gate)?.enabled ? 'grant' : 'revoke', gate]);
        receipt.permissionsRestored = JSON.stringify(await invoke(['permissions', 'list'])) === JSON.stringify(initial); } catch { receipt.permissionsRestored = false; }
    await save(); await app.dispose();
}
const complete = receipt.complete && receipt.ownedProjectRemoved && receipt.ownedResourcesRemoved && receipt.permissionsRestored
    && !receipt.failure && !receipt.buildCleanupFailure && !receipt.projectCleanupFailure && !receipt.resourceCleanupFailure;
console.log(JSON.stringify({ receipt: resolve(directory, 'receipt.json'), complete, failure: receipt.failure })); process.exitCode = complete ? 0 : 1;
