// CamoVehicleRow — a frame-1 action for the game's own TIERRA / AIRE screens (customizelandscreen / customizeairscreen),
// compiled behind CamoCommon.as by External/keku/make_camomenu_doc.py and put on the timeline by the document's stage op
// `script:camovehiclerow:<vars>:<base64>`.
//
// Every row of the list (the KitView widget: one row per vehicle class the MAP and the MODE field, which is why nothing here
// names a vehicle) gets a second button beside PERSONAL.: CAMUFLAJE, which opens that vehicle's camo window (keku, 2026-09-23:
// *"añadir un botón al lado de cada PERSONAL. llamado CAMUFLAJE (dependiendo del idioma)"*). The button is the game's own
// widget: the kit movie's vehicle row symbol carries a button2 since the document's stage edit, the way the EQUIPOS rows carry
// two, and the row's own code shows it and fires Button2Released for it. Its label is the widget's BtnLabel2 property, which is
// ONE string for every language -- so this script puts our word for the player's language in its place as the widget is set
// up. The game has no text that says just CAMOUFLAGE (measured over its 15 515 strings: the camo window's title has the same
// problem and the same list), so the word is ours; a language not listed keeps the property's own text, a text id of the game.
//
// ⭐ THE ROWS OF OUR OWN (keku, 2026-09-26: *"esos vehiculos no tendran los iconos de gadgets, ni la estrella ni el boton de PERSONAL.
// solo añadiremos el de CAMUFLAJE… lo que si mantenemos es el nombre del vehiculo y su icono que están en el juego"*, the icon = the
// MINIMAP's, CAMUFLAJE on the right). The vehicles without a customization of their own get a row of the game's list through an asset
// of ours (ext/Shared/VehicleRows.lua): its label is the vehicle's name SID. Such a row is known by that label (the raw SID the game
// sends, or its text where the preview localises the payload) and dressed here as the data arrives: the class icon and its service
// star, the bar, the part slots and PERSONAL. go; CAMUFLAJE takes PERSONAL.'s place; the minimap icon (the kit movie's vehicle row
// carries a clip of mc_iconsIngameBig since the document's stage edit, where the class icon sits, hidden on the game's rows) shows
// there, on its frame. The
// same slot can be a row of the game's on the next update: everything this changes is put back for those. And Enter -- which fires
// PERSONAL. of the focused row on PC -- fires CAMUFLAJE on ours, their only button.
//
// ⭐ AFTER THE FIRST TEST IN THE GAME (keku, 2026-09-26: *"funciona todo correctamente"*, and three things): *"haz el icono del vehiculo
// algo mas grande como el vanilla"*, *"el icono del vehiculo no para de parpadear entre varios colores distintos, dejalo blanco de
// manera default como es vanilla"*, and a NARROW row -- only its header strip (icon, name, CAMUFLAJE), without the three part boxes
// below it that ours never fill. 📐 Measured offline (the editor's cache and the preview's converted icon movie):
//   . every vehicle frame of mc_iconsIngameBig places its picture as an inner clip named `icon`, 4 frames and NO stop: 1 = on a blue
//     disc (friendly), 2 = empty, 3 = on a green disc (squad), 4 = the WHITE glyph with a dark halo. The HUD picks one with
//     icon.gotoAndStop(team) (SupportIconTag); left alone it cycles -- the blink. The same for Jeep, Car, Boat, ATV, DirtBike,
//     JetBomber (and Tank) ⇒ _vehIconWhite.
//   . the white glyph is 9-17 px wide and 14-24 px tall at scale 1 (the Jeep 10 x 14, the transport helicopter 13 x 24); the class
//     icon of the game's rows draws ~25 x 26 px ⇒ every icon scaled to one height, _vehIconHeight (the Jeep at the 180 % keku
//     approved), by its own glyph's height (the table's third field). Scaled HERE, not in the kit movie: the list measures a row's
//     height from its bounds when it attaches it, and the clip's first frame must stay inside the row then (see
//     make_camomenu_doc.py, VEHICLE_MAP_ICON*).
//   . the row (KitView_2) is 580 x 109: the name at y 5, the buttons y 6-40, the plus y 3-25; the part boxes' labels start at y 42 and
//     their corner brackets at y 45 -- shapes without a name, so only a MASK hides them ⇒ _vehOwnHeight. The list lays every row at
//     index x its height and scrolls by whole rows of that height (KitView.setupSlots / scrollSlots / scrollBarUpdated), so once a
//     row of ours is shorter this lays the rows out one under the other and scrolls by their real heights; a list with no row of
//     ours is left to the game's own code.
// Each of the three has its old look at 0 (_vehOwnHeight 0 = the game's full row, _vehIconHeight 0 = the minimap's own size,
// _vehIconWhite 0 = the icon's own animation): the way back, and the seam's control.
//
// Parameters (besides CamoCommon's):
//   _vehRoot        the screen's root clip ("instance1")
//   _vehKitView     the list widget ("KitView_01")
//   _vehWords       the word by language: "en~CAMOUFLAGE;es~CAMUFLAJE;…" (the camo window's own title list)
//   _vehKind        "land" / "air", for the receipt
//   _vehOwnRows     our rows: "<name SID>~<minimap icon frame or empty>~<its white glyph's height, px>;…" (VehicleRowsData.lua,
//                   read by the document's generator, which adds the measured glyph heights)
//   _vehMapIcon     the minimap icon clip in the row ("vehMapIcon")
//   _vehOwnHeight   the height our rows are cut to, in px (0 = the game's full row)
//   _vehIconHeight  the height the icon's white glyph is drawn at on our rows, in px (0 = as the minimap draws it)
//   _vehIconWhite   the frame of the icon's inner picture shown on our rows (0 = its own animation)

var vehScreen = this[String(_vehRoot)];
var vehWords = {};
var vehWordParts = String(_vehWords).split(";");
var vehW = 0;
while(vehW < vehWordParts.length)
{
   var vehTilde = vehWordParts[vehW].indexOf("~");
   if(vehTilde > 0)
   {
      vehWords[vehWordParts[vehW].substr(0, vehTilde).toLowerCase()] = vehWordParts[vehW].substr(vehTilde + 1);
   }
   vehW = vehW + 1;
}

// the game's language, reduced to the two-letter key of the list: getLanguage() answers the engine's LanguageFormat INDEX as a
// string in the game (measured 2026-09-17: "3" on a Spanish client) and a two-letter code in the preview -- the camo window's rule
var vehLanguageIndexKeys = ["en", "fr", "de", "es", "it", "ja", "ru", "pl", "nl", "pt", "zh", "ko", "cs"];
var vehLanguageKey = function()
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
   if(!_global.isNaN(index) && index >= 0 && index < vehLanguageIndexKeys.length)
   {
      return vehLanguageIndexKeys[index];
   }
   var key = lang.substr(0, 2);
   if(key == "us") key = "en";
   if(key == "ge") key = "de";
   if(key == "jp") key = "ja";
   return key;
};

// our word for the language, else the property's own text through the game's localiser
var vehWord = function(fallback)
{
   var key = vehLanguageKey();
   if(key != "" && vehWords[key] != undefined)
   {
      return vehWords[key];
   }
   return camoLocalise(fallback);
};

// ⛔ A VEHICLE CLASS'S NAME IS LONGER THAN A KIT'S (measured on the preview in Spanish, 2026-09-23: "HELICÓPTEROS DE ATAQUE"
// ran under the buttons once the row had two -- the EQUIPOS layout was drawn for ASALTO / APOYO). The name keeps the
// game's size; when it does not fit before the buttons, its field ends there and the name SCROLLS through it (keku,
// 2026-09-23: *"en vez de hacer el texto más pequeño… que recorra de izquierda a derecha y se reinicie la animación para leerlo
// todo"*) -- the way the game scrolls its own long labels and the camo window scrolls a long camo name: the game's
// ScrollTextTimer (2.5 s still, 2 s forward, 1 s still, 0.5 s back, every label in step) drives the field's own horizontal
// scroll; without the timer (never in the game) a frame handler does the same. On the clip for the seam: _camoRoom = the width
// the name has, _camoRight = where its field ends, _camoScrolls = whether it scrolls.
var vehNameGap = 8;
var vehMakeScrollUpdate = function(txt)
{
   return function(val)
   {
      if(txt.maxhscroll > 0)
      {
         txt.hscroll = Math.round(txt.maxhscroll * val);
      }
   };
};
var vehMakeUnregister = function(fn)
{
   return function()
   {
      Util.ScrollTextTimer.getInstance().unregister(fn);
   };
};
var vehMarqueePause = 45;
var vehMakeMarquee = function(txt)
{
   var wait = vehMarqueePause;
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
         wait = vehMarqueePause;
         return undefined;
      }
      if(txt.hscroll >= txt.maxhscroll)
      {
         back = true;
         wait = vehMarqueePause;
         return undefined;
      }
      txt.hscroll = txt.hscroll + 1;
   };
};
var vehFitName = function(slot)
{
   var header = slot.header;
   var txt = header == undefined ? undefined : header.txt;
   // the name's room ends at the leftmost button shown (CAMUFLAJE, left of PERSONAL.)
   var button = undefined;
   if(slot.button1 != undefined && slot.button1._visible)
   {
      button = slot.button1;
   }
   if(slot.button2 != undefined && slot.button2._visible && (button == undefined || slot.button2._x < button._x))
   {
      button = slot.button2;
   }
   if(txt == undefined || button == undefined)
   {
      return undefined;
   }
   // the last name's scroll off, the field back to the game's width, then measured again
   var timer = Util != undefined && Util.ScrollTextTimer != undefined ? Util.ScrollTextTimer.getInstance() : undefined;
   if(header._camoScroll != undefined && timer != undefined)
   {
      timer.unregister(header._camoScroll);
   }
   header._camoScroll = undefined;
   delete header.onEnterFrame;
   if(header._camoWidth == undefined)
   {
      header._camoWidth = txt._width;   // the game's own box, taken once
   }
   txt._width = header._camoWidth;
   txt.hscroll = 0;
   var room = button._x - (header._x + txt._x) - vehNameGap;
   header._camoRoom = room;
   header._camoScrolls = false;
   // the field never reaches under the buttons (the game's box is 360 px, drawn for a row with one button)
   if(room > 0 && room < txt._width)
   {
      txt._width = room;
   }
   if(txt.textWidth > room && room > 0)
   {
      if(txt.maxhscroll > 0)
      {
         header._camoScrolls = true;
         if(timer != undefined)
         {
            var fn = vehMakeScrollUpdate(txt);
            header._camoScroll = fn;
            timer.register(fn);
            header.onUnload = vehMakeUnregister(fn);
         }
         else
         {
            header.onEnterFrame = vehMakeMarquee(txt);
         }
      }
   }
   header._camoRight = header._x + txt._x + txt._width;
   header._camoSize = txt.getTextFormat().size;   // the size it is drawn at: the game's, never touched
};

// our rows: name SID -> minimap icon frame ("" = none) and its white glyph's height; the text of each SID too, where the payload
// arrives localised (the preview)
var vehOwn = {};
var vehOwnSid = {};
var vehOwnGlyph = {};
var vehOwnParts = String(_vehOwnRows).split(";");
var vehO = 0;
while(vehO < vehOwnParts.length)
{
   var vehOwnBits = vehOwnParts[vehO].split("~");
   if(vehOwnBits.length >= 2 && vehOwnBits[0] != "")
   {
      var vehOwnKey = vehOwnBits[0];
      var vehOwnFrame = vehOwnBits[1];
      var vehOwnHigh = vehOwnBits.length >= 3 ? Number(vehOwnBits[2]) : 0;
      vehOwn[vehOwnKey] = vehOwnFrame;
      vehOwnSid[vehOwnKey] = vehOwnKey;
      vehOwnGlyph[vehOwnKey] = vehOwnHigh;
      var vehOwnText = String(camoLocalise(vehOwnKey));
      if(vehOwnText != "" && vehOwnText != vehOwnKey && vehOwnText != "undefined")
      {
         vehOwn[vehOwnText] = vehOwnFrame;
         vehOwnSid[vehOwnText] = vehOwnKey;
         vehOwnGlyph[vehOwnText] = vehOwnHigh;
      }
   }
   vehO = vehO + 1;
}

// the three looks of our rows (see the top): a value that is not a number is the old look
var vehOwnHeight = Number(_vehOwnHeight);
if(_global.isNaN(vehOwnHeight) || vehOwnHeight < 0)
{
   vehOwnHeight = 0;
}
var vehIconHeight = Number(_vehIconHeight);
if(_global.isNaN(vehIconHeight) || vehIconHeight < 0)
{
   vehIconHeight = 0;
}
// the icon's scale on a row of ours, %: its white glyph drawn _vehIconHeight tall (a glyph not measured: the minimap's own size)
var vehIconScale = function(label)
{
   var glyph = Number(vehOwnGlyph[label]);
   if(vehIconHeight <= 0 || _global.isNaN(glyph) || glyph <= 0)
   {
      return 100;
   }
   return 100 * vehIconHeight / glyph;
};
var vehIconWhite = Number(_vehIconWhite);
if(_global.isNaN(vehIconWhite) || vehIconWhite < 0)
{
   vehIconWhite = 0;
}
// the list's gap under each row (KitView.SLOT_PADDING)
var vehPad = 2;
if(Widget.CustomizeKit.KitView.SLOT_PADDING != undefined)
{
   vehPad = Number(Widget.CustomizeKit.KitView.SLOT_PADDING);
}

// the minimap icon's inner picture held on its white frame (it cycles blue / nothing / green / white when left alone). The inner
// clip exists as soon as the icon is on its frame; a player that builds it a frame later gets it set there (a few frames at most)
var vehWhiten = function(icon)
{
   delete icon.onEnterFrame;
   if(vehIconWhite <= 0)
   {
      return undefined;
   }
   if(icon.icon != undefined)
   {
      icon.icon.gotoAndStop(vehIconWhite);
      return undefined;
   }
   var tries = 10;
   icon.onEnterFrame = function()
   {
      tries = tries - 1;
      if(this.icon != undefined)
      {
         this.icon.gotoAndStop(vehIconWhite);
      }
      if(this.icon != undefined || tries <= 0)
      {
         delete this.onEnterFrame;
      }
   };
};

// a row of ours cut to its header strip: a mask as wide as the row and _vehOwnHeight tall (the part boxes' brackets are shapes
// without a name), its mouse area as tall; a row of the game's whole again. The mask is a child of the row, inside its bounds
// (the way the list masks itself with its own sizeDummy)
var vehCutRow = function(slot, own)
{
   if(own && vehOwnHeight > 0)
   {
      if(slot._vehCut == undefined)
      {
         var b = slot._vehBounds;
         var cut = slot.createEmptyMovieClip("vehRowCut", slot.getNextHighestDepth());
         cut.beginFill(0xFFFFFF, 100);
         cut.moveTo(b.xMin, b.yMin);
         cut.lineTo(b.xMax, b.yMin);
         cut.lineTo(b.xMax, vehOwnHeight);
         cut.lineTo(b.xMin, vehOwnHeight);
         cut.lineTo(b.xMin, b.yMin);
         cut.endFill();
         slot._vehCut = cut;
      }
      slot.setMask(slot._vehCut);
      if(slot.mouseArea != undefined)
      {
         slot.mouseArea._height = vehOwnHeight;
      }
      return undefined;
   }
   if(slot._vehCut != undefined)
   {
      slot.setMask(null);
      slot._vehCut.removeMovieClip();
      slot._vehCut = undefined;
   }
   if(slot.mouseArea != undefined && slot._vehMouseH != undefined)
   {
      slot.mouseArea._height = slot._vehMouseH;
   }
};

// the rows one under the other at their own heights (ours cut, the game's whole), and the scroll bar for that total. A list that
// has no row of ours -- and had none -- is not touched: the game's setupSlots has laid it out and set its bar
var vehLayOut = function(kit)
{
   var mixed = false;
   var i = 0;
   while(i < kit.m_slots.length)
   {
      if(kit.m_slots[i]._vehOwn == true && vehOwnHeight > 0)
      {
         mixed = true;
      }
      i = i + 1;
   }
   if(!mixed && kit._vehMixed != true)
   {
      return undefined;
   }
   var y = 0;
   i = 0;
   while(i < kit.m_slots.length)
   {
      var slot = kit.m_slots[i];
      slot._y = Math.floor(y);
      y = y + (slot._vehOwn == true && vehOwnHeight > 0 ? vehOwnHeight + vehPad : kit.m_slotHeight);
      i = i + 1;
   }
   kit._vehMixed = mixed;
   kit._vehTotal = Math.max(0, y - vehPad);
   if(kit.m_scrollbar != null)
   {
      // the bar reports to the list's scrollBarUpdated as it was when the bar was made: ours from now on (it hands the game's
      // own back whenever the list has no row of ours)
      kit.m_scrollbar.scrollUpdatedFunction = fb.Delegate.Create(kit, kit.scrollBarUpdated);
   }
   if(!mixed)
   {
      // back to the game's rows alone: its own bar, its own place for the rows
      if(kit.m_scrollbar != null)
      {
         kit.scrollBarUpdated(kit.m_scrollbar.pageJumpPerc);
      }
      else
      {
         kit.m_container._y = 0;
      }
      return undefined;
   }
   // a bar when the rows do not fit, with the game's own slack (a row counts as seen up to 4 px past the list's mask)
   if(kit._vehTotal > kit.m_mask._height + 4)
   {
      kit.addScrollbar();
      kit.m_scrollbar.scrollUpdatedFunction = fb.Delegate.Create(kit, kit.scrollBarUpdated);
      kit.m_scrollbar.setTagetSize(kit._vehTotal);
      kit.scrollBarUpdated(kit.m_scrollbar.pageJumpPerc);
      kit.scrollSlots(kit.m_index);
   }
   else
   {
      kit.removeScrollbar();
      kit.m_container._y = 0;
   }
};

// a row of ours dressed, or a row of the game's given back what an earlier dressing of the same slot changed
var vehDressRow = function(slot, data)
{
   if(slot == undefined)
   {
      return "";
   }
   // the game's own layout, taken once per slot (the row's code places both buttons when it is initialised)
   if(slot._vehHome == undefined)
   {
      slot._vehHome = true;
      slot._vehB1Seen = slot.button1._visible;
      slot._vehB2x = slot.button2._x;
      slot._vehB1x = slot.button1._x;
      slot._vehBounds = slot.getBounds(slot);          // the whole row, before anything here is cut or scaled
      if(slot.mouseArea != undefined)
      {
         slot._vehMouseH = slot.mouseArea._height;
      }
   }
   var label = data == undefined || data.Label == undefined ? "" : String(data.Label);
   var frame = vehOwn[label];
   var icon = slot[String(_vehMapIcon)];
   var s = 1;
   if(frame == undefined)
   {
      slot._vehOwn = false;
      slot.button1._visible = slot._vehB1Seen;
      slot.button2._x = slot._vehB2x;
      while(s <= 3)
      {
         if(slot["slotLabel" + s] != undefined)
         {
            slot["slotLabel" + s]._visible = true;
         }
         s = s + 1;
      }
      if(icon != undefined)
      {
         delete icon.onEnterFrame;
         icon.stop();
         if(icon.icon != undefined)
         {
            icon.icon.stop();
         }
         icon._xscale = icon._yscale = 100;
         icon._visible = false;
      }
      vehCutRow(slot, false);
      return "";
   }
   slot._vehOwn = true;
   slot.button1._visible = false;
   slot.button2._x = slot._vehB1x;
   if(slot.headerIcon != undefined)
   {
      slot.headerIcon._visible = false;
   }
   if(slot.bar != undefined)
   {
      slot.bar._visible = false;
   }
   while(s <= 3)
   {
      if(slot["slot" + s] != undefined)
      {
         slot["slot" + s]._visible = false;
      }
      if(slot["slotLabel" + s] != undefined)
      {
         slot["slotLabel" + s]._visible = false;
      }
      s = s + 1;
   }
   if(icon != undefined)
   {
      if(frame == "")
      {
         icon._visible = false;
      }
      else
      {
         icon.gotoAndStop(frame);
         vehWhiten(icon);
         icon._xscale = icon._yscale = vehIconScale(label);
         icon._x = slot.headerIcon != undefined ? slot.headerIcon._x : 18;
         icon._y = slot.headerIcon != undefined ? slot.headerIcon._y : 22;
         icon._visible = true;
      }
   }
   vehCutRow(slot, true);
   return vehOwnSid[label] + "=" + frame + (icon == undefined ? "(no clip)" : "");
};

var vehOwnSaid = "";
var vehKit = vehScreen == undefined ? undefined : vehScreen[String(_vehKitView)];
if(vehKit != undefined)
{
   // every time the rows get their data (the names are set there): our rows dressed, then each name fitted again
   var vehKitSlots = vehKit.updateSlots;
   vehKit.updateSlots = function(slots)
   {
      vehKitSlots.call(this, slots);
      var ours = [];
      var i = 0;
      while(i < this.m_slots.length)
      {
         var said = vehDressRow(this.m_slots[i], slots[i]);
         if(said != "")
         {
            ours.push(said);
         }
         vehFitName(this.m_slots[i]);
         i = i + 1;
      }
      vehLayOut(this);
      // the receipt, when it changes: "VOR<land|air>,<ours>/<rows>,<sid>=<frame>;…"
      var receipt = String(_vehKind) + "," + ours.length + "/" + this.m_slots.length + "," + ours.join(";");
      if(receipt != vehOwnSaid)
      {
         vehOwnSaid = receipt;
         camoSignal("VOR" + receipt);
      }
   };
   // with rows of ours the list scrolls by the rows' real heights (vehLayOut): the selected row brought into sight, and the bar's
   // percentage over the rows' total; without them, the game's own code
   var vehKitScroll = vehKit.scrollSlots;
   vehKit.scrollSlots = function(index)
   {
      if(this._vehMixed != true)
      {
         return vehKitScroll.call(this, index);
      }
      var slot = this.m_slots[index];
      var range = this._vehTotal - this.m_mask._height;
      if(this.m_scrollbar == null || slot == undefined || range <= 0)
      {
         return undefined;
      }
      var top = slot._y;
      var bottom = top + (slot._vehOwn == true ? vehOwnHeight : this.m_slotHeight - vehPad);
      var now = - this.m_container._y;
      var want = now;
      if(top < now)
      {
         want = top;
      }
      else if(bottom > now + this.m_mask._height)
      {
         want = bottom - this.m_mask._height;
      }
      else
      {
         return undefined;
      }
      want = Math.max(0, Math.min(range, want));
      this.m_scrollbar.setPercentage(want / range * 100);
   };
   var vehKitScrolled = vehKit.scrollBarUpdated;
   vehKit.scrollBarUpdated = function(percentage)
   {
      if(this._vehMixed != true)
      {
         return vehKitScrolled.call(this, percentage);
      }
      this.m_container._y = - Math.round(Math.max(0, this._vehTotal - this.m_mask._height) * percentage / 100);
   };
   // Enter on PC fires PERSONAL. (BTN_1) of the focused row; on a row of ours, whose only button is CAMUFLAJE, it fires that one
   var vehKitInput = vehKit.onInputConceptReleased;
   vehKit.onInputConceptReleased = function(concept)
   {
      if(this.m_focused && concept == fb.Input.InputConceptActions.Action_Activate)
      {
         var focused = this.m_slots[this.m_index];
         if(focused != undefined && focused._vehOwn == true)
         {
            focused.onButtonReleased(2);
            return undefined;
         }
      }
      return vehKitInput.call(this, concept);
   };
   var vehKitInit = vehKit.initialize;
   vehKit.initialize = function(initData)
   {
      var label = "";
      if(initData != undefined && initData.BtnLabel2 != undefined && String(initData.BtnLabel2) != "")
      {
         label = vehWord(String(initData.BtnLabel2));
         initData.BtnLabel2 = label;
      }
      vehKitInit.call(this, initData);
      // the receipt: "VRW<land|air>,<label>" -- the row script ran and what the second button reads
      camoSignal("VRW" + String(_vehKind) + "," + label);
   };
}
