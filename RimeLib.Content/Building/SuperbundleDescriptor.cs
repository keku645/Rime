using System;
using System.Collections.Generic;
using RimeLib.Content.Mounting;
using RimeLib.Frostbite.Core;

namespace RimeLib.Content.Building
{
    public class SuperbundleDescriptor
    {
        public string SuperbundleName { get; }

        // CAS builds only: catalog-membership probe (sha1 of a STORED payload frame -> is it
        // already in the player's cas.cat, base or patch). Lets the serializer emit embedded
        // noncas sources as pure sha1 refs instead of inline copies. Null = never assume a
        // payload is catalog-backed (everything not already a catalog ref ships as idata).
        public Func<Sha1, bool>? CatalogProbe { get; set; }

        // CAS builds only: per-item resolvers used by the serializer to de-inline INLINE-SWAPPABLE
        // items. The mounter prefers idata variants over the catalog, so an item's PICKED variant is
        // often NOT a catalog key even though ANOTHER mounted variant of the same item IS catalog-
        // backed. These return that catalog-backed variant (or null) so the item ships as a pure
        // sha1 ref instead of embedded bytes. Keyed by resource name / chunk guid / partition name.
        public Func<string, IReadableObjectWithHash?>? CatalogResourceVariant { get; set; }
        public Func<GUID, IReadableObjectWithHash?>? CatalogChunkVariant { get; set; }
        public Func<string, IReadableObjectWithHash?>? CatalogPartitionVariant { get; set; }

        public Dictionary<GUID, IChunkObject> Chunks { get; }

        // CAS toc chunks: emitted in the toc chunk list as { id, sha1 } refs (NO payload in the sb).
        // This is how vanilla CAS superbundles carry their sb-level streaming chunks (terrain etc.) —
        // needed to build a complete level-sb override (a clone missing these hangs 'Loading terrain').
        public Dictionary<GUID, Sha1> CasTocChunks { get; }

        public Dictionary<string, BundleDescriptor> Bundles { get; }

        public bool Cas { get; }

        internal SuperbundleDescriptor(string p_SbName, bool p_Cas = false)
        {
            SuperbundleName = p_SbName;

            Chunks = new Dictionary<GUID, IChunkObject>();
            CasTocChunks = new Dictionary<GUID, Sha1>();
            Bundles = new Dictionary<string, BundleDescriptor>();
            Cas = p_Cas;
        }
    }
}
