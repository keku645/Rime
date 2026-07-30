using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Frostbite.Core;

namespace RimeLib.Cmd.Commands.SbBuilding
{
    [CommandDescription("Adds ONE TOC-LEVEL chunk to the superbundle being built as a pure { id, sha1 } CAS " +
        "reference (no payload shipped). Companion of clone_sb_chunks, for when you need specific chunks " +
        "rather than a whole donor toc. WHY IT MATTERS: a chunk declared inside a BUNDLE MANIFEST " +
        "(add_cas_chunk / resolve_missing_chunks) is NOT in the engine's chunk INDEX, and that index is what " +
        "fb::ResourceManager::beginChunkRead -> fb::turboLoaderRequestChunk probes by GUID — with a " +
        "manifest-only chunk the index table is null (crash) or the GUID probe loop never terminates. A " +
        "TOC-level chunk enters the index as soon as its superbundle is MOUNTED, so it is also fetchable " +
        "without loading any bundle. Use for DLC texture/mesh payloads referenced against a generated " +
        "catalog (mount_external_cat). The sha1 must exist in some mounted catalog. Args: <chunkGuid> <sha1hex>.")]
    internal class AddCasTocChunkCommand : Command
    {
        [CommandArgument(Description = "The GUID of the chunk (as it appears in the owning resource/manifest).")]
        public GUID? ChunkId { get; set; }

        [CommandArgument(Description = "The 40-hex-char SHA1 of the chunk payload.")]
        public string? Sha1Hex { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            var s_Ctx = p_Context as SbBuildingContext;
            if (s_Ctx == null)
            {
                p_Writer.WriteLine("add_cas_toc_chunk must run inside a superbundle-building context (build_sb).");
                return false;
            }

            if (ChunkId == null || string.IsNullOrWhiteSpace(Sha1Hex))
            {
                p_Writer.WriteLine("usage (sb ctx): add_cas_toc_chunk <chunkGuid> <sha1hex>");
                return false;
            }

            Sha1 s_Hash;
            try
            {
                s_Hash = new Sha1(Sha1Hex!);
            }
            catch
            {
                p_Writer.WriteLine($"add_cas_toc_chunk: '{Sha1Hex}' is not a valid 40-hex-char sha1.");
                return false;
            }

            s_Ctx.AddCasTocChunk(ChunkId, s_Hash);
            p_Writer.WriteLine($"add_cas_toc_chunk: toc chunk {ChunkId} -> {s_Hash} (cas ref, 0 bytes shipped).");
            return true;
        }
    }
}
