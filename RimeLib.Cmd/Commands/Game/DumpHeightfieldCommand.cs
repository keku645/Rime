using System;
using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Terrain.Frostbite;
using RimeLib.Terrain.Frostbite.Heightfield;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Rasterizes a terrain streamingtree's HEIGHTFIELD quadtree (the persistent embedded R16 " +
                        "samples, deepest node wins) into a flat world grid and writes it as a raw file: " +
                        "'RHF1' + f32 minX, minZ, sizeX, sizeZ + u32 N + N*N f32 heights (row-major, Z rows). " +
                        "The real terrain height field — e.g. bathymetry for the water system (no props, no ships).")]
    public class DumpHeightfieldCommand : Command
    {
        [CommandArgument(Description = "The streamingtree resource name, e.g. levels/mp_017/terrain/mp_017_terrain.streamingtree")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Output raw file path")]
        public FileInfo? Output { get; set; }

        [CommandArgument(Description = "Output grid resolution per side (default 1024)", Optional = true)]
        public string? Resolution { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || Output == null)
            {
                p_Writer.WriteLine("Usage: dump_heightfield <streamingtree-resource> <out-file> [resolution]");
                return false;
            }
            int s_N = 1024;
            if (!string.IsNullOrWhiteSpace(Resolution) && int.TryParse(Resolution, out var s_R)) s_N = System.Math.Max(64, System.Math.Min(4096, s_R));

            var s_Mounter = ((GameContext)p_Context).GetMounter();
            if (!s_Mounter.TryGetResource(Name!, out var s_Resource) || s_Resource.FirstVariant == null)
            {
                p_Writer.WriteLine($"Could not find streamingtree resource ({Name}).");
                return false;
            }

            byte[] s_Bytes;
            using (var s_R0 = s_Resource.FirstVariant.GetReader())
                s_Bytes = s_R0.ReadBytes((int)s_R0.Length);

            var s_Tree = new RimeLib.Terrain.Frostbite2_0.TerrainStreamingTree();
            using (var s_Ms = new MemoryStream(s_Bytes))
            using (var s_Rd = new RimeReader(s_Ms, Endianness.LittleEndian, false))
                s_Tree.Deserialize(s_Rd);

            HeightfieldTreeBase? s_Hf = null;
            foreach (var s_Rt in s_Tree.RasterTrees)
                if (s_Rt is HeightfieldTreeBase s_H) { s_Hf = s_H; break; }
            if (s_Hf?.HeightfieldRootNode == null) { p_Writer.WriteLine("No heightfield tree / root in this streamingtree."); return false; }

            var s_Root = s_Hf.HeightfieldRootNode;
            float s_MinX = s_Root.BoundingBox.min.x, s_MinZ = s_Root.BoundingBox.min.z;
            float s_SizeX = s_Root.BoundingBox.max.x - s_MinX, s_SizeZ = s_Root.BoundingBox.max.z - s_MinZ;
            int s_S = (int)s_Hf.NodeSamplesPerSide;
            float s_YScale = s_Hf.WorldSizeY / 65535.0f;
            int s_DataNodes = 0;
            CountData(s_Root, ref s_DataNodes);
            p_Writer.WriteLine($"heightfield: root bbox x[{s_MinX}, {s_Root.BoundingBox.max.x}] z[{s_MinZ}, {s_Root.BoundingBox.max.z}] " +
                               $"samples/side={s_S} worldSizeY={s_Hf.WorldSizeY} levels<={s_Hf.LevelMax} nodesWithData={s_DataNodes}");

            var s_Out = new float[s_N * s_N];
            int s_Missed = 0;
            for (int zi = 0; zi < s_N; zi++)
            {
                float wz = s_MinZ + ((float)zi + 0.5f) / s_N * s_SizeZ;
                for (int xi = 0; xi < s_N; xi++)
                {
                    float wx = s_MinX + ((float)xi + 0.5f) / s_N * s_SizeX;
                    HeightfieldTreeNode? s_Best = null;
                    FindDeepest(s_Root, wx, wz, ref s_Best);
                    if (s_Best == null) { s_Missed++; s_Out[zi * s_N + xi] = 0.0f; continue; }
                    s_Out[zi * s_N + xi] = SampleNode(s_Best, wx, wz, s_S, s_YScale);
                }
            }
            if (s_Missed > 0) p_Writer.WriteLine($"WARNING: {s_Missed} cells had no data-bearing node (written as 0).");

            if (Output.Directory != null && !Output.Directory.Exists) Output.Directory.Create();
            using (var s_Fs = File.Create(Output.FullName))
            using (var s_Bw = new BinaryWriter(s_Fs))
            {
                s_Bw.Write(0x31464852u);   // 'RHF1'
                s_Bw.Write(s_MinX); s_Bw.Write(s_MinZ); s_Bw.Write(s_SizeX); s_Bw.Write(s_SizeZ);
                s_Bw.Write((uint)s_N);
                foreach (var v in s_Out) s_Bw.Write(v);
            }
            float s_Mn = float.MaxValue, s_Mx = float.MinValue; double s_Sum = 0;
            foreach (var v in s_Out) { if (v < s_Mn) s_Mn = v; if (v > s_Mx) s_Mx = v; s_Sum += v; }
            p_Writer.WriteLine($"Wrote {s_N}x{s_N} heights -> {Output.FullName} | h min={s_Mn:F1} mean={s_Sum / (s_N * (double)s_N):F1} max={s_Mx:F1}");
            return true;
        }

        static void CountData(HeightfieldTreeNode p_N, ref int p_C)
        {
            if (p_N.EmbeddedData != null && p_N.EmbeddedData.Length > 0) p_C++;
            if (p_N.Children != null)
                foreach (var c in p_N.Children)
                    if (c is HeightfieldTreeNode h) CountData(h, ref p_C);
        }

        // deepest node containing (x, z) that carries embedded height samples
        static void FindDeepest(HeightfieldTreeNode p_N, float x, float z, ref HeightfieldTreeNode? p_Best)
        {
            var bb = p_N.BoundingBox;
            if (x < bb.min.x || x > bb.max.x || z < bb.min.z || z > bb.max.z) return;
            if (p_N.EmbeddedData != null && p_N.EmbeddedData.Length > 0) p_Best = p_N;
            if (p_N.Children != null)
                foreach (var c in p_N.Children)
                    if (c is HeightfieldTreeNode h) FindDeepest(h, x, z, ref p_Best);
        }

        // bilinear R16 sample of one node's embedded grid (assumed to span the node bbox, row-major Z rows)
        static float SampleNode(HeightfieldTreeNode p_N, float x, float z, int p_S, float p_YScale)
        {
            var bb = p_N.BoundingBox;
            float u = (x - bb.min.x) / (bb.max.x - bb.min.x) * (p_S - 1);
            float v = (z - bb.min.z) / (bb.max.z - bb.min.z) * (p_S - 1);
            int iu = System.Math.Min(System.Math.Max((int)u, 0), p_S - 1), iv = System.Math.Min(System.Math.Max((int)v, 0), p_S - 1);
            int iu1 = System.Math.Min(iu + 1, p_S - 1), iv1 = System.Math.Min(iv + 1, p_S - 1);
            float tu = u - iu, tv = v - iv;
            float A = Raw(p_N, iv * p_S + iu, p_YScale),  B = Raw(p_N, iv * p_S + iu1, p_YScale);
            float C = Raw(p_N, iv1 * p_S + iu, p_YScale), D = Raw(p_N, iv1 * p_S + iu1, p_YScale);
            return (A + (B - A) * tu) * (1 - tv) + (C + (D - C) * tu) * tv;
        }

        static float Raw(HeightfieldTreeNode p_N, int p_Idx, float p_YScale)
        {
            int o = p_Idx * 2;
            if (o + 1 >= p_N.EmbeddedData.Length) return 0.0f;
            return (ushort)(p_N.EmbeddedData[o] | (p_N.EmbeddedData[o + 1] << 8)) * p_YScale;
        }
    }
}
