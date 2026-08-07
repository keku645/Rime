using System;
using RimeLib.Frostbite;
using RimeLib.IO;
using System.Diagnostics.CodeAnalysis;
using fb;
using RimeLib.Shader.Frostbite2_0.Frostbite.Solutions;
using System.Collections.Generic;

namespace RimeLib.Shader.Frostbite2_0.Frostbite;

public class SurfaceShaderInfo
{
    public class StreamableTextureBase
    {
        public ShaderTextureCoordType CoordType { get; set; } = ShaderTextureCoordType.ShaderTextureCoordType_Unknown;
        public VertexElementUsage VertexUsage { get; set; } = VertexElementUsage.VertexElementUsage_Unknown;
        public float Factor { get; set; }
    }
    
    public class StreamableTexture : StreamableTextureBase
    {
        public string Name { get; set; } = string.Empty;

        public StreamableTexture()
        {
        }

        public StreamableTexture(RimeReader p_Reader)
        {
            Name = p_Reader.ReadNullTerminatedString();

            CoordType = (ShaderTextureCoordType)p_Reader.ReadUInt32();
            VertexUsage = (VertexElementUsage)p_Reader.ReadUInt32();
            Factor = p_Reader.ReadSingle();
        }
    }

    public class StreamableExternalTexture : StreamableTextureBase
    {
        public string ParameterName { get; set; } = string.Empty;
        public uint ParameterId { get; set; }

        public StreamableExternalTexture()
        {
        }

        public StreamableExternalTexture(RimeReader p_Reader)
        {
            ParameterName = p_Reader.ReadNullTerminatedString();
            ParameterId = p_Reader.ReadUInt32();

            CoordType = (ShaderTextureCoordType)p_Reader.ReadUInt32();
            VertexUsage = (VertexElementUsage)p_Reader.ReadUInt32();
            Factor = p_Reader.ReadSingle();
        }
    }

    public SurfaceShaderType SurfaceShaderType { get; set; } = SurfaceShaderType.SurfaceShaderType_Opaque;

    public byte Flags { get; set; } = 0;
    public byte BoolParameterCount { get; set; } = 0;
    public byte BoolParameterDefaultMask { get; set; } = 0;
    public byte BoolParameterRequiredMask { get; set; } = 0;

    public uint[] BoolParameterIds { get; set; } = new uint[8];

    public StreamableTexture[] StreamableTextures { get; set; } = Array.Empty<StreamableTexture>();

    public StreamableExternalTexture[] StreamableExternalTextures { get; set; } = Array.Empty<StreamableExternalTexture>();

    public ShaderSolution[] Solutions { get; set; } = Array.Empty<ShaderSolution>();

    public Dictionary<ulong, ShaderSolution> SolutionMap { get; } = new();


    public uint NameHash { get; internal set; } = 0;

    public SurfaceShaderInfo()
    {
    }

    public SurfaceShaderInfo(RimeReader p_Reader, ShaderSolution[] p_Solutions, uint? p_NameHash = null)
    {
        SurfaceShaderType = (SurfaceShaderType)p_Reader.ReadUInt32();

        Flags = p_Reader.ReadUByte();
        BoolParameterCount = p_Reader.ReadUByte();
        BoolParameterDefaultMask = p_Reader.ReadUByte();
        BoolParameterRequiredMask = p_Reader.ReadUByte();

        for (var i = 0; i < 8; i++)
            BoolParameterIds[i] = p_Reader.ReadUInt32();

        var s_StreamableTexturesCount = p_Reader.ReadUInt32();
        StreamableTextures = new StreamableTexture[s_StreamableTexturesCount];
        for (var i = 0; i < s_StreamableTexturesCount; i++)
            StreamableTextures[i] = new StreamableTexture(p_Reader);

        var s_StreamableExternalTexturesCount = p_Reader.ReadUInt32();
        StreamableExternalTextures = new StreamableExternalTexture[s_StreamableExternalTexturesCount];
        for (var i = 0; i < s_StreamableExternalTexturesCount; i++)
            StreamableExternalTextures[i] = new StreamableExternalTexture(p_Reader);

        var s_SolutionCount = p_Reader.ReadUInt32();
        Solutions = new ShaderSolution[s_SolutionCount];

        for (var i = 0; i < s_SolutionCount; i++)
        {
            var s_Solution = p_Solutions[p_Reader.ReadUInt16()];

            Solutions[i] = s_Solution;
            SolutionMap.Add(s_Solution.StateHash, s_Solution);
        }

        if (p_NameHash != null)
            NameHash = (uint) p_NameHash;
    }

    // Mirror of the reader: u32 SurfaceShaderType | 4 * u8 (Flags, BoolParamCount, DefaultMask, RequiredMask) |
    // 8 * u32 BoolParameterIds | u32 count * StreamableTexture | u32 count * StreamableExternalTexture |
    // u32 count * u16 (index into the database's full solution array).
    public bool Serialize(RimeWriter p_Writer, ShaderSolution[] p_AllSolutions)
    {
        p_Writer.Write((uint) SurfaceShaderType);

        p_Writer.Write(Flags);
        p_Writer.Write(BoolParameterCount);
        p_Writer.Write(BoolParameterDefaultMask);
        p_Writer.Write(BoolParameterRequiredMask);

        for (var i = 0; i < 8; i++)
            p_Writer.Write(BoolParameterIds[i]);

        p_Writer.Write((uint) StreamableTextures.Length);
        foreach (var s_Texture in StreamableTextures)
        {
            p_Writer.WriteNullTerminatedString(s_Texture.Name);
            p_Writer.Write((uint) s_Texture.CoordType);
            p_Writer.Write((uint) s_Texture.VertexUsage);
            p_Writer.Write(s_Texture.Factor);
        }

        p_Writer.Write((uint) StreamableExternalTextures.Length);
        foreach (var s_Texture in StreamableExternalTextures)
        {
            p_Writer.WriteNullTerminatedString(s_Texture.ParameterName);
            p_Writer.Write(s_Texture.ParameterId);
            p_Writer.Write((uint) s_Texture.CoordType);
            p_Writer.Write((uint) s_Texture.VertexUsage);
            p_Writer.Write(s_Texture.Factor);
        }

        p_Writer.Write((uint) Solutions.Length);
        foreach (var s_Solution in Solutions)
            p_Writer.Write((ushort) Array.IndexOf(p_AllSolutions, s_Solution));

        return true;
    }


}