using RimeLib.Frostbite;
using RimeLib.IO;
using RimeLib.Shader.Frostbite2_0.Frostbite.ShaderConstants;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using fb;

namespace RimeLib.Shader.Frostbite2_0.Frostbite;

public class ShaderConstant : IFbSerializable
{
    public ushort ConstantCount { get; set; }
    public ushort ValueConstantsStart { get; set; }

    public Vec4[] ValueConstants { get; set; } = Array.Empty<Vec4>();
    
    public TextureConstant[] Textures { get; set; } = Array.Empty<TextureConstant>();

    public ExternalValueConstant[] ExternalValues { get; set; } = Array.Empty<ExternalValueConstant>();
    public ExternalTextureConstant[] ExternalTextures { get; set; } = Array.Empty<ExternalTextureConstant>();

    public SamplerState[] Samplers { get; set; } = Array.Empty<SamplerState>();

    // Raw payload of this constant record (everything AFTER the 4-byte size prefix), retained verbatim from the reader.
    // A ShaderConstant is offset-driven (see Deserialize): the reader records the parsed blocks but discards the exact
    // header pad, offset ordering and inter-block padding, so re-emitting a byte-identical record from the parsed fields
    // requires a full offset-planning writer (TODO, needed only when AUTHORING a brand-new constant). For round-trip and
    // for constants we don't modify we re-emit these raw bytes, which is byte-identical by construction.
    public byte[]? RawBytes { get; set; }

    public ShaderConstant()
    {
    }

    public ShaderConstant(RimeReader p_Reader)
    {
        Deserialize(p_Reader);
    }

    // Writes the constant PAYLOAD (without the 4-byte size prefix, which the container writes). For retained records
    // this is byte-identical. Authoring a new constant from the parsed fields (offset planning) is not implemented yet.
    public bool Serialize(RimeWriter p_Writer)
    {
        // Retained (unmodified) constant: re-emit verbatim (byte-identical round-trip).
        if (RawBytes != null)
        {
            p_Writer.Write(RawBytes);
            return true;
        }

        // Authored constant: offset-driven layout (mirror of Deserialize). All five block offsets are relative to the
        // SIZE-FIELD position (= payload start - 4, since the container wrote the u32 size just before calling us).
        // Header: pad(4) + 5*u64 block offsets + u16 ConstantCount + u16 ValueConstantsStart + 5*u8 block counts.
        var s_StartPosition = p_Writer.Position - 4;

        p_Writer.WriteNullBytes(4); // pad

        var s_OffsetsPos = p_Writer.Position;
        for (var i = 0; i < 5; i++)
            p_Writer.Write((ulong) 0); // offset placeholders, back-patched below

        p_Writer.Write(ConstantCount);
        p_Writer.Write(ValueConstantsStart);
        p_Writer.Write((byte) ValueConstants.Length);
        p_Writer.Write((byte) Textures.Length);
        p_Writer.Write((byte) ExternalValues.Length);
        p_Writer.Write((byte) ExternalTextures.Length);
        p_Writer.Write((byte) Samplers.Length);

        var s_Offsets = new ulong[5];

        s_Offsets[0] = (ulong) (p_Writer.Position - s_StartPosition);
        foreach (var s_Value in ValueConstants)
        {
            p_Writer.Write(s_Value.x);
            p_Writer.Write(s_Value.y);
            p_Writer.Write(s_Value.z);
            p_Writer.Write(s_Value.w);
        }

        s_Offsets[1] = (ulong) (p_Writer.Position - s_StartPosition);
        foreach (var s_Texture in Textures)
            s_Texture.Serialize(p_Writer);

        s_Offsets[2] = (ulong) (p_Writer.Position - s_StartPosition);
        foreach (var s_External in ExternalValues)
            s_External.Serialize(p_Writer);

        s_Offsets[3] = (ulong) (p_Writer.Position - s_StartPosition);
        foreach (var s_External in ExternalTextures)
            s_External.Serialize(p_Writer);

        s_Offsets[4] = (ulong) (p_Writer.Position - s_StartPosition);
        foreach (var s_Sampler in Samplers)
            s_Sampler.Serialize(p_Writer);

        var s_End = p_Writer.Position;

        p_Writer.Seek(s_OffsetsPos, SeekOrigin.Begin);
        for (var i = 0; i < 5; i++)
            p_Writer.Write(s_Offsets[i]);
        p_Writer.Seek(s_End, SeekOrigin.Begin);

        return true;
    }

    public void Deserialize(RimeReader p_Reader)
    {
        var s_StartPosition = p_Reader.Position - 4; //4 bytes allready used...

        p_Reader.Seek(4, SeekOrigin.Current); //Pad

        var s_ValueConstantOffset = p_Reader.ReadUInt64();
        var s_TextureConstantOffset = p_Reader.ReadUInt64();

        var s_ExternalValueConstantOffset = p_Reader.ReadUInt64();
        var s_ExternalTextureConstantOffset = p_Reader.ReadUInt64();

        var s_SamplerStatesOffset = p_Reader.ReadUInt64();
        
        ConstantCount = p_Reader.ReadUInt16();
        ValueConstantsStart = p_Reader.ReadUInt16();

        var s_ValueConstantCount = p_Reader.ReadUByte();
        var s_TextureConstantCount = p_Reader.ReadUByte();
        var s_ExternalValueConstantCount = p_Reader.ReadUByte();
        var s_ExternalTextureConstantCount = p_Reader.ReadUByte();
        var s_SamplerStateCount = p_Reader.ReadUByte();
        
        if (s_ValueConstantCount > 0)
        {
            p_Reader.Seek(s_StartPosition + (long) s_ValueConstantOffset, SeekOrigin.Begin);

            ValueConstants = new Vec4[s_ValueConstantCount];

            for (var i = 0; i < s_ValueConstantCount; ++i)
            {
                ValueConstants[i].x = p_Reader.ReadSingle();
                ValueConstants[i].y = p_Reader.ReadSingle();
                ValueConstants[i].z = p_Reader.ReadSingle();
                ValueConstants[i].w = p_Reader.ReadSingle();
            }
        }

        if (s_TextureConstantCount > 0)
        {
            p_Reader.Seek(s_StartPosition + (long) s_TextureConstantOffset, SeekOrigin.Begin);

            Textures = new TextureConstant[s_TextureConstantCount];

            for (var i = 0; i < s_TextureConstantCount; i++)
                Textures[i] = new TextureConstant(p_Reader);
        }

        if (s_ExternalValueConstantCount > 0)
        {
            p_Reader.Seek(s_StartPosition + (long) s_ExternalValueConstantOffset, SeekOrigin.Begin);

            ExternalValues = new ExternalValueConstant[s_ExternalValueConstantCount];

            for (var i = 0; i < s_ExternalValueConstantCount; i++)
                ExternalValues[i] = new ExternalValueConstant(p_Reader);
        }

        if (s_ExternalTextureConstantCount > 0)
        {
            p_Reader.Seek(s_StartPosition + (long) s_ExternalTextureConstantOffset, SeekOrigin.Begin);

            ExternalTextures = new ExternalTextureConstant[s_ExternalTextureConstantCount];

            for (var i = 0; i < s_ExternalTextureConstantCount; i++)
                ExternalTextures[i] = new ExternalTextureConstant(p_Reader);
        }

        if (s_SamplerStateCount > 0)
        {
            p_Reader.Seek(s_StartPosition + (long) s_SamplerStatesOffset, SeekOrigin.Begin);

            Samplers = new SamplerState[s_SamplerStateCount];

            for (var i = 0; i < s_SamplerStateCount; i++)
                Samplers[i] = new SamplerState(p_Reader);
        }
    }

    public bool Serialize([NotNullWhen(true)] out byte[]? p_Data)
    {
        p_Data = null;
        throw new System.NotImplementedException();
    }
        
    public void Deserialize(byte[] p_Data)
    {
        throw new System.NotImplementedException();
    }

}