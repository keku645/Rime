using System;
using RimeLib.IO;
using fb;
using RimeLib.Shader.Frostbite2_0.Frostbite.Shaders;
using SharpDX.Direct3D11;

namespace RimeLib.Shader.Frostbite2_0.Frostbite.Solutions;

public class ShaderSolution
{
    public ulong StateHash { get; set; }
    public ShaderSolutionState State { get; set; } = default!;

    public byte Flags { get; set; } // 1 = DoubleSided, 2 = GammaCorrection

    public SurfaceShaderType SurfaceType { get; set; } = SurfaceShaderType.SurfaceShaderType_Opaque;
    public ShaderBlendMode BlendMode { get; set; } = ShaderBlendMode.ShaderBlendMode_Lerp;

    public VertexShaderPermutation? VertexPermutation { get; set; }
    public PixelShaderPermutation? PixelPermutation { get; set; }
    public GeometryShaderPermutation? GeometryPermutation { get; set; }
    public ShaderConstant? VertexConstants { get; set; }
    public ShaderConstant? PixelConstants { get; set; }

    // The 13 bytes between (Flags, SurfaceType, BlendMode) and the permutation indices. The reader seeks
    // over them; retained verbatim so the writer can re-emit the solution byte-identically.
    public byte[] ExtraData { get; set; } = new byte[0xD];

    public ShaderSolution()
    {
    }

    public ShaderSolution(
        RimeReader p_Reader,
        VertexShaderPermutation[] p_VertexShaderPermutations,
        PixelShaderPermutation[] p_PixelShaderPermutations,
        GeometryShaderPermutation[] p_GeometryShaderPermutations,
        ShaderConstant[] p_Constants
    )
    {
        StateHash = p_Reader.ReadUInt64( );

        Flags = p_Reader.ReadUByte(); // 1 = DoubleSided, 2 = GammaCorrection

        SurfaceType = (SurfaceShaderType)p_Reader.ReadUByte();
        BlendMode = (ShaderBlendMode)p_Reader.ReadUByte();

        ExtraData = p_Reader.ReadBytes(0xD);

        var s_VertexPermutationIndex = p_Reader.ReadInt64();
        
        if (s_VertexPermutationIndex != -1)
            VertexPermutation = p_VertexShaderPermutations[s_VertexPermutationIndex];

        var s_PixelPermutationIndex = p_Reader.ReadInt64();
        
        if (s_PixelPermutationIndex != -1)
            PixelPermutation = p_PixelShaderPermutations[s_PixelPermutationIndex];

        var s_GeometryPermutationIndex = p_Reader.ReadInt64();
        
        if (s_GeometryPermutationIndex != -1)
            GeometryPermutation = p_GeometryShaderPermutations[s_GeometryPermutationIndex];

        var s_VertexConstantsIndex = p_Reader.ReadInt64();
        
        if (s_VertexConstantsIndex != -1)
            VertexConstants = p_Constants[s_VertexConstantsIndex];
        
        var s_PixelConstantsIndex = p_Reader.ReadInt64();

        if (s_PixelConstantsIndex != -1)
            PixelConstants = p_Constants[s_PixelConstantsIndex];
    }

    // Mirror of the reader: u64 StateHash | u8 Flags | u8 SurfaceType | u8 BlendMode | 13 retained bytes |
    // i64 * (VertexPerm, PixelPerm, GeometryPerm, VertexConstants, PixelConstants) indices (-1 = null).
    // Indices resolve against the database's retained arrays. (State is written separately, in the states section.)
    public bool Serialize(
        RimeWriter p_Writer,
        VertexShaderPermutation[] p_VertexShaderPermutations,
        PixelShaderPermutation[] p_PixelShaderPermutations,
        GeometryShaderPermutation[] p_GeometryShaderPermutations,
        ShaderConstant[] p_Constants
    )
    {
        p_Writer.Write(StateHash);

        p_Writer.Write(Flags);
        p_Writer.Write((byte) SurfaceType);
        p_Writer.Write((byte) BlendMode);

        p_Writer.Write(ExtraData);

        p_Writer.Write((long) (VertexPermutation != null ? Array.IndexOf(p_VertexShaderPermutations, VertexPermutation) : -1));
        p_Writer.Write((long) (PixelPermutation != null ? Array.IndexOf(p_PixelShaderPermutations, PixelPermutation) : -1));
        p_Writer.Write((long) (GeometryPermutation != null ? Array.IndexOf(p_GeometryShaderPermutations, GeometryPermutation) : -1));
        p_Writer.Write((long) (VertexConstants != null ? Array.IndexOf(p_Constants, VertexConstants) : -1));
        p_Writer.Write((long) (PixelConstants != null ? Array.IndexOf(p_Constants, PixelConstants) : -1));

        return true;
    }

    public void GenerateD3DResources(Device p_Device)
    {
        /*if (VertexPermutation != null)
        {
            var s_Shader = new VertexShader(p_Device, VertexPermutation.ShaderBytecode);
            var s_InputLayout = new InputLayout(
                p_Device,
                s_Solution.VertexPermutation.InputSignatureBytecode,
                s_Solution.VertexPermutation.Elements
            );

            s_VertexShaders.Add(s_Shader);
            s_InputLayouts.Add(s_InputLayout);
        }

        if (s_Solution.VertexConstants != null)
        {
            var s_Samplers = new List<SamplerState>();
                    
            foreach (var s_Sampler in s_Solution.VertexConstants.Samplers)
            {
                s_Samplers.Add(new SamplerState(p_Device, s_Sampler.Desc));
            }
                    
            s_VertexSamplers.Add(s_Samplers.ToArray());
        }*/
    }
}