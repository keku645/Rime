using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ant;
using Newtonsoft.Json;
using Rimelib.Animation.Frostbite2_0.Frostbite;
using RimeLib.Content.Mounting;
using RimeLib.Frostbite.Core;
using RimeLib.IO;
using RimeLib.Serialization;

namespace RimeLib.Cmd.Contexts
{
    /// <summary>
    /// The pose that assembles a first-person weapon: its parts, as the player sees them.
    ///
    /// MEASURED (2026-09-11): a weapon mesh stores every animated part around the REST position of its bone
    /// in the shared weapon skeleton, and what puts the parts where they belong in the game is the weapon's
    /// own animation package — its 'IdlePose Anim' (an uncompressed frame animation on the '1P_Upperbody_DOF'
    /// rig) carries, per bone, the local transform relative to the parent bone (the M240's feed cover at
    /// (0, 0.068, 0.569) instead of the rest (0, 0.146, 0.191): forward over the feed tray and down onto the
    /// receiver). The per-weapon package holds only the animations; the clips, the rig (which names the DOFs)
    /// and the channel→DOF maps live in the static bank every weapon shares.
    ///
    /// The pose file this writes is what dump_mesh_sections applies: per bone name, a 3x4 row-vector
    /// transform in the mesh's own space (right, up, forward, translation) that takes a stored vertex to its
    /// assembled place — the pose composed down from the weapon root (which stays the identity, so the mesh
    /// keeps its own space) with the rest position subtracted. Bones not in the file stay put.
    /// </summary>
    internal static class WeaponPose
    {
        private const string c_StaticBank = "animations/antanimations/s_basicassets";
        private const string c_UpperBodyRig = "1P_Upperbody_DOF";
        private const string c_IdleAnim = "IdlePose Anim";

        private static AssetBank? s_Static;

        public sealed class PoseFile
        {
            public string Mesh { get; set; } = "";
            public string Skeleton { get; set; } = "";
            public string Bank { get; set; } = "";
            public string Anim { get; set; } = "";
            public Dictionary<string, float[]> Bones { get; set; } = new();
        }

        /// <summary>Writes the pose file for a weapon mesh; returns how many bones got a transform.</summary>
        public static int Dump(IEngineMounter p_Mounter, string p_Mesh, string p_Skeleton, FileInfo p_Destination,
            TextWriter p_Writer)
        {
            var (s_Values, s_PackageName, s_AnimName) = IdleValues(p_Mounter, p_Mesh, p_Writer);
            var s_Skeleton = LoadSkeleton(p_Mounter, p_Skeleton);

            var s_File = new PoseFile { Mesh = p_Mesh, Skeleton = p_Skeleton, Bank = s_PackageName, Anim = s_AnimName };
            ComposePose(s_Skeleton, p_Skeleton, s_Values, s_File, p_Writer);

            if (p_Destination.Directory != null && !p_Destination.Directory.Exists)
                p_Destination.Directory.Create();

            File.WriteAllText(p_Destination.FullName, JsonConvert.SerializeObject(s_File, Formatting.Indented));
            return s_File.Bones.Count;
        }

        /// <summary>A skeleton partition's SkeletonAsset (bone names, parents, rest poses).</summary>
        private static fb.SkeletonAsset LoadSkeleton(IEngineMounter p_Mounter, string p_Skeleton)
        {
            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(p_Mounter.GetEngineType());
            if (!p_Mounter.TryGetPartition(p_Skeleton, out var s_SkeletonMounted))
                throw new Exception($"skeleton '{p_Skeleton}' is not mounted.");

            return (s_Converter.FromPartitionObject(p_Skeleton, s_SkeletonMounted.FirstVariant)
                       as RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition)?
                   .InstanceMap.Values.OfType<fb.SkeletonAsset>().FirstOrDefault()
                   ?? throw new Exception($"'{p_Skeleton}' holds no SkeletonAsset.");
        }

        public sealed class CameraFile
        {
            public string Skeleton { get; set; } = "";
            public string WeaponSkeleton { get; set; } = "";
            public SortedDictionary<string, WeaponCamera> Meshes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Where the first-person eye sits in a weapon mesh's own space, and where it looks.</summary>
        public sealed class WeaponCamera
        {
            public float[] Eye { get; set; } = new float[3];
            public float[] Look { get; set; } = new float[3];
            public float[] Up { get; set; } = new float[3];
            public string Bank { get; set; } = "";
            public string Anim { get; set; } = "";
        }

        /// <summary>
        /// The first-person camera in a weapon mesh's own space (keku 2026-09-29: "simular cómo se vería el soldado en 1ª persona con el
        /// arma en las manos"; "la cámara correcta es camera_Joint"): the soldier's 1P skeleton composed WHOLE from its root with the
        /// weapon's idle pose (the same row-vector composition as <see cref="ComposePose"/>, bones the pose does not name at rest), then
        /// its camera bone taken into its weapon bone's space, and that into the mesh through the weapon skeleton's rest pose of the same
        /// bone (the mesh is skinned to that skeleton: a point in the bone's space is point · rest in the mesh). Row 0 of a Frostbite
        /// transform is its LEFT (the 1P eye sits on the weapon's +x, the side with the M416's bolt catch — measured by picture).
        /// Merges the weapon into <paramref name="p_Destination"/> (one file for every weapon, keyed by mesh).
        /// </summary>
        public static WeaponCamera Camera(IEngineMounter p_Mounter, string p_Mesh, string p_Skeleton, string p_WeaponSkeleton,
            FileInfo p_Destination, TextWriter p_Writer)
        {
            var (s_Values, s_PackageName, s_AnimName) = IdleValues(p_Mounter, p_Mesh, p_Writer);
            var s_Skeleton = LoadSkeleton(p_Mounter, p_Skeleton);
            var s_Count = s_Skeleton.BoneNames.Count;
            int Bone(fb.SkeletonAsset p_In, string p_Name, string p_What) =>
                p_In.BoneNames.FindIndex(p_N => p_N.Equals(p_Name, StringComparison.OrdinalIgnoreCase)) is var s_I and >= 0
                    ? s_I
                    : throw new Exception($"'{p_What}' has no bone '{p_Name}'.");

            var s_Camera = Bone(s_Skeleton, s_Skeleton.CameraBoneName, p_Skeleton);
            var s_Weapon = Bone(s_Skeleton, s_Skeleton.WeaponBoneName, p_Skeleton);
            var (s_Rotation, s_Translation, s_Named) = ComposeWhole(s_Skeleton, s_Values);

            // The camera in the weapon bone's space: (c − w)·Wᵀ, its axes C·Wᵀ.
            var s_Inverse = Transpose(s_Rotation[s_Weapon]);
            var s_Eye = Transform(new[]
            {
                s_Translation[s_Camera][0] - s_Translation[s_Weapon][0], s_Translation[s_Camera][1] - s_Translation[s_Weapon][1],
                s_Translation[s_Camera][2] - s_Translation[s_Weapon][2],
            }, s_Inverse);
            var s_Axes = Multiply(s_Rotation[s_Camera], s_Inverse);

            // ...and into the mesh: the weapon skeleton's rest pose of the same bone.
            var s_WeaponSkeleton = LoadSkeleton(p_Mounter, p_WeaponSkeleton);
            var s_Rest = s_WeaponSkeleton.ModelPose[Bone(s_WeaponSkeleton, s_Skeleton.WeaponBoneName, p_WeaponSkeleton)];
            var s_RestRotation = new[] { s_Rest.right.x, s_Rest.right.y, s_Rest.right.z, s_Rest.up.x, s_Rest.up.y, s_Rest.up.z, s_Rest.forward.x, s_Rest.forward.y, s_Rest.forward.z };
            var s_Result = new WeaponCamera
            {
                Eye = Add(Transform(s_Eye, s_RestRotation), new[] { s_Rest.trans.x, s_Rest.trans.y, s_Rest.trans.z }),
                Look = Transform(new[] { s_Axes[6], s_Axes[7], s_Axes[8] }, s_RestRotation),
                Up = Transform(new[] { s_Axes[3], s_Axes[4], s_Axes[5] }, s_RestRotation),
                Bank = s_PackageName,
                Anim = s_AnimName,
            };

            p_Writer.WriteLine($"WEAPONCAMERA: {p_Mesh}: {s_Named} bone(s) of {s_Count} posed; camera '{s_Skeleton.CameraBoneName}' in " +
                               $"'{s_Skeleton.WeaponBoneName}' space at ({s_Eye[0]:0.####}, {s_Eye[1]:0.####}, {s_Eye[2]:0.####}); the weapon " +
                               $"skeleton's rest of that bone at ({s_Rest.trans.x:0.####}, {s_Rest.trans.y:0.####}, {s_Rest.trans.z:0.####}); in the " +
                               $"mesh: eye ({s_Result.Eye[0]:0.####}, {s_Result.Eye[1]:0.####}, {s_Result.Eye[2]:0.####}) looking " +
                               $"({s_Result.Look[0]:0.###}, {s_Result.Look[1]:0.###}, {s_Result.Look[2]:0.###}) up ({s_Result.Up[0]:0.###}, {s_Result.Up[1]:0.###}, {s_Result.Up[2]:0.###})");

            var s_File = p_Destination.Exists
                ? JsonConvert.DeserializeObject<CameraFile>(File.ReadAllText(p_Destination.FullName)) ?? new CameraFile()
                : new CameraFile();
            s_File.Meshes = new SortedDictionary<string, WeaponCamera>(s_File.Meshes, StringComparer.OrdinalIgnoreCase)
            {
                [p_Mesh] = s_Result,
            };
            s_File.Skeleton = p_Skeleton;
            s_File.WeaponSkeleton = p_WeaponSkeleton;
            if (p_Destination.Directory != null && !p_Destination.Directory.Exists)
                p_Destination.Directory.Create();
            File.WriteAllText(p_Destination.FullName, JsonConvert.SerializeObject(s_File, Formatting.Indented));
            return s_Result;
        }

        /// <summary>
        /// Every bone of a skeleton posed WHOLE from its root: the named DOF values where the pose has them, the rest local transform
        /// elsewhere, composed down the hierarchy (row vectors: v' = (v·L + tL)·P + tP). Model-space rotation rows and translations per
        /// bone, and how many bones the values named.
        /// </summary>
        private static (float[][] Rotation, float[][] Translation, int Named) ComposeWhole(fb.SkeletonAsset p_Skeleton,
            Dictionary<string, float[]> p_Values)
        {
            var s_Count = p_Skeleton.BoneNames.Count;
            var s_Rotation = new float[s_Count][];
            var s_Translation = new float[s_Count][];
            var s_Named = 0;
            for (var i = 0; i < s_Count; i++)
            {
                var s_Parent = i < p_Skeleton.Hierarchy.Count ? p_Skeleton.Hierarchy[i] : -1;
                var s_Name = p_Skeleton.BoneNames[i];
                var s_Local = p_Skeleton.LocalPose[i];
                var s_LocalRotation = p_Values.TryGetValue(s_Name + ".q", out var s_Q)
                    ? RowsOf(s_Q[0], s_Q[1], s_Q[2], s_Q[3])
                    : new[] { s_Local.right.x, s_Local.right.y, s_Local.right.z, s_Local.up.x, s_Local.up.y, s_Local.up.z, s_Local.forward.x, s_Local.forward.y, s_Local.forward.z };
                var s_LocalTranslation = p_Values.TryGetValue(s_Name + ".t", out var s_T)
                    ? new[] { s_T[0], s_T[1], s_T[2] }
                    : new[] { s_Local.trans.x, s_Local.trans.y, s_Local.trans.z };
                if (s_Q != null || s_T != null)
                    s_Named++;

                if (s_Parent < 0 || s_Parent >= i)
                {
                    s_Rotation[i] = s_LocalRotation;
                    s_Translation[i] = s_LocalTranslation;
                    continue;
                }

                s_Rotation[i] = Multiply(s_LocalRotation, s_Rotation[s_Parent]);
                s_Translation[i] = Add(Transform(s_LocalTranslation, s_Rotation[s_Parent]), s_Translation[s_Parent]);
            }

            return (s_Rotation, s_Translation, s_Named);
        }

        /// <summary>
        /// The pose file that puts the soldier's first-person ARMS where they hold a weapon, in that weapon mesh's own space (keku
        /// 2026-09-29: "el soldado en 1ª persona con el arma en las manos"): the arms mesh is skinned to the 1P skeleton and stored in
        /// its rest pose, so each bone's transform is its rest undone, the weapon's idle pose (the whole skeleton, <see cref="ComposeWhole"/>)
        /// applied, and the result taken into the weapon bone's space: M = Rest⁻¹ · Posed · Weapon⁻¹ (row vectors). Every bone of the
        /// skeleton is written, in the format dump_mesh_sections applies (rows right, up, forward, then translation).
        /// </summary>
        public static int ArmsPose(IEngineMounter p_Mounter, string p_WeaponMesh, string p_Skeleton, FileInfo p_Destination,
            TextWriter p_Writer)
        {
            var (s_Values, s_PackageName, s_AnimName) = IdleValues(p_Mounter, p_WeaponMesh, p_Writer);
            var s_Skeleton = LoadSkeleton(p_Mounter, p_Skeleton);
            var s_Weapon = s_Skeleton.BoneNames.FindIndex(p_N => p_N.Equals(s_Skeleton.WeaponBoneName, StringComparison.OrdinalIgnoreCase));
            if (s_Weapon < 0)
                throw new Exception($"'{p_Skeleton}' has no weapon bone '{s_Skeleton.WeaponBoneName}'.");

            var (s_Rotation, s_Translation, s_Named) = ComposeWhole(s_Skeleton, s_Values);
            var s_WeaponInverse = Transpose(s_Rotation[s_Weapon]);
            var s_File = new PoseFile { Mesh = p_WeaponMesh, Skeleton = p_Skeleton, Bank = s_PackageName, Anim = s_AnimName };
            for (var i = 0; i < s_Skeleton.BoneNames.Count; i++)
            {
                var s_Rest = s_Skeleton.ModelPose[i];
                var s_RestInverse = Transpose(new[] { s_Rest.right.x, s_Rest.right.y, s_Rest.right.z, s_Rest.up.x, s_Rest.up.y, s_Rest.up.z, s_Rest.forward.x, s_Rest.forward.y, s_Rest.forward.z });
                var s_RestTranslation = new[] { s_Rest.trans.x, s_Rest.trans.y, s_Rest.trans.z };

                // Rest⁻¹ · Posed: rotation Rrᵀ·Ri, translation −tr·Rrᵀ·Ri + ti; then · Weapon⁻¹: (· Rwᵀ), (t − tw)·Rwᵀ.
                var s_Rotation1 = Multiply(s_RestInverse, s_Rotation[i]);
                var s_Undone = Transform(Transform(s_RestTranslation, s_RestInverse), s_Rotation[i]);
                var s_Translation1 = new[] { s_Translation[i][0] - s_Undone[0], s_Translation[i][1] - s_Undone[1], s_Translation[i][2] - s_Undone[2] };
                var s_Final = Multiply(s_Rotation1, s_WeaponInverse);
                var s_FinalTranslation = Transform(new[]
                {
                    s_Translation1[0] - s_Translation[s_Weapon][0], s_Translation1[1] - s_Translation[s_Weapon][1],
                    s_Translation1[2] - s_Translation[s_Weapon][2],
                }, s_WeaponInverse);
                s_File.Bones[s_Skeleton.BoneNames[i]] = new[]
                {
                    s_Final[0], s_Final[1], s_Final[2], s_Final[3], s_Final[4], s_Final[5], s_Final[6], s_Final[7], s_Final[8],
                    s_FinalTranslation[0], s_FinalTranslation[1], s_FinalTranslation[2],
                };
            }

            p_Writer.WriteLine($"ARMSPOSE: {p_WeaponMesh}: {s_Named} bone(s) of {s_Skeleton.BoneNames.Count} named by '{s_AnimName}' ({s_PackageName}); " +
                               $"every bone written into '{s_Skeleton.WeaponBoneName}' space.");
            if (p_Destination.Directory != null && !p_Destination.Directory.Exists)
                p_Destination.Directory.Create();
            File.WriteAllText(p_Destination.FullName, JsonConvert.SerializeObject(s_File, Formatting.Indented));
            return s_File.Bones.Count;
        }

        /// <summary>
        /// A weapon's idle pose as named DOF values ('Bone.q' x,y,z,w; 'Bone.t'), from its own animation package: the blueprint beside
        /// the mesh names the package; the package's chunk holds the 'IdlePose Anim' on the 1P_Upperbody_DOF rig of the static bank.
        /// </summary>
        private static (Dictionary<string, float[]> Values, string Package, string Anim) IdleValues(IEngineMounter p_Mounter, string p_Mesh,
            TextWriter p_Writer)
        {
            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(p_Mounter.GetEngineType());

            // 1. The weapon's animation package, through the blueprint that lives beside the mesh: the first
            //    SoldierWeaponData under the mesh's folder that names one.
            var s_Slash = p_Mesh.IndexOf('/', p_Mesh.IndexOf('/') + 1);
            if (s_Slash < 0)
                throw new Exception($"'{p_Mesh}' is not a weapons/<folder>/<mesh> name.");

            var s_Folder = p_Mesh[..(s_Slash + 1)];
            GUID? s_PackageGuid = null;
            string? s_Blueprint = null;
            foreach (var s_Name in p_Mounter.GetPartitions().Keys
                         .Where(p_N => p_N.StartsWith(s_Folder, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(p_N => p_N.Length))
            {
                if (s_Name.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase) ||
                    !p_Mounter.TryGetPartition(s_Name, out var s_Mounted))
                    continue;

                RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition? s_Partition;
                try
                {
                    s_Partition = s_Converter.FromPartitionObject(s_Name, s_Mounted.FirstVariant)
                        as RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition;
                }
                catch (Exception)
                {
                    continue;
                }

                var s_Weapon = s_Partition?.InstanceMap.Values.OfType<fb.SoldierWeaponData>()
                    .FirstOrDefault(p_W => p_W.AnimationData.PartitionGuid != GUID.Empty);
                if (s_Weapon == null)
                    continue;

                s_PackageGuid = s_Weapon.AnimationData.PartitionGuid;
                s_Blueprint = s_Name;
                break;
            }

            if (s_PackageGuid == null || !p_Mounter.TryGetPartitionByGuid(s_PackageGuid, out var s_PackageName, out var s_PackageMounted))
                throw new Exception($"no weapon under '{s_Folder}' names an animation package.");

            var s_Package = (s_Converter.FromPartitionObject(s_PackageName, s_PackageMounted.FirstVariant)
                    as RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition)?
                .InstanceMap.Values.OfType<fb.AntPackageAsset>().FirstOrDefault();
            if (s_Package == null)
                throw new Exception($"'{s_PackageName}' holds no AntPackageAsset.");

            if (!p_Mounter.TryGetChunk(s_Package.StreamingGuid, out var s_Chunk))
                throw new Exception($"the package chunk {s_Package.StreamingGuid} of '{s_PackageName}' is not mounted.");

            p_Writer.WriteLine($"WEAPONPOSE: blueprint={s_Blueprint} package={s_PackageName} chunk={s_Package.StreamingGuid}");

            // 2. The static bank (once per process) and the weapon's bank; the weapon's references resolve
            //    against the static bank's objects through the shared resolver.
            if (s_Static == null)
            {
                if (!p_Mounter.TryGetResource(c_StaticBank, out var s_StaticResource))
                    throw new Exception($"the static animation bank '{c_StaticBank}' is not mounted.");

                using var s_StaticReader = s_StaticResource.FirstVariant.GetReader();
                var s_StaticBytes = s_StaticReader.ReadBytes((int) s_StaticReader.Length);
                s_Static = new AssetBank();
                using var s_Reader = new RimeReader(new MemoryStream(s_StaticBytes));
                s_Static.Load(s_Reader, s_Static);
                p_Writer.WriteLine($"WEAPONPOSE: static bank loaded, {s_Static.Objects.Count} objects");
            }

            using var s_ChunkReader = s_Chunk.FirstVariant.GetReader();
            var s_ChunkBytes = s_ChunkReader.ReadBytes((int) s_ChunkReader.Length);
            var s_Bank = new AssetBank();
            using (var s_Reader = new RimeReader(new MemoryStream(s_ChunkBytes)))
                s_Bank.Load(s_Reader, s_Bank);

            // The idle pose is a one-frame animation: a FrameAnimationAsset on most weapons, a two-key
            // RawAnimationAsset on a few (the Type 88) — the same channel layout per key, the first key taken.
            AnimationAsset? s_Anim = s_Bank.Objects.OfType<FrameAnimationAsset>().FirstOrDefault(p_A => p_A.ObjectName == c_IdleAnim)
                                     ?? (AnimationAsset?) s_Bank.Objects.OfType<RawAnimationAsset>().FirstOrDefault(p_A => p_A.ObjectName == c_IdleAnim)
                                     ?? s_Bank.Objects.OfType<FrameAnimationAsset>().FirstOrDefault(p_A =>
                                         p_A.ObjectName.Contains("IdlePose", StringComparison.OrdinalIgnoreCase));
            if (s_Anim == null)
                throw new Exception($"'{s_PackageName}' holds no '{c_IdleAnim}' frame or raw animation " +
                                    $"({string.Join(", ", s_Bank.Objects.Select(p_O => $"{p_O.GetType().Name} '{p_O.ObjectName}'"))}).");

            var (s_QuatCount, s_Vec3Count, s_Data) = s_Anim switch
            {
                FrameAnimationAsset s_Frame => (s_Frame.QuatCount, s_Frame.Vec3Count, s_Frame.Data),
                RawAnimationAsset s_Raw => (s_Raw.QuatCount, s_Raw.Vec3Count, s_Raw.Data),
                _ => throw new Exception("unreachable"),
            };

            var s_Rig = s_Static.Objects.OfType<LayoutHierarchyAsset>().FirstOrDefault(p_R => p_R.ObjectName == c_UpperBodyRig)
                        ?? throw new Exception($"the static bank holds no '{c_UpperBodyRig}' rig.");
            var s_Dofs = Flatten(s_Rig);
            var s_Map = s_Anim.ChannelToDofAsset.Object
                        ?? throw new Exception($"the channel map of '{s_Anim.ObjectName}' did not resolve (static bank not loaded first?).");

            // 3. Channels -> named DOFs. Quaternions first, then vec3s, four floats each (the fourth of a
            //    vec3 is padding).
            var s_Values = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            var s_Channels = (int) (s_QuatCount + s_Vec3Count);
            for (int s_Channel = 0, s_At = 0; s_Channel < s_Channels && s_At + 4 <= s_Data.Count; s_Channel++, s_At += 4)
            {
                if (s_Channel >= s_Map.IndexData.Count)
                    break;

                var s_Dof = s_Map.IndexData[s_Channel];
                if (s_Dof >= s_Dofs.Count)
                    continue;

                s_Values[s_Dofs[s_Dof].Name] = new[] { s_Data[s_At], s_Data[s_At + 1], s_Data[s_At + 2], s_Data[s_At + 3] };
            }

            p_Writer.WriteLine($"WEAPONPOSE: anim='{s_Anim.ObjectName}' ({s_Anim.GetType().Name}) q={s_QuatCount} v={s_Vec3Count} " +
                               $"rig='{s_Rig.ObjectName}' dofs={s_Dofs.Count} mapped={s_Values.Count}");
            return (s_Values, s_PackageName, s_Anim.ObjectName);
        }

        /// <summary>
        /// Composes named local DOF values ('Bone.q' quaternion x,y,z,w; 'Bone.t' translation) down from the
        /// skeleton's weapon root (identity: the mesh keeps its own space) into the pose file's per-bone 3x4
        /// (rows right, up, forward, then translation with the rest position subtracted: what a skinned mesh's
        /// base pose takes). A bone the values do not name keeps its rest local transform; a bone outside the
        /// root's subtree stays. Validated by picture on the 1P assembly (M240, AS VAL, M416).
        /// </summary>
        internal static void ComposePose(fb.SkeletonAsset s_Skeleton, string p_Skeleton, Dictionary<string, float[]> s_Values, PoseFile s_File,
            TextWriter? p_Writer)
        {
            var s_Count = s_Skeleton.BoneNames.Count;
            var s_Root = s_Skeleton.BoneNames.FindIndex(p_N => p_N.Equals(s_Skeleton.WeaponBoneName, StringComparison.OrdinalIgnoreCase));
            if (s_Root < 0)
                throw new Exception($"'{p_Skeleton}' has no weapon bone '{s_Skeleton.WeaponBoneName}'.");

            // 5. Compose the pose down from the weapon root (identity: the mesh keeps its own space). A bone
            //    the pose does not name keeps its rest local transform; a bone outside the root's subtree stays.
            var s_Rotation = new float[s_Count][];
            var s_Translation = new float[s_Count][];
            var s_Posed = new bool[s_Count];

            for (var i = 0; i < s_Count; i++)
            {
                var s_Parent = i < s_Skeleton.Hierarchy.Count ? s_Skeleton.Hierarchy[i] : -1;
                var s_Name = s_Skeleton.BoneNames[i];

                if (i == s_Root || s_Parent < 0 || s_Parent >= i || !s_Posed[s_Parent] && s_Parent != s_Root)
                {
                    // The root itself, and anything not under it: identity (the parts of the arms, the camera).
                    s_Rotation[i] = Identity3();
                    s_Translation[i] = new float[3];
                    s_Posed[i] = i == s_Root;
                    continue;
                }

                var s_Local = s_Skeleton.LocalPose[i];
                float[] s_LocalRotation;
                float[] s_LocalTranslation;
                if (s_Values.TryGetValue(s_Name + ".q", out var s_Q))
                    s_LocalRotation = RowsOf(s_Q[0], s_Q[1], s_Q[2], s_Q[3]);
                else
                    s_LocalRotation = new[] { s_Local.right.x, s_Local.right.y, s_Local.right.z, s_Local.up.x, s_Local.up.y, s_Local.up.z, s_Local.forward.x, s_Local.forward.y, s_Local.forward.z };

                if (s_Values.TryGetValue(s_Name + ".t", out var s_T))
                    s_LocalTranslation = new[] { s_T[0], s_T[1], s_T[2] };
                else
                    s_LocalTranslation = new[] { s_Local.trans.x, s_Local.trans.y, s_Local.trans.z };

                // Row-vector composition: v' = (v·L + tL)·P + tP.
                s_Rotation[i] = Multiply(s_LocalRotation, s_Rotation[s_Parent]);
                s_Translation[i] = Add(Transform(s_LocalTranslation, s_Rotation[s_Parent]), s_Translation[s_Parent]);
                s_Posed[i] = true;

                var s_Rest = s_Skeleton.ModelPose[i].trans;
                var s_Offset = Transform(new[] { s_Rest.x, s_Rest.y, s_Rest.z }, s_Rotation[i]);
                s_File.Bones[s_Name] = new[]
                {
                    s_Rotation[i][0], s_Rotation[i][1], s_Rotation[i][2],
                    s_Rotation[i][3], s_Rotation[i][4], s_Rotation[i][5],
                    s_Rotation[i][6], s_Rotation[i][7], s_Rotation[i][8],
                    s_Translation[i][0] - s_Offset[0], s_Translation[i][1] - s_Offset[1], s_Translation[i][2] - s_Offset[2],
                };

                p_Writer?.WriteLine($"WEAPONPOSE: {s_Name,-20} rest=({s_Rest.x:0.###},{s_Rest.y:0.###},{s_Rest.z:0.###}) " +
                                    $"posed=({s_Translation[i][0]:0.###},{s_Translation[i][1]:0.###},{s_Translation[i][2]:0.###})" +
                                    $"{(s_Values.ContainsKey(s_Name + ".t") ? "" : " (rest local)")}" +
                                    $"{(s_Values.TryGetValue(s_Name + ".q", out var s_Rq) && System.Math.Abs(s_Rq[3]) < 0.9999 ? " rotated" : "")}");
            }
        }

        /// <summary>Reads a pose file back (the other half, used by the mesh dump).</summary>
        public static PoseFile Read(string p_Path) =>
            JsonConvert.DeserializeObject<PoseFile>(File.ReadAllText(p_Path))
            ?? throw new Exception($"'{p_Path}' is not a pose file.");

        /// <summary>
        /// The runtime DOF table of a rig: a dedup'd pre-order flatten of the hierarchy — each layout once,
        /// in first-visit order, its slots in stored order, then the children (the delta-trajectory layout
        /// contributes eight unnamed DOFs).
        /// </summary>
        private static List<LayoutEntry> Flatten(LayoutHierarchyAsset p_Rig)
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

        private static float[] Identity3() => new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        /// <summary>The rotation of a quaternion (x, y, z, w) as three row vectors: the images of the axes.</summary>
        private static float[] RowsOf(float p_X, float p_Y, float p_Z, float p_W)
        {
            var s_Length = MathF.Sqrt(p_X * p_X + p_Y * p_Y + p_Z * p_Z + p_W * p_W);
            if (s_Length > 1e-6f)
            {
                p_X /= s_Length;
                p_Y /= s_Length;
                p_Z /= s_Length;
                p_W /= s_Length;
            }

            return new[]
            {
                1 - 2 * (p_Y * p_Y + p_Z * p_Z), 2 * (p_X * p_Y + p_Z * p_W), 2 * (p_X * p_Z - p_Y * p_W),
                2 * (p_X * p_Y - p_Z * p_W), 1 - 2 * (p_X * p_X + p_Z * p_Z), 2 * (p_Y * p_Z + p_X * p_W),
                2 * (p_X * p_Z + p_Y * p_W), 2 * (p_Y * p_Z - p_X * p_W), 1 - 2 * (p_X * p_X + p_Y * p_Y),
            };
        }

        /// <summary>Row-vector product A·B of two 3x3 matrices stored row-major.</summary>
        private static float[] Multiply(float[] p_A, float[] p_B)
        {
            var s_Result = new float[9];
            for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                s_Result[r * 3 + c] = p_A[r * 3] * p_B[c] + p_A[r * 3 + 1] * p_B[3 + c] + p_A[r * 3 + 2] * p_B[6 + c];

            return s_Result;
        }

        /// <summary>v·M for a row vector and a row-major 3x3.</summary>
        private static float[] Transform(float[] p_V, float[] p_M) => new[]
        {
            p_V[0] * p_M[0] + p_V[1] * p_M[3] + p_V[2] * p_M[6],
            p_V[0] * p_M[1] + p_V[1] * p_M[4] + p_V[2] * p_M[7],
            p_V[0] * p_M[2] + p_V[1] * p_M[5] + p_V[2] * p_M[8],
        };

        private static float[] Add(float[] p_A, float[] p_B) => new[] { p_A[0] + p_B[0], p_A[1] + p_B[1], p_A[2] + p_B[2] };

        /// <summary>The transpose of a row-major 3x3 (a rotation's inverse).</summary>
        private static float[] Transpose(float[] p_M) => new[] { p_M[0], p_M[3], p_M[6], p_M[1], p_M[4], p_M[7], p_M[2], p_M[5], p_M[8] };
    }
}
