// UiRecorder — a frame-1 action injected on the main timeline of a screen movie (Data/UiRecorder.avm1 is this file
// compiled; External/keku/compile_recorder.py rebuilds it). The game's UI system hands a screen everything its widgets
// show through two calls on the screen clip: initializeScreen(data) with one init record per widget (InstanceName,
// HasFocus, NumEvents, Event_i, Align, ZDepthLevel, the widget properties and the bound data channels) and
// refreshScreen([{widgetName, widgetData}]) for every later data update. This action wraps both ON THE INSTANCE of the
// screen clip (never a shared class: the wrapper dies with the screen and touches nothing else), serialises what
// arrives as JSON and sends it out: inside the editor's preview through ExternalInterface ("rec"), inside the game as
// bits over requestTypingInput — the same frame the AS2 log channel decodes (0x55 | len | bytes | xor, MSB first) —
// in numbered chunks "#R<run>.<kind><seq>|<chunk>|<chunks>|<len>|<payload>" that the editor's Import recording… reassembles.
// While the screen initialises, the component calls the widgets make (getData, formatString) are captured too, with
// the engine's answers, and restored right after; nothing outside that synchronous window is touched. After the init,
// the traffic between the engine and the widgets (input concepts, focus, in-events, fireEvent, requestFocus) is noted on
// the widget instances and sent as "event" records every half second (see rueWrapWidget below).
// The two variables in front are set by the injector: _rueScreen (the screen's name) and _rueRoot (its root clip's name).

var rueScreen = String(_rueScreen);
var rueRootName = String(_rueRoot);
if(_global._rueRuns == undefined)
{
   _global._rueRuns = 0;
}
_global._rueRuns = _global._rueRuns + 1;
var rueRun = _global._rueRuns;
var rueSeq = 0;
var rueChunk = 180;

var rueHex = function(n)
{
   var digits = "0123456789abcdef";
   var s = "";
   var i = 0;
   while(i < 4)
   {
      s = digits.charAt(n & 15) + s;
      n = n >> 4;
      i = i + 1;
   }
   return s;
};

var rueEscape = function(str)
{
   var out = "";
   var i = 0;
   var n = str.length;
   while(i < n)
   {
      var c = str.charCodeAt(i);
      if(c == 34)
      {
         out = out + "\\\"";
      }
      else if(c == 92)
      {
         out = out + "\\\\";
      }
      else if(c < 32 || c > 126)
      {
         out = out + "\\u" + rueHex(c);
      }
      else
      {
         out = out + str.charAt(i);
      }
      i = i + 1;
   }
   return out;
};

var rueJson = function(v, depth)
{
   var t = typeof(v);
   if(v == undefined || v == null)
   {
      return "null";
   }
   if(t == "string")
   {
      return "\"" + rueEscape(v) + "\"";
   }
   if(t == "number")
   {
      if(isNaN(v) || !isFinite(v))
      {
         return "null";
      }
      return String(v);
   }
   if(t == "boolean")
   {
      return v ? "true" : "false";
   }
   if(t == "function" || t == "movieclip")
   {
      return undefined;
   }
   if(depth > 12)
   {
      return "\"<deep>\"";
   }
   if(v instanceof Array)
   {
      var a = "[";
      var i = 0;
      while(i < v.length)
      {
         var e = rueJson(v[i], depth + 1);
         if(i > 0)
         {
            a = a + ",";
         }
         a = a + (e == undefined ? "null" : e);
         i = i + 1;
      }
      return a + "]";
   }
   var o = "{";
   var first = true;
   for(var k in v)
   {
      var m = rueJson(v[k], depth + 1);
      if(m != undefined)
      {
         if(!first)
         {
            o = o + ",";
         }
         o = o + "\"" + rueEscape(String(k)) + "\":" + m;
         first = false;
      }
   }
   return o + "}";
};

// one frame of the AS2 log channel: 0x55 | len | bytes | xor(len, bytes), MSB first, then a closing false
var rueSend = function(str)
{
   var comp = _global.Data.UIWidgetEventComp;
   if(comp == undefined)
   {
      return false;
   }
   var n = str.length;
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
   return true;
};

var rueEmit = function(rid, json)
{
   if(flash.external.ExternalInterface.available)
   {
      flash.external.ExternalInterface.call("rue", "rec", "record", [rid, json]);
      return true;
   }
   var total = Math.ceil(json.length / rueChunk);
   if(total < 1)
   {
      total = 1;
   }
   var i = 0;
   while(i < total)
   {
      var piece = json.substr(i * rueChunk, rueChunk);
      rueSend("#R" + rid + "|" + (i + 1) + "|" + total + "|" + piece.length + "|" + piece);
      i = i + 1;
   }
   return true;
};

// wraps comp.method for the duration of one call, recording (arguments -> answer) into store; returns the restorer
var rueCapture = function(compName, method, store)
{
   var comp = _global.Data[compName];
   if(comp == undefined || typeof(comp[method]) != "function")
   {
      return undefined;
   }
   var orig = comp[method];
   comp[method] = function()
   {
      var r = orig.apply(comp, arguments);
      var key = String(arguments[0]);
      var j = 1;
      while(j < arguments.length)
      {
         key = key + "," + String(arguments[j]);
         j = j + 1;
      }
      store[key] = r;
      return r;
   };
   return function()
   {
      comp[method] = orig;
   };
};

// apply() on the engine's own functions is verified once on a harmless call; without it nothing is captured
var rueCanCapture = function()
{
   var comp = _global.Data.UILocalizeComp;
   if(comp == undefined || typeof(comp.getLanguage) != "function")
   {
      return false;
   }
   var r = comp.getLanguage.apply(comp, []);
   return typeof(r) == "string";
};

var rueRecord = function(kind, payloadName, payload, origFn, target, arg)
{
   var seq = rueSeq;
   rueSeq = rueSeq + 1;
   var rid = rueRun + "." + kind.charAt(0) + seq;
   // serialised before the call: what the engine handed over, untouched by the widgets
   var body = rueJson(payload, 0);
   var answers = new Object();
   var texts = new Object();
   var restore = new Array();
   var captured = rueCanCapture();
   if(captured)
   {
      restore.push(rueCapture("UIDataInterfaceComp", "getData", answers));
      restore.push(rueCapture("UILocalizeComp", "formatString", texts));
   }
   var result = origFn.call(target, arg);
   var q = 0;
   while(q < restore.length)
   {
      if(restore[q] != undefined)
      {
         restore[q]();
      }
      q = q + 1;
   }
   var json = "{\"screen\":\"" + rueEscape(rueScreen) + "\",\"root\":\"" + rueEscape(rueRootName) + "\",\"run\":" + rueRun + ",\"kind\":\"" + kind + "\",\"seq\":" + seq + ",\"captured\":" + (captured ? "true" : "false") + ",\"" + payloadName + "\":" + (body == undefined ? "null" : body) + ",\"getData\":" + rueJson(answers, 0) + ",\"texts\":" + rueJson(texts, 0) + "}";
   rueEmit(rid, json);
   return result;
};

// --- the traffic between the engine and the widgets after the init: what the engine delivers to a widget (input concepts, focus,
// the in-events of its ports) and what the widget sends back (fireEvent with the event id of its table, requestFocus). Wrapped on
// the widget INSTANCES the init record names (their class methods stay untouched), buffered, and flushed as one "event" record
// every half second while the screen lives: {kind:"event", seq, events:[{t, w, m, a, r}]} — t = ms since the movie started.
var rueEvents = new Array();
var rueList = function(v)
{
   if(v instanceof Array)
   {
      return v;
   }
   var out = new Array();
   if(v == undefined)
   {
      return out;
   }
   var n = Number(v.length);
   var i = 0;
   while(i < n)
   {
      out.push(v[i]);
      i = i + 1;
   }
   return out;
};
// what a note keeps of a value: its SHAPE, never a whole payload — a row's list with its descriptions is tens of kilobytes, and every
// character goes out as eight synchronous calls on the typing channel: noting whole payloads froze the client on every key (measured
// 2026-09-16: the player got disconnected changing weapons). A string is cut to 60 characters, an array-like becomes "[n]" and the
// shape of its first element, an object the list of its keys (up to 8)
var rueBrief = function(v, depth)
{
   var t = typeof(v);
   if(v == undefined || v == null || t == "number" || t == "boolean")
   {
      return v;
   }
   if(t == "string")
   {
      return v.length > 60 ? v.substr(0, 60) + "…(" + v.length + ")" : v;
   }
   if(t == "function" || t == "movieclip")
   {
      return "<" + t + ">";
   }
   var n = v.length;
   if(v instanceof Array || (typeof(n) == "number" && n >= 0 && v[0] != undefined))
   {
      return "[" + n + "]" + (n > 0 && depth < 1 ? " of " + String(rueBrief(v[0], depth + 1)) : "");
   }
   var keys = "";
   var count = 0;
   for(var k in v)
   {
      if(count < 8)
      {
         keys = keys + (count > 0 ? "," : "") + k;
      }
      count = count + 1;
   }
   return "{" + keys + (count > 8 ? ",…" + count : "") + "}";
};
var rueNote = function(widget, method, args, result)
{
   var a = new Array();
   var i = 0;
   while(i < args.length && i < 3)
   {
      a.push(rueBrief(args[i], 0));
      i = i + 1;
   }
   rueEvents.push({t:getTimer(), w:widget, m:method, a:a, r:rueBrief(result, 0)});
};
var rueFlush = function()
{
   if(rueEvents.length == 0)
   {
      return undefined;
   }
   var batch = rueEvents;
   rueEvents = new Array();
   // a burst of traffic (a held key) is capped per flush: the rest is counted, not sent
   if(batch.length > 40)
   {
      var dropped = batch.length - 40;
      batch = batch.slice(0, 40);
      batch.push({t:getTimer(), w:"", m:"dropped", a:[dropped], r:undefined});
   }
   var seq = rueSeq;
   rueSeq = rueSeq + 1;
   rueEmit(rueRun + ".e" + seq, "{\"screen\":\"" + rueEscape(rueScreen) + "\",\"root\":\"" + rueEscape(rueRootName) + "\",\"run\":" + rueRun + ",\"kind\":\"event\",\"seq\":" + seq + ",\"events\":" + rueJson(batch, 0) + "}");
};
// one wrapper: the original called first (on the widget, with the call's arguments), then the note. A named factory, not a
// function literal called in place: the compiler emitted that form as the factory itself sitting on the widget (its calls returned
// the inner function and did nothing), which left the screen without focus.
var rueMakeWrap = function(name, m, orig)
{
   return function()
   {
      var r = orig.apply(this, arguments);
      rueNote(name, m, arguments, r);
      return r;
   };
};
var rueWrapWidget = function(w, name)
{
   if(w == undefined || typeof(w) != "movieclip")
   {
      return undefined;
   }
   // the engine's calls into a widget: input, focus, in-events — and the data paths (the game re-fed the accessory rows after a weapon
   // change without one refreshScreen on the screen: measured 2026-09-16), so the widget's own refresh entry and its data handlers are
   // noted too, with what they receive
   var names = ["onInputConceptPressed", "onInputConceptReleased", "onFocused", "onUnfocused", "handleInEvent", "requestFocus",
      "refresh", "setupRefreshData", "updateSetupData", "updateVisibilityData", "updateTextData", "updateGridItemsData", "updateselectedIndexData",
      "updateButtonItemData", "updateHeaderData", "updateSubHeaderData", "updateIconData", "updateButtonsData", "updateDefaultButtonSetData",
      "updateVisibleData", "updateInputOnReleaseData", "updateSquadBoostersData", "updateDescriptionData", "updateDataData"];
   var i = 0;
   while(i < names.length)
   {
      var method = names[i];
      if(typeof(w[method]) == "function")
      {
         w[method] = rueMakeWrap(name, method, w[method]);
      }
      i = i + 1;
   }
   // fireEvent(index, param, param2): the index picks the event id from the widget's own table (m_events, filled by init)
   if(typeof(w.fireEvent) == "function")
   {
      var origFire = w.fireEvent;
      w.fireEvent = function(event, param, param2)
      {
         var r = origFire.apply(this, arguments);
         rueNote(name, "fireEvent", [this.m_events == undefined ? event : this.m_events[event], param, param2], r);
         return r;
      };
   }
};
var rueWrapWidgets = function(root, data)
{
   var list = rueList(data);
   var i = 0;
   while(i < list.length)
   {
      var rec = list[i];
      if(rec != undefined && rec.InstanceName != undefined)
      {
         rueWrapWidget(root[String(rec.InstanceName)], String(rec.InstanceName));
      }
      i = i + 1;
   }
};

var rueRoot = this[rueRootName];
if(rueRoot != undefined && typeof(rueRoot.initializeScreen) == "function")
{
   var rueOrigInit = rueRoot.initializeScreen;
   var rueOrigRefresh = rueRoot.refreshScreen;
   rueRoot.initializeScreen = function(data)
   {
      // the wrappers go on before the init runs: the focus the engine gives while initialising is traffic too
      rueWrapWidgets(this, data);
      var r = rueRecord("init", "widgets", data, rueOrigInit, this, data);
      var flushId = setInterval(function()
      {
         // the screen gone (popped): its clip has no name any more — the wrappers died with it, the timer goes too
         if(rueRoot._name == undefined)
         {
            clearInterval(flushId);
            return undefined;
         }
         rueFlush();
      }, 500);
      return r;
   };
   rueRoot.refreshScreen = function(widgetList)
   {
      return rueRecord("refresh", "list", widgetList, rueOrigRefresh, this, widgetList);
   };
   // the screen leaving (the engine's exitScreen): what happened in the last half second goes out before the clip dies
   if(typeof(rueRoot.exitScreen) == "function")
   {
      var rueOrigExit = rueRoot.exitScreen;
      rueRoot.exitScreen = function()
      {
         rueNote("", "exitScreen", [], undefined);
         rueFlush();
         return rueOrigExit.apply(this, arguments);
      };
   }
   rueEmit(rueRun + ".h0", "{\"screen\":\"" + rueEscape(rueScreen) + "\",\"root\":\"" + rueEscape(rueRootName) + "\",\"run\":" + rueRun + ",\"kind\":\"hello\"}");
}
