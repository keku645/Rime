using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using fb;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Serialization;
using RimeLib.Serialization.Frostbite2_0.Ebx;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Lists the texture parameters bound PER MATERIAL for every mesh material whose surface " +
                        "shader matches a name substring, read from a MeshVariationDatabase partition. This is " +
                        "where texture bindings live when a shader's shaderdb entry lists few or no streamables: " +
                        "the game feeds those sampler registers per material instance (and per variation), not " +
                        "per shader. Complements dump_shader_textures, which only sees the shaderdb.")]
    public class DumpShaderMaterialTexturesCommand : Command
    {
        [CommandArgument(Description = "The MVDB partition name, e.g. levels/mp_017/mp_017/meshvariationdb_win32, " +
                                       "OR a level prefix like levels/mp_017 — the prefix form scans EVERY " +
                                       "sublevel database under it. That matters: the base sublevel only knows " +
                                       "static props, while soldier gear lives in the GAMEMODE sublevels' " +
                                       "databases, so a shader like CharacterRoot has zero materials in the base " +
                                       "database and its whole wardrobe in the gamemode ones.")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Surface shader name substring, e.g. SS_VentilationModule_01")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "Optional mesh partition name substring; skips loading meshes that do " +
                                       "not match, which turns a whole-level scan into a handful of loads. " +
                                       "Shaders and their meshes share a folder by convention, so the shader's " +
                                       "folder is the natural filter.", Optional = true)]
        public string? MeshFilter { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader))
            {
                p_Writer.WriteLine("Usage: dump_shader_material_textures <mvdb-partition> <shader-substring>");
                return false;
            }

            var s_Context = (GameContext)p_Context;
            var s_Mounter = s_Context.GetMounter();
            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(s_Mounter.GetEngineType());

            // A name that resolves is a single database; anything else is a level prefix expanded against the
            // mounted partition list. Both paths end as a list so the scan below is one code path.
            var s_DbNames = new List<string>();
            if (s_Mounter.TryGetPartition(Name!, out _))
            {
                s_DbNames.Add(Name!);
            }
            else
            {
                var s_Prefix = Name!.TrimEnd('/').ToLowerInvariant() + "/";
                s_DbNames.AddRange(s_Context.GetMountedPartitions()
                    .Where(p_Partition => p_Partition.ToLowerInvariant().StartsWith(s_Prefix) &&
                                          p_Partition.ToLowerInvariant().EndsWith("meshvariationdb_win32"))
                    .OrderBy(p_Partition => p_Partition, StringComparer.OrdinalIgnoreCase));

                if (s_DbNames.Count == 0)
                {
                    p_Writer.WriteLine($"Could not find MVDB partition ({Name}), and no sublevel databases " +
                                       "match it as a level prefix.");
                    return false;
                }

                p_Writer.WriteLine($"SHMATTEX-SCOPE: {s_DbNames.Count} sublevel database(s) under {Name}.");
            }

            var s_Needle = Shader!.ToLowerInvariant();
            var s_Matched = 0;
            var s_Lines = 0;

            // Mesh partitions repeat across MVDB entries (one entry per variation) and across sublevel
            // databases (every gamemode lists the same soldier gear), so each is parsed once — and identical
            // rows from sibling databases collapse to one.
            var s_Cache = new Dictionary<string, DatabasePartitionBase?>();
            var s_Emitted = new HashSet<string>();

            void Emit(string p_Line)
            {
                if (!s_Emitted.Add(p_Line))
                    return;
                p_Writer.WriteLine(p_Line);
                s_Lines++;
            }

            DatabasePartitionBase? LoadPartition(string p_Name)
            {
                if (s_Cache.TryGetValue(p_Name, out var s_Known))
                    return s_Known;

                DatabasePartitionBase? s_Loaded = null;
                if (s_Mounter.TryGetPartition(p_Name, out var s_Object))
                {
                    var s_Variant = s_Object.Variants.FirstOrDefault(v => v.GetContainedBundle() != null)
                                    ?? s_Object.FirstVariant;
                    if (s_Variant != null)
                        try { s_Loaded = s_Converter.FromPartitionObject(p_Name, s_Variant); }
                        catch { /* an unparseable mesh partition only costs its own materials */ }
                }

                s_Cache[p_Name] = s_Loaded;
                return s_Loaded;
            }

            string TexName(CtrRefBase p_Ref) =>
                s_Mounter.TryGetPartitionByGuid(p_Ref.PartitionGuid, out var s_TexPartition, out _)
                    ? s_TexPartition
                    : "(unresolved)";

            void Scan(string? p_MeshFilter)
            {
            foreach (var s_DbName in s_DbNames)
            {
            if (LoadPartition(s_DbName) is not { } s_Db)
                continue;

            foreach (var s_Instance in s_Db.Instances)
            {
                if (s_Instance is not MeshVariationDatabaseEntry s_Entry)
                    continue;

                foreach (var s_Material in s_Entry.Materials)
                {
                    // The MVDB material only points at a MeshMaterial by guid; the surface shader it uses is on
                    // that MeshMaterial, so the mesh partition has to be opened to filter by shader name.
                    if (!s_Mounter.TryGetPartitionByGuid(s_Material.Material.PartitionGuid, out var s_MeshName, out _))
                        continue;

                    if (!string.IsNullOrWhiteSpace(p_MeshFilter) &&
                        !s_MeshName.ToLowerInvariant().Contains(p_MeshFilter!.ToLowerInvariant()))
                        continue;

                    if (LoadPartition(s_MeshName) is not DatabasePartition s_MeshPartition)
                        continue;

                    if ((s_Material.Material.InstanceId as DataContainerId.Guid)?.Id is not { } s_InstanceGuid ||
                        !s_MeshPartition.InstanceMap.TryGetValue(s_InstanceGuid, out var s_Container) ||
                        s_Container is not MeshMaterial s_MeshMaterial)
                        continue;

                    if (!s_Mounter.TryGetPartitionByGuid(s_MeshMaterial.Shader.Shader.PartitionGuid,
                            out var s_ShaderName, out _) ||
                        !s_ShaderName.ToLowerInvariant().Contains(s_Needle))
                        continue;

                    s_Matched++;

                    // A human-readable label for the variation: the variation asset's partition name when there
                    // is one, "(base)" for the unvaried master (hash 0 carries a null variation ref).
                    var s_VariationName = s_Material.MaterialVariation.IsNull()
                        ? "(base)"
                        : s_Mounter.TryGetPartitionByGuid(s_Material.MaterialVariation.PartitionGuid,
                            out var s_VarName, out _)
                            ? s_VarName
                            : $"0x{s_Entry.VariationAssetNameHash:X8}";

                    // One line per matched (entry, material) even when the material binds no texture
                    // parameters — a streamable-textured prop's entry is exactly that, and the MESH name is
                    // what a variation bake needs to copy the entry into its own database.
                    Emit($"SHMATMESH: shader={s_ShaderName} mesh={s_MeshName} " +
                         $"variation={s_Entry.VariationAssetNameHash} variationName={s_VariationName}");

                    void Dump(string p_Source, List<TextureShaderParameter> p_Parameters)
                    {
                        foreach (var s_Parameter in p_Parameters)
                            Emit($"SHMATTEX: shader={s_ShaderName} mesh={s_MeshName} " +
                                 $"variation={s_Entry.VariationAssetNameHash} " +
                                 $"variationName={s_VariationName} source={p_Source} " +
                                 $"param={s_Parameter.ParameterName} texture={TexName(s_Parameter.Value)}");
                    }

                    void DumpVectors(string p_Source, List<VectorShaderParameter> p_Parameters)
                    {
                        foreach (var s_Parameter in p_Parameters)
                        {
                            var s_Value = s_Parameter.Value;
                            Emit(string.Create(CultureInfo.InvariantCulture,
                                $"SHMATVEC: shader={s_ShaderName} mesh={s_MeshName} " +
                                $"variation={s_Entry.VariationAssetNameHash} " +
                                $"variationName={s_VariationName} source={p_Source} " +
                                $"param={s_Parameter.ParameterName} " +
                                $"value={s_Value.x},{s_Value.y},{s_Value.z},{s_Value.w}"));
                        }
                    }

                    // Both places a texture can be bound: the material's own parameters, and the variation's
                    // overrides recorded in the MVDB entry itself. Vector parameters (the cb1 instance values)
                    // live on the material and on the VARIATION ASSET's own shader struct.
                    Dump("material", s_MeshMaterial.Shader.TextureParameters);
                    Dump("variation", s_Material.TextureParameters);
                    DumpVectors("material", s_MeshMaterial.Shader.VectorParameters);

                    if (!s_Material.MaterialVariation.IsNull() &&
                        s_Mounter.TryGetPartitionByGuid(s_Material.MaterialVariation.PartitionGuid,
                            out var s_VariationPartitionName, out _) &&
                        LoadPartition(s_VariationPartitionName) is DatabasePartition s_VariationPartition &&
                        (s_Material.MaterialVariation.InstanceId as DataContainerId.Guid)?.Id is { } s_VariationGuid &&
                        s_VariationPartition.InstanceMap.TryGetValue(s_VariationGuid, out var s_VariationContainer) &&
                        s_VariationContainer is MeshMaterialVariation s_MaterialVariation)
                    {
                        Dump("variationAsset", s_MaterialVariation.Shader.TextureParameters);
                        DumpVectors("variationAsset", s_MaterialVariation.Shader.VectorParameters);
                    }
                }
            }
            }

            }

            Scan(MeshFilter);

            // A SHARED shader (shaders/Root/...) lives in no mesh's folder, so the folder-convention filter can
            // exclude every mesh in the level. Zero matches with a filter on means the filter was wrong, not
            // that no material exists - retry unfiltered rather than reporting an empty truth.
            if (s_Matched == 0 && !string.IsNullOrWhiteSpace(MeshFilter))
            {
                p_Writer.WriteLine($"SHMATTEX-RETRY: mesh filter '{MeshFilter}' matched nothing; " +
                                   "scanning every mesh in the database.");
                Scan(null);
            }

            p_Writer.WriteLine($"SHMATTEX-DONE: {Name} '{Shader}' databases={s_DbNames.Count} " +
                               $"matchedMaterials={s_Matched} textures={s_Lines}");
            return s_Matched > 0;
        }
    }
}
