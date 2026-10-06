using System;
using System.Collections.Generic;
using System.Text;

namespace RimeLib.Shader.Frostbite2_0.Frostbite;

/// <summary>
/// The vertex-shader half of the emblem PROJECTED on a weapon (keku 2026-09-29: "que pueda poner el sticker donde quiera y que sea
/// calculado acorde para luego el emblema"; "las armas en BF4 también proyectan emblemas"). BF4 draws a weapon's emblem through a
/// second, UNIQUE unwrap its meshes carry (TexCoord1: 0 % shared texels, against 30 % in the texture's own unwrap); a BF3 mesh has only
/// the shared one, so the unique coordinate is computed instead, from the vertex's position in the weapon's own space — which the
/// pixel shader never gets: the weapon's vertex shader hands over the world position, the tangent rows and the UV.
///
/// MEASURED on WeaponPresetShadowFP (xp2_skybar, the three colour-pass vertex shaders of the M416's declaration 0xCE4574FD): the
/// output that receives the UV (`mov oK.xy, v5.xyxx`) marks the layout — oK-4 carries the world position and oK-3..oK-1 the tangent
/// rows, and the .w of every one of them is written `mov oN.w, l(0)`, a component the pixel shader never reads. The patch turns the
/// first three into `mov oN.w, v0.x|y|z` (v0 = the position input, the mesh's own space before the bone transform): the same two
/// dwords, so nothing moves but the container checksum. A vertex shader without that pattern (the depth-only ones) is left alone.
/// </summary>
public static class DxbcMeshPosition
{
    private const int c_OpMov = 0x36;
    private const int c_OpCustomData = 0x35;
    private const int c_TypeInput = 1;
    private const int c_TypeOutput = 2;
    private const int c_TypeImmediate32 = 4;

    /// <summary>Patches a copy of a vertex shader; null (and why, in <paramref name="p_Report"/>) when it does not have the pattern.</summary>
    public static byte[]? Patch(byte[] p_Bytecode, out string p_Report)
    {
        var s_Data = (byte[]) p_Bytecode.Clone();
        var s_Movs = new List<(int At, int Length, uint Register, uint Mask, int Source, uint SourceType, uint SourceCount, uint SourceDims)>();
        foreach (var (s_At, s_Opcode, s_Length) in Instructions(s_Data))
        {
            if (s_Opcode != c_OpMov)
                continue;

            var s_Dest = U32(s_Data, s_At + 4);
            if (((s_Dest >> 12) & 0xFF) != c_TypeOutput || ((s_Dest >> 20) & 3) != 1 || (s_Dest >> 31) != 0)
                continue;

            var s_Source = s_At + 12;
            var s_SourceToken = U32(s_Data, s_Source);
            s_Movs.Add((s_At, s_Length, U32(s_Data, s_At + 8), (s_Dest >> 4) & 0xF, s_Source, (s_SourceToken >> 12) & 0xFF,
                s_SourceToken & 3, (s_SourceToken >> 20) & 3));
        }

        var s_Uv = s_Movs.FindAll(p_M => p_M.SourceType == c_TypeInput && p_M.SourceDims == 1 && p_M.Mask == 0b0011 &&
                                         U32(s_Data, p_M.Source + 4) == 5);
        if (s_Uv.Count != 1)
        {
            p_Report = $"no single `mov oK.xy, v5` ({s_Uv.Count})";
            return null;
        }

        var s_K = s_Uv[0].Register;
        var s_Done = new StringBuilder($"uv in o{s_K}");
        for (var s_Component = 0u; s_Component < 3; s_Component++)
        {
            var s_Register = s_K - 4 + s_Component;
            var s_Hits = s_Movs.FindAll(p_M => p_M.Register == s_Register && p_M.Mask == 0b1000 && p_M.SourceType == c_TypeImmediate32 &&
                                               p_M.SourceCount == 1 && p_M.Length == 5);
            if (s_Hits.Count != 1 || BitConverter.ToSingle(s_Data, s_Hits[0].Source + 4) != 0f)
            {
                p_Report = $"o{s_Register}.w is not written once as l(0) ({s_Hits.Count})";
                return null;
            }

            // an input operand: 4 components, select-1 of the component, type INPUT, one immediate index (v0)
            var s_Token = 2u | (2u << 2) | (s_Component << 4) | ((uint) c_TypeInput << 12) | (1u << 20);
            Put(s_Data, s_Hits[0].Source, s_Token);
            Put(s_Data, s_Hits[0].Source + 4, 0);
            s_Done.Append($"; o{s_Register}.w <- v0.{"xyz"[(int) s_Component]}");
        }

        Checksum(s_Data).CopyTo(s_Data, 4);
        p_Report = s_Done.ToString();
        return s_Data;
    }

    /// <summary>
    /// Whether a vertex shader already carries the patch — an OUTPUT's .w taking each of v0.x, v0.y and v0.z — so a chained bake does not
    /// patch twice. (Any one `mov ?.w, v0` is not enough: a temp can take one in a game's own shader, and a false "already" would leave the
    /// shader unpatched in silence.)
    /// </summary>
    public static bool IsPatched(byte[] p_Bytecode)
    {
        var s_Components = 0;
        foreach (var (s_At, s_Opcode, _) in Instructions(p_Bytecode))
        {
            if (s_Opcode != c_OpMov)
                continue;

            var s_Dest = U32(p_Bytecode, s_At + 4);
            var s_Source = U32(p_Bytecode, s_At + 12);
            if (((s_Dest >> 12) & 0xFF) == c_TypeOutput && ((s_Dest >> 4) & 0xF) == 0b1000 &&
                ((s_Source >> 12) & 0xFF) == c_TypeInput && ((s_Source >> 2) & 3) == 2 && U32(p_Bytecode, s_At + 16) == 0)
                s_Components |= 1 << (int) ((s_Source >> 4) & 3);
        }

        return (s_Components & 0b111) == 0b111;
    }

    /// <summary>The DXBC container checksum: MD5 over bytes 20..end with the container's own final blocks (bit count first and last).</summary>
    public static byte[] Checksum(byte[] p_Data)
    {
        var s_Length = p_Data.Length - 20;
        var s_Bits = (uint) s_Length * 8;
        var s_Bits2 = (s_Bits >> 2) | 1;
        uint[] s_State = { 0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476 };
        var s_Full = s_Length - s_Length % 64;
        var s_Block = new byte[64];
        for (var s_At = 0; s_At < s_Full; s_At += 64)
        {
            Array.Copy(p_Data, 20 + s_At, s_Block, 0, 64);
            Transform(s_State, s_Block);
        }

        var s_Left = s_Length - s_Full;
        Array.Clear(s_Block);
        if (s_Left >= 56)
        {
            Array.Copy(p_Data, 20 + s_Full, s_Block, 0, s_Left);
            s_Block[s_Left] = 0x80;
            Transform(s_State, s_Block);
            Array.Clear(s_Block);
            Put(s_Block, 0, s_Bits);
            Put(s_Block, 60, s_Bits2);
            Transform(s_State, s_Block);
        }
        else
        {
            Put(s_Block, 0, s_Bits);
            Array.Copy(p_Data, 20 + s_Full, s_Block, 4, s_Left);
            s_Block[4 + s_Left] = 0x80;
            Put(s_Block, 60, s_Bits2);
            Transform(s_State, s_Block);
        }

        var s_Out = new byte[16];
        for (var i = 0; i < 4; i++)
            Put(s_Out, i * 4, s_State[i]);
        return s_Out;
    }

    private static readonly int[] s_Shifts =
    {
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    };

    private static readonly uint[] s_Constants = BuildConstants();

    private static uint[] BuildConstants()
    {
        var s_K = new uint[64];
        for (var i = 0; i < 64; i++)
            s_K[i] = (uint) (long) System.Math.Floor(System.Math.Abs(System.Math.Sin(i + 1)) * 4294967296.0);
        return s_K;
    }

    private static void Transform(uint[] p_State, byte[] p_Block)
    {
        var s_M = new uint[16];
        for (var i = 0; i < 16; i++)
            s_M[i] = U32(p_Block, i * 4);

        uint a = p_State[0], b = p_State[1], c = p_State[2], d = p_State[3];
        for (var i = 0; i < 64; i++)
        {
            uint f;
            int g;
            if (i < 16) { f = (b & c) | (~b & d); g = i; }
            else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) % 16; }
            else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) % 16; }
            else { f = c ^ (b | ~d); g = (7 * i) % 16; }

            f = f + a + s_Constants[i] + s_M[g];
            a = d;
            d = c;
            c = b;
            b += (f << s_Shifts[i]) | (f >> (32 - s_Shifts[i]));
        }

        p_State[0] += a;
        p_State[1] += b;
        p_State[2] += c;
        p_State[3] += d;
    }

    /// <summary>The instructions of the SHEX/SHDR chunk: (byte offset, opcode, length in dwords).</summary>
    private static IEnumerable<(int At, int Opcode, int Length)> Instructions(byte[] p_Data)
    {
        var s_Count = (int) U32(p_Data, 28);
        for (var i = 0; i < s_Count; i++)
        {
            var s_Offset = (int) U32(p_Data, 32 + 4 * i);
            var s_Name = Encoding.ASCII.GetString(p_Data, s_Offset, 4);
            if (s_Name != "SHEX" && s_Name != "SHDR")
                continue;

            var s_Start = s_Offset + 8;
            var s_End = s_Start + (int) U32(p_Data, s_Offset + 4);
            var s_At = s_Start + 8;
            while (s_At < s_End)
            {
                var s_Token = U32(p_Data, s_At);
                var s_Opcode = (int) (s_Token & 0x7FF);
                var s_Length = s_Opcode == c_OpCustomData ? (int) U32(p_Data, s_At + 4) : (int) ((s_Token >> 24) & 0x7F);
                if (s_Length <= 0)
                    yield break;

                yield return (s_At, s_Opcode, s_Length);
                s_At += s_Length * 4;
            }

            yield break;
        }
    }

    private static uint U32(byte[] p_Data, int p_At) => BitConverter.ToUInt32(p_Data, p_At);

    private static void Put(byte[] p_Data, int p_At, uint p_Value) => BitConverter.GetBytes(p_Value).CopyTo(p_Data, p_At);
}
