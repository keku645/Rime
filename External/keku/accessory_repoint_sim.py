"""Seam for the framework's accessory re-pointing, without the game.

The Lua walks a weapon's socket objects and, per field (asset1p / asset1pzoom / asset3p), writes the active
package's clone for the mesh the field holds -- and writes the GAME's mesh back when there is no clone for it
(that is what turns a package off, and what rescues a socket left pointing at a package that is gone).

This runs the SAME rule over the real data: the sockets as the game ships them (the studio's accessory
catalogue, read out of the EBX) and the clone names as the generated index.lua carries them. What it can
catch, and what a boot would otherwise: a clone whose mesh name does not match any socket field (the
re-pointing would silently do nothing), a field left holding a clone after the package is turned off, and a
pass that is not idempotent.

usage: python accessory_repoint_sim.py <index.lua> <weapon folder> [expected changed fields]
"""
import json
import re
import sys

INDEX = sys.argv[1]
FOLDER = sys.argv[2].upper()
CATALOG = r"F:\Desktop\Venice Unleashed\Rime-src\RimeCamoStudio\Data\accessories.json"
FIELDS = ("asset1p", "asset1pZoom", "asset3p")   # the catalogue's spelling; the bindings use asset1pzoom

# ---- the index, as the loader reads it ------------------------------------------------------------------
text = open(INDEX, encoding="utf-8").read()
packages = {}            # key -> folder -> mesh(lower) -> clone
vanilla_of_clone = {}    # clone(lower) -> the game's mesh

for block in re.finditer(r"\{\s*key = \"([^\"]+)\"(.*?)\n\t\},", text, re.S):
    key, body = block.group(1).upper(), block.group(2)
    for acc in re.finditer(r"\{ weapon = \"([^\"]+)\", tag = \"([^\"]+)\", meshes = \{(.*?)\} \},", body, re.S):
        weapon, tag, meshes = acc.group(1).upper(), acc.group(2).lower(), acc.group(3)
        for m in re.finditer(r"\{ mesh = \"([^\"]+)\", clone = \"([^\"]+)\" \},", meshes):
            mesh, clone = m.group(1), m.group(2)
            packages.setdefault(key, {}).setdefault(weapon, {}).setdefault(tag, {})[mesh.lower()] = clone
            vanilla_of_clone[clone.lower()] = mesh

if not packages:
    print("FAIL: the index carries no accessory clones at all")
    sys.exit(1)

# ---- the sockets, as the game ships them ----------------------------------------------------------------
weapons = json.loads(open(CATALOG, encoding="utf-8").read())
entry = next((w for w in weapons if w["folder"].upper() == FOLDER), None)
if entry is None:
    print("FAIL: '%s' is not in the accessory catalogue" % FOLDER)
    sys.exit(1)

# One socket object can be reached from several unlocks; the state lives on the object, so they are keyed by
# identity the way the engine holds them (partition + bone + the fields as shipped).
objects = []
seen = set()
for row in entry["rows"]:
    for unlock in row["unlocks"]:
        for socket in unlock.get("sockets", []):
            for obj in socket.get("objects", []):
                ident = (socket["partition"], socket["bone"],
                         tuple((obj.get(f) or {}).get("name", "") for f in FIELDS))
                if ident in seen:
                    continue
                seen.add(ident)
                # The socket's own unlock is what the loader reads off SocketData.unlockAsset: its leaf
                # without the "U_" prefix is the studio's tag for the accessory.
                leaf = unlock["unlock"].split("/")[-1].lower()
                objects.append({"unlock": unlock["unlock"].split("/")[-1], "bone": socket["bone"],
                                "tag": leaf[2:] if leaf.startswith("u_") else leaf,
                                "fields": {f: (obj.get(f) or {}).get("name") for f in FIELDS},
                                "types": {f: (obj.get(f) or {}).get("type") for f in FIELDS}})

print("%s: %d distinct socket object(s), %d field(s) with an asset"
      % (FOLDER, len(objects), sum(1 for o in objects for f in FIELDS if o["fields"][f])))


def repoint(state, key):
    """The Lua's rule, once over every field. Returns (changes, touched clone names)."""
    by_tag = packages.get(key, {}).get(FOLDER, {}) if key else {}
    changes, used = [], set()
    for obj in state:
        wanted = by_tag.get(obj["tag"], {})
        for f in FIELDS:
            name = obj["fields"][f]
            if not name:
                continue
            vanilla = vanilla_of_clone.get(name.lower(), name)
            target = wanted.get(vanilla.lower(), vanilla)
            if target.lower() != name.lower():
                changes.append((obj["unlock"], obj["bone"], f, name, target))
                obj["fields"][f] = target
            if target.lower() in vanilla_of_clone:
                used.add(target.lower())
    return changes, used


KEY = sorted(packages)[0]
problems = []

# pass 1: point them at the package
on, used = repoint(objects, KEY)
print("\npass 1 (package %s): %d field(s) re-pointed" % (KEY, len(on)))
for unlock, bone, field, was, now in on:
    print("  %-18s %-12s %-12s %s -> %s" % (unlock, bone, field, was.split("/")[-1], now.split("/")[-1]))

unused = [c for l_Tag in packages[KEY][FOLDER].values() for c in l_Tag.values() if c.lower() not in used]
if unused:
    problems.append("%d clone(s) no socket field ever names (the mesh name does not match anything the "
                    "weapon holds): %s" % (len(unused), ", ".join(sorted(unused))))

if not on:
    problems.append("nothing was re-pointed at all")

# pass 2: the same pass again must be a no-op
again, _ = repoint(objects, KEY)
print("pass 2 (same package again): %d change(s) -- must be 0" % len(again))
if again:
    problems.append("the pass is not idempotent: %d field(s) changed on the second run" % len(again))

# pass 3: turn it off -- every field goes back to the game's mesh
off, _ = repoint(objects, None)
print("pass 3 (turned off): %d field(s) put back -- must be %d" % (len(off), len(on)))
if len(off) != len(on):
    problems.append("%d field(s) came back of %d re-pointed: a socket would keep a clone that is not loaded"
                    % (len(off), len(on)))

left = [(o["unlock"], f, o["fields"][f]) for o in objects for f in FIELDS
        if o["fields"][f] and o["fields"][f].lower() in vanilla_of_clone]
if left:
    problems.append("after turning it off, %d field(s) still hold a clone: %s" % (len(left), left[:3]))

if len(sys.argv) > 3 and len(on) != int(sys.argv[3]):
    problems.append("expected %s field(s) re-pointed, got %d" % (sys.argv[3], len(on)))

print()
if problems:
    print("ACCSIM: FAIL")
    for p in problems:
        print("  " + p)
    sys.exit(1)

print("ACCSIM: PASS - %d field(s) of %s take package %s, all clones used, idempotent, reversible"
      % (len(on), FOLDER, KEY))
