using RimeLib.Frostbite.Core;
using RimeLib.IO;
using System;
using RimeLib.Shader.Frostbite2_0.Frostbite.Functions;

namespace RimeLib.Shader.Frostbite2_0.Frostbite.Shaders;

public class PixelShaderPermutation
{
    public GUID Guid { get; set; } = GUID.Empty;
    public byte[] ShaderBytecode { get; set; } = Array.Empty<byte>();

    public ShaderConstant Constant { get; set; } = default!;
    public ShaderConstantFunctionData ConstantFunction { get; set; } = default!;
    public ShaderTextureFunctionData TextureFunction { get; set; } = default!;

    public uint InstructionCount { get; set; }

    public PixelShaderPermutation()
    {
    }

    // Mirror of the reader: sequential, indices resolved against the database's retained arrays.
    // Guid | u32 bytecodeSize + bytecode | u32 idx Constant | u32 idx ConstantFunction | u32 idx TextureFunction | u32 InstructionCount.
    public bool Serialize(
        RimeWriter p_Writer,
        ShaderConstant[] p_Constants,
        ShaderConstantFunctionData[] p_ConstantFunctionData,
        ShaderTextureFunctionData[] p_TextureFunctionData
    )
    {
        Guid.Serialize(p_Writer);

        p_Writer.Write((uint) ShaderBytecode.Length);
        p_Writer.Write(ShaderBytecode);

        p_Writer.Write((uint) Array.IndexOf(p_Constants, Constant));
        p_Writer.Write((uint) Array.IndexOf(p_ConstantFunctionData, ConstantFunction));
        p_Writer.Write((uint) Array.IndexOf(p_TextureFunctionData, TextureFunction));

        p_Writer.Write(InstructionCount);

        return true;
    }

    public PixelShaderPermutation(
        RimeReader p_Reader,
        ShaderConstant[] p_Constants,
        ShaderConstantFunctionData[] p_ConstantFunctionData,
        ShaderTextureFunctionData[] p_TextureFunctionData
    )
    {
        Guid = new GUID(p_Reader);

        var s_DataSize = p_Reader.ReadUInt32();
        ShaderBytecode = p_Reader.ReadBytes((int)s_DataSize);

        Constant = p_Constants[p_Reader.ReadUInt32()];
        ConstantFunction = p_ConstantFunctionData[p_Reader.ReadUInt32()];
        TextureFunction = p_TextureFunctionData[p_Reader.ReadUInt32()];

        InstructionCount = p_Reader.ReadUInt32();
    }
}