using System;
﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RimeLib.Cmd.Commands.Game;
using RimeLib.Cmd.Scaleform;
using RimeLib.Terrain.Resources;
using RimeLib.Content.Frostbite;
using RimeLib.Content.Mounting;
using RimeLib.Frostbite;
using RimeLib.Frostbite.Core;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Mesh;
using RimeLib.Mesh.Frostbite;
using RimeLib.Serialization;
using RimeLib.Texture;
using RimeLib.Toolkit;
using SharpGLTF.Schema2;

namespace RimeLib.Cmd.Contexts
{
    /// <summary>
    /// 
    /// </summary>
    public class GameContext : ExecutionContext
    {
        // Game context id, if there are multiple game context this determines which one is assigned to which game
        protected int m_Id;

        // Frostbite engine mounter interface (this can be different implementations based on Frostbite revision)
        protected IEngineMounter m_Mounter;

        internal IEngineMounter GetMounter() => m_Mounter;

        /// <summary>
        /// GameContext constructor
        /// </summary>
        /// <param name="p_Parent">Parent execution context</param>
        /// <param name="p_Id">Id of this mounted game context</param>
        /// <param name="p_Mounter">Frostbite engine mounter interface for this version of fb</param>
        public GameContext(BaseContext p_Parent, int p_Id, IEngineMounter p_Mounter)
        {
            Parent = p_Parent;
            m_Id = p_Id;
            m_Mounter = p_Mounter;

            RegisterCommand<ListSbCommand>();
            RegisterCommand<MountSbCommand>();
            RegisterCommand<MountStandaloneSbCommand>();
            RegisterCommand<ListMountedSbCommand>();
            RegisterCommand<ListBundlesCommand>();
            RegisterCommand<MountBundleCommand>();
            RegisterCommand<MountAllBundlesCommand>();
            RegisterCommand<ListMountedBundlesCommand>();
            RegisterCommand<ListChunksCommand>();
            RegisterCommand<ListDuplicateChunksCommand>();
            RegisterCommand<ListResourcesCommand>();
            RegisterCommand<ListResourcesOfTypeCommand>();
            RegisterCommand<DumpShaderDbCommand>();
            RegisterCommand<ShaderDbRoundtripCommand>();
            RegisterCommand<ShaderDbFunctionsCommand>();
            RegisterCommand<DumpShaderBindingsCommand>();
            RegisterCommand<ShaderDbAddTextureCommand>();
            RegisterCommand<ShaderDbAddExternalTextureCommand>();
            RegisterCommand<ShaderDbSetExternalValueCommand>();
            RegisterCommand<ShaderDbCopySamplersCommand>();
            RegisterCommand<ShaderDbCloneEntryCommand>();
            RegisterCommand<ShaderDbSetFlagsCommand>();
            RegisterCommand<ShaderDbVertexMeshPosCommand>();
            RegisterCommand<ShaderDbMergeCommand>();
            RegisterCommand<ShaderDbSliceCommand>();
            RegisterCommand<DumpHeightfieldCommand>();
            RegisterCommand<ReplaceShaderBytecodeCommand>();
            RegisterCommand<ReplaceVertexShaderBytecodeCommand>();
            RegisterCommand<ExtractShaderDxbcCommand>();
            RegisterCommand<DumpShaderSolutionsCommand>();
            RegisterCommand<DumpShaderTexturesCommand>();
            RegisterCommand<ShaderDbCensusCommand>();
            RegisterCommand<DumpShaderProgramDbCommand>();
            RegisterCommand<DumpMvdbVariationsCommand>();
            RegisterCommand<DumpShaderMaterialTexturesCommand>();
            RegisterCommand<ListPartitionsCommand>();
            RegisterCommand<ListSbChunksCommand>();
            RegisterCommand<ListBundleChunksCommand>();
            RegisterCommand<ListBundleResourcesCommand>();
            RegisterCommand<ListBundlePartitionsCommand>();
            RegisterCommand<DumpChunkCommand>();
            RegisterCommand<DumpResourceCommand>();
            RegisterCommand<DumpResourceMetaCommand>();
            RegisterCommand<ResourceIdataCensusCommand>();
            RegisterCommand<DumpResEntryCommand>();
            RegisterCommand<DumpBundleChunkMetaCommand>();
            RegisterCommand<ListSbBundlesCommand>();
            RegisterCommand<DumpResourceWithChunksCommand>();
            RegisterCommand<DumpSwfJsonCommand>();
            RegisterCommand<RimeLib.Cmd.Commands.Common.GfxStageListCommand>();
            RegisterCommand<RimeLib.Cmd.Commands.Common.GfxStageEditCommand>();
            RegisterCommand<UiBuildModCommand>();
            RegisterCommand<DumpPartitionCommand>();
            RegisterCommand<DumpPartitionByGuidCommand>();
            RegisterCommand<HashMountedPayloadsCommand>();
            RegisterCommand<VerifyCatalogHashesCommand>();
            RegisterCommand<ClassifyTexturesCommand>();
            RegisterCommand<BuildCasCatalogCommand>();
            RegisterCommand<BuildNoncasIndexCommand>();
            RegisterCommand<ProbeCatalogCommand>();
            RegisterCommand<MountExternalCatCommand>();

            var s_EngineType = m_Mounter.GetEngineType();

            if (EngineInterfaceRegistry.IsSupported<IPartitionConverter>(s_EngineType))
            {
                RegisterCommand<DumpPartitionJsonCommand>();
                RegisterCommand<DumpPartitionJsonByGuidCommand>();
                RegisterCommand<DumpMountedPartitionsJsonCommand>();
            }

            if (EngineInterfaceRegistry.IsSupported<ITextureConverter>(s_EngineType))
            {
                RegisterCommand<DumpTextureCommand>();
                RegisterCommand<DumpChunkVariantsCommand>();
                RegisterCommand<DumpPartitionVariantsCommand>();
            }

            if (EngineInterfaceRegistry.IsSupported<ITerrainDecalsConverter>(s_EngineType))
            {
                RegisterCommand<DumpTerrainDecalsJsonCommand>();
            }

            if (EngineInterfaceRegistry.IsSupported<IMeshConverter>(s_EngineType))
            {
                RegisterCommand<DumpMeshCommand>();
                RegisterCommand<DumpMeshSectionsCommand>();
                RegisterCommand<VehiclePartCensusCommand>();
                RegisterCommand<DumpMeshLodChunksCommand>();
                RegisterCommand<DumpWeaponPoseCommand>();
                RegisterCommand<DumpWeaponCameraCommand>();
                RegisterCommand<DumpArmsPoseCommand>();
                RegisterCommand<ListAnimBankCommand>();
                RegisterCommand<DumpAnimFrameCommand>();
                RegisterCommand<DumpWeaponPartPosesCommand>();
                RegisterCommand<MeshRoundtripCommand>();
                RegisterCommand<MeshRenameCommand>();

                if (EngineInterfaceRegistry.IsSupported<IToolKit>(s_EngineType) && EngineInterfaceRegistry.IsSupported<IPartitionConverter>(s_EngineType))
                {
                    RegisterCommand<DumpLevelMeshesCommand>();
                }
            }
        }

        public override string GetShortDescription()
        {
            return $"{m_Id} - {m_Mounter.GetGamePath()} - {m_Mounter.GetEngineType()}";
        }

        public override string GetLongDescription()
        {
            var s_Text = "Mounted Game Context\n\n";
            s_Text += $"Game ID: {m_Id}\n";
            s_Text += $"Game Path: {m_Mounter.GetGamePath()}\n";
            s_Text += $"Game Engine: {m_Mounter.GetEngineType()}";
            return s_Text;
        }

        /// <summary>
        /// Mount a superbundle by name
        /// </summary>
        /// <param name="p_Name">Name of superbundle to mount</param>
        /// <param name="p_AutoMount">Automatically mount contained bundles</param>
        internal void MountSuperbundle(string p_Name, bool p_AutoMount)
        {
            m_Mounter.MountSuperbundle(p_Name, p_AutoMount).Wait();
        }

        /// <summary>
        /// Mount a superbundle by path
        /// </summary>
        /// <param name="p_Name">Name of superbundle to mount</param>
        /// <param name="p_Path">Path to superbundle</param>
        /// <param name="p_AutoMount">Automatically mount contained bundles</param>
        internal void MountStandaloneSuperbundle(string p_Name, string p_Path, bool p_AutoMount)
        {
            m_Mounter.MountStandaloneSuperbundle(p_Name, p_Path, p_AutoMount).Wait();
        }

        /// <summary>
        /// Mount a bundle by name
        /// </summary>
        /// <param name="p_Name">Name of bundle to mount</param>
        internal void MountBundle(string p_Name)
        {
            m_Mounter.MountBundle(p_Name).Wait();
        }

        /// <summary>
        /// Gets an enumerable of all available superbundles
        /// </summary>
        /// <returns>Enumerable of superbundle names</returns>
        internal IEnumerable<string> GetAvailableSuperbundles()
        {
            return m_Mounter.GetAvailableSuperbundles();
        }

        /// <summary>
        /// Gets an enumerable of all available bundles
        /// </summary>
        /// <returns>Enumerable of bundle names</returns>
        internal IEnumerable<string> GetAvailableBundles()
        {
            return m_Mounter.GetAvailableBundles();
        }

        /// <summary>
        /// Gets an enumerable of all MOUNTED superbundles
        /// </summary>
        /// <returns>Enumerable of mounted superbundles</returns>
        internal IEnumerable<string> GetMountedSuperbundles()
        {
            return m_Mounter.GetMountedSuperbundles();
        }

        /// <summary>
        /// Gets an enumerable of all MOUNTED bundles
        /// </summary>
        /// <returns>Enumerable of mounted bundles</returns>
        internal IEnumerable<string> GetMountedBundles()
        {
            return m_Mounter.GetMountedBundles();
        }

        /// <summary>
        /// Dumps a specific chunk based on GUID
        /// </summary>
        /// <param name="p_Guid">Input GUID of the chunk to dump</param>
        /// <param name="p_Destination">Destination file to write</param>
        /// <exception cref="Exception">If the chunk is not found, exception will be thrown</exception>
        internal void DumpChunk(GUID p_Guid, FileInfo p_Destination)
        {
            if (!m_Mounter.TryGetChunk(p_Guid, out var s_Chunk))
                throw new Exception($"Could not find chunk with id '{p_Guid.ToString("D")}'.");

            // Ensure that the directory of the destination file exists
            if (p_Destination.Directory != null && !Directory.Exists(p_Destination.Directory.FullName))
                Directory.CreateDirectory(p_Destination.Directory.FullName);

            // TODO: this is causing issues when cloning (noncas) bundles.
            // Here we look for the first chunk variant with logical offset 0.
            // That's because variants with non-0 offsets can be partial mips, etc.
            using var s_Reader = s_Chunk.Variants.First(p_Variant => true).GetReader();
            using var s_FileStream = File.Create(p_Destination.FullName);

            s_Reader.CopyTo(s_FileStream);
        }

        /// <summary>
        /// Dumps a specific resource tag
        /// </summary>
        /// <param name="p_Name">Name of the resource</param>
        /// <param name="p_Destination">Destination file to write</param>
        /// <exception cref="Exception">If the resource is not found, exception will be thrown</exception>
        internal void DumpResource(string p_Name, FileInfo p_Destination, bool p_LastVariant = false)
        {
            if (!m_Mounter.TryGetResource(p_Name, out var s_Resource))
                throw new Exception($"Could not find resource with name '{p_Name}'.");

            // Ensure that the directory of the destination file exists
            if (p_Destination.Directory != null && !Directory.Exists(p_Destination.Directory.FullName))
                Directory.CreateDirectory(p_Destination.Directory.FullName);

            // A name can be mounted more than once (the game's own copy plus a standalone superbundle's).
            // The first variant is the game's; the LAST is the most recently mounted — asking for it is how
            // a mod's copy of a colliding name (a level's shader database) is read instead of the game's.
            var s_Variant = p_LastVariant ? System.Linq.Enumerable.Last(s_Resource.Variants) : s_Resource.FirstVariant;

            using var s_Reader = s_Variant.GetReader();
            using var s_FileStream = File.Create(p_Destination.FullName);

            s_Reader.CopyTo(s_FileStream);
        }

        /// <summary>
        /// Round-trip harness for a MeshSet resource: read it, parse the MeshSetLayout header,
        /// re-serialize that header (LittleEndian, same as the reader) and report whether the
        /// re-serialized bytes are byte-identical to the original header. Validates the mesh
        /// WRITER (Serialize) before it is used to author a new mesh from an imported FBX.
        /// </summary>
        internal void MeshRoundtrip(string p_Name, TextWriter p_Writer)
        {
            if (!m_Mounter.TryGetResource(p_Name, out var s_Resource))
                throw new Exception($"Could not find resource with name '{p_Name}'.");

            if (s_Resource.FirstVariant.GetResourceType() != ResourceType.MeshSet)
                throw new Exception($"Resource '{p_Name}' is not a MeshSet (it is {s_Resource.FirstVariant.GetResourceType()}).");

            // Original resource bytes.
            byte[] s_Original;
            using (var s_Reader0 = s_Resource.FirstVariant.GetReader())
                s_Original = s_Reader0.ReadBytes((int)s_Reader0.Length);

            // Parse the header (LittleEndian — same as MeshConverter).
            var s_Layout = new MeshSetLayout(new RimeReader(new MemoryStream(s_Original)));
            p_Writer.WriteLine($"[mesh_roundtrip] {p_Name}: {s_Original.Length} bytes | Type={s_Layout.MeshType} Flags={s_Layout.Flags} LODs={s_Layout.LodCount} subsets={s_Layout.TotalSubsetCount}");
            p_Writer.WriteLine($"  Name='{s_Layout.Name.Object}' ShortName='{s_Layout.ShortName.Object}' NameHash=0x{s_Layout.NameHash:X8} Padding=0x{s_Layout.Padding:X8}");
            for (var i = 0; i < 5; ++i)
            {
                p_Writer.WriteLine($"  LOD[{i}] BaseAddress=0x{s_Layout.Lods[i].BaseAddress:X} present={s_Layout.Lods[i].Object != null}" +
                                   (s_Layout.Lods[i].Object is { } s_LodInfo
                                       ? $" flags={s_LodInfo.Flags} chunk={s_LodInfo.DataChunkId} (resolve_missing_chunks adds a LOD's chunk only with IsBaseLod set)"
                                       : ""));

                // Which MATERIAL each subset of this LOD draws with, and its vertex declaration: a variation that only
                // repoints the material the base LOD uses leaves the lower LODs (often another preset) as shipped.
                if (s_Layout.Lods[i].Object is { } s_LodSubsets)
                {
                    var s_Lines = new List<string>();
                    for (var s_S = 0; s_S < s_LodSubsets.Subsets.Get.Length; ++s_S)
                    {
                        var s_Subset = s_LodSubsets.Subsets.Get[s_S];
                        s_Lines.Add($"subset {s_S}: material {s_Subset.MaterialIndex} decl=0x{s_Subset.GeometryDeclarationDesc.Hash:X8} verts={s_Subset.VertexCount}");
                    }

                    p_Writer.WriteLine($"  LOD[{i}] SUBSETS: {string.Join(" | ", s_Lines)}");
                }
            }

            // Re-serialize just the header and compare to original[0..headerLen].
            byte[] s_Rewritten;
            using (var s_Ms = new MemoryStream())
            {
                using (var s_HeaderWriter = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
                    s_Layout.Serialize(s_HeaderWriter);
                s_Rewritten = s_Ms.ToArray();
            }

            var s_HeaderLen = s_Rewritten.Length;
            var s_FirstDiff = -1;
            for (var i = 0; i < s_HeaderLen; ++i)
            {
                if (i >= s_Original.Length || s_Original[i] != s_Rewritten[i]) { s_FirstDiff = i; break; }
            }

            if (s_FirstDiff < 0)
                p_Writer.WriteLine($"  HEADER round-trip: BYTE-IDENTICAL over {s_HeaderLen} bytes [OK]");
            else
                p_Writer.WriteLine($"  HEADER round-trip: DIFF at byte {s_FirstDiff} (orig=0x{(s_FirstDiff < s_Original.Length ? s_Original[s_FirstDiff] : 0):X2} new=0x{s_Rewritten[s_FirstDiff]:X2}) of {s_HeaderLen}");

            // Validate each LOD struct: re-serialize the MeshLayout and compare at its BaseAddress.
            for (var s_I = 0; s_I < 5; ++s_I)
            {
                var s_Lod = s_Layout.Lods[s_I].Object;
                if (s_Lod == null)
                    continue;

                var s_Off = (int)s_Layout.Lods[s_I].BaseAddress;
                byte[] s_LodBytes;
                using (var s_LodMs = new MemoryStream())
                {
                    using (var s_LodW = new RimeWriter(s_LodMs, Endianness.LittleEndian, false))
                        s_Lod.Serialize(s_LodW);
                    s_LodBytes = s_LodMs.ToArray();
                }

                var s_LodDiff = -1;
                for (var s_J = 0; s_J < s_LodBytes.Length; ++s_J)
                {
                    if (s_Off + s_J >= s_Original.Length || s_Original[s_Off + s_J] != s_LodBytes[s_J]) { s_LodDiff = s_J; break; }
                }

                // A skinned mesh's bone tables, read raw from the resource header (no data chunk needed): the skeleton bone index each
                // mesh bone slot maps to, and the bone short-name hashes. A StaticModelEntity's basePoseTransforms run in THIS order
                // (one per mesh bone), not the skeleton's.
                if (s_Layout.MeshType == fb.MeshType.MeshType_Skinned && s_Lod.PartCount > 0)
                {
                    string PeekWords(ulong p_Address, int p_Words)
                    {
                        if (p_Address == 0 || (long) p_Address + p_Words * 4L > s_Original.Length) return "(none)";
                        var s_Words = new List<string>();
                        for (var k = 0; k < p_Words; k++) s_Words.Add(BitConverter.ToUInt32(s_Original, (int) p_Address + k * 4).ToString());
                        return string.Join(",", s_Words);
                    }
                    string PeekHex(ulong p_Address, int p_Words)
                    {
                        if (p_Address == 0 || (long) p_Address + p_Words * 4L > s_Original.Length) return "(none)";
                        var s_Words = new List<string>();
                        for (var k = 0; k < p_Words; k++) s_Words.Add($"0x{BitConverter.ToUInt32(s_Original, (int) p_Address + k * 4):X8}");
                        return string.Join(",", s_Words);
                    }
                    var s_Bones = (int) System.Math.Min(64, s_Lod.PartCount);
                    p_Writer.WriteLine($"  LOD[{s_I}] BONES: partCount={s_Lod.PartCount} boneIndexArray=[{PeekWords(s_Lod.BoneIndexArrayPartBoundingBoxes.BaseAddress, s_Bones)}] boneShortNameHashes=[{PeekHex(s_Lod.BoneShortNameArrayPartTransforms.BaseAddress, s_Bones)}]");
                }

                if (s_LodDiff < 0)
                    p_Writer.WriteLine($"  LOD[{s_I}] struct ({s_LodBytes.Length}B @0x{s_Off:X}): BYTE-IDENTICAL [OK]");
                else
                    p_Writer.WriteLine($"  LOD[{s_I}] struct ({s_LodBytes.Length}B @0x{s_Off:X}): DIFF at +{s_LodDiff} (orig=0x{(s_Off + s_LodDiff < s_Original.Length ? s_Original[s_Off + s_LodDiff] : 0):X2} new=0x{s_LodBytes[s_LodDiff]:X2})");
            }

            // Validate the subsets of LOD[0] (MeshSubset contains the GeometryDeclarationDesc = vertex declaration).
            var s_FirstLod = s_Layout.Lods[0].Object;
            if (s_FirstLod != null && s_FirstLod.Subsets.BaseAddress != 0)
            {
                var s_SubBase = (int)s_FirstLod.Subsets.BaseAddress;
                var s_Subs = s_FirstLod.Subsets.Get;
                var s_Cursor = 0;
                for (var s_K = 0; s_K < s_Subs.Length; ++s_K)
                {
                    byte[] s_SubBytes;
                    using (var s_SubMs = new MemoryStream())
                    {
                        using (var s_SubW = new RimeWriter(s_SubMs, Endianness.LittleEndian, false))
                            s_Subs[s_K].Serialize(s_SubW);
                        s_SubBytes = s_SubMs.ToArray();
                    }

                    var s_SubOff = s_SubBase + s_Cursor;
                    var s_SubDiff = -1;
                    for (var s_J = 0; s_J < s_SubBytes.Length; ++s_J)
                    {
                        if (s_SubOff + s_J >= s_Original.Length || s_Original[s_SubOff + s_J] != s_SubBytes[s_J]) { s_SubDiff = s_J; break; }
                    }

                    if (s_SubDiff < 0)
                        p_Writer.WriteLine($"  LOD0 subset[{s_K}] ({s_SubBytes.Length}B @0x{s_SubOff:X}): BYTE-IDENTICAL [OK]");
                    else
                        p_Writer.WriteLine($"  LOD0 subset[{s_K}] ({s_SubBytes.Length}B @0x{s_SubOff:X}): DIFF at +{s_SubDiff} (orig=0x{(s_SubOff + s_SubDiff < s_Original.Length ? s_Original[s_SubOff + s_SubDiff] : 0):X2} new=0x{s_SubBytes[s_SubDiff]:X2})");

                    s_Cursor += s_SubBytes.Length;
                }
            }

            // --- FULL RECONSTRUCTION with gap tracking ---
            // Write every parsed piece into a fresh buffer at its original offset, mark written bytes,
            // then report: (a) written bytes that MISMATCH the original, (b) GAPS (bytes never written =
            // sub-blobs we don't parse yet + the reloc table). When gaps -> 0 and 0 mismatches, the
            // MeshSet resource writer is complete.
            var s_Buf = new byte[s_Original.Length];
            var s_Mask = new bool[s_Original.Length];

            void WriteAt(long p_Off, byte[] p_Bytes)
            {
                for (var s_I = 0; s_I < p_Bytes.Length; ++s_I)
                {
                    var s_A = p_Off + s_I;
                    if (s_A >= 0 && s_A < s_Buf.Length) { s_Buf[s_A] = p_Bytes[s_I]; s_Mask[s_A] = true; }
                }
            }

            byte[] Ser(IFbSerializable p_Obj)
            {
                using var s_M = new MemoryStream();
                using (var s_W = new RimeWriter(s_M, Endianness.LittleEndian, false))
                    p_Obj.Serialize(s_W);
                return s_M.ToArray();
            }

            void WriteStr(RelocPtr<string> p_Ptr)
            {
                if (p_Ptr.BaseAddress == 0 || p_Ptr.Object == null) return;
                var s_Str = System.Text.Encoding.UTF8.GetBytes(p_Ptr.Object);
                var s_Full = new byte[s_Str.Length + 1];
                Array.Copy(s_Str, s_Full, s_Str.Length);
                WriteAt((long)p_Ptr.BaseAddress, s_Full);
            }

            WriteAt(0, s_Rewritten);                    // header
            WriteStr(s_Layout.Name);
            WriteStr(s_Layout.ShortName);
            for (var s_I = 0; s_I < 5; ++s_I)
            {
                var s_Lod = s_Layout.Lods[s_I].Object;
                if (s_Lod == null) continue;
                WriteAt((long)s_Layout.Lods[s_I].BaseAddress, Ser(s_Lod));
                if (s_Lod.Subsets.BaseAddress != 0)
                {
                    var s_C = (long)s_Lod.Subsets.BaseAddress;
                    foreach (var s_Sub in s_Lod.Subsets.Get) { var s_B = Ser(s_Sub); WriteAt(s_C, s_B); s_C += s_B.Length; }
                }
                foreach (var s_Cat in s_Lod.CategorySubsetIndices)
                    if (s_Cat.BaseAddress != 0) WriteAt((long)s_Cat.BaseAddress, s_Cat.Get);
                WriteStr(s_Lod.Name);
                WriteStr(s_Lod.ShortName);
                WriteStr(s_Lod.ShaderDebugName);
            }

            // Report: mismatches among written bytes + gap ranges (unwritten).
            var s_Mismatches = 0;
            var s_Written = 0;
            for (var s_I = 0; s_I < s_Original.Length; ++s_I)
                if (s_Mask[s_I]) { s_Written++; if (s_Buf[s_I] != s_Original[s_I]) s_Mismatches++; }

            p_Writer.WriteLine($"  RECONSTRUCT: wrote {s_Written}/{s_Original.Length} bytes, mismatches={s_Mismatches}");
            var s_GapStart = -1;
            var s_GapCount = 0;
            for (var s_I = 0; s_I <= s_Original.Length; ++s_I)
            {
                var s_IsGap = s_I < s_Original.Length && !s_Mask[s_I];
                if (s_IsGap && s_GapStart < 0) s_GapStart = s_I;
                else if (!s_IsGap && s_GapStart >= 0)
                {
                    if (s_GapCount < 12) p_Writer.WriteLine($"    GAP 0x{s_GapStart:X}..0x{s_I:X} ({s_I - s_GapStart}B)");
                    s_GapCount++;
                    s_GapStart = -1;
                }
            }
            p_Writer.WriteLine($"  RECONSTRUCT: {s_GapCount} gap range(s) (unparsed sub-blobs + reloc table)");
        }

        /// <summary>
        /// Dumps a SwfMovie (Scaleform .gfx) resource's structure as JSON: header
        /// fields plus the tag table (each tag's type, offset, length and raw hex
        /// body). Tag bodies are NOT field-decoded — for full editable XML use ffdec
        /// (-swf2xml) on the dumped .gfx. Useful for quickly inspecting which tags a
        /// movie contains (DefineEditText, PlaceObject3, DefineSprite, etc.).
        /// </summary>
        /// <param name="p_Name">Name of the SwfMovie resource.</param>
        /// <param name="p_Destination">Destination .json file to write.</param>
        /// <exception cref="Exception">If the resource is missing or not a SwfMovie.</exception>
        internal void DumpSwfJson(string p_Name, FileInfo p_Destination)
        {
            if (!m_Mounter.TryGetResource(p_Name, out var s_Resource))
                throw new Exception($"Could not find resource with name '{p_Name}'.");

            var s_Variant = s_Resource.FirstVariant;
            if (s_Variant.GetResourceType() != ResourceType.SwfMovie)
                throw new Exception($"Resource '{p_Name}' is not a SwfMovie (it is {s_Variant.GetResourceType()}).");

            using var s_Reader = s_Variant.GetReader();
            var s_Swf = new SwfFile(s_Reader);

            var s_Tags = new List<object>();
            for (var i = 0; i < s_Swf.Tags.Count; ++i)
            {
                var s_Tag = s_Swf.Tags[i];
                s_Tags.Add(new
                {
                    index = i,
                    tag = s_Tag.Tag.ToString(),
                    tagId = (int)s_Tag.Tag,
                    offset = s_Tag.Offset,
                    length = s_Tag.Data.Length,
                    dataHex = Convert.ToHexString(s_Tag.Data),
                });
            }

            var s_Doc = new
            {
                name = p_Name,
                version = s_Swf.Version,
                compressed = s_Swf.IsCompressed,
                stripped = s_Swf.IsStripped,
                fps = s_Swf.Fps,
                frameCount = s_Swf.FrameCount,
                rect = new { left = s_Swf.Rect.X, top = s_Swf.Rect.Y, right = s_Swf.Rect.Z, bottom = s_Swf.Rect.W },
                tagCount = s_Swf.Tags.Count,
                tags = s_Tags,
            };

            if (p_Destination.Directory != null && !Directory.Exists(p_Destination.Directory.FullName))
                Directory.CreateDirectory(p_Destination.Directory.FullName);

            File.WriteAllText(p_Destination.FullName, JsonConvert.SerializeObject(s_Doc, Formatting.Indented));
        }

        /// <summary>
        /// Dumps a TerrainDecals (.decals) resource's full structure as JSON: header,
        /// the three geometries (2d/3d/water) with their blocks, decoded vertices and
        /// indices. Reading the resource and writing it back is byte-identical to vanilla.
        /// </summary>
        /// <param name="p_Name">Name of the TerrainDecals resource.</param>
        /// <param name="p_Destination">Destination .json file to write.</param>
        /// <param name="p_Formatting">JSON formatting (whitespace only; does not affect rebuild).</param>
        /// <exception cref="Exception">If the resource is missing or not a TerrainDecals.</exception>
        internal void DumpTerrainDecalsJson(string p_Name, FileInfo p_Destination, Formatting p_Formatting)
        {
            if (!m_Mounter.TryGetResource(p_Name, out var s_Resource))
                throw new Exception($"Could not find resource with name '{p_Name}'.");

            var s_Variant = s_Resource.FirstVariant;
            if (s_Variant.GetResourceType() != ResourceType.TerrainDecals)
                throw new Exception($"Resource '{p_Name}' is not a TerrainDecals (it is {s_Variant.GetResourceType()}).");

            using var s_Reader = s_Variant.GetReader();
            var s_Converter = EngineInterfaceRegistry.Create<ITerrainDecalsConverter>(m_Mounter.GetEngineType());
            var s_Decals = s_Converter.Read(s_Reader);

            if (p_Destination.Directory != null && !Directory.Exists(p_Destination.Directory.FullName))
                Directory.CreateDirectory(p_Destination.Directory.FullName);

            File.WriteAllText(p_Destination.FullName, JsonConvert.SerializeObject(
                s_Decals, p_Formatting, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        }

        /// <summary>
        /// Dumps a partition
        /// </summary>
        /// <param name="p_Name">Name of the partition to dump</param>
        /// <param name="p_Destination">Destination file to write</param>
        /// <exception cref="Exception">If the partition does not exist, exception will be thrown</exception>
        internal void DumpPartition(string p_Name, FileInfo p_Destination)
        {
            if (!m_Mounter.TryGetPartition(p_Name, out var s_Partition))
                throw new Exception($"Could not find partition with name '{p_Name}'.");

            // Ensure that the directory of the destination file exists
            if (p_Destination.Directory != null && !Directory.Exists(p_Destination.Directory.FullName))
                Directory.CreateDirectory(p_Destination.Directory.FullName);

            using var s_Reader = s_Partition.FirstVariant.GetReader();
            using var s_FileStream = File.Create(p_Destination.FullName);

            s_Reader.CopyTo(s_FileStream);
        }

        /// <summary>
        /// Dumps a partition
        /// </summary>
        /// <param name="p_GUID">Guid of the partition to dump</param>
        /// <param name="p_Destination">Destination file to write</param>
        /// <exception cref="Exception">If the partition does not exist, exception will be thrown</exception>
        internal void DumpPartitionByGuid(GUID p_GUID, FileInfo p_Destination)
        {
            if (!m_Mounter.TryGetPartitionByGuid(p_GUID, out var Name, out var s_Partition))
                throw new Exception($"Could not find partition with GUID '{p_GUID.ToString("D")}'.");

            // Ensure that the directory of the destination file exists
            if (p_Destination.Directory != null && !Directory.Exists(p_Destination.Directory.FullName))
                Directory.CreateDirectory(p_Destination.Directory.FullName);

            using var s_Reader = s_Partition.FirstVariant.GetReader();
            using var s_FileStream = File.Create(p_Destination.FullName);

            s_Reader.CopyTo(s_FileStream);
        }

        /// <summary>
        /// Dumps a partition as json
        /// </summary>
        /// <param name="p_Name">Name of partition to dump</param>
        /// <param name="p_Destination">Destination file to write</param>
        /// <param name="p_Formatting">Formatting type, indented or not</param>
        /// <exception cref="Exception">If the partition does not exist, exception will be thrown</exception>
        internal void DumpPartitionJson(string p_Name, FileInfo p_Destination, Formatting p_Formatting)
        {
            if (!m_Mounter.TryGetPartition(p_Name, out var s_PartitionObject))
                throw new Exception($"Could not find partition with name '{p_Name}'.");

            // Ensure that the directory of the destination file exists
            if (p_Destination.Directory != null && !Directory.Exists(p_Destination.Directory.FullName))
                Directory.CreateDirectory(p_Destination.Directory.FullName);

            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(m_Mounter.GetEngineType());

            var s_Partition = s_Converter.FromPartitionObject(p_Name, s_PartitionObject.FirstVariant);
            s_Partition.ToJsonFile(p_Destination.FullName, p_Formatting);
        }

        /// <summary>
        /// Dumps a partition as json
        /// </summary>
        /// <param name="p_GUID">Guid of partition to dump</param>
        /// <param name="p_Destination">Destination file to write</param>
        /// <param name="p_Formatting">Formatting type, indented or not</param>
        /// <exception cref="Exception">If the partition does not exist, exception will be thrown</exception>
        internal void DumpPartitionJsonByGuid(GUID p_GUID, FileInfo p_Destination, Formatting p_Formatting)
        {
            if (!m_Mounter.TryGetPartitionByGuid(p_GUID, out var s_Name, out var s_PartitionObject))
                throw new Exception($"Could not find partition with GUID '{p_GUID.ToString("D")}'.");

            // Ensure that the directory of the destination file exists
            if (p_Destination.Directory != null && !Directory.Exists(p_Destination.Directory.FullName))
                Directory.CreateDirectory(p_Destination.Directory.FullName);

            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(m_Mounter.GetEngineType());

            var s_Partition = s_Converter.FromPartitionObject(s_Name, s_PartitionObject.FirstVariant);
            s_Partition.ToJsonFile(p_Destination.FullName, p_Formatting);
        }

        /// <summary>
        /// Dumps a texture as Direct Draw Surface (.dds) format
        /// </summary>
        /// <param name="p_Name">Name of texture to dump</param>
        /// <param name="p_Destination">Destination file to write</param>
        /// <exception cref="Exception">If the texture resource name is not found, exception will be thrown</exception>
        internal void DumpTexture(string p_Name, FileInfo p_Destination)
        {
            if (!m_Mounter.TryGetResource(p_Name, out var s_Resource))
                throw new Exception($"Could not find resource with name '{p_Name}'.");

            // Ensure that the directory of the destination file exists
            if (p_Destination.Directory != null && !Directory.Exists(p_Destination.Directory.FullName))
                Directory.CreateDirectory(p_Destination.Directory.FullName);

            var s_Converter = EngineInterfaceRegistry.Create<ITextureConverter>(m_Mounter.GetEngineType());

            var s_FileStream = File.Create(p_Destination.FullName);
            using var s_Writer = new RimeWriter(s_FileStream);

            s_Converter.ConvertToDDS(s_Resource.FirstVariant!, m_Mounter, s_Writer);
        }

        /// <summary>
        /// Get the chunks of a specified superbundle
        /// </summary>
        /// <param name="p_Superbundle">Superbundle name</param>
        /// <returns>Enumerable of GUIDs for each of the superbundle chunks</returns>
        internal IEnumerable<GUID> GetSuperbundleChunks(string p_Superbundle)
        {
            return m_Mounter.GetChunksInSuperbundle(p_Superbundle);
        }

        /// <summary>
        /// Get the chunks of a specified bundle
        /// </summary>
        /// <param name="p_Bundle">Bundle name</param>
        /// <returns>Enumerable of GUIDs for each of the bundle chunks</returns>
        internal IEnumerable<GUID> GetBundleChunks(string p_Bundle)
        {
            return m_Mounter.GetChunksInBundle(p_Bundle);
        }

        /// <summary>
        /// Get the chunks (with asset name hash) of a specified bundle. (Only non cas bundles.)
        /// </summary>
        /// <param name="p_Bundle">Bundle name</param>
        /// <returns>Enumerable of GUIDs for each of the bundle chunks</returns>
        internal IEnumerable<(GUID Guid, int AssetNameHash)> GetBundleChunksWithHash(string p_Bundle)
        {
            return m_Mounter.GetChunksWithHashInBundle(p_Bundle);
        }

        /// <summary>
        /// Gets the resources of a specified bundle
        /// </summary>
        /// <param name="p_Bundle">Bundle name</param>
        /// <returns>Enumerable of resource names for each of the bundle resources</returns>
        internal IEnumerable<(string Name, ResourceType ResourceType)> GetBundleResources(string p_Bundle)
        {
            return m_Mounter.GetResourcesInBundle(p_Bundle);
        }

        /// <summary>
        /// Gets the available bundle partitions
        /// </summary>
        /// <param name="p_Bundle">Enumerable of bundle partition names</param>
        /// <returns></returns>
        internal IEnumerable<string> GetBundlePartitions(string p_Bundle)
        {
            return m_Mounter.GetPartitionsInBundle(p_Bundle);
        }

        /// <summary>
        /// Gets the mounted chunks
        /// </summary>
        /// <returns>Enumerable of chunk GUIDs</returns>
        internal IEnumerable<GUID> GetMountedChunks()
        {
            return m_Mounter.GetChunks().Keys;
        }

        /// <summary>
        /// Get the mounted resources
        /// </summary>
        /// <returns>Enumerable of resource names</returns>
        internal IEnumerable<string> GetMountedResources()
        {
            return m_Mounter.GetResources().Keys;
        }

        /// <summary>
        /// Get the mounted partitions
        /// </summary>
        /// <returns>Enumerable of mounted partition names</returns>
        internal IEnumerable<string> GetMountedPartitions()
        {
            return m_Mounter.GetPartitions().Keys;
        }

        /// <summary>
        /// Gets a dictionary of GUID <-> Mounted chunk variations
        /// </summary>
        /// <returns>Dictionary<GUID, IMountedObject<IChunkVariant></returns>
        internal IReadOnlyDictionary<GUID, IMountedObject<IChunkVariant>> GetMountedChunkVariations()
        {
            return m_Mounter.GetChunks();
        }

        /// <summary>
        /// Gets a dictionary of resource name <-> Mounted resource variations
        /// </summary>
        /// <returns>Dictionary<string, IMountedObject<IResourceVariant>></returns>
        internal IReadOnlyDictionary<string, IMountedObject<IResourceVariant>> GetMountedResourceVariations()
        {
            return m_Mounter.GetResources();
        }

        /// <summary>
        /// Dumps a specified mesh in a specific format
        /// </summary>
        /// <param name="p_Type">Output mesh type (gltf, obj, etc.)</param>
        /// <param name="p_Name">Name of the MeshSet resource</param>
        /// <param name="p_Destination">Output file destination</param>
        /// <exception cref="NotImplementedException">If the format isn't supported</exception>
        /// <summary>
        /// LOD-0 subset -> material -> surface shader, walked in the SAME order the OBJ converter walks
        /// subsets (its usemtl Material_N groups are numbered by that walk). The shader comes from the
        /// mesh partition's own MeshMaterial instances — the same resolution the MVDB tooling uses.
        /// </summary>
        /// <summary>
        /// The model-space pose of a skeleton's bones, one 3x4 row-vector transform per bone (right, up,
        /// forward, translation), read from its SkeletonAsset. Null when the partition is not mounted or
        /// holds no skeleton.
        /// </summary>
        private (string Name, List<float[]> Bones)? LoadSkeletonPose(string p_Name, TextWriter p_Writer) =>
            LoadSkeletonPose(p_Name, p_Writer, out _);

        /// <summary>
        /// A pose FILE (dump_weapon_pose's output) as the per-bone transforms the dump applies: the file's
        /// transform for every bone it names, the identity for the rest, in the order of the skeleton the
        /// file names. Null when the file or its skeleton cannot be read.
        /// </summary>
        private (string Name, List<float[]> Bones)? LoadPoseFile(string p_Path, TextWriter p_Writer)
        {
            WeaponPose.PoseFile s_File;
            try
            {
                s_File = WeaponPose.Read(p_Path);
            }
            catch (Exception s_Exception)
            {
                p_Writer.WriteLine($"MESHPOSE: pose file '{p_Path}' unreadable ({s_Exception.Message}) — vertices stay as stored.");
                return null;
            }

            if (LoadSkeletonPose(s_File.Skeleton, p_Writer, out var s_Names) is not { } s_Rest)
                return null;

            var s_Bones = new List<float[]>();
            var s_Applied = 0;
            for (var i = 0; i < s_Rest.Bones.Count; i++)
            {
                if (i < s_Names.Count && s_File.Bones.TryGetValue(s_Names[i], out var s_Transform) && s_Transform.Length == 12)
                {
                    s_Bones.Add(s_Transform);
                    s_Applied++;
                }
                else
                {
                    s_Bones.Add(new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 });
                }
            }

            p_Writer.WriteLine($"MESHPOSE: pose file {Path.GetFileName(p_Path)} (bank {s_File.Bank}, '{s_File.Anim}'): " +
                               $"{s_Applied} of {s_Bones.Count} bone(s) posed, the rest stay.");
            return (p_Path, s_Bones);
        }

        private (string Name, List<float[]> Bones)? LoadSkeletonPose(string p_Name, TextWriter p_Writer, out List<string> p_BoneNames)
        {
            p_BoneNames = new List<string>();
            if (!m_Mounter.TryGetPartition(p_Name, out var s_Mounted))
            {
                p_Writer.WriteLine($"MESHPOSE: skeleton '{p_Name}' is not mounted — vertices stay in bone space.");
                return null;
            }

            try
            {
                var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(m_Mounter.GetEngineType());
                if (s_Converter.FromPartitionObject(p_Name, s_Mounted.FirstVariant)
                        is not RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition s_Partition)
                    return null;

                var s_Skeleton = s_Partition.InstanceMap.Values.OfType<fb.SkeletonAsset>().FirstOrDefault();
                if (s_Skeleton == null)
                {
                    p_Writer.WriteLine($"MESHPOSE: '{p_Name}' holds no SkeletonAsset — vertices stay in bone space.");
                    return null;
                }

                var s_Bones = new List<float[]>();
                foreach (var s_Pose in s_Skeleton.ModelPose)
                    s_Bones.Add(new[]
                    {
                        s_Pose.right.x, s_Pose.right.y, s_Pose.right.z,
                        s_Pose.up.x, s_Pose.up.y, s_Pose.up.z,
                        s_Pose.forward.x, s_Pose.forward.y, s_Pose.forward.z,
                        s_Pose.trans.x, s_Pose.trans.y, s_Pose.trans.z,
                    });

                p_BoneNames = s_Skeleton.BoneNames.ToList();
                p_Writer.WriteLine($"MESHPOSE: skeleton={s_Skeleton.Name} bones={s_Bones.Count} " +
                                   $"({string.Join(",", s_Skeleton.BoneNames)})");
                return (p_Name, s_Bones);
            }
            catch (Exception s_Exception)
            {
                p_Writer.WriteLine($"MESHPOSE: skeleton '{p_Name}' unreadable ({s_Exception.Message}) — vertices stay in bone space.");
                return null;
            }
        }

        /// <summary>
        /// Writes, per LOD-0 subset of a mesh, its shader and its geometry, as the sectioned binary the
        /// preview and the sticker composer read ("RSM4": per section a shader name, a material name, the
        /// category, the double-sided flag, then position+uv per vertex and relative indices).
        ///
        /// A skinned weapon comes out "disassembled" — the feed cover floating over the receiver, the
        /// magazine hanging below the grip (keku, 2026-09-11, with pictures of the M240 and the AS VAL).
        /// MEASURED: its vertices are stored in model space with every animated part sitting around the
        /// REST position of its bone in the shared weapon skeleton (animations/skeletons/weapon/weaponske01,
        /// 25 translation-only bones, one for every hand-held weapon); the game assembles the parts with the
        /// weapon's own animation package (animations/antanimations/&lt;weapon&gt;), and posing with the rest
        /// skeleton only doubles the offsets. The dump therefore stays as the game stores it, and the bone
        /// palettes and per-vertex bone indices are read so that a POSE FILE (dump_weapon_pose: the idle pose of
        /// the weapon's own animation package, composed on that skeleton) assembles it — each vertex blended
        /// through the transforms of its bones. The RSM4 stamp marks dumps made by this version; consumers
        /// re-dump anything older.
        /// </summary>
        /// A COMPOSITE object (every vehicle body) comes out disassembled for a different reason, and no
        /// skeleton can fix it: there is none. Its geometry is split into PARTS, each modelled around its own
        /// origin, and the placement of each part lives in the mesh layout itself — the three fields after
        /// PartCount are a union read by mesh type (the same trio Frosty's MeshSet reader calls
        /// bonePartOffset01/02/03): for a skinned mesh they are the bone index array and the bone short-name
        /// hashes, for a composite one they are a bounding box per part, a TRANSFORM per part, and, per
        /// section, a 24-byte bitfield naming the parts that section touches. A vertex says which part it
        /// belongs to in its BoneIndices element (UByte4, and no weights at all), so p_Parts assembles the
        /// object by running every vertex through the transform of its own part.
        /// <param name="p_Skeleton">
        /// A skeleton partition whose ModelPose assembles the mesh, or null to leave the vertices as stored.
        /// </param>
        /// <param name="p_Parts">
        /// True assembles a COMPOSITE mesh with its own part transforms. Opt-in on purpose: the weapons
        /// taught that transforming vertices that are already placed doubles the offset, so a caller asks for
        /// it and checks the result with a picture.
        /// </param>
        internal int DumpMeshSections(string p_Name, FileInfo p_Destination, TextWriter p_Writer,
            string? p_ShaderDb = null, string? p_Skeleton = null, bool p_Parts = false, int p_UvSet = 0,
            IReadOnlyCollection<string>? p_Swap = null)
        {
            // Per-shader double-sided flags, read from the level's shaderdb when one is named: DoubleSided
            // is bit 1 of each SOLUTION's Flags (measured on the glass preset's RE), and it is consistent
            // enough per shader that "any visible solution carries it" is the per-section answer a preview
            // needs. No shaderdb (or a parse failure) just means every section culls back faces.
            var s_DoubleSided = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(p_ShaderDb) &&
                m_Mounter.TryGetResource(p_ShaderDb!, out var s_DbResource) && s_DbResource.FirstVariant != null)
                try
                {
                    var s_Resolver = EngineInterfaceRegistry.Create<RimeLib.Shader.IShaderResolver>(m_Mounter.GetEngineType());
                    s_Resolver.Initialize(s_DbResource.FirstVariant, m_Mounter);
                    var s_Container = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.GetValue(s_Resolver);
                    if (s_Container?.GetType().GetProperty("Databases")?.GetValue(s_Container)
                        is System.Collections.IDictionary s_Databases)
                        foreach (var s_PathKey in s_Databases.Keys)
                        {
                            if (s_Databases[s_PathKey]?.GetType().GetProperty("Shaders")
                                    ?.GetValue(s_Databases[s_PathKey]) is not System.Collections.IDictionary s_Shaders)
                                continue;

                            foreach (var s_ShaderKey in s_Shaders.Keys)
                            {
                                var s_Info = s_Shaders[s_ShaderKey];
                                if (s_Info?.GetType().GetProperty("Solutions")?.GetValue(s_Info)
                                        is not System.Array s_Solutions)
                                    continue;

                                foreach (var s_Solution in s_Solutions)
                                    if (s_Solution?.GetType().GetProperty("Flags")?.GetValue(s_Solution) is byte s_Flags &&
                                        (s_Flags & 1) != 0)
                                    {
                                        s_DoubleSided.Add(s_ShaderKey?.ToString() ?? "");
                                        break;
                                    }
                            }
                        }
                }
                catch (Exception s_Exception)
                {
                    p_Writer.WriteLine($"(double-sided flags unavailable: {s_Exception.Message})");
                }

            bool IsDoubleSided(string p_Shader) =>
                s_DoubleSided.Contains(p_Shader) ||
                s_DoubleSided.Any(p_K => p_K.EndsWith("/" + p_Shader, StringComparison.OrdinalIgnoreCase) ||
                                         p_Shader.EndsWith("/" + p_K, StringComparison.OrdinalIgnoreCase));
            // The material list, by index, from the mesh EBX partition.
            var s_ShadersByIndex = new System.Collections.Generic.Dictionary<int, string>();
            if (m_Mounter.TryGetPartition(p_Name, out var s_MeshMounted))
            {
                var s_Variant = s_MeshMounted.Variants.FirstOrDefault(p_V => p_V.GetContainedBundle() != null)
                                ?? s_MeshMounted.FirstVariant;
                var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(m_Mounter.GetEngineType());

                if (s_Variant != null &&
                    s_Converter.FromPartitionObject(p_Name, s_Variant)
                        is RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition s_Partition)
                {
                    var s_MeshAsset = s_Partition.InstanceMap.Values.OfType<fb.MeshAsset>().FirstOrDefault();
                    if (s_MeshAsset != null)
                    {
                        var s_Index = 0;
                        foreach (var s_MaterialRef in s_MeshAsset.Materials)
                        {
                            if ((s_MaterialRef.InstanceId as DataContainerId.Guid)?.Id is { } s_MaterialGuid &&
                                s_Partition.InstanceMap.TryGetValue(s_MaterialGuid, out var s_Instance) &&
                                s_Instance is fb.MeshMaterial s_Material &&
                                m_Mounter.TryGetPartitionByGuid(s_Material.Shader.Shader.PartitionGuid,
                                    out var s_ShaderName, out _))
                                s_ShadersByIndex[s_Index] = s_ShaderName;

                            s_Index++;
                        }
                    }
                }
            }

            // The LOD-0 geometry, decoded per VISIBLE-category subset (opaque, transparent, transparent
            // decal — NOT ZOnly) straight from the vertex streams, written as a simple sectioned binary
            // ("RSM1"). The OBJ path was abandoned for this: its writer only walks the OPAQUE category
            // (a glass canopy silently vanished) and renames every material, so the section mapping was
            // unrecoverable from the file.
            var s_Count = 0;

            foreach (var s_Resource in m_Mounter.GetResources().Where(p_R => p_R.Key == p_Name))
            {
                var s_Variant = s_Resource.Value.FirstVariant;
                if (s_Variant.GetResourceType() != ResourceType.MeshSet)
                    continue;

                using var s_ResourceReader1 = s_Variant.GetReader();
                var s_Data = s_ResourceReader1.ReadBytes((int) s_ResourceReader1.Length);
                using var s_Reader = new RimeReader(new MemoryStream(s_Data));

                var s_MeshSet = new RimeLib.Mesh.Frostbite.MeshSetLayout(s_Reader);
                var s_MeshLayout = s_MeshSet.Lods[0].Object;
                if (s_MeshLayout == null)
                    continue;

                if (!m_Mounter.TryGetChunk(s_MeshLayout.DataChunkId, out var s_MeshChunk))
                {
                    p_Writer.WriteLine($"Mesh data chunk {s_MeshLayout.DataChunkId} is not mounted.");
                    break;
                }

                using var s_ChunkReader1 = s_MeshChunk.FirstVariant.GetReader();
                var s_ChunkData = s_ChunkReader1.ReadBytes((int) s_ChunkReader1.Length);
                var s_VertexData = s_ChunkData.AsSpan(0, (int) s_MeshLayout.VertexDataSize).ToArray();
                var s_IndexData = s_ChunkData.AsSpan((int) s_MeshLayout.VertexDataSize,
                    (int) s_MeshLayout.IndexDataSize).ToArray();

                // The categories a player can SEE, deduplicated (a subset may be listed by more than one);
                // each subset remembers the FIRST category that listed it — a preview needs to know a
                // transparent section from an opaque one to stand in for it sanely.
                var s_SubsetIndices = new System.Collections.Generic.List<(int Index, int Category)>();
                foreach (var s_Category in new[]
                         {
                             RimeLib.Mesh.Frostbite.Fb2.MeshSubsetCategory.Opaque,
                             RimeLib.Mesh.Frostbite.Fb2.MeshSubsetCategory.Transparent,
                             RimeLib.Mesh.Frostbite.Fb2.MeshSubsetCategory.TransparentDecal,
                         })
                    foreach (int s_SubsetIndex in s_MeshLayout.CategorySubsetIndices[(int) s_Category].Get)
                        if (s_SubsetIndices.All(p_S => p_S.Index != s_SubsetIndex))
                            s_SubsetIndices.Add((s_SubsetIndex, (int) s_Category));

                using var s_Out = new BinaryWriter(File.Create(p_Destination.FullName));
                // RSM5 carries BOTH texture coordinate sets per vertex (RSM4 carried one). A vehicle body
                // preset samples its diffuse with one and its normal map with the other, so a dump that
                // keeps a single set cannot draw it whichever one it keeps — the consumer needs both and
                // decides per shader, the way the game's vertex shader does.
                // RSM6 adds the part index per vertex (see the write below); RSM5 added the second UV set.
                // RSM7 adds a THIRD uv slot per vertex (the jets' TexCoord2, or empty), and the slots are
                // written in the order the section's shader hands them over (MESHUV `written=`).
                s_Out.Write(System.Text.Encoding.ASCII.GetBytes("RSM7"));
                var s_CountPosition = s_Out.BaseStream.Position;
                s_Out.Write(0);

                using var s_VertexReader = new RimeReader(new MemoryStream(s_VertexData));

                // What the layout says about its bones — or about its PARTS: the count and the two tables,
                // read raw at their pointers. Which of the two meanings applies is the mesh's TYPE, so the
                // type is printed first; a mesh that says nothing about itself is how a whole family (the
                // vehicles) went unnoticed while this line only came out for skinned meshes.
                var s_LodType = s_MeshLayout.Type;
                var s_Skinned = s_MeshSet.MeshType == fb.MeshType.MeshType_Skinned;
                var s_Composite = s_LodType == fb.MeshType.MeshType_Composite ||
                                  s_MeshSet.MeshType == fb.MeshType.MeshType_Composite;

                string Peek(ulong p_Address, int p_Words)
                {
                    if (p_Address == 0 || (long) p_Address + p_Words * 4L > s_Reader.Length)
                        return "(none)";

                    s_Reader.Seek((long) p_Address, SeekOrigin.Begin);
                    var s_Words = new List<string>();
                    for (var i = 0; i < p_Words; i++)
                    {
                        var s_Word = s_Reader.ReadUInt32();
                        s_Words.Add(s_Word < 0x10000 ? s_Word.ToString() : $"0x{s_Word:X8}/{BitConverter.ToSingle(BitConverter.GetBytes(s_Word), 0):0.###}");
                    }

                    return string.Join(",", s_Words);
                }

                p_Writer.WriteLine($"MESHLAYOUT: type={s_MeshSet.MeshType}/{s_LodType} partCount={s_MeshLayout.PartCount} " +
                                   $"boneIndexArrayPartBoxes@{s_MeshLayout.BoneIndexArrayPartBoundingBoxes.BaseAddress}=[{Peek(s_MeshLayout.BoneIndexArrayPartBoundingBoxes.BaseAddress, (int) System.Math.Min(64, System.Math.Max(s_MeshLayout.PartCount, 1)))}] " +
                                   $"boneShortNamesPartTransforms@{s_MeshLayout.BoneShortNameArrayPartTransforms.BaseAddress}=[{Peek(s_MeshLayout.BoneShortNameArrayPartTransforms.BaseAddress, (int) System.Math.Min(64, System.Math.Max(s_MeshLayout.PartCount, 1)))}] " +
                                   $"subsetPartIndices@{s_MeshLayout.SubsetPartIndices} data@{s_MeshLayout.Data} auxOffset={s_MeshLayout.AuxVertexIndexDataOffset} " +
                                   $"edgeSize={s_MeshLayout.EdgePartitionBufferSize} setFlags={s_MeshSet.Flags} lodFlags={s_MeshLayout.Flags}");

                // The parts of a composite mesh, decoded through that union: a box and a transform per part,
                // and the parts each section rides. A float3 in a resource is padded to 16 bytes, so a box is
                // 32 bytes and a transform (right, up, forward, trans) is 64.
                var s_PartTransforms = new List<float[]>();
                var s_SubsetParts = new System.Collections.Generic.Dictionary<int, List<int>>();
                if (s_Composite && s_MeshLayout.PartCount > 0)
                {
                    float[] ReadPaddedVector3()
                    {
                        var s_X = s_Reader.ReadSingle();
                        var s_Y = s_Reader.ReadSingle();
                        var s_Z = s_Reader.ReadSingle();
                        s_Reader.ReadSingle();
                        return new[] { s_X, s_Y, s_Z };
                    }

                    var s_Boxes = new List<float[]>();
                    var s_BoxAddress = s_MeshLayout.BoneIndexArrayPartBoundingBoxes.BaseAddress;
                    if (s_BoxAddress != 0 && (long) s_BoxAddress + s_MeshLayout.PartCount * 32L <= s_Reader.Length)
                    {
                        s_Reader.Seek((long) s_BoxAddress, SeekOrigin.Begin);
                        for (var i = 0; i < s_MeshLayout.PartCount; i++)
                        {
                            var s_Min = ReadPaddedVector3();
                            var s_Max = ReadPaddedVector3();
                            s_Boxes.Add(new[] { s_Min[0], s_Min[1], s_Min[2], s_Max[0], s_Max[1], s_Max[2] });
                        }
                    }

                    var s_TransformAddress = s_MeshLayout.BoneShortNameArrayPartTransforms.BaseAddress;
                    if (s_TransformAddress != 0 && (long) s_TransformAddress + s_MeshLayout.PartCount * 64L <= s_Reader.Length)
                    {
                        s_Reader.Seek((long) s_TransformAddress, SeekOrigin.Begin);
                        for (var i = 0; i < s_MeshLayout.PartCount; i++)
                        {
                            var s_Right = ReadPaddedVector3();
                            var s_Up = ReadPaddedVector3();
                            var s_Forward = ReadPaddedVector3();
                            var s_Trans = ReadPaddedVector3();
                            s_PartTransforms.Add(new[]
                            {
                                s_Right[0], s_Right[1], s_Right[2],
                                s_Up[0], s_Up[1], s_Up[2],
                                s_Forward[0], s_Forward[1], s_Forward[2],
                                s_Trans[0], s_Trans[1], s_Trans[2],
                            });
                        }
                    }

                    // Per section, 24 bytes = one bit per part, low bit first (Frosty reads the same 0x18).
                    var s_SubsetCount = s_MeshLayout.Subsets.Get.Length;
                    if (s_MeshLayout.SubsetPartIndices != 0 &&
                        (long) s_MeshLayout.SubsetPartIndices + s_SubsetCount * 24L <= s_Reader.Length)
                    {
                        s_Reader.Seek((long) s_MeshLayout.SubsetPartIndices, SeekOrigin.Begin);
                        for (var s_S = 0; s_S < s_SubsetCount; s_S++)
                        {
                            var s_Parts = new List<int>();
                            for (var i = 0; i < 24; i++)
                            {
                                var s_Byte = s_Reader.ReadUByte();
                                for (var j = 0; j < 8; j++)
                                    if ((s_Byte & (1 << j)) != 0)
                                        s_Parts.Add(i * 8 + j);
                            }

                            s_SubsetParts[s_S] = s_Parts;
                        }
                    }

                    for (var i = 0; i < s_MeshLayout.PartCount; i++)
                        p_Writer.WriteLine($"MESHPART: part={i} " +
                                           (i < s_Boxes.Count
                                               ? $"box=({s_Boxes[i][0]:0.###},{s_Boxes[i][1]:0.###},{s_Boxes[i][2]:0.###})-({s_Boxes[i][3]:0.###},{s_Boxes[i][4]:0.###},{s_Boxes[i][5]:0.###}) "
                                               : "box=(none) ") +
                                           (i < s_PartTransforms.Count
                                               ? $"right=({s_PartTransforms[i][0]:0.###},{s_PartTransforms[i][1]:0.###},{s_PartTransforms[i][2]:0.###}) " +
                                                 $"up=({s_PartTransforms[i][3]:0.###},{s_PartTransforms[i][4]:0.###},{s_PartTransforms[i][5]:0.###}) " +
                                                 $"forward=({s_PartTransforms[i][6]:0.###},{s_PartTransforms[i][7]:0.###},{s_PartTransforms[i][8]:0.###}) " +
                                                 $"trans=({s_PartTransforms[i][9]:0.###},{s_PartTransforms[i][10]:0.###},{s_PartTransforms[i][11]:0.###})"
                                               : "transform=(none)"));
                }

                // The pose to assemble a skinned mesh with, ONLY when a skeleton is named. ⛔ MEASURED
                // (2026-09-11): the vertices of a weapon already sit in model space around each bone's REST
                // position of the shared weapon skeleton (the feed cover of the M240 at y 0.12-0.16 over a
                // receiver ending at 0.09); posing them with that same rest pose DOUBLES every offset. The
                // pose that assembles a weapon in the game is the one its own animation package drives, so
                // until that pose is available a dump stays as the game stores it, and a caller who has a
                // skeleton carrying the assembled pose names it.
                // A pose FILE (dump_weapon_pose) carries the assembled pose per bone in the mesh's own space; a
                // skeleton NAME applies its raw ModelPose (see above for why that alone is not the answer).
                (string Name, List<float[]> Bones)? s_Pose = null;
                if (s_Skinned && p_Skeleton != null)
                    s_Pose = p_Skeleton.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? LoadPoseFile(p_Skeleton, p_Writer)
                        : LoadSkeletonPose(p_Skeleton, p_Writer);

                var s_PosedVertices = 0;
                var s_UnposedVertices = 0;

                foreach (var (s_SubsetIndex, s_SubsetCategory) in s_SubsetIndices)
                {
                    var s_Subset = s_MeshLayout.Subsets.Get[s_SubsetIndex];
                    var s_MaterialIndex = (int) s_Subset.MaterialIndex;
                    var s_Shader = s_ShadersByIndex.TryGetValue(s_MaterialIndex, out var s_Found) ? s_Found : "";

                    // Decode position, the first UV and — for posing — the bone indices and weights per
                    // vertex; the preview computes normals and tangents itself. Formats are read by width —
                    // halves through the same conversion the exporter uses.
                    var s_Positions = new float[s_Subset.VertexCount * 3];
                    var s_Uvs = new float[s_Subset.VertexCount * 2];

                    // ⛔ A VEHICLE DECLARES TWO UV SETS, and which one carries the unique unwrap is not a
                    // given: the body presets come in a `vehiclepreset_mud` flavour (two sets) and a
                    // `vehiclepreset1uvset_mud` one (a single set), so the second set is what the family is
                    // named after. Both are read and both are measured (MESHUV below); p_UvSet says which
                    // one is written, because "the art does not follow the panels" is exactly what reading
                    // the wrong one looks like.
                    var s_Uvs1 = new float[s_Subset.VertexCount * 2];

                    // ⭐ AND A THIRD ONE. The jets' declaration (0x4544C182 on the F/A-18F) adds TexCoord2 after
                    // the bone indices, and their presets hand the pixel shader (set 1, set 2) as its pair and
                    // set 0 on another interpolator — measured in the game's own vertex shaders, 2026-09-22.
                    // Read whenever the declaration has it; written as the dump's third slot (RSM7).
                    var s_Uvs2 = new float[s_Subset.VertexCount * 2];
                    var s_HasThirdElement = false;
                    var s_BoneIds = new int[s_Subset.VertexCount * 4];
                    var s_BoneWeights = new float[s_Subset.VertexCount * 4];
                    var s_HasBoneIds = false;
                    var s_HasBoneWeights = false;
                    var s_Declaration = s_Subset.GeometryDeclarationDesc;

                    // A shader can only draw the vertex declarations it has a compiled solution for, so the
                    // layout is what decides whether a given preset is usable on this mesh at all. The hash
                    // is the one the shaderdb keys its solutions by (dump_shader_solutions prints decl=).
                    p_Writer.WriteLine($"MESHDECL: subset={s_SubsetIndex} material={s_MaterialIndex} " +
                                       $"stride={s_Subset.VertexStride} shader={s_Shader} decl=0x{s_Declaration.Hash:X8} elements=" +
                                       string.Join(",", s_Declaration.Elements
                                           .Where(e => e.Usage != fb.VertexElementUsage.VertexElementUsage_Unknown)
                                           .Select(e => $"{e.Usage}:{e.Format}@{e.Offset}/s{e.StreamIndex}")));

                    // ⛔ THE STREAMS, because a vertex is not always ONE interleaved block. A declaration can
                    // split its elements into several streams, and a stream's vertices are stored one after
                    // another — all of stream 0 for every vertex, then all of stream 1 — with the element
                    // offsets running CUMULATIVELY across them. Reading everything at one stride from one base
                    // (what this dumper did) only works while there is a single stream.
                    p_Writer.WriteLine($"MESHSTREAMS: subset={s_SubsetIndex} count={s_Declaration.Streams.Count} " +
                                       string.Join(",", s_Declaration.Streams.Select((p_S, p_I) => $"s{p_I}:stride={p_S.Stride}/{p_S.Classification}")));

                    // The subset's bone palette: a vertex's bone index is an index INTO THIS, not into the
                    // skeleton. Read straight from the layout at the pointer the subset carries.
                    var s_Palette = new int[s_Subset.BoneCount];
                    if (s_Subset.BoneCount > 0 && s_Subset.BoneIndices.BaseAddress != 0 &&
                        (long) s_Subset.BoneIndices.BaseAddress + s_Subset.BoneCount * 2L <= s_Reader.Length)
                    {
                        s_Reader.Seek((long) s_Subset.BoneIndices.BaseAddress, SeekOrigin.Begin);
                        for (var i = 0; i < s_Subset.BoneCount; i++)
                            s_Palette[i] = s_Reader.ReadUInt16();
                    }

                    // Where a vertex element lives, stream-aware (this is Frostbite's own layout, and the
                    // line Frosty's exporter uses): the streams are stored ONE AFTER ANOTHER — every vertex
                    // of stream 0, then every vertex of stream 1 — and an element's offset runs cumulatively
                    // across them, so the offset inside its own stream is Offset minus the strides before it.
                    // With a single stream this is exactly the old base + index * stride + offset.
                    long AddressOf(RimeLib.Mesh.Frostbite.GeometryDeclarationDesc.Element p_Element, int p_Vertex)
                    {
                        long s_Before = 0;
                        for (var i = 0; i < p_Element.StreamIndex && i < s_Declaration.Streams.Count; i++)
                            s_Before += s_Declaration.Streams[i].Stride;

                        var s_Stride = p_Element.StreamIndex < s_Declaration.Streams.Count &&
                                       s_Declaration.Streams[p_Element.StreamIndex].Stride > 0
                            ? s_Declaration.Streams[p_Element.StreamIndex].Stride
                            : s_Subset.VertexStride;

                        return s_Subset.VertexOffset + s_Before * s_Subset.VertexCount +
                               (long) p_Vertex * s_Stride + (p_Element.Offset - s_Before);
                    }

                    for (var s_VertexIndex = 0; s_VertexIndex < s_Subset.VertexCount; s_VertexIndex++)
                    {
                        foreach (var s_Element in s_Declaration.Elements)
                        {
                            var s_IsPosition = s_Element.Usage == fb.VertexElementUsage.VertexElementUsage_Pos;
                            var s_IsUv = s_Element.Usage == fb.VertexElementUsage.VertexElementUsage_TexCoord0;
                            var s_IsUv1 = s_Element.Usage == fb.VertexElementUsage.VertexElementUsage_TexCoord1;
                            var s_IsUv2 = s_Element.Usage == fb.VertexElementUsage.VertexElementUsage_TexCoord2;
                            var s_IsBoneIds = s_Element.Usage == fb.VertexElementUsage.VertexElementUsage_BoneIndices;
                            var s_IsBoneWeights = s_Element.Usage == fb.VertexElementUsage.VertexElementUsage_BoneWeights;
                            if (!s_IsPosition && !s_IsUv && !s_IsUv1 && !s_IsUv2 && !s_IsBoneIds && !s_IsBoneWeights)
                                continue;

                            s_VertexReader.Seek(AddressOf(s_Element, s_VertexIndex), SeekOrigin.Begin);

                            if (s_IsBoneIds || s_IsBoneWeights)
                            {
                                // UByte4 indices and UByte4N weights are what every weapon declares; any
                                // other width is read as bytes too, which is what the engine's own decode
                                // of those formats amounts to.
                                for (var c = 0; c < 4; c++)
                                {
                                    var s_Byte = s_VertexReader.ReadUByte();
                                    if (s_IsBoneIds)
                                        s_BoneIds[s_VertexIndex * 4 + c] = s_Byte;
                                    else
                                        s_BoneWeights[s_VertexIndex * 4 + c] = s_Byte / 255f;
                                }

                                s_HasBoneIds |= s_IsBoneIds;
                                s_HasBoneWeights |= s_IsBoneWeights;
                                continue;
                            }

                            var s_Components = ReadVertexElement(s_VertexReader, s_Element.Format);

                            if (s_IsPosition)
                                for (var c = 0; c < 3 && c < s_Components.Length; c++)
                                    s_Positions[s_VertexIndex * 3 + c] = s_Components[c];
                            else if (s_IsUv2)
                            {
                                s_HasThirdElement = true;
                                for (var c = 0; c < 2 && c < s_Components.Length; c++)
                                    s_Uvs2[s_VertexIndex * 2 + c] = s_Components[c];
                            }
                            else if (s_IsUv1)
                                for (var c = 0; c < 2 && c < s_Components.Length; c++)
                                    s_Uvs1[s_VertexIndex * 2 + c] = s_Components[c];
                            else
                                for (var c = 0; c < 2 && c < s_Components.Length; c++)
                                    s_Uvs[s_VertexIndex * 2 + c] = s_Components[c];
                        }
                    }

                    // Assemble a COMPOSITE object: every vertex rides the part it names in BoneIndices
                    // (through the subset's palette when it has one), and a part is placed by its own
                    // transform. A vertex naming a part the layout does not have stays where it is and is
                    // counted, so a wrong reading shows up as a number and not as a picture that is nearly
                    // right. The parts each subset touches are printed even when nothing is applied, because
                    // that is the measurement that says whether this reading is the right one at all.
                    // ⛔ MEASURED, and it is the opposite of the skinned rule: the byte a vertex carries is the
                    // part index ITSELF, not an index into the subset's list. The subset's list and the
                    // bitfield agree exactly (LAV-25 subset 1: palette [21,26,28,47,49] = declared
                    // [21,26,28,47,49]), and reading the byte through that list answered −1 for every single
                    // vertex — the values are 21, 26, 28… straight away. A skinned mesh keeps its palette.
                    var s_UsedParts = new SortedSet<int>();
                    if (s_Composite)
                    {
                        if (s_HasBoneIds)
                            for (var s_VertexIndex = 0; s_VertexIndex < s_Subset.VertexCount; s_VertexIndex++)
                            {
                                var s_Part = s_BoneIds[s_VertexIndex * 4];
                                s_UsedParts.Add(s_Part);

                                if (!p_Parts)
                                    continue;

                                if (s_Part < 0 || s_Part >= s_PartTransforms.Count)
                                {
                                    s_UnposedVertices++;
                                    continue;
                                }

                                var m = s_PartTransforms[s_Part];
                                var s_Px = s_Positions[s_VertexIndex * 3];
                                var s_Py = s_Positions[s_VertexIndex * 3 + 1];
                                var s_Pz = s_Positions[s_VertexIndex * 3 + 2];
                                s_Positions[s_VertexIndex * 3] = s_Px * m[0] + s_Py * m[3] + s_Pz * m[6] + m[9];
                                s_Positions[s_VertexIndex * 3 + 1] = s_Px * m[1] + s_Py * m[4] + s_Pz * m[7] + m[10];
                                s_Positions[s_VertexIndex * 3 + 2] = s_Px * m[2] + s_Py * m[5] + s_Pz * m[8] + m[11];
                                s_PosedVertices++;
                            }

                        // The check that says whether this reading is right: every part a VERTEX names must be
                        // one of the parts the SECTION declares. Those two come from opposite ends of the
                        // resource (a byte in the vertex stream against a bitfield in the layout), so an
                        // agreement is not a coincidence — and a disagreement names itself instead of
                        // arriving as a mesh that looks nearly right.
                        var s_Declared = s_SubsetParts.TryGetValue(s_SubsetIndex, out var s_Bits)
                            ? s_Bits
                            : new List<int>();
                        var s_Outside = s_UsedParts.Where(p_P => !s_Declared.Contains(p_P)).ToList();
                        p_Writer.WriteLine($"MESHPARTS: subset={s_SubsetIndex} bonesPerVertex={s_Subset.BonesPerVertex} " +
                                           $"palette=[{string.Join(",", s_Palette)}] " +
                                           $"declared=[{(s_Declared.Count > 0 ? string.Join(",", s_Declared) : "(none)")}] " +
                                           $"usedByVertices=[{string.Join(",", s_UsedParts)}] " +
                                           $"outsideDeclared=[{string.Join(",", s_Outside)}]");
                    }

                    // Assemble: every vertex through the model-space pose of its bones. A vertex whose
                    // weights are all zero rides its first bone whole; one naming a bone the skeleton does not
                    // have stays where it is and is counted, so a wrong skeleton reads as a number, not as
                    // a picture that is almost right.
                    var s_UsedBones = new SortedSet<int>();
                    if (s_Pose != null && s_HasBoneIds)
                    {
                        var s_Bones = s_Pose.Value.Bones;
                        for (var s_VertexIndex = 0; s_VertexIndex < s_Subset.VertexCount; s_VertexIndex++)
                        {
                            float s_X = 0, s_Y = 0, s_Z = 0, s_Total = 0;
                            var s_Px = s_Positions[s_VertexIndex * 3];
                            var s_Py = s_Positions[s_VertexIndex * 3 + 1];
                            var s_Pz = s_Positions[s_VertexIndex * 3 + 2];
                            var s_Valid = true;

                            for (var c = 0; c < 4; c++)
                            {
                                var s_Weight = s_HasBoneWeights ? s_BoneWeights[s_VertexIndex * 4 + c] : c == 0 ? 1f : 0f;
                                if (s_Weight <= 0f)
                                    continue;

                                var s_Local = s_BoneIds[s_VertexIndex * 4 + c];
                                var s_Bone = s_Palette.Length > 0
                                    ? s_Local < s_Palette.Length ? s_Palette[s_Local] : -1
                                    : s_Local;
                                if (s_Bone < 0 || s_Bone >= s_Bones.Count)
                                {
                                    s_Valid = false;
                                    break;
                                }

                                s_UsedBones.Add(s_Bone);
                                var m = s_Bones[s_Bone];
                                s_X += s_Weight * (s_Px * m[0] + s_Py * m[3] + s_Pz * m[6] + m[9]);
                                s_Y += s_Weight * (s_Px * m[1] + s_Py * m[4] + s_Pz * m[7] + m[10]);
                                s_Z += s_Weight * (s_Px * m[2] + s_Py * m[5] + s_Pz * m[8] + m[11]);
                                s_Total += s_Weight;
                            }

                            if (!s_Valid || s_Total <= 0f)
                            {
                                s_UnposedVertices++;
                                continue;
                            }

                            s_Positions[s_VertexIndex * 3] = s_X / s_Total;
                            s_Positions[s_VertexIndex * 3 + 1] = s_Y / s_Total;
                            s_Positions[s_VertexIndex * 3 + 2] = s_Z / s_Total;
                            s_PosedVertices++;
                        }
                    }

                    if (s_Skinned)
                        p_Writer.WriteLine($"MESHBONES: subset={s_SubsetIndex} bonesPerVertex={s_Subset.BonesPerVertex} " +
                                           $"palette=[{string.Join(",", s_Palette)}] used=[{string.Join(",", s_UsedBones)}]");

                    // Subset indices are 16-bit and RELATIVE to the subset's own vertex window.
                    var s_Indices = new int[s_Subset.PrimitiveCount * 3];
                    for (var i = 0; i < s_Indices.Length; i++)
                    {
                        var s_Offset = ((long) s_Subset.StartIndex + i) * sizeof(ushort);
                        s_Indices[i] = BitConverter.ToUInt16(s_IndexData, (int) s_Offset);
                    }

                    // What each UV set actually spans. A unique unwrap lives inside [0,1]; a set that runs
                    // to ±7 is a TILED one, and telling them apart by looking is the whole question here.
                    void Span(float[] p_Set, out float p_MinU, out float p_MinV, out float p_MaxU, out float p_MaxV)
                    {
                        p_MinU = p_MinV = float.MaxValue;
                        p_MaxU = p_MaxV = float.MinValue;
                        for (var i = 0; i < s_Subset.VertexCount; i++)
                        {
                            p_MinU = System.Math.Min(p_MinU, p_Set[i * 2]);
                            p_MaxU = System.Math.Max(p_MaxU, p_Set[i * 2]);
                            p_MinV = System.Math.Min(p_MinV, p_Set[i * 2 + 1]);
                            p_MaxV = System.Math.Max(p_MaxV, p_Set[i * 2 + 1]);
                        }
                    }

                    // The per-subset UV ratios the layout carries (float[6]) — Frostbite's own way of saying
                    // "these packed coordinates are a FRACTION of the real ones"; nothing here has ever used
                    // them, so they are printed before being trusted.
                    var s_Ratios = new List<string>();
                    for (var i = 0; i < s_Subset.TexCoordRatios.Count; i++)
                        s_Ratios.Add(s_Subset.TexCoordRatios[i].ToString("0.####"));

                    Span(s_Uvs, out var s_U0, out var s_V0, out var s_U1, out var s_V1);
                    Span(s_Uvs1, out var s_U2, out var s_V2, out var s_U3, out var s_V3);
                    Span(s_Uvs2, out var s_U4, out var s_V4, out var s_U5, out var s_V5);

                    var s_HasSecond = false;
                    for (var i = 0; i < s_Uvs1.Length && !s_HasSecond; i++)
                        s_HasSecond = s_Uvs1[i] != 0f;

                    var s_HasThird = false;
                    for (var i = 0; s_HasThirdElement && i < s_Uvs2.Length && !s_HasThird; i++)
                        s_HasThird = s_Uvs2[i] != 0f;

                    // The order a caller asked for THIS shader, by identity: `<shader>` is the plain swap (1:0),
                    // `<shader>=a:b:c` the three slots, and `<shader>@0x<decl>=a:b:c` binds one declaration of
                    // it (the exact form wins over the shader-wide one). Null when the shader is not listed.
                    static int[]? UvOrderOf(string p_Shader, uint p_Declaration, IReadOnlyCollection<string>? p_Entries)
                    {
                        if (p_Entries == null || p_Shader.Length == 0)
                            return null;

                        int[]? s_Found = null;
                        foreach (var s_Entry in p_Entries)
                        {
                            var s_Equals = s_Entry.IndexOf('=');
                            var s_Key = (s_Equals < 0 ? s_Entry : s_Entry[..s_Equals]).Trim();
                            var s_At = s_Key.IndexOf('@');
                            var s_Name = s_At < 0 ? s_Key : s_Key[..s_At];
                            if (!p_Shader.Equals(s_Name, StringComparison.OrdinalIgnoreCase))
                                continue;

                            var s_Exact = s_At >= 0;
                            if (s_Exact)
                            {
                                var s_Hex = s_Key[(s_At + 1)..];
                                if (s_Hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                                    s_Hex = s_Hex[2..];
                                if (!uint.TryParse(s_Hex, System.Globalization.NumberStyles.HexNumber, null, out var s_Decl) ||
                                    s_Decl != p_Declaration)
                                    continue;
                            }

                            var s_Order = s_Equals < 0
                                ? new[] { 1, 0 }
                                : s_Entry[(s_Equals + 1)..]
                                    .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Select(p_S => int.TryParse(p_S, out var s_Set) ? s_Set : -1)
                                    .ToArray();
                            if (s_Order.Length == 0)
                                s_Order = new[] { 1, 0 };

                            if (s_Exact)
                                return s_Order;
                            s_Found ??= s_Order;
                        }

                        return s_Found;
                    }

                    // ⭐ THE PAIR IS SWAPPED FOR THE SECTIONS WHOSE SHADER SWAPS IT, AND THAT IS MEASURED IN
                    // THE GAME'S OWN VERTEX SHADER. For the LAV-25's declaration, `vehiclepreset_mud` does
                    // `mov o8.xyzw, v3.zwxy` — the input holds both sets as one float4 (the two Half2 at 24
                    // and 28, read as R16G16B16A16@24) and the shader hands them over SWAPPED, so ITS .xy is
                    // the SECOND set. The lights, the slat cage and the kits do `mov o.xy, v3.xyxx`: no swap.
                    // The caller names those shaders — it derives them from the translated graphs, where a
                    // shader that swaps is one that reads TWO HALVES of the same interpolator — because a
                    // preview draws every section with ONE vertex shader and cannot decide this per section.
                    // ⛔ By IDENTITY (the full resource name), never by last segment: the BTR-90's
                    // `vehicles/xpack01/shaders/vehiclepreset_mud` reads ONE set (`mov o4.xy, v3.xyxx`, its
                    // declaration keeps TexCoord1 behind the bone indices) while `vehicles/shaders/
                    // vehiclepreset_mud` swaps the pair. An EndsWith("/vehiclepreset_mud") put the swap on
                    // the BTR — on a section whose second set is all zeros (2026-09-22).
                    // ⭐ THE SETS ARE WRITTEN IN THE ORDER THE SHADER'S OWN VERTEX SHADER HANDS THEM TO ITS
                    // PIXEL SHADER, three slots: the pair's .xy, the pair's .zw and the set of a second uv
                    // interpolator where the shader reads one. Measured per shader in the game's bytecode
                    // (the caller derives the list from the vertex shaders the translation leaves on disk):
                    //   vehicles/shaders/vehiclepreset_mud    `mov o8.xyzw, v3.zwxy`                      → 1,0
                    //   vehicles/shaders/vehiclepreset_jet    `o4.xy ← v4.zw · o4.zw ← v5.xy · o5.xy ← v4.xy` → 1,2,0
                    //   vehicles/shaders/vehiclepreset_lights `mov o5.xy, v3.xyxx`                       → 0
                    // A preview draws every section with ONE vertex shader per pixel shader and cannot decide
                    // this per section, so the order travels in the DATA. A set the section has not got falls
                    // back to the lowest one still unused (1,2,0 on a two-set section keeps its pair whole as
                    // 1,0); the third slot may stay empty.
                    var s_Order = UvOrderOf(s_Shader, s_Declaration.Hash, p_Swap);
                    var s_Available = new[] { true, s_HasSecond, s_HasThird };
                    var s_Sets = new[] { s_Uvs, s_Uvs1, s_Uvs2 };
                    var s_Slots = new[] { -1, -1, -1 };
                    string s_WrittenLabel;
                    if (s_Order != null)
                    {
                        var s_Used = new HashSet<int>();
                        for (var s_Slot = 0; s_Slot < 3; s_Slot++)
                        {
                            var s_Want = s_Slot < s_Order.Length ? s_Order[s_Slot] : -1;
                            if (s_Want < 0 || s_Want > 2 || !s_Available[s_Want] || s_Used.Contains(s_Want))
                                s_Want = s_Slot < 2
                                    ? Enumerable.Range(0, 3).Where(i => s_Available[i] && !s_Used.Contains(i)).DefaultIfEmpty(-1).First()
                                    : -1;
                            if (s_Want >= 0)
                                s_Used.Add(s_Want);
                            s_Slots[s_Slot] = s_Want;
                        }

                        s_WrittenLabel = "order:" + string.Join(",", s_Slots.Select(i => i < 0 ? "-" : i.ToString()));
                    }
                    else
                    {
                        // ⛔ WHICH SET THE DIFFUSE USES IS A MEASUREMENT, AND ON A VEHICLE IT IS THE SECOND ONE.
                        // Measured on the LAV-25 by drawing the wheel's triangles over the atlas: read as
                        // TexCoord0 they land on hull plates (and partly on the EMPTY background of the atlas);
                        // read as TexCoord1 they land exactly on the tyre. The first set is what the NORMAL map
                        // uses — the same atlas has the wheel's hub drawn in `_n` right where TexCoord0 pointed,
                        // and the preset's graph samples the normal with the OTHER half of the interpolator.
                        // A section whose second set is all zeros (the lights here, and everything a
                        // `…1uvset…` shader draws) has only one, and that one is the first.
                        var s_First = p_UvSet == 1 || (p_UvSet < 0 && s_HasSecond);
                        s_Slots = new[] { s_First ? 1 : 0, s_First ? 0 : 1, s_HasThird ? 2 : -1 };
                        s_WrittenLabel = p_UvSet < 0 ? (s_HasSecond ? "auto:1" : "auto:0") : p_UvSet.ToString();
                    }

                    p_Writer.WriteLine($"MESHUV: subset={s_SubsetIndex} written={s_WrittenLabel} " +
                                       $"uv0=({s_U0:0.###},{s_V0:0.###})-({s_U1:0.###},{s_V1:0.###}) " +
                                       $"uv1=({s_U2:0.###},{s_V2:0.###})-({s_U3:0.###},{s_V3:0.###}) " +
                                       (s_HasThirdElement ? $"uv2=({s_U4:0.###},{s_V4:0.###})-({s_U5:0.###},{s_V5:0.###}) " : "") +
                                       $"texCoordRatios=[{string.Join(",", s_Ratios)}]");

                    var s_Empty = new float[s_Subset.VertexCount * 2];
                    var s_UvWritten = s_Slots[0] >= 0 ? s_Sets[s_Slots[0]] : s_Uvs;
                    var s_UvOther = s_Slots[1] >= 0 ? s_Sets[s_Slots[1]] : s_Empty;
                    var s_UvExtra = s_Slots[2] >= 0 ? s_Sets[s_Slots[2]] : s_Empty;
                    var s_ShaderBytes = System.Text.Encoding.UTF8.GetBytes(s_Shader);
                    var s_MaterialBytes = System.Text.Encoding.UTF8.GetBytes(s_Subset.MaterialName.Object ?? "");
                    s_Out.Write(s_ShaderBytes.Length);
                    s_Out.Write(s_ShaderBytes);
                    s_Out.Write(s_MaterialBytes.Length);
                    s_Out.Write(s_MaterialBytes);
                    s_Out.Write(s_SubsetCategory);
                    s_Out.Write(s_Shader.Length > 0 && IsDoubleSided(s_Shader) ? 1 : 0);
                    s_Out.Write((int) s_Subset.VertexCount);
                    for (var i = 0; i < s_Subset.VertexCount; i++)
                    {
                        s_Out.Write(s_Positions[i * 3]);
                        s_Out.Write(s_Positions[i * 3 + 1]);
                        s_Out.Write(s_Positions[i * 3 + 2]);
                        s_Out.Write(s_UvWritten[i * 2]);
                        s_Out.Write(s_UvWritten[i * 2 + 1]);
                        s_Out.Write(s_UvOther[i * 2]);
                        s_Out.Write(s_UvOther[i * 2 + 1]);
                        s_Out.Write(s_UvExtra[i * 2]);
                        s_Out.Write(s_UvExtra[i * 2 + 1]);
                    }

                    s_Out.Write(s_Indices.Length);
                    foreach (var s_Index in s_Indices)
                        s_Out.Write(s_Index);

                    // RSM6: the PART each vertex rides, kept after the assembly has already moved it.
                    //
                    // ⛔ WHY IT HAS TO TRAVEL: a composite's section is one draw but several OBJECTS — the
                    // Sprut-SD's kit section is stowage AND the reactive-armour blocks, and the game equips
                    // the armour as an unlock while the stowage is always there. Once assembled, nothing in
                    // the dump says which triangle is which: the only thing that does is this byte, and it
                    // is read and thrown away here. A consumer can now split a section the way the game
                    // does. -1 for a mesh with no parts (every weapon), so the layout stays uniform.
                    for (var i = 0; i < s_Subset.VertexCount; i++)
                        s_Out.Write(s_Composite && s_HasBoneIds ? s_BoneIds[i * 4] : -1);

                    s_Count++;
                }

                if (s_Skinned)
                    p_Writer.WriteLine($"MESHPOSE: posed={s_PosedVertices} unposed={s_UnposedVertices} " +
                                       $"skeleton={(s_Pose?.Name ?? "(none)")}");
                else if (s_Composite)
                    p_Writer.WriteLine($"MESHASSEMBLY: parts={s_PartTransforms.Count} applied={p_Parts} " +
                                       $"placed={s_PosedVertices} left={s_UnposedVertices}");

                s_Out.Seek((int) s_CountPosition, SeekOrigin.Begin);
                s_Out.Write(s_Count);
                break;
            }

            return s_Count;
        }

        /// <summary>One vertex element as floats, by declared format width; halves converted like the exporter.</summary>
        private static float[] ReadVertexElement(RimeReader p_Reader,
            fb.VertexElementFormat p_Format)
        {
            switch (p_Format)
            {
                case fb.VertexElementFormat.VertexElementFormat_Float:
                    return new[] { p_Reader.ReadSingle() };
                case fb.VertexElementFormat.VertexElementFormat_Float2:
                    return new[] { p_Reader.ReadSingle(), p_Reader.ReadSingle() };
                case fb.VertexElementFormat.VertexElementFormat_Float3:
                    return new[] { p_Reader.ReadSingle(), p_Reader.ReadSingle(), p_Reader.ReadSingle() };
                case fb.VertexElementFormat.VertexElementFormat_Float4:
                    return new[]
                    {
                        p_Reader.ReadSingle(), p_Reader.ReadSingle(), p_Reader.ReadSingle(), p_Reader.ReadSingle(),
                    };
                case fb.VertexElementFormat.VertexElementFormat_Half:
                    return new[] { RimeLib.Math.RimeMath.HalfToFloat(p_Reader.ReadUInt16()) };
                case fb.VertexElementFormat.VertexElementFormat_Half2:
                    return new[]
                    {
                        RimeLib.Math.RimeMath.HalfToFloat(p_Reader.ReadUInt16()),
                        RimeLib.Math.RimeMath.HalfToFloat(p_Reader.ReadUInt16()),
                    };
                case fb.VertexElementFormat.VertexElementFormat_Half3:
                    return new[]
                    {
                        RimeLib.Math.RimeMath.HalfToFloat(p_Reader.ReadUInt16()),
                        RimeLib.Math.RimeMath.HalfToFloat(p_Reader.ReadUInt16()),
                        RimeLib.Math.RimeMath.HalfToFloat(p_Reader.ReadUInt16()),
                    };
                case fb.VertexElementFormat.VertexElementFormat_Half4:
                    return new[]
                    {
                        RimeLib.Math.RimeMath.HalfToFloat(p_Reader.ReadUInt16()),
                        RimeLib.Math.RimeMath.HalfToFloat(p_Reader.ReadUInt16()),
                        RimeLib.Math.RimeMath.HalfToFloat(p_Reader.ReadUInt16()),
                        RimeLib.Math.RimeMath.HalfToFloat(p_Reader.ReadUInt16()),
                    };
                default:
                    return Array.Empty<float>();
            }
        }

        internal void DumpMesh(MeshConverterType p_Type, string p_Name, FileInfo p_Destination)
        {
            // Get all resources
            var s_Resources = m_Mounter.GetResources();

            // Filter out by name
            var s_MeshResources = s_Resources.Where(p_Resource => p_Resource.Key == p_Name);

            // Iterate over all results
            foreach (var s_MeshResource in s_MeshResources)
            {
                // Get the name of the mesh
                var s_Name = s_MeshResource.Key;

                // Get the resource
                var s_Resource = s_MeshResource.Value;

                // Get the variant
                var s_Variant = s_Resource.FirstVariant;

                // Sanity check
                if (s_Variant.GetResourceType() != ResourceType.MeshSet)
                    continue;

                // Create a new mesh converter
                var s_Converter = EngineInterfaceRegistry.Create<IMeshConverter>(m_Mounter.GetEngineType());

                switch (p_Type)
                {
                    case MeshConverterType.Gltf:
                        s_Converter.ConvertToGltf(s_Variant, m_Mounter, p_Destination.FullName);
                        break;
                    case MeshConverterType.Glb:
                        s_Converter.ConvertToGlb(s_Variant, m_Mounter, p_Destination.FullName);
                        break;
                    case MeshConverterType.Obj:
                        s_Converter.ConvertToObj(s_Variant, m_Mounter, p_Destination.FullName);
                        break;
                    case MeshConverterType.BlenderScript:
                    default:
                        throw new NotImplementedException($"Unknown mesh converter type '{p_Type}'.");
                }
            }
        }

        internal void DumpResourceWithChunks(string p_Name, DirectoryInfo p_Destination)
        {
            // Get all resources
            if (!m_Mounter.TryGetResource(p_Name, out var s_Resource))
                throw new Exception($"Could not find resource with name '{p_Name}'.");

            var s_Variant = s_Resource.FirstVariant;

            // Ensure that the directory of the destination file exists
            if (!Directory.Exists(p_Destination.FullName))
                Directory.CreateDirectory(p_Destination.FullName);

            var s_ResourceName = new FileInfo(Path.Combine(p_Destination.FullName, $"{s_Resource.OriginalName}.{s_Variant.GetResourceType()}"));
            if (s_ResourceName.Directory != null && !Directory.Exists(s_ResourceName.Directory.FullName))
                Directory.CreateDirectory(s_ResourceName.Directory.FullName);

            switch (s_Variant.GetResourceType())
            {
                 case ResourceType.MeshSet:
                    if (!EngineInterfaceRegistry.IsSupported<IMeshConverter>(m_Mounter.GetEngineType()))
                    {
                        throw new NotImplementedException($"Dumping mesh resources with chunks is not yet implemented for engine type '{m_Mounter.GetEngineType()}'.");
                    }

                    var s_MeshConverter = EngineInterfaceRegistry.Create<IMeshConverter>(m_Mounter.GetEngineType());
                    Dictionary<string, GUID> s_MeshChunks = s_MeshConverter.GetChunkGuids(s_Variant);

                    foreach (var s_MeshChunk in s_MeshChunks)
                        DumpChunk(s_MeshChunk.Value, new FileInfo(Path.Combine(p_Destination.FullName, s_MeshChunk.Key + ".chunk")));

                    Console.WriteLine("Mesh Resource Name: " + s_Resource.OriginalName);
                    Console.WriteLine("Chunks:");
                    foreach (var s_MeshChunk in s_MeshChunks)
                        Console.WriteLine($" - {s_MeshChunk.Key}: {s_MeshChunk.Value}");

                    break;
                case ResourceType.DxTexture:
                case ResourceType.Ps3Texture:
                case ResourceType.ITexture:
                    if (!EngineInterfaceRegistry.IsSupported<ITextureConverter>(m_Mounter.GetEngineType()))
                    {
                        throw new NotImplementedException($"Dumping texture resources with chunks is not yet implemented for engine type '{m_Mounter.GetEngineType()}'.");
                    }

                    var s_TextureConverter = EngineInterfaceRegistry.Create<ITextureConverter>(m_Mounter.GetEngineType());
                    var s_TextureChunk = s_TextureConverter.GetTextureChunkId(s_Variant);
                    DumpChunk(s_TextureChunk, new FileInfo(Path.Combine(p_Destination.FullName, s_Resource.OriginalName + ".chunk")));

                    Console.WriteLine("Texture Resource Name: " + s_Resource.OriginalName);
                    Console.WriteLine("Chunk: " + s_TextureChunk);

                    break;
                default:
                    throw new NotImplementedException($"Dumping resources with chunks is not yet implemented for resource type '{s_Variant.GetResourceType()}'.");
            }

            using var s_Reader = s_Variant.GetReader();
            using var s_FileStream = File.Create(s_ResourceName.FullName);
            s_Reader.CopyTo(s_FileStream);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="p_Format"></param>
        /// <param name="p_LevelPartition"></param>
        /// <param name="p_OutputDestination"></param>
        /// <param name="p_InputTransforms"></param>
        /// <param name="p_Writer"></param>
        /// <exception cref="NotImplementedException"></exception>
        internal void DumpLevelMesh(MeshConverterType p_Format, string p_LevelPartition, FileInfo p_OutputDestination, FileInfo? p_InputTransforms, TextWriter p_Writer)
        {
            var s_Toolkit = EngineInterfaceRegistry.Create<IToolKit>(m_Mounter.GetEngineType());

            if (!s_Toolkit.Initialize(m_Mounter, p_Writer))
            {
                p_Writer.WriteLine($"Failed to initialize toolkit.");
                return;
            }

            if (!s_Toolkit.ConvertLevelMesh(p_LevelPartition, out var s_SceneBuilder))
            {
                p_Writer.WriteLine($"Failed to convert level mesh.");
                return;
            }

            var s_Model = s_SceneBuilder!.ToGltf2();

            switch (p_Format)
            {
                case MeshConverterType.Gltf:
                    s_Model.SaveGLTF(p_OutputDestination.FullName, new WriteSettings() { JsonIndented = true });
                    break;
                case MeshConverterType.Glb:
                    s_Model.SaveGLB(p_OutputDestination.FullName, new WriteSettings { JsonIndented = true });
                    break;
                case MeshConverterType.Obj:
                    s_Model.SaveAsWavefront(p_OutputDestination.FullName);
                    break;
                default:
                    p_Writer.WriteLine($"Failed to convert level mesh, unsupported format '{p_Format}'.");
                    return;
            }

            p_Writer.WriteLine($"Level converted to {p_Format} at {p_OutputDestination.FullName}.");
        }
    }
}
