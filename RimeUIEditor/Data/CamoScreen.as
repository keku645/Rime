// CamoScreen — a frame-1 action for the camo screen the document makes (compiled behind CamoCommon.as by
// External/keku/make_camomenu_doc.py; the document's stage op `script:camoscreen:<vars>:<base64>` puts it on the timeline).
//
// The screen the camo row of the accessories screen opens, the way the newer game paints a weapon: a page header (the
// accessories path plus ACCESSORIES, and the camo title, both through the game's localiser), the kit's name, BACK, eight
// family buttons (MISC, ADAPTIVE, AUTUMN, DESERT, NAVAL, SNOW, URBAN, WOODLAND) and a Grid of thumbnails (KitCell: picture +
// name, two to a row, each as wide as a family button, on the buttons' grid) listing the camos of the family picked — the
// grid's own vertical scrollbar when the family has more rows than the grid shows — and the info box with the camo's
// description under the mouse or the cursor. The grid's data is the camo row's payload (GridItems ← WeaponAccessory4),
// made ours at the door (CamoCommon: the table's names, thumbnails and descriptions; carriers dropped). Picking a cell fires
// the grid's Released event WITH THE CAMO'S IDENTIFIER (not the cell's index), which the screen's graph wires to
// SetAccessory4 and on to the AccessoryChanged output — the flow graph's store action does the rest, as on the accessories
// screen; the screen stays up, the weapon shows the pick. BACK / Esc go back to the accessories screen through the graph.
//
// Keyboard / controller: the grid holds the focus (the only widget of focus group 0), so the engine hands it the input
// concepts; a cursor over the family buttons (two columns, four rows — the mouse-over look marks the one under the cursor)
// and the grid's cells (the grid's own selection, its scrollbar following): Down out of the buttons into the grid, Up out
// of the grid's first row back to the buttons; Activate on a button picks the family (the cursor stays), on a cell picks the
// camo. The cursor starts on the button of the family worn.
//
// Everything is done on the INSTANCES the screen places; closures come from NAMED factories (a function literal called in
// place does not compile right).
//
// Parameters (besides CamoCommon's):
//   _camoRoot      the screen's root clip ("instance1")
//   _camoButtons   the family buttons, in the families' order ("CamoCat_01,CamoCat_02,…")
//   _camoGrid      the grid ("CamoGrid")
//   _camoInfo      the info box ("KitInfoBox_01"; "" = none), parked by its ART's top-left at _camoInfoAt ("x,y,scale%")
//   _camoCell      the thumbnail cells: "width,gap" in px ("224,6")
//   _camoImageScale  the picture inside a cell (the camo band, the no-camo mark), % of the cell's own picture box ("150")
//   _camoLabelShift  the cell's name moved down this much, in the cell's own units (a cell is scaled about twice on screen) ("1.5")
//   _camoHeader    the page header ("PageHeader_01"; "" = none)
//   _camoPath      the header's path: text ids joined the way the game joins its own ("ID_M_CUST_KITSACC_TITLE_PATH,ID_M_CUST_KITSACC_TITLE")
//   _camoTitle     the header's title when the language is not in _camoTitles: a text id ("ID_P_CAT_CAMO")
//   _camoTitles    the title by language, our own word ("en~CAMOUFLAGE;es~CAMUFLAJE;…" — the game has no text that says just that;
//                  the language is the localiser's getLanguage(), matched by its first two letters, "us"→en, "ge"→de, "jp"→ja)
//   _camoDragLeft  the stage x (1280 wide) from which a mouse press is a drag over the 3D half, not a click on the panel ("480")
//   _camoVehicle   "land" / "air" = a VEHICLE's window (the CAMUFLAJE button of a TIERRA / AIRE row): its own "Sin camuflaje"
//                  cell, since the game hands it no items; a pick says "VVC<id>" to the vehicle view. "" = a weapon's window
//   _camoSoldier   "1" = the SOLDIER's window, in place of APARIENCIA (see SOLDIER MODE below); "0" everywhere else, with:
//   _camoTabs      the tab buttons ("SkinTab_01,…"), put in front of the family buttons as the buttons' first row
//   _camoTabWords  the tabs' words by language ("en~OUTFIT,HEAD,TORSO,LEGS;es~CONJUNTO,CABEZA,TORSO,PIERNAS;…")
//   _camoButtonColumns  the buttons' columns ("2" the camo screen's two, "4" the soldier's: the tabs and the families four to a row)
//   _camoTeamKey / _camoKitKey  the data keys that say which soldier: the KITS screen's sub header (ID_M_KITS_US / _RU) and its
//                  kit row (0 assault, 1 engineer, 2 support, 3 recon)
//   _camoSkinHide  identifiers the grid never lists (the row of ours in the kit's list, which only exists so no kit rule drops ours)

var camoScreen = this[String(_camoRoot)];
var camoTimeline = this;         // the screen's timeline, where the recorder's rueNote lives when one rides (a closure run by a timer has no this)
var camoGridName = String(_camoGrid);
var camoInfoName = String(_camoInfo);
var camoInfoAt = String(_camoInfoAt).split(",");
var camoInfoHome = undefined;   // the info box's own place, taken once
var camoHeaderName = String(_camoHeader);
var camoPathIds = String(_camoPath).split(",");
var camoTitleId = String(_camoTitle);
var camoDragLeft = Number(_camoDragLeft);   // presses right of this stage x turn the weapon (the panel lives left of it)
var camoTitles = {};
var camoTitleParts = String(_camoTitles).split(";");
var camoT = 0;
while(camoT < camoTitleParts.length)
{
   var camoTilde = camoTitleParts[camoT].indexOf("~");
   if(camoTilde > 0)
   {
      camoTitles[camoTitleParts[camoT].substr(0, camoTilde).toLowerCase()] = camoTitleParts[camoT].substr(camoTilde + 1);
   }
   camoT = camoT + 1;
}
// the game's language as the localiser names it, reduced to the two-letter key of camoTitles ("" when unknown). In the game
// getLanguage() answers the engine's LanguageFormat INDEX as a string (measured 2026-09-17: "3" on a Spanish client): 0 English,
// 1 French, 2 German, 3 Spanish, 4 Italian, 5 Japanese, 6 Russian, 7 Polish, 8 Dutch, 9 Portuguese, 10 Traditional Chinese,
// 11 Korean, 12 Czech; the preview answers a two-letter code
var camoLanguageIndexKeys = ["en", "fr", "de", "es", "it", "ja", "ru", "pl", "nl", "pt", "zh", "ko", "cs"];
var camoLanguageKey = function()
{
   var lang = "";
   if(_global.Data != undefined && _global.Data.UILocalizeComp != undefined && _global.Data.UILocalizeComp.getLanguage != undefined)
   {
      lang = String(_global.Data.UILocalizeComp.getLanguage()).toLowerCase();
   }
   if(lang == "" || lang == "undefined" || lang == "null")
   {
      return "";
   }
   var index = Number(lang);
   if(!_global.isNaN(index) && index >= 0 && index < camoLanguageIndexKeys.length)
   {
      return camoLanguageIndexKeys[index];
   }
   var key = lang.substr(0, 2);
   if(key == "us") key = "en";
   if(key == "ge") key = "de";
   if(key == "jp") key = "ja";
   return key;
};
// the title: our word for the language, else the game's camo category text
var camoTitleText = function()
{
   var key = camoLanguageKey();
   if(key != "" && camoTitles[key] != undefined)
   {
      return camoTitles[key];
   }
   return camoLocalise(camoTitleId);
};
// the line for an attachment that draws nothing, by language (_camoNoModel, our own text: the game has no string that
// says it). Same key as the title; English when the language is not listed, and "" when the document ships no table.
var camoNoModelTexts = {};
var camoNoModelParts = String(_camoNoModel).split(";");
var camoN = 0;
while(camoN < camoNoModelParts.length)
{
   var camoNTilde = camoNoModelParts[camoN].indexOf("~");
   if(camoNTilde > 0)
   {
      camoNoModelTexts[camoNoModelParts[camoN].substr(0, camoNTilde).toLowerCase()] = camoNoModelParts[camoN].substr(camoNTilde + 1);
   }
   camoN = camoN + 1;
}
var camoNoModelText = function()
{
   var key = camoLanguageKey();
   if(key != "" && camoNoModelTexts[key] != undefined)
   {
      return camoNoModelTexts[key];
   }
   return camoNoModelTexts["en"] == undefined ? "" : camoNoModelTexts["en"];
};
// is THIS screen about an attachment the weapon draws nothing for? (the framework's "X" record)
var camoScreenHasNoModel = function()
{
   return camoBaseMode && camoBaseWanted != "" && camoNoModel[camoIdKey(camoBaseWanted)] == true;
};

var camoCellSpec = String(_camoCell).split(",");
var camoCellWidth = camoCellSpec.length >= 1 && !_global.isNaN(Number(camoCellSpec[0])) ? Number(camoCellSpec[0]) : 224;
var camoCellGap = camoCellSpec.length >= 2 && !_global.isNaN(Number(camoCellSpec[1])) ? Number(camoCellSpec[1]) : 6;
var camoCellScale = camoCellWidth / Widget.Grid.Cell.KitCell.KIT_CELL_WIDTH * 100;   // the game's 113x47 kit cell scaled to the button's width
var camoImageScale = _global.isNaN(Number(_camoImageScale)) ? 150 : Number(_camoImageScale);
var camoLabelShift = _global.isNaN(Number(_camoLabelShift)) ? 1.5 : Number(_camoLabelShift);
var camoCellColumns = 2;
var camoButtonNames = String(_camoButtons) == "" || String(_camoButtons) == "undefined" ? [] : String(_camoButtons).split(",");

// SOLDIER MODE (_camoSoldier = "1"; keku 2026-09-28: *"cuando haces click en apariencia aparece esta ventana, vamos a sustituir esta
// UI y pondremos la misma que usamos en nuestro sistema"*, and each part with any other: *"todo con todo"*). The window the KITS
// screen's APARIENCIA opens: the same window, with a row of four TABS on top -- CONJUNTO (every part at once), CABEZA, TORSO, PIERNAS.
// Its cells are the kit's own looks (the game's appearance rows, the grid's binding) and the SKINS of the mailbox made for this
// soldier that have that part; a pick is this soldier's for that part (all three on CONJUNTO) and goes to the client's Lua
// ("SKN<part>,<id>,<soldier>", CamoSoldierView.lua), which has the server dress the soldier. The window never writes the game's own
// appearance: that one is the BASE, worn by every part with no pick of ours -- and picking the game's own look on CONJUNTO takes the
// picks of ours off ("SKNall,0,…"). The tabs are the buttons' first row (four columns): the keyboard walks tabs, families and cells
// as one panel.
var camoSoldierMode = String(_camoSoldier) == "1";
var camoTabNames = camoSoldierMode && String(_camoTabs) != "" && String(_camoTabs) != "undefined" ? String(_camoTabs).split(",") : [];
var camoTabCount = camoTabNames.length;
var camoTab = 0;                                            // the tab picked: 0 CONJUNTO (every part), 1 head, 2 torso, 3 legs
var camoTabParts = ["all", "head", "torso", "legs"];
if(camoTabCount > 0)
{
   camoButtonNames = camoTabNames.concat(camoButtonNames);
}
var camoTabWords = {};                                      // language key -> [word of each tab]
var camoTabWordParts = String(_camoTabWords).split(";");
var camoTW = 0;
while(camoTW < camoTabWordParts.length)
{
   var camoTWTilde = camoTabWordParts[camoTW].indexOf("~");
   if(camoTWTilde > 0)
   {
      camoTabWords[camoTabWordParts[camoTW].substr(0, camoTWTilde).toLowerCase()] = camoTabWordParts[camoTW].substr(camoTWTilde + 1).split(",");
   }
   camoTW = camoTW + 1;
}
var camoSkinHide = {};                                      // identifiers the grid never lists
var camoSkinHideParts = String(_camoSkinHide).split(",");
var camoSH = 0;
while(camoSH < camoSkinHideParts.length)
{
   if(camoSkinHideParts[camoSH].length > 0 && camoSkinHideParts[camoSH] != "undefined")
   {
      camoSkinHide[camoIdKey(camoSkinHideParts[camoSH])] = true;
   }
   camoSH = camoSH + 1;
}
var camoTeamKey = _global.isNaN(Number(_camoTeamKey)) ? 0 : Number(_camoTeamKey);
var camoKitKey = _global.isNaN(Number(_camoKitKey)) ? 0 : Number(_camoKitKey);
var camoSoldierClasses = ["Assault", "Engineer", "Support", "Recon"];   // the KITS screen's rows, in its order (measured)
var camoSoldierKey = "";     // "US_Assault", "RU_Engineer_XP4"…: the soldier this window is about (the studio's and the framework's key)
var camoSoldierTeam = "";    // "US" / "RU" as last read
var camoStock = [];          // the game's appearance rows of the kit (made ours), each with its base mark (_camoBase) kept

// ACCESSORY MODE (_camoBase = "1"): the same screen, showing the camos of ONE accessory instead of the camos of the weapon.
// The row hands over every item of its slot -- the plain accessories AND the camos of each, all in one list -- so what this
// screen shows is the accessory worn plus the camos whose table entry names it as their base. There are no family buttons in
// this mode: the grid is the whole panel and the keys live in it.
var camoBaseMode = String(_camoBase) == "1";
var camoBaseWanted = "";
// _camoBaseOwn = "1" (the PISTOL's window, opened from the LOADOUT screen's SIDEARM row): the piece is the item worn even when no
// table entry names a base yet -- the window lists that pistol ("Sin camuflaje") and the camos of it, never the kit's other pistols
var camoBaseOwn = String(_camoBaseOwn) == "1";
// the accessory WITHOUT a camo is drawn as the weapon's own "no camo" row is (keku 2026-09-18: "el box de camuflaje
// predeterminado en vez del icono y el nombre del accesorio"): on this screen every cell is a camo, and the plain
// accessory is the camo-less one. It also belongs to the family with no keywords, where the weapon's own default sits.
var camoDefaultLabel = String(_camoDefaultLabel);
var camoDefaultImage = String(_camoDefaultImage);
var camoDefaultDesc = String(_camoDefaultDesc) == "undefined" ? "" : String(_camoDefaultDesc);

// VEHICLE MODE (_camoVehicle = "land" / "air"; keku 2026-09-23: *"una ventana nueva que será exactamente la que ya usamos para
// accesorios y armas pero esta vez adaptada a vehículos"*): the same window, opened by the CAMUFLAJE button of a TIERRA / AIRE
// row. The game has no camos of vehicles and hands this screen no items at all, so the list is made here: the one cell that is
// always there, "Sin camuflaje", out of the same values the weapon's own no-camo cell is drawn with. Whatever the grid's binding
// ever does deliver (the vehicle camos, once the framework publishes them) comes after it -- the cell stays at the head.
var camoVehicleMode = String(_camoVehicle) == "land" || String(_camoVehicle) == "air";
// ⭐ THE GAME'S VEHICLE IS TAKEN AWAY, AS THE WEAPON WINDOW PARKS THE MANNEQUIN (keku 2026-09-23: the vehicle is placed "como el
// arma", so the view shows a copy of its own -- see CamoVehicleView.lua). _camoHideMs after the window is set up (the view reads
// the game's vehicle in that time: it needs its mesh to build the copy), the grid fires its TooltipActive event -- unused by
// a KitCell grid -- which the window's graph wires to its HideVehicle output, and the flow graph takes it to the game's own
// UnspawnVehicle; the way back spawns it again. "VHD" tells the view when it happened. -1 = never.
// ⛔ RETIRED with the copy (_camoHideMs is -1 for good): TooltipActive is now the pick's RESPAWN (see camoRespawnMs below), and
// HideVehicle has no wire into it.
var camoHideMs = _global.isNaN(Number(_camoHideMs)) ? 1500 : Number(_camoHideMs);
var camoHideTimer = undefined;
var camoMakeHide = function(grid)
{
   return function()
   {
      clearInterval(camoHideTimer);
      grid.fireEvent(Widget.Grid.Grid.TOOLTIP_ACTIVE, "hide");
      camoSignal("VHD");
   };
};
// ⭐ A PICK MAKES THE SHOWROOM'S VEHICLE AGAIN: the pick's own chain (the glitch, then the game's UnspawnVehicle) takes it away,
// and _camoRespawnMs later the grid fires its TooltipActive event -- unused by a KitCell grid -- which the window's graph wires
// to its RespawnVehicle output and the flow graph to the game's SpawnVehicle: the vehicle comes back made with the mesh the
// client's Lua has just pointed it at. Apart, because a spawn in the same chain reaches the server while the old vehicle is
// still there and does nothing (measured, run 112). "VRS" tells the view. -1 = never.
// ⛔ AND THE SPAWN NEEDS THE VEHICLE SELECTED AGAIN (run 113: the spawn apart, 0.5 s later, still brought nothing back, while the
// way back to TIERRA / AIRE -- whose row selects its vehicle as it comes up -- did): the game itself never spawns after an unspawn
// without writing the selection first (CustomizationGraph: UnSpawnVehicle → SetVehicleCategory → SetVehicle → SpawnVehicle). So
// the event carries the row this window was opened for -- CustSelectedVehicle as it stood when the window came up, before any
// unspawn -- and the window's graph writes it back (its own SetVehicle, the door's) before the spawn.
// "VRS<row when opened>,<row now>" tells the view, so the log shows whether the unspawn had cleared it -- ✅ it does (run 114:
// "row 0 selected again (it read -1 now)" on every pick, and the tank came back wearing the camo).
var camoVehicleSetKey = -1749749687;   // UICustomizationComp.CustSelectedVehicle (make_camomenu_doc.py VEHICLE_SET_KEY)
var camoVehicleRow = "";
var camoDataValue = function(data)
{
   if(data == undefined)
   {
      return "";
   }
   if(typeof data == "number" || typeof data == "string")
   {
      return String(data);
   }
   var named = ["Value", "Data", "Int", "Index", "Text", "Param"];
   var i = 0;
   while(i < named.length)
   {
      if(typeof data[named[i]] == "number" || (typeof data[named[i]] == "string" && String(data[named[i]]) != ""))
      {
         return String(data[named[i]]);
      }
      i = i + 1;
   }
   for(var k in data)
   {
      if(typeof data[k] == "number" || (typeof data[k] == "string" && String(data[k]) != ""))
      {
         return String(data[k]);
      }
   }
   return "";
};
var camoVehicleRowNow = function()
{
   if(_global.Data == undefined || _global.Data.UIDataInterfaceComp == undefined)
   {
      return "";
   }
   return camoDataValue(_global.Data.UIDataInterfaceComp.getData(camoVehicleSetKey));
};
// ⛔ AND NOT BEFORE THE OLD VEHICLE IS GONE (run 115, the respawn at once: "it read -1 now; held now: vehicles/m1a2/m1abrams" --
// the selection was cleared on the client at once, but the server still had the vehicle when the spawn reached it, ignored it,
// and then took the vehicle away; the next pick, with the room already empty, did bring one). A spawn while the vehicle is still
// there does nothing (run 110), so the ask is REPEATED: at each of _camoRespawnMs ("150,400,900", ms after the pick) the row is
// selected again and the spawn asked -- the first one after the server has taken the old vehicle brings the new one, and the
// rest find it there and do nothing. As soon as the server allows, whatever its lag.
var camoRespawnSteps = String(_camoRespawnMs == undefined ? "150,400,900" : _camoRespawnMs).split(",");
var camoRespawnStep = 0;
var camoRespawnTimer = undefined;
var camoMakeRespawn = function(grid)
{
   return function()
   {
      clearInterval(camoRespawnTimer);
      camoRespawnTimer = undefined;
      var now = camoVehicleRowNow();
      var row = camoVehicleRow != "" ? camoVehicleRow : now;
      grid.fireEvent(Widget.Grid.Grid.TOOLTIP_ACTIVE, row == "" ? "0" : row);
      camoRespawnStep = camoRespawnStep + 1;
      camoSignal("VRS" + camoVehicleRow + "," + now + "," + camoRespawnStep);
      if(camoRespawnStep < camoRespawnSteps.length)
      {
         var wait = Number(camoRespawnSteps[camoRespawnStep]) - Number(camoRespawnSteps[camoRespawnStep - 1]);
         camoRespawnTimer = setInterval(camoMakeRespawn(grid), _global.isNaN(wait) || wait < 1 ? 1 : wait);
      }
   };
};
var camoVehicleNoCamoId = String(_camoNoCamoIds).split(",")[0];
// The list: "Sin camuflaje", whatever the grid's binding delivers (nothing today), and THE VEHICLE CAMOS OF THE TABLE for this
// window's CLASS -- the entries whose base "@V:<class SID>,…" names it. CamoVehicleView.lua writes the whole table before any
// window opens (all the vehicle camos, and "W~<id>~<class SID>" for what each class wears), and the class is what the window's
// name binding carries the instant it opens (UIKitComp.SelectedVehicleName: ID_EOR_SCORINGBUCKET_VEHICLEMBT for an MBT) -- so
// the list is there at once, as the weapon window's is (keku 2026-09-23: *"debe ser todo instantáneo"*). The worn cell is the
// one marked; with none worn, "Sin camuflaje" is.
var camoVehicleClass = "";   // the class SID of this window, from its name binding ("" = not known yet)
// a class named by the table matches the window's either as the SID or as what the SID reads in this language (whichever the
// binding hands over)
var camoVehicleClassIs = function(sid)
{
   return camoVehicleClass != "" && (String(sid) == camoVehicleClass || camoLocalise(String(sid)) == camoVehicleClass);
};
var camoVehicleClassesOf = function(entry)
{
   var base = entry == undefined ? "" : String(entry.base);
   if(base.indexOf("@V:") != 0)
   {
      return [];
   }
   return base.substr(3).split(",");
};
// this window's class as the SID the table and the Lua key it by: the binding may hand over the SID itself or what it reads in
// this language, and then the SID is the table's class that reads the same ("" / the text as it came when none does)
var camoVehicleClassSid = function()
{
   if(camoVehicleClass == "" || camoVehicleClass.indexOf("ID_") == 0)
   {
      return camoVehicleClass;
   }
   for(var id in camoTable)
   {
      var classes = camoVehicleClassesOf(camoTable[id]);
      var c = 0;
      while(c < classes.length)
      {
         if(camoVehicleClassIs(classes[c]))
         {
            return String(classes[c]);
         }
         c = c + 1;
      }
   }
   for(var sid in camoVehicleWornBy)
   {
      if(camoVehicleClassIs(sid))
      {
         return String(sid);
      }
   }
   return camoVehicleClass;
};
// what this window's class wears ("" = its own paint)
var camoVehicleWornHere = function()
{
   for(var sid in camoVehicleWornBy)
   {
      if(camoVehicleClassIs(sid))
      {
         return String(camoVehicleWornBy[sid]);
      }
   }
   return "";
};
var camoVehicleItems = function(items)
{
   var out = [];
   var worn = false;
   var i = 0;
   while(i < items.length)
   {
      if(items[i] != undefined && items[i].DefaultSelected == true && !camoIsNoCamo(items[i]))
      {
         worn = true;
      }
      i = i + 1;
   }
   var wornHere = camoVehicleWornHere();
   var ours = [];
   for(var id in camoTable)
   {
      var classes = camoVehicleClassesOf(camoTable[id]);
      var mine = false;
      var c = 0;
      while(c < classes.length)
      {
         if(camoVehicleClassIs(classes[c]))
         {
            mine = true;
         }
         c = c + 1;
      }
      if(mine)
      {
         var wears = wornHere != "" && wornHere == String(id);
         if(wears)
         {
            worn = true;
         }
         ours.push({Index:Number(id), Label:camoTable[id].name, ItemImage:camoTable[id].texture, DefaultSelected:wears});
      }
   }
   ours.sort(function(a, b)
   {
      return String(a.Label) < String(b.Label) ? -1 : (String(a.Label) > String(b.Label) ? 1 : 0);
   });
   out.push({Index:camoVehicleNoCamoId, Label:camoDefaultLabel, ItemImage:camoDefaultImage, DefaultSelected:!worn});
   i = 0;
   while(i < items.length)
   {
      if(items[i] != undefined && !camoIsNoCamo(items[i]))
      {
         out.push(items[i]);
      }
      i = i + 1;
   }
   i = 0;
   while(i < ours.length)
   {
      out.push(ours[i]);
      i = i + 1;
   }
   return out;
};
// the cursor goes to the WORN cell once the list is known (table AND class: whichever comes last), as an accessory window opens
// on the piece worn; a pick of the player's own before that means he chose, and the cursor is left where he put it
var camoVehicleCursorSet = false;
var camoVehicleTableIn = false;
// the list made again from what is known (the table and the class), the cursor on the worn cell the first time both are, and the
// receipt "VTB<camos>,<worn>,<class>"
var camoVehicleRebuild = function()
{
   camoItems = camoVehicleItems(camoItems0 == null ? [] : camoRewriteItems(camoItems0));
   var placeCursor = camoVehicleTableIn && camoVehicleClass != "" && !camoVehicleCursorSet;
   if(camoZone == "" || placeCursor)
   {
      camoFamily = camoFamilyOfSelected();
   }
   camoRefresh();
   if(placeCursor)
   {
      camoVehicleCursorSet = true;
      var wornCell = camoCellOfWorn();
      if(wornCell >= 0)
      {
         camoCursorToCell(wornCell);
      }
   }
   var n = 0;
   var k = 0;
   while(k < camoItems.length)
   {
      if(!camoIsNoCamo(camoItems[k]))
      {
         n = n + 1;
      }
      k = k + 1;
   }
   camoSignal("VTB" + n + "," + camoVehicleWornHere() + "," + camoVehicleClassSid() +
      (camoVehicleClassSid() == camoVehicleClass ? "" : " (read as " + camoVehicleClass + ")"));
};
// ---- SOLDIER MODE: which soldier, what each part wears, the cells of the tab picked -------------------------------------------
// the soldier this window is about, as the studio and the framework key it: the team out of the KITS screen's sub header
// ("ID_M_KITS_US" / "_RU"), the class out of its kit row, "_XP4" when the mailbox says the level's kits are Aftermath's.
// Read every time the list is made (it costs two reads); "SKK<key>" says it to the Lua's log when it changes.
var camoSoldierKeyNow = function()
{
   var team = camoSoldierTeam;
   var kit = -1;
   if(_global.Data != undefined && _global.Data.UIDataInterfaceComp != undefined)
   {
      var header = camoDataValue(_global.Data.UIDataInterfaceComp.getData(camoTeamKey)).toUpperCase();
      if(header.indexOf("_RU") >= 0)
      {
         team = "RU";
      }
      else if(header.indexOf("_US") >= 0)
      {
         team = "US";
      }
      var row = Number(camoDataValue(_global.Data.UIDataInterfaceComp.getData(camoKitKey)));
      if(!_global.isNaN(row) && row >= 0 && row < camoSoldierClasses.length)
      {
         kit = row;
      }
   }
   if(team == "" || kit < 0)
   {
      return camoSoldierKey;
   }
   camoSoldierTeam = team;
   var key = team + "_" + camoSoldierClasses[kit] + (String(camoSkinPack).toUpperCase() == "XP4" ? "_XP4" : "");
   if(key != camoSoldierKey)
   {
      camoSignal("SKK" + key);
   }
   return key;
};
// what this soldier wears on a part: "" = the game's own look (no pick of ours), else the identifier; on CONJUNTO the one all three
// parts wear, "-" when they differ
var camoSoldierWorn = function(part)
{
   var w = camoSkinWorn[camoSoldierKey];
   if(w == undefined)
   {
      return "";
   }
   if(part != "all")
   {
      return w[part] == undefined ? "" : String(w[part]);
   }
   if(w.head == undefined && w.torso == undefined && w.legs == undefined)
   {
      return "";
   }
   if(w.head != undefined && String(w.head) == String(w.torso) && String(w.torso) == String(w.legs))
   {
      return String(w.head);
   }
   return "-";
};
// the game's rows as the engine hands them, kept with their own mark (the base look): the row of ours and anything without a name
// (an unlock the client's description index does not know) never listed
var camoSoldierStock = function(items)
{
   var out = [];
   var i = 0;
   while(i < items.length)
   {
      var it = items[i];
      if(it != undefined && camoSkinHide[camoIdKey(it.Index)] != true && it.Label != undefined && String(it.Label) != "")
      {
         it._camoBase = it.DefaultSelected == true;
         out.push(it);
      }
      i = i + 1;
   }
   return out;
};
// the tab's cells: every look of the game's for the kit, then the skins of this soldier that have the tab's part (any part on
// CONJUNTO), by name; the one worn on that part marked (the game's own look when there is no pick of ours; none on CONJUNTO when
// the parts differ)
var camoSoldierItems = function()
{
   camoSoldierKey = camoSoldierKeyNow();
   var part = camoTabParts[camoTab];
   var worn = camoSoldierWorn(part);
   var out = [];
   var i = 0;
   while(i < camoStock.length)
   {
      var s = camoStock[i];
      out.push({Index:s.Index, Label:s.Label, ItemImage:s.ItemImage, Description:s.Description, ShowPlus:false, _camoBase:s._camoBase,
                DefaultSelected:worn == "" ? s._camoBase == true : (worn != "-" && camoIdKey(s.Index) == worn)});
      i = i + 1;
   }
   var skins = [];
   for(var id in camoTable)
   {
      var e = camoTable[id];
      if(e != undefined && e.soldier != undefined && String(e.soldier) == camoSoldierKey &&
         (part == "all" || ("," + String(e.parts) + ",").indexOf("," + part + ",") >= 0))
      {
         // on a part's tab, the part's OWN cell when the skin gives it one (keku 2026-09-28: each part can be a different thing) -- its
         // picture, its family and its name; CONJUNTO (and a part with none) the skin's
         var own = part == "all" || camoSkinParts[id] == undefined ? undefined : camoSkinParts[id][part];
         var tex = own != undefined && own.texture != "" ? own.texture : e.texture;
         var fam = own != undefined && own.family != "" ? own.family : e.family;
         var label = own != undefined && own.name != undefined && own.name != "" ? own.name : e.name;
         var text = own != undefined && own.desc != undefined && own.desc != "" ? own.desc : e.desc;
         skins.push({Index:Number(id), Label:label, ItemImage:tex, ShowPlus:false, _camoSkin:true, _camoFamily:fam,
                     DefaultSelected:worn != "" && worn != "-" && worn == String(id),
                     Description:{Type:1, Label:label, Description:text, Index:Number(id), Image:tex, Icon:tex, Category:fam}});
      }
   }
   skins.sort(function(a, b)
   {
      return String(a.Label) < String(b.Label) ? -1 : (String(a.Label) > String(b.Label) ? 1 : 0);
   });
   i = 0;
   while(i < skins.length)
   {
      out.push(skins[i]);
      i = i + 1;
   }
   return out;
};
// the family of the cell worn on this tab, or -1 when none is marked (the window then stays on the family it shows)
var camoSoldierFamilyOfWorn = function()
{
   var i = 0;
   while(i < camoItems.length)
   {
      if(camoItems[i].DefaultSelected == true)
      {
         return camoFamilyOf(camoItems[i]);
      }
      i = i + 1;
   }
   return -1;
};
// the list made again for the tab (a tab picked, the table in, the rows in, a pick) -- on the worn cell's family when the cursor is
// not in use yet or the tab changed
var camoSoldierRebuild = function(p_FollowWorn)
{
   camoItems = camoSoldierItems();
   if(p_FollowWorn)
   {
      var f = camoSoldierFamilyOfWorn();
      if(f >= 0)
      {
         camoFamily = f;
      }
   }
   camoRefresh();
};
// where the keyboard cursor comes to rest when it appears: the family worn's button -- on the soldier's window the tab picked
// (the seam's photo, 2026-09-28: it sat on MISC while the window showed SNOW)
var camoHomeButton = function()
{
   return camoSoldierMode ? camoTab : camoTabCount + camoFamily;
};
var camoSoldierTableIn = false;   // the soldier table arrived once (the first one puts the window on the worn look's family)
// a tab's word for the game's language (English when the language is not listed, the property's own text when neither is)
var camoTabWord = function(tab)
{
   var key = camoLanguageKey();
   var words = key != "" && camoTabWords[key] != undefined ? camoTabWords[key] : camoTabWords["en"];
   return words == undefined || words[tab] == undefined ? "" : String(words[tab]);
};

// when this screen came up, so a mouse release left over from the click that opened it cannot count as a pick (the door is
// the row's release, so this should never fire -- it is the belt to that fix, and it costs one comparison)
var camoOpenedAt = getTimer();
var camoOpenGuardMs = 350;
var camoOpenCell = -1;     // the cell the window opened on (the camo worn): a stale hover must not steal it
var camoOpenStolen = 0;    // said once, so the log shows the theft happened without a line per event

var camoItems = [];        // the camo row's items as the engine hands them, made ours (Label, ItemImage, Index, DefaultSelected…)
var camoItems0 = null;     // the same payload BEFORE it was made ours: a table that arrives late dresses it again (see the mailbox)
var camoShown = [];        // grid cell i -> index into camoItems
var camoFamily = 0;        // the family button picked
var camoZone = "";         // the keyboard cursor: "" = none yet, "buttons" = on a family button, "grid" = on a cell
var camoCursor = 0;        // the family button under the cursor while camoZone == "buttons"
// the buttons' columns: the camo screen's two; the soldier window's four (its tabs are the first row of the same grid)
var camoButtonColumns = _global.isNaN(Number(_camoButtonColumns)) || Number(_camoButtonColumns) < 1 ? 2 : Number(_camoButtonColumns);

var camoGridClip = function()
{
   return camoScreen[camoGridName];
};
var camoInfoClip = function()
{
   return camoInfoName == "" || camoInfoName == "undefined" ? undefined : camoScreen[camoInfoName];
};
var camoHeaderClip = function()
{
   return camoHeaderName == "" || camoHeaderName == "undefined" ? undefined : camoScreen[camoHeaderName];
};
var camoButtonClip = function(i)
{
   return camoScreen[camoButtonNames[i]];
};

// a name wider than its cell's label scrolls (keku, 2026-09-17: the Spanish names — "Camuflaje táctico para L85A2" — were cut) the way
// the game scrolls its own long labels (the info box's Util.ScrollText): the game's ScrollTextTimer (2.5 s still, 2 s forward, 1 s
// still, 0.5 s back, all its labels in step) drives the field's own horizontal scroll; the callback is unregistered when the cell
// goes (the label clip's onUnload). Without the timer (never in the game), a frame handler on the label clip does the same by itself
var camoMakeScrollUpdate = function(txt)
{
   return function(val)
   {
      if(txt.maxhscroll > 0)
      {
         txt.hscroll = Math.round(txt.maxhscroll * val);
      }
   };
};
var camoMakeUnregister = function(fn)
{
   return function()
   {
      Util.ScrollTextTimer.getInstance().unregister(fn);
   };
};
var camoMarqueePause = 45;
var camoMakeMarquee = function(txt)
{
   var wait = camoMarqueePause;
   var back = false;
   return function()
   {
      if(wait > 0)
      {
         wait = wait - 1;
         return undefined;
      }
      if(back)
      {
         txt.hscroll = 0;
         back = false;
         wait = camoMarqueePause;
         return undefined;
      }
      if(txt.hscroll >= txt.maxhscroll)
      {
         back = true;
         wait = camoMarqueePause;
         return undefined;
      }
      txt.hscroll = txt.hscroll + 1;
   };
};
var camoMarquee = function(label)
{
   var txt = label == undefined ? undefined : label.txt;
   if(txt == undefined)
   {
      return false;
   }
   txt.hscroll = 0;
   delete label.onEnterFrame;
   var timer = Util != undefined && Util.ScrollTextTimer != undefined ? Util.ScrollTextTimer.getInstance() : undefined;
   if(label._camoScroll != undefined && timer != undefined)
   {
      timer.unregister(label._camoScroll);
   }
   label._camoScroll = undefined;
   if(txt.maxhscroll <= 0)
   {
      return false;
   }
   if(timer != undefined)
   {
      var fn = camoMakeScrollUpdate(txt);
      label._camoScroll = fn;
      timer.register(fn);
      label.onUnload = camoMakeUnregister(fn);
   }
   else
   {
      label.onEnterFrame = camoMakeMarquee(txt);
   }
   return true;
};

// a cell's content loaded (the next frame after its data): the name in a font that fits the label (the cell is scaled, the text with it),
// scrolling when it is still too long
// ⭐ THE PLATE EVERY CELL SITS ON (keku, 2026-09-20, with the game's own cell beside ours: *"vanilla tiene en todas
// las casillas un fondo gris de manera predeterminada"*). Ours had none, so a picture with transparency -- the
// crossed-out "no camo" icon above all -- was drawn straight onto the menu's sky.
// ⛔ IT IS A SIBLING OF THE CELLS, NOT A CHILD OF ONE (his second look: *"el fondo gris lo has puesto por encima no
// por debajo"*). A cell's own children live at NEGATIVE timeline depths, so a clip created inside it at -16000 sat
// ABOVE them -- and below -16384 there is no depth left to ask for. One clip on the cells' PARENT, under the lowest
// of them, is behind every cell whatever the cell does inside itself.
var camoPlateFill = 1974553;     // 0x1E2419 — the dark grey the game's cells wear
var camoPlateAlpha = 72;
var camoPlateLine = 5464171;     // 0x536B6B — its lighter edge
var camoPlateLineAlpha = 55;

var camoPlatesClip = undefined;   // kept, so an EMPTY family can still clear what the last one drew

var camoDrawPlates = function(grid, count, cw, cellH)
{
   var first = grid.gridClips[grid.getCellName(0)];
   var host = first == undefined ? undefined : first._parent;
   var plates = camoPlatesClip;
   if(plates == undefined && host != undefined)
   {
      // under the lowest child there is, which is what "behind the cells" means here
      var low = 0;
      for(var k in host)
      {
         if(typeof host[k] == "movieclip" && host[k].getDepth() < low)
         {
            low = host[k].getDepth();
         }
      }
      var depth = low - 1;
      if(depth < -16384)
      {
         depth = -16384;
      }
      plates = host.createEmptyMovieClip("_camoPlates", depth);
      camoPlatesClip = plates;
   }
   if(plates == undefined)
   {
      return;
   }
   plates.clear();
   if(count <= 0 || _global.isNaN(Number(cw)) || cw <= 0)
   {
      return;
   }
   var c = 0;
   while(c < count)
   {
      var cell = grid.gridClips[grid.getCellName(c)];
      if(cell != undefined)
      {
         plates.lineStyle(1, camoPlateLine, camoPlateLineAlpha);
         plates.beginFill(camoPlateFill, camoPlateAlpha);
         plates.moveTo(cell._x, cell._y);
         plates.lineTo(cell._x + cw, cell._y);
         plates.lineTo(cell._x + cw, cell._y + cellH);
         plates.lineTo(cell._x, cell._y + cellH);
         plates.lineTo(cell._x, cell._y);
         plates.endFill();
      }
      c = c + 1;
   }
};

var camoMakeCellLoad = function(orig)
{
   var load = function()
   {
      orig.call(this);
      // the picture bigger than the cell's own box: the cell's ImageManager fits what it loads into startW x startH (measured on
      // its clip's load, top-left anchored), so the box is widened before the fit and the clip moved to keep the box's centre
      var img = this.m_image;
      if(img != undefined && img._camoScaled != true && !_global.isNaN(Number(img.startW)) && camoImageScale != 100)
      {
         var k = camoImageScale / 100;
         img._x = img._x - img.startW * (k - 1) * 0.5;
         img._y = img._y - img.startH * (k - 1) * 0.5;
         img.startW = img.startW * k;
         img.startH = img.startH * k;
         img._camoScaled = true;
      }
      var txt = this.m_label == undefined ? undefined : this.m_label.txt;
      if(txt != undefined)
      {
         var fmt = new TextFormat();
         fmt.size = 10;   // the cell is scaled about twice: 10 pt reads as the buttons' text
         txt.setNewTextFormat(fmt);
         txt.setTextFormat(fmt);
         // the name a little lower: the game's label sits on the cell's top edge (keku 2026-09-17: "demasiado arriba")
         if(this.m_label._camoShifted != true && camoLabelShift != 0)
         {
            this.m_label._y = this.m_label._y + camoLabelShift;
            this.m_label._camoShifted = true;
         }
         this._camoScrolls = camoMarquee(this.m_label);
      }
   };
   load._camoWrap = true;
   return load;
};

// the info box put where the document says, by its ART's top-left: the box's art sits far from its clip's origin (the clip is
// placed at (0,0), the panel draws around (750,360)), so the offset from origin to art is measured once (getBounds) and scaled
var camoParkInfo = function()
{
   var info = camoInfoClip();
   if(info == undefined || camoInfoAt.length < 3)
   {
      return undefined;
   }
   if(camoInfoHome == undefined)
   {
      var b = info.getBounds(info._parent);
      var s = info._xscale == 0 ? 100 : info._xscale;
      camoInfoHome = {x:info._x, y:info._y, scale:s, offX:(b.xMin - info._x) * 100 / s, offY:(b.yMin - info._y) * 100 / s};
   }
   var scale = Number(camoInfoAt[2]);
   info._xscale = scale;
   info._yscale = scale;
   info._x = Number(camoInfoAt[0]) - camoInfoHome.offX * scale / 100;
   info._y = Number(camoInfoAt[1]) - camoInfoHome.offY * scale / 100;
   info._camoArtX = info._x + camoInfoHome.offX * info._xscale / 100;
   info._camoArtY = info._y + camoInfoHome.offY * info._yscale / 100;
};

// the camo under the mouse or the cursor: its name and text in the info box (the box's own Type 1 layout: label + description)
var camoShowInfo = function(index)
{
   var info = camoInfoClip();
   // ⛔ ON A PIECE THAT DRAWS NOTHING, THE LINE WINS OVER EVERY CELL (keku's boot 2026-09-19: the window still
   // showed "Camuflaje predeterminado" -- the cursor lands on the first cell as the screen opens and the cell's own
   // description overwrote the message written a moment earlier). A rule that has to win is written where the box is
   // filled, not before it.
   if(camoScreenHasNoModel() && info != undefined)
   {
      var line = camoNoModelText();
      if(line.length > 0)
      {
         if(info.showData1 != undefined)
         {
            info.showData1({Label:camoTitleText(), Description:line});
         }
         if(info.show != undefined)
         {
            info.show();
         }
         return undefined;
      }
   }
   if(info == undefined || index < 0 || index >= camoShown.length)
   {
      return undefined;
   }
   var item = camoItems[camoShown[index]];
   // ⛔ on an accessory screen every cell is a CAMO: the cell that means "no camo" shows the game's own no-camo text, not
   // the description of the accessory that item really is (keku, 2026-09-18: "aquí no debe decir la info del accesorio
   // sino del camo" -- the info box was showing "Mira rifle (6x)")
   var desc = camoIsNoCamo(item)
      ? {Type:1, Label:camoLocalise(camoDefaultLabel), Description:camoLocalise(camoDefaultDesc)}
      : camoInfoOf(item);
   if(info.showData1 != undefined)
   {
      info.showData1({Label:String(desc.Label), Description:desc.Description == undefined ? "" : String(desc.Description)});
   }
   if(info.show != undefined)
   {
      info.show();
   }
};
var camoHideInfo = function()
{
   var info = camoInfoClip();
   if(info != undefined && info.hide != undefined)
   {
      info.hide();
   }
};

// the family buttons: the one of the family picked shows selected (and on the soldier's window the tab picked, in the first row)
var camoMarkButtons = function()
{
   var i = 0;
   while(i < camoButtonNames.length)
   {
      var btn = camoButtonClip(i);
      if(btn != undefined)
      {
         btn.selected = i < camoTabCount ? i == camoTab : i - camoTabCount == camoFamily;
      }
      i = i + 1;
   }
};

// --- the keyboard cursor (see the header): where it is, published on the grid for anyone measuring from outside
var camoPublishCursor = function()
{
   var grid = camoGridClip();
   if(grid != undefined)
   {
      grid._camoZone = camoZone;
      grid._camoCursor = camoCursor;
      grid._camoFamily = camoFamily;
      grid._camoTab = camoTab;
   }
};
// a button's look under the cursor: the plain mouse-over look — also on the family picked, whose own overSelected look draws the same
// pixels as its selected look (measured: the cursor would not be seen there); off the cursor the button's normal look, which its
// state turns into the selected look on the family picked
var camoButtonLook = function(i, over)
{
   var btn = camoButtonClip(i);
   var mc = btn == undefined ? undefined : btn.buttonMc;
   if(mc == undefined || mc.setState == undefined)
   {
      return undefined;
   }
   if(over)
   {
      var wasSelected = mc._selected;
      mc._selected = false;
      mc.setState(mc.OVER);
      mc._selected = wasSelected;
   }
   else
   {
      mc.setState(mc.NORMAL);
   }
};
var camoShowCursor = function()
{
   if(camoZone == "buttons")
   {
      camoButtonLook(camoCursor, true);
   }
   camoPublishCursor();
};
var camoCursorToButton = function(i)
{
   if(camoZone == "buttons" && camoCursor != i)
   {
      camoButtonLook(camoCursor, false);
   }
   camoZone = "buttons";
   camoCursor = i;
   camoHideInfo();
   camoShowCursor();
};
var camoCursorToCell = function(index)
{
   var grid = camoGridClip();
   var n = grid == undefined || grid.gridData == undefined ? 0 : grid.gridData.length;
   if(n == 0)
   {
      return false;
   }
   if(camoZone == "buttons")
   {
      camoButtonLook(camoCursor, false);
   }
   camoZone = "grid";
   index = Math.max(0, Math.min(index, n - 1));
   grid.selectedIndex = index;
   if(grid.m_usingScrollbar && grid.adjustScrollPoss != undefined)
   {
      grid.adjustScrollPoss(index);
   }
   camoShowInfo(index);
   camoPublishCursor();
   return true;
};

// the grid shows the camos of the family picked: KitCell reads Label and ImagePath
// the plain accessory this screen is about (the cell that means "no camo on it")
var camoIsPlain = function(item)
{
   return camoBaseMode && item != undefined && item.Index != undefined && camoIdKey(item.Index) == camoBaseWanted
      && camoBaseOf(item) == "";
};

// ⛔ THE WEAPON'S SCREEN HAS ITS OWN "no camo", AND IT IS NOT THE ONE ABOVE (keku, 2026-09-20: *"lo de sin camuflaje
// añadelo tambien al apartado de camos de armas normal, no solo en accesorios"*). `camoIsPlain` asks whether an item
// is the PLAIN ACCESSORY of the row being camouflaged, which only exists on an accessory screen (camoBaseMode); on
// the weapon's camo screen there is nothing to compare against, so the camo-less entry is the game's own row, by
// identifier -- the pair baked into `_camoNoCamoIds` (DefaultCamo / NoCamo).
var camoNoCamoIds = {};
var camoNoCamoParts = String(_camoNoCamoIds).split(",");
var camoN = 0;
while(camoN < camoNoCamoParts.length)
{
   if(camoNoCamoParts[camoN].length > 0)
   {
      camoNoCamoIds[camoIdKey(camoNoCamoParts[camoN])] = true;
   }
   camoN = camoN + 1;
}

// an item that would draw as an empty box: no name and no picture (see camoRefresh)
var camoBlanksSaid = "";
var camoIsBlankItem = function(item)
{
   if(item == undefined)
   {
      return true;
   }
   var label = item.Label == undefined ? "" : String(item.Label);
   var image = item.ItemImage == undefined ? "" : String(item.ItemImage);
   return label == "" && (image == "" || image == "undefined" || image == "null");
};

var camoIsNoCamo = function(item)
{
   if(camoIsPlain(item))
   {
      return true;
   }
   // no mode test: on an accessory screen these ids are not among the row's unlocks anyway, and tying the
   // check to the mode is what kept the weapon's own screen from ever seeing its camo-less row
   return item != undefined && item.Index != undefined && camoNoCamoIds[camoIdKey(item.Index)] == true;
};

// the family a cell belongs to: the plain accessory goes where the weapon's own default camo goes -- the family with no
// keywords -- and everything else answers as it always did
var camoFamilyOfCell = function(item)
{
   if(camoIsPlain(item))
   {
      var f = 0;
      var i = 0;
      while(i < camoFamilies.length)
      {
         if(camoFamilies[i].keys.length == 0)
         {
            f = i;
         }
         i = i + 1;
      }
      return f;
   }
   return camoFamilyOf(item);
};

// ⛔ THE GRID'S SCROLL OUTLIVED ITS FAMILY (keku, 2026-09-28: *"cuando mueves la barra scroll vertical lateral y te mueves a otra
// pestaña de categoria camo, a veces aparecen vacias"*). The game's grid moves its cells with gridClips._y from its scrollbar
// (Grid.scrollBarUpdated), and its removeScrollbar() takes the bar away WITHOUT putting the cells back: a family that fits with no
// bar kept the last family's offset and drew its cells above the mask -- an empty grid until a family with a bar was scrolled
// again. So after every layout: no bar = the cells at the grid's start; with a bar, a new family or tab starts at the top (the worn
// cell then brought into view, as the game's own drawGrid does) and the same one keeps its place, clamped to its new length. The
// bar's setPercentage fires only on a change, so the percentage is set and applied here by hand.
var camoFixScroll = function(grid, sel)
{
   if(grid == undefined || grid.gridClips == undefined)
   {
      return undefined;
   }
   var start = grid.gridStartPoss == undefined ? 0 : grid.gridStartPoss;
   var key = camoFamily + "/" + camoTab;
   var changed = grid._camoScrollKey != key;
   grid._camoScrollKey = key;
   var bar = grid.m_scrollbar;
   if(!grid.m_usingScrollbar || bar == undefined)
   {
      grid.gridClips._y = start;
      return undefined;
   }
   var p = changed || bar.m_percentage == undefined ? 0 : Math.max(0, Math.min(100, bar.m_percentage));
   bar.m_percentage = p;
   if(bar.updateBarPosition != undefined)
   {
      bar.updateBarPosition();
   }
   if(grid.scrollBarUpdated != undefined)
   {
      grid.scrollBarUpdated(p);
   }
   if(changed && sel >= 0 && grid.adjustScrollPoss != undefined)
   {
      grid.adjustScrollPoss(sel);
   }
};

var camoRefresh = function()
{
   var grid = camoGridClip();
   if(grid == undefined)
   {
      return undefined;
   }
   camoShown = [];
   var cells = [];
   // ⭐ "SIN CAMUFLAJE" IS IN EVERY CATEGORY, AND FIRST (keku, 2026-09-20: *"haz que en todas las categorias la
   // casilla de camuflaje predeterminado esté el primero, para que el usuario no tenga que ir a MISC para
   // ponerlo"*). Taking the camo OFF is not a camo of a family: it is what every family needs at hand, so it is
   // kept whatever button is picked and put at the head of the list instead of wherever its own family put it.
   var plainAt = -1;
   var plainCell = undefined;
   var blanks = "";
   var i = 0;
   while(i < camoItems.length)
   {
      var item = camoItems[i];
      // the accessory screen keeps what belongs to the accessory worn; the camo screen keeps the family picked.
      // camoBaseWanted "" = no table says which camo belongs to which accessory (nothing installed yet): then the screen
      // shows the row whole rather than one lonely cell.
      var plain = camoIsNoCamo(item);
      var keep = plain || camoFamilyOfCell(item) == camoFamily;
      // ⛔ A CELL WITH NO NAME AND NO PICTURE IS NOT A CAMO (keku 2026-09-29, a photo of the M416's MISC: "hay un hueco en
      // blanco que no sé lo que es"). It is an unlock the client's description index never knew -- the same empty box the
      // accessory rows already leave out (camoCollapseAccessoryItems) -- and the player can do nothing with it. Left out
      // here too, and its identifier said once (GBL), which is the only thing that names it.
      if(keep && !plain && camoIsBlankItem(item))
      {
         keep = false;
         blanks = blanks + "," + camoIdKey(item.Index);
      }
      if(camoBaseMode && camoBaseWanted != "")
      {
         keep = keep && camoFamilyBaseOf(item) == camoBaseWanted;
      }
      if(keep)
      {
         if(plain)
         {
            plainAt = i;
            plainCell = {Label:camoLocalise(camoDefaultLabel), ImagePath:camoDefaultImage, Index:item.Index, IsExcluded:false};
         }
         else
         {
            camoShown.push(i);
            cells.push({Label:camoCellLabel(item), ImagePath:String(item.ItemImage), Index:item.Index, IsExcluded:false});
         }
      }
      i = i + 1;
   }
   // at the head, and the two lists stay in step (camoShown maps a cell back to its item)
   if(plainCell != undefined)
   {
      camoShown.unshift(plainAt);
      cells.unshift(plainCell);
   }
   if(blanks != "" && blanks != camoBlanksSaid)
   {
      camoBlanksSaid = blanks;
      camoSignal("GBL" + camoFamily + blanks);
   }
   if(Widget.Grid.Grid != undefined && Widget.Grid.Grid.prototype != undefined && Widget.Grid.Grid.prototype.updateGridItemsData != undefined)
   {
      Widget.Grid.Grid.prototype.updateGridItemsData.call(grid, cells);
   }
   // ⛔ AN ATTACHMENT THAT DRAWS NOTHING SAYS SO (keku 2026-09-19). The framework marks those in its table ("X"), because
   // only the weapon's data knows it: the M240 has no iron sights, a heavy barrel has no socket, the "No…" rows are
   // placeholders. Without this the window opens on a piece-shaped hole, which reads as a camo that failed. The line goes in
   // the info box, which is the screen's own place for a sentence, and stays up (there is nothing to hover).
   if(camoScreenHasNoModel())
   {
      var info = camoInfoClip();
      var line = camoNoModelText();
      if(info != undefined && line.length > 0)
      {
         if(info.showData1 != undefined)
         {
            info.showData1({Label:camoTitleText(), Description:line});
         }
         if(info.show != undefined)
         {
            info.show();
         }
      }
   }
   // the cells, two to a row on the family buttons' grid: each scaled to a button's width (a camo band is a 256x64 picture; at the
   // game's 113x47 kit cell it reads as a stamp), the buttons' gap between columns and between rows — laid out here over the grid's
   // own cells, the grid's columns/rows/rowHeight told the layout (rowHeight = cell + gap), its mask and its scrollbar set up again
   // over it — so its selection, scrolling and keyboard code keep working
   if(grid.gridClips != undefined && grid.gridData != undefined && grid.gridData.length > 0)
   {
      var cw = Widget.Grid.Cell.KitCell.KIT_CELL_WIDTH * camoCellScale / 100;
      var ch = Widget.Grid.Cell.KitCell.KIT_CELL_HEIGHT * camoCellScale / 100 + camoCellGap;
      var n = grid.gridData.length;
      var c = 0;
      while(c < n)
      {
         var cell = grid.gridClips[grid.getCellName(c)];
         if(cell != undefined)
         {
            cell._xscale = camoCellScale;
            cell._yscale = camoCellScale;
            cell._x = (c % camoCellColumns) * (cw + camoCellGap);
            cell._y = Math.floor(c / camoCellColumns) * ch;
            if(cell.m_image != undefined)
            {
               cell.m_image.resizeToTarget = true;
            }
            if(cell.loadContent != undefined && cell.loadContent._camoWrap != true)
            {
               cell.loadContent = camoMakeCellLoad(cell.loadContent);
            }
         }
         c = c + 1;
      }
      // the plates, once the cells are where they belong: a rectangle under each, on their own parent
      camoDrawPlates(grid, n, cw, ch - camoCellGap);
      grid.columns = camoCellColumns;
      grid.rows = Math.ceil(n / camoCellColumns);
      grid.rowHeight = ch;
      var panelWidth = camoCellColumns * cw + (camoCellColumns - 1) * camoCellGap;
      // the mask a gap wider and taller than the cells: a cell's highlight frame (its corner ticks) is drawn a pixel past the cell on the
      // right and the bottom (KitCell: 114 x 48 over a 113 x 47 cell, ~4 px at this scale) and the mask cut it on the second column and
      // the last row (keku 2026-09-17: "se cortan por muy poco, no tienen los picos en las esquinas"); a gap below is still short of the
      // next row (rows are a cell plus a gap apart), so nothing of a hidden row shows
      grid.setupMask(panelWidth + camoCellGap);
      if(grid.mask != undefined)
      {
         grid.mask._height = grid.gridHeight + camoCellGap;
      }
      if(grid.rows * ch - camoCellGap > grid.gridHeight)
      {
         grid.addScrollbar(panelWidth + camoCellGap + 14, grid.rows, ch);
      }
      else
      {
         grid.removeScrollbar();
      }
   }
   // a grid whose family has no camos draws nothing and keeps the old cells: clear them
   // ⛔ AND THE PLATES WITH THEM (keku, 2026-09-20, with SNOW empty: *"aparecen los fondos grises sin haber
   // camos"*). The plates are drawn where the cells are laid out, and that block only runs when there ARE cells,
   // so an empty family kept the rectangles of the family before it -- four grey boxes with nothing in them.
   if(cells.length == 0)
   {
      camoDrawPlates(grid, 0, 1, 1);
      // and the last family's scrollbar: nothing to scroll (the grid's own drawGrid returns early with no data)
      if(grid.removeScrollbar != undefined)
      {
         grid.removeScrollbar();
      }
   }
   if(cells.length == 0 && grid.gridClips != undefined)
   {
      for(var name in grid.gridClips)
      {
         if(typeof grid.gridClips[name] == "movieclip")
         {
            grid.gridClips[name].removeMovieClip();
         }
      }
   }
   // the camo the weapon wears, highlighted in the grid
   var sel = -1;
   var s = 0;
   while(s < camoShown.length)
   {
      if(camoItems[camoShown[s]].DefaultSelected == true)
      {
         sel = s;
      }
      s = s + 1;
   }
   if(sel >= 0 && grid.gridData != undefined && grid.gridData.length > sel)
   {
      grid.selectedIndex = sel;
   }
   camoFixScroll(grid, sel);
   camoMarkButtons();
   camoParkInfo();
   camoShowCursor();
};

// the accessory worn now: the item the engine marks as selected, resolved to the plain accessory it belongs to (a camo of the
// ACOG answers "the ACOG"). "" when nothing is marked, and then the screen shows nothing rather than everything.
var camoBaseOfSelected = function()
{
   // with no entry naming a base, nothing here is a camo of anything: the screen shows the row whole -- except on a window that
   // is about the item WORN whatever the table says (_camoBaseOwn, the pistol's: keku 2026-09-18 on the accessory windows, "lo has
   // entendido mal", the window is the camos of THAT piece, never the row's other pieces)
   var any = camoBaseOwn;
   var i = 0;
   while(i < camoItems.length)
   {
      if(camoBaseOf(camoItems[i]) != "")
      {
         any = true;
      }
      i = i + 1;
   }
   if(!any)
   {
      return "";
   }
   i = 0;
   while(i < camoItems.length)
   {
      if(camoItems[i].DefaultSelected == true)
      {
         return camoFamilyBaseOf(camoItems[i]);
      }
      i = i + 1;
   }
   return "";
};

// the family of the camo the weapon wears now (the screen opens on it)
// ⛔ WHICH CELL THE WEAPON IS ALREADY WEARING (keku, 2026-09-21: *"ya se guarda el accesorio pero no se guarda el camo
// seleccionado"* — the accessory came back painted, but reopening its window left every cell unmarked). The window used
// to put the cursor on cell 0, which is "Sin camuflaje", so a player who had picked a camo was shown the one choice he
// had NOT made. Two ways of knowing, in order: the item the ENGINE marks, and -- when it marks none -- the identifier
// the ROW opened this window with, which is the twin the weapon wears. -1 = neither, and only then does cell 0 stand.
var camoCellOfWorn = function()
{
   var c = 0;
   while(c < camoShown.length)
   {
      var item = camoItems[camoShown[c]];
      if(item != undefined && item.DefaultSelected == true)
      {
         return c;
      }
      c = c + 1;
   }

   if(_global._camoOpenBase != undefined && String(_global._camoOpenSlot) == String(_camoSlot))
   {
      var wanted = camoIdKey(_global._camoOpenBase);
      c = 0;
      while(c < camoShown.length)
      {
         var byId = camoItems[camoShown[c]];
         if(byId != undefined && byId.Index != undefined && camoIdKey(byId.Index) == wanted)
         {
            return c;
         }
         c = c + 1;
      }
   }

   return -1;
};

var camoFamilyOfSelected = function()
{
   var i = 0;
   while(i < camoItems.length)
   {
      if(camoItems[i].DefaultSelected == true)
      {
         return camoFamilyOf(camoItems[i]);
      }
      i = i + 1;
   }
   return 0;
};

// the wheel over the grid: its scrollbar a row per notch (delta > 0 = up), through the bar's own setPercentage so the cells follow
var camoMakeWheel = function(grid)
{
   return function(delta)
   {
      var bar = grid.m_scrollbar;
      // for the recorder, when one rides: every wheel event that reaches the movie (whether the grid can scroll or not)
      if(camoTimeline.rueNote != undefined)
      {
         camoTimeline.rueNote("CamoGrid", "camoWheel", [String(delta), String(grid.m_usingScrollbar), String(bar == undefined ? "nobar" : bar.m_percentage)], undefined);
      }
      if(bar == undefined || !grid.m_usingScrollbar || bar.targetSize == undefined || bar.targetSize <= bar.size)
      {
         return undefined;
      }
      var step = grid.rowHeight / (bar.targetSize - bar.size) * 100;
      var notches = delta > 0 ? -1 : 1;
      bar.setPercentage(bar.m_percentage + notches * step, true);
      grid._camoWheel = (grid._camoWheel == undefined ? 0 : grid._camoWheel) + 1;
   };
};

// the drag over the 3D half: the press and the release go to the client's Lua (camoSignal "WV1" / "WV0"), which turns the
// mannequin with the cursor (CamoWeaponView.lua); a press left of camoDragLeft (the root sprite's own stage x) is the panel's, not a
// drag. The listener hears
// every press of the movie (Mouse.addListener), so a release anywhere ends the drag; the grid carries it and takes it off on unload
var camoMakeDrag = function(grid)
{
   var listener = new Object();
   // with the game's gfxExtensions on, the listener is told which button (a code the Lua logs: whether the engine hands the
   // movie the right button decides if it can turn the weapon too)
   listener.onMouseDown = function(button, mouseIndex)
   {
      if(camoScreen._xmouse < camoDragLeft)
      {
         return undefined;
      }
      grid._camoDragOn = true;
      grid._camoDrag = (grid._camoDrag == undefined ? 0 : grid._camoDrag) + 1;
      camoSignal("WV1" + (button == undefined ? "" : String(button)));
   };
   listener.onMouseUp = function()
   {
      if(grid._camoDragOn != true)
      {
         return undefined;
      }
      grid._camoDragOn = false;
      camoSignal("WV0");
   };
   return listener;
};

// a cell picked: the grid's Released event with the camo's IDENTIFIER (the graph wires it to SetAccessory4 → AccessoryChanged)
var camoPick = function(index)
{
   var grid = camoGridClip();
   if(grid == undefined || index < 0 || index >= camoShown.length)
   {
      return undefined;
   }
   var item = camoItems[camoShown[index]];
   grid.playSound(fb.Defines.SOUND_SELECT);
   if(camoSoldierMode)
   {
      // the soldier's window: the look is this soldier's for the tab's part (all three on CONJUNTO) and goes to the client's Lua,
      // "SKN<part>,<id>,<soldier>"; the game's own look picked on CONJUNTO takes every pick of ours off ("SKNall,0,<soldier>").
      // Marked here at once; the grid's Released only carries the glitch out (SkinPicked).
      var part = camoTabParts[camoTab];
      var id = camoIdKey(item.Index);
      var clear = part == "all" && item._camoSkin != true && item._camoBase == true;
      var skinSignal = "SKN" + part + "," + (clear ? "0" : id) + "," + camoSoldierKey;
      _global._camoLastPickSignal = skinSignal;
      camoSignal(skinSignal);
      if(camoSkinWorn[camoSoldierKey] == undefined)
      {
         camoSkinWorn[camoSoldierKey] = {};
      }
      var w = camoSkinWorn[camoSoldierKey];
      if(part == "all")
      {
         if(clear)
         {
            delete w.head;
            delete w.torso;
            delete w.legs;
         }
         else
         {
            w.head = id;
            w.torso = id;
            w.legs = id;
         }
      }
      else
      {
         w[part] = id;
      }
      grid.fireEvent(Widget.Grid.Grid.RELEASED, item.Index);
      grid._camoPicked = item.Index;
      camoSoldierRebuild(false);
      return undefined;
   }
   // on an accessory screen the pick is what that ROW must wear when we go back: the row rebuilds itself from the stored
   // value, which has not caught up yet, so it reads this on its way in (CamoRow.camoWantedFor)
   if(camoBaseMode)
   {
      // unsigned, always: these travel to another movie and into the table's keys (see camoIdKey). The value handed to the
      // engine's own event below stays exactly as the widget gave it.
      _global._camoPicked = camoIdKey(item.Index);
      _global._camoPickedSlot = String(_camoSlot);
      _global._camoOpenBase = camoIdKey(item.Index);
   }
   // the client's Lua repaints what it shows: the weapon ("WVC"), or the vehicle on a vehicle window ("VVC")
   // a vehicle window also says its CLASS ("VVC<id>,<class SID>"): the camo is chosen for the class, whichever vehicle of it
   var pickSignal = camoVehicleMode ? ("VVC" + camoIdKey(item.Index) + "," + camoVehicleClassSid()) : ("WVC" + camoIdKey(item.Index));
   _global._camoLastPickSignal = pickSignal;   // kept apart: a vehicle pick's respawn signals right after it
   camoSignal(pickSignal);
   grid.fireEvent(Widget.Grid.Grid.RELEASED, item.Index);
   grid._camoPicked = item.Index;
   camoProbePick(item.Index);
   if(camoVehicleMode)
   {
      // a vehicle window: the pick is what this class wears from now on -- marked here at once (the Lua points the class's
      // vehicles at it, the pick's chain takes the ShowRoom's vehicle away and the respawn below brings it back wearing it)
      if(camoRespawnTimer != undefined)
      {
         clearInterval(camoRespawnTimer);
         camoRespawnTimer = undefined;
      }
      camoRespawnStep = 0;
      var firstWait = Number(camoRespawnSteps[0]);
      if(!_global.isNaN(firstWait) && firstWait >= 0)
      {
         // ⛔ never `camoMakeRespawn(grid)()`: that form compiled to nothing here (the seam: no ToggleOn after a pick)
         camoRespawnTimer = setInterval(camoMakeRespawn(grid), firstWait < 1 ? 1 : firstWait);
      }
      for(var worn in camoVehicleWornBy)
      {
         if(camoVehicleClassIs(worn))
         {
            delete camoVehicleWornBy[worn];
         }
      }
      if(!camoIsNoCamo(item) && camoVehicleClass != "")
      {
         camoVehicleWornBy[camoVehicleClassSid()] = camoIdKey(item.Index);
      }
      camoVehicleCursorSet = true;
      camoItems = camoVehicleItems(camoItems0 == null ? [] : camoRewriteItems(camoItems0));
      camoRefresh();
   }
};
// a probe for the recorder, when one rides on this timeline (its rueNote is a variable of the same frame): what the engine's
// component holds for the camo (CustSelectedAccessory4) right after the pick and half a second later — whether the pick reached
// the graph's SetAccessory4 at all, apart from whether the weapon repaints
var camoProbeKey = -991137947;   // UICustomizationComp.CustSelectedAccessory4
var camoProbeTimer = undefined;
var camoProbeRead = function(when, wanted)
{
   var held = undefined;
   if(_global.Data != undefined && _global.Data.UIDataInterfaceComp != undefined)
   {
      held = _global.Data.UIDataInterfaceComp.getData(camoProbeKey);
   }
   if(camoTimeline.rueNote != undefined)
   {
      camoTimeline.rueNote("CamoGrid", "camoProbe", [when, String(wanted), String(held)], undefined);
   }
};
var camoMakeProbeLater = function(wanted)
{
   return function()
   {
      clearInterval(camoProbeTimer);
      camoProbeTimer = undefined;
      camoProbeRead("later", wanted);
   };
};
var camoProbePick = function(wanted)
{
   camoProbeRead("now", wanted);
   if(camoProbeTimer != undefined)
   {
      clearInterval(camoProbeTimer);
   }
   camoProbeTimer = setInterval(camoMakeProbeLater(wanted), 500);
};

// an arrow: buttons — two columns, Up/Down a row, Down out of the last row into the grid (the same column); grid — its own
// moveSelection (bounds, the scrollbar following the cell), Up out of its first row back to the buttons' last row
var camoNavigate = function(concept)
{
   if(camoZone == "")
   {
      if(camoBaseMode)
      {
         camoCursorToCell(0);
      }
      else
      {
         camoCursorToButton(camoHomeButton());
      }
      return undefined;
   }
   var buttonRows = Math.ceil(camoButtonNames.length / camoButtonColumns);
   if(camoZone == "buttons")
   {
      var col = camoCursor % camoButtonColumns;
      var rowIdx = Math.floor(camoCursor / camoButtonColumns);
      if(concept == fb.Input.InputConceptActions.Action_NavigateUp)
      {
         if(rowIdx > 0)
         {
            camoCursorToButton(camoCursor - camoButtonColumns);
         }
      }
      else if(concept == fb.Input.InputConceptActions.Action_NavigateDown)
      {
         if(rowIdx + 1 < buttonRows && camoCursor + camoButtonColumns < camoButtonNames.length)
         {
            camoCursorToButton(camoCursor + camoButtonColumns);
         }
         else
         {
            camoCursorToCell(col);
         }
      }
      else if(concept == fb.Input.InputConceptActions.Action_NavigateLeft)
      {
         if(col > 0)
         {
            camoCursorToButton(camoCursor - 1);
         }
      }
      else if(concept == fb.Input.InputConceptActions.Action_NavigateRight)
      {
         if(col + 1 < camoButtonColumns && camoCursor + 1 < camoButtonNames.length)
         {
            camoCursorToButton(camoCursor + 1);
         }
      }
      return undefined;
   }
   var grid = camoGridClip();
   if(grid == undefined)
   {
      return undefined;
   }
   var index = grid.selectedIndex;
   // with no family buttons there is nowhere above the grid to go: the top row stays put
   if(concept == fb.Input.InputConceptActions.Action_NavigateUp && index < grid.columns && camoButtonNames.length > 0)
   {
      camoCursorToButton((buttonRows - 1) * camoButtonColumns + Math.min(index % grid.columns, camoButtonColumns - 1));
      return undefined;
   }
   if(grid.moveSelection != undefined)
   {
      grid.moveSelection(concept);
   }
   camoShowInfo(grid.selectedIndex);
   camoPublishCursor();
};
// Activate: on a button its own release (the family picked, the cursor stays on it); on a cell the camo picked; no cursor yet: the cursor shows
var camoActivate = function()
{
   if(camoZone == "buttons")
   {
      var btn = camoButtonClip(camoCursor);
      if(btn != undefined && btn.buttonReleased != undefined)
      {
         btn.buttonReleased();
      }
      camoShowCursor();
   }
   else if(camoZone == "grid")
   {
      var grid = camoGridClip();
      camoPick(grid == undefined ? -1 : grid.selectedIndex);
   }
   else if(camoBaseMode)
   {
      camoCursorToCell(0);
   }
   else
   {
      camoCursorToButton(camoHomeButton());
   }
};

// ⭐ WHAT A PISTOL WEARS IS THE SERVER'S, NOT THE GAME'S (2026-10-06): the game never stores a camo copy as the player's pistol
// (keku's run 353), so the pistol's camo is kept by the server and handed at the deploy, the vehicle windows' way -- and the cell
// the window marks as worn is the one the table's "SA~<pistol>~<copy>" names for the pistol this window is about (no record: the
// pistol itself, as the game marks it). Returns the cell's id, or "".
var camoMarkSidearmWorn = function()
{
   if(!camoBaseMode || !camoBaseOwn || camoBaseWanted == "" || camoSidearmWorn[camoBaseWanted] == undefined)
   {
      return "";
   }
   var worn = camoSidearmWorn[camoBaseWanted];
   var found = false;
   var k = 0;
   while(k < camoItems.length)
   {
      if(camoItems[k].Index != undefined && camoIdKey(camoItems[k].Index) == worn)
      {
         found = true;
      }
      k = k + 1;
   }
   if(!found)
   {
      return "";
   }
   k = 0;
   while(k < camoItems.length)
   {
      camoItems[k].DefaultSelected = camoItems[k].Index != undefined && camoIdKey(camoItems[k].Index) == worn;
      k = k + 1;
   }
   return worn;
};

// --- the grid: the camo row's payload {Items, ItemCategory, ShowStepPlus} comes in through the binding as GridItems
var camoGrid = camoGridClip();
if(camoGrid != undefined)
{
   // ⭐ THE MAILBOX (see CamoCommon): the table the mod writes into our own data key arrives here by its binding's
   // name, exactly like the row's items do. Installed BEFORE the items handler so a table that comes with the first
   // refresh is already parsed when the cells are named.
   // ⛔ AND THE SAME NET THE ROW HAS (2026-09-20): if the table lands AFTER the cells were named, everything the baked
   // table did not know would sit there nameless for good -- which is exactly the state the baked table used to hide,
   // and what emptying it would expose. The cells are named again from the payload as it came, and the line says
   // whether that happened: "MGR<entries>,<new>,<delivered>,<redrawn>". `delivered` is in it because entries alone
   // cannot tell "the whole table arrived" from "nothing arrived and they were already baked".
   camoGrid.updateCamoTableData = function(data)
   {
      if(camoSoldierMode)
      {
         // the soldier's window: the table is the level's pack, every skin (for which soldier, which parts) and what each soldier
         // wears on each part -- an earlier delivery's skins and marks go first, then the list is made again for this soldier and
         // tab; the receipt "STB<skins of this soldier>,<soldier>,<head>/<torso>/<legs>"
         for(var oldSkin in camoTable)
         {
            if(camoTable[oldSkin] != undefined && camoTable[oldSkin].soldier != undefined)
            {
               delete camoTable[oldSkin];
            }
         }
         camoSkinWorn = {};
         camoRowLook = {};
         camoMailbox(data);
         camoSoldierRebuild(camoZone == "" || !camoSoldierTableIn);
         camoSoldierTableIn = true;
         var mine = 0;
         for(var sk in camoTable)
         {
            if(camoTable[sk].soldier != undefined && String(camoTable[sk].soldier) == camoSoldierKey)
            {
               mine = mine + 1;
            }
         }
         var wornNow = camoSkinWorn[camoSoldierKey];
         camoSignal("STB" + mine + "," + camoSoldierKey + "," + (wornNow == undefined ? "-/-/-" :
            (wornNow.head == undefined ? "-" : wornNow.head) + "/" + (wornNow.torso == undefined ? "-" : wornNow.torso) + "/" +
            (wornNow.legs == undefined ? "-" : wornNow.legs)));
         return undefined;
      }
      if(camoVehicleMode)
      {
         // a vehicle window: the table is every vehicle camo and what each class wears (see camoVehicleItems) -- what an earlier
         // delivery said goes first, then the list is made again for this window's class
         for(var old in camoTable)
         {
            if(camoTable[old] != undefined && String(camoTable[old].base).indexOf("@V") == 0)
            {
               delete camoTable[old];
            }
         }
         camoVehicleWornBy = {};
         camoMailbox(data);
         camoVehicleTableIn = true;
         camoVehicleRebuild();
         return undefined;
      }
      var got = camoMailText(data);
      var before = 0;
      for(var b in camoTable)
      {
         before = before + 1;
      }
      camoMailbox(data);
      var after = 0;
      for(var a in camoTable)
      {
         after = after + 1;
      }
      var redrawn = 0;
      var markedWorn = "";
      if(after > before && camoItems0 != null)
      {
         camoItems = camoRewriteItems(camoItems0);
         markedWorn = camoMarkSidearmWorn();
         camoRefresh();
         redrawn = 1;
      }
      else
      {
         // (a pistol's window whose table only said again what it wears: the mark moves, nothing else)
         markedWorn = camoMarkSidearmWorn();
         if(markedWorn != "")
         {
            camoRefresh();
            redrawn = 2;
         }
      }
      if(markedWorn != "")
      {
         // and the view builds the pistol with that camo (the line the window sends when its items come in, see below)
         camoSignal("WVA" + String(_camoSlot) + "," + markedWorn);
      }
      camoSignal("MGR" + after + "," + (after - before) + "," + got.text.length + "," + redrawn);
   };
   camoWatchRefresh(camoGrid);

   camoGrid.updateGridItemsData = function(data)
   {
      var first = camoItems.length == 0;
      if(data != undefined && data.Items != undefined)
      {
         // kept AS IT CAME, so a table delivered after the cells were named can name them again
         camoItems0 = fb.Base.UIBase.ObjectToArray(data.Items);
         camoItems = camoRewriteItems(data.Items);
      }
      else if(data != undefined)
      {
         camoItems0 = fb.Base.UIBase.ObjectToArray(data);
         camoItems = camoRewriteItems(data);
      }
      else
      {
         camoItems0 = null;
         camoItems = [];
      }
      if(camoVehicleMode)
      {
         // "VGR<cells>,<delivered>": the vehicle window's list -- its own cell plus what the engine delivered (0 today)
         camoItems = camoVehicleItems(camoItems);
         camoSignal("VGR" + camoItems.length + "," + (camoItems0 == null ? 0 : camoItems0.length));
      }
      if(camoSoldierMode)
      {
         // "SGR<game's looks>,<cells of the tab>,<soldier>": the kit's rows (the row of ours and any nameless one left out) and the
         // tab's cells made of them and the mailbox's skins
         camoStock = camoSoldierStock(camoItems);
         camoItems = camoSoldierItems();
         camoSignal("SGR" + camoStock.length + "," + camoItems.length + "," + camoSoldierKey);
      }
      if(first || camoZone == "")
      {
         camoFamily = camoFamilyOfSelected();
         // ⛔ REVERTED 2026-09-21 with the store wiring: an "FSL<family>,<cells>,<selected>" probe stood here, and it
         // shipped in the SAME build as the door stores -- the build whose screens came up blank. Two new things on
         // one screen measure nothing, so both went back; this one returns alone once the screen is known good.
      }
      if(camoBaseMode)
      {
         // which accessory this screen is about: the one the row has on, every time data comes (a pick re-sends it)
         camoBaseWanted = camoBaseOfSelected();
         // the row that opened this screen said which accessory it was showing; it is AHEAD of what the engine has stored
         // (the store happens on the way in), so it wins. A camo of an accessory answers with the accessory itself.
         if(_global._camoOpenBase != undefined && String(_global._camoOpenSlot) == String(_camoSlot))
         {
            var fromRow = camoIdKey(_global._camoOpenBase);
            var rowEntry = camoTable[fromRow];
            var rowBase = rowEntry != undefined && rowEntry.base != undefined && String(rowEntry.base) != "" ? String(rowEntry.base) : fromRow;
            if(rowBase != "" && rowBase != "0" && rowBase != "undefined")
            {
               camoBaseWanted = rowBase;
            }
         }
         // a pistol's window: the camo the server keeps for it is the cell worn (see camoMarkSidearmWorn)
         camoMarkSidearmWorn();
         // ⛔⛔ WHAT THE VIEW IS TOLD IS WHAT IS **WORN**, NOT WHAT THIS WINDOW IS ABOUT (keku, 2026-09-21, after the
         // receipts had cleared the cursor and the hover: *"aun no se guarda"*). The two are different things and they
         // were sharing one value: `camoBaseWanted` is the BASE accessory, which is what filters the cells (this window
         // lists the camos OF the PK-A) -- and the client's Lua takes this line as "the piece the player wears" and
         // rebuilds the weapon with it. So a player who had picked a camo got the window's 3D showing the PLAIN piece:
         // the camo looked lost, although the row, the store and the cell cursor all had it right. His own log said it
         // three boots in a row and I read past it: `the screen says row 1 wears unlock 443288379 (the row read
         // 3325141568)` -- the twin going in, the base coming out.
         // The worn one, in order: the item the engine marks, then the identifier the row opened this window with.
         var wornId = camoBaseWanted;
         var w = 0;
         while(w < camoItems.length)
         {
            if(camoItems[w].DefaultSelected == true && camoItems[w].Index != undefined)
            {
               wornId = camoIdKey(camoItems[w].Index);
               w = camoItems.length;
            }
            w = w + 1;
         }
         if(wornId == camoBaseWanted && _global._camoOpenBase != undefined &&
            String(_global._camoOpenSlot) == String(_camoSlot))
         {
            wornId = camoIdKey(_global._camoOpenBase);
         }
         camoSignal("WVA" + String(_camoSlot) + "," + String(wornId));
      }
      camoRefresh();
      // the cursor where the screen opens: on the family worn, or -- with no buttons -- on the CAMO worn
      if(camoZone == "")
      {
         if(camoBaseMode)
         {
            // the receipt says which cell and how it was found, because "it opened on Sin camuflaje" has two
            // causes that look the same: nothing came marked, or the one marked is not in this window's cells
            var worn = camoCellOfWorn();
            camoOpenCell = worn < 0 ? 0 : worn;
            camoOpenedAt = getTimer();
            camoSignal("FSL" + camoShown.length + "," + worn + "," +
               (_global._camoOpenBase == undefined ? "none" : String(camoIdKey(_global._camoOpenBase))));
            camoCursorToCell(camoOpenCell);
         }
         else
         {
            camoCursorToButton(camoHomeButton());
         }
      }
   };
   // the mouse on a cell: over/out feed the info box; a release picks the camo (with its identifier — the widget's own release
   // fires the cell's index, which is not what the graph's SetAccessory4 wants) and puts the cursor there
   var camoGridMouse = camoGrid.cellMouseStateHandler;
   camoGrid.cellMouseStateHandler = function(event, index)
   {
      if(event == Widget.Grid.Cell.BaseCell.MOUSE_EVENT_RELEASE)
      {
         if(camoBaseMode && getTimer() - camoOpenedAt < camoOpenGuardMs)
         {
            // the click that opened this screen, still coming up: not a pick
            return undefined;
         }
         if(camoZone == "buttons")
         {
            camoButtonLook(camoCursor, false);
         }
         camoZone = "grid";
         this.selectedIndex = index;
         camoPublishCursor();
         camoPick(index);
         return undefined;
      }
      camoGridMouse.call(this, event, index);
      if(event == Widget.Grid.Cell.BaseCell.MOUSE_EVENT_OVER)
      {
         // ⛔⛔ A HOVER THAT NOBODY MADE STEALS THE OPENING SELECTION (keku, 2026-09-21: *"sigue igual"*, after the
         // receipt proved the cursor DID open on the right cell -- `FSL2,1,3325141568`). The grid's own handler moves
         // the selection on MOUSE_OVER (Grid.as: `if(index != this._selectedIndex …) this.selectedIndex = index`),
         // and an accessory window puts its grid exactly where the row the player just clicked was, so the pointer is
         // already sitting on a cell when the screen appears: the widget fires OVER without the mouse having moved and
         // the camo worn stops being the one shown. Same instant the RELEASE guard above covers, same reason -- this
         // is its other half. Once the player really moves the pointer, the hover rules again.
         if(camoBaseMode && camoOpenCell >= 0 && getTimer() - camoOpenedAt < camoOpenGuardMs &&
            index != camoOpenCell)
         {
            this.selectedIndex = camoOpenCell;
            if(camoOpenStolen != 1)
            {
               camoOpenStolen = 1;
               camoSignal("FSH" + camoOpenCell + "," + index);
            }
         }
         camoShowInfo(index);
      }
      else if(event == Widget.Grid.Cell.BaseCell.MOUSE_EVENT_OUT)
      {
         camoHideInfo();
      }
   };
   // the mouse wheel: a row per notch on the grid's own scrollbar (the game's grids have no wheel: keku 2026-09-17, "la rueda del ratón
   // no mueve la barra"); the widget's own helper puts one Mouse listener on the grid, taken off when the screen unloads
   camoGrid.addMouseWheelListener(camoMakeWheel(camoGrid));
   // the drag over the weapon (press / release to Lua)
   camoGrid._camoDragListener = camoMakeDrag(camoGrid);
   Mouse.addListener(camoGrid._camoDragListener);
   var camoGridUnload = camoGrid.onClipUnload;
   camoGrid.onClipUnload = function()
   {
      // a window closed before its vehicle was taken away must not take away the vehicle of the screen it went back to
      if(camoHideTimer != undefined)
      {
         clearInterval(camoHideTimer);
      }
      // a respawn still pending when the window goes: its way back spawns the vehicle already (StoreVehicleAccessories →
      // SpawnVehicle), and this grid is gone
      if(camoRespawnTimer != undefined)
      {
         clearInterval(camoRespawnTimer);
         camoRespawnTimer = undefined;
      }
      if(this.m_mouseListener != undefined)
      {
         Mouse.removeListener(this.m_mouseListener);
         this.m_mouseListener = undefined;
      }
      if(this._camoDragListener != undefined)
      {
         if(this._camoDragOn == true)
         {
            this._camoDragOn = false;
            camoSignal("WV0");
         }
         Mouse.removeListener(this._camoDragListener);
         this._camoDragListener = undefined;
      }
      if(camoGridUnload != undefined)
      {
         camoGridUnload.call(this);
      }
   };
   // the keys: the grid is the focused widget of the screen, so the engine hands it the concepts — the arrows move the cursor
   // (over the buttons or the cells), Activate picks; the widget's own key code is not used (it fires the cell's index)
   camoGrid.onInputConceptPressed = function(concept)
   {
      if(concept == fb.Input.InputConceptActions.Action_NavigateUp || concept == fb.Input.InputConceptActions.Action_NavigateDown || concept == fb.Input.InputConceptActions.Action_NavigateLeft || concept == fb.Input.InputConceptActions.Action_NavigateRight)
      {
         camoNavigate(concept);
      }
   };
   camoGrid.onInputConceptReleased = function(concept)
   {
      if(concept == fb.Input.InputConceptActions.Action_Activate)
      {
         camoActivate();
      }
   };
   // a vehicle window's own cell does not wait for data that never comes: once the grid is set up (its cell type known),
   // the list is made -- the binding may still deliver later, and then the list is made again with that in it
   if(camoVehicleMode)
   {
      var camoGridInit = camoGrid.initialize;
      camoGrid.initialize = function(initData)
      {
         camoGridInit.call(this, initData);
         if(camoItems.length == 0)
         {
            this.updateGridItemsData(undefined);
         }
         // the game's vehicle goes away once the view has read it (see camoMakeHide)
         if(camoHideTimer == undefined && camoHideMs >= 0)
         {
            camoHideTimer = setInterval(camoMakeHide(this), camoHideMs);
         }

      };
   }
}

// --- a vehicle window's CLASS: its name field's binding (UIKitComp.SelectedVehicleName, the class SID) arrives the instant the
// window opens; the list is made again for it. Read as the engine hands it to the field (refresh({Text: …})), and -- in case it
// came before this wrap -- from the engine's data component once the grid is set up.
var camoVehicleNameKey = -1827784044;   // UIKitComp.SelectedVehicleName (make_camomenu_doc.py VEHICLE_NAME_KEY)
var camoVehicleClassFrom = function(value, how)
{
   var text = camoMailText(value).text;
   if(text == "" || text == "undefined" || text == camoVehicleClass)
   {
      return undefined;
   }
   camoVehicleClass = text;
   camoSignal("VCL" + how + "," + text);
   camoVehicleRebuild();
};
if(camoVehicleMode)
{
   var camoNameField = camoScreen["TextField_01"];
   if(camoNameField != undefined && camoNameField.refresh != undefined)
   {
      var camoNameRefresh = camoNameField.refresh;
      camoNameField.refresh = function(data)
      {
         var r = camoNameRefresh.apply(this, arguments);
         if(data != undefined && data.Text != undefined)
         {
            camoVehicleClassFrom(data.Text, "name");
         }
         return r;
      };
   }
   if(camoGrid != undefined)
   {
      var camoGridInitClass = camoGrid.initialize;
      camoGrid.initialize = function(initData)
      {
         camoGridInitClass.call(this, initData);
         // the row this window was opened for, before any pick takes its vehicle away (see camoMakeRespawn)
         if(camoVehicleRow == "")
         {
            camoVehicleRow = camoVehicleRowNow();
         }
         if(camoVehicleClass == "" && _global.Data != undefined && _global.Data.UIDataInterfaceComp != undefined)
         {
            camoVehicleClassFrom(_global.Data.UIDataInterfaceComp.getData(camoVehicleNameKey), "data");
         }
      };
   }
}

// --- the family buttons: a release picks the family (the button's own release path calls releaseFunction with the clip's name)
var camoMakePick = function(family)
{
   return function(name)
   {
      camoFamily = family;
      camoRefresh();
   };
};
var camoMakeButtonInit = function(orig, family)
{
   return function(initData)
   {
      orig.call(this, initData);
      this.releaseFunction = camoMakePick(family);
      this.sendEventWithFunction = false;
      // the grid's first data may come before this button exists (the widgets initialise in the graph's order): the family mark and
      // the cursor's look are put on again now that the button can show them
      camoMarkButtons();
      camoShowCursor();
   };
};
// --- the soldier window's tabs (the buttons' first row): a release picks the tab -- the list made again for its part, on the worn
// look's family -- and the button reads our word for the language ("SKT<tab>" to the log)
var camoMakeTab = function(tab)
{
   return function(name)
   {
      camoTab = tab;
      camoSoldierRebuild(true);
      // the keyboard cursor goes with the tab (unless it is in the grid)
      if(camoZone != "grid")
      {
         camoCursorToButton(tab);
      }
      camoSignal("SKT" + tab + "," + camoItems.length);
   };
};
var camoMakeTabInit = function(orig, tab)
{
   return function(initData)
   {
      orig.call(this, initData);
      var word = camoTabWord(tab);
      if(word != "" && this.updateButtonTextsArray != undefined)
      {
         this.updateButtonTextsArray([word]);
      }
      this.releaseFunction = camoMakeTab(tab);
      this.sendEventWithFunction = false;
      camoMarkButtons();
      camoShowCursor();
   };
};
var camoB = 0;
while(camoB < camoButtonNames.length)
{
   var camoBtn = camoButtonClip(camoB);
   if(camoBtn != undefined)
   {
      camoBtn.initialize = camoB < camoTabCount ? camoMakeTabInit(camoBtn.initialize, camoB) :
         camoMakeButtonInit(camoBtn.initialize, camoB - camoTabCount);
   }
   camoB = camoB + 1;
}

// --- the page header: the accessories path plus ACCESSORIES (joined the way the game's own path is joined), the camo title
var camoHeader = camoHeaderClip();
if(camoHeader != undefined)
{
   var camoHeaderInit = camoHeader.initialize;
   camoHeader.initialize = function(initData)
   {
      camoHeaderInit.call(this, initData);
      var path = "";
      var i = 0;
      while(i < camoPathIds.length)
      {
         var part = camoLocalise(camoPathIds[i]);
         if(part != "" && part != "undefined")
         {
            if(path == "")
            {
               path = part;
            }
            else
            {
               path = path + (path.indexOf(" / ") >= 0 ? " / " : "/") + part;
            }
         }
         i = i + 1;
      }
      if(path != "" && this.updateHeaderData != undefined)
      {
         this.updateHeaderData(path);
      }
      if(this.updateSubHeaderData != undefined)
      {
         this.updateSubHeaderData(camoTitleText());
      }
      this._camoPath = path;
      this._camoLang = camoLanguageKey();
      // for the recorder: what the localiser answers for the language, raw, and the key made of it
      if(camoTimeline.rueNote != undefined)
      {
         var raw = _global.Data != undefined && _global.Data.UILocalizeComp != undefined && _global.Data.UILocalizeComp.getLanguage != undefined ? _global.Data.UILocalizeComp.getLanguage() : undefined;
         var store = _global.Data != undefined && _global.Data.UILocalizeComp != undefined && _global.Data.UILocalizeComp.getStoreLanguage != undefined ? _global.Data.UILocalizeComp.getStoreLanguage() : undefined;
         camoTimeline.rueNote("PageHeader_01", "camoLang", [String(raw), String(store), this._camoLang], undefined);
      }
   };
}
