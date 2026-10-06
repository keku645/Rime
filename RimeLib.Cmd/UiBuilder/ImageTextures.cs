using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// The document's pictures as textures of the game. The recipe is the one the camo framework's row thumbnails ship by and
    /// that loads in game: DXT5, one mip, no sRGB (the shape of every ui/art texture the game carries); a texture resource of the
    /// UI kind — header only, streaming + on-demand — with its TextureAsset partition in the mod's bundle, and the pixels (the DDS
    /// without its 128-byte header) as a chunk in a chunk store superbundle of the mod. The converter is texconv, fed a 32-bit TGA:
    /// the one picture format with alpha and no colour-space metadata for it to honour (a PNG with gAMA/sRGB comes out darkened,
    /// a BMP loses the alpha).
    /// </summary>
    public static class ImageTextures
    {
        /// <summary>A shipped UI texture the new ones copy their pool group from (the camo thumbnails' donor; loads in game).</summary>
        public const string GroupDonor = "UI/Art/Persistence/Specializations/Camo/PremiumCamo_ABU";

        /// <summary>texconv.exe chosen by the caller (the editor's settings); null = looked for beside the tools.</summary>
        public static string? TexconvOverride;

        /// <summary>One converted picture: what the recipe ships.</summary>
        public sealed record Prepared(ImageEntry Image, string TextureName, string Dds, string PixelsBin, string PartitionJson, string ChunkGuid, string PartitionGuid, string InstanceGuid, int Width, int Height, int Mips);

        /// <summary>
        /// texconv.exe: the override, then beside this assembly and the running program, then the Rime tools folder those live under
        /// (Rime-src/bin/Release), then RIME_TEXCONV. Null when none of them has it.
        /// </summary>
        public static string? FindTexconv()
        {
            var s_Candidates = new List<string?>();
            if (!string.IsNullOrWhiteSpace(TexconvOverride)) s_Candidates.Add(TexconvOverride);
            foreach (var s_Base in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(ImageTextures).Assembly.Location) })
            {
                if (string.IsNullOrEmpty(s_Base)) continue;
                s_Candidates.Add(Path.Combine(s_Base, "texconv.exe"));
                // a tool built under <Rime-src>/<Project>/bin/Release/net8.0-windows: the tools folder is <Rime-src>/bin/Release
                s_Candidates.Add(Path.Combine(s_Base, "..", "..", "..", "..", "bin", "Release", "texconv.exe"));
                s_Candidates.Add(Path.Combine(s_Base, "..", "..", "..", "bin", "Release", "texconv.exe"));
            }
            s_Candidates.Add(Environment.GetEnvironmentVariable("RIME_TEXCONV"));
            foreach (var s_Candidate in s_Candidates)
            {
                try { if (!string.IsNullOrWhiteSpace(s_Candidate) && File.Exists(s_Candidate)) return Path.GetFullPath(s_Candidate); } catch { }
            }
            return null;
        }

        /// <summary>The picture's file on disk: as written when absolute, else under the document's folder.</summary>
        public static string ImagePath(ImageEntry p_Image, string p_DocDir) => Path.IsPathRooted(p_Image.File) ? p_Image.File : Path.Combine(p_DocDir, p_Image.File);

        /// <summary>The stable guids a picture's texture is born with, from the mod and the picture's name (a rebuild gives the same).</summary>
        public static (string Partition, string Instance, string Chunk) GuidsFor(ScreenDocument p_Doc, ImageEntry p_Image) =>
            (ModEmitter.StableGuid(p_Doc.Name, "image", p_Image.Name, "partition"), ModEmitter.StableGuid(p_Doc.Name, "image", p_Image.Name, "instance"), ModEmitter.StableGuid(p_Doc.Name, "image", p_Image.Name, "chunk"));

        /// <summary>
        /// Converts every picture of the document into the work folder (img_&lt;name&gt;.tga → .dds, .bin, tex_&lt;name&gt;.json) and
        /// returns what the recipe ships. Throws on a missing file, a picture that cannot be read, a missing texconv or a
        /// conversion that did not come out DXT5: a mod without its pictures is not the mod.
        /// </summary>
        public static List<Prepared> Prepare(ScreenDocument p_Doc, string p_DocDir, string p_WorkDir, TextWriter p_Log)
        {
            var s_Out = new List<Prepared>();
            if (p_Doc.Images.Count == 0) return s_Out;
            var s_Texconv = FindTexconv() ?? throw new Exception("texconv.exe was not found (beside the editor, in Rime-src\\bin\\Release, or Settings… ▸ texconv): the document's pictures cannot be converted");
            var s_Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s_Image in p_Doc.Images)
            {
                if (!IsValidName(s_Image.Name)) throw new Exception($"picture name '{s_Image.Name}' is not letters, digits and _");
                if (!s_Names.Add(s_Image.Name)) throw new Exception($"two pictures are named '{s_Image.Name}'");
                var s_Path = ImagePath(s_Image, p_DocDir);
                if (!File.Exists(s_Path)) throw new Exception($"picture '{s_Image.Name}': its file {s_Path} does not exist");
                PngImage s_Png;
                try { s_Png = PngImage.Load(s_Path); }
                catch (Exception s_Ex) { throw new Exception($"picture '{s_Image.Name}' ({s_Path}): {s_Ex.Message}"); }
                // DXT5 blocks are 4x4: the picture is padded to a multiple of 4 with transparent pixels (never scaled)
                var s_W = (s_Png.Width + 3) / 4 * 4; var s_H = (s_Png.Height + 3) / 4 * 4;
                var s_Tga = Path.Combine(p_WorkDir, $"img_{s_Image.Name}.tga");
                WriteTga(s_Tga, s_Png, s_W, s_H);
                var s_Dds = Path.Combine(p_WorkDir, $"img_{s_Image.Name}.dds");
                var s_ConvDir = Path.Combine(p_WorkDir, "conv_" + s_Image.Name);
                Directory.CreateDirectory(s_ConvDir);
                var s_Error = RunTexconv(s_Texconv, $"-nologo -y -m 1 -f DXT5 -tgazeroalpha -o \"{s_ConvDir}\" \"{s_Tga}\"", Path.Combine(s_ConvDir, $"img_{s_Image.Name}.dds"), s_Dds);
                if (s_Error != null) throw new Exception($"picture '{s_Image.Name}': {s_Error}");
                var s_Header = ReadDdsHeader(s_Dds);
                if (s_Header.FourCc != "DXT5" || s_Header.Width != s_W || s_Header.Height != s_H || s_Header.Mips > 1)
                    throw new Exception($"picture '{s_Image.Name}' came out as {s_Header.FourCc} {s_Header.Width}x{s_Header.Height} with {s_Header.Mips} mip(s), expected DXT5 {s_W}x{s_H} with one");
                var s_Bytes = File.ReadAllBytes(s_Dds);
                if (s_Bytes.Length != 128 + s_W * s_H) throw new Exception($"picture '{s_Image.Name}': the DDS holds {s_Bytes.Length - 128} bytes of pixels, expected {s_W * s_H} (DXT5 = one byte a pixel)");
                var s_Bin = Path.Combine(p_WorkDir, $"img_{s_Image.Name}.bin");
                File.WriteAllBytes(s_Bin, s_Bytes.Skip(128).ToArray());
                var (s_Partition, s_Instance, s_Chunk) = GuidsFor(p_Doc, s_Image);
                var s_Name = p_Doc.ImageTextureName(s_Image);
                var s_Json = Path.Combine(p_WorkDir, $"tex_{s_Image.Name}.json");
                File.WriteAllText(s_Json, TextureStub(s_Name, s_Partition, s_Instance).ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
                p_Log.WriteLine($"picture {s_Image.Name}: {Path.GetFileName(s_Path)} {s_Png.Width}x{s_Png.Height} -> {s_Name} DXT5 {s_W}x{s_H}, 1 mip, {s_W * s_H} bytes of pixels (chunk {s_Chunk})");
                s_Out.Add(new Prepared(s_Image, s_Name, s_Dds, s_Bin, s_Json, s_Chunk, s_Partition, s_Instance, s_W, s_H, s_Header.Mips));
            }
            return s_Out;
        }

        public static bool IsValidName(string p_Name) => p_Name.Length > 0 && p_Name.All(c => char.IsLetterOrDigit(c) || c == '_');

        /// <summary>A name for a picture from its file name: letters, digits and _ only, "Image" when nothing is left.</summary>
        public static string NameFromFile(string p_Path)
        {
            var s_Stem = Path.GetFileNameWithoutExtension(p_Path);
            var s_Name = new string(s_Stem.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            return s_Name.Length > 0 ? s_Name : "Image";
        }

        /// <summary>The TextureAsset partition the texture resource is looked up by (the shape the camo thumbnails ship).</summary>
        static JObject TextureStub(string p_Name, string p_Partition, string p_Instance) => new()
        {
            ["PrimaryInstanceGuid"] = p_Instance,
            ["Instances"] = new JObject { [p_Instance] = new JObject { ["$type"] = "TextureAsset", ["Name"] = p_Name } },
            ["Name"] = p_Name,
            ["PartitionGuid"] = p_Partition,
        };

        /// <summary>
        /// A 32-bit TGA (blue, green, red, alpha; top-left origin, 8 alpha bits) of the picture on a transparent canvas of the
        /// padded size — pixels only, no colour-space metadata.
        /// </summary>
        public static void WriteTga(string p_Path, PngImage p_Png, int p_Width, int p_Height)
        {
            using var s_Writer = new BinaryWriter(File.Create(p_Path));
            s_Writer.Write((byte)0);              // id length
            s_Writer.Write((byte)0);              // no colour map
            s_Writer.Write((byte)2);              // uncompressed true colour
            s_Writer.Write(new byte[5]);          // colour map spec
            s_Writer.Write((ushort)0); s_Writer.Write((ushort)0);
            s_Writer.Write((ushort)p_Width); s_Writer.Write((ushort)p_Height);
            s_Writer.Write((byte)32);
            s_Writer.Write((byte)0x28);           // 8 alpha bits, origin top-left
            var s_Row = new byte[p_Width * 4];
            for (var y = 0; y < p_Height; ++y)
            {
                Array.Clear(s_Row);
                if (y < p_Png.Height) Array.Copy(p_Png.Bgra, y * p_Png.Width * 4, s_Row, 0, p_Png.Width * 4);
                s_Writer.Write(s_Row);
            }
        }

        static string? RunTexconv(string p_Texconv, string p_Arguments, string p_Produced, string p_Claim)
        {
            if (File.Exists(p_Produced)) File.Delete(p_Produced);
            var s_Process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p_Texconv)
            {
                Arguments = p_Arguments, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            var s_Output = s_Process == null ? "" : s_Process.StandardOutput.ReadToEnd() + s_Process.StandardError.ReadToEnd();
            s_Process?.WaitForExit();
            if (!File.Exists(p_Produced)) return $"texconv produced nothing: {s_Output.Trim()}";
            if (File.Exists(p_Claim)) File.Delete(p_Claim);
            File.Move(p_Produced, p_Claim);
            return null;
        }

        public static (string FourCc, int Width, int Height, int Mips) ReadDdsHeader(string p_Dds)
        {
            var s_Header = new byte[128];
            using (var s_Stream = File.OpenRead(p_Dds)) _ = s_Stream.Read(s_Header, 0, s_Header.Length);
            return (Encoding.ASCII.GetString(s_Header, 84, 4), BitConverter.ToInt32(s_Header, 16), BitConverter.ToInt32(s_Header, 12), BitConverter.ToInt32(s_Header, 28));
        }
    }
}
