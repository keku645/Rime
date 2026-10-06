// CamoMenu — a frame-1 action for the accessories screen (Data/CamoMenu.avm1 is this file compiled with
// External/keku/compile_as.py; the document's stage op `script:camomenu:<vars>:<base64>` puts it on the screen's timeline).
//
// The camo row of the accessories screen (a KitSelector: one item at a time, arrows to step) becomes a picker the way the newer
// game does it: the row loses its arrows (left/right do nothing there); a click anywhere on the row — or Activate on the row —
// opens a panel beside the rows: eight family buttons (MISC, ADAPTIVE, AUTUMN, DESERT, NAVAL, SNOW, URBAN, WOODLAND) and a Grid
// of thumbnails (KitCell: picture + name, two to a row, each as wide as a family button and laid out on the buttons' grid) listing the camos of the
// family picked; the grid's own vertical scrollbar appears when the family has more rows than the grid shows. The info box that
// sits where the panel goes moves under the rows while the panel is open and follows the thumbnail under the mouse (the camo's
// description, through the row's own info event). Picking a cell selects that camo THROUGH THE ROW (KitSelectorSlot.select fires the row's SetIndex with the unlock's
// identifier), so the shipped wiring — SetAccessory4 → AccessoryChanged → the engine's store action — does the rest; the row
// itself shows the pick as before. The panel closes when another row takes the focus.
//
// Keyboard / controller: the camo row keeps the focus while the panel is open, so the engine keeps handing it the input concepts
// (measured in game: Up/Down/Left/Right and Activate reach the focused row as Pressed/Released; Back/Esc is taken by the screen's
// graph before any widget sees it, so it leaves the screen as always and cannot close the panel). With the panel open the arrows
// move a cursor instead of moving the focus: over the family buttons (two columns, four rows — the mouse-over look marks the one
// under the cursor), Down out of the buttons into the grid (the grid's own selection is the cursor there, its scrollbar follows),
// Up out of the grid's first row back to the buttons, Up out of the buttons' first row closes the panel. Activate on a button picks
// the family (the cursor stays on it); Activate on a cell picks the camo through the row and closes the panel — the choice made,
// the rows take the keys again. Activate on the closed row opens the panel with the cursor on the family worn; a panel the mouse
// opened has no cursor until the first key, which only shows it.
//
// Everything is done on the INSTANCES the screen places (never on a shared class: the wrappers die with the screen and the
// game's other grids, buttons and rows are untouched); the wrappers are set here before the engine initialises the widgets, so
// the grid's data handler is ours from its first call. Closures come from NAMED factories (a function literal called in place
// does not compile right).
//
// Parameters, set by the injector as variables of this timeline:
//   _camoRoot      the screen's root clip ("instance1")
//   _camoRow       the camo row widget ("KitSelector_05")
//   _camoButtons   the family buttons the document added, in the families' order ("CamoCat_01,CamoCat_02,…")
//   _camoGrid      the grid the document added ("CamoGrid")
//   _camoInfo      the info box the panel covers ("KitInfoBox_01"; "" = none): parked under the rows while the panel is open
//   _camoInfoAt    where it parks, "x,y,scale%" ("100,592,80")
//   _camoCell      the thumbnail cells: "width,gap" in px ("224,6") — two columns as wide as the family buttons above, the same gap
//                  between columns and between rows, so the cells line up under the buttons
//   _camoFamilies  "FAMILY~keyword,keyword;FAMILY~keyword;…" in the buttons' order; a camo belongs to the first family with a
//                  keyword in its (localised) name, else to the family with no keywords (MISC)

var camoScreen = this[String(_camoRoot)];
var camoRowName = String(_camoRow);
var camoGridName = String(_camoGrid);
var camoInfoName = String(_camoInfo);
var camoInfoAt = String(_camoInfoAt).split(",");
var camoInfoHome = undefined;   // the info box's own place and scale, taken the first time the panel opens
var camoCellSpec = String(_camoCell).split(",");
var camoCellWidth = camoCellSpec.length >= 1 && !_global.isNaN(Number(camoCellSpec[0])) ? Number(camoCellSpec[0]) : 224;
var camoCellGap = camoCellSpec.length >= 2 && !_global.isNaN(Number(camoCellSpec[1])) ? Number(camoCellSpec[1]) : 6;
var camoCellScale = camoCellWidth / Widget.Grid.Cell.KitCell.KIT_CELL_WIDTH * 100;   // the game's 113x47 kit cell scaled to the button's width
var camoCellColumns = 2;
var camoButtonNames = String(_camoButtons).split(",");
var camoFamilyText = String(_camoFamilies);

var camoItems = [];        // the camo row's items as the engine hands them (Label, ItemImage, Index, DefaultSelected…)
var camoShown = [];        // grid cell i -> index into camoItems
var camoFamily = 0;        // the family button picked
var camoPanelOn = false;   // whether the panel is open
var camoFamilies = [];     // [{name, keys:[…]}]
var camoZone = "";         // the keyboard cursor: "" = none yet (the mouse opened the panel), "buttons" = on a family button, "grid" = on a cell
var camoCursor = 0;        // the family button under the cursor while camoZone == "buttons" (on the grid the cursor is the grid's selectedIndex)
var camoButtonColumns = 2; // the family buttons' layout: two columns, the rows the document's count of buttons makes

var camoParseFamilies = function(text)
{
   var out = [];
   var parts = text.split(";");
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
camoFamilies = camoParseFamilies(camoFamilyText);

// the camo's name as the player reads it: an ID_* label goes through the game's localiser (the same proxy the widgets use)
var camoLabelText = function(item)
{
   var label = String(item.Label);
   if(label.indexOf("ID_") == 0 && _global.Data != undefined && _global.Data.UILocalizeComp != undefined)
   {
      var text = _global.Data.UILocalizeComp.formatString(label);
      if(text != undefined && String(text) != "")
      {
         label = String(text);
      }
   }
   return label;
};

// the family of a camo: the first family with a keyword in its name (raw id and localised text both tried), else the one without keywords
var camoFamilyOf = function(item)
{
   var raw = String(item.Label).toLowerCase();
   var text = camoLabelText(item).toLowerCase();
   var fallback = 0;
   var i = 0;
   while(i < camoFamilies.length)
   {
      var fam = camoFamilies[i];
      if(fam.keys.length == 0)
      {
         fallback = i;
      }
      var k = 0;
      while(k < fam.keys.length)
      {
         var key = fam.keys[k];
         if(key.length > 0 && (raw.indexOf(key) >= 0 || text.indexOf(key) >= 0))
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
// the newer game names its patterns — a cell is 113 px wide; "No Camo" and other two-word names stay whole
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

var camoGridClip = function()
{
   return camoScreen[camoGridName];
};
var camoRowClip = function()
{
   return camoScreen[camoRowName];
};
var camoInfoClip = function()
{
   return camoInfoName == "" || camoInfoName == "undefined" ? undefined : camoScreen[camoInfoName];
};
var camoButtonClip = function(i)
{
   return camoScreen[camoButtonNames[i]];
};
var camoSlotClip = function()
{
   var row = camoRowClip();
   return row == undefined || row.mcContainer == undefined ? undefined : row.mcContainer.Slot_0;
};

// a cell's content loaded (the next frame after its data): the name in a font that fits the label (the cell is scaled, the text with it)
var camoMakeCellLoad = function(orig)
{
   var load = function()
   {
      orig.call(this);
      var txt = this.m_label == undefined ? undefined : this.m_label.txt;
      if(txt != undefined)
      {
         var fmt = new TextFormat();
         fmt.size = 10;   // the cell is scaled about twice: 10 pt reads as the buttons' text
         txt.setNewTextFormat(fmt);
         txt.setTextFormat(fmt);
      }
   };
   load._camoWrap = true;
   return load;
};

// the info box: parked under the rows while the panel is open (the panel takes its place), back home when it closes. The box's art
// sits far from its clip's origin (the clip is placed at (0,0), the panel draws around (750,360)), so the park position is where the
// ART's top-left goes: the offset from origin to art, measured once at home (getBounds), scaled with the box
var camoParkInfo = function(on)
{
   var info = camoInfoClip();
   if(info == undefined)
   {
      return undefined;
   }
   if(camoInfoHome == undefined)
   {
      var b = info.getBounds(info._parent);
      var s = info._xscale == 0 ? 100 : info._xscale;
      camoInfoHome = {x:info._x, y:info._y, scale:s, offX:(b.xMin - info._x) * 100 / s, offY:(b.yMin - info._y) * 100 / s};
   }
   if(on && camoInfoAt.length >= 3)
   {
      var scale = Number(camoInfoAt[2]);
      info._xscale = scale;
      info._yscale = scale;
      info._x = Number(camoInfoAt[0]) - camoInfoHome.offX * scale / 100;
      info._y = Number(camoInfoAt[1]) - camoInfoHome.offY * scale / 100;
   }
   else
   {
      info._x = camoInfoHome.x;
      info._y = camoInfoHome.y;
      info._xscale = camoInfoHome.scale;
      info._yscale = camoInfoHome.scale;
   }
   // where the art's top-left is now (for anyone measuring the box from outside)
   info._camoArtX = info._x + camoInfoHome.offX * info._xscale / 100;
   info._camoArtY = info._y + camoInfoHome.offY * info._yscale / 100;
};

// the thumbnail under the mouse: its camo's description to the info box, the way the row's own hover does it (the row's info
// event, wired to the box by the screen's graph)
var camoShowInfo = function(index)
{
   var row = camoRowClip();
   if(row == undefined || row.fireGeneralInfoEvent == undefined || index < 0 || index >= camoShown.length)
   {
      return undefined;
   }
   row.fireGeneralInfoEvent(camoItems[camoShown[index]].Description);
   // the box shows on a row's hover (its OnItemOver → OnShow wire) and hides when the mouse leaves the row: over a thumbnail it is shown here
   var info = camoInfoClip();
   if(info != undefined && info.show != undefined)
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

// the family buttons: the one of the family picked shows selected
var camoMarkButtons = function()
{
   var i = 0;
   while(i < camoButtonNames.length)
   {
      var btn = camoButtonClip(i);
      if(btn != undefined)
      {
         btn.selected = i == camoFamily;
      }
      i = i + 1;
   }
};

// --- the keyboard cursor (see the header): where it is, published on the row for anyone measuring from outside
var camoPublishCursor = function()
{
   var row = camoRowClip();
   if(row != undefined)
   {
      row._camoZone = camoZone;
      row._camoCursor = camoCursor;
   }
};
// a button's look under the cursor: the plain mouse-over look — also on the family picked, whose own overSelected look draws the same
// pixels as its selected look (measured in the seam: the cursor would not be seen there); off the cursor the button's normal look,
// which its state turns into the selected look on the family picked
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
// the cursor's look put back (a refresh resets the buttons' looks when it marks the family picked)
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
// the cursor onto a cell: the grid's own selection (the cell's selected look, the scrollbar following it), the camo's description
// to the info box the way the mouse over the cell does; false when the family has no cell to go to
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
var camoClearCursor = function()
{
   if(camoZone == "buttons")
   {
      camoButtonLook(camoCursor, false);
   }
   camoZone = "";
   camoPublishCursor();
};
// an arrow inside the panel. Buttons: two columns, Up/Down a row, Down out of the last row into the grid (the same column), Up out of
// the first row closes the panel. Grid: its own moveSelection (bounds, the scrollbar following the cell), but Up out of its first row
// goes back to the buttons' last row. No cursor yet (the mouse opened the panel): the first arrow only shows it, on the family picked
var camoNavigate = function(concept)
{
   if(camoZone == "")
   {
      camoCursorToButton(camoFamily);
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
         else
         {
            camoSetPanel(false);
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
   if(concept == fb.Input.InputConceptActions.Action_NavigateUp && index < grid.columns)
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
// Activate inside the panel: on a button its own release (the family picked, the cursor stays on it); on a cell the camo picked through
// the row and the panel closed; no cursor yet: the cursor shows
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
      var index = grid == undefined ? -1 : grid.selectedIndex;
      var slot = camoSlotClip();
      var row = camoRowClip();
      if(index >= 0 && index < camoShown.length && slot != undefined && slot.select != undefined)
      {
         if(row != undefined && row.playSound != undefined)
         {
            row.playSound(fb.Defines.SOUND_SELECT);
         }
         slot.select(camoShown[index], true);
      }
      camoSetPanel(false);
   }
   else
   {
      camoCursorToButton(camoFamily);
   }
};

// the grid shows the camos of the family picked: KitCell reads Label and ImagePath
var camoRefresh = function()
{
   var grid = camoGridClip();
   if(grid == undefined)
   {
      return undefined;
   }
   camoShown = [];
   var cells = [];
   var i = 0;
   while(i < camoItems.length)
   {
      var item = camoItems[i];
      if(camoFamilyOf(item) == camoFamily)
      {
         camoShown.push(i);
         cells.push({Label:camoCellLabel(item), ImagePath:String(item.ItemImage), Index:item.Index, IsExcluded:false});
      }
      i = i + 1;
   }
   if(Widget.Grid.Grid != undefined && Widget.Grid.Grid.prototype != undefined && Widget.Grid.Grid.prototype.updateGridItemsData != undefined)
   {
      Widget.Grid.Grid.prototype.updateGridItemsData.call(grid, cells);
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
            // a camo thumbnail is a 256x64 band: the cell's picture (an ImageManager, loaded on the cell's next frame) fits it into the
            // cell instead of showing it at its pixel size over the neighbours; the label's font a size that shows the name whole
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
      grid.columns = camoCellColumns;
      grid.rows = Math.ceil(n / camoCellColumns);
      grid.rowHeight = ch;
      var panelWidth = camoCellColumns * cw + (camoCellColumns - 1) * camoCellGap;
      grid.setupMask(panelWidth);
      if(grid.rows * ch - camoCellGap > grid.gridHeight)
      {
         // the bar by its right edge (that is how addScrollbar places it): a gap past the second column
         grid.addScrollbar(panelWidth + camoCellGap + 14, grid.rows, ch);
      }
      else
      {
         grid.removeScrollbar();
      }
   }
   // a grid whose family has no camos draws nothing and keeps the old cells: clear them
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
   // the camo the row has selected, highlighted in the grid
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
   camoMarkButtons();
   camoShowCursor();
};

// the family of the camo the row wears now (the panel opens on it)
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

var camoSetPanel = function(on)
{
   camoPanelOn = on;
   var grid = camoGridClip();
   if(grid != undefined)
   {
      grid._visible = on;
   }
   var i = 0;
   while(i < camoButtonNames.length)
   {
      var btn = camoButtonClip(i);
      if(btn != undefined)
      {
         btn._visible = on;
      }
      i = i + 1;
   }
   camoParkInfo(on);
   if(on)
   {
      camoFamily = camoFamilyOfSelected();
      camoRefresh();
   }
   else
   {
      camoClearCursor();
   }
};

var camoTogglePanel = function()
{
   camoSetPanel(!camoPanelOn);
};

// --- the grid: the row's payload {Items, ItemCategory, ShowStepPlus} comes in through the binding as GridItems
var camoGrid = camoGridClip();
if(camoGrid != undefined)
{
   camoGrid.updateGridItemsData = function(data)
   {
      if(data != undefined && data.Items != undefined)
      {
         camoItems = fb.Base.UIBase.ObjectToArray(data.Items);
      }
      else if(data != undefined)
      {
         camoItems = fb.Base.UIBase.ObjectToArray(data);
      }
      else
      {
         camoItems = [];
      }
      if(camoItems == undefined)
      {
         camoItems = [];
      }
      camoRefresh();
   };
   // the widget's own initialize ends with _visible = true: the panel starts closed
   var camoGridInit = camoGrid.initialize;
   camoGrid.initialize = function(initData)
   {
      camoGridInit.call(this, initData);
      this._visible = camoPanelOn;
   };
   // a cell released: the camo picked, selected through the row (its SetIndex carries the unlock's identifier)
   var camoGridMouse = camoGrid.cellMouseStateHandler;
   camoGrid.cellMouseStateHandler = function(event, index)
   {
      camoGridMouse.call(this, event, index);
      if(event == Widget.Grid.Cell.BaseCell.MOUSE_EVENT_OVER)
      {
         camoShowInfo(index);
      }
      else if(event == Widget.Grid.Cell.BaseCell.MOUSE_EVENT_OUT)
      {
         camoHideInfo();
      }
      if(event == Widget.Grid.Cell.BaseCell.MOUSE_EVENT_RELEASE && index >= 0 && index < camoShown.length)
      {
         var slot = camoSlotClip();
         if(slot != undefined && slot.select != undefined)
         {
            slot.select(camoShown[index], true);
         }
      }
   };
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
      this._visible = camoPanelOn;
   };
};
var camoB = 0;
while(camoB < camoButtonNames.length)
{
   var camoBtn = camoButtonClip(camoB);
   if(camoBtn != undefined)
   {
      camoBtn.initialize = camoMakeButtonInit(camoBtn.initialize, camoB);
   }
   camoB = camoB + 1;
}

// --- the camo row: no arrows, a click anywhere on it opens / closes the panel, Activate opens it (inside, the keys are the cursor's),
// losing the focus closes it
var camoNoStep = function()
{
};
var camoMakePress = function(orig, slot)
{
   var press = function()
   {
      orig();
      camoTogglePanel();
   };
   press._camoWrap = true;
   return press;
};
// the arrows off, the row's press a toggle, left/right idle — on a slot that has initialised (its buttons and mouse area exist then).
// Every initialise of the slot (one per data the row gets: after each pick too) sets a fresh onPress (KitViewSlot.setupMouseInteraction),
// so the press is wrapped again whenever the one there is not ours — keyed on the FUNCTION, not on the slot (measured in game
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
// the row attaches its slot in updateSetupData and initialises it on the NEXT frame (KitView.setupSlots: onEnterFrame → initializeSlots →
// slot.initialize): the disarming rides on the slot's initialize; a slot already initialised is disarmed on the spot
var camoPatchSlot = function()
{
   var slot = camoSlotClip();
   if(slot == undefined || slot._camoPatched == true)
   {
      return undefined;
   }
   slot._camoPatched = true;
   // every initialise of this slot (now or on the next frame, and after each data) ends with the disarming
   slot.initialize = camoMakeSlotInit(slot.initialize);
   if(slot.m_mcMouseArea != undefined)
   {
      camoDisarmSlot(slot);
   }
};
var camoRow = camoRowClip();
if(camoRow != undefined)
{
   var camoRowSetup = camoRow.updateSetupData;
   camoRow.updateSetupData = function(data)
   {
      camoRowSetup.call(this, data);
      camoPatchSlot();
   };
   // the panel open: the arrows move the cursor inside it instead of the focus to the neighbouring rows (the row's own handler is
   // not called for them); everything else is the row's as before
   var camoRowPressed = camoRow.onInputConceptPressed;
   camoRow.onInputConceptPressed = function(concept)
   {
      if(this.m_focused && camoPanelOn && (concept == fb.Input.InputConceptActions.Action_NavigateUp || concept == fb.Input.InputConceptActions.Action_NavigateDown || concept == fb.Input.InputConceptActions.Action_NavigateLeft || concept == fb.Input.InputConceptActions.Action_NavigateRight))
      {
         camoNavigate(concept);
         return undefined;
      }
      camoRowPressed.call(this, concept);
   };
   // Activate: the panel closed → open, the cursor on the family worn (a key opened it); open → the cursor's pick
   var camoRowReleased = camoRow.onInputConceptReleased;
   camoRow.onInputConceptReleased = function(concept)
   {
      camoRowReleased.call(this, concept);
      if(this.m_focused && concept == fb.Input.InputConceptActions.Action_Activate)
      {
         if(camoPanelOn)
         {
            camoActivate();
         }
         else
         {
            camoSetPanel(true);
            camoCursorToButton(camoFamily);
         }
      }
   };
   var camoRowUnfocused = camoRow.onUnfocused;
   camoRow.onUnfocused = function()
   {
      camoRowUnfocused.call(this);
      camoSetPanel(false);
   };
}
