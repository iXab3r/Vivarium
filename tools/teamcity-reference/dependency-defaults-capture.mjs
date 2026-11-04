import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';

const target = 'http://127.0.0.1:18111', profile = 'vivarium-teamcity-lab-202613';
const state = resolve('artifacts/p0-rest-first/teamcity-lab/cli-state');
if (resolve(process.env.APPDATA ?? '').toLowerCase() !== resolve(state, 'roaming').toLowerCase()
    || resolve(process.env.LOCALAPPDATA ?? '').toLowerCase() !== resolve(state, 'local').toLowerCase()) throw Error('Private lab profile required.');
const image = execFileSync('docker', ['inspect', '--format', '{{.Image}}', 'vivarium-p0-rest-teamcity-2026-1-3'],
    { encoding: 'utf8', windowsHide: true, timeout: 30000 }).trim();
if (image !== 'sha256:e62c922b94edbef8536bb0d514a193a38bd480fd3855fdac7a649e0a5f8a550a') throw Error('Pinned reference image required.');
const { createTeamCityCli } = await import(pathToFileURL(resolve('artifacts/p0-rest-first/reference/cli-acquisition/dist/src/cli.js')));
const suffix = randomUUID().replaceAll('-', '').slice(0, 12), project = 'VivUs013Dependency_' + suffix;
const upstream = project + '_Upstream', downstream = project + '_Downstream';
const directory = resolve('artifacts/pilot-chain/dependency-defaults-' + suffix); await mkdir(directory, { recursive: true });
const hash = value => createHash('sha256').update(value).digest('hex');
const source = await readFile(new URL(import.meta.url)); await writeFile(resolve(directory, 'producer.mjs'), source);
const receipt = { kind: 'PINNED_REFERENCE_DEPENDENCY_DEFAULTS_NOT_PRODUCT_ACCEPTANCE', target, profile, image,
    project, upstream, downstream, producerSha256: hash(source), exchanges: [], complete: false,
    limits: 'One owned project, two empty configurations, no Agent/build/global server changes. Observes omitted snapshot properties and the reference400 rejection of an omitted required artifact selector. Earlier exploratory200 expectation is retained in its failed receipt, not a product oracle.' };
let authorization, verified = false, attempted = false, initial;
const save = () => writeFile(resolve(directory, 'receipt.json'), JSON.stringify(receipt, null, 2) + '\n');
function allowed(method, path, body) {
    if (method === 'GET' && path === '/app/rest/server') return true;
    if (path === '/app/rest/projects' && method === 'POST') return JSON.parse(body).id === project;
    if (path === '/app/rest/projects/id:' + project && ['GET', 'DELETE'].includes(method)) return true;
    if (path === '/app/rest/buildTypes' && method === 'POST') {
        const value = JSON.parse(body); return [upstream, downstream].includes(value.id) && value.project?.id === project;
    }
    return ['snapshot-dependencies', 'artifact-dependencies'].some(kind => path === '/app/rest/buildTypes/id:' + downstream + '/' + kind
        && (method === 'GET' || method === 'POST' && JSON.parse(body)['source-buildType']?.id === upstream));
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
    receipt.exchanges.push({ request: { method: request.method, path: url.pathname, query: Object.fromEntries(url.searchParams),
        headers: headers(request.headers), bodyBase64: Buffer.from(body).toString('base64'), bodySha256: hash(Buffer.from(body)) },
        response: { status: response.status, headers: headers(response.headers), bodyBase64: bytes.toString('base64'), bodyBytes: bytes.length, bodySha256: hash(bytes) } });
    await save(); return response;
}
const app = createTeamCityCli({ fetch: transport });
const invoke = args => app.execute(['-p', profile, '--json', ...args], AbortSignal.timeout(30000));
async function call(method, path, body, expected = 200) {
    const response = await transport(target + path, { method,
        headers: { Authorization: authorization, Accept: 'application/json', ...(body ? { 'Content-Type': 'application/json' } : {}) },
        ...(body ? { body: JSON.stringify(body) } : {}) });
    if (response.status !== expected) throw Error('Unexpected reference response: ' + response.status);
    return response.status === 204 ? null : response.json();
}
try {
    initial = await invoke(['permissions', 'list']);
    const server = await invoke(['server', 'status']);
    if (server.version !== '2026.1.3 (build 222742)' || String(server.buildNumber) !== '222742') throw Error('Pinned reference version required.');
    verified = true; await invoke(['permissions', 'grant', 'Update']);
    attempted = true; await call('POST', '/app/rest/projects', { id: project, name: 'Owned dependency defaults', parentProject: { id: '_Root' } });
    for (const id of [upstream, downstream]) await call('POST', '/app/rest/buildTypes', { id, name: id.endsWith('_Upstream') ? 'Upstream' : 'Downstream', project: { id: project } });
    receipt.snapshot = await call('POST', '/app/rest/buildTypes/id:' + downstream + '/snapshot-dependencies', { type: 'snapshot_dependency', 'source-buildType': { id: upstream } });
    receipt.snapshotReadback = await call('GET', '/app/rest/buildTypes/id:' + downstream + '/snapshot-dependencies');
    receipt.artifact = await call('POST', '/app/rest/buildTypes/id:' + downstream + '/artifact-dependencies', { type: 'artifact_dependency', 'source-buildType': { id: upstream }, properties: { property: [{ name: 'pathRules', value: 'proof.txt => inputs' }] } }, 400);
    receipt.artifactReadback = await call('GET', '/app/rest/buildTypes/id:' + downstream + '/artifact-dependencies');
    receipt.complete = true;
} catch (error) { receipt.failure = { type: error?.name ?? 'Error', message: authorization ? String(error?.message).split(authorization.slice(7)).join('[REDACTED]') : 'Reference setup failed' }; }
finally {
    if (attempted && verified) try { await call('DELETE', '/app/rest/projects/id:' + project, undefined, 204); await call('GET', '/app/rest/projects/id:' + project, undefined, 404); receipt.ownedProjectRemoved = true; }
    catch { receipt.cleanupFailure = true; }
    if (initial) try {
        await invoke(['permissions', initial.find(p => p.name === 'Update')?.enabled ? 'grant' : 'revoke', 'Update']);
        receipt.permissionsRestored = JSON.stringify(await invoke(['permissions', 'list'])) === JSON.stringify(initial);
    } catch { receipt.permissionsRestored = false; }
    await save();
}
const complete = receipt.complete && receipt.ownedProjectRemoved && receipt.permissionsRestored && !receipt.cleanupFailure && !receipt.failure;
console.log(JSON.stringify({ receipt: resolve(directory, 'receipt.json'), complete })); process.exitCode = complete ? 0 : 1;
