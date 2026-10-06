using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace RimeLib.Cmd.Scaleform
{
    /// <summary>
    /// Stage-level model of a Scaleform GFX/CFX movie: the tag stream with DefineSprite children parsed, and
    /// the tags a UI screen is composed of decoded (PlaceObject2 = a widget placed on the stage,
    /// ImportAssets2 = a widget symbol imported from another movie, ExportAssets = symbols this movie exports).
    ///
    /// Editing is byte surgery with the same guarantees the offline tooling was verified with in game:
    /// a tag that was not touched is written back from its original bytes (header form included); only a
    /// modified tag and the DefineSprite that contains it are re-emitted, and the file header length
    /// (payload + 8) is rewritten. Loading and saving without edits reproduces the input payload exactly.
    /// </summary>
    public class GfxMovie
    {
        public const int TagEnd = 0;
        public const int TagShowFrame = 1;
        public const int TagPlaceObject2 = 26;
        public const int TagDefineSprite = 39;
        public const int TagExportAssets = 56;
        public const int TagImportAssets2 = 71;

        public string Magic { get; private set; } = "CFX";
        public byte Version { get; private set; }
        public bool Compressed => Magic[0] == 'C';

        /// <summary>RECT + frame rate + frame count, kept verbatim.</summary>
        public byte[] HeaderRaw { get; private set; } = Array.Empty<byte>();

        /// <summary>The movie's frame size from its header RECT, in px (BF3 screens: 1280 × 720).</summary>
        public (double Width, double Height) FrameSize
        {
            get
            {
                if (HeaderRaw.Length < 5) return (0, 0);
                var r = ReadRect(HeaderRaw, 0);
                return ((r.Right - r.Left) / 20.0, (r.Bottom - r.Top) / 20.0);
            }
        }

        /// <summary>Frames per second from the header (8.8 fixed).</summary>
        public double FrameRate => HeaderRaw.Length >= 4 ? BitConverter.ToUInt16(HeaderRaw, HeaderRaw.Length - 4) / 256.0 : 0;
        public ushort FrameCount => HeaderRaw.Length >= 2 ? BitConverter.ToUInt16(HeaderRaw, HeaderRaw.Length - 2) : (ushort)0;

        public List<GfxTag> Tags { get; } = new();

        public static GfxMovie Load(string p_Path) => Load(File.ReadAllBytes(p_Path));

        public static GfxMovie Load(byte[] p_File)
        {
            var s_Movie = new GfxMovie();
            s_Movie.Magic = Encoding.ASCII.GetString(p_File, 0, 3);
            if (s_Movie.Magic != "CFX" && s_Movie.Magic != "GFX" && s_Movie.Magic != "CWS" && s_Movie.Magic != "FWS")
                throw new InvalidDataException($"not a SWF/GFX movie (magic '{s_Movie.Magic}')");
            s_Movie.Version = p_File[3];
            var s_Declared = BitConverter.ToUInt32(p_File, 4);

            byte[] s_Payload;
            if (s_Movie.Compressed)
            {
                var s_Inflater = new Inflater(false);
                s_Inflater.SetInput(p_File, 8, p_File.Length - 8);
                var s_Out = new MemoryStream();
                var s_Buffer = new byte[65536];
                while (!s_Inflater.IsFinished)
                {
                    var s_Count = s_Inflater.Inflate(s_Buffer);
                    if (s_Count == 0 && (s_Inflater.IsNeedingInput || s_Inflater.IsNeedingDictionary))
                        break;
                    s_Out.Write(s_Buffer, 0, s_Count);
                }
                s_Payload = s_Out.ToArray();
            }
            else
            {
                s_Payload = p_File.Skip(8).ToArray();
            }

            if (s_Declared != s_Payload.Length + 8)
                throw new InvalidDataException($"header length {s_Declared} != payload {s_Payload.Length} + 8");

            var s_NBits = s_Payload[0] >> 3;
            var s_HeaderLength = (5 + 4 * s_NBits + 7) / 8 + 4;
            s_Movie.HeaderRaw = s_Payload.Take(s_HeaderLength).ToArray();
            s_Movie.Tags.AddRange(GfxTag.ParseStream(s_Payload, s_HeaderLength, s_Payload.Length));
            return s_Movie;
        }

        public byte[] ToPayload()
        {
            var s_Out = new MemoryStream();
            s_Out.Write(HeaderRaw, 0, HeaderRaw.Length);
            foreach (var s_Tag in Tags)
            {
                var s_Bytes = s_Tag.Emit();
                s_Out.Write(s_Bytes, 0, s_Bytes.Length);
            }
            return s_Out.ToArray();
        }

        public byte[] ToFile()
        {
            var s_Payload = ToPayload();
            var s_Out = new MemoryStream();
            s_Out.Write(Encoding.ASCII.GetBytes(Magic), 0, 3);
            s_Out.WriteByte(Version);
            s_Out.Write(BitConverter.GetBytes((uint)(s_Payload.Length + 8)), 0, 4);
            if (Compressed)
            {
                var s_Deflater = new Deflater(Deflater.BEST_COMPRESSION, false);
                s_Deflater.SetInput(s_Payload);
                s_Deflater.Finish();
                var s_Buffer = new byte[65536];
                while (!s_Deflater.IsFinished)
                {
                    var s_Count = s_Deflater.Deflate(s_Buffer);
                    s_Out.Write(s_Buffer, 0, s_Count);
                }
            }
            else
            {
                s_Out.Write(s_Payload, 0, s_Payload.Length);
            }
            return s_Out.ToArray();
        }

        public void Save(string p_Path) => File.WriteAllBytes(p_Path, ToFile());

        // ------------------------------------------------------------------------------------------ queries

        /// <summary>A tag that places a character on a timeline: PlaceObject2 (26) or PlaceObject3 (70).</summary>
        public static bool IsPlaceTag(int p_Code) => p_Code is TagPlaceObject2 or TagPlaceObject3;

        /// <summary>The placement record of a PlaceObject2/3 tag (the PlaceObject3 extras kept verbatim).</summary>
        public static PlaceObject2 DecodePlace(GfxTag p_Tag) => PlaceObject2.Decode(p_Tag.Body, p_Tag.Code == TagPlaceObject3);

        /// <summary>Every PlaceObject2/3 in the movie, with the sprite that contains it (null = top level).</summary>
        public IEnumerable<(GfxTag? Sprite, GfxTag Tag, PlaceObject2 Place)> Placements()
        {
            foreach (var s_Tag in Tags)
            {
                if (IsPlaceTag(s_Tag.Code))
                    yield return (null, s_Tag, DecodePlace(s_Tag));
                if (s_Tag.Code == TagDefineSprite)
                    foreach (var s_Child in s_Tag.Children)
                        if (IsPlaceTag(s_Child.Code))
                            yield return (s_Tag, s_Child, DecodePlace(s_Child));
            }
        }

        public IEnumerable<ImportAssets2> Imports() =>
            Tags.Where(t => t.Code == TagImportAssets2).Select(t => ImportAssets2.Decode(t.Body));

        public IEnumerable<ExportAssets> Exports() =>
            Tags.Where(t => t.Code == TagExportAssets).Select(t => ExportAssets.Decode(t.Body));

        public const int TagDefineSubImage = 1008, TagDefineExternalImage2 = 1009;

        /// <summary>The movie's atlas textures (GFx DefineExternalImage2, tag 1009): one per BF3 movie in practice.</summary>
        public record ExternalImage(uint ImageId, ushort Format, ushort Width, ushort Height, string ExportName, string FileName);

        /// <summary>A bitmap character that is a rectangle of an atlas (GFx DefineSubImage, tag 1008; pixels).</summary>
        public record SubImage(ushort CharacterId, ushort ImageId, ushort Left, ushort Top, ushort Right, ushort Bottom);

        public IEnumerable<ExternalImage> ExternalImages()
        {
            foreach (var t in Tags.Where(t => t.Code == TagDefineExternalImage2))
            {
                var b = t.Body;
                var p = 0;
                var s_Id = BitConverter.ToUInt32(b, p); p += 4;
                var s_Fmt = BitConverter.ToUInt16(b, p); p += 2;
                var s_W = BitConverter.ToUInt16(b, p); p += 2;
                var s_H = BitConverter.ToUInt16(b, p); p += 2;
                var s_ExpLen = b[p++]; var s_Exp = Encoding.ASCII.GetString(b, p, s_ExpLen); p += s_ExpLen;
                var s_FileLen = b[p++]; var s_File = Encoding.ASCII.GetString(b, p, s_FileLen);
                yield return new ExternalImage(s_Id & 0xFFFF, s_Fmt, s_W, s_H, s_Exp, s_File);
            }
        }

        public IEnumerable<SubImage> SubImages()
        {
            foreach (var t in Tags.Where(t => t.Code == TagDefineSubImage && t.Body.Length >= 12))
                yield return new SubImage(BitConverter.ToUInt16(t.Body, 0), BitConverter.ToUInt16(t.Body, 2), BitConverter.ToUInt16(t.Body, 4),
                                          BitConverter.ToUInt16(t.Body, 6), BitConverter.ToUInt16(t.Body, 8), BitConverter.ToUInt16(t.Body, 10));
        }

        /// <summary>
        /// The placement a clip name addresses. A bare name must be unique in the movie; "&lt;sprite&gt;/&lt;name&gt;" looks inside one
        /// sprite only, the sprite given by its character id or by the symbol name it is exported under ("KitView_2/button1") —
        /// a widget movie's row symbols place clips of the same names (button1 in every row kind).
        /// </summary>
        (GfxTag? Sprite, GfxTag Tag, PlaceObject2 Place) FindPlacement(string p_Name)
        {
            var s_Slash = p_Name.IndexOf('/');
            var s_Clip = s_Slash < 0 ? p_Name : p_Name[(s_Slash + 1)..];
            ushort? s_SpriteId = s_Slash < 0 ? null : SpriteIdOf(p_Name[..s_Slash]);
            var s_Hits = Placements().Where(p => p.Place.Name == s_Clip && (s_SpriteId == null || p.Sprite?.SpriteId == s_SpriteId)).ToList();
            if (s_Hits.Count == 0)
                throw new Exception($"no placement named '{p_Name}'");
            if (s_Hits.Count > 1)
                throw new Exception($"{s_Hits.Count} placements named '{p_Name}' -- refusing to guess");
            return s_Hits[0];
        }

        /// <summary>A sprite's character id from its number or from the symbol name it is exported under.</summary>
        ushort SpriteIdOf(string p_Sprite)
        {
            if (ushort.TryParse(p_Sprite, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s_Id))
                return s_Id;
            foreach (var s_Export in Exports())
                foreach (var (s_Char, s_Symbol) in s_Export.Characters)
                    if (s_Symbol == p_Sprite)
                        return s_Char;
            throw new Exception($"no sprite '{p_Sprite}' (neither a character id nor an exported symbol)");
        }

        static void Replace(GfxTag? p_Sprite, GfxTag p_Tag, byte[] p_NewBody)
        {
            p_Tag.SetBody(p_NewBody);
            p_Sprite?.MarkModified();
        }

        // ------------------------------------------------------------------------------------------ edits

        public void Move(string p_Name, double p_XPx, double p_YPx)
        {
            var (s_Sprite, s_Tag, s_Place) = FindPlacement(p_Name);
            if (s_Place.Matrix == null) throw new Exception($"'{p_Name}' has no matrix");
            s_Place.Matrix.TranslateX = (int)System.Math.Round(p_XPx * 20);
            s_Place.Matrix.TranslateY = (int)System.Math.Round(p_YPx * 20);
            Replace(s_Sprite, s_Tag, s_Place.Encode());
        }

        /// <summary>Sets the placement's scale (factors along its own axes). A rotated placement keeps its angle: the matrix is rebuilt from (sx, sy, angle).</summary>
        public void Scale(string p_Name, double p_Sx, double p_Sy)
        {
            var (s_Sprite, s_Tag, s_Place) = FindPlacement(p_Name);
            if (s_Place.Matrix == null) throw new Exception($"'{p_Name}' has no matrix");
            if (s_Place.Matrix.HasRotate)
                SetLinear(s_Place.Matrix, p_Sx, p_Sy, s_Place.Matrix.Angle);
            else
            {
                s_Place.Matrix.ScaleX = (int)System.Math.Round(p_Sx * 65536);
                s_Place.Matrix.ScaleY = (int)System.Math.Round(p_Sy * 65536);
                s_Place.Matrix.HasScale = true;
            }
            Replace(s_Sprite, s_Tag, s_Place.Encode());
        }

        /// <summary>
        /// Sets the placement's rotation in degrees (positive = clockwise on screen, Flash's convention) keeping the
        /// scale magnitudes along its own axes: [ScaleX RotateSkew1; RotateSkew0 ScaleY] = [sx·cos −sy·sin; sx·sin sy·cos].
        /// </summary>
        public void Rotate(string p_Name, double p_Degrees)
        {
            var (s_Sprite, s_Tag, s_Place) = FindPlacement(p_Name);
            if (s_Place.Matrix == null) throw new Exception($"'{p_Name}' has no matrix");
            var (s_Sx, s_Sy) = s_Place.Matrix.Magnitudes;
            SetLinear(s_Place.Matrix, s_Sx, s_Sy, p_Degrees * System.Math.PI / 180.0);
            Replace(s_Sprite, s_Tag, s_Place.Encode());
        }

        static void SetLinear(SwfMatrix m, double p_Sx, double p_Sy, double p_Theta)
        {
            var s_Cos = System.Math.Cos(p_Theta); var s_Sin = System.Math.Sin(p_Theta);
            m.ScaleX = (int)System.Math.Round(p_Sx * s_Cos * 65536); m.ScaleY = (int)System.Math.Round(p_Sy * s_Cos * 65536);
            m.HasScale = true;
            m.RotateSkew0 = (int)System.Math.Round(p_Sx * s_Sin * 65536); m.RotateSkew1 = (int)System.Math.Round(-p_Sy * s_Sin * 65536);
            m.HasRotate = m.RotateSkew0 != 0 || m.RotateSkew1 != 0;
        }

        public void SetCharacter(string p_Name, ushort p_CharacterId)
        {
            var (s_Sprite, s_Tag, s_Place) = FindPlacement(p_Name);
            s_Place.CharacterId = p_CharacterId;
            s_Place.HasCharacter = true;
            Replace(s_Sprite, s_Tag, s_Place.Encode());
        }

        /// <summary>
        /// Puts a placement at a depth — its drawing order among its sprite's siblings (a higher depth draws in front; the display
        /// list is ordered by depth, not by the tags' order). The sibling that held that depth takes the placement's old one (a swap),
        /// so no two clips of a sprite share a depth.
        /// </summary>
        public void SetDepth(string p_Name, ushort p_Depth)
        {
            var (s_Sprite, s_Tag, s_Place) = FindPlacement(p_Name);
            if (s_Place.Depth == p_Depth) return;
            var s_Old = s_Place.Depth;
            var s_Other = Placements().FirstOrDefault(p => p.Sprite == s_Sprite && p.Tag != s_Tag && p.Place.Depth == p_Depth);
            if (s_Other.Tag != null)
            {
                s_Other.Place.Depth = s_Old;
                Replace(s_Other.Sprite, s_Other.Tag, s_Other.Place.Encode());
            }
            s_Place.Depth = p_Depth;
            Replace(s_Sprite, s_Tag, s_Place.Encode());
        }

        /// <summary>The construct-time variables a placement sets on itself (FrostEd's per-instance widget parameters), or null when it has none.</summary>
        public List<(string Name, object? Value)>? ClipVarsOf(string p_Name) => FindPlacement(p_Name).Place.ClipVars;

        /// <summary>
        /// Sets construct-time variables on a placement (m_rowType = "bold1"…): a variable of the same name is replaced, a new one
        /// appended, the rest of the record and every other event record kept. A widget's constructor reads them before onClipLoad
        /// (TextField attaches "mc_" + m_rowType), so a placement the editor adds needs the ones its widget's other placements carry.
        /// </summary>
        public void SetClipVars(string p_Name, IList<(string Name, object? Value)> p_Vars)
        {
            var (s_Sprite, s_Tag, s_Place) = FindPlacement(p_Name);
            var s_Actions = s_Place.DecodeClipActions();
            if (s_Actions == null && s_Place.HasClipActions) throw new Exception($"'{p_Name}': its clip actions sit behind filters / blend data this editor does not decode");
            s_Actions ??= new ClipActions();
            var s_Merged = s_Actions.ConstructVars();
            foreach (var (s_Var, s_Value) in p_Vars)
            {
                var i = s_Merged.FindIndex(x => x.Name == s_Var);
                if (i >= 0) s_Merged[i] = (s_Var, s_Value); else s_Merged.Add((s_Var, s_Value));
            }
            s_Actions.SetConstructVars(s_Merged);
            s_Place.SetClipActions(s_Actions);
            Replace(s_Sprite, s_Tag, s_Place.Encode());
        }

        /// <summary>"m_rowType=bold1|m_align=left|m_useBorder=false" → typed variables (see ClipActions.ParseValue).</summary>
        public static List<(string Name, object? Value)> ParseClipVars(string p_Spec)
        {
            var s_Out = new List<(string, object?)>();
            foreach (var s_Part in p_Spec.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = s_Part.IndexOf('=');
                if (i <= 0) throw new Exception($"clip variable '{s_Part}' is not name=value");
                s_Out.Add((s_Part[..i].Trim(), ClipActions.ParseValue(s_Part[(i + 1)..])));
            }
            return s_Out;
        }

        public static string FormatClipVars(IEnumerable<(string Name, object? Value)> p_Vars) => string.Join("|", p_Vars.Select(v => v.Name + "=" + ClipActions.FormatValue(v.Value)));

        public void Remove(string p_Name)
        {
            var (s_Sprite, s_Tag, _) = FindPlacement(p_Name);
            var s_List = s_Sprite?.Children ?? Tags;
            s_List.Remove(s_Tag);
            s_Sprite?.MarkModified();
        }

        /// <summary>Appends an ImportAssets2 right after the last top-level import (before the sprites that use it).</summary>
        public void AddImport(string p_Url, ushort p_CharacterId, string p_Symbol)
        {
            var s_Last = Tags.FindLastIndex(t => t.Code == TagImportAssets2);
            var s_Tag = GfxTag.Create(TagImportAssets2, new ImportAssets2 { Url = p_Url, Characters = { (p_CharacterId, p_Symbol) } }.Encode());
            Tags.Insert(s_Last < 0 ? 0 : s_Last + 1, s_Tag);
        }

        /// <summary>
        /// Puts a frame action on the main timeline, in front of the first ShowFrame: a compiled DoAction body (End included) with
        /// string variables assigned in front of it (what the script reads as its parameters) and a marker variable
        /// (_rueScript_&lt;name&gt; = "1") that names it — a movie already carrying a script of that name gets it replaced. Frame
        /// actions run after the frame's placements exist and before the loader reports the movie loaded, so a script can wrap
        /// methods on the instances the screen places before the engine initialises them (the data recorder rides the same way).
        /// </summary>
        public void AddFrameScript(string p_Name, IEnumerable<(string Name, string Value)> p_Vars, byte[] p_Action)
        {
            var s_Marker = "_rueScript_" + p_Name;
            var s_Body = GfxToSwf.AssignStrings(new[] { (s_Marker, "1") }.Concat(p_Vars)).Concat(p_Action).ToArray();
            var s_Old = Tags.FindIndex(t => t.Code == 12 && Encoding.Latin1.GetString(t.Body).Contains(s_Marker));
            if (s_Old >= 0) { Tags[s_Old] = GfxTag.Create(12, s_Body); return; }
            var s_Index = Tags.FindIndex(t => t.Code == TagShowFrame);
            if (s_Index < 0) throw new Exception("the movie has no ShowFrame on its main timeline");
            Tags.Insert(s_Index, GfxTag.Create(12, s_Body));
        }

        /// <summary>The names of the frame scripts on the main timeline (AddFrameScript's markers).</summary>
        public IEnumerable<string> FrameScripts() =>
            Tags.Where(t => t.Code == 12).Select(t => Encoding.Latin1.GetString(t.Body)).Where(b => b.Contains("_rueScript_"))
                .Select(b => { var i = b.IndexOf("_rueScript_", StringComparison.Ordinal) + "_rueScript_".Length; var e = b.IndexOf('\0', i); return e > i ? b[i..e] : b[i..]; });

        /// <summary>
        /// A second placement of what an existing one places — same character, scale and construct-time variables (a widget
        /// clip's per-instance parameters) — under a new name, at a free depth of the same sprite and a new position in pixels,
        /// right after the original. What the game does when one of its symbols carries two of the same widget (a kit row's
        /// button1 and button2 are one Button at one scale, only their names and places differ).
        /// </summary>
        public void ClonePlacement(string p_Source, string p_Name, ushort p_Depth, double p_XPx, double p_YPx)
        {
            var (s_Sprite, s_Tag, _) = FindPlacement(p_Source);
            var s_Siblings = s_Sprite?.Children ?? Tags;
            if (s_Siblings.Any(c => IsPlaceTag(c.Code) && DecodePlace(c).Depth == p_Depth))
                throw new Exception($"depth {p_Depth} is already used beside '{p_Source}'");
            if (s_Siblings.Any(c => IsPlaceTag(c.Code) && DecodePlace(c).Name == p_Name))
                throw new Exception($"'{p_Name}' is already placed beside '{p_Source}'");
            var s_Copy = DecodePlace(s_Tag);
            if (s_Copy.Matrix == null) throw new Exception($"'{p_Source}' has no matrix");
            s_Copy.Name = p_Name;
            s_Copy.HasName = true;
            s_Copy.Depth = p_Depth;
            s_Copy.Matrix.TranslateX = (int)System.Math.Round(p_XPx * 20);
            s_Copy.Matrix.TranslateY = (int)System.Math.Round(p_YPx * 20);
            s_Siblings.Insert(s_Siblings.IndexOf(s_Tag) + 1, GfxTag.Create(s_Tag.Code, s_Copy.Encode()));
            s_Sprite?.MarkModified();
        }

        /// <summary>Appends a named placement inside a DefineSprite, before its first ShowFrame. Position in pixels.</summary>
        public void AddPlacement(ushort p_SpriteId, string p_Name, ushort p_CharacterId, ushort p_Depth,
                                 double p_XPx, double p_YPx, double p_Sx, double p_Sy)
        {
            var s_Sprite = Tags.FirstOrDefault(t => t.Code == TagDefineSprite && t.SpriteId == p_SpriteId)
                           ?? throw new Exception($"no DefineSprite with id {p_SpriteId}");
            if (s_Sprite.Children.Any(c => IsPlaceTag(c.Code) && DecodePlace(c).Depth == p_Depth))
                throw new Exception($"depth {p_Depth} is already used in sprite {p_SpriteId}");
            var s_Place = new PlaceObject2
            {
                HasCharacter = true, CharacterId = p_CharacterId, Depth = p_Depth, HasName = true, Name = p_Name,
                Matrix = new SwfMatrix
                {
                    HasScale = true, ScaleX = (int)System.Math.Round(p_Sx * 65536), ScaleY = (int)System.Math.Round(p_Sy * 65536),
                    TranslateX = (int)System.Math.Round(p_XPx * 20), TranslateY = (int)System.Math.Round(p_YPx * 20),
                },
            };
            var s_Index = s_Sprite.Children.FindIndex(c => c.Code == TagShowFrame);
            s_Sprite.Children.Insert(s_Index < 0 ? s_Sprite.Children.Count : s_Index, GfxTag.Create(TagPlaceObject2, s_Place.Encode()));
            s_Sprite.MarkModified();
        }

        // ------------------------------------------------------------------------------------------ bounds

        public const int TagDefineShape = 2, TagDefineText = 11, TagDefineShape2 = 22, TagDefineShape3 = 32, TagDefineText2 = 33,
                         TagDefineEditText = 37, TagDefineShape4 = 83;

        /// <summary>Axis-aligned bounds in twips.</summary>
        public struct Bounds
        {
            public int Left, Top, Right, Bottom;
            public bool Empty => Right <= Left || Bottom <= Top;
            public static Bounds Union(Bounds a, Bounds b)
            {
                if (a.Empty) return b;
                if (b.Empty) return a;
                return new Bounds { Left = System.Math.Min(a.Left, b.Left), Top = System.Math.Min(a.Top, b.Top), Right = System.Math.Max(a.Right, b.Right), Bottom = System.Math.Max(a.Bottom, b.Bottom) };
            }
            public Bounds Transform(SwfMatrix? m)
            {
                if (m == null) return this;
                double sx = m.ScaleXf, sy = m.ScaleYf;
                var l = (int)System.Math.Round(Left * sx) + m.TranslateX; var r = (int)System.Math.Round(Right * sx) + m.TranslateX;
                var t = (int)System.Math.Round(Top * sy) + m.TranslateY; var b = (int)System.Math.Round(Bottom * sy) + m.TranslateY;
                return new Bounds { Left = System.Math.Min(l, r), Right = System.Math.Max(l, r), Top = System.Math.Min(t, b), Bottom = System.Math.Max(t, b) };
            }
            public Bounds Transform(Affine m)
            {
                if (Empty) return this;
                var (x0, y0) = m.Apply(Left, Top); var (x1, y1) = m.Apply(Right, Top); var (x2, y2) = m.Apply(Left, Bottom); var (x3, y3) = m.Apply(Right, Bottom);
                return new Bounds
                {
                    Left = (int)System.Math.Round(System.Math.Min(System.Math.Min(x0, x1), System.Math.Min(x2, x3))), Right = (int)System.Math.Round(System.Math.Max(System.Math.Max(x0, x1), System.Math.Max(x2, x3))),
                    Top = (int)System.Math.Round(System.Math.Min(System.Math.Min(y0, y1), System.Math.Min(y2, y3))), Bottom = (int)System.Math.Round(System.Math.Max(System.Math.Max(y0, y1), System.Math.Max(y2, y3))),
                };
            }
            public override string ToString() => string.Format(System.Globalization.CultureInfo.InvariantCulture, "[{0:0.#},{1:0.#} .. {2:0.#},{3:0.#}]px", Left / 20.0, Top / 20.0, Right / 20.0, Bottom / 20.0);
        }

        /// <summary>A 2D affine transform in twips (x' = A x + C y + Tx; y' = B x + D y + Ty), the SWF MATRIX as numbers.</summary>
        public struct Affine
        {
            public double A, B, C, D, Tx, Ty;
            public static readonly Affine Identity = new() { A = 1, D = 1 };
            public static Affine FromSwf(SwfMatrix? m) => m == null ? Identity : new Affine
            {
                A = m.ScaleXf, B = m.HasRotate ? m.RotateSkew0 / 65536.0 : 0, C = m.HasRotate ? m.RotateSkew1 / 65536.0 : 0, D = m.ScaleYf, Tx = m.TranslateX, Ty = m.TranslateY,
            };
            /// <summary>parent ∘ local: apply local first, then parent.</summary>
            public static Affine Multiply(Affine p, Affine l) => new()
            {
                A = p.A * l.A + p.C * l.B, B = p.B * l.A + p.D * l.B,
                C = p.A * l.C + p.C * l.D, D = p.B * l.C + p.D * l.D,
                Tx = p.A * l.Tx + p.C * l.Ty + p.Tx, Ty = p.B * l.Tx + p.D * l.Ty + p.Ty,
            };
            public (double X, double Y) Apply(double x, double y) => (A * x + C * y + Tx, B * x + D * y + Ty);
            public (double X, double Y) ApplyVector(double x, double y) => (A * x + C * y, B * x + D * y);
            public Affine Inverse()
            {
                var det = A * D - B * C;
                if (System.Math.Abs(det) < 1e-12) return Identity;
                var ia = D / det; var ib = -B / det; var ic = -C / det; var id = A / det;
                return new Affine { A = ia, B = ib, C = ic, D = id, Tx = -(ia * Tx + ic * Ty), Ty = -(ib * Tx + id * Ty) };
            }
            public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1 && Tx == 0 && Ty == 0;
        }

        /// <summary>A named placement as it sits on the stage: inside which sprite, with the transform of everything above it.</summary>
        public class StagePlacement
        {
            public GfxTag Sprite = null!;          // the DefineSprite whose timeline holds the placement
            public GfxTag Tag = null!;
            public PlaceObject2 Place = null!;
            public Affine Parent;                  // stage <- sprite's own space (the sprites above, composed)
            public string Path = "";               // names from the root: instance1/PageHeader_01
            public Affine World => Affine.Multiply(Parent, Affine.FromSwf(Place.Matrix));
        }

        /// <summary>
        /// The named PlaceObject2/3 placements reachable from frame 1 of the main timeline, walking into the
        /// movie's own sprites (imports stop the walk: their insides belong to the widget), each with the
        /// composed transform of the sprites above it. A screen's root sprite is often placed at the stage
        /// centre with its children at negative local coordinates — without the parent transform their
        /// bounds land in the wrong place.
        /// </summary>
        public List<StagePlacement> StagePlacements()
        {
            var s_Out = new List<StagePlacement>();
            var s_Chars = Characters();
            void Walk(IEnumerable<GfxTag> p_Tags, GfxTag? p_Sprite, Affine p_Parent, string p_Path, int p_Depth)
            {
                if (p_Depth > 8) return;
                var s_Named = new Dictionary<ushort, (GfxTag Tag, PlaceObject2 Place)>();
                foreach (var t in p_Tags)
                {
                    if (t.Code == TagShowFrame) break;
                    if (IsPlaceTag(t.Code))
                    {
                        var p = DecodePlace(t);
                        if (p.HasName && p.Name != null && p_Sprite != null) s_Named[p.Depth] = (t, p);
                    }
                }
                foreach (var fp in FrameOnePlacements(p_Tags))
                {
                    var s_Path = p_Path.Length == 0 ? (fp.Name ?? "") : (fp.Name == null ? p_Path : p_Path + "/" + fp.Name);
                    if (fp.Name != null && p_Sprite != null && s_Named.TryGetValue(fp.Depth, out var s_Entry))
                        s_Out.Add(new StagePlacement { Sprite = p_Sprite, Tag = s_Entry.Tag, Place = s_Entry.Place, Parent = p_Parent, Path = s_Path });
                    if (s_Chars.TryGetValue(fp.CharacterId, out var s_Char) && s_Char.Code == TagDefineSprite)
                        Walk(s_Char.Children, s_Char, Affine.Multiply(p_Parent, Affine.FromSwf(fp.Matrix)), s_Path, p_Depth + 1);
                }
            }
            Walk(Tags, null, Affine.Identity, "", 0);
            return s_Out;
        }

        static Bounds ReadRect(byte[] p_Body, int p_Pos)
        {
            var c = new BitCursor(p_Body, p_Pos);
            var n = c.Read(5);
            var r = new Bounds { Left = c.Read(n, true), Right = c.Read(n, true), Top = c.Read(n, true), Bottom = c.Read(n, true) };
            return r;
        }

        /// <summary>Character id -> defining tag (top level only; sprites included).</summary>
        public Dictionary<ushort, GfxTag> Characters()
        {
            var s_Out = new Dictionary<ushort, GfxTag>();
            foreach (var t in Tags)
            {
                if (t.Code == TagDefineSprite) { s_Out[t.SpriteId] = t; continue; }
                if (t.Code is TagDefineShape or TagDefineShape2 or TagDefineShape3 or TagDefineShape4 or TagDefineText or TagDefineText2 or TagDefineEditText
                    or 6 or 20 or 21 or 35 or 36 or 46 or 84 or 48 or 75 or 1001 or TagDefineSubImage)
                    if (t.Body.Length >= 2)
                        s_Out[BitConverter.ToUInt16(t.Body, 0)] = t;
            }
            return s_Out;
        }

        /// <summary>Imported character id -> (url, symbol name).</summary>
        public Dictionary<ushort, (string Url, string Symbol)> ImportedCharacters()
        {
            var s_Out = new Dictionary<ushort, (string, string)>();
            foreach (var i in Imports())
                foreach (var (id, name) in i.Characters)
                    s_Out[id] = (i.Url, name);
            return s_Out;
        }

        /// <summary>
        /// Bounds of a character of this movie, in the character's own coordinates (twips). Sprites are the union
        /// of their placed children (frame 1) transformed by their matrices. Imported characters are resolved
        /// through p_Import(url, symbol) -> (movie, character id) when given; unknown characters report empty bounds.
        /// </summary>
        public Bounds CharacterBounds(ushort p_Id, Func<string, string, (GfxMovie? Movie, ushort Id)>? p_Import = null, int p_Depth = 0)
        {
            if (p_Depth > 8) return default;
            var s_Chars = Characters();
            if (!s_Chars.TryGetValue(p_Id, out var t))
            {
                if (p_Import != null && ImportedCharacters().TryGetValue(p_Id, out var imp))
                {
                    var (m, id) = p_Import(imp.Url, imp.Symbol);
                    if (m != null) return m.CharacterBounds(id, p_Import, p_Depth + 1);
                }
                return default;
            }
            switch (t.Code)
            {
                case TagDefineShape: case TagDefineShape2: case TagDefineShape3: case TagDefineShape4:
                case TagDefineText: case TagDefineText2: case TagDefineEditText:
                    return ReadRect(t.Body, 2);
                case TagDefineSprite:
                {
                    var s_Union = default(Bounds);
                    foreach (var p in FrameOnePlacements(t.Children))
                    {
                        var b = CharacterBounds(p.CharacterId, p_Import, p_Depth + 1);
                        if (!b.Empty) s_Union = Bounds.Union(s_Union, b.Transform(p.Matrix));
                    }
                    return s_Union;
                }
                default:
                    return default;
            }
        }

        public const int TagPlaceObject3 = 70, TagRemoveObject2 = 28;

        /// <summary>What frame 1 of a timeline shows: one entry per depth, PlaceObject2 and PlaceObject3 alike.</summary>
        public class FramePlacement
        {
            public ushort Depth, CharacterId, ClipDepth;
            public SwfMatrix? Matrix;
            public SwfCxform? ColorTransform;
            public string? Name;
            public List<SwfFilter> Filters = new();
            public bool HasClipDepth;
            /// <summary>The construct-time variables of the placement's ClipActions (m_rowType…), null when it has none or they sit behind filter data.</summary>
            public List<(string Name, object? Value)>? ClipVars;
        }

        /// <summary>
        /// The display list at the end of frame 1 of a tag stream (the movie's top level or a sprite's children):
        /// placements in depth order, moves applied, removals honoured.
        /// </summary>
        public static List<FramePlacement> FrameOnePlacements(IEnumerable<GfxTag> p_Tags)
        {
            var s_ByDepth = new SortedDictionary<ushort, FramePlacement>();
            foreach (var c in p_Tags)
            {
                if (c.Code == TagShowFrame) break;
                if (c.Code == TagRemoveObject2 && c.Body.Length >= 2) { s_ByDepth.Remove(BitConverter.ToUInt16(c.Body, 0)); continue; }
                if (c.Code == TagPlaceObject2)
                {
                    var p = PlaceObject2.Decode(c.Body);
                    if (!s_ByDepth.TryGetValue(p.Depth, out var e) || !p.Move) { e = new FramePlacement { Depth = p.Depth }; s_ByDepth[p.Depth] = e; }
                    if (p.HasCharacter) e.CharacterId = p.CharacterId;
                    if (p.HasMatrix) e.Matrix = p.Matrix;
                    if (p.HasName) e.Name = p.Name;
                    if (p.HasColorTransform && p.ColorTransformRaw.Length > 0) e.ColorTransform = SwfCxform.Read(new BitCursor(p.ColorTransformRaw, 0), true);
                    if (p.HasClipDepth && p.Tail.Length >= 2) { e.HasClipDepth = true; e.ClipDepth = BitConverter.ToUInt16(p.Tail, 0); }
                    if (p.HasClipActions) { try { e.ClipVars = p.ClipVars; } catch { /* unreadable actions: no variables */ } }
                }
                else if (c.Code == TagPlaceObject3)
                {
                    var p = PlaceObject3.Decode(c.Body);
                    if (!s_ByDepth.TryGetValue(p.Depth, out var e) || !p.Move) { e = new FramePlacement { Depth = p.Depth }; s_ByDepth[p.Depth] = e; }
                    if (p.HasCharacter) e.CharacterId = p.CharacterId;
                    if (p.HasMatrix) e.Matrix = p.Matrix;
                    if (p.HasName) e.Name = p.Name;
                    if (p.HasColorTransform) e.ColorTransform = p.ColorTransform;
                    if (p.HasClipDepth) { e.HasClipDepth = true; e.ClipDepth = p.ClipDepth; }
                    if (p.HasFilterList) e.Filters = p.Filters;
                    // the variables live in the ClipActions after the filter data; readable when there is no such data in front of them
                    if (p.HasClipActions) { try { e.ClipVars = PlaceObject2.Decode(c.Body, true).ClipVars; } catch { /* behind filters / blend: unreadable */ } }
                }
            }
            return s_ByDepth.Values.Where(e => e.CharacterId != 0).ToList();
        }

        /// <summary>Character id exported under a symbol name, if any.</summary>
        public ushort? ExportedCharacter(string p_Symbol)
        {
            foreach (var e in Exports())
                foreach (var (id, name) in e.Characters)
                    if (name == p_Symbol) return id;
            return null;
        }

        /// <summary>Human-readable stage listing (imports, exports, placements with their matrices).</summary>
        public string Describe()
        {
            var s_Sb = new StringBuilder();
            s_Sb.AppendLine($"{Magic} v{Version} tags={Tags.Count}");
            foreach (var s_Import in Imports())
                s_Sb.AppendLine($"  import {s_Import.Url}: " + string.Join(", ", s_Import.Characters.Select(c => $"{c.Id}:{c.Name}")));
            foreach (var s_Export in Exports())
                s_Sb.AppendLine("  export " + string.Join(", ", s_Export.Characters.Select(c => $"{c.Id}:{c.Name}")));
            foreach (var (s_Sprite, _, s_Place) in Placements())
            {
                var s_Where = s_Sprite == null ? "top" : $"sprite{s_Sprite.SpriteId}";
                var s_Inv = System.Globalization.CultureInfo.InvariantCulture;
                var s_Matrix = s_Place.Matrix == null ? "(no matrix)" :
                    string.Format(s_Inv, "scale={0:0.####}/{1:0.####} px=({2:0.#},{3:0.#})", s_Place.Matrix.ScaleXf, s_Place.Matrix.ScaleYf,
                        s_Place.Matrix.TranslateX / 20.0, s_Place.Matrix.TranslateY / 20.0);
                s_Sb.AppendLine($"  {s_Where,-10} depth={s_Place.Depth,-3} char={(s_Place.HasCharacter ? s_Place.CharacterId.ToString() : "-"),-4} name={s_Place.Name ?? "-",-24} {s_Matrix}");
            }
            return s_Sb.ToString();
        }
    }

    /// <summary>One tag. Untouched tags keep their original bytes; DefineSprite tags carry their children.</summary>
    public class GfxTag
    {
        public int Code { get; private set; }
        public byte[] Body { get; private set; } = Array.Empty<byte>();
        public List<GfxTag> Children { get; } = new();
        public ushort SpriteId { get; private set; }
        public ushort SpriteFrameCount { get; private set; }

        byte[]? m_Raw; // original header + body, null once modified

        public bool Modified => m_Raw == null;

        public static IEnumerable<GfxTag> ParseStream(byte[] p_Data, int p_Start, int p_End)
        {
            var s_Pos = p_Start;
            while (s_Pos + 2 <= p_End)
            {
                var s_TagStart = s_Pos;
                var s_Header = BitConverter.ToUInt16(p_Data, s_Pos);
                var s_Code = s_Header >> 6;
                var s_Length = s_Header & 0x3F;
                s_Pos += 2;
                if (s_Length == 0x3F)
                {
                    s_Length = BitConverter.ToInt32(p_Data, s_Pos);
                    s_Pos += 4;
                }
                var s_Tag = new GfxTag { Code = s_Code, Body = p_Data.Skip(s_Pos).Take(s_Length).ToArray() };
                s_Tag.m_Raw = p_Data.Skip(s_TagStart).Take(s_Pos + s_Length - s_TagStart).ToArray();
                s_Pos += s_Length;
                if (s_Code == GfxMovie.TagDefineSprite && s_Tag.Body.Length >= 4)
                {
                    s_Tag.SpriteId = BitConverter.ToUInt16(s_Tag.Body, 0);
                    s_Tag.SpriteFrameCount = BitConverter.ToUInt16(s_Tag.Body, 2);
                    s_Tag.Children.AddRange(ParseStream(s_Tag.Body, 4, s_Tag.Body.Length));
                }
                yield return s_Tag;
                if (s_Code == GfxMovie.TagEnd)
                    yield break;
            }
        }

        public static GfxTag Create(int p_Code, byte[] p_Body) => new() { Code = p_Code, Body = p_Body };

        public void SetBody(byte[] p_Body)
        {
            Body = p_Body;
            m_Raw = null;
        }

        public void MarkModified() => m_Raw = null;

        public byte[] Emit()
        {
            if (m_Raw != null)
                return m_Raw;
            var s_Body = Body;
            if (Code == GfxMovie.TagDefineSprite)
            {
                var s_Inner = new MemoryStream();
                s_Inner.Write(BitConverter.GetBytes(SpriteId), 0, 2);
                s_Inner.Write(BitConverter.GetBytes(SpriteFrameCount), 0, 2);
                foreach (var s_Child in Children)
                {
                    var s_Bytes = s_Child.Emit();
                    s_Inner.Write(s_Bytes, 0, s_Bytes.Length);
                }
                s_Body = s_Inner.ToArray();
            }
            // long form for anything re-emitted: unambiguous and legal for every length
            var s_Out = new byte[6 + s_Body.Length];
            BitConverter.GetBytes((ushort)((Code << 6) | 0x3F)).CopyTo(s_Out, 0);
            BitConverter.GetBytes(s_Body.Length).CopyTo(s_Out, 2);
            s_Body.CopyTo(s_Out, 6);
            return s_Out;
        }
    }

    // ---------------------------------------------------------------------------------------------- bit helpers

    class BitCursor
    {
        readonly byte[] m_Data;
        public int BytePos;
        int m_Bit;

        public BitCursor(byte[] p_Data, int p_Pos) { m_Data = p_Data; BytePos = p_Pos; }

        public int Read(int p_Bits, bool p_Signed = false)
        {
            long s_Value = 0;
            for (var i = 0; i < p_Bits; ++i)
            {
                s_Value = (s_Value << 1) | (uint)((m_Data[BytePos] >> (7 - m_Bit)) & 1);
                if (++m_Bit == 8) { m_Bit = 0; ++BytePos; }
            }
            if (p_Signed && p_Bits > 0 && ((s_Value >> (p_Bits - 1)) & 1) == 1)
                s_Value -= 1L << p_Bits;
            return (int)s_Value;
        }

        public void Align()
        {
            if (m_Bit != 0) { m_Bit = 0; ++BytePos; }
        }
    }

    class BitBuilder
    {
        readonly List<bool> m_Bits = new();

        public void Write(long p_Value, int p_Bits)
        {
            for (var i = p_Bits - 1; i >= 0; --i)
                m_Bits.Add(((p_Value >> i) & 1) == 1);
        }

        public static int SignedBits(params int[] p_Values)
        {
            var s_N = 1;
            foreach (var s_V in p_Values)
                while (!(-(1L << (s_N - 1)) <= s_V && s_V < (1L << (s_N - 1))))
                    ++s_N;
            return s_N;
        }

        public byte[] ToBytes()
        {
            var s_Out = new byte[(m_Bits.Count + 7) / 8];
            for (var i = 0; i < m_Bits.Count; ++i)
                if (m_Bits[i])
                    s_Out[i / 8] |= (byte)(1 << (7 - i % 8));
            return s_Out;
        }
    }

    /// <summary>SWF MATRIX: fixed 16.16 scale/rotate, twips translate; bit-packed.</summary>
    public class SwfMatrix
    {
        public bool HasScale;
        public int ScaleX = 65536, ScaleY = 65536;
        public bool HasRotate;
        public int RotateSkew0, RotateSkew1;
        public int TranslateX, TranslateY;

        public double ScaleXf => HasScale ? ScaleX / 65536.0 : 1.0;
        public double ScaleYf => HasScale ? ScaleY / 65536.0 : 1.0;
        /// <summary>The rotation in radians (atan2 of the first column), 0 for a plain scale.</summary>
        public double Angle => System.Math.Atan2(HasRotate ? RotateSkew0 / 65536.0 : 0.0, ScaleXf);
        /// <summary>The scale along the placement's own x and y axes (the column lengths), whatever the rotation.</summary>
        public (double Sx, double Sy) Magnitudes
        {
            get
            {
                var b = HasRotate ? RotateSkew0 / 65536.0 : 0.0; var c = HasRotate ? RotateSkew1 / 65536.0 : 0.0;
                return (System.Math.Sqrt(ScaleXf * ScaleXf + b * b), System.Math.Sqrt(c * c + ScaleYf * ScaleYf));
            }
        }

        internal static SwfMatrix Read(BitCursor p_Cursor)
        {
            var s_M = new SwfMatrix();
            s_M.HasScale = p_Cursor.Read(1) == 1;
            if (s_M.HasScale)
            {
                var s_N = p_Cursor.Read(5);
                s_M.ScaleX = p_Cursor.Read(s_N, true);
                s_M.ScaleY = p_Cursor.Read(s_N, true);
            }
            s_M.HasRotate = p_Cursor.Read(1) == 1;
            if (s_M.HasRotate)
            {
                var s_N = p_Cursor.Read(5);
                s_M.RotateSkew0 = p_Cursor.Read(s_N, true);
                s_M.RotateSkew1 = p_Cursor.Read(s_N, true);
            }
            var s_T = p_Cursor.Read(5);
            s_M.TranslateX = p_Cursor.Read(s_T, true);
            s_M.TranslateY = p_Cursor.Read(s_T, true);
            p_Cursor.Align();
            return s_M;
        }

        internal void Write(BitBuilder p_Builder)
        {
            p_Builder.Write(HasScale ? 1 : 0, 1);
            if (HasScale)
            {
                var s_N = BitBuilder.SignedBits(ScaleX, ScaleY);
                p_Builder.Write(s_N, 5);
                p_Builder.Write(ScaleX & ((1L << s_N) - 1), s_N);
                p_Builder.Write(ScaleY & ((1L << s_N) - 1), s_N);
            }
            p_Builder.Write(HasRotate ? 1 : 0, 1);
            if (HasRotate)
            {
                var s_N = BitBuilder.SignedBits(RotateSkew0, RotateSkew1);
                p_Builder.Write(s_N, 5);
                p_Builder.Write(RotateSkew0 & ((1L << s_N) - 1), s_N);
                p_Builder.Write(RotateSkew1 & ((1L << s_N) - 1), s_N);
            }
            var s_T = TranslateX == 0 && TranslateY == 0 ? 0 : BitBuilder.SignedBits(TranslateX, TranslateY);
            p_Builder.Write(s_T, 5);
            if (s_T > 0)
            {
                p_Builder.Write(TranslateX & ((1L << s_T) - 1), s_T);
                p_Builder.Write(TranslateY & ((1L << s_T) - 1), s_T);
            }
        }
    }

    /// <summary>
    /// PlaceObject2 (26) — and PlaceObject3 (70), the same record with a second flags byte and an optional
    /// class name — with the fields the stage needs decoded (character, depth, matrix, name); the colour
    /// transform, ratio, clip depth, filters, blend mode and clip actions are carried as raw bytes so they
    /// come back exactly as they were. Screens place widgets with either tag (the com-rose screen uses
    /// PlaceObject3 for all of its buttons), so every stage query and edit accepts both.
    /// </summary>
    public class PlaceObject2
    {
        public bool HasClipActions, HasClipDepth, HasName, HasRatio, HasColorTransform, HasMatrix, HasCharacter, Move;
        public ushort Depth;
        public ushort CharacterId;
        public SwfMatrix? Matrix;
        public byte[] ColorTransformRaw = Array.Empty<byte>();
        public byte[] RatioRaw = Array.Empty<byte>();
        public string? Name;
        public byte[] Tail = Array.Empty<byte>(); // clip depth + (v3: filters, blend mode, cache) + clip actions, verbatim
        /// <summary>True for a PlaceObject3 tag: Flags2 and ClassNameRaw are then part of the record.</summary>
        public bool IsPlaceObject3;
        public byte Flags2;
        public byte[] ClassNameRaw = Array.Empty<byte>();   // NUL-terminated class name when Flags2 says so, verbatim

        public static PlaceObject2 Decode(byte[] p_Body) => Decode(p_Body, false);

        public static PlaceObject2 Decode(byte[] p_Body, bool p_PlaceObject3)
        {
            var s_P = new PlaceObject2();
            var s_Flags = p_Body[0];
            s_P.HasClipActions = (s_Flags & 0x80) != 0;
            s_P.HasClipDepth = (s_Flags & 0x40) != 0;
            s_P.HasName = (s_Flags & 0x20) != 0;
            s_P.HasRatio = (s_Flags & 0x10) != 0;
            s_P.HasColorTransform = (s_Flags & 0x08) != 0;
            s_P.HasMatrix = (s_Flags & 0x04) != 0;
            s_P.HasCharacter = (s_Flags & 0x02) != 0;
            s_P.Move = (s_Flags & 0x01) != 0;
            var s_Pos = 1;
            if (p_PlaceObject3) { s_P.IsPlaceObject3 = true; s_P.Flags2 = p_Body[1]; s_Pos = 2; }
            s_P.Depth = BitConverter.ToUInt16(p_Body, s_Pos); s_Pos += 2;
            if (p_PlaceObject3 && ((s_P.Flags2 & 0x08) != 0 || ((s_P.Flags2 & 0x10) != 0 && s_P.HasCharacter)))
            {
                // HasClassName, or HasImage with a character: a NUL-terminated class name sits before the character id
                var s_End = Array.IndexOf(p_Body, (byte)0, s_Pos);
                s_P.ClassNameRaw = p_Body.Skip(s_Pos).Take(s_End - s_Pos + 1).ToArray();
                s_Pos = s_End + 1;
            }
            if (s_P.HasCharacter) { s_P.CharacterId = BitConverter.ToUInt16(p_Body, s_Pos); s_Pos += 2; }
            if (s_P.HasMatrix)
            {
                var s_Cursor = new BitCursor(p_Body, s_Pos);
                s_P.Matrix = SwfMatrix.Read(s_Cursor);
                s_Pos = s_Cursor.BytePos;
            }
            if (s_P.HasColorTransform)
            {
                var s_Cursor = new BitCursor(p_Body, s_Pos);
                // CXFORMWITHALPHA: HasAdd(1) HasMult(1) Nbits(4) then 4 mult + 4 add terms of Nbits each
                var s_HasAdd = s_Cursor.Read(1);
                var s_HasMul = s_Cursor.Read(1);
                var s_N = s_Cursor.Read(4);
                for (var i = 0; i < (s_HasMul == 1 ? 4 : 0) + (s_HasAdd == 1 ? 4 : 0); ++i)
                    s_Cursor.Read(s_N, true);
                s_Cursor.Align();
                s_P.ColorTransformRaw = p_Body.Skip(s_Pos).Take(s_Cursor.BytePos - s_Pos).ToArray();
                s_Pos = s_Cursor.BytePos;
            }
            if (s_P.HasRatio) { s_P.RatioRaw = p_Body.Skip(s_Pos).Take(2).ToArray(); s_Pos += 2; }
            if (s_P.HasName)
            {
                var s_End = Array.IndexOf(p_Body, (byte)0, s_Pos);
                s_P.Name = Encoding.Latin1.GetString(p_Body, s_Pos, s_End - s_Pos);
                s_Pos = s_End + 1;
            }
            s_P.Tail = p_Body.Skip(s_Pos).ToArray();
            return s_P;
        }

        /// <summary>Whether the clip actions can be read: a PlaceObject3 with filters, blend mode or bitmap cache keeps them behind data this editor carries verbatim.</summary>
        public bool ClipActionsDecodable => HasClipActions && (!IsPlaceObject3 || (Flags2 & 0x07) == 0);

        /// <summary>The placement's ClipActions (the code its events run), or null when it has none or they cannot be read.</summary>
        public ClipActions? DecodeClipActions() => ClipActionsDecodable ? ClipActions.Decode(Tail, HasClipDepth ? 2 : 0) : null;

        /// <summary>Replaces the placement's ClipActions (the clip depth in front of them is kept).</summary>
        public void SetClipActions(ClipActions p_Actions)
        {
            if (IsPlaceObject3 && (Flags2 & 0x07) != 0) throw new Exception("clip actions behind filters / blend data cannot be rewritten");
            var s_Prefix = HasClipDepth ? Tail.Take(2).ToArray() : Array.Empty<byte>();
            Tail = s_Prefix.Concat(p_Actions.Encode()).ToArray();
            HasClipActions = true;
        }

        /// <summary>The variables the placement sets on itself at construction (FrostEd's per-instance widget parameters), or null.</summary>
        public List<(string Name, object? Value)>? ClipVars => DecodeClipActions()?.ConstructVars();

        public byte[] Encode()
        {
            var s_Out = new MemoryStream();
            var s_Flags = (HasClipActions ? 0x80 : 0) | (HasClipDepth ? 0x40 : 0) | (HasName ? 0x20 : 0) | (HasRatio ? 0x10 : 0)
                          | (HasColorTransform ? 0x08 : 0) | (HasMatrix || Matrix != null ? 0x04 : 0) | (HasCharacter ? 0x02 : 0) | (Move ? 0x01 : 0);
            s_Out.WriteByte((byte)s_Flags);
            if (IsPlaceObject3) s_Out.WriteByte(Flags2);
            s_Out.Write(BitConverter.GetBytes(Depth), 0, 2);
            if (IsPlaceObject3) s_Out.Write(ClassNameRaw, 0, ClassNameRaw.Length);
            if (HasCharacter) s_Out.Write(BitConverter.GetBytes(CharacterId), 0, 2);
            if (Matrix != null)
            {
                var s_Builder = new BitBuilder();
                Matrix.Write(s_Builder);
                var s_Bytes = s_Builder.ToBytes();
                s_Out.Write(s_Bytes, 0, s_Bytes.Length);
            }
            if (HasColorTransform) s_Out.Write(ColorTransformRaw, 0, ColorTransformRaw.Length);
            if (HasRatio) s_Out.Write(RatioRaw, 0, RatioRaw.Length);
            if (HasName)
            {
                var s_Name = Encoding.Latin1.GetBytes(Name ?? "");
                s_Out.Write(s_Name, 0, s_Name.Length);
                s_Out.WriteByte(0);
            }
            s_Out.Write(Tail, 0, Tail.Length);
            return s_Out.ToArray();
        }
    }

    /// <summary>
    /// The ClipActions block of a PlaceObject2/3 (SWF 6+): a reserved u16, the union of the event flags, then records of
    /// (event flags u32, action bytes length u32, actions) up to a zero flag word. FrostEd sets every widget instance's
    /// parameters here — a Construct-event record (flag 0x00040000) of ActionPush / ActionSetVariable pairs
    /// (m_rowType = "bold1", m_viewType = "KitView_3"…) that the widget's constructor reads before onClipLoad. Measured over
    /// every screen movie of the game: 44 widget types carry such a record on 100 % of their placements. Other records
    /// are carried verbatim; the Construct record is rebuilt from its variables when they are set.
    /// </summary>
    public class ClipActions
    {
        public const uint ConstructEvent = 0x00040000;
        public ushort Reserved;
        public uint AllEventFlags;

        public class Record
        {
            public uint Flags;
            public byte[] Actions = Array.Empty<byte>();   // the record's bytes after the size (key code included when KeyPress)
        }

        public List<Record> Records = new();

        public static ClipActions Decode(byte[] p_Bytes, int p_Offset)
        {
            var c = new ClipActions();
            var p = p_Offset;
            if (p + 6 > p_Bytes.Length) return c;
            c.Reserved = BitConverter.ToUInt16(p_Bytes, p); p += 2;
            c.AllEventFlags = BitConverter.ToUInt32(p_Bytes, p); p += 4;
            while (p + 4 <= p_Bytes.Length)
            {
                var s_Flags = BitConverter.ToUInt32(p_Bytes, p); p += 4;
                if (s_Flags == 0) break;
                if (p + 4 > p_Bytes.Length) break;
                var s_Size = (int)BitConverter.ToUInt32(p_Bytes, p); p += 4;
                c.Records.Add(new Record { Flags = s_Flags, Actions = p_Bytes.Skip(p).Take(s_Size).ToArray() });
                p += s_Size;
            }
            return c;
        }

        public byte[] Encode()
        {
            var s_Out = new MemoryStream();
            s_Out.Write(BitConverter.GetBytes(Reserved), 0, 2);
            uint s_All = 0;
            foreach (var r in Records) s_All |= r.Flags;
            s_Out.Write(BitConverter.GetBytes(s_All), 0, 4);
            foreach (var r in Records)
            {
                s_Out.Write(BitConverter.GetBytes(r.Flags), 0, 4);
                s_Out.Write(BitConverter.GetBytes((uint)r.Actions.Length), 0, 4);
                s_Out.Write(r.Actions, 0, r.Actions.Length);
            }
            s_Out.Write(BitConverter.GetBytes(0u), 0, 4);
            return s_Out.ToArray();
        }

        Record? Construct => Records.FirstOrDefault(r => (r.Flags & ConstructEvent) != 0);

        /// <summary>The (name, value) pairs the Construct record assigns, in order: strings, booleans, numbers (float pushes read as double, int pushes as int), null.</summary>
        public List<(string Name, object? Value)> ConstructVars()
        {
            var r = Construct;
            return r == null ? new List<(string, object?)>() : DecodeVars(r.Actions, out _);
        }

        /// <summary>Rebuilds the Construct record from variables (one Push of name+value and a SetVariable each, no constant pool), keeping every other record.</summary>
        public void SetConstructVars(IList<(string Name, object? Value)> p_Vars)
        {
            var r = Construct;
            if (r != null)
            {
                DecodeVars(r.Actions, out var s_OnlyVars);
                if (!s_OnlyVars) throw new Exception("the placement's construct record carries code beyond variable assignments; it is not rewritten");
                r.Actions = EncodeVars(p_Vars);
            }
            else Records.Add(new Record { Flags = ConstructEvent, Actions = EncodeVars(p_Vars) });
        }

        /// <summary>Push / SetVariable pairs of an action stream (ActionConstantPool honoured); p_OnlyVars says whether nothing else was in it.</summary>
        public static List<(string Name, object? Value)> DecodeVars(byte[] p_Actions, out bool p_OnlyVars)
        {
            var s_Out = new List<(string, object?)>();
            var s_Stack = new List<object?>();
            var s_Pool = new List<string>();
            p_OnlyVars = true;
            var p = 0;
            while (p < p_Actions.Length)
            {
                var s_Op = p_Actions[p++];
                if (s_Op == 0) break;
                byte[] s_Body = Array.Empty<byte>();
                if (s_Op >= 0x80)
                {
                    if (p + 2 > p_Actions.Length) break;
                    var s_Len = BitConverter.ToUInt16(p_Actions, p); p += 2;
                    s_Body = p_Actions.Skip(p).Take(s_Len).ToArray(); p += s_Len;
                }
                switch (s_Op)
                {
                    case 0x88:   // ActionConstantPool
                    {
                        var n = BitConverter.ToUInt16(s_Body, 0); var q = 2; s_Pool.Clear();
                        for (var i = 0; i < n && q < s_Body.Length; ++i) { var e = Array.IndexOf(s_Body, (byte)0, q); s_Pool.Add(Encoding.UTF8.GetString(s_Body, q, e - q)); q = e + 1; }
                        break;
                    }
                    case 0x96:   // ActionPush
                    {
                        var q = 0;
                        while (q < s_Body.Length)
                        {
                            var t = s_Body[q++];
                            switch (t)
                            {
                                case 0: { var e = Array.IndexOf(s_Body, (byte)0, q); s_Stack.Add(Encoding.UTF8.GetString(s_Body, q, e - q)); q = e + 1; break; }
                                case 1: s_Stack.Add((double)BitConverter.ToSingle(s_Body, q)); q += 4; break;
                                case 2: s_Stack.Add(null); break;
                                case 3: s_Stack.Add("undefined"); break;
                                case 4: s_Stack.Add("register" + s_Body[q]); q += 1; break;
                                case 5: s_Stack.Add(s_Body[q] != 0); q += 1; break;
                                case 6: s_Stack.Add(BitConverter.ToDouble(s_Body, q)); q += 8; break;
                                case 7: s_Stack.Add(BitConverter.ToInt32(s_Body, q)); q += 4; break;
                                case 8: { var i = s_Body[q]; s_Stack.Add(i < s_Pool.Count ? s_Pool[i] : "const" + i); q += 1; break; }
                                case 9: { var i = BitConverter.ToUInt16(s_Body, q); s_Stack.Add(i < s_Pool.Count ? s_Pool[i] : "const" + i); q += 2; break; }
                                default: q = s_Body.Length; p_OnlyVars = false; break;
                            }
                        }
                        break;
                    }
                    case 0x1D:   // ActionSetVariable
                        if (s_Stack.Count >= 2)
                        {
                            var v = s_Stack[^1]; var n = s_Stack[^2]; s_Stack.RemoveRange(s_Stack.Count - 2, 2);
                            s_Out.Add((n?.ToString() ?? "", v));
                        }
                        else p_OnlyVars = false;
                        break;
                    default:
                        p_OnlyVars = false;
                        break;
                }
            }
            return s_Out;
        }

        /// <summary>The bytes FrostEd writes for such assignments: per variable one ActionPush carrying the name and the value, then ActionSetVariable; ActionEnd last.</summary>
        public static byte[] EncodeVars(IList<(string Name, object? Value)> p_Vars)
        {
            var s_Out = new MemoryStream();
            foreach (var (s_Name, s_Value) in p_Vars)
            {
                var s_Push = new MemoryStream();
                s_Push.WriteByte(0); var s_N = Encoding.UTF8.GetBytes(s_Name); s_Push.Write(s_N, 0, s_N.Length); s_Push.WriteByte(0);
                switch (s_Value)
                {
                    case null: s_Push.WriteByte(2); break;
                    case bool b: s_Push.WriteByte(5); s_Push.WriteByte((byte)(b ? 1 : 0)); break;
                    case int i: s_Push.WriteByte(7); s_Push.Write(BitConverter.GetBytes(i), 0, 4); break;
                    case long l: s_Push.WriteByte(7); s_Push.Write(BitConverter.GetBytes((int)l), 0, 4); break;
                    case float f: s_Push.WriteByte(1); s_Push.Write(BitConverter.GetBytes(f), 0, 4); break;
                    case double d: s_Push.WriteByte(1); s_Push.Write(BitConverter.GetBytes((float)d), 0, 4); break;
                    default: { s_Push.WriteByte(0); var s_S = Encoding.UTF8.GetBytes(s_Value.ToString() ?? ""); s_Push.Write(s_S, 0, s_S.Length); s_Push.WriteByte(0); break; }
                }
                s_Out.WriteByte(0x96); s_Out.Write(BitConverter.GetBytes((ushort)s_Push.Length), 0, 2); s_Push.WriteTo(s_Out);
                s_Out.WriteByte(0x1D);
            }
            s_Out.WriteByte(0);
            return s_Out.ToArray();
        }

        /// <summary>One value as the ops and the catalogue spell it: true/false, numbers (a float push keeps its ".0"), strings verbatim, null.</summary>
        public static string FormatValue(object? p_Value) => p_Value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            double d => d == System.Math.Floor(d) && System.Math.Abs(d) < 1e15 ? d.ToString("0.0", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture),
            float f => FormatValue((double)f),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            _ => p_Value.ToString() ?? "",
        };

        /// <summary>The value a spelled variable means: true/false → bool, a number with a point → float push, a whole number → int push, else a string.</summary>
        public static object? ParseValue(string p_Text)
        {
            if (p_Text == "null") return null;
            if (string.Equals(p_Text, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(p_Text, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (p_Text.Contains('.') && double.TryParse(p_Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            if (int.TryParse(p_Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            return p_Text;
        }
    }

    public class ImportAssets2
    {
        public string Url = "";
        public byte Flag1 = 1, Flag2 = 0;
        public List<(ushort Id, string Name)> Characters = new();

        public static ImportAssets2 Decode(byte[] p_Body)
        {
            var s_I = new ImportAssets2();
            var s_End = Array.IndexOf(p_Body, (byte)0);
            s_I.Url = Encoding.Latin1.GetString(p_Body, 0, s_End);
            var s_Pos = s_End + 1;
            s_I.Flag1 = p_Body[s_Pos++];
            s_I.Flag2 = p_Body[s_Pos++];
            var s_Count = BitConverter.ToUInt16(p_Body, s_Pos); s_Pos += 2;
            for (var i = 0; i < s_Count; ++i)
            {
                var s_Id = BitConverter.ToUInt16(p_Body, s_Pos); s_Pos += 2;
                var s_NameEnd = Array.IndexOf(p_Body, (byte)0, s_Pos);
                s_I.Characters.Add((s_Id, Encoding.Latin1.GetString(p_Body, s_Pos, s_NameEnd - s_Pos)));
                s_Pos = s_NameEnd + 1;
            }
            return s_I;
        }

        public byte[] Encode()
        {
            var s_Out = new MemoryStream();
            var s_Url = Encoding.Latin1.GetBytes(Url);
            s_Out.Write(s_Url, 0, s_Url.Length); s_Out.WriteByte(0);
            s_Out.WriteByte(Flag1); s_Out.WriteByte(Flag2);
            s_Out.Write(BitConverter.GetBytes((ushort)Characters.Count), 0, 2);
            foreach (var (s_Id, s_Name) in Characters)
            {
                s_Out.Write(BitConverter.GetBytes(s_Id), 0, 2);
                var s_N = Encoding.Latin1.GetBytes(s_Name);
                s_Out.Write(s_N, 0, s_N.Length); s_Out.WriteByte(0);
            }
            return s_Out.ToArray();
        }
    }

    public class ExportAssets
    {
        public List<(ushort Id, string Name)> Characters = new();

        public static ExportAssets Decode(byte[] p_Body)
        {
            var s_E = new ExportAssets();
            var s_Count = BitConverter.ToUInt16(p_Body, 0);
            var s_Pos = 2;
            for (var i = 0; i < s_Count; ++i)
            {
                var s_Id = BitConverter.ToUInt16(p_Body, s_Pos); s_Pos += 2;
                var s_NameEnd = Array.IndexOf(p_Body, (byte)0, s_Pos);
                s_E.Characters.Add((s_Id, Encoding.Latin1.GetString(p_Body, s_Pos, s_NameEnd - s_Pos)));
                s_Pos = s_NameEnd + 1;
            }
            return s_E;
        }
    }
}
