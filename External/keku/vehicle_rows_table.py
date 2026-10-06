"""The vehicles WITHOUT a customization of their own that get a row in TIERRA / AIRE (keku 2026-09-26), measured in the EBX dump:
per blueprint its HudData (VehicleItemHash, MinimapIcon, Customization must be null), its name SID in UI/UIVehicleMetaData (the
description whose ItemIds carry that hash), its seats and component count. Prints one line per vehicle, and the Lua lines of the
framework's data table (CamoFramework/ext/Shared/VehicleRowsData.lua: sid, icon, air -- the showroom offset is
measured apart, per vehicle)."""
import json, os, sys

ROOT = r"F:\Desktop\Venice Unleashed\EBX-Json"

# keku: no gunships, no fixed weapons, no gadgets; the multiplayer blueprints only (no SP/coop/AI/old)
CANDIDATES = [
    "Vehicles/HumveeArmored/HumveeArmored",
    "Vehicles/GAZ-3937_Vodnik/GAZ-3937_Vodnik",
    "Vehicles/GrowlerITV/GrowlerITV",
    "Vehicles/VDV_Buggy/VDV_Buggy",
    "Vehicles/AAV-7A1/AAV-7A1",
    "Vehicles/RHIB/RHIB",
    "Vehicles/RHIB/RHIB_XP3",
    "Vehicles/XPack01/DPV/DPV",
    "Vehicles/XPack01/SkidLoader/SkidLoader",
    "Vehicles/XP3/QuadBike/QuadBike",
    "Vehicles/XP4/HumveeModified/HumveeModified",
    "Vehicles/XP4/VanModified/VanModified",
    "Vehicles/XP4/VodnikModified/VodnikModified_V2",
    "Vehicles/XP5/Humvee_ASRAD/Humvee_ASRAD",
    "Vehicles/XP5/KLR650/KLR650",
    "Vehicles/XP5/VodnikPhoenix/VodnikPhoenix",
    "Vehicles/Venom/Venom",
    "Vehicles/KA-60_Kasatka/KA-60_Kasatka",
    "Vehicles/A-10_THUNDERBOLT/A10_THUNDERBOLT",
]

# the minimap icon enum -> the frame of mc_iconsIngameBig (iconsingame.gfx) that draws it: the SAME name for most (measured: the
# frame labels of sprite 389). TankLC and HeliTrans have no frame of their name: the frame whose NUMBER is the enum's value does
# (TankLC 134 = "Transport", HeliTrans 135 = "Transport_Heli") -- the rule "frame number = enum value" holds for every pair with a
# name in common (Boat 50, Car 51, Jeep 52, Tank 55, JetBomber 61, TankDestroyer 154, Gunship 155, ATV 156; DirtBike, 169, is the
# movie's last frame, 167), measured 2026-09-26; both draw the same 4-frame inner clip `icon` as the others (4 = white)
ICON_FRAME = {"UIHudIcon_Jeep": "Jeep", "UIHudIcon_Car": "Car", "UIHudIcon_Boat": "Boat", "UIHudIcon_ATV": "ATV",
              "UIHudIcon_DirtBike": "DirtBike", "UIHudIcon_JetBomber": "JetBomber", "UIHudIcon_HeliAttack": "HeliAttack",
              "UIHudIcon_HeliScout": "HeliScout", "UIHudIcon_Jet": "Jet", "UIHudIcon_Tank": "Tank",
              "UIHudIcon_TankLC": "Transport", "UIHudIcon_HeliTrans": "Transport_Heli"}
# ⭐ where the showroom places each (VehicleHudData.CustomizationOffset). 📐 Measured on the game's own 19 (vehextent.py, 2026-09-26):
# every ground class sits at (3.3, -2.7, -21) whatever its size (LAV, BMP -2.6, Tunguska, LAV-AD, Stryker, Sprut, HIMARS, Tornado;
# MBTs -22; the BTR-90 alone nearer, (1.7, -2.13, -16)); the helicopters and jets each their own. In all of them the vehicle's ground
# contact lands on one plane: y + contact ≈ -0.13 |z| (a camera ~7° down) -- so a vehicle takes the place of the game's one it is
# most like, its y moved by the difference in how far below its origin it touches the ground (skid / wheel centre vs skid / wheel
# centre, the same kind), and a SMALL one comes nearer along the same line of sight (the whole vector scaled: the same spot on the
# screen, bigger). First places: keku's eye in the showroom is the judge, and each line is his knob.
GROUND = (3.3, -2.7, -21.0)                      # the LAV-25's: the Humvee's since the first test (keku: "funciona todo correctamente")
SMALL = tuple(round(v * 0.6, 2) for v in GROUND)  # 60 % of the way: a 1.1-1.7 m wheelbase (the LAV's 3.9) drawn ~1.7x nearer
OFFSET = {
    "Vehicles/HumveeArmored/HumveeArmored": GROUND,
    "Vehicles/GAZ-3937_Vodnik/GAZ-3937_Vodnik": GROUND,       # wheelbase 2.72, wheel centre 0.56: origin on the ground, as the LAV
    "Vehicles/GrowlerITV/GrowlerITV": GROUND,                 # 2.93 / 0.38
    "Vehicles/VDV_Buggy/VDV_Buggy": GROUND,                   # 2.61 / 0.35
    "Vehicles/AAV-7A1/AAV-7A1": (3.3, -2.6, -21.0),            # tracked 5.14, road wheels 0.35-0.78: the BMP-2's (5.08, 0.36-0.76)
    "Vehicles/RHIB/RHIB": (3.3, -2.35, -21.0),                 # no wheels: the hull's lowest part in the data is the water jet at
    "Vehicles/RHIB/RHIB_XP3": (3.3, -2.35, -21.0),             # -0.17 -> raised 0.35 m over the ground place (the hull, ESTIMATED)
    "Vehicles/XPack01/DPV/DPV": GROUND,                        # 2.90 / 0.40
    "Vehicles/XPack01/SkidLoader/SkidLoader": SMALL,           # 1.12 / 0.39
    "Vehicles/XP3/QuadBike/QuadBike": SMALL,                   # 1.68 / 0.34
    "Vehicles/XP4/HumveeModified/HumveeModified": GROUND,      # the Humvee's
    "Vehicles/XP4/VanModified/VanModified": GROUND,            # 3.13 / 0.39
    "Vehicles/XP4/VodnikModified/VodnikModified_V2": GROUND,   # the Vodnik's
    "Vehicles/XP5/Humvee_ASRAD/Humvee_ASRAD": GROUND,          # the Humvee's
    "Vehicles/XP5/KLR650/KLR650": SMALL,                       # 1.54 / 0.30
    "Vehicles/XP5/VodnikPhoenix/VodnikPhoenix": GROUND,        # the Vodnik's
    # the AH-1Z's (4.0, -1.4, -26), skids 1.77 below its origin; the Venom's 2.40 (the same H-1 family) -> 0.63 higher
    "Vehicles/Venom/Venom": (4.0, -0.77, -26.0),
    # the Mi-28's floor (y -1.8, wheel centres 2.39 below, at -33) brought to the AH-1Z's -26 on its line of sight, the Ka-60's wheel
    # centres 2.66 below: y -0.64; x as the Venom's
    "Vehicles/KA-60_Kasatka/KA-60_Kasatka": (4.0, -0.64, -26.0),
    # the F/A-18's (1.9, -2.15, -33), wheel centres 1.88 below; the A-10's 0.74 (it sits low) -> 1.14 lower
    "Vehicles/A-10_THUNDERBOLT/A10_THUNDERBOLT": (1.9, -3.29, -33.0),
}
AIR_ICONS = {"UIHudIcon_HeliTrans", "UIHudIcon_JetBomber", "UIHudIcon_Jet", "UIHudIcon_HeliAttack", "UIHudIcon_HeliScout"}


def val(f, d=None):
    return f.get("$value", d) if isinstance(f, dict) else d


# the dump writes the enum as its NUMBER: the names from VU's typings (UIHudIcon.lua)
import re
ENUM = {}
for m in re.finditer(r"(UIHud[Ii]con_\w+)\s*=\s*(\d+)", open(
        r"C:\Users\keku\AppData\Roaming\Code\User\globalStorage\imposter.vscode-lua-vu\data\types\fb\UIHudIcon.lua", encoding="utf-8").read()):
    ENUM[int(m.group(2))] = m.group(1)


def load(rel):
    return json.load(open(os.path.join(ROOT, rel + ".json"), encoding="utf-8"))


# UI/UIVehicleMetaData: item hash -> name SID
names = {}
meta = load("UI/UIVehicleMetaData")
for inst in meta["$instances"]:
    fl = inst["$fields"]
    ids = val(fl.get("ItemIds"), []) or []
    name = val(fl.get("Name"))
    for i in ids:
        v = val(i) if isinstance(i, dict) else i
        try:
            names.setdefault(int(v) & 0xFFFFFFFF, name)
        except (TypeError, ValueError):
            pass

rows = []
for bp in CANDIDATES:
    try:
        d = load(bp)
    except FileNotFoundError:
        print("MISSING", bp)
        continue
    ved = next(i for i in d["$instances"] if i["$type"] == "VehicleEntityData")
    fl = ved["$fields"]
    hud = val(fl.get("HudData")) or {}
    item = int(val(hud.get("VehicleItemHash"), 0)) & 0xFFFFFFFF
    icon = val(hud.get("MinimapIcon"))
    icon = ENUM.get(int(icon), icon) if isinstance(icon, int) else icon
    cust = val(hud.get("Customization"))
    rcc = int(val(fl.get("RuntimeComponentCount"), 0)) & 0xFF
    sid = names.get(item, "?")
    air = icon in AIR_ICONS
    frame = ICON_FRAME.get(str(icon))
    rows.append((bp, sid, frame, air))
    print("%-48s item %10d  sid %-26s icon %-22s frame %-10s %s  rcc %3d  cust %s" % (
        bp, item, sid, icon, frame, "AIR " if air else "LAND", rcc, "null" if not cust else cust))

print()
print("-- lua lines")
for bp, sid, frame, air in rows:
    off = OFFSET.get(bp)
    print('\t{ blueprint = "%s", sid = "%s", icon = %s, air = %s%s },' % (
        bp, sid, ('"%s"' % frame) if frame else "nil", "true" if air else "false",
        (", offset = { %s, %s, %s }" % tuple(("%.2f" % v).rstrip("0").rstrip(".") for v in off)) if off else ""))
