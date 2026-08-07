using RimeLib.IO;
using RimeLib.Shader.Frostbite2_0.Frostbite.Shaders;
using RimeLib.Shader.Frostbite2_0.Frostbite.Solutions;
using System;
using System.Collections.Generic;
using System.IO;
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
            
            if (!p_Mounter.TryGetPartitionByHashLower(s_Key, out var s_PartitionObject))
                throw new Exception($"Could not find partition for shader asset with hash '{s_Key}'. Is the appropriate content mounted?");

            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(EngineType.Frostbite2_0);
            var s_Partition = s_Converter.FromPartitionObject(s_PartitionObject.OriginalName, s_PartitionObject.FirstVariant);
            
            if (s_Partition.PrimaryInstance is not SurfaceShaderBaseAsset s_Asset)
                throw new Exception($"Primary instance of shader asset partition '{s_Partition.Name}' is not a SurfaceShaderBaseAsset.");
            
            var s_Info = new SurfaceShaderInfo(p_Reader, s_Solutions, RimeLib.Frostbite.Utils.HashQuick(s_Asset.Name));
            Shaders.Add(s_Asset.Name, s_Info);
            ShaderEntries.Add((s_Key, s_Info));
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