using fb;
using RimeLib.IO;
using System;


namespace RimeLib.Terrain.Frostbite.Heightfield;

public class HeightfieldTreeNode : RasterTreeNode
{
    public AxisAlignedBox BoundingBox { get; set; } = new AxisAlignedBox();
    public float SamplesPerMeter { get; set; }
    public byte[] EmbeddedData { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Offset of <see cref="EmbeddedData"/> relative to the start of the heightfield tree payload,
    /// or -1 when the node carries no embedded data. The tree is only parsed partially, so editing
    /// the height samples is done by patching them in place inside the raw payload, which requires
    /// knowing where they live.
    /// </summary>
    public long EmbeddedDataOffset { get; set; } = -1;

    /// <summary>
    /// Offset of the node bounding box relative to the start of the heightfield tree payload.
    /// </summary>
    public long BoundingBoxOffset { get; set; } = -1;

    // Warsaw addition
    public bool PartialNonPhysics { get; set; }

    public HeightfieldTreeNode()
    {

    }

    public HeightfieldTreeNode(RimeReader p_Reader)
    {
        BoundingBox = new AxisAlignedBox();

        // The padding isn't actually in the file so we have to read them out separately
        BoundingBox.min.x = p_Reader.ReadSingle();
        BoundingBox.min.y = p_Reader.ReadSingle();
        BoundingBox.min.z = p_Reader.ReadSingle();

        BoundingBox.max.x = p_Reader.ReadSingle();
        BoundingBox.max.y = p_Reader.ReadSingle();
        BoundingBox.max.z = p_Reader.ReadSingle();
    }
}

