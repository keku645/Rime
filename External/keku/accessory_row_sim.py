"""Seam for what the ACCESSORY ROWS of the game's screen show, without the game.

A row of the accessories screen steps through the accessories of one slot. Our twins (an accessory with a camo
on it) are items of that same row, so without a fold they turn the arrows into a list of camo names and any
twin the table does not name draws blank (keku's photo, 2026-09-19). This runs the script's own rules over the
REAL data -- the weapon's rows from the studio's catalogue, the twins from the generated index, the table as
the framework writes it -- and checks what a boot would otherwise:

  * every item the row shows has a NAME (no blanks), and no name is a camo's
  * the row ends with exactly the accessories of THAT row: no entry added, none lost
  * a twin the player wears stands in its accessory's place, so the row still holds OUR identifier (which is
    what the loadout stores and what paints the camo)
  * a camo just picked on that row's screen is in the list, or the pick could never be stored
  * ⭐ ids are compared UNSIGNED: identifiers >= 2^31 reach the script NEGATIVE

usage: python accessory_row_sim.py <index.lua> [<weapon folder>|all]

"all" does not stop at the weapon the package was baked for: it pretends one camo covers EVERY paintable
accessory of EVERY weapon in the catalogue and folds all of their rows, because what a row holds is the
weapon's own business (keku, 2026-09-19: "ten en cuenta que dependiendo del arma tienes unas miras u otros
accesorios", "no solo opticas, accesorios tambien, revisalo todo") -- optics, ammunition, a PDW that puts a
flashlight where a rifle puts a grip. The fold must end with exactly the accessories of that row, whatever
they are.
"""
import json
import re
import sys

INDEX = sys.argv[1]
TARGET = (sys.argv[2] if len(sys.argv) > 2 else "XP1_L85A2").upper()
ONE_PASS = "--onepass" in sys.argv        # the negative control: classify while walking, as it shipped on 2026-09-19
CATALOG_PATH = r"F:\Desktop\Venice Unleashed\Rime-src\RimeCamoStudio\Data\accessories.json"


def identifier_of(name):
    h = 0x1505
    for ch in name:
        h = ((h * 0x21) & 0xFFFFFFFF) ^ ord(ch)
        h &= 0xFFFFFFFF
    return h


def signed(identifier):
    """How the widgets hand an identifier to the script: a 32-bit INT."""
    return identifier - 2 ** 32 if identifier >= 2 ** 31 else identifier


def camo_id_key(value):
    """CamoCommon.camoIdKey: every id that crosses into the table is made unsigned first."""
    n = int(value)
    return str(n + 2 ** 32 if n < 0 else n)


def tag_of(name):
    leaf = name.split("/")[-1].lower()
    return leaf[2:] if leaf.startswith("u_") else leaf


# ---- the packages of the index, and the catalogue --------------------------------------------------------
text = open(INDEX, encoding="utf-8", newline="").read()
packages = []
for block in re.finditer(r"\{\s*key = \"([^\"]+)\"(.*?)\n\t\},", text, re.S):
    key, body = block.group(1), block.group(2)
    tags = [(m.group(1).upper(), m.group(2)) for m in
            re.finditer(r"\{ weapon = \"([^\"]+)\", tag = \"([^\"]+)\", meshes", body, re.S)]
    if not tags:
        continue
    name = re.search(r"\n\t\tname = \"(.*?)\",\r?\n", body)
    thumb = re.search(r"\n\t\tthumbnail = \"(.*?)\",\r?\n", body)
    packages.append({"key": key, "name": name.group(1) if name else key,
                     "thumb": thumb.group(1) if thumb else "", "tags": tags})

CATALOG = json.loads(open(CATALOG_PATH, encoding="utf-8", newline="").read())
ROWS_OF = {entry["folder"].upper(): entry["rows"] for entry in CATALOG}


def table_for(folder, tags_by_package):
    """The table the framework publishes for one weapon: one entry per twin, keyed UNSIGNED."""
    out = {}
    for package, tags in tags_by_package:
        for tag in tags:
            base = None
            for row in ROWS_OF.get(folder, []):
                for unlock in row["unlocks"]:
                    if tag_of(unlock["unlock"]) == tag.lower():
                        base = int(unlock["identifier"])
            if base is None:
                continue
            twin = identifier_of("Weapons/Custom/U_ACC_%s_%s_%s" % (package["key"].upper(), folder, tag.upper()))
            out[str(twin)] = {"name": package["name"], "texture": package["thumb"], "base": str(base)}
    return out


def fold(items, table, picked=None, one_pass=False):
    """CamoCommon.camoCollapseAccessoryItems, line for line.

    The table arrives INSIDE the list, in a carrier the framework puts right after the first twin, so the
    carriers are read in a pass of their own before anything is classified. `one_pass` is the negative
    control: classify as you walk, the way it shipped on 2026-09-19, and the first twin of the first row
    falls through nameless because the table is not there yet.
    """
    known = {}

    def base_of(item):
        entry = known.get(camo_id_key(item["Index"]))
        return "" if entry is None else entry["base"]

    if not one_pass:
        for item in items:                       # pass 1: what the list CARRIES
            if item.get("Carrier"):
                known.update(table)
        items = [i for i in items if not i.get("Carrier")]

    plain, twins = [], []
    for item in items:                           # pass 2: what the list HOLDS
        if item.get("Carrier"):
            known.update(table)
            continue
        if known.get(camo_id_key(item["Index"])) is None and not str(item["Label"]):
            continue                             # nameless and unknown: never goes in the row
        (twins if base_of(item) != "" else plain).append(item)

    table = known
    worn = {}
    for twin in twins:
        base = base_of(twin)
        if twin.get("DefaultSelected") and base not in worn:
            worn[base] = twin
        if picked is not None and camo_id_key(twin["Index"]) == camo_id_key(picked):
            worn[base] = twin

    out = []
    for accessory in plain:
        stand = worn.get(camo_id_key(accessory["Index"]))
        if stand is not None:
            stand = dict(stand)
            stand["DefaultSelected"] = bool(accessory.get("DefaultSelected")) or bool(stand.get("DefaultSelected"))
            stand["Label"] = accessory["Label"]          # camoDressLike
            stand["ItemImage"] = accessory["ItemImage"]
            out.append(stand)
        else:
            out.append(accessory)
    return out, plain, twins


def check_weapon(folder, table, problems, verbose):
    """Fold every row of one weapon and check what the player would see."""
    names = [p["name"] for p in packages]
    for row in ROWS_OF.get(folder, []):
        vanilla = row["unlocks"]
        items = [{"Index": signed(int(u["identifier"])), "Label": u["unlock"].split("/")[-1],
                  "ItemImage": "ui/…/" + tag_of(u["unlock"]), "DefaultSelected": False} for u in vanilla]
        for twin, entry in table.items():
            if any(str(u["identifier"]) == entry["base"] for u in vanilla):
                items.append({"Index": signed(int(twin)), "Label": "", "ItemImage": "", "DefaultSelected": False})
                if len(items) == len(vanilla) + 1:
                    # the framework adds the carrier of the table the first time a row takes a twin, so this is where it
                    # lands: after the first twin and before all the others
                    items.append({"Index": signed(4171126845), "Label": "", "ItemImage": "",
                                  "DefaultSelected": False, "Carrier": True})

        # the player wears a camo on the first accessory of the row and has just picked one on the second
        worn_twin = next((t for t, e in table.items() if e["base"] == str(vanilla[0]["identifier"])), None)
        picked_twin = next((t for t, e in table.items()
                            if len(vanilla) > 1 and e["base"] == str(vanilla[1]["identifier"])), None)
        for item in items:
            if worn_twin is not None and camo_id_key(item["Index"]) == worn_twin:
                item["DefaultSelected"] = True

        out, plain, twins = fold(items, table, signed(int(picked_twin)) if picked_twin else None,
                                 one_pass=ONE_PASS)
        blanks = [i for i in out if str(i["Label"]) == ""]
        camo_named = [i for i in out if str(i["Label"]) in names]
        shown_bases = [camo_id_key(i["Index"]) if table.get(camo_id_key(i["Index"])) is None
                       else table[camo_id_key(i["Index"])]["base"] for i in out]
        wanted_bases = [str(u["identifier"]) for u in vanilla]

        if verbose:
            print("  %-12s vanilla %2d + twins %2d = %2d handed  ->  %2d shown, %d blank, %d named after a camo"
                  % (row["name"], len(vanilla), len(twins), len(items), len(out), len(blanks), len(camo_named)))

        where = "%s/%s" % (folder, row["name"])
        if shown_bases != wanted_bases:
            problems.append("%s: the row does not end with its own accessories (%d shown, %d expected)" %
                            (where, len(out), len(vanilla)))
        if blanks:
            problems.append("%s: %d item(s) with no name" % (where, len(blanks)))
        if camo_named:
            problems.append("%s: %d item(s) named after a camo package" % (where, len(camo_named)))
        if worn_twin is not None and not any(camo_id_key(i["Index"]) == worn_twin for i in out):
            problems.append("%s: the twin the player WEARS is not in the row (the camo would be dropped)" % where)
        if picked_twin is not None and not any(camo_id_key(i["Index"]) == picked_twin for i in out):
            problems.append("%s: the twin just PICKED is not in the row (the pick could not be stored)" % where)


problems = []

if TARGET == "ALL":
    # one camo over EVERY paintable accessory of EVERY weapon: what "generalise" means, and the only way to see
    # that the fold does not depend on what a particular weapon happens to carry
    everything = {"key": "ACCTEST_ALL", "name": packages[0]["name"] if packages else "Camo",
                  "thumb": packages[0]["thumb"] if packages else ""}
    weapons = sorted(ROWS_OF)
    rows_seen, twins_seen = 0, 0
    for folder in weapons:
        tags = [tag_of(u["unlock"]) for row in ROWS_OF[folder] for u in row["unlocks"] if u.get("meshes")]
        table = table_for(folder, [(everything, tags)])
        twins_seen += len(table)
        rows_seen += len(ROWS_OF[folder])
        check_weapon(folder, table, problems, verbose=False)
    print("ALL: %d weapon(s), %d row(s), %d twin(s) folded" % (len(weapons), rows_seen, twins_seen))
else:
    if TARGET not in ROWS_OF:
        print("FAIL: %s is not in the catalogue" % TARGET)
        sys.exit(1)
    table = table_for(TARGET, [(p, [t for w, t in p["tags"] if w == TARGET]) for p in packages])
    print("%s: %d package(s) with accessories, %d twin(s) on this weapon" % (TARGET, len(packages), len(table)))
    check_weapon(TARGET, table, problems, verbose=True)

print()
if problems:
    print("ACCROW: FAIL")
    for p in problems[:30]:
        print("  " + p)
    if len(problems) > 30:
        print("  … and %d more" % (len(problems) - 30))
    sys.exit(1)
print("ACCROW: PASS - every row ends with its own accessories, none blank, none named after a camo, and the "
      "worn and picked twins are in it under their accessory's name")
