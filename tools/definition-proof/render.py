"""Bounded editor for the candidate's managed C# declarations, never arbitrary user C#."""
import json


def quote(value):
    return 'null' if value is None else json.dumps(value, ensure_ascii=True)


def array(items):
    return '[' + ', '.join(items) + ']'


def constructor(*values):
    return 'new(' + ', '.join(values) + ')'


def parameter(item):
    return constructor(*(quote(item[key]) for key in ('name', 'literal', 'expression')))


def step(item):
    return constructor(quote(item['id']), quote(item['name']), quote(item['runner']),
                       array(parameter(p) for p in item['properties']))


def settings(value):
    configurations = []
    for item in value['configurations']:
        steps = array(step(item) for item in item['steps'])
        requirements = array(constructor(quote(r['name']), quote(r['value'])) for r in item['requirements'])
        snapshots = array(constructor(*(quote(s[k]) for k in ('configuration', 'failure', 'cancel')))
                          for s in item['snapshots'])
        artifacts = array(constructor(quote(a['configuration']), quote(a['selector']), quote(a['rules']),
                                      str(a['cleanDestination']).lower()) for a in item['artifacts'])
        triggers = array(constructor(quote(t['type']), quote(t['branchFilter']), str(t['quietSeconds']),
                                     *([quote(t.get('quietPeriodMode')), quote(t.get('rules'))]
                                       if t.get('quietPeriodMode') is not None or t.get('rules') is not None else []))
                         for t in item['triggers'])
        configurations.append(constructor(quote(item['id']), quote(item['name']),
                                         array(parameter(p) for p in item['parameters']), steps,
                                         requirements, snapshots, artifacts, str(item['timeoutMinutes']), triggers,
                                         *([quote(item['checkoutMode'])] if item.get('checkoutMode') is not None else [])))
    source = 'null' if value['source'] is None else constructor(
        *(quote(value['source'][key]) for key in ('rootId', 'url', 'branch', 'checkoutRules')),
        *([quote(value['source']['name'])] if value['source'].get('name') is not None else []))
    configuration_source = ('[\n            ' + ',\n            '.join(configurations) + '\n        ]') if configurations else '[]'
    return ('using DefinitionProof;\npublic static class Managed\n{\n'
            '    public static Settings Declare() => new(' + quote(value['projectId']) + ', ' + source + ',\n'
            '        ' + array(parameter(p) for p in value['parameters']) + ',\n'
            '        ' + configuration_source + ');\n}\n')


def control(value):
    binding = value['binding']
    bound = 'null' if binding is None else constructor(*(quote(binding[key]) for key in
        ('repository', 'path', 'branch', 'trustReference', 'credentialReference')))
    return ('using DefinitionProof;\npublic static class Control\n{\n'
            '    public static Project Declare() => ' + constructor(*(quote(value[key]) for key in
                ('id', 'parent', 'name', 'description')), bound) + ';\n}\n')


def computed_settings(edits):
    """Render one typed C# edit declaration after untouched user-owned C# computation.

    The compiled Edits property is the readback for subsequent edits; JSON is transient.
    Removal suppresses the local computed parameter, then normal inheritance resolves it.
    """
    if len({e['configuration'] for e in edits}) != len(edits):
        raise ValueError('Duplicate configuration edit')
    declarations = []
    allowed = {'configuration', 'name', 'parameters', 'steps', 'timeoutMinutes'}
    for edit in edits:
        if set(edit) - allowed:
            raise ValueError('Unsupported computed-field edit')
        changes = edit.get('parameters')
        if changes is not None:
            if len({p['name'] for p in changes}) != len(changes):
                raise ValueError('Duplicate parameter edit')
            for p in changes:
                if set(p) - {'name', 'value', 'remove'} or 'name' not in p:
                    raise ValueError('Unknown parameter edit field')
                if type(p.get('remove', False)) is not bool or p.get('remove', False) == (p.get('value') is not None):
                    raise ValueError('Expected literal set or explicit local removal')
                if p.get('value') is not None and not isinstance(p['value'], str):
                    raise ValueError('Expected literal string')
        timeout = edit.get('timeoutMinutes')
        if timeout is not None and (type(timeout) is not int or not 0 <= timeout <= 2147483647):
            raise ValueError('Expected non-negative Int32 timeout')
        if all(edit.get(key) is None for key in allowed - {'configuration'}):
            raise ValueError('Empty edit')
        declarations.append(constructor(
            quote(edit['configuration']), quote(edit.get('name')),
            'null' if changes is None else array(constructor(quote(p['name']), quote(p.get('value')),
                                                           str(p.get('remove', False)).lower()) for p in changes),
            'null' if edit.get('steps') is None else array(step(s) for s in edit['steps']),
            'null' if timeout is None else str(timeout)))
    return ('using DefinitionProof;\npublic static class Managed\n{\n'
            '    public static ConfigurationEdit[] Edits => ' + array(declarations) + ';\n'
            '    public static Settings Declare() => ProofEdits.Apply(UserCode.Declare(), Edits);\n}\n')
