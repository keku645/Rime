"""What generalising accessory camos COSTS, measured without the game.

The seam next door (accessory_unlock_sim.py) answers "would this one weapon work?". This one answers the
question that decides whether the whole arsenal is viable, and it answers it for EVERY package in the index
at once, because the cost is paid per package:

  * PER LEVEL, the loader builds one twin unlock per (package, weapon, accessory) and copies under it the
    weapon's socket(s), their socket objects, the modifier entry and the sway modifier. Those are engine
    objects created on BOTH realms on every level load -- the number nobody has measured yet.
  * THE TABLE the accessory screens read rides inside ONE borrowed description as text. Its length is the
    other ceiling: one entry per twin, "<id>~<name>~~<thumbnail>~<text>~<base id>".

Both are printed per package, as a total, and projected onto the whole catalogue (every accessory unlock of
every weapon that has a mesh), which is what "all" would mean.

usage: python accessory_scale_sim.py <index.lua> [<accessories.json>]
"""
import json
import os
import re
import sys

INDEX = sys.argv[1]
CATALOG_PATH = sys.argv[2] if len(sys.argv) > 2 else \
    r"F:\Desktop\Venice Unleashed\Rime-src\RimeCamoStudio\Data\accessories.json"
EBX = r"F:\Desktop\Venice Unleashed\EBX-Json"
FIELDS = ("asset1p", "asset1pZoom", "asset3p")
PREFIX = "Weapons/Custom/U_ACC_"
MARKER = "@camotable;"


def identifier_of(name):
    """The framework's IdentifierOf: djb2-XOR over the name AS WRITTEN (case-sensitive)."""
    h = 0x1505
    for ch in name:
        h = ((h * 0x21) & 0xFFFFFFFF) ^ ord(ch)
        h &= 0xFFFFFFFF
    return h


def table_field(text):
    """AccessoryTableField: the table's own separators replaced, never dropped."""
    return re.sub(r"[~;|:]", " ", "" if text is None else str(text))


def value(field):
    return field.get("$value") if isinstance(field, dict) else field


# ---- the packages, from the generated index --------------------------------------------------------------
text = open(INDEX, encoding="utf-8", newline="").read()
packages = []
for block in re.finditer(r"\{\s*key = \"([^\"]+)\"(.*?)\n\t\},", text, re.S):
    key, body = block.group(1), block.group(2)
    accessories = [(m.group(1), m.group(2),
                    {a.group(1).lower(): a.group(2)
                     for a in re.finditer(r"\{ mesh = \"([^\"]+)\", clone = \"([^\"]+)\" \},", m.group(3))})
                   for m in re.finditer(r"\{ weapon = \"([^\"]+)\", tag = \"([^\"]+)\", meshes = \{(.*?)\} \},",
                                        body, re.S)]
    if not accessories:
        continue

    def field(name, default=""):
        # the index is written CRLF: a pattern that ends at "\n" matches nothing there (and every field would
        # silently fall back to the key, which made the table look a quarter of its real size)
        m = re.search(r"\n\t\t%s = (nil|\"(.*?)\"),\r?\n" % name, body, re.S)
        if m is None or m.group(1) == "nil":
            return default
        return m.group(2)

    packages.append({"key": key, "name": field("name", key), "text": field("text"),
                     "thumbnail": field("thumbnail"), "accessories": accessories})

if not packages:
    print("FAIL: no package in the index ships accessory clones")
    sys.exit(1)

# ---- the catalogue: every accessory unlock of every weapon, and which of them have meshes ------------------
CATALOG = json.loads(open(CATALOG_PATH, encoding="utf-8", newline="").read())
catalog_of = {}
for entry in CATALOG:
    catalog_of[entry["folder"].upper()] = entry

arsenal_unlocks, arsenal_with_mesh = 0, 0
for entry in CATALOG:
    for row in entry["rows"]:
        for unlock in row["unlocks"]:
            arsenal_unlocks += 1
            if unlock.get("meshes"):
                arsenal_with_mesh += 1

# ---- the weapons' own data -------------------------------------------------------------------------------
weapon_cache = {}


def weapon_data(folder):
    """Every partition under Weapons/<folder>, plus the SoldierWeaponData of the weapon."""
    if folder in weapon_cache:
        return weapon_cache[folder]
    parts = {}
    directory = os.path.join(EBX, "Weapons", folder)
    if os.path.isdir(directory):
        for name in os.listdir(directory):
            if name.endswith(".json"):
                parts[name] = json.loads(open(os.path.join(directory, name), encoding="utf-8",
                                              newline="").read())
    found = None
    for name, data in parts.items():
        for inst in data.get("$instances", []):
            if inst["$type"] == "SoldierWeaponData":
                found = (name, data, inst)
    weapon_cache[folder] = (parts, found)
    return weapon_cache[folder]


# Every unlock of every weapon, by reference and by identifier. It has to be the WHOLE tree, not the weapon's
# own folder: a socket can name an unlock that lives elsewhere (Weapons/Common/NoOptics is the weapon's iron
# sights), and a folder-blind resolver reports that accessory as "no socket names it", which is a lie.
UNLOCK_REF = {}
TAKEN = {}
for root, _dirs, files in os.walk(os.path.join(EBX, "Weapons")):
    for f in files:
        if not f.endswith(".json"):
            continue
        try:
            data = json.loads(open(os.path.join(root, f), encoding="utf-8", newline="").read())
        except Exception:
            continue
        if not isinstance(data, dict):
            continue
        guid = str(data.get("$guid", "")).lower()
        for inst in data.get("$instances", []):
            if inst["$type"] not in ("UnlockAsset", "SoldierWeaponUnlockAsset"):
                continue
            name = value(inst["$fields"].get("Name", {}))
            ident = value(inst["$fields"].get("Identifier", {}))
            UNLOCK_REF[(guid, str(inst["$guid"]).lower())] = (name, ident)
            if ident is not None:
                TAKEN.setdefault(int(ident), name or f)


def unlock_at(parts, ref):
    """The (Name, Identifier) of the UnlockAsset a reference points at (parts kept for call-site symmetry)."""
    if not isinstance(ref, dict):
        return None
    del parts
    return UNLOCK_REF.get((str(ref.get("$partitionGuid", "")).lower(),
                           str(ref.get("$instanceGuid", "")).lower()))


def tag_of(name):
    leaf = str(name).split("/")[-1].lower()
    return leaf[2:] if leaf.startswith("u_") else leaf


# ---- the plan, package by package ------------------------------------------------------------------------
problems = []
entries = []
grand = {"twins": 0, "sockets": 0, "objects": 0, "transforms": 0, "modifiers": 0, "sway": 0, "fields": 0}

for package in packages:
    per = {"twins": 0, "sockets": 0, "objects": 0, "transforms": 0, "modifiers": 0, "sway": 0, "fields": 0}
    print("package %s (\"%s\"): %d accessor(ies)" % (package["key"], package["name"],
                                                     len(package["accessories"])))

    for weapon, tag, clones in package["accessories"]:
        folder = weapon.upper()
        parts, found = weapon_data(folder)
        if found is None:
            problems.append("%s/%s: no SoldierWeaponData under Weapons/%s" % (package["key"], tag, weapon))
            continue
        w_file, w_data, w_inst = found
        short = tag_of(tag)

        sockets, vanilla, objects, transforms = [], None, 0, 0
        for inst in w_data["$instances"]:
            if inst["$type"] != "SocketData":
                continue
            got = unlock_at(parts, value(inst["$fields"].get("UnlockAsset", {})))
            if got and tag_of(got[0]) == short:
                vanilla = got
                sockets.append(inst)
                # each available object is copied, and a rigid one copies its 3P transforms one by one
                for obj in value(inst["$fields"].get("AvailableObjects", [])) or []:
                    objects += 1
                    p, i = str(obj.get("$partitionGuid", "")).lower(), str(obj.get("$instanceGuid", "")).lower()
                    for _n, d in parts.items():
                        if str(d.get("$guid", "")).lower() != p:
                            continue
                        for other in d.get("$instances", []):
                            if str(other["$guid"]).lower() == i and \
                                    other["$type"] == "WeaponRegularSocketObjectData":
                                transforms += len(value(other["$fields"].get("Mesh3pTransforms", [])) or [])

        if vanilla is None:
            problems.append("%s/%s: no socket of %s names that accessory" % (package["key"], tag, folder))
            continue

        # which socket fields would take a clone (the meshes live outside the weapon's folder: the studio's
        # catalogue, built from these same sockets, is what knows them)
        used, fields = set(), 0
        for row in catalog_of.get(folder, {}).get("rows", []):
            for unlock in row["unlocks"]:
                if tag_of(unlock["unlock"]) != short:
                    continue
                for socket in unlock.get("sockets", []):
                    for obj in socket.get("objects", []):
                        for f in FIELDS:
                            name = ((obj.get(f) or {}).get("name") or "").lower()
                            if name and name in clones:
                                used.add(name)
                                fields += 1

        mods = 0
        for entry in value(w_inst["$fields"]["WeaponModifierData"]) or []:
            got = unlock_at(parts, value(entry.get("UnlockAsset", {})))
            if got and got[1] == vanilla[1]:
                mods += 1

        sway = 0
        for _name, d in parts.items():
            for inst in d.get("$instances", []):
                if inst["$type"] == "GunSwayModifierData":
                    got = unlock_at(parts, value(inst["$fields"].get("UnlockAsset", {})))
                    if got and got[1] == vanilla[1]:
                        sway += 1

        twin = PREFIX + package["key"].upper() + "_" + folder + "_" + short.upper()
        ident = identifier_of(twin)
        if ident in TAKEN:
            problems.append("%s/%s: the twin's identifier %d is already %s" %
                            (package["key"], tag, ident, TAKEN[ident]))

        per["twins"] += 1
        per["sockets"] += len(sockets)
        per["objects"] += objects
        per["transforms"] += transforms
        per["modifiers"] += mods
        per["sway"] += sway
        per["fields"] += fields

        entries.append((package["key"], str(ident), str(vanilla[1]),
                        "~".join([str(ident), table_field(package["name"]), "",
                                  table_field(package["thumbnail"]), table_field(package["text"]),
                                  str(vanilla[1])])))

        if fields == 0:
            problems.append("%s/%s: no socket field would take a clone" % (package["key"], tag))
        missing = [c for m, c in clones.items() if m not in used]
        if missing:
            problems.append("%s/%s: %d clone(s) never used" % (package["key"], tag, len(missing)))

    print("   per level: %d twin unlock(s), %d socket(s) copied, %d socket object(s), %d transform(s), "
          "%d modifier entr(ies), %d sway clone(s), %d field(s) repointed"
          % (per["twins"], per["sockets"], per["objects"], per["transforms"], per["modifiers"], per["sway"],
             per["fields"]))
    for k in grand:
        grand[k] += per[k]

old_table = MARKER + ";".join(e[3] for e in entries)

# what the framework writes now: one header per package, then "<id>~<base>" per twin
parts, seen = [], None
for key, ident, base, _old in entries:
    if key != seen:
        seen = key
        package = next(p for p in packages if p["key"] == key)
        parts.append("~".join(["P", table_field(package["name"]), "",
                               table_field(package["thumbnail"]), table_field(package["text"])]))
    parts.append(ident + "~" + base)
table = MARKER + ";".join(parts)
per_entry = (len(table) - len(MARKER)) / float(len(entries)) if entries else 0

print()
print("TOTAL per level, every package in this index: %d twin unlock(s), %d socket(s), %d socket object(s), "
      "%d transform(s), %d modifier entr(ies), %d sway clone(s)"
      % (grand["twins"], grand["sockets"], grand["objects"], grand["transforms"], grand["modifiers"],
         grand["sway"]))
print("TABLE: %d camo(s), %d characters (%.1f per camo) in ONE borrowed description -- the first shape, one full entry "
      "per camo, took %d (%.1f each)"
      % (len(entries), len(table), per_entry, len(old_table),
         (len(old_table) - len(MARKER)) / float(len(entries)) if entries else 0))
print("CATALOGUE: %d accessory unlock(s) in the game's weapons, %d of them with a mesh; %d unlock identifier(s) "
      "taken by the game" % (arsenal_unlocks, arsenal_with_mesh, len(TAKEN)))
# what actually scales is the "<id>~<base>;" of each camo: the package header is said once, however many camos it has
per_camo = sum(len(e[1]) + len(e[2]) + 2 for e in entries) / float(len(entries)) if entries else 0
print("PROJECTION for one camo on EVERY accessory of EVERY weapon: ~%d twin(s) per level and ~%d characters of table "
      "(%.0f KB, %.0f per camo + one header) -- per camo package"
      % (arsenal_with_mesh, arsenal_with_mesh * per_camo, arsenal_with_mesh * per_camo / 1024.0, per_camo))

# and the text is parsed back with the screen's own rules, so a shape the script cannot read is caught here
parsed, pkg = {}, {"name": "", "family": "", "texture": "", "desc": ""}
for record in table[len(MARKER):].split(";"):
    f = record.split("~")
    if f[0] == "P":
        pkg = {"name": f[1], "family": f[2], "texture": f[3], "desc": f[4]}
    elif len(f) == 2 and f[0]:
        parsed[f[0]] = dict(pkg, base=f[1])
    elif len(f) >= 6 and f[0]:
        parsed[f[0]] = {"name": f[1], "family": f[2], "texture": f[3], "desc": f[4], "base": f[5]}

if len(parsed) != len(entries) or any(parsed.get(e[1], {}).get("base") != e[2] for e in entries):
    problems.append("the table does not read back: %d camo(s) written, %d parsed" % (len(entries), len(parsed)))
elif any(parsed[e[1]]["name"] == "" for e in entries):
    problems.append("a camo parsed back with no name (its package header did not reach it)")
else:
    print("TABLE READS BACK: %d camo(s), every one with its package's name and its own accessory" % len(parsed))

print()
if problems:
    print("ACCSCALE: FAIL")
    for p in problems:
        print("  " + p)
    sys.exit(1)
print("ACCSCALE: PASS - every accessory of every package has its sockets, its fields and its table entry")
