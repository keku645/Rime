using RimeLib.Frostbite;
using RimeLib.Frostbite.Core;
using RimeLib.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RimeLib.Terrain.Frostbite;
using RimeLib.Terrain.Frostbite.Destruction;
using RimeLib.Terrain.Frostbite.Heightfield;
using RimeLib.Terrain.Frostbite.TerrainMaterial;

namespace RimeLib.Terrain.Frostbite2_0
{
    public class TerrainStreamingTree : TerrainStreamingTreeBase, IFbSerializable
    {
        /// <summary>
        /// A raster tree kept exactly as it appears in the resource.
        /// Only the heightfield, terrain material and destruction depth trees have a parser, and
        /// even those do not consume their whole payload yet, so the untouched bytes are kept here
        /// to be able to write the resource back out byte-for-byte.
        /// </summary>
        public class RawRasterTree
        {
            public RasterTree.RasterTreeTypes Type { get; set; }
            public byte[] Data { get; set; } = Array.Empty<byte>();
        }

        /// <summary>
        /// The raster trees in the exact order they appear in the resource.
        /// </summary>
        public List<RawRasterTree> RawRasterTrees { get; set; } = new List<RawRasterTree>();

        private HeightfieldNode LoadNodes(RimeReader p_Reader, uint p_NodeIndex, ref uint p_FirstFreeNodeIndex, QuadtreeNodeId p_NodeId)
        {
            var s_Node = new HeightfieldNode();

            s_Node.ID = p_NodeId;
            s_Node.Lod0ChunkSize = p_Reader.ReadUInt32();
            s_Node.Lod0ChunkID = new GUID(p_Reader);

            // The heightfield tree is only partially parsed, so the matching node is not guaranteed
            // to exist. It is only a convenience link, so a missing one must not fail the load.
            s_Node.TreeNode = (HeightfieldTree as HeightfieldTree)?.TryFindNode(p_NodeId) ?? new HeightfieldTreeNode();

            s_Node.Lod1Enabled = p_Reader.ReadBool();

            if (s_Node.Lod1Enabled)
            {
                s_Node.Lod1ChunkSize = p_Reader.ReadUInt32();
                s_Node.Lod1ChunkID = new GUID(p_Reader);
            }

            s_Node.PersistentDedicatedServer = p_Reader.ReadBool();

            var s_HasChildren = p_Reader.ReadBool();

            if (!s_HasChildren)
                return s_Node;

            // Parse children nodes.
            s_Node.Children = new HeightfieldNode[4];

            var s_FirstChildIndex = p_FirstFreeNodeIndex + 4;
            s_FirstChildIndex += 4;

            for (var i = 0; i < 4; ++i)
            {
                var s_ChildNodeId = new QuadtreeNodeId(p_NodeId);
                ++s_ChildNodeId.Level;

                s_ChildNodeId.IndexX = (ushort)(QuadtreeNodeId.m_QuadtreeNodeChildOffsetX[i] + 2 * s_ChildNodeId.IndexX);
                s_ChildNodeId.IndexY = (ushort)(QuadtreeNodeId.m_QuadtreeNodeChildOffsetY[i] + 2 * s_ChildNodeId.IndexY);

                s_Node.Children[i] = LoadNodes(p_Reader, (uint)(i + s_FirstChildIndex), ref p_FirstFreeNodeIndex, s_ChildNodeId);
            }

            return s_Node;
        }

        private void SaveNodes(RimeWriter p_Writer, HeightfieldNode p_Node)
        {
            p_Writer.Write(p_Node.Lod0ChunkSize);

            if (!p_Node.Lod0ChunkID.Serialize(p_Writer))
                throw new InvalidDataException("Failed to write the lod0 chunk id.");

            p_Writer.Write(p_Node.Lod1Enabled);

            if (p_Node.Lod1Enabled)
            {
                p_Writer.Write(p_Node.Lod1ChunkSize);

                if (!p_Node.Lod1ChunkID.Serialize(p_Writer))
                    throw new InvalidDataException("Failed to write the lod1 chunk id.");
            }

            p_Writer.Write(p_Node.PersistentDedicatedServer);

            var s_HasChildren = p_Node.Children.Length != 0;
            p_Writer.Write(s_HasChildren);

            if (!s_HasChildren)
                return;

            if (p_Node.Children.Length != 4)
                throw new InvalidDataException($"A quadtree node must have exactly 4 children, got {p_Node.Children.Length}.");

            foreach (var s_Child in p_Node.Children)
                SaveNodes(p_Writer, s_Child);
        }

        public bool Serialize(RimeWriter p_Writer)
        {
            p_Writer.Write(UnblurredSamplesPerNodeSidePot);
            p_Writer.Write(TrackTextureDetailFalloff);
            p_Writer.Write(InvisibleDetailReductionFactor);
            p_Writer.Write(OccludedDetailReductionFactor);
            p_Writer.Write(ResourceBlurriness);
            p_Writer.Write(NodeCount);
            p_Writer.Write(FreeStreamingEnabled);

            foreach (var s_RasterTree in RawRasterTrees)
            {
                p_Writer.Write((byte)s_RasterTree.Type);
                p_Writer.Write((uint)s_RasterTree.Data.Length);
                p_Writer.Write(s_RasterTree.Data);
            }

            // The list is terminated by the invalid type on its own. There is no size and no
            // padding behind it -- the nodes start immediately (verified against MP_Subway,
            // MP_013 and XP5_001, which all consume their resource exactly).
            p_Writer.Write((byte)RasterTree.RasterTreeTypes.RasterTreeTypeInvalid);

            SaveNodes(p_Writer, RootNode);

            return true;
        }

        public bool Serialize([NotNullWhen(true)] out byte[]? p_Data)
        {
            p_Data = null;

            using var s_Stream = new MemoryStream();
            using (var s_Writer = new RimeWriter(s_Stream, p_ShouldDispose: false))
            {
                if (!Serialize(s_Writer))
                    return false;
            }

            p_Data = s_Stream.ToArray();
            return true;
        }

        public void Deserialize(RimeReader p_Reader)
        {
            var s_StartPosition = p_Reader.Position;

            UnblurredSamplesPerNodeSidePot = p_Reader.ReadUInt32();
            TrackTextureDetailFalloff = p_Reader.ReadBool();
            InvisibleDetailReductionFactor = p_Reader.ReadSingle();
            OccludedDetailReductionFactor = p_Reader.ReadSingle();
            ResourceBlurriness = p_Reader.ReadUInt32();
            NodeCount = p_Reader.ReadUInt32();
            FreeStreamingEnabled = p_Reader.ReadBool();

            RasterTrees = new List<RasterTree>();
            RawRasterTrees = new List<RawRasterTree>();

            for (; ; )
            {
                var s_RasterTreeType = (RasterTree.RasterTreeTypes)p_Reader.ReadByte();
                if (s_RasterTreeType == RasterTree.RasterTreeTypes.RasterTreeTypeInvalid)
                    break;

                var s_RasterTreeLoadSize = p_Reader.ReadUInt32();

                Debug.WriteLine("Parsing '{0}' raster tree with size '{1}'.", s_RasterTreeType, s_RasterTreeLoadSize);

                var s_InitialPosition = p_Reader.Position;

                if (s_RasterTreeLoadSize < 1)
                    throw new InvalidDataException("Raster tree load size is invalid.");

                // Keep the payload verbatim so the resource can be written back out unchanged,
                // then rewind and let the (partial) parsers run over it.
                var s_RawData = p_Reader.ReadBytes((int)s_RasterTreeLoadSize);
                p_Reader.Seek(s_InitialPosition, SeekOrigin.Begin);

                RawRasterTrees.Add(new RawRasterTree
                {
                    Type = s_RasterTreeType,
                    Data = s_RawData
                });

                if (s_RasterTreeType == RasterTree.RasterTreeTypes.HeightfieldTreeType)
                {
                    var s_Tree = new HeightfieldTree();
                    s_Tree.Deserialize(p_Reader);
                    RasterTrees.Add(s_Tree);
                }
                else if (s_RasterTreeType == RasterTree.RasterTreeTypes.TerrainMaterialTreeType)
                {
                    var s_Tree = new TerrainMaterialTree();
                    s_Tree.Deserialize(p_Reader);
                    RasterTrees.Add(s_Tree);
                }
                else if (s_RasterTreeType == RasterTree.RasterTreeTypes.DestructionDepthTreeType)
                {
                    var s_Tree = new DestructionDepthTree();
                    s_Tree.Deserialize(p_Reader);
                    RasterTrees.Add(s_Tree);
                }
                else
                {
                    Debug.WriteLine("Skipping raster tree loading of type '{0}'.", s_RasterTreeType);
                }

                Debug.WriteLine("Read '{0}' bytes.", p_Reader.Position - s_InitialPosition);

                // None of the parsers consume their tree completely, so always resynchronize on the
                // declared size instead of trusting where they left the stream.
                p_Reader.Seek(s_InitialPosition + s_RasterTreeLoadSize, SeekOrigin.Begin);
            }

            var s_RootNode = new QuadtreeNodeId()
            {
                IndexY = 0,
                IndexX = 0,
                Level = 0
            };

            uint s_FirstFreeNodeIndex = 1;
            RootNode = LoadNodes(p_Reader, 0, ref s_FirstFreeNodeIndex, s_RootNode);

            Debug.WriteLine("Read {0} out of {1} bytes.", p_Reader.Position - s_StartPosition, p_Reader.BaseStream.Length - s_StartPosition);
        }

        public void Deserialize(byte[] p_Data)
        {
            using var s_Stream = new MemoryStream(p_Data);
            using var s_Reader = new RimeReader(s_Stream);

            Deserialize(s_Reader);
        }
    }

}
