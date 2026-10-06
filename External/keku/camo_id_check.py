"""Are the identifiers a user camo takes when it stops borrowing one FREE?

With CAMO_NAMES = "table" a camo's unlock keeps the identifier of its own name (djb2-XOR over
"Weapons/Custom/U_PKG_<KEY>", case sensitive). The loadout and the menu key on that number, so one that
collides with an unlock of the game would equip somebody else's item. This answers it without the game:
the packages come from the generated index, the game's identifiers from its EBX.

usage: python camo_id_check.py <index.lua>
"""
import io
import json
import os
import re
import sys

INDEX = sys.argv[1] if len(sys.argv) > 1 else \
    r"C:\Users\keku\Documents\Battlefield 3\Server\Admin\Mods\CamoFramework\ext\Shared\Camos\index.lua"
EBX = r"F:\Desktop\Venice Unleashed\EBX-Json"


def identifier_of(name):
    h = 0x1505
    for ch in name:
        h = ((h * 0x21) & 0xFFFFFFFF) ^ ord(ch)
        h &= 0xFFFFFFFF
    return h


text = io.open(INDEX, encoding="utf-8", newline="").read()
packages = []
for block in re.finditer(r"\{\s*key = \"([^\"]+)\"(.*?)\n\t\},", text, re.S):
    key, body = block.group(1), block.group(2)
    native = re.search(r"\n\t\tnative = (true|false),", body)
    packages.append((key, native is not None and native.group(1) == "true"))

taken = {}
for root, _dirs, files in os.walk(os.path.join(EBX, "Weapons")):
    for f in files:
        if not f.endswith(".json"):
            continue
        try:
            data = json.loads(open(os.path.join(root, f), encoding="utf-8").read())
        except Exception:
            continue
        if not isinstance(data, dict):
            continue
        for inst in data.get("$instances", []):
            if inst["$type"] not in ("UnlockAsset", "SoldierWeaponUnlockAsset"):
                continue
            fields = inst["$fields"]

            def value(x):
                return x.get("$value") if isinstance(x, dict) else x

            ident = value(fields.get("Identifier", {}))
            if ident is not None:
                taken.setdefault(int(ident), value(fields.get("Name", {})) or f)

print("%d unlock identifier(s) in the game's weapons" % len(taken))
clashes, mine = [], {}
for key, native in packages:
    if native:
        continue
    name = "Weapons/Custom/U_PKG_" + key.upper()
    ident = identifier_of(name)
    where = "CLASH with " + str(taken[ident]) if ident in taken else ("REPEATED with " + mine[ident] if ident in mine else "free")
    if ident in taken or ident in mine:
        clashes.append((key, ident, where))
    mine[ident] = key
    print("%-14s %-34s %10d  %s" % (key, name, ident, where))

print()
print("CAMOID: %s" % ("FAIL" if clashes else "PASS - every user camo's own identifier is free"))
sys.exit(1 if clashes else 0)
