using fb;
using RimeLib.Frostbite;
using RimeLib.IO;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace RimeLib.Shader.Frostbite2_0.Frostbite.ShaderConstants;

public class ExternalValueConstant : IFbSerializable
{
    public string Name { get; set; } = string.Empty;

    public uint Handle { get; set; }
    public ushort Index { get; set; }
    public ushort ArraySize { get; set; }
    public byte Size { get; set; }
    public bool Required { get; set; }

    public Vec4 DefaultValue { get; set; } = new();

    public ExternalValueConstant()
    {
    }

    public ExternalValueConstant(RimeReader p_Reader)
    {
        Deserialize(p_Reader);
    }

    // Mirror of Deserialize: 0x20 fixed-length name | u32 Handle | u16 Index | u16 ArraySize | u8 Size |
    // u8 Required | 2 pad | 4 * f32 DefaultValue.
    public bool Serialize(RimeWriter p_Writer)
    {
        var s_Name = Encoding.UTF8.GetBytes(Name);
        if (s_Name.Length >= 0x20)
            return false;

        p_Writer.Write(s_Name);
        p_Writer.WriteNullBytes((uint) (0x20 - s_Name.Length));

        p_Writer.Write(Handle);
        p_Writer.Write(Index);
        p_Writer.Write(ArraySize);
        p_Writer.Write(Size);
        p_Writer.Write((byte) (Required ? 1 : 0));
        p_Writer.WriteNullBytes(2);

        p_Writer.Write(DefaultValue.x);
        p_Writer.Write(DefaultValue.y);
        p_Writer.Write(DefaultValue.z);
        p_Writer.Write(DefaultValue.w);
        return true;
    }

    public void Deserialize(RimeReader p_Reader)
    {
        Name = p_Reader.ReadFixedLengthString(0x20);

        Handle = p_Reader.ReadUInt32();

        Index = p_Reader.ReadUInt16();

        ArraySize = p_Reader.ReadUInt16();

        Size = p_Reader.ReadUByte();
        Required = p_Reader.ReadBool();

        p_Reader.Seek(2, System.IO.SeekOrigin.Current);

        DefaultValue.x = p_Reader.ReadSingle();
        DefaultValue.y = p_Reader.ReadSingle();
        DefaultValue.z = p_Reader.ReadSingle();
        DefaultValue.w = p_Reader.ReadSingle();
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