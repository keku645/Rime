using RimeLib.IO;
using RimeLib.Shader.Frostbite2_0.Frostbite.Shaders;
using RimeLib.Shader.Frostbite2_0.Frostbite.Solutions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using fb;
using RimeLib.Content.Mounting;
using RimeLib.Frostbite;
using RimeLib.Mesh.Frostbite;
using RimeLib.Serialization;
using RimeLib.Shader.Frostbite2_0.Frostbite.Functions;

namespace RimeLib.Shader.Frostbite2_0.Frostbite;

public class ShaderDatabase
{
    public ShaderRenderPath RenderPath { get; set; }
    public Dictionary<string, SurfaceShaderInfo> Shaders { get; set; } = new();
    public ShaderConstant[] Constants { get; private set; } = [];

    // Raw arrays retained in read order so the writer can re-emit them byte-identically (permutations reference
    // Constant/ConstantFunction/TextureFunction BY INDEX into these, so their order must be preserved on write).
    public ShaderConstantFunctionData[] ConstantFunctions { get; private set; } = [];
    public ShaderTextureFunctionData[] TextureFunctions { get; private set; } = [];
    public VertexShaderPermutation[] VertexPermutations { get; private set; } = [];
    public PixelShaderPermutation[] PixelPermutations { get; private set; } = [];
    public GeometryShaderPermutation[] GeometryPermutations { get; private set; } = [];
    public ShaderSolution[] Solutions { get; private set; } = [];

    // Geometry declaration table in read order (u32 hash + GeometryDeclarationDesc). The reader also fans each desc
    // out to the solutions that reference its hash; retained here so the writer can re-emit the table byte-identically.
    public List<(uint Hash, GeometryDeclarationDesc Desc)> Declarations { get; private set; } = new();

    // Shader table in read order: (u32 asset-name-hash key, SurfaceShaderInfo). The Shaders dictionary is keyed by
    // resolved asset name and loses both the on-disk key and the order, so the writer uses this list instead.
    public List<(uint Key, SurfaceShaderInfo Info)> ShaderEntries { get; private set; } = new();

    public ShaderDatabase()
    {
    }

    public ShaderDatabase(RimeReader p_Reader, IEngineMounter p_Mounter)
    {
        var s_Version = p_Reader.ReadUInt32();

        if (s_Version != 182)
            throw new Exception($"Unsupported shader database version (expected 182 got {s_Version}).");

        RenderPath = (ShaderRenderPath)p_Reader.ReadUInt32();

        //

        var s_ConstantsCount = p_Reader.ReadUInt32();
        Constants = new ShaderConstant[s_ConstantsCount];

        for (var i = 0; i < s_ConstantsCount; i++)
        {
            //Size object included in size...
            var s_Size = p_Reader.ReadUInt32() - 4;

            var s_CurrentPosition = p_Reader.Position;

            using (var s_ConstantsReader = new LimitedRimeReader(p_Reader, s_Size))
                Constants[i] = new ShaderConstant(s_ConstantsReader);

            // Retain the raw payload (everything after the size prefix) so the writer can re-emit this offset-driven
            // record byte-identically without having to re-plan its internal offsets.
            p_Reader.Seek(s_CurrentPosition, SeekOrigin.Begin);
            Constants[i].RawBytes = p_Reader.ReadBytes((int) s_Size);

            p_Reader.Seek(s_CurrentPosition + s_Size, SeekOrigin.Begin);
        }

        //

        var s_ConstantFunctionsCount = p_Reader.ReadUInt32();
        var s_ConstantFunctions = new ShaderConstantFunctionData[s_ConstantFunctionsCount];

        for (var i = 0; i < s_ConstantFunctionsCount; i++)
            s_ConstantFunctions[i] = new ShaderConstantFunctionData(p_Reader);

        //

        var s_TextureFunctionCount = p_Reader.ReadUInt32();
        var s_TextureFunctions = new ShaderTextureFunctionData[s_TextureFunctionCount];

        for (var i = 0; i < s_TextureFunctionCount; i++)
            s_TextureFunctions[i] = new ShaderTextureFunctionData(p_Reader);

        //

        var s_VertexShaderPermutationsCount = p_Reader.ReadUInt32();
        var s_VertexShaderPermutations = new VertexShaderPermutation[s_VertexShaderPermutationsCount];

        for (var i = 0; i < s_VertexShaderPermutationsCount; i++)
        {
            s_VertexShaderPermutations[i] = new VertexShaderPermutation(
                p_Reader,
                Constants,
                s_ConstantFunctions,
                s_TextureFunctions
            );
        }

        //
        
        var s_PixelShaderPermutationsCount = p_Reader.ReadUInt32();
        var s_PixelShaderPermutations = new PixelShaderPermutation[s_PixelShaderPermutationsCount];

        for (var i = 0; i < s_PixelShaderPermutationsCount; i++)
        {
            s_PixelShaderPermutations[i] = new PixelShaderPermutation(
                p_Reader,
                Constants,
                s_ConstantFunctions,
                s_TextureFunctions
            );
        }

        //
       
        var s_GeometryShaderPermutationsCount = p_Reader.ReadUInt32();

        var s_GeometryShaderPermutations = new GeometryShaderPermutation[s_GeometryShaderPermutationsCount];

        for (var i = 0; i < s_GeometryShaderPermutationsCount; i++)
            s_GeometryShaderPermutations[i] = new GeometryShaderPermutation(p_Reader);

        //
        
        var s_SolutionCount = p_Reader.ReadUInt32();
        var s_Solutions = new ShaderSolution[s_SolutionCount];

        for (var i = 0; i < s_SolutionCount; i++)
        {
            s_Solutions[i] = new ShaderSolution(
                p_Reader,
                s_VertexShaderPermutations,
                s_PixelShaderPermutations,
                s_GeometryShaderPermutations,
                Constants
            );
        }

        //

        var s_SolutionStateCount = p_Reader.ReadUInt32();
        
        if (s_SolutionStateCount != s_SolutionCount)
            throw new Exception($"Solution state count doesn't match solution count (expected {s_SolutionCount} got {s_SolutionStateCount}). Is this shader database corrupted?");


        for (var i = 0; i < s_SolutionStateCount; i++)
        {
            var s_SolutionState = new ShaderSolutionState(p_Reader);

            if (s_Solutions[i].StateHash != s_SolutionState.Hash)
                throw new Exception($"Solution state hash doesnt match solution hash. (expected 0x{s_Solutions[i].StateHash:X016} got 0x{s_SolutionState.Hash:X016}). Is this shader database corrupted?");

            s_Solutions[i].State = s_SolutionState;
        }

        // Retain the raw arrays (read order) for byte-identical writing.
        ConstantFunctions   = s_ConstantFunctions;
        TextureFunctions    = s_TextureFunctions;
        VertexPermutations  = s_VertexShaderPermutations;
        PixelPermutations   = s_PixelShaderPermutations;
        GeometryPermutations = s_GeometryShaderPermutations;
        Solutions           = s_Solutions;

        //

        var s_DeclarationCount = p_Reader.ReadUInt32();
            
        for (var i = 0; i < s_DeclarationCount; i++)
        {
            var s_Hash = p_Reader.ReadUInt32();
            var s_Desc = new GeometryDeclarationDesc(p_Reader);

            Declarations.Add((s_Hash, s_Desc));

            foreach (var s_Solution in s_Solutions)
                if (s_Solution.State.GeometryDeclarationHash == s_Hash)
                    s_Solution.State.GeometryDeclarationDesc = s_Desc;
        }

        //
        
        var s_ShaderCount = p_Reader.ReadUInt32();

        for (var i = 0; i < s_ShaderCount; i++)
        {
            var s_Key = p_Reader.ReadUInt32();

            // A key with no mounted partition is NOT corruption: a FRESH-NAMED entry (a cloned shader for a
            // custom variation) hashes a name that exists only in the mod's own bundle. The info still has to
            // be CONSUMED from the stream either way — throwing here made every re-parse of a patched .bin
            // (command chaining, verification) explode on its own output.
            if (!p_Mounter.TryGetPartitionByHashLower(s_Key, out var s_PartitionObject))
            {
                var s_Fresh = new SurfaceShaderInfo(p_Reader, s_Solutions, s_Key);
                Shaders.Add($"__unresolved_0x{s_Key:x8}", s_Fresh);
                ShaderEntries.Add((s_Key, s_Fresh));
                continue;
            }

            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(EngineType.Frostbite2_0);
            var s_Partition = s_Converter.FromPartitionObject(s_PartitionObject.OriginalName, s_PartitionObject.FirstVariant);

            if (s_Partition.PrimaryInstance is not SurfaceShaderBaseAsset s_Asset)
                throw new Exception($"Primary instance of shader asset partition '{s_Partition.Name}' is not a SurfaceShaderBaseAsset.");

            var s_Info = new SurfaceShaderInfo(p_Reader, s_Solutions, RimeLib.Frostbite.Utils.HashQuick(s_Asset.Name));
            Shaders.Add(s_Asset.Name, s_Info);
            ShaderEntries.Add((s_Key, s_Info));
        }
    }

    /// <summary>
    /// Clones a shader's entry under a FRESH NAME — the mechanism behind per-instance custom variations: the
    /// vanilla entry stays untouched, and a material whose shader asset carries the new name resolves the
    /// clone (the engine keys entries by FNV of the lowercased asset name). Only the solutions of the given
    /// MODE are deep-cloned (solution + pixel permutation + BOTH constants, so later per-clone surgery —
    /// bytecode, texture slots — can never leak into shaders sharing the originals); everything else is
    /// shared by reference exactly like sibling vanilla entries share. Passing bytecode patches the cloned
    /// GBuffer permutations in the same step. Returns a report line ("ERROR: ..." on failure).
    /// </summary>
    public string CloneShaderEntry(string p_SourceName, string p_NewName, uint p_NewKey,
        byte[]? p_NewBytecode, string? p_ModeFilter, byte[]? p_ReferenceOriginal = null,
        List<(byte[] Ref, byte[] New)>? p_Patches = null, ShaderDatabase? p_SourceDb = null,
        uint? p_NewDeclHash = null, uint? p_FromDeclHash = null)
    {
        // The source may live in ANOTHER database (a merge transplanting one mod's addition into another
        // mod's database). Every by-index reference must then be re-pointed at THIS container's tables:
        // the serializer resolves permutations and functions via Array.IndexOf into its own arrays, and an
        // object belonging to the source container simply is not there — the write dies or, worse, lands on
        // index -1. Both databases descend from the same level database, so the shared tables are equal
        // element-for-element and the remap is by INDEX.
        var s_From = p_SourceDb ?? this;
        var s_RemapFailed = (string?) null;

        T? Remap<T>(T? p_Object, T[] p_Mine, T[] p_Theirs, string p_What) where T : class
        {
            if (p_Object == null || ReferenceEquals(s_From, this))
                return p_Object;

            var s_Index = Array.IndexOf(p_Theirs, p_Object);
            if (s_Index >= 0 && s_Index < p_Mine.Length)
                return p_Mine[s_Index];

            s_RemapFailed ??= p_What;
            return null;
        }

        string? s_SourceKey = null;
        SurfaceShaderInfo? s_Source = null;
        foreach (var (s_Name, s_Info) in s_From.Shaders)
            if (s_Name.Equals(p_SourceName, StringComparison.OrdinalIgnoreCase))
            {
                s_SourceKey = s_Name;
                s_Source = s_Info;
                break;
            }

        if (s_Source == null)
            return $"ERROR: no shader named '{p_SourceName}' in this database";

        foreach (var s_Name in Shaders.Keys)
            if (s_Name.Equals(p_NewName, StringComparison.OrdinalIgnoreCase))
                return $"ERROR: '{p_NewName}' already exists in this database";

        var s_NewSolutions = new List<ShaderSolution>();
        var s_AppendSolutions = new List<ShaderSolution>();
        var s_AppendPixels = new List<PixelShaderPermutation>();
        var s_AppendConstants = new List<ShaderConstant>();
        var s_PixelClones = new Dictionary<PixelShaderPermutation, PixelShaderPermutation>();
        var s_ConstantClones = new Dictionary<ShaderConstant, ShaderConstant>();

        ShaderConstant? CloneConstant(ShaderConstant? p_Constant)
        {
            if (p_Constant == null)
                return null;

            if (s_ConstantClones.TryGetValue(p_Constant, out var s_Known))
                return s_Known;

            var s_Clone = new ShaderConstant
            {
                ConstantCount = p_Constant.ConstantCount,
                ValueConstantsStart = p_Constant.ValueConstantsStart,
                ValueConstants = p_Constant.ValueConstants,
                Textures = (ShaderConstants.TextureConstant[]) p_Constant.Textures.Clone(),
                ExternalValues = p_Constant.ExternalValues,
                ExternalTextures = p_Constant.ExternalTextures,
                Samplers = p_Constant.Samplers,
                RawBytes = (byte[]?) p_Constant.RawBytes?.Clone(),
            };

            s_ConstantClones[p_Constant] = s_Clone;
            s_AppendConstants.Add(s_Clone);
            return s_Clone;
        }

        var s_Patched = 0;
        var s_Cloned = 0;

        foreach (var s_Solution in s_Source.Solutions)
        {
            var s_Mode = s_Solution.State?.Mode.ToString() ?? "";
            var s_IsTarget = string.IsNullOrWhiteSpace(p_ModeFilter) ||
                             s_Mode.Contains(p_ModeFilter, StringComparison.OrdinalIgnoreCase);

            // EVERY mode's solutions are cloned and re-keyed — a solution merely SHARED keeps a state that
            // answers to the SOURCE's key, so the clone never owns a depth/shadow pass under its own name
            // and any runtime path that wants the full set walks into the source's. The MODE filter only
            // decides which permutations get the AUTHORED bytecode: a GBuffer pixel shader patched into a
            // ZOnly permutation would be garbage, so off-mode clones keep their vanilla bytes.
            PixelShaderPermutation? s_PixelClone = null;
            if (s_Solution.PixelPermutation is { } s_PixelSource)
            {
                if (!s_PixelClones.TryGetValue(s_PixelSource, out s_PixelClone))
                {
                    // A mode has SEVERAL pixel variants (probe-lit, instanced…), each with its own extra
                    // code and inputs; the authored shader is equivalent to exactly ONE of them — the
                    // reference it was translated/authored against. Patching it into every variant is what
                    // made probe-lit objects lose their ambient term and instanced batches shade garbage:
                    // the other variants must keep their own vanilla bytes. No reference given = old
                    // behaviour (patch the whole mode).
                    byte[]? s_Patch = null;
                    if (p_Patches != null)
                    {
                        // Per-variant patching: each pair maps ONE vanilla variant's exact bytes to the
                        // bytecode authored against THAT variant's contract. Unmatched variants stay vanilla.
                        foreach (var (s_Ref, s_New) in p_Patches)
                            if (s_PixelSource.ShaderBytecode.AsSpan().SequenceEqual(s_Ref))
                            {
                                s_Patch = s_New;
                                break;
                            }
                    }
                    else if (p_ReferenceOriginal == null ||
                             s_PixelSource.ShaderBytecode.AsSpan().SequenceEqual(p_ReferenceOriginal))
                    {
                        s_Patch = p_NewBytecode;
                    }

                    var s_Bytecode = s_IsTarget && s_Patch != null ? s_Patch : s_PixelSource.ShaderBytecode;

                    s_PixelClone = new PixelShaderPermutation
                    {
                        Guid = new RimeLib.Frostbite.Core.GUID(Guid.NewGuid()),
                        ShaderBytecode = s_Bytecode,
                        Constant = CloneConstant(s_PixelSource.Constant)!,
                        ConstantFunction = Remap(s_PixelSource.ConstantFunction, ConstantFunctions,
                            s_From.ConstantFunctions, "pixel constant function"),
                        TextureFunction = Remap(s_PixelSource.TextureFunction, TextureFunctions,
                            s_From.TextureFunctions, "pixel texture function"),
                        // The record's instruction count must describe the bytes it rides with, so a patch
                        // recomputes it from the compiler's own STAT chunk instead of keeping the source's.
                        InstructionCount = ReferenceEquals(s_Bytecode, s_PixelSource.ShaderBytecode)
                            ? s_PixelSource.InstructionCount
                            : InstructionCountOf(s_Bytecode, s_PixelSource.InstructionCount),
                    };

                    s_PixelClones[s_PixelSource] = s_PixelClone;
                    s_AppendPixels.Add(s_PixelClone);
                    if (!ReferenceEquals(s_Bytecode, s_PixelSource.ShaderBytecode))
                        s_Patched++;
                }
            }

            // The solution STATE embeds the shader's name hash as its FIRST field, and the runtime selects a
            // solution by matching the whole state — drawing the clone asks for (cloneKey, decl, mode, …).
            // A cloned solution sharing the source's state still answers to the SOURCE key, so the clone's
            // entry is found but no solution ever matches it: the mesh silently does not draw. Re-key a copy
            // of the state and recompute its hash (the reader validates solution.StateHash == state.Hash).
            var s_StateClone = CloneStateForKey(s_Solution.State, p_NewKey, p_NewDeclHash, p_FromDeclHash);

            var s_SolutionClone = new ShaderSolution
            {
                StateHash = s_StateClone.Hash,
                State = s_StateClone,
                Flags = s_Solution.Flags,
                SurfaceType = s_Solution.SurfaceType,
                BlendMode = s_Solution.BlendMode,
                ExtraData = s_Solution.ExtraData,
                VertexPermutation = Remap(s_Solution.VertexPermutation, VertexPermutations,
                    s_From.VertexPermutations, "vertex permutation"),
                PixelPermutation = s_PixelClone,
                GeometryPermutation = Remap(s_Solution.GeometryPermutation, GeometryPermutations,
                    s_From.GeometryPermutations, "geometry permutation"),
                // BOTH constants go through the same clone map so the source's IDENTITY TOPOLOGY survives:
                // vanilla solutions hold ONE object in both slots, and the runtime relies on that identity —
                // cloning only the pixel side split the pair, so the engine wrote per-object data (ambient
                // probes, instance batches) into one object while the shader bound the other. Seen live in a
                // dump: vanilla solution +0x30==+0x38, cloned solution +0x30!=+0x38, black-in-shadow barrels.
                VertexConstants = CloneConstant(s_Solution.VertexConstants),
                PixelConstants = CloneConstant(s_Solution.PixelConstants),
            };

            s_AppendSolutions.Add(s_SolutionClone);
            s_NewSolutions.Add(s_SolutionClone);
            s_Cloned++;
        }

        // Nothing was committed yet: a cross-database reference that could not be re-pointed aborts the
        // whole clone instead of serializing a dangling index.
        if (s_RemapFailed != null)
            return $"ERROR: cross-database transplant of '{p_SourceName}' could not remap a {s_RemapFailed} " +
                   "— the databases do not share the same level tables";

        var s_NewInfo = new SurfaceShaderInfo
        {
            SurfaceShaderType = s_Source.SurfaceShaderType,
            Flags = s_Source.Flags,
            BoolParameterCount = s_Source.BoolParameterCount,
            BoolParameterDefaultMask = s_Source.BoolParameterDefaultMask,
            BoolParameterRequiredMask = s_Source.BoolParameterRequiredMask,
            BoolParameterIds = (uint[]) s_Source.BoolParameterIds.Clone(),
            StreamableTextures = s_Source.StreamableTextures
                .Select(p_T => new SurfaceShaderInfo.StreamableTexture
                {
                    Name = p_T.Name, CoordType = p_T.CoordType, VertexUsage = p_T.VertexUsage, Factor = p_T.Factor,
                })
                .ToArray(),
            StreamableExternalTextures = s_Source.StreamableExternalTextures,
            Solutions = s_NewSolutions.ToArray(),
            NameHash = RimeLib.Frostbite.Utils.HashQuick(p_NewName),
        };

        foreach (var s_Solution in s_NewInfo.Solutions)
            s_NewInfo.SolutionMap.TryAdd(s_Solution.StateHash, s_Solution);

        Solutions = Solutions.Concat(s_AppendSolutions).ToArray();

        // The pixel-permutation section is stored SORTED by the guid's serialized bytes, and the engine
        // resolves guid -> permutation by binary search: a fresh record appended at the end is simply never
        // found — the solution draws nothing and the mesh is invisible, with no error anywhere. Measured on
        // the level database: 1517 records, strictly ascending; every historical fresh-named clone that
        // shipped un-sorted came out invisible. Solutions and constants are referenced by index, so their
        // appends are order-free; only this array re-sorts (a no-op for the already-sorted originals, and
        // every index-based reference is recomputed at serialize time from this same list).
        PixelPermutations = PixelPermutations.Concat(s_AppendPixels)
            .OrderBy(p_P => p_P.Guid.Id, ByteArrayComparer.Instance)
            .ToArray();

        Constants = Constants.Concat(s_AppendConstants).ToArray();
        Shaders.Add(p_NewName, s_NewInfo);
        ShaderEntries.Add((p_NewKey, s_NewInfo));

        // The patched COUNT is always reported: a manifest whose reference bytes match nothing would
        // otherwise clone silently with every permutation left vanilla — a working-looking, wrong bake.
        return $"cloned '{s_SourceKey}' -> '{p_NewName}' (key 0x{p_NewKey:x8}): {s_Cloned} solution(s) deep-cloned " +
               $"({s_AppendPixels.Count} pixel permutation(s), {s_Patched} patched, " +
               $"{s_AppendConstants.Count} constant(s)), {s_NewSolutions.Count - s_Cloned} shared outside the mode";
    }

    /// <summary>
    /// Merges another mod's database (same level, same render path) into this one, using the VANILLA
    /// database to tell what that mod actually changed. Entries under names vanilla does not have
    /// (variation clones) transplant whole; a vanilla-named entry whose pixel bytecode differs from vanilla
    /// carries those bytes over spot by spot. Anywhere BOTH mods carry a DIFFERENT change for the same spot
    /// is a conflict and the merge refuses loudly — silently letting one mod eat the other is the failure
    /// mode this merge exists to end.
    /// </summary>
    /// <summary>
    /// Builds a NEW database holding ONLY the named shaders, with every shared table trimmed to what those
    /// entries actually reach.
    ///
    /// No deep copy and no index arithmetic: the writer resolves permutations, constants and functions by
    /// Array.IndexOf into THIS database's own tables, so keeping the same objects and emitting them in the
    /// source's order is what re-bases the references. Slicing every shader therefore reproduces the source.
    ///
    /// ⛔ The geometry declaration table travels WHOLE on purpose: solutions reference a declaration by HASH
    /// (the reader fans each desc out to them), not by index, so a trimmed table risks a solution whose decl
    /// hash no longer resolves — and a solution whose (shader × vertex-decl) pair has no entry does not draw.
    /// It is metadata-sized; there is no space here worth that failure mode.
    /// </summary>
    /// <summary>
    /// The database's own key for a shader name: djb2 with XOR mixing over the name in LOWERCASE
    /// (h = 0x1505; h = (h * 0x21) ^ c). Verified against a clone the surgery had just written.
    /// </summary>
    public static uint KeyOf(string p_Name)
    {
        var s_Hash = 0x1505u;
        foreach (var s_Char in p_Name.ToLowerInvariant())
            s_Hash = (s_Hash * 0x21) ^ s_Char;

        return s_Hash;
    }

    public ShaderDatabase Slice(IEnumerable<string> p_Names, out List<string> p_Missing)
    {
        p_Missing = new List<string>();

        // Reference identity, never value: two permutations can hold byte-identical bytecode and still be
        // distinct entries that other solutions point at.
        var s_Reached = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var s_Infos = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var s_Slice = new ShaderDatabase { RenderPath = RenderPath };

        void Reach(object? p_Item)
        {
            if (p_Item != null)
                s_Reached.Add(p_Item);
        }

        foreach (var s_Name in p_Names)
        {
            var s_Pair = Shaders.FirstOrDefault(p_P => p_P.Key.Equals(s_Name, StringComparison.OrdinalIgnoreCase));
            var s_Info = s_Pair.Value;

            // ⛔ A database stores the KEY (a hash of the name), never the name itself: the reader resolves
            // names against the mounted catalogue, so an entry a mod just created — whose name the game has
            // never heard of — reads back nameless and no by-name lookup can ever find it. Its own key still
            // matches, and that is exactly the entry an additive database is made of.
            if (s_Info == null)
            {
                var s_Key = KeyOf(s_Name);
                foreach (var (s_EntryKey, s_EntryInfo) in ShaderEntries)
                    if (s_EntryKey == s_Key)
                    {
                        s_Info = s_EntryInfo;
                        break;
                    }
            }

            if (s_Info == null)
            {
                p_Missing.Add(s_Name);
                continue;
            }

            s_Infos.Add(s_Info);
            s_Slice.Shaders[s_Pair.Key ?? s_Name] = s_Info;

            foreach (var s_Solution in s_Info.Solutions)
            {
                Reach(s_Solution);
                Reach(s_Solution.VertexPermutation);
                Reach(s_Solution.PixelPermutation);
                Reach(s_Solution.GeometryPermutation);
                Reach(s_Solution.VertexConstants);
                Reach(s_Solution.PixelConstants);

                if (s_Solution.VertexPermutation is { } s_Vertex)
                {
                    Reach(s_Vertex.Constant);
                    Reach(s_Vertex.ConstantFunction);
                    Reach(s_Vertex.TextureFunction);
                }

                if (s_Solution.PixelPermutation is { } s_Pixel)
                {
                    Reach(s_Pixel.Constant);
                    Reach(s_Pixel.ConstantFunction);
                    Reach(s_Pixel.TextureFunction);
                }
            }
        }

        s_Slice.Constants = Constants.Where(p_C => s_Reached.Contains(p_C)).ToArray();
        s_Slice.ConstantFunctions = ConstantFunctions.Where(p_F => s_Reached.Contains(p_F)).ToArray();
        s_Slice.TextureFunctions = TextureFunctions.Where(p_F => s_Reached.Contains(p_F)).ToArray();
        s_Slice.VertexPermutations = VertexPermutations.Where(p_P => s_Reached.Contains(p_P)).ToArray();
        s_Slice.PixelPermutations = PixelPermutations.Where(p_P => s_Reached.Contains(p_P)).ToArray();
        s_Slice.GeometryPermutations = GeometryPermutations.Where(p_P => s_Reached.Contains(p_P)).ToArray();
        s_Slice.Solutions = Solutions.Where(p_S => s_Reached.Contains(p_S)).ToArray();
        s_Slice.Declarations = new List<(uint, GeometryDeclarationDesc)>(Declarations);
        s_Slice.ShaderEntries = ShaderEntries.Where(p_E => s_Infos.Contains(p_E.Info)).ToList();

        return s_Slice;
    }

    public string MergeFrom(ShaderDatabase p_Other, ShaderDatabase p_Vanilla, TextWriter p_Writer)
    {
        var s_Added = 0;
        var s_Carried = 0;
        var s_Duplicates = 0;

        static bool SameBytes(byte[]? p_A, byte[]? p_B) =>
            ReferenceEquals(p_A, p_B) || (p_A != null && p_B != null && p_A.AsSpan().SequenceEqual(p_B));

        static byte[]?[] PermBytes(SurfaceShaderInfo p_Info) =>
            p_Info.Solutions.Select(p_S => p_S.PixelPermutation?.ShaderBytecode).ToArray();

        foreach (var (s_Name, s_Theirs) in p_Other.Shaders)
        {
            var s_Vanilla = p_Vanilla.Shaders
                .FirstOrDefault(p_P => p_P.Key.Equals(s_Name, StringComparison.OrdinalIgnoreCase)).Value;
            var s_Mine = Shaders
                .FirstOrDefault(p_P => p_P.Key.Equals(s_Name, StringComparison.OrdinalIgnoreCase)).Value;

            if (s_Vanilla == null)
            {
                // An ADDITION (a variation clone). The same addition arriving from two mods is fine when it
                // is byte-identical (the same bake installed twice), a conflict when it is not.
                if (s_Mine != null)
                {
                    var s_MineBytes = PermBytes(s_Mine);
                    var s_TheirBytes = PermBytes(s_Theirs);
                    if (s_MineBytes.Length == s_TheirBytes.Length &&
                        s_MineBytes.Zip(s_TheirBytes).All(p_Z => SameBytes(p_Z.First, p_Z.Second)))
                    {
                        s_Duplicates++;
                        continue;
                    }

                    return $"ERROR: both mods add '{s_Name}' with different contents — rename one variation";
                }

                var s_Key = 0u;
                foreach (var s_Entry in p_Other.ShaderEntries)
                    if (ReferenceEquals(s_Entry.Info, s_Theirs))
                    {
                        s_Key = s_Entry.Key;
                        break;
                    }

                if (s_Key == 0)
                    return $"ERROR: '{s_Name}' has no entry key in the source database";

                var s_Report = CloneShaderEntry(s_Name, s_Name, s_Key, null, "", null, null, p_Other);
                if (s_Report.StartsWith("ERROR", StringComparison.Ordinal))
                    return s_Report;

                p_Writer.WriteLine($"  + {s_Name}");
                s_Added++;
                continue;
            }

            // A vanilla-named entry: carry over exactly the pixel bytecode the other mod replaced. The three
            // databases descend from the same level database, so solutions align by index.
            if (s_Mine == null)
                return $"ERROR: '{s_Name}' is vanilla-named but missing here — databases of different levels?";

            if (s_Theirs.Solutions.Length != s_Vanilla.Solutions.Length ||
                s_Mine.Solutions.Length != s_Vanilla.Solutions.Length)
                return $"ERROR: '{s_Name}' has a different solution count across the databases";

            for (var i = 0; i < s_Vanilla.Solutions.Length; i++)
            {
                // A vertex replacement would be dropped silently by the pixel-only carry below, and a merge
                // that quietly ships less than its inputs is worse than one that refuses.
                var s_TheirVertex = s_Theirs.Solutions[i].VertexPermutation;
                var s_VanillaVertex = s_Vanilla.Solutions[i].VertexPermutation;
                if (s_TheirVertex != null && s_VanillaVertex != null &&
                    !SameBytes(s_TheirVertex.ShaderBytecode, s_VanillaVertex.ShaderBytecode))
                    return $"ERROR: '{s_Name}' carries a vertex-shader replacement, which the merge does not support";

                var s_TheirPerm = s_Theirs.Solutions[i].PixelPermutation;
                var s_VanillaPerm = s_Vanilla.Solutions[i].PixelPermutation;
                var s_MinePerm = s_Mine.Solutions[i].PixelPermutation;

                if (s_TheirPerm == null || s_VanillaPerm == null || s_MinePerm == null ||
                    SameBytes(s_TheirPerm.ShaderBytecode, s_VanillaPerm.ShaderBytecode))
                    continue;

                if (SameBytes(s_MinePerm.ShaderBytecode, s_TheirPerm.ShaderBytecode))
                    continue;

                if (!SameBytes(s_MinePerm.ShaderBytecode, s_VanillaPerm.ShaderBytecode))
                    return $"ERROR: both mods replace '{s_Name}' (solution {i}) with different bytecode";

                s_MinePerm.ShaderBytecode = s_TheirPerm.ShaderBytecode;
                s_MinePerm.InstructionCount = s_TheirPerm.InstructionCount;
                s_Carried++;
            }
        }

        return $"merged: {s_Added} entr(ies) transplanted, {s_Carried} permutation(s) carried over, " +
               $"{s_Duplicates} identical duplicate(s) skipped";
    }

    /// <summary>Instruction count as the compiler recorded it (first dword of the STAT chunk); the fallback
    /// covers a container without one, which no fxc-produced pixel shader is.</summary>
    private static uint InstructionCountOf(byte[] p_Dxbc, uint p_Fallback)
    {
        try
        {
            var s_ChunkCount = BitConverter.ToUInt32(p_Dxbc, 28);
            for (var i = 0; i < s_ChunkCount; i++)
            {
                var s_Offset = BitConverter.ToInt32(p_Dxbc, 32 + i * 4);
                if (p_Dxbc[s_Offset] == (byte) 'S' && p_Dxbc[s_Offset + 1] == (byte) 'T' &&
                    p_Dxbc[s_Offset + 2] == (byte) 'A' && p_Dxbc[s_Offset + 3] == (byte) 'T')
                    return BitConverter.ToUInt32(p_Dxbc, s_Offset + 8);
            }
        }
        catch
        {
            // Malformed container: the fallback below is no worse than what shipped before this fix.
        }

        return p_Fallback;
    }

    /// <summary>Field-by-field copy of a solution state answering to a NEW shader key. Everything but the
    /// name hash is kept verbatim — the state must stay identical to the source's or the runtime's state
    /// match (decl, mode, skinning…) stops finding it for the reason it previously found it.</summary>
    private static Solutions.ShaderSolutionState CloneStateForKey(Solutions.ShaderSolutionState p_Source,
        uint p_NewKey, uint? p_NewDeclHash = null, uint? p_FromDeclHash = null)
    {
        // ToArray on purpose AFTER the writer's dispose (a flush): it stays valid on a closed MemoryStream.
        var s_Stream = new MemoryStream();
        using (var s_Writer = new RimeWriter(s_Stream, RimeLib.IO.Conversion.Endianness.LittleEndian, false))
            p_Source.Serialize(s_Writer);
        var s_Bytes = s_Stream.ToArray();

        using var s_Reader = new RimeReader(new MemoryStream(s_Bytes),
            RimeLib.IO.Conversion.Endianness.LittleEndian, false);
        var s_Clone = new Solutions.ShaderSolutionState(s_Reader)
        {
            SurfaceShaderNameHash = p_NewKey,
            GeometryDeclarationDesc = p_Source.GeometryDeclarationDesc,
        };

        // Re-labelling the declaration is what lets a shader draw geometry it was never compiled for. The
        // runtime picks a solution by matching (shaderKey, declHash, mode, ...), so pointing the clone's
        // solutions at ANOTHER declaration's hash makes them the answer for meshes using that layout. It is
        // only sound when the target layout is a SUPERSET of what the vertex shader reads: D3D11 tolerates an
        // input layout carrying elements the VS ignores, but not one missing a semantic the VS declares.
        // Re-label ONE declaration's solutions, not every one: a shader carries several declarations per
        // mode, and pointing them all at the same hash makes them collide on the runtime's lookup key
        // (shaderKey, declHash, mode, ...). The engine then answers with an arbitrary one of them, which
        // renders as a camo that is right in one pass, inverted in another and everywhere in a third - and
        // re-reading such a database throws "An item with the same key has already been added".
        if (p_NewDeclHash is { } s_Decl &&
            (p_FromDeclHash is not { } s_From2 || p_Source.GeometryDeclarationHash == s_From2))
            s_Clone.GeometryDeclarationHash = s_Decl;

        return s_Clone;
    }

    /// <summary>Lexicographic byte order — the order the permutation section is stored in on disk.</summary>
    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? p_A, byte[]? p_B)
        {
            if (p_A == null || p_B == null)
                return (p_A == null ? 0 : 1) - (p_B == null ? 0 : 1);

            for (var i = 0; i < p_A.Length && i < p_B.Length; i++)
                if (p_A[i] != p_B[i])
                    return p_A[i] - p_B[i];

            return p_A.Length - p_B.Length;
        }
    }

    // Mirror of the reader, section for section, all little-endian, no alignment between sections (the reader is purely
    // sequential except inside the size-prefixed constant records). Permutations/solutions/shaders reference the retained
    // arrays by index, resolved here via Array.IndexOf. Requires that the database was populated by the reader (so the
    // retained raw arrays, constant RawBytes, solution ExtraData, declaration and shader tables are present).
    public bool Serialize(RimeWriter p_Writer)
    {
        p_Writer.Write((uint) 182);
        p_Writer.Write((uint) RenderPath);

        // Constants (each record is size-prefixed; the size counts the 4-byte size field itself). Size is written as a
        // placeholder and back-patched, so this works for both retained (RawBytes) and authored (offset-driven) constants.
        p_Writer.Write((uint) Constants.Length);
        foreach (var s_Constant in Constants)
        {
            var s_SizePos = p_Writer.Position;
            p_Writer.Write((uint) 0);
            s_Constant.Serialize(p_Writer);
            var s_ConstEnd = p_Writer.Position;
            p_Writer.Seek(s_SizePos, SeekOrigin.Begin);
            p_Writer.Write((uint) (s_ConstEnd - s_SizePos));
            p_Writer.Seek(s_ConstEnd, SeekOrigin.Begin);
        }

        // Constant functions.
        p_Writer.Write((uint) ConstantFunctions.Length);
        foreach (var s_ConstantFunction in ConstantFunctions)
            s_ConstantFunction.Serialize(p_Writer);

        // Texture functions.
        p_Writer.Write((uint) TextureFunctions.Length);
        foreach (var s_TextureFunction in TextureFunctions)
            s_TextureFunction.Serialize(p_Writer);

        // Vertex shader permutations.
        p_Writer.Write((uint) VertexPermutations.Length);
        foreach (var s_Permutation in VertexPermutations)
            s_Permutation.Serialize(p_Writer, Constants, ConstantFunctions, TextureFunctions);

        // Pixel shader permutations.
        p_Writer.Write((uint) PixelPermutations.Length);
        foreach (var s_Permutation in PixelPermutations)
            s_Permutation.Serialize(p_Writer, Constants, ConstantFunctions, TextureFunctions);

        // Geometry shader permutations.
        p_Writer.Write((uint) GeometryPermutations.Length);
        foreach (var s_Permutation in GeometryPermutations)
            s_Permutation.Serialize(p_Writer);

        // Solutions.
        p_Writer.Write((uint) Solutions.Length);
        foreach (var s_Solution in Solutions)
            s_Solution.Serialize(p_Writer, VertexPermutations, PixelPermutations, GeometryPermutations, Constants);

        // Solution states (count must equal solution count; each state re-serializes byte-identically).
        p_Writer.Write((uint) Solutions.Length);
        foreach (var s_Solution in Solutions)
            s_Solution.State.Serialize(p_Writer);

        // Geometry declarations.
        p_Writer.Write((uint) Declarations.Count);
        foreach (var s_Declaration in Declarations)
        {
            p_Writer.Write(s_Declaration.Hash);
            s_Declaration.Desc.Serialize(p_Writer);
        }

        // Shaders.
        p_Writer.Write((uint) ShaderEntries.Count);
        foreach (var s_Entry in ShaderEntries)
        {
            p_Writer.Write(s_Entry.Key);
            s_Entry.Info.Serialize(p_Writer, Solutions);
        }

        return true;
    }
}