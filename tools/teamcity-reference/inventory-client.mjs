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
// The constant fixture tables omit inline MSW handlers and their client-behavior branches.
// Preserve each syntax occurrence, including helpers/wildcards/dynamic methods, without executing it.
const testFiles = git('ls-tree', '-r', '--name-only', revision, '--', 'integrations/teamcity/tests')
  .toString('utf8').split('\n').filter(path => path.endsWith('.ts')).sort();
const testSources = [], handlers = [];
for (const path of testFiles) {
  const bytes = git('show', revision + ':' + path);
  const sourceHash = hash(bytes);
  const parsed = ts.createSourceFile(path, bytes.toString('utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  if (parsed.parseDiagnostics.length) throw new Error('TypeScript test parse failed: ' + path);
  testSources.push({ source_id: 'CLI-TEST-SOURCE:' + path, source: path, source_revision: revision,
    source_sha256: sourceHash, bytes: bytes.length, state: 'PINNED_TEST_SOURCE_NOT_SERVER_ORACLE' });
  const httpNames = new Set();
  for (const statement of parsed.statements) {
    if (!ts.isImportDeclaration(statement) || statement.moduleSpecifier.text !== 'msw') continue;
    const bindings = statement.importClause?.namedBindings;
    if (bindings && ts.isNamedImports(bindings)) for (const item of bindings.elements)
      if ((item.propertyName ?? item.name).text === 'http') httpNames.add(item.name.text);
  }
  function visit(node) {
    if (ts.isCallExpression(node) &&
        (ts.isPropertyAccessExpression(node.expression) || ts.isElementAccessExpression(node.expression)) &&
        ts.isIdentifier(node.expression.expression) && httpNames.has(node.expression.expression.text)) {
      const method = ts.isPropertyAccessExpression(node.expression)
        ? node.expression.name.getText(parsed) : node.expression.argumentExpression.getText(parsed);
      let enclosing = node.parent, testName = '<helper-or-shared-handler>';
      while (enclosing) {
        if (ts.isCallExpression(enclosing) && ts.isIdentifier(enclosing.expression) && enclosing.expression.text === 'test' &&
            enclosing.arguments[0] && ts.isStringLiteralLike(enclosing.arguments[0])) {
          testName = enclosing.arguments[0].text; break;
        }
        enclosing = enclosing.parent;
      }
      const position = location(parsed, node);
      handlers.push({ source_id: `CLI-INLINE:${path}:${position.line}:${position.column}`,
        source: path, source_revision: revision, source_sha256: sourceHash, ...position,
        method_expression: method, path_expression: node.arguments[0]?.getText(parsed) ?? '<omitted>',
        enclosing_test: testName, declaration_sha256: hash(node.getText(parsed)),
        primary_subsystem: 'SS-PROTO', gap_id: 'STORY-GAP-CLI-UNION',
        state: 'SYNTHETIC_HANDLER_SYNTAX_NOT_REQUEST_VARIANT_OR_SERVER_EXPECTATION' });
    }
    ts.forEachChild(node, visit);
  }
  visit(parsed);
  testSources.at(-1).inline_handlers = handlers.filter(row => row.source === path).length;
}
function table(rows) {
  if (!rows.length) throw new Error('Unexpected empty census.');
  const columns = Object.keys(rows[0]);
  const escape = value => String(value).replaceAll('\t', '\\t').replaceAll('\r', '\\r').replaceAll('\n', '\\n');
  return [columns.join('\t'), ...rows.map(row => columns.map(c => escape(row[c])).join('\t'))].join('\n') + '\n';
}
await mkdir(output, { recursive: true });
const outputs = { 'cli-client-calls.tsv': table(callRows), 'cli-client-methods.tsv': table(methodRows),
  'cli-path-fragments.tsv': table(fragments), 'cli-test-sources.tsv': table(testSources),
  'cli-inline-handlers.tsv': table(handlers) };
for (const [name, content] of Object.entries(outputs)) {
  const path = join(output, name);
  if (check) {
    if (await readFile(path, 'utf8') !== content) throw new Error('Changed/missing generated census: ' + name);
  } else await writeFile(path, content, 'utf8');
}
console.log(JSON.stringify({ source_revision: revision, parser: ts.version, implementation_files: sources.size,
  call_sites: callRows.length, transport_reachable_methods: methodRows.length, lexical_path_fragments: fragments.length,
  test_sources: testSources.length, inline_handler_occurrences: handlers.length,
  outputs: Object.fromEntries(Object.entries(outputs).map(([name, content]) => [relative(root, join(output, name)), hash(content)])),
  claim: 'Finite syntax census, including dynamic helper calls; no runtime dispatch, normalized denominator, actor/effect oracle or compatibility coverage.' }));
