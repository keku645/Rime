using System.IO;
using System.Linq;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Content;
using RimeLib.Content.Frostbite;
using RimeLib.IO;
using RimeLib.Mesh.Frostbite;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Prints each LOD of a mesh set: its flags, its data chunk and whether any variant of that chunk is catalog-backed. A package can only point at a LOD's data if the catalog holds it; otherwise the data lives only inside the levels that use the mesh. Args: <mesh set resource name>.")]
    public class DumpMeshLodChunksCommand : Command
    {
        [CommandArgument(Description = "The mesh set resource name.")]
        public string? Name { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            var s_Mounter = ((GameContext)p_Context).GetMounter();

            if (!s_Mounter.TryGetResource(Name!, out var s_Res))
            {
                p_Writer.WriteLine($"MESHLODS: {Name} NOTFOUND");
                return false;
            }

            byte[] s_Data;
            using (var s_Reader1 = s_Res.FirstVariant.GetReader())
                s_Data = s_Reader1.ReadBytes((int)s_Reader1.Length);

            using var s_Reader = new RimeReader(new MemoryStream(s_Data));
            var s_Layout = new MeshSetLayout(s_Reader);
            var s_AllResident = true;
            p_Writer.WriteLine($"MESHLODS: {Name} lods={s_Layout.LodCount}");

            for (var i = 0; i < s_Layout.LodCount; i++)
            {
                var s_Lod = s_Layout.Lods[i].Object;
                if (s_Lod == null)
                    continue;

                var s_Resident = (s_Lod.Flags & MeshLayout.MeshLayoutFlags.IsBaseLod) != 0;
                var s_Catalog = false;
                var s_Variants = 0;

                if (s_Mounter.TryGetChunk(s_Lod.DataChunkId, out var s_Chunk))
                {
                    s_Variants = s_Chunk.Variants.Count();
                    s_Catalog = s_Chunk.Variants.Any(p_V => p_V.Cas);
                }

                if (s_Resident && !s_Catalog)
                    s_AllResident = false;

                p_Writer.WriteLine($"  MESHLOD[{i}] resident={(s_Resident ? 1 : 0)} chunk={s_Lod.DataChunkId} variants={s_Variants} catalog={(s_Catalog ? 1 : 0)} flags={s_Lod.Flags}");
            }

            p_Writer.WriteLine($"MESHLODS-RESIDENT-IN-CATALOG: {Name} {(s_AllResident ? "yes" : "no")}");
            return true;
        }
    }
}
