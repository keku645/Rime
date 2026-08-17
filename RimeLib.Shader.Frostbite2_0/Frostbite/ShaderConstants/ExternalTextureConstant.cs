using RimeLib.Frostbite;
using RimeLib.IO;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using fb;

namespace RimeLib.Shader.Frostbite2_0.Frostbite.ShaderConstants;

public class ExternalTextureConstant : IFbSerializable
{
    public string Name { get; set; } = string.Empty;

    public uint Handle { get; set; }
    public ushort Index { get; set; }
    public TextureType TextureType { get; set; } = TextureType.TextureType_2d;
    public bool Required { get; set; }

    public ExternalTextureConstant()
    {

    }

    public ExternalTextureConstant(RimeReader p_Reader)
    {
        Deserialize(p_Reader);
    }

    // Mirror of Deserialize: 0x20 fixed-length name | u32 Handle | u16 Index | u8 TextureType | u8 Required.
    public bool Serialize(RimeWriter p_Writer)
    {
        var s_Name = Encoding.UTF8.GetBytes(Name);
        if (s_Name.Length >= 0x20)
            return false;

        p_Writer.Write(s_Name);
        p_Writer.WriteNullBytes((uint) (0x20 - s_Name.Length));

        p_Writer.Write(Handle);
        p_Writer.Write(Index);
        p_Writer.Write((byte) TextureType);
        p_Writer.Write((byte) (Required ? 1 : 0));
        return true;
    }
        
    public void Deserialize(RimeReader p_Reader)
    {
        Name = p_Reader.ReadFixedLengthString(0x20);

        Handle = p_Reader.ReadUInt32();

        Index = p_Reader.ReadUInt16();

        TextureType = (TextureType)p_Reader.ReadUByte();
        Required = p_Reader.ReadBool(); 
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