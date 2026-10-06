namespace RimeLib.Texture.Generation;

public record TextureAttributes
{
    public string Name { get; init; } = default!;
    public string TextureGroup { get; set; } = "Default";
    public bool SrgbGamma { get; set; } = false;
    // When false, the texture is generated FULLY RESIDENT (all mips in its own chunk, no Streaming flag).
    // Required when the texture is delivered self-contained in a mod bundle (the engine streaming pool
    // won't have the high-res mips) — otherwise only the small resident mips load (pale) or none (black).
    public bool Streaming { get; set; } = true;
    // When true, use the Frostbite NormalDXT1/DXN normal-map formats (correct normal reconstruction).
    public bool IsNormalMap { get; set; } = false;
    // UI textures need this. fb::UIScaleformTextureStreamingManager only ever reaches a texture through
    // its m_onDemandTextures map, and EVERY vanilla UI texture header carries flags 9 =
    // Streaming|OnDemandLoaded (measured on UI/Art/Persistence/Specializations/Camo/PremiumCamo_*).
    // Without the bit the resource binds by name and the image still never reaches the screen.
    public bool OnDemandLoaded { get; set; } = false;
    // Fixes the streaming chunk's guid instead of drawing a random one. Needed to SPLIT a generated
    // texture across two superbundles -- the header resource must ship NONCAS (in CAS the client's
    // bind(ITexture,name) returns NULL) while the pixel chunk ships CAS -- because the header in one
    // bundle has to point at the chunk built into the other.
    public string? ChunkId { get; set; }
}
