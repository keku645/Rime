using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using RimeLib.Cmd.Commands.BundleBuilding;
using RimeLib.Content.Building;
using RimeLib.Content.Frostbite;
using RimeLib.Content.Mounting;
using RimeLib.Extensions;
using RimeLib.Frostbite;
using RimeLib.Frostbite.Core;
using RimeLib.Frostbite.Db;
using RimeLib.IO;
using RimeLib.Serialization;
using RimeLib.Shader;
using RimeLib.Texture.Generation;
using RimeLib.Utils;

namespace RimeLib.Cmd.Contexts
{
    public class BundleBuildingContext : ExecutionContext
    {
        internal class ResourceFileReader : SbBuildingContext.FileReader, IResourceObject
        {
            private readonly ResourceType m_ResourceType;
            private readonly ResourceRef m_ResourceId;

            public ResourceFileReader(string p_Path, ResourceType p_ResourceType, string p_Name) : base(p_Path)
            {
                m_ResourceType = p_ResourceType;
                m_ResourceId = new ResourceRef(p_Name, this);
            }

            public ResourceType GetResourceType()
            {
                return m_ResourceType;
            }

            public bool TryGetMeta([NotNullWhen(true)] out byte[]? p_Meta)
            {
                p_Meta = null;
                return false;
            }

            public ResourceRef GetId(string? p_Name = null)
            {
                return m_ResourceId;
            }
        }

        internal class ResourceMemoryReader : SbBuildingContext.MemoryReader, IResourceObject
        {
            private readonly ResourceType m_ResourceType;
            private readonly ResourceRef m_ResourceId;
            private readonly byte[]? m_Meta;

            public ResourceMemoryReader(byte[] p_Data, ResourceType p_ResourceType, string p_Name, byte[]? p_Meta = null) : base(p_Data)
            {
                m_ResourceType = p_ResourceType;
                m_ResourceId = new ResourceRef(p_Name, this);
                m_Meta = p_Meta;
            }

            public ResourceType GetResourceType()
            {
                return m_ResourceType;
            }

            public bool TryGetMeta([NotNullWhen(true)] out byte[]? p_Meta)
            {
                p_Meta = m_Meta;
                return m_Meta != null;
            }
            
            public ResourceRef GetId(string? p_Name = null)
            {
                return m_ResourceId;
            }
        }

        internal class ChunkMemoryReader : SbBuildingContext.MemoryReader, IChunkObject
        {
            private readonly string m_AssetName;

            public ChunkMemoryReader(byte[] p_Data, string p_AssetName) : base(p_Data)
            {
                m_AssetName = p_AssetName;
            }

            public bool TryGetMeta([NotNullWhen(true)] out DbObject? p_Meta)
            {
                p_Meta = null;
                return false;
            }

            public uint GetRangeStart()
            {
                return 0;
            }

            public uint GetRangeEnd()
            {
                return (uint)GetSize();
            }

            public uint GetLogicalOffset()
            {
                return 0;
            }

            public uint GetLogicalSize()
            {
                return (uint)GetSize();
            }

            public int? GetAssetNameHash()
            {
                return (int)Frostbite.Utils.HashQuick(m_AssetName);
            }
        }

        protected readonly string m_BundleName;

        protected BundleBuilder m_Builder;

        protected fb.RegistryContainer? m_GeneratedRegistry;

        public BundleBuildingContext(SbBuildingContext p_Parent, string p_BundleName)
        {
            Parent = p_Parent;
            m_BundleName = p_BundleName;

            m_Builder = BundleBuilder.Create(m_BundleName);

            RegisterCommand<AddChunkCommand>();
            RegisterCommand<AddExistingChunkCommand>();
            RegisterCommand<Commands.BundleBuilding.AddCasChunkCommand>();
            RegisterCommand<RemoveChunkCommand>();
            RegisterCommand<ListChunksCommand>();
            RegisterCommand<AddResourceCommand>();
            RegisterCommand<AddExistingResourceCommand>();
            RegisterCommand<ReplaceResourceCommand>();
            RegisterCommand<ReplaceResourceAsCommand>();
            RegisterCommand<RemoveResourceCommand>();

            if (EngineInterfaceRegistry.IsSupported<RimeLib.Terrain.Resources.ITerrainDecalsConverter>(
                ((SbBuildingContext)p_Parent).EngineType))
            {
                RegisterCommand<ReplaceTerrainDecalsCommand>();
            }
            RegisterCommand<ListResourcesCommand>();
            RegisterCommand<AddPartitionCommand>();
            RegisterCommand<AddExistingPartitionCommand>();
            RegisterCommand<AddRawPartitionCommand>();
            RegisterCommand<RaiseWaterPhysicsCommand>();
            RegisterCommand<ClonePartitionFreshCommand>();
            RegisterCommand<MvdbAddEntryCommand>();
            RegisterCommand<MvdbKeepPrefixCommand>();
            RegisterCommand<MvdbAddAllCommand>();
            RegisterCommand<CheckTexturesCommand>();
            RegisterCommand<CheckChunksCommand>();
            RegisterCommand<CheckCasRefsCommand>();
            RegisterCommand<StripSbLevelChunksCommand>();
            RegisterCommand<StripTargetDupsCommand>();
            RegisterCommand<DestreamTextureCommand>();
            RegisterCommand<CapTextureCommand>();
            RegisterCommand<BundleStatsCommand>();

            var s_EngineType = ((SbBuildingContext)p_Parent).EngineType;

            if (EngineInterfaceRegistry.IsSupported<IPartitionConverter>(s_EngineType) && 
                EngineInterfaceRegistry.IsSupported<IPartitionGenerator>(s_EngineType))
            {
                RegisterCommand<AddJsonPartitionCommand>();
            }

            RegisterCommand<RemovePartitionCommand>();
            RegisterCommand<ListPartitionsCommand>();

            if (EngineInterfaceRegistry.IsSupported<ITextureGenerator>(s_EngineType))
            {
                RegisterCommand<AddDdsTextureCommand>();
            }

            RegisterCommand<BuildCommand>();
            RegisterCommand<CloneBundleCommand>();
            RegisterCommand<AddDependencyBundleCommand>();
            RegisterCommand<AddDependencySuperbundleCommand>();

            if (EngineInterfaceRegistry.IsSupported<IPartitionConverter>(s_EngineType))
            {
                RegisterCommand<ResolvePartitionDependenciesCommand>();
                RegisterCommand<ResolveEbxChunksCommand>();
            }

            RegisterCommand<ResolveResourceDependenciesCommand>();

            if (EngineInterfaceRegistry.IsSupported<IShaderResolver>(s_EngineType))
            {
                RegisterCommand<ResolveShaderTexturesCommand>();
            }

            RegisterCommand<RemoveDuplicateBundleItemsCommand>();
            RegisterCommand<StripPartitionsByPrefixCommand>();
            RegisterCommand<StripResourcesByPrefixCommand>();
            RegisterCommand<RenamePartitionsByPrefixCommand>();
            RegisterCommand<ResolveMissingChunksCommand>();
            RegisterCommand<ExportBundleContentsCommand>();
            RegisterCommand<GenerateRegistryContainerCommand>();
            RegisterCommand<CompareRegistryContainersCommand>();
            RegisterCommand<EmitSubworldRegistryCommand>();
            RegisterCommand<KeepOnlyPartitionCommand>();
        }

        public bool Cas()
        {
            var s_SbBuildingContext = Parent as SbBuildingContext;
            if (s_SbBuildingContext == null)
                throw new System.Exception();

            return s_SbBuildingContext.Cas();
        }

        public override string GetShortDescription()
        {
            var s_Chunks = m_Builder.GetChunks().Count;
            var s_Resources = m_Builder.GetResources().Count;
            var s_Partitions = m_Builder.GetPartitions().Count;

            return $"building bundle - {m_BundleName} - {s_Chunks} chunk{(s_Chunks == 1 ? "" : "s")} - {s_Resources} resource{(s_Resources == 1 ? "" : "s")} - {s_Partitions} partition{(s_Partitions == 1 ? "" : "s")}";
        }

        public override string GetLongDescription()
        {
            var s_Chunks = m_Builder.GetChunks().Count;
            var s_Resources = m_Builder.GetResources().Count;
            var s_Partitions = m_Builder.GetPartitions().Count;

            var s_Text = "Bundle Builder\n\n";
            s_Text += $"Bundle name: {m_BundleName}\n";
            s_Text += $"Chunks added: {s_Chunks}\n";
            s_Text += $"Resources added: {s_Resources}\n";
            s_Text += $"Partitions added: {s_Partitions}\n";

            return s_Text;
        }

        internal void AddChunk(GUID p_Guid, FileInfo p_File, string p_AssetName)
        {
            m_Builder.WithChunk(p_Guid, new SbBuildingContext.ChunkFileReader(p_File.FullName, p_AssetName));
        }

        internal void AddChunk(GUID p_Guid, FileInfo p_File, int p_AssetHash)
        {
            m_Builder.WithChunk(p_Guid, new SbBuildingContext.ChunkFileReader(p_File.FullName, p_AssetHash));
        }

        internal void AddChunk(GUID p_Guid, IChunkObject p_Object)
        {
            m_Builder.WithChunk(p_Guid, p_Object);
        }

        internal void RemoveChunk(GUID p_Guid)
        {
            m_Builder.RemoveChunk(p_Guid);
        }

        internal IReadOnlyDictionary<GUID, IChunkObject> GetChunks()
        {
            return m_Builder.GetChunks();
        }

        internal void AddResource(string p_Name, ResourceType p_Type, FileInfo p_File)
        {
            m_Builder.WithResource(p_Name, new ResourceFileReader(p_File.FullName, p_Type, p_Name));
        }

        internal void AddResource(string p_Name, IResourceObject p_Object)
        {
            m_Builder.WithResource(p_Name, p_Object);
        }

        internal void RemoveResource(string p_Name)
        {
            m_Builder.RemoveResource(p_Name);
        }

        internal IReadOnlyDictionary<string, IResourceObject> GetResources()
        {
            return m_Builder.GetResources();
        }

        internal void AddPartition(string p_Name, FileInfo p_File)
        {
            m_Builder.WithPartition(p_Name, new SbBuildingContext.FileReader(p_File.FullName));
        }

        internal void AddPartition(string p_Name, IObjectVariant p_Partition)
        {
            m_Builder.WithPartition(p_Name, p_Partition);
        }

        internal void AddJsonPartition(string p_Name, FileInfo p_File)
        {
            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(((SbBuildingContext)Parent!).EngineType);
            var s_Generator = EngineInterfaceRegistry.Create<IPartitionGenerator>(((SbBuildingContext)Parent!).EngineType);

            using var s_JsonReader = p_File.OpenText();
            var s_Partition = s_Converter.FromJsonStream(s_JsonReader);

            var s_Stream = new MemoryStream();
            using var s_Writer = new RimeWriter(s_Stream);
            s_Generator.Generate(s_Partition, s_Writer);

            m_Builder.WithPartition(p_Name, new SbBuildingContext.MemoryReader(s_Stream.ToArray()));
        }

        internal void AddRawPartition(string p_Name, FileInfo p_File)
        {
            // Inject a raw EBX partition binary (from dump_partition) byte-for-byte, bypassing
            // the JSON converter — preserves the exact game-built structure (so it realizes).
            m_Builder.WithPartition(p_Name, new SbBuildingContext.MemoryReader(File.ReadAllBytes(p_File.FullName)));
        }

        internal void AddRawPartitionBytes(string p_Name, byte[] p_Data)
        {
            m_Builder.WithPartition(p_Name, new SbBuildingContext.MemoryReader(p_Data));
        }

        internal void RemovePartition(string p_Name)
        {
            m_Builder.RemovePartition(p_Name);
        }

        // Re-keys every partition whose (lowercased) name starts with p_OldPrefix so it starts with
        // p_NewPrefix instead, keeping the same reader/variant. `clone_bundle` copies partitions under
        // their OriginalName (e.g. levels/mp_subway/teamdeathmatch), but the engine looks up a sublevel's
        // root DataContainer BY NAME (fb::ResourceManager::lookupDataContainer(compartment, name) @ retail
        // 0x11A1342, reached from the level-load sublevel recursion sub_11A1170); a mismatch between the
        // SubWorldReference's BundleName (levels/<newlevel>/...) and the cloned partition name returns NULL
        // and crashes (sub_11A1170, mov esi,[NULL+0x20]). Inter-partition refs are by GUID (imports), so
        // re-keying names does NOT break them — only the path lookup is affected. Returns the count renamed.
        internal int RenamePartitionsByPrefix(string p_OldPrefix, string p_NewPrefix)
        {
            var s_Partitions = m_Builder.GetPartitions();
            var s_Old = p_OldPrefix.ToLowerInvariant();
            var s_New = p_NewPrefix.ToLowerInvariant();

            var s_Matches = s_Partitions.Keys.Where(k => k.StartsWith(s_Old)).ToList();
            foreach (var s_Key in s_Matches)
            {
                var s_Value = s_Partitions[s_Key];
                s_Partitions.Remove(s_Key);
                s_Partitions[s_New + s_Key.Substring(s_Old.Length)] = s_Value;
            }

            return s_Matches.Count;
        }

        internal void SetGeneratedRegistry(fb.RegistryContainer p_Registry)
        {
            m_GeneratedRegistry = p_Registry;
        }

        internal fb.RegistryContainer? GetGeneratedRegistry()
        {
            return m_GeneratedRegistry;
        }

        // MVDB registry refs (2026-07-05): a mini-MVDB is injected via AddRawPartitionBytes (raw bytes),
        // so generate_registry_container can't parse it (MemoryReader is not an IObjectVariant) and never
        // collects it. mvdb_add_all stashes the sliced MVDB's {partitionGuid, primaryInstanceGuid} here so
        // emit_subworld_registry can add it to the SubWorld's AssetRegistry (= the load-time mesh-variation
        // index feed / camo binding).
        private readonly System.Collections.Generic.List<(RimeLib.Frostbite.Core.GUID Part, RimeLib.Frostbite.Core.GUID Inst)> m_MvdbRegistryRefs = new();

        internal void AddMvdbRegistryRef(RimeLib.Frostbite.Core.GUID p_Part, RimeLib.Frostbite.Core.GUID p_Inst)
        {
            m_MvdbRegistryRefs.Add((p_Part, p_Inst));
        }

        internal System.Collections.Generic.IReadOnlyList<(RimeLib.Frostbite.Core.GUID Part, RimeLib.Frostbite.Core.GUID Inst)> GetMvdbRegistryRefs()
        {
            return m_MvdbRegistryRefs;
        }

        internal IReadOnlyDictionary<string, IReadableObject> GetPartitions()
        {
            return m_Builder.GetPartitions();
        }

        internal void AddDependencyBundle(string p_BundleName)
        {
            m_Builder.WithDependencyBundle(p_BundleName);
        }

        internal IEnumerable<string> GetDependencyBundles()
        {
            return m_Builder.Build().DependencyBundles;
        }

        internal void AddDependencySuperbundle(string p_SuperbundleName)
        {
            m_Builder.WithDependencySuperbundle(p_SuperbundleName);
        }

        internal IEnumerable<string> GetDependencySuperbundles()
        {
            return m_Builder.Build().DependencySuperbundles;
        }

        // TODO: This should probably be moved somewhere else
        internal void AddDDSTexture(FileInfo p_File, TextureAttributes p_Attributes, string? p_GroupDonor = null)
        {
            var s_TextureGenerator = EngineInterfaceRegistry.Create<ITextureGenerator>(((SbBuildingContext)Parent!).EngineType);

            // THE BLACK-VEHICLE FIX: a regenerated texture must keep the ORIGINAL's TextureGroup
            // ("Vehicle", "World_SkipNo_St", ...). The old hardcoded "Default" is not a valid BF3
            // group -> the mesh/MVDB texture-bind path never pools/uploads it -> vehicles sample
            // BLACK (the by-name shaderdb path tolerated it, which is why the water palette worked).
            // Read the group (char[16] at offset 112) from the mounted original's 128-byte header;
            // a BRAND-NEW name has no original, so a DONOR (a sibling texture of the same material)
            // lends its group instead.
            try
            {
                var s_BaseCtx = (BaseContext)((SbBuildingContext)Parent!).Parent!;
                var s_Mounter = s_BaseCtx.GetMounters().Values.FirstOrDefault();

                string? GroupOf(string p_Name)
                {
                    if (s_Mounter == null || !s_Mounter.TryGetResource(p_Name, out var s_Orig))
                        return null;

                    using var s_OrigReader = s_Orig.FirstVariant.GetReader();
                    if (s_OrigReader.Length < 128)
                        return null;

                    var s_Header = s_OrigReader.ReadBytes(128);
                    var s_GroupEnd = System.Array.IndexOf(s_Header, (byte)0, 112, 16);
                    if (s_GroupEnd < 0) s_GroupEnd = 128;
                    var s_Group = System.Text.Encoding.ASCII.GetString(s_Header, 112, s_GroupEnd - 112);
                    return string.IsNullOrWhiteSpace(s_Group) ? null : s_Group;
                }

                var s_Resolved = GroupOf(p_Attributes.Name) ??
                                 (p_GroupDonor != null ? GroupOf(p_GroupDonor) : null);
                if (s_Resolved != null)
                    p_Attributes.TextureGroup = s_Resolved;
            }
            catch { /* no original -> keep the attribute's group */ }

            var s_ResourceMemoryStream = new MemoryStream();
            using var s_ResourceWriter = new RimeWriter(s_ResourceMemoryStream);

            using var s_DDSReader = new RimeReader(File.OpenRead(p_File.FullName));
            s_TextureGenerator.GenerateFromDDS(
                s_DDSReader,
                p_Attributes,
                s_ResourceWriter,
                out var s_Chunks
            );

            m_Builder.WithResource(
                p_Attributes.Name,
                // meta = 16 zero bytes, matching every original DxTexture res entry (empty meta is
                // another generated-vs-original delta on the bind path).
                new ResourceMemoryReader(s_ResourceMemoryStream.ToArray(), s_TextureGenerator.GetTargetResourceType(), p_Attributes.Name, new byte[16])
            );

            foreach (var (s_Id, s_ChunkStream) in s_Chunks)
            {
                m_Builder.WithChunk(s_Id, new ChunkMemoryReader(s_ChunkStream.ToArray(), p_Attributes.Name));
                s_ChunkStream.Dispose();
            }
        }

        internal void AddGeneratedResource(string p_Name, byte[] p_Data, ResourceType p_Type, byte[]? p_Meta)
        {
            m_Builder.WithResource(p_Name, new ResourceMemoryReader(p_Data, p_Type, p_Name, p_Meta));
        }

        internal BundleDescriptor Build()
        {
            return m_Builder.Build();
        }
    }
}
