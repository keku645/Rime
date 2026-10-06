using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Content.Frostbite;
using RimeLib.Content.Mounting;
using RimeLib.Frostbite.Core;
using RimeLib.IO;
using RimeLib.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    /// <summary>
    /// The SECOND IDENTITY of an accessory mesh, in one command: what the 26 boots of the accessory-camo probe
    /// arrived at (see the phase-E recipe), minus the Python round-trip that used to carry it — a partition
    /// dump, a hand-written guid map file, a renamed header written to disk and three more commands, each of
    /// which had to agree with the others about names, order and guids.
    /// </summary>
    [CommandDescription("Gives an accessory MESH a second identity under a new name, ready for a camo of its own: the MeshSet " +
                        "header is renamed in place (name + NameHash + hash(name_lodN)) and published as a resource under the " +
                        "new name, and the mesh's EBX partition is cloned with a FRESH partition guid and its instance guids " +
                        "KEPT (so a variation entry can point at the same materials) with its Name and NameHash rewritten. " +
                        "Prints 'ACCCLONE:' with the vanilla and clone partition guids — what mvdb_add_entry needs for the " +
                        "(clone, 0) entry every engine socket reads. The new name MUST be as long as the old one (both the " +
                        "header and the EBX store the string in place). Chunks are untouched: resolve_missing_chunks binds them.")]
    internal class AccessoryCamoCloneCommand : Command
    {
        [CommandArgument(Description = "The vanilla mesh resource, e.g. weapons/accessories/acog/acog_scope_3p_mesh")]
        public string? Mesh { get; set; }

        [CommandArgument(Description = "Id returned by mount_game.")]
        public int Id { get; set; }

        [CommandArgument(Description = "The new name (EXACTLY as long as the mesh's own), e.g. WeaponCamoFramework/acog_scope_3p_camo1_Mesh")]
        public string? NewName { get; set; }

        [CommandArgument(Description = "A folder for the renamed MeshSet bytes (the resource is served from that file until the build).")]
        public DirectoryInfo? WorkDir { get; set; }

        [CommandArgument(Description = "Optional: the clone's partition guid, so a builder can DERIVE it (and write the " +
                                       "variation entry that repoints to it) instead of reading it back. Omitted = a random " +
                                       "fresh guid, printed either way.", Optional = true)]
        public string? NewPartition { get; set; }

        [CommandArgument(Description = "Optional: 'clone' (default) renames and publishes the MeshSet header under the new name; " +
                                       "'vanilla' clones the EBX ONLY — its Name KEPT, so the engine binds the game's own MeshSet " +
                                       "(ResourceManager_bind by the asset's Name string) with the LOD data its level already " +
                                       "binds to it, and its NameHash rewritten to fnv(new name), the key the variation database " +
                                       "registers its entries by (the entry's Mesh → MeshAsset+0x18). For a mesh whose resident LOD " +
                                       "data lives only inside its levels (the expansion vehicles: a renamed header waits for data " +
                                       "no bundle binds to it and the client froze on the loading screen, run 149). No mesh byte ships.",
                          Optional = true)]
        public string? MeshSet { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Mesh) || string.IsNullOrWhiteSpace(NewName) || WorkDir == null)
            {
                p_Writer.WriteLine("Usage: accessory_camo_clone <mesh-resource> <mount-id> <new-name-same-length> <work-dir>");
                return false;
            }

            var s_BundleContext = (BundleBuildingContext) p_Context;
            var s_SbContext = (SbBuildingContext?) s_BundleContext.Parent;
            if (s_SbContext?.Parent is not BaseContext s_BaseContext)
            {
                p_Writer.WriteLine("Context is invalid (build_sb + build_bundle first).");
                return false;
            }

            if (!s_BaseContext.GetMounters().TryGetValue(Id, out var s_Mounter))
            {
                p_Writer.WriteLine($"Id ({Id}) is not valid, ensure you mounted a game first.");
                return false;
            }

            var s_Vanilla = string.Equals(MeshSet, "vanilla", StringComparison.OrdinalIgnoreCase);
            if (!s_Vanilla && !string.IsNullOrWhiteSpace(MeshSet) && !string.Equals(MeshSet, "clone", StringComparison.OrdinalIgnoreCase))
            {
                p_Writer.WriteLine($"MeshSet must be 'clone' or 'vanilla' (got '{MeshSet}').");
                return false;
            }

            // --- the MeshSet: renamed header, published under the new name (or the game's own, 'vanilla') ------
            if (!s_Mounter.TryGetResource(Mesh!, out var s_Resource) || s_Resource.FirstVariant == null)
            {
                p_Writer.WriteLine($"Could not find mesh resource ({Mesh}).");
                return false;
            }

            if (s_Resource.FirstVariant.GetResourceType() != ResourceType.MeshSet)
            {
                p_Writer.WriteLine($"Resource '{Mesh}' is not a MeshSet (it is {s_Resource.FirstVariant.GetResourceType()}).");
                return false;
            }

            var s_MeshBytes = Array.Empty<byte>();
            var s_NewMeshHash = 0u;
            if (!s_Vanilla)
            {
                using (var s_Reader = s_Resource.FirstVariant.GetReader())
                    s_MeshBytes = s_Reader.ReadBytes((int) s_Reader.Length);

                if (!Game.MeshRenameCommand.Rename(s_MeshBytes, NewName!, p_Writer, out s_NewMeshHash))
                    return false;
            }

            // --- the EBX partition: fresh PARTITION guid, instance guids KEPT ------------------------------
            // ⛔ The instances keep their guids on purpose: the variation entry the bake writes points at the
            // mesh's own MeshMaterial instances, and a fresh guid there would leave it pointing at nothing.
            if (!s_Mounter.TryGetPartition(Mesh!, out var s_Mounted))
            {
                p_Writer.WriteLine($"Could not find the mesh's EBX partition ({Mesh}).");
                return false;
            }

            var s_Variant = s_Mounted.Variants.FirstOrDefault(p_V => p_V.GetContainedBundle() != null) ?? s_Mounted.FirstVariant;
            if (s_Variant == null)
            {
                p_Writer.WriteLine($"Could not find a valid variant of the partition ({Mesh}).");
                return false;
            }

            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(s_SbContext.EngineType);
            var s_Db = s_Converter.FromPartitionObject(Mesh!, s_Variant);

            // The name the EBX stores (mixed case, its own Name field) — the string to rewrite, and what
            // decides the length the new name has to match. Read, never guessed from the resource name.
            var s_MeshAsset = s_Db.Instances.OfType<fb.MeshAsset>().FirstOrDefault();
            var s_OldName = s_MeshAsset?.Name;
            if (string.IsNullOrWhiteSpace(s_OldName))
            {
                p_Writer.WriteLine($"The partition '{Mesh}' carries no MeshAsset with a Name.");
                return false;
            }

            // (the string is rewritten in place only when the header is renamed too: 'vanilla' keeps it)
            if (!s_Vanilla && s_OldName!.Length != NewName!.Length)
            {
                p_Writer.WriteLine($"The new name must be {s_OldName.Length} characters long like '{s_OldName}' (got {NewName!.Length}).");
                return false;
            }

            byte[] s_EbxBytes;
            using (var s_Reader = s_Variant.GetReader())
                s_EbxBytes = s_Reader.ReadBytes((int) s_Reader.Length);

            var s_OldPartition = s_Db.PartitionGuid;
            var s_NewPartition = string.IsNullOrWhiteSpace(NewPartition)
                ? new GUID(Guid.NewGuid())
                : new GUID(NewPartition!.Trim());
            var s_Remapped = ReplaceAll(s_EbxBytes, s_OldPartition.Id, s_NewPartition.Id);

            // ⛔ 'vanilla': the Name string is what the engine binds the MeshSet by — kept, so the game's own resource answers
            var s_StringHits = s_Vanilla ? 0 : ReplaceAll(s_EbxBytes, Encoding.ASCII.GetBytes(s_OldName!), Encoding.ASCII.GetBytes(NewName!));
            if (!s_Vanilla && s_StringHits == 0)
            {
                p_Writer.WriteLine($"WARNING: the name '{s_OldName}' was not found in the partition bytes (nothing rewritten).");
                return false;
            }

            // The asset's NameHash rides beside the string (fnv of the lowercased name): the variation database keys its
            // entries by it (MeshAsset+0x18), so a clone that kept it would answer for the vanilla. (The MeshSet itself is
            // bound by the Name STRING — ResourceManager_bind(compartment, type, const char*), IDA 2026-09-27 — which is
            // what 'vanilla' relies on.)
            var s_OldHash = RimeLib.Frostbite.Utils.HashQuickLowerCase(s_OldName);
            var s_NewHash = RimeLib.Frostbite.Utils.HashQuickLowerCase(NewName!);
            var s_HashHits = 0;
            for (var i = 0; i + 4 <= s_EbxBytes.Length; i += 4)
            {
                if (BitConverter.ToUInt32(s_EbxBytes, i) != s_OldHash)
                    continue;

                BitConverter.GetBytes(s_NewHash).CopyTo(s_EbxBytes, i);
                s_HashHits++;
            }

            if (s_HashHits == 0)
            {
                p_Writer.WriteLine($"WARNING: the asset's NameHash (0x{s_OldHash:X8}) was not found — the clone would answer as the vanilla.");
                return false;
            }

            if (!s_Vanilla && s_NewHash != s_NewMeshHash)
            {
                p_Writer.WriteLine($"The EBX name hash (0x{s_NewHash:X8}) and the MeshSet's (0x{s_NewMeshHash:X8}) differ — " +
                                   "the variation database keys by that hash, so they must be the same name.");
                return false;
            }

            // --- publish both (the EBX alone, 'vanilla') ---------------------------------------------------
            var s_File = "(the game's own MeshSet: none shipped)";
            if (!s_Vanilla)
            {
                WorkDir!.Create();
                s_File = Path.Combine(WorkDir.FullName, Sanitize(NewName!) + ".meshset.bin");
                File.WriteAllBytes(s_File, s_MeshBytes);
            }

            s_BundleContext.AddRawPartitionBytes(NewName!, s_EbxBytes);
            if (!s_Vanilla)
                s_BundleContext.AddResource(NewName!.ToLowerInvariant(),
                    new RenamedMeshResource(s_Resource.FirstVariant, s_File, NewName!.ToLowerInvariant()));

            var s_Materials = s_Db.Instances.OfType<fb.MeshMaterial>().Count();
            p_Writer.WriteLine($"Accessory clone '{s_OldName}' -> '{NewName}'{(s_Vanilla ? " (EBX only, Name kept: binds the game's MeshSet)" : "")}: " +
                               $"partition {s_Remapped} ref(s) remapped, {s_StringHits} name occurrence(s), {s_HashHits} hash field(s), " +
                               $"{s_Materials} material(s), {s_MeshBytes.Length} mesh byte(s) -> {s_File}");
            p_Writer.WriteLine($"ACCCLONE: mesh={Mesh} new={NewName!.ToLowerInvariant()} hash={s_NewHash} " +
                               $"oldpartition={s_OldPartition} newpartition={s_NewPartition} materials={s_Materials} " +
                               $"meshset={(s_Vanilla ? "vanilla" : "clone")}");
            return true;
        }

        private static string Sanitize(string p_Text) =>
            new(p_Text.Select(p_C => char.IsLetterOrDigit(p_C) ? p_C : '_').ToArray());

        private static int ReplaceAll(byte[] p_Data, byte[] p_Find, byte[] p_Replacement)
        {
            if (p_Find.Length != p_Replacement.Length || p_Find.Length == 0)
                return 0;

            var s_Count = 0;
            for (var i = 0; i <= p_Data.Length - p_Find.Length; ++i)
            {
                var s_Match = true;
                for (var j = 0; j < p_Find.Length; ++j)
                    if (p_Data[i + j] != p_Find[j])
                    {
                        s_Match = false;
                        break;
                    }

                if (!s_Match)
                    continue;

                Array.Copy(p_Replacement, 0, p_Data, i, p_Replacement.Length);
                s_Count++;
                i += p_Find.Length - 1;
            }

            return s_Count;
        }

        /// <summary>The vanilla MeshSet's type and meta, serving the renamed bytes under the new name.</summary>
        private sealed class RenamedMeshResource : IResourceObject
        {
            private readonly IResourceVariant m_Original;
            private readonly string m_Path;
            private readonly string m_Name;

            public RenamedMeshResource(IResourceVariant p_Original, string p_Path, string p_Name)
            {
                m_Original = p_Original;
                m_Path = p_Path;
                m_Name = p_Name;
            }

            public ResourceType GetResourceType() => m_Original.GetResourceType();

            public bool TryGetMeta([NotNullWhen(true)] out byte[]? p_Meta) => m_Original.TryGetMeta(out p_Meta);

            public ResourceRef GetId(string? p_Name = null)
            {
                try
                {
                    return m_Original.GetId(p_Name ?? m_Name);
                }
                catch
                {
                    return new ResourceRef(p_Name ?? m_Name, this);
                }
            }

            public RimeReader GetReader() =>
                new(File.Open(m_Path, FileMode.Open, FileAccess.Read, FileShare.Read));

            public long GetSize() => new FileInfo(m_Path).Length;
        }
    }
}
