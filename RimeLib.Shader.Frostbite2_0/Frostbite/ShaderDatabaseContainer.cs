using RimeLib.IO;
using System.Collections.Generic;
using System.IO;
using fb;
using RimeLib.Content.Mounting;
using RimeLib.IO.Conversion;

namespace RimeLib.Shader.Frostbite2_0.Frostbite;

public class ShaderDatabaseContainer
{
    public Dictionary<ShaderRenderPath, ShaderDatabase> Databases { get; set; } = new();

    public ShaderDatabaseContainer()
    {
    }

    public ShaderDatabaseContainer(RimeReader p_Reader, IEngineMounter p_Mounter)
    {
        var s_PrevEndianness = p_Reader.Endianness;
        p_Reader.Endianness = Endianness.LittleEndian;
        
        var s_ShaderRenderPaths = p_Reader.ReadUInt32();

        for (var i = 0; i < s_ShaderRenderPaths; i++)
        {
            var s_ShaderPath = p_Reader.ReadUInt32();
            var s_ShaderDbSize = p_Reader.ReadUInt32();

            var s_CurrentPosition = p_Reader.BaseStream.Position;

            using (var s_LimitedStream = new LimitedRimeReader(p_Reader, s_ShaderDbSize))
                Databases.Add((ShaderRenderPath) s_ShaderPath, new ShaderDatabase(s_LimitedStream, p_Mounter));

            p_Reader.Seek(s_CurrentPosition + s_ShaderDbSize, SeekOrigin.Begin);
        }

        p_Reader.Endianness = s_PrevEndianness;
    }

    // Mirror of the reader: little-endian, u32 database count, then per database u32 render-path + u32 size +
    // the ShaderDatabase payload. The size is written as a placeholder and back-patched once the payload length
    // is known. Database order follows the dictionary's enumeration order (insertion order = on-disk order).
    public bool Serialize(RimeWriter p_Writer)
    {
        var s_PrevEndianness = p_Writer.Endianness;
        p_Writer.Endianness = Endianness.LittleEndian;

        p_Writer.Write((uint) Databases.Count);

        foreach (var s_Pair in Databases)
        {
            p_Writer.Write((uint) s_Pair.Key);

            // Reserve the size field, remember where it is, and back-patch it after writing the payload.
            var s_SizePosition = p_Writer.Position;
            p_Writer.Write((uint) 0);

            var s_StartPosition = p_Writer.Position;
            s_Pair.Value.Serialize(p_Writer);
            var s_EndPosition = p_Writer.Position;

            p_Writer.Seek(s_SizePosition, SeekOrigin.Begin);
            p_Writer.Write((uint) (s_EndPosition - s_StartPosition));
            p_Writer.Seek(s_EndPosition, SeekOrigin.Begin);
        }

        p_Writer.Endianness = s_PrevEndianness;
        return true;
    }
}