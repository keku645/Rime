using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace RimeLib.Cmd.Scaleform
{
    /// <summary>
    /// Turns a game UI movie (GFX/CFX, Scaleform's flavour of SWF) into a plain SWF a Flash player can open:
    /// the atlas rectangles (GFx DefineSubImage over the movie's external DXT texture) become ordinary lossless
    /// bitmaps under the same character ids, so every shape's bitmap fill keeps working; the GFx-only tags
    /// (exporter info, external image, font texture info) are dropped; ID_* initial texts are localised, as the
    /// game's text translator does at runtime; everything else — sprites, placements with their clip actions,
    /// scripts, fonts, imports — is copied byte for byte. Imports stay relative ("Grid.swf", "../Static/gfxfontlib.swf"):
    /// the caller lays the converted movies out in a folder with those paths, and <see cref="FontLibrary"/> makes
    /// the font library movie the imports ask for.
    /// </summary>
    public static class GfxToSwf
    {
        /// <summary>The pixels of an atlas texture: top-left origin, 4 bytes per pixel, B G R A.</summary>
        public record Atlas(int Width, int Height, byte[] Bgra);

        public class Report
        {
            public List<string> Notes { get; } = new();
            public int SubImages, Dropped, Translated, Kept;
            /// <summary>"img://" image urls in the scripts turned into "./img/" (served by the preview as images).</summary>
            public int ImageUrls;
            public override string ToString() => $"{SubImages} atlas rectangles embedded, {Translated} texts localised, {Dropped} GFx tags dropped, {Kept} tags kept" + (ImageUrls > 0 ? $", {ImageUrls} image url(s) redirected" : "") + (Notes.Count > 0 ? "; " + string.Join("; ", Notes) : "");
        }

        const int c_DefineBitsLossless2 = 36, c_DefineEditText = 37, c_DefineSprite = 39, c_ExportAssets = 56, c_ShowFrame = 1, c_End = 0, c_FileAttributes = 69, c_DefineFont3 = 75;

        /// <summary>
        /// The movie as SWF bytes (CWS, the movie's version). p_AtlasByFile gives the pixels of the external image named
        /// in the movie (its DefineExternalImage2 file name, "button_i7.tga"); p_Translate maps an ID_* string to its text
        /// (null = unknown, the ID stays).
        /// </summary>
        public static byte[] Convert(GfxMovie p_Movie, Func<string, Atlas?> p_AtlasByFile, Func<string, string?>? p_Translate, Report p_Report)
            => Convert(p_Movie, p_AtlasByFile, p_Translate, null, 0, p_Report);

        /// <summary>
        /// A screen movie with a prologue in front of its own frame: p_WaitFrames frames of waiting (a player that resolves
        /// imports asynchronously — Ruffle — would otherwise place the widgets before their symbols are in), then
        /// Object.registerClass(symbol, class) for every imported widget (what each widget movie's own init action does in
        /// the game, so the placements construct as their widget class), then the screen's placements as its last frame.
        /// </summary>
        public static byte[] Convert(GfxMovie p_Movie, Func<string, Atlas?> p_AtlasByFile, Func<string, string?>? p_Translate,
                                     IReadOnlyList<(string Symbol, string Class)>? p_Registrations, int p_WaitFrames, Report p_Report)
            => Convert(p_Movie, p_AtlasByFile, p_Translate, p_Registrations, p_WaitFrames, null, p_Report);

        /// <summary>The data a widget instance gets at initialise: its dot path from the movie root ("instance1.PageHeader_01") and the channel → value pairs (Header, SubHeader, Text…).</summary>
        public record InitData(string Path, IReadOnlyDictionary<string, string> Data);

        /// <summary>
        /// As above, plus an epilogue frame after the screen's own: widget.initialize({data: {...}}) for every widget that has
        /// static data (what the game's UI system does from the bindings once the placements exist), then stop().
        /// </summary>
        public static byte[] Convert(GfxMovie p_Movie, Func<string, Atlas?> p_AtlasByFile, Func<string, string?>? p_Translate,
                                     IReadOnlyList<(string Symbol, string Class)>? p_Registrations, int p_WaitFrames, IReadOnlyList<InitData>? p_Inits, Report p_Report)
        {
            var s_Externals = p_Movie.ExternalImages().ToDictionary(e => e.ImageId, e => e);
            var s_Atlases = new Dictionary<uint, Atlas?>();
            Atlas? AtlasOf(uint p_ImageId)
            {
                if (s_Atlases.TryGetValue(p_ImageId, out var s_Cached)) return s_Cached;
                Atlas? s_Atlas = null;
                if (s_Externals.TryGetValue(p_ImageId, out var s_Ext))
                {
                    s_Atlas = p_AtlasByFile(s_Ext.FileName);
                    if (s_Atlas == null) p_Report.Notes.Add($"atlas {s_Ext.FileName} not available: its rectangles are left out");
                }
                else p_Report.Notes.Add($"sub-image refers to image {p_ImageId} the movie does not declare");
                s_Atlases[p_ImageId] = s_Atlas;
                return s_Atlas;
            }

            var s_Prologue = p_WaitFrames > 0 || (p_Registrations != null && p_Registrations.Count > 0);
            var s_Payload = new MemoryStream();
            var s_Header = (byte[])p_Movie.HeaderRaw.Clone();
            if (s_Prologue)
            {
                // two frames more on the main timeline: the wait loop and the registrations; the epilogue rides in the movie's own last frame
                // (a frame of its own would wait for the player's chunked preload of a movie loaded later, which stalls behind its imports)
                var s_Frames = (ushort)(BitConverter.ToUInt16(s_Header, s_Header.Length - 2) + 2);
                BitConverter.GetBytes(s_Frames).CopyTo(s_Header, s_Header.Length - 2);
            }
            s_Payload.Write(s_Header, 0, s_Header.Length);
            var s_PrologueDone = !s_Prologue;
            var s_LastShowFrame = p_Movie.Tags.FindLastIndex(t => t.Code == c_ShowFrame);
            for (var s_Index = 0; s_Index < p_Movie.Tags.Count; ++s_Index)
            {
                var t = p_Movie.Tags[s_Index];
                if (s_Prologue && s_Index == s_LastShowFrame)
                {
                    // the epilogue closes the movie's own frame, after its placements: the widgets' static data (or the host's turn), and
                    // stop() so the movie never loops back into the prologue
                    var e = new Avm1();
                    // with a host on the other side of ExternalInterface, the host initialises the widgets (the engine's way); without one
                    // (a desktop player) the static data goes in from here
                    e.PushString("[screen] epilogue frame of ").PushString("this").GetVariable().PushString("_name").GetMember().Add2().PushString(": ExternalInterface.available=").Add2();
                    e.PushString("flash").GetVariable().PushString("external").GetMember().PushString("ExternalInterface").GetMember().PushString("available").GetMember().Add2().Trace();
                    e.PushString("flash").GetVariable().PushString("external").GetMember().PushString("ExternalInterface").GetMember().PushString("available").GetMember().If("hosted");
                    foreach (var s_Init in p_Inits ?? new List<InitData>())
                    {
                        // <path>.initialize({data: {key: value, …}})
                        e.PushString("data");
                        foreach (var kv in s_Init.Data) e.PushString(kv.Key).PushString(kv.Value);
                        e.PushInt(s_Init.Data.Count).InitObject();
                        e.PushInt(1).InitObject();
                        e.PushInt(1);
                        var s_Path = s_Init.Path.Split('.');
                        e.PushString("this").GetVariable();
                        foreach (var s_Member in s_Path) e.PushString(s_Member).GetMember();
                        e.PushString("initialize").CallMethod().Pop();
                        e.PushString($"[screen] {s_Init.Path}.initialize({string.Join(", ", s_Init.Data.Select(kv => kv.Key + "=" + kv.Value))})").Trace();
                    }
                    e.Jump("done");
                    // rue("screen", "loaded", this._name): the host takes over from here — the clip name says which screen of the stack loaded
                    e.Label("hosted").PushString("this").GetVariable().PushString("_name").GetMember().PushString("loaded").PushString("screen").PushString("rue").PushInt(4);
                    e.PushString("flash").GetVariable().PushString("external").GetMember().PushString("ExternalInterface").GetMember().PushString("call").CallMethod().Pop();
                    e.PushString("[screen] loaded: the host initialises the widgets").Trace();
                    e.Label("done").Stop();
                    WriteTag(s_Payload, 12, e.End());
                    WriteTag(s_Payload, c_ShowFrame, Array.Empty<byte>());
                    p_Report.Notes.Add($"epilogue: {p_Inits?.Count ?? 0} widgets initialised with static data");
                    continue;
                }
                if (!s_PrologueDone && (t.Code == c_ShowFrame || GfxMovie.IsPlaceTag(t.Code)))
                {
                    // frame 1: count ticks and loop until the wait is over; frame 2: register the classes; the movie's own frame follows
                    var a = new Avm1();
                    a.PushString("_rue_wait").PushString("_rue_wait").GetVariable().PushInt(0).BitOr().PushInt(1).Add2().SetVariable();
                    a.PushString("_rue_wait").GetVariable().PushInt(System.Math.Max(1, p_WaitFrames)).Less2().If("again").Jump("done");
                    a.Label("again").GotoFrame(0).Play();
                    a.Label("done");
                    WriteTag(s_Payload, 12, a.End());
                    WriteTag(s_Payload, c_ShowFrame, Array.Empty<byte>());
                    var b = new Avm1();
                    foreach (var (s_Symbol, s_Class) in p_Registrations ?? new List<(string, string)>())
                    {
                        // Object.registerClass(symbol, <class path>): args pushed last-first, then the count, the object and the method name
                        var s_Path = s_Class.Split('.');
                        b.PushString(s_Path[0]).GetVariable();
                        foreach (var s_Member in s_Path.Skip(1)) b.PushString(s_Member).GetMember();
                        b.PushString(s_Symbol).PushInt(2).PushString("Object").GetVariable().PushString("registerClass").CallMethod().Pop();
                    }
                    b.PushString($"[screen] {p_Registrations?.Count ?? 0} widget classes registered after {p_WaitFrames} frames").Trace();
                    WriteTag(s_Payload, 12, b.End());
                    WriteTag(s_Payload, c_ShowFrame, Array.Empty<byte>());
                    s_PrologueDone = true;
                    p_Report.Notes.Add($"prologue: {p_WaitFrames} wait frames, {p_Registrations?.Count ?? 0} registerClass");
                }
                EmitTag(t, s_Payload, AtlasOf, p_Translate, p_Report);
            }
            return Package(p_Movie.Version, s_Payload.ToArray());
        }

        static void EmitTag(GfxTag t, Stream p_Out, Func<uint, Atlas?> p_AtlasOf, Func<string, string?>? p_Translate, Report p_Report)
        {
            switch (t.Code)
            {
                case 1000: case 1001: case 1002: case 1003: case 1004: case 1005: case 1006: case 1007: case GfxMovie.TagDefineExternalImage2:
                    ++p_Report.Dropped;
                    return;
                case GfxMovie.TagDefineSubImage:
                {
                    if (t.Body.Length < 12) { ++p_Report.Dropped; return; }
                    var s_Sub = new GfxMovie.SubImage(BitConverter.ToUInt16(t.Body, 0), BitConverter.ToUInt16(t.Body, 2), BitConverter.ToUInt16(t.Body, 4),
                                                      BitConverter.ToUInt16(t.Body, 6), BitConverter.ToUInt16(t.Body, 8), BitConverter.ToUInt16(t.Body, 10));
                    var s_Atlas = p_AtlasOf(s_Sub.ImageId);
                    if (s_Atlas == null) { ++p_Report.Dropped; return; }
                    WriteTag(p_Out, c_DefineBitsLossless2, Lossless2(s_Sub, s_Atlas));
                    ++p_Report.SubImages;
                    return;
                }
                case c_DefineEditText when p_Translate != null:
                {
                    var s_Body = TranslateEditText(t.Body, p_Translate, out var s_Changed);
                    if (s_Changed) { WriteTag(p_Out, c_DefineEditText, s_Body); ++p_Report.Translated; return; }
                    break;
                }
                case GfxMovie.TagPlaceObject2 when p_Translate != null && (t.Body.Length > 0 && (t.Body[0] & 0x80) != 0):
                case GfxMovie.TagPlaceObject3 when p_Translate != null && (t.Body.Length > 0 && (t.Body[0] & 0x80) != 0):
                {
                    // the construct-time variables of a placement (FrostEd's per-instance parameters) carry ID_* strings the game's
                    // translator renders when a widget puts them in a text field — a tab bar's "ID_M_TAB_KITS;ID_M_TAB_LAND;ID_M_TAB_AIR"
                    // (m_tabsDataStr): localised here, as the initial texts are, so the player shows KITS / LAND / AIR
                    var s_Body = TranslateClipVars(t.Body, t.Code == GfxMovie.TagPlaceObject3, p_Translate, out var s_Changed);
                    if (s_Changed) { WriteTag(p_Out, t.Code, s_Body); ++p_Report.Translated; ++p_Report.Kept; return; }
                    break;
                }
                case 12: case 59:
                {
                    // the image manager loads textures as "img://<path>" (the engine's image protocol); a player resolves "./img/<path>"
                    // against the movies' folder instead, where the preview serves the texture as an image — same length, so the
                    // bytecode keeps its offsets
                    var s_Body = ReplaceAll(t.Body, c_ImageProtocol, c_ImageFolder, out var s_Count);
                    if (s_Count > 0) { WriteTag(p_Out, t.Code, s_Body); p_Report.ImageUrls += s_Count; ++p_Report.Kept; return; }
                    break;
                }
                case c_DefineSprite:
                {
                    var s_Inner = new MemoryStream();
                    s_Inner.Write(BitConverter.GetBytes(t.SpriteId), 0, 2);
                    s_Inner.Write(BitConverter.GetBytes(t.SpriteFrameCount), 0, 2);
                    foreach (var c in t.Children) EmitTag(c, s_Inner, p_AtlasOf, p_Translate, p_Report);
                    WriteTag(p_Out, c_DefineSprite, s_Inner.ToArray());
                    return;
                }
            }
            var s_Raw = t.Emit();
            p_Out.Write(s_Raw, 0, s_Raw.Length);
            ++p_Report.Kept;
        }

        /// <summary>The engine's image protocol in the ActionScript ("img://" + texture path) and the preview's folder of the same length.</summary>
        static readonly byte[] c_ImageProtocol = Encoding.ASCII.GetBytes("img://");
        public const string ImageFolder = "./img/";
        static readonly byte[] c_ImageFolder = Encoding.ASCII.GetBytes(ImageFolder);

        /// <summary>Every occurrence of a byte sequence replaced by another of the same length; p_Count says how many.</summary>
        static byte[] ReplaceAll(byte[] p_Data, byte[] p_From, byte[] p_To, out int p_Count)
        {
            p_Count = 0;
            if (p_From.Length != p_To.Length || p_From.Length == 0) return p_Data;
            byte[]? s_Copy = null;
            for (var i = 0; i + p_From.Length <= p_Data.Length; ++i)
            {
                var s_Match = true;
                for (var j = 0; j < p_From.Length && s_Match; ++j) s_Match = p_Data[i + j] == p_From[j];
                if (!s_Match) continue;
                s_Copy ??= (byte[])p_Data.Clone();
                Array.Copy(p_To, 0, s_Copy, i, p_To.Length);
                ++p_Count;
                i += p_From.Length - 1;
            }
            return s_Copy ?? p_Data;
        }

        static void WriteTag(Stream p_Out, int p_Code, byte[] p_Body)
        {
            // long form: unambiguous for every length
            p_Out.Write(BitConverter.GetBytes((ushort)((p_Code << 6) | 0x3F)), 0, 2);
            p_Out.Write(BitConverter.GetBytes(p_Body.Length), 0, 4);
            p_Out.Write(p_Body, 0, p_Body.Length);
        }

        /// <summary>DefineBitsLossless2 (format 5: 32-bit ARGB, premultiplied, zlib) of one atlas rectangle, under the sub-image's character id.</summary>
        static byte[] Lossless2(GfxMovie.SubImage s, Atlas p_Atlas)
        {
            var l = System.Math.Clamp((int)s.Left, 0, p_Atlas.Width); var t = System.Math.Clamp((int)s.Top, 0, p_Atlas.Height);
            var r = System.Math.Clamp((int)s.Right, l, p_Atlas.Width); var b = System.Math.Clamp((int)s.Bottom, t, p_Atlas.Height);
            var w = System.Math.Max(1, r - l); var h = System.Math.Max(1, b - t);
            var s_Argb = new byte[w * h * 4];
            for (var y = 0; y < h; ++y)
                for (var x = 0; x < w; ++x)
                {
                    var s_Src = ((t + y) * p_Atlas.Width + (l + x)) * 4;
                    var s_Dst = (y * w + x) * 4;
                    if (s_Src + 3 >= p_Atlas.Bgra.Length) continue;
                    var a = p_Atlas.Bgra[s_Src + 3];
                    s_Argb[s_Dst] = a;
                    s_Argb[s_Dst + 1] = (byte)(p_Atlas.Bgra[s_Src + 2] * a / 255);
                    s_Argb[s_Dst + 2] = (byte)(p_Atlas.Bgra[s_Src + 1] * a / 255);
                    s_Argb[s_Dst + 3] = (byte)(p_Atlas.Bgra[s_Src] * a / 255);
                }
            var s_Out = new MemoryStream();
            s_Out.Write(BitConverter.GetBytes(s.CharacterId), 0, 2);
            s_Out.WriteByte(5);
            s_Out.Write(BitConverter.GetBytes((ushort)w), 0, 2);
            s_Out.Write(BitConverter.GetBytes((ushort)h), 0, 2);
            var s_Zipped = Zlib(s_Argb);
            s_Out.Write(s_Zipped, 0, s_Zipped.Length);
            return s_Out.ToArray();
        }

        /// <summary>
        /// A PlaceObject2/3 body with the ID_* strings of its construct-time variables localised (each ';'-separated part of a value
        /// on its own, as the tab bars list their tabs); untouched when the placement has no readable construct record, or when the
        /// record carries code beyond variable assignments (it is not rewritten).
        /// </summary>
        public static byte[] TranslateClipVars(byte[] p_Body, bool p_PlaceObject3, Func<string, string?> p_Translate, out bool p_Changed)
        {
            p_Changed = false;
            try
            {
                var s_Place = PlaceObject2.Decode(p_Body, p_PlaceObject3);
                var s_Actions = s_Place.DecodeClipActions();
                if (s_Actions == null) return p_Body;
                var s_Vars = s_Actions.ConstructVars();
                if (s_Vars.Count == 0) return p_Body;
                var s_New = new List<(string Name, object? Value)>();
                foreach (var (s_Name, s_Value) in s_Vars)
                {
                    if (s_Value is string s_Text && s_Text.Contains("ID_", StringComparison.Ordinal))
                    {
                        var s_Parts = s_Text.Split(';');
                        var s_Any = false;
                        for (var i = 0; i < s_Parts.Length; ++i)
                        {
                            if (!s_Parts[i].StartsWith("ID_", StringComparison.Ordinal)) continue;
                            var s_Localised = p_Translate(s_Parts[i]);
                            if (s_Localised != null && s_Localised != s_Parts[i]) { s_Parts[i] = s_Localised; s_Any = true; }
                        }
                        if (s_Any) { s_New.Add((s_Name, string.Join(";", s_Parts))); p_Changed = true; continue; }
                    }
                    s_New.Add((s_Name, s_Value));
                }
                if (!p_Changed) return p_Body;
                s_Actions.SetConstructVars(s_New);
                s_Place.SetClipActions(s_Actions);
                return s_Place.Encode();
            }
            catch { p_Changed = false; return p_Body; }
        }

        /// <summary>
        /// A DefineEditText body with its initial text localised when it is an ID_* string (the fields in front of the text are
        /// walked, never re-encoded: font, class, height, colour, max length, layout, variable name).
        /// </summary>
        public static byte[] TranslateEditText(byte[] b, Func<string, string?> p_Translate, out bool p_Changed)
        {
            p_Changed = false;
            if (b.Length < 6) return b;
            var s_NBits = b[2] >> 3;
            var p = 2 + (5 + 4 * s_NBits + 7) / 8;
            if (p + 2 > b.Length) return b;
            var f1 = b[p]; var f2 = b[p + 1]; p += 2;
            var s_HasText = (f1 & 0x80) != 0; var s_HasTextColor = (f1 & 0x04) != 0; var s_HasMaxLength = (f1 & 0x02) != 0; var s_HasFont = (f1 & 0x01) != 0;
            var s_HasFontClass = (f2 & 0x80) != 0; var s_HasLayout = (f2 & 0x20) != 0;
            if (s_HasFont) p += 2;
            if (s_HasFontClass) p = SkipString(b, p);
            if (s_HasFont) p += 2;
            if (s_HasTextColor) p += 4;
            if (s_HasMaxLength) p += 2;
            if (s_HasLayout) p += 9;
            p = SkipString(b, p);   // variable name
            if (p > b.Length) return b;
            if (!s_HasText) return b;
            var s_TextEnd = Array.IndexOf(b, (byte)0, p);
            if (s_TextEnd < 0) return b;
            var s_Text = Encoding.UTF8.GetString(b, p, s_TextEnd - p);
            if (!s_Text.StartsWith("ID_", StringComparison.Ordinal)) return b;
            var s_New = p_Translate(s_Text);
            if (s_New == null || s_New == s_Text) return b;
            var s_Out = new MemoryStream();
            s_Out.Write(b, 0, p);
            var s_Bytes = Encoding.UTF8.GetBytes(s_New);
            s_Out.Write(s_Bytes, 0, s_Bytes.Length);
            s_Out.WriteByte(0);
            p_Changed = true;
            return s_Out.ToArray();
        }

        static int SkipString(byte[] b, int p)
        {
            var e = Array.IndexOf(b, (byte)0, p);
            return e < 0 ? b.Length + 1 : e + 1;
        }

        /// <summary>
        /// The font library movie the game's movies import as "../Static/gfxfontlib.swf": the font collection's DefineFont3
        /// glyph fonts, exported under their long names and under every Scaleform alias the font map gives them
        /// ("$Menu_bold" → "Purista EA Semibold"), so an ImportAssets2 of "$Menu_bold" resolves in a plain player the way
        /// the game's font map resolves it.
        /// </summary>
        public static byte[] FontLibrary(GfxMovie p_Collection, IEnumerable<(string Alias, string LongName)> p_Aliases, Report p_Report)
        {
            var s_Fonts = new List<(ushort Id, string Name, GfxTag Tag)>();
            foreach (var t in p_Collection.Tags.Where(t => t.Code == c_DefineFont3 && t.Body.Length >= 5))
            {
                var s_Id = BitConverter.ToUInt16(t.Body, 0);
                var s_Len = t.Body[4];
                var s_Name = Encoding.UTF8.GetString(t.Body, 5, System.Math.Min(s_Len, t.Body.Length - 5)).TrimEnd('\0');
                s_Fonts.Add((s_Id, s_Name, t));
            }
            var s_Exports = new List<(ushort Id, string Name)>();
            foreach (var f in s_Fonts) s_Exports.Add((f.Id, f.Name));
            foreach (var (s_Alias, s_Long) in p_Aliases)
            {
                var s_Font = s_Fonts.FirstOrDefault(f => string.Equals(f.Name, s_Long, StringComparison.OrdinalIgnoreCase));
                if (s_Font.Tag == null) { p_Report.Notes.Add($"font alias {s_Alias} → '{s_Long}' has no glyph font in the collection"); continue; }
                if (!s_Exports.Any(e => e.Name == s_Alias)) s_Exports.Add((s_Font.Id, s_Alias));
            }
            var s_Payload = new MemoryStream();
            s_Payload.Write(p_Collection.HeaderRaw, 0, p_Collection.HeaderRaw.Length);
            var s_Attrs = p_Collection.Tags.FirstOrDefault(t => t.Code == c_FileAttributes);
            if (s_Attrs != null) { var raw = s_Attrs.Emit(); s_Payload.Write(raw, 0, raw.Length); }
            foreach (var f in s_Fonts) { var raw = f.Tag.Emit(); s_Payload.Write(raw, 0, raw.Length); ++p_Report.Kept; }
            var s_Export = new MemoryStream();
            s_Export.Write(BitConverter.GetBytes((ushort)s_Exports.Count), 0, 2);
            foreach (var (s_Id, s_Name) in s_Exports)
            {
                s_Export.Write(BitConverter.GetBytes(s_Id), 0, 2);
                var s_Bytes = Encoding.UTF8.GetBytes(s_Name);
                s_Export.Write(s_Bytes, 0, s_Bytes.Length);
                s_Export.WriteByte(0);
            }
            WriteTag(s_Payload, c_ExportAssets, s_Export.ToArray());
            WriteTag(s_Payload, c_ShowFrame, Array.Empty<byte>());
            WriteTag(s_Payload, c_End, Array.Empty<byte>());
            p_Report.Notes.Add($"font library: {s_Fonts.Count} fonts ({string.Join(", ", s_Fonts.Select(f => f.Name))}), {s_Exports.Count} export names");
            return Package(p_Collection.Version, s_Payload.ToArray());
        }

        /// <summary>What the shell loads, in order: a child clip name, the movie's url (relative to the shell) and the variable path that exists once the movie is in and initialised.</summary>
        public record ShellLoad(string Clip, string Url, string[] ReadyPath);

        /// <summary>
        /// The shell movie that plays a screen the way the game's UI system does: one root with the ActionScript class
        /// libraries loaded first (their init actions define every widget class under _global) and the screen loaded as a
        /// child clip afterwards, each load waited for before the next (a frame loop until the movie's marker exists).
        /// 1280x720, 30 fps, black, one frame per step; trace() lines mark the steps in the player's log.
        /// </summary>
        public static byte[] Shell(IReadOnlyList<ShellLoad> p_Loads)
        {
            var s_Frames = new List<byte[]>();
            var s_Depth = 1;
            {
                // the host's way to push and pop screens later (the game's MainUI does the same with createEmptyMovieClip + loadClip):
                // this.rueLoadScreen(clip, url, depth) and this.rueUnloadScreen(clip), on the root, defined before anything loads
                var s_Root = new Avm1();
                var f = new Avm1();
                // this.createEmptyMovieClip(clip, depth)
                f.PushRegister(3).PushRegister(1).PushInt(2).PushString("this").GetVariable().PushString("createEmptyMovieClip").CallMethod().Pop();
                // this[clip].loadMovie(url)
                f.PushRegister(2).PushInt(1).PushString("this").GetVariable().PushRegister(1).GetMember().PushString("loadMovie").CallMethod().Pop();
                f.PushString("[shell] rueLoadScreen ").PushRegister(1).Add2().PushString(" <- ").Add2().PushRegister(2).Add2().Trace();
                s_Root.PushString("this").GetVariable().PushString("rueLoadScreen").Function(new[] { "clip", "url", "depth" }, 4, f.Bytes()).SetMember();
                var g = new Avm1();
                // this[clip].removeMovieClip()
                g.PushInt(0).PushString("this").GetVariable().PushRegister(1).GetMember().PushString("removeMovieClip").CallMethod().Pop();
                s_Root.PushString("this").GetVariable().PushString("rueUnloadScreen").Function(new[] { "clip" }, 2, g.Bytes()).SetMember();
                // this.rueShowScreen(clip, visible): this[clip]._visible = visible — a StateNode's Show/Hide on a screen already on the player
                var h = new Avm1();
                h.PushString("this").GetVariable().PushRegister(1).GetMember().PushString("_visible").PushRegister(2).SetMember();
                h.PushString("[shell] rueShowScreen ").PushRegister(1).Add2().PushString(" ").Add2().PushRegister(2).Add2().Trace();
                s_Root.PushString("this").GetVariable().PushString("rueShowScreen").Function(new[] { "clip", "visible" }, 3, h.Bytes()).SetMember();
                // the stage rectangles the game's widget base class aligns against (fb.Base.UIBase.updateAlignment reads Stage.visibleRect,
                // Stage.originalRect and Stage.safeRect — extensions of the game's player, undefined in a plain one, which landed an aligned
                // widget at NaN): the design size, no safe-area inset, so a widget's Align keeps it where it was authored
                foreach (var s_StageRect in new[] { "visibleRect", "originalRect", "safeRect" })
                {
                    s_Root.PushString("Stage").GetVariable().PushString(s_StageRect);
                    s_Root.PushString("x").PushInt(0).PushString("y").PushInt(0).PushString("width").PushInt(1280).PushString("height").PushInt(720).PushInt(4).InitObject();
                    s_Root.SetMember();
                }
                s_Root.PushString("[shell] rueLoadScreen / rueUnloadScreen / rueShowScreen defined on the root; Stage.visibleRect/originalRect/safeRect = 1280x720").Trace();
                s_Frames.Add(s_Root.End());
            }
            foreach (var l in p_Loads)
            {
                // load frame: this.createEmptyMovieClip(clip, depth); this.<clip>.loadMovie(url); trace(...)
                var a = new Avm1();
                a.PushInt(s_Depth++).PushString(l.Clip).PushInt(2).PushString("this").GetVariable().PushString("createEmptyMovieClip").CallMethod().Pop();
                a.PushString(l.Url).PushInt(1).PushString(l.Clip).GetVariable().PushString("loadMovie").CallMethod().Pop();
                a.PushString($"[shell] loading {l.Url} into {l.Clip}").Trace();
                s_Frames.Add(a.End());
                // wait frame: if (typeof(<ready path>) == "undefined") { trace once; gotoAndPlay(this frame) } — else fall through to the next frame
                var s_WaitIndex = s_Frames.Count;   // 0-based index of the frame being emitted
                var b = new Avm1();
                b.PushString(l.ReadyPath[0]).GetVariable();
                foreach (var s_Member in l.ReadyPath.Skip(1)) b.PushString(s_Member).GetMember();
                b.TypeOf().PushString("undefined").Equals2().If("again").PushString($"[shell] {l.Clip} ready ({string.Join(".", l.ReadyPath)} exists)").Trace().Jump("done");
                b.Label("again");
                b.PushString("_rw_" + l.Clip).GetVariable().TypeOf().PushString("undefined").Equals2().Not().If("looping");
                b.PushString($"[shell] waiting for {l.Clip} ({string.Join(".", l.ReadyPath)})").Trace().PushString("_rw_" + l.Clip).PushInt(1).SetVariable();
                b.Label("looping").GotoFrame(s_WaitIndex).Play();
                b.Label("done");
                s_Frames.Add(b.End());
            }
            var c = new Avm1();
            c.PushString("[shell] all loaded").Trace().Stop();
            s_Frames.Add(c.End());

            var s_Payload = new MemoryStream();
            // RECT 0..25600 x 0..14400 twips (16-bit fields), frame rate 30 (8.8), frame count
            var s_Bits = new BitWriter();
            s_Bits.Write(16, 5); s_Bits.Write(0, 16); s_Bits.Write(25600, 16); s_Bits.Write(0, 16); s_Bits.Write(14400, 16);
            var s_Rect = s_Bits.ToBytes();
            s_Payload.Write(s_Rect, 0, s_Rect.Length);
            s_Payload.Write(new byte[] { 0x00, 30 }, 0, 2);
            s_Payload.Write(BitConverter.GetBytes((ushort)s_Frames.Count), 0, 2);
            WriteTag(s_Payload, c_FileAttributes, new byte[] { 0x01, 0, 0, 0 });   // UseNetwork (bit 0) — not ActionScript3 (bit 3), this is an AVM1 movie
            WriteTag(s_Payload, 9, new byte[] { 0, 0, 0 });                         // black stage
            foreach (var f in s_Frames)
            {
                WriteTag(s_Payload, 12, f);   // DoAction
                WriteTag(s_Payload, c_ShowFrame, Array.Empty<byte>());
            }
            WriteTag(s_Payload, c_End, Array.Empty<byte>());
            // version 10 (still AVM1: the ActionScript3 attribute is off). A call from the page into the movie runs under the ROOT clip of
            // the stage — this shell — and the player turns an empty string coming in that way into the text "null" when that root movie
            // is below version 9 (Flash's old behaviour); the data the host hands the widgets carries empty strings (an empty category
            // label, say). Measured: bumping the bridge alone changed nothing, bumping the shell keeps the empty strings.
            return Package(10, s_Payload.ToArray());
        }

        /// <summary>
        /// The bridge movie: what the game's engine puts under _global.Data for the ActionScript — every component with its
        /// methods — as proxies that forward each call to the host through ExternalInterface.call("rue", component, method,
        /// arguments), plus the host's way in: ExternalInterface.addCallback("rueDispatch") takes {path, method, a0, a1, a2}
        /// and calls path.method(a0, a1, a2) on any clip (a widget's initialize, update…Data, handleInEvent). Loaded by the
        /// shell before the class libraries, so every widget finds the components the game would give it.
        /// </summary>
        public static byte[] Bridge(IReadOnlyList<(string Component, string[] Methods)> p_Components)
        {
            var a = new Avm1();
            // _global.Data = {}
            a.PushString("_global").GetVariable().PushString("Data").PushInt(0).InitObject().SetMember();
            foreach (var (s_Comp, s_Methods) in p_Components)
            {
                // _global.Data.<comp> = { method: function() { return flash.external.ExternalInterface.call("rue", comp, method, arguments); }, … }
                a.PushString("_global").GetVariable().PushString("Data").GetMember().PushString(s_Comp);
                foreach (var s_Method in s_Methods)
                {
                    var f = new Avm1();
                    f.PushString("arguments").GetVariable().PushString(s_Method).PushString(s_Comp).PushString("rue").PushInt(4);
                    f.PushString("flash").GetVariable().PushString("external").GetMember().PushString("ExternalInterface").GetMember().PushString("call").CallMethod().Return();
                    a.PushString(s_Method).Function(Array.Empty<string>(), 1, f.Bytes());
                }
                a.PushInt(s_Methods.Length).InitObject().SetMember();
            }
            // ExternalInterface.addCallback("rueDispatch", null, function(msg) { var t = eval(msg.path); return t[msg.method](msg.a0, msg.a1, msg.a2); })
            {
                var f = new Avm1();
                f.PushRegister(1).PushString("path").GetMember().GetVariable().StoreRegister(2).Pop();
                f.PushRegister(1).PushString("a2").GetMember().PushRegister(1).PushString("a1").GetMember().PushRegister(1).PushString("a0").GetMember().PushInt(3);
                f.PushRegister(2).PushRegister(1).PushString("method").GetMember().CallMethod().Return();
                a.Function(new[] { "msg" }, 3, f.Bytes()).PushNull().PushString("rueDispatch").PushInt(3);
                a.PushString("flash").GetVariable().PushString("external").GetMember().PushString("ExternalInterface").GetMember().PushString("addCallback").CallMethod().Pop();
            }
            // hello: rue("bridge", "ready")
            a.PushString("ready").PushString("bridge").PushString("rue").PushInt(3);
            a.PushString("flash").GetVariable().PushString("external").GetMember().PushString("ExternalInterface").GetMember().PushString("call").CallMethod().Pop();
            a.PushString($"[bridge] _global.Data ready: {p_Components.Count} components, {p_Components.Sum(c => c.Methods.Length)} methods proxied to the host").Trace().Stop();

            var s_Payload = new MemoryStream();
            var s_Bits = new BitWriter();
            s_Bits.Write(16, 5); s_Bits.Write(0, 16); s_Bits.Write(25600, 16); s_Bits.Write(0, 16); s_Bits.Write(14400, 16);
            var s_Rect = s_Bits.ToBytes();
            s_Payload.Write(s_Rect, 0, s_Rect.Length);
            s_Payload.Write(new byte[] { 0x00, 30 }, 0, 2);
            s_Payload.Write(BitConverter.GetBytes((ushort)1), 0, 2);
            WriteTag(s_Payload, c_FileAttributes, new byte[] { 0x01, 0, 0, 0 });
            WriteTag(s_Payload, 12, a.End());
            WriteTag(s_Payload, c_ShowFrame, Array.Empty<byte>());
            WriteTag(s_Payload, c_End, Array.Empty<byte>());
            // version 10 like the shell (still AVM1): a value the page RETURNS to a call one of these proxies makes (getData, formatString…)
            // is converted under the calling function's movie — this one — and the player turns an empty string into the text "null"
            // below version 9; what the page PUSHES (initializeScreen, refresh, handleInEvent) converts under the stage's root, the shell
            return Package(10, s_Payload.ToArray());
        }

        /// <summary>The handful of AVM1 actions the shell needs, assembled with forward branch labels.</summary>
        class Avm1
        {
            readonly MemoryStream m_Out = new();
            readonly Dictionary<string, int> m_Labels = new();
            readonly List<(int Pos, string Label)> m_Fixups = new();

            Avm1 Op(byte p_Code) { m_Out.WriteByte(p_Code); return this; }
            Avm1 Op(byte p_Code, byte[] p_Data) { m_Out.WriteByte(p_Code); m_Out.Write(BitConverter.GetBytes((ushort)p_Data.Length), 0, 2); m_Out.Write(p_Data, 0, p_Data.Length); return this; }
            public Avm1 PushString(string s) { var b = Encoding.UTF8.GetBytes(s); return Op(0x96, new byte[] { 0 }.Concat(b).Concat(new byte[] { 0 }).ToArray()); }
            public Avm1 PushInt(int v) => Op(0x96, new byte[] { 7 }.Concat(BitConverter.GetBytes(v)).ToArray());
            public Avm1 PushNull() => Op(0x96, new byte[] { 2 });
            public Avm1 PushRegister(byte r) => Op(0x96, new byte[] { 4, r });
            public Avm1 StoreRegister(byte r) => Op(0x87, new byte[] { r });
            public Avm1 Return() => Op(0x3E);
            /// <summary>DefineFunction2: an anonymous function pushed on the stack; parameters land in registers 1..n, p_Registers registers in all, arguments and this available.</summary>
            public Avm1 Function(string[] p_Params, byte p_Registers, byte[] p_Body)
            {
                var s_Header = new MemoryStream();
                s_Header.WriteByte(0);   // no name
                s_Header.Write(BitConverter.GetBytes((ushort)p_Params.Length), 0, 2);
                s_Header.WriteByte(p_Registers);
                s_Header.Write(BitConverter.GetBytes((ushort)0), 0, 2);   // flags: nothing suppressed, nothing preloaded
                for (var i = 0; i < p_Params.Length; ++i)
                {
                    s_Header.WriteByte((byte)(i + 1));
                    var s_Name = Encoding.UTF8.GetBytes(p_Params[i]);
                    s_Header.Write(s_Name, 0, s_Name.Length);
                    s_Header.WriteByte(0);
                }
                s_Header.Write(BitConverter.GetBytes((ushort)p_Body.Length), 0, 2);
                Op(0x8E, s_Header.ToArray());
                m_Out.Write(p_Body, 0, p_Body.Length);   // the code follows the action record
                return this;
            }
            /// <summary>The actions without a terminating End (a function body).</summary>
            public byte[] Bytes() => Resolve(false);
            public Avm1 GetVariable() => Op(0x1C);
            public Avm1 SetVariable() => Op(0x1D);
            public Avm1 GetMember() => Op(0x4E);
            public Avm1 SetMember() => Op(0x4F);
            public Avm1 Add2() => Op(0x47);
            public Avm1 InitObject() => Op(0x43);
            public Avm1 Less2() => Op(0x48);
            public Avm1 BitOr() => Op(0x60);
            public Avm1 CallMethod() => Op(0x52);
            public Avm1 Pop() => Op(0x17);
            public Avm1 Equals2() => Op(0x49);
            public Avm1 TypeOf() => Op(0x44);
            public Avm1 Not() => Op(0x12);
            public Avm1 Trace() => Op(0x26);
            public Avm1 Play() => Op(0x06);
            public Avm1 Stop() => Op(0x07);
            public Avm1 GotoFrame(int p_Frame0) => Op(0x81, BitConverter.GetBytes((ushort)p_Frame0));
            public Avm1 If(string p_Label) { m_Out.WriteByte(0x9D); m_Out.Write(new byte[] { 2, 0 }, 0, 2); m_Fixups.Add(((int)m_Out.Position, p_Label)); m_Out.Write(new byte[2], 0, 2); return this; }
            public Avm1 Jump(string p_Label) { m_Out.WriteByte(0x99); m_Out.Write(new byte[] { 2, 0 }, 0, 2); m_Fixups.Add(((int)m_Out.Position, p_Label)); m_Out.Write(new byte[2], 0, 2); return this; }
            public Avm1 Label(string p_Name) { m_Labels[p_Name] = (int)m_Out.Position; return this; }

            public byte[] End() => Resolve(true);

            byte[] Resolve(bool p_End)
            {
                if (p_End) m_Out.WriteByte(0);
                var s_Bytes = m_Out.ToArray();
                foreach (var (s_Pos, s_Label) in m_Fixups)
                {
                    // a branch offset counts from the byte after its own 2-byte operand
                    var s_Offset = (short)(m_Labels[s_Label] - (s_Pos + 2));
                    BitConverter.GetBytes(s_Offset).CopyTo(s_Bytes, s_Pos);
                }
                return s_Bytes;
            }
        }

        class BitWriter
        {
            readonly List<bool> m_Bits = new();
            public void Write(long p_Value, int p_Bits) { for (var i = p_Bits - 1; i >= 0; --i) m_Bits.Add(((p_Value >> i) & 1) == 1); }
            public byte[] ToBytes()
            {
                var s_Out = new byte[(m_Bits.Count + 7) / 8];
                for (var i = 0; i < m_Bits.Count; ++i) if (m_Bits[i]) s_Out[i / 8] |= (byte)(0x80 >> (i % 8));
                return s_Out;
            }
        }

        /// <summary>CWS file: magic, version, total uncompressed length, zlib payload.</summary>
        static byte[] Package(byte p_Version, byte[] p_Payload)
        {
            var s_Out = new MemoryStream();
            s_Out.Write(Encoding.ASCII.GetBytes("CWS"), 0, 3);
            s_Out.WriteByte(p_Version < 8 ? (byte)8 : p_Version);
            s_Out.Write(BitConverter.GetBytes((uint)(p_Payload.Length + 8)), 0, 4);
            var s_Zipped = Zlib(p_Payload);
            s_Out.Write(s_Zipped, 0, s_Zipped.Length);
            return s_Out.ToArray();
        }

        static byte[] Zlib(byte[] p_Data)
        {
            var s_Deflater = new Deflater(Deflater.BEST_COMPRESSION, false);
            s_Deflater.SetInput(p_Data);
            s_Deflater.Finish();
            var s_Out = new MemoryStream();
            var s_Buffer = new byte[65536];
            while (!s_Deflater.IsFinished)
            {
                var s_Count = s_Deflater.Deflate(s_Buffer);
                s_Out.Write(s_Buffer, 0, s_Count);
            }
            return s_Out.ToArray();
        }

        /// <summary>
        /// AVM1 actions that set timeline variables to strings (name = "value"; …), without a terminating End — a prologue put in
        /// front of a compiled frame action so the script finds its parameters as variables of the timeline it runs on.
        /// </summary>
        public static byte[] AssignStrings(IEnumerable<(string Name, string Value)> p_Vars)
        {
            var a = new Avm1();
            foreach (var (s_Name, s_Value) in p_Vars) a.PushString(s_Name).PushString(s_Value).SetVariable();
            return a.Bytes();
        }

        /// <summary>An import url as the resource next to the importer ("Grid.swf" from ui/assets/x → ui/assets/grid; "../Static/gfxfontlib.swf" → ui/static/gfxfontlib).</summary>
        public static string ResolveImport(string p_Importer, string p_Url)
        {
            var s_Dir = p_Importer.Contains('/') ? p_Importer[..p_Importer.LastIndexOf('/')] : "";
            var s_Parts = new List<string>(s_Dir.Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (var s_Seg in p_Url.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (s_Seg == "..") { if (s_Parts.Count > 0) s_Parts.RemoveAt(s_Parts.Count - 1); }
                else if (s_Seg != ".") s_Parts.Add(s_Seg);
            }
            var s_Name = string.Join("/", s_Parts);
            var s_Dot = s_Name.LastIndexOf('.');
            if (s_Dot > s_Name.LastIndexOf('/')) s_Name = s_Name[..s_Dot];
            return s_Name.ToLowerInvariant();
        }
    }
}
