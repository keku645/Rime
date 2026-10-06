using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Texture.Generation;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    [CommandDescription("Generates a new texture resource and chunks from a DDS texture and adds it to this bundle.")]
    public class AddDdsTextureCommand : Command
    {
        [CommandArgument(Description = "The name of the resource asset to create.")]
        public string? AssetName { get; set; }
        
        [CommandArgument(Description = "The path to the DDS texture.")]
        public FileInfo? FilePath { get; set; }
        
        [CommandArgument(Description = "Whether this texture should use sRGB gamma. Defaults to 'false'.", Optional = true)]
        public bool SrgbGamma { get; set; } = false;

        [CommandArgument(Description = "Whether the texture is streamable. Defaults to 'true'. Set 'false' for a fully-resident texture delivered self-contained in a mod bundle (else only small mips load -> pale/black).", Optional = true)]
        public bool Streaming { get; set; } = true;

        [CommandArgument(Description = "Whether this is a normal map (uses NormalDXT1/DXN formats). Defaults to 'false'.", Optional = true)]
        public bool NormalMap { get; set; } = false;

        // The engine's texture pools gate on the 16-char group at header offset 112 ("Vehicle",
        // "AtlasTextureGro", ...). A generated texture under a NEW name has no mounted original to
        // copy the group from, and "Default" is not a valid BF3 group -> the pool never uploads it
        // (black vehicles; purple/garbage emitter atlas). This argument sets it explicitly.
        [CommandArgument(Description = "TextureGroup override (16-char engine pool group, e.g. 'AtlasTextureGro'). Defaults to the mounted original's group (same-name lookup) or 'Default'.", Optional = true)]
        public string? TextureGroup { get; set; }

        [CommandArgument(Description = "Optional DONOR texture name whose TextureGroup is copied when the asset name " +
                                       "has no mounted original (a BRAND-NEW texture has no same-name original, and " +
                                       "'Default' is not a valid pool group -> black texture).", Optional = true)]
        public string? GroupDonor { get; set; }

        // A UI texture is never bound directly: fb::UIScaleformTextureStreamingManager pulls it through
        // m_onDemandTextures, and every vanilla UI texture ships flags 9 = Streaming|OnDemandLoaded.
        // Generated without the bit, the resource is found by name and the image still never appears.
        [CommandArgument(Description = "Whether the texture is loaded on demand. UI textures REQUIRE this (vanilla UI headers carry Streaming|OnDemandLoaded). Defaults to 'false'.", Optional = true)]
        public bool OnDemandLoaded { get; set; } = false;

        // Same texture, generated twice, one chunk: the header resource must ship from a NONCAS bundle and
        // the pixel chunk from a CAS one, so the two builds have to agree on the chunk's guid.
        [CommandArgument(Description = "Fixed streaming chunk guid (else random). Use to SPLIT the delivery: header resource in a NONCAS bundle, pixel chunk in a CAS one.", Optional = true)]
        public string? ChunkId { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (!FilePath!.Exists)
            {
                p_Writer.WriteLine("The specified file could not be found.");
                return false;
            }

            var s_Attributes = new TextureAttributes()
            {
                Name = AssetName!,
                TextureGroup = TextureGroup ?? "Default",
                SrgbGamma = SrgbGamma,
                Streaming = Streaming,
                IsNormalMap = NormalMap,
                OnDemandLoaded = OnDemandLoaded,
                ChunkId = ChunkId,
            };

            ((BundleBuildingContext) p_Context).AddDDSTexture(FilePath, s_Attributes, GroupDonor);

            return true;
        }
    }
}
