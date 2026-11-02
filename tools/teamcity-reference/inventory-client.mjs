/** Read pinned TypeScript syntax only. Never execute a client, fixture or HTTP request. */
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, join, relative } from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';

const root = process.cwd();
const work = join(root, '.workspace/workstreams/coherent-refactor');
const repository = resolve(root, 'artifacts/depot-refactor-study/cli-reference');
const revision = 'b158fe03eab3c31982a21b36c278f690f6b541d9';
const args = process.argv.slice(2);
const check = args.includes('--check');
const outputIndex = args.indexOf('--output');
const output = outputIndex < 0 ? work : resolve(args[outputIndex + 1]);
const compilerIndex = args.indexOf('--compiler-package');
const compilerPackage = compilerIndex < 0
  ? join(root, 'artifacts/p0-rest-first/reference/cli-acquisition/package.json')
  : resolve(args[compilerIndex + 1]);
const ts = createRequire(compilerPackage)('typescript');
if (ts.version !== '5.9.3') throw new Error('Expected the existing pinned TypeScript 5.9.3 parser.');
const git = (...args) => execFileSync('git', ['--no-optional-locks', '-C', repository, ...args], { maxBuffer: 4 * 1024 * 1024 });
if (resolve(git('rev-parse', '--show-toplevel').toString().trim()) !== repository ||
    git('rev-parse', 'HEAD').toString().trim() !== revision)
  throw new Error('Expected an independent checkout at the pinned CLI revision.');
const hash = value => createHash('sha256').update(value).digest('hex');
const manifest = JSON.parse(await readFile(join(work, 'source-union-checkpoint.json'), 'utf8'));
if (manifest.cli_source_revision !== revision) throw new Error('Source manifest pin differs.');
const clientPath = 'integrations/teamcity/src/client.ts';
const sources = new Map();
for (const input of manifest.cli_implementation_files) {
  if (!input.path.startsWith('integrations/teamcity/src/') || !input.path.endsWith('.ts') || input.path.includes('..'))
    throw new Error('Unexpected implementation path.');
  const bytes = git('show', revision + ':' + input.path);
  if (hash(bytes) !== input.sha256) throw new Error('Pinned source hash differs: ' + input.path);
  const source = ts.createSourceFile(input.path, bytes.toString('utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  if (source.parseDiagnostics.length) throw new Error('TypeScript parse failed: ' + input.path);
  sources.set(input.path, { source, sha256: input.sha256 });
}
const { source, sha256 } = sources.get(clientPath);
const methods = new Map();
function index(node) {
  if (ts.isMethodDeclaration(node)) {
    const name = node.name.getText(source);
    if (methods.has(name)) throw new Error('Method overload needs explicit census support: ' + name);
    methods.set(name, node);
  }
  ts.forEachChild(node, index);
}
index(source);
const edges = [];
function calls(node, owner) {
  if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression) &&
      node.expression.expression.kind === ts.SyntaxKind.ThisKeyword)
    edges.push({ owner, target: node.expression.name.getText(source), node });
  ts.forEachChild(node, child => calls(child, owner));
}
for (const [owner, method] of methods) if (method.body) calls(method.body, owner);
const reachable = new Set(['#response']);
for (let changed = true; changed;) {
  changed = false;
  for (const edge of edges) if (reachable.has(edge.target) && !reachable.has(edge.owner)) {
    reachable.add(edge.owner); changed = true;
  }
}
function location(source, node) {
  const position = source.getLineAndCharacterOfPosition(node.getStart(source));
  return { line: position.line + 1, column: position.character + 1 };
}
const callRows = edges.filter(edge => reachable.has(edge.target)).map(edge => {
  const position = location(source, edge.node);
  const parameters = methods.get(edge.target).parameters;
  return { site_id: `CLI-CALL:${position.line}:${position.column}`, source: clientPath, source_revision: revision,
    source_sha256: sha256, ...position, owner_method: edge.owner, called_method: edge.target,
    parameter_expressions: JSON.stringify(Object.fromEntries(parameters.map((p, index) =>
      [p.name.getText(source), edge.node.arguments[index]?.getText(source) ?? '<omitted>']))),
    state: 'SYNTAX_CALL_GRAPH_NOT_EXECUTED_VARIANT' };
});
const methodRows = [...reachable].sort().map(name => {
  const method = methods.get(name);
  return { method_id: 'CLI-METHOD:' + name, source: clientPath, source_revision: revision,
    source_sha256: sha256, ...location(source, method), method: name,
    parameters: JSON.stringify(method.parameters.map(p => p.getText(source))),
    declaration_sha256: hash(method.getText(source)), direct_http_transport: name === '#response',
    state: 'REACHES_TRANSPORT_BY_SYNTAX_NOT_SEMANTIC_DISPATCH' };
});
const fragments = [];
for (const [path, input] of sources) {
  function visit(node) {
    if ((ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node) || ts.isTemplateExpression(node)) &&
        /\/(?:app\/rest|guestAuth|httpAuth|download[^/]*|repository)(?:\/|\b)/.test(node.getText(input.source))) {
      const position = location(input.source, node);
      fragments.push({ fragment_id: `CLI-PATH:${path}:${position.line}:${position.column}`, source: path,
        source_revision: revision, source_sha256: input.sha256, ...position,
        expression: node.getText(input.source), state: 'LEXICAL_PATH_FRAGMENT_NOT_OPERATION_OR_ORACLE' });
    }
    ts.forEachChild(node, visit);
  }
  visit(input.source);
}
function table(rows) {
  if (!rows.length) throw new Error('Unexpected empty census.');
  const columns = Object.keys(rows[0]);
  const escape = value => String(value).replaceAll('\t', '\\t').replaceAll('\r', '\\r').replaceAll('\n', '\\n');
  return [columns.join('\t'), ...rows.map(row => columns.map(c => escape(row[c])).join('\t'))].join('\n') + '\n';
}
await mkdir(output, { recursive: true });
const outputs = { 'cli-client-calls.tsv': table(callRows), 'cli-client-methods.tsv': table(methodRows),
  'cli-path-fragments.tsv': table(fragments) };
for (const [name, content] of Object.entries(outputs)) {
  const path = join(output, name);
  if (check) {
    if (await readFile(path, 'utf8') !== content) throw new Error('Changed/missing generated census: ' + name);
  } else await writeFile(path, content, 'utf8');
}
console.log(JSON.stringify({ source_revision: revision, parser: ts.version, implementation_files: sources.size,
  call_sites: callRows.length, transport_reachable_methods: methodRows.length, lexical_path_fragments: fragments.length,
  outputs: Object.fromEntries(Object.entries(outputs).map(([name, content]) => [relative(root, join(output, name)), hash(content)])),
  claim: 'Finite syntax census, including dynamic helper calls; no runtime dispatch, normalized denominator, actor/effect oracle or compatibility coverage.' }));
