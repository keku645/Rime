using RimeLib.Frostbite;
using RimeLib.IO;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using fb;

namespace RimeLib.Shader.Frostbite2_0.Frostbite.ShaderConstants;

public class TextureConstant : IFbSerializable
{
    public byte Index { get; set; }
    public TextureType TextureType { get; set; } = TextureType.TextureType_2d;
    public string Name { get; set; } = string.Empty;

    public TextureConstant()
    {

    }

    public TextureConstant(RimeReader p_Reader)
    {
        Deserialize(p_Reader);
    }

    // Mirror of Deserialize: u8 Index | u8 TextureType | 6 pad | 0x80 fixed-length name | 0x10 pad.
    public bool Serialize(RimeWriter p_Writer)
    {
        p_Writer.Write((byte) Index);
        p_Writer.Write((byte) TextureType);
        p_Writer.WriteNullBytes(0x6);

        var s_Name = Encoding.UTF8.GetBytes(Name);
        if (s_Name.Length >= 0x80)
            return false;

        p_Writer.Write(s_Name);
        p_Writer.WriteNullBytes((uint) (0x80 - s_Name.Length));

        p_Writer.WriteNullBytes(0x10);
        return true;
    }

    public void Deserialize(RimeReader p_Reader)
    {
        Index = p_Reader.ReadUByte();
        TextureType = (TextureType)p_Reader.ReadUByte();

        p_Reader.Seek(0x6, SeekOrigin.Current); 

        Name = p_Reader.ReadFixedLengthString(0x80);

        p_Reader.Seek(0x10, SeekOrigin.Current); //This is moved to the previous pad in newer shaderdbs
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