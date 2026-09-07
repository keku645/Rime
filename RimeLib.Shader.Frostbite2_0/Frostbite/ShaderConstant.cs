using RimeLib.Frostbite;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Shader.Frostbite2_0.Frostbite.ShaderConstants;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
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

    /// <summary>
    /// Adds a streamable-texture slot (TextureConstant) to this constant record. The record is offset-driven, so
    /// blocks may live anywhere inside it; the authored writer is used only when it can PROVE fidelity by
    /// reproducing the current record byte-identically, otherwise the texture block (old entries + the new one) is
    /// appended at the record's TAIL and only the texture-block offset + count in the header are patched — every
    /// other original byte stays in place. Returns a report line ("ERROR: ..." on failure).
    /// </summary>
    public string AddTextureConstant(byte p_Register, byte p_TextureType, string p_Name)
    {
        // A register the record ALREADY binds is a REPLACE, not an append: two entries for one register is
        // undefined territory, and replacing is exactly what a custom variation does to wear its own art.
        // The name field is 0x80 fixed-length, so the raw record can be patched in place — no relocation.
        for (var i = 0; i < Textures.Length; i++)
        {
            if (Textures[i].Index != p_Register)
                continue;

            var s_ReplacementBytes = Encoding.UTF8.GetBytes(p_Name);
            if (s_ReplacementBytes.Length >= 0x80)
                return "ERROR: texture name too long";

            var s_Previous = Textures[i].Name;
            Textures[i] = new TextureConstant
            {
                Index = p_Register, TextureType = (TextureType) p_TextureType, Name = p_Name,
            };

            if (RawBytes != null)
            {
                var s_BlockOff = (long) BitConverter.ToUInt64(RawBytes, 12) - 4;
                const int c_Entry = 0x98;
                var s_NameOff = (int) s_BlockOff + i * c_Entry + 8;
                if (s_BlockOff < 53 || s_NameOff + 0x80 > RawBytes.Length ||
                    RawBytes[(int) s_BlockOff + i * c_Entry] != p_Register)
                    return "ERROR: texture block layout mismatch on in-place replace";

                Array.Clear(RawBytes, s_NameOff, 0x80);
                Array.Copy(s_ReplacementBytes, 0, RawBytes, s_NameOff, s_ReplacementBytes.Length);
                RawBytes[(int) s_BlockOff + i * c_Entry + 1] = p_TextureType;
            }

            return $"replaced t{p_Register}: '{s_Previous}' -> '{p_Name}' (in place)";
        }

        var s_New = new TextureConstant { Index = p_Register, TextureType = (TextureType) p_TextureType, Name = p_Name };

        var s_Model = new TextureConstant[Textures.Length + 1];
        Array.Copy(Textures, s_Model, Textures.Length);
        s_Model[Textures.Length] = s_New;

        if (RawBytes == null)
        {
            // Already an authored constant — just extend the model.
            Textures = s_Model;
            return "authored constant: texture appended to the model";
        }

        // Fidelity probe: does the authored writer reproduce this record byte-identically?
        byte[] s_Authored;
        var s_Saved = RawBytes;
        RawBytes = null;
        using (var s_Ms = new MemoryStream())
        {
            using (var s_W = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
            {
                s_W.Write((uint) 0); // stand-in for the container's size field, so offsets match the on-disk convention
                Serialize(s_W);
            }
            s_Authored = s_Ms.ToArray();
        }
        RawBytes = s_Saved;

        if (s_Authored.Length - 4 == RawBytes.Length && s_Authored.AsSpan(4).SequenceEqual(RawBytes))
        {
            Textures = s_Model;
            RawBytes = null; // re-emit through the (now fidelity-proven) authored writer
            return "authored writer verified byte-identical -> texture appended, record re-authored";
        }

        // Tail-append surgery on the raw record. Header layout inside RawBytes (payload, size field excluded):
        // pad @0 | 5*u64 block offsets @4 (relative to the SIZE FIELD = payload start - 4) | u16 ConstantCount @44 |
        // u16 ValueConstantsStart @46 | 5*u8 block counts @48 (value, texture, extVal, extTex, sampler).
        const int c_EntrySize = 0x98; // u8 Index + u8 Type + 6 pad + 0x80 name + 0x10 pad
        var s_Old = RawBytes;
        if (s_Old.Length < 53)
            return "ERROR: record too small to be a ShaderConstant";

        int s_TexCount = s_Old[49];
        var s_TexOff = (long) BitConverter.ToUInt64(s_Old, 12) - 4; // payload-relative
        if (s_TexCount != Textures.Length)
            return $"ERROR: header texture count {s_TexCount} != parsed {Textures.Length}";
        if (s_TexCount > 0 && (s_TexOff < 53 || s_TexOff + (long) s_TexCount * c_EntrySize > s_Old.Length))
            return "ERROR: texture block out of bounds (unexpected layout)";
        if (s_TexCount > 0 && s_Old[(int) s_TexOff] != Textures[0].Index)
            return "ERROR: texture block sanity check failed (entry layout mismatch)";

        var s_NameBytes = Encoding.UTF8.GetBytes(p_Name);
        if (s_NameBytes.Length >= 0x80)
            return "ERROR: texture name too long";

        var s_Out = new byte[s_Old.Length + (s_TexCount + 1) * c_EntrySize];
        Array.Copy(s_Old, s_Out, s_Old.Length);

        var s_TailOff = s_Old.Length; // payload-relative position of the relocated texture block
        if (s_TexCount > 0)
            Array.Copy(s_Old, (int) s_TexOff, s_Out, s_TailOff, s_TexCount * c_EntrySize);

        var s_E = s_TailOff + s_TexCount * c_EntrySize;
        s_Out[s_E] = p_Register;
        s_Out[s_E + 1] = p_TextureType;
        Array.Copy(s_NameBytes, 0, s_Out, s_E + 8, s_NameBytes.Length);

        BitConverter.GetBytes((ulong) (s_TailOff + 4)).CopyTo(s_Out, 12); // back to size-field-relative
        s_Out[49] = (byte) (s_TexCount + 1);

        RawBytes = s_Out;
        Textures = s_Model; // keep the parsed model in sync for dumps
        return $"raw tail-append: texture block relocated to +{s_TailOff}, {s_TexCount}+1 entries, record {s_Old.Length} -> {s_Out.Length} B";
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