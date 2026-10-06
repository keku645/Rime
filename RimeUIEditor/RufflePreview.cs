using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.Scaleform;
using RimeLib.Cmd.UiBuilder;

namespace RimeUIEditor
{
    /// <summary>
    /// The live preview: the open screen, with the document's edits, converted to plain SWFs and played by a Flash player
    /// (Ruffle) with the game's own ActionScript — the widget classes construct themselves, hover and press animate, texts
    /// come out of the game's fonts. What the game's UI system would do is done here: the two class libraries load before the
    /// screen (the shell movie), each imported widget is registered under its class, and every widget with static data gets its
    /// initialize({data}). Widgets fed from the game's data components at runtime (lists, kit rows, button bars) stay empty:
    /// that data does not exist outside the game.
    /// </summary>
    public static class RufflePreview
    {
        public record Result(int Written, int Failed, string ShellPath, string OutDir);

        /// <summary>
        /// The components the engine puts under _global.Data for the ActionScript, with their methods — read from the game
        /// binary (the registration constructor of each GFxFunctionHandler names its methods). The bridge proxies every one to
        /// the host.
        /// </summary>
        public static readonly (string Component, string[] Methods)[] EngineComponents =
        {
            ("UIWidgetEventComp", new[] { "fireEvent", "requestFocus", "requestTypingInput" }),
            ("UIScreenEventComp", new[] { "screenEnterCompleted", "screenExitCompleted", "screenLoaded", "screenInitializeError" }),
            ("UIDirectAccessComp", new[] { "registerMC", "unregisterMC" }),
            ("UIDirectAccess", new[] { "registerMC", "unregisterMC" }),
            ("UIDataInterfaceComp", new[] { "setData", "getData" }),
            ("UILocalizeComp", new[] { "formatString", "formatNumber", "getDate", "getTime", "getLanguage", "getStoreLanguage" }),
            ("UITextureStreamingComponent", new[] { "loadTextureAsync", "unloadTextureAsync", "textureExists" }),
            ("UIOnDemandFontComponent", new[] { "loadFontBundle" }),
            ("UINametagComp", new[] { "onWidgetUnload", "getUIColor" }),
            ("UI3dLaserTagComp", new[] { "onWidgetUnload" }),
            ("UICapturepointtagComp", new[] { "onWidgetUnload" }),
            ("UIMapmarkertagComp", new[] { "onWidgetUnload" }),
            ("UITeamSupportTagComp", new[] { "onWidgetUnload" }),
            ("UITrackingtagComp", new[] { "onWidgetUnload" }),
            ("UIAlerttagComp", new[] { "onWidgetUnload" }),
        };

        /// <summary>The page the editor's WebView2 opens: the Ruffle web player on the shell, wired to the host through window.rue and rueDispatch.</summary>
        const string c_HostPage = @"<!doctype html>
<html><head><meta charset=""utf-8""><title>Rime UI preview</title>
<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}ruffle-player{width:100%;height:100%;display:block}
body.fixed ruffle-player{position:absolute;left:50%;top:50%;transform:translate(-50%,-50%)}</style>
</head><body>
<script src=""ruffle/ruffle.js""></script>
<script>
window.RufflePlayer = window.RufflePlayer || {};
var rueLog = function (t) { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage({ comp: 'page', method: 'log', args: [t] }); };
// what the editor pushed: the data components' values by key ({key: value}) and the localised texts by id, answered synchronously
// (an ExternalInterface call cannot wait for the editor)
window.rueData = {}; window.rueTexts = {}; window.rueLanguage = 'en';
// the text database is keyed by the game's hash of the id (h = 0xFFFFFFFF; h = h * 33 + byte, over UTF-8); a text may also be pushed under its plain id
window.rueHash = function (id) {
  var bytes = unescape(encodeURIComponent(String(id))), h = 0xFFFFFFFF;
  for (var i = 0; i < bytes.length; i++) h = (Math.imul(h, 33) + bytes.charCodeAt(i)) >>> 0;
  return String(h);
};
// {0} {1} … and %1 %2 … stand for the extra arguments of formatString
window.rueFormat = function (text, a) {
  for (var i = 1; i < a.length; i++) { var v = String(a[i]); text = text.split('{' + (i - 1) + '}').join(v).split('%' + i).join(v); }
  return text;
};
// the ActionScript side: _global.Data.<comp>.<method>(...) -> ExternalInterface.call('rue', comp, method, arguments)
window.rue = function (comp, method, args) {
  // the bridge's proxies pass their `arguments` (array-like); a direct call passes one value (a string is NOT a list of characters)
  var a = [];
  if (args != null && typeof args === 'object' && typeof args.length === 'number') { for (var i = 0; i < args.length; i++) a.push(args[i]); }
  else if (args !== undefined && args !== null) a.push(args);
  var reply;
  // a widget event fired to the engine while the page hands an input concept: the engine hands a press no further (see op 'concept')
  if (comp === 'UIWidgetEventComp' && method === 'fireEvent') window.rueFired = true;
  if (comp === 'UIDataInterfaceComp' && method === 'getData') { var k = String(a[0]); reply = (k in window.rueData) ? { value: window.rueData[k] } : undefined; }
  else if (comp === 'UIDataInterfaceComp' && method === 'setData') { window.rueData[String(a[0])] = a[1]; }
  else if (comp === 'UILocalizeComp' && (method === 'getLanguage' || method === 'getStoreLanguage')) reply = window.rueLanguage;
  else if (comp === 'UILocalizeComp' && method === 'formatString') {
    var id = String(a[0]), h = window.rueHash(id);
    reply = (id in window.rueTexts) ? window.rueFormat(window.rueTexts[id], a) : (h in window.rueTexts) ? window.rueFormat(window.rueTexts[h], a) : id;
  }
  else if (comp === 'UILocalizeComp' && method === 'formatNumber') reply = String(a[0]);
  // textures: the load is accepted here; the editor answers with <clip>.loadTextureAsyncDone(found, url) and serves the image under ./img/
  else if (comp === 'UITextureStreamingComponent' && method === 'loadTextureAsync') reply = true;
  else if (comp === 'UITextureStreamingComponent' && method === 'textureExists') reply = true;
  // the recorder's echo can be large (a whole screen's init data): it goes to the editor as it is, nothing is answered
  if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage({ comp: comp, method: method, args: a, reply: reply === undefined ? null : reply });
  return reply;
};
window.addEventListener('DOMContentLoaded', function () {
  var ruffle = window.RufflePlayer.newest();
  var player = ruffle.createPlayer();
  document.body.appendChild(player);
  window.ruePlayer = player;
  // the page's own errors, forwarded to the editor's log; the ActionScript's trace() lines too, once the player instance exists
  window.onerror = function (m, s, l) { rueLog('page error: ' + m + ' (' + s + ':' + l + ')'); };
  player.addEventListener('error', function (e) { rueLog('player error: ' + (e && e.message ? e.message : e)); });
  player.addEventListener('loadedmetadata', function () {
    rueLog('player: movie loaded ' + player.metadata.width + 'x' + player.metadata.height);
    try { player.traceObserver = function (t) { rueLog('trace: ' + t); }; } catch (x) { rueLog('no trace observer: ' + x); }
  });
  // base: what the movies' relative urls (loadMovie, imports) resolve against — the widgets' folder, as the desktop player does with the root movie's own location
  var base = new URL('ui/assets/', window.location.href).href;
  player.ruffle().load({ url: 'ui/assets/preview.swf', base: base, allowScriptAccess: true, autoplay: 'on', unmuteOverlay: 'hidden', splashScreen: false, letterbox: 'on', logLevel: 'info', warnOnUnsupportedContent: false, contextMenu: 'off' })
    .then(function () { rueLog('player: load resolved, playing=' + !player.suspended); try { player.traceObserver = function (t) { rueLog('trace: ' + t); }; } catch (x) {} })
    .catch(function (x) { rueLog('player: load failed ' + x); });
  // the host's way in: {path, method, a0, a1, a2} -> the bridge's rueDispatch calls path.method(a0, a1, a2)
  if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', function (e) {
    var m = e.data;
    if (m && m.op === 'data') { var n = 0; for (var k in m.data) { window.rueData[k] = m.data[k]; n++; } rueLog('data: ' + n + ' keys set'); return; }
    if (m && m.op === 'texts') { var t = 0; for (var k2 in m.texts) { window.rueTexts[k2] = m.texts[k2]; t++; } if (m.language) window.rueLanguage = m.language; rueLog('texts: ' + t + ' strings set'); return; }
    // the player's layout: 'fixed' = the 1280x720 stage at ONE device pixel per stage unit, centred (the game keeps its whole UI at its
    // 720p pixel size on any resolution, only the 3D behind it grows); 'fit' = scaled to the window (letterboxed)
    if (m && m.op === 'layout') {
      var fixed = m.mode === 'fixed', dpr = window.devicePixelRatio || 1;
      document.body.classList.toggle('fixed', fixed);
      player.style.width = fixed ? (1280 / dpr) + 'px' : '100%';
      player.style.height = fixed ? (720 / dpr) + 'px' : '100%';
      var r = player.getBoundingClientRect();
      if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage({ comp: 'rue', method: 'layout', args: [m.mode, Math.round(r.width * dpr), Math.round(r.height * dpr), Math.round(r.left * dpr), Math.round(r.top * dpr), dpr], reply: null });
      return;
    }
    // an input concept for the clips listed, in order — the engine's way (measured on the game's traffic): a press stops after the
    // first widget that fires an event during its handler, a release reaches every clip; the editor learns which were handed it
    if (m && m.op === 'concept') {
      var handed = [], method = m.pressed ? 'onInputConceptPressed' : 'onInputConceptReleased';
      for (var p = 0; p < m.paths.length; p++) {
        window.rueFired = false;
        try { player.ruffle().callExternalInterface('rueDispatch', { path: m.paths[p], method: method, a0: m.concept, a1: null, a2: null }); }
        catch (x) { rueLog('concept dispatch failed: ' + x); }
        handed.push(m.paths[p]);
        if (m.pressed && window.rueFired) break;
      }
      if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage({ comp: 'rue', method: 'conceptHanded', args: [m.pressed, m.concept, handed], reply: null });
      return;
    }
    try { var r = player.ruffle().callExternalInterface('rueDispatch', m); if (m && m.echo) rueLog('dispatch ' + m.path + '.' + m.method + ' -> ' + JSON.stringify(r)); }
    catch (x) { rueLog('dispatch failed: ' + x); }
  });
  rueLog('page ready');
});
</script>
</body></html>";

        /// <summary>
        /// The conversion's version, bumped whenever what a converted movie contains changes (the inliner's rules, the prologue and
        /// epilogue, the recorder): a full preview stamps its folder with it, and a pushed screen reuses a widget already in the
        /// folder only under the same stamp — otherwise the widget is converted again.
        /// </summary>
        public const int ConverterVersion = 2;
        static string StampPath(string p_OutDir) => Path.Combine(p_OutDir, "ui", "assets", "converter.version");

        /// <summary>
        /// Converts a screen movie (ui/assets/&lt;screen&gt;) and everything it imports into &lt;out&gt;/ui/…: the class libraries,
        /// the widgets made self-contained (their own imports inlined), the font library, the screen with the document's stage
        /// ops and static texts, and the shell (ui/assets/preview.swf) that loads them in the game's order.
        /// </summary>
        public static Result Convert(RimeUiService p_Rime, string p_Language, string p_Resource, ScreenEntry? p_DocEntry, string p_OutDir, Action<string> p_Log,
                                     IReadOnlyList<MovieOverride>? p_DocMovies = null)
            => Convert(p_Rime, p_Language, p_Resource, p_DocEntry, p_OutDir, p_Log, false, p_DocMovies);

        /// <summary>
        /// As above; with p_ScreenOnly the widgets and class libraries already converted under p_OutDir are reused as they are and no
        /// shell, bridge or host page is written — a screen the host pushes into a running preview next to the first one.
        /// p_DocMovies = the document's own movie entries: a widget movie the document edits (stage ops on the game's own, e.g. the
        /// kit rows of ui/assets/kitview) is converted WITH those edits, as the build ships it.
        /// </summary>
        public static Result Convert(RimeUiService p_Rime, string p_Language, string p_Resource, ScreenEntry? p_DocEntry, string p_OutDir, Action<string> p_Log, bool p_ScreenOnly,
                                     IReadOnlyList<MovieOverride>? p_DocMovies = null)
        {
            var s_MapLang = p_Language switch { "us" => "en", "ge" => "de", "jp" => "ja", _ => p_Language };
            TextDatabase? s_Texts = null;
            try { s_Texts = TextDatabase.Load(p_Rime, p_Language); } catch (Exception s_Ex) { p_Log("texts: " + s_Ex.Message); }
            var s_Aliases = new List<(string, string)>();
            try
            {
                var s_Json = p_Rime.PartitionJson($"ui/static/fontcollection_{s_MapLang}_fontmapcollection");
                foreach (var s_Font in ((JObject)s_Json["Instances"]!).Properties().Select(p => p.Value).OfType<JObject>().SelectMany(i => (i["Fonts"] as JArray ?? new JArray()).OfType<JObject>()))
                    foreach (var s_Name in (s_Font["ScaleformFontName"] as JArray ?? new JArray()))
                        s_Aliases.Add(((string)s_Name!, (string?)s_Font["FontLongName"] ?? ""));
            }
            catch (Exception s_Ex) { p_Log("font map: " + s_Ex.Message); }

            GfxToSwf.Atlas? AtlasOf(string p_Movie, string p_FileName)
            {
                try
                {
                    var s_Name = RimeUiService.AtlasResourceName(p_Movie, p_FileName);
                    var s_Bitmap = View.DdsImage.Load(p_Rime.TextureDds(s_Name));
                    if (s_Bitmap == null) return null;
                    if (s_Bitmap.Format != System.Windows.Media.PixelFormats.Bgra32) s_Bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(s_Bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                    var s_Pixels = new byte[s_Bitmap.PixelWidth * s_Bitmap.PixelHeight * 4];
                    s_Bitmap.CopyPixels(s_Pixels, s_Bitmap.PixelWidth * 4, 0);
                    return new GfxToSwf.Atlas(s_Bitmap.PixelWidth, s_Bitmap.PixelHeight, s_Pixels);
                }
                catch (Exception s_Ex) { p_Log($"atlas {p_FileName} of {p_Movie}: {s_Ex.Message}"); return null; }
            }
            string? Translate(string p_Id) => s_Texts?.Lookup(p_Id);
            var s_WidgetSettings = WidgetSettings.Load();

            var s_Failed = 0; var s_Written = 0;
            var s_Exports = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var s_Ready = new Dictionary<string, GfxMovie?>(StringComparer.OrdinalIgnoreCase);
            var s_Screen = p_Resource.ToLowerInvariant();
            GfxMovie? s_FontLibMovie = null;
            // the font library the widgets import as ../Static/gfxfontlib.swf: the glyph fonts under their Scaleform aliases
            GfxMovie? FontLib()
            {
                if (s_FontLibMovie != null) return s_FontLibMovie;
                try
                {
                    var s_Collection = GfxMovie.Load(p_Rime.ResourceBytes($"ui/static/fontcollection_{s_MapLang}_fontlib"));
                    var s_Report = new GfxToSwf.Report();
                    var s_Lib = GfxToSwf.FontLibrary(s_Collection, s_Aliases, s_Report);
                    s_FontLibMovie = GfxMovie.Load(s_Lib);
                    var s_Path = Path.Combine(p_OutDir, "ui", "static", "gfxfontlib.swf");
                    Directory.CreateDirectory(Path.GetDirectoryName(s_Path)!);
                    File.WriteAllBytes(s_Path, s_Lib);
                    p_Log($"ui/static/gfxfontlib.swf ({s_Lib.Length} bytes): {s_Report}");
                }
                catch (Exception s_Ex) { p_Log("font library: " + s_Ex.Message); ++s_Failed; }
                return s_FontLibMovie;
            }
            // a movie converted by an earlier preview into this folder is reused by a pushed screen only when the converter that wrote it
            // is this one: the folder carried popup and kit-list widgets converted before the inliner copied a movie's extra exports, and
            // the popup's buttons (attachMovie("smallTextButton") out of Button.swf) came up empty for a whole session of green runs
            var s_StampOk = File.Exists(StampPath(p_OutDir)) && File.ReadAllText(StampPath(p_OutDir)).Trim() == ConverterVersion.ToString();
            // a movie converted to SWF and made self-contained: what it imports (fonts, textures, other widgets) is inlined into it,
            // recursively, because the player never finishes preloading an imported movie that has imports of its own. The screen
            // keeps its widget imports (one level, each widget its own library, as in the game) and gets the prologue and epilogue.
            GfxMovie? Prepare(string r)
            {
                if (s_Ready.TryGetValue(r, out var s_Cached)) return s_Cached;
                s_Ready[r] = null;   // a cycle would come back here
                var s_IsScreen = string.Equals(r, s_Screen, StringComparison.OrdinalIgnoreCase);
                // (a widget the document edits is always converted afresh: a copy in the folder may predate the edit)
                if (p_ScreenOnly && !s_IsScreen && p_DocMovies?.Any(m => string.Equals(m.Resource, r, StringComparison.OrdinalIgnoreCase) && m.Stage.Count > 0) != true)
                {
                    // a widget or class library converted by an earlier preview into the same folder: taken as it is (its class
                    // registrations are still readable from it — the init actions survive the conversion)
                    var s_Existing = Path.Combine(p_OutDir, r.Replace('/', Path.DirectorySeparatorChar) + ".swf");
                    if (File.Exists(s_Existing) && !s_StampOk) p_Log($"{r}: the converted copy in the folder is from another converter version (no stamp {ConverterVersion}); converting again");
                    if (File.Exists(s_Existing) && s_StampOk)
                    {
                        try
                        {
                            var s_Reused = GfxMovie.Load(s_Existing);
                            s_Exports[r] = s_Reused.Exports().SelectMany(e => e.Characters.Select(c => c.Name)).ToList();
                            s_Ready[r] = s_Reused;
                            return s_Reused;
                        }
                        catch (Exception s_Ex) { p_Log($"{r}: the converted copy could not be read ({s_Ex.Message}); converting again"); }
                    }
                }
                GfxMovie s_Gfx;
                // a screen the document makes has no movie in the game: it starts from its template's, as the build does
                var s_MovieSource = s_IsScreen && p_DocEntry?.New == true ? "ui/assets/" + p_DocEntry.Template.Split('/').Last() : r;
                try { s_Gfx = GfxMovie.Load(p_Rime.ResourceBytes(s_MovieSource)); }
                catch (Exception s_Ex) { p_Log($"{r}: not loaded ({s_Ex.Message})"); ++s_Failed; return null; }
                if (s_MovieSource != r) p_Log($"{r}: a screen of the document, converted from its template {s_MovieSource}");
                // a widget movie the document edits: its stage ops on the game's own, as the build applies them
                var s_DocMovie = s_IsScreen ? null : p_DocMovies?.FirstOrDefault(m => string.Equals(m.Resource, r, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(m.File) && m.Stage.Count > 0);
                if (s_DocMovie != null)
                {
                    foreach (var s_Op in s_DocMovie.Stage)
                    {
                        try { ModEmitter.ApplyOp(s_Gfx, s_Op); }
                        catch (Exception s_Ex) { p_Log($"{r}: stage op '{s_Op}': {s_Ex.Message}"); ++s_Failed; }
                    }
                    p_Log($"{r}: {s_DocMovie.Stage.Count} stage op(s) of the document applied");
                }
                s_Exports[r] = s_Gfx.Exports().SelectMany(e => e.Characters.Select(c => c.Name)).ToList();
                List<(string, string)>? s_Registrations = null;
                List<GfxToSwf.InitData>? s_Inits = null;
                if (s_IsScreen)
                {
                    // the document's stage ops first (added widgets, moves, clip variables), so the imports and placements below see them
                    foreach (var s_Op in p_DocEntry?.Stage ?? new List<string>())
                    {
                        try { ModEmitter.ApplyOp(s_Gfx, s_Op); }
                        catch (Exception s_Ex) { p_Log($"stage op '{s_Op}': {s_Ex.Message}"); }
                    }
                    // the recorder in front of the screen's frame: what the host hands initializeScreen/refreshScreen comes back through
                    // ExternalInterface as a record — the same script the recorder mod ships into the game
                    if (DataRecorder.Available)
                    {
                        try { DataRecorder.Inject(s_Gfx, r.Split('/').Last()); p_Log($"{r}: recorder action injected (initializeScreen/refreshScreen echo back to the host)"); }
                        catch (Exception s_Ex) { p_Log($"{r}: recorder not injected: {s_Ex.Message}"); }
                    }
                    // what the game's UI system hands each widget at initialise from its binding's static fields: the shipped graph's
                    // bindings, overridden by the document's inline ones (a text typed in the editor)
                    s_Inits = new List<GfxToSwf.InitData>();
                    var s_Root = GfxMovie.FrameOnePlacements(s_Gfx.Tags).FirstOrDefault(p => p.Name != null)?.Name ?? "instance1";
                    void Init(string p_Instance, JObject b)
                    {
                        var d = new Dictionary<string, string>();
                        string Resolve(string? s) => s == null ? "" : (s_Texts?.Resolve(s) ?? s);
                        switch ((string?)b["$type"])
                        {
                            case "UIPageHeaderBinding":
                                if (!string.IsNullOrEmpty((string?)b["StaticHeader"])) d["Header"] = Resolve((string?)b["StaticHeader"]);
                                if (!string.IsNullOrEmpty((string?)b["StaticSubHeader"])) d["SubHeader"] = Resolve((string?)b["StaticSubHeader"]);
                                break;
                            case "UITextDataBinding":
                                if (!string.IsNullOrEmpty((string?)b["StaticText"])) d["Text"] = Resolve((string?)b["StaticText"]);
                                break;
                        }
                        s_Inits.RemoveAll(i => i.Path == s_Root + "." + p_Instance);
                        if (d.Count > 0) s_Inits.Add(new GfxToSwf.InitData(s_Root + "." + p_Instance, d));
                    }
                    try
                    {
                        var s_Partition = p_Rime.PartitionJson("ui/flow/screen/" + r.Split('/').Last());
                        var s_Instances = (JObject)s_Partition["Instances"]!;
                        foreach (var s_Node in s_Instances.Properties().Select(p => p.Value).OfType<JObject>().Where(o => (string?)o["$type"] == "WidgetNode"))
                        {
                            var s_Name = (string?)s_Node["InstanceName"] ?? (string?)s_Node["Name"] ?? "";
                            if (s_Node["DataBinding"] is JObject s_Ref && (string?)s_Ref["InstanceGuid"] is { } s_Guid && s_Instances[s_Guid] is JObject s_Binding)
                                Init(s_Name, s_Binding);
                        }
                    }
                    catch (Exception s_Ex) { p_Log("screen graph: " + s_Ex.Message); }
                    foreach (var n in p_DocEntry?.Nodes ?? new List<NodeEntry>())
                        if (n.Fields.TryGetValue("DataBinding", out var s_DocBinding) && s_DocBinding is JObject s_Obj && s_Obj["$type"] != null) Init(n.InstanceName, s_Obj);
                    // the widgets first (self-contained), and their class registrations read from their own scripts
                    s_Registrations = new List<(string, string)>();
                    foreach (var s_Import in s_Gfx.Imports())
                    {
                        if (s_Import.Url.EndsWith("gfxfontlib.swf", StringComparison.OrdinalIgnoreCase)) continue;
                        var s_Source = Prepare(GfxToSwf.ResolveImport(r, s_Import.Url));
                        var s_Calls = s_Source != null ? SwfInline.RegisterClassCalls(s_Source) : new List<(string Symbol, string Class)>();
                        foreach (var (_, s_Symbol) in s_Import.Characters)
                        {
                            var s_Class = s_Calls.FirstOrDefault(c => c.Symbol == s_Symbol).Class;
                            if (string.IsNullOrEmpty(s_Class)) { var w = s_WidgetSettings?.Get(GfxToSwf.ResolveImport(r, s_Import.Url)); if (w != null && w.Symbol == s_Symbol) s_Class = w.Class; }
                            if (!string.IsNullOrEmpty(s_Class)) s_Registrations.Add((s_Symbol, s_Class));
                            else p_Log($"{r}: no class known for imported symbol {s_Symbol} from {s_Import.Url}");
                        }
                    }
                }
                var s_Report = new GfxToSwf.Report();
                var s_Swf = GfxToSwf.Convert(s_Gfx, f => AtlasOf(r, f), Translate, s_Registrations, s_Registrations != null ? 15 : 0, s_Inits, s_Report);
                var s_Movie = GfxMovie.Load(s_Swf);
                var s_Inline = new SwfInline.Report();
                if (!s_IsScreen)
                    SwfInline.Inline(s_Movie, u => u.EndsWith("gfxfontlib.swf", StringComparison.OrdinalIgnoreCase) ? FontLib() : Prepare(GfxToSwf.ResolveImport(r, u)), s_Inline);
                s_Ready[r] = s_Movie;
                var s_Bytes = s_Movie.ToFile();
                var s_Path = Path.Combine(p_OutDir, r.Replace('/', Path.DirectorySeparatorChar) + ".swf");
                Directory.CreateDirectory(Path.GetDirectoryName(s_Path)!);
                File.WriteAllBytes(s_Path, s_Bytes);
                ++s_Written;
                p_Log($"{r} ({s_Bytes.Length} bytes): {s_Report}" + (s_IsScreen ? "" : $"; {s_Inline}"));
                return s_Movie;
            }
            // the class libraries the game's UI system loads before any screen (every widget class lives in them), then the screen
            Prepare("ui/assets/actionscriptlibrary");
            Prepare("ui/assets/menuactionscriptlibrary");
            Prepare(s_Screen);

            // the shell: loads the base library (marker: _global.fb.Base.UIBase), the menu library (marker: a class only it exports), then the screen
            var s_ShellRelative = "ui/assets/preview.swf";
            if (p_ScreenOnly) return new Result(s_Written, s_Failed, s_ShellRelative, p_OutDir);   // the running preview keeps its shell, bridge and page
            // a full conversion stamps the folder: the movies in it are this converter's, a pushed screen may reuse them
            Directory.CreateDirectory(Path.GetDirectoryName(StampPath(p_OutDir))!);
            File.WriteAllText(StampPath(p_OutDir), ConverterVersion.ToString());
            try
            {
                string[] MenuMarker()
                {
                    var s_Base = new HashSet<string>(s_Exports.TryGetValue("ui/assets/actionscriptlibrary", out var b) ? b : new List<string>(), StringComparer.Ordinal);
                    var s_Menu = s_Exports.TryGetValue("ui/assets/menuactionscriptlibrary", out var m) ? m : new List<string>();
                    var s_Own = s_Menu.FirstOrDefault(n => n.StartsWith("__Packages.", StringComparison.Ordinal) && !s_Base.Contains(n)) ?? throw new Exception("no class exported by the menu library alone");
                    return new[] { "_global" }.Concat(s_Own["__Packages.".Length..].Split('.')).ToArray();
                }
                // the bridge first: the components the engine would provide, proxied to the host
                var s_BridgePath = Path.Combine(p_OutDir, "ui", "assets", "bridge.swf");
                Directory.CreateDirectory(Path.GetDirectoryName(s_BridgePath)!);
                File.WriteAllBytes(s_BridgePath, GfxToSwf.Bridge(EngineComponents));
                ++s_Written;
                var s_Loads = new List<GfxToSwf.ShellLoad>
                {
                    new("bridge", "bridge.swf", new[] { "_global", "Data", "UIWidgetEventComp" }),
                    new("lib0", "actionscriptlibrary.swf", new[] { "_global", "fb", "Base", "UIBase" }),
                    new("lib1", "menuactionscriptlibrary.swf", MenuMarker()),
                    new("sc1", s_Screen.Split('/').Last() + ".swf", new[] { "sc1", "instance1" }),
                };
                // next to the widgets: the player resolves a loaded movie's imports against the shell's location
                var s_ShellPath = Path.Combine(p_OutDir, "ui", "assets", "preview.swf");
                Directory.CreateDirectory(Path.GetDirectoryName(s_ShellPath)!);
                File.WriteAllBytes(s_ShellPath, GfxToSwf.Shell(s_Loads));
                ++s_Written;
                p_Log($"preview.swf: loads {string.Join(" → ", s_Loads.Select(l => $"{l.Url} (until {string.Join(".", l.ReadyPath)})"))}");
            }
            catch (Exception s_Ex) { p_Log("shell: " + s_Ex.Message); ++s_Failed; }
            // the host page and the Ruffle web player next to it (shipped with the editor under Ruffle\)
            try
            {
                File.WriteAllText(Path.Combine(p_OutDir, "host.html"), c_HostPage, new System.Text.UTF8Encoding(false));
                var s_RuffleDir = Path.Combine(AppContext.BaseDirectory, "Ruffle");
                var s_Target = Path.Combine(p_OutDir, "ruffle");
                Directory.CreateDirectory(s_Target);
                var s_Copied = 0;
                if (Directory.Exists(s_RuffleDir))
                    foreach (var s_File in Directory.GetFiles(s_RuffleDir))
                    {
                        var s_Dest = Path.Combine(s_Target, Path.GetFileName(s_File));
                        if (!File.Exists(s_Dest) || new FileInfo(s_Dest).Length != new FileInfo(s_File).Length) { File.Copy(s_File, s_Dest, true); ++s_Copied; }
                    }
                else p_Log($"Ruffle web player not found at {s_RuffleDir}: the in-editor preview cannot run (the desktop ruffle.exe still can)");
                p_Log($"host.html written; Ruffle web player {(s_Copied > 0 ? $"copied ({s_Copied} files)" : "in place")}");
            }
            catch (Exception s_Ex) { p_Log("host page: " + s_Ex.Message); }
            return new Result(s_Written, s_Failed, s_ShellRelative, p_OutDir);
        }

        /// <summary>The host page's url on a server over the converted folder.</summary>
        public static string HostUrl(Server p_Server) => p_Server.Url + "host.html";

        /// <summary>
        /// A local HTTP server over the converted folder: a player opening the shell by URL loads the other movies by relative
        /// URL with no per-folder file-system prompts. 127.0.0.1 only, a free port, files only.
        /// </summary>
        public sealed class Server : IDisposable
        {
            readonly HttpListener m_Listener = new();
            readonly string m_Root;
            public string Url { get; } = null!;
            /// <summary>Every request served ("GET /ui/assets/bridge.swf -> 200"), for the log; called on the server's thread.</summary>
            public Action<string>? OnRequest { get; set; }
            /// <summary>Answers a path no file covers (relative, forward slashes) with bytes and a content type — the images the movies ask for by texture path; null = 404. Called on the server's thread.</summary>
            public Func<string, (byte[] Bytes, string ContentType)?>? Resolve { get; set; }

            public Server(string p_Root)
            {
                m_Root = Path.GetFullPath(p_Root);
                // a free TCP port can still be one HTTP.sys reserves for another service (netsh http show urlacl: 10243, 10246, 10247… on
                // this machine, right where the ephemeral ports get handed out): the listener then refuses it with "access denied" —
                // another port is tried, a few times, before giving up
                Exception? s_Last = null;
                for (var s_Try = 0; s_Try < 20; ++s_Try)
                {
                    var s_Port = FreePort();
                    var s_Url = $"http://127.0.0.1:{s_Port}/";
                    try
                    {
                        m_Listener.Prefixes.Clear();
                        m_Listener.Prefixes.Add(s_Url);
                        m_Listener.Start();
                        Url = s_Url;
                        break;
                    }
                    catch (HttpListenerException s_Ex) { s_Last = s_Ex; }
                }
                if (Url == null) throw new Exception("no port for the preview's server after 20 tries: " + (s_Last?.Message ?? "?"));
                var s_Thread = new Thread(Serve) { IsBackground = true, Name = "RufflePreview http" };
                s_Thread.Start();
            }

            static int FreePort()
            {
                var s_Socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                s_Socket.Start();
                var s_Port = ((IPEndPoint)s_Socket.LocalEndpoint).Port;
                s_Socket.Stop();
                return s_Port;
            }

            void Serve()
            {
                while (m_Listener.IsListening)
                {
                    HttpListenerContext s_Ctx;
                    try { s_Ctx = m_Listener.GetContext(); } catch { break; }
                    try
                    {
                        var s_Relative = Uri.UnescapeDataString(s_Ctx.Request.Url?.AbsolutePath.TrimStart('/') ?? "");
                        var s_Rel = s_Relative.Replace('/', Path.DirectorySeparatorChar);
                        var s_File = Path.GetFullPath(Path.Combine(m_Root, s_Rel));
                        // the images the movies ask for by texture path are the resolver's before any file: it sizes them for the player's
                        // scale (the game keeps an icon's pixel size whatever the resolution), so no copy on disk and no browser cache may answer
                        var s_Image = s_Relative.StartsWith("ui/assets/img/", StringComparison.OrdinalIgnoreCase);
                        if (s_Image) s_Ctx.Response.Headers["Cache-Control"] = "no-store";
                        if (s_Image || !s_File.StartsWith(m_Root, StringComparison.OrdinalIgnoreCase) || !File.Exists(s_File))
                        {
                            // not a file of the folder: the resolver may still have it (a texture asked for by path, served as an image)
                            (byte[] Bytes, string ContentType)? s_Resolved = null;
                            try { s_Resolved = s_File.StartsWith(m_Root, StringComparison.OrdinalIgnoreCase) ? Resolve?.Invoke(s_Relative) : null; }
                            catch (Exception s_Ex) { OnRequest?.Invoke($"GET {s_Ctx.Request.Url?.AbsolutePath} -> resolver failed: {s_Ex.Message}"); }
                            if (s_Resolved == null)
                            {
                                OnRequest?.Invoke($"GET {s_Ctx.Request.Url?.AbsolutePath} -> 404");
                                s_Ctx.Response.StatusCode = 404; s_Ctx.Response.Close(); continue;
                            }
                            OnRequest?.Invoke($"GET {s_Ctx.Request.Url?.AbsolutePath} -> 200 ({s_Resolved.Value.Bytes.Length} bytes, {s_Resolved.Value.ContentType}, resolved)");
                            s_Ctx.Response.ContentType = s_Resolved.Value.ContentType;
                            s_Ctx.Response.ContentLength64 = s_Resolved.Value.Bytes.Length;
                            s_Ctx.Response.OutputStream.Write(s_Resolved.Value.Bytes, 0, s_Resolved.Value.Bytes.Length);
                            s_Ctx.Response.Close();
                            continue;
                        }
                        var s_Bytes = File.ReadAllBytes(s_File);
                        OnRequest?.Invoke($"GET {s_Ctx.Request.Url?.AbsolutePath} -> 200 ({s_Bytes.Length} bytes)");
                        s_Ctx.Response.ContentType = Path.GetExtension(s_File).ToLowerInvariant() switch
                        {
                            ".swf" => "application/x-shockwave-flash",
                            ".html" => "text/html; charset=utf-8",
                            ".js" => "text/javascript",
                            ".wasm" => "application/wasm",
                            ".json" => "application/json",
                            _ => "application/octet-stream",
                        };
                        s_Ctx.Response.ContentLength64 = s_Bytes.Length;
                        s_Ctx.Response.OutputStream.Write(s_Bytes, 0, s_Bytes.Length);
                        s_Ctx.Response.Close();
                    }
                    catch { try { s_Ctx.Response.Abort(); } catch { } }
                }
            }

            public void Dispose() { try { m_Listener.Stop(); m_Listener.Close(); } catch { } }
        }

        /// <summary>
        /// Starts the player on the shell's URL and moves its window onto p_Screen (the editor's monitor) once it exists;
        /// null when the player is not there. The caller keeps the process to close it on the next preview.
        /// </summary>
        public static Process? Launch(string p_RufflePath, string p_Url, View.Monitors.Screen? p_Screen, Action<string> p_Log)
        {
            if (string.IsNullOrWhiteSpace(p_RufflePath) || !File.Exists(p_RufflePath))
            {
                p_Log("Ruffle not found: set Settings… ▸ Ruffle player to ruffle.exe (the desktop build from https://ruffle.rs, Windows x86_64); the preview is served at " + p_Url);
                return null;
            }
            try
            {
                var s_Process = Process.Start(new ProcessStartInfo(p_RufflePath) { ArgumentList = { "--width", "1300", "--height", "760", p_Url }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = false, RedirectStandardError = false });
                if (s_Process != null) View.Monitors.MoveProcessWindow(s_Process, p_Screen, TimeSpan.FromSeconds(8), EditorSettings.Headless);
                p_Log($"Ruffle opened {p_Url}" + (p_Screen != null ? $" on the monitor at ({p_Screen.Left},{p_Screen.Top})" : ""));
                return s_Process;
            }
            catch (Exception s_Ex) { p_Log("Ruffle: " + s_Ex.Message); return null; }
        }
    }
}
