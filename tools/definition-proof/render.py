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


def settings(value):
    configurations = []
    for item in value['configurations']:
        steps = array(constructor(quote(step['id']), quote(step['name']), quote(step['runner']),
                                  array(parameter(p) for p in step['properties'])) for step in item['steps'])
        requirements = array(constructor(quote(r['name']), quote(r['value'])) for r in item['requirements'])
        snapshots = array(constructor(*(quote(s[k]) for k in ('configuration', 'failure', 'cancel')))
                          for s in item['snapshots'])
        artifacts = array(constructor(quote(a['configuration']), quote(a['selector']), quote(a['rules']),
                                      str(a['cleanDestination']).lower()) for a in item['artifacts'])
        triggers = array(constructor(quote(t['type']), quote(t['branchFilter']), str(t['quietSeconds']))
                         for t in item['triggers'])
        configurations.append(constructor(quote(item['id']), quote(item['name']),
                                         array(parameter(p) for p in item['parameters']), steps,
                                         requirements, snapshots, artifacts, str(item['timeoutMinutes']), triggers))
    source = 'null' if value['source'] is None else constructor(*(quote(value['source'][key]) for key in
        ('rootId', 'url', 'branch', 'checkoutRules')))
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
