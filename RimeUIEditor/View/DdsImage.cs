using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RimeUIEditor.View
{
    /// <summary>
    /// DDS → BitmapSource for the UI atlases (the same block decoder the shader editor uses for its
    /// thumbnails: BC1/BC2/BC3/BC4/BC5 and 32-bit RGBA/BGRA, top mip only). WPF has no DDS codec and the
    /// game's converter emits nothing else.
    /// </summary>
    public static class DdsImage
    {
        const uint c_Magic = 0x20534444; // "DDS "

        enum Codec { Unsupported, Bc1, Bc2, Bc3, Bc4, Bc5, Bgra8, Rgba8 }

        public static BitmapSource? Load(byte[] p_File)
        {
            try
            {
                using var s_Reader = new BinaryReader(new MemoryStream(p_File));
                if (s_Reader.ReadUInt32() != c_Magic) return null;
                if (s_Reader.ReadUInt32() != 124) return null;
                s_Reader.ReadUInt32();
                var s_Height = (int)s_Reader.ReadUInt32();
                var s_Width = (int)s_Reader.ReadUInt32();
                s_Reader.ReadUInt32(); s_Reader.ReadUInt32(); s_Reader.ReadUInt32();
                for (var i = 0; i < 11; i++) s_Reader.ReadUInt32();
                s_Reader.ReadUInt32();
                var s_PixelFlags = s_Reader.ReadUInt32();
                var s_FourCc = s_Reader.ReadUInt32();
                var s_BitCount = s_Reader.ReadUInt32();
                var s_RedMask = s_Reader.ReadUInt32();
                s_Reader.ReadUInt32(); s_Reader.ReadUInt32(); s_Reader.ReadUInt32();
                for (var i = 0; i < 5; i++) s_Reader.ReadUInt32();
                var s_Codec = Identify(s_PixelFlags, s_FourCc, s_BitCount, s_RedMask);
                if (s_FourCc == FourCc("DX10"))
                {
                    var s_Dxgi = s_Reader.ReadUInt32();
                    for (var i = 0; i < 4; i++) s_Reader.ReadUInt32();
                    s_Codec = FromDxgi(s_Dxgi);
                }
                if (s_Codec == Codec.Unsupported || s_Width <= 0 || s_Height <= 0) return null;
                var s_Payload = s_Reader.ReadBytes(RequiredBytes(s_Codec, s_Width, s_Height));
                var s_Pixels = Decode(s_Codec, s_Payload, s_Width, s_Height);
                if (s_Pixels == null) return null;
                var s_Bitmap = BitmapSource.Create(s_Width, s_Height, 96, 96, PixelFormats.Bgra32, null, s_Pixels, s_Width * 4);
                s_Bitmap.Freeze();
                return s_Bitmap;
            }
            catch { return null; }
        }

        static uint FourCc(string p_Code) => (uint)(p_Code[0] | (p_Code[1] << 8) | (p_Code[2] << 16) | (p_Code[3] << 24));

        static Codec Identify(uint p_PixelFlags, uint p_FourCc, uint p_BitCount, uint p_RedMask)
        {
            if ((p_PixelFlags & 0x4) != 0)
            {
                if (p_FourCc == FourCc("DXT1")) return Codec.Bc1;
                if (p_FourCc == FourCc("DXT2") || p_FourCc == FourCc("DXT3")) return Codec.Bc2;
                if (p_FourCc == FourCc("DXT4") || p_FourCc == FourCc("DXT5")) return Codec.Bc3;
                if (p_FourCc == FourCc("ATI1") || p_FourCc == FourCc("BC4U")) return Codec.Bc4;
                if (p_FourCc == FourCc("ATI2") || p_FourCc == FourCc("BC5U")) return Codec.Bc5;
                return Codec.Unsupported;
            }
            if ((p_PixelFlags & 0x40) != 0 && p_BitCount == 32) return p_RedMask == 0x00ff0000 ? Codec.Bgra8 : Codec.Rgba8;
            return Codec.Unsupported;
        }

        static Codec FromDxgi(uint p_Format) => p_Format switch
        {
            70 or 71 or 72 => Codec.Bc1, 73 or 74 or 75 => Codec.Bc2, 76 or 77 or 78 => Codec.Bc3,
            79 or 80 or 81 => Codec.Bc4, 82 or 83 or 84 => Codec.Bc5, 87 or 88 => Codec.Bgra8, 28 or 29 => Codec.Rgba8,
            _ => Codec.Unsupported,
        };

        static int RequiredBytes(Codec p_Codec, int w, int h)
        {
            var bx = Math.Max(1, (w + 3) / 4); var by = Math.Max(1, (h + 3) / 4);
            return p_Codec switch { Codec.Bc1 or Codec.Bc4 => bx * by * 8, Codec.Bc2 or Codec.Bc3 or Codec.Bc5 => bx * by * 16, _ => w * h * 4 };
        }

        static byte[]? Decode(Codec p_Codec, byte[] d, int w, int h)
        {
            var s_Out = new byte[w * h * 4];
            switch (p_Codec)
            {
                case Codec.Bgra8:
                    if (d.Length < s_Out.Length) return null;
                    Array.Copy(d, s_Out, s_Out.Length); return s_Out;
                case Codec.Rgba8:
                    if (d.Length < s_Out.Length) return null;
                    for (var i = 0; i < w * h; i++) { s_Out[i * 4] = d[i * 4 + 2]; s_Out[i * 4 + 1] = d[i * 4 + 1]; s_Out[i * 4 + 2] = d[i * 4]; s_Out[i * 4 + 3] = d[i * 4 + 3]; }
                    return s_Out;
            }
            var s_Bx = Math.Max(1, (w + 3) / 4); var s_By = Math.Max(1, (h + 3) / 4);
            var s_BlockBytes = p_Codec is Codec.Bc1 or Codec.Bc4 ? 8 : 16;
            var c = new byte[64];
            var o = 0;
            for (var by = 0; by < s_By; by++)
            for (var bx = 0; bx < s_Bx; bx++)
            {
                if (o + s_BlockBytes > d.Length) return s_Out;
                Array.Clear(c);
                switch (p_Codec)
                {
                    case Codec.Bc1: ColorBlock(d, o, c, true); break;
                    case Codec.Bc2:
                        ColorBlock(d, o + 8, c, false);
                        for (var i = 0; i < 16; i++) { var n = d[o + i / 2]; c[i * 4 + 3] = (byte)((i % 2 == 0 ? n & 0x0F : n >> 4) * 17); }
                        break;
                    case Codec.Bc3: ColorBlock(d, o + 8, c, false); AlphaBlock(d, o, c, 3); break;
                    case Codec.Bc4:
                        AlphaBlock(d, o, c, 0);
                        for (var i = 0; i < 16; i++) { c[i * 4 + 1] = c[i * 4]; c[i * 4 + 2] = c[i * 4]; c[i * 4 + 3] = 255; }
                        break;
                    case Codec.Bc5:
                        AlphaBlock(d, o, c, 2); AlphaBlock(d, o + 8, c, 1);
                        for (var i = 0; i < 16; i++) { c[i * 4] = 128; c[i * 4 + 3] = 255; }
                        break;
                }
                for (var py = 0; py < 4; py++)
                for (var px = 0; px < 4; px++)
                {
                    var x = bx * 4 + px; var y = by * 4 + py;
                    if (x >= w || y >= h) continue;
                    var s = (py * 4 + px) * 4; var t = (y * w + x) * 4;
                    s_Out[t] = c[s]; s_Out[t + 1] = c[s + 1]; s_Out[t + 2] = c[s + 2]; s_Out[t + 3] = c[s + 3];
                }
                o += s_BlockBytes;
            }
            return s_Out;
        }

        static void ColorBlock(byte[] d, int o, byte[] p_Out, bool p_AllowAlpha)
        {
            var c0 = (ushort)(d[o] | (d[o + 1] << 8)); var c1 = (ushort)(d[o + 2] | (d[o + 3] << 8));
            var r = new byte[4]; var g = new byte[4]; var b = new byte[4]; var a = new byte[] { 255, 255, 255, 255 };
            Unpack565(c0, out r[0], out g[0], out b[0]); Unpack565(c1, out r[1], out g[1], out b[1]);
            if (c0 > c1 || !p_AllowAlpha)
            {
                r[2] = (byte)((2 * r[0] + r[1]) / 3); g[2] = (byte)((2 * g[0] + g[1]) / 3); b[2] = (byte)((2 * b[0] + b[1]) / 3);
                r[3] = (byte)((r[0] + 2 * r[1]) / 3); g[3] = (byte)((g[0] + 2 * g[1]) / 3); b[3] = (byte)((b[0] + 2 * b[1]) / 3);
            }
            else
            {
                r[2] = (byte)((r[0] + r[1]) / 2); g[2] = (byte)((g[0] + g[1]) / 2); b[2] = (byte)((b[0] + b[1]) / 2); a[3] = 0;
            }
            for (var i = 0; i < 16; i++)
            {
                var idx = (d[o + 4 + i / 4] >> (2 * (i % 4))) & 0x3;
                p_Out[i * 4] = b[idx]; p_Out[i * 4 + 1] = g[idx]; p_Out[i * 4 + 2] = r[idx];
                if (p_Out[i * 4 + 3] == 0) p_Out[i * 4 + 3] = a[idx];
            }
        }

        static void AlphaBlock(byte[] d, int o, byte[] p_Out, int p_Channel)
        {
            var a0 = d[o]; var a1 = d[o + 1];
            var v = new byte[8]; v[0] = a0; v[1] = a1;
            if (a0 > a1) for (var i = 0; i < 6; i++) v[2 + i] = (byte)(((6 - i) * a0 + (1 + i) * a1) / 7);
            else { for (var i = 0; i < 4; i++) v[2 + i] = (byte)(((4 - i) * a0 + (1 + i) * a1) / 5); v[6] = 0; v[7] = 255; }
            ulong bits = 0;
            for (var i = 0; i < 6; i++) bits |= (ulong)d[o + 2 + i] << (8 * i);
            for (var i = 0; i < 16; i++) p_Out[i * 4 + p_Channel] = v[(int)((bits >> (3 * i)) & 0x7)];
        }

        static void Unpack565(ushort v, out byte r, out byte g, out byte b)
        {
            r = (byte)((((v >> 11) & 0x1F) * 255 + 15) / 31); g = (byte)((((v >> 5) & 0x3F) * 255 + 31) / 63); b = (byte)(((v & 0x1F) * 255 + 15) / 31);
        }
    }
}
