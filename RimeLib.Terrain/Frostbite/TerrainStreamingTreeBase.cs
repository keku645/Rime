using System.Collections.Generic;
using System.Linq;
using RimeLib.Terrain.Frostbite.Destruction;
using RimeLib.Terrain.Frostbite.Heightfield;
using RimeLib.Terrain.Frostbite.TerrainMaterial;


namespace RimeLib.Terrain.Frostbite;
public abstract class TerrainStreamingTreeBase
{
    public HeightfieldNode RootNode { get; set; } = new HeightfieldNode();
    public uint UnblurredSamplesPerNodeSidePot { get; set; }
    public bool TrackTextureDetailFalloff { get; set; }
    public float InvisibleDetailReductionFactor { get; set; }
    public float OccludedDetailReductionFactor { get; set; }
    public uint ResourceBlurriness { get; set; }
    public uint NodeCount { get; set; }
    public bool FreeStreamingEnabled { get; set; }
    public List<RasterTree> RasterTrees { get; set; } = new List<RasterTree>();


    // The raster trees are stored in the order they appear in the resource, which does not
    // necessarily match their RasterTreeTypes value: a tree can be missing entirely (MP_Subway
    // ships no color tree at all). They are therefore looked up by their concrete type rather
    // than indexed by the enum value.
    public HeightfieldTreeBase? HeightfieldTree => RasterTrees.OfType<HeightfieldTreeBase>().FirstOrDefault();
    public TerrainMaterialTree? TerrainMaterialTree => RasterTrees.OfType<TerrainMaterialTree>().FirstOrDefault();
    public DestructionDepthTree? DestructionTree => RasterTrees.OfType<DestructionDepthTree>().FirstOrDefault();

    // The mask and color trees have no parser yet, so they are skipped while deserializing and
    // never end up in the list. Their raw payload is still preserved for round-tripping.
    public RasterTree? TerrainMaskTree => null;
    public RasterTree? TerrainColorTree => null;
}
