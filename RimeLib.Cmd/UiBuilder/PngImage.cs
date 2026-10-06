using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// A PNG's pixels, decoded here so the build needs no imaging library: the common shapes (8-bit RGBA, RGB, grey, grey+alpha,
    /// palette with or without transparency, 16-bit taken at its high byte), non-interlaced. Interlaced files are refused with a
    /// message that says so. The colour-space chunks (gAMA, sRGB, iCCP) are read for nothing: the pixels come out as stored —
    /// a converter handed the PNG itself would honour them and darken the picture (measured on the camo thumbnails).
    /// </summary>
    public sealed class PngImage
    {
        public int Width { get; }
        public int Height { get; }
        /// <summary>Rows top to bottom, 4 bytes a pixel: blue, green, red, alpha (straight, not premultiplied).</summary>
        public byte[] Bgra { get; }

        PngImage(int p_Width, int p_Height, byte[] p_Bgra) { Width = p_Width; Height = p_Height; Bgra = p_Bgra; }

        public static PngImage Load(string p_Path) => Decode(File.ReadAllBytes(p_Path));

        static readonly byte[] s_Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static PngImage Decode(byte[] p_Bytes)
        {
            if (p_Bytes.Length < 8 + 25) throw new InvalidDataException("not a PNG (too short)");
            for (var i = 0; i < 8; ++i) if (p_Bytes[i] != s_Signature[i]) throw new InvalidDataException("not a PNG (signature)");
            var s_Pos = 8;
            int s_Width = 0, s_Height = 0, s_Depth = 0, s_ColorType = 0, s_Interlace = 0;
            byte[]? s_Palette = null;
            byte[]? s_Transparency = null;
            var s_Data = new MemoryStream();
            var s_Seen = false;
            while (s_Pos + 8 <= p_Bytes.Length)
            {
                var s_Length = ReadBE(p_Bytes, s_Pos);
                var s_Type = Encoding.ASCII.GetString(p_Bytes, s_Pos + 4, 4);
                s_Pos += 8;
                if (s_Length < 0 || s_Pos + s_Length + 4 > p_Bytes.Length) throw new InvalidDataException($"PNG chunk {s_Type} runs past the file");
                switch (s_Type)
                {
                    case "IHDR":
                        s_Width = ReadBE(p_Bytes, s_Pos); s_Height = ReadBE(p_Bytes, s_Pos + 4);
                        s_Depth = p_Bytes[s_Pos + 8]; s_ColorType = p_Bytes[s_Pos + 9]; s_Interlace = p_Bytes[s_Pos + 12];
                        s_Seen = true;
                        break;
                    case "PLTE": s_Palette = new byte[s_Length]; Array.Copy(p_Bytes, s_Pos, s_Palette, 0, s_Length); break;
                    case "tRNS": s_Transparency = new byte[s_Length]; Array.Copy(p_Bytes, s_Pos, s_Transparency, 0, s_Length); break;
                    case "IDAT": s_Data.Write(p_Bytes, s_Pos, s_Length); break;
                }
                s_Pos += s_Length + 4;   // the chunk's CRC is not checked: a damaged file fails in the inflate or the filter
                if (s_Type == "IEND") break;
            }
            if (!s_Seen) throw new InvalidDataException("PNG without IHDR");
            if (s_Width <= 0 || s_Height <= 0 || (long)s_Width * s_Height > 64L * 1024 * 1024) throw new InvalidDataException($"PNG size {s_Width}x{s_Height} is not usable");
            if (s_Interlace != 0) throw new InvalidDataException("interlaced PNG: save it without interlacing (Adam7 is not read)");
            if (s_Depth != 8 && s_Depth != 16 && !(s_ColorType == 3 && s_Depth is 1 or 2 or 4) && !(s_ColorType == 0 && s_Depth is 1 or 2 or 4))
                throw new InvalidDataException($"PNG bit depth {s_Depth} with colour type {s_ColorType} is not read");
            var s_Channels = s_ColorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {s_ColorType} is not read") };
            var s_BitsPerPixel = s_Channels * s_Depth;
            var s_Stride = (s_Width * s_BitsPerPixel + 7) / 8;
            var s_FilterStep = System.Math.Max(1, s_BitsPerPixel / 8);

            // inflate: zlib stream (2-byte header) of height × (filter byte + stride)
            var s_Raw = new byte[(s_Stride + 1) * s_Height];
            s_Data.Position = 0;
            using (var s_Inflate = new ZLibStream(s_Data, CompressionMode.Decompress))
            {
                var s_Read = 0;
                while (s_Read < s_Raw.Length)
                {
                    var n = s_Inflate.Read(s_Raw, s_Read, s_Raw.Length - s_Read);
                    if (n <= 0) break;
                    s_Read += n;
                }
                if (s_Read < s_Raw.Length) throw new InvalidDataException($"PNG image data is short ({s_Read} of {s_Raw.Length} bytes)");
            }

            // unfilter, row by row
            var s_Rows = new byte[s_Stride * s_Height];
            var s_Prev = new byte[s_Stride];
            for (var y = 0; y < s_Height; ++y)
            {
                var s_Filter = s_Raw[y * (s_Stride + 1)];
                var s_In = y * (s_Stride + 1) + 1;
                var s_Out = y * s_Stride;
                for (var x = 0; x < s_Stride; ++x)
                {
                    int a = x >= s_FilterStep ? s_Rows[s_Out + x - s_FilterStep] : 0;
                    int b = s_Prev[x];
                    int c = x >= s_FilterStep ? s_Prev[x - s_FilterStep] : 0;
                    int v = s_Raw[s_In + x];
                    v += s_Filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => Paeth(a, b, c),
                        _ => throw new InvalidDataException($"PNG filter {s_Filter} on row {y}"),
                    };
                    s_Rows[s_Out + x] = (byte)v;
                }
                Array.Copy(s_Rows, s_Out, s_Prev, 0, s_Stride);
            }

            // to BGRA
            var s_Bgra = new byte[s_Width * s_Height * 4];
            for (var y = 0; y < s_Height; ++y)
            {
                var s_Row = y * s_Stride;
                for (var x = 0; x < s_Width; ++x)
                {
                    byte r, g, b, a = 255;
                    switch (s_ColorType)
                    {
                        case 6: { var i = s_Row + x * 4 * (s_Depth / 8); r = s_Rows[i]; g = s_Rows[i + s_Depth / 8]; b = s_Rows[i + 2 * (s_Depth / 8)]; a = s_Rows[i + 3 * (s_Depth / 8)]; break; }
                        case 2:
                        {
                            var i = s_Row + x * 3 * (s_Depth / 8); r = s_Rows[i]; g = s_Rows[i + s_Depth / 8]; b = s_Rows[i + 2 * (s_Depth / 8)];
                            if (s_Transparency != null && s_Transparency.Length >= 6 && s_Depth == 8 && r == s_Transparency[1] && g == s_Transparency[3] && b == s_Transparency[5]) a = 0;
                            break;
                        }
                        case 4: { var i = s_Row + x * 2 * (s_Depth / 8); r = g = b = s_Rows[i]; a = s_Rows[i + s_Depth / 8]; break; }
                        case 0:
                        {
                            var v = Sample(s_Rows, s_Row, x, s_Depth);
                            var s_Grey = s_Depth switch { 16 => v, 8 => v, _ => v * 255 / ((1 << s_Depth) - 1) };
                            r = g = b = (byte)s_Grey;
                            if (s_Transparency != null && s_Transparency.Length >= 2 && v == ((s_Transparency[0] << 8) | s_Transparency[1]) % (1 << System.Math.Min(s_Depth, 8))) a = 0;
                            break;
                        }
                        default:
                        {
                            var s_Index = Sample(s_Rows, s_Row, x, s_Depth);
                            if (s_Palette == null || s_Index * 3 + 2 >= s_Palette.Length) throw new InvalidDataException("PNG palette index out of range");
                            r = s_Palette[s_Index * 3]; g = s_Palette[s_Index * 3 + 1]; b = s_Palette[s_Index * 3 + 2];
                            if (s_Transparency != null && s_Index < s_Transparency.Length) a = s_Transparency[s_Index];
                            break;
                        }
                    }
                    var o = (y * s_Width + x) * 4;
                    s_Bgra[o] = b; s_Bgra[o + 1] = g; s_Bgra[o + 2] = r; s_Bgra[o + 3] = a;
                }
            }
            return new PngImage(s_Width, s_Height, s_Bgra);
        }

        /// <summary>One sample of a packed row (1/2/4/8/16 bits; 16-bit gives its high byte).</summary>
        static int Sample(byte[] p_Rows, int p_Row, int p_X, int p_Depth)
        {
            switch (p_Depth)
            {
                case 8: return p_Rows[p_Row + p_X];
                case 16: return p_Rows[p_Row + p_X * 2];
                default:
                {
                    var s_Bit = p_X * p_Depth;
                    var s_Byte = p_Rows[p_Row + s_Bit / 8];
                    var s_Shift = 8 - p_Depth - (s_Bit % 8);
                    return (s_Byte >> s_Shift) & ((1 << p_Depth) - 1);
                }
            }
        }

        static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            var pa = System.Math.Abs(p - a); var pb = System.Math.Abs(p - b); var pc = System.Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        static int ReadBE(byte[] p_Bytes, int p_At) => (p_Bytes[p_At] << 24) | (p_Bytes[p_At + 1] << 16) | (p_Bytes[p_At + 2] << 8) | p_Bytes[p_At + 3];
    }
}
