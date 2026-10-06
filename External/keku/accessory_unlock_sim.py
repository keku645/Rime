"""Seam for the framework's per-player accessory unlocks, without the game.

The loader gives each accessory camo an unlock of its own and copies, under it, everything the weapon says
about the vanilla accessory: its socket(s) with the clones, its modifier entry (zoom, hand pose), its sway
modifier when it has one, and its place in the row. This runs that plan over the REAL data -- the weapon's
own EBX and the generated index.lua -- and checks what a boot would otherwise:

  * every clone of the package lands on a socket field (a name that matches nothing = silently no camo)
  * the accessory HAS a modifier entry, so the twin can carry the same one (else: scope with no zoom)
  * a sway modifier of the vanilla unlock is seen (else: foregrip that no longer steadies the weapon)
  * the row that offers the vanilla unlock exists (that is where the twin is added)
  * ⭐ the twin's identifier is FREE across every unlock of the game's weapons (the UI and the loadout key
    on that number: a collision would equip somebody else's attachment)

usage: python accessory_unlock_sim.py <index.lua> <weapon folder>
"""
import json
import os
import re
import sys

INDEX = sys.argv[1]
FOLDER = sys.argv[2] if len(sys.argv) > 2 else "XP1_L85A2"
EBX = r"F:\Desktop\Venice Unleashed\EBX-Json"
WEAPON_DIR = os.path.join(EBX, "Weapons", FOLDER)
FIELDS = ("asset1p", "asset1pZoom", "asset3p")
PREFIX = "Weapons/Custom/U_ACC_"


def identifier_of(name):
    """The framework's IdentifierOf: djb2-XOR over the name AS WRITTEN (case-sensitive)."""
    h = 0x1505
    for ch in name:
        h = ((h * 0x21) & 0xFFFFFFFF) ^ ord(ch)
        h &= 0xFFFFFFFF
    return h


# ---- the package's accessories, from the index -----------------------------------------------------------
text = open(INDEX, encoding="utf-8").read()
packages = {}
for block in re.finditer(r"\{\s*key = \"([^\"]+)\"(.*?)\n\t\},", text, re.S):
    key, body = block.group(1).upper(), block.group(2)
    for acc in re.finditer(r"\{ weapon = \"([^\"]+)\", tag = \"([^\"]+)\", meshes = \{(.*?)\} \},", body, re.S):
        weapon, tag, meshes = acc.group(1).upper(), acc.group(2).lower(), acc.group(3)
        clones = {m.group(1).lower(): m.group(2)
                  for m in re.finditer(r"\{ mesh = \"([^\"]+)\", clone = \"([^\"]+)\" \},", meshes)}
        packages.setdefault(key, {}).setdefault(weapon, {})[tag] = clones

if not packages:
    print("FAIL: no accessory clones in the index")
    sys.exit(1)

# ---- the weapon, from the EBX ---------------------------------------------------------------------------
parts = {}
for name in os.listdir(WEAPON_DIR):
    if name.endswith(".json"):
        parts[name] = json.loads(open(os.path.join(WEAPON_DIR, name), encoding="utf-8").read())

weapon = None
for name, data in parts.items():
    for inst in data.get("$instances", []):
        if inst["$type"] == "SoldierWeaponData":
            weapon = (name, data, inst)
if weapon is None:
    print("FAIL: no SoldierWeaponData under Weapons/%s" % FOLDER)
    sys.exit(1)

W_FILE, W_DATA, W_INST = weapon
W_GUID = str(W_DATA["$guid"]).lower()
INSTANCES = {str(i["$guid"]).lower(): i for i in W_DATA["$instances"]}


def value(field):
    return field.get("$value") if isinstance(field, dict) else field


def unlock_name(ref):
    """The Name of the UnlockAsset a reference points at."""
    if not isinstance(ref, dict):
        return None
    p, i = str(ref.get("$partitionGuid", "")).lower(), str(ref.get("$instanceGuid", "")).lower()
    for name, data in parts.items():
        if str(data.get("$guid", "")).lower() != p:
            continue
        for inst in data.get("$instances", []):
            if str(inst["$guid"]).lower() == i and inst["$type"] == "UnlockAsset":
                return value(inst["$fields"]["Name"]), value(inst["$fields"]["Identifier"])
    return None


def tag_of(name):
    leaf = name.split("/")[-1].lower()
    return leaf[2:] if leaf.startswith("u_") else leaf


# every unlock identifier the game ships (for the collision control)
taken = {}
for root, _dirs, files in os.walk(os.path.join(EBX, "Weapons")):
    for f in files:
        if not f.endswith(".json"):
            continue
        try:
            d = json.loads(open(os.path.join(root, f), encoding="utf-8").read())
        except Exception:
            continue
        if not isinstance(d, dict):
            continue
        for inst in d.get("$instances", []):
            if inst["$type"] in ("UnlockAsset", "SoldierWeaponUnlockAsset"):
                ident = value(inst["$fields"].get("Identifier", {}))
                if ident is not None:
                    taken.setdefault(int(ident), value(inst["$fields"].get("Name", {})) or f)

print("%s: %d unlock identifier(s) in the game's weapons" % (FOLDER, len(taken)))

CATALOG = json.loads(open(r"F:\Desktop\Venice Unleashed\Rime-src\RimeCamoStudio\Data\accessories.json",
                          encoding="utf-8").read())
CATALOG_UNLOCKS = []
for entry in CATALOG:
    if entry["folder"].upper() == FOLDER:
        for row in entry["rows"]:
            CATALOG_UNLOCKS.extend(row["unlocks"])

problems = []
KEY = sorted(packages)[0]
by_tag = packages[KEY].get(FOLDER, {})
print("package %s: %d accessor(ies) on this weapon\n" % (KEY, len(by_tag)))

for tag, clones in sorted(by_tag.items()):
    # the sockets of that accessory
    sockets, vanilla = [], None
    for inst in W_DATA["$instances"]:
        if inst["$type"] != "SocketData":
            continue
        got = unlock_name(value(inst["$fields"].get("UnlockAsset", {})))
        if got and tag_of(got[0]) == tag:
            vanilla = got
            sockets.append(inst)

    if vanilla is None:
        problems.append("%s: no socket of the weapon names that accessory" % tag)
        continue

    # Which mesh each socket field holds: read from the studio's accessory catalogue, which was built from
    # these same sockets (the meshes themselves live under Weapons/Accessories, not in the weapon's folder).
    used, fields = set(), 0
    for unlock in CATALOG_UNLOCKS:
        if tag_of(unlock["unlock"]) != tag:
            continue
        for socket in unlock.get("sockets", []):
            for obj in socket.get("objects", []):
                for f in FIELDS:
                    asset = obj.get(f) or {}
                    name = (asset.get("name") or "").lower()
                    if name and name in clones:
                        used.add(name)
                        fields += 1

    missing = [c for m, c in clones.items() if m not in used]

    # the modifier entry and the sway modifier
    mods = 0
    for entry in value(W_INST["$fields"]["WeaponModifierData"]):
        got = unlock_name(value(entry.get("UnlockAsset", {})))
        if got and got[1] == vanilla[1]:
            mods += 1

    sway = 0
    for inst in W_DATA["$instances"]:
        if inst["$type"] == "GunSwayModifierData":
            got = unlock_name(value(inst["$fields"].get("UnlockAsset", {})))
            if got and got[1] == vanilla[1]:
                sway += 1
    for name, d in parts.items():
        for inst in d.get("$instances", []):
            if inst["$type"] == "GunSwayModifierData" and name != W_FILE:
                got = unlock_name(value(inst["$fields"].get("UnlockAsset", {})))
                if got and got[1] == vanilla[1]:
                    sway += 1

    # the row
    row = None
    for name, d in parts.items():
        for inst in d.get("$instances", []):
            if inst["$type"] != "CustomizationUnlockParts":
                continue
            for u in value(inst["$fields"]["SelectableUnlocks"]):
                got = unlock_name(u)
                if got and got[1] == vanilla[1]:
                    row = (name, len(value(inst["$fields"]["SelectableUnlocks"])))

    twin = PREFIX + KEY + "_" + FOLDER.upper() + "_" + tag.upper()
    ident = identifier_of(twin)
    clash = taken.get(ident)

    print("%-16s vanilla=%s id=%s" % (tag, vanilla[0].split("/")[-1], vanilla[1]))
    print("   sockets %d, %d field(s) take a clone, %d clone(s) unused" % (len(sockets), fields, len(missing)))
    print("   modifier entries %d, sway modifiers %d, row %s" % (mods, sway, row and "%s (%d entries)" % row or "NONE"))
    print("   twin %s -> identifier %d %s" % (twin, ident, "CLASH with " + str(clash) if clash else "(free)"))

    if fields == 0:
        problems.append("%s: no socket field would take a clone" % tag)
    if missing:
        problems.append("%s: %d clone(s) never used: %s" % (tag, len(missing), ", ".join(missing)))
    if mods == 0:
        problems.append("%s: the weapon has no modifier entry for it (the twin would lose what it DOES)" % tag)
    if row is None:
        problems.append("%s: no customization row offers the vanilla unlock" % tag)
    if clash:
        problems.append("%s: the twin's identifier %d is already %s" % (tag, ident, clash))

print()
if problems:
    print("ACCUNLOCK: FAIL")
    for p in problems:
        print("  " + p)
    sys.exit(1)

print("ACCUNLOCK: PASS - every accessory of %s has its sockets, its modifiers, its row and a free identifier" % FOLDER)
