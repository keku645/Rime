using RimeLib.Frostbite.Core;
using System;

namespace RimeLib.Terrain.Frostbite.Heightfield;
public class HeightfieldNode
{
    public HeightfieldTreeNode TreeNode { get; set; } = new HeightfieldTreeNode();

    public QuadtreeNodeId ID { get; set; } = new QuadtreeNodeId();

    public GUID Lod0ChunkID { get; set; } = GUID.Empty;
    public uint Lod0ChunkSize { get; set; }

    // Stored explicitly instead of being derived from Lod1ChunkID: the enabled flag is what the
    // resource actually holds, and deriving it would not round-trip a node whose lod1 chunk id
    // happens to be all zeroes.
    public bool Lod1Enabled { get; set; }
    public GUID Lod1ChunkID { get; set; } = GUID.Empty;
    public uint Lod1ChunkSize { get; set; }

    public bool PersistentDedicatedServer { get; set; }

    public HeightfieldNode[] Children { get; set; } = Array.Empty<HeightfieldNode>();
}

