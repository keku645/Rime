using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Content;
using RimeLib.Content.Frostbite;
using RimeLib.Content.Mounting;
using RimeLib.Texture;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Prints every mounted variant of a texture's pixel chunk: superbundle, bundle, backing, range, logical offset, size, h32 and chunk meta, next to the h32 the texture's name hashes to (as written and lower case). Tells a chunk that only lives sliced inside level superbundles from one with a full catalog copy. Args: <texture resource name>.")]
    public class DumpChunkVariantsCommand : Command
    {
        [CommandArgument(Description = "The texture resource name.")]
        public string? Name { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            var s_Ctx = (GameContext)p_Context;
            var s_Mounter = s_Ctx.GetMounter();

            if (!s_Mounter.TryGetResource(Name!, out var s_Res))
            {
                p_Writer.WriteLine($"CHUNKVARS: {Name} NOTFOUND");
                return false;
            }

            byte[] s_Header;
            using (var s_R = s_Res.FirstVariant.GetReader())
            {
                if (s_R.Length < 128)
                {
                    p_Writer.WriteLine($"CHUNKVARS: {Name} SHORTHEADER {s_R.Length}");
                    return false;
                }

                s_Header = s_R.ReadBytes(128);
            }

            var s_Converter = EngineInterfaceRegistry.Create<ITextureConverter>(s_Mounter.GetEngineType());
            var s_Probe = new BundleBuildingContext.ResourceMemoryReader(s_Header, ResourceType.DxTexture, Name!);
            var s_ChunkId = s_Converter.GetTextureChunkId(s_Probe);

            var s_AsWritten = (int)RimeLib.Frostbite.Utils.HashQuick(Name!);
            var s_Lower = (int)RimeLib.Frostbite.Utils.HashQuickLowerCase(Name!);
            p_Writer.WriteLine($"CHUNKVARS: {Name} chunk={s_ChunkId} mips={s_Header[26]} mipBase={s_Header[27]} flags=0x{System.BitConverter.ToUInt32(s_Header, 12):X} h32(as written)={s_AsWritten} h32(lower)={s_Lower}");

            foreach (var s_RV in s_Res.Variants)
                p_Writer.WriteLine($"  RESVAR sb={s_RV.GetContainedSuperbundle()} bundle={s_RV.GetContainedBundle() ?? "-"} cas={s_RV.Cas}");

            if (!s_Mounter.TryGetChunk(s_ChunkId, out var s_Chunk))
            {
                p_Writer.WriteLine("  (chunk not mounted)");
                return true;
            }

            var s_I = 0;

            foreach (var s_V in s_Chunk.Variants)
            {
                var s_Meta = "(none)";

                if (s_V.TryGetMeta(out RimeLib.Frostbite.Db.DbObject? s_Db) && s_Db != null)
                    s_Meta = s_Db.ToString().Replace("\r", " ").Replace("\n", " ");

                var s_H32 = s_V.GetAssetNameHash();
                var s_Match = s_H32 == s_AsWritten ? " =written" : s_H32 == s_Lower ? " =lower" : "";
                p_Writer.WriteLine($"  CHUNKVAR[{s_I++}] sb={s_V.GetContainedSuperbundle()} bundle={s_V.GetContainedBundle() ?? "-"} cas={s_V.Cas} " +
                    $"range={s_V.GetRangeStart()}..{s_V.GetRangeEnd()} logical={s_V.GetLogicalOffset()} kind={s_V.GetType().Name} h32={s_H32?.ToString() ?? "-"}{s_Match} meta={s_Meta}");
            }

            return true;
        }
    }
}
