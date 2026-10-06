using System;
using System.IO;
using System.Linq;
using System.Text;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Content.Frostbite;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Mesh.Frostbite;

namespace RimeLib.Cmd.Commands.Game;

[CommandDescription("Writes a MeshSet resource's HEADER under a NEW NAME (same length as the old one): the name string, the " +
                    "header's NameHash and every LOD's NameHash are rewritten in place, the rest of the bytes (layouts, " +
                    "subsets, bone tables) stay as the game ships them and the geometry chunks stay referenced. Pair the " +
                    "file with replace_resource_as <orig> <new> and a MeshAsset clone carrying the new Name/NameHash: a " +
                    "SECOND IDENTITY of a mesh the game already has (the variation database keys by name hash).")]
public class MeshRenameCommand : Command
{
    [CommandArgument(Description = "The MeshSet resource to rename, e.g. weapons/accessories/acog/acog_scope_3p_mesh")]
    public string? Path { get; set; }

    [CommandArgument(Description = "The new name, EXACTLY as long as the old one (the header stores the string in place).")]
    public string? NewName { get; set; }

    [CommandArgument(Description = "Output file for the renamed resource bytes.")]
    public FileInfo? Output { get; set; }

    public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
    {
        if (string.IsNullOrWhiteSpace(Path) || string.IsNullOrWhiteSpace(NewName) || Output == null)
        {
            p_Writer.WriteLine("Usage: mesh_rename <meshset-resource> <new-name-same-length> <out.bin>");
            return false;
        }

        var s_Mounter = ((GameContext) p_Context).GetMounter();
        if (!s_Mounter.TryGetResource(Path!, out var s_Resource) || s_Resource.FirstVariant == null)
        {
            p_Writer.WriteLine($"Could not find resource ({Path}).");
            return false;
        }

        if (s_Resource.FirstVariant.GetResourceType() != ResourceType.MeshSet)
        {
            p_Writer.WriteLine($"Resource '{Path}' is not a MeshSet (it is {s_Resource.FirstVariant.GetResourceType()}).");
            return false;
        }

        byte[] s_Bytes;
        using (var s_Reader = s_Resource.FirstVariant.GetReader())
            s_Bytes = s_Reader.ReadBytes((int) s_Reader.Length);

        if (!Rename(s_Bytes, NewName!, p_Writer, out var s_NewHash))
            return false;

        if (Output.Directory != null && !Output.Directory.Exists)
            Output.Directory.Create();
        File.WriteAllBytes(Output.FullName, s_Bytes);

        p_Writer.WriteLine($"Renamed MeshSet -> '{NewName!.ToLowerInvariant()}' (0x{s_NewHash:X8}): " +
                           $"{s_Bytes.Length} bytes -> {Output.FullName}");
        p_Writer.WriteLine($"MESH-RENAME: name={NewName!.ToLowerInvariant()} hash={s_NewHash}");
        return true;
    }

    /// <summary>
    /// Renames a MeshSet resource's header IN PLACE (the caller owns the bytes): the name string, the header's
    /// NameHash and every LOD's hash("&lt;name&gt;_lod&lt;N&gt;"). Everything else — layouts, subsets, bone tables, the
    /// chunk references — stays exactly as the game ships it. False with the reason written to the log.
    /// Shared with <c>accessory_camo_clone</c>, which does this without a file in between.
    /// </summary>
    public static bool Rename(byte[] p_Bytes, string p_NewName, TextWriter p_Writer, out uint p_NewHash)
    {
        p_NewHash = 0;
        var s_Bytes = p_Bytes;
        var NewName = p_NewName;

        var s_Layout = new MeshSetLayout(new RimeReader(new MemoryStream(s_Bytes)));
        var s_OldName = s_Layout.Name.Object ?? "";
        var s_OldHash = s_Layout.NameHash;
        var s_NewLower = NewName!.ToLowerInvariant();

        if (s_OldName.Length != s_NewLower.Length)
        {
            p_Writer.WriteLine($"The new name must be {s_OldName.Length} characters long like '{s_OldName}' (got {s_NewLower.Length}).");
            return false;
        }

        // The name string lives at the header's relocated pointer; the resource stores it lowercase and the
        // engine hashes it lowercase (HashQuickLowerCase), the same way texture resources are keyed.
        var s_NameAt = (int) s_Layout.Name.BaseAddress;
        var s_OldAscii = Encoding.ASCII.GetBytes(s_OldName);
        if (s_NameAt <= 0 || s_NameAt + s_OldAscii.Length > s_Bytes.Length ||
            !s_Bytes.AsSpan(s_NameAt, s_OldAscii.Length).SequenceEqual(s_OldAscii))
        {
            p_Writer.WriteLine($"The header's name pointer (0x{s_NameAt:X}) does not point at '{s_OldName}'.");
            return false;
        }

        Encoding.ASCII.GetBytes(s_NewLower).CopyTo(s_Bytes, s_NameAt);

        // The hashes: the header's NameHash = hash(name) and, per LOD, hash("<name>_lod<N>") (measured on the
        // ACOG: 0x55CC67F0..F4 = fnv of "…acog_scope_3p_mesh_lod0".."_lod4"). Patched at the parsed offsets,
        // not by a blind scan, so an unrelated u32 that happens to equal a hash stays.
        var s_NewHash = Frostbite.Utils.HashQuickLowerCase(s_NewLower);
        var s_Patched = 0;

        bool PatchHashAt(int p_Offset, uint p_Old, uint p_New)
        {
            if (p_Offset < 0 || p_Offset + 4 > s_Bytes.Length || BitConverter.ToUInt32(s_Bytes, p_Offset) != p_Old)
                return false;
            BitConverter.GetBytes(p_New).CopyTo(s_Bytes, p_Offset);
            s_Patched++;
            return true;
        }

        // The header's NameHash offset comes from the header's own writer (pointers are 8 bytes wide): it is the
        // u32 right after the ShortName pointer = header length − 8.
        int s_HeaderLen;
        using (var s_HdrMs = new MemoryStream())
        {
            using (var s_HdrW = new RimeWriter(s_HdrMs, Endianness.LittleEndian, false))
                s_Layout.Serialize(s_HdrW);
            s_HeaderLen = (int) s_HdrMs.Length;
        }

        if (!PatchHashAt(s_HeaderLen - 8, s_OldHash, s_NewHash))
        {
            p_Writer.WriteLine($"The header's NameHash was not found at offset {s_HeaderLen - 8} (expected 0x{s_OldHash:X8}).");
            return false;
        }

        var s_LodsPatched = 0;
        for (var i = 0; i < 5; ++i)
        {
            if (s_Layout.Lods[i].Object == null)
                continue;

            var s_Lod = s_Layout.Lods[i].Object!;
            var s_OldLodHash = Frostbite.Utils.HashQuickLowerCase($"{s_OldName}_lod{i}");
            var s_NewLodHash = Frostbite.Utils.HashQuickLowerCase($"{s_NewLower}_lod{i}");
            if (s_Lod.NameHash != s_OldLodHash)
            {
                p_Writer.WriteLine($"LOD {i} NameHash 0x{s_Lod.NameHash:X8} is not hash('{s_OldName}_lod{i}') = 0x{s_OldLodHash:X8}: unknown LOD naming, refusing.");
                return false;
            }

            // Found by re-serializing the LOD and locating the field within it, so the layout's writer decides
            // where it is.
            using var s_Ms = new MemoryStream();
            using (var s_W = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
                s_Lod.Serialize(s_W);
            var s_LodBytes = s_Ms.ToArray();
            var s_Base = (int) s_Layout.Lods[i].BaseAddress;
            var s_Hit = false;

            for (var s_Off = 0; s_Off + 4 <= s_LodBytes.Length; s_Off += 4)
                if (BitConverter.ToUInt32(s_LodBytes, s_Off) == s_OldLodHash && PatchHashAt(s_Base + s_Off, s_OldLodHash, s_NewLodHash))
                    s_Hit = true;

            if (!s_Hit)
            {
                p_Writer.WriteLine($"LOD {i}: its NameHash field was not found in the resource bytes.");
                return false;
            }

            s_LodsPatched++;
        }

        // Read back: the renamed header must parse with the new name and the new hashes.
        var s_Check = new MeshSetLayout(new RimeReader(new MemoryStream(s_Bytes)));
        var s_LodHashes = Enumerable.Range(0, 5).Where(p_I => s_Check.Lods[p_I].Object != null)
            .Select(p_I => (Index: p_I, Hash: s_Check.Lods[p_I].Object!.NameHash)).ToList();
        var s_LodsOk = s_LodHashes.All(p_L => p_L.Hash == Frostbite.Utils.HashQuickLowerCase($"{s_NewLower}_lod{p_L.Index}"));
        if (s_Check.Name.Object != s_NewLower || s_Check.NameHash != s_NewHash || !s_LodsOk)
        {
            p_Writer.WriteLine($"RENAME FAILED on read-back: Name='{s_Check.Name.Object}' NameHash=0x{s_Check.NameHash:X8} " +
                               $"LOD hashes={string.Join(",", s_LodHashes.Select(p_L => $"0x{p_L.Hash:X8}"))} (wanted 0x{s_NewHash:X8} + hash(name_lodN)).");
            return false;
        }

        p_Writer.WriteLine($"Renamed MeshSet '{s_OldName}' (0x{s_OldHash:X8}) -> '{s_NewLower}' (0x{s_NewHash:X8}): " +
                           $"{s_Patched} hash field(s) patched (header + {s_LodsPatched} LOD(s) as hash(name_lodN)), ShortName '{s_Check.ShortName.Object}' kept.");
        p_NewHash = s_NewHash;
        return true;
    }
}
