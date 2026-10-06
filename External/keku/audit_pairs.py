"""What connects to what in the shipped UI graphs (diagnostic oracle for the editor's wiring rules).

Over every ui/flow/* partition in the editor cache, for each UINodeConnection:
  source = (owner node type, port field, port kind)  ->  target = (owner node type, port field, port kind)
where port kind is the widget event query for WidgetNode ports (OnItemReleased...), the UIInputAction for
UIInputEventNodePorts, else '-'. Also: self-wires, what feeds DataInputs, what feeds widget inputs,
fan-out per source port, fan-in per target port.
"""
import json, os, sys, collections

CACHE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), 'rue_selftest', 'cache', '1f0bbddc1b0e')
PART = os.path.join(CACHE, 'partitions')
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), 'audit_pairs.txt')

def ref_guid(v):
    return v.get('InstanceGuid') if isinstance(v, dict) else None

pairs = collections.Counter()            # (srcType, srcField, srcKind, dstType, dstField, dstKind)
slot_pairs = collections.Counter()       # (srcType, srcField, dstType, dstField)
self_wires = 0
self_examples = []
data_inputs_from = collections.Counter()
widget_inputs_from = collections.Counter()
widget_outputs_to = collections.Counter()
fan_out = collections.Counter()
fan_in = collections.Counter()
total = 0
src_types = collections.Counter(); dst_types = collections.Counter()
per_widget_event_dir = collections.defaultdict(collections.Counter)   # query -> Counter(source/target)
graphs_with_state = 0

for fn in sorted(os.listdir(PART)):
    if not (fn.startswith('ui_flow_') or (fn.startswith('ui_xp') and '_flow_' in fn)):   # base game + the expansions' ui/xp5/flow
        continue
    with open(os.path.join(PART, fn), encoding='utf-8') as f:
        j = json.load(f)
    inst = j['Instances']
    types = {g: (i.get('$type') or '') for g, i in inst.items()}
    port_owner = {}
    for g, i in inst.items():
        t = types[g]
        if 'NodePort' in t or t == 'UINodeConnection':
            continue
        for k, v in i.items():
            if k == '$type':
                continue
            if isinstance(v, dict) and ref_guid(v) and 'NodePort' in types.get(ref_guid(v), ''):
                port_owner[ref_guid(v)] = (t, k, g)
            elif isinstance(v, list):
                for e in v:
                    if isinstance(e, dict) and ref_guid(e) and 'NodePort' in types.get(ref_guid(e), ''):
                        port_owner[ref_guid(e)] = (t, k, g)
    def kind_of(pg):
        p = inst.get(pg, {})
        q = str(p.get('Query', '')).replace('UIWidgetEventID_', '')
        ie = str(p.get('InputEventType', '')).replace('UIInputAction_', '')
        if types.get(pg) == 'UIInputEventNodePort':
            return 'action:' + ie
        return q if q and q != 'None' else '-'
    for g, i in inst.items():
        if types[g] != 'UINodeConnection':
            continue
        sn, sp, tn, tp = ref_guid(i.get('SourceNode')), ref_guid(i.get('SourcePort')), ref_guid(i.get('TargetNode')), ref_guid(i.get('TargetPort'))
        if sp not in port_owner or tp not in port_owner:
            continue
        st, sf, sowner = port_owner[sp]; tt, tf, towner = port_owner[tp]
        sk = kind_of(sp) if st == 'WidgetNode' or types.get(sp) == 'UIInputEventNodePort' else '-'
        tk = kind_of(tp) if tt == 'WidgetNode' else '-'
        total += 1
        pairs[(st, sf, sk, tt, tf, tk)] += 1
        slot_pairs[(st, sf, tt, tf)] += 1
        src_types[st] += 1; dst_types[tt] += 1
        fan_out[sp] += 1; fan_in[tp] += 1
        if sn == tn:
            self_wires += 1
            if len(self_examples) < 10: self_examples.append((fn, st, sf, sk, tf, tk))
        if tf == 'DataInputs':
            data_inputs_from[(st, sf, sk)] += 1
        if tt == 'WidgetNode':
            widget_inputs_from[(st, sf, sk, tk)] += 1
            per_widget_event_dir[tk]['target'] += 1
        if st == 'WidgetNode':
            widget_outputs_to[(sk, tt, tf)] += 1
            per_widget_event_dir[sk]['source'] += 1

with open(OUT, 'w', encoding='utf-8') as o:
    o.write(f'TOTAL connections with both ports owned: {total}\n')
    o.write(f'self-wires (source node == target node): {self_wires}  examples: {self_examples}\n\n')
    o.write('== slot pairs (srcType.srcField -> dstType.dstField) ==\n')
    for (st, sf, tt, tf), c in sorted(slot_pairs.items(), key=lambda x: -x[1]):
        o.write(f'{c:6}  {st}.{sf} -> {tt}.{tf}\n')
    o.write('\n== what feeds DataInputs ==\n')
    for k, c in data_inputs_from.most_common():
        o.write(f'{c:6}  {k}\n')
    o.write('\n== what feeds widget inputs (srcType, srcField, srcKind, targetEvent) ==\n')
    for k, c in widget_inputs_from.most_common():
        o.write(f'{c:6}  {k}\n')
    o.write('\n== where widget outputs go (event, dstType, dstField) ==\n')
    for k, c in widget_outputs_to.most_common():
        o.write(f'{c:6}  {k}\n')
    o.write('\n== widget events: as source vs as target ==\n')
    for q, c in sorted(per_widget_event_dir.items()):
        o.write(f'{q}: {dict(c)}\n')
    o.write('\n== fan-out distribution (connections per source port) ==\n')
    o.write(f'{collections.Counter(fan_out.values())}\n')
    o.write('== fan-in distribution (connections per target port) ==\n')
    o.write(f'{collections.Counter(fan_in.values())}\n')
    o.write('\n== source node types ==\n')
    o.write(f'{src_types.most_common()}\n')
    o.write('== target node types ==\n')
    o.write(f'{dst_types.most_common()}\n')
    o.write('\n== full pairs (with widget event kinds) ==\n')
    for k, c in sorted(pairs.items(), key=lambda x: -x[1]):
        o.write(f'{c:6}  {k}\n')
print('wrote', OUT)
