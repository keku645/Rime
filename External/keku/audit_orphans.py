"""Where do the ports that no node's port field owns live? Walk every value of every instance (nested) and
report the (owner type, path) that references each orphan port guid."""
import json, os, sys, collections

CACHE = os.path.join(os.path.dirname(__file__), 'rue_selftest', 'cache', '1f0bbddc1b0e')
PART = os.path.join(CACHE, 'partitions')

def walk(v, path, out):
    if isinstance(v, dict):
        if 'InstanceGuid' in v and len(v) <= 3:
            out.append((path, v['InstanceGuid']))
        for k, x in v.items():
            walk(x, path + '.' + k, out)
    elif isinstance(v, list):
        for i, x in enumerate(v):
            walk(x, path + '[]', out)

where = collections.Counter()
examples = {}
unreferenced = collections.Counter()
for fn in sorted(os.listdir(PART)):
    if not (fn.startswith('ui_flow_') or (fn.startswith('ui_xp') and '_flow_' in fn)):   # base game + the expansions' ui/xp5/flow
        continue
    j = json.load(open(os.path.join(PART, fn), encoding='utf-8'))
    inst = j['Instances']
    types = {g: (i.get('$type') or '') for g, i in inst.items()}
    # direct port ownership as the editor does it: node.<field> (single or array) -> port
    owned = set()
    for g, i in inst.items():
        t = types[g]
        if not t.endswith('Node') or 'NodePort' in t:
            continue
        for k, v in i.items():
            if isinstance(v, dict) and v.get('InstanceGuid') in types and 'NodePort' in types[v['InstanceGuid']]:
                owned.add(v['InstanceGuid'])
            elif isinstance(v, list):
                for e in v:
                    if isinstance(e, dict) and e.get('InstanceGuid') in types and 'NodePort' in types[e['InstanceGuid']]:
                        owned.add(e['InstanceGuid'])
    refs = collections.defaultdict(list)
    for g, i in inst.items():
        out = []
        walk(i, types[g], out)
        for path, target in out:
            refs[target].append(path)
    for g, t in types.items():
        if 'NodePort' in t and g not in owned:
            ps = [p for p in refs.get(g, []) if not p.startswith('UINodeConnection')]
            key = tuple(sorted(set(ps))) or ('<unreferenced>',)
            where[key] += 1
            if key not in examples:
                examples[key] = (fn, g, inst[g])
            if not ps:
                unreferenced[fn] += 1

print('orphan port holders:')
for k, c in where.most_common():
    print(c, k)
    fn, g, p = examples[k]
    print('    e.g.', fn, g, json.dumps(p)[:300])
print('unreferenced per partition (top):', unreferenced.most_common(10))
