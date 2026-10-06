using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Content.Frostbite;
using RimeLib.Content.Mounting;
using RimeLib.Frostbite.Core;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Mesh.Frostbite;
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
    /// A PIECE of a vehicle body for a camo (keku 2026-09-24, option B): the given parts of the vehicle's COMPOSITE body cut out as a
    /// RIGID mesh of its own, published under a new name with its own geometry chunk and an EBX clone of the body's partition.
    ///
    /// Why rigid: a composite mesh drawn outside its own vehicle crashes (the draw copies per-part matrices only the owning vehicle
    /// fills). Why the geometry is NOT moved: a part's rest transform in the body is exactly the transform of the component that moves
    /// it in the blueprint (the M1A2's turret part 59 sits at (0, 1.5656, 0.0909) = its turret component), so the piece hangs from
    /// that component at identity with the vertices as the game stores them -- the way an accessory clone hangs from the game's own
    /// socket. Parts of one piece are expressed in the space of the FIRST part named.
    ///
    /// The format, measured on the game's own rigid M1A2 (vehicles/m1a2/m1a2_static_mesh) and checked against the body:
    ///  - the rigid declaration is the composite one WITHOUT BoneIndices (UByte4) and with every later element 4 bytes earlier
    ///    (TexCoord1 @32 -> @28, stride 48 -> 32): a vertex is copied as is, minus those four bytes;
    ///  - one LOD (flags StreamingEnable), subsets per material in their own category, plus a depth-only subset (Pos Half4, w = 1,
    ///    stride 16, welded by position, empty material name) in category 3 covering the opaque triangles;
    ///  - chunk = [vertices of every subset][depth-only vertices][indices of every subset][depth-only indices], u16 indices relative
    ///    to each subset's own vertex window;
    ///  - resource = [header 0x70][LOD 0xA0][subsets 148 B, 16-aligned][strings][category bytes][relocation table of u32 offsets];
    ///    meta = {bytes before the table, 0, bytes of the table, 0x70, 0x94}.
    /// The EBX clone keeps every instance guid (so a variation entry points at the body's own materials), takes a fresh partition
    /// guid, the new name and hash, and its asset TYPE becomes RigidMeshAsset (same fields as CompositeMeshAsset; the type is named
    /// by its hash in the type table and its string in the keyword table).
    /// </summary>
    [CommandDescription("Cuts the given parts of a vehicle's COMPOSITE body mesh out as a RIGID mesh of their own (the camo piece of " +
                        "option B): new MeshSet resource (one LOD, rigid declaration, depth-only subset), its own geometry chunk in this " +
                        "bundle (h32 = the new resource name) and an EBX clone of the body's partition (fresh partition guid, instance " +
                        "guids KEPT, new name/hash, asset type RigidMeshAsset). The new name MUST be as long as the body's EBX name. " +
                        "Prints 'PIECECLONE:' — what mvdb_add_entry needs for the (piece, 0) entry.")]
    internal class VehiclePartCloneCommand : Command
    {
        [CommandArgument(Description = "The vanilla body mesh resource, e.g. vehicles/m1a2/m1abrams_mesh")]
        public string? Mesh { get; set; }

        [CommandArgument(Description = "Id returned by mount_game.")]
        public int Id { get; set; }

        [CommandArgument(Description = "The piece's name (EXACTLY as long as the body's EBX name), e.g. C/BERKUT_TANK/m1abrams_p059")]
        public string? NewName { get; set; }

        [CommandArgument(Description = "The body parts the piece is made of, comma separated (e.g. 59); the first names the piece's space.")]
        public string? Parts { get; set; }

        [CommandArgument(Description = "A folder for the piece's MeshSet and chunk bytes (served from those files until the build).")]
        public DirectoryInfo? WorkDir { get; set; }

        [CommandArgument(Description = "The piece partition's guid (derived by the builder, so the variation entry can point at it).")]
        public string? NewPartition { get; set; }

        [CommandArgument(Description = "The guid of the piece's geometry chunk (last byte EVEN: stored raw).")]
        public string? ChunkGuid { get; set; }

        [CommandArgument(Description = "Optional: only these material indices of the body, comma separated (e.g. 0); empty = every " +
                                       "material the parts use.", Optional = true)]
        public string? Materials { get; set; }

        [CommandArgument(Description = "Optional: material indices whose sections get their two uv sets exchanged (TexCoord0 <-> " +
                                       "TexCoord1), comma separated, \"-\" for none: a section drawn with a preset whose vertex stage " +
                                       "reads the pair the other way round.", Optional = true)]
        public string? SwapUv { get; set; }

        [CommandArgument(Description = "Optional: \"<cabin-mesh>:<interior materials>:<reference materials>:<body materials>\" (ids \"|\"-" +
                                       "separated), \"-\" for none — a helicopter's first-person cockpit mesh whose detailed cabin rides " +
                                       "in the piece too: the body's own simple cabin is culled from those body materials (its loose " +
                                       "pieces that sit on the detailed cabin or inside it and are not in the cockpit's own exterior, and " +
                                       "the hull's faces that look into the cabin from right on it).", Optional = true)]
        public string? Cull { get; set; }

        private const int HeaderSize = 0x70;
        private const int LodSize = 0xA0;
        private const int SubsetSize = 148;
        private const int DeclSize = 76;

        private sealed class Piece
        {
            public int Category;
            public uint MaterialIndex;
            public string MaterialName = "";
            public float[] Ratios = new float[6];
            public byte[] Declaration = new byte[DeclSize];
            public int Stride;
            public readonly List<byte[]> Vertices = new();
            public readonly List<ushort> Indices = new();
        }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Mesh) || string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(Parts) ||
                WorkDir == null || string.IsNullOrWhiteSpace(NewPartition) || string.IsNullOrWhiteSpace(ChunkGuid))
            {
                p_Writer.WriteLine("Usage: vehicle_part_clone <body-mesh> <mount-id> <new-name-same-length> <parts> <work-dir> " +
                                   "<partition-guid> <chunk-guid> [materials] [swap-uv] [cull]");
                return false;
            }

            var s_BundleContext = (BundleBuildingContext) p_Context;
            var s_SbContext = (SbBuildingContext?) s_BundleContext.Parent;
            if (s_SbContext?.Parent is not BaseContext s_BaseContext ||
                !s_BaseContext.GetMounters().TryGetValue(Id, out var s_Mounter))
            {
                p_Writer.WriteLine($"Context or mount id ({Id}) is invalid (mount_game, build_sb and build_bundle first).");
                return false;
            }

            var s_Wanted = Parts!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p_P => int.TryParse(p_P, out var s_N) ? s_N : -1).ToList();
            var s_OnlyMaterials = (Materials ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p_M => p_M != "-")
                .Select(p_M => uint.TryParse(p_M, out var s_N) ? s_N : uint.MaxValue).ToHashSet();
            var s_SwapUv = (SwapUv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p_M => p_M != "-")
                .Select(p_M => uint.TryParse(p_M, out var s_N) ? s_N : uint.MaxValue).ToHashSet();

            // --- the body: its MeshSet, its LOD0 and that LOD's geometry --------------------------------------------------------
            if (!s_Mounter.TryGetResource(Mesh!, out var s_Resource) || s_Resource.FirstVariant == null ||
                s_Resource.FirstVariant.GetResourceType() != ResourceType.MeshSet)
            {
                p_Writer.WriteLine($"'{Mesh}' is not a mounted MeshSet resource.");
                return false;
            }

            byte[] s_BodyBytes;
            using (var s_Reader = s_Resource.FirstVariant.GetReader())
                s_BodyBytes = s_Reader.ReadBytes((int) s_Reader.Length);

            var s_Body = new MeshSetLayout(new RimeReader(new MemoryStream(s_BodyBytes)));
            var s_Lod = s_Body.Lods[0].Object;
            if (s_Lod == null || s_Body.MeshType != fb.MeshType.MeshType_Composite || s_Lod.PartCount == 0)
            {
                p_Writer.WriteLine($"'{Mesh}' is not a composite mesh with parts (type {s_Body.MeshType}).");
                return false;
            }

            if (s_Wanted.Count == 0 || s_Wanted.Any(p_P => p_P < 0 || p_P >= s_Lod.PartCount))
            {
                p_Writer.WriteLine($"Parts '{Parts}' are not all within the body's {s_Lod.PartCount} parts.");
                return false;
            }

            var s_Transforms = ReadPartTransforms(s_BodyBytes, s_Lod);
            if (s_Transforms == null)
            {
                p_Writer.WriteLine("The body's part transforms could not be read.");
                return false;
            }

            if (!s_Mounter.TryGetChunk(s_Lod.DataChunkId, out IMountedObject<IChunkVariant> s_BodyChunk))
            {
                p_Writer.WriteLine($"The body's LOD0 chunk {s_Lod.DataChunkId} is not mounted.");
                return false;
            }

            byte[] s_Geometry;
            using (var s_Reader = s_BodyChunk.FirstVariant.GetReader())
                s_Geometry = s_Reader.ReadBytes((int) s_Reader.Length);

            // --- a cabin whose own detail replaces the body's simple one (Cull) ---------------------------------------------------
            CullField? s_Cull = null;
            var s_CullBody = new HashSet<uint>();
            if (!string.IsNullOrWhiteSpace(Cull) && Cull!.Trim() != "-")
            {
                s_Cull = CullField.Load(s_Mounter, Cull!, s_Wanted, s_CullBody, p_Writer);
                if (s_Cull == null)
                    return false;
            }

            // --- the triangles of the parts, material by material ---------------------------------------------------------------
            var s_Anchor = s_Transforms[s_Wanted[0]];
            var s_Pieces = new List<Piece>();
            var s_Subsets = s_Lod.Subsets.Get;

            for (var s_Category = 0; s_Category < 3; s_Category++)
            {
                foreach (var s_SubsetIndex in s_Lod.CategorySubsetIndices[s_Category].Get)
                {
                    var s_Subset = s_Subsets[s_SubsetIndex];
                    if (s_OnlyMaterials.Count > 0 && !s_OnlyMaterials.Contains(s_Subset.MaterialIndex))
                        continue;

                    var s_Swap = s_SwapUv.Contains(s_Subset.MaterialIndex);
                    var s_Drop = s_Cull != null && s_CullBody.Contains(s_Subset.MaterialIndex)
                        ? s_Cull.Drop(s_Subset, s_Geometry, s_Lod, s_Wanted, s_Transforms, p_Writer)
                        : null;
                    var s_Piece = CutSubset(s_Subset, s_Geometry, s_Lod, s_Wanted, s_Transforms, s_Anchor, s_Swap, p_Writer, s_Drop);
                    if (s_Piece == null)
                        continue;

                    s_Piece.Category = s_Category;
                    s_Pieces.Add(s_Piece);
                    p_Writer.WriteLine($"  section {s_SubsetIndex} (material #{s_Subset.MaterialIndex} {s_Subset.MaterialName.Object}, " +
                                       $"category {s_Category}): {s_Piece.Indices.Count / 3} of {s_Subset.PrimitiveCount} triangles, " +
                                       $"{s_Piece.Vertices.Count} vertices{(s_Swap ? ", uv sets exchanged" : "")}");
                }
            }

            if (s_Pieces.Count == 0)
            {
                p_Writer.WriteLine($"No triangle of the body uses parts {Parts}{(s_OnlyMaterials.Count > 0 ? $" with materials {Materials}" : "")}.");
                return false;
            }

            // --- the depth-only section: the opaque triangles, welded by position ---------------------------------------------
            var s_Depth = BuildDepthOnly(s_Pieces.Where(p_P => p_P.Category == 0).ToList());

            // --- chunk, header, EBX -------------------------------------------------------------------------------------------
            var s_ChunkGuid = new GUID(ChunkGuid!.Trim());
            var s_NewLower = NewName!.ToLowerInvariant();
            var s_Chunk = BuildChunk(s_Pieces, s_Depth, out var s_VertexOffsets, out var s_StartIndices, out var s_DepthVertexOffset,
                out var s_DepthStartIndex, out var s_VertexBytes, out var s_IndexBytes);
            var s_MeshSet = WriteRigidMeshSet(NewName!, s_Pieces, s_Depth, s_VertexOffsets, s_StartIndices, s_DepthVertexOffset,
                s_DepthStartIndex, s_VertexBytes, s_IndexBytes, s_ChunkGuid, out var s_RelocAt);
            var s_Meta = new byte[16];
            BitConverter.GetBytes((uint) s_RelocAt).CopyTo(s_Meta, 0);
            BitConverter.GetBytes((uint) (s_MeshSet.Length - s_RelocAt)).CopyTo(s_Meta, 8);
            BitConverter.GetBytes((ushort) HeaderSize).CopyTo(s_Meta, 12);
            BitConverter.GetBytes((ushort) SubsetSize).CopyTo(s_Meta, 14);

            // The piece read back the way the game's own layout is read: a writer that disagrees with the reader stops here.
            var s_Back = new MeshSetLayout(new RimeReader(new MemoryStream(s_MeshSet)));
            var s_BackLod = s_Back.Lods[0].Object;
            if (s_BackLod == null || s_BackLod.Subsets.Get.Length != s_Pieces.Count + 1 ||
                s_BackLod.VertexDataSize != s_VertexBytes || s_BackLod.IndexDataSize != s_IndexBytes ||
                s_Back.NameHash != RimeLib.Frostbite.Utils.HashQuickLowerCase(s_NewLower) ||
                s_BackLod.Subsets.Get[^1].GeometryDeclarationDesc.Elements.Count != 1)
            {
                p_Writer.WriteLine("The piece does not read back as it was written — nothing published.");
                return false;
            }

            if (!CloneEbx(s_Mounter, s_SbContext, Mesh!, NewName!, NewPartition!, p_Writer, out var s_Ebx, out var s_EbxHash))
                return false;

            // --- publish -------------------------------------------------------------------------------------------------------
            WorkDir!.Create();
            var s_Stem = Path.Combine(WorkDir.FullName, new string(s_NewLower.Select(p_C => char.IsLetterOrDigit(p_C) ? p_C : '_').ToArray()));
            File.WriteAllBytes(s_Stem + ".meshset.bin", s_MeshSet);
            File.WriteAllBytes(s_Stem + ".chunk.bin", s_Chunk);

            s_BundleContext.AddRawPartitionBytes(NewName!, s_Ebx);
            s_BundleContext.AddResource(s_NewLower, new PieceMeshResource(s_Resource.FirstVariant, s_Stem + ".meshset.bin", s_NewLower, s_Meta));
            s_BundleContext.AddChunk(s_ChunkGuid, new FileInfo(s_Stem + ".chunk.bin"), s_NewLower);

            var s_Triangles = s_Pieces.Sum(p_P => p_P.Indices.Count / 3);
            p_Writer.WriteLine($"Piece '{NewName}' from parts {Parts} of '{Mesh}': {s_Pieces.Count} section(s), {s_Triangles} triangles, " +
                               $"{s_Depth.Positions.Count} depth-only vertices; resource {s_MeshSet.Length} B (meta " +
                               $"{string.Concat(s_Meta.Select(p_B => p_B.ToString("x2")))}), chunk {s_Chunk.Length} B -> {s_Stem}.*");
            p_Writer.WriteLine($"PIECECLONE: mesh={Mesh} new={s_NewLower} hash={s_EbxHash} newpartition={NewPartition} " +
                               $"chunk={ChunkGuid} parts={Parts} sections={s_Pieces.Count} triangles={s_Triangles}");
            return true;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // The body
        // ---------------------------------------------------------------------------------------------------------------------

        /// <summary>A part's rest transform: right, up, forward, translation (float3 each, padded to 16 bytes).</summary>
        internal static float[][]? ReadPartTransforms(byte[] p_Bytes, MeshLayout p_Lod)
        {
            var s_At = (long) p_Lod.BoneShortNameArrayPartTransforms.BaseAddress;
            if (s_At <= 0 || s_At + p_Lod.PartCount * 64L > p_Bytes.Length)
                return null;

            var s_Out = new float[p_Lod.PartCount][];
            for (var i = 0; i < p_Lod.PartCount; i++)
            {
                s_Out[i] = new float[12];
                for (var r = 0; r < 4; r++)
                    for (var c = 0; c < 3; c++)
                        s_Out[i][r * 3 + c] = BitConverter.ToSingle(p_Bytes, (int) (s_At + i * 64 + r * 16 + c * 4));
            }

            return s_Out;
        }

        private static int SizeOf(fb.VertexElementFormat p_F) => p_F switch
        {
            fb.VertexElementFormat.VertexElementFormat_Half => 2,
            fb.VertexElementFormat.VertexElementFormat_Half2 => 4,
            fb.VertexElementFormat.VertexElementFormat_Half3 => 6,
            fb.VertexElementFormat.VertexElementFormat_Half4 => 8,
            fb.VertexElementFormat.VertexElementFormat_Float => 4,
            fb.VertexElementFormat.VertexElementFormat_Float2 => 8,
            fb.VertexElementFormat.VertexElementFormat_Float3 => 12,
            fb.VertexElementFormat.VertexElementFormat_Float4 => 16,
            _ => 4,
        };

        /// <summary>
        /// The rigid declaration of a composite one: every element but BoneIndices (and the vertex colour, below), those after them
        /// that many bytes earlier, the stride rounded to 16. A body with ONE uv set gets a second one at the end, a copy of the first
        /// (see SourceOf): the rigid solutions the levels compile for vehicle presets all read two (0xC83353E0), and none reads one
        /// alone (0x842E724C, the 2S25's, has no solution in any level). Null when the declaration is not one this cutter handles (no
        /// UByte4 BoneIndices, or several streams).
        /// </summary>
        internal static GeometryDeclarationDesc? RigidDeclarationOf(GeometryDeclarationDesc p_Decl, out int p_Stride)
        {
            p_Stride = 0;
            var s_Bones = p_Decl.GetByUsage(fb.VertexElementUsage.VertexElementUsage_BoneIndices);
            if (s_Bones == null || p_Decl.Streams.Count != 1 || s_Bones.Format != fb.VertexElementFormat.VertexElementFormat_UByte4)
                return null;

            // ⭐ …and the vertex COLOUR goes with the bones (2026-09-26): the DPV's body carries one (Color0 UByte4N @8, decl
            // 0x384BA267 — the only vehicle body of the 56 cached that does, with the F-35B's cockpit) and no level compiles a vehicle
            // preset for the rigid layout that keeps it (0x10405C4F: all its parts left out of every piece). The rigid solutions a piece
            // can be drawn with (0xC83353E0) read no colour at all, so without it the piece is drawn exactly as it would be with it,
            // and its layout is the ground family's. Every body without a colour element comes out as before.
            static bool Dropped(GeometryDeclarationDesc.Element p_E) =>
                p_E.Usage is fb.VertexElementUsage.VertexElementUsage_BoneIndices or fb.VertexElementUsage.VertexElementUsage_Color0;

            var s_Rigid = new GeometryDeclarationDesc();
            foreach (var s_E in p_Decl.Elements.Where(p_E => !Dropped(p_E)))
                s_Rigid.Elements.Add(new GeometryDeclarationDesc.Element
                {
                    Usage = s_E.Usage,
                    Format = s_E.Format,
                    // (earlier by the bytes of every dropped element before it: with the bones alone, 4 when it comes after them)
                    Offset = (byte) (s_E.Offset - p_Decl.Elements.Where(p_D => Dropped(p_D) && p_D.Offset < s_E.Offset).Sum(p_D => SizeOf(p_D.Format))),
                    StreamIndex = 0,
                });

            var s_Uv0 = s_Rigid.GetByUsage(fb.VertexElementUsage.VertexElementUsage_TexCoord0);
            if (s_Uv0 != null && s_Rigid.GetByUsage(fb.VertexElementUsage.VertexElementUsage_TexCoord1) == null)
                s_Rigid.Elements.Add(new GeometryDeclarationDesc.Element
                {
                    Usage = fb.VertexElementUsage.VertexElementUsage_TexCoord1,
                    Format = s_Uv0.Format,
                    Offset = (byte) s_Rigid.Elements.Max(p_E => p_E.Offset + SizeOf(p_E.Format)),
                    StreamIndex = 0,
                });

            // ⭐ rounded to 16, as the game's own declarations are (16/32/48/64): the aircraft's three uv sets end at 36 and the game's
            // rigid layout of the same elements is 0x1D397D6A with stride 48 — rounded to 4 it hashed to 0x1139356A, which no level
            // compiles (2026-09-25). The ground family ends at 32 either way (0xC83353E0).
            p_Stride = s_Rigid.Elements.Max(p_E => p_E.Offset + SizeOf(p_E.Format));
            p_Stride = (p_Stride + 15) & ~15;
            s_Rigid.Streams.Add(new GeometryDeclarationDesc.Stream { Stride = (byte) p_Stride });
            return s_Rigid;
        }

        private static bool IsSame(float[] p_A, float[] p_B)
        {
            for (var i = 0; i < 12; i++)
                if (System.Math.Abs(p_A[i] - p_B[i]) > 1e-6f)
                    return false;
            return true;
        }

        /// <summary>Where a rigid element's bytes come from in the composite vertex: the element of the same usage, and for a second
        /// uv set the body does not have, the first (see RigidDeclarationOf).</summary>
        private static GeometryDeclarationDesc.Element? SourceOf(GeometryDeclarationDesc p_Decl, GeometryDeclarationDesc.Element p_Rigid) =>
            p_Decl.GetByUsage(p_Rigid.Usage) ??
            (p_Rigid.Usage == fb.VertexElementUsage.VertexElementUsage_TexCoord1
                ? p_Decl.GetByUsage(fb.VertexElementUsage.VertexElementUsage_TexCoord0)
                : null);

        /// <summary>
        /// One section of the body cut down to the triangles whose three vertices ride the wanted parts, its vertices in the
        /// rigid declaration (BoneIndices dropped, later elements 4 bytes earlier) and in the space of the first part; with
        /// p_SwapUv, its two uv sets exchanged.
        /// </summary>
        private static Piece? CutSubset(MeshSubset p_Subset, byte[] p_Geometry, MeshLayout p_Lod, List<int> p_Parts,
            float[][] p_Transforms, float[] p_Anchor, bool p_SwapUv, TextWriter p_Writer, HashSet<int>? p_Drop = null)
        {
            var s_Decl = p_Subset.GeometryDeclarationDesc;
            var s_Bones = s_Decl.GetByUsage(fb.VertexElementUsage.VertexElementUsage_BoneIndices);
            var s_Rigid = RigidDeclarationOf(s_Decl, out var s_Stride);
            if (s_Bones == null || s_Rigid == null)
                return null;

            // every rigid element with its source: (source offset, rigid offset, size)
            var s_Copies = new List<(int From, int To, int Size)>();
            foreach (var s_E in s_Rigid.Elements)
            {
                var s_Source = SourceOf(s_Decl, s_E);
                if (s_Source == null || SizeOf(s_Source.Format) != SizeOf(s_E.Format))
                {
                    p_Writer.WriteLine($"  section of material #{p_Subset.MaterialIndex}: no source for its {s_E.Usage} — not cut.");
                    return null;
                }

                s_Copies.Add((s_Source.Offset, s_E.Offset, SizeOf(s_E.Format)));
            }

            var s_Uv0 = s_Rigid.GetByUsage(fb.VertexElementUsage.VertexElementUsage_TexCoord0);
            var s_Uv1 = s_Rigid.GetByUsage(fb.VertexElementUsage.VertexElementUsage_TexCoord1);
            if (p_SwapUv && (s_Uv0 == null || s_Uv1 == null || s_Uv0.Format != s_Uv1.Format))
            {
                p_Writer.WriteLine($"  section of material #{p_Subset.MaterialIndex}: its uv sets cannot be exchanged — not cut.");
                return null;
            }

            var s_DeclBytes = new byte[DeclSize];
            using (var s_Ms = new MemoryStream())
            {
                using (var s_W = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
                    s_Rigid.Serialize(s_W);
                Array.Copy(s_Ms.ToArray(), s_DeclBytes, DeclSize);
            }

            var s_Piece = new Piece
            {
                MaterialIndex = p_Subset.MaterialIndex,
                MaterialName = p_Subset.MaterialName.Object ?? "",
                Declaration = s_DeclBytes,
                Stride = s_Stride,
            };
            for (var i = 0; i < 6 && i < p_Subset.TexCoordRatios.Count; i++)
                s_Piece.Ratios[i] = p_Subset.TexCoordRatios[i];

            var s_SrcStride = p_Subset.VertexStride;
            var s_VertexBase = (int) p_Subset.VertexOffset;
            var s_IndexBase = (int) p_Lod.VertexDataSize;
            var s_Remap = new Dictionary<int, ushort>();

            byte PartOf(int p_V) => p_Geometry[s_VertexBase + p_V * s_SrcStride + s_Bones.Offset];

            for (var t = 0; t < p_Subset.PrimitiveCount; t++)
            {
                var s_Tri = new int[3];
                for (var k = 0; k < 3; k++)
                    s_Tri[k] = BitConverter.ToUInt16(p_Geometry, s_IndexBase + ((int) p_Subset.StartIndex + t * 3 + k) * 2);

                if (!s_Tri.All(p_V => p_Parts.Contains(PartOf(p_V))) || (p_Drop != null && p_Drop.Contains(t)))
                    continue;

                foreach (var s_V in s_Tri)
                {
                    if (!s_Remap.TryGetValue(s_V, out var s_New))
                    {
                        var s_Src = new byte[s_SrcStride];
                        Array.Copy(p_Geometry, s_VertexBase + s_V * s_SrcStride, s_Src, 0, s_SrcStride);
                        var s_Out = new byte[s_Stride];
                        foreach (var (s_From, s_To, s_Size) in s_Copies)
                            Array.Copy(s_Src, s_From, s_Out, s_To, s_Size);

                        if (p_SwapUv)
                        {
                            var s_UvSize = SizeOf(s_Uv0!.Format);
                            var s_Held = s_Out.AsSpan(s_Uv0.Offset, s_UvSize).ToArray();
                            Array.Copy(s_Out, s_Uv1!.Offset, s_Out, s_Uv0.Offset, s_UvSize);
                            s_Held.CopyTo(s_Out, s_Uv1.Offset);
                        }

                        var s_Part = p_Transforms[s_Src[s_Bones.Offset]];
                        if (!IsSame(s_Part, p_Anchor))
                            IntoAnchorSpace(s_Out, s_Rigid, s_Part, p_Anchor);

                        s_New = (ushort) s_Piece.Vertices.Count;
                        s_Piece.Vertices.Add(s_Out);
                        s_Remap[s_V] = s_New;
                    }

                    s_Piece.Indices.Add(s_New);
                }
            }

            if (s_Piece.Indices.Count == 0)
                return null;

            if (s_Piece.Vertices.Count > ushort.MaxValue)
            {
                p_Writer.WriteLine($"  a section of the piece has {s_Piece.Vertices.Count} vertices: more than 16-bit indices reach.");
                return null;
            }

            return s_Piece;
        }

        /// <summary>A vertex of another part moved into the first part's space: position by the whole transform, normal and
        /// tangent by the rotation (rest transforms are rigid).</summary>
        private static void IntoAnchorSpace(byte[] p_Vertex, GeometryDeclarationDesc p_Decl, float[] p_Part, float[] p_Anchor)
        {
            float[] ToWorld(float[] v, bool p_Point) => new[]
            {
                v[0] * p_Part[0] + v[1] * p_Part[3] + v[2] * p_Part[6] + (p_Point ? p_Part[9] : 0),
                v[0] * p_Part[1] + v[1] * p_Part[4] + v[2] * p_Part[7] + (p_Point ? p_Part[10] : 0),
                v[0] * p_Part[2] + v[1] * p_Part[5] + v[2] * p_Part[8] + (p_Point ? p_Part[11] : 0),
            };

            float[] ToAnchor(float[] w, bool p_Point)
            {
                var d = p_Point ? new[] { w[0] - p_Anchor[9], w[1] - p_Anchor[10], w[2] - p_Anchor[11] } : w;
                return new[]
                {
                    d[0] * p_Anchor[0] + d[1] * p_Anchor[1] + d[2] * p_Anchor[2],
                    d[0] * p_Anchor[3] + d[1] * p_Anchor[4] + d[2] * p_Anchor[5],
                    d[0] * p_Anchor[6] + d[1] * p_Anchor[7] + d[2] * p_Anchor[8],
                };
            }

            foreach (var s_E in p_Decl.Elements)
            {
                var s_Point = s_E.Usage == fb.VertexElementUsage.VertexElementUsage_Pos;
                var s_Direction = s_E.Usage is fb.VertexElementUsage.VertexElementUsage_Normal or fb.VertexElementUsage.VertexElementUsage_Tangent;
                if ((!s_Point && !s_Direction) ||
                    s_E.Format is not (fb.VertexElementFormat.VertexElementFormat_Half3 or fb.VertexElementFormat.VertexElementFormat_Half4))
                    continue;

                var s_V = new float[3];
                for (var c = 0; c < 3; c++)
                    s_V[c] = (float) BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(p_Vertex, s_E.Offset + c * 2));

                var s_R = ToAnchor(ToWorld(s_V, s_Point), s_Point);
                for (var c = 0; c < 3; c++)
                    BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half) s_R[c])).CopyTo(p_Vertex, s_E.Offset + c * 2);
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // The cabin cull
        // ---------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A helicopter's first-person cockpit mesh against the body it is cut next to (the AH-6, 2026-09-25): the cockpit carries the
        /// same parts as the body, its DETAILED cabin as a material of its own and a simple exterior around it; the body carries its
        /// own simple cabin inside the same material as its skin. With the detailed cabin in the piece, the body's simple one poked
        /// through it. What goes, checked by rendering both against the game's own first-person look:
        ///  · a loose piece of the body (a welded island that is not the largest) whose surface sits on the detailed cabin, or lies
        ///    inside the cabin's box, and is NOT part of the cockpit's own exterior (its seats, sticks, consoles, compass);
        ///  · a face of the largest island (the skin) that looks INTO the cabin from right on it (its floor, bulkheads, the inside of
        ///    the door frames) — every face that looks out stays, so the camo'd outside keeps every triangle.
        /// Distances in metres, measured on the surface (corners, centroid and edge midpoints of each triangle).
        /// </summary>
        private sealed class CullField
        {
            private const float Cell = 0.02f;
            private const float IslandReach = 0.06f;
            private const float SkinReach = 0.04f;
            private const float Weld = 0.005f;

            private readonly Dictionary<(int, int, int), List<System.Numerics.Vector3>> m_Cabin = new();
            private readonly Dictionary<(int, int, int), List<System.Numerics.Vector3>> m_Reference = new();
            private System.Numerics.Vector3 m_Low = new(float.MaxValue), m_High = new(float.MinValue);

            public static CullField? Load(IEngineMounter p_Mounter, string p_Spec, List<int> p_Parts, HashSet<uint> p_BodyMaterials,
                TextWriter p_Writer)
            {
                var s_Fields = p_Spec.Split(':');
                if (s_Fields.Length != 4)
                {
                    p_Writer.WriteLine($"Cull '{p_Spec}' is not <cabin-mesh>:<interior materials>:<reference materials>:<body materials>.");
                    return null;
                }

                static HashSet<uint> Ids(string p_Text) => p_Text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p_I => uint.TryParse(p_I, out var s_N) ? s_N : uint.MaxValue).ToHashSet();
                var s_Interior = Ids(s_Fields[1]);
                var s_Reference = Ids(s_Fields[2]);
                p_BodyMaterials.UnionWith(Ids(s_Fields[3]));

                if (!p_Mounter.TryGetResource(s_Fields[0], out var s_Resource) || s_Resource.FirstVariant == null ||
                    s_Resource.FirstVariant.GetResourceType() != ResourceType.MeshSet)
                {
                    p_Writer.WriteLine($"Cull: '{s_Fields[0]}' is not a mounted MeshSet resource.");
                    return null;
                }

                byte[] s_Bytes;
                using (var s_Reader = s_Resource.FirstVariant.GetReader())
                    s_Bytes = s_Reader.ReadBytes((int) s_Reader.Length);

                var s_Layout = new MeshSetLayout(new RimeReader(new MemoryStream(s_Bytes)));
                var s_Lod = s_Layout.Lods[0].Object;
                var s_Transforms = s_Lod == null ? null : ReadPartTransforms(s_Bytes, s_Lod);
                if (s_Lod == null || s_Transforms == null || !p_Mounter.TryGetChunk(s_Lod.DataChunkId, out IMountedObject<IChunkVariant> s_Chunk))
                {
                    p_Writer.WriteLine($"Cull: the LOD0 of '{s_Fields[0]}' (its parts or its geometry) could not be read.");
                    return null;
                }

                byte[] s_Geometry;
                using (var s_Reader = s_Chunk.FirstVariant.GetReader())
                    s_Geometry = s_Reader.ReadBytes((int) s_Reader.Length);

                var s_Field = new CullField();
                int s_CabinTriangles = 0, s_ReferenceTriangles = 0;
                // ⛔ the drawn sections only (categories 0-2, as the cutter reads them): the depth-only section covers every opaque
                // triangle and carries a material index of its own — counted as the "exterior" it made the whole cabin exterior
                foreach (var s_Subset in Enumerable.Range(0, 3).SelectMany(p_C => s_Lod.CategorySubsetIndices[p_C].Get).Select(p_I => s_Lod.Subsets.Get[p_I]))
                {
                    var s_IsCabin = s_Interior.Contains(s_Subset.MaterialIndex);
                    var s_IsReference = s_Reference.Contains(s_Subset.MaterialIndex);
                    if (!s_IsCabin && !s_IsReference)
                        continue;

                    // the cabin whole; the reference exterior only where the piece is (its parts)
                    foreach (var (s_Tri, _) in Triangles(s_Subset, s_Geometry, s_Lod, s_Transforms, s_IsCabin ? null : p_Parts))
                    {
                        foreach (var s_P in Samples(s_Tri))
                        {
                            Add(s_IsCabin ? s_Field.m_Cabin : s_Field.m_Reference, s_P);
                            if (s_IsCabin)
                            {
                                s_Field.m_Low = System.Numerics.Vector3.Min(s_Field.m_Low, s_P);
                                s_Field.m_High = System.Numerics.Vector3.Max(s_Field.m_High, s_P);
                            }
                        }

                        if (s_IsCabin)
                            s_CabinTriangles++;
                        else
                            s_ReferenceTriangles++;
                    }
                }

                if (s_CabinTriangles == 0 || s_ReferenceTriangles == 0)
                {
                    p_Writer.WriteLine($"Cull: '{s_Fields[0]}' has {s_CabinTriangles} cabin and {s_ReferenceTriangles} reference triangle(s) " +
                                       $"on parts {string.Join(",", p_Parts)} — nothing to cull against.");
                    return null;
                }

                p_Writer.WriteLine($"  cull: cabin of '{s_Fields[0]}' ({s_CabinTriangles} triangles, box {s_Field.m_Low} - {s_Field.m_High}), " +
                                   $"its exterior on the piece's parts {s_ReferenceTriangles} triangles");
                return s_Field;
            }

            /// <summary>The indices (within the subset) of the triangles of the piece's parts the cull takes out.</summary>
            public HashSet<int> Drop(MeshSubset p_Subset, byte[] p_Geometry, MeshLayout p_Lod, List<int> p_Parts, float[][] p_Transforms,
                TextWriter p_Writer)
            {
                var s_Triangles = Triangles(p_Subset, p_Geometry, p_Lod, p_Transforms, p_Parts).ToList();
                var s_Drop = new HashSet<int>();
                if (s_Triangles.Count == 0)
                    return s_Drop;

                // islands: triangles welded by position
                var s_Parent = new Dictionary<(int, int, int), (int, int, int)>();
                (int, int, int) Key(System.Numerics.Vector3 p_V) =>
                    ((int) MathF.Round(p_V.X / Weld), (int) MathF.Round(p_V.Y / Weld), (int) MathF.Round(p_V.Z / Weld));
                (int, int, int) Find((int, int, int) p_K)
                {
                    if (!s_Parent.ContainsKey(p_K))
                    {
                        s_Parent[p_K] = p_K;
                        return p_K;
                    }

                    while (true)
                    {
                        var s_Up = s_Parent[p_K];
                        if (s_Up == p_K)
                            return p_K;
                        s_Parent[p_K] = s_Parent[s_Up];
                        p_K = s_Up;
                    }
                }

                foreach (var (s_Tri, _) in s_Triangles)
                    for (var k = 1; k < 3; k++)
                    {
                        var s_A = Find(Key(s_Tri[0]));
                        var s_B = Find(Key(s_Tri[k]));
                        if (s_A != s_B)
                            s_Parent[s_A] = s_B;
                    }

                var s_Islands = s_Triangles.GroupBy(p_T => Find(Key(p_T.Tri[0]))).OrderByDescending(p_G => p_G.Count()).ToList();
                var s_Centre = (m_Low + m_High) / 2;
                int s_Loose = 0, s_Skin = 0;

                for (var i = 0; i < s_Islands.Count; i++)
                {
                    if (i == 0)
                    {
                        // the skin: only the faces that look into the cabin from right on it
                        foreach (var (s_Tri, s_At) in s_Islands[i])
                        {
                            var s_Normal = System.Numerics.Vector3.Cross(s_Tri[1] - s_Tri[0], s_Tri[2] - s_Tri[0]);
                            var s_Inward = System.Numerics.Vector3.Dot(s_Normal, s_Centre - (s_Tri[0] + s_Tri[1] + s_Tri[2]) / 3) > 0;
                            if (s_Inward && Share(Samples(s_Tri), m_Cabin, SkinReach) >= 0.5f)
                            {
                                s_Drop.Add(s_At);
                                s_Skin++;
                            }
                        }

                        continue;
                    }

                    var s_Points = s_Islands[i].SelectMany(p_T => Samples(p_T.Tri)).ToList();
                    var s_OnCabin = Share(s_Points, m_Cabin, IslandReach);
                    var s_OnExterior = Share(s_Points, m_Reference, IslandReach);
                    var s_Inside = s_Islands[i].All(p_T => p_T.Tri.All(p_V =>
                        p_V.X >= m_Low.X && p_V.Y >= m_Low.Y && p_V.Z >= m_Low.Z && p_V.X <= m_High.X && p_V.Y <= m_High.Y && p_V.Z <= m_High.Z));
                    if (s_OnExterior < 0.5f && (s_OnCabin >= 0.5f || s_Inside))
                        foreach (var (_, s_At) in s_Islands[i])
                        {
                            s_Drop.Add(s_At);
                            s_Loose++;
                        }
                }

                p_Writer.WriteLine($"  cull (material #{p_Subset.MaterialIndex}): {s_Drop.Count} of {s_Triangles.Count} triangles out — {s_Loose} of " +
                                   $"{s_Islands.Count - 1} loose pieces' and {s_Skin} of the skin's {s_Islands[0].Count()} (faces into the cabin)");
                return s_Drop;
            }

            /// <summary>A subset's triangles on the given parts (all parts when null), in the MESH's space (each vertex through its
            /// part's rest transform), with their index within the subset.</summary>
            private static IEnumerable<(System.Numerics.Vector3[] Tri, int At)> Triangles(MeshSubset p_Subset, byte[] p_Geometry, MeshLayout p_Lod,
                float[][] p_Transforms, List<int>? p_Parts)
            {
                var s_Decl = p_Subset.GeometryDeclarationDesc;
                var s_Bones = s_Decl.GetByUsage(fb.VertexElementUsage.VertexElementUsage_BoneIndices);
                var s_Pos = s_Decl.GetByUsage(fb.VertexElementUsage.VertexElementUsage_Pos);
                if (s_Bones == null || s_Pos == null)
                    yield break;

                var s_Stride = p_Subset.VertexStride;
                var s_VertexBase = (int) p_Subset.VertexOffset;
                var s_IndexBase = (int) p_Lod.VertexDataSize;
                var s_Half = s_Pos.Format is fb.VertexElementFormat.VertexElementFormat_Half3 or fb.VertexElementFormat.VertexElementFormat_Half4;

                System.Numerics.Vector3 At(int p_V, out int p_Part)
                {
                    var s_At = s_VertexBase + p_V * s_Stride;
                    p_Part = p_Geometry[s_At + s_Bones.Offset];
                    var c = new float[3];
                    for (var k = 0; k < 3; k++)
                        c[k] = s_Half
                            ? (float) BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(p_Geometry, s_At + s_Pos.Offset + k * 2))
                            : BitConverter.ToSingle(p_Geometry, s_At + s_Pos.Offset + k * 4);
                    var m = p_Transforms[p_Part];
                    return new System.Numerics.Vector3(
                        c[0] * m[0] + c[1] * m[3] + c[2] * m[6] + m[9],
                        c[0] * m[1] + c[1] * m[4] + c[2] * m[7] + m[10],
                        c[0] * m[2] + c[1] * m[5] + c[2] * m[8] + m[11]);
                }

                for (var t = 0; t < p_Subset.PrimitiveCount; t++)
                {
                    var s_Tri = new System.Numerics.Vector3[3];
                    var s_Keep = true;
                    for (var k = 0; k < 3; k++)
                    {
                        var s_V = BitConverter.ToUInt16(p_Geometry, s_IndexBase + ((int) p_Subset.StartIndex + t * 3 + k) * 2);
                        s_Tri[k] = At(s_V, out var s_Part);
                        s_Keep &= p_Parts == null || p_Parts.Contains(s_Part);
                    }

                    if (s_Keep)
                        yield return (s_Tri, t);
                }
            }

            private static IEnumerable<System.Numerics.Vector3> Samples(System.Numerics.Vector3[] p_Tri)
            {
                yield return p_Tri[0];
                yield return p_Tri[1];
                yield return p_Tri[2];
                yield return (p_Tri[0] + p_Tri[1] + p_Tri[2]) / 3;
                yield return (p_Tri[0] + p_Tri[1]) / 2;
                yield return (p_Tri[1] + p_Tri[2]) / 2;
                yield return (p_Tri[2] + p_Tri[0]) / 2;
            }

            private static (int, int, int) CellOf(System.Numerics.Vector3 p_P) =>
                ((int) MathF.Floor(p_P.X / Cell), (int) MathF.Floor(p_P.Y / Cell), (int) MathF.Floor(p_P.Z / Cell));

            private static void Add(Dictionary<(int, int, int), List<System.Numerics.Vector3>> p_Grid, System.Numerics.Vector3 p_P)
            {
                var s_Cell = CellOf(p_P);
                if (!p_Grid.TryGetValue(s_Cell, out var s_List))
                    p_Grid[s_Cell] = s_List = new List<System.Numerics.Vector3>();
                s_List.Add(p_P);
            }

            private static bool Near(Dictionary<(int, int, int), List<System.Numerics.Vector3>> p_Grid, System.Numerics.Vector3 p_P, float p_Reach)
            {
                var (s_X, s_Y, s_Z) = CellOf(p_P);
                var s_R = (int) MathF.Ceiling(p_Reach / Cell);
                var s_Reach2 = p_Reach * p_Reach;
                for (var dx = -s_R; dx <= s_R; dx++)
                for (var dy = -s_R; dy <= s_R; dy++)
                for (var dz = -s_R; dz <= s_R; dz++)
                    if (p_Grid.TryGetValue((s_X + dx, s_Y + dy, s_Z + dz), out var s_List) &&
                        s_List.Any(p_Q => System.Numerics.Vector3.DistanceSquared(p_P, p_Q) <= s_Reach2))
                        return true;
                return false;
            }

            private static float Share(IEnumerable<System.Numerics.Vector3> p_Points, Dictionary<(int, int, int), List<System.Numerics.Vector3>> p_Grid,
                float p_Reach)
            {
                int s_Near = 0, s_All = 0;
                foreach (var s_P in p_Points)
                {
                    s_All++;
                    if (Near(p_Grid, s_P, p_Reach))
                        s_Near++;
                }

                return s_All == 0 ? 0 : (float) s_Near / s_All;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // The piece
        // ---------------------------------------------------------------------------------------------------------------------

        private sealed class DepthOnly
        {
            public readonly List<byte[]> Positions = new();   // Pos Half4, w = 1
            public readonly List<ushort> Indices = new();
        }

        private static DepthOnly BuildDepthOnly(List<Piece> p_Opaque)
        {
            var s_Out = new DepthOnly();
            var s_Weld = new Dictionary<string, ushort>();
            var s_One = BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half) 1f));

            foreach (var s_Piece in p_Opaque)
            {
                var s_Pos = GeometryPositionOffset(s_Piece);
                foreach (var s_I in s_Piece.Indices)
                {
                    var s_V = s_Piece.Vertices[s_I];
                    var s_Key = Convert.ToHexString(s_V, s_Pos, 6);
                    if (!s_Weld.TryGetValue(s_Key, out var s_New))
                    {
                        var s_P = new byte[16];
                        Array.Copy(s_V, s_Pos, s_P, 0, 6);
                        s_One.CopyTo(s_P, 6);
                        s_New = (ushort) s_Out.Positions.Count;
                        s_Out.Positions.Add(s_P);
                        s_Weld[s_Key] = s_New;
                    }

                    s_Out.Indices.Add(s_New);
                }
            }

            return s_Out;
        }

        /// <summary>Where the position lives in a piece's vertices (read back from the rigid declaration it carries).</summary>
        private static int GeometryPositionOffset(Piece p_Piece)
        {
            for (var e = 0; e < 16; e++)
                if (p_Piece.Declaration[e * 4] == (byte) fb.VertexElementUsage.VertexElementUsage_Pos)
                    return p_Piece.Declaration[e * 4 + 2];
            return 0;
        }

        private static byte[] BuildChunk(List<Piece> p_Pieces, DepthOnly p_Depth, out int[] p_VertexOffsets, out int[] p_StartIndices,
            out int p_DepthVertexOffset, out int p_DepthStartIndex, out int p_VertexBytes, out int p_IndexBytes)
        {
            using var s_Ms = new MemoryStream();
            p_VertexOffsets = new int[p_Pieces.Count];
            p_StartIndices = new int[p_Pieces.Count];

            for (var i = 0; i < p_Pieces.Count; i++)
            {
                p_VertexOffsets[i] = (int) s_Ms.Position;
                foreach (var s_V in p_Pieces[i].Vertices)
                    s_Ms.Write(s_V);
            }

            p_DepthVertexOffset = (int) s_Ms.Position;
            foreach (var s_P in p_Depth.Positions)
                s_Ms.Write(s_P);

            p_VertexBytes = (int) s_Ms.Position;
            var s_Index = 0;
            for (var i = 0; i < p_Pieces.Count; i++)
            {
                p_StartIndices[i] = s_Index;
                foreach (var s_I in p_Pieces[i].Indices)
                    s_Ms.Write(BitConverter.GetBytes(s_I));
                s_Index += p_Pieces[i].Indices.Count;
            }

            p_DepthStartIndex = s_Index;
            foreach (var s_I in p_Depth.Indices)
                s_Ms.Write(BitConverter.GetBytes(s_I));

            p_IndexBytes = (int) s_Ms.Position - p_VertexBytes;
            return s_Ms.ToArray();
        }

        private static int Align(int p_Offset, int p_Alignment) => (p_Offset + p_Alignment - 1) & ~(p_Alignment - 1);

        /// <summary>The rigid MeshSet, laid out as the game's own rigid meshes are (see the class notes).</summary>
        private static byte[] WriteRigidMeshSet(string p_Name, List<Piece> p_Pieces, DepthOnly p_Depth, int[] p_VertexOffsets,
            int[] p_StartIndices, int p_DepthVertexOffset, int p_DepthStartIndex, int p_VertexBytes, int p_IndexBytes, GUID p_Chunk,
            out int p_RelocAt)
        {
            var s_Short = p_Name[(p_Name.LastIndexOf('/') + 1)..];
            var s_Lod = HeaderSize;
            var s_Subsets = Align(s_Lod + LodSize, 16);
            var s_SubsetCount = p_Pieces.Count + 1;

            // strings: section material names (the depth-only one empty), the LOD's debug name, name and short name, then the set's
            var s_Strings = new List<(string Text, int At)>();
            var s_Cursor = Align(s_Subsets + s_SubsetCount * SubsetSize, 16);

            int Put(string p_Text)
            {
                var s_At = s_Cursor;
                s_Strings.Add((p_Text, s_At));
                s_Cursor += Encoding.ASCII.GetByteCount(p_Text) + 1;
                return s_At;
            }

            var s_MaterialAt = p_Pieces.Select(p_P => Put(p_P.MaterialName)).Append(Put("")).ToArray();
            var s_DebugAt = Put($"Mesh:{p_Name}_lod0");
            var s_LodNameAt = Put($"{p_Name}_lod0");
            var s_LodShortAt = Put($"{s_Short}_lod0");
            var s_NameAt = Put(p_Name);
            var s_ShortAt = Put(s_Short);

            // category bytes: the sections of each category, the depth-only one in category 3
            s_Cursor = Align(s_Cursor, 16);
            var s_Categories = new List<byte>[4];
            for (var k = 0; k < 4; k++)
                s_Categories[k] = new List<byte>();
            for (var i = 0; i < p_Pieces.Count; i++)
                s_Categories[p_Pieces[i].Category].Add((byte) i);
            s_Categories[3].Add((byte) p_Pieces.Count);

            var s_CategoryAt = new int[4];
            for (var k = 0; k < 4; k++)
            {
                s_CategoryAt[k] = s_Cursor;
                s_Cursor += s_Categories[k].Count;
            }

            p_RelocAt = Align(s_Cursor, 16);
            var s_Relocs = new List<int>();
            var s_Buffer = new byte[p_RelocAt];

            void U32(int p_At, uint p_V) => BitConverter.GetBytes(p_V).CopyTo(s_Buffer, p_At);
            void F32(int p_At, float p_V) => BitConverter.GetBytes(p_V).CopyTo(s_Buffer, p_At);
            void Ptr(int p_At, int p_Target)
            {
                BitConverter.GetBytes((ulong) p_Target).CopyTo(s_Buffer, p_At);
                s_Relocs.Add(p_At);
            }

            // bounds of the piece
            var s_Min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var s_Max = new[] { float.MinValue, float.MinValue, float.MinValue };
            foreach (var s_P in p_Depth.Positions)
                for (var c = 0; c < 3; c++)
                {
                    var s_F = (float) BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(s_P, c * 2));
                    s_Min[c] = System.Math.Min(s_Min[c], s_F);
                    s_Max[c] = System.Math.Max(s_Max[c], s_F);
                }

            // header
            U32(0x00, 0);                 // rigid
            U32(0x04, 0x1);               // flags, as the game's rigid meshes
            U32(0x08, 1);                 // one LOD
            U32(0x0C, (uint) s_SubsetCount);
            for (var c = 0; c < 3; c++)
            {
                F32(0x10 + c * 4, s_Min[c]);
                F32(0x20 + c * 4, s_Max[c]);
            }

            Ptr(0x30, s_Lod);
            Ptr(0x58, s_NameAt);
            Ptr(0x60, s_ShortAt);
            U32(0x68, RimeLib.Frostbite.Utils.HashQuickLowerCase(p_Name.ToLowerInvariant()));

            // LOD
            U32(s_Lod + 0x00, 0);
            U32(s_Lod + 0x04, (uint) s_SubsetCount);
            Ptr(s_Lod + 0x08, s_Subsets);
            for (var k = 0; k < 4; k++)
            {
                U32(s_Lod + 0x10 + k * 12, (uint) s_Categories[k].Count);
                Ptr(s_Lod + 0x14 + k * 12, s_CategoryAt[k]);
            }

            U32(s_Lod + 0x40, 0x40);      // StreamingEnable
            U32(s_Lod + 0x44, 0);         // 16-bit indices
            U32(s_Lod + 0x48, (uint) p_IndexBytes);
            U32(s_Lod + 0x4C, (uint) p_VertexBytes);
            U32(s_Lod + 0x50, 0);
            p_Chunk.Id.CopyTo(s_Buffer, s_Lod + 0x54);
            U32(s_Lod + 0x64, 0xFFFFFFFF);
            Ptr(s_Lod + 0x70, s_DebugAt);
            Ptr(s_Lod + 0x78, s_LodNameAt);
            Ptr(s_Lod + 0x80, s_LodShortAt);
            U32(s_Lod + 0x88, RimeLib.Frostbite.Utils.HashQuickLowerCase($"{p_Name}_lod0".ToLowerInvariant()));

            // sections
            for (var i = 0; i < s_SubsetCount; i++)
            {
                var s_At = s_Subsets + i * SubsetSize;
                var s_IsDepth = i == p_Pieces.Count;
                Ptr(s_At + 0x08, s_MaterialAt[i]);
                if (!s_IsDepth)
                {
                    var s_P = p_Pieces[i];
                    U32(s_At + 0x10, s_P.MaterialIndex);
                    U32(s_At + 0x14, (uint) (s_P.Indices.Count / 3));
                    U32(s_At + 0x18, (uint) p_StartIndices[i]);
                    U32(s_At + 0x1C, (uint) p_VertexOffsets[i]);
                    U32(s_At + 0x20, (uint) s_P.Vertices.Count);
                    s_Buffer[s_At + 0x24] = (byte) s_P.Stride;
                    s_P.Declaration.CopyTo(s_Buffer, s_At + 0x30);
                    for (var r = 0; r < 6; r++)
                        F32(s_At + 0x7C + r * 4, s_P.Ratios[r]);
                }
                else
                {
                    // the depth-only section wears the first opaque section's material index, as the game's do
                    U32(s_At + 0x10, p_Pieces.FirstOrDefault(p_P => p_P.Category == 0)?.MaterialIndex ?? 0);
                    U32(s_At + 0x14, (uint) (p_Depth.Indices.Count / 3));
                    U32(s_At + 0x18, (uint) p_DepthStartIndex);
                    U32(s_At + 0x1C, (uint) p_DepthVertexOffset);
                    U32(s_At + 0x20, (uint) p_Depth.Positions.Count);
                    s_Buffer[s_At + 0x24] = 16;
                    var s_Decl = new GeometryDeclarationDesc();
                    s_Decl.Elements.Add(new GeometryDeclarationDesc.Element
                    {
                        Usage = fb.VertexElementUsage.VertexElementUsage_Pos,
                        Format = fb.VertexElementFormat.VertexElementFormat_Half4,
                        Offset = 0,
                        StreamIndex = 0,
                    });
                    s_Decl.Streams.Add(new GeometryDeclarationDesc.Stream { Stride = 16 });
                    using var s_Ms = new MemoryStream();
                    using (var s_W = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
                        s_Decl.Serialize(s_W);
                    Array.Copy(s_Ms.ToArray(), 0, s_Buffer, s_At + 0x30, DeclSize);
                    for (var r = 0; r < 6; r++)
                        F32(s_At + 0x7C + r * 4, 1f);
                }

                s_Buffer[s_At + 0x25] = 3;    // triangle list
            }

            foreach (var (s_Text, s_At) in s_Strings)
                Encoding.ASCII.GetBytes(s_Text).CopyTo(s_Buffer, s_At);

            for (var k = 0; k < 4; k++)
                s_Categories[k].ToArray().CopyTo(s_Buffer, s_CategoryAt[k]);

            var s_Out = new byte[p_RelocAt + s_Relocs.Count * 4];
            s_Buffer.CopyTo(s_Out, 0);
            for (var i = 0; i < s_Relocs.Count; i++)
                BitConverter.GetBytes((uint) s_Relocs[i]).CopyTo(s_Out, p_RelocAt + i * 4);

            return s_Out;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // The EBX clone
        // ---------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The body's EBX partition as the piece's: fresh partition guid, instance guids KEPT (the variation entry points at the
        /// body's own MeshMaterial instances), the new name and its hash, and the asset's type CompositeMeshAsset -> RigidMeshAsset
        /// (the two declare the same fields; a type is named by the hash in its descriptor and by its string in the keyword table).
        /// Every patch must land exactly where expected, or nothing is published.
        /// </summary>
        private static bool CloneEbx(IEngineMounter p_Mounter, SbBuildingContext p_SbContext, string p_Mesh, string p_NewName,
            string p_NewPartition, TextWriter p_Writer, [NotNullWhen(true)] out byte[]? p_Bytes, out uint p_Hash)
        {
            p_Bytes = null;
            p_Hash = 0;

            if (!p_Mounter.TryGetPartition(p_Mesh, out var s_Mounted))
            {
                p_Writer.WriteLine($"The body's EBX partition ({p_Mesh}) is not mounted.");
                return false;
            }

            var s_Variant = s_Mounted.Variants.FirstOrDefault(p_V => p_V.GetContainedBundle() != null) ?? s_Mounted.FirstVariant;
            if (s_Variant == null)
            {
                p_Writer.WriteLine($"No readable variant of the partition ({p_Mesh}).");
                return false;
            }

            var s_Db = EngineInterfaceRegistry.Create<IPartitionConverter>(p_SbContext.EngineType).FromPartitionObject(p_Mesh, s_Variant);
            var s_Asset = s_Db.Instances.OfType<fb.CompositeMeshAsset>().FirstOrDefault();
            if (s_Asset == null || string.IsNullOrWhiteSpace(s_Asset.Name))
            {
                p_Writer.WriteLine($"The partition '{p_Mesh}' carries no named CompositeMeshAsset.");
                return false;
            }

            var s_OldName = s_Asset.Name;
            if (s_OldName.Length != p_NewName.Length)
            {
                p_Writer.WriteLine($"The piece's name must be {s_OldName.Length} characters long like '{s_OldName}' (got {p_NewName.Length}).");
                return false;
            }

            byte[] s_Bytes;
            using (var s_Reader = s_Variant.GetReader())
                s_Bytes = s_Reader.ReadBytes((int) s_Reader.Length);

            var s_Guids = ReplaceAll(s_Bytes, s_Db.PartitionGuid.Id, new GUID(p_NewPartition.Trim()).Id, false);
            var s_Names = ReplaceAll(s_Bytes, Encoding.ASCII.GetBytes(s_OldName), Encoding.ASCII.GetBytes(p_NewName), false);

            var s_OldHash = RimeLib.Frostbite.Utils.HashQuickLowerCase(s_OldName);
            p_Hash = RimeLib.Frostbite.Utils.HashQuickLowerCase(p_NewName);
            var s_Hashes = ReplaceAll(s_Bytes, BitConverter.GetBytes(s_OldHash), BitConverter.GetBytes(p_Hash), true);

            var s_Types = ReplaceAll(s_Bytes, BitConverter.GetBytes(RimeLib.Frostbite.Utils.HashQuick("CompositeMeshAsset")),
                BitConverter.GetBytes(RimeLib.Frostbite.Utils.HashQuick("RigidMeshAsset")), false);
            var s_Keywords = ReplaceAll(s_Bytes, Encoding.ASCII.GetBytes("CompositeMeshAsset\0"),
                Encoding.ASCII.GetBytes("RigidMeshAsset\0\0\0\0\0"), false);

            p_Writer.WriteLine($"  EBX clone of '{s_OldName}': partition guid x{s_Guids}, name x{s_Names}, name hash x{s_Hashes}, " +
                               $"asset type x{s_Types}, type keyword x{s_Keywords}, {s_Db.Instances.OfType<fb.MeshMaterial>().Count()} material(s)");

            if (s_Guids == 0 || s_Names == 0 || s_Hashes == 0 || s_Types != 1 || s_Keywords > 1)
            {
                p_Writer.WriteLine("  the clone did not patch where it must (guid, name and hash at least once; the asset type exactly " +
                                   "once) — nothing published.");
                return false;
            }

            p_Bytes = s_Bytes;
            return true;
        }

        private static int ReplaceAll(byte[] p_Data, byte[] p_Find, byte[] p_Replacement, bool p_Aligned)
        {
            if (p_Find.Length != p_Replacement.Length || p_Find.Length == 0)
                return 0;

            var s_Count = 0;
            for (var i = 0; i <= p_Data.Length - p_Find.Length; i += p_Aligned ? 4 : 1)
            {
                if (!p_Data.AsSpan(i, p_Find.Length).SequenceEqual(p_Find))
                    continue;

                p_Replacement.CopyTo(p_Data, i);
                s_Count++;
                if (!p_Aligned)
                    i += p_Find.Length - 1;
            }

            return s_Count;
        }

        /// <summary>The body's MeshSet type, serving the piece's bytes under the piece's name with the piece's meta.</summary>
        private sealed class PieceMeshResource : IResourceObject
        {
            private readonly IResourceVariant m_Original;
            private readonly string m_Path;
            private readonly string m_Name;
            private readonly byte[] m_Meta;

            public PieceMeshResource(IResourceVariant p_Original, string p_Path, string p_Name, byte[] p_Meta)
            {
                m_Original = p_Original;
                m_Path = p_Path;
                m_Name = p_Name;
                m_Meta = p_Meta;
            }

            public ResourceType GetResourceType() => m_Original.GetResourceType();

            public bool TryGetMeta([NotNullWhen(true)] out byte[]? p_Meta)
            {
                p_Meta = m_Meta;
                return true;
            }

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

            public RimeReader GetReader() => new(File.Open(m_Path, FileMode.Open, FileAccess.Read, FileShare.Read));

            public long GetSize() => new FileInfo(m_Path).Length;
        }
    }
}
