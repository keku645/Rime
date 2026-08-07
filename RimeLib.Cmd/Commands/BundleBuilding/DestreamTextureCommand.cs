using System.IO;
using System.Linq;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Content;
using RimeLib.Content.Frostbite;
using RimeLib.Texture;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    [CommandDescription("Delivers a STREAMING game texture fully-resident with its ORIGINAL bytes: takes the mounted 128-byte DxTexture header, clears TextureFlags.Streaming and MipmapBaseIndex (all mips resident), keeps format/group/nameHash intact, and adds the texture's COMPLETE cas chunk as a FULL-RANGE catalog SHA1 reference (0 shipped bytes, byte-exact DICE data). Replaces any resolve-pulled partial resource/ranged chunk of the same name. Only works for catalog-backed (base-game cas) textures. MODE (optional 2nd arg): default = header+chunk in THIS bundle — ⚠ the generated header ships as cas-bundle idata, which the MVDB texture-bind REJECTS (BLACK body law; by-name shader textures tolerate it). 'chunkonly' = only the full-range chunk (with h32 chunkMeta) + REMOVE the streaming resource — use in the CAS pack bundle. 'headeronly' = only the patched 128B header resource — use in the NONCAS annex bundle (the proven generated-resource delivery, v0.0.111 law); the chunk must ship via 'chunkonly' in a bundle that loads alongside.")]
    internal class DestreamTextureCommand : Command
    {
        [CommandArgument(Description = "The exact resource/asset name, e.g. vehicles/m1a2/m1a2_d.")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Optional mode: 'chunkonly' (CAS pack: full-range chunk + remove resource), 'headeronly' (NONCAS annex: patched header resource only), 'keepmips', or 'ondemand' (NONCAS annex, header patched to Streaming|OnDemandLoaded + mipBase 0 = TexturePool 2 = stays OUT of the turbo chunk path; pair with a FULL-CHAIN toc chunk). Default: both in this bundle.", Optional = true)]
        public string? Mode { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            var s_Ctx = p_Context as BundleBuildingContext;
            if (s_Ctx == null || string.IsNullOrWhiteSpace(Name))
            {
                p_Writer.WriteLine("usage (bundle ctx): destream_texture <name> [chunkonly|headeronly]");
                return false;
            }
            var s_Mode = (Mode ?? "").ToLowerInvariant();
            if (s_Mode != "" && s_Mode != "chunkonly" && s_Mode != "headeronly" && s_Mode != "keepmips" && s_Mode != "ondemand")
            {
                p_Writer.WriteLine($"destream_texture: unknown mode '{Mode}' (use chunkonly|headeronly|keepmips|ondemand|<empty>).");
                return false;
            }

            var s_SbCtx = (SbBuildingContext)s_Ctx.Parent!;
            var s_BaseCtx = (BaseContext)s_SbCtx.Parent!;
            var s_Mounter = s_BaseCtx.GetMounters().Values.FirstOrDefault() as RimeLib.Content.Frostbite2_0.Mounting.EngineMounter;
            if (s_Mounter == null)
            {
                p_Writer.WriteLine("destream_texture: no Frostbite2_0 mounter.");
                return false;
            }

            if (!s_Mounter.TryGetResource(Name!, out var s_Res))
            {
                p_Writer.WriteLine($"destream_texture: resource not found: {Name}");
                return false;
            }

            byte[] s_Header;
            using (var s_R = s_Res.FirstVariant.GetReader())
            {
                if (s_R.Length < 128)
                {
                    p_Writer.WriteLine($"destream_texture: {Name} is not a 128-byte DxTexture header ({s_R.Length}).");
                    return false;
                }
                s_Header = s_R.ReadBytes(128);
            }

            // patch: flags u32 @12 &= ~Streaming(0x1). Default modes also zero MipmapBaseIndex @27
            // (all mips resident, pair with the FULL chunk). 'keepmips' KEEPS mipBase and pairs with
            // the RETAIL-RANGED chunk: resident-from-mipBase, byte-coherent with the header, and the
            // texture never registers with the streaming system (no worker requests). Needed for
            // TEXTURE ARRAYS (dust_d): the full-resident frankenstein (mipBase 1->0 + full chunk)
            // crashes the streaming worker when aircraft canopies sample it, a shape retail never ships.
            // 'ondemand' keeps Streaming(0x1) AND sets OnDemandLoaded(0x8). That pair is what puts the
            // texture in TexturePool 2, and pool 2 is the ONLY value that keeps fb::ResourceManager::
            // beginChunkRead out of the turbo branch: the streaming manager derives entry[50] from these
            // very bits (0x004C42D0), and 0/1 both request the chunk through fb::turboLoaderRequestChunk,
            // whose index table is NULL outside a compartment's turbo-load window -> AV with no null check.
            // Pool 2 routes to the FileSuperBundleManager instead, i.e. through m_tocs/casChunks -> the
            // catalog lookup, which is the only path a cas-ref chunk can be served from.
            // Retail shape for this pair (censused over 13016 DxTexture headers): mipBase 0 on 3637/3637,
            // one mip on 3635/3637, chunk = the COMPLETE chain, always declared at superbundle-TOC level.
            // So pair 'ondemand' with a full-chain chunk (add_cas_toc_chunk), never with a ranged slice.
            //
            // ⚠ PROVEN IN-GAME NOT TO DELIVER PIXELS FOR MATERIAL TEXTURES — do not reach for this to fix
            // a black vehicle. The very bit that selects pool 2 is also the first thing sub_6C5760 tests,
            // and it returns a mip mask of 0 for it, so the texture is registered but never queued for
            // loading at all. Result: the turbo AV disappears and the client runs, but the textures bind
            // and draw black. Every retail user of this pair is UI (dogtags, minimaps, loading screens),
            // acquired explicitly by the UI code — nothing requests it for a mesh material. Keep it for
            // UI-shaped content or as a diagnostic; the vehicle-texture case needs an engine-side change.
            var s_Flags = System.BitConverter.ToUInt32(s_Header, 12);
            var s_NewFlags = s_Mode == "ondemand" ? (s_Flags | 0x9u) : (s_Flags & ~0x1u);
            System.BitConverter.GetBytes(s_NewFlags).CopyTo(s_Header, 12);
            var s_MipBase = s_Header[27];
            if (s_Mode != "keepmips")
                s_Header[27] = 0;

            if (s_Mode == "ondemand")
            {
                // Pool-2 UI delivery (dogtags/minimaps/loading screens in retail; every retail user of this
                // flag pair is UI). EVERY UI texture (flags 0x20/0x0 = pool 0) fetches its mip chunk via the
                // turbo path (fb::ResourceManager::beginChunkRead a7==2), which AVs on a null index outside a
                // load window (vu+0xc1c76) — INLINE mip chunks fetch through turbo too, they are NOT resident
                // (measured: leaving them pool-0 still crashed). So patch the header to pool 2 (file path)
                // AND put the mip chunk at superbundle-TOC level. Pool 2 routes through the FILE path
                // (sub_4CF340 = FileSuperBundleManager/m_tocs, always valid), which ONLY sees TOC chunks; the
                // bundle-manifest copy clone_bundle left is invisible there. Catalog-backed chunks ship as
                // 0-byte cas-refs; inline chunks (small UI icons, no cas.cat entry) ship their own payload at
                // TOC (tiny). The generated header ships as cas-idata, which by-name UI tolerates (the MVDB
                // material bind would reject it — BLACK-body law).
                var s_OdConverter = EngineInterfaceRegistry.Create<ITextureConverter>(s_SbCtx.EngineType);
                var s_OdProbe = new BundleBuildingContext.ResourceMemoryReader(s_Header, ResourceType.DxTexture, Name!);
                var s_OdChunkId = s_OdConverter.GetTextureChunkId(s_OdProbe);
                // The chunk MUST become a 0-byte cas-ref at superbundle-TOC level: a CAS sb's toc requires a
                // sha1 per chunk (a payload toc chunk null-derefs fb::FileSuperBundleManager::mount), and the
                // pool-2 file path only sees TOC chunks. TryGetChunkCatalogSha1 covers both catalog-backed
                // and inline-but-content-in-cas.cat chunks. If neither, leave the texture UNTOUCHED rather
                // than ship a pool-2 header whose chunk the file path can't reach.
                if (s_OdChunkId == RimeLib.Frostbite.Core.GUID.Empty
                    || !s_Mounter.TryGetChunkCatalogSha1(s_OdChunkId, out var s_OdSha1))
                {
                    p_Writer.WriteLine($"destream-ondemand {Name}: chunk {s_OdChunkId} NOT cas-ref-able — LEFT UNTOUCHED (needs another delivery).");
                    return true;
                }

                // patch the header to pool 2, drop the bundle-manifest copy, ship the chunk as a toc cas-ref
                try { s_Ctx.RemoveResource(Name!); } catch { }
                s_Ctx.AddGeneratedResource(Name!, s_Header, ResourceType.DxTexture, new byte[16]);
                try { s_Ctx.RemoveChunk(s_OdChunkId); } catch { }
                s_SbCtx.AddCasTocChunk(s_OdChunkId, s_OdSha1!);
                p_Writer.WriteLine($"destream-ondemand {Name}: flags 0x{s_Flags:X}->0x{s_NewFlags:X}, mipBase {s_MipBase}->0; toc cas-ref chunk {s_OdChunkId} -> {s_OdSha1}.");
                return true;
            }

            if (s_Mode == "headeronly")
            {
                // NONCAS annex delivery: the generated 128B header only (the chunk ships separately
                // via 'chunkonly' in the CAS pack). Generated resources render ONLY via noncas.
                try { s_Ctx.RemoveResource(Name!); } catch { }
                s_Ctx.AddGeneratedResource(Name!, s_Header, ResourceType.DxTexture, new byte[16]);
                p_Writer.WriteLine($"destream-header {Name}: flags 0x{s_Flags:X}->0x{s_NewFlags:X}, mipBase {s_MipBase}->0 (128B noncas resource).");
                return true;
            }

            // resolve the chunk id via the engine converter (correct guid byte order)
            var s_Converter = EngineInterfaceRegistry.Create<ITextureConverter>(s_SbCtx.EngineType);
            var s_Probe = new BundleBuildingContext.ResourceMemoryReader(s_Header, ResourceType.DxTexture, Name!);
            var s_ChunkId = s_Converter.GetTextureChunkId(s_Probe);
            if (s_ChunkId == RimeLib.Frostbite.Core.GUID.Empty)
            {
                p_Writer.WriteLine($"destream_texture: {Name} has no streaming chunk id.");
                return false;
            }

            if (s_Mode == "keepmips")
            {
                // RETAIL-RANGED chunk (persistent mips + logicalOffset + chunkMeta {h32, firstMip}),
                // matched by h32 — byte-coherent with the KEPT mipBase.
                if (!s_Mounter.TryGetTextureChunkRetailVariant(s_ChunkId, Name!, out var s_Retail))
                {
                    p_Writer.WriteLine($"destream_texture: {Name} chunk {s_ChunkId} has no retail h32 variant — keepmips unavailable.");
                    return false;
                }
                try { s_Ctx.RemoveResource(Name!); } catch { }
                try { s_Ctx.RemoveChunk(s_ChunkId); } catch { }
                s_Ctx.AddGeneratedResource(Name!, s_Header, ResourceType.DxTexture, new byte[16]);
                s_Ctx.AddChunk(s_ChunkId, s_Retail);
                p_Writer.WriteLine($"destream-keepmips {Name}: flags 0x{s_Flags:X}->0x{s_NewFlags:X}, mipBase {s_MipBase} KEPT, retail-ranged chunk {s_ChunkId}.");
                return true;
            }

            // carry the asset name so the manifest writes a DICE-style chunkMeta { h32: fnv(name) } —
            // an empty chunkMeta breaks the chunk<->texture association at bundle load.
            if (!s_Mounter.TryGetFullRangeCasChunkVariant(s_ChunkId, out var s_Full, Name!))
            {
                p_Writer.WriteLine($"destream_texture: {Name} chunk {s_ChunkId} has NO catalog-backed variant (DLC/noncas?) — fall back to add_dds for this one.");
                return false;
            }

            // replace whatever resolve pulled (partial ranged chunk / streaming resource)
            try { s_Ctx.RemoveResource(Name!); } catch { }
            try { s_Ctx.RemoveChunk(s_ChunkId); } catch { }

            if (s_Mode == "chunkonly")
            {
                // CAS pack delivery: full-range catalog chunk only; the resource stays REMOVED (its
                // generated header would be cas-idata = the MVDB BLACK-body gate; it ships in the
                // noncas annex via 'headeronly' and binds by name at TextureAsset realize).
                s_Ctx.AddChunk(s_ChunkId, s_Full);
                p_Writer.WriteLine($"destream-chunk {Name}: chunk {s_ChunkId} full-range catalog ref (h32 meta), resource removed.");
                return true;
            }

            s_Ctx.AddGeneratedResource(Name!, s_Header, ResourceType.DxTexture, new byte[16]);
            s_Ctx.AddChunk(s_ChunkId, s_Full);

            p_Writer.WriteLine($"destreamed {Name}: flags 0x{s_Flags:X}->0x{s_NewFlags:X}, mipBase {s_MipBase}->0, chunk {s_ChunkId} full-range catalog ref.");
            return true;
        }
    }
}
