using System;
using System.Collections.Generic;
using System.Text;

namespace RimeLib.Cmd.Scaleform
{
    // ------------------------------------------------------------------------------------------------------
    // Decoders for the tags a static render of frame 1 needs: shapes (with fill/line styles and edge records),
    // edit text, fonts (glyph shapes), PlaceObject3 and colour transforms. Pure parsing on the SWF spec as
    // GFx 3.x reads it (the tag table of the BF3 client was read to be sure which tags matter); no rendering here.
    // Coordinates stay in twips (1/20 px); glyphs in DefineFont3 units (20480 per em).
    // ------------------------------------------------------------------------------------------------------

    public struct SwfColor
    {
        public byte R, G, B, A;
        public SwfColor(byte r, byte g, byte b, byte a) { R = r; G = g; B = b; A = a; }
        public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";

        internal static SwfColor Read(BitCursor c, bool p_Alpha)
        {
            c.Align();
            var r = (byte)c.Read(8); var g = (byte)c.Read(8); var b = (byte)c.Read(8);
            var a = p_Alpha ? (byte)c.Read(8) : (byte)255;
            return new SwfColor(r, g, b, a);
        }
    }

    /// <summary>CXFORMWITHALPHA: colour = colour * Mul/256 + Add, per channel.</summary>
    public class SwfCxform
    {
        public int MulR = 256, MulG = 256, MulB = 256, MulA = 256;
        public int AddR, AddG, AddB, AddA;
        public bool IsIdentity => MulR == 256 && MulG == 256 && MulB == 256 && MulA == 256 && AddR == 0 && AddG == 0 && AddB == 0 && AddA == 0;
        public double AlphaFactor => System.Math.Clamp(MulA / 256.0 + AddA / 255.0, 0, 1);

        internal static SwfCxform Read(BitCursor c, bool p_Alpha)
        {
            c.Align();
            var s = new SwfCxform();
            var s_HasAdd = c.Read(1) == 1; var s_HasMul = c.Read(1) == 1; var n = c.Read(4);
            if (s_HasMul)
            {
                s.MulR = c.Read(n, true); s.MulG = c.Read(n, true); s.MulB = c.Read(n, true);
                if (p_Alpha) s.MulA = c.Read(n, true);
            }
            if (s_HasAdd)
            {
                s.AddR = c.Read(n, true); s.AddG = c.Read(n, true); s.AddB = c.Read(n, true);
                if (p_Alpha) s.AddA = c.Read(n, true);
            }
            c.Align();
            return s;
        }
    }

    public class SwfGradient
    {
        public int Spread, Interpolation;
        public List<(byte Ratio, SwfColor Color)> Records = new();
        public double FocalPoint;
    }

    public class SwfFillStyle
    {
        public byte Type;                 // 0 solid, 0x10 linear, 0x12 radial, 0x13 focal radial, 0x40-0x43 bitmap
        public SwfColor Color;
        public SwfMatrix? Matrix;
        public SwfGradient? Gradient;
        public ushort BitmapId;
        public bool IsSolid => Type == 0;
        public bool IsGradient => Type is 0x10 or 0x12 or 0x13;
        public bool IsBitmap => Type >= 0x40 && Type <= 0x43;
        public bool BitmapClipped => Type is 0x41 or 0x43;
        public bool BitmapSmoothed => Type is 0x40 or 0x41;
    }

    public class SwfLineStyle
    {
        public int Width;                 // twips; 0 = hairline
        public SwfColor Color;
        public SwfFillStyle? Fill;        // LINESTYLE2 with a fill
        public bool NoClose;
        public int StartCap, EndCap, Join;
    }

    /// <summary>One edge of a shape, absolute twips. Style indices are 1-based into the shape's style lists (0 = none).</summary>
    public struct ShapeEdge
    {
        public int X0, Y0, X1, Y1, CX, CY;
        public bool IsCurve;
        public int Fill0, Fill1, Line;
    }

    public class SwfShape
    {
        public ushort Id;
        public GfxMovie.Bounds Bounds, EdgeBounds;
        public bool UsesNonZeroWinding;
        public List<SwfFillStyle> Fills = new();
        public List<SwfLineStyle> Lines = new();
        public List<ShapeEdge> Edges = new();
        /// <summary>Byte offsets in the tag body of every bitmap fill's character id (u16), for rewriting the ids in place.</summary>
        public List<int> BitmapIdOffsets = new();

        public static SwfShape Decode(int p_Code, byte[] p_Body)
        {
            var s_Ver = p_Code switch { GfxMovie.TagDefineShape => 1, GfxMovie.TagDefineShape2 => 2, GfxMovie.TagDefineShape3 => 3, GfxMovie.TagDefineShape4 => 4, _ => throw new Exception("not a shape tag") };
            var s = new SwfShape();
            var c = new BitCursor(p_Body, 0);
            s.Id = (ushort)(p_Body[0] | (p_Body[1] << 8)); c.BytePos = 2;
            s.Bounds = ReadRect(c);
            if (s_Ver == 4)
            {
                s.EdgeBounds = ReadRect(c);
                c.Align(); var s_Flags = c.Read(8);
                s.UsesNonZeroWinding = (s_Flags & 4) != 0;
            }
            else s.EdgeBounds = s.Bounds;
            ReadShapeWithStyle(c, s_Ver, s, true);
            return s;
        }

        internal static GfxMovie.Bounds ReadRect(BitCursor c)
        {
            c.Align();
            var n = c.Read(5);
            var r = new GfxMovie.Bounds { Left = c.Read(n, true), Right = c.Read(n, true), Top = c.Read(n, true), Bottom = c.Read(n, true) };
            c.Align();
            return r;
        }

        internal static SwfMatrix ReadMatrix(BitCursor c) { c.Align(); return SwfMatrix.Read(c); }

        static void ReadFillStyles(BitCursor c, int p_Ver, List<SwfFillStyle> p_Out, List<int>? p_BitmapOffsets = null)
        {
            c.Align();
            int n = c.Read(8);
            if (n == 0xFF && p_Ver >= 2) n = c.Read(8) | (c.Read(8) << 8);
            for (var i = 0; i < n; ++i) p_Out.Add(ReadFillStyle(c, p_Ver, p_BitmapOffsets));
        }

        static SwfFillStyle ReadFillStyle(BitCursor c, int p_Ver, List<int>? p_BitmapOffsets = null)
        {
            c.Align();
            var f = new SwfFillStyle { Type = (byte)c.Read(8) };
            if (f.Type == 0)
                f.Color = SwfColor.Read(c, p_Ver >= 3);
            else if (f.IsGradient)
            {
                f.Matrix = ReadMatrix(c);
                c.Align();
                var g = new SwfGradient { Spread = c.Read(2), Interpolation = c.Read(2) };
                var s_Count = c.Read(4);
                for (var i = 0; i < s_Count; ++i)
                {
                    var s_Ratio = (byte)c.Read(8);
                    g.Records.Add((s_Ratio, SwfColor.Read(c, p_Ver >= 3)));
                }
                if (f.Type == 0x13) { c.Align(); g.FocalPoint = (short)(c.Read(8) | (c.Read(8) << 8)) / 256.0; }
                f.Gradient = g;
            }
            else if (f.IsBitmap)
            {
                c.Align();
                p_BitmapOffsets?.Add(c.BytePos);
                f.BitmapId = (ushort)(c.Read(8) | (c.Read(8) << 8));
                f.Matrix = ReadMatrix(c);
            }
            else throw new Exception($"unknown fill style type 0x{f.Type:X2}");
            return f;
        }

        static void ReadLineStyles(BitCursor c, int p_Ver, List<SwfLineStyle> p_Out, List<int>? p_BitmapOffsets = null)
        {
            c.Align();
            int n = c.Read(8);
            if (n == 0xFF) n = c.Read(8) | (c.Read(8) << 8);
            for (var i = 0; i < n; ++i)
            {
                c.Align();
                var l = new SwfLineStyle { Width = c.Read(8) | (c.Read(8) << 8) };
                if (p_Ver == 4)
                {
                    l.StartCap = c.Read(2); l.Join = c.Read(2);
                    var s_HasFill = c.Read(1) == 1;
                    c.Read(1); c.Read(1); c.Read(1); c.Read(5);
                    l.NoClose = c.Read(1) == 1; l.EndCap = c.Read(2);
                    if (l.Join == 2) { c.Align(); c.Read(16); }
                    if (s_HasFill) { l.Fill = ReadFillStyle(c, 4, p_BitmapOffsets); l.Color = l.Fill.Color; }
                    else l.Color = SwfColor.Read(c, true);
                }
                else l.Color = SwfColor.Read(c, p_Ver >= 3);
                p_Out.Add(l);
            }
        }

        /// <summary>SHAPEWITHSTYLE (p_Styled) or a glyph SHAPE (no style arrays, fill 1 = ink).</summary>
        internal static void ReadShapeWithStyle(BitCursor c, int p_Ver, SwfShape s, bool p_Styled)
        {
            if (p_Styled)
            {
                ReadFillStyles(c, p_Ver, s.Fills, s.BitmapIdOffsets);
                ReadLineStyles(c, p_Ver, s.Lines, s.BitmapIdOffsets);
            }
            c.Align();
            var s_FillBits = c.Read(4); var s_LineBits = c.Read(4);
            int x = 0, y = 0, s_Fill0 = 0, s_Fill1 = 0, s_Line = 0, s_FillBase = 0, s_LineBase = 0;
            while (true)
            {
                var s_IsEdge = c.Read(1) == 1;
                if (!s_IsEdge)
                {
                    var s_Flags = c.Read(5);
                    if (s_Flags == 0) break;
                    if ((s_Flags & 1) != 0) { var n = c.Read(5); x = c.Read(n, true); y = c.Read(n, true); }
                    if ((s_Flags & 2) != 0) { var v = c.Read(s_FillBits); s_Fill0 = v == 0 ? 0 : v + s_FillBase; }
                    if ((s_Flags & 4) != 0) { var v = c.Read(s_FillBits); s_Fill1 = v == 0 ? 0 : v + s_FillBase; }
                    if ((s_Flags & 8) != 0) { var v = c.Read(s_LineBits); s_Line = v == 0 ? 0 : v + s_LineBase; }
                    if ((s_Flags & 16) != 0 && p_Styled)
                    {
                        s_FillBase = s.Fills.Count; s_LineBase = s.Lines.Count;
                        ReadFillStyles(c, p_Ver, s.Fills, s.BitmapIdOffsets);
                        ReadLineStyles(c, p_Ver, s.Lines, s.BitmapIdOffsets);
                        c.Align();
                        s_FillBits = c.Read(4); s_LineBits = c.Read(4);
                    }
                }
                else
                {
                    var s_Straight = c.Read(1) == 1;
                    var n = c.Read(4) + 2;
                    var e = new ShapeEdge { X0 = x, Y0 = y, Fill0 = s_Fill0, Fill1 = s_Fill1, Line = s_Line };
                    if (s_Straight)
                    {
                        var s_General = c.Read(1) == 1;
                        if (s_General) { x += c.Read(n, true); y += c.Read(n, true); }
                        else if (c.Read(1) == 1) y += c.Read(n, true);
                        else x += c.Read(n, true);
                    }
                    else
                    {
                        x += c.Read(n, true); y += c.Read(n, true);
                        e.CX = x; e.CY = y; e.IsCurve = true;
                        x += c.Read(n, true); y += c.Read(n, true);
                    }
                    e.X1 = x; e.Y1 = y;
                    s.Edges.Add(e);
                }
            }
            c.Align();
        }
    }

    /// <summary>DefineEditText (37): a text field; the game fills most of them at runtime.</summary>
    public class SwfEditText
    {
        public ushort Id;
        public GfxMovie.Bounds Bounds;
        public bool HasText, WordWrap, Multiline, Password, ReadOnly, HasTextColor, HasMaxLength, HasFont, HasFontClass, AutoSize, HasLayout, NoSelect, Border, WasStatic, Html, UseOutlines;
        public ushort FontId, FontHeight, MaxLength;
        public string FontClass = "";
        public SwfColor TextColor = new(0, 0, 0, 255);
        public int Align, LeftMargin, RightMargin, Indent, Leading;
        public string VariableName = "", InitialText = "";

        public static SwfEditText Decode(byte[] b)
        {
            var t = new SwfEditText();
            var c = new BitCursor(b, 0);
            t.Id = (ushort)(b[0] | (b[1] << 8)); c.BytePos = 2;
            t.Bounds = SwfShape.ReadRect(c);
            c.Align();
            t.HasText = c.Read(1) == 1; t.WordWrap = c.Read(1) == 1; t.Multiline = c.Read(1) == 1; t.Password = c.Read(1) == 1;
            t.ReadOnly = c.Read(1) == 1; t.HasTextColor = c.Read(1) == 1; t.HasMaxLength = c.Read(1) == 1; t.HasFont = c.Read(1) == 1;
            t.HasFontClass = c.Read(1) == 1; t.AutoSize = c.Read(1) == 1; t.HasLayout = c.Read(1) == 1; t.NoSelect = c.Read(1) == 1;
            t.Border = c.Read(1) == 1; t.WasStatic = c.Read(1) == 1; t.Html = c.Read(1) == 1; t.UseOutlines = c.Read(1) == 1;
            var p = c.BytePos;
            if (t.HasFont) { t.FontId = (ushort)(b[p] | (b[p + 1] << 8)); p += 2; }
            if (t.HasFontClass) { t.FontClass = ReadString(b, ref p); }
            if (t.HasFont) { t.FontHeight = (ushort)(b[p] | (b[p + 1] << 8)); p += 2; }
            if (t.HasTextColor) { t.TextColor = new SwfColor(b[p], b[p + 1], b[p + 2], b[p + 3]); p += 4; }
            if (t.HasMaxLength) { t.MaxLength = (ushort)(b[p] | (b[p + 1] << 8)); p += 2; }
            if (t.HasLayout)
            {
                t.Align = b[p++];
                t.LeftMargin = b[p] | (b[p + 1] << 8); p += 2;
                t.RightMargin = b[p] | (b[p + 1] << 8); p += 2;
                t.Indent = b[p] | (b[p + 1] << 8); p += 2;
                t.Leading = (short)(b[p] | (b[p + 1] << 8)); p += 2;
            }
            t.VariableName = ReadString(b, ref p);
            if (t.HasText) t.InitialText = ReadString(b, ref p);
            return t;
        }

        internal static string ReadString(byte[] b, ref int p)
        {
            var e = Array.IndexOf(b, (byte)0, p);
            if (e < 0) e = b.Length;
            var s = Encoding.UTF8.GetString(b, p, e - p);
            p = e + 1;
            return s;
        }
    }

    /// <summary>DefineFont2/3 (48/75): glyph outlines + code table + layout. DefineFont3 units: 20480 per em.</summary>
    public class SwfFont
    {
        public ushort Id;
        public string Name = "";
        public bool Bold, Italic, HasLayout;
        public int UnitsPerEm = 20480;
        public List<SwfShape> Glyphs = new();
        public Dictionary<int, int> CodeToGlyph = new();
        public int Ascent, Descent, Leading;
        public short[] Advances = Array.Empty<short>();
        public GfxMovie.Bounds[] GlyphBounds = Array.Empty<GfxMovie.Bounds>();

        public static SwfFont Decode(int p_Code, byte[] b)
        {
            if (p_Code != 48 && p_Code != 75) throw new Exception("not a DefineFont2/3 tag");
            var f = new SwfFont { Id = (ushort)(b[0] | (b[1] << 8)), UnitsPerEm = p_Code == 75 ? 20480 : 1024 };
            var s_Flags = b[2];
            f.HasLayout = (s_Flags & 0x80) != 0; var s_WideOffsets = (s_Flags & 0x08) != 0; var s_WideCodes = (s_Flags & 0x04) != 0;
            f.Italic = (s_Flags & 0x02) != 0; f.Bold = (s_Flags & 0x01) != 0;
            var p = 4; // language code skipped
            var s_NameLen = b[p++];
            f.Name = Encoding.UTF8.GetString(b, p, s_NameLen).TrimEnd('\0'); p += s_NameLen;
            var s_Count = b[p] | (b[p + 1] << 8); p += 2;
            var s_TableStart = p;
            var s_Offsets = new int[s_Count];
            for (var i = 0; i < s_Count; ++i)
            {
                s_Offsets[i] = s_WideOffsets ? BitConverter.ToInt32(b, p) : (b[p] | (b[p + 1] << 8));
                p += s_WideOffsets ? 4 : 2;
            }
            var s_CodeTableOffset = s_WideOffsets ? BitConverter.ToInt32(b, p) : (b[p] | (b[p + 1] << 8));
            p += s_WideOffsets ? 4 : 2;
            for (var i = 0; i < s_Count; ++i)
            {
                var c = new BitCursor(b, s_TableStart + s_Offsets[i]);
                var g = new SwfShape();
                try { SwfShape.ReadShapeWithStyle(c, 1, g, false); }
                catch { /* a broken glyph draws nothing */ }
                f.Glyphs.Add(g);
            }
            p = s_TableStart + s_CodeTableOffset;
            var s_WideCodesReal = s_WideCodes || p_Code == 75;
            for (var i = 0; i < s_Count && p < b.Length; ++i)
            {
                var s_Code = s_WideCodesReal ? (b[p] | (b[p + 1] << 8)) : b[p];
                p += s_WideCodesReal ? 2 : 1;
                f.CodeToGlyph.TryAdd(s_Code, i);
            }
            if (f.HasLayout && p + 6 <= b.Length)
            {
                f.Ascent = b[p] | (b[p + 1] << 8); p += 2;
                f.Descent = b[p] | (b[p + 1] << 8); p += 2;
                f.Leading = (short)(b[p] | (b[p + 1] << 8)); p += 2;
                f.Advances = new short[s_Count];
                for (var i = 0; i < s_Count && p + 1 < b.Length; ++i) { f.Advances[i] = (short)(b[p] | (b[p + 1] << 8)); p += 2; }
                f.GlyphBounds = new GfxMovie.Bounds[s_Count];
                var c = new BitCursor(b, p);
                for (var i = 0; i < s_Count && c.BytePos < b.Length; ++i)
                {
                    try { f.GlyphBounds[i] = SwfShape.ReadRect(c); } catch { break; }
                }
            }
            return f;
        }

        /// <summary>Advance of a code in font units, or a default when the glyph is missing.</summary>
        public int Advance(int p_Code)
        {
            if (CodeToGlyph.TryGetValue(p_Code, out var g) && g < Advances.Length) return Advances[g];
            return UnitsPerEm / 2;
        }
    }

    /// <summary>
    /// PlaceObject3 (70) decoded like PlaceObject2 plus the SWF8 extras (filters, blend mode, class name),
    /// enough to place a character; the tail (clip actions) is not needed for a static render.
    /// </summary>
    public class PlaceObject3
    {
        public bool HasClipActions, HasClipDepth, HasName, HasRatio, HasColorTransform, HasMatrix, HasCharacter, Move;
        public bool HasImage, HasClassName, HasCacheAsBitmap, HasBlendMode, HasFilterList;
        public ushort Depth, CharacterId, ClipDepth, Ratio;
        public SwfMatrix? Matrix;
        public SwfCxform? ColorTransform;
        public string? Name, ClassName;
        public byte BlendMode;
        public List<SwfFilter> Filters = new();

        public static PlaceObject3 Decode(byte[] b)
        {
            var o = new PlaceObject3();
            var f1 = b[0]; var f2 = b[1];
            o.HasClipActions = (f1 & 0x80) != 0; o.HasClipDepth = (f1 & 0x40) != 0; o.HasName = (f1 & 0x20) != 0; o.HasRatio = (f1 & 0x10) != 0;
            o.HasColorTransform = (f1 & 0x08) != 0; o.HasMatrix = (f1 & 0x04) != 0; o.HasCharacter = (f1 & 0x02) != 0; o.Move = (f1 & 0x01) != 0;
            o.HasImage = (f2 & 0x10) != 0; o.HasClassName = (f2 & 0x08) != 0; o.HasCacheAsBitmap = (f2 & 0x04) != 0; o.HasBlendMode = (f2 & 0x02) != 0; o.HasFilterList = (f2 & 0x01) != 0;
            var p = 2;
            o.Depth = (ushort)(b[p] | (b[p + 1] << 8)); p += 2;
            if (o.HasClassName || (o.HasImage && o.HasCharacter)) o.ClassName = SwfEditText.ReadString(b, ref p);
            if (o.HasCharacter) { o.CharacterId = (ushort)(b[p] | (b[p + 1] << 8)); p += 2; }
            var c = new BitCursor(b, p);
            if (o.HasMatrix) { o.Matrix = SwfMatrix.Read(c); }
            if (o.HasColorTransform) { o.ColorTransform = SwfCxform.Read(c, true); }
            p = c.BytePos;
            if (o.HasRatio) { o.Ratio = (ushort)(b[p] | (b[p + 1] << 8)); p += 2; }
            if (o.HasName) o.Name = SwfEditText.ReadString(b, ref p);
            if (o.HasClipDepth) { o.ClipDepth = (ushort)(b[p] | (b[p + 1] << 8)); p += 2; }
            if (o.HasFilterList && p < b.Length)
            {
                int n = b[p++];
                for (var i = 0; i < n && p < b.Length; ++i)
                {
                    var s_Filter = SwfFilter.Read(b, ref p);
                    if (s_Filter == null) break;
                    o.Filters.Add(s_Filter);
                }
            }
            if (o.HasBlendMode && p < b.Length) o.BlendMode = b[p++];
            return o;
        }
    }

    /// <summary>The filters a placement can carry; only the ones the BF3 UI uses (drop shadow, glow, blur) are decoded in full.</summary>
    public class SwfFilter
    {
        public byte Kind;                 // 0 drop shadow, 1 blur, 2 glow, 3 bevel, 4 gradient glow, 5 convolution, 6 colour matrix, 7 gradient bevel
        public SwfColor Color;
        public double BlurX, BlurY, Angle, Distance, Strength;
        public bool Inner, Knockout;
        public int Passes;

        internal static SwfFilter? Read(byte[] b, ref int p)
        {
            var f = new SwfFilter { Kind = b[p++] };
            var q = p;
            double Fixed32() { var v = BitConverter.ToInt32(b, q) / 65536.0; q += 4; return v; }
            double Fixed8() { var v = (short)(b[q] | (b[q + 1] << 8)) / 256.0; q += 2; return v; }
            switch (f.Kind)
            {
                case 0: // drop shadow
                    f.Color = new SwfColor(b[q], b[q + 1], b[q + 2], b[q + 3]); q += 4;
                    f.BlurX = Fixed32(); f.BlurY = Fixed32(); f.Angle = Fixed32(); f.Distance = Fixed32(); f.Strength = Fixed8();
                    { var s_Flags = b[q++]; f.Inner = (s_Flags & 0x80) != 0; f.Knockout = (s_Flags & 0x40) != 0; f.Passes = s_Flags & 0x1F; }
                    break;
                case 1: // blur
                    f.BlurX = Fixed32(); f.BlurY = Fixed32(); f.Passes = b[q++] >> 3;
                    break;
                case 2: // glow
                    f.Color = new SwfColor(b[q], b[q + 1], b[q + 2], b[q + 3]); q += 4;
                    f.BlurX = Fixed32(); f.BlurY = Fixed32(); f.Strength = Fixed8();
                    { var s_Flags = b[q++]; f.Inner = (s_Flags & 0x80) != 0; f.Knockout = (s_Flags & 0x40) != 0; f.Passes = s_Flags & 0x1F; }
                    break;
                case 3: q += 27; break;                       // bevel
                case 4: case 7: { int n = b[q++]; q += n * 5 + 19; break; } // gradient glow / bevel
                case 5: { int mx = b[q], my = b[q + 1]; q += 2 + 8 + mx * my * 4 + 4 + 1; break; } // convolution
                case 6: q += 80; break;                       // colour matrix
                default: return null;
            }
            p = q;
            return f;
        }
    }

    /// <summary>DefineScalingGrid (78): the 9-slice centre of a character.</summary>
    public class SwfScalingGrid
    {
        public ushort CharacterId;
        public GfxMovie.Bounds Centre;
        public static SwfScalingGrid Decode(byte[] b)
        {
            var c = new BitCursor(b, 2);
            return new SwfScalingGrid { CharacterId = (ushort)(b[0] | (b[1] << 8)), Centre = SwfShape.ReadRect(c) };
        }
    }
}
