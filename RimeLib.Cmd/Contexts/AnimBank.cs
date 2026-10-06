using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ant;
using Newtonsoft.Json;
using Rimelib.Animation.Frostbite2_0.Frostbite;
using RimeLib.Animation.Frostbite2_0.EA.Compression.DCT;
using RimeLib.Content.Mounting;
using RimeLib.Frostbite.Core;
using RimeLib.IO;
using RimeLib.Serialization;

namespace RimeLib.Cmd.Contexts
{
    /// <summary>
    /// An animation bank opened for reading: the shared static bank (the rigs, the channel maps, the clips)
    /// plus one package (a weapon's streamed chunk, or a bundled bank held as a resource), so that its
    /// animations can be listed and a single frame of one of them can be read out by DOF name.
    ///
    /// A one-frame animation on a rig is a POSE: per bone, the local transform relative to the parent bone
    /// ('Wep_Bipod1.t' = where the bipod bone sits relative to the weapon root). The 1P weapon assembly reads
    /// one such pose (WeaponPose); this reader is the general form, for finding the 3P poses.
    /// </summary>
    internal static class AnimBank
    {
        private const string c_StaticBank = "animations/antanimations/s_basicassets";

        private static AssetBank? s_Static;

        public sealed class Frame
        {
            public string Bank { get; set; } = "";
            public string Anim { get; set; } = "";
            public string Rig { get; set; } = "";
            public int FrameIndex { get; set; }
            public int QuatCount { get; set; }
            public int Vec3Count { get; set; }
            public int FloatCount { get; set; }
            /// <summary>DOF name -> four floats (x, y, z, w). Quaternions and vec3s alike.</summary>
            public Dictionary<string, float[]> Dofs { get; set; } = new();
            /// <summary>Channels the map did not name (channel index -> four floats).</summary>
            public Dictionary<int, float[]> Unnamed { get; set; } = new();
        }

        /// <summary>The static bank, loaded once per process (every package resolves against it).</summary>
        public static AssetBank Static(IEngineMounter p_Mounter, TextWriter p_Writer)
        {
            if (s_Static != null)
                return s_Static;

            if (!p_Mounter.TryGetResource(c_StaticBank, out var s_Resource))
                throw new Exception($"the static animation bank '{c_StaticBank}' is not mounted.");

            using var s_ResourceReader = s_Resource.FirstVariant.GetReader();
            var s_Bytes = s_ResourceReader.ReadBytes((int) s_ResourceReader.Length);
            s_Static = new AssetBank();
            using var s_Reader = new RimeReader(new MemoryStream(s_Bytes));
            s_Static.Load(s_Reader, s_Static);
            p_Writer.WriteLine($"ANIMBANK: static bank loaded, {s_Static.Objects.Count} objects");
            return s_Static;
        }

        /// <summary>
        /// Opens a package by name: a partition holding an AntPackageAsset with a streaming chunk (the weapons:
        /// 'animations/antanimations/xp1_l85a2'), else a resource of that name (the bundled banks:
        /// 'animations/antanimations/b_menu'). The static bank is loaded first.
        /// </summary>
        public static AssetBank Open(IEngineMounter p_Mounter, string p_Name, TextWriter p_Writer)
        {
            Static(p_Mounter, p_Writer);

            byte[]? s_Bytes = null;
            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(p_Mounter.GetEngineType());

            if (p_Mounter.TryGetPartition(p_Name, out var s_Mounted))
            {
                var s_Package = (s_Converter.FromPartitionObject(p_Name, s_Mounted.FirstVariant)
                        as RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition)?
                    .InstanceMap.Values.OfType<fb.AntPackageAsset>().FirstOrDefault();

                if (s_Package != null && s_Package.StreamingGuid != GUID.Empty)
                {
                    if (!p_Mounter.TryGetChunk(s_Package.StreamingGuid, out var s_Chunk))
                        throw new Exception($"the package chunk {s_Package.StreamingGuid} of '{p_Name}' is not mounted.");

                    using var s_ChunkReader = s_Chunk.FirstVariant.GetReader();
                    s_Bytes = s_ChunkReader.ReadBytes((int) s_ChunkReader.Length);
                    p_Writer.WriteLine($"ANIMBANK: '{p_Name}' = streamed package, chunk {s_Package.StreamingGuid}, {s_Bytes.Length} bytes");
                }
                else if (s_Package != null)
                {
                    p_Writer.WriteLine($"ANIMBANK: '{p_Name}' = {s_Package.PackagingType} package without a chunk (file '{s_Package.Win32FileName}'); trying it as a resource");
                }
            }

            if (s_Bytes == null)
            {
                if (!p_Mounter.TryGetResource(p_Name, out var s_Resource))
                    throw new Exception($"'{p_Name}' is neither a partition with a streamed AntPackageAsset nor a mounted resource.");

                using var s_ResourceReader = s_Resource.FirstVariant.GetReader();
                s_Bytes = s_ResourceReader.ReadBytes((int) s_ResourceReader.Length);
                p_Writer.WriteLine($"ANIMBANK: '{p_Name}' = resource, {s_Bytes.Length} bytes");
            }

            var s_Bank = new AssetBank();
            using var s_Reader = new RimeReader(new MemoryStream(s_Bytes));
            s_Bank.Load(s_Reader, s_Bank);
            p_Writer.WriteLine($"ANIMBANK: '{p_Name}' loaded, {s_Bank.Objects.Count} objects");
            return s_Bank;
        }

        /// <summary>Lists a bank's objects: every animation with its shape, every rig with its DOF count, the rest by type.</summary>
        public static void List(AssetBank p_Bank, string? p_Filter, TextWriter p_Writer)
        {
            var s_Filter = p_Filter ?? "";
            var s_Shown = 0;

            foreach (var s_Object in p_Bank.Objects)
            {
                var s_Name = s_Object.ObjectName ?? "";
                var s_Type = s_Object.GetType().Name;

                if (s_Filter.Length > 0 && !s_Name.Contains(s_Filter, StringComparison.OrdinalIgnoreCase) &&
                    !s_Type.Contains(s_Filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                s_Shown++;

                switch (s_Object)
                {
                    case AnimationAsset s_Anim:
                    {
                        var s_Map = s_Anim.ChannelToDofAsset.Object;
                        var s_MapText = s_Map == null
                            ? "c2d=unresolved"
                            : $"c2d='{s_Map.ObjectName}' storage={s_Map.StorageType} bytes={s_Map.IndexData.Count} maxdof={MaxDof(s_Map)}";
                        p_Writer.WriteLine($"ANIM  {s_Type,-22} '{s_Name}' {Shape(s_Anim)} codec={s_Anim.CodecType} end={s_Anim.EndFrame} additive={s_Anim.Additive} {s_MapText}");
                        break;
                    }
                    case LayoutHierarchyAsset s_Rig:
                        p_Writer.WriteLine($"RIG   {s_Type,-22} '{s_Name}' dofs={Flatten(s_Rig).Count}");
                        break;
                    default:
                        p_Writer.WriteLine($"OBJ   {s_Type,-22} '{s_Name}'");
                        break;
                }
            }

            p_Writer.WriteLine($"ANIMBANK: {s_Shown} of {p_Bank.Objects.Count} objects listed" + (s_Filter.Length > 0 ? $" (filter '{s_Filter}')" : ""));
        }

        /// <summary>Prints a rig's DOF table (index: name).</summary>
        public static void ListRig(LayoutHierarchyAsset p_Rig, string? p_Filter, TextWriter p_Writer)
        {
            var s_Dofs = Flatten(p_Rig);
            for (var i = 0; i < s_Dofs.Count; i++)
                if (string.IsNullOrEmpty(p_Filter) || s_Dofs[i].Name.Contains(p_Filter, StringComparison.OrdinalIgnoreCase))
                    p_Writer.WriteLine($"DOF   {i,4}: {s_Dofs[i].Name}");

            p_Writer.WriteLine($"ANIMBANK: rig '{p_Rig.ObjectName}' has {s_Dofs.Count} DOFs");
        }

        /// <summary>Finds a rig by name in the package, then in the static bank.</summary>
        public static LayoutHierarchyAsset? FindRig(AssetBank p_Bank, string p_Name)
        {
            return p_Bank.Objects.OfType<LayoutHierarchyAsset>().FirstOrDefault(p_R => p_R.ObjectName.Equals(p_Name, StringComparison.OrdinalIgnoreCase))
                   ?? s_Static?.Objects.OfType<LayoutHierarchyAsset>().FirstOrDefault(p_R => p_R.ObjectName.Equals(p_Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The animations of a package named exactly p_Name (else those whose name contains it), in bank order; with
        /// p_Quats > 0 only those with that many quaternion channels (a bank repeats a name across rigs and weapons).
        /// </summary>
        public static List<AnimationAsset> FindAnims(AssetBank p_Bank, string p_Name, int p_Quats)
        {
            bool Shape(AnimationAsset p_Anim) => p_Quats <= 0 || Quats(p_Anim) == p_Quats;

            var s_Exact = p_Bank.Objects.OfType<AnimationAsset>()
                .Where(p_A => p_A.ObjectName.Equals(p_Name, StringComparison.OrdinalIgnoreCase) && Shape(p_A)).ToList();
            if (s_Exact.Count > 0)
                return s_Exact;

            return p_Bank.Objects.OfType<AnimationAsset>()
                .Where(p_A => p_A.ObjectName.Contains(p_Name, StringComparison.OrdinalIgnoreCase) && Shape(p_A)).ToList();
        }

        private static int Quats(AnimationAsset p_Anim) => p_Anim switch
        {
            FrameAnimationAsset s_F => (int) s_F.QuatCount,
            RawAnimationAsset s_R => (int) s_R.QuatCount,
            DctAnimationAsset s_D => s_D.NumQuats,
            _ => -1,
        };

        /// <summary>
        /// Reads one frame of an animation: every channel as four floats, in channel order (quaternions first,
        /// then vec3s, then floats), from a frame animation (one frame), a raw animation (keys of the same
        /// layout) or a DCT animation (decoded).
        /// </summary>
        public static (int quats, int vec3s, int floats, List<float[]> channels) ReadFrame(AnimationAsset p_Anim, int p_Frame)
        {
            switch (p_Anim)
            {
                case FrameAnimationAsset s_Frame:
                {
                    var s_Channels = (int) (s_Frame.QuatCount + s_Frame.Vec3Count + s_Frame.FloatCount);
                    var s_Values = Slice(s_Frame.Data, s_Channels, p_Frame);
                    return ((int) s_Frame.QuatCount, (int) s_Frame.Vec3Count, (int) s_Frame.FloatCount, s_Values);
                }
                case RawAnimationAsset s_Raw:
                {
                    var s_Channels = (int) (s_Raw.QuatCount + s_Raw.Vec3Count + s_Raw.FloatCount);
                    var s_Values = Slice(s_Raw.Data, s_Channels, p_Frame);
                    return ((int) s_Raw.QuatCount, (int) s_Raw.Vec3Count, (int) s_Raw.FloatCount, s_Values);
                }
                case DctAnimationAsset s_Dct:
                {
                    var s_Decoded = new Decompressor().DecodeFrame(s_Dct, (ushort) p_Frame);
                    var s_Values = s_Decoded.Select(p_V => new[] { p_V.X, p_V.Y, p_V.Z, p_V.W }).ToList();
                    return (s_Dct.NumQuats, s_Dct.NumVec3, s_Dct.NumFloatVec, s_Values);
                }
                default:
                    throw new Exception($"'{p_Anim.ObjectName}' is a {p_Anim.GetType().Name}: not a frame, raw or DCT animation.");
            }
        }

        /// <summary>
        /// The channel -> DOF map of an animation as an array indexed by channel. Storage 1 (overwrite): one byte
        /// per channel = the DOF. Storage 2 (append): pairs (dof, channel) read as big-endian u16 each. Storage 0
        /// (read) is taken like 1. Channels the map does not cover are -1.
        /// </summary>
        public static int[] ChannelDofs(ChannelToDofAsset p_Map, int p_Channels, TextWriter p_Writer)
        {
            var s_Dofs = Enumerable.Repeat(-1, p_Channels).ToArray();
            var s_Data = p_Map.IndexData;

            if (p_Map.StorageType == 2)
            {
                for (var i = 0; i + 3 < s_Data.Count; i += 4)
                {
                    var s_Dof = (s_Data[i] << 8) | s_Data[i + 1];
                    var s_Channel = (s_Data[i + 2] << 8) | s_Data[i + 3];
                    if (s_Channel >= 0 && s_Channel < p_Channels)
                        s_Dofs[s_Channel] = s_Dof;
                }

                p_Writer.WriteLine($"ANIMBANK: c2d '{p_Map.ObjectName}' storage 2: {s_Data.Count / 4} (dof, channel) u16-BE pairs; first bytes {string.Join(" ", s_Data.Take(16).Select(p_B => p_B.ToString("X2")))}");
            }
            else
            {
                for (var i = 0; i < s_Data.Count && i < p_Channels; i++)
                    s_Dofs[i] = s_Data[i];

                p_Writer.WriteLine($"ANIMBANK: c2d '{p_Map.ObjectName}' storage {p_Map.StorageType}: {s_Data.Count} bytes, one DOF per channel; first bytes {string.Join(" ", s_Data.Take(16).Select(p_B => p_B.ToString("X2")))}");
            }

            return s_Dofs;
        }

        /// <summary>One frame of an animation as DOF name -> four floats under a rig (channels the map does not name are dropped).</summary>
        public static Dictionary<string, float[]> ReadDofs(AnimationAsset p_Anim, LayoutHierarchyAsset p_Rig, int p_FrameIndex, TextWriter? p_Writer)
        {
            var (_, _, _, s_Channels) = ReadFrame(p_Anim, p_FrameIndex);
            var s_Map = p_Anim.ChannelToDofAsset.Object ?? throw new Exception($"the channel map of '{p_Anim.ObjectName}' did not resolve.");
            var s_Dofs = Flatten(p_Rig);
            var s_ChannelDofs = ChannelDofs(s_Map, s_Channels.Count, p_Writer ?? TextWriter.Null);
            var s_Values = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

            for (var s_Channel = 0; s_Channel < s_Channels.Count; s_Channel++)
            {
                var s_Dof = s_ChannelDofs[s_Channel];
                if (s_Dof >= 0 && s_Dof < s_Dofs.Count)
                    s_Values[s_Dofs[s_Dof].Name] = s_Channels[s_Channel];
            }

            return s_Values;
        }

        /// <summary>Reads one frame of an animation by DOF name under a rig and writes it as JSON; prints the named channels.</summary>
        public static Frame DumpFrame(AssetBank p_Bank, string p_BankName, AnimationAsset p_Anim, LayoutHierarchyAsset p_Rig, int p_FrameIndex,
            FileInfo? p_Destination, string? p_PrintFilter, TextWriter p_Writer)
        {
            var (s_Quats, s_Vec3s, s_Floats, s_Channels) = ReadFrame(p_Anim, p_FrameIndex);
            var s_Map = p_Anim.ChannelToDofAsset.Object ?? throw new Exception($"the channel map of '{p_Anim.ObjectName}' did not resolve.");
            var s_Dofs = Flatten(p_Rig);
            var s_ChannelDofs = ChannelDofs(s_Map, s_Channels.Count, p_Writer);

            var s_Frame = new Frame
            {
                Bank = p_BankName, Anim = p_Anim.ObjectName, Rig = p_Rig.ObjectName, FrameIndex = p_FrameIndex,
                QuatCount = s_Quats, Vec3Count = s_Vec3s, FloatCount = s_Floats,
            };

            for (var s_Channel = 0; s_Channel < s_Channels.Count; s_Channel++)
            {
                var s_Dof = s_ChannelDofs[s_Channel];
                var s_Kind = s_Channel < s_Quats ? "q" : s_Channel < s_Quats + s_Vec3s ? "v" : "f";
                var s_Value = s_Channels[s_Channel];

                if (s_Dof >= 0 && s_Dof < s_Dofs.Count)
                {
                    var s_Name = s_Dofs[s_Dof].Name;
                    s_Frame.Dofs[s_Name] = s_Value;

                    if (string.IsNullOrEmpty(p_PrintFilter) || s_Name.Contains(p_PrintFilter, StringComparison.OrdinalIgnoreCase))
                        p_Writer.WriteLine($"FRAME {s_Kind} ch{s_Channel,3} dof{s_Dof,4} {s_Name,-28} ({s_Value[0]:0.####}, {s_Value[1]:0.####}, {s_Value[2]:0.####}, {s_Value[3]:0.####})");
                }
                else
                {
                    s_Frame.Unnamed[s_Channel] = s_Value;
                    if (string.IsNullOrEmpty(p_PrintFilter))
                        p_Writer.WriteLine($"FRAME {s_Kind} ch{s_Channel,3} dof{s_Dof,4} (unnamed)                     ({s_Value[0]:0.####}, {s_Value[1]:0.####}, {s_Value[2]:0.####}, {s_Value[3]:0.####})");
                }
            }

            p_Writer.WriteLine($"ANIMBANK: '{p_Anim.ObjectName}' ({p_Anim.GetType().Name}) frame {p_FrameIndex}: q={s_Quats} v={s_Vec3s} f={s_Floats} " +
                               $"rig='{p_Rig.ObjectName}' dofs={s_Dofs.Count} named={s_Frame.Dofs.Count} unnamed={s_Frame.Unnamed.Count}");

            if (p_Destination != null)
            {
                if (p_Destination.Directory != null && !p_Destination.Directory.Exists)
                    p_Destination.Directory.Create();

                File.WriteAllText(p_Destination.FullName, JsonConvert.SerializeObject(s_Frame, Formatting.Indented));
            }

            return s_Frame;
        }

        private static string Shape(AnimationAsset p_Anim) => p_Anim switch
        {
            FrameAnimationAsset s_F => $"frame q={s_F.QuatCount} v={s_F.Vec3Count} f={s_F.FloatCount} floats={s_F.Data.Count}",
            RawAnimationAsset s_R => $"raw q={s_R.QuatCount} v={s_R.Vec3Count} f={s_R.FloatCount} floats={s_R.Data.Count}",
            DctAnimationAsset s_D => $"dct keys={s_D.NumKeys} q={s_D.NumQuats} v={s_D.NumVec3} f={s_D.NumFloat} fv={s_D.NumFloatVec} cycle={s_D.Cycle}",
            _ => "(other codec)",
        };

        private static int MaxDof(ChannelToDofAsset p_Map)
        {
            if (p_Map.IndexData.Count == 0)
                return -1;

            if (p_Map.StorageType == 2)
            {
                var s_Max = -1;
                for (var i = 0; i + 1 < p_Map.IndexData.Count; i += 4)
                    s_Max = System.Math.Max(s_Max, (p_Map.IndexData[i] << 8) | p_Map.IndexData[i + 1]);
                return s_Max;
            }

            return p_Map.IndexData.Max();
        }

        /// <summary>The four floats of every channel of one frame, from a flat float list (four per channel per frame).</summary>
        private static List<float[]> Slice(List<float> p_Data, int p_Channels, int p_Frame)
        {
            var s_Values = new List<float[]>();
            var s_Base = p_Frame * p_Channels * 4;

            for (int s_Channel = 0, s_At = s_Base; s_Channel < p_Channels && s_At + 4 <= p_Data.Count; s_Channel++, s_At += 4)
                s_Values.Add(new[] { p_Data[s_At], p_Data[s_At + 1], p_Data[s_At + 2], p_Data[s_At + 3] });

            return s_Values;
        }

        /// <summary>The runtime DOF table of a rig (the same flatten WeaponPose uses).</summary>
        public static List<LayoutEntry> Flatten(LayoutHierarchyAsset p_Rig)
        {
            var s_Seen = new HashSet<LayoutAsset>();
            var s_List = new List<LayoutEntry>();

            void Visit(LayoutHierarchyAsset p_Node)
            {
                foreach (var s_Ref in p_Node.LayoutAssets)
                {
                    var s_Layout = s_Ref.Object;
                    if (s_Layout == null || !s_Seen.Add(s_Layout))
                        continue;

                    if (s_Layout is DeltaTrajLayoutAsset && s_Layout.Slots.Count == 0)
                        for (var i = 0; i < 8; i++)
                            s_List.Add(new LayoutEntry { Name = $"DeltaTraj[{i}]" });

                    s_List.AddRange(s_Layout.Slots);
                }

                foreach (var s_Child in p_Node.Children)
                    if (s_Child.Object != null)
                        Visit(s_Child.Object);
            }

            Visit(p_Rig);
            return s_List;
        }
    }
}
