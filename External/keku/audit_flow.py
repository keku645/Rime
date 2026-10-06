"""Ground truth of the UI flow graphs from the cached partition dumps (diagnostic only).

Answers, over every ui/flow/* partition in the editor cache:
  - every $type seen, with the fields that reference *NodePort instances (single / array)
  - the direction of every (type, field) port slot, measured from UINodeConnection usage
  - connections whose source/target node is not a *Node instance
  - primary instance type of each partition category and its graph fields
  - per-partition counts (nodes, ports, connections) for the C# audit to match
"""
import json, os, sys, collections

CACHE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), 'rue_selftest', 'cache', '1f0bbddc1b0e')
PART = os.path.join(CACHE, 'partitions')
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), 'audit_flow.txt')

type_fields = collections.defaultdict(lambda: collections.defaultdict(set))   # type -> field -> {'single','array'}
type_count = collections.Counter()
port_dir = collections.defaultdict(collections.Counter)      # (type, field) -> Counter(source/target)
port_names = collections.defaultdict(collections.Counter)    # (type, field) -> Counter(name)
port_types = collections.Counter()
odd_conn = []
primary_types = collections.defaultdict(collections.Counter)  # category -> Counter(primary type)
primary_fields = collections.defaultdict(set)
per_partition = {}
orphan_ports = collections.Counter()   # ports not owned by any node
shared_ports = 0
nonnode_types_with_ports = collections.Counter()
node_types_not_suffixed = set()
conn_fields = collections.Counter()
missing_port_refs = 0
port_owner_kinds = collections.Counter()

def ref_guid(v):
    return v.get('InstanceGuid') if isinstance(v, dict) else None

for fn in sorted(os.listdir(PART)):
    if not (fn.startswith('ui_flow_') or (fn.startswith('ui_xp') and '_flow_' in fn)):   # base game + the expansions' ui/xp5/flow
        continue
    s_Parts = fn.split('_')
    cat = s_Parts[s_Parts.index('flow') + 1]
    with open(os.path.join(PART, fn), encoding='utf-8') as f:
        j = json.load(f)
    inst = j['Instances']
    prim = inst.get(j.get('PrimaryInstanceGuid'), {})
    primary_types[cat][prim.get('$type')] += 1
    for k in prim.keys():
        primary_fields[prim.get('$type')].add(k)
    types = {g: (i.get('$type') or '') for g, i in inst.items()}
    port_owner = {}
    nodes = 0; ports = 0; conns = 0
    for g, i in inst.items():
        t = types[g]
        type_count[t] += 1
        if 'NodePort' in t:
            ports += 1; port_types[t] += 1
            continue
        if t == 'UINodeConnection':
            conns += 1
            for k in i.keys():
                conn_fields[k] += 1
            continue
        is_node = t.endswith('Node')
        if is_node:
            nodes += 1
        for k, v in i.items():
            if k == '$type':
                continue
            if isinstance(v, dict) and ref_guid(v) and 'NodePort' in types.get(ref_guid(v), ''):
                type_fields[t][k].add('single')
                if not is_node: nonnode_types_with_ports[t] += 1
                pg = ref_guid(v)
                if pg in port_owner: shared_ports += 1
                port_owner[pg] = (t, k, g)
                p = inst[pg]
                port_names[(t, k)][(p.get('InstanceName') or p.get('Name') or '') + '|' + str(p.get('Query', '')).replace('UIWidgetEventID_', '') + '|' + str(p.get('InputEventType', ''))] += 1
            elif isinstance(v, list):
                for e in v:
                    if isinstance(e, dict) and ref_guid(e) and 'NodePort' in types.get(ref_guid(e), ''):
                        type_fields[t][k].add('array')
                        if not is_node: nonnode_types_with_ports[t] += 1
                        pg = ref_guid(e)
                        if pg in port_owner: shared_ports += 1
                        port_owner[pg] = (t, k, g)
                        p = inst[pg]
                        port_names[(t, k)][(p.get('InstanceName') or p.get('Name') or '') + '|' + str(p.get('Query', '')).replace('UIWidgetEventID_', '') + '|' + str(p.get('InputEventType', ''))] += 1
        if 'ParentGraph' in i and not is_node:
            node_types_not_suffixed.add(t)
    for g, i in inst.items():
        if 'NodePort' in types[g] and g not in port_owner:
            orphan_ports[types[g]] += 1
    for g, i in inst.items():
        if types[g] != 'UINodeConnection':
            continue
        sn, sp, tn, tp = ref_guid(i.get('SourceNode')), ref_guid(i.get('SourcePort')), ref_guid(i.get('TargetNode')), ref_guid(i.get('TargetPort'))
        for role, pg, ng in (('source', sp, sn), ('target', tp, tn)):
            if pg is None or pg not in port_owner:
                missing_port_refs += 1
                odd_conn.append((fn, role, 'port not owned', pg, types.get(pg), types.get(ng)))
                continue
            t, k, owner = port_owner[pg]
            port_dir[(t, k)][role] += 1
            if owner != ng:
                odd_conn.append((fn, role, 'port owner != node ref', types.get(ng), types.get(owner), k))
            if ng is None or not types.get(ng, '').endswith('Node'):
                odd_conn.append((fn, role, 'node ref not a *Node', types.get(ng)))
    per_partition[fn] = (nodes, ports, conns, len(inst))

with open(OUT, 'w', encoding='utf-8') as o:
    o.write('== primary instance types by category ==\n')
    for cat, c in primary_types.items():
        o.write(f'{cat}: {dict(c)}\n')
    o.write('\n== primary type fields ==\n')
    for t, fs in primary_fields.items():
        o.write(f'{t}: {sorted(fs)}\n')
    o.write('\n== node-like types (have ParentGraph) that do not end with Node ==\n')
    o.write(f'{sorted(node_types_not_suffixed)}\n')
    o.write('\n== non-node types holding ports ==\n')
    o.write(f'{dict(nonnode_types_with_ports)}\n')
    o.write('\n== port types ==\n')
    o.write(f'{dict(port_types)}  orphan(not owned by any node)={dict(orphan_ports)}  shared={shared_ports}\n')
    o.write(f'connection fields: {dict(conn_fields)}  missing port refs: {missing_port_refs}\n')
    o.write('\n== port slots per type: field(kind) -> direction counts, top names ==\n')
    for t in sorted(type_fields):
        o.write(f'{t} (x{type_count[t]})\n')
        for k in sorted(type_fields[t]):
            d = port_dir[(t, k)]
            names = port_names[(t, k)].most_common(8)
            o.write(f'    {k} [{",".join(sorted(type_fields[t][k]))}] source={d["source"]} target={d["target"]}  names: {names}\n')
    o.write('\n== odd connections ==\n')
    for x in odd_conn[:200]:
        o.write(f'{x}\n')
    o.write(f'... total odd: {len(odd_conn)}\n')
    o.write('\n== all types ==\n')
    for t, c in type_count.most_common():
        o.write(f'{c:6} {t}\n')
    o.write('\n== per partition: nodes ports conns instances ==\n')
    tn = tp = tc = 0
    for fn, (n, p, c, i) in per_partition.items():
        tn += n; tp += p; tc += c
        o.write(f'{fn}\t{n}\t{p}\t{c}\t{i}\n')
    o.write(f'TOTAL partitions={len(per_partition)} nodes={tn} ports={tp} conns={tc}\n')
print('wrote', OUT)
