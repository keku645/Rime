using System;
using System.IO;
using System.Linq;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Content;
using RimeLib.Content.Frostbite;
using RimeLib.Content.Mounting;
using RimeLib.Frostbite.Core;
using RimeLib.IO;
using RimeLib.Mesh.Frostbite;
using RimeLib.Texture;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    [CommandDescription("Resolves missing chunk dependencies for dxtextures and meshes in the current bundle.")]
    public class ResolveMissingChunksCommand : Command
    {
        [CommandArgument(Description = "Optional: '1' = in a CAS bundle, a mesh chunk with no catalog-backed variant is NOT " +
                                       "copied in: it lives only inside the levels that use that mesh, which carry it themselves. " +
                                       "A drop-in package ships references and its own data, never the game's bytes.",
                         Optional = true)]
        public string? CatalogOnly { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            var s_BundleContext = (BundleBuildingContext)p_Context;
            var s_SbBuildingContext = (SbBuildingContext)s_BundleContext.Parent!;
            var s_BaseContext = (BaseContext)s_SbBuildingContext.Parent!;

            var s_Mounters = s_BaseContext.GetMounters();
            if (s_Mounters.Count == 0)
            {
                p_Writer.WriteLine("Error: No game mounter found.");
                return false;
            }
            var s_Mounter = s_Mounters.Values.First();

            var s_Resources = s_BundleContext.GetResources().ToList();
            int s_AddedChunks = 0;
            var s_CatalogOnly = CatalogOnly is "1" or "true" or "True" && s_BundleContext.Cas();

            foreach (var s_Res in s_Resources)
            {
                var s_Type = s_Res.Value.GetResourceType();
                GUID s_ChunkId = GUID.Empty;

                try
                {
                    if (s_Type == ResourceType.DxTexture || s_Type == ResourceType.Ps3Texture)
                    {
                        var s_TextureConverter = EngineInterfaceRegistry.Create<ITextureConverter>(s_SbBuildingContext.EngineType);
                        s_ChunkId = s_TextureConverter.GetTextureChunkId(s_Res.Value);
                    }
                    else if (s_Type == ResourceType.MeshSet)
                    {
                        using var s_Reader1 = s_Res.Value.GetReader();
                        var s_Data = s_Reader1.ReadBytes((int)s_Reader1.Length);
                        using var s_Reader = new RimeReader(new MemoryStream(s_Data));

                        // NOTE: the real fix for the "Stream length ... 2^31 (offset)" crash that
                        // dropped ~34 meshes (vehicle interiors/wrecks/projectiles) is in
                        // RelocPtr.DeserializeObject — those layouts carry an out-of-buffer RelocPtr
                        // that is now treated as null instead of aborting the parse.
                        var s_MeshSetLayout = new MeshSetLayout(s_Reader);

                        // The chunk's bundle entry carries h32 = fb::hashQuick(resource name); that is how a resident
                        // LOD binds its vertex data at bundle load. A vanilla-named resource finds a variant carrying its
                        // own hash; a RENAMED resource (a second identity of a game mesh) has none, and its STREAMED LODs
                        // (the 1P weapon meshes are a single StreamingEnable LOD, no resident one) never reached the
                        // bundle at all: measured 2026-09-18, spawn crash vu+0x297658 = the render thread drawing a
                        // vertex stream whose buffer is null. So every LOD's chunk goes in, and one with no variant
                        // under the resource's name is delivered as the catalog-backed full-range variant re-keyed to
                        // that name (a sha1 reference: no geometry bytes travel, only the entry).
                        var s_Fb2Mounter = s_Mounter as RimeLib.Content.Frostbite2_0.Mounting.EngineMounter;

                        // Measured 2026-09-18 (boots 13-15 of the accessory camo probe): with its resident LODs re-keyed
                        // to the clone, the 3P ACOG clone stopped painting (drawn from the resident low LOD, default
                        // materials) while with the old listing (resident LODs under the VANILLA hash = not bound to
                        // the clone) it streamed LOD0 and painted. So: a mesh WITH resident LODs keeps the old
                        // behaviour (resident LODs only, first variant); the re-keyed full listing is for meshes with
                        // NO resident LOD (every LOD StreamingEnable-only: the 1P weapon meshes), which otherwise
                        // crash at the first draw.
                        var s_HasResident = false;
                        for (var i = 0; i < s_MeshSetLayout.LodCount; i++)
                            if (s_MeshSetLayout.Lods[i].Object is { } s_L && (s_L.Flags & MeshLayout.MeshLayoutFlags.IsBaseLod) != 0)
                                s_HasResident = true;

                        for (var i = 0; i < s_MeshSetLayout.LodCount; i++)
                        {
                            var s_Lod = s_MeshSetLayout.Lods[i].Object;
                            if (s_Lod == null || s_Lod.DataChunkId == GUID.Empty || s_BundleContext.GetChunks().ContainsKey(s_Lod.DataChunkId))
                                continue;

                            if (s_HasResident && (s_Lod.Flags & MeshLayout.MeshLayoutFlags.IsBaseLod) == 0)
                                continue;

                            if (!s_Mounter.TryGetChunk(s_Lod.DataChunkId, out var s_ChunkObj))
                            {
                                p_Writer.WriteLine($"Warning: Missing mesh chunk {s_Lod.DataChunkId} for resource {s_Res.Key} not found in mounter.");
                                continue;
                            }

                            var s_ResNameHash = (int)RimeLib.Frostbite.Utils.HashQuick(s_Res.Key);
                            var s_Variant = s_ChunkObj.Variants.FirstOrDefault(v => v.GetAssetNameHash() == s_ResNameHash);
                            var s_How = "retail variant under the resource's own name";

                            if (s_Variant == null && !s_HasResident && s_Fb2Mounter != null &&
                                s_Fb2Mounter.TryGetFullRangeCasChunkVariant(s_Lod.DataChunkId, out var s_Rekeyed, s_Res.Key))
                            {
                                s_Variant = s_Rekeyed;
                                s_How = $"catalog reference re-keyed to h32 of '{s_Res.Key}'";
                            }

                            if (s_Variant == null && s_CatalogOnly)
                            {
                                s_Variant = s_ChunkObj.Variants.FirstOrDefault(v => v.Cas);
                                s_How = "catalog variant (its h32 is NOT this resource's name: a resident LOD will not bind)";

                                if (s_Variant == null)
                                {
                                    p_Writer.WriteLine($"Left to the level (no catalog copy): mesh chunk {s_Lod.DataChunkId} for resource {s_Res.Key} (LOD {i}, {s_Lod.Flags})");
                                    continue;
                                }
                            }

                            if (s_Variant == null)
                            {
                                s_Variant = s_ChunkObj.FirstVariant;
                                s_How = "first variant (its h32 is NOT this resource's name: a resident LOD will not bind)";
                            }

                            s_BundleContext.AddChunk(s_Lod.DataChunkId, s_Variant);
                            p_Writer.WriteLine($"Added missing mesh chunk: {s_Lod.DataChunkId} for resource {s_Res.Key} (LOD {i}, {s_Lod.Flags}; {s_How})");
                            s_AddedChunks++;
                        }
                    }

                    if (s_ChunkId != GUID.Empty && !s_BundleContext.GetChunks().ContainsKey(s_ChunkId))
                    {
                        if (s_Mounter.TryGetChunk(s_ChunkId, out var s_ChunkObj))
                        {
                            var s_Variant = s_ChunkObj.Variants.FirstOrDefault(v => v.GetContainedBundle() != null) ?? s_ChunkObj.FirstVariant;

                            if (s_CatalogOnly && !s_Variant.Cas)
                            {
                                var s_Catalog = s_ChunkObj.Variants.FirstOrDefault(v => v.Cas);
                                if (s_Catalog == null)
                                {
                                    p_Writer.WriteLine($"Left to the level (no catalog copy): texture chunk {s_ChunkId} for resource {s_Res.Key}");
                                    continue;
                                }

                                s_Variant = s_Catalog;
                            }

                            s_BundleContext.AddChunk(s_ChunkId, s_Variant);
                            p_Writer.WriteLine($"Added missing texture chunk: {s_ChunkId} for resource {s_Res.Key}");
                            s_AddedChunks++;
                        }
                        else
                        {
                            p_Writer.WriteLine($"Warning: Missing texture chunk {s_ChunkId} for resource {s_Res.Key} not found in mounter.");
                        }
                    }
                }
                catch (Exception p_Ex)
                {
                    p_Writer.WriteLine($"Error resolving chunks for {s_Res.Key}: {p_Ex.Message}");
                }
            }

            p_Writer.WriteLine($"Done tracking down missing chunks. Added {s_AddedChunks} missing chunks to bundle.");
            return true;
        }
    }
}
