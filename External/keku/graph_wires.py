"""Print a UI flow graph's nodes and wires with names, from the EBX dump.

Used to answer: WHO stores a weapon's accessory choice, and WHEN -- the accessories screen writes
CustSelectedAccessoryN, but something has to turn that into the player's loadout, and the row comes back
wearing the old accessory, so that step is not happening when we think it is.

usage: python graph_wires.py <partition path under EBX-Json> [name filter]
"""
import json
import sys

PATH = sys.argv[1]
FILTER = (sys.argv[2] if len(sys.argv) > 2 else "").lower()

d = json.loads(open(PATH, encoding="utf-8").read())
byg = {str(i["$guid"]).lower(): i for i in d["$instances"]}


def val(x):
    return x.get("$value") if isinstance(x, dict) else x


def ref(r):
    return byg.get(str(r.get("$instanceGuid", "")).lower()) if isinstance(r, dict) else None


def name(i):
    if i is None:
        return "?"
    f = i.get("$fields", {})
    for k in ("InstanceName", "Name", "DebugName"):
        v = val(f.get(k, {}))
        if v:
            return str(v)
    return i["$type"]


def port_name(p):
    if p is None:
        return "?"
    f = p.get("$fields", {})
    return "%s" % (val(f.get("Name", {})) or val(f.get("InstanceName", {})) or "?")


# nodes of interest
print("=== nodes")
for i in d["$instances"]:
    t = i["$type"]
    if not (t.endswith("Node") or t == "UIStateNode"):
        continue
    n = name(i)
    if FILTER and FILTER not in n.lower() and FILTER not in t.lower():
        continue
    extra = ""
    f = i.get("$fields", {})
    if "ActionKey" in f:
        extra = " ActionKey=%s Params=%s" % (val(f["ActionKey"]), json.dumps(val(f.get("Params", {}))))
    if "Screen" in f and isinstance(f["Screen"], dict):
        extra += " Screen=%s" % json.dumps(val(f["Screen"]))[:80]
    print("  %-34s %-22s%s" % (n, t, extra))

print("\n=== wires")
for c in d["$instances"]:
    if "Connection" not in c["$type"]:
        continue
    f = {k: val(v) for k, v in c["$fields"].items()}
    sn, sp = ref(f.get("SourceNode")), ref(f.get("SourcePort"))
    tn, tp = ref(f.get("TargetNode")), ref(f.get("TargetPort"))
    line = "%s.%s -> %s.%s" % (name(sn), port_name(sp), name(tn), port_name(tp))
    pops = f.get("NumScreensToPop")
    if pops:
        line += "  (pop %s)" % pops
    if FILTER and FILTER not in line.lower():
        continue
    print("  " + line)
