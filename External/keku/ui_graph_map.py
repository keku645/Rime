"""Resolve a BF3 UI screen's EBX graph into a readable wiring map.

Every widget, every port, every connection, with guids resolved to names. This is the thing we have been
guessing at all night: what is wired to what, and which ports a widget actually receives events on.

    python graph_map.py <Screen.txt> [<Screen2.txt> ...]
"""

import io
import re
import sys

ROOT = r"F:\Desktop\Venice Unleashed\Venice-EBX-master\UI\Flow\Screen"


def parse(path):
    t = io.open(path, encoding="utf-8").read()
    # split into top-level instances: "<Type> <GUID>" at column 0
    blocks = {}
    order = []
    for m in re.finditer(r"^([A-Za-z]\w*) ([0-9A-F]{8}-[0-9A-F-]+)[^\n]*\n((?:[ \t].*\n|\n)*)", t, re.M):
        kind, guid, body = m.group(1), m.group(2), m.group(3)
        blocks[guid] = (kind, body)
        order.append(guid)
    return t, blocks, order


def field(body, name):
    m = re.search(r"^\s*" + name + r" (.+)$", body, re.M)
    return m.group(1).strip() if m else None


def members(body, section):
    m = re.search(r"^\s*" + section + r"::array\n((?:\s+member\(\d+\).*\n)+)", body, re.M)
    if not m:
        return []
    return re.findall(r"member\(\d+\) ([0-9A-F][0-9A-F-]+)", m.group(1))


def run(path):
    t, blocks, order = parse(path)
    print("=" * 100)
    print(path)
    print("=" * 100)

    ports = {g: b for g, (k, b) in blocks.items() if k == "UINodePort"}
    binds = {g: b for g, (k, b) in blocks.items() if k.endswith("DataBinding")}
    nodes = {g: b for g, (k, b) in blocks.items() if k == "WidgetNode"}

    def port_str(g):
        b = ports.get(g)
        if b is None:
            return "%s(?)" % g[:8]
        return "%s [%s]" % (field(b, "Name"), field(b, "Query"))

    def node_name(g):
        b = blocks.get(g, (None, None))[1]
        if b is None:
            return g[:8]
        inst = field(b, "InstanceName") or field(b, "Name") or "?"
        return "%s(%s)" % (inst, blocks[g][0])

    for g, b in nodes.items():
        inst = field(b, "InstanceName")
        print("\n--- %-22s  widget=%s" % (inst, field(b, "WidgetAsset")))
        print("    Name=%-18s FocusIndex=%-4s ZDepth=%s" % (field(b, "Name"), field(b, "FocusIndex"),
                                                           field(b, "ZDepthLevel")))
        bg = field(b, "DataBinding")
        if bg and bg in binds:
            for m in re.finditer(r"member\(\d+\)::UIDataSourceInfo\n((?:\s{12}.*\n)+)", binds[bg]):
                s = m.group(1)
                print("    BIND  DataName=%-14s DataKey=%-14s direct=%-6s updOnInit=%s"
                      % (field(s, "DataName"), field(s, "DataKey"),
                         field(s, "UseDirectAccess"), field(s, "UpdateOnInitialize")))
                print("          category=%s" % field(s, "DataCategory"))
        elif bg and bg != "*nullGuid*":
            print("    BIND  (%s) %s" % (blocks.get(bg, ("?",))[0], bg[:8]))

        props = re.findall(r"member\(\d+\)::UIWidgetProperty\n\s+Name (.*)\n\s+Value (.*)", b)
        if props:
            print("    PROPS " + " | ".join("%s=%s" % (n.strip(), v.strip()) for n, v in props))

        ins = members(b, "Inputs")
        print("    IN   " + (", ".join(port_str(p) for p in ins) if ins else "(none)"))
        outs = members(b, "Outputs")
        print("    OUT  " + (", ".join(port_str(p) for p in outs) if outs else "(none)"))

    print("\n--- CONNECTIONS ---")
    for g, (k, b) in blocks.items():
        if k != "UINodeConnection":
            continue
        sn, tn = field(b, "SourceNode"), field(b, "TargetNode")
        sp, tp = field(b, "SourcePort"), field(b, "TargetPort")
        print("  %-34s %-28s  ->  %-34s %s"
              % (node_name(sn), port_str(sp), node_name(tn), port_str(tp)))


for a in (sys.argv[1:] or ["CustomizeAccessoriesScreen.txt", "SpawnScreenPC.txt"]):
    run(a if "\\" in a or "/" in a else ROOT + "\\" + a)
