// CamoCommon — the part the camo scripts share (External/keku/make_camomenu_doc.py puts it in front of CamoRow.as and
// CamoScreen.as before compiling each; it is not a script of its own).
//
// THE TABLE. The engine lists every camo unlock of the weapon whether or not the client's description index knows it (an unlock
// the index does not know comes as a row with no Label, no ItemImage and an info box that says NO DATA — but it can be picked
// and it paints the weapon: measured 2026-09). What the menu shows for those is ours: a table `identifier → name, family,
// thumbnail, description` the scripts apply to the items at the door, so the widgets draw our camos the way they draw the
// game's. The table comes two ways, both the same text: the document's `_camoTable` variable (baked into the movie) and, at
// run time, a CARRIER item of the list — an item whose description text starts with "@camotable;" carries the table in the
// rest of it (the framework writes it into one borrowed description); the carrier itself is dropped from the list.
//
// Table text: records separated by ";", fields by "~". A record is either a PACKAGE ("P~name~family~texture~description",
// said once) or a CAMO of the package above it ("id~base"); the six-field "id~name~family~texture~description~base" of the
// first version is still read. Names and texts may not contain those two characters (nor "|" or ":" while the table rides
// in a script variable).
//
// `base` is what an ACCESSORY camo adds: the identifier of the plain accessory it is a camo OF (the ACOG's own unlock for a
// camouflaged ACOG). An entry without it is a weapon camo, as before. It is what lets a row show the accessories and its
// screen show the camos of the one worn, from one flat list the engine hands over.
//
// Parameters the scripts read (set by the injector as variables of the timeline):
//   _camoTable     the baked table ("" = none)
//   _camoFamilies  "FAMILY~keyword,keyword;FAMILY~keyword;…" in the buttons' order; a camo belongs to the family its table entry
//                  names, else to the first family with a keyword in its (localised) name, else to the family with no keywords

var camoTable = {};        // identifier (as a string) -> {name, family, texture, desc}
var camoNoModel = {};      // "<weapon>|<attachment>" -> true: that weapon draws nothing for it (the table)
var camoNoWindow = {};     // attachment identifier -> true: nothing to paint on any weapon (baked from the game)
var camoFamilies = [];     // [{name, keys:[…]}]
var camoTableMarker = "@camotable;";
// a VEHICLE window's table (CamoVehicleView.lua) also says what each vehicle CLASS wears: "W~<id>~<class SID>" (a class with no
// W record wears its own paint); its camo entries carry the base "@V:<class SID>,<class SID>…" (the classes it has a clone for)
var camoVehicleWornBy = {};   // class SID -> the camo identifier it wears (unsigned, as a string)
// a PISTOL window's table (SidearmTwins.lua) also says what each pistol wears: "SA~<pistol unlock>~<its camo copy>" (no record = the
// pistol as the game ships it) -- the server keeps the choice and hands it at the deploy, as the vehicles'
var camoSidearmWorn = {};     // pistol unlock -> the camo copy it wears (unsigned, as strings)
// the SOLDIER window's table (CamoSoldierView.lua): "K~<base|XP4>" (the level's kits), a skin per "S~<id>~<name>~<family>~<thumbnail>~
// <text>~<soldier>~<parts>" (soldier = US_Assault, RU_Engineer_XP4…; parts = "head,torso,legs" or fewer) and what each soldier wears
// on each part, "SW~<soldier>~<part>~<id>" (a row of the game's or a skin; a part with no record wears the game's own look)
var camoSkinWorn = {};        // soldier -> {head, torso, legs} -> identifier (unsigned, as a string)
var camoSkinPack = "";        // "XP4" on a level of Aftermath kits, "base" (or "") otherwise
// a stock look's NAME by its row ("R~<row id>~<look>": Urban, Desert02, XP2_DsrtTiger…, the same in every language): the family
// keywords are matched against it too -- a localised label says nothing to them ("VERDE DEL EJÉRCITO" has no "green")
var camoRowLook = {};         // row identifier (unsigned, as a string) -> the look's name
// a skin part's OWN cell on its tab ("SP~<skin id>~<part>~<thumbnail>~<family>~<name>~<text>", keku 2026-09-28: each part can be a
// different thing): its picture, family, name and INFO text there, "" = the skin's
var camoSkinParts = {};       // skin identifier (unsigned, as a string) -> {head|torso|legs: {texture, family, name, desc}}

var camoParseFamilies = function(text)
{
   var out = [];
   var parts = String(text).split(";");
   var i = 0;
   while(i < parts.length)
   {
      var part = parts[i];
      if(part.length > 0)
      {
         var tilde = part.indexOf("~");
         var name = tilde < 0 ? part : part.substr(0, tilde);
         var keys = [];
         if(tilde >= 0 && tilde + 1 < part.length)
         {
            keys = part.substr(tilde + 1).split(",");
         }
         var k = 0;
         while(k < keys.length)
         {
            keys[k] = keys[k].toLowerCase();
            k = k + 1;
         }
         out.push({name:name, keys:keys});
      }
      i = i + 1;
   }
   return out;
};
camoFamilies = camoParseFamilies(_camoFamilies);

// ⛔⛔ AN IDENTIFIER REACHES THE SCRIPT SIGNED (measured 2026-09-19 from keku's photo: rows with no name and no picture).
// The engine hands an unlock identifier as a 32-bit INT, so anything from 0x80000000 up arrives NEGATIVE -- 11 of the 21
// twins on his L85A2 -- while the framework writes the table keyed UNSIGNED. "-1689696087" found nothing in it, so those
// items kept the empty label and empty thumbnail of an unlock the client's description index does not know: the blank. The
// law was already written for the ids the AS2 channel sends to Lua; it holds for EVERY id that crosses this boundary, so
// every lookup and every comparison in these scripts goes through this one function.
var camoIdKey = function(value)
{
   var n = Number(value);
   if(isNaN(n))
   {
      return String(value);
   }
   if(n < 0)
   {
      n = n + 4294967296;
   }
   return String(n);
};

// the table text into camoTable (entries add to what is there: the baked table first, a carrier's on top); returns how many
//
// Two shapes, and a record tells which it is by its FIRST field, because an identifier is always a number:
//   "P~<name>~<family>~<thumbnail>~<text>"   a package: what every camo of it is called and looks like, said ONCE
//   "<id>~<accessory>"                       a camo, wearing the package above it
//   "<id>~<name>~<family>~<thumbnail>~<text>~<accessory>"   the old shape, still read (a table from an older build)
// The package header is what keeps the table small: of the 138 characters an entry used to take, 118 were those same
// strings repeated (measured 2026-09-19 -- 140 KB for the whole arsenal, ~23 KB like this).
var camoParseTable = function(text)
{
   var n = 0;
   if(text == undefined || String(text) == "" || String(text) == "undefined")
   {
      return 0;
   }
   var entries = String(text).split(";");
   var pkg = {name:"", family:"", texture:"", desc:""};
   var i = 0;
   while(i < entries.length)
   {
      var f = entries[i].split("~");
      if(f[0] == "X")
      {
         // "X~<weapon>~<id>,<id>…": what THAT weapon draws nothing for. Per weapon, never global: the same unlock is
         // the iron sights of one rifle and nothing at all on another (Weapons/Common/NoOptics). A row will not open
         // its window for one of these -- there would be nothing in it (keku 2026-09-19: "que no se abra dicha
         // ventana").
         var weapon = camoIdKey(f.length > 1 ? f[1] : "");
         var ids = String(f.length > 2 ? f[2] : "").split(",");
         var x = 0;
         while(x < ids.length)
         {
            if(ids[x].length > 0)
            {
               camoNoModel[weapon + "|" + camoIdKey(ids[x])] = true;
            }
            x = x + 1;
         }
      }
      else if(f[0] == "SA")
      {
         // what a pistol (or the crossbow) wears: "SA~<pistol>~<camo copy>" -- the server keeps it, the vehicle windows' way (the game
         // does not store a camo copy as the player's pistol); the pistol's window marks that cell
         if(f.length > 2 && String(f[1]) != "" && String(f[2]) != "")
         {
            camoSidearmWorn[camoIdKey(f[1])] = camoIdKey(f[2]);
         }
      }
      else if(f[0] == "W")
      {
         // what a vehicle class wears -- not a camo of the list (it would read as an old-shape entry "W")
         if(f.length > 2 && String(f[2]) != "")
         {
            camoVehicleWornBy[String(f[2])] = camoIdKey(f[1]);
         }
      }
      else if(f[0] == "P")
      {
         pkg = {name:f.length > 1 ? f[1] : "", family:f.length > 2 ? f[2] : "", texture:f.length > 3 ? f[3] : "",
                    desc:f.length > 4 ? f[4] : ""};
      }
      else if(f[0] == "K")
      {
         // the soldier window's level: base or Aftermath kits
         camoSkinPack = f.length > 1 ? String(f[1]) : "";
      }
      else if(f[0] == "S")
      {
         // a soldier SKIN: named, filed and pictured like a camo, and said for which soldier and which of its parts
         if(f.length > 7 && f[1].length > 0)
         {
            camoTable[camoIdKey(f[1])] = {name:f[2], family:f[3], texture:f[4], desc:f[5], base:"@S:" + f[6],
                                          soldier:String(f[6]), parts:String(f[7])};
            // its parts' own cells come after it (a table of this level: none left from another)
            camoSkinParts[camoIdKey(f[1])] = {};
            n = n + 1;
         }
      }
      else if(f[0] == "SP")
      {
         // a skin part's own cell: its picture and family on its tab
         if(f.length > 4 && f[1].length > 0 && f[2].length > 0)
         {
            if(camoSkinParts[camoIdKey(f[1])] == undefined)
            {
               camoSkinParts[camoIdKey(f[1])] = {};
            }
            camoSkinParts[camoIdKey(f[1])][String(f[2])] = {texture:String(f[3]), family:String(f[4]), name:f.length > 5 ? String(f[5]) : "",
                                                             desc:f.length > 6 ? String(f[6]) : ""};
         }
      }
      else if(f[0] == "R")
      {
         // a stock look's name, for the family buttons -- not a camo of the list
         if(f.length > 2 && String(f[1]) != "")
         {
            camoRowLook[camoIdKey(f[1])] = String(f[2]);
         }
      }
      else if(f[0] == "SW")
      {
         // what a soldier wears on a part -- not a camo of the list (it would read as an old-shape entry "SW")
         if(f.length > 3 && String(f[1]) != "" && String(f[2]) != "")
         {
            if(camoSkinWorn[String(f[1])] == undefined)
            {
               camoSkinWorn[String(f[1])] = {};
            }
            camoSkinWorn[String(f[1])][String(f[2])] = camoIdKey(f[3]);
         }
      }
      else if(f.length == 2 && f[0].length > 0)
      {
         camoTable[String(f[0])] = {name:pkg.name, family:pkg.family, texture:pkg.texture,
                                    desc:pkg.desc, base:String(f[1])};
         n = n + 1;
      }
      else if(f.length >= 2 && f[0].length > 0)
      {
         camoTable[String(f[0])] = {name:f[1], family:f.length > 2 ? f[2] : "", texture:f.length > 3 ? f[3] : "", desc:f.length > 4 ? f[4] : "", base:f.length > 5 ? String(f[5]) : ""};
         n = n + 1;
      }
      i = i + 1;
   }
   return n;
};
var camoTableCount = camoParseTable(_camoTable);

// the table entry of an item (by its identifier, the number the pick travels on), or undefined
var camoEntryOf = function(item)
{
   if(item == undefined || item.Index == undefined)
   {
      return undefined;
   }
   return camoTable[camoIdKey(item.Index)];
};

// the accessory an item is a camo OF (its table entry's `base`), or "" when it is not one: what tells a camouflaged ACOG from
// the plain ACOG and from the EOTech, all three of them items of the same row
var camoBaseOf = function(item)
{
   var entry = camoEntryOf(item);
   return entry == undefined || entry.base == undefined ? "" : String(entry.base);
};

// the accessory an item BELONGS to: itself when it is a plain one, its base when it is a camo of one
var camoFamilyBaseOf = function(item)
{
   var base = camoBaseOf(item);
   return base != "" ? base : (item == undefined || item.Index == undefined ? "" : camoIdKey(item.Index));
};

// ⛔ WHICH ATTACHMENTS HAVE NO WINDOW. Two sources, and the first is the one that always answers:
//   _camoNoWindow  the GAME's own list, baked into this movie from its customization rows (the iron sights, the heavy
//                  barrels, the "No…" placeholders, a shotgun's ammunition -- everything with nothing to paint). It is
//                  static data, so it needs no mod running and no package installed: that is why it is here and not in
//                  the table (measured 2026-09-19: the framework's table only reaches a row that got a camo, so on a
//                  weapon no package paints there was nothing to answer with and every window opened).
//   the table's "X" records, per (weapon, attachment), when a package's carrier does reach the row.
// Anything neither says about is assumed to draw, so missing data never blocks a window.
var camoNoWindowParts = String(_camoNoWindow).split(",");
var camoW = 0;
while(camoW < camoNoWindowParts.length)
{
   if(camoNoWindowParts[camoW].length > 0)
   {
      camoNoWindow[camoIdKey(camoNoWindowParts[camoW])] = true;
   }
   camoW = camoW + 1;
}
// ⛔ AND THE PAIRS, BAKED: one identifier cannot say "the iron sights of this rifle, nothing on that one".
// "No optic" is a single unlock shared by the 60 weapons (Weapons/Common/NoOptics) -- the sights of 29 of
// them and an empty socket on the other 31 -- so keeping it in the list above shut every sight window, and
// taking it out would open them all onto a hole. These pairs are the 31, worked out from the catalogue when
// the menu is built, so they answer with no mod running (keku 2026-09-20: the sights get their window).
// They go into the SAME map the framework's own "X" records fill: one question, one place to ask it.
var camoNoWindowPairParts = String(_camoNoWindowPairs).split(",");
var camoP = 0;
while(camoP < camoNoWindowPairParts.length)
{
   var camoPair = camoNoWindowPairParts[camoP].split(".");
   if(camoPair.length == 2 && camoPair[0].length > 0 && camoPair[1].length > 0)
   {
      camoNoModel[camoIdKey(camoPair[0]) + "|" + camoIdKey(camoPair[1])] = true;
   }
   camoP = camoP + 1;
}

var camoDrawsNothing = function(weaponId, accessoryId)
{
   return camoNoWindow[camoIdKey(accessoryId)] == true ||
      camoNoModel[camoIdKey(weaponId) + "|" + camoIdKey(accessoryId)] == true;
};

// a carrier: an item whose description text opens with the marker
var camoIsCarrier = function(item)
{
   return item != undefined && item.Description != undefined && String(item.Description.Description).indexOf(camoTableMarker) == 0;
};

// a carrier's table, read AND counted: the framework writes every entry into ONE borrowed description, so what matters is
// how much of it ARRIVES. A description that comes back cut would leave the entries past the cut nameless and nothing would
// say so (the framework's own read-back only proves the marker is there) -- the count and the length go to the mod's log.
var camoReadCarrier = function(item)
{
   var text = String(item.Description.Description).substr(camoTableMarker.length);
   var n = camoParseTable(text);
   camoSignal("TBL" + n + "," + text.length);
   return n;
};

// the items as the engine hands them, made ours: carriers read and dropped, every item the table knows given its name, its
// thumbnail, its description (Type 1: name + text, what the info box shows) and no plus marker
var camoRewriteItems = function(items)
{
   // The second look (see the probe at the end of this file): by now the widgets have been built and fed, which
   // the script's own load is too early for.
   camoCanary(1);

   var list = fb.Base.UIBase.ObjectToArray(items);
   if(list == undefined)
   {
      return [];
   }
   var out = [];
   var i = 0;
   while(i < list.length)
   {
      var item = list[i];
      if(camoIsCarrier(item))
      {
         camoReadCarrier(item);
      }
      else
      {
         out.push(item);
      }
      i = i + 1;
   }
   i = 0;
   while(i < out.length)
   {
      var it = out[i];
      var entry = camoEntryOf(it);
      if(entry != undefined)
      {
         it.Label = entry.name;
         if(entry.texture != "")
         {
            it.ItemImage = entry.texture;
         }
         it.ShowPlus = false;
         it.Description = {Type:1, Label:entry.name, Description:entry.desc, Index:it.Index, Image:String(it.ItemImage), Icon:String(it.ItemImage), Category:entry.family};
      }
      i = i + 1;
   }
   return out;
};

// ---- the accessory ROWS of the game's screen ----------------------------------------------------------------------------
// ⛔ A ROW IS NOT A GRID (keku 2026-09-19, with a photo of the accessories screen: "me están saliendo los camos de prueba o el
// no data/blank cuando eso no debe ocurrir"). A row of the accessories screen steps through the ACCESSORIES of that slot, one
// at a time; our twins are the same accessory with a camo on it, so as entries of their own they turn the arrows into a list of
// camo names ("Acctest All" where the ACOG should be) and any twin the table does not name draws BLANK. The camo belongs to
// the row's OWN screen, which is the only place its name is the answer to anything.
//
// So, in a row: every twin is folded into the accessory it is a camo OF, and the one the player wears TAKES THE PLACE of that
// accessory in the list, wearing the accessory's name and picture. The fold keeps exactly two twins visible to the widget --
// the one the engine has stored (or the row could not show what is equipped) and the one just picked on our screen (or the
// pick could never be stored: the row's own select() is the wire into SetAccessoryN) -- and both wear the base's clothes.
var camoDressLike = function(item, base)
{
   if(item == undefined || base == undefined)
   {
      return item;
   }
   item.Label = base.Label;
   item.ItemImage = base.ItemImage;
   item.ShowPlus = base.ShowPlus;
   item.Description = base.Description;
   return item;
};

var camoCollapseAccessoryItems = function(items, picked, slotNumber)
{
   var list = fb.Base.UIBase.ObjectToArray(items);
   if(list == undefined)
   {
      return [];
   }
   var plain = [];        // the accessories themselves, in the row's own order
   var twins = [];        // ours, each one a camo OF one of them
   var orphans = "";      // ours by the table, but with no accessory named: they cannot stand in for one
   var rest = [];
   var i = 0;
   // ⛔⛔ THE CARRIER FIRST, ALWAYS (measured 2026-09-19 from keku's log: "drop,x121131578" -- the table did not know an id
   // whose entry it was carrying). The framework adds the carrier to the row right after the FIRST twin, so a single pass
   // classifies that twin while the table is still empty: it fell through as an accessory with no name, and only ever the
   // first twin of the first row (by the time the other rows get data the table has been read). A list is read for what it
   // CARRIES before it is read for what it holds.
   while(i < list.length)
   {
      if(camoIsCarrier(list[i]))
      {
         camoReadCarrier(list[i]);
      }
      else
      {
         rest.push(list[i]);
      }
      i = i + 1;
   }
   i = 0;
   while(i < rest.length)
   {
      var item = rest[i];
      if(camoEntryOf(item) != undefined)
      {
         // OURS: the table knows this id. With an accessory named it can take that accessory's place; without one it is
         // dropped rather than left in the row, where it would draw as an empty box -- the net under the pass above.
         if(camoBaseOf(item) != "")
         {
            twins.push(item);
         }
         else
         {
            orphans = orphans + (orphans.length < 30 ? "," + camoIdKey(item.Index) : "");
         }
      }
      else if(item.Label == undefined || String(item.Label) == "")
      {
         // NOT the game's either: an item with no name and no picture is an unlock the client's description index never
         // knew -- ours (the table's carrier when its seat belongs to another metadata) or a leftover. The player can do
         // nothing with an empty box, so it never goes in the row.
         orphans = orphans + (orphans.length < 30 ? ",x" + camoIdKey(item.Index) : "");
      }
      else
      {
         plain.push(item);
      }
      i = i + 1;
   }
   // which twin each accessory shows, if any: what the engine has stored beats nothing, and a fresh pick beats the stored one
   var worn = {};
   i = 0;
   while(i < twins.length)
   {
      var twin = twins[i];
      var base = camoBaseOf(twin);
      if(twin.DefaultSelected == true && worn[base] == undefined)
      {
         worn[base] = twin;
      }
      if(picked != undefined && camoIdKey(twin.Index) == camoIdKey(picked))
      {
         worn[base] = twin;
      }
      i = i + 1;
   }
   var out = [];
   var nameless = "";      // what the fold could not account for: an item of ours the table does not name (see the trace below)
   i = 0;
   while(i < plain.length)
   {
      if(nameless.length < 40 && (plain[i].Label == undefined || String(plain[i].Label) == ""))
      {
         nameless = nameless + "," + camoIdKey(plain[i].Index);
      }
      var accessory = plain[i];
      var stand = worn[camoIdKey(accessory.Index)];
      if(stand != undefined)
      {
         // the twin in the accessory's place: same name, same picture, and the row still holds OUR identifier, which is what
         // the loadout stores and what paints the camo
         stand.DefaultSelected = accessory.DefaultSelected == true || stand.DefaultSelected == true;
         out.push(camoDressLike(stand, accessory));
      }
      else
      {
         out.push(accessory);
      }
      i = i + 1;
   }
   // what this row ended up with, to the mod's log: how many of each, and the id of anything left without a name. An item
   // with no name is either an unlock of ours the table does not carry or one the client's description index never knew --
   // the two look identical on screen, and only the id tells them apart.
   // ⛔ ONCE PER SHAPE, NOT ONCE PER UPDATE (keku 2026-09-19: the server timed him out while he played with camos). A row
   // gets data on every arrow, every pick and every screen, and each of these is a message across the network from his
   // client. The counts of a row do not change between updates, so only a shape not seen before -- or anything wrong --
   // is worth sending. [[keku-instrumentation-cost-budget]]
   var shape = String(slotNumber) + "," + plain.length + "," + twins.length + "," + out.length +
      (nameless == "" ? "" : ",blank" + nameless) + (orphans == "" ? "" : ",drop" + orphans);
   if(_global._camoFoldSeen == undefined)
   {
      _global._camoFoldSeen = {};
   }
   if(_global._camoFoldSeen[shape] != true)
   {
      _global._camoFoldSeen[shape] = true;
      camoSignal("WVF" + shape);
   }
   return out;
};

// a text id through the game's localiser (the same proxy the widgets use); anything else as it is
var camoLocalise = function(text)
{
   var s = String(text);
   if(s.indexOf("ID_") == 0 && _global.Data != undefined && _global.Data.UILocalizeComp != undefined)
   {
      var t = _global.Data.UILocalizeComp.formatString(s);
      if(t != undefined && String(t) != "")
      {
         return String(t);
      }
   }
   return s;
};

// the camo's name as the player reads it
var camoLabelText = function(item)
{
   return camoLocalise(item.Label);
};

// the family of a camo: its table entry's family (by name, case-insensitively), else the first family with a keyword in its
// name (raw id and localised text both tried), else the one without keywords
var camoFamilyOf = function(item)
{
   var fallback = 0;
   var i = 0;
   var entry = camoEntryOf(item);
   // (a cell filed under a family of its own -- a skin part's own cell on its tab -- is filed there, not where its entry is)
   var wanted = item._camoFamily != undefined ? String(item._camoFamily).toLowerCase() : (entry == undefined ? "" : String(entry.family).toLowerCase());
   while(i < camoFamilies.length)
   {
      if(camoFamilies[i].keys.length == 0)
      {
         fallback = i;
      }
      if(wanted != "" && String(camoFamilies[i].name).toLowerCase() == wanted)
      {
         return i;
      }
      i = i + 1;
   }
   var raw = String(item.Label).toLowerCase();
   var text = camoLabelText(item).toLowerCase();
   // a stock soldier look: its name as well (the soldier window's table), the same in every language
   var look = item.Index == undefined || camoRowLook[camoIdKey(item.Index)] == undefined ? "" : String(camoRowLook[camoIdKey(item.Index)]).toLowerCase();
   i = 0;
   while(i < camoFamilies.length)
   {
      var fam = camoFamilies[i];
      var k = 0;
      while(k < fam.keys.length)
      {
         var key = fam.keys[k];
         if(key.length > 0 && (raw.indexOf(key) >= 0 || text.indexOf(key) >= 0 || look.indexOf(key) >= 0))
         {
            return i;
         }
         k = k + 1;
      }
      i = i + 1;
   }
   return fallback;
};

// the name a cell shows: the weapon's own prefix ("SCAR-H ", "M416 ") and a trailing " Camo" dropped, as the thumbnail grid of
// the newer game names its patterns; "No Camo" and other two-word names stay whole
var camoCellLabel = function(item)
{
   var text = camoLabelText(item);
   var lower = text.toLowerCase();
   if(lower.indexOf("no ") == 0)
   {
      return text;
   }
   if(text.length > 5 && lower.substr(text.length - 5) == " camo")
   {
      text = text.substr(0, text.length - 5);
   }
   var space = text.indexOf(" ");
   if(space > 0 && space < text.length - 1)
   {
      var first = text.substr(0, space);
      var i = 0;
      var weaponLike = false;
      while(i < first.length)
      {
         var c = first.charAt(i);
         if(c == "-" || (c >= "0" && c <= "9"))
         {
            weaponLike = true;
         }
         i = i + 1;
      }
      if(weaponLike)
      {
         text = text.substr(space + 1);
      }
   }
   return text;
};

// the description the info box shows for an item (its own, or one built from the table / the name)
var camoInfoOf = function(item)
{
   if(item == undefined)
   {
      return undefined;
   }
   if(item.Description != undefined && item.Description.Type != undefined)
   {
      return item.Description;
   }
   return {Type:1, Label:camoLabelText(item), Description:""};
};

// ---- a word to the client's Lua: the AS2 log channel (bits over the engine's typing-mode request, which the client hook
// UI:EnableTypingMode of the AS2LogChannel mod decodes and re-dispatches as its "AS2:Frame" event). Frame: 0x55 | length | bytes |
// xor(length, bytes), most significant bit first, a closing false; at most 200 characters. The same sender the channel's own
// kitselector.gfx defines, kept under its name so whichever movie loads first defines it once; nothing outside the game (no
// _global.Data) and nothing while a frame is being sent.
if(_global.fbLog == undefined)
{
   _global.fbLog = function(s)
   {
      if(_global.Data == undefined || _global.Data.UIWidgetEventComp == undefined)
      {
         return false;
      }
      if(_global._fbLogBusy == true)
      {
         _global._fbLogDropped = (_global._fbLogDropped == undefined ? 0 : _global._fbLogDropped) + 1;
         return false;
      }
      _global._fbLogBusy = true;
      var comp = _global.Data.UIWidgetEventComp;
      var str = String(s);
      var n = str.length;
      if(n > 200)
      {
         n = 200;
      }
      var bytes = new Array();
      bytes.push(85);
      bytes.push(n);
      var x = n;
      var i = 0;
      while(i < n)
      {
         var c = str.charCodeAt(i) & 255;
         bytes.push(c);
         x = x ^ c;
         i = i + 1;
      }
      bytes.push(x & 255);
      var k = 0;
      while(k < bytes.length)
      {
         var b = bytes[k];
         var m = 128;
         while(m > 0)
         {
            comp.requestTypingInput((b & m) != 0);
            m = m >> 1;
         }
         k = k + 1;
      }
      comp.requestTypingInput(false);
      _global._fbLogBusy = false;
      return true;
   };
   _global._fbLogBusy = false;
}
// a short word to Lua; counts what was sent (for the seam: the count moves even where the channel does not exist)
var camoSignal = function(s)
{
   _global._camoSignals = (_global._camoSignals == undefined ? 0 : _global._camoSignals) + 1;
   _global._camoLastSignal = String(s);
   // and the last few, in order (a seam asking "was X sent" cannot trust the LAST one: a screen's mailbox receipt may come after)
   if(_global._camoSignalArr == undefined)
   {
      _global._camoSignalArr = [];
   }
   _global._camoSignalArr.push(String(s));
   if(_global._camoSignalArr.length > 16)
   {
      _global._camoSignalArr.shift();
   }
   _global._camoSignalTrail = _global._camoSignalArr.join(" | ");
   return _global.fbLog(s);
};

// ---- THE MAILBOX: THE TABLE, DELIVERED AT RUN TIME ----------------------------------------------------------------
// ⭐ WHY THIS EXISTS (keku, 2026-09-20): the table is compiled into this movie today, so a camo baked afterwards does
// not exist for the menu until the menu is rebuilt -- and he wants the framework to be the base mod, with the studio
// only dropping packages into a folder. So the table has to ARRIVE, not be baked. This is the engine's own path: a
// DataSetNode writes our key when the screen is entered and the grid's binding delivers it here, by the name the
// binding gives it (`CamoTable` -> `updateCamoTableData`, the same convention as `GridItems` -> `updateGridItemsData`).
// ⛔ The shape a data value arrives in is NOT assumed: a string, or an object with the usual one field. Whatever it
// turns out to be is reported, so a wrong guess is a line in the log and not a silent zero.
var camoMailText = function(data)
{
   if(data == undefined)
   {
      return {text:"", shape:"none"};
   }
   if(typeof data == "string")
   {
      return {text:String(data), shape:"str"};
   }
   var named = ["Text", "Value", "Data", "String", "Param"];
   var i = 0;
   while(i < named.length)
   {
      if(data[named[i]] != undefined && typeof data[named[i]] == "string")
      {
         return {text:String(data[named[i]]), shape:named[i]};
      }
      i = i + 1;
   }
   for(var k in data)
   {
      if(typeof data[k] == "string" && String(data[k]).length > 0)
      {
         return {text:String(data[k]), shape:String(k)};
      }
   }
   return {text:"", shape:"empty"};
};

// What arrived, parsed straight into the same table the baked one filled (entries ADD, so what is baked stays and
// what arrives wins where they meet). The line is the measurement AND the receipt: how many entries and how long.
var camoMailbox = function(data)
{
   var got = camoMailText(data);
   // the framework hands the text with its marker on (the same string the carrier carried); the parser wants the records
   if(got.text.indexOf(camoTableMarker) == 0)
   {
      got.text = got.text.substr(camoTableMarker.length);
   }
   // a delivered table is the whole of it: what the pistols wear is said again (a pistol put back on its own look has no record)
   if(got.text.length > 0)
   {
      camoSidearmWorn = {};
   }
   var n = camoParseTable(got.text);
   camoSignal("TBL" + n + "," + got.text.length + "," + got.shape);

   // ⭐ AND WHICH FAMILY EACH PACKAGE CAME WITH, AS *THIS SIDE* READ IT (keku 2026-09-20: a camo turned up under
   // DESERT). The framework prints what it SENT; this prints what the screen PARSED, and the two together say
   // which side moved it -- instead of a tab on screen being the only evidence.
   var seen = "";
   var count = 0;
   for(var id in camoTable)
   {
      var fam = String(camoTable[id].family);
      if(fam != "" && seen.indexOf("[" + fam + "]") < 0 && count < 6)
      {
         seen = seen + "[" + fam + "]";
         count = count + 1;
      }
   }
   camoSignal("FAM" + count + "," + seen);
   return n;
};

// ⛔ AND THE CENSUS THAT MAKES A SILENCE USEFUL: the engine refreshes a widget by calling `refresh({DataName: payload})`
// (measured 2026-09-16), so wrapping that says EXACTLY which names reach this widget. If our key never arrives, this
// names the ones that do -- which is the next thing to try instead of the end of the road.
var camoWatchSeen = 0;

var camoWatchRefresh = function(clip)
{
   if(clip == undefined || clip._camoWatched == true)
   {
      return;
   }
   clip._camoWatched = true;
   var original = clip.refresh;
   clip.refresh = function(data)
   {
      if(camoWatchSeen < 10)
      {
         for(var k in data)
         {
            camoWatchSeen = camoWatchSeen + 1;
            var got = camoMailText(data[k]);
            camoSignal("UPD" + String(k) + "," + got.text.length);
         }
      }
      return original.apply(this, arguments);
   };
};

// ---- THE MAILBOX PROBE ------------------------------------------------------------------------------------------
// ⭐ WHAT THIS ANSWERS (keku, 2026-09-20): whether a value the MOD writes at run time -- a WidgetProperty on a node
// the screen is built from, which is how this whole screen is built (nodes created in Lua) -- reaches this script.
// If it does, the camo table stops being baked into the movie: the framework writes what exists NOW and nothing has
// to be rebuilt when a camo is baked. Two unknowns and one boot: WHERE such a value lands, and HOW MUCH of it
// arrives (the description route carried 8203 characters whole; the whole arsenal is projected at ~23 KB).
//
// ⛔ It is placed at the END of this file ON PURPOSE: `camoSignal` is a `var` assigned above, and a probe written
// higher up would call a value that does not exist yet -- the AS2 cousin of the Lua local-order law.
// ⛔⛔ ANSWERED, AND TURNED OFF (boot 44): a `UIWidgetProperty` written at run time DOES NOT REACH THIS SCRIPT.
// The clip carries 13 keys starting with "p_" (the NAMES arrive), the game's own `p_cellType` reads EMPTY, ours
// read undefined, and hunting the text itself -- a marker no other string in the game has -- found NOTHING on the
// widget, on the row, or one level into anything hanging off them. The control (`CTL5`) proves the probe was
// looking in the right place by then, so this is about the ROUTE. What is left is the DATA route: a DataSetNode
// written from Lua feeding a binding, which arrives as the payload our own `updateSetupData` already receives.
// `camoProbeOn = 1` puts the whole thing back (and a rebuild is needed for it to reach the movie).
var camoProbeOn = 0;
var camoCanaryDone = 0;

// ⛔ THE TIMELINE IS CAPTURED HERE, NOT ASKED FOR INSIDE THE FUNCTION. At the top level of this script `this` IS the
// timeline the script runs on; inside a function called by its bare name it is whatever AS2 decides to bind, and a
// probe that looks in the wrong object reports "nowhere" for a value that did arrive -- a false negative that would
// cost a boot and close a road that works.
var camoScope = this;

// ⛔⛔ THE PROBE ASKS WHERE THE PRODUCT ASKS (fixed after boot 42, where everything came back 0 -- including DICE's
// own `p_cellType`, which is what gave it away). These scripts DO NOT reach their widgets through `_root`: they go
// `this[_camoRoot]` -> the screen's root clip ("instance1") -> the clip named like the node ("CamoGrid",
// "KitSelector_05"), which is how `CamoRow.as` installs `updateSetupData` on the row. My list asked `_root.CamoGrid`
// -- an address nothing lives at (the census said 0 keys) -- so the zero measured MY ADDRESS, not the route.
// Law: a probe looks up its subject the same way the code that uses it does; a negative from another address is not
// a negative.
var camoCanaryRoot = function()
{
   var r = camoScope[String(_camoRoot)];
   return r == undefined ? camoScope : r;
};

var camoCanaryWidget = function(name)
{
   var r = camoCanaryRoot();
   return r == undefined ? undefined : r[name];
};

// The places a written value could land, ASKED IN ORDER: the number that comes back is the answer to "where", so a
// hit says where it was found and a miss (0) says it reached none of them -- one reading, not a guess.
var camoCanaryLook = function(name)
{
   var places = [camoScope, _root, _global, camoCanaryRoot(), camoCanaryWidget("CamoGrid"),
      camoCanaryWidget("KitSelector_05"), camoCanaryWidget("KitInfoBox_01"), _root.CamoGrid];
   var i = 0;
   while(i < places.length)
   {
      var p = places[i];
      if(p != undefined && p[name] != undefined)
      {
         return {where:i + 1, value:p[name]};
      }
      i = i + 1;
   }
   return {where:0, value:undefined};
};

// ⛔⛔ THE CONTROL, ADDED AFTER THE FIRST BOOT (2026-09-20, all three canaries came back `where=0, len=0`): THAT
// NEGATIVE HAS TWO READINGS -- "a property the mod writes does not reach this script" and "the write never
// happened" -- and a test whose negative means two things measures nothing.
// `p_cellType` is DICE'S OWN property on that same grid node (value "KitCell", shipped, not ours). If the control
// is not visible either, the answer is about the ROUTE; if the control is visible and ours are not, the answer is
// about our write. And the census says how many keys each place holds at all, so "empty object" and "object I
// cannot see into" stop looking alike.
var camoCanaryCensus = function()
{
   var places = [camoScope, camoCanaryRoot(), camoCanaryWidget("CamoGrid"), camoCanaryWidget("KitSelector_05")];
   var i = 0;
   while(i < places.length)
   {
      var p = places[i];
      var keys = 0;
      var props = 0;
      if(p != undefined)
      {
         for(var k in p)
         {
            keys = keys + 1;
            if(String(k).substr(0, 2) == "p_")
            {
               props = props + 1;
            }
         }
      }
      camoSignal("CNP" + (i + 1) + "," + keys + "," + props);
      i = i + 1;
   }
   var hit = camoCanaryLook("p_cellType");
   var text = hit.value == undefined ? "" : String(hit.value);
   camoSignal("CTL" + hit.where + "," + text.length + "," + (text == "KitCell" ? "1" : "0"));
   camoCanaryHunt();
};

// ⛔⛔ STOP GUESSING THE ADDRESS -- HUNT THE MARKER (boot 43). Two boots were spent on two wrong addresses of
// mine, and the third measured something real: the widget's clip DOES exist and DOES hold 13 keys starting with
// "p_", but the one the game itself writes (`p_cellType`) reads EMPTY and ours read undefined ⇒ the values do not
// live in the clip's timeline variables, wherever they do live. So this no longer asks for a NAME anywhere: it
// walks the widget (and one level into whatever objects hang off it, `initData` included) looking for the TEXT --
// the canary starts with "[CAN", which nothing else in the game does -- and reports WHERE it found it.
// Capped and cycle-guarded on purpose: a walk that follows `_parent` in a movie clip never comes back, and this
// runs in HIS client.
var camoHuntSeen = 0;

var camoHuntIn = function(obj, depth)
{
   if(obj == undefined || depth > 2 || camoHuntSeen > 400)
   {
      return undefined;
   }
   for(var k in obj)
   {
      var key = String(k);
      if(key == "_parent" || key == "_root" || key == "_global" || key == "_level0")
      {
         continue;
      }
      camoHuntSeen = camoHuntSeen + 1;
      if(camoHuntSeen > 400)
      {
         return undefined;
      }
      var v = obj[k];
      var t = typeof v;
      if(t == "string" && String(v).indexOf("[CAN") == 0)
      {
         return {path:key, len:String(v).length, whole:String(v).substr(String(v).length - 4) == "]END"};
      }
      if(t == "object" || t == "movieclip")
      {
         var deeper = camoHuntIn(v, depth + 1);
         if(deeper != undefined)
         {
            return {path:key + "." + deeper.path, len:deeper.len, whole:deeper.whole};
         }
      }
   }
   return undefined;
};

var camoCanaryHunt = function()
{
   camoHuntSeen = 0;
   var subjects = [camoCanaryWidget("CamoGrid"), camoCanaryWidget("KitSelector_05"), camoCanaryRoot()];
   var i = 0;
   while(i < subjects.length)
   {
      var hit = camoHuntIn(subjects[i], 0);
      if(hit != undefined)
      {
         camoSignal("MRK" + (i + 1) + ",1," + hit.len + "," + (hit.whole ? "1" : "0") + "," +
            hit.path.substr(0, 24));
         return;
      }
      camoHuntSeen = 0;
      i = i + 1;
   }
   camoSignal("MRK0,0,0,0,none");
};

// One line per canary, and each one says the same three things: where, how long, and whether the END MARKER made it
// -- a length alone cannot tell "arrived whole" from "cut at exactly that point".
var camoCanary = function(tag)
{
   if(camoProbeOn != 1 || camoCanaryDone == 1)
   {
      return 0;
   }
   camoCanaryCensus();
   var names = ["p_camoCanary1", "p_camoCanary2", "p_camoCanary3"];
   var found = 0;
   var i = 0;
   while(i < names.length)
   {
      var hit = camoCanaryLook(names[i]);
      var text = hit.value == undefined ? "" : String(hit.value);
      var tail = text.length < 4 ? "" : text.substr(text.length - 4);
      if(hit.where > 0)
      {
         found = found + 1;
      }
      camoSignal("CAN" + (i + 1) + "," + hit.where + "," + text.length + "," + (tail == "]END" ? "1" : "0") + "," + tag);
      i = i + 1;
   }
   // Only a run that FOUND something stops the probe: the script loads before the widgets are fed, so the first
   // look can legitimately come back empty and the one from the items is the one that counts.
   if(found > 0)
   {
      camoCanaryDone = 1;
   }
   return found;
};

camoCanary(0);
