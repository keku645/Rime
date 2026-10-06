r"""ui_map.py -- the static map of the whole BF3 UI, from the EBX text dump.

    python ui_map.py [<out dir>]        default out dir: F:\Desktop\Venice Unleashed\UI-Map

Reads every partition under Venice-EBX-master\UI\Flow (Screen, Graph, State, Logic, Action, Bundle) plus
UI\Assets (widget assets) and UI\UIComponents, and writes:

    <out>\flow\<Category>\<Partition>.md    one readable map per partition: every node of every type with
                                            its fields, its ports (name + event query), and every connection
                                            resolved to  node.port -> node.port
    <out>\index\node_types.md               census of node types, per category
    <out>\index\widgets.md                  every UIWidgetAsset: its events (the UIWidgetEventIDs it can fire),
                                            how many nodes use it, and which properties those nodes set
    <out>\index\datakeys.md                 every (component, DataKey) with the name the dump annotates and
                                            the DataNames it is bound under, plus where it is used
    <out>\index\actions.md                  every ActionKey used, with its parameters, and where
    <out>\index\screens.md                  every screen: which graphs place it (StateNode), its instance
                                            inputs/outputs, and which widgets it holds
    <out>\index\events.md                   every UIWidgetEventID actually queried by a port, and by which widgets
    <out>\ui_map.json                       the same data, machine-readable (for the Rime port and the builder)

Nothing here touches the game; it is a reading of the shipped data. Values are printed as the dump has
them; nothing is inferred.
"""
import collections
import io
import json
import os
import re
import sys

EBX = r"F:\Desktop\Venice Unleashed\Venice-EBX-master\UI"
OUT = sys.argv[1] if len(sys.argv) > 1 else r"F:\Desktop\Venice Unleashed\UI-Map"

GUID = r"[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}"
BLOCK = re.compile(r"^([A-Za-z]\w*) (" + GUID + r")[^\n]*\n((?:[ \t].*\n|\n)*)", re.M)


# ----------------------------------------------------------------------------------------------- parsing

def read(path):
    return io.open(path, encoding="utf-8", errors="replace").read()


def blocks_of(text):
    """{guid: (type, body)} in file order."""
    out = collections.OrderedDict()
    for m in BLOCK.finditer(text):
        out[m.group(2)] = (m.group(1), m.group(3))
    return out


def field(body, name):
    m = re.search(r"^\s*" + re.escape(name) + r" (.*)$", body, re.M)
    return m.group(1).strip() if m else None


def array(body, name):
    """Guid members of `<name>::array`."""
    m = re.search(r"^\s*" + re.escape(name) + r"::array\n((?:\s+member\(\d+\).*\n(?:\s{3,}.*\n)*)+)", body, re.M)
    if not m:
        return []
    return re.findall(r"member\(\d+\) (" + GUID + ")", m.group(1))


def structs(body, name, struct_type):
    """Bodies of `<name>::array` members of a struct type (indented blocks)."""
    m = re.search(r"^\s*" + re.escape(name) + r"::array\n((?:\s+member\(\d+\)::" + struct_type + r"\n(?:\s{3,}.*\n)*)+)", body, re.M)
    if not m:
        return []
    return [x for x in re.split(r"\s+member\(\d+\)::" + struct_type + r"\n", m.group(1)) if x.strip()]


def scalar_fields(body):
    """Top-level 'Key value' lines of a block (skips $:: base-class lines and array/struct bodies)."""
    out = collections.OrderedDict()
    for line in body.split("\n"):
        m = re.match(r"^\t([A-Za-z]\w*) (.*)$", line)
        if m and not m.group(1).startswith("$"):
            out[m.group(1)] = m.group(2).strip()
        m = re.match(r"^\t\t([A-Za-z]\w*) (.*)$", line)  # base-class fields ($::UINodeData -> Name, ParentGraph...)
        if m and m.group(1) in ("Name", "IsRootNode", "ParentIsScreen", "InstanceName", "Query", "AllowManualRemove"):
            out.setdefault(m.group(1), m.group(2).strip())
    return out


def ref_name(value):
    """'UI/Flow/Screen/X/GUID' -> 'UI/Flow/Screen/X'; '*nullGuid*' -> None."""
    if not value or value.startswith("*null"):
        return None
    m = re.match(r"(.*)/" + GUID + r"$", value)
    return m.group(1) if m else value


# ----------------------------------------------------------------------------------------------- one partition

class Partition:
    def __init__(self, path, category):
        self.path = path
        self.category = category
        self.name = os.path.splitext(os.path.basename(path))[0]
        self.blocks = blocks_of(read(path))
        self.ports = {g: b for g, (k, b) in self.blocks.items() if k in ("UINodePort", "UIInputEventNodePort")}
        self.nodes = collections.OrderedDict((g, (k, b)) for g, (k, b) in self.blocks.items() if k.endswith("Node"))
        self.conns = [b for g, (k, b) in self.blocks.items() if k == "UINodeConnection"]
        self.bindings = {g: (k, b) for g, (k, b) in self.blocks.items() if k.endswith("Binding")}
        self.asset = next(((k, b) for g, (k, b) in self.blocks.items() if k in ("UIScreenAsset", "UIGraphAsset", "UIStateAsset", "UILogicAsset", "UIActionAsset")), None)
        self.port_owner = {}
        for g in self.nodes:
            for sec, pg in self.node_ports(g):
                self.port_owner[pg] = sec

    def port_desc(self, g):
        b = self.ports.get(g)
        if b is None:
            return "%s?" % (g[:8] if g else "-")
        q = field(b, "Query") or ""
        q = q.replace("UIWidgetEventID_", "")
        extra = field(b, "InputEventType")
        label = field(b, "InstanceName") or field(b, "Name") or self.port_owner.get(g, "?")
        s = "%s[%s]" % (label, q if q != "None" else "")
        if extra:
            s += "{%s}" % extra.replace("UIInputAction_", "")
        return s

    def node_desc(self, g):
        k, b = self.nodes.get(g, (None, None))
        if b is None:
            return "%s?" % (g[:8] if g else "-")
        return "%s(%s)" % (field(b, "InstanceName") or field(b, "Name") or "?", k.replace("Node", ""))

    def node_ports(self, g):
        """All port guids owned by a node, whatever the field is called."""
        k, b = self.nodes[g]
        owned = []
        for line in b.split("\n"):
            m = re.match(r"^\t([A-Za-z]\w*) (" + GUID + r")$", line)
            if m and m.group(2) in self.ports:
                owned.append((m.group(1), m.group(2)))
        for sec in ("Inputs", "Outputs", "DataInputs", "Splits"):
            for p in array(b, sec):
                if p in self.ports:
                    owned.append((sec, p))
        return owned


def node_record(part, g):
    k, b = part.nodes[g]
    rec = {"type": k, "guid": g, "fields": {}}
    for key, val in scalar_fields(b).items():
        if re.fullmatch(GUID, val) and val in part.ports:
            continue  # ports are listed separately
        rec["fields"][key] = val
    rec["ports"] = [(sec, part.port_desc(p), p) for sec, p in part.node_ports(g)]
    # WidgetNode specifics
    if k == "WidgetNode":
        rec["widget"] = ref_name(field(b, "WidgetAsset"))
        rec["props"] = [(field(s, "Name"), field(s, "Value")) for s in structs(b, "WidgetProperties", "UIWidgetProperty")]
        bg = field(b, "DataBinding")
        rec["binding"] = None
        if bg and bg in part.bindings:
            bk, bb = part.bindings[bg]
            srcs = []
            for s in re.finditer(r"::UIDataSourceInfo\n((?:\s{2,}.*\n)+?)(?=\s*(?:member\(|\w+ |\Z))", bb):
                sb = s.group(1)
                srcs.append({"DataName": field(sb, "DataName"), "DataKey": field(sb, "DataKey"),
                             "DataCategory": ref_name(field(sb, "DataCategory")),
                             "UseDirectAccess": field(sb, "UseDirectAccess"), "UpdateOnInitialize": field(sb, "UpdateOnInitialize")})
            rec["binding"] = {"type": bk, "sources": srcs, "fields": {kk: vv for kk, vv in scalar_fields(bb).items()}}
    if k in ("DataSetNode", "DataGetNode", "RefreshNode", "DataToggleNode", "DataStepNode", "DataIncrementNode", "ComparisonLogicNode"):
        m = re.search(r"DataSource::UIDataSourceInfo\n((?:\s{2,}.*\n)+)", b)
        if m:
            sb = m.group(1)
            rec["datasource"] = {"DataName": field(sb, "DataName"), "DataKey": field(sb, "DataKey"), "DataCategory": ref_name(field(sb, "DataCategory"))}
    if k == "DialogNode":
        rec["buttons"] = [(field(s, "InputConcept"), field(s, "Label")) for s in structs(b, "Buttons", "UIPopupButton")]
    return rec


def partition_record(part):
    rec = {"name": part.name, "category": part.category, "asset": part.asset[0] if part.asset else None,
           "nodes": [node_record(part, g) for g in part.nodes], "connections": []}
    for cb in part.conns:
        rec["connections"].append({
            "from": part.node_desc(field(cb, "SourceNode")), "fromPort": part.port_desc(field(cb, "SourcePort")),
            "to": part.node_desc(field(cb, "TargetNode")), "toPort": part.port_desc(field(cb, "TargetPort")),
            "pop": field(cb, "NumScreensToPop")})
    return rec


def write_partition_md(rec, path):
    L = ["# %s  (%s, %s)" % (rec["name"], rec["category"], rec["asset"] or "?"), ""]
    L.append("%d nodes, %d connections" % (len(rec["nodes"]), len(rec["connections"])))
    L.append("")
    L.append("## Nodes")
    for n in rec["nodes"]:
        f = n["fields"]
        head = "### %s  `%s`" % (f.get("InstanceName") or f.get("Name") or "?", n["type"])
        L.append(head)
        if n["type"] == "WidgetNode":
            L.append("- widget: `%s`" % n["widget"])
            if n["props"]:
                L.append("- props: " + ", ".join("`%s=%s`" % p for p in n["props"]))
            if n["binding"]:
                L.append("- binding: `%s`" % n["binding"]["type"])
                for s in n["binding"]["sources"]:
                    L.append("  - `%s` key `%s` @ `%s` direct=%s init=%s" % (s["DataName"], s["DataKey"], s["DataCategory"], s["UseDirectAccess"], s["UpdateOnInitialize"]))
                extra = {k: v for k, v in n["binding"]["fields"].items() if not re.fullmatch(GUID, v or "")}
                if extra:
                    L.append("  - fields: " + ", ".join("`%s=%s`" % kv for kv in extra.items()))
        else:
            shown = {k: v for k, v in f.items() if k not in ("Name", "InstanceName", "ParentGraph", "IsRootNode", "ParentIsScreen")}
            if shown:
                L.append("- " + ", ".join("`%s=%s`" % kv for kv in shown.items()))
            if "datasource" in n:
                L.append("- data: `%s` key `%s` @ `%s`" % (n["datasource"]["DataName"], n["datasource"]["DataKey"], n["datasource"]["DataCategory"]))
            if "buttons" in n:
                L.append("- buttons: " + ", ".join("`%s:%s`" % b for b in n["buttons"]))
        if n["ports"]:
            L.append("- ports: " + ", ".join("%s=%s" % (sec, d) for sec, d, _ in n["ports"]))
        L.append("")
    L.append("## Connections")
    for c in rec["connections"]:
        pop = ("  (pop %s)" % c["pop"]) if c["pop"] not in (None, "0") else ""
        L.append("- %s . %s  ->  %s . %s%s" % (c["from"], c["fromPort"], c["to"], c["toPort"], pop))
    io.open(path, "w", encoding="utf-8", newline="\n").write("\n".join(L) + "\n")


# ----------------------------------------------------------------------------------------------- assets & indexes

def widget_assets():
    out = {}
    root = os.path.join(EBX, "Assets")
    for fn in sorted(os.listdir(root)):
        if not fn.endswith(".txt"):
            continue
        t = read(os.path.join(root, fn))
        b = blocks_of(t)
        for g, (k, body) in b.items():
            if k == "UIWidgetAsset":
                pairs = []
                for s_ in structs(body, "WidgetEvents", "WidgetEventQueryPair"):
                    pairs.append({"name": field(s_, "Name"), "query": (field(s_, "Query") or "").replace("UIWidgetEventID_", ""),
                                  "output": field(s_, "IsOutput") == "True"})
                out["UI/Assets/" + fn[:-4]] = {"guid": g, "name": field(body, "Name"),
                                               "events": ["%s%s%s" % ("<-" if not e["output"] else "", e["name"], ("->" + e["query"]) if e["query"] and e["query"] != e["name"] else "") for e in pairs],
                                               "pairs": pairs}
    return out


def main():
    os.makedirs(OUT, exist_ok=True)
    flow = os.path.join(EBX, "Flow")
    parts = []
    for cat in sorted(os.listdir(flow)):
        d = os.path.join(flow, cat)
        if not os.path.isdir(d):
            continue
        for dirpath, _, files in os.walk(d):
            for fn in sorted(files):
                if fn.endswith(".txt"):
                    parts.append(Partition(os.path.join(dirpath, fn), cat))
    print("partitions:", len(parts))

    records = []
    census = collections.defaultdict(collections.Counter)
    widget_use = collections.defaultdict(lambda: {"count": 0, "props": collections.Counter(), "screens": set()})
    datakeys = collections.defaultdict(lambda: {"names": set(), "dataNames": collections.Counter(), "where": set()})
    actions = collections.defaultdict(lambda: {"params": collections.Counter(), "where": set()})
    events = collections.defaultdict(lambda: {"widgets": set(), "count": 0})
    screens = collections.defaultdict(lambda: {"placed_by": set(), "widgets": [], "inputs": [], "outputs": [], "category": ""})

    for part in parts:
        rec = partition_record(part)
        records.append(rec)
        rel = os.path.relpath(part.path, flow)[:-4]
        out_md = os.path.join(OUT, "flow", rel + ".md")
        os.makedirs(os.path.dirname(out_md), exist_ok=True)
        write_partition_md(rec, out_md)
        key = "UI/Flow/" + rel.replace("\\", "/")
        screens[key]["category"] = part.category
        for n in rec["nodes"]:
            census[part.category][n["type"]] += 1
            f = n["fields"]
            if n["type"] == "WidgetNode":
                w = n["widget"]
                widget_use[w]["count"] += 1
                widget_use[w]["screens"].add(key)
                for pn, pv in n["props"]:
                    widget_use[w]["props"][pn] += 1
                screens[key]["widgets"].append((f.get("InstanceName"), w))
                if n["binding"]:
                    for s in n["binding"]["sources"]:
                        if s["DataKey"]:
                            dk = s["DataKey"].split(" ")[0]
                            nm = re.search(r"\((.*)\)", s["DataKey"])
                            e = datakeys[(s["DataCategory"], dk)]
                            if nm:
                                e["names"].add(nm.group(1))
                            e["dataNames"][s["DataName"]] += 1
                            e["where"].add(key)
                for sec, d, pg in n["ports"]:
                    m = re.search(r"\[(\w+)\]", d)
                    if m and m.group(1):
                        events[m.group(1)]["widgets"].add(w)
                        events[m.group(1)]["count"] += 1
            if n["type"] == "InstanceInputNode":
                screens[key]["inputs"].append(f.get("Name"))
            if n["type"] == "InstanceOutputNode":
                screens[key]["outputs"].append(f.get("Name"))
            if n["type"] in ("StateNode", "DialogNode"):
                scr = ref_name(f.get("Screen"))
                if scr:
                    screens[scr]["placed_by"].add(key)
            if n["type"] == "ActionNode":
                ak = f.get("ActionKey", "?")
                actions[ak]["where"].add(key)
                for p in re.findall(r"member\(\d+\) (.*)", part.nodes[n["guid"]][1].split("Params::array")[1].split("AppendIncomingParams")[0]) if "Params::array" in part.nodes[n["guid"]][1] else []:
                    actions[ak]["params"][p.strip()] += 1
            if "datasource" in n and n["datasource"]["DataKey"]:
                dk = n["datasource"]["DataKey"].split(" ")[0]
                nm = re.search(r"\((.*)\)", n["datasource"]["DataKey"])
                e = datakeys[(n["datasource"]["DataCategory"], dk)]
                if nm:
                    e["names"].add(nm.group(1))
                e["dataNames"][n["type"]] += 1
                e["where"].add(key)

    assets = widget_assets()
    idx = os.path.join(OUT, "index")
    os.makedirs(idx, exist_ok=True)

    L = ["# Node types by category", ""]
    for cat in sorted(census):
        L.append("## %s" % cat)
        for t, c in census[cat].most_common():
            L.append("- %s: %d" % (t, c))
        L.append("")
    io.open(os.path.join(idx, "node_types.md"), "w", encoding="utf-8", newline="\n").write("\n".join(L))

    L = ["# Widgets: assets, the events they can fire, and how screens use them", ""]
    for w in sorted(set(assets) | set(widget_use), key=lambda x: -widget_use[x]["count"] if x in widget_use else 0):
        a = assets.get(w, {})
        u = widget_use.get(w, {"count": 0, "props": collections.Counter(), "screens": set()})
        L.append("## %s  (%d uses)" % (w, u["count"]))
        L.append("- events: " + (", ".join(a.get("events", [])) or "(none / asset not found)"))
        if u["props"]:
            L.append("- properties set by nodes: " + ", ".join("%s(%d)" % kv for kv in u["props"].most_common()))
        if u["screens"]:
            L.append("- used in: " + ", ".join(sorted(u["screens"])[:12]) + (" ..." if len(u["screens"]) > 12 else ""))
        L.append("")
    io.open(os.path.join(idx, "widgets.md"), "w", encoding="utf-8", newline="\n").write("\n".join(L))

    L = ["# Data keys per component (as bound by widgets and data nodes)", ""]
    bycomp = collections.defaultdict(list)
    for (comp, dk), e in datakeys.items():
        bycomp[comp].append((dk, e))
    for comp in sorted(bycomp, key=lambda c: c or ""):
        L.append("## %s" % comp)
        for dk, e in sorted(bycomp[comp], key=lambda x: int(x[0]) if re.fullmatch(r"-?\d+", x[0]) else 0):
            L.append("- `%s` %s  bound as: %s  | in %d partition(s): %s" % (
                dk, ("(%s)" % "/".join(sorted(e["names"]))) if e["names"] else "",
                ", ".join("%s(%d)" % kv for kv in e["dataNames"].most_common()), len(e["where"]),
                ", ".join(sorted(e["where"])[:6]) + (" ..." if len(e["where"]) > 6 else "")))
        L.append("")
    io.open(os.path.join(idx, "datakeys.md"), "w", encoding="utf-8", newline="\n").write("\n".join(L))

    L = ["# Actions (ActionNode.ActionKey)", ""]
    for ak, e in sorted(actions.items(), key=lambda kv: -len(kv[1]["where"])):
        L.append("- `%s`  in %d partition(s)%s" % (ak, len(e["where"]), ("  params: " + ", ".join("%s(%d)" % kv for kv in e["params"].most_common(8))) if e["params"] else ""))
    io.open(os.path.join(idx, "actions.md"), "w", encoding="utf-8", newline="\n").write("\n".join(L))

    L = ["# Widget events actually queried by ports", ""]
    for ev, e in sorted(events.items(), key=lambda kv: -kv[1]["count"]):
        L.append("- `%s`  %d port(s)  widgets: %s" % (ev, e["count"], ", ".join(sorted(x or "?" for x in e["widgets"]))))
    io.open(os.path.join(idx, "events.md"), "w", encoding="utf-8", newline="\n").write("\n".join(L))

    L = ["# Screens and graphs", ""]
    for s in sorted(screens):
        e = screens[s]
        L.append("## %s  (%s)" % (s, e["category"]))
        if e["placed_by"]:
            L.append("- placed by: " + ", ".join(sorted(e["placed_by"])))
        if e["inputs"]:
            L.append("- inputs: " + ", ".join(x or "?" for x in e["inputs"]))
        if e["outputs"]:
            L.append("- outputs: " + ", ".join(x or "?" for x in e["outputs"]))
        if e["widgets"]:
            L.append("- widgets: " + ", ".join("%s:%s" % (n, (w or "?").replace("UI/Assets/", "")) for n, w in e["widgets"]))
        L.append("")
    io.open(os.path.join(idx, "screens.md"), "w", encoding="utf-8", newline="\n").write("\n".join(L))

    def jsonable(o):
        if isinstance(o, set):
            return sorted(o)
        if isinstance(o, collections.Counter):
            return dict(o)
        if isinstance(o, tuple):
            return list(o)
        raise TypeError(type(o))
    with io.open(os.path.join(OUT, "ui_map.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"partitions": records, "widgets": assets,
                   "widget_use": {k: {"count": v["count"], "props": dict(v["props"]), "screens": sorted(v["screens"])} for k, v in widget_use.items()},
                   "datakeys": [{"component": c, "key": k, "names": sorted(e["names"]), "dataNames": dict(e["dataNames"]), "where": sorted(e["where"])} for (c, k), e in datakeys.items()],
                   "actions": {k: {"params": dict(v["params"]), "where": sorted(v["where"])} for k, v in actions.items()},
                   "events": {k: {"widgets": sorted(x or "?" for x in v["widgets"]), "count": v["count"]} for k, v in events.items()},
                   "screens": {k: {"category": v["category"], "placed_by": sorted(v["placed_by"]), "inputs": v["inputs"], "outputs": v["outputs"], "widgets": v["widgets"]} for k, v in screens.items()}},
                  f, indent=1, default=jsonable)
    print("written to", OUT)
    print("node types:", sum(len(c) for c in census.values()), "| widgets used:", len(widget_use), "| datakeys:", len(datakeys), "| actions:", len(actions), "| events:", len(events))


if __name__ == "__main__":
    main()
