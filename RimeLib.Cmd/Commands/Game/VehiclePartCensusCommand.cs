using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Commands.BundleBuilding;
using RimeLib.Cmd.Contexts;
using RimeLib.Content.Frostbite;
using RimeLib.IO;
using RimeLib.Mesh.Frostbite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RimeLib.Cmd.Commands.Game
{
    /// <summary>
    /// What a vehicle's COMPOSITE body is made of, part by part: each part's rest transform and how many triangles of each material
    /// ride it (a vertex names its part in BoneIndices[0]). It is what the camo pieces are planned from: the parts a camo paints, and
    /// the component each one moves with (a part's rest transform is that component's transform).
    /// </summary>
    [CommandDescription("Lists a vehicle's COMPOSITE body part by part (LOD0): the part's rest transform and its triangles per " +
                        "material. Prints 'PARTCENSUS:' once, 'PARTSUBSET:' per section and 'PART:' per part.")]
    public class VehiclePartCensusCommand : Command
    {
        [CommandArgument(Description = "The body mesh resource, e.g. vehicles/m1a2/m1abrams_mesh")]
        public string? Mesh { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Mesh))
            {
                p_Writer.WriteLine("Usage: vehicle_part_census <body-mesh>");
                return false;
            }

            var s_Mounter = ((GameContext) p_Context).GetMounter();

            if (!s_Mounter.TryGetResource(Mesh!, out var s_Resource) || s_Resource.FirstVariant == null ||
                s_Resource.FirstVariant.GetResourceType() != ResourceType.MeshSet)
            {
                p_Writer.WriteLine($"'{Mesh}' is not a mounted MeshSet resource.");
                return false;
            }

            byte[] s_Bytes;
            using (var s_Reader = s_Resource.FirstVariant.GetReader())
                s_Bytes = s_Reader.ReadBytes((int) s_Reader.Length);

            var s_Layout = new MeshSetLayout(new RimeReader(new MemoryStream(s_Bytes)));
            var s_Lod = s_Layout.Lods[0].Object;
            if (s_Lod == null || s_Layout.MeshType != fb.MeshType.MeshType_Composite || s_Lod.PartCount == 0)
            {
                p_Writer.WriteLine($"'{Mesh}' is not a composite mesh with parts (type {s_Layout.MeshType}).");
                return false;
            }

            var s_Transforms = VehiclePartCloneCommand.ReadPartTransforms(s_Bytes, s_Lod);
            if (s_Transforms == null)
            {
                p_Writer.WriteLine("The body's part transforms could not be read.");
                return false;
            }

            if (!s_Mounter.TryGetChunk(s_Lod.DataChunkId, out var s_Chunk))
            {
                p_Writer.WriteLine($"The body's LOD0 chunk {s_Lod.DataChunkId} is not mounted.");
                return false;
            }

            byte[] s_Geometry;
            using (var s_Reader = s_Chunk.FirstVariant.GetReader())
                s_Geometry = s_Reader.ReadBytes((int) s_Reader.Length);

            var s_Subsets = s_Lod.Subsets.Get;
            var s_PerPart = new SortedDictionary<int, SortedDictionary<uint, int>>();
            var s_IndexBase = (int) s_Lod.VertexDataSize;
            var s_Sections = 0;

            p_Writer.WriteLine($"PARTCENSUS: mesh={Mesh} parts={s_Lod.PartCount} subsets={s_Subsets.Length}");

            // the drawn categories (opaque, transparent, transparent decal); the depth-only one repeats the opaque triangles
            for (var s_Category = 0; s_Category < 3; s_Category++)
            {
                foreach (var s_SubsetIndex in s_Lod.CategorySubsetIndices[s_Category].Get)
                {
                    var s_Subset = s_Subsets[s_SubsetIndex];
                    var s_Bones = s_Subset.GeometryDeclarationDesc.GetByUsage(fb.VertexElementUsage.VertexElementUsage_BoneIndices);
                    // the declaration a piece cut from this section is drawn with: its shader needs a solution for it
                    var s_Rigid = VehiclePartCloneCommand.RigidDeclarationOf(s_Subset.GeometryDeclarationDesc, out _);
                    p_Writer.WriteLine($"PARTSUBSET: index={s_SubsetIndex} category={s_Category} material={s_Subset.MaterialIndex} " +
                                       $"name={s_Subset.MaterialName.Object} triangles={s_Subset.PrimitiveCount} " +
                                       $"parts={(s_Bones != null ? "yes" : "no")} decl=0x{s_Subset.GeometryDeclarationDesc.Hash:X8} " +
                                       $"rigid={(s_Rigid != null ? $"0x{s_Rigid.Hash:X8}" : "-")}");
                    s_Sections++;

                    if (s_Bones == null)
                        continue;

                    var s_Stride = s_Subset.VertexStride;
                    var s_VertexBase = (int) s_Subset.VertexOffset;

                    for (var t = 0; t < s_Subset.PrimitiveCount; t++)
                    {
                        // the triangle's part is its first vertex's (a triangle never straddles two parts in a composite body)
                        var s_V = BitConverter.ToUInt16(s_Geometry, s_IndexBase + ((int) s_Subset.StartIndex + t * 3) * 2);
                        var s_Part = s_Geometry[s_VertexBase + s_V * s_Stride + s_Bones.Offset];

                        if (!s_PerPart.TryGetValue(s_Part, out var s_Mats))
                            s_PerPart[s_Part] = s_Mats = new SortedDictionary<uint, int>();

                        s_Mats[s_Subset.MaterialIndex] = s_Mats.GetValueOrDefault(s_Subset.MaterialIndex) + 1;
                    }
                }
            }

            string F(float p_V) => p_V.ToString("0.#####", CultureInfo.InvariantCulture);

            for (var i = 0; i < s_Lod.PartCount; i++)
            {
                var s_T = s_Transforms[i];
                var s_Mats = s_PerPart.GetValueOrDefault(i);
                p_Writer.WriteLine($"PART: {i} trans={F(s_T[9])},{F(s_T[10])},{F(s_T[11])} " +
                                   $"rot={string.Join(",", s_T.Take(9).Select(F))} " +
                                   $"mats={(s_Mats == null ? "-" : string.Join(";", s_Mats.Select(p_M => $"{p_M.Key}:{p_M.Value}")))}");
            }

            p_Writer.WriteLine($"Census of '{Mesh}': {s_Lod.PartCount} part(s), {s_Sections} section(s), " +
                               $"{s_PerPart.Count} part(s) with triangles.");
            return true;
        }
    }
}
