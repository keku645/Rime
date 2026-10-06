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

    /// <summary>
    /// Adds an EXTERNAL texture parameter (ExternalTextureConstant: a material parameter, bound per material
    /// instance by NAME — the way Diffuse, Camo and Specular reach a weapon preset) to this constant record at
    /// a pixel register. The handle is the engine's own id of the name (hashQuickLowerCase, measured on the
    /// weapon presets: Camo = 2087764101). Same surgery as <see cref="AddTextureConstant"/>: the authored
    /// writer when it proves fidelity, otherwise the external-texture block (old entries + the new one) is
    /// appended at the record's tail and only that block's offset + count in the header are patched.
    /// A register the record already binds externally is REPLACED in place (name + handle); a name already
    /// bound at another register is refused. Returns a report line ("ERROR: ..." on failure).
    /// </summary>
    public string AddExternalTextureConstant(ushort p_Register, byte p_TextureType, string p_Name, uint p_Handle,
        bool p_Required = false)
    {
        const int c_EntrySize = 0x28; // 0x20 name + u32 Handle + u16 Index + u8 TextureType + u8 Required
        const int c_NameSize = 0x20;

        var s_NameBytes = Encoding.UTF8.GetBytes(p_Name);
        if (s_NameBytes.Length >= c_NameSize)
            return "ERROR: external texture name too long (31 bytes at most)";

        for (var i = 0; i < ExternalTextures.Length; i++)
        {
            var s_Existing = ExternalTextures[i];
            if (s_Existing.Index != p_Register &&
                string.Equals(s_Existing.Name, p_Name, StringComparison.OrdinalIgnoreCase))
                return $"ERROR: external texture '{p_Name}' is already bound at t{s_Existing.Index}";

            if (s_Existing.Index != p_Register)
                continue;

            var s_Previous = s_Existing.Name;
            ExternalTextures[i] = new ExternalTextureConstant
            {
                Name = p_Name, Handle = p_Handle, Index = p_Register,
                TextureType = (TextureType) p_TextureType, Required = p_Required,
            };

            if (RawBytes != null)
            {
                var s_BlockOff = (long) BitConverter.ToUInt64(RawBytes, 28) - 4;
                var s_EntryOff = (int) s_BlockOff + i * c_EntrySize;
                if (s_BlockOff < 53 || s_EntryOff + c_EntrySize > RawBytes.Length ||
                    BitConverter.ToUInt16(RawBytes, s_EntryOff + 0x24) != p_Register)
                    return "ERROR: external texture block layout mismatch on in-place replace";

                Array.Clear(RawBytes, s_EntryOff, c_NameSize);
                Array.Copy(s_NameBytes, 0, RawBytes, s_EntryOff, s_NameBytes.Length);
                BitConverter.GetBytes(p_Handle).CopyTo(RawBytes, s_EntryOff + 0x20);
                RawBytes[s_EntryOff + 0x26] = p_TextureType;
                RawBytes[s_EntryOff + 0x27] = (byte) (p_Required ? 1 : 0);
            }

            return $"replaced external t{p_Register}: '{s_Previous}' -> '{p_Name}' (in place)";
        }

        var s_New = new ExternalTextureConstant
        {
            Name = p_Name, Handle = p_Handle, Index = p_Register,
            TextureType = (TextureType) p_TextureType, Required = p_Required,
        };

        var s_Model = new ExternalTextureConstant[ExternalTextures.Length + 1];
        Array.Copy(ExternalTextures, s_Model, ExternalTextures.Length);
        s_Model[ExternalTextures.Length] = s_New;

        if (RawBytes == null)
        {
            ExternalTextures = s_Model;
            return "authored constant: external texture appended to the model";
        }

        // Fidelity probe: does the authored writer reproduce this record byte-identically?
        byte[] s_Authored;
        var s_Saved = RawBytes;
        RawBytes = null;
        using (var s_Ms = new MemoryStream())
        {
            using (var s_W = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
            {
                s_W.Write((uint) 0);
                Serialize(s_W);
            }
            s_Authored = s_Ms.ToArray();
        }
        RawBytes = s_Saved;

        if (s_Authored.Length - 4 == RawBytes.Length && s_Authored.AsSpan(4).SequenceEqual(RawBytes))
        {
            ExternalTextures = s_Model;
            RawBytes = null;
            return "authored writer verified byte-identical -> external texture appended, record re-authored";
        }

        // Tail-append surgery on the raw record: block offset [3] @28 (size-field-relative), count @51.
        var s_Old = RawBytes;
        if (s_Old.Length < 53)
            return "ERROR: record too small to be a ShaderConstant";

        int s_ExtCount = s_Old[51];
        var s_ExtOff = (long) BitConverter.ToUInt64(s_Old, 28) - 4;
        if (s_ExtCount != ExternalTextures.Length)
            return $"ERROR: header external texture count {s_ExtCount} != parsed {ExternalTextures.Length}";
        if (s_ExtCount > 0 && (s_ExtOff < 53 || s_ExtOff + (long) s_ExtCount * c_EntrySize > s_Old.Length))
            return "ERROR: external texture block out of bounds (unexpected layout)";
        if (s_ExtCount > 0 && BitConverter.ToUInt16(s_Old, (int) s_ExtOff + 0x24) != ExternalTextures[0].Index)
            return "ERROR: external texture block sanity check failed (entry layout mismatch)";

        var s_Out = new byte[s_Old.Length + (s_ExtCount + 1) * c_EntrySize];
        Array.Copy(s_Old, s_Out, s_Old.Length);

        var s_TailOff = s_Old.Length;
        if (s_ExtCount > 0)
            Array.Copy(s_Old, (int) s_ExtOff, s_Out, s_TailOff, s_ExtCount * c_EntrySize);

        var s_E = s_TailOff + s_ExtCount * c_EntrySize;
        Array.Copy(s_NameBytes, 0, s_Out, s_E, s_NameBytes.Length);
        BitConverter.GetBytes(p_Handle).CopyTo(s_Out, s_E + 0x20);
        BitConverter.GetBytes(p_Register).CopyTo(s_Out, s_E + 0x24);
        s_Out[s_E + 0x26] = p_TextureType;
        s_Out[s_E + 0x27] = (byte) (p_Required ? 1 : 0);

        BitConverter.GetBytes((ulong) (s_TailOff + 4)).CopyTo(s_Out, 28);
        s_Out[51] = (byte) (s_ExtCount + 1);

        RawBytes = s_Out;
        ExternalTextures = s_Model;
        return $"raw tail-append: external texture block relocated to +{s_TailOff}, {s_ExtCount}+1 entries, record {s_Old.Length} -> {s_Out.Length} B";
    }

    /// <summary>
    /// Puts an EXTERNAL VALUE parameter (ExternalValueConstant: a float4 the engine fills per draw BY NAME from the
    /// entity's ShaderParameterBlock — the way ScopeOcc reaches a weapon preset from the weapon's
    /// ShaderParameterComponentData) at a pixel register of this constant record. The handle is the engine's id of
    /// the name (hashQuickLowerCase, the same id the external textures carry).
    /// A register the record already binds externally is RENAMED in place (name, handle and default; the entry's
    /// Size/ArraySize/Required are kept). A new register goes through the authored writer when it proves fidelity,
    /// otherwise the external-value block (old entries + the new one) is appended at the record's tail and only that
    /// block's offset + count in the header are patched — the surgery of <see cref="AddExternalTextureConstant"/>;
    /// ConstantCount grows to cover the register. A register inside the literal value constants, or a name already
    /// bound at another register, is refused. Returns a report line ("ERROR: ..." on failure).
    /// </summary>
    public string SetExternalValueConstant(ushort p_Register, string p_Name, uint p_Handle, float p_DefaultX,
        float p_DefaultY, float p_DefaultZ, float p_DefaultW)
    {
        const int c_EntrySize = 0x3C; // 0x20 name | u32 Handle | u16 Index | u16 ArraySize | u8 Size | u8 Required | 2 pad | 4 * f32
        const int c_NameSize = 0x20;
        var s_Default = new Vec4 { x = p_DefaultX, y = p_DefaultY, z = p_DefaultZ, w = p_DefaultW };

        var s_NameBytes = Encoding.UTF8.GetBytes(p_Name);
        if (s_NameBytes.Length >= c_NameSize)
            return "ERROR: external value name too long (31 bytes at most)";

        if (ValueConstants.Length > 0 && p_Register >= ValueConstantsStart &&
            p_Register < ValueConstantsStart + ValueConstants.Length)
            return $"ERROR: c{p_Register} holds a literal value constant (c{ValueConstantsStart}..c{ValueConstantsStart + ValueConstants.Length - 1})";

        for (var i = 0; i < ExternalValues.Length; i++)
        {
            var s_Existing = ExternalValues[i];
            if (s_Existing.Index != p_Register &&
                string.Equals(s_Existing.Name, p_Name, StringComparison.OrdinalIgnoreCase))
                return $"ERROR: external value '{p_Name}' is already bound at c{s_Existing.Index}";

            if (s_Existing.Index != p_Register)
                continue;

            var s_Previous = s_Existing.Name;
            s_Existing.Name = p_Name;
            s_Existing.Handle = p_Handle;
            s_Existing.DefaultValue = s_Default;

            if (RawBytes != null)
            {
                var s_BlockOff = (long) BitConverter.ToUInt64(RawBytes, 20) - 4;
                var s_EntryOff = (int) s_BlockOff + i * c_EntrySize;
                if (s_BlockOff < 53 || s_EntryOff + c_EntrySize > RawBytes.Length ||
                    BitConverter.ToUInt16(RawBytes, s_EntryOff + 0x24) != p_Register)
                    return "ERROR: external value block layout mismatch on in-place rename";

                Array.Clear(RawBytes, s_EntryOff, c_NameSize);
                Array.Copy(s_NameBytes, 0, RawBytes, s_EntryOff, s_NameBytes.Length);
                BitConverter.GetBytes(p_Handle).CopyTo(RawBytes, s_EntryOff + 0x20);
                WriteVec4(RawBytes, s_EntryOff + 0x2C, s_Default);
            }

            return $"renamed external c{p_Register}: '{s_Previous}' -> '{p_Name}' (in place)";
        }

        // A new register: a single float4 — Size is the number of components the engine writes, so FOUR whatever the record's
        // first external holds (⛔ modelled on it, the emblem slot's layers came out size 3 — DiffuseDarkening is a float3 — and
        // their w, a layer's opacity and half height, would never be written; readback 2026-09-29). ArraySize from that one.
        var s_Template = ExternalValues.Length > 0 ? ExternalValues[0] : null;
        var s_New = new ExternalValueConstant
        {
            Name = p_Name, Handle = p_Handle, Index = p_Register, DefaultValue = s_Default,
            ArraySize = s_Template?.ArraySize ?? 1, Size = 4, Required = false,
        };

        var s_Model = new ExternalValueConstant[ExternalValues.Length + 1];
        Array.Copy(ExternalValues, s_Model, ExternalValues.Length);
        s_Model[ExternalValues.Length] = s_New;
        // ⛔ The Index is ONE-based and the count is the highest Index (measured 2026-09-29 on WeaponPresetShadowFP: a table of
        // 12 externals holds Index 1..12 for the bytecode's elements c0..c11 and counts 12; one with a 4-register $Globals block
        // before them holds 5..16 and counts 16) — the append of 2026-09-29 16:25 counted one too many (register + 1).
        var s_Count = (ushort) System.Math.Max(ConstantCount, p_Register);

        if (RawBytes == null)
        {
            ExternalValues = s_Model;
            ConstantCount = s_Count;
            return "authored constant: external value appended to the model";
        }

        // Fidelity probe: does the authored writer reproduce this record byte-identically?
        byte[] s_Authored;
        var s_Saved = RawBytes;
        RawBytes = null;
        using (var s_Ms = new MemoryStream())
        {
            using (var s_W = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
            {
                s_W.Write((uint) 0);
                Serialize(s_W);
            }
            s_Authored = s_Ms.ToArray();
        }
        RawBytes = s_Saved;

        if (s_Authored.Length - 4 == RawBytes.Length && s_Authored.AsSpan(4).SequenceEqual(RawBytes))
        {
            ExternalValues = s_Model;
            ConstantCount = s_Count;
            RawBytes = null;
            return $"authored writer verified byte-identical -> external value appended, record re-authored, {ConstantCount} constants";
        }

        // Tail-append surgery on the raw record: block offset [2] @20 (size-field-relative), count @50,
        // ConstantCount @44.
        var s_Old = RawBytes;
        if (s_Old.Length < 53)
            return "ERROR: record too small to be a ShaderConstant";

        int s_ExtCount = s_Old[50];
        var s_ExtOff = (long) BitConverter.ToUInt64(s_Old, 20) - 4;
        if (s_ExtCount != ExternalValues.Length)
            return $"ERROR: header external value count {s_ExtCount} != parsed {ExternalValues.Length}";
        if (s_ExtCount > 0 && (s_ExtOff < 53 || s_ExtOff + (long) s_ExtCount * c_EntrySize > s_Old.Length))
            return "ERROR: external value block out of bounds (unexpected layout)";
        if (s_ExtCount > 0 && BitConverter.ToUInt16(s_Old, (int) s_ExtOff + 0x24) != ExternalValues[0].Index)
            return "ERROR: external value block sanity check failed (entry layout mismatch)";

        // The block already ends the record (an earlier append put it there): it grows by the one entry in place. Otherwise it is
        // relocated to the tail once, the old copy left where it was (unreferenced) — eighty appends relocating it every time left
        // ~250 KB of dead copies per record.
        var s_AtTail = s_ExtCount > 0 && s_ExtOff + (long) s_ExtCount * c_EntrySize == s_Old.Length;
        var s_Out = new byte[s_Old.Length + (s_AtTail ? 1 : s_ExtCount + 1) * c_EntrySize];
        Array.Copy(s_Old, s_Out, s_Old.Length);

        var s_TailOff = s_AtTail ? (int) s_ExtOff : s_Old.Length;
        if (s_ExtCount > 0 && !s_AtTail)
            Array.Copy(s_Old, (int) s_ExtOff, s_Out, s_TailOff, s_ExtCount * c_EntrySize);

        var s_E = s_TailOff + s_ExtCount * c_EntrySize;
        Array.Copy(s_NameBytes, 0, s_Out, s_E, s_NameBytes.Length);
        BitConverter.GetBytes(p_Handle).CopyTo(s_Out, s_E + 0x20);
        BitConverter.GetBytes(p_Register).CopyTo(s_Out, s_E + 0x24);
        BitConverter.GetBytes(s_New.ArraySize).CopyTo(s_Out, s_E + 0x26);
        s_Out[s_E + 0x28] = s_New.Size;
        s_Out[s_E + 0x29] = 0;
        WriteVec4(s_Out, s_E + 0x2C, s_Default);

        BitConverter.GetBytes((ulong) (s_TailOff + 4)).CopyTo(s_Out, 20);
        s_Out[50] = (byte) (s_ExtCount + 1);
        BitConverter.GetBytes(s_Count).CopyTo(s_Out, 44);

        RawBytes = s_Out;
        ExternalValues = s_Model;
        ConstantCount = s_Count;
        return $"raw tail-append: external value block {(s_AtTail ? "grown in place" : "relocated")} at +{s_TailOff}, {s_ExtCount}+1 entries, " +
               $"{s_Count} constants, record {s_Old.Length} -> {s_Out.Length} B";
    }

    private static void WriteVec4(byte[] p_Buffer, int p_Offset, Vec4 p_Value)
    {
        BitConverter.GetBytes(p_Value.x).CopyTo(p_Buffer, p_Offset);
        BitConverter.GetBytes(p_Value.y).CopyTo(p_Buffer, p_Offset + 4);
        BitConverter.GetBytes(p_Value.z).CopyTo(p_Buffer, p_Offset + 8);
        BitConverter.GetBytes(p_Value.w).CopyTo(p_Buffer, p_Offset + 12);
    }

    /// <summary>
    /// Puts a SAMPLER STATE on this constant record at its register. Addressing and filtering are state of the
    /// shader kept HERE, not in the bytecode: a pixel stage that reads a texture through sN gets whatever this
    /// record says for N (the decals of a vehicle preset clamp, its camo repeats). A register the record already
    /// holds is overwritten in place (kept when identical); a new one goes through the authored writer when it
    /// proves fidelity, otherwise the sampler block (old entries + the new one) is appended at the record's tail
    /// and only that block's offset + count in the header are patched — the surgery of
    /// <see cref="AddExternalTextureConstant"/>. Returns a report line ("ERROR: ..." on failure).
    /// </summary>
    public string SetSamplerState(SamplerState p_State)
    {
        const int c_EntrySize = 0x40; // u32 Index + i32 Filter + 3*i32 Address + f32 Bias + i32 Aniso + i32 Cmp + 4*f32 Border + 2*f32 Lod + 8 pad
        var s_New = SamplerBytes(p_State);

        for (var i = 0; i < Samplers.Length; i++)
        {
            if (Samplers[i].Index != p_State.Index)
                continue;

            if (SamplerBytes(Samplers[i]).AsSpan().SequenceEqual(s_New))
                return $"s{p_State.Index} already {DescribeSampler(p_State)}: kept";

            var s_Previous = DescribeSampler(Samplers[i]);
            if (RawBytes != null)
            {
                var s_BlockOff = (long) BitConverter.ToUInt64(RawBytes, 36) - 4;
                var s_EntryOff = (int) s_BlockOff + i * c_EntrySize;
                if (s_BlockOff < 53 || s_EntryOff + c_EntrySize > RawBytes.Length ||
                    BitConverter.ToUInt32(RawBytes, s_EntryOff) != p_State.Index)
                    return "ERROR: sampler block layout mismatch on in-place replace";

                Array.Copy(s_New, 0, RawBytes, s_EntryOff, c_EntrySize);
            }

            Samplers[i] = p_State;
            return $"replaced s{p_State.Index}: {s_Previous} -> {DescribeSampler(p_State)} (in place)";
        }

        var s_Model = new SamplerState[Samplers.Length + 1];
        Array.Copy(Samplers, s_Model, Samplers.Length);
        s_Model[Samplers.Length] = p_State;

        if (RawBytes == null)
        {
            Samplers = s_Model;
            return $"authored constant: s{p_State.Index} {DescribeSampler(p_State)} appended to the model";
        }

        // Fidelity probe: does the authored writer reproduce this record byte-identically?
        byte[] s_Authored;
        var s_Saved = RawBytes;
        RawBytes = null;
        using (var s_Ms = new MemoryStream())
        {
            using (var s_W = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
            {
                s_W.Write((uint) 0);
                Serialize(s_W);
            }
            s_Authored = s_Ms.ToArray();
        }
        RawBytes = s_Saved;

        if (s_Authored.Length - 4 == RawBytes.Length && s_Authored.AsSpan(4).SequenceEqual(RawBytes))
        {
            Samplers = s_Model;
            RawBytes = null;
            return $"authored writer verified byte-identical -> s{p_State.Index} {DescribeSampler(p_State)} appended, record re-authored";
        }

        // Tail-append surgery on the raw record: block offset [4] @36 (size-field-relative), count @52.
        var s_Old = RawBytes;
        if (s_Old.Length < 53)
            return "ERROR: record too small to be a ShaderConstant";

        int s_Count = s_Old[52];
        var s_Off = (long) BitConverter.ToUInt64(s_Old, 36) - 4;
        if (s_Count != Samplers.Length)
            return $"ERROR: header sampler count {s_Count} != parsed {Samplers.Length}";
        if (s_Count > 0 && (s_Off < 53 || s_Off + (long) s_Count * c_EntrySize > s_Old.Length))
            return "ERROR: sampler block out of bounds (unexpected layout)";
        if (s_Count > 0 && BitConverter.ToUInt32(s_Old, (int) s_Off) != Samplers[0].Index)
            return "ERROR: sampler block sanity check failed (entry layout mismatch)";

        var s_Out = new byte[s_Old.Length + (s_Count + 1) * c_EntrySize];
        Array.Copy(s_Old, s_Out, s_Old.Length);

        var s_TailOff = s_Old.Length;
        if (s_Count > 0)
            Array.Copy(s_Old, (int) s_Off, s_Out, s_TailOff, s_Count * c_EntrySize);

        Array.Copy(s_New, 0, s_Out, s_TailOff + s_Count * c_EntrySize, c_EntrySize);

        BitConverter.GetBytes((ulong) (s_TailOff + 4)).CopyTo(s_Out, 36);
        s_Out[52] = (byte) (s_Count + 1);

        RawBytes = s_Out;
        Samplers = s_Model;
        return $"raw tail-append: s{p_State.Index} {DescribeSampler(p_State)}, sampler block relocated to +{s_TailOff}, " +
               $"{s_Count}+1 entries, record {s_Old.Length} -> {s_Out.Length} B";
    }

    /// <summary>A sampler state as the 64 bytes the record stores (see <see cref="SamplerState.Serialize"/>).</summary>
    public static byte[] SamplerBytes(SamplerState p_State)
    {
        using var s_Ms = new MemoryStream();
        using (var s_W = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
            p_State.Serialize(s_W);
        return s_Ms.ToArray();
    }

    /// <summary>"Clamp/Clamp/Wrap MinMagMipLinear aniso 1", the way the dumps print a sampler.</summary>
    public static string DescribeSampler(SamplerState p_State) =>
        $"{p_State.Desc.AddressU}/{p_State.Desc.AddressV}/{p_State.Desc.AddressW} {p_State.Desc.Filter} aniso {p_State.Desc.MaximumAnisotropy}";

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