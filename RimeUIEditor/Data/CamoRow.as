// CamoRow — a frame-1 action for the accessories screen (compiled behind CamoCommon.as by External/keku/make_camomenu_doc.py;
// the document's stage op `script:camorow:<vars>:<base64>` puts it on the screen's timeline).
//
// The camo row of the accessories screen (a KitSelector: one item at a time, arrows to step) becomes the door to the camo
// screen: it shows the name and the thumbnail of the camo worn (ours from the table, the game's as they come), loses its
// arrows (left/right do nothing there), and a click anywhere on it — or Activate on it — fires its Button1Released event
// with the camo's identifier, which the screen's graph wires to the CamoButton output: the flow graph replaces this screen
// with the camo screen. The descriptions live on the camo screen; the row's own hover still feeds the info box whatever the
// item carries.
//
// Everything is done on the INSTANCES the screen places (never on a shared class); closures come from NAMED factories (a
// function literal called in place does not compile right).
//
// Parameters (besides CamoCommon's): _camoRoot (the screen's root clip, "instance1"), _camoRow (the camo row, "KitSelector_05")
//   _camoLoadoutRows  the rows whose equipped item goes to the client's Lua when the door opens, in the order the Lua expects
//                     (weapon, accessory 1-3, camo: "KitSelector_01,…,KitSelector_05"); a row's slot keeps the equipped item's Index as m_id

var camoScreen = this[String(_camoRoot)];
var camoRowName = String(_camoRow);
var camoLoadoutRows = String(_camoLoadoutRows).split(",");

var camoRowClip = function()
{
   return camoScreen[camoRowName];
};
var camoSlotClip = function()
{
   var row = camoRowClip();
   return row == undefined || row.mcContainer == undefined ? undefined : row.mcContainer.Slot_0;
};

// what the player has on the weapon, for the client's Lua (the weapon view composes the weapon from it): the equipped item's
// identifier of each row, sent as one frame "WVL<weapon>,<acc1>,<acc2>,<acc3>,<camo>" the moment the door event leaves
var camoLoadout = function()
{
   // a screen with no loadout rows (the LOADOUT screen's SIDEARM door) has nothing of the primary weapon to say: an empty frame
   // would wipe what the view knows of it
   if(camoLoadoutRows.length == 0 || (camoLoadoutRows.length == 1 && camoLoadoutRows[0] == ""))
   {
      return undefined;
   }
   var ids = new Array();
   var i = 0;
   while(i < camoLoadoutRows.length)
   {
      var row = camoScreen[camoLoadoutRows[i]];
      var slot = row == undefined || row.mcContainer == undefined ? undefined : row.mcContainer.Slot_0;
      ids.push(slot != undefined && slot.m_id != undefined ? camoIdKey(slot.m_id) : "");
      i = i + 1;
   }
   camoSignal("WVL" + ids.join(","));
};

// the weapon on the screen: the first loadout row is the weapon's own, and its slot keeps the unlock's id
var camoWeaponId = function()
{
   var row = camoScreen[camoLoadoutRows[0]];
   var slot = row == undefined || row.mcContainer == undefined ? undefined : row.mcContainer.Slot_0;
   return slot != undefined && slot.m_id != undefined ? camoIdKey(slot.m_id) : "";
};

// the door: the row's Button1Released with the camo worn (what Activate on the row fires by itself on PC), wired to the screen's
// CamoButton output by the graph
var camoOpen = function()
{
   var row = camoRowClip();
   var slot = camoSlotClip();
   if(row == undefined || row.fireEvent == undefined)
   {
      return undefined;
   }
   var id = slot != undefined && slot.m_id != undefined ? slot.m_id : 0;   // the slot keeps the item's Index as m_id (KitViewSlot.updateData)
   row.playSound(fb.Defines.SOUND_SELECT);
   row.fireEvent(Widget.CustomizeKit.KitView.BTN_1_RELEASED, id);
};

var camoNoStep = function()
{
};
var camoMakePress = function(orig, slot)
{
   var press = function()
   {
      orig();
      camoOpen();
   };
   press._camoWrap = true;
   return press;
};
// the arrows off, the row's press the door, left/right idle — on a slot that has initialised (its buttons and mouse area exist
// then). Every initialise of the slot (one per data the row gets) sets a fresh onPress (KitViewSlot.setupMouseInteraction), so
// the press is wrapped again whenever the one there is not ours — keyed on the FUNCTION, not on the slot (measured in game
// 2026-09-16: a slot-level flag left the second click dead after the first pick)
var camoDisarmSlot = function(slot)
{
   if(slot.m_mcButtonLeft != undefined)
   {
      slot.m_mcButtonLeft._visible = false;
   }
   if(slot.m_mcButtonRight != undefined)
   {
      slot.m_mcButtonRight._visible = false;
   }
   slot.stepLeft = camoNoStep;
   slot.stepRight = camoNoStep;
   if(slot.m_mcMouseArea != undefined && slot.m_mcMouseArea.onPress != undefined && slot.m_mcMouseArea.onPress._camoWrap != true)
   {
      slot.m_mcMouseArea.onPress = camoMakePress(slot.m_mcMouseArea.onPress, slot);
   }
};
var camoMakeSlotInit = function(orig)
{
   return function(owner, index, buttonLabel1, buttonLabel2)
   {
      orig.call(this, owner, index, buttonLabel1, buttonLabel2);
      camoDisarmSlot(this);
   };
};
// the row attaches its slot in updateSetupData and initialises it on the NEXT frame (KitView.setupSlots: onEnterFrame →
// initializeSlots → slot.initialize): the disarming rides on the slot's initialize; a slot already initialised is disarmed on the spot
var camoPatchSlot = function()
{
   var slot = camoSlotClip();
   if(slot == undefined || slot._camoPatched == true)
   {
      return undefined;
   }
   slot._camoPatched = true;
   slot.initialize = camoMakeSlotInit(slot.initialize);
   if(slot.m_mcMouseArea != undefined)
   {
      camoDisarmSlot(slot);
   }
};

// ---- THE MAILBOX ON THIS SCREEN ----------------------------------------------------------------------------------
// ⭐ WHY IT IS A CLIP OF OUR OWN (keku, 2026-09-21: *"no queremos tomar prestado nada vanilla que ya se esté usando"*).
// The four camo screens are ours, so there the live table rides on the binding of the grid we built. This screen is the
// GAME'S: appending a source to a row's binding would be hanging our data off a widget of DICE's that is already busy,
// so instead the document places an EMPTY TEXTFIELD of ours off screen (character the movie already imports, a free
// depth) whose only job is to be the address the table is delivered to. Not one of the game's ten clips is touched.
var camoMailName = String(_camoMail);

var camoMailClip = function()
{
   return camoMailName == "" || camoMailName == "undefined" ? undefined : camoScreen[camoMailName];
};

var camoCountTable = function()
{
   var n = 0;
   for(var id in camoTable)
   {
      n = n + 1;
   }
   return n;
};

// ⛔ THE ROW MAY ALREADY BE DRESSED WHEN THE TABLE ARRIVES. The row reads the table when its items come in, so a camo the
// BAKED table did not know would keep the engine's empty name and no thumbnail if the delivery lost that race. The row's
// last payload is therefore kept as it CAME (items before they were rewritten), and dressing it again is idempotent: the
// rewrite keys on the item's Index, which nothing touches. Only the camo row is redone -- the accessory rows consume the
// pick the player made (`_camoPicked`), and replaying that would move a row behind his back.
var camoRowReapply = function()
{
   var row = camoRowClip();
   if(row == undefined || row._camoData == undefined || row._camoItems0 == undefined || row._camoSetup == undefined)
   {
      return 0;
   }
   var data = row._camoData;
   data.Items = camoRewriteItems(row._camoItems0);
   row._camoSetup.call(row, data);
   camoPatchSlot();
   return 1;
};

// ⛔⛔ AND THE ACCESSORY ROWS NEED THE SAME NET — LEAVING THEM OUT IS WHAT BROKE HIS PICK (keku, 2026-09-21: *"si
// cambio de accesorio, entro en la ventana de camo y aplico el camo y vuelvo, no se guarda ni el camo ni el
// accesorio"*). A row folds its items the moment the engine feeds it, and on this screen that happens BEFORE the
// table is delivered — measured in his log, five seconds apart:
//     01:13:48  row fold 1,13,0,13,drop,x3325141568      <- 13 accessories, ZERO twins, our twin thrown away
//     01:13:53  the mailbox TBL2,6496,str                <- the table arrives
// With an empty table the fold does not recognise the twin (the `x` prefix is that branch: "no name and the table
// does not know it"), so it is dropped from the row ENTIRELY and no amount of timing on the pick can find it later.
// The baked table used to hide this race; emptying it (2026-09-20) left it in the open. Only the FOLD is redone
// here -- never camoPutRowOn, which would move a row behind the player's back.
var camoAccReapply = function()
{
   var done = 0;
   var i = 0;
   while(i < camoAccRows.length)
   {
      var row = camoAccRowClip(camoAccRows[i]);
      if(row != undefined && row._camoAccItems0 != undefined && row._camoAccSetup != undefined)
      {
         var data = row._camoAccData;
         data.Items = camoCollapseAccessoryItems(row._camoAccItems0, camoWantedFor(row._camoAccSlotNumber),
            row._camoAccSlotNumber);
         row._camoAccSetup.call(row, data);
         done = done + 1;
      }
      i = i + 1;
   }
   return done;
};

var camoRow = camoRowClip();
if(camoRow != undefined)
{
   // the door event, however it is fired (a click through camoOpen, Activate through the widget itself): the loadout goes first
   var camoRowFire = camoRow.fireEvent;
   camoRow.fireEvent = function(event, param, param2)
   {
      if(event == Widget.CustomizeKit.KitView.BTN_1_RELEASED)
      {
         camoLoadout();
      }
      return camoRowFire.call(this, event, param, param2);
   };
   // the row's data at the door: the items made ours (name, thumbnail, description from the table; carriers read and dropped)
   var camoRowSetup = camoRow.updateSetupData;
   camoRow._camoSetup = camoRowSetup;
   camoRow.updateSetupData = function(data)
   {
      if(data != undefined && data.Items != undefined)
      {
         // kept BEFORE the rewrite, so a table that arrives late can dress the same payload again
         this._camoData = data;
         this._camoItems0 = fb.Base.UIBase.ObjectToArray(data.Items);
         data.Items = camoRewriteItems(data.Items);
      }
      camoRowSetup.call(this, data);
      camoPatchSlot();
   };
   // what the row applied last, for anyone measuring from outside
   camoRow._camoTableCount = camoTableCount;
}

// ---- the accessory rows: the same door, and they KEEP their arrows ------------------------------------------------------
// keku's spec (2026-09-18): "aparte de cambiar de accesorio con las flechas, cuando haga click en el box de cada accesorio le
// llevará a la ventana de camos específica del accesorio". So these rows are not disarmed like the camo one: the arrows go on
// stepping through the accessories, and a press opens that row's own screen (Activate fires the same event by itself).
// _camoAccRows: the rows, in slot order ("KitSelector_02,KitSelector_03,KitSelector_04" = optics, underbarrel, accessory).

var camoAccRows = String(_camoAccRows) == "" || String(_camoAccRows) == "undefined" ? [] : String(_camoAccRows).split(",");
// _camoAccSlots: each row's slot NAME, in the rows' order ("" = their position, 1..3: the accessories screen). It is what the
// screen the row opens shares with it through _global (_camoOpenSlot, _camoPickedSlot), so a row of another screen -- the
// LOADOUT's SIDEARM row, "S" -- must never answer to an accessory slot's number.
var camoAccSlots = String(_camoAccSlots) == "" || String(_camoAccSlots) == "undefined" ? [] : String(_camoAccSlots).split(",");
// _camoAccOnly: the unlocks each row's door opens on, rows apart by ';' (in the rows' order) and ids by ','; a row with none opens on
// anything. ⭐ keku 2026-10-06: *"el usuario solo puede abrir la ventana de camo en el gadget cuando SOLO aparezca la ballesta, o la
// ballesta con mira"* -- the gadget rows carry the crossbow's two unlocks; a camo of one (the table names it as its base) opens too.
var camoAccOnlyParts = String(_camoAccOnly) == "" || String(_camoAccOnly) == "undefined" ? [] : String(_camoAccOnly).split(";");
var camoAccOnlyBySlot = {};
var camoO = 0;
while(camoO < camoAccOnlyParts.length)
{
   if(camoAccOnlyParts[camoO] != "")
   {
      var camoOnlyIds = camoAccOnlyParts[camoO].split(",");
      var camoOnlyMap = {};
      var camoOI = 0;
      while(camoOI < camoOnlyIds.length)
      {
         camoOnlyMap[camoIdKey(camoOnlyIds[camoOI])] = true;
         camoOI = camoOI + 1;
      }
      camoAccOnlyBySlot[String(camoO < camoAccSlots.length && camoAccSlots[camoO] != "" ? camoAccSlots[camoO] : camoO + 1)] = camoOnlyMap;
   }
   camoO = camoO + 1;
}
// may this row's door open on that item? (no list = yes; a camo twin answers with the unlock it is a camo of)
var camoAccAllowed = function(slotNumber, id)
{
   var only = camoAccOnlyBySlot[String(slotNumber)];
   if(only == undefined)
   {
      return true;
   }
   var base = camoBaseOf({Index:id});
   return only[camoIdKey(id)] == true || (base != "" && only[camoIdKey(base)] == true);
};

var camoAccRowClip = function(name)
{
   return camoScreen[name];
};

var camoAccOpen = function(name, slotNumber)
{
   var row = camoAccRowClip(name);
   var slot = row == undefined || row.mcContainer == undefined ? undefined : row.mcContainer.Slot_0;
   if(row == undefined || row.fireEvent == undefined)
   {
      return undefined;
   }
   var id = slot != undefined && slot.m_id != undefined ? slot.m_id : 0;
   // ⛔⛔ AN ATTACHMENT THIS WEAPON DRAWS NOTHING FOR HAS NO WINDOW (keku 2026-09-19: *"cuando el jugador intente hacer
   // click para que se abra esta ventana no ocurra nada"*). The M240 has no iron sights, a heavy barrel has no socket in
   // BF3, the "No…" rows are placeholders: a camo window on one of those is an empty room, so the door does not open
   // -- no message to translate, nothing to explain. The framework says which (weapon, attachment) pairs those are.
   if(camoDrawsNothing(camoWeaponId(), id))
   {
      camoSignal("WVD" + String(slotNumber) + "," + camoIdKey(id));
      return undefined;
   }
   // a gadget row opens only on the crossbow: anything else shown there has no camo window
   if(!camoAccAllowed(slotNumber, id))
   {
      camoSignal("WVO" + String(slotNumber) + "," + camoIdKey(id));
      return undefined;
   }
   // ⛔ WHAT THE ROW SHOWS IS NOT YET WHAT THE GAME HAS STORED (keku, 2026-09-18, after finding it himself: "para que se
   // guarde realmente la óptica debes dejar el accesorio puesto, volver atrás y volver a accesorios"). Stepping with the
   // arrows is what stores it -- KitSelectorSlot.select(n, true) fires SELECTOR_CHANGED, which is the wire into
   // SetAccessoryN. So opening RE-SELECTS what is already there: nothing moves on screen, and the engine now holds the
   // accessory the player is looking at. Without it the screen opened on whatever was stored before (his Réflex).
   // what the row actually holds, to the client's Lua (and from there to the mod's database): the index it thinks is
   // current, how many items it has, the id of that item and the id the slot reports -- the three have to agree
   var before = slot == undefined ? "-" : String(slot.m_current) + "/" + String(slot.m_data == undefined ? -1 : slot.m_data.length);
   var atCurrent = slot == undefined || slot.m_data == undefined || slot.m_data[slot.m_current] == undefined
      ? "-" : camoIdKey(slot.m_data[slot.m_current].Index);
   camoSignal("WVR" + String(slotNumber) + ",open," + before + "," + atCurrent + "," + camoIdKey(id));

   if(slot != undefined && slot.select != undefined && slot.m_current != undefined)
   {
      slot.select(slot.m_current, true);
      if(slot.m_id != undefined)
      {
         id = slot.m_id;
      }
   }
   // and the row tells the screen which accessory this is about, in case the stored value has not caught up yet (unsigned:
   // the screen looks it up in the table, whose keys are unsigned -- camoIdKey)
   _global._camoOpenBase = camoIdKey(id);
   _global._camoOpenSlot = String(slotNumber);
   row.playSound(fb.Defines.SOUND_SELECT);
   row.fireEvent(Widget.CustomizeKit.KitView.BTN_1_RELEASED, id);
};

// ⛔⛔ THE DOOR IS THE RELEASE, NOT THE PRESS (measured 2026-09-18, three boots of keku: "AUNQUE SELECCIONE LA ACOG SIGUE
// SALIENDO LA REFLEX"). Opening on the press hands the screen over while the mouse button is still DOWN, so the release
// lands on whatever is under the cursor in the NEW screen -- and an accessory screen puts its grid exactly where the rows
// are (y 140), so the release picked the cell under the pointer: the one top right, the Réflex. The camo screen never
// showed it because its row is the last one, far below its own grid. The slot's own press is left alone (it only focuses
// the row: KitViewSlot.activate -> selectSlot), and the screen opens when the button comes back up, with nothing pending.
var camoMakeAccRelease = function(name, slotNumber)
{
   var release = function()
   {
      camoAccOpen(name, slotNumber);
   };
   release._camoWrap = true;
   return release;
};

// The release is set on the slot's mouse area (the arrows stay: they are their own buttons), and set again on every
// initialise of the slot -- the widget hands its mouse area fresh handlers each time the row gets data, so the mark goes on
// the FUNCTION, not on the slot.
var camoArmAccSlot = function(slot, name, slotNumber)
{
   if(slot == undefined || slot.m_mcMouseArea == undefined)
   {
      return undefined;
   }
   if(slot.m_mcMouseArea.onRelease == undefined || slot.m_mcMouseArea.onRelease._camoWrap != true)
   {
      slot.m_mcMouseArea.onRelease = camoMakeAccRelease(name, slotNumber);
   }
};

var camoWantedFor = function(slotNumber)
{
   if(_global._camoPicked != undefined && String(_global._camoPickedSlot) == String(slotNumber))
   {
      return String(_global._camoPicked);
   }
   return undefined;
};

// puts the row on that accessory, through the widget's own selection (which also stores it); does nothing when the row is
// already there, so the data the pick sends back does not start a loop
var camoPutRowOn = function(slot, wanted, slotNumber)
{
   if(slot == undefined || slot.m_data == undefined || wanted == undefined)
   {
      camoSignal("WVR" + String(slotNumber) + ",back,-,-," + String(wanted));
      return undefined;
   }
   camoSignal("WVR" + String(slotNumber) + ",back," + String(slot.m_current) + "/" + String(slot.m_data.length) + "," +
      (slot.m_data[slot.m_current] == undefined ? "-" : camoIdKey(slot.m_data[slot.m_current].Index)) + "," + String(wanted));
   // consumed here, whatever happens next: applied once, and never again on later updates of the row
   if(_global._camoPicked != undefined && String(_global._camoPickedSlot) == String(slotNumber))
   {
      _global._camoPicked = undefined;
   }
   var i = 0;
   while(i < slot.m_data.length)
   {
      if(camoIdKey(slot.m_data[i].Index) == camoIdKey(wanted))
      {
         if(slot.m_current != i)
         {
            camoSignal("WVR" + String(slotNumber) + ",move," + String(slot.m_current) + "->" + String(i) + ",-," + String(wanted));
            slot.select(i, true);
         }
         return undefined;
      }
      i = i + 1;
   }
};

var camoMakeAccSlotInit = function(orig, name, slotNumber)
{
   return function(owner, index, buttonLabel1, buttonLabel2)
   {
      orig.call(this, owner, index, buttonLabel1, buttonLabel2);
      camoArmAccSlot(this, name, slotNumber);

      // ⛔⛔ AND HERE IS WHERE THE PICK LANDS, NOT IN updateSetupData (keku 2026-09-21: *"si cambio de accesorio, entro
      // en la ventana de camo y aplico el camo directamente y vuelvo, no se guarda ni el camo ni el accesorio"*).
      // The row attaches its slot while it takes its data and INITIALISES IT ON THE NEXT FRAME, so the call that used
      // to sit right after `orig.call` always found `m_data` empty -- his log says it in one line, every time:
      // `row trace 1,back,-,-,3325141568` (the two dashes ARE "the slot has no items yet"). With the pick never
      // applied, the row was left pointing at nothing and fell onto a neighbour -- his PK-A came back as a Ballistic
      // Scope -- and the store on the way out then wrote THAT. It works from here because the slot has its items.
      // ⛔ and only once the slot HAS its items: KitView.initializeSlots calls slot.initialize BEFORE updateSlots hands the slot
      // its data (KitSelectorSlot.updateData sets m_data), so on a slot made fresh with the screen this is too early -- measured
      // 2026-10-06 on the LOADOUT's SIDEARM row: "WVRS,back,-,-,<pick>" and the pick never applied. The data's own arrival is
      // wrapped below (camoMakeAccSlotData); this call stays for a slot that is only being re-initialised with its items.
      if(camoWantedFor(slotNumber) != undefined && this.m_data != undefined)
      {
         camoPutRowOn(this, camoWantedFor(slotNumber), slotNumber);
      }
   };
};

// the slot's items arriving (KitSelectorSlot.updateData: m_data, then the stored item selected): the pick made on the row's own
// screen goes on here, once -- camoPutRowOn consumes the mark, so a later update (an arrow) never moves the row back
var camoMakeAccSlotData = function(orig, slotNumber)
{
   return function(data)
   {
      orig.call(this, data);
      if(camoWantedFor(slotNumber) != undefined && this.m_data != undefined)
      {
         camoPutRowOn(this, camoWantedFor(slotNumber), slotNumber);
      }
   };
};

// What this row should be showing, when it is not what the game stored: the accessory picked on that row's own screen, or
// the one we confirmed on the way in. The row rebuilds itself from the stored value every time it gets data
// (KitSelectorSlot.updateData walks the items for DefaultSelected), so coming back from the screen puts it on the OLD one
// (keku, 2026-09-18: "si estaba la réflex y cambio a la acog y entro, al salir vuelve a estar la réflex").
// ⛔⛔ ONLY WHAT THE PLAYER PICKED ON THE SCREEN, AND ONLY ONCE (keku, 2026-09-18: "poner la acog con las flechas hace que
// se siga poniendo la réflex, en cambio haciendo click sí se pone"). This runs on EVERY update of the row, and an arrow is
// an update: stepping to the ACOG re-sent the data, this put the row back on the id remembered from the last time the
// screen was opened, and the row bounced to the old accessory. The id the row opened with (_camoOpenBase) is for the
// SCREEN to know what it is about -- it must never move a row. The pick is consumed the first time it is applied.

// ⛔⛔ THE LOADOUT'S FIRST ENTRY NEVER GETS THE TABLE (keku's runs 342 and 347, his mod.db): its mailbox answered ("MRW…") only
// when he came BACK from a window, never on the first entry -- and that entry's fold threw the pistol's camo twins out of the row
// ("row fold S,18,0,18,drop,x2143872018,…": nameless and unknown to the table), so a twin the player wears would have nowhere to
// stand. The value our writer sets is in the game's data store whether or not the binding hands it over, and the game's own
// scripts read that store synchronously (fb.HelperFunctions: _global.Data.UIDataInterfaceComp.getData(<key>)): so the row
// reads it itself before it folds. Only a text that carries the table's marker counts (the key holds the event's payload when
// the writer had nothing), and only a delivery of a new length is parsed again. Receipt: "MPL<entries>,<new>,<length>".
var camoTableKey = Number(_camoTableKey);
var camoPulledLength = -1;
var camoPullTable = function()
{
   if(isNaN(camoTableKey) || camoTableKey == 0 || _global.Data == undefined || _global.Data.UIDataInterfaceComp == undefined)
   {
      return 0;
   }
   var value = _global.Data.UIDataInterfaceComp.getData(camoTableKey);
   var got = camoMailText(value);
   if(got.text.indexOf(camoTableMarker) != 0)
   {
      // what the store held instead, once per screen: "MPN<length>,<shape>" -- whether the first entry's writer ran with nothing
      // (the loadout made before the table existed) or the read itself found nothing, which is the next thing to decide
      if(camoPulledLength == -1)
      {
         camoPulledLength = -2;
         camoSignal("MPN" + got.text.length + "," + got.shape);
         // ⛔ AND THE STORE WAS EMPTY ON THE FIRST ENTRY (keku's run 349: "MPN0,none"; run 350 the same, and the row then sat on
         // another pistol, 17/18, which the door's re-select would have STORED). The framework had written the table into the
         // writer node long before (05:20:53, the entry 05:21:26) -- what never happened on that entry is the writer FIRING. So the
         // row fires it: our mailbox clip raises the same event a row's box raises on its release (the game's own event id, read
         // off this screen's first weapon row), and the loadout wires CamoMail.OnItemReleased into CamoTableSet -- which sets the
         // key, the binding hands the table to camoMail.updateCamoTableData, and that folds the rows again (MRW…). "MPF<fired>".
         var mailClip = camoMailClip();
         var firstRow = camoAccRows.length > 0 ? camoAccRowClip(camoAccRows[0]) : undefined;
         var fired = 0;
         if(mailClip != undefined && firstRow != undefined && firstRow.m_events != undefined &&
            _global.Data.UIWidgetEventComp != undefined)
         {
            _global.Data.UIWidgetEventComp.fireEvent(String(mailClip), firstRow.m_events[Widget.CustomizeKit.KitView.BTN_1_RELEASED], 0);
            fired = 1;
         }
         camoSignal("MPF" + fired);
      }
      return 0;
   }
   if(got.text.length == camoPulledLength)
   {
      return 0;
   }
   camoPulledLength = got.text.length;
   var before = camoCountTable();
   camoMailbox(value);
   var after = camoCountTable();
   // and the rows that already folded without it fold again (run 352: the SIDEARM row folded a moment BEFORE the table came in
   // through here, on the next row's data -- "drop,x…" then "MPL8,8" -- and the mailbox's MRW that followed found nothing new)
   var refolded = after > before ? camoAccReapply() : 0;
   camoSignal("MPL" + after + "," + (after - before) + "," + got.text.length + "," + refolded);
   return after - before;
};

// Moved below camoWantedFor/camoPutRowOn on purpose: it CALLS them (deferred, from the slot's own initialise),
// and the order of the `var`s is the order of execution in this file -- a law this codebase has paid for.
var camoMakeAccSetup = function(orig, name, slotNumber)
{
   return function(data)
   {
      // the table first, from the data store, when the mailbox has not brought it (see camoPullTable)
      camoPullTable();
      if(data != undefined && data.Items != undefined)
      {
         // kept AS IT CAME, so a table that lands after this row folded can fold it again (see camoAccReapply)
         this._camoAccData = data;
         this._camoAccItems0 = fb.Base.UIBase.ObjectToArray(data.Items);
         this._camoAccSetup = orig;
         this._camoAccSlotNumber = slotNumber;
         // ⛔ NOT camoRewriteItems here: that dresses every twin as the CAMO, which is right in the grid of that row's own
         // screen and wrong in the row itself (keku 2026-09-19: "me están saliendo los camos de prueba o el no data/blank").
         // In the row a twin is the ACCESSORY it is a camo of, and only the worn one (or the one just picked) is kept.
         data.Items = camoCollapseAccessoryItems(data.Items, camoWantedFor(slotNumber), slotNumber);
      }
      orig.call(this, data);
      // back from that row's screen: put the row on what the player left it with -- if the slot ALREADY has its items
      // (a row that is only being re-fed). The first time round it does not, and the pick is applied from the slot's
      // own initialise instead; asking here as well costs nothing and keeps the case where the slot is already up.
      var back = this.mcContainer == undefined ? undefined : this.mcContainer.Slot_0;

      if(camoWantedFor(slotNumber) != undefined && back != undefined && back.m_data != undefined)
      {
         camoPutRowOn(back, camoWantedFor(slotNumber), slotNumber);
      }

      var slot = this.mcContainer == undefined ? undefined : this.mcContainer.Slot_0;
      if(slot != undefined && slot._camoPatched != true)
      {
         slot._camoPatched = true;
         slot.initialize = camoMakeAccSlotInit(slot.initialize, name, slotNumber);
         slot.updateData = camoMakeAccSlotData(slot.updateData, slotNumber);
         camoArmAccSlot(slot, name, slotNumber);
      }
   };
};

var camoA = 0;
while(camoA < camoAccRows.length)
{
   var camoAccName = camoAccRows[camoA];
   var camoAccRow = camoAccRowClip(camoAccName);
   if(camoAccRow != undefined && camoAccRow.updateSetupData != undefined && camoAccRow._camoArmed != true)
   {
      camoAccRow._camoArmed = true;
      camoAccRow._camoSlotName = camoA < camoAccSlots.length && camoAccSlots[camoA] != "" ? camoAccSlots[camoA] : camoA + 1;
      camoAccRow.updateSetupData = camoMakeAccSetup(camoAccRow.updateSetupData, camoAccName, camoAccRow._camoSlotName);
      // the loadout goes to the client's Lua on the way out, as it does for the camo row
      var camoAccFire = camoAccRow.fireEvent;
      camoAccRow._camoFire = camoAccFire;
      camoAccRow.fireEvent = function(event, param, param2)
      {
         if(event == Widget.CustomizeKit.KitView.BTN_1_RELEASED)
         {
            // the same door, however it is opened: Activate on a controller or the keyboard fires this event by
            // itself, without going through the mouse handler, so the gate lives here too -- an attachment the
            // weapon draws nothing for has no window to open.
            if(camoDrawsNothing(camoWeaponId(), param))
            {
               camoSignal("WVD" + camoIdKey(param));
               return undefined;
            }
            // and a gadget row only on the crossbow (Activate fires this event without the mouse handler: the gate is here too)
            if(!camoAccAllowed(this._camoSlotName, param))
            {
               camoSignal("WVO" + String(this._camoSlotName) + "," + camoIdKey(param));
               return undefined;
            }
            camoLoadout();
         }
         return this._camoFire.call(this, event, param, param2);
      };
   }
   camoA = camoA + 1;
}

// ---- THE MAILBOX GOES LAST ON PURPOSE ---------------------------------------------------------------------
// It re-applies the rows when the table lands late, so everything it touches -- the camo row's payload, the
// accessory rows and their wrappers -- has to exist by the time it is installed. The order of the `var`s in
// this file IS the order of execution.
var camoMail = camoMailClip();
if(camoMail != undefined)
{
   camoMail.updateCamoTableData = function(data)
   {
      // ⛔ THE DELIVERED LENGTH IS PART OF THE RECEIPT, or the line cannot tell "the whole table arrived" from
      // "nothing arrived and the entries were already baked": both leave the same count behind. It is read the
      // same way the mailbox reads it, before the mailbox consumes it.
      var got = camoMailText(data);
      var before = camoCountTable();
      camoMailbox(data);
      var after = camoCountTable();
      // the receipt IS the measurement: how many entries there are now, how many the baked table did not have,
      // how much text the delivery carried, and whether the row had to be dressed again -- every time, because a
      // silent re-apply and a race that never happened look the same in the log otherwise.
      // the fifth field is the accessory rows folded again: without it their twins stay dropped (the fold ran before
      // this delivery), and the camo the player picks on one of their screens has nowhere to land in the row
      camoSignal("MRW" + after + "," + (after - before) + "," + got.text.length + "," +
         (after > before ? camoRowReapply() : 0) + "," + (after > before ? camoAccReapply() : 0));
   };
   camoWatchRefresh(camoMail);
}
