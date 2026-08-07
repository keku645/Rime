using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Frostbite.Core;
using RimeLib.IO;
using RimeLib.Serialization;
using System;
using System.IO;
using System.Linq;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    [CommandDescription("Copies ONE MeshVariationDatabase entry (the one for <mesh_name>) from a SOURCE mvdb partition into a TARGET mvdb partition, and adds the patched target partition to this bundle (override). Lets a standalone IMPORTED mesh render on another level WITHOUT mounting the whole source MVDB (which drags in every other mesh's textures -> CreateTexture2D crash). The entry keeps its CtrRefs (mesh / materials / TextureParameters) — bring those partitions into the same bundle so the imports resolve.")]
    internal class MvdbAddEntryCommand : Command
    {
        [CommandArgument(Description = "Source MVDB partition name (has the entry), e.g. levels/mp_017/mp_017/meshvariationdb_win32.")]
        public string? SourceName { get; set; }

        [CommandArgument(Description = "Target MVDB partition name to patch (the current level's mvdb).")]
        public string? TargetName { get; set; }

        [CommandArgument(Description = "Id returned by mount_game.")]
        public int Id { get; set; }

        [CommandArgument(Description = "Mesh asset name of the entry to copy, e.g. levels/mp_017/terrain/mp_017_waves_01_mesh.")]
        public string? MeshName { get; set; }

        [CommandArgument(Description = "Optional: 'all' copies EVERY entry for the mesh (base + all VariationAssetNameHash entries — appearance/camo variations realize with hash=fnv(variation name) and an absent entry = invisible mesh). Default: first entry only.", Optional = true)]
        public string? Mode { get; set; }

        [CommandArgument(Description = "Optional: repoint the copied entry's Mesh/Materials refs from this SOURCE partition guid...", Optional = true)]
        public string? RepointFrom { get; set; }

        [CommandArgument(Description = "...to this TARGET partition guid. Use when the entry must map a CLONED mesh partition (same instance guids, fresh partition guid) instead of the original — the engine keys the MVDB entry by the mesh's guid, so a fresh mesh needs its own entry with Mesh repointed.", Optional = true)]
        public string? RepointTo { get; set; }

        [CommandArgument(Description = "Optional '1'/'true' = STRIP each material's TextureParameters + MaterialVariation from the copied entry. Use for a GREY silhouette: the entry still maps mesh->shader but binds NO textures, so the shader draws with defaults (grey) instead of crashing vu+0xC0E24 on a texture the bundle didn't ship / isn't resident. Textures come later via a full MVDB-tex pass.", Optional = true)]
        public string? StripTextures { get; set; }

        [CommandArgument(Description = "Optional: GRAFT the TextureParameters + MaterialVariation from THIS OTHER mesh's entry (in the same source MVDB) onto the copied entry's materials, by index. Use when the copied entry's shader was changed to match a different family (e.g. the mesh clones the M2 but its material uses VehiclePreset_Mud): mud samples Diffuse/Camo/Specular/HeatMap/Normal by PARAMETER, bound only via the entry's TextureParameters, and those must come from a mud-using mesh (the RHIB body) — the M2's weapon params never bind mud. The material's Material CtrRef (-> your mesh's MeshMaterial) is kept.", Optional = true)]
        public string? TexDonorMesh { get; set; }

        [CommandArgument(Description = "Optional: assign a FRESH partition guid to the target MVDB instead of inheriting the SOURCE partition's guid. ⛔ REQUIRED when the source MVDB is the SAME as the played map's loaded MVDB (e.g. sourcing mp_017/cql to patch a mesh on mp_017 itself): the inherited guid COLLIDES with the loaded native MVDB, the engine dedupes the partition, and the entry is never ingested into the mesh-variation index -> INVISIBLE mesh. A fresh guid makes the extra MVDB load as a distinct resource. Instance guids are preserved (internal refs are by-index).", Optional = true)]
        public string? NewGuid { get; set; }

        [CommandArgument(Description = "Optional: assign a FRESH INSTANCE guid to the target MVDB's primary MeshVariationDatabase instance. ⛔ REQUIRED with NewGuid when the source is the PLAYED map's own MVDB (cql): NewGuid freshens only the PARTITION guid, but Generate() inherits the primaryInstance guid from the source, and the MeshVariationManager DEDUPES MVDBs by that instance guid -> the target collides with the loaded cql and is never ingested (mesh invisible). RM_Common sidesteps this by sourcing from ANOTHER map (distinct instance). Use a fresh guid != the cql's MVDB instance.", Optional = true)]
        public string? NewInstGuid { get; set; }

        [CommandArgument(Description = "Optional (LAST arg): after grafting, REPOINT the entry's TextureParameters from donor texture partitions to YOUR OWN (custom textures shipped via add_dds_texture + a Name-only TextureAsset). Format: 'oldPartGuid>newPartGuid:newInstGuid,...' (matched by the texture ref's partition guid, unique per texture). Binds PWC_D/N/RGB instead of the RHIB's.", Optional = true)]
        public string? RetexMap { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(SourceName) || string.IsNullOrWhiteSpace(TargetName) || string.IsNullOrWhiteSpace(MeshName))
            {
                p_Writer.WriteLine("source_name, target_name and mesh_name are required.");
                return false;
            }

            var s_BundleContext = (BundleBuildingContext)p_Context;
            var s_SbBuildingContext = (SbBuildingContext?)s_BundleContext.Parent;
            var s_BaseContext = s_SbBuildingContext?.Parent as BaseContext;
            if (s_SbBuildingContext == null || s_BaseContext == null)
            {
                p_Writer.WriteLine("Context is invalid.");
                return false;
            }

            if (!s_BaseContext.GetMounters().TryGetValue(Id, out var s_EngineMounter))
            {
                p_Writer.WriteLine($"Id ({Id}) is not valid, ensure you mounted a game first");
                return false;
            }

            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(s_SbBuildingContext.EngineType);
            var s_Generator = EngineInterfaceRegistry.Create<IPartitionGenerator>(s_SbBuildingContext.EngineType);

            // --- parse the SOURCE mvdb and find the entry for <mesh_name> -------------------------
            if (!s_EngineMounter.TryGetPartition(SourceName!, out var s_SrcMounted))
            {
                p_Writer.WriteLine($"Could not find source partition ({SourceName}).");
                return false;
            }
            var s_SrcVariant = s_SrcMounted.Variants.FirstOrDefault(p_V => p_V.GetContainedBundle() != null) ?? s_SrcMounted.FirstVariant;
            var s_SrcDb = s_Converter.FromPartitionObject(SourceName!, s_SrcVariant!);

            // Find the entry among the SOURCE partition's instances (they ARE DataContainers). Match by
            // resolving the entry's Mesh CtrRef PARTITION GUID back to a name via the mounter — this
            // avoids .Get() (needs the PartitionRegistry) and avoids re-parsing the mesh partition
            // (a 2nd/3rd FromPartitionObject closes a shared stream -> ObjectDisposedException).
            var s_All = string.Equals(Mode, "all", StringComparison.OrdinalIgnoreCase);
            var s_Matches = new System.Collections.Generic.List<fb.MeshVariationDatabaseEntry>();
            int s_EntryCount = 0;
            foreach (var s_Inst in s_SrcDb.Instances)
            {
                if (s_Inst is not fb.MeshVariationDatabaseEntry s_E) continue;
                s_EntryCount++;
                if (s_EngineMounter.TryGetPartitionByGuid(s_E.Mesh.PartitionGuid, out var s_MName, out _)
                    && string.Equals(s_MName, MeshName, StringComparison.OrdinalIgnoreCase))
                {
                    s_Matches.Add(s_E);
                    if (!s_All)
                        break;
                }
            }
            if (s_Matches.Count == 0)
            {
                p_Writer.WriteLine($"No MVDB entry found for mesh '{MeshName}' in '{SourceName}'. ({s_EntryCount} entry instance(s) scanned).");
                return false;
            }
            var s_Entry = s_Matches[0];

            // Find the TEXTURE-DONOR entry in the SAME parsed source db (both meshes' entries live in the
            // level MVDB, so no 2nd FromPartitionObject) — used below to graft mud's texture parameters.
            fb.MeshVariationDatabaseEntry? s_Donor = null;
            if (!string.IsNullOrWhiteSpace(TexDonorMesh))
            {
                foreach (var s_Inst in s_SrcDb.Instances)
                {
                    if (s_Inst is not fb.MeshVariationDatabaseEntry s_E) continue;
                    if (s_EngineMounter.TryGetPartitionByGuid(s_E.Mesh.PartitionGuid, out var s_DName, out _)
                        && string.Equals(s_DName, TexDonorMesh, StringComparison.OrdinalIgnoreCase))
                    {
                        s_Donor = s_E;
                        break;
                    }
                }
                if (s_Donor == null)
                {
                    p_Writer.WriteLine($"No texture-donor MVDB entry found for mesh '{TexDonorMesh}' in '{SourceName}'.");
                    return false;
                }
            }

            // REPORT the textures this entry binds (materials' TextureParameters). The mesh's own material
            // params are often empty — the texture->slot binding lives here — so the caller must bring these
            // texture partitions into the bundle or the entry's imports dangle. Just reads guids (no Get()).
            var s_TexNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // partitions the copied entries' materials IMPORT beyond the mesh itself (the ObjectVariation
            // partitions carrying MeshMaterialVariation for hash!=0 entries) — the caller must bring these
            // into the SAME bundle or the mini's imports dangle at realize (MVDB-VAR lines, like MVDB-TEX).
            var s_VarNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s_M in s_Matches)
                foreach (var s_Mat in s_M.Materials)
                {
                    foreach (var s_Tp in s_Mat.TextureParameters)
                    {
                        if (s_Tp?.Value == null) continue;
                        if (s_EngineMounter.TryGetPartitionByGuid(s_Tp.Value.PartitionGuid, out var s_TexName, out _)
                            && !string.IsNullOrWhiteSpace(s_TexName))
                            s_TexNames.Add(s_TexName);
                    }
                    if (s_Mat.MaterialVariation?.PartitionGuid is { } s_MvG && s_MvG != GUID.Empty
                        && s_EngineMounter.TryGetPartitionByGuid(s_MvG, out var s_MvName, out _)
                        && !string.IsNullOrWhiteSpace(s_MvName)
                        && !string.Equals(s_MvName, MeshName, StringComparison.OrdinalIgnoreCase))
                        s_VarNames.Add(s_MvName);
                }
            foreach (var s_T in s_TexNames)
                p_Writer.WriteLine($"MVDB-TEX: {s_T}");
            foreach (var s_V in s_VarNames)
                p_Writer.WriteLine($"MVDB-VAR: {s_V}");

            // --- build a MINIMAL standalone MVDB = the source STRIPPED to only this one entry ------
            // Regenerating the target level's huge (595-entry) MVDB isn't byte-faithful -> it crashed
            // XP1_002 at load. Instead we strip the SOURCE MVDB down to just the wave-mesh entry and
            // add it as a NEW partition (TargetName): the level's own MVDB stays UNTOUCHED, and the
            // game consults this extra MVDB for the mesh's variation entry. The tiny regen is faithful.
            var s_KeepIds = new System.Collections.Generic.List<(fb.MeshVariationDatabaseEntry Entry, GUID Id)>();
            foreach (var s_M in s_Matches)
            {
                if (s_M.InstanceId is not DataContainerId.Guid s_IdG)
                {
                    p_Writer.WriteLine($"Entry (hash={s_M.VariationAssetNameHash}) has a non-guid instance id — skipped.");
                    continue;
                }
                s_KeepIds.Add((s_M, s_IdG.Id));
            }
            if (s_KeepIds.Count == 0)
            {
                p_Writer.WriteLine("Entry has a non-guid instance id.");
                return false;
            }
            var s_SrcConcrete = (RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition)s_SrcDb;
            var s_SrcMvdb = (fb.MeshVariationDatabase)s_SrcConcrete.PrimaryInstance;

            // keep ONLY the primary MVDB instance + the matched entries; drop every other instance
            var s_PrimG = s_SrcConcrete.PrimaryInstanceGuid;
            var s_ToRemove = s_SrcConcrete.InstanceMap.Keys
                .Where(p_K => p_K.CompareTo(s_PrimG) != 0 && !s_KeepIds.Any(p_E => p_K.CompareTo(p_E.Id) == 0))
                .ToList();
            foreach (var s_K in s_ToRemove)
                s_SrcConcrete.InstanceMap.Remove(s_K);

            // Assign a FRESH partition guid if requested — set BEFORE the Entries CtrRefs and Generate below
            // (both read s_SrcConcrete.PartitionGuid). Prevents the target MVDB from colliding with the source
            // when the source == the played map's own loaded MVDB (the collision made the partition dedupe ->
            // entry never ingested -> invisible). Instance guids stay (internal entry refs are by-index).
            if (!string.IsNullOrWhiteSpace(NewGuid))
            {
                s_SrcConcrete.PartitionGuid = new GUID(NewGuid!);
                p_Writer.WriteLine($"Reassigned target MVDB partition guid -> {NewGuid}.");
            }

            // Also freshen the primary MeshVariationDatabase INSTANCE guid. The MeshVariationManager dedupes
            // loaded MVDBs by this instance guid, and Generate() inherited it from the source cql (a loaded
            // MVDB on the played map) -> the target would be deduped away and its entry never ingested (mesh
            // invisible). Re-key the InstanceMap entry (order shifts to the end, but the engine reads the
            // primary by its PrimaryInstanceGuid, not by position). Must run AFTER the s_ToRemove filter above
            // (which keys on the OLD primary guid) and can run before/independently of the Entries rebuild.
            if (!string.IsNullOrWhiteSpace(NewInstGuid))
            {
                var s_OldPrim = s_SrcConcrete.PrimaryInstanceGuid;
                var s_NewPrim = new GUID(NewInstGuid!);
                if (s_SrcConcrete.InstanceMap.TryGetValue(s_OldPrim, out var s_PrimInst))
                {
                    s_SrcConcrete.InstanceMap.Remove(s_OldPrim);
                    s_SrcConcrete.InstanceMap[s_NewPrim] = s_PrimInst;
                    s_SrcConcrete.PrimaryInstanceGuid = s_NewPrim;
                    p_Writer.WriteLine($"Reassigned target MVDB primary instance guid -> {NewInstGuid}.");
                }
                else
                {
                    p_Writer.WriteLine($"WARN: primary instance {s_OldPrim} not found in InstanceMap — instance guid unchanged.");
                }
            }

            // the MVDB now lists only our entries
            s_SrcMvdb.Entries.Clear();
            foreach (var s_E in s_KeepIds)
                s_SrcMvdb.Entries.AddRef(new CtrRef<fb.MeshVariationDatabaseEntry>(s_SrcConcrete.PartitionGuid, s_E.Entry.InstanceId));
            s_SrcMvdb.RedirectEntries.Clear();

            // Repoint the entry's Mesh + Materials refs from the ORIGINAL mesh partition to a CLONE partition
            // (fresh partition guid, SAME instance guids). The engine keys the MVDB entry by the mesh's guid,
            // so a byte-cloned mesh (fresh partition) needs its OWN entry with Mesh (and its Materials) pointing
            // at the clone. Only the partition guid changes; instance guids are preserved by the byte-clone.
            if (!string.IsNullOrWhiteSpace(RepointFrom) && !string.IsNullOrWhiteSpace(RepointTo))
            {
                var s_From = new GUID(RepointFrom!);
                var s_To = new GUID(RepointTo!);
                int s_Repointed = 0;
                foreach (var s_M in s_Matches)
                {
                    if (s_M.Mesh.PartitionGuid == s_From) { s_M.Mesh.SetValue(s_To, s_M.Mesh.InstanceId); s_Repointed++; }
                    foreach (var s_Mat in s_M.Materials)
                    {
                        if (s_Mat.Material.PartitionGuid == s_From) { s_Mat.Material.SetValue(s_To, s_Mat.Material.InstanceId); s_Repointed++; }
                        if (s_Mat.MaterialVariation.PartitionGuid == s_From) { s_Mat.MaterialVariation.SetValue(s_To, s_Mat.MaterialVariation.InstanceId); s_Repointed++; }
                    }
                }
                p_Writer.WriteLine($"Repointed {s_Repointed} ref(s) from partition {s_From} to {s_To}.");
            }

            // Graft mud's texture parameters from the donor entry onto the copied entry's materials (by index),
            // keeping the copied entry's Material CtrRef (-> our mesh's MeshMaterial). This binds Diffuse/Camo/
            // Specular/HeatMap/Normal for VehiclePreset_Mud, which the M2 (weapon) entry could never provide.
            if (s_Donor != null)
            {
                int s_Grafted = 0;
                foreach (var s_M in s_Matches)
                {
                    int s_N = System.Math.Min(s_M.Materials.Count, s_Donor.Materials.Count);
                    for (var i = 0; i < s_N; i++)
                    {
                        s_M.Materials[i].TextureParameters = s_Donor.Materials[i].TextureParameters;
                        s_M.Materials[i].MaterialVariation.SetValue(s_Donor.Materials[i].MaterialVariation);
                        s_Grafted += s_M.Materials[i].TextureParameters.Count;
                    }
                }
                p_Writer.WriteLine($"Grafted {s_Grafted} texture parameter(s) from donor '{TexDonorMesh}'.");
            }

            // Repoint the grafted TextureParameters from the donor's textures (e.g. the RHIB's RHIB_D/N/RGB)
            // to OUR OWN custom textures. Matched by the texture ref's PARTITION guid (unique per texture);
            // the new value is our Name-only TextureAsset (add_json_partition) whose Name resolves the
            // DxTexture resource (add_dds_texture). Lets an authored mesh wear its OWN textures.
            if (!string.IsNullOrWhiteSpace(RetexMap))
            {
                var s_Retex = new System.Collections.Generic.Dictionary<GUID, (GUID, GUID)>();
                foreach (var s_Pair in RetexMap!.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var s_Sides = s_Pair.Split('>');
                    var s_New = s_Sides[1].Split(':');
                    s_Retex[new GUID(s_Sides[0])] = (new GUID(s_New[0]), new GUID(s_New[1]));
                }
                int s_Repointed2 = 0;
                foreach (var s_M in s_Matches)
                    foreach (var s_Mat in s_M.Materials)
                        foreach (var s_Tp in s_Mat.TextureParameters)
                        {
                            if (s_Tp?.Value == null) continue;
                            if (s_Retex.TryGetValue(s_Tp.Value.PartitionGuid, out var s_N))
                            {
                                s_Tp.Value.SetValue(s_N.Item1, new DataContainerId.Guid(s_N.Item2));
                                s_Repointed2++;
                            }
                        }
                p_Writer.WriteLine($"Retex: repointed {s_Repointed2} texture parameter(s) to custom textures.");
            }

            // Strip texture bindings for a GREY silhouette (no texture refs -> no missing-texture crash).
            if (!string.IsNullOrWhiteSpace(StripTextures) && (StripTextures == "1" || StripTextures!.ToLowerInvariant() == "true"))
            {
                int s_Stripped = 0;
                foreach (var s_M in s_Matches)
                    foreach (var s_Mat in s_M.Materials)
                    {
                        s_Stripped += s_Mat.TextureParameters.Count;
                        s_Mat.TextureParameters.Clear();
                        s_Mat.MaterialVariation.SetValue(GUID.Empty, new DataContainerId.Guid(GUID.Empty));
                    }
                p_Writer.WriteLine($"Stripped {s_Stripped} texture parameter(s) + material variations (grey render).");
            }

            var s_Stream = new MemoryStream();
            using var s_ResWriter = new RimeWriter(s_Stream);   // function-scoped: keep stream alive
            s_Generator.Generate(s_SrcDb, s_ResWriter);
            var s_Bytes = s_Stream.ToArray();

            s_BundleContext.AddRawPartitionBytes(TargetName!, s_Bytes);
            // Register the target MVDB in the bundle's mvdb-registry-ref list so a following
            // emit_subworld_registry can put it in a SubWorld's AssetRegistry (AddRegistry delivery fallback).
            s_BundleContext.AddMvdbRegistryRef(s_SrcConcrete.PartitionGuid, s_PrimG);

            var s_Hashes = string.Join(",", s_KeepIds.Select(p_E => p_E.Entry.VariationAssetNameHash));
            p_Writer.WriteLine($"Built minimal MVDB '{TargetName}' with {s_KeepIds.Count} '{MeshName}' entr(ies) (variationAssetNameHash={s_Hashes}, {s_Bytes.Length} bytes).");
            return true;
        }
    }
}
