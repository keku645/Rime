using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Frostbite.Core;
using RimeLib.IO;
using RimeLib.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    [CommandDescription("Builds a MINIMAL MeshVariationDatabase from a SOURCE mvdb keeping ONLY the entries whose " +
                        "MESH name starts with one of the given comma-separated PREFIXES (e.g. 'characters/,weapons/,gameplay/'), " +
                        "and adds it as a NEW partition (override) under <target_name>. Use to keep a foreign gamemode's " +
                        "SOLDIER/character mesh->material bindings while dropping its LEVEL-art entries (whose meshes are not " +
                        "resident -> null-mesh crash in MeshVariationManager at load). Optional striptextures='1' clears each " +
                        "material's TextureParameters+MaterialVariation for a GREY render (no missing-character-texture crash).")]
    internal class MvdbKeepPrefixCommand : Command
    {
        [CommandArgument(Description = "Source MVDB partition name (has the entries), e.g. levels/mp_subway/teamdeathmatch/meshvariationdb_win32.")]
        public string? SourceName { get; set; }

        [CommandArgument(Description = "Target MVDB partition name to create (the new minimal mvdb the level will consult).")]
        public string? TargetName { get; set; }

        [CommandArgument(Description = "Id returned by mount_game.")]
        public int Id { get; set; }

        [CommandArgument(Description = "Comma-separated mesh-name PREFIXES to KEEP, e.g. 'characters/,weapons/,gameplay/'.")]
        public string? Prefixes { get; set; }

        [CommandArgument(Description = "Optional '1'/'true' = STRIP each kept entry's material TextureParameters + MaterialVariation (grey render, no missing-texture crash).", Optional = true)]
        public string? StripTextures { get; set; }

        [CommandArgument(Description = "Optional '1'/'true' = for the SECOND source only, keep ONLY entries whose MESH partition is PRESENT in this bundle (resident). Avoids the null-mesh load crash (sub_17402D0) for 2nd-source entries whose mesh is NOT resolved into the bundle — needed when the 2nd source (the MAIN level mvdb for fx/ mesh-particles) has more entries than the meshes actually shipped. The PRIMARY source is kept in full (it is the trusted gamemode mvdb).", Optional = true)]
        public string? RequireInBundle { get; set; }

        [CommandArgument(Description = "Optional: a SECOND source MVDB whose matching entries are MERGED into the SAME target mvdb (one indexed database). The MeshVariationManager appears to ingest only ONE mvdb per bundle, so a separate 2nd mvdb loads but its entries are never indexed -> unbound mesh. Use to fold the MAIN level mvdb's fx/ mesh-particle entries into the gamemode's characters/weapons mvdb.", Optional = true)]
        public string? SecondSource { get; set; }

        [CommandArgument(Description = "Comma-separated mesh-name PREFIXES to keep from SecondSource (e.g. 'fx/'). Same RequireInBundle filter applies. Required if SecondSource is set.", Optional = true)]
        public string? SecondPrefixes { get; set; }

        [CommandArgument(Description = "Optional comma-separated mesh-name PREFIXES that are EXEMPT from the RequireInBundle check, e.g. 'levels/x/messscattering/', while RequireInBundle keeps protecting the other prefixes. " +
            "⚠ USE WITH CARE: an exempt entry whose mesh is NOT in this bundle is a LOAD-TIME null-mesh crash (sub_17402D0) - the variation loader resolves the mesh when ITS bundle loads, so 'the mesh-variation index is global' does NOT make it safe (that only holds for the runtime lookup). " +
            "Measured 2026-08-07: exempting fx/meshparticles/weapons/ so its entries could sit in the MAIN mvdb, while those meshes live in the gamemode bundle, crashed the client before the UI. " +
            "Only exempt a prefix whose meshes reach THIS bundle by some other route; otherwise move the meshes here too (entry and mesh belong in the SAME bundle).", Optional = true)]
        public string? SecondExemptPrefixes { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(SourceName) || string.IsNullOrWhiteSpace(TargetName) || string.IsNullOrWhiteSpace(Prefixes))
            {
                p_Writer.WriteLine("source_name, target_name and prefixes are required.");
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

            var s_Prefixes = Prefixes!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Select(p_P => p_P.ToLowerInvariant()).ToArray();

            bool s_ReqInBundle = !string.IsNullOrWhiteSpace(RequireInBundle)
                                 && (RequireInBundle == "1" || RequireInBundle!.ToLowerInvariant() == "true");
            var s_InBundle = s_ReqInBundle
                ? new HashSet<string>(s_BundleContext.GetPartitions().Select(p_P => p_P.Key), StringComparer.OrdinalIgnoreCase)
                : null;

            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(s_SbBuildingContext.EngineType);
            var s_Generator = EngineInterfaceRegistry.Create<IPartitionGenerator>(s_SbBuildingContext.EngineType);

            if (!s_EngineMounter.TryGetPartition(SourceName!, out var s_SrcMounted))
            {
                p_Writer.WriteLine($"Could not find source partition ({SourceName}).");
                return false;
            }
            var s_SrcVariant = s_SrcMounted.Variants.FirstOrDefault(p_V => p_V.GetContainedBundle() != null) ?? s_SrcMounted.FirstVariant;
            var s_SrcDb = s_Converter.FromPartitionObject(SourceName!, s_SrcVariant!);

            // Select entries whose MESH name starts with one of the prefixes.
            var s_Keep = new List<(fb.MeshVariationDatabaseEntry Entry, GUID Id)>();
            int s_EntryCount = 0;
            foreach (var s_Inst in s_SrcDb.Instances)
            {
                if (s_Inst is not fb.MeshVariationDatabaseEntry s_E) continue;
                s_EntryCount++;
                if (!s_EngineMounter.TryGetPartitionByGuid(s_E.Mesh.PartitionGuid, out var s_MName, out _) || s_MName == null)
                    continue;
                var s_Lower = s_MName.ToLowerInvariant();
                if (!s_Prefixes.Any(p_P => s_Lower.StartsWith(p_P)))
                    continue;
                if (s_E.InstanceId is not DataContainerId.Guid s_IdG)
                    continue;
                s_Keep.Add((s_E, s_IdG.Id));
            }
            if (s_Keep.Count == 0)
            {
                p_Writer.WriteLine($"No MVDB entry matched the prefixes in '{SourceName}'. ({s_EntryCount} entry instance(s) scanned).");
                return false;
            }

            var s_SrcConcrete = (RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition)s_SrcDb;
            var s_SrcMvdb = (fb.MeshVariationDatabase)s_SrcConcrete.PrimaryInstance;

            // keep ONLY the primary MVDB instance + the matched entries; drop every other instance
            var s_PrimG = s_SrcConcrete.PrimaryInstanceGuid;
            var s_KeepSet = new HashSet<GUID>(s_Keep.Select(p_E => p_E.Id));
            var s_ToRemove = s_SrcConcrete.InstanceMap.Keys
                .Where(p_K => p_K.CompareTo(s_PrimG) != 0 && !s_KeepSet.Contains(p_K))
                .ToList();
            foreach (var s_K in s_ToRemove)
                s_SrcConcrete.InstanceMap.Remove(s_K);

            s_SrcMvdb.Entries.Clear();
            foreach (var s_E in s_Keep)
                s_SrcMvdb.Entries.AddRef(new CtrRef<fb.MeshVariationDatabaseEntry>(s_SrcConcrete.PartitionGuid, s_E.Entry.InstanceId));
            s_SrcMvdb.RedirectEntries.Clear();

            // MERGE a SECOND source mvdb's matching entries INTO this SAME target (one indexed database).
            // The MeshVariationManager ingests only ONE mvdb per bundle, so a separate 2nd mvdb loads but is
            // never indexed -> unbound mesh. Copy the 2nd source's entry instances into this partition's
            // InstanceMap and add them to the Entries list (their Mesh/Material CtrRefs are external imports,
            // collected by Generate). StripTextures below clears their MaterialVariation so no dangling import.
            var s_Keep2 = new List<(fb.MeshVariationDatabaseEntry Entry, GUID Id)>();
            if (!string.IsNullOrWhiteSpace(SecondSource))
            {
                if (string.IsNullOrWhiteSpace(SecondPrefixes))
                {
                    p_Writer.WriteLine("second_prefixes is required when second_source is set.");
                    return false;
                }
                var s_Prefixes2 = SecondPrefixes!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                 .Select(p_P => p_P.ToLowerInvariant()).ToArray();
                var s_Exempt2 = string.IsNullOrWhiteSpace(SecondExemptPrefixes)
                    ? System.Array.Empty<string>()
                    : SecondExemptPrefixes!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                           .Select(p_P => p_P.ToLowerInvariant()).ToArray();
                if (!s_EngineMounter.TryGetPartition(SecondSource!, out var s_Src2Mounted))
                {
                    p_Writer.WriteLine($"Could not find second source partition ({SecondSource}).");
                    return false;
                }
                var s_Src2Variant = s_Src2Mounted.Variants.FirstOrDefault(p_V => p_V.GetContainedBundle() != null) ?? s_Src2Mounted.FirstVariant;
                var s_Src2Db = s_Converter.FromPartitionObject(SecondSource!, s_Src2Variant!);
                var s_Src2Concrete = (RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition)s_Src2Db;
                foreach (var s_Inst in s_Src2Db.Instances)
                {
                    if (s_Inst is not fb.MeshVariationDatabaseEntry s_E) continue;
                    if (!s_EngineMounter.TryGetPartitionByGuid(s_E.Mesh.PartitionGuid, out var s_MName, out _) || s_MName == null)
                        continue;
                    var s_Lower = s_MName.ToLowerInvariant();
                    if (!s_Prefixes2.Any(p_P => s_Lower.StartsWith(p_P)))
                        continue;
                    // Exempt prefixes opt out of the in-bundle check while it keeps protecting
                    // everything else. NOTE (measured, do not "optimise" this away): being exempt is
                    // only safe if the mesh still reaches THIS bundle somehow - the variation loader
                    // resolves it when this bundle loads, so a missing mesh is a null-deref at LOAD
                    // time (sub_17402D0), not a silently unbound material. The global mesh-variation
                    // index only saves the runtime lookup, not the load.
                    if (s_ReqInBundle
                        && !s_Exempt2.Any(p_P => s_Lower.StartsWith(p_P))
                        && !s_InBundle!.Contains(s_MName))
                        continue;
                    if (s_E.InstanceId is not DataContainerId.Guid s_IdG)
                        continue;
                    if (s_SrcConcrete.InstanceMap.ContainsKey(s_IdG.Id))
                        continue; // instance-guid collision with the primary: skip
                    s_SrcConcrete.InstanceMap[s_IdG.Id] = s_Src2Concrete.InstanceMap[s_IdG.Id];
                    s_Keep2.Add((s_E, s_IdG.Id));
                }
                foreach (var s_E in s_Keep2)
                    s_SrcMvdb.Entries.AddRef(new CtrRef<fb.MeshVariationDatabaseEntry>(s_SrcConcrete.PartitionGuid, s_E.Entry.InstanceId));
                p_Writer.WriteLine($"Merged {s_Keep2.Count} entr(ies) from second source '{SecondSource}' (prefixes: {string.Join(",", s_Prefixes2)}).");
            }

            // Strip ONLY the PRIMARY entries (char/weapon: gray render, no missing-texture crash). The SECOND
            // source (fx mesh-particles) is kept INTACT: its shader is selected via the MaterialVariation, so
            // clearing it (as striptextures does) leaves the emitter mesh-batch renderable with a NULL shader
            // instance (v20[3]) -> vu+0x146aeae crash when the mesh-particle is drawn (firing). Keep it.
            int s_Stripped = 0;
            if (!string.IsNullOrWhiteSpace(StripTextures) && (StripTextures == "1" || StripTextures!.ToLowerInvariant() == "true"))
            {
                foreach (var s_E in s_Keep)
                    foreach (var s_Mat in s_E.Entry.Materials)
                    {
                        s_Stripped += s_Mat.TextureParameters.Count;
                        s_Mat.TextureParameters.Clear();
                        s_Mat.MaterialVariation.SetValue(GUID.Empty, new DataContainerId.Guid(GUID.Empty));
                    }
            }

            var s_Stream = new MemoryStream();
            using var s_ResWriter = new RimeWriter(s_Stream);
            s_Generator.Generate(s_SrcDb, s_ResWriter);
            var s_Bytes = s_Stream.ToArray();

            s_BundleContext.AddRawPartitionBytes(TargetName!, s_Bytes);

            p_Writer.WriteLine($"Built minimal MVDB '{TargetName}' with {s_Keep.Count}+{s_Keep2.Count}={s_Keep.Count + s_Keep2.Count} entries " +
                               $"(prefixes: {string.Join(",", s_Prefixes)}; stripped {s_Stripped} texture param(s); {s_Bytes.Length} bytes).");
            foreach (var s_E in s_Keep.Concat(s_Keep2))
                if (s_EngineMounter.TryGetPartitionByGuid(s_E.Entry.Mesh.PartitionGuid, out var s_KMName, out _) && s_KMName != null)
                    p_Writer.WriteLine($"  MVDB-KEPT: {s_KMName}");
            return true;
        }
    }
}
