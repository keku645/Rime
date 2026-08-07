using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Content.Mounting;
using RimeLib.Frostbite.Core;
using RimeLib.Serialization;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    [CommandDescription("Ships the CAS chunks referenced by EBX FIELDS (SoundDataAsset.Chunks[].ChunkId) of the " +
        "partitions in this bundle. resolve_missing_chunks only follows chunk ids embedded in RESOURCE data " +
        "(dxtexture/meshset); a SoundWaveAsset carries its stream chunk id inside the EBX itself, so its chunk is " +
        "never pulled. The level's wave-asset registration then primes an unbacked wave with turbo priority -> " +
        "turboLoaderRequestChunk with a null index out of the load window -> AV (vu+0xc1c76). The chunk is added " +
        "bundle-level exactly like resolve_missing_chunks so it loads in-window with the level and the wave is " +
        "resident (WaveManager::addToCache early-out, no turbo request). Run AFTER resolve_partition_dependencies.")]
    internal class ResolveEbxChunksCommand : Command
    {
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
            var s_Fb2 = s_Mounter as RimeLib.Content.Frostbite2_0.Mounting.EngineMounter;
            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(s_Mounter.GetEngineType());

            var s_Partitions = s_BundleContext.GetPartitions();
            var s_Names = s_Partitions.Keys.ToList();

            var s_Seen = new HashSet<GUID>();
            int s_Added = 0, s_CasRef = 0, s_Payload = 0, s_NotFound = 0;
            long s_PayloadKiB = 0;

            foreach (var s_Name in s_Names)
            {
                var s_Obj = s_Partitions[s_Name];

                // Only mounted variants carry a typed sound asset we can walk. Generated/raw
                // partitions (add_json_partition/add_raw_partition) have no EBX chunk fields.
                if (s_Obj is not IObjectVariant s_Variant)
                    continue;

                DatabasePartitionBase s_Partition;
                try
                {
                    s_Partition = s_Converter.FromPartitionObject(s_Name, s_Variant);
                }
                catch (Exception s_Ex)
                {
                    p_Writer.WriteLine($"Warning: failed to parse partition '{s_Name}': {s_Ex.Message}");
                    continue;
                }

                foreach (var s_Instance in s_Partition.Instances)
                {
                    // SoundWaveAsset (and any other SoundDataAsset subtype) keeps its stream chunk id
                    // in .Chunks[].ChunkId, invisible to the resource-based resolve_missing_chunks.
                    if (s_Instance is not fb.SoundDataAsset s_Sda)
                        continue;

                    foreach (var s_Chunk in s_Sda.Chunks)
                    {
                        var s_Id = s_Chunk.ChunkId;
                        if (s_Id == GUID.Empty || !s_Seen.Add(s_Id))
                            continue;
                        if (s_BundleContext.GetChunks().ContainsKey(s_Id))
                            continue;

                        if (!s_Mounter.TryGetChunk(s_Id, out var s_ChunkObj))
                        {
                            p_Writer.WriteLine($"Warning: sound chunk {s_Id} ({s_Sda.Name}) not found in mounter.");
                            s_NotFound++;
                            continue;
                        }

                        var s_ChunkVar = s_ChunkObj.Variants.FirstOrDefault(v => v.GetContainedBundle() != null)
                                       ?? s_ChunkObj.FirstVariant;

                        s_BundleContext.AddChunk(s_Id, s_ChunkVar);
                        s_Added++;

                        // A CAS build content-addresses the stored frame: if the chunk payload is in
                        // cas.cat it serializes as a 0-byte cas-ref, otherwise the bytes are inlined.
                        var s_IsCas = s_Fb2 != null && s_Fb2.TryGetChunkCatalogSha1(s_Id, out _);
                        if (s_IsCas)
                        {
                            s_CasRef++;
                        }
                        else
                        {
                            s_Payload++;
                            s_PayloadKiB += s_Chunk.ChunkSize / 1024;
                        }
                    }
                }
            }

            p_Writer.WriteLine($"resolve_ebx_chunks: added {s_Added} sound chunk(s) — " +
                               $"{s_CasRef} cas-ref (0 bytes), {s_Payload} payload (~{s_PayloadKiB} KiB), " +
                               $"{s_NotFound} not found.");
            return true;
        }
    }
}
