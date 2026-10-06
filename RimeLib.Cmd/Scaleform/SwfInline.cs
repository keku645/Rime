using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RimeLib.Cmd.Scaleform
{
    /// <summary>
    /// Makes a movie self-contained: every ImportAssets/ImportAssets2 is replaced by copies of the imported characters,
    /// taken from the movie the url names, together with everything they reference (a sprite's children, a shape's
    /// bitmaps, a text field's font), under fresh character ids — the imported symbol itself under the id the movie
    /// already uses for it, so its placements do not change. The symbol's init action (its class registration) and its
    /// export name come along, so attachMovie and registerClass keep working. Needed for a player that never finishes
    /// preloading an imported movie that has imports of its own (the game resolves each import by resource name at load).
    /// </summary>
    public static class SwfInline
    {
        public class Report
        {
            public List<string> Notes { get; } = new();
            public int Imports, Characters;
            public override string ToString() => $"{Imports} imports inlined as {Characters} characters" + (Notes.Count > 0 ? "; " + string.Join("; ", Notes) : "");
        }

        const int c_ImportAssets = 57, c_DoAction = 12, c_ExportAssets = 56, c_DoInitAction = 59, c_DefineSprite = 39, c_DefineEditText = 37, c_DefineText = 11, c_DefineText2 = 33;
        const int c_CsmTextSettings = 74, c_DefineScalingGrid = 78, c_DefineFontAlignZones = 73, c_DefineFont2 = 48, c_DefineFont3 = 75;
        static readonly HashSet<int> s_Bitmaps = new() { 6, 20, 21, 35, 36, 90 };
        static readonly HashSet<int> s_Shapes = new() { GfxMovie.TagDefineShape, GfxMovie.TagDefineShape2, GfxMovie.TagDefineShape3, GfxMovie.TagDefineShape4 };

        /// <summary>Rewrites p_Movie in place. p_SourceByUrl gives the movie an import url names (already self-contained); null = leave that import as it is.</summary>
        public static void Inline(GfxMovie p_Movie, Func<string, GfxMovie?> p_SourceByUrl, Report p_Report)
        {
            var s_Used = new HashSet<ushort>(p_Movie.Characters().Keys);
            foreach (var t in p_Movie.Tags.Where(t => t.Code == GfxMovie.TagImportAssets2 || t.Code == c_ImportAssets))
                foreach (var (s_Id, _) in DecodeImport(t).Characters) s_Used.Add(s_Id);
            var s_Next = (ushort)(s_Used.Where(id => id < 20480).DefaultIfEmpty((ushort)0).Max() + 1);
            var s_Exported = new HashSet<string>(p_Movie.Exports().SelectMany(e => e.Characters.Select(c => c.Name)), StringComparer.Ordinal);
            // every name the movie imports, from any of its imports: that name belongs to the id the movie places, whichever import
            // is inlined first (kitselector imports Button.swf before iconsShared.swf, and a self-contained Button.swf exports the
            // service-star badge it absorbed from iconsShared — copied as an extra it took the name, the placed id 30 never got its
            // export, registerClass bound the class to the copy and the badge kept its authored "50")
            var s_ImportedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in p_Movie.Tags.Where(t => t.Code == GfxMovie.TagImportAssets2 || t.Code == c_ImportAssets))
                foreach (var (_, s_Name) in DecodeImport(t).Characters) s_ImportedNames.Add(s_Name);
            for (var i = 0; i < p_Movie.Tags.Count; ++i)
            {
                var t = p_Movie.Tags[i];
                if (t.Code != GfxMovie.TagImportAssets2 && t.Code != c_ImportAssets) continue;
                var s_Import = DecodeImport(t);
                var s_Source = p_SourceByUrl(s_Import.Url);
                if (s_Source == null) continue;
                if (s_Source.Tags.Any(x => x.Code == GfxMovie.TagImportAssets2 || x.Code == c_ImportAssets))
                    p_Report.Notes.Add($"{s_Import.Url} still has imports of its own: its inlined characters may miss what they import");
                var s_Copied = CopyCharacters(s_Source, s_Import.Characters, s_ImportedNames, ref s_Next, s_Exported, p_Report);
                p_Movie.Tags.RemoveAt(i);
                p_Movie.Tags.InsertRange(i, s_Copied);
                i += s_Copied.Count - 1;
                ++p_Report.Imports;
            }
        }

        /// <summary>ImportAssets (v1: url, count, entries) or ImportAssets2 (url, reserved u16, count, entries).</summary>
        public static ImportAssets2 DecodeImport(GfxTag t)
        {
            if (t.Code == GfxMovie.TagImportAssets2) return ImportAssets2.Decode(t.Body);
            var s_Out = new ImportAssets2();
            var p = 0;
            var e = Array.IndexOf(t.Body, (byte)0, p);
            s_Out.Url = Encoding.Latin1.GetString(t.Body, p, e - p); p = e + 1;
            var n = BitConverter.ToUInt16(t.Body, p); p += 2;
            for (var i = 0; i < n; ++i)
            {
                var s_Id = BitConverter.ToUInt16(t.Body, p); p += 2;
                e = Array.IndexOf(t.Body, (byte)0, p);
                s_Out.Characters.Add((s_Id, Encoding.Latin1.GetString(t.Body, p, e - p)));
                p = e + 1;
            }
            return s_Out;
        }

        static List<GfxTag> CopyCharacters(GfxMovie p_Source, IEnumerable<(ushort Id, string Name)> p_Wanted, HashSet<string> p_ImportedNames, ref ushort p_Next, HashSet<string> p_TargetExports, Report p_Report)
        {
            var s_Chars = p_Source.Characters();
            var s_Map = new Dictionary<ushort, ushort>();
            var s_Stack = new Stack<ushort>();
            // the source id that owns each name this import asks for: only that id may carry the name into the target
            var s_WantedIds = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            foreach (var (s_Id, s_Name) in p_Wanted)
            {
                // the font map spells its aliases "$MENU_bold" where the movies import "$Menu_bold": names match regardless of case
                var s_Export = p_Source.ExportedCharacter(s_Name)
                               ?? p_Source.Exports().SelectMany(e => e.Characters).Where(c => string.Equals(c.Name, s_Name, StringComparison.OrdinalIgnoreCase)).Select(c => (ushort?)c.Id).FirstOrDefault();
                if (s_Export == null) { p_Report.Notes.Add($"'{s_Name}' is not exported by the imported movie"); continue; }
                s_Map[s_Export.Value] = s_Id;
                s_WantedIds[s_Name] = s_Export.Value;
                s_Stack.Push(s_Export.Value);
            }
            // every other symbol the imported movie exports comes along too, under its name: the scripts attach them at runtime by name
            // (a kit row's arrow is a Button whose art is attachMovie("largeArrowButtonFlat") out of Button.swf, never referenced by a tag —
            // without it the arrows initialise to nothing and stay invisible)
            // (a name the target imports — from THIS import or any other — is never taken by an extra: the name belongs to the id the
            // target places; a self-contained Button.swf exports the service-star badge it absorbed from iconsShared, and copied as an
            // extra ahead of the iconsShared import it stole the name from kitselector's placed id, so registerClass bound the class to
            // the copy and the badge kept its authored "50")
            foreach (var c in p_Source.Exports().SelectMany(e => e.Characters))
            {
                if (s_Map.ContainsKey(c.Id) || p_ImportedNames.Contains(c.Name) || p_TargetExports.Contains(c.Name) || !s_Chars.ContainsKey(c.Id)) continue;
                s_Map[c.Id] = p_Next++;
                s_Stack.Push(c.Id);
            }
            var s_Seen = new HashSet<ushort>();
            while (s_Stack.Count > 0)
            {
                var s_Id = s_Stack.Pop();
                if (!s_Seen.Add(s_Id)) continue;
                if (!s_Chars.TryGetValue(s_Id, out var s_Tag)) { p_Report.Notes.Add($"character {s_Id} referenced but not defined in the imported movie"); continue; }
                foreach (var r in References(s_Tag, p_Report))
                {
                    if (!s_Map.ContainsKey(r)) s_Map[r] = p_Next++;
                    s_Stack.Push(r);
                }
            }
            var s_Out = new List<GfxTag>();
            foreach (var t in p_Source.Tags)
            {
                if (t.Code == c_ExportAssets)
                {
                    // a name the target imports is exported only under the id that import resolved to (a copy reached by reference
                    // may carry the same name in the source, and a name asked of another import stays with that import's id)
                    var s_Entries = ExportAssets.Decode(t.Body).Characters
                        .Where(c => s_Map.ContainsKey(c.Id) && !p_TargetExports.Contains(c.Name))
                        .Where(c => s_WantedIds.TryGetValue(c.Name, out var s_Owner) ? s_Owner == c.Id : !p_ImportedNames.Contains(c.Name))
                        .ToList();
                    if (s_Entries.Count == 0) continue;
                    foreach (var c in s_Entries) p_TargetExports.Add(c.Name);
                    var s_Body = new MemoryStream();
                    s_Body.Write(BitConverter.GetBytes((ushort)s_Entries.Count), 0, 2);
                    foreach (var (s_Id, s_Name) in s_Entries)
                    {
                        s_Body.Write(BitConverter.GetBytes(s_Map[s_Id]), 0, 2);
                        var s_Bytes = Encoding.Latin1.GetBytes(s_Name);
                        s_Body.Write(s_Bytes, 0, s_Bytes.Length);
                        s_Body.WriteByte(0);
                    }
                    s_Out.Add(MakeTag(c_ExportAssets, s_Body.ToArray()));
                    continue;
                }
                if (t.Body.Length < 2) continue;
                var s_Subject = BitConverter.ToUInt16(t.Body, 0);
                var s_HasSubject = s_Shapes.Contains(t.Code) || s_Bitmaps.Contains(t.Code) || t.Code is c_DefineSprite or c_DefineEditText or c_DefineText or c_DefineText2
                                   or c_DefineFont2 or c_DefineFont3 or c_CsmTextSettings or c_DefineScalingGrid or c_DefineFontAlignZones or c_DoInitAction;
                if (!s_HasSubject || !s_Map.ContainsKey(s_Subject)) continue;
                s_Out.Add(Remap(t, s_Map, p_Report));
                if (s_Shapes.Contains(t.Code) || s_Bitmaps.Contains(t.Code) || t.Code is c_DefineSprite or c_DefineEditText or c_DefineText or c_DefineText2 or c_DefineFont2 or c_DefineFont3) ++p_Report.Characters;
            }
            return s_Out;
        }

        /// <summary>The character ids a definition refers to (what must travel with it).</summary>
        static IEnumerable<ushort> References(GfxTag t, Report p_Report)
        {
            if (s_Shapes.Contains(t.Code))
            {
                SwfShape s_Shape;
                try { s_Shape = SwfShape.Decode(t.Code, t.Body); } catch (Exception s_Ex) { p_Report.Notes.Add($"shape {BitConverter.ToUInt16(t.Body, 0)}: {s_Ex.Message}"); yield break; }
                foreach (var f in s_Shape.Fills.Where(f => f.IsBitmap && f.BitmapId != 0xFFFF)) yield return f.BitmapId;
                foreach (var l in s_Shape.Lines.Where(l => l.Fill != null && l.Fill.IsBitmap && l.Fill.BitmapId != 0xFFFF)) yield return l.Fill!.BitmapId;
            }
            else if (t.Code == c_DefineSprite)
            {
                foreach (var c in t.Children.Where(c => GfxMovie.IsPlaceTag(c.Code)))
                {
                    PlaceObject2 p;
                    try { p = GfxMovie.DecodePlace(c); } catch { continue; }
                    if (p.HasCharacter) yield return p.CharacterId;
                }
            }
            else if (t.Code == c_DefineEditText)
            {
                var s_Text = SwfEditText.Decode(t.Body);
                if (s_Text.HasFont) yield return s_Text.FontId;
            }
            else if (t.Code is c_DefineText or c_DefineText2)
                p_Report.Notes.Add($"static text {BitConverter.ToUInt16(t.Body, 0)}: its fonts are not followed");
        }

        /// <summary>The tag with every character id it carries rewritten through the map.</summary>
        static GfxTag Remap(GfxTag t, Dictionary<ushort, ushort> p_Map, Report p_Report)
        {
            ushort M(ushort id) => p_Map.TryGetValue(id, out var m) ? m : id;
            if (t.Code == c_DefineSprite)
            {
                var s_Inner = new MemoryStream();
                s_Inner.Write(BitConverter.GetBytes(M(t.SpriteId)), 0, 2);
                s_Inner.Write(BitConverter.GetBytes(t.SpriteFrameCount), 0, 2);
                foreach (var c in t.Children)
                {
                    var s_Body = (byte[])c.Body.Clone();
                    if (GfxMovie.IsPlaceTag(c.Code)) PatchPlace(s_Body, c.Code == GfxMovie.TagPlaceObject3, M);
                    WriteTag(s_Inner, c.Code, s_Body);
                }
                return MakeTag(c_DefineSprite, s_Inner.ToArray());
            }
            var b = (byte[])t.Body.Clone();
            BitConverter.GetBytes(M(BitConverter.ToUInt16(b, 0))).CopyTo(b, 0);
            if (s_Shapes.Contains(t.Code))
            {
                var s_Shape = SwfShape.Decode(t.Code, t.Body);
                foreach (var o in s_Shape.BitmapIdOffsets)
                {
                    var s_Id = BitConverter.ToUInt16(b, o);
                    if (s_Id != 0xFFFF) BitConverter.GetBytes(M(s_Id)).CopyTo(b, o);
                }
            }
            else if (t.Code == c_DefineEditText)
            {
                var s_NBits = b[2] >> 3;
                var p = 2 + (5 + 4 * s_NBits + 7) / 8;
                if ((b[p] & 0x01) != 0) BitConverter.GetBytes(M(BitConverter.ToUInt16(b, p + 2))).CopyTo(b, p + 2);
            }
            return MakeTag(t.Code, b);
        }

        static void PatchPlace(byte[] b, bool p_V3, Func<ushort, ushort> M)
        {
            var s_Flags = b[0];
            if ((s_Flags & 0x02) == 0) return;
            var p = p_V3 ? 4 : 3;
            if (p_V3 && ((b[1] & 0x08) != 0 || ((b[1] & 0x10) != 0))) p = Array.IndexOf(b, (byte)0, p) + 1;
            BitConverter.GetBytes(M(BitConverter.ToUInt16(b, p))).CopyTo(b, p);
        }

        static void WriteTag(Stream p_Out, int p_Code, byte[] p_Body)
        {
            p_Out.Write(BitConverter.GetBytes((ushort)((p_Code << 6) | 0x3F)), 0, 2);
            p_Out.Write(BitConverter.GetBytes(p_Body.Length), 0, 4);
            p_Out.Write(p_Body, 0, p_Body.Length);
        }

        /// <summary>A tag built from its bytes and parsed back, so a sprite gets its id, frame count and children.</summary>
        static GfxTag MakeTag(int p_Code, byte[] p_Body)
        {
            var s_Stream = new MemoryStream();
            WriteTag(s_Stream, p_Code, p_Body);
            var s_Bytes = s_Stream.ToArray();
            return GfxTag.ParseStream(s_Bytes, 0, s_Bytes.Length).First();
        }

        // ------------------------------------------------------------------------------------------ class registrations

        /// <summary>
        /// The Object.registerClass(symbol, class) calls in the movie's scripts (DoInitAction / DoAction), read from the AVM1
        /// bytecode: the symbol name and the class path ("Widget.CustomizeKit.KitInfoBox"). What a widget movie does when it
        /// loads, so an importer can do it in the widget's stead.
        /// </summary>
        public static List<(string Symbol, string Class)> RegisterClassCalls(GfxMovie p_Movie)
        {
            var s_Out = new List<(string, string)>();
            foreach (var t in p_Movie.Tags.Where(t => t.Code is c_DoInitAction or c_DoAction))
            {
                var s_Actions = t.Code == c_DoInitAction ? t.Body.Skip(2).ToArray() : t.Body;
                var s_Tokens = Tokens(s_Actions);
                for (var i = 0; i < s_Tokens.Count; ++i)
                {
                    if (s_Tokens[i].Kind != "call" || i < 5) continue;
                    if (s_Tokens[i - 1].Kind != "str" || s_Tokens[i - 1].Text != "registerClass" || s_Tokens[i - 2].Kind != "getvar" || s_Tokens[i - 3].Text != "Object" || s_Tokens[i - 4].Kind != "int" || s_Tokens[i - 5].Kind != "str") continue;
                    var s_Symbol = s_Tokens[i - 5].Text;
                    // the class path sits in front: STR root GETVAR (STR member GETMEMBER)*
                    var s_Path = new List<string>();
                    var j = i - 6;
                    while (j >= 1 && s_Tokens[j].Kind == "getmember" && s_Tokens[j - 1].Kind == "str") { s_Path.Insert(0, s_Tokens[j - 1].Text); j -= 2; }
                    if (j >= 1 && s_Tokens[j].Kind == "getvar" && s_Tokens[j - 1].Kind == "str") s_Path.Insert(0, s_Tokens[j - 1].Text);
                    else continue;
                    s_Out.Add((s_Symbol, string.Join(".", s_Path)));
                }
            }
            return s_Out;
        }

        record Token(string Kind, string Text);

        static List<Token> Tokens(byte[] a)
        {
            var s_Out = new List<Token>();
            var s_Pool = new List<string>();
            var p = 0;
            while (p < a.Length)
            {
                var s_Code = a[p++];
                if (s_Code == 0) break;
                byte[] s_Data = Array.Empty<byte>();
                if (s_Code >= 0x80)
                {
                    if (p + 2 > a.Length) break;
                    var s_Len = BitConverter.ToUInt16(a, p); p += 2;
                    if (p + s_Len > a.Length) break;
                    s_Data = a.Skip(p).Take(s_Len).ToArray(); p += s_Len;
                }
                switch (s_Code)
                {
                    case 0x88:   // ConstantPool
                    {
                        s_Pool.Clear();
                        var n = BitConverter.ToUInt16(s_Data, 0); var q = 2;
                        for (var i = 0; i < n && q < s_Data.Length; ++i) { var e = Array.IndexOf(s_Data, (byte)0, q); if (e < 0) break; s_Pool.Add(Encoding.UTF8.GetString(s_Data, q, e - q)); q = e + 1; }
                        break;
                    }
                    case 0x96:   // Push: several values in one action
                    {
                        var q = 0;
                        while (q < s_Data.Length)
                        {
                            var s_Type = s_Data[q++];
                            switch (s_Type)
                            {
                                case 0: { var e = Array.IndexOf(s_Data, (byte)0, q); if (e < 0) e = s_Data.Length; s_Out.Add(new Token("str", Encoding.UTF8.GetString(s_Data, q, e - q))); q = e + 1; break; }
                                case 1: s_Out.Add(new Token("num", "")); q += 4; break;
                                case 2: s_Out.Add(new Token("null", "")); break;
                                case 3: s_Out.Add(new Token("undef", "")); break;
                                case 4: s_Out.Add(new Token("reg", "")); q += 1; break;
                                case 5: s_Out.Add(new Token("bool", "")); q += 1; break;
                                case 6: s_Out.Add(new Token("num", "")); q += 8; break;
                                case 7: s_Out.Add(new Token("int", BitConverter.ToInt32(s_Data, q).ToString())); q += 4; break;
                                case 8: { var k = s_Data[q++]; s_Out.Add(new Token("str", k < s_Pool.Count ? s_Pool[k] : "")); break; }
                                case 9: { var k = BitConverter.ToUInt16(s_Data, q); q += 2; s_Out.Add(new Token("str", k < s_Pool.Count ? s_Pool[k] : "")); break; }
                                default: q = s_Data.Length; break;
                            }
                        }
                        break;
                    }
                    case 0x1C: s_Out.Add(new Token("getvar", "")); break;
                    case 0x4E: s_Out.Add(new Token("getmember", "")); break;
                    case 0x52: s_Out.Add(new Token("call", "")); break;
                    default: s_Out.Add(new Token("op", s_Code.ToString("X2"))); break;
                }
            }
            return s_Out;
        }
    }
}
