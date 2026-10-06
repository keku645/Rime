"""Bakes RimeUIEditor/Data/CamoSidearmPieces.lua: per pistol 3P mesh, where the in-game weapon view draws it and what it must hide.

The weapon view poses a weapon's base mesh with the EBX deltas (SoldierWeaponData.WeaponStates[0].Mesh3pTransforms), which on the
rifles is the weapon assembled and its authored box. On the PISTOLS the authored box is the BIND pose's (the Taurus' runs to z 0.43,
the drawn pistol to 0.26), so a view centred on that box turns the pistol about a point ~12 cm ahead of it -- and one piece is
parked by those deltas where the game never shows it (the Taurus' speedloader, Wep_Mag_Ammo, behind the grip; the game's own idle
pose parks it a metre back, off camera). Measured (keku 2026-10-06, a photo of the Taurus in the pistol window):

  dump   one mount: each mesh posed with the EBX deltas (what the view draws), then once per bone with that bone moved 10 m;
         the vertices that moved are the bone's piece.
  bake   per mesh: the box of everything kept, its centre and radius (half the longest side, as the view's BoundsRadius), and the
         bones whose piece lies wholly outside the box of the rest (expanded 5 mm) -- those the view collapses to nothing.

usage: python sidearm_pieces.py dump <work dir> [EBX folder…]   (mounts the game: minutes; the folders = only those weapons)
       python sidearm_pieces.py bake <work dir>     (writes Data/CamoSidearmPieces.lua)
"""
import json
import math
import os
import struct
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REPL = os.path.join(ROOT, "bin", "Release", "RimeREPL.exe")
OUT_LUA = os.path.join(ROOT, "RimeUIEditor", "Data", "CamoSidearmPieces.lua")
GAME = r"E:\GAMES\Battlefield 3"
DB = "levels/xp2_skybar/xp2_skybar/shaderdb"
EBX = r"F:\Desktop\Venice Unleashed\EBX-Json\Weapons"
BONES = ["exportAnimation", "Wep_Root", "Wep_Extra1", "Wep_Trigger", "Wep_Slide", "Wep_Grenade1", "Wep_Grenade2", "Wep_Mag", "Wep_Mag_Ammo",
         "Wep_Physic1", "Wep_Physic2", "Wep_Physic3", "Wep_Belt1", "Wep_Belt2", "Wep_Belt3", "Wep_Belt4", "Wep_Belt5", "Wep_Bipod1",
         "Wep_Bipod2", "Wep_Bipod3", "IK_Joint_LeftHand", "IK_Joint_RightHand", "Wep_Extra2", "Wep_Extra3", "Wep_Aim"]
#   EBX folder, blueprint, 3P mesh, the Gun Master blueprint's 3P mesh (byte-identical: the same entry under its own leaf)
PISTOLS = [("Taurus44", "Taurus44", "weapons/taurus44/taurus44_3p_mesh", "taurus44_gm_3p_mesh"),
           ("M93R", "M93R", "weapons/m93r/m93r_3p_mesh", "m93r_gm_3p_mesh"),
           ("M9", "M9", "weapons/m9/m9_3p_mesh", "m9_gm_3p_mesh"),
           ("Glock18", "Glock18", "weapons/glock18/glock18_3p_mesh", None),
           ("M1911", "M1911", "weapons/m1911/m1911_3p_mesh", None),
           ("MP412Rex", "MP412Rex", "weapons/mp412rex/mp412rex_3p_mesh", None),
           ("MP443", "MP443", "weapons/mp443/mp443_3p_mesh", "mp443_gm_3p_mesh"),
           # the crossbow, a GADGET with the same window (keku 2026-10-06)
           ("xp4_crossbow_prototype", "XP4_Crossbow_Prototype", "weapons/xp4_crossbow_prototype/xp4_crossbow_prototype_3p_mesh", None)]
MOVE = 10.0
# Pieces hidden whatever the boxes say, by EBX folder. ⭐ The crossbow's BOLTS (keku 2026-10-06, a photo of the window: *"salen
# todos los tipos de virotes a la vez, no debe haber ninguno tanto en ballesta como en ballesta con mira"*): the EBX deltas lay
# every bolt kind on the rail at once (heads together at the tip), and the game's own idle pose (the ant bank's IdlePose Anim)
# parks exactly these four bones off the weapon (y -0.26..-0.44, z -0.72..-1.14) -- measured, so the list is the game's.
ALWAYS_HIDE = {"xp4_crossbow_prototype": ["Wep_Physic1", "Wep_Physic2", "Wep_Physic3", "Wep_Extra2"]}
v = lambda x: x.get("$value") if isinstance(x, dict) and "$value" in x else x


def ebx_pose(folder, name):
    d = json.load(open(os.path.join(EBX, folder, name + ".json"), encoding="utf-8"))
    data = next(i for i in d["$instances"] if i["$type"] == "SoldierWeaponData")
    s0 = v(v(data["$fields"]["WeaponStates"])[0])
    s0 = v(s0.get("$fields", s0))
    out = {}
    for i, t in enumerate(v(s0["Mesh3pTransforms"])):
        t = v(t)
        row = []
        for k in ("right", "up", "forward", "trans"):
            row += [float(v(v(t[k])[c])) for c in ("x", "y", "z")]
        out[BONES[i]] = row
    return out


def read_rsm(path):
    b = open(path, "rb").read()
    if b[:4] != b"RSM7":
        raise SystemExit(f"{path}: not an RSM7 dump ({b[:4]!r})")
    n = struct.unpack_from("<i", b, 4)[0]
    p = 8
    pos = []
    for _ in range(n):
        p += 4 + struct.unpack_from("<i", b, p)[0]
        p += 4 + struct.unpack_from("<i", b, p)[0]
        p += 8
        vc = struct.unpack_from("<i", b, p)[0]
        p += 4
        for i in range(vc):
            pos.append(struct.unpack_from("<3f", b, p + i * 36))
        p += vc * 36
        p += 4 + struct.unpack_from("<i", b, p)[0] * 4
        p += vc * 4
    return pos


def dump(work, only=None):
    os.makedirs(work, exist_ok=True)
    lines = [f'mount_game "{GAME}" Frostbite2_0 true', "select_game 1"]
    for folder, name, mesh, _gm in PISTOLS:
        if only and folder not in only:
            continue
        base = ebx_pose(folder, name)
        variants = [("base", base)]
        for bone in BONES[2:]:
            moved = {k: list(r) for k, r in base.items()}
            moved[bone][10] += MOVE
            variants.append((bone, moved))
        for tag, pose in variants:
            pose_file = os.path.join(work, f"{folder}_{tag}.pose.json")
            json.dump({"Mesh": mesh, "Skeleton": "animations/skeletons/weapon/weaponske01", "Bank": "ebx", "Anim": "Mesh3pTransforms",
                       "Bones": pose}, open(pose_file, "w", encoding="utf-8"))
            lines.append(f'dump_mesh_sections {mesh} "{os.path.join(work, f"{folder}_{tag}.rsm")}" {DB} "{pose_file}"')
    lines.append("exit")
    script = os.path.join(work, "sidearm_pieces.rime")
    open(script, "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
    with open(os.path.join(work, "sidearm_pieces.log"), "w", encoding="utf-8") as log:
        print("exit", subprocess.call([REPL, script], cwd=os.path.dirname(REPL), stdout=log, stderr=subprocess.STDOUT))


def box(points):
    return [min(p[k] for p in points) for k in range(3)], [max(p[k] for p in points) for k in range(3)]


def apart(a, b, margin=0.005):
    """Two boxes that do not meet, with a margin: one is wholly outside the other along some axis."""
    return any(a[1][k] < b[0][k] - margin or a[0][k] > b[1][k] + margin for k in range(3))


def bake(work):
    entries, lines = [], []
    for folder, _name, mesh, gm in PISTOLS:
        base = read_rsm(os.path.join(work, f"{folder}_base.rsm"))
        pieces = {}
        for index, bone in enumerate(BONES):
            path = os.path.join(work, f"{folder}_{bone}.rsm")
            if index < 2 or not os.path.exists(path):
                continue
            moved = read_rsm(path)
            if len(moved) != len(base):
                raise SystemExit(f"{folder} {bone}: {len(moved)} vertices against the baseline's {len(base)}")
            idx = {i for i, (a, c) in enumerate(zip(base, moved)) if math.dist(a, c) > MOVE * 0.5}
            if idx:
                pieces[index + 1] = idx                                  # 1-based: the Lua's mesh3pTransforms index
        hidden = [BONES.index(b) + 1 for b in ALWAYS_HIDE.get(folder, []) if BONES.index(b) + 1 in pieces]
        if len(hidden) != len(ALWAYS_HIDE.get(folder, [])):
            raise SystemExit(f"{folder}: a bone of ALWAYS_HIDE carries no piece in the dump")
        for bone_index, idx in pieces.items():
            if bone_index in hidden:
                continue
            rest = [base[i] for i in range(len(base)) if i not in idx]
            if rest and apart(box([base[i] for i in idx]), box(rest)):
                hidden.append(bone_index)
        gone = set().union(*(pieces[b] for b in hidden)) if hidden else set()
        kept = [base[i] for i in range(len(base)) if i not in gone]
        lo, hi = box(kept)
        centre = [(lo[k] + hi[k]) * 0.5 for k in range(3)]
        radius = max(hi[k] - lo[k] for k in range(3)) * 0.5
        hide_text = ", ".join(str(b) for b in sorted(hidden))
        names = ", ".join(BONES[b - 1] for b in sorted(hidden)) or "none"
        leaf = mesh.split("/")[-1]
        # the separating comma goes BEFORE the comment (after it, Lua never sees it)
        value = (f"{{ centre = {{ {centre[0]:.4f}, {centre[1]:.4f}, {centre[2]:.4f} }}, radius = {radius:.4f}, "
                 f"hide = {{ {hide_text} }} }},")
        entries.append(f'  ["{leaf}"] = {value}   -- hides {names}; drawn box ({lo[0]:+.3f},{lo[1]:+.3f},{lo[2]:+.3f})..'
                       f'({hi[0]:+.3f},{hi[1]:+.3f},{hi[2]:+.3f})')
        if gm:
            entries.append(f'  ["{gm}"] = {value}   -- the Gun Master copy of {leaf}')
        lines.append(f"{folder}: {len(base)} vertices, {len(pieces)} piece(s), hides {names}, centre ({centre[0]:+.3f},{centre[1]:+.3f},{centre[2]:+.3f}), "
                     f"{radius * 2:.3f} m across")
    text = ("-- The pistols as the weapon view draws them (3P mesh posed with its EBX deltas), per mesh leaf (lower case, which a camo's\n"
            "-- clone of the mesh shares): the centre and radius of the drawn weapon -- its authored box is the BIND pose's, so it cannot\n"
            "-- say -- and the bones whose piece those deltas park outside the weapon (mesh3pTransforms index), which the game never shows.\n"
            "-- Generated by External/keku/sidearm_pieces.py from Rime's dump_mesh_sections; do not edit by hand.\n"
            "return {\n" + "\n".join(entries) + "\n}\n")
    with open(OUT_LUA, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("\n".join(lines))
    print("written", OUT_LUA)


if __name__ == "__main__":
    if sys.argv[1] == "dump":
        dump(os.path.abspath(sys.argv[2]), sys.argv[3:] or None)
    else:
        bake(os.path.abspath(sys.argv[2]))
