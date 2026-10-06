// TextSize — a frame-1 action the editor puts on a screen's timeline (Data/TextSize.avm1 is this file compiled with
// External/keku/compile_as.py; the stage op `textsize:<clip>:<px>` sets `_rueTextSize` on the clip's construct variables and
// adds this script once per movie, with `_rueRoot` = the screen's root clip).
//
// A text widget draws its text at the size its row symbol carries (m_rowType = bold1 is 20 px, baseText0 32 px…): the game
// offers no other size, and scaling the placement changes the box, not the glyphs (the widget resets its own scale). This
// script gives a text widget the size the document asks for: after every text the engine hands it (updateTextData, from the
// init and every refresh), the field's text format takes the size and the field grows to fit (autoSize follows m_align),
// so the glyphs — the game's own font, vector — come out at that size.
//
// Everything is done on the INSTANCES of this screen (the wrappers die with it; the widget class is untouched), through a
// named factory (a function literal called in place does not compile right).

var rueRootClip = this[String(_rueRoot)];

var rueTextField = function(w)
{
   var row = w.m_textField;
   if(row == undefined)
   {
      row = w[String(w.m_rowType)];
   }
   if(row == undefined)
   {
      return undefined;
   }
   var t = row.txtDisplay;
   if(t == undefined)
   {
      t = row.txt;
   }
   return t;
};

var rueApplyTextSize = function(w)
{
   var px = Number(w._rueTextSize);
   if(_global.isNaN(px) || px <= 0)
   {
      return undefined;
   }
   var t = rueTextField(w);
   if(t == undefined)
   {
      return undefined;
   }
   var f = new TextFormat();
   f.size = px;
   t.setNewTextFormat(f);
   t.setTextFormat(f);
   var align = String(w.m_align);
   t.autoSize = align == "center" ? "center" : (align == "right" ? "right" : "left");
};

var rueMakeTextWrap = function(w, orig)
{
   return function(data)
   {
      var r = orig.call(this, data);
      rueApplyTextSize(this);
      return r;
   };
};

if(rueRootClip != undefined)
{
   for(var k in rueRootClip)
   {
      var w = rueRootClip[k];
      if(typeof(w) == "movieclip" && w._rueTextSize != undefined && typeof(w.updateTextData) == "function")
      {
         w.updateTextData = rueMakeTextWrap(w, w.updateTextData);
         rueApplyTextSize(w);
      }
   }
}
