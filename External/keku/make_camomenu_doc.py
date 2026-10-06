"""Writes RimeUIEditor/Data/CamoMenu.json — the UI editor document that gives the game a camo screen: the camo row of the
accessories screen shows the camo worn and opens, on a click or Activate, a screen of its own (UI/Flow/Screen/CustomizeCamoScreen,
made from the game's empty screen) with the page header, the kit's name, BACK, eight family buttons and a Grid of camo
thumbnails with the info box; a thumbnail picks the camo through the screen's own SetAccessory4 → AccessoryChanged, the flow
graph (customizationgraph) stores it as it does for the accessories screen; BACK / Esc return to the accessories screen.

The frame scripts: Data/CamoRow.as (the accessories screen) and Data/CamoScreen.as (the camo screen), each compiled behind
Data/CamoCommon.as (the shared part: the camo table, the localiser, the families) by External/keku/compile_as.py — the .avm1
and its base64 land in Data/ and ride in the `script:` stage ops. Also written: Data/CamoMenu.seam.json, the same document with
a test table entry (the focused seam `camomenu` loads it; the shipping document carries no table: the framework's carrier
description brings it at run time).

    python External/keku/make_camomenu_doc.py

The families (buttons) and the keywords that sort a game camo into them are the FAMILIES table below: edit, rerun, rebuild.
"""
import io, json, os, re, subprocess, sys, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DATA = os.path.join(ROOT, 'RimeUIEditor', 'Data')
COMPILE = os.path.join(ROOT, 'External', 'keku', 'compile_as.py')

# family name -> keywords found in a game camo's name (the raw ID_* label and the localised text are both searched, case-insensitively);
# the family with no keywords takes every camo that matches none; a camo of the table goes to the family its entry names
FAMILIES = [
    ('MISC', []),
    ('ADAPTIVE', ['digital', 'adaptive', 'multicam', 'ucp', 'acu']),
    ('AUTUMN', ['autumn', 'fall', 'oak', 'brown']),
    ('DESERT', ['desert', 'dsrt', 'sand', 'arid', 'tan', 'khaki']),
    ('NAVAL', ['navy', 'naval', 'marine', 'blue', 'sea']),
    ('SNOW', ['snow', 'winter', 'arctic', 'white', 'alpine']),
    ('URBAN', ['urban', 'city', 'grey', 'gray', 'black', 'concrete']),
    ('WOODLAND', ['woodland', 'forest', 'jungle', 'green', 'spec', 'ranger']),
]

# the accessories screen (shipped)
ACC_PARTITION = 'ui/flow/screen/customizeaccessoriesscreen'
ACC_MOVIE = 'ui/assets/customizeaccessoriesscreen'
ACC_ROOT = 'instance1'
CAMO_ROW = 'KitSelector_05'
LOADOUT_ROWS = 'KitSelector_01,KitSelector_02,KitSelector_03,KitSelector_04,KitSelector_05'   # weapon, accessories 1-3, camo: what the weapon view composes from

# the camo screen (new, from the game's empty screen: root sprite = character 2, "instance1"; InputListener.swf is character 1)
CAMO_PARTITION = 'ui/flow/screen/customizecamoscreen'
CAMO_MOVIE = 'ui/assets/customizecamoscreen'
CAMO_TITLE = 'CustomizeCamoScreen'
CAMO_ROOT = 'instance1'
ROOT_SPRITE = 2
CHAR_PAGEHEADER, CHAR_TEXTFIELD, CHAR_BUTTON, CHAR_INFOBOX, CHAR_GRID = 3, 4, 5, 6, 7

# the flow graph that chains the customization screens (shipped): the accessories StateNode gains a CamoButton output, a StateNode
# shows the camo screen, its outputs go where the accessories screen's go
FLOW_PARTITION = 'ui/flow/graph/spawn/customizationgraph'
ACC_STATE = 'CustomizeAccessoriesScreen'
CAMO_STATE = 'CustomizeCamoScreen'
STORE_ACTION = 'UpdateWeaponAccessory'   # the flow graph's engine action StorePrimaryWeaponAccessories, fed by AccessoryChanged

# ⛔⛔ HOW THE GAME STORES A WEAPON'S ACCESSORIES (read out of the shipped CustomizationGraph, 2026-09-18, after three boots
# of keku's ending in "al salir sigue saliendo la réflex"): ONE engine action with a parameter —
#   AccessoryChanged            → ActionKey 1375026578 Params ["0"]  = refresh what is shown, store NOTHING
#   Confirm / Deactivate (Esc)  → SaveAccessoriesEnabled → ActionKey 1375026578 Params ["1"]  = STORE, then pop
# So the choice survives only because LEAVING the accessories screen stores it. Our screens take that screen away by
# popping it (the rows' door), so the game's store never runs and the row comes back wearing the old accessory. Every one
# of our doors therefore stores first, with the game's own action: on the way IN (what the arrows changed) and on the way
# BACK (what was picked on our screen).
STORE_ACTION_KEY = 1375026578
STORE_PARAMS = ['1']
POSTPROCESS_ACTION = 2033918385         # the engine action the graph's PostProcessEffect nodes fire (the glitch between screens), Params ['0.2']

CUSTOMIZATION_COMP = 'ui/uicomponents/uicustomizationcomp'
KIT_COMP = 'ui/uicomponents/uikitcomp'


def data_key_of(source, component=CUSTOMIZATION_COMP):
    """The number the client computes for a component's data source: djb2-xor (seed 5381, h = (h * 33) ^ c) over
    "UI_<component file name>_<source>" UPPER-CASED -- fb::UIComponentManager::registerComponentDataKeys, RE'd
    2026-09-15 and implemented in Rime (RimeLib.Cmd/UiBuilder/DataKeys.cs). Signed, the way the EBX stores it."""
    short = component.replace('\\', '/').split('/')[-1]
    value = 5381
    for byte in ('UI_' + short + '_' + source).upper().encode('utf-8'):
        value = ((value * 33) & 0xFFFFFFFF) ^ byte
    return value - 0x100000000 if value >= 0x80000000 else value


# ⭐ THE MAILBOX (2026-09-20). A data source of OUR OWN, so the mod can hand the screen a value AT RUN TIME -- the
# camo table of whatever packages are installed -- instead of the table being compiled into the movie, which is what
# makes every bake need the menu rebuilt (keku: "no querría tener que bakear todo el mod cada vez").
# It rides the engine's own path: a DataSetNode writes the key when the screen is entered, and the grid's binding
# delivers it to the widget, where our script already receives what the engine sends (updateSetupData).
# ⛔ What is NOT known yet, and what the boot measures: whether the store accepts a key no component DECLARES. The
# script therefore also counts everything the engine does deliver to that widget, by name, so a silence names the
# next step instead of ending the road.
CAMO_TABLE_SOURCE = 'CamoTable'
CAMO_TABLE_KEY = data_key_of(CAMO_TABLE_SOURCE)
CAMO_TABLE_NODE = 'CamoTableSet'

# ⭐ THE MAILBOX ON THE SHIPPED ROWS SCREEN (2026-09-21). The other four screens are ours, so the table rides on the
# binding of a widget we made (the grid). This one is the GAME'S screen, and keku's rule is that nothing of the game
# already in use gets borrowed ("no queremos tomar prestado nada vanilla que ya se esté usando") -- so no source is
# appended to a row's binding: the table arrives at a clip of OUR OWN that exists for nothing else.
# 📐 Read out of the cached movie (no mount): the screen's root sprite is character 10 ("instance1") with its ten
# widgets at depths 1..10, and the movie ALREADY IMPORTS TextField.swf as character 7. So the mailbox is a second,
# empty TextField placed off screen -- no import is added (an import that is not copied is what once shipped an
# empty screen) and not one of the game's clips is touched.
# ⭐ THE TABLE IS NO LONGER BAKED INTO THE MOVIES (2026-09-20 23:44, once the mailbox was measured on ALL FIVE screens:
# `writer node FOUND x1` on each, `TBL2,6496,str` where the grids are and `MRW2,0,6507,0` on the rows screen). With it
# baked, a camo REMOVED from the framework's folder would go on haunting the menu until the next rebuild, and every
# bake would be a rebuild -- which is the whole thing keku did not want. `True` puts the fallback back (the table is
# then compiled into the movies AND the live one still arrives: entries ADD and the live one wins where they meet).
BAKE_TABLE = False

CAMO_MAIL_CLIP = 'CamoMail'
ACC_ROOT_SPRITE = 10
ACC_CHAR_TEXTFIELD = 7
ACC_MAIL_DEPTH = 11
ACC_MAIL_AT = (-4000.0, -4000.0)
CAMO_KEY = -1858312129            # UICustomizationComp.WeaponAccessory4 — the camo row's data
SET_ACCESSORY4_KEY = -991137947   # UICustomizationComp.CustSelectedAccessory4 — what the accessories screen's SetAccessory4 writes
KIT_NAME_KEY = 1978414970         # UIKitComp.SelectedKitName — the kit's name under the header

# The three ACCESSORY rows get the same door and a screen each: the camos of the accessory they have on. The engine hands a row
# every unlock of its slot — the plain accessories and, with the framework's per-player unlocks, a camo of each — so the screen
# shows the ones whose table entry names the accessory worn as their base.
# The keys are computed the way the client registers them (DataKeys: djb2-xor of "UI_UICustomizationComp_<source>" upper-cased);
# the two the camo screen already used come out right, which is what says the other six are right too.
#   row              slot  screen partition                             title (the game's own category)   state node
ACCESSORY_ROWS = [
    ('KitSelector_02', 1, 'ui/flow/screen/customizeopticscreen', 'ID_P_CAT_OPTICS', 'CustomizeOpticScreen', 'OpticButton', 'CustomizeOpticScreen'),
    ('KitSelector_03', 2, 'ui/flow/screen/customizeunderbarrelscreen', 'ID_P_CAT_WEPATTACHMENT', 'CustomizeUnderbarrelScreen', 'UnderbarrelButton', 'CustomizeUnderbarrelScreen'),
    ('KitSelector_04', 3, 'ui/flow/screen/customizeaccessoryscreen', 'ID_P_CAT_WEPATTACHMENT', 'CustomizeAccessoryScreen', 'AccessoryButton', 'CustomizeAccessoryScreen'),
]
ACCESSORY_ITEMS_KEY = {1: -1858312134, 2: -1858312135, 3: -1858312136}      # UICustomizationComp.WeaponAccessory1..3
ACCESSORY_SET_KEY = {1: -991137952, 2: -991137949, 3: -991137950}          # UICustomizationComp.CustSelectedAccessory1..3
ACCESSORY_MOVIE = {1: 'ui/assets/customizeopticscreen', 2: 'ui/assets/customizeunderbarrelscreen', 3: 'ui/assets/customizeaccessoryscreen'}
# the accessory WITHOUT a camo is shown as the weapon's own "no camo" row is (its name and its crossed-out picture), not as
# the accessory's icon and name: on this screen the cells are camos, and the plain accessory is the camo-less one
# ⛔ MEASURED, not guessed (keku 2026-09-18: "el icono de camuflaje predeterminado es el que tiene el símbolo de prohibido"):
# the row the game offers as "no camo" on every weapon is Weapons/Common/DefaultCamo (identifier 316449042) / NoCamo
# (2341043160), and the description that claims those ids in UIWeaponAccessoryMetaData carries the crossed-out picture.
# (ID_P_CAMO_NAME_CAMO_DEFAULT + UI/Art/Persistence/Camo/default, which is what a first pass used, belong to the SOLDIER's
# camo family -- a plausible name is not the same asset.)
# ⭐ "Sin camuflaje", not "Camuflaje predeterminado" (keku, 2026-09-20). Both are the GAME's own strings, so
# either way it reads in the player's language -- and this one says what the cell does: it takes the camo OFF.
# Measured with `RimeUIEditor --texts`: ID_P_ANAME_NOCAMO = "No Camo" / "Sin camuflaje";
# ID_P_ANAME_DEFAULTCAMO = "Default Camo" / "Camuflaje predeterminado".
DEFAULT_CAMO_LABEL = 'ID_P_ANAME_NOCAMO'
DEFAULT_CAMO_DESC = 'ID_P_ADESC_DEFAULTCAMO'
DEFAULT_CAMO_IMAGE = 'UI/Art/Persistence/WeaponAccessory/NoSelection'
# ⭐ THE GAME'S OWN "no camo" ROWS, by identifier (keku, 2026-09-20: *"lo de sin camuflaje añadelo tambien al
# apartado de camos de armas normal, no solo en accesorios"*). On an ACCESSORY screen the camo-less entry is the
# plain accessory, which the script recognises by the row it belongs to; on the WEAPON's camo screen there is no
# such thing to compare against, so it is these two ids -- `Weapons/Common/DefaultCamo` and `NoCamo`, the pair
# named above and confirmed in a recorded vanilla payload (`Index: "316449042"`, its picture NoSelection).
NO_CAMO_IDS = '316449042,2341043160'

# the header: the accessories path plus ACCESSORIES, joined the way the game joins its own; the title = the game's camo category
# (no text of the game says "CAMOUFLAGES"; the closest in every language is ID_P_CAT_CAMO: CAMOUFLAGE PATTERN / DISEÑO DE CAMUFLAJE)
HEADER_PATH_IDS = 'ID_M_CUST_KITSACC_TITLE_PATH,ID_M_CUST_KITSACC_TITLE'
HEADER_TITLE_ID = 'ID_P_CAT_CAMO'
# the title itself: our word by language (keku 2026-09-17: "camuflaje" a secas, y el resto de idiomas) — the game has no text that says just
# CAMOUFLAGE in every language (measured over its 15 515 strings; only us/es are installed here). The language comes from the game's
# localiser (getLanguage: in the game the LanguageFormat index, 0 English … 3 Spanish … 12 Czech; in the preview a two-letter code); a
# language not listed falls back to ID_P_CAT_CAMO
HEADER_TITLES = 'en~CAMOUFLAGE;es~CAMUFLAJE;fr~CAMOUFLAGE;de~TARNUNG;it~MIMETICA;pl~KAMUFLAŻ;ru~КАМУФЛЯЖ;cs~KAMUFLÁŽ;ja~カモフラージュ;ko~위장;nl~CAMOUFLAGE;pt~CAMUFLAGEM;zh~迷彩'

# ⛔ THE LINE A WINDOW SHOWS WHERE THE PIECE WOULD BE, when that attachment draws nothing at all (the M240 has no iron
# sights, a heavy barrel has no socket in BF3, the "No…" rows are placeholders): the framework says which ones in the
# table it publishes ("X~<id>,<id>…") and the screen puts this there. Our own text per language, as the title is: the
# game has no string that says it -- measured over the 6 243 ids its data names, the closest is "Sin camuflaje", which
# means something else. Same key as the title (the localiser's getLanguage), English when the language is not listed.
NO_MODEL_TEXTS = ('en~This attachment has no part to paint.;'
                  'es~Este accesorio no tiene ninguna pieza que pintar.;'
                  'fr~Cet accessoire n’a aucune pièce à peindre.;'
                  'de~Dieses Zubehör hat kein lackierbares Teil.;'
                  'it~Questo accessorio non ha parti da dipingere.;'
                  'pl~Ten dodatek nie ma części do pomalowania.;'
                  'ru~У этого аксессуара нет деталей для покраски.;'
                  'cs~Toto příslušenství nemá žádné díly k nabarvení.;'
                  'ja~このアタッチメントには塗装できるパーツがありません。;'
                  'ko~이 부착물에는 도색할 부품이 없습니다.;'
                  'nl~Dit accessoire heeft geen te schilderen onderdeel.;'
                  'pt~Este acessório não tem nenhuma peça para pintar.;'
                  'zh~此配件没有可上色的部件。')

# ⛔ THE ATTACHMENTS WHOSE WINDOW NEVER OPENS (keku 2026-09-19: *"en general para todas las armas debes bloquear que no
# se pueda abrir ni la mira metalica ni el cañon pesado"*, and before that *"que no ocurra nada"* on a piece with nothing
# to paint). A camo window on one of those is an empty room: the heavy barrel has no socket at all in BF3, the iron
# sights are the absence of an optic, the "No…" entries are placeholders and a shotgun's ammunition paints nothing.
# The list is the GAME's own, read from the studio's catalogue (every unlock of every customization row with no mesh,
# plus the iron sights and the heavy barrels by name) and baked into the movie -- it is static game data, so it needs
# no mod, no package and no runtime table: 78 identifiers cover all 60 weapons, because the placeholders are shared.
ACCESSORY_CATALOGUE = 'F:/Desktop/Venice Unleashed/Rime-src/RimeCamoStudio/Data/accessories.json'


FRAMEWORK_LUA = (r"C:\Users\keku\Documents\Battlefield 3\Server\Admin\Mods"
                 r"\CamoFramework\ext\Shared\__init__.lua")


def weapon_unlocks():
    """(folder, identifier) of every weapon unlock, read from the framework's own WEAPONS table.

    One folder is several unlocks -- a weapon's grenade-launcher and faction variants are separate unlocks
    with their own sockets -- and all of them have to be gated, so they all come back. The folder is taken
    the way the framework takes it (`FolderOfPattern`: the camo pattern's second segment, upper case).
    """
    try:
        text = io.open(FRAMEWORK_LUA, encoding='utf-8', newline='').read()
    except Exception as ex:
        print('WARNING: no framework Lua (%s): the per-weapon gate will be empty' % ex)
        return []

    out = []
    for unlock, pattern in re.findall(r'\{\s*"(Weapons/[^"]+)"\s*,\s*"(Weapons/[^"]+)"\s*\}', text):
        folder = re.match(r'^Weapons/([^/]+)/', pattern)
        if folder:
            out.append((folder.group(1).upper(), identifier_of(unlock)))

    return out


def no_window_pairs():
    """"<weapon id>|<attachment id>,…": the doors that stay shut on THIS weapon although the same attachment
    opens on another. Baked, so it needs no mod running -- the framework's own per-weapon list only ever
    reaches a row a package painted (measured 2026-09-19), which is exactly the weapons this is about."""
    try:
        with io.open(ACCESSORY_CATALOGUE, encoding='utf-8', newline='') as f:
            catalogue = json.load(f)
    except Exception as ex:
        print('WARNING: no accessory catalogue (%s): no per-weapon gate' % ex)
        return ''

    blocked = set(int(i) for i in no_window_ids().split(',') if i)
    weapons = weapon_unlocks()
    pairs = []

    for weapon in catalogue:
        folder = str(weapon['folder']).upper()
        ids = [i for f, i in weapons if f == folder]

        for row in weapon['rows']:
            for unlock in row['unlocks']:
                identifier = int(unlock['identifier'])

                # Draws nothing HERE, and is not already shut everywhere: that is the case only a pair can say.
                if not unlock.get('meshes') and identifier not in blocked:
                    for weapon_id in ids:
                        # a DOT joins them: ':' and '|' are the separators of the script-vars string
                        # itself (the generator refuses them), and an id is a decimal with neither.
                        pairs.append('%d.%d' % (weapon_id, identifier))

    return ','.join(sorted(set(pairs)))


def no_window_ids():
    """Identifiers of the attachments whose camo window would be empty; '' when the catalogue is not there."""
    try:
        with io.open(ACCESSORY_CATALOGUE, encoding='utf-8', newline='') as f:
            catalogue = json.load(f)
    except Exception as ex:
        print('WARNING: no accessory catalogue (%s): every window will open' % ex)
        return ''

    ids, draws = set(), set()
    for weapon in catalogue:
        for row in weapon['rows']:
            for unlock in row['unlocks']:
                leaf = unlock['unlock'].split('/')[-1].lower()
                tag = leaf[2:] if leaf.startswith('u_') else leaf
                # ⛔ A PLACEHOLDER WITH A MODEL IS NOT A PLACEHOLDER. "No optic" carries the weapon's own IRON
                # SIGHTS on 29 of the 60 weapons, and keku wants their window (2026-09-20: "activando la ui en
                # las miras de hierro para que el usuario pueda personalizar el camo"), so the rule is what it
                # always should have been -- is there anything to paint -- and not what the row is called.
                # ⚠ The identifier is SHARED by all 60 weapons (Weapons/Common/NoOptics), so this list cannot
                # keep it off the 31 that have no sights: that case needs the per-weapon pairs below.
                if not unlock.get('meshes') or 'heavybarrel' in tag:
                    ids.add(int(unlock['identifier']))
                if unlock.get('meshes'):
                    draws.add(int(unlock['identifier']))

    # ⛔ AND WHAT DRAWS ON ONE WEAPON IS NOT SHUT ON ALL OF THEM. The same identifier is the iron sights of 29
    # weapons and nothing on the other 31; leaving it here kept every sight window shut (keku's 2026-09-20 ask
    # was exactly to open them). Those weapons are covered one by one, by `no_window_pairs`.
    return ','.join(str(i) for i in sorted(ids - draws))


# ---------------------------------------------------------------------------------------------------------
# THE CAMO TABLE, BAKED
#
# ⛔ MEASURED 2026-09-20 (keku's run 34): a description the mod ADDS to the metadata is invisible to the UI's
# own item index -- the script receives `Description == undefined` for our carrier and drops it
# (`row fold 1,13,0,13,drop,...,x1212260461,...`), so a table that travels inside one never arrives. Only
# descriptions the game shipped resolve, and leaning on one of those is exactly the borrowing keku had asked
# three times to end. So the table stops travelling through the game's data: it is baked here, into the
# document, the way the blocked-window identifiers and the thirteen languages already are.
#
# The text is byte-compatible with what the framework used to publish, minus the marker:
#     P~<name>~~<thumbnail>~<text>   one header per package
#     <twin id>~<base id>            one per accessory the package repaints
#     <package id>~                  the camo OF THE WEAPON (no accessory named)
# joined with ';'. Identifiers are djb2-XOR of the unlock name AS WRITTEN -- the same number the framework's
# Lua and the studio's C# compute, which is what lets three independent programs agree without talking.
#
# ⚠ THE PRICE, SAID OUT LOUD: the table is now part of the menu mod, so a camo baked AFTER this document was
# built does not appear until the menu is rebuilt.
# ---------------------------------------------------------------------------------------------------------
CAMO_INDEX = r"C:\Users\keku\Documents\Battlefield 3\Server\Admin\Mods\CamoFramework\ext\Shared\Camos\index.lua"


def identifier_of(name):
    """The id the UI matches on: djb2-XOR over the name AS WRITTEN (case sensitive)."""
    value = 0x1505
    for char in name:
        value = ((value * 0x21) & 0xFFFFFFFF) ^ ord(char)
    return value & 0xFFFFFFFF


def field(text):
    """A field of the table: its separators replaced, never dropped, so a name still reads."""
    return re.sub(r"[~;|:]", " ", str(text or ""))


def base_ids():
    """(weapon folder upper, tag upper) -> the vanilla accessory unlock's identifier."""
    with io.open(ACCESSORY_CATALOGUE, encoding="utf-8", newline="") as handle:
        catalogue = json.load(handle)

    out = {}
    for weapon in catalogue:
        folder = str(weapon["folder"]).upper()
        for row in weapon["rows"]:
            for unlock in row["unlocks"]:
                leaf = unlock["unlock"].split("/")[-1]
                tag = leaf[2:] if leaf[:2].lower() == "u_" else leaf
                out[(folder, tag.upper())] = int(unlock["identifier"])
    return out


def packages():
    """The index's user packages, in order: key, name, text, thumbnail, accessories."""
    text = io.open(CAMO_INDEX, encoding="utf-8", newline="").read()
    out = []
    for block in re.finditer(r"\{\s*key = \"([^\"]+)\"(.*?)\n\t\},", text, re.S):
        key, body = block.group(1), block.group(2)
        if re.search(r"native = true", body):
            continue
        out.append({
            "key": key,
            "name": (re.search(r'\n\t\tname = "([^"]*)"', body) or [None, key])[1],
            "text": (re.search(r'\n\t\ttext = "([^"]*)"', body) or [None, ""])[1],
            "family": (re.search(r'\n\t\tfamily = "([^"]*)"', body) or [None, ""])[1],
            "thumbnail": (re.search(r'\n\t\tthumbnail = "([^"]*)"', body) or [None, ""])[1],
            "accessories": [(w.upper(), t.upper()) for w, t in
                            re.findall(r'\{ weapon = "([^"]+)", tag = "([^"]+)"', body)],
        })
    return out


def camo_table():
    bases = base_ids()
    parts, missing, twins = [], [], 0

    for package in packages():
        # the family the user chose when baking; empty lets the screens guess it from the name, as before
        parts.append("~".join(["P", field(package["name"]), field(package["family"]),
                               field(package["thumbnail"]), field(package["text"])]))

        seen = set()
        for folder, tag in package["accessories"]:
            name = "Weapons/Custom/U_ACC_%s_%s_%s" % (package["key"], folder, tag)
            base = bases.get((folder, tag))

            if base is None:
                missing.append("%s %s/%s" % (package["key"], folder, tag))
                continue

            record = "%d~%d" % (identifier_of(name), base)
            if record not in seen:
                seen.add(record)
                parts.append(record)
                twins += 1

        # the camo OF THE WEAPON: no accessory named, which is what tells the screens what it is
        parts.append("%d~" % identifier_of("Weapons/Custom/U_PKG_%s" % package["key"]))

    table = ";".join(parts)
    print("packages %d, twin records %d, %d characters" % (len(packages()), twins, len(table)))
    if missing:
        print("WARNING: no base identifier for %d accessory(ies): %s" % (len(missing), ", ".join(missing[:6])))
    return table

# the camo screen's layout (720p stage): the left column the accessories rows use (x 96), the weapon on the right
PANEL_X = 96
BUTTON_W, BUTTON_H, BUTTON_GAP = 224, 34, 6
BUTTONS_Y = 140
GRID_Y = BUTTONS_Y + 4 * (BUTTON_H + BUTTON_GAP) + 10   # 310
GRID_WIDTH = 300                 # only has to make the grid's first layout two columns wide (226..338 px for 113 px cells)
CELL_W, CELL_GAP = BUTTON_W, BUTTON_GAP
CELL_H = 47 * CELL_W / 113
IMAGE_SCALE = 150                        # the picture inside a cell, % of the cell's own picture box (keku 2026-09-17: "un 50 % mas grandes")
LABEL_SHIFT = 1.5                        # the cell's name moved down, in cell units (~2x on screen; keku 2026-09-17: "demasiado arriba")
GRID_ROWS = 2                    # rows shown before the grid scrolls (the info box wants the room below)
INFO_AT = '96,515,85'            # the info box's art top-left and scale %: under the grid, within the 720 px stage
DRAG_LEFT = 480                  # stage x from which a mouse press turns the weapon (the panel's right edge plus a margin)
INFO_BOX = 'KitInfoBox_01'
# the symbols' own sizes, measured on the stage (2026-09-16, the seam's placement bounds / scale): Grid 82.15 x 62, Button 64 x 64;
# a widget reads its placed size at load (Grid.onClipLoad: gridWidth = _width) — the scale sets the box the widget works in
GRID_NATURAL = (82.15, 62.0)
BUTTON_NATURAL = (64.0, 64.0)

BUTTON_VARS = 'buttonType=smallTextButton|buttonText=|iconType=none|buttonIconFrame=|resizeIfTextFieldIsToWide=false|alignTextButton=left|alignText=center|FLASH_DEBUG_MODE=false'

# ---------------------------------------------------------------------------------------------------------
# VEHICLES (keku, 2026-09-23: *"añadir un botón al lado de cada PERSONAL. llamado CAMUFLAJE (dependiendo del idioma)… llevará a
# una ventana nueva que será exactamente la que ya usamos para accesorios y armas pero esta vez adaptada a vehículos, del mismo
# modo dejamos rotar el vehículo y hacer zoom"*; the base first: the button, the window showing "Sin camuflaje", the 3D view).
#
# ⛔ NOTHING HERE NAMES A VEHICLE (keku: *"dependiendo del mapa y el modo de juego habrán unos vehículos u otros, por lo que eso
# será dinámico"*). The rows of TIERRA / AIRE are the game's own list, one per vehicle class the map fields; the button rides IN
# each row, so it is there exactly for the vehicles that map has, and the window shows the vehicle the game spawned for it.
#
# 📐 Measured offline (the UI editor's cache, no mount):
#   . the kit movie's vehicle row symbol `KitView_2` places ONE button (button1 = PERSONAL., at x 426); the EQUIPOS row
#     `KitView_1` places TWO of the same Button at the same scale (button1 at 276, button2 at 426). The row's code already
#     drives a button2 (visible when BtnLabel2 is set, `Button2Released` = UIWidgetEventID_OnChanged; hidden, button1 takes
#     its place) -- so the vehicle row gets a button2 = the game's button1 cloned (see KIT_BUTTON* for where each goes).
#   . customizeland|airscreen: one KitView_01 (BtnLabel1 ID_M_TAB_CUSTOMIZE, BtnLabel2 "" -- the port is there, unwired);
#     PERSONAL. = Button1Released -> SetVehicle (DataSet CustSelectedVehicle) -> GotoCustomize, and the flow graph does
#     GotoCustomize --pop 1--> SpawnVehicle (-77536549, no params) -> CustomizeLand|AirSubScreen.EnterScreen; the way back is
#     ... --pop 1--> StoreVehicleAccessories (-741920220, no params) -> CustomizeLand|AirScreen.EnterScreen.
#   . the subscreens' header: ID_M_CUST_LANDSUB_TITLE_PATH + ID_M_CUST_LANDSUB_TITLE (AIRSUB for air); the vehicle's name is
#     UIKitComp.SelectedVehicleName (-1827784044). The keys below come out of data_key_of exactly as the game has them.
# ---------------------------------------------------------------------------------------------------------
KITVIEW_MOVIE = 'ui/assets/kitview'
VEHICLE_ROW_SYMBOL = 'KitView_2'
# ⛔ NOT the EQUIPOS geometry itself (button1 at 276, button2 at 426, 148 px each): a vehicle class's name is far longer than a
# kit's, and in Spanish "HELICÓPTEROS DE ATAQUE" ran under PERSONAL. (the seam's photo, 2026-09-23). The two buttons are the
# game's Button at the game's height, 118 px wide (CAMUFLAJE / PERSONAL. are ~90 px of text; the widget keeps its text at 100 %
# whatever its width), flush with the row's right edge (574) with the game's 2 px between them; a name that still does not fit
# keeps its size and SCROLLS through the room it has (keku: "en vez de hacer el texto más pequeño… que recorra de izquierda a
# derecha y se reinicie"), the rows' script's job.
KIT_BUTTON_W = 118                 # px, each button
KIT_BUTTON_H_SCALE = 0.53125       # the game's own (34 px of the Button's 64)
KIT_ROW_RIGHT = 574                # the row's right edge (its frame's corner pieces)
# keku, 2026-09-23: *"el botón personalizar debe ir a la derecha y el de camuflaje a la izquierda"* -- PERSONAL. keeps the
# right edge it always had, CAMUFLAJE goes left of it
KIT_BUTTON1_AT = (KIT_ROW_RIGHT - KIT_BUTTON_W, 6)                  # PERSONAL., at the right edge
KIT_BUTTON2_AT = (KIT_BUTTON1_AT[0] - KIT_BUTTON_W - 2, 6)          # CAMUFLAJE, just left of it
KIT_BUTTON2_DEPTH = 30             # a free depth of KitView_2 (29 = plus, 31 = button1)
# ⭐ THE ROWS OF OUR OWN (keku 2026-09-26: the vehicles without a customization of their own get a row -- their name, their MINIMAP
# icon, only CAMUFLAJE; the framework adds them to the game's lists, ext/Shared/VehicleRows.lua). The rows' script knows them by their
# name SID and dresses them; the SIDs and icon frames are the framework's table (VehicleRowsData.lua), read here the way the
# per-weapon gate reads the framework's WEAPONS table -- one source, and a table that yields nothing STOPS the build.
# 📐 Measured offline (the editor's cache): the row movie has no minimap icons (it imports mc_iconsMenuKitLargeCustomize alone, 13
# class frames); the HUD's mc_iconsIngameBig is imported as iconsIngame.swf by 27 of the game's movies, the loadout screen's squad
# booster box among them (so it resolves in the customize screens); its vehicle frames are 21-34 px, the class icons 24-32 px ⇒ scale
# 1, placed WHERE THE CLASS ICON SITS (18,22), hidden by the script on the game's rows; KitView_2 is sprite 28 of the kit movie, its
# depths end at 33 (header) and its own characters at 45.
# ⛔ NEVER OFF THE ROW: the list measures a row's height from its bounds when it attaches it (KitView.setupSlots: _height + 2), so a
# clip parked at (-4000,-4000) made every row 4000 px tall and pushed all but the first out of the list (the seam's photo, 2026-09-26:
# only MBT drawn, IFV and ours gone). At (18,22) its first frame (23x25 px) lies inside the row: the rows keep the game's height.
VEHICLE_ROWS_DATA = (r"C:\Users\keku\Documents\Battlefield 3\Server\Admin\Mods"
                     r"\CamoFramework\ext\Shared\VehicleRowsData.lua")
VEHICLE_ROW_SPRITE = 28
VEHICLE_MAP_ICON = 'vehMapIcon'
VEHICLE_MAP_ICON_URL = 'iconsIngame.swf'
VEHICLE_MAP_ICON_SYMBOL = 'mc_iconsIngameBig'
VEHICLE_MAP_ICON_CHAR = 46
VEHICLE_MAP_ICON_DEPTH = 34
# ⭐ keku after the first test in the game (2026-09-26): the icon bigger "como el vanilla", WHITE and still ("no para de parpadear
# entre varios colores"), and our row NARROW -- its header strip, without the three part boxes. The rows' script does all three on our
# rows only; each at 0 is the look he tested (the way back, and the seam's control). 📐 Measured offline:
#   . every vehicle frame of mc_iconsIngameBig places an inner clip `icon` of 4 frames with no stop (1 blue disc, 2 empty, 3 green
#     disc, 4 the WHITE glyph with a dark halo): the HUD sets one per team, left alone it cycles ⇒ frame 4. Jeep, Car, Boat, ATV,
#     DirtBike, JetBomber and Tank alike.
#   . the white glyph, px at scale 1 (VEHICLE_MAP_ICON_GLYPH); the class icon of the game's rows draws ~25 x 26 px. keku approved
#     the Jeep at 180 % (14 px -> 25.2) ⇒ every icon is drawn that tall (VEHICLE_MAP_ICON_HEIGHT): one scale for all would draw the
#     transport helicopter's 24 px glyph 43 px tall. Scaled by the script once the row is laid out, NOT here: at attach the clip's
#     first frame must stay inside the row (above).
#   . KitView_2 is 580 x 109: name y 5, buttons y 6-40, plus y 3-25; the part labels start at y 42, the brackets at y 45 ⇒ 44 px.
VEHICLE_OWN_ROW_HEIGHT = 44
VEHICLE_MAP_ICON_HEIGHT = 25.2
VEHICLE_MAP_ICON_WHITE = 4
# 📐 the white glyph of each icon the table uses (inner frame 4), px tall at scale 1 -- measured on the preview's converted icon movie
# (scratchpad vehrows/iconwhite.py, 2026-09-26; Car and Jeep are the same picture). A frame the table uses without a height here
# STOPS the build: its row would draw at the minimap's own size
VEHICLE_MAP_ICON_GLYPH = {'Jeep': 14, 'Car': 14, 'ATV': 14, 'DirtBike': 14, 'Boat': 16, 'Transport': 16, 'JetBomber': 20,
                          'Transport_Heli': 24}


_vehicle_own_rows = None


def vehicle_own_rows():
    """"<name SID>~<minimap icon frame>~<its white glyph's height>;…" of the framework's rows table (one per SID, in its order;
    "" = no frame)."""
    global _vehicle_own_rows
    if _vehicle_own_rows is not None:
        return _vehicle_own_rows
    try:
        text = io.open(VEHICLE_ROWS_DATA, encoding='utf-8', newline='').read()
    except Exception as ex:
        raise SystemExit('the framework rows table cannot be read (%s): the rows of our own would look like the game\'s' % ex)
    seen, out = set(), []
    for sid, frame in re.findall(r'\{\s*blueprint\s*=\s*"[^"]+"\s*,\s*sid\s*=\s*"([^"]+)"\s*,\s*icon\s*=\s*(?:"([^"]*)"|nil)', text):
        if sid not in seen:
            seen.add(sid)
            if frame and frame not in VEHICLE_MAP_ICON_GLYPH:
                raise SystemExit('the framework rows table gives %s the icon %s, whose glyph height is not measured '
                                 '(VEHICLE_MAP_ICON_GLYPH): its row would draw it at the minimap\'s size' % (sid, frame))
            out.append(sid + '~' + (frame or '') + '~' + (str(VEHICLE_MAP_ICON_GLYPH[frame]) if frame else ''))
    if not out:
        raise SystemExit('the framework rows table (%s) yielded no row' % VEHICLE_ROWS_DATA)
    print('rows of our own (from the framework table): %d -- %s' % (len(out), ';'.join(out)))
    _vehicle_own_rows = ';'.join(out)
    return _vehicle_own_rows
VEHICLE_CAMO_BUTTON_TEXT = 'ID_P_CAT_CAMO'   # BtnLabel2 as the property carries it: the game's own camo text, what a language
                                             # our list lacks shows; the rows' script puts our word (HEADER_TITLES) in its place
VEHICLE_ROOT = 'instance1'
VEHICLE_KITVIEW = 'KitView_01'
VEHICLE_SET_KEY = data_key_of('CustSelectedVehicle')              # -1749749687, what the rows' own SetVehicle writes
VEHICLE_NAME_KEY = data_key_of('SelectedVehicleName', KIT_COMP)   # -1827784044, the vehicle's name under the header
SPAWN_VEHICLE_ACTION = -77536549
STORE_VEHICLE_ACTION = -741920220
UNSPAWN_VEHICLE_ACTION = -1179058080
# ⭐ A PICK = <window>.PickVehicleCamo -> PostProcessEffect ['0.2'] -> UnspawnVehicle, one wire per output (the graphs follow ONE
# wire per output port: two effects are CHAINED, as the weapon pick's glitch is), and VEHICLE_RESPAWN_MS later the window's
# <window>.RespawnVehicle -> SpawnVehicle. The glitch is the weapon and accessory windows' own on a pick (keku 2026-09-23: *"el
# efecto glitch cuando se selecciona el camo, mira como lo hacen los accesorios y armas"*); the ShowRoom's vehicle has to be MADE
# AGAIN to wear the mesh the client's Lua has just pointed it at.
# ⛔ Measured, all three refuted as a way to bring it back: SpawnVehicle alone (run 110: the old vehicle stayed), the game's "apply
# the vehicle's customization" SetCustomization ["0"] (1764738330, what PERSONAL.'s AccessoryChanged fires; run 111: 8 picks, the
# mesh pointed at the clone every time, the view never saw a new vehicle) and UnspawnVehicle -> SpawnVehicle in ONE chain (run 112:
# the vehicle went -- 29 vehicles in the level, then 28 -- and nothing came back in 4 s: the spawn reached the server with the old
# vehicle not gone yet, as in run 110). Hence the spawn apart, after the unspawn has had time to land -- ⛔ and alone that is not
# enough either (run 113: VRS 0.5 s after the pick, nothing back in 4 s; back on TIERRA / AIRE, whose row selects its vehicle as it
# comes up, the tank WAS there, wearing the camo). The game never spawns after an unspawn without writing the selection first
# (UnSpawnVehicle -> SetVehicleCategory -> SetVehicle -> SpawnVehicle), so the respawn writes CustSelectedVehicle back (the row the
# window was opened for) before the spawn: <window>.TooltipActive(row) -> RespawnSetVehicle -> RespawnVehicle -> SpawnVehicle.
# ✅ Measured (run 114, keku: *"ya funciona, el tanque pintado sale después de clickar"*): the unspawn leaves the selection at -1
# on every pick, and with it written back the tank comes back wearing the camo (0.5 s after the pick; keku: *"tarda alrededor de
# 1s de más"*). ⛔ But BOTH are needed (run 115, the respawn at once: the client's selection was cleared at once, the server still
# had the vehicle when the spawn reached it and ignored it, then took it away -- nothing came back). A spawn while the vehicle is
# there does nothing (run 110), so the ask is REPEATED at each of these times (ms after the pick): the first after the server has
# taken the old vehicle brings the new one, as soon as its lag allows; the others find it there and do nothing.
VEHICLE_RESPAWN_MS = '150,400,900'
# ⭐ THE VEHICLE SHOWN IS THE GAME'S OWN (v11, keku 2026-09-23: "¿podemos simplemente girar la cámara que hay actualmente un
# poco para que así el vehículo que el juego spawnea quede un poco a la derecha?"): the window's view (CamoVehicleView.lua)
# turns the scene's camera so the vehicle stands where the weapon windows show the weapon, so the game's vehicle must STAY.
# A positive value is how long after it is set up the window asks the game's own UnspawnVehicle (spawned again on the way back:
# SpawnVehicle before TIERRA / AIRE) -- it served a copy of the vehicle made of entities of ours, and every such copy crashed
# the client (the view's header says how). -1 = never.
VEHICLE_HIDE_MS = -1
# ⭐ the window's own data source: nothing writes it today (the game has no vehicle camos), so the window makes its "Sin
# camuflaje" cell itself; it is where the vehicle camos will arrive once the framework publishes them
VEHICLE_ITEMS_SOURCE = 'VehicleCamoItems'
VEHICLE_ITEMS_KEY = data_key_of(VEHICLE_ITEMS_SOURCE)
# ⭐ THE VEHICLE WINDOW'S MAILBOX (keku 2026-09-23: *"ahora debes hacerlo que se pueda seleccionar en la ventana de camos"*):
# the weapon screens' mailbox, with a key and a writer node of its own -- the weapon view writes every node named
# CamoTableSet, and this table is another one: EVERY vehicle camo, each with the vehicle CLASSES it has a clone for
# ("<id>~<name>~~<thumbnail>~<text>~@V:<class SID>,…"), and what each class wears ("W~<id>~<class SID>"). A class is what the
# window already knows the instant it opens -- its name binding (UIKitComp.SelectedVehicleName) carries the class SID,
# ID_EOR_SCORINGBUCKET_VEHICLEMBT for an MBT -- so, like the weapon window, the list is there at once (keku 2026-09-23: *"no
# queremos que salgan al cabo de 1s, debe ser todo instantáneo"*). CamoVehicleView.lua writes it as the level loads and at
# every change; the grid's binding delivers it under the weapon screens' name (CamoTable -> updateCamoTableData) when the
# screen is entered.
VEHICLE_TABLE_KEY = data_key_of('VehicleCamoTable')
VEHICLE_TABLE_NODE = 'VehicleTableSet'
# the pick: the grid's Released leaves the window through this output, and the flow graph chains the glitch and the ShowRoom's
# vehicle made again (see A PICK above) -- with the mesh the client's Lua has just pointed its class at
VEHICLE_PICK_OUTPUT = 'PickVehicleCamo'
# and the ShowRoom's vehicle spawned again, apart from the pick (see A PICK above), its row selected again first
VEHICLE_RESPAWN_OUTPUT = 'RespawnVehicle'
VEHICLE_RESPAWN_SET = 'RespawnSetVehicle'
#   kind   rows screen (the game's)              rows movie                          rows state
#          camo window (ours)                    its title                           its state                  door (output)   header path
VEHICLE_KINDS = [
    ('land', 'ui/flow/screen/customizelandscreen', 'ui/assets/customizelandscreen', 'CustomizeLandScreen',
     'ui/flow/screen/customizelandcamoscreen', 'CustomizeLandCamoScreen', 'CustomizeLandCamoScreen', 'LandCamoButton',
     'ID_M_CUST_LANDSUB_TITLE_PATH,ID_M_CUST_LANDSUB_TITLE'),
    ('air', 'ui/flow/screen/customizeairscreen', 'ui/assets/customizeairscreen', 'CustomizeAirScreen',
     'ui/flow/screen/customizeaircamoscreen', 'CustomizeAirCamoScreen', 'CustomizeAirCamoScreen', 'AirCamoButton',
     'ID_M_CUST_AIRSUB_TITLE_PATH,ID_M_CUST_AIRSUB_TITLE'),
]

# ---------------------------------------------------------------------------------------------------------
# SOLDIERS (keku, 2026-09-28: *"cuando haces click en apariencia aparece esta ventana, vamos a sustituir esta UI y pondremos la
# misma que usamos en nuestro sistema"*; our window IN PLACE OF APARIENCIA -- not a row in the game's --, tabs CONJUNTO · CABEZA ·
# TORSO · PIERNAS, and each part mixes with any other: *"todo con todo"*, the 18 stock looks and the skins).
#
# 📐 Measured offline (EBX-Json + the editor's recordings, no mount):
#   . the KITS screen (customizesoldierscreen) fires ONE output for APARIENCIA, `ChangeAppearance` -- from its KitView_01's
#     Button2Released and from the console bar's Edit -- and the flow graph takes it on: ChangeAppearance --pop 1-->
#     UpdateSoldierLoadout (2001688152 ['-1']) -> CustomizeAppearanceScreen.EnterScreen. The door is moved IN THE KITS SCREEN (see
#     KITS_DOOR: a live removal in the flow graph did not take in the game): its two triggers go to an output of ours, SkinButton,
#     and the flow graph takes that into a loadout update of ours and on to our window. The button keeps the game's word (APARIENCIA).
#   . the game's appearance screen lists the kit's rows (UICustomizationComp.KitAppearance = 309039297: the 18 camo rows, each
#     {Label, ItemImage, Index = the row's identifier, DefaultSelected, Description}); our grid binds the same key, so the stock
#     looks are the game's own data, per kit and team. Its header: ID_M_CUST_APPEARANCE_TITLE_PATH / ID_M_CUST_APPEARANCE_TITLE.
#   . which soldier the window is about: the team in the KITS screen's sub header (CustomizationSubHeader = ID_M_KITS_US / _RU)
#     and the kit row (UIKitComp.SelectedKit = 0 assault, 1 engineer, 2 support, 3 recon); base or Aftermath comes with the
#     framework's table (the level's kits).
# ⭐ A PICK IS STORED THE WAY THE WEAPONS' IS -- BY THE GAME'S OWN KEY AND STORE ACTION (keku's boot, 2026-09-28: *"en el maniquí
# funciona pero una vez ingame tengo el camo default"*; *"¿has mirado cómo lo hacíamos con las armas y accesorios?"*). The first
# build only told the server, which dressed the mannequin; but on deploy the CLIENT sends the appearance its profile holds for the
# kit (IDA: the deploy, sub_90F830 -> sub_887CD0, sends the kit's stored row's linkedTo[team x kit]) and the soldier wore the
# game's. The weapon windows never had that problem: their pick goes through the game's own SetAccessoryN and store action. So a
# pick here also writes the game's CustSelectedAppearance -- with OUR row (SOLDIER_ROW_NAME) -- and fires the game's
# StoreSoldierAppearance (the same action and parameter as the game's APARIENCIA on BACK): the client's profile holds our row for
# that kit. On that client our row links, per team x kit, a "slot" of this player's, which the server fills with the pieces he
# picked for head, torso and legs (ext/Shared/SoldierSkins.lua) -- the one piece the weapons did not need, since the game sends
# ONE appearance per kit and the parts mix. The window still tells the client's Lua ("SKN<part>,<id>,<soldier>"): the server keeps
# the picks and dresses the mannequin at once.
# ---------------------------------------------------------------------------------------------------------
SOLDIER_PARTITION = 'ui/flow/screen/customizeskinscreen'
SOLDIER_MOVIE = 'ui/assets/customizeskinscreen'
SOLDIER_TITLE = 'CustomizeSkinScreen'
SOLDIER_STATE = 'CustomizeSkinScreen'
KITS_STATE = 'CustomizeSoldierScreen'
KITS_PARTITION = 'ui/flow/screen/customizesoldierscreen'
# ⛔⛔ THE DOOR IS MOVED IN THE KITS SCREEN, NOT IN THE FLOW GRAPH (keku's boot, 2026-09-28: *"he hecho click en apariencia y aun carga
# el ui vanilla"*). The first build removed the flow graph's wire ChangeAppearance --pop 1--> UpdateSoldierLoadout LIVE (the Lua's
# [UNWIRE]) and added ours from the same port: the new wire went in ("-> true") and the game still opened its APARIENCIA -- a live
# removal of a shipped wire had never been measured in the game (memory: "SIN PROBAR IN-GAME"), and the engine follows one wire per
# output port. So the proven road of the vehicle rows instead: the KITS screen, shipped as a partition of ours, has the two wires into
# its ChangeAppearance output taken out -- its KitView_01's Button2Released (Query 9 = OnChanged) and its console bar's Edit (Query 14
# = SetIndex) -- and put into an output of OUR OWN, SkinButton, which the flow graph (live, adding only) takes to our window. The
# game's ChangeAppearance output is left with no wire into it: nothing opens the game's APARIENCIA any more.
KITS_DOOR = 'SkinButton'
KITS_OLD_WIRES = [('03727261-DF82-4D5C-8DDF-FCB2E8C69E5A', 'KitView_01.Button2Released', 'ChangeAppearance.In'),
                  ('CCC8878F-E833-4C4C-8724-5A2F1D97F7DE', 'ConsoleButtonBar_01.Edit', 'ChangeAppearance.In')]
UPDATE_LOADOUT_ACTION = 2001688152       # UpdateSoldierLoadout, Params ['-1']: what the game fires on the way into APARIENCIA
SOLDIER_ITEMS_KEY = data_key_of('KitAppearance')                  # 309039297, the game's appearance rows
SOLDIER_TEAM_KEY = data_key_of('CustomizationSubHeader')          # 1500544559, "ID_M_KITS_US" / "ID_M_KITS_RU"
SOLDIER_KIT_KEY = data_key_of('SelectedKit', KIT_COMP)            # 1985842909, the kit row
SOLDIER_TABLE_KEY = data_key_of('SoldierSkinTable')
SOLDIER_TABLE_NODE = 'SkinTableSet'
SOLDIER_PICK_OUTPUT = 'SkinPicked'
SOLDIER_HEADER_PATH = 'ID_M_CUST_APPEARANCE_TITLE_PATH'
SOLDIER_HEADER_TITLE = 'ID_M_CUST_APPEARANCE_TITLE'
# the row of ours at the end of each kit's appearance list (SoldierSkins.lua ROW_NAME): on each client it links that player's slots;
# the window does not list it
SOLDIER_ROW_NAME = 'UI/Art/Persistence/Camo/U_CAMO_SKINS'
# a pick stores our row as the kit's appearance, as the game's APARIENCIA does (measured in its partition and its graph):
#   SetAppearance = DataSetNode on UICustomizationComp.CustSelectedAppearance (745180783), the row's identifier;
#   StoreSoldierAppearance = ActionNode 1764738330 ['1'] (IDA sub_8801E0: the identifier -> the row's index among the kit's
#   eligible rows -> the profile option of that kit; ['0'] / ['0','1'] send the selection instead).
# A DataSetNode with a Param of its own writes the Param even when the wire into it carries a value: the game's own screens do it
# (KitSelector.Button1Released -> SetWeaponCategory '0', Button.Over -> SetCommand '9'). The identifier goes as a SIGNED number:
# the game reads a string value with atoi (uiCustomizationComp::getAsUInt), which saturates past 2^31.
SOLDIER_SELECTED_KEY = data_key_of('CustSelectedAppearance')      # 745180783
SOLDIER_ROW_SET_NODE = 'SkinRowSet'
STORE_APPEARANCE_ACTION = 1764738330
assert SOLDIER_SELECTED_KEY == 745180783, SOLDIER_SELECTED_KEY


def signed32(value):
    return value - (1 << 32) if value >= (1 << 31) else value
SOLDIER_TABS = ['SkinTab_01', 'SkinTab_02', 'SkinTab_03', 'SkinTab_04']
# the tabs' words by language (keku's: CONJUNTO · CABEZA · TORSO · PIERNAS); the game has no text for them. Same language key as
# the camo title (the localiser's getLanguage); English when the language is not listed
SOLDIER_TAB_WORDS = ('en~OUTFIT,HEAD,TORSO,LEGS;es~CONJUNTO,CABEZA,TORSO,PIERNAS;fr~TENUE,TÊTE,TORSE,JAMBES;'
                     'de~OUTFIT,KOPF,OBERKÖRPER,BEINE;it~COMPLETO,TESTA,BUSTO,GAMBE;pl~ZESTAW,GŁOWA,TUŁÓW,NOGI;'
                     'ru~КОМПЛЕКТ,ГОЛОВА,ТОРС,НОГИ;cs~SADA,HLAVA,TRUP,NOHY;ja~セット,頭,胴,脚;ko~세트,머리,몸통,다리;'
                     'nl~OUTFIT,HOOFD,TORSO,BENEN;pt~CONJUNTO,CABEÇA,TRONCO,PERNAS;zh~套装,头部,躯干,腿部')
# the window's layout (720p stage): the tabs in one row of four where the families start, the eight families in two rows of four
# under them, the grid and the info box below -- the families narrow to a tab's width so the grid keeps the camo screen's two rows
SOLDIER_BUTTON_W = (2 * BUTTON_W + BUTTON_GAP - 3 * BUTTON_GAP) // 4    # 109: four in the width of two family buttons
SOLDIER_TABS_Y = BUTTONS_Y
# the tabs set apart from the families (keku 2026-09-28, with a capture: *"separa algo más los botones nuevos de las categorías camo,
# para así poder diferenciarlo bien, ahora hay demasiado botón junto"*): 10 px read as one block of twelve buttons
SOLDIER_TABS_GAP = 26
SOLDIER_FAMILIES_Y = SOLDIER_TABS_Y + BUTTON_H + SOLDIER_TABS_GAP
SOLDIER_GRID_Y = SOLDIER_FAMILIES_Y + 2 * (BUTTON_H + BUTTON_GAP) + 4
SOLDIER_INFO_AT = '96,%d,85' % (SOLDIER_GRID_Y + round(GRID_ROWS * CELL_H + (GRID_ROWS - 1) * CELL_GAP) + 13)
for _key, _want in ((SOLDIER_ITEMS_KEY, 309039297), (SOLDIER_TEAM_KEY, 1500544559), (SOLDIER_KIT_KEY, 1985842909)):
    assert _key == _want, (_key, _want)   # the keys the game's own screens bind, measured: the formula must give them back


def soldier_vars(on=False):
    """The soldier window's variables. The camo screen's script runs on SEVEN screens (the camo screen, three accessory screens, two
    vehicle windows and this one): a variable it reads is given on ALL of them, or the others read "undefined" -- the trap of the
    three variable lists (2026-09-20). Off = the values that leave every other screen as it was."""
    if not on:
        return [('_camoSoldier', '0'), ('_camoTabs', ''), ('_camoTabWords', ''), ('_camoButtonColumns', '2'),
                ('_camoTeamKey', '0'), ('_camoKitKey', '0'), ('_camoSkinHide', '')]
    return [('_camoSoldier', '1'), ('_camoTabs', ','.join(SOLDIER_TABS)), ('_camoTabWords', SOLDIER_TAB_WORDS),
            ('_camoButtonColumns', '4'), ('_camoTeamKey', str(SOLDIER_TEAM_KEY)), ('_camoKitKey', str(SOLDIER_KIT_KEY)),
            ('_camoSkinHide', str(identifier_of(SOLDIER_ROW_NAME)))]


# the seam's soldier table, as the client's Lua writes it: the level's pack, one skin of the US assault with a torso and legs of its
# own (none for the head), and the torso of the US assault wearing it -- the window must list it in CONJUNTO / TORSO / PIERNAS, not in
# CABEZA, mark it on TORSO, and mark nothing on CONJUNTO (the parts differ)
SEAM_SOLDIER_SKIN = 1234567891
# and one stock look's NAME, as SoldierSkins.lua publishes it (the NWU row: "XP2_NWU" plus the word "navy") -- the window must file it
# under NAVAL whatever its label reads in the language
SEAM_SOLDIER_NWU_ROW = identifier_of('UI/Art/Persistence/Camo/U_CAMO_XP2_NWU')
# Its LEGS are another thing than its torso (keku 2026-09-28: "¿qué ocurre si en cada parte del cuerpo es una cosa distinta?"): the
# legs' own cell -- another picture, filed under URBAN -- on PIERNAS; the torso has none, so TORSO and CONJUNTO show the skin's (SNOW)
SEAM_SOLDIER_LEGS_THUMB = 'UI/Art/Persistence/Specializations/Camo/PremiumCamo_Berkut'
SEAM_SOLDIER_LEGS_FAMILY = 'URBAN'
# and a name of their own ("que cada parte tenga un nombre distinto"), with an accent: what a Spanish user types
SEAM_SOLDIER_LEGS_NAME = 'Pantalón Rayas'
# and an INFO text of their own (keku 2026-09-28: "añade la opción de descripción a cada parte del cuerpo también")
SEAM_SOLDIER_LEGS_TEXT = 'Rayas en las piernas.'
SEAM_SOLDIER_TABLE = ('@camotable;K~base;R~%d~XP2_NWU navy;S~%d~Snow Test Skin~SNOW~UI/Art/Persistence/Camo/default~A skin of the seam.~US_Assault~torso,legs;'
                      'SP~%d~legs~%s~%s~%s~%s;SW~US_Assault~torso~%d' % (SEAM_SOLDIER_NWU_ROW, SEAM_SOLDIER_SKIN, SEAM_SOLDIER_SKIN,
                                                                         SEAM_SOLDIER_LEGS_THUMB, SEAM_SOLDIER_LEGS_FAMILY, SEAM_SOLDIER_LEGS_NAME,
                                                                         SEAM_SOLDIER_LEGS_TEXT, SEAM_SOLDIER_SKIN))

# the seam's table: a game camo of the SCAR-H (its identifier) renamed, so the preview can see the table applied at the door
# (a name wider than a cell's label, so the seam sees it scroll)
SEAM_TABLE = '566124738~Dune Test of a Long Desert Stripe Name~DESERT~~A camo of the table - the desert stripe of the SCAR-H under a name of ours.'
# and the VEHICLE windows' mailbox in the seam's document, as CamoVehicleView.lua writes it: one camo with a clone for the MBT
# class, worn by it -- the window (whose name binding says MBT in the seam) must list it after "Sin camuflaje" and mark it.
# Filed under DESERT (the family chosen at the bake, keku 2026-09-24), a family its name would never guess: the window opens on the
# worn camo's family, so it lists it only if the family travels (by its name, Berkut would land under MISC)
SEAM_VEHICLE_TABLE = ('@camotable;3913710241~Berkut~DESERT~UI/Art/Persistence/Specializations/Camo/PremiumCamo_Berkut~'
                      'The seam vehicle camo~@V:ID_EOR_SCORINGBUCKET_VEHICLEMBT;W~3913710241~ID_EOR_SCORINGBUCKET_VEHICLEMBT')
# and the LOADOUT's mailbox in the seam's document, as SidearmTwins.lua hands it: one package header and the bench's twin of the M9
# (U_SEC_TEST_U_M9, 319457744 -- its id in keku's run 341) said as a camo OF the M9 (287899934). Shipping, the writer's Param is ''
# and the framework writes it at run time; in the preview an empty Param delivers null, which the widget never hands the script --
# so without a table here the seam could not tell a mailbox that works from one that is not there.
SEAM_SIDEARM_TABLE = '@camotable;P~PRUEBA C~MISC~~;%d~%d' % (identifier_of('Weapons/Custom/U_SEC_TEST_U_M9'), 287899934)


# ---------------------------------------------------------------------------------------------------------
# SIDEARMS (keku, 2026-10-06: *"lo que queremos y es como lo hemos hecho con todos los camos es que se abra una ventana nueva"*, and
# the pistols' camos *"con el mismo método de los accesorios de las armas"*). The SIDEARM row of the LOADOUT screen is a door, as
# an accessory row is: a click on its box opens a camo window of that pistol -- the worn pistol as "Sin camuflaje" and the camos
# made of it -- and a pick is stored the way the game stores a sidearm.
#
# 📐 Measured offline (EBX-Json, no mount):
#   . the LOADOUT screen (customizesoldiersubscreen, movie ui/assets/customizesoldiersubscreen) has five KitSelector rows bound to
#     UICustomizationComp.KitPrimaryWeapon / KitSecondaryWeapon / KitGadget1 / KitGadget2 / KitSpecialization ("Setup"). Only the
#     weapon row's Button1Released is wired (-> SetWeaponCategory '0' -> AccessoriesButton); the SIDEARM row's is FREE.
#   . a sidearm chosen on the row: SelectorChanged -> SetWeaponCategory (Param '1') -> SetSecondaryWeapon (Param '', on
#     CustSecondaryWeapon) -> SetWeapon (output) -> the flow graph's UpdateWeaponCustomization (2001688152 ['0','1']). A
#     DataSetNode passes on the value that came IN, not its Param: SetSecondaryWeapon writes the selector's item.
#   . leaving the loadout through Confirm / Esc stores it (SaveWeaponsEnabled -> StoreWeaponCustomization ['1']); its
#     AccessoriesButton --pop 1--> the accessories screen, and the way back --> CustomizeSoldierSubScreen.EnterScreen.
# So the window is an accessory window on the SIDEARM row's list (its grid binds what the row binds, as the accessory windows bind
# WeaponAccessoryN), its pick runs the game's own chain (SetWeaponCategory '1' -> SetSecondaryWeapon -> SetWeapon -> the game's
# UpdateWeaponCustomization), and BACK returns to the loadout, whose own BACK stores as it always does.
# The door is moved in the LOADOUT screen shipped static, as the KITS screen's (the law of the skins: a flow graph door changes
# in the screen that fires it); nothing of the game is removed -- the port was free.
#
# ⭐ AND THE CROSSBOW (keku, 2026-10-06: *"la ballesta es detectado como gadget, el usuario solo puede abrir la ventana de camo en el
# gadget cuando SOLO aparezca la ballesta, o la ballesta con mira"*). It is a GADGET: GADGET1 of the assault, engineer and recon
# kits, GADGET2 of the support kit (both teams, base and Aftermath), as two unlocks -- U_Crossbow_Scoped_Cobra (its Extra: the Kobra)
# and U_Crossbow_Scoped_RifleScope (the PKS-07). Measured on the loadout: the GADGET1 row (KitSelector_03, KitGadget1) writes
# category '2' into CustGadgetOne, the GADGET2 row (KitSelector_04, KitGadget2) '5' into CustGadgetTwo -- both Button1Released
# free. So each gadget row gets the SIDEARM row's door and a window of its own, and the door opens ONLY on those two unlocks (or a
# camo of one): on a medkit, C4, mine… nothing happens -- the rows' script checks the item shown, on the click and on Activate.
LOADOUT_PARTITION = 'ui/flow/screen/customizesoldiersubscreen'
LOADOUT_MOVIE = 'ui/assets/customizesoldiersubscreen'
LOADOUT_ROOT = 'instance1'
LOADOUT_STATE = 'CustomizeSoldierSubScreen'
LOADOUT_HEADER_PATH = 'ID_M_CUST_KITSSUB_TITLE_PATH,ID_M_CUST_KITSSUB_TITLE'
# 📐 the loadout movie, read out of the cached movie (scratchpad xbow/swf_tags.py): root sprite = character 8 ("instance1") with its
# eleven widgets at depths 1..11, and it ALREADY IMPORTS TextField.swf as character 4 -- so its mailbox is the accessories screen's:
# a second, empty TextField of our own off screen, no import added and not one of the game's clips touched (keku 2026-10-06: the
# pistols' camos are picked in their window, so the SIDEARM row must fold them -- and folding needs the table)
LOADOUT_ROOT_SPRITE = 8
LOADOUT_CHAR_TEXTFIELD = 4
LOADOUT_MAIL_DEPTH = 12
WEAPON_CATEGORY_KEY = data_key_of('CustSelectedWeaponCategory')        # 832223043
UPDATE_WEAPON_ACTION = 2001688152       # UpdateWeaponCustomization, Params ['0','1']: what the game fires on SetWeapon
WEAPON_PICK_OUTPUT = 'SetWeapon'        # a window's pick leaves through an output named as the loadout's own
CROSSBOW_UNLOCKS = ['Weapons/XP4_Crossbow_Prototype/U_Crossbow_Scoped_Cobra',
                    'Weapons/XP4_Crossbow_Prototype/U_Crossbow_Scoped_RifleScope']
# The loadout rows that open a camo window. Per row:
#   row         the loadout's KitSelector
#   slot        the window's slot name in the scripts' shared marks (_global._camoOpenSlot, "WVA<slot>,<id>"): letters, so they never
#               meet the accessory rows' 1..3
#   category    CustSelectedWeaponCategory of the row (its own SetWeaponCategory Param, measured)
#   items/set   the row's list (its "Setup" binding) and what its Set<slot> writes
#   set_node    the loadout's own name for that Set node (the window's pick is the same chain)
#   partition / state / door: the window, its StateNode in the flow graph, the loadout's output into it
#   only        the unlocks the door opens on ([] = any): the gadget rows open only on the crossbow
WEAPON_ROWS = [
    {'row': 'KitSelector_02', 'slot': 'S', 'category': '1', 'items': data_key_of('KitSecondaryWeapon'),
     'set': data_key_of('CustSecondaryWeapon'), 'set_node': 'SetSecondaryWeapon',
     'partition': 'ui/flow/screen/customizesidearmscreen', 'state': 'CustomizeSidearmScreen', 'door': 'SidearmButton', 'only': []},
    {'row': 'KitSelector_03', 'slot': 'G1', 'category': '2', 'items': data_key_of('KitGadget1'),
     'set': data_key_of('CustGadgetOne'), 'set_node': 'SetGadgetOne',
     'partition': 'ui/flow/screen/customizegadget1screen', 'state': 'CustomizeGadget1Screen', 'door': 'Gadget1Button',
     'only': CROSSBOW_UNLOCKS},
    {'row': 'KitSelector_04', 'slot': 'G2', 'category': '5', 'items': data_key_of('KitGadget2'),
     'set': data_key_of('CustGadgetTwo'), 'set_node': 'SetGadgetTwo',
     'partition': 'ui/flow/screen/customizegadget2screen', 'state': 'CustomizeGadget2Screen', 'door': 'Gadget2Button',
     'only': CROSSBOW_UNLOCKS},
]
for _spec, _items, _set in zip(WEAPON_ROWS, (-1698501497, -167212006, -167212007), (-378770976, 305101192, 305127776)):
    assert (_spec['items'], _spec['set']) == (_items, _set), (_spec['row'], _spec['items'], _spec['set'])   # measured on the loadout
assert WEAPON_CATEGORY_KEY == 832223043
assert [identifier_of(n) for n in CROSSBOW_UNLOCKS] == [120742943, 3181383740]   # the ids the game's EBX carries (measured)


def sidearm_vars(on=False):
    """The loadout weapon windows' variable (pistol, crossbow), given on every screen the camo screen's script runs on (the
    three-lists trap, see soldier_vars). _camoBaseOwn "1" = the window is about the item WORN even when no table entry names a base
    (the pistol alone, never the whole row of pistols); "0" leaves every other screen as it was."""
    return [('_camoBaseOwn', '1' if on else '0')]


def compile_script(body, tag):
    """CamoCommon.as + Data/<body>.as -> Data/<body>.avm1 (+ .b64); returns the base64 text."""
    common = open(os.path.join(DATA, 'CamoCommon.as'), encoding='utf-8').read()
    src = open(os.path.join(DATA, body + '.as'), encoding='utf-8').read()
    tmp = os.path.join(tempfile.gettempdir(), body + '.full.as')
    with open(tmp, 'w', encoding='utf-8', newline='\n') as f:
        f.write(common + '\n\n// ==== ' + body + '.as ====\n\n' + src)
    avm1 = os.path.join(DATA, body + '.avm1')
    b64 = os.path.join(DATA, body + '.avm1.b64')
    out = subprocess.run([sys.executable, COMPILE, tmp, avm1, '--base64', b64], capture_output=True, text=True, encoding='utf-8', errors='replace')
    if out.returncode != 0 or not os.path.exists(b64):
        print(out.stdout[-2000:]); print(out.stderr[-2000:])
        raise SystemExit('compile of ' + body + ' failed')
    print(f'{body}: {os.path.getsize(avm1)} bytes ({tag})')
    return open(b64, encoding='ascii').read().strip()


def script_vars(pairs):
    for name, value in pairs:
        for bad in (':', '|'):
            assert bad not in str(value), f'script variable {name} may not contain {bad!r}: {value!r}'
    return '|'.join(f'{n}={v}' for n, v in pairs)


def widget_node(name, widget, focus=-1, props=None, fields=None, connections=None, graph_y=0.0, binding=None):
    return {
        'instanceName': name, 'new': True, 'type': 'WidgetNode', 'widget': widget, 'focusIndex': focus, 'zDepthLevel': 0,
        'properties': props or {}, 'replaceProperties': True, 'binding': binding, 'fields': fields or {}, 'ports': [],
        'connections': connections or [], 'graphX': 300.0, 'graphY': graph_y,
    }


def logic_node(name, node_type, fields=None, ports=None, connections=None, graph_x=700.0, graph_y=0.0, new=True):
    return {
        'instanceName': name, 'new': new, 'type': node_type, 'widget': None, 'focusIndex': -1, 'zDepthLevel': 0,
        'properties': {}, 'replaceProperties': False, 'binding': None, 'fields': fields or {}, 'ports': ports or [],
        'connections': connections or [], 'graphX': graph_x, 'graphY': graph_y,
    }


def accessories_screen(row_b64, table):
    families = ';'.join(name + '~' + ','.join(keys) for name, keys in FAMILIES)
    acc_rows = ','.join(row for row, _slot, _p, _t, _s, _o, _n in ACCESSORY_ROWS)
    vars_ = script_vars([('_camoRoot', ACC_ROOT), ('_camoRow', CAMO_ROW), ('_camoAccRows', acc_rows), ('_camoAccSlots', ''), ('_camoAccOnly', ''),
                         ('_camoLoadoutRows', LOADOUT_ROWS), ('_camoTable', table), ('_camoFamilies', families),
                         ('_camoNoWindow', no_window_ids()), ('_camoNoWindowPairs', no_window_pairs()),
                         ('_camoMail', CAMO_MAIL_CLIP), ('_camoTableKey', str(CAMO_TABLE_KEY))])
    nodes = [
        # the row's Button1Released (what Activate fires on PC; the script fires it on a click) leaves the screen through CamoButton
        {**logic_node(CAMO_ROW, 'WidgetNode', new=False, graph_x=300.0, graph_y=500.0),
         'connections': [{'event': 'OnItemReleased', 'toNode': 'CamoButton', 'toPort': 'In'}]},
        logic_node('CamoButton', 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=700.0, graph_y=500.0),
    ]
    # the same door on the three accessory rows. Their Button1Released is FREE in the shipped screen (measured: the wire that
    # writes SetAccessory1..3 comes out of SelectorChanged, which is what the arrows fire), so the arrows go on working.
    for i, (row, _slot, _partition, _title, _state, out_node, _name) in enumerate(ACCESSORY_ROWS):
        nodes.append({**logic_node(row, 'WidgetNode', new=False, graph_x=300.0, graph_y=560.0 + 60.0 * i),
                      'connections': [{'event': 'OnItemReleased', 'toNode': out_node, 'toPort': 'In'}]})
        nodes.append(logic_node(out_node, 'InstanceOutputNode', fields={'DestroyGraph': True},
                                graph_x=700.0, graph_y=560.0 + 60.0 * i))
    # the mailbox: our own clip, its binding, its writer, and the screen's OWN EnterScreen as the trigger (the shipped
    # graph already has that node -- its port is `Out`, read from the screen itself; nothing of the game is replaced,
    # a wire is added to a port it already fires)
    nodes.append(widget_node(CAMO_MAIL_CLIP, 'ui/assets/textfield', graph_y=800.0,
                             fields={'DataBinding': {'$type': 'UIDynamicDataBinding', 'Refresh': True, 'Bindings': [
                                 {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP,
                                  'DataKey': CAMO_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}}))
    nodes.append(logic_node(CAMO_TABLE_NODE, 'DataSetNode', graph_x=400.0, graph_y=800.0,
                            fields={'Param': '', 'SetToEmptyString': False, 'ForceUpdate': True,
                                    'DataSource': {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP,
                                                   'DataKey': CAMO_TABLE_KEY, 'UseDirectAccess': False,
                                                   'UpdateOnInitialize': True}}))
    nodes.append(logic_node('EnterScreen', 'InstanceInputNode', new=False, graph_x=40.0, graph_y=800.0,
                            connections=[{'fromPort': 'Out', 'toNode': CAMO_TABLE_NODE, 'toPort': 'In'}]))
    return {
        'partition': ACC_PARTITION, 'movie': ACC_MOVIE, 'new': False, 'template': 'ui/flow/screen/emptyscreen', 'title': None,
        'stage': [f'add:{ACC_ROOT_SPRITE}:{CAMO_MAIL_CLIP}:{ACC_CHAR_TEXTFIELD}:{ACC_MAIL_DEPTH}:'
                  f'{ACC_MAIL_AT[0]}:{ACC_MAIL_AT[1]}:1:1',
                  f'script:camorow:{vars_}:{row_b64}'],
        'nodes': nodes,
        'fields': {}, 'removedConnections': [],
    }


def accessory_screen(spec, screen_b64, table):
    """One accessory row's own screen: the same screen as the camo one, with no family buttons and the grid filtered to the
    camos of the accessory worn (the script's accessory mode). Its pick writes that row's CustSelectedAccessory."""
    row, slot, partition, title_id, state, _out, screen_title = spec
    families = ';'.join(name + '~' + ','.join(keys) for name, keys in FAMILIES)
    buttons = ['CamoCat_%02d' % (i + 1) for i in range(len(FAMILIES))]
    grid_y = GRID_Y                          # under the buttons, exactly where the camo screen puts its grid
    rows_shown = GRID_ROWS
    vars_ = script_vars([
        ('_camoRoot', CAMO_ROOT), ('_camoButtons', ','.join(buttons)), ('_camoBase', '1'), ('_camoSlot', str(slot)),
        ('_camoDefaultLabel', DEFAULT_CAMO_LABEL), ('_camoDefaultDesc', DEFAULT_CAMO_DESC),
        ('_camoNoCamoIds', NO_CAMO_IDS),
        ('_camoDefaultImage', DEFAULT_CAMO_IMAGE), ('_camoGrid', 'CamoGrid'),
        ('_camoInfo', INFO_BOX), ('_camoInfoAt', INFO_AT), ('_camoCell', f'{CELL_W},{CELL_GAP}'),
        ('_camoImageScale', str(IMAGE_SCALE)), ('_camoLabelShift', str(LABEL_SHIFT)),
        ('_camoHeader', 'PageHeader_01'), ('_camoPath', HEADER_PATH_IDS), ('_camoTitle', title_id), ('_camoTitles', ''),
        ('_camoTable', table), ('_camoFamilies', families), ('_camoDragLeft', str(DRAG_LEFT)),
        ('_camoNoModel', NO_MODEL_TEXTS), ('_camoNoWindow', no_window_ids()),
        ('_camoNoWindowPairs', no_window_pairs()),
    ] + soldier_vars(False) + sidearm_vars(False))
    grid_w = GRID_WIDTH
    grid_h = round(rows_shown * CELL_H + (rows_shown - 1) * CELL_GAP)
    grid_sx, grid_sy = grid_w / GRID_NATURAL[0], grid_h / GRID_NATURAL[1]
    depth = 10
    stage = [
        f'import:PageHeader.swf:{CHAR_PAGEHEADER}:PageHeader',
        f'import:TextField.swf:{CHAR_TEXTFIELD}:TextField',
        f'import:Button.swf:{CHAR_BUTTON}:Button',
        f'import:KitInfoBox.swf:{CHAR_INFOBOX}:KitInfoBox',
        f'import:Grid.swf:{CHAR_GRID}:Grid',
        f'add:{ROOT_SPRITE}:PageHeader_01:{CHAR_PAGEHEADER}:{depth}:0:0:1:1',
        f'add:{ROOT_SPRITE}:TextField_01:{CHAR_TEXTFIELD}:{depth + 1}:96:96:1:1',
        'vars:TextField_01:m_useBorder=false|m_align=left|m_rowType=bold1',
        f'add:{ROOT_SPRITE}:Button_02:{CHAR_BUTTON}:{depth + 2}:545:97:2.0469:0.5',
        'vars:Button_02:' + BUTTON_VARS,
        f'add:{ROOT_SPRITE}:{INFO_BOX}:{CHAR_INFOBOX}:{depth + 3}:0:0:1:1',
        f'vars:{INFO_BOX}:DEBUG_TEST_IN_FLASH=false',
        f'add:{ROOT_SPRITE}:CamoGrid:{CHAR_GRID}:{depth + 4}:{PANEL_X}:{grid_y}:{grid_sx:.4f}:{grid_sy:.4f}',
        'vars:CamoGrid:i_cellType=KitCell|i_selectedIndex=0.0|i_loopNavigation=false|DEBUG_TEST_IN_FLASH=false',
    ]
    set_node = f'SetAccessory{slot}'
    nodes = [
        widget_node('PageHeader_01', 'ui/assets/pageheader', graph_y=40.0, fields={'DataBinding': {
            '$type': 'UIPageHeaderBinding',
            'Header': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'SubHeader': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'Icon': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'StaticHeader': 'ID_M_CUST_KITSACC_TITLE_PATH', 'StaticSubHeader': title_id, 'StaticIcon': '', 'LevelSpecificHeaders': [],
        }}),
        widget_node('TextField_01', 'ui/assets/textfield', graph_y=120.0, fields={'DataBinding': {
            '$type': 'UIDynamicDataBinding', 'Refresh': True,
            'Bindings': [{'DataName': 'Text', 'DataCategory': KIT_COMP, 'DataKey': KIT_NAME_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}}),
        widget_node('Button_02', 'ui/assets/button', graph_y=200.0,
                    props={'TextData': 'ID_M_BACK', 'IconData': '', 'Visible': '', 'Toggled': '', 'HideOnConsole': 'true'},
                    connections=[{'event': 'OnItemReleased', 'toNode': 'Confirm', 'toPort': 'In'}]),
        widget_node(INFO_BOX, 'ui/assets/kitinfobox', graph_y=280.0),
        {**widget_node('CamoGrid', 'ui/assets/grid', focus=0, graph_y=360.0,
                       props={'p_cellType': 'KitCell', 'p_selectedIndex': '0', 'p_highlightInactive': '0', 'p_keyboardNavigation': '0',
                              'p_dynamicLoading': '0', 'p_itemPicker': '0', 'p_animTime': '0.5', 'p_itemPickerAlphaMin': '100', 'p_itemPickerAlphaMax': '100'},
                       fields={'DataBinding': {'$type': 'UIDynamicDataBinding', 'Refresh': True, 'Bindings': [
                           {'DataName': 'GridItems', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': ACCESSORY_ITEMS_KEY[slot], 'UseDirectAccess': False, 'UpdateOnInitialize': True},
                           # the mailbox (see the camo screen): the live table, by our own key
                           {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': CAMO_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}},
                       connections=[{'event': 'OnItemReleased', 'toNode': set_node, 'toPort': 'In'}])},
        logic_node(set_node, 'DataSetNode', graph_x=700.0, graph_y=360.0,
                   fields={'Param': '', 'SetToEmptyString': False, 'ForceUpdate': False,
                           'DataSource': {'DataName': '', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': ACCESSORY_SET_KEY[slot], 'UseDirectAccess': False, 'UpdateOnInitialize': True}},
                   connections=[{'fromPort': 'Out', 'toNode': 'AccessoryChanged', 'toPort': 'In'}]),
        # the mailbox's writer on this screen too: the mod fills its Param at run time (2026-09-20, measured working
        # on the camo screen: TBL45,8203 arrived by this exact path)
        logic_node(CAMO_TABLE_NODE, 'DataSetNode', graph_x=400.0, graph_y=40.0,
                   fields={'Param': '', 'SetToEmptyString': False, 'ForceUpdate': True,
                           'DataSource': {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP,
                                          'DataKey': CAMO_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}}),
        logic_node('AccessoryChanged', 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=1000.0, graph_y=360.0),
        logic_node('Confirm', 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=700.0, graph_y=200.0),
        logic_node('EnterScreen', 'InstanceInputNode', graph_x=40.0, graph_y=40.0,
                   connections=[{'fromPort': 'Out', 'toNode': CAMO_TABLE_NODE, 'toPort': 'In'}]),
    ]
    btn_sx, btn_sy = BUTTON_W / BUTTON_NATURAL[0], BUTTON_H / BUTTON_NATURAL[1]
    for i, (name, label) in enumerate(zip(buttons, [n for n, _ in FAMILIES])):
        col, row_i = i % 2, i // 2
        x = PANEL_X + col * (BUTTON_W + BUTTON_GAP)
        y = BUTTONS_Y + row_i * (BUTTON_H + BUTTON_GAP)
        stage.append(f'add:{ROOT_SPRITE}:{name}:{CHAR_BUTTON}:{depth + 5 + i}:{x}:{y}:{btn_sx:.4f}:{btn_sy:.4f}')
        stage.append(f'vars:{name}:' + BUTTON_VARS)
        nodes.append(widget_node(name, 'ui/assets/button', graph_y=460.0 + 60.0 * i,
                                 props={'TextData': label, 'HideOnConsole': '', 'ButtonItem': '', 'IconData': '', 'Toggled': ''}))

    stage.append(f'script:camoscreen:{vars_}:{screen_b64}')
    return {
        'partition': partition, 'movie': ACCESSORY_MOVIE[slot], 'new': True, 'template': 'ui/flow/screen/emptyscreen',
        'title': screen_title, 'stage': stage, 'nodes': nodes, 'fields': {}, 'removedConnections': [],
    }


def camo_screen(screen_b64, table):
    names = [name for name, _ in FAMILIES]
    families = ';'.join(name + '~' + ','.join(keys) for name, keys in FAMILIES)
    buttons = ['CamoCat_%02d' % (i + 1) for i in range(len(FAMILIES))]
    vars_ = script_vars([
        ('_camoRoot', CAMO_ROOT), ('_camoButtons', ','.join(buttons)), ('_camoGrid', 'CamoGrid'),
        ('_camoInfo', INFO_BOX), ('_camoInfoAt', INFO_AT), ('_camoCell', f'{CELL_W},{CELL_GAP}'), ('_camoImageScale', str(IMAGE_SCALE)), ('_camoLabelShift', str(LABEL_SHIFT)),
        ('_camoHeader', 'PageHeader_01'), ('_camoPath', HEADER_PATH_IDS), ('_camoTitle', HEADER_TITLE_ID), ('_camoTitles', HEADER_TITLES),
        ('_camoTable', table), ('_camoFamilies', families), ('_camoDragLeft', str(DRAG_LEFT)),
        # ⛔ THE CAMO-LESS CELL NEEDS THESE HERE TOO (keku, 2026-09-20: *"estos problemas solo ocurren en el
        # apartado camos de las armas, en los accesorios está bien"*). They were on the ACCESSORY screens only,
        # so on the weapon's own screen the script had no name for that cell and no ids to recognise it by: it
        # drew with the game's label ("Camuflaje predeterminado") wherever its family put it. Same four values,
        # same meaning, both screens.
        ('_camoDefaultLabel', DEFAULT_CAMO_LABEL), ('_camoDefaultDesc', DEFAULT_CAMO_DESC),
        ('_camoDefaultImage', DEFAULT_CAMO_IMAGE), ('_camoNoCamoIds', NO_CAMO_IDS),
    ] + soldier_vars(False) + sidearm_vars(False))
    grid_w = GRID_WIDTH
    grid_h = round(GRID_ROWS * CELL_H + (GRID_ROWS - 1) * CELL_GAP)
    grid_sx, grid_sy = grid_w / GRID_NATURAL[0], grid_h / GRID_NATURAL[1]
    btn_sx, btn_sy = BUTTON_W / BUTTON_NATURAL[0], BUTTON_H / BUTTON_NATURAL[1]
    depth = 10
    stage = [
        f'import:PageHeader.swf:{CHAR_PAGEHEADER}:PageHeader',
        f'import:TextField.swf:{CHAR_TEXTFIELD}:TextField',
        f'import:Button.swf:{CHAR_BUTTON}:Button',
        f'import:KitInfoBox.swf:{CHAR_INFOBOX}:KitInfoBox',
        f'import:Grid.swf:{CHAR_GRID}:Grid',
        # the header, the kit's name and BACK where the accessories screen has them
        f'add:{ROOT_SPRITE}:PageHeader_01:{CHAR_PAGEHEADER}:{depth}:0:0:1:1',
        f'add:{ROOT_SPRITE}:TextField_01:{CHAR_TEXTFIELD}:{depth + 1}:96:96:1:1',
        'vars:TextField_01:m_useBorder=false|m_align=left|m_rowType=bold1',
        f'add:{ROOT_SPRITE}:Button_02:{CHAR_BUTTON}:{depth + 2}:545:97:2.0469:0.5',
        'vars:Button_02:' + BUTTON_VARS,
        f'add:{ROOT_SPRITE}:{INFO_BOX}:{CHAR_INFOBOX}:{depth + 3}:0:0:1:1',
        f'vars:{INFO_BOX}:DEBUG_TEST_IN_FLASH=false',
        f'add:{ROOT_SPRITE}:CamoGrid:{CHAR_GRID}:{depth + 4}:{PANEL_X}:{GRID_Y}:{grid_sx:.4f}:{grid_sy:.4f}',
        'vars:CamoGrid:i_cellType=KitCell|i_selectedIndex=0.0|i_loopNavigation=false|DEBUG_TEST_IN_FLASH=false',
    ]
    nodes = [
        widget_node('PageHeader_01', 'ui/assets/pageheader', graph_y=40.0, fields={'DataBinding': {
            '$type': 'UIPageHeaderBinding',
            'Header': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'SubHeader': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'Icon': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'StaticHeader': 'ID_M_CUST_KITSACC_TITLE_PATH', 'StaticSubHeader': HEADER_TITLE_ID, 'StaticIcon': '', 'LevelSpecificHeaders': [],
        }}),
        widget_node('TextField_01', 'ui/assets/textfield', graph_y=120.0, fields={'DataBinding': {
            '$type': 'UIDynamicDataBinding', 'Refresh': True,
            'Bindings': [{'DataName': 'Text', 'DataCategory': KIT_COMP, 'DataKey': KIT_NAME_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}}),
        widget_node('Button_02', 'ui/assets/button', graph_y=200.0,
                    props={'TextData': 'ID_M_BACK', 'IconData': '', 'Visible': '', 'Toggled': '', 'HideOnConsole': 'true'},
                    connections=[{'event': 'OnItemReleased', 'toNode': 'Confirm', 'toPort': 'In'}]),
        widget_node(INFO_BOX, 'ui/assets/kitinfobox', graph_y=280.0),
        # the grid: the only widget of focus group 0 — the engine hands it the keys; its data is the camo row's
        {**widget_node('CamoGrid', 'ui/assets/grid', focus=0, graph_y=360.0,
                       props={'p_cellType': 'KitCell', 'p_selectedIndex': '0', 'p_highlightInactive': '0', 'p_keyboardNavigation': '0',
                              'p_dynamicLoading': '0', 'p_itemPicker': '0', 'p_animTime': '0.5', 'p_itemPickerAlphaMin': '100', 'p_itemPickerAlphaMax': '100'},
                       fields={'DataBinding': {'$type': 'UIDynamicDataBinding', 'Refresh': True, 'Bindings': [
                           {'DataName': 'GridItems', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': CAMO_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
                           # the mailbox, read where the camos are drawn: whatever the mod put in the key arrives here by its own name
                           {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': CAMO_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}},
                       connections=[{'event': 'OnItemReleased', 'toNode': 'SetAccessory4', 'toPort': 'In'}])},
        # the pick: SetAccessory4 (the same key the accessories screen's node writes) → the AccessoryChanged output → the flow graph stores
        logic_node('SetAccessory4', 'DataSetNode', graph_x=700.0, graph_y=360.0,
                   fields={'Param': '', 'SetToEmptyString': False, 'ForceUpdate': False,
                           'DataSource': {'DataName': '', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': SET_ACCESSORY4_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}},
                   connections=[{'fromPort': 'Out', 'toNode': 'AccessoryChanged', 'toPort': 'In'}]),
        # ⭐ THE MAILBOX'S WRITER: fires when the screen is ENTERED and puts whatever `Param` holds into our key.
        # `Param` is empty here on purpose -- the mod writes the live table into it at run time, which is the whole
        # point: nothing about a camo is compiled into this movie.
        logic_node(CAMO_TABLE_NODE, 'DataSetNode', graph_x=400.0, graph_y=40.0,
                   fields={'Param': '', 'SetToEmptyString': False, 'ForceUpdate': True,
                           'DataSource': {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP,
                                          'DataKey': CAMO_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}}),
        logic_node('AccessoryChanged', 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=1000.0, graph_y=360.0),
        logic_node('Confirm', 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=700.0, graph_y=200.0),
        logic_node('EnterScreen', 'InstanceInputNode', graph_x=40.0, graph_y=40.0,
                   connections=[{'fromPort': 'Out', 'toNode': CAMO_TABLE_NODE, 'toPort': 'In'}]),
    ]
    for i, (name, label) in enumerate(zip(buttons, names)):
        col, row = i % 2, i // 2
        x = PANEL_X + col * (BUTTON_W + BUTTON_GAP)
        y = BUTTONS_Y + row * (BUTTON_H + BUTTON_GAP)
        stage.append(f'add:{ROOT_SPRITE}:{name}:{CHAR_BUTTON}:{depth + 5 + i}:{x}:{y}:{btn_sx:.4f}:{btn_sy:.4f}')
        stage.append(f'vars:{name}:' + BUTTON_VARS)
        nodes.append(widget_node(name, 'ui/assets/button', graph_y=460.0 + 60.0 * i,
                                 props={'TextData': label, 'HideOnConsole': '', 'ButtonItem': '', 'IconData': '', 'Toggled': ''}))
    stage.append(f'script:camoscreen:{vars_}:{screen_b64}')
    return {
        'partition': CAMO_PARTITION, 'movie': CAMO_MOVIE, 'new': True, 'template': 'ui/flow/screen/emptyscreen', 'title': CAMO_TITLE,
        'stage': stage, 'nodes': nodes, 'fields': {}, 'removedConnections': [],
    }


def kitview_movie():
    """The game's kit movie with a second button on its VEHICLE row: button1 (PERSONAL.) cloned as button2 (CAMUFLAJE), both
    118 px wide, PERSONAL. at the right edge and CAMUFLAJE left of it. Only TIERRA / AIRE place this row (measured); a row
    whose BtnLabel2 is empty hides button2 (the row's own code) -- never the case with this document, which sets it."""
    sx = KIT_BUTTON_W / BUTTON_NATURAL[0]
    return {
        'resource': KITVIEW_MOVIE, 'file': '',
        'stage': [f'clone:{VEHICLE_ROW_SYMBOL}/button1:button2:{KIT_BUTTON2_DEPTH}:{KIT_BUTTON2_AT[0]}:{KIT_BUTTON2_AT[1]}',
                  f'move:{VEHICLE_ROW_SYMBOL}/button1:{KIT_BUTTON1_AT[0]}:{KIT_BUTTON1_AT[1]}',
                  f'scale:{VEHICLE_ROW_SYMBOL}/button1:{sx:.5f}:{KIT_BUTTON_H_SCALE}',
                  f'scale:{VEHICLE_ROW_SYMBOL}/button2:{sx:.5f}:{KIT_BUTTON_H_SCALE}',
                  # the rows of our own: the HUD's minimap icon where the class icon sits, hidden by the script on the game's rows
                  f'import:{VEHICLE_MAP_ICON_URL}:{VEHICLE_MAP_ICON_CHAR}:{VEHICLE_MAP_ICON_SYMBOL}',
                  f'add:{VEHICLE_ROW_SPRITE}:{VEHICLE_MAP_ICON}:{VEHICLE_MAP_ICON_CHAR}:{VEHICLE_MAP_ICON_DEPTH}:18:22:1:1'],
    }


def vehicle_rows_screen(spec, row_b64):
    """TIERRA or AIRE (the game's screen): the rows' second button reads CAMUFLAJE and opens the camo window.
    Its Button2Released goes through a SetVehicle of our own (the same key the PERSONAL. path writes, so the row clicked is
    the vehicle selected) into a new output; the flow graph takes it from there."""
    kind, partition, movie, _state, _camo_partition, _title, _camo_state, door, _path = spec
    set_node = door + 'SetVehicle'
    vars_ = script_vars([('_vehRoot', VEHICLE_ROOT), ('_vehKitView', VEHICLE_KITVIEW), ('_vehWords', HEADER_TITLES),
                         ('_vehKind', kind), ('_camoFamilies', ''), ('_camoTable', ''),
                         ('_vehOwnRows', vehicle_own_rows()), ('_vehMapIcon', VEHICLE_MAP_ICON),
                         ('_vehOwnHeight', VEHICLE_OWN_ROW_HEIGHT), ('_vehIconHeight', VEHICLE_MAP_ICON_HEIGHT),
                         ('_vehIconWhite', VEHICLE_MAP_ICON_WHITE)])
    nodes = [
        # the targets first, then who wires to them
        logic_node(door, 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=1000.0, graph_y=500.0),
        logic_node(set_node, 'DataSetNode', graph_x=700.0, graph_y=500.0,
                   fields={'Param': '', 'SetToEmptyString': False, 'ForceUpdate': False,
                           'DataSource': {'DataName': '', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': VEHICLE_SET_KEY,
                                          'UseDirectAccess': False, 'UpdateOnInitialize': True}},
                   connections=[{'fromPort': 'Out', 'toNode': door, 'toPort': 'In'}]),
        # the list: its second button gets a label (the property keeps BtnLabel1; the script localises BtnLabel2) and a wire
        {**logic_node(VEHICLE_KITVIEW, 'WidgetNode', new=False, graph_x=300.0, graph_y=500.0),
         'properties': {'BtnLabel2': VEHICLE_CAMO_BUTTON_TEXT}, 'replaceProperties': False,
         'connections': [{'event': 'OnChanged', 'toNode': set_node, 'toPort': 'In'}]},
    ]
    return {
        'partition': partition, 'movie': movie, 'new': False, 'template': 'ui/flow/screen/emptyscreen', 'title': None,
        'stage': [f'script:camovehiclerow:{vars_}:{row_b64}'],
        'nodes': nodes, 'fields': {}, 'removedConnections': [],
    }


def vehicle_camo_screen(spec, screen_b64, vehicle_table=''):
    """A vehicle's camo window: the weapon's camo screen (header, name, BACK, the eight families, the grid, the info box) in
    the script's vehicle mode -- the name is the vehicle's, the grid lists the window's own "Sin camuflaje" and the vehicle
    camos of its mailbox. `vehicle_table` = what the mailbox's writer carries in the document ('' shipping: the Lua writes it)."""
    kind, _partition, _movie, _state, partition, title, _camo_state, _door, path = spec
    names = [name for name, _ in FAMILIES]
    families = ';'.join(name + '~' + ','.join(keys) for name, keys in FAMILIES)
    buttons = ['CamoCat_%02d' % (i + 1) for i in range(len(FAMILIES))]
    vars_ = script_vars([
        ('_camoRoot', CAMO_ROOT), ('_camoButtons', ','.join(buttons)), ('_camoGrid', 'CamoGrid'),
        ('_camoInfo', INFO_BOX), ('_camoInfoAt', INFO_AT), ('_camoCell', f'{CELL_W},{CELL_GAP}'), ('_camoImageScale', str(IMAGE_SCALE)), ('_camoLabelShift', str(LABEL_SHIFT)),
        ('_camoHeader', 'PageHeader_01'), ('_camoPath', path), ('_camoTitle', HEADER_TITLE_ID), ('_camoTitles', HEADER_TITLES),
        ('_camoTable', ''), ('_camoFamilies', families), ('_camoDragLeft', str(DRAG_LEFT)),
        # the no-camo cell: the weapon's own name and picture; no description (the game's says "default camo" of a WEAPON)
        ('_camoDefaultLabel', DEFAULT_CAMO_LABEL), ('_camoDefaultDesc', ''),
        ('_camoDefaultImage', DEFAULT_CAMO_IMAGE), ('_camoNoCamoIds', NO_CAMO_IDS),
        ('_camoVehicle', kind), ('_camoHideMs', str(VEHICLE_HIDE_MS)), ('_camoRespawnMs', VEHICLE_RESPAWN_MS),
    ] + soldier_vars(False) + sidearm_vars(False))
    grid_h = round(GRID_ROWS * CELL_H + (GRID_ROWS - 1) * CELL_GAP)
    grid_sx, grid_sy = GRID_WIDTH / GRID_NATURAL[0], grid_h / GRID_NATURAL[1]
    btn_sx, btn_sy = BUTTON_W / BUTTON_NATURAL[0], BUTTON_H / BUTTON_NATURAL[1]
    depth = 10
    stage = [
        f'import:PageHeader.swf:{CHAR_PAGEHEADER}:PageHeader',
        f'import:TextField.swf:{CHAR_TEXTFIELD}:TextField',
        f'import:Button.swf:{CHAR_BUTTON}:Button',
        f'import:KitInfoBox.swf:{CHAR_INFOBOX}:KitInfoBox',
        f'import:Grid.swf:{CHAR_GRID}:Grid',
        f'add:{ROOT_SPRITE}:PageHeader_01:{CHAR_PAGEHEADER}:{depth}:0:0:1:1',
        f'add:{ROOT_SPRITE}:TextField_01:{CHAR_TEXTFIELD}:{depth + 1}:96:96:1:1',
        'vars:TextField_01:m_useBorder=false|m_align=left|m_rowType=bold1',
        f'add:{ROOT_SPRITE}:Button_02:{CHAR_BUTTON}:{depth + 2}:545:97:2.0469:0.5',
        'vars:Button_02:' + BUTTON_VARS,
        f'add:{ROOT_SPRITE}:{INFO_BOX}:{CHAR_INFOBOX}:{depth + 3}:0:0:1:1',
        f'vars:{INFO_BOX}:DEBUG_TEST_IN_FLASH=false',
        f'add:{ROOT_SPRITE}:CamoGrid:{CHAR_GRID}:{depth + 4}:{PANEL_X}:{GRID_Y}:{grid_sx:.4f}:{grid_sy:.4f}',
        'vars:CamoGrid:i_cellType=KitCell|i_selectedIndex=0.0|i_loopNavigation=false|DEBUG_TEST_IN_FLASH=false',
    ]
    nodes = [
        widget_node('PageHeader_01', 'ui/assets/pageheader', graph_y=40.0, fields={'DataBinding': {
            '$type': 'UIPageHeaderBinding',
            'Header': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'SubHeader': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'Icon': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'StaticHeader': path.split(',')[0], 'StaticSubHeader': HEADER_TITLE_ID, 'StaticIcon': '', 'LevelSpecificHeaders': [],
        }}),
        # the vehicle's name, where the subscreen (PERSONAL.) shows it
        widget_node('TextField_01', 'ui/assets/textfield', graph_y=120.0, fields={'DataBinding': {
            '$type': 'UIDynamicDataBinding', 'Refresh': True,
            'Bindings': [{'DataName': 'Text', 'DataCategory': KIT_COMP, 'DataKey': VEHICLE_NAME_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}}),
        widget_node('Button_02', 'ui/assets/button', graph_y=200.0,
                    props={'TextData': 'ID_M_BACK', 'IconData': '', 'Visible': '', 'Toggled': '', 'HideOnConsole': 'true'},
                    connections=[{'event': 'OnItemReleased', 'toNode': 'Confirm', 'toPort': 'In'}]),
        widget_node(INFO_BOX, 'ui/assets/kitinfobox', graph_y=280.0),
        # the grid: the only widget of focus group 0; its items source is ours and empty (see VEHICLE_ITEMS_SOURCE) -- the cells
        # come from the MAILBOX (VEHICLE_TABLE_KEY). Its Released is the pick (VEHICLE_PICK_OUTPUT); its TooltipActive event
        # (ToggleOn: unused by a KitCell grid) is the script's "spawn the vehicle again", VEHICLE_RESPAWN_MS after a pick.
        {**widget_node('CamoGrid', 'ui/assets/grid', focus=0, graph_y=360.0,
                       props={'p_cellType': 'KitCell', 'p_selectedIndex': '0', 'p_highlightInactive': '0', 'p_keyboardNavigation': '0',
                              'p_dynamicLoading': '0', 'p_itemPicker': '0', 'p_animTime': '0.5', 'p_itemPickerAlphaMin': '100', 'p_itemPickerAlphaMax': '100'},
                       fields={'DataBinding': {'$type': 'UIDynamicDataBinding', 'Refresh': True, 'Bindings': [
                           {'DataName': 'GridItems', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': VEHICLE_ITEMS_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
                           {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': VEHICLE_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}},
                       connections=[{'event': 'ToggleOn', 'toNode': VEHICLE_RESPAWN_SET, 'toPort': 'In'},
                                    {'event': 'OnItemReleased', 'toNode': VEHICLE_PICK_OUTPUT, 'toPort': 'In'}])},
        # the respawn's selection: the row the event carries written back where the door's SetVehicle writes it (empty Param = the
        # event's), then out to the flow graph's spawn -- the game's UnSpawnVehicle → SetVehicle → SpawnVehicle
        logic_node(VEHICLE_RESPAWN_SET, 'DataSetNode', graph_x=400.0, graph_y=540.0,
                   fields={'Param': '', 'SetToEmptyString': False, 'ForceUpdate': True,
                           'DataSource': {'DataName': '', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': VEHICLE_SET_KEY,
                                          'UseDirectAccess': False, 'UpdateOnInitialize': True}},
                   connections=[{'fromPort': 'Out', 'toNode': VEHICLE_RESPAWN_OUTPUT, 'toPort': 'In'}]),
        # the mailbox's writer (Param written at run time by CamoVehicleView.lua)
        logic_node(VEHICLE_TABLE_NODE, 'DataSetNode', graph_x=400.0, graph_y=40.0,
                   fields={'Param': vehicle_table, 'SetToEmptyString': False, 'ForceUpdate': True,
                           'DataSource': {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP,
                                          'DataKey': VEHICLE_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}}),
        # ⛔ HideVehicle no longer has a wire into it: the game's vehicle stays (VEHICLE_HIDE_MS = -1) and its event became the
        # table's. The node stays so the flow graph's port keeps its target.
        logic_node('HideVehicle', 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=700.0, graph_y=420.0),
        # (DestroyGraph as the weapon window's AccessoryChanged, which fires on a pick too and leaves the window up)
        logic_node(VEHICLE_PICK_OUTPUT, 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=700.0, graph_y=480.0),
        logic_node(VEHICLE_RESPAWN_OUTPUT, 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=700.0, graph_y=540.0),
        logic_node('Confirm', 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=700.0, graph_y=200.0),
        logic_node('EnterScreen', 'InstanceInputNode', graph_x=40.0, graph_y=40.0,
                   connections=[{'fromPort': 'Out', 'toNode': VEHICLE_TABLE_NODE, 'toPort': 'In'}]),
    ]
    for i, (name, label) in enumerate(zip(buttons, names)):
        col, row = i % 2, i // 2
        x = PANEL_X + col * (BUTTON_W + BUTTON_GAP)
        y = BUTTONS_Y + row * (BUTTON_H + BUTTON_GAP)
        stage.append(f'add:{ROOT_SPRITE}:{name}:{CHAR_BUTTON}:{depth + 5 + i}:{x}:{y}:{btn_sx:.4f}:{btn_sy:.4f}')
        stage.append(f'vars:{name}:' + BUTTON_VARS)
        nodes.append(widget_node(name, 'ui/assets/button', graph_y=460.0 + 60.0 * i,
                                 props={'TextData': label, 'HideOnConsole': '', 'ButtonItem': '', 'IconData': '', 'Toggled': ''}))
    stage.append(f'script:camoscreen:{vars_}:{screen_b64}')
    return {
        'partition': partition, 'movie': 'ui/assets/' + partition.split('/')[-1], 'new': True, 'template': 'ui/flow/screen/emptyscreen',
        'title': title, 'stage': stage, 'nodes': nodes, 'fields': {}, 'removedConnections': [],
    }


def soldier_skin_screen(screen_b64, soldier_table=''):
    """The soldier's window, in place of APARIENCIA (see SOLDIERS): the camo screen in the script's soldier mode -- the appearance
    path and title, the kit's name, BACK, a row of four tabs (CONJUNTO · CABEZA · TORSO · PIERNAS, our words by language), the eight
    families under them in two rows of four, the grid and the info box. The grid lists the kit's own looks (the game's appearance
    rows) and the skins of the mailbox for this soldier and tab; a pick goes to the client's Lua ("SKN") and out through SkinPicked
    (the glitch). `soldier_table` = what the mailbox's writer carries in the document ('' shipping: the Lua writes it)."""
    names = [name for name, _ in FAMILIES]
    families = ';'.join(name + '~' + ','.join(keys) for name, keys in FAMILIES)
    buttons = ['CamoCat_%02d' % (i + 1) for i in range(len(FAMILIES))]
    vars_ = script_vars([
        ('_camoRoot', CAMO_ROOT), ('_camoButtons', ','.join(buttons)), ('_camoGrid', 'CamoGrid'),
        ('_camoInfo', INFO_BOX), ('_camoInfoAt', SOLDIER_INFO_AT), ('_camoCell', f'{CELL_W},{CELL_GAP}'), ('_camoImageScale', str(IMAGE_SCALE)), ('_camoLabelShift', str(LABEL_SHIFT)),
        ('_camoHeader', 'PageHeader_01'), ('_camoPath', SOLDIER_HEADER_PATH), ('_camoTitle', SOLDIER_HEADER_TITLE), ('_camoTitles', ''),
        ('_camoTable', ''), ('_camoFamilies', families),
        # the drag turns the mannequin, as the weapon and vehicle windows turn theirs (keku 2026-09-28: *"añade la opción de rotar el
        # personaje, pero sólo en 360º"*): a press right of the panel -> "WV1"/"WV0" -> CamoSoldierView.lua (soldierRotation, yaw only)
        ('_camoDragLeft', str(DRAG_LEFT)),
        # no camo-less cell: every cell is a look
        ('_camoDefaultLabel', ''), ('_camoDefaultDesc', ''), ('_camoDefaultImage', ''), ('_camoNoCamoIds', ''),
    ] + soldier_vars(True) + sidearm_vars(False))
    grid_h = round(GRID_ROWS * CELL_H + (GRID_ROWS - 1) * CELL_GAP)
    grid_sx, grid_sy = GRID_WIDTH / GRID_NATURAL[0], grid_h / GRID_NATURAL[1]
    btn_sx, btn_sy = SOLDIER_BUTTON_W / BUTTON_NATURAL[0], BUTTON_H / BUTTON_NATURAL[1]
    depth = 10
    stage = [
        f'import:PageHeader.swf:{CHAR_PAGEHEADER}:PageHeader',
        f'import:TextField.swf:{CHAR_TEXTFIELD}:TextField',
        f'import:Button.swf:{CHAR_BUTTON}:Button',
        f'import:KitInfoBox.swf:{CHAR_INFOBOX}:KitInfoBox',
        f'import:Grid.swf:{CHAR_GRID}:Grid',
        f'add:{ROOT_SPRITE}:PageHeader_01:{CHAR_PAGEHEADER}:{depth}:0:0:1:1',
        f'add:{ROOT_SPRITE}:TextField_01:{CHAR_TEXTFIELD}:{depth + 1}:96:96:1:1',
        'vars:TextField_01:m_useBorder=false|m_align=left|m_rowType=bold1',
        f'add:{ROOT_SPRITE}:Button_02:{CHAR_BUTTON}:{depth + 2}:545:97:2.0469:0.5',
        'vars:Button_02:' + BUTTON_VARS,
        f'add:{ROOT_SPRITE}:{INFO_BOX}:{CHAR_INFOBOX}:{depth + 3}:0:0:1:1',
        f'vars:{INFO_BOX}:DEBUG_TEST_IN_FLASH=false',
        f'add:{ROOT_SPRITE}:CamoGrid:{CHAR_GRID}:{depth + 4}:{PANEL_X}:{SOLDIER_GRID_Y}:{grid_sx:.4f}:{grid_sy:.4f}',
        'vars:CamoGrid:i_cellType=KitCell|i_selectedIndex=0.0|i_loopNavigation=false|DEBUG_TEST_IN_FLASH=false',
    ]
    nodes = [
        widget_node('PageHeader_01', 'ui/assets/pageheader', graph_y=40.0, fields={'DataBinding': {
            '$type': 'UIPageHeaderBinding',
            'Header': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'SubHeader': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'Icon': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'StaticHeader': SOLDIER_HEADER_PATH, 'StaticSubHeader': SOLDIER_HEADER_TITLE, 'StaticIcon': '', 'LevelSpecificHeaders': [],
        }}),
        # the kit's name, where the game's appearance screen shows it
        widget_node('TextField_01', 'ui/assets/textfield', graph_y=120.0, fields={'DataBinding': {
            '$type': 'UIDynamicDataBinding', 'Refresh': True,
            'Bindings': [{'DataName': 'Text', 'DataCategory': KIT_COMP, 'DataKey': KIT_NAME_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}}),
        widget_node('Button_02', 'ui/assets/button', graph_y=200.0,
                    props={'TextData': 'ID_M_BACK', 'IconData': '', 'Visible': '', 'Toggled': '', 'HideOnConsole': 'true'},
                    connections=[{'event': 'OnItemReleased', 'toNode': 'Confirm', 'toPort': 'In'}]),
        widget_node(INFO_BOX, 'ui/assets/kitinfobox', graph_y=280.0),
        # the grid: the only widget of focus group 0; its items are the game's appearance rows of the kit, the mailbox brings the
        # skins and what this player wears on each part. Its Released is the pick: our row into the game's selected appearance, then
        # out through SkinPicked (the flow graph stores it and glitches; the parts themselves travel by Lua)
        {**widget_node('CamoGrid', 'ui/assets/grid', focus=0, graph_y=360.0,
                       props={'p_cellType': 'KitCell', 'p_selectedIndex': '0', 'p_highlightInactive': '0', 'p_keyboardNavigation': '0',
                              'p_dynamicLoading': '0', 'p_itemPicker': '0', 'p_animTime': '0.5', 'p_itemPickerAlphaMin': '100', 'p_itemPickerAlphaMax': '100'},
                       fields={'DataBinding': {'$type': 'UIDynamicDataBinding', 'Refresh': True, 'Bindings': [
                           {'DataName': 'GridItems', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': SOLDIER_ITEMS_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
                           {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': SOLDIER_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}},
                       connections=[{'event': 'OnItemReleased', 'toNode': SOLDIER_ROW_SET_NODE, 'toPort': 'In'}])},
        # our row as the kit's selected appearance (see SOLDIER_SELECTED_KEY): the game's SetAppearance, with our row for Param
        logic_node(SOLDIER_ROW_SET_NODE, 'DataSetNode', graph_x=500.0, graph_y=360.0,
                   fields={'Param': str(signed32(identifier_of(SOLDIER_ROW_NAME))), 'SetToEmptyString': False, 'ForceUpdate': False,
                           'DataSource': {'DataName': '', 'DataCategory': CUSTOMIZATION_COMP,
                                          'DataKey': SOLDIER_SELECTED_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}},
                   connections=[{'fromPort': 'Out', 'toNode': SOLDIER_PICK_OUTPUT, 'toPort': 'In'}]),
        # the mailbox's writer (Param written at run time by CamoSoldierView.lua)
        logic_node(SOLDIER_TABLE_NODE, 'DataSetNode', graph_x=400.0, graph_y=40.0,
                   fields={'Param': soldier_table, 'SetToEmptyString': False, 'ForceUpdate': True,
                           'DataSource': {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP,
                                          'DataKey': SOLDIER_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}}),
        logic_node(SOLDIER_PICK_OUTPUT, 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=700.0, graph_y=360.0),
        logic_node('Confirm', 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=700.0, graph_y=200.0),
        logic_node('EnterScreen', 'InstanceInputNode', graph_x=40.0, graph_y=40.0,
                   connections=[{'fromPort': 'Out', 'toNode': SOLDIER_TABLE_NODE, 'toPort': 'In'}]),
    ]
    # the tabs (one row of four), then the families (two rows of four), all 109 px wide: the script sees them as ONE grid of
    # buttons, four columns -- the tabs its first row -- so the keyboard cursor walks tabs, families and cells as one panel
    for i, name in enumerate(SOLDIER_TABS):
        x = PANEL_X + i * (SOLDIER_BUTTON_W + BUTTON_GAP)
        stage.append(f'add:{ROOT_SPRITE}:{name}:{CHAR_BUTTON}:{depth + 5 + i}:{x}:{SOLDIER_TABS_Y}:{btn_sx:.4f}:{btn_sy:.4f}')
        stage.append(f'vars:{name}:' + BUTTON_VARS)
        # the property carries the game's appearance title (a text every language has); the script puts our word for the tab in
        nodes.append(widget_node(name, 'ui/assets/button', graph_y=460.0 + 60.0 * i,
                                 props={'TextData': SOLDIER_HEADER_TITLE, 'HideOnConsole': '', 'ButtonItem': '', 'IconData': '', 'Toggled': ''}))
    for i, (name, label) in enumerate(zip(buttons, names)):
        col, row = i % 4, i // 4
        x = PANEL_X + col * (SOLDIER_BUTTON_W + BUTTON_GAP)
        y = SOLDIER_FAMILIES_Y + row * (BUTTON_H + BUTTON_GAP)
        stage.append(f'add:{ROOT_SPRITE}:{name}:{CHAR_BUTTON}:{depth + 5 + len(SOLDIER_TABS) + i}:{x}:{y}:{btn_sx:.4f}:{btn_sy:.4f}')
        stage.append(f'vars:{name}:' + BUTTON_VARS)
        nodes.append(widget_node(name, 'ui/assets/button', graph_y=700.0 + 60.0 * i,
                                 props={'TextData': label, 'HideOnConsole': '', 'ButtonItem': '', 'IconData': '', 'Toggled': ''}))
    stage.append(f'script:camoscreen:{vars_}:{screen_b64}')
    return {
        'partition': SOLDIER_PARTITION, 'movie': SOLDIER_MOVIE, 'new': True, 'template': 'ui/flow/screen/emptyscreen',
        'title': SOLDIER_TITLE, 'stage': stage, 'nodes': nodes, 'fields': {}, 'removedConnections': [],
    }


def weapon_row_screen(spec, screen_b64):
    """A loadout weapon's camo window (see SIDEARMS: the pistol's, the crossbow's on either gadget row): the weapon camo screen's layout (header, the kit's name, BACK, the eight families, the
    grid, the info box) in the script's accessory mode on the SIDEARM row's list -- the pistol worn as "Sin camuflaje" and the camos
    made of it (_camoBaseOwn: that pistol alone even before any camo of it exists). A pick runs the game's own chain for that row:
    SetWeaponCategory '1' -> SetSecondaryWeapon -> the SetWeapon output (the flow graph fires the game's UpdateWeaponCustomization)."""
    names = [name for name, _ in FAMILIES]
    families = ';'.join(name + '~' + ','.join(keys) for name, keys in FAMILIES)
    buttons = ['CamoCat_%02d' % (i + 1) for i in range(len(FAMILIES))]
    vars_ = script_vars([
        ('_camoRoot', CAMO_ROOT), ('_camoButtons', ','.join(buttons)), ('_camoBase', '1'), ('_camoSlot', spec['slot']),
        ('_camoGrid', 'CamoGrid'), ('_camoInfo', INFO_BOX), ('_camoInfoAt', INFO_AT), ('_camoCell', f'{CELL_W},{CELL_GAP}'),
        ('_camoImageScale', str(IMAGE_SCALE)), ('_camoLabelShift', str(LABEL_SHIFT)),
        ('_camoHeader', 'PageHeader_01'), ('_camoPath', LOADOUT_HEADER_PATH), ('_camoTitle', HEADER_TITLE_ID), ('_camoTitles', HEADER_TITLES),
        ('_camoTable', ''), ('_camoFamilies', families), ('_camoDragLeft', str(DRAG_LEFT)),
        # the cell without a camo: the weapon's own "Sin camuflaje" (name, crossed-out picture), as on the weapon and accessory windows
        ('_camoDefaultLabel', DEFAULT_CAMO_LABEL), ('_camoDefaultDesc', DEFAULT_CAMO_DESC),
        ('_camoDefaultImage', DEFAULT_CAMO_IMAGE), ('_camoNoCamoIds', NO_CAMO_IDS),
        ('_camoNoModel', ''), ('_camoNoWindow', ''), ('_camoNoWindowPairs', ''),
    ] + soldier_vars(False) + sidearm_vars(True))
    grid_h = round(GRID_ROWS * CELL_H + (GRID_ROWS - 1) * CELL_GAP)
    grid_sx, grid_sy = GRID_WIDTH / GRID_NATURAL[0], grid_h / GRID_NATURAL[1]
    btn_sx, btn_sy = BUTTON_W / BUTTON_NATURAL[0], BUTTON_H / BUTTON_NATURAL[1]
    depth = 10
    stage = [
        f'import:PageHeader.swf:{CHAR_PAGEHEADER}:PageHeader',
        f'import:TextField.swf:{CHAR_TEXTFIELD}:TextField',
        f'import:Button.swf:{CHAR_BUTTON}:Button',
        f'import:KitInfoBox.swf:{CHAR_INFOBOX}:KitInfoBox',
        f'import:Grid.swf:{CHAR_GRID}:Grid',
        f'add:{ROOT_SPRITE}:PageHeader_01:{CHAR_PAGEHEADER}:{depth}:0:0:1:1',
        f'add:{ROOT_SPRITE}:TextField_01:{CHAR_TEXTFIELD}:{depth + 1}:96:96:1:1',
        'vars:TextField_01:m_useBorder=false|m_align=left|m_rowType=bold1',
        f'add:{ROOT_SPRITE}:Button_02:{CHAR_BUTTON}:{depth + 2}:545:97:2.0469:0.5',
        'vars:Button_02:' + BUTTON_VARS,
        f'add:{ROOT_SPRITE}:{INFO_BOX}:{CHAR_INFOBOX}:{depth + 3}:0:0:1:1',
        f'vars:{INFO_BOX}:DEBUG_TEST_IN_FLASH=false',
        f'add:{ROOT_SPRITE}:CamoGrid:{CHAR_GRID}:{depth + 4}:{PANEL_X}:{GRID_Y}:{grid_sx:.4f}:{grid_sy:.4f}',
        'vars:CamoGrid:i_cellType=KitCell|i_selectedIndex=0.0|i_loopNavigation=false|DEBUG_TEST_IN_FLASH=false',
    ]
    nodes = [
        widget_node('PageHeader_01', 'ui/assets/pageheader', graph_y=40.0, fields={'DataBinding': {
            '$type': 'UIPageHeaderBinding',
            'Header': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'SubHeader': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'Icon': {'DataName': '', 'DataCategory': None, 'DataKey': 0, 'UseDirectAccess': False, 'UpdateOnInitialize': True},
            'StaticHeader': LOADOUT_HEADER_PATH.split(',')[0], 'StaticSubHeader': HEADER_TITLE_ID, 'StaticIcon': '', 'LevelSpecificHeaders': [],
        }}),
        widget_node('TextField_01', 'ui/assets/textfield', graph_y=120.0, fields={'DataBinding': {
            '$type': 'UIDynamicDataBinding', 'Refresh': True,
            'Bindings': [{'DataName': 'Text', 'DataCategory': KIT_COMP, 'DataKey': KIT_NAME_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}}),
        widget_node('Button_02', 'ui/assets/button', graph_y=200.0,
                    props={'TextData': 'ID_M_BACK', 'IconData': '', 'Visible': '', 'Toggled': '', 'HideOnConsole': 'true'},
                    connections=[{'event': 'OnItemReleased', 'toNode': 'Confirm', 'toPort': 'In'}]),
        widget_node(INFO_BOX, 'ui/assets/kitinfobox', graph_y=280.0),
        # the grid: the only widget of focus group 0; its items are the SIDEARM row's own (the kit's pistols, and the camos of each
        # once they exist), the mailbox brings the table that says which camo is of which pistol
        {**widget_node('CamoGrid', 'ui/assets/grid', focus=0, graph_y=360.0,
                       props={'p_cellType': 'KitCell', 'p_selectedIndex': '0', 'p_highlightInactive': '0', 'p_keyboardNavigation': '0',
                              'p_dynamicLoading': '0', 'p_itemPicker': '0', 'p_animTime': '0.5', 'p_itemPickerAlphaMin': '100', 'p_itemPickerAlphaMax': '100'},
                       fields={'DataBinding': {'$type': 'UIDynamicDataBinding', 'Refresh': True, 'Bindings': [
                           {'DataName': 'GridItems', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': spec['items'], 'UseDirectAccess': False, 'UpdateOnInitialize': True},
                           {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': CAMO_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}},
                       connections=[{'event': 'OnItemReleased', 'toNode': 'SetWeaponCategory', 'toPort': 'In'}])},
        # the pick, node for node the loadout's own chain for its SIDEARM row: the category (Param '1'), then the sidearm (the value
        # that came in: the grid's Released carries the unlock's identifier), then out through SetWeapon
        logic_node('SetWeaponCategory', 'DataSetNode', graph_x=500.0, graph_y=360.0,
                   fields={'Param': spec['category'], 'SetToEmptyString': False, 'ForceUpdate': False,
                           'DataSource': {'DataName': '', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': WEAPON_CATEGORY_KEY,
                                          'UseDirectAccess': False, 'UpdateOnInitialize': True}},
                   connections=[{'fromPort': 'Out', 'toNode': spec['set_node'], 'toPort': 'In'}]),
        logic_node(spec['set_node'], 'DataSetNode', graph_x=700.0, graph_y=360.0,
                   fields={'Param': '', 'SetToEmptyString': False, 'ForceUpdate': False,
                           'DataSource': {'DataName': '', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': spec['set'],
                                          'UseDirectAccess': False, 'UpdateOnInitialize': True}},
                   connections=[{'fromPort': 'Out', 'toNode': WEAPON_PICK_OUTPUT, 'toPort': 'In'}]),
        # the mailbox's writer (the mod fills its Param at run time, as on the other camo windows)
        logic_node(CAMO_TABLE_NODE, 'DataSetNode', graph_x=400.0, graph_y=40.0,
                   fields={'Param': '', 'SetToEmptyString': False, 'ForceUpdate': True,
                           'DataSource': {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP,
                                          'DataKey': CAMO_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}}),
        logic_node(WEAPON_PICK_OUTPUT, 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=1000.0, graph_y=360.0),
        logic_node('Confirm', 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=700.0, graph_y=200.0),
        logic_node('EnterScreen', 'InstanceInputNode', graph_x=40.0, graph_y=40.0,
                   connections=[{'fromPort': 'Out', 'toNode': CAMO_TABLE_NODE, 'toPort': 'In'}]),
    ]
    for i, (name, label) in enumerate(zip(buttons, names)):
        col, row = i % 2, i // 2
        x = PANEL_X + col * (BUTTON_W + BUTTON_GAP)
        y = BUTTONS_Y + row * (BUTTON_H + BUTTON_GAP)
        stage.append(f'add:{ROOT_SPRITE}:{name}:{CHAR_BUTTON}:{depth + 5 + i}:{x}:{y}:{btn_sx:.4f}:{btn_sy:.4f}')
        stage.append(f'vars:{name}:' + BUTTON_VARS)
        nodes.append(widget_node(name, 'ui/assets/button', graph_y=460.0 + 60.0 * i,
                                 props={'TextData': label, 'HideOnConsole': '', 'ButtonItem': '', 'IconData': '', 'Toggled': ''}))
    stage.append(f'script:camoscreen:{vars_}:{screen_b64}')
    return {
        'partition': spec['partition'], 'movie': 'ui/assets/' + spec['partition'].split('/')[-1], 'new': True,
        'template': 'ui/flow/screen/emptyscreen', 'title': spec['state'], 'stage': stage, 'nodes': nodes, 'fields': {}, 'removedConnections': [],
    }


def flow_graph():
    # One store node per DOOR (an output port drives one wire, so they cannot be shared): the way IN to each of our
    # four screens, and the way BACK out of each of them.
    # ⛔⛔ THE TWO GROUPS CANNOT SIT TOGETHER — the live edit wires as it goes, so each node has to be declared before
    # whoever wires TO it and after whoever it wires to. That pins the order exactly:
    #     BACK stores (they wire to the game's own accessories screen, which is always there)
    #       -> our StateNodes (their Confirm/Deactivate wire INTO those back stores)
    #         -> ENTER stores (they wire INTO our StateNodes)
    #           -> the accessories screen (its doors wire INTO those enter stores)
    # Both orders are wrong in one direction or the other, which is why "put them first" and "put them last" each
    # dropped four wires. The guard at the end of this file refuses to emit either.
    back_stores = [('CamoBackStore', ACC_STATE)] + \
                  [(state + 'BackStore', ACC_STATE) for _r, _sl, _p, _t, state, _o, _n in ACCESSORY_ROWS]
    enter_stores = [('CamoEnterStore', CAMO_STATE)] + \
                   [(out + 'Store', state) for _r, _sl, _p, _t, state, out, _n in ACCESSORY_ROWS]

    def store_nodes(p_Stores, p_Y):
        return [logic_node(name, 'ActionNode', graph_x=500.0, graph_y=p_Y + 60.0 * i,
                           fields={'ActionKey': STORE_ACTION_KEY, 'Params': STORE_PARAMS,
                                   'AppendIncomingParams': False, 'ActionAsset': None},
                           connections=[{'fromPort': 'Out', 'toNode': target,
                                         'toPort': 'Inputs:EnterScreen', 'pop': 0}])
                for i, (name, target) in enumerate(p_Stores)]

    # each door of the accessories screen goes into its store, and the store opens the screen
    door_wires = [{'fromPort': 'Outputs:' + out, 'toNode': out + 'Store' if out != 'CamoButton' else 'CamoEnterStore',
                   'toPort': 'In', 'pop': 1}
                  for out in ['CamoButton'] + [o for _r, _sl, _p, _t, _s, o, _n in ACCESSORY_ROWS]]

    # ---- the vehicle windows (see VEHICLE_KINDS): the same doors the game gives PERSONAL., node for node --
    #   CustomizeLand|AirScreen.<door> --pop 1--> SpawnVehicle (ours, no params) -> <window>.EnterScreen
    #   <window>.HideVehicle --pop 0--> UnspawnVehicle (ours: the game's vehicle away while the view shows its copy)
    #   <window>.Confirm / Deactivate --pop 1--> StoreVehicleAccessories (ours) -> SpawnVehicle (ours: the game's vehicle back)
    #                                            -> CustomizeLand|AirScreen.EnterScreen
    # The groups go where the weapon's go, for the same reason (the live edit wires as it goes): a node before whoever wires to it.
    def vehicle_action(name, key, target, y, port='Inputs:EnterScreen', params=None):
        return logic_node(name, 'ActionNode', graph_x=500.0, graph_y=y,
                          fields={'ActionKey': key, 'Params': list(params or []), 'AppendIncomingParams': False, 'ActionAsset': None},
                          connections=[] if target is None else [{'fromPort': 'Out', 'toNode': target, 'toPort': port, 'pop': 0}])

    vehicle_back = []
    for i, (_k, _p, _m, rows_state, _cp, _t, camo_state, _d, _h) in enumerate(VEHICLE_KINDS):
        vehicle_back.append(vehicle_action(camo_state + 'BackSpawn', SPAWN_VEHICLE_ACTION, rows_state, 1700.0 + 180.0 * i))
        vehicle_back.append(vehicle_action(camo_state + 'BackStore', STORE_VEHICLE_ACTION, camo_state + 'BackSpawn', 1760.0 + 180.0 * i, 'In'))
        vehicle_back.append(vehicle_action(camo_state + 'Hide', UNSPAWN_VEHICLE_ACTION, None, 1820.0 + 180.0 * i))
        # a pick (see A PICK): the glitch and the ShowRoom's vehicle taken away; the window's RespawnVehicle, a moment later, spawns
        # it again -- made with the mesh the client's Lua has just pointed its class at. Declared target first: the live edit wires
        # as it goes.
        vehicle_back.append(vehicle_action(camo_state + 'PickSpawn', SPAWN_VEHICLE_ACTION, None, 1940.0 + 180.0 * i))
        vehicle_back.append(vehicle_action(camo_state + 'PickUnspawn', UNSPAWN_VEHICLE_ACTION, None, 1910.0 + 180.0 * i))
        vehicle_back.append(vehicle_action(camo_state + 'PickGlitch', POSTPROCESS_ACTION, camo_state + 'PickUnspawn',
                                           1880.0 + 180.0 * i, 'In', params=['0.2']))
    vehicle_states = [
        logic_node(camo_state, 'StateNode', graph_x=700.0, graph_y=1800.0 + 120.0 * i,
                   fields={'Screen': camo_partition, 'RenderToTexture': False},
                   ports=[{'field': 'Inputs', 'name': 'EnterScreen', 'event': None, 'inputEvent': None},
                          {'field': 'Outputs', 'name': 'Initialized [Screen]', 'instanceName': 'Initialized', 'event': None, 'inputEvent': None},
                          {'field': 'Outputs', 'name': 'HideVehicle', 'event': None, 'inputEvent': None},
                          {'field': 'Outputs', 'name': VEHICLE_PICK_OUTPUT, 'event': None, 'inputEvent': None},
                          {'field': 'Outputs', 'name': VEHICLE_RESPAWN_OUTPUT, 'event': None, 'inputEvent': None},
                          {'field': 'Outputs', 'name': 'Confirm', 'event': None, 'inputEvent': None},
                          {'field': 'Outputs', 'name': 'Deactivate', 'event': None, 'inputEvent': 'Deactivate'}],
                   connections=[{'fromPort': 'Outputs:Initialized [Screen]', 'toNode': 'CamoPostProcess', 'toPort': 'In'},
                                {'fromPort': 'Outputs:HideVehicle', 'toNode': camo_state + 'Hide', 'toPort': 'In', 'pop': 0},
                                {'fromPort': 'Outputs:' + VEHICLE_PICK_OUTPUT, 'toNode': camo_state + 'PickGlitch', 'toPort': 'In', 'pop': 0},
                                {'fromPort': 'Outputs:' + VEHICLE_RESPAWN_OUTPUT, 'toNode': camo_state + 'PickSpawn', 'toPort': 'In', 'pop': 0},
                                {'fromPort': 'Outputs:Confirm', 'toNode': camo_state + 'BackStore', 'toPort': 'In', 'pop': 1},
                                {'fromPort': 'Outputs:Deactivate', 'toNode': camo_state + 'BackStore', 'toPort': 'In', 'pop': 1}])
        for i, (_k, _p, _m, _rs, camo_partition, _t, camo_state, _d, _h) in enumerate(VEHICLE_KINDS)]
    vehicle_enter = [vehicle_action(door + 'Spawn', SPAWN_VEHICLE_ACTION, camo_state, 2100.0 + 60.0 * i)
                     for i, (_k, _p, _m, _rs, _cp, _t, camo_state, door, _h) in enumerate(VEHICLE_KINDS)]
    vehicle_rows = [
        logic_node(rows_state, 'StateNode', new=False, graph_x=300.0, graph_y=1800.0 + 120.0 * i,
                   ports=[{'field': 'Outputs', 'name': door, 'event': None, 'inputEvent': None}],
                   connections=[{'fromPort': 'Outputs:' + door, 'toNode': door + 'Spawn', 'toPort': 'In', 'pop': 1}])
        for i, (_k, _p, _m, rows_state, _cp, _t, _cs, door, _h) in enumerate(VEHICLE_KINDS)]

    # ---- the soldier's window (see SOLDIERS), in place of the game's APARIENCIA, the same doors the game gives it:
    #   CustomizeSoldierScreen.SkinButton --pop 1--> SkinEnterLoadout (UpdateSoldierLoadout ['-1'], as the game's) -> our window
    #   <window>.SkinPicked --> SkinRowStore (the game's StoreSoldierAppearance: our row into the profile) --> SkinPickGlitch (the
    #   glitch on a pick, as the weapon windows'; the parts themselves travel by Lua)
    #   <window>.Confirm / Deactivate --pop 1--> SkinBackGlitch -> CustomizeSoldierScreen.EnterScreen (the game's way back from
    #   APARIENCIA stores the GAME's appearance, which this window never changes: nothing to store)
    # Declared target first (the live edit wires as it goes). SkinButton is a port NEW on the game's StateNode (as TIERRA's LandCamoButton).
    soldier_nodes = [
        logic_node('SkinBackGlitch', 'ActionNode', graph_x=500.0, graph_y=2400.0,
                   fields={'ActionKey': POSTPROCESS_ACTION, 'Params': ['0.2'], 'AppendIncomingParams': False, 'ActionAsset': None},
                   connections=[{'fromPort': 'Out', 'toNode': KITS_STATE, 'toPort': 'Inputs:EnterScreen', 'pop': 0}]),
        logic_node('SkinPickGlitch', 'ActionNode', graph_x=500.0, graph_y=2460.0,
                   fields={'ActionKey': POSTPROCESS_ACTION, 'Params': ['0.2'], 'AppendIncomingParams': False, 'ActionAsset': None}),
        # the pick stored as the game's APARIENCIA stores it on BACK (the window has just set our row as the selected appearance):
        # the client's profile holds our row for the kit, and the deploy sends this player's slot (see SOLDIER_SELECTED_KEY)
        logic_node('SkinRowStore', 'ActionNode', graph_x=400.0, graph_y=2460.0,
                   fields={'ActionKey': STORE_APPEARANCE_ACTION, 'Params': ['1'], 'AppendIncomingParams': False, 'ActionAsset': None},
                   connections=[{'fromPort': 'Out', 'toNode': 'SkinPickGlitch', 'toPort': 'In', 'pop': 0}]),
        logic_node(SOLDIER_STATE, 'StateNode', graph_x=700.0, graph_y=2400.0,
                   fields={'Screen': SOLDIER_PARTITION, 'RenderToTexture': False},
                   ports=[{'field': 'Inputs', 'name': 'EnterScreen', 'event': None, 'inputEvent': None},
                          {'field': 'Outputs', 'name': 'Initialized [Screen]', 'instanceName': 'Initialized', 'event': None, 'inputEvent': None},
                          {'field': 'Outputs', 'name': SOLDIER_PICK_OUTPUT, 'event': None, 'inputEvent': None},
                          {'field': 'Outputs', 'name': 'Confirm', 'event': None, 'inputEvent': None},
                          {'field': 'Outputs', 'name': 'Deactivate', 'event': None, 'inputEvent': 'Deactivate'}],
                   connections=[{'fromPort': 'Outputs:Initialized [Screen]', 'toNode': 'CamoPostProcess', 'toPort': 'In'},
                                {'fromPort': 'Outputs:' + SOLDIER_PICK_OUTPUT, 'toNode': 'SkinRowStore', 'toPort': 'In', 'pop': 0},
                                {'fromPort': 'Outputs:Confirm', 'toNode': 'SkinBackGlitch', 'toPort': 'In', 'pop': 1},
                                {'fromPort': 'Outputs:Deactivate', 'toNode': 'SkinBackGlitch', 'toPort': 'In', 'pop': 1}]),
        logic_node('SkinEnterLoadout', 'ActionNode', graph_x=500.0, graph_y=2340.0,
                   fields={'ActionKey': UPDATE_LOADOUT_ACTION, 'Params': ['-1'], 'AppendIncomingParams': False, 'ActionAsset': None},
                   connections=[{'fromPort': 'Out', 'toNode': SOLDIER_STATE, 'toPort': 'Inputs:EnterScreen', 'pop': 0}]),
        logic_node(KITS_STATE, 'StateNode', new=False, graph_x=300.0, graph_y=2340.0,
                   ports=[{'field': 'Outputs', 'name': KITS_DOOR, 'event': None, 'inputEvent': None}],
                   connections=[{'fromPort': 'Outputs:' + KITS_DOOR, 'toNode': 'SkinEnterLoadout', 'toPort': 'In', 'pop': 1}]),
    ]

    # ---- the loadout weapons' camo windows (see SIDEARMS: the pistol's, the crossbow's on each gadget row), the doors the game gives
    # the loadout's accessories screen, per row (<p> = its door's name without "Button": Sidearm, Gadget1, Gadget2):
    #   CustomizeSoldierSubScreen.<p>Button --pop 1--> its window (as AccessoriesButton --pop 1--> the accessories screen)
    #   <window>.SetWeapon --> <p>PickUpdate (the game's UpdateWeaponCustomization ['0','1'], what the loadout's SetWeapon fires)
    #   --> <p>PickGlitch (the glitch on a pick, as the weapon windows')
    #   <window>.Confirm / Deactivate --pop 1--> <p>BackGlitch -> CustomizeSoldierSubScreen.EnterScreen (the loadout's own BACK
    #   stores, as it does for a weapon chosen with the arrows)
    # Declared target first (the live edit wires as it goes): every row's actions, then the windows, then the game's StateNode with
    # the NEW ports, one per row.
    def row_prefix(p_Spec):
        return p_Spec['door'][:-len('Button')]

    sidearm_nodes = []
    for i, spec in enumerate(WEAPON_ROWS):
        p, y = row_prefix(spec), 2600.0 + 180.0 * i
        sidearm_nodes += [
            logic_node(p + 'BackGlitch', 'ActionNode', graph_x=500.0, graph_y=y,
                       fields={'ActionKey': POSTPROCESS_ACTION, 'Params': ['0.2'], 'AppendIncomingParams': False, 'ActionAsset': None},
                       connections=[{'fromPort': 'Out', 'toNode': LOADOUT_STATE, 'toPort': 'Inputs:EnterScreen', 'pop': 0}]),
            logic_node(p + 'PickGlitch', 'ActionNode', graph_x=600.0, graph_y=y + 60.0,
                       fields={'ActionKey': POSTPROCESS_ACTION, 'Params': ['0.2'], 'AppendIncomingParams': False, 'ActionAsset': None}),
            logic_node(p + 'PickUpdate', 'ActionNode', graph_x=400.0, graph_y=y + 60.0,
                       fields={'ActionKey': UPDATE_WEAPON_ACTION, 'Params': ['0', '1'], 'AppendIncomingParams': False, 'ActionAsset': None},
                       connections=[{'fromPort': 'Out', 'toNode': p + 'PickGlitch', 'toPort': 'In', 'pop': 0}]),
        ]
    for i, spec in enumerate(WEAPON_ROWS):
        p = row_prefix(spec)
        sidearm_nodes.append(
            logic_node(spec['state'], 'StateNode', graph_x=700.0, graph_y=2600.0 + 180.0 * i,
                       fields={'Screen': spec['partition'], 'RenderToTexture': False},
                       ports=[{'field': 'Inputs', 'name': 'EnterScreen', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'Initialized [Screen]', 'instanceName': 'Initialized', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': WEAPON_PICK_OUTPUT, 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'Confirm', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'Deactivate', 'event': None, 'inputEvent': 'Deactivate'}],
                       connections=[{'fromPort': 'Outputs:Initialized [Screen]', 'toNode': 'CamoPostProcess', 'toPort': 'In'},
                                    {'fromPort': 'Outputs:' + WEAPON_PICK_OUTPUT, 'toNode': p + 'PickUpdate', 'toPort': 'In', 'pop': 0},
                                    {'fromPort': 'Outputs:Confirm', 'toNode': p + 'BackGlitch', 'toPort': 'In', 'pop': 1},
                                    {'fromPort': 'Outputs:Deactivate', 'toNode': p + 'BackGlitch', 'toPort': 'In', 'pop': 1}]))
    sidearm_nodes.append(
        logic_node(LOADOUT_STATE, 'StateNode', new=False, graph_x=300.0, graph_y=2600.0,
                   ports=[{'field': 'Outputs', 'name': spec['door'], 'event': None, 'inputEvent': None} for spec in WEAPON_ROWS],
                   connections=[{'fromPort': 'Outputs:' + spec['door'], 'toNode': spec['state'], 'toPort': 'Inputs:EnterScreen', 'pop': 1}
                                for spec in WEAPON_ROWS]))

    # edited LIVE (delivery "lua"): a static copy of the flow graph would drag copies of everything it imports — every customization
    # screen, its audio mapping (the UI sounds), the post-process effect (the glitch between screens) — whose imports outside ui/ no
    # longer bind in the bundle (measured 2026-09-17: v6 shipped it static and the customization screens lost their sounds and the glitch).
    # The nodes the wires lead to come first: the live edit wires as it goes.
    return {
        'partition': FLOW_PARTITION, 'movie': None, 'new': False, 'template': 'ui/flow/screen/emptyscreen', 'title': None, 'delivery': 'lua',
        'stage': [],
        'nodes': [
            # the glitch on entering the camo screen: the same engine action every customization screen's Initialized output fires (a node
            # of our own, not one of the graph's three PostProcessEffect nodes of that name)
            logic_node('CamoPostProcess', 'ActionNode', graph_x=1000.0, graph_y=700.0,
                       fields={'ActionKey': POSTPROCESS_ACTION, 'Params': ['0.2'], 'AppendIncomingParams': False, 'ActionAsset': None}),
        ] + [
            # ⭐⭐ EVERY DOOR OF OURS STORES ON ITS WAY OUT — WHICH IS WHAT THE GAME'S OWN BACK BUTTON DOES (keku,
            # 2026-09-21: *"al entrar en el modo camuflaje, si has hecho cualquier cosa antes en la pestana de
            # accesorios no se guarda... mi pista mas fuerte es ver que guarda al darle a ATRAS"*). His hint was right,
            # and the game's graph answers it exactly:
            #     CustomizeAccessoriesScreen.Confirm -> SaveAccessoriesEnabled
            #     SaveAccessoriesEnabled.True  --pop=1-->  StoreWeaponAccessories (1375026578, ['1'])
            #     StoreWeaponAccessories       --pop=0-->  CustomizeSoldierSubScreen.EnterScreen
            # ⇒ the arrows only change what is SHOWN (AccessoryChanged fires UpdateWeaponAccessory with ['0'], which
            # stores nothing); the loadout is written when the screen is LEFT. Our doors popped that screen without ever
            # passing through it, so everything the player did with the arrows died with it — and the same for the camo
            # screen's own way back, which is why re-entering it showed the old state.
            # ⛔⛔⛔ AND THE THREE BLANK SCREENS IT COST (09-18, and twice on 09-21) WERE NEVER ABOUT THE STORE: they were
            # the ORDER of this list. The live edit writes each node's wires as it adds that node, so a wire to a node
            # declared later is DROPPED ("target node not found -- SKIPPED") and the door leads nowhere. Measured from
            # his own log the moment the graph report reached disk, with a harmless post-process node in the door
            # behaving exactly the same as the store. Hence the order these groups are emitted in, and the guard.
        ] + store_nodes(back_stores, 1200.0) + vehicle_back + [
        ] + [
            # the glitch on every pick (keku): the engine follows ONE wire per output port (measured on the game's 174 graphs: of 3656
            # wires, the 12 that share a source port are duplicates), so the pick CHAINS through this node into the store action, the way
            # the game chains StoreWeaponCustomization → PostProcessEffect → EnterScreen; a node of its own so that Initialized's does not
            # store on entering
            logic_node('CamoPickPostProcess', 'ActionNode', graph_x=1000.0, graph_y=780.0,
                       fields={'ActionKey': POSTPROCESS_ACTION, 'Params': ['0.2'], 'AppendIncomingParams': False, 'ActionAsset': None},
                       connections=[{'fromPort': 'Out', 'toNode': STORE_ACTION, 'toPort': 'In'}]),
            # the camo screen's StateNode: entered through EnterScreen; Initialized → the glitch; its pick → the glitch → stored by the same
            # action the accessories screen's is; BACK (Confirm) and Esc (Deactivate) back to the accessories screen, the camo screen popped
            # first
            logic_node(CAMO_STATE, 'StateNode', graph_x=700.0, graph_y=600.0,
                       fields={'Screen': CAMO_PARTITION, 'RenderToTexture': False},
                       ports=[{'field': 'Inputs', 'name': 'EnterScreen', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'Initialized [Screen]', 'instanceName': 'Initialized', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'AccessoryChanged', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'Confirm', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'Deactivate', 'event': None, 'inputEvent': 'Deactivate'}],
                       connections=[{'fromPort': 'Outputs:Initialized [Screen]', 'toNode': 'CamoPostProcess', 'toPort': 'In'},
                                    {'fromPort': 'Outputs:AccessoryChanged', 'toNode': 'CamoPickPostProcess', 'toPort': 'In'},
                                    # BACK and Esc: store first (the pop rides this wire), then the accessories screen
                                    {'fromPort': 'Outputs:Confirm', 'toNode': 'CamoBackStore', 'toPort': 'In', 'pop': 1},
                                    {'fromPort': 'Outputs:Deactivate', 'toNode': 'CamoBackStore', 'toPort': 'In', 'pop': 1}]),
        ] + [
            # ⛔ REVERTED 2026-09-18 (keku: "ya no puedo ni entrar a la ventana de camo de los accesorios ni nada"): putting
            # the game's store action IN THE PATH of a door broke the door itself — the screens stopped opening. The store
            # (ActionKey 1375026578 with "1") is what the accessories screen fires on its way out, and it does not behave as
            # a pass-through node in the middle of a screen change. The doors go straight through again; the choice is kept
            # by the scripts (the row confirms on the way in, the screen publishes its pick), and a store of our own has to
            # be found somewhere that is NOT the wire that opens a screen.
        ] + [
            # one StateNode per accessory screen, wired like the camo one — but BACK and Esc go through the store first, so
            # what the player did (with the arrows before entering, or on our screen) is in the loadout when the row is rebuilt
            logic_node(state, 'StateNode', graph_x=700.0, graph_y=900.0 + 120.0 * i,
                       fields={'Screen': partition, 'RenderToTexture': False},
                       ports=[{'field': 'Inputs', 'name': 'EnterScreen', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'Initialized [Screen]', 'instanceName': 'Initialized', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'AccessoryChanged', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'Confirm', 'event': None, 'inputEvent': None},
                              {'field': 'Outputs', 'name': 'Deactivate', 'event': None, 'inputEvent': 'Deactivate'}],
                       connections=[{'fromPort': 'Outputs:Initialized [Screen]', 'toNode': 'CamoPostProcess', 'toPort': 'In'},
                                    {'fromPort': 'Outputs:AccessoryChanged', 'toNode': 'CamoPickPostProcess', 'toPort': 'In'},
                                    {'fromPort': 'Outputs:Confirm', 'toNode': state + 'BackStore', 'toPort': 'In', 'pop': 1},
                                    {'fromPort': 'Outputs:Deactivate', 'toNode': state + 'BackStore', 'toPort': 'In', 'pop': 1}])
            for i, (_row, slot, partition, _title, state, _out, _name) in enumerate(ACCESSORY_ROWS)
        ] + vehicle_states + store_nodes(enter_stores, 1400.0) + vehicle_enter + [
            # the accessories screen's doors: each one INTO its store, which opens the screen. The stores are declared
            # just above for that reason -- a wire is written when its own node is added, so the target has to exist.
            logic_node(ACC_STATE, 'StateNode', new=False, graph_x=300.0, graph_y=600.0,
                       ports=[{'field': 'Outputs', 'name': 'CamoButton', 'event': None, 'inputEvent': None}] +
                             [{'field': 'Outputs', 'name': out, 'event': None, 'inputEvent': None}
                              for _row, _slot, _p, _t, _s, out, _n in ACCESSORY_ROWS],
                       # ⛔ 2026-09-18: opening them ON TOP (no pop) -- the game's own pattern in 19 of its 26
                       # wires -- hung the client on "creating level". The screen replaces the accessories one.
                       connections=door_wires),
        ] + vehicle_rows + soldier_nodes + sidearm_nodes,
        'fields': {}, 'removedConnections': [],
    }


def kits_screen():
    """The KITS screen (the game's customizesoldierscreen): APARIENCIA's two triggers -- the row's second button and the console bar's
    Edit -- out of the game's ChangeAppearance output and into ours, SkinButton (see KITS_DOOR). Shipped static, like TIERRA / AIRE."""
    nodes = [
        # the target first, then who wires to it
        logic_node(KITS_DOOR, 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=1000.0, graph_y=500.0),
        {**logic_node('KitView_01', 'WidgetNode', new=False, graph_x=300.0, graph_y=500.0),
         'connections': [{'event': 'OnChanged', 'toNode': KITS_DOOR, 'toPort': 'In'}]},
        {**logic_node('ConsoleButtonBar_01', 'WidgetNode', new=False, graph_x=300.0, graph_y=560.0),
         'connections': [{'event': 'SetIndex', 'toNode': KITS_DOOR, 'toPort': 'In'}]},
    ]
    return {
        'partition': KITS_PARTITION, 'movie': None, 'new': False, 'template': 'ui/flow/screen/emptyscreen', 'title': None,
        'stage': [], 'nodes': nodes, 'fields': {},
        'removedConnections': [{'guid': g, 'from': f, 'to': t} for g, f, t in KITS_OLD_WIRES],
    }


def loadout_screen(row_b64, mail_param=''):
    """The LOADOUT screen (the game's customizesoldiersubscreen), shipped static like the KITS screen: each weapon row's free
    Button1Released (SIDEARM, GADGET1, GADGET2: see WEAPON_ROWS) -> SetWeaponCategory with the row's category (ours, as the weapon
    row's own door sets '0' before AccessoriesButton) -> an output of ours, <p>Button, which the flow graph takes to that row's
    camo window. The rows' script makes a click on the row's box fire that event on its release (the accessory rows' door), keeps
    the rows' arrows, and opens a gadget row only on the crossbow (_camoAccOnly); nothing of the game is removed."""
    families = ';'.join(name + '~' + ','.join(keys) for name, keys in FAMILIES)
    # the unlocks each row opens on, rows apart by ';' and ids by ',' ('' = any): what the gadget rows are allowed to open on
    only = ';'.join(','.join(str(identifier_of(n)) for n in spec['only']) for spec in WEAPON_ROWS)
    vars_ = script_vars([('_camoRoot', LOADOUT_ROOT), ('_camoRow', ''),
                         ('_camoAccRows', ','.join(spec['row'] for spec in WEAPON_ROWS)),
                         ('_camoAccSlots', ','.join(spec['slot'] for spec in WEAPON_ROWS)), ('_camoAccOnly', only),
                         ('_camoLoadoutRows', ''), ('_camoTable', ''), ('_camoFamilies', families),
                         ('_camoNoWindow', ''), ('_camoNoWindowPairs', ''), ('_camoMail', CAMO_MAIL_CLIP),
                         ('_camoTableKey', str(CAMO_TABLE_KEY))])
    # the mailbox first (the rows' wires below never reach it): our own clip, its binding, its writer, and the screen's OWN
    # EnterScreen (an InstanceInputNode whose port is `Out`, read from the shipped partition) as the trigger -- a wire added to a
    # port the game already fires, nothing replaced. The rows' script folds a twin into its pistol only once the table is in.
    nodes = [
        logic_node(CAMO_TABLE_NODE, 'DataSetNode', graph_x=400.0, graph_y=800.0,
                   fields={'Param': mail_param, 'SetToEmptyString': False, 'ForceUpdate': True,
                           'DataSource': {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP,
                                          'DataKey': CAMO_TABLE_KEY, 'UseDirectAccess': False,
                                          'UpdateOnInitialize': True}}),
        # ⛔ and the mailbox can ASK for it (keku's runs 349/350: on the loadout's first entry the writer never fired, though the
        # framework had written its Param): the rows' script fires this clip's OnItemReleased when it folds with no table
        # (CamoRow.as camoPullTable, "MPF1"), and that fires the writer again
        widget_node(CAMO_MAIL_CLIP, 'ui/assets/textfield', graph_y=800.0,
                    fields={'DataBinding': {'$type': 'UIDynamicDataBinding', 'Refresh': True, 'Bindings': [
                        {'DataName': CAMO_TABLE_SOURCE, 'DataCategory': CUSTOMIZATION_COMP,
                         'DataKey': CAMO_TABLE_KEY, 'UseDirectAccess': False, 'UpdateOnInitialize': True}]}},
                    connections=[{'event': 'OnItemReleased', 'toNode': CAMO_TABLE_NODE, 'toPort': 'In'}]),
        logic_node('EnterScreen', 'InstanceInputNode', new=False, graph_x=40.0, graph_y=800.0,
                   connections=[{'fromPort': 'Out', 'toNode': CAMO_TABLE_NODE, 'toPort': 'In'}]),
    ]
    for i, spec in enumerate(WEAPON_ROWS):
        door_set = spec['door'][:-len('Button')] + 'Category'
        y = 500.0 + 60.0 * i
        nodes += [
            # the targets first, then who wires to them
            logic_node(spec['door'], 'InstanceOutputNode', fields={'DestroyGraph': True}, graph_x=1000.0, graph_y=y),
            logic_node(door_set, 'DataSetNode', graph_x=700.0, graph_y=y,
                       fields={'Param': spec['category'], 'SetToEmptyString': False, 'ForceUpdate': False,
                               'DataSource': {'DataName': '', 'DataCategory': CUSTOMIZATION_COMP, 'DataKey': WEAPON_CATEGORY_KEY,
                                              'UseDirectAccess': False, 'UpdateOnInitialize': True}},
                       connections=[{'fromPort': 'Out', 'toNode': spec['door'], 'toPort': 'In'}]),
            {**logic_node(spec['row'], 'WidgetNode', new=False, graph_x=300.0, graph_y=y),
             'connections': [{'event': 'OnItemReleased', 'toNode': door_set, 'toPort': 'In'}]},
        ]
    return {
        'partition': LOADOUT_PARTITION, 'movie': LOADOUT_MOVIE, 'new': False, 'template': 'ui/flow/screen/emptyscreen', 'title': None,
        'stage': [f'add:{LOADOUT_ROOT_SPRITE}:{CAMO_MAIL_CLIP}:{LOADOUT_CHAR_TEXTFIELD}:{LOADOUT_MAIL_DEPTH}:'
                  f'{ACC_MAIL_AT[0]}:{ACC_MAIL_AT[1]}:1:1',
                  f'script:camorow:{vars_}:{row_b64}'],
        'nodes': nodes, 'fields': {}, 'removedConnections': [],
    }


def check_live_wiring(screen):
    """A LIVE-edited screen wires AS IT GOES: refuse to emit a wire whose target will not exist yet.

    ⛔ THIS IS THE GUARD FOR THE BUG THAT COST keku THREE BOOTS (2026-09-18 and twice on 09-21, all three read as
    "the screen comes up blank"). The client creates the document's nodes in order and writes each node's
    connections the moment it adds that node, so a wire to a node declared LATER is dropped with
    "target node not found -- SKIPPED" — the door fires and nothing opens. It is invisible in the document, in
    the build log and in the game: only the client's own report says it, and until today that report went to a
    console nobody can read. So the check belongs HERE, where it costs nothing and fails before shipping.
    """
    if screen.get('delivery') != 'lua':
        return []

    seen = set()
    bad = []

    for node in screen['nodes']:
        name = node['instanceName']
        for wire in node.get('connections', []):
            target = wire['toNode']
            # a node of the GAME'S OWN (not declared new in this document) is already there: those are fine
            declared = next((n for n in screen['nodes'] if n['instanceName'] == target), None)
            if declared is not None and declared.get('new') and target not in seen:
                bad.append(f"{name}.{wire['fromPort']} -> {target}: declared later, the live edit will DROP this wire")
        seen.add(name)

    return bad


def document(row_b64, screen_b64, table, vehicle_row_b64):
    return {
        'name': 'CustomizeCamoMenu',
        'description': 'A camo screen for the weapon customization: the camo row of the accessories screen shows the camo worn and opens, on a click or Activate, a screen of its own with eight family buttons, a grid of thumbnails and the description; a thumbnail picks the camo, BACK or Esc return. The names, thumbnails and descriptions of camos the game does not know come from a table the script applies.',
        'version': '2.0.0',
        'authors': ['keku'],
        'superbundle': 'customizecamomenu/ui',
        'bundle': 'customizecamomenu/uibundle',
        'graphDelivery': 'static',
        # ⭐ ONE MOD TO LOAD (keku, 2026-09-20: *"fusionamos UI dentro del framework"*, and before that *"usar
        # weaponcamoframework como mod base, ofrece toda la UI nueva que hemos hecho"*). The build writes into the
        # framework's folder instead of a mod of its own: its Lua goes to ext/Client/CustomizeCamoMenuClient.lua and
        # ext/Shared/CustomizeCamoMenuShared.lua (the framework's own __init__.lua files require them and are never
        # written), and mod.json is merged, so the studio can keep declaring its camo packages in the same file.
        'mergeInto': 'CamoFramework',
        'screens': [accessories_screen(row_b64, table), camo_screen(screen_b64, table)] +
                   [accessory_screen(spec, screen_b64, table) for spec in ACCESSORY_ROWS] +
                   # the vehicles: TIERRA / AIRE (the game's, a second button on each row) and a camo window each
                   [vehicle_rows_screen(spec, vehicle_row_b64) for spec in VEHICLE_KINDS] +
                   [vehicle_camo_screen(spec, screen_b64, SEAM_VEHICLE_TABLE if table is SEAM_TABLE else '')
                    for spec in VEHICLE_KINDS] +
                   # the soldier's window, in place of APARIENCIA (the door is moved in the KITS screen)
                   [kits_screen(), soldier_skin_screen(screen_b64, SEAM_SOLDIER_TABLE if table is SEAM_TABLE else '')] +
                   # the loadout weapons' camo windows: the pistol's and the crossbow's on each gadget row (the doors are the
                   # LOADOUT screen's rows)
                   [loadout_screen(row_b64, SEAM_SIDEARM_TABLE if table is SEAM_TABLE else '')] +
                   [weapon_row_screen(spec, screen_b64) for spec in WEAPON_ROWS] +
                   [flow_graph()],
        # the kit movie's vehicle row with its second button (the game's own movie, edited at build time)
        'movies': [kitview_movie()],
        'images': [],
        # the weapon view and the vehicle view: client Lua the build ships beside ext/Client/__init__.lua
        'clientScripts': ['CamoWeaponView.lua', 'CamoWeaponPoses.lua', 'CamoAccessoryBones.lua', 'CamoVehicleView.lua',
                          'CamoSoldierView.lua',
                          # the pistols as the view draws them: their drawn centre and size and the parked pieces (sidearm_pieces.py)
                          'CamoSidearmPieces.lua'],
        # the screen's ActionScript tells the Lua about the drag through the AS2 channel: the mod carries the receiver itself
        'as2Channel': True,
    }


def main():
    row_b64 = compile_script('CamoRow', 'the accessories screen')
    screen_b64 = compile_script('CamoScreen', 'the camo screen')
    vehicle_row_b64 = compile_script('CamoVehicleRow', 'the TIERRA / AIRE rows')
    baked = camo_table() if BAKE_TABLE else ''
    if not BAKE_TABLE:
        print('baked table: OFF -- the five screens live on the one the framework hands them (BAKE_TABLE in this file)')

    # the live-wiring guard, before a single file is written: a dropped wire is a door that opens nothing
    for screen in document(row_b64, screen_b64, baked, vehicle_row_b64)['screens']:
        problems = check_live_wiring(screen)

        if problems:
            print('LIVE WIRING would be dropped in ' + screen['partition'] + ':')
            for line in problems:
                print('   ' + line)
            raise SystemExit('refusing to write the document: move those nodes AFTER the ones they wire to')

    # ⛔ THE THREE-LISTS TRAP, COUNTED (2026-09-20): every screen the camo screen's script runs on carries the soldier's variables
    # (on for the soldier's window, off elsewhere) -- counted in the document, never asserted once
    for table in (baked, SEAM_TABLE):
        screens = document(row_b64, screen_b64, table, vehicle_row_b64)['screens']
        ops = [op for s in screens for op in s['stage'] if op.startswith('script:camoscreen:')]
        with_vars = [op for op in ops if '_camoSoldier=' in op and '_camoTabWords=' in op and '_camoSkinHide=' in op and '_camoBaseOwn=' in op]
        soldier = [op for op in ops if '_camoSoldier=1|' in op]
        own = [op for op in ops if '_camoBaseOwn=1' in op]
        want = 7 + len(WEAPON_ROWS)
        if len(ops) != want or len(with_vars) != len(ops) or len(soldier) != 1 or len(own) != len(WEAPON_ROWS):
            raise SystemExit('the camo screen script runs on %d screen(s), %d carry the soldier and weapon-row variables, %d in soldier '
                             'mode, %d in weapon-row mode; expected %d, %d, 1, %d' % (len(ops), len(with_vars), len(soldier), len(own),
                                                                                   want, want, len(WEAPON_ROWS)))
        # and the rows' script: on the accessories screen and on the loadout, each with the slot names it needs
        rows = [op for s in screens for op in s['stage'] if op.startswith('script:camorow:')]
        if len(rows) != 2 or any('_camoAccSlots=' not in op or '_camoAccOnly=' not in op or
                                 ('_camoTableKey=%d' % CAMO_TABLE_KEY) not in op for op in rows):
            raise SystemExit('the rows script runs on %d screen(s) (expected 2, each with _camoAccSlots and _camoAccOnly)' % len(rows))
        # and both get the table: a rows screen without its mailbox draws every twin as a pistol of its own (the fold needs it)
        mails = [s for s in screens if any(op.startswith('script:camorow:') for op in s['stage'])
                 and any(op.startswith('add:') and (':' + CAMO_MAIL_CLIP + ':') in op for op in s['stage'])
                 and any(n['instanceName'] == CAMO_TABLE_NODE for n in s['nodes'])]
        if len(mails) != 2:
            raise SystemExit('%d rows screen(s) carry the mailbox clip and its writer (expected 2)' % len(mails))
    print('the camo screen script: %d screens carry the soldier and weapon-row variables, 1 in soldier mode, %d in weapon-row '
          'mode; the rows script on 2 screens' % (7 + len(WEAPON_ROWS), len(WEAPON_ROWS)))

    for filename, table in (('CamoMenu.json', baked), ('CamoMenu.seam.json', SEAM_TABLE)):
        out = os.path.join(DATA, filename)
        with open(out, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(document(row_b64, screen_b64, table, vehicle_row_b64), f, indent=2, ensure_ascii=False)
            f.write('\n')
        print(out, 'written', '(seam table)' if table is SEAM_TABLE else
              ('baked table: %d characters' % len(table)) if table else '(no table)')
    print('buttons:', ', '.join(name for name, _ in FAMILIES), '; grid', f'{GRID_WIDTH}x{round(GRID_ROWS * CELL_H + (GRID_ROWS - 1) * CELL_GAP)}', 'at', (PANEL_X, GRID_Y), '; info box at', INFO_AT)


if __name__ == '__main__':
    main()
