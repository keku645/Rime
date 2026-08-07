using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RimeLib.Frostbite;
using RimeLib.IO;
using RimeLib.IO.Conversion;

namespace RimeLib.Mesh.Frostbite
{
    /// <summary>
    /// Authors a complete, engine-valid Frostbite 2 (Battlefield 3) MeshSet resource — data
    /// section plus relocation table — from a populated <see cref="MeshSetLayout"/>.
    ///
    /// RIGID meshes only (no part/bone sub-blobs), which is exactly the class the PWC jet-ski
    /// body needs. Composite meshes (parts/bones, e.g. the RHIB) are not handled here.
    ///
    /// Format facts established by decoding shipped BF3 rigid meshes (m2_browning_mesh):
    ///  - Layout: [MeshSetLayout 0x70] [MeshLayout LODs, 0xA0 stride] [MeshSubset groups, each
    ///    16-aligned] [strings] [CategorySubsetIndices bytes] [relocation table].
    ///  - A rigid MeshLayout occupies 0xA0 (160) bytes in the file, NOT the 0xB0 (176) that
    ///    <see cref="MeshLayout.Serialize"/> emits: the trailing two u64 fields
    ///    (BoneShortNameArrayPartTransforms, SubsetPartIndices) do not exist for a rigid mesh.
    ///    We still serialize 176 bytes per LOD but advance the cursor by 160; the 16-byte tail
    ///    is overwritten by the next LOD's header (and the last LOD's tail by the first subset),
    ///    reproducing the shipped layout exactly.
    ///  - The relocation table is a flat list of little-endian u32 file offsets with NO count
    ///    header, at the very end of the resource. Each entry is the offset of a RelocPtr/
    ///    RelocArray BaseAddress field whose value is non-zero. Null pointers are omitted.
    ///    Order is irrelevant to the engine (it patches each listed offset in place).
    /// </summary>
    public static class MeshSetResourceWriter
    {
        const int HeaderSize   = 0x70; // MeshSetLayout
        const int RigidLodStride = 0xA0; // MeshLayout allocation for a rigid LOD
        const int LodSerSize   = 0xB0; // bytes MeshLayout.Serialize actually writes (176)
        const int SubsetSize   = 148;  // MeshSubset

        // Pointer-field offsets inside a MeshLayout (relative to the LOD base).
        const int LodSubsetsBA = 0x08;      // RelocArray<MeshSubset> BaseAddress
        const int LodCat0BA    = 0x14;      // CategorySubsetIndices[0] BaseAddress (+12 per index)
        const int LodShaderDbgBA = 0x70;
        const int LodNameBA    = 0x78;
        const int LodShortBA   = 0x80;
        // Pointer-field offsets inside a MeshSubset (relative to the subset base).
        const int SubMaterialBA = 0x08;
        // Pointer-field offsets inside the header.
        const int HdrLod0BA    = 0x30;      // +8 per LOD
        const int HdrNameBA    = 0x58;
        const int HdrShortBA   = 0x60;

        static int Align(int p_Off, int p_Align) => (p_Off + p_Align - 1) & ~(p_Align - 1);

        static byte[] Ser(IFbSerializable p_Obj)
        {
            using var s_Ms = new MemoryStream();
            using (var s_W = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
                p_Obj.Serialize(s_W);
            return s_Ms.ToArray();
        }

        /// <summary>
        /// Serializes the given rigid MeshSet layout to a complete resource byte buffer.
        /// </summary>
        /// <param name="p_Layout">A populated MeshSetLayout (rigid).</param>
        /// <returns>The resource bytes, ready to ship as a MeshSet resource.</returns>
        public static byte[] Write(MeshSetLayout p_Layout)
        {
            // Collect the present LODs and their original header slots (0..4).
            var s_Lods = new List<MeshLayout>();
            var s_Slot = new List<int>();
            for (var i = 0; i < 5; ++i)
            {
                var s_L = p_Layout.Lods[i]?.Object;
                if (s_L != null) { s_Lods.Add(s_L); s_Slot.Add(i); }
            }
            var s_N = s_Lods.Count;

            // ---------- PASS 1: assign every offset ----------
            var s_LodOff = new int[s_N];
            var s_Cur = HeaderSize;
            for (var i = 0; i < s_N; ++i) { s_LodOff[i] = s_Cur; s_Cur += RigidLodStride; }

            var s_SubOff = new int[s_N][];
            for (var i = 0; i < s_N; ++i)
            {
                s_Cur = Align(s_Cur, 16);
                var s_Subs = s_Lods[i].Subsets.Get;
                s_SubOff[i] = new int[s_Subs.Length];
                for (var j = 0; j < s_Subs.Length; ++j) { s_SubOff[i][j] = s_Cur; s_Cur += SubsetSize; }
            }

            // Strings: per-LOD (each subset material name, then shader-debug, LOD name, LOD short
            // name), then the header name and short name. Every pointer gets its own copy — no
            // sharing (the engine only needs a valid null-terminated string at each offset).
            s_Cur = Align(s_Cur, 16);
            var s_SubMatOff = new int[s_N][];
            var s_ShDbgOff  = new int[s_N];
            var s_LodNameOff = new int[s_N];
            var s_LodShortOff = new int[s_N];

            int PutStr(string p_Str)
            {
                var s_Off = s_Cur;
                s_Cur += Encoding.UTF8.GetByteCount(p_Str ?? string.Empty) + 1;
                return s_Off;
            }

            for (var i = 0; i < s_N; ++i)
            {
                var s_Subs = s_Lods[i].Subsets.Get;
                s_SubMatOff[i] = new int[s_Subs.Length];
                for (var j = 0; j < s_Subs.Length; ++j)
                    s_SubMatOff[i][j] = PutStr(s_Subs[j].MaterialName.Object);
                s_ShDbgOff[i]   = PutStr(s_Lods[i].ShaderDebugName.Object);
                s_LodNameOff[i] = PutStr(s_Lods[i].Name.Object);
                s_LodShortOff[i] = PutStr(s_Lods[i].ShortName.Object);
            }
            var s_HdrNameOff  = PutStr(p_Layout.Name.Object);
            var s_HdrShortOff = PutStr(p_Layout.ShortName.Object);

            // CategorySubsetIndices bytes (per LOD, 4 arrays). Zero-length arrays still get a
            // (non-zero) offset so they receive a relocation entry, matching the shipped format.
            s_Cur = Align(s_Cur, 16);
            var s_CatOff = new int[s_N][];
            for (var i = 0; i < s_N; ++i)
            {
                s_CatOff[i] = new int[4];
                for (var k = 0; k < 4; ++k)
                {
                    s_CatOff[i][k] = s_Cur;
                    s_Cur += (int)s_Lods[i].CategorySubsetIndices[k].Count;
                }
            }

            var s_PreReloc = s_Cur;
            var s_RelocStart = Align(s_PreReloc, 16);

            // ---------- PASS 2: set BaseAddresses + collect relocation offsets ----------
            var s_Reloc = new List<int>();

            // Header.
            for (var i = 0; i < s_N; ++i)
            {
                p_Layout.Lods[s_Slot[i]].BaseAddress = (ulong)s_LodOff[i];
                s_Reloc.Add(HdrLod0BA + s_Slot[i] * 8);
            }
            p_Layout.Name.BaseAddress = (ulong)s_HdrNameOff;   s_Reloc.Add(HdrNameBA);
            p_Layout.ShortName.BaseAddress = (ulong)s_HdrShortOff; s_Reloc.Add(HdrShortBA);

            // LODs + subsets.
            for (var i = 0; i < s_N; ++i)
            {
                var s_Lod = s_Lods[i];
                var s_Base = s_LodOff[i];
                var s_Subs = s_Lod.Subsets.Get;

                s_Lod.Subsets.Count = (uint)s_Subs.Length;
                s_Lod.Subsets.BaseAddress = (ulong)s_SubOff[i][0 < s_Subs.Length ? 0 : 0];
                if (s_Subs.Length == 0) s_Lod.Subsets.BaseAddress = (ulong)s_LodOff[i]; // degenerate guard
                s_Reloc.Add(s_Base + LodSubsetsBA);

                for (var k = 0; k < 4; ++k)
                {
                    s_Lod.CategorySubsetIndices[k].BaseAddress = (ulong)s_CatOff[i][k];
                    s_Reloc.Add(s_Base + LodCat0BA + k * 12);
                }

                s_Lod.ShaderDebugName.BaseAddress = (ulong)s_ShDbgOff[i]; s_Reloc.Add(s_Base + LodShaderDbgBA);
                s_Lod.Name.BaseAddress = (ulong)s_LodNameOff[i];         s_Reloc.Add(s_Base + LodNameBA);
                s_Lod.ShortName.BaseAddress = (ulong)s_LodShortOff[i];   s_Reloc.Add(s_Base + LodShortBA);

                for (var j = 0; j < s_Subs.Length; ++j)
                {
                    s_Subs[j].MaterialName.BaseAddress = (ulong)s_SubMatOff[i][j];
                    s_Reloc.Add(s_SubOff[i][j] + SubMaterialBA);
                }
            }

            // ---------- PASS 3: write the buffer ----------
            var s_Total = s_RelocStart + s_Reloc.Count * 4;
            var s_Buf = new byte[s_Total];

            void WriteAt(int p_Off, byte[] p_Bytes) => Array.Copy(p_Bytes, 0, s_Buf, p_Off, p_Bytes.Length);
            void WriteStrAt(int p_Off, string p_Str)
            {
                var s_B = Encoding.UTF8.GetBytes(p_Str ?? string.Empty);
                Array.Copy(s_B, 0, s_Buf, p_Off, s_B.Length);
                s_Buf[p_Off + s_B.Length] = 0;
            }

            // Header + LOD structs + subset structs (BaseAddresses now set).
            WriteAt(0, Ser(p_Layout));
            for (var i = 0; i < s_N; ++i)
            {
                WriteAt(s_LodOff[i], Ser(s_Lods[i]));       // 176 bytes; tail overlap is overwritten next
                var s_Subs = s_Lods[i].Subsets.Get;
                for (var j = 0; j < s_Subs.Length; ++j)
                    WriteAt(s_SubOff[i][j], Ser(s_Subs[j]));
            }

            // Strings.
            for (var i = 0; i < s_N; ++i)
            {
                var s_Subs = s_Lods[i].Subsets.Get;
                for (var j = 0; j < s_Subs.Length; ++j)
                    WriteStrAt(s_SubMatOff[i][j], s_Subs[j].MaterialName.Object);
                WriteStrAt(s_ShDbgOff[i], s_Lods[i].ShaderDebugName.Object);
                WriteStrAt(s_LodNameOff[i], s_Lods[i].Name.Object);
                WriteStrAt(s_LodShortOff[i], s_Lods[i].ShortName.Object);
            }
            WriteStrAt(s_HdrNameOff, p_Layout.Name.Object);
            WriteStrAt(s_HdrShortOff, p_Layout.ShortName.Object);

            // Category subset indices bytes.
            for (var i = 0; i < s_N; ++i)
                for (var k = 0; k < 4; ++k)
                {
                    var s_Data = s_Lods[i].CategorySubsetIndices[k].Get;
                    if (s_Data != null && s_Data.Length > 0)
                        WriteAt(s_CatOff[i][k], s_Data);
                }

            // Relocation table (flat u32 LE, no header).
            var s_R = s_RelocStart;
            foreach (var s_Entry in s_Reloc)
            {
                s_Buf[s_R + 0] = (byte)(s_Entry      & 0xFF);
                s_Buf[s_R + 1] = (byte)(s_Entry >> 8 & 0xFF);
                s_Buf[s_R + 2] = (byte)(s_Entry >> 16 & 0xFF);
                s_Buf[s_R + 3] = (byte)(s_Entry >> 24 & 0xFF);
                s_R += 4;
            }

            return s_Buf;
        }
    }
}
