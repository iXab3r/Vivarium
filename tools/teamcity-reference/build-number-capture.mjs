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
const docker = (...args) => execFileSync('docker', args, { encoding: 'utf8', windowsHide: true, timeout: 30000 }).trim();
const server = 'vivarium-p0-rest-teamcity-2026-1-3';
const serverId = 'a98f4443f2a5cddbe614fb442e4e8d644ce5a92ebc7bf085c40df51f1cb89cce';
const image = 'sha256:e62c922b94edbef8536bb0d514a193a38bd480fd3855fdac7a649e0a5f8a550a';
const agentImage = 'jetbrains/teamcity-minimal-agent@sha256:f711ef16fd62a46bc6e5d3e07f0fcd0eaf7c3c8f905fc4500135a58d5ad05765';
const metadata = JSON.parse(docker('inspect', server))[0];
if (metadata.Id !== serverId || metadata.Image !== image) throw Error('Pinned reference required.');
const suffix = randomUUID().replaceAll('-', '').slice(0, 12), project = 'VivUs013BuildNumber_' + suffix;
const owner = 'us013-pilot-build-number', network = 'vivarium-chain-net-' + suffix;
const container = 'vivarium-chain-agent-' + suffix, agentName = project + '_Agent';
const directory = resolve('artifacts/pilot-chain/build-number-' + suffix); await mkdir(directory, { recursive: true });
const hash = value => createHash('sha256').update(value).digest('hex');
const source = await readFile(new URL(import.meta.url)); await writeFile(resolve(directory, 'producer.mjs'), source);
const receipt = { kind: 'PINNED_BUILD_NUMBER_REFERENCE_NOT_PRODUCT_ACCEPTANCE', target, profile, image,
    project, sourceSha256: hash(source), agentImage, cases: [], setup: [], cleanup: [], complete: false,
    limits: 'Owned source-free Linux jobs emit a semver buildNumber or an escaped buildNumber, followed by success or actual exit7. Records actual live/terminal numbers, outcomes and ordinary counters; no generic service-message or full API wire proof.' };
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
    const value = await call('GET', '/app/rest/builds/id:' + id + '?fields=id,buildTypeId,number,state,status,statusText,failedToStart,personal,queuedDate,startDate,finishDate,canceledInfo,agent(id),snapshot-dependencies(build(id,buildTypeId,state,status),count)');
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
    networkAttempted = true; docker('network', 'create', '--internal', '--label', 'vivarium.owner=' + owner, network);
    connected = true; docker('network', 'connect', network, server);
    agentAttempted = true; docker('run', '-d', '--name', container, '--label', 'vivarium.owner=' + owner, '--network', network,
        '--cpus', '1', '--memory', '1g', '--env', 'SERVER_URL=http://' + server + ':8111', '--env', 'AGENT_NAME=' + agentName, agentImage);
    for (let i = 0; i < 120; i++) {
        const page = await invoke(['agents', 'list', '--connected', 'any', '--enabled', 'any', '--authorized', 'any', '--limit', '10']);
        if (page.hasMore || page.items.some(a => a.name !== agentName)) throw Error('Unexpected Agent.');
        const agent = page.items.find(a => a.name === agentName && a.connected); if (agent) { agentId = agent.id; break; } await pause(500);
    }
    if (!agentId) throw Error('Owned Agent did not register.');
    await enable(false); await invoke(['agents', 'authorized', 'set', String(agentId), 'true', '--comment', 'Owned lifecycle reference']);
    for(const mode of ['semver','success','failure']){
        active=receipt.setup;
        const id=project+'_'+mode;jobs.add(id);
        await call('POST','/app/rest/buildTypes',{id,name:'Owned number '+mode,project:{id:project}});
        const value=mode==='semver'?'1.2.3-nightly.20261104.%build.counter%':"number with |'quote||and|[bracket|]";
        const script=`#!/bin/sh
set -eu
echo "##teamcity[buildNumber '${value}']"
sleep 6
exit ${mode==='failure'?'7':'0'}
`;
        await invoke(['jobs','steps','replace-all',id,'--item',JSON.stringify({name:'Owned number producer',type:'simpleRunner',
            properties:[{name:'script.content',value:script},{name:'use.custom.script',value:'true'}]})]);
        const scenario={id:'build-number:'+mode,exchanges:[],observations:[]};receipt.cases.push(scenario);active=scenario.exchanges;
        await enable(true);
        const root=await call('POST','/app/rest/buildQueue',{buildType:{id}});builds.add(root.id);scenario.admittedId=root.id;
        for(let i=0;i<120;i++){
            const value=await observe(root.id);scenario.observations.push(value);
            if(value.state==='finished'){scenario.terminalObservation=value;break;}
            await pause(250);
        }
        if(!scenario.terminalObservation)throw Error('Number reference did not finish.');
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
        if (agentId) {
            for (let i = 0; i < 40; i++) {
                const agent = await invoke(['agents', 'show', String(agentId)]);
                if (agent.name !== agentName) throw Error('Agent identity differs.');
                if (!agent.connected) break; if (i === 39) throw Error('Agent stayed connected.'); await pause(500);
            }
            await invoke(['agents', 'delete', String(agentId)]);
        }
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
