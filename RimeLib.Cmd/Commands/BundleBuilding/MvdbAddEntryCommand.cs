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

        [CommandArgument(Description = "Optional: make the copied entry a BRAND-NEW VARIATION — sets its " +
                                       "VariationAssetNameHash to this value (fb hashQuickLowerCase of the " +
                                       "variation asset name; an instance requesting that hash realizes this " +
                                       "entry). Use with VariationRefs.", Optional = true)]
        public string? VariationHash { get; set; }

        [CommandArgument(Description = "Optional: point the copied entry's MaterialVariation refs at YOUR " +
                                       "MeshMaterialVariation instances (the ObjectVariation partition added " +
                                       "via add_json_partition). Format: 'partGuid:instGuid1[,instGuid2...]' — " +
                                       "instances are consumed in material order, filtered by VariationShaderFilter.",
                         Optional = true)]
        public string? VariationRefs { get; set; }

        [CommandArgument(Description = "Optional: only materials whose MeshMaterial surface shader resolves to " +
                                       "THIS partition name get a VariationRefs instance (a multi-material mesh " +
                                       "must not have its OTHER shaders' materials repointed at yours). Omit = " +
                                       "all materials.", Optional = true)]
        public string? VariationShaderFilter { get; set; }

        [CommandArgument(Description = "Optional (LAST arg): path to a JSON spec that REBUILDS the copied " +
                                       "entry's Materials list, so an authored mesh with N subsets gets its N " +
                                       "material bindings even though no shipped donor entry has N materials. " +
                                       "The entry is still COPIED from the donor (its structure, and the nested " +
                                       "Materials[]/TextureParameters[] arrays, come from DICE) — only the list " +
                                       "contents are replaced, which is why this works where authoring the whole " +
                                       "MVDB from JSON does not. Shape: {\"materials\":[{\"material\":{\"partition\":" +
                                       "\"guid\",\"instance\":\"guid\"},\"textures\":[{\"name\":\"Diffuse\"," +
                                       "\"partition\":\"guid\",\"instance\":\"guid\"}]}]}. Entry.Materials[i] binds " +
                                       "mesh subset i (MaterialIndex=i), so order matters.", Optional = true)]
        public string? MaterialsSpec { get; set; }

        [CommandArgument(Description = "Optional: '1'/'true' = apply RetexMap ONLY to the NEW variation entry " +
                                       "(the one carrying VariationHash), leaving every copied entry untouched. " +
                                       "⛔ WITHOUT this the repoint hits EVERY copied entry that binds the old " +
                                       "texture — including the BASE one — because it runs before the new entry " +
                                       "exists. Measured: repointing a weapon's base camo to author a NEW camo " +
                                       "silently changed what that weapon looks like with NO camo selected. Use " +
                                       "it whenever the repoint is meant to DEFINE a variation rather than to " +
                                       "re-skin a cloned mesh (the original use, which wants every entry moved " +
                                       "and stays the default).", Optional = true)]
        public string? RetexVariationOnly { get; set; }

        [CommandArgument(Description = "Optional: ADD texture parameters to the NEW variation entry that its " +
                                       "materials do not have at all. Format: 'Name>partGuid:instGuid[,...]'. " +
                                       "RetexMap can only MOVE a binding that already exists, so it cannot " +
                                       "author a camo on a weapon whose entry has no Camo slot — measured: " +
                                       "such a weapon renders with the camo layer WHITE, because the shader " +
                                       "samples a texture nothing bound. Putting it on the MeshMaterialVariation " +
                                       "instead does NOT work either; the engine reads texture bindings from the " +
                                       "ENTRY. Applies only to the entry carrying VariationHash, never to the " +
                                       "copied ones. A parameter already present is left alone — use RetexMap " +
                                       "for those.", Optional = true)]
        public string? AddTextureParams { get; set; }

        private bool RetexScopedToVariation =>
            RetexVariationOnly is "1" or "true" or "True" or "TRUE";

        /// <summary>A fresh CtrRef with the same target — never the SAME object, the serializer treats
        /// shared references per-use but shared mutable state across two entries is a trap.</summary>
        private static CtrRef<T> CloneRef<T>(CtrRefBase? p_Source) where T : RimeLib.Serialization.DataContainerBase
        {
            if (p_Source == null || p_Source.IsNull() || p_Source.InstanceId is not DataContainerId.Guid s_Id)
                return new CtrRef<T>();

            return new CtrRef<T>(p_Source.PartitionGuid, s_Id.Id);
        }

        private static GUID DeterministicGuid(string p_Seed)
        {
            using var s_Md5 = System.Security.Cryptography.MD5.Create();
            var s_Bytes = s_Md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(p_Seed));
            return new GUID(new System.Guid(s_Bytes).ToString());
        }

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
            System.Collections.Generic.Dictionary<GUID, (GUID, GUID)>? s_Retex = null;

            if (!string.IsNullOrWhiteSpace(RetexMap))
            {
                s_Retex = new System.Collections.Generic.Dictionary<GUID, (GUID, GUID)>();
                foreach (var s_Pair in RetexMap!.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var s_Sides = s_Pair.Split('>');
                    var s_New = s_Sides[1].Split(':');
                    s_Retex[new GUID(s_Sides[0])] = (new GUID(s_New[0]), new GUID(s_New[1]));
                }
            }

            // Applies the repoint to ONE entry, so the caller decides the blast radius.
            int ApplyRetex(fb.MeshVariationDatabaseEntry p_Entry)
            {
                if (s_Retex == null) return 0;

                var s_Count = 0;
                foreach (var s_Mat in p_Entry.Materials)
                    foreach (var s_Tp in s_Mat.TextureParameters)
                    {
                        if (s_Tp?.Value == null) continue;
                        if (s_Retex.TryGetValue(s_Tp.Value.PartitionGuid, out var s_N))
                        {
                            s_Tp.Value.SetValue(s_N.Item1, new DataContainerId.Guid(s_N.Item2));
                            s_Count++;
                        }
                    }

                return s_Count;
            }

            // ⛔ BY DEFAULT THIS MOVES EVERY COPIED ENTRY, AND THAT INCLUDES THE BASE ONE. Right for the
            // original use — a cloned mesh wearing its own art everywhere — and WRONG when the repoint is what
            // DEFINES a new variation: authoring a new weapon camo this way also repainted the weapon's
            // NO-camo look, because the base entry binds the very texture being repointed (measured on the
            // M240: its base came back bound to the new camo). RetexVariationOnly scopes it to the new entry.
            if (s_Retex != null && !RetexScopedToVariation)
            {
                var s_Repointed2 = 0;
                foreach (var s_M in s_Matches)
                    s_Repointed2 += ApplyRetex(s_M);

                p_Writer.WriteLine($"Retex: repointed {s_Repointed2} texture parameter(s) to custom textures.");
            }

            // Brand-new variation: re-hash the copied entry and hang OUR MeshMaterialVariation instances off
            // its materials. The shader filter keeps a multi-material mesh honest — only the materials whose
            // surface shader IS the one being varied get repointed; the rest keep their vanilla variation.
            if (!string.IsNullOrWhiteSpace(VariationHash))
            {
                if (!uint.TryParse(VariationHash, out var s_NewHash))
                {
                    p_Writer.WriteLine($"Invalid VariationHash '{VariationHash}' (expected a u32).");
                    return false;
                }

                // The copied vanilla entries keep their OWN hashes and ride along UNCHANGED: the runtime's
                // per-mesh variation list is headed by whatever registered LAST, and a mini-MVDB carrying
                // ONLY the new variation made every fresh spawn default to it (the security booth turned
                // red with no variation selected — its vanilla entry had been left behind in the level
                // MVDB). With the vanilla entries copied first and OUR entry appended, the list head stays
                // vanilla and the new variation is reachable by its hash. Callers pass mode 'all' for this.
                var s_Base = s_Matches.FirstOrDefault(p_M => p_M.VariationAssetNameHash == 0) ?? s_Matches[0];

                // ⛔ A KEY THIS PARTITION ALREADY CARRIES MUST BE AUTHORED IN PLACE, NOT APPENDED TO.
                // The runtime keys a variation by (mesh nameHash, variationAssetNameHash) and the FIRST
                // entry to reach that key fills it — every later entry for the same key is dropped in
                // silence, INCLUDING one in the same partition, because the entries of one database are
                // registered in list order. Appending a second entry under a hash the copied vanilla
                // entries already carry therefore ships a variation the engine never reads: the vanilla
                // copy sits ahead of it. Measured: a hash-0 takeover built this way changed nothing in
                // game even with the mod's bundle loading first. So when the hash is already present,
                // THAT entry is the one whose material variation refs get repointed.
                var s_Existing = s_Matches.FirstOrDefault(p_M => p_M.VariationAssetNameHash == s_NewHash);

                fb.MeshVariationDatabaseEntry s_Ours;
                if (s_Existing != null)
                {
                    s_Ours = s_Existing;
                    p_Writer.WriteLine($"Variation hash {s_NewHash} is already an entry of this mesh — " +
                                       "authoring THAT entry in place (a second entry for the same key " +
                                       "would be dropped by the engine).");
                }
                else
                {
                    s_Ours = new fb.MeshVariationDatabaseEntry
                    {
                        Mesh = CloneRef<fb.MeshAsset>(s_Base.Mesh),
                        VariationAssetNameHash = s_NewHash,
                    };

                    foreach (var s_Mat in s_Base.Materials)
                        s_Ours.Materials.Add(new fb.MeshVariationDatabaseMaterial
                        {
                            Material = CloneRef<fb.MeshMaterial>(s_Mat.Material),
                            MaterialVariation = CloneRef<fb.MeshMaterialVariation>(s_Mat.MaterialVariation),
                            TextureParameters = s_Mat.TextureParameters
                                .Select(p_T => new fb.TextureShaderParameter
                                {
                                    ParameterName = p_T.ParameterName,
                                    Value = CloneRef<fb.TextureBaseAsset>(p_T.Value),
                                })
                                .ToList(),
                        });
                }

                // Bindings the material does not have at all. A weapon whose entry never had a Camo slot
                // cannot get one by repointing, and the shader that samples it then reads nothing and draws
                // that layer white.
                if (!string.IsNullOrWhiteSpace(AddTextureParams))
                {
                    var s_Added = 0;
                    foreach (var s_Pair in AddTextureParams!.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var s_Sides = s_Pair.Split('>');
                        var s_Ref = s_Sides[1].Split(':');
                        var s_Name = s_Sides[0].Trim();

                        foreach (var s_Mat in s_Ours.Materials)
                        {
                            if (s_Mat.TextureParameters.Any(p_T => p_T?.ParameterName == s_Name))
                                continue;

                            s_Mat.TextureParameters.Add(new fb.TextureShaderParameter
                            {
                                ParameterName = s_Name,
                                Value = new CtrRef<fb.TextureBaseAsset>(
                                    new GUID(s_Ref[0]), new DataContainerId.Guid(new GUID(s_Ref[1]))),
                            });
                            s_Added++;
                        }
                    }

                    p_Writer.WriteLine($"Added {s_Added} texture parameter(s) to the NEW variation entry.");

                    if (s_Added == 0)
                        p_Writer.WriteLine("WARN: added none — every material already had those parameters.");
                }

                // Scoped repoint: the new entry was cloned from an untouched base, so it still binds the old
                // texture and this is where it becomes the new camo — and the only place it changes.
                if (RetexScopedToVariation)
                {
                    var s_Scoped = ApplyRetex(s_Ours);
                    p_Writer.WriteLine($"Retex: repointed {s_Scoped} texture parameter(s) on the NEW variation " +
                                       "entry only (copied entries left untouched).");

                    if (s_Scoped == 0 && s_Retex != null)
                        p_Writer.WriteLine("WARN: nothing matched — the new entry binds none of the textures " +
                                           "named in RetexMap, so this variation is a copy of the base.");
                }

                // A mesh with no hash-0 entry (its vanilla look IS a named variation, like the security
                // booth's variation_01) gets a synthesized BASE: a faithful clone of the first vanilla
                // entry with hash 0. The map editor keys its "Default variation" row on hash 0, and this
                // shape - vanilla copies + synthesized base + the new variation - is the one PROVEN by an
                // actual client boot (the 3-row panel with Default first). ⚠ Shipping the base WITHOUT the
                // vanilla copies crashed the server's load in a headless A/B - if this ever needs slimming,
                // re-verify against the engine, not against the round-trip.
                if (!s_Matches.Any(p_M => p_M.VariationAssetNameHash == 0) &&
                    System.Environment.GetEnvironmentVariable("RIME_NO_BASE_SYNTH") == null)
                {
                    var s_BaseZero = new fb.MeshVariationDatabaseEntry
                    {
                        Mesh = CloneRef<fb.MeshAsset>(s_Base.Mesh),
                        VariationAssetNameHash = 0,
                    };

                    foreach (var s_Mat in s_Base.Materials)
                        s_BaseZero.Materials.Add(new fb.MeshVariationDatabaseMaterial
                        {
                            Material = CloneRef<fb.MeshMaterial>(s_Mat.Material),
                            MaterialVariation = CloneRef<fb.MeshMaterialVariation>(s_Mat.MaterialVariation),
                            TextureParameters = s_Mat.TextureParameters
                                .Select(p_T => new fb.TextureShaderParameter
                                {
                                    ParameterName = p_T.ParameterName,
                                    Value = CloneRef<fb.TextureBaseAsset>(p_T.Value),
                                })
                                .ToList(),
                        });

                    var s_BaseGuid = DeterministicGuid((NewInstGuid ?? TargetName ?? "varentry") + "::base-entry");
                    s_BaseZero.InstanceId = new DataContainerId.Guid(s_BaseGuid);
                    s_SrcConcrete.InstanceMap[s_BaseGuid] = s_BaseZero;
                    s_SrcMvdb.Entries.AddRef(
                        new CtrRef<fb.MeshVariationDatabaseEntry>(s_SrcConcrete.PartitionGuid, s_BaseZero.InstanceId));

                    p_Writer.WriteLine("Synthesized a hash-0 BASE entry (the mesh has none; its vanilla " +
                                       "look is a named variation) so default spawns keep the vanilla look.");
                }

                // Registered under a deterministic fresh instance guid, appended AFTER the vanilla copies.
                // Skipped when the entry IS one of the copies (an in-place takeover of a key already here):
                // it is already listed, and re-registering it would duplicate the key.
                if (s_Existing == null)
                {
                    var s_OursGuid = DeterministicGuid((NewInstGuid ?? TargetName ?? "varentry") + "::variation-entry");
                    s_Ours.InstanceId = new DataContainerId.Guid(s_OursGuid);
                    s_SrcConcrete.InstanceMap[s_OursGuid] = s_Ours;
                    s_SrcMvdb.Entries.AddRef(
                        new CtrRef<fb.MeshVariationDatabaseEntry>(s_SrcConcrete.PartitionGuid, s_Ours.InstanceId));
                }

                if (!string.IsNullOrWhiteSpace(VariationRefs))
                {
                    var s_RefSides = VariationRefs!.Split(':');
                    if (s_RefSides.Length != 2)
                    {
                        p_Writer.WriteLine("Invalid VariationRefs (expected 'partGuid:instGuid1[,instGuid2...]').");
                        return false;
                    }

                    var s_VarPartition = new GUID(s_RefSides[0]);
                    var s_VarInstances = s_RefSides[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p_G => new GUID(p_G)).ToList();

                    // Resolving each material's surface shader needs its MeshMaterial, which lives in the mesh
                    // partition — parsed once here, same converter pattern as dump_shader_material_textures.
                    RimeLib.Serialization.DatabasePartitionBase? s_MeshDb = null;
                    if (!string.IsNullOrWhiteSpace(VariationShaderFilter) &&
                        s_EngineMounter.TryGetPartition(MeshName!, out var s_MeshMounted))
                    {
                        var s_MeshVariant = s_MeshMounted.Variants.FirstOrDefault(p_V => p_V.GetContainedBundle() != null)
                                            ?? s_MeshMounted.FirstVariant;
                        if (s_MeshVariant != null)
                            try { s_MeshDb = s_Converter.FromPartitionObject(MeshName!, s_MeshVariant); }
                            catch { /* filter falls back to 'all materials' below, reported */ }
                    }

                    bool MaterialMatchesFilter(fb.MeshVariationDatabaseMaterial p_Mat)
                    {
                        if (string.IsNullOrWhiteSpace(VariationShaderFilter))
                            return true;

                        if (s_MeshDb is not RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition s_Concrete ||
                            (p_Mat.Material.InstanceId as DataContainerId.Guid)?.Id is not { } s_MatGuid ||
                            !s_Concrete.InstanceMap.TryGetValue(s_MatGuid, out var s_MatInst) ||
                            s_MatInst is not fb.MeshMaterial s_MeshMat)
                            return false;

                        return s_EngineMounter.TryGetPartitionByGuid(s_MeshMat.Shader.Shader.PartitionGuid,
                                   out var s_ShaderName, out _) &&
                               s_ShaderName.Equals(VariationShaderFilter, StringComparison.OrdinalIgnoreCase);
                    }

                    var s_Consumed = 0;
                    foreach (var s_Mat in s_Ours.Materials)
                    {
                        if (s_Consumed >= s_VarInstances.Count)
                            break;
                        if (!MaterialMatchesFilter(s_Mat))
                            continue;

                        s_Mat.MaterialVariation.SetValue(s_VarPartition,
                            new DataContainerId.Guid(s_VarInstances[s_Consumed]));
                        s_Consumed++;
                    }

                    // (The texture-parameter strip that used to live here is gone: it was a workaround for
                    // the writer declaring fidelity-mined sizes over sequentially-written value types —
                    // fixed at the descriptor, verified by round-trip. Faithful parameters ride again.)

                    p_Writer.WriteLine($"Variation: hash={s_NewHash}, {s_Consumed}/{s_VarInstances.Count} " +
                                       $"material(s) repointed at {s_RefSides[0]}" +
                                       (string.IsNullOrWhiteSpace(VariationShaderFilter)
                                           ? ""
                                           : $" (filter '{VariationShaderFilter}')"));

                    if (s_Consumed == 0)
                    {
                        p_Writer.WriteLine("VARIATION FAILED: no material took a variation instance (wrong " +
                                           "filter, or the mesh partition did not parse).");
                        return false;
                    }
                }
                else
                {
                    p_Writer.WriteLine($"Variation: hash={s_NewHash} (materials keep their copied variation refs).");
                }
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

            // Rebuild the copied entry's Materials from a spec. An authored mesh can have any number of
            // subsets, and Entry.Materials is POSITIONAL (Materials[i] binds subset i), but no shipped
            // donor entry has an arbitrary N. Authoring the whole MVDB partition from JSON is NOT an
            // option: the EbxWriter mis-emits nested arrays (Materials[] of structs that each hold
            // TextureParameters[]) and Rime's own reader throws ArgumentOutOfRange on the result — the
            // engine died at the very start of level load on such a file. Copying the donor entry and
            // replacing only the LIST CONTENTS keeps DICE's nested-array shape and stays valid.
            if (!string.IsNullOrWhiteSpace(MaterialsSpec))
            {
                if (!File.Exists(MaterialsSpec))
                {
                    p_Writer.WriteLine($"MaterialsSpec not found: {MaterialsSpec}");
                    return false;
                }

                var s_Spec = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(MaterialsSpec!));
                var s_SpecMats = (Newtonsoft.Json.Linq.JArray?) s_Spec["materials"];
                if (s_SpecMats == null || s_SpecMats.Count == 0)
                {
                    p_Writer.WriteLine("MaterialsSpec has no 'materials' array.");
                    return false;
                }

                foreach (var s_M in s_Matches)
                {
                    s_M.Materials.Clear();

                    foreach (var s_SpecMat in s_SpecMats)
                    {
                        var s_MatRef = s_SpecMat["material"]!;
                        var s_New = new fb.MeshVariationDatabaseMaterial
                        {
                            Material = new CtrRef<fb.MeshMaterial>(
                                new GUID((string) s_MatRef["partition"]!),
                                new GUID((string) s_MatRef["instance"]!)),
                        };

                        var s_Texes = (Newtonsoft.Json.Linq.JArray?) s_SpecMat["textures"];
                        if (s_Texes != null)
                            foreach (var s_Tex in s_Texes)
                                s_New.TextureParameters.Add(new fb.TextureShaderParameter
                                {
                                    ParameterName = (string) s_Tex["name"]!,
                                    Value = new CtrRef<fb.TextureBaseAsset>(
                                        new GUID((string) s_Tex["partition"]!),
                                        new GUID((string) s_Tex["instance"]!)),
                                });

                        s_M.Materials.Add(s_New);
                    }
                }

                p_Writer.WriteLine($"MaterialsSpec: rebuilt {s_Matches.Count} entr(ies) with " +
                                   $"{s_SpecMats.Count} material(s) each.");
            }

            // ⛔ A REPOINTED ENTRY MUST NOT STILL REACH OUT OF THE BUNDLE IT RIDES IN. Repointing exists so
            // the database can load BEFORE the one that owns the mesh, and anything the entry still points
            // at outside the stand-in partition is then simply not loaded: the engine reads null. The mesh
            // and material refs are moved above, but an entry can also bind TEXTURES per material (a
            // vehicle's camo does; a prop whose textures are streamable, like a concrete barrier, binds
            // none). Counted here so the caller can refuse to ship one instead of paying a boot for it.
            if (!string.IsNullOrWhiteSpace(RepointFrom) && !string.IsNullOrWhiteSpace(RepointTo))
            {
                var s_Target = new GUID(RepointTo!);
                var s_Outside = s_SrcConcrete.InstanceMap.Values.OfType<fb.MeshVariationDatabaseEntry>()
                    .SelectMany(p_E => p_E.Materials)
                    .SelectMany(p_M => p_M.TextureParameters)
                    .Count(p_T => p_T?.Value != null && p_T.Value.PartitionGuid != s_Target &&
                                  p_T.Value.PartitionGuid != GUID.Empty);

                p_Writer.WriteLine($"Repointed entry binds {s_Outside} texture(s) outside the target partition.");
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
