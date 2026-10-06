using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ant;
using Rimelib.Animation.Frostbite2_0.Frostbite;
using RimeLib.Content.Mounting;
using RimeLib.Frostbite.Core;
using RimeLib.Serialization;

namespace RimeLib.Cmd.Contexts
{
    /// <summary>
    /// The poses a HELD weapon's parts take, per weapon, baked for a Lua consumer.
    ///
    /// MEASURED (2026-09-17): a weapon's EBX 3P bone deltas (SoldierWeaponData.weaponStates[].mesh3pTransforms) are the
    /// weapon on its own: with a foregrip mounted the game moves the foregrip bone (Wep_Bipod1) elsewhere — the L85A2's
    /// EBX puts it at z 0.773 (the muzzle), the game draws the grip under the handguard at z 0.553. That pose is an
    /// animation on the 21-bone 'WeaponParts' rig: the weapon's own package holds 'FG_IdlePose Anim' (foregrip), and
    /// the shared MP bank holds, per weapon and unnamed, 'IK_NoAddon_StandPose Anim' (nothing mounted),
    /// 'IK_FG_StandPose Anim' (foregrip; equals the package's) and 'IK_Bipod_WeaponAssemblerPose Anim' (bipod, in the
    /// customization screen: the M240's bipod bone turned 90 degrees about X with the legs 30 degrees apart). The MP
    /// poses carry no weapon name: they are matched to a weapon by the translations of the bones a mount does not
    /// move (feed cover, trigger, slide, magazine, physics, sight).
    ///
    /// The output is a Lua module: weapon partition name (lower case) -> pose kind -> weapon-skeleton bone index
    /// (1-based, WeaponSke01 order) -> the 3x4 that a StaticModelEntityData.basePoseTransforms entry takes for that
    /// bone (rows right, up, forward, then the translation with the bone's rest subtracted), composed exactly as the
    /// 1P assembly composes its idle pose (WeaponPose, validated by picture).
    /// </summary>
    internal static class WeaponPartPoses
    {
        private const string c_MpBank = "Animations/AntAnimations/b_BasicAssetsMP";
        private const string c_Rig = "WeaponParts";
        private const string c_Skeleton = "animations/skeletons/weapon/weaponske01";
        private const string c_PackageFgPose = "FG_IdlePose Anim";
        private const string c_PackageIdle = "IdlePose Anim";
        private const string c_1PRig = "1P_Upperbody_DOF";

        private static readonly (string kind, string anim)[] s_MpKinds =
        {
            ("noaddon", "IK_NoAddon_StandPose Anim"),
            ("fg", "IK_FG_StandPose Anim"),
            ("bipod", "IK_Bipod_WeaponAssemblerPose Anim"),
        };

        /// <summary>
        /// The bones a mount does not move: the weapon's fingerprint among the unnamed MP poses (measured on the L85A2:
        /// the package's foregrip pose and the MP one agree on every bone but Wep_Aim and Wep_Extra3, which the MP
        /// stand poses set per state, so those two are left out).
        /// </summary>
        private static readonly string[] s_FingerprintDofs =
        {
            "Wep_Extra1.t", "Wep_Trigger.t", "Wep_Slide.t", "Wep_Grenade1.t", "Wep_Mag.t", "Wep_Mag_Ammo.t", "Wep_Physic1.t",
            "Wep_Physic2.t", "Wep_Physic3.t", "Wep_Extra2.t",
        };

        /// <summary>The bones the baked table carries: Wep_Bipod1..3 (foregrips and bipods hang there), 1-based skeleton indices.</summary>
        private static readonly string[] s_BakedBones = { "Wep_Bipod1", "Wep_Bipod2", "Wep_Bipod3" };

        private const float c_FingerprintTolerance = 0.04f;    // the M240's package pose and its MP poses differ by 5 mm on the magazine, the
                                                                // Steyr AUG's by 3.4 cm on the grenade bone
        private const float c_FingerprintMargin = 0.02f;        // the runner-up must be this much farther, or the match is ambiguous
        private const int c_AgreeingMin = 5;                    // second pass: at least this many fingerprint bones within c_AgreeingTolerance
        private const float c_AgreeingTolerance = 0.004f;

        private sealed class MpPose
        {
            public string Kind = "";
            public int Index;
            public Dictionary<string, float[]> Dofs = new();
        }

        /// <summary>Bakes every weapon's part poses into a Lua module; returns how many weapons got at least one pose.</summary>
        public static int Bake(IEngineMounter p_Mounter, FileInfo p_Destination, string? p_Only, TextWriter p_Writer)
        {
            var s_Converter = EngineInterfaceRegistry.Create<IPartitionConverter>(p_Mounter.GetEngineType());

            // 1. The rig, the skeleton, the shared MP poses.
            var s_Mp = AnimBank.Open(p_Mounter, c_MpBank, p_Writer);
            var s_Rig = AnimBank.FindRig(s_Mp, c_Rig) ?? throw new Exception($"no '{c_Rig}' rig in the static bank.");
            var s_1PRig = AnimBank.FindRig(s_Mp, c_1PRig) ?? throw new Exception($"no '{c_1PRig}' rig in the static bank.");

            if (!p_Mounter.TryGetPartition(c_Skeleton, out var s_SkeletonMounted))
                throw new Exception($"skeleton '{c_Skeleton}' is not mounted.");

            var s_Skeleton = (s_Converter.FromPartitionObject(c_Skeleton, s_SkeletonMounted.FirstVariant)
                    as RimeLib.Serialization.Frostbite2_0.Ebx.DatabasePartition)?
                .InstanceMap.Values.OfType<fb.SkeletonAsset>().FirstOrDefault()
                ?? throw new Exception($"'{c_Skeleton}' holds no SkeletonAsset.");

            var s_MpPoses = new List<MpPose>();
            foreach (var (s_Kind, s_AnimName) in s_MpKinds)
            {
                var s_Anims = AnimBank.FindAnims(s_Mp, s_AnimName, 21);
                for (var i = 0; i < s_Anims.Count; i++)
                    s_MpPoses.Add(new MpPose { Kind = s_Kind, Index = i, Dofs = AnimBank.ReadDofs(s_Anims[i], s_Rig, 0, null) });

                p_Writer.WriteLine($"PARTPOSES: MP bank: {s_Anims.Count} '{s_AnimName}' poses on '{c_Rig}'");
            }

            // 2. Every weapon: the partitions under weapons/ that hold a SoldierWeaponData with an animation package.
            var s_Names = p_Mounter.GetPartitions().Keys
                .Where(p_N => p_N.StartsWith("weapons/", StringComparison.OrdinalIgnoreCase) && p_N.Count(p_C => p_C == '/') == 2 &&
                              !p_N.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase))
                .Where(p_N => string.IsNullOrEmpty(p_Only) || p_N.Contains(p_Only, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p_N => p_N, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var s_Lua = new StringBuilder();
            s_Lua.AppendLine("-- The poses a held weapon's parts take, per weapon: what the game plays on the weapon-parts rig when a foregrip");
            s_Lua.AppendLine("-- ('fg'), a bipod ('bipod', as posed in the customization screen) or nothing ('noaddon') is mounted. Per weapon");
            s_Lua.AppendLine("-- (partition name, lower case) and pose kind, the bones Wep_Bipod1..3 (weapon skeleton indices 18..20) as the");
            s_Lua.AppendLine("-- 3x4 a StaticModelEntityData.basePoseTransforms entry takes: left/right (3), up (3), forward (3), translation");
            s_Lua.AppendLine("-- with the bone's rest subtracted (3). Generated by Rime (dump_weapon_part_poses) from the game's animation");
            s_Lua.AppendLine("-- banks; do not edit by hand.");
            s_Lua.AppendLine("return {");

            var s_Baked = 0;
            var s_Seen = 0;
            var s_Packages = new Dictionary<GUID, AssetBank>();

            foreach (var s_Name in s_Names)
            {
                if (!p_Mounter.TryGetPartition(s_Name, out var s_Mounted))
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

                s_Seen++;
                var s_PackageGuid = s_Weapon.AnimationData.PartitionGuid;

                if (!s_Packages.TryGetValue(s_PackageGuid, out var s_Bank))
                {
                    if (!p_Mounter.TryGetPartitionByGuid(s_PackageGuid, out var s_PackageName, out _))
                    {
                        p_Writer.WriteLine($"PARTPOSES: {s_Name}: its animation package {s_PackageGuid} is not mounted; skipped");
                        continue;
                    }

                    try
                    {
                        s_Bank = AnimBank.Open(p_Mounter, s_PackageName, TextWriter.Null);
                    }
                    catch (Exception s_Exception)
                    {
                        p_Writer.WriteLine($"PARTPOSES: {s_Name}: package '{s_PackageName}' did not open ({s_Exception.Message}); skipped");
                        continue;
                    }

                    s_Packages[s_PackageGuid] = s_Bank;
                }

                // 3. The package's own foregrip pose (weapon-parts rig) and its 1P idle (the fingerprint when there is no foregrip pose).
                var s_Poses = new Dictionary<string, Dictionary<string, float[]>>();
                var s_PackageFg = AnimBank.FindAnims(s_Bank, c_PackageFgPose, 21).FirstOrDefault();
                if (s_PackageFg != null)
                    s_Poses["fg"] = AnimBank.ReadDofs(s_PackageFg, s_Rig, 0, null);

                var s_Fingerprints = new List<(string source, Dictionary<string, float[]> dofs)>();
                if (s_Poses.TryGetValue("fg", out var s_FgDofs))
                    s_Fingerprints.Add(("parts pose", s_FgDofs));

                var s_Idle = AnimBank.FindAnims(s_Bank, c_PackageIdle, 96).FirstOrDefault()
                             ?? AnimBank.FindAnims(s_Bank, c_PackageIdle, 0).FirstOrDefault();
                if (s_Idle != null)
                {
                    try
                    {
                        s_Fingerprints.Add(("1P idle", AnimBank.ReadDofs(s_Idle, s_1PRig, 0, null)));
                    }
                    catch (Exception)
                    {
                    }
                }

                // 4. The MP poses of this weapon, by fingerprint.
                var s_Report = new List<string>();
                if (s_Fingerprints.Count > 0)
                {
                    foreach (var (s_Kind, _) in s_MpKinds)
                    {
                        // the nearest MP pose of this kind under either fingerprint; a runner-up too close = ambiguous (logged, nearest taken)
                        MpPose? s_Chosen = null;
                        var s_Best = float.MaxValue;
                        var s_RunnerUp = float.MaxValue;
                        var s_Source = "";
                        Dictionary<string, float[]>? s_Fingerprint = null;

                        foreach (var (l_Source, l_Dofs) in s_Fingerprints)
                        {
                            var l_Ranked = s_MpPoses.Where(p_P => p_P.Kind == s_Kind)
                                .Select(p_P => (pose: p_P, distance: Distance(l_Dofs, p_P.Dofs)))
                                .OrderBy(p_C => p_C.distance)
                                .ToList();

                            if (l_Ranked.Count > 0 && l_Ranked[0].distance < s_Best)
                            {
                                s_Chosen = l_Ranked[0].pose;
                                s_Best = l_Ranked[0].distance;
                                s_RunnerUp = l_Ranked.Count > 1 ? l_Ranked[1].distance : float.MaxValue;
                                s_Source = l_Source;
                                s_Fingerprint = l_Dofs;
                            }
                        }

                        if (s_Chosen == null || s_Fingerprint == null || s_Best > c_FingerprintTolerance)
                        {
                            // second pass: the pose most of whose fingerprint bones agree (a package parks the bones the weapon
                            // does not use far from where the MP poses keep them)
                            MpPose? s_ByCount = null;
                            var s_Count = 0;
                            var s_Second = 0;
                            var s_CountSource = "";

                            foreach (var (l_Source, l_Dofs) in s_Fingerprints)
                            {
                                var l_Ranked = s_MpPoses.Where(p_P => p_P.Kind == s_Kind)
                                    .Select(p_P => (pose: p_P, agreeing: Agreeing(l_Dofs, p_P.Dofs)))
                                    .OrderByDescending(p_C => p_C.agreeing)
                                    .ToList();

                                if (l_Ranked.Count > 0 && l_Ranked[0].agreeing > s_Count)
                                {
                                    s_ByCount = l_Ranked[0].pose;
                                    s_Count = l_Ranked[0].agreeing;
                                    s_Second = l_Ranked.Count > 1 ? l_Ranked[1].agreeing : 0;
                                    s_CountSource = l_Source;
                                }
                            }

                            if (s_ByCount != null && s_Count >= c_AgreeingMin && s_Count - s_Second >= 2)
                            {
                                s_Report.Add($"{s_Kind}: MP #{s_ByCount.Index} by {s_Count} of {s_FingerprintDofs.Length} agreeing bones (runner-up {s_Second}, by the {s_CountSource}" +
                                             (s_Chosen != null && s_Fingerprint != null ? $"; the nearest by distance was #{s_Chosen.Index} at {s_Best:0.###} m on {FarthestDof(s_Fingerprint, s_Chosen.Dofs)})" : ")"));
                                s_Poses[s_Kind] = s_ByCount.Dofs;
                                continue;
                            }

                            s_Report.Add($"{s_Kind}: no MP pose within {c_FingerprintTolerance} m" +
                                         (s_Chosen != null && s_Fingerprint != null ? $" (nearest #{s_Chosen.Index} at {s_Best:0.###} m by the {s_Source} on {FarthestDof(s_Fingerprint, s_Chosen.Dofs)}" : "(") +
                                         (s_ByCount != null ? $"; most agreeing bones {s_Count} at #{s_ByCount.Index}, runner-up {s_Second})" : ")"));
                            continue;
                        }

                        if (s_RunnerUp - s_Best < c_FingerprintMargin)
                            s_Report.Add($"{s_Kind}: AMBIGUOUS, the runner-up is {s_RunnerUp:0.###} m against {s_Best:0.###} m (by the {s_Source}); the nearest taken");
                        if (s_Kind == "fg" && s_Poses.ContainsKey("fg"))
                        {
                            // the MP pose is the 3P one (the customization screen's); the package's is noted when it differs
                            var s_Agree = Distance(s_Poses["fg"], s_Chosen.Dofs, s_BakedBones.Select(p_B => p_B + ".t").ToArray());
                            s_Report.Add($"fg: MP #{s_Chosen.Index} (distance {s_Best:0.####} by the {s_Source}); the package's pose {(s_Agree <= 0.012f ? "agrees" : $"differs by {s_Agree:0.###} m on the bipod bones")}");
                        }
                        else
                        {
                            s_Report.Add($"{s_Kind}: MP #{s_Chosen.Index} (distance {s_Best:0.####} by the {s_Source})");
                        }

                        s_Poses[s_Kind] = s_Chosen.Dofs;
                    }
                }
                else
                {
                    s_Report.Add("no weapon-parts pose and no 1P idle in the package: MP poses not matched");
                }

                if (s_Poses.Count == 0)
                {
                    p_Writer.WriteLine($"PARTPOSES: {s_Name}: nothing to bake ({string.Join("; ", s_Report)})");
                    continue;
                }

                // 5. Compose and emit.
                s_Lua.AppendLine($"  [\"{s_Name.ToLowerInvariant()}\"] = {{");
                foreach (var s_Kind in new[] { "noaddon", "fg", "bipod" })
                {
                    if (!s_Poses.TryGetValue(s_Kind, out var s_Dofs))
                        continue;

                    var s_File = new WeaponPose.PoseFile { Skeleton = c_Skeleton, Bank = s_Name, Anim = s_Kind };
                    WeaponPose.ComposePose(s_Skeleton, c_Skeleton, s_Dofs, s_File, null);

                    s_Lua.Append($"    {s_Kind} = {{ ");
                    foreach (var s_Bone in s_BakedBones)
                    {
                        var s_Index = s_Skeleton.BoneNames.FindIndex(p_B => p_B.Equals(s_Bone, StringComparison.OrdinalIgnoreCase));
                        if (s_Index < 0 || !s_File.Bones.TryGetValue(s_Bone, out var s_M))
                            continue;

                        s_Lua.Append($"[{s_Index + 1}] = {{ {string.Join(", ", s_M.Select(p_V => p_V.ToString("0.#####", CultureInfo.InvariantCulture)))} }}, ");
                    }

                    s_Lua.AppendLine("},");

                    var s_B1 = s_Dofs.TryGetValue("Wep_Bipod1.t", out var s_T) ? $"({s_T[0]:0.###}, {s_T[1]:0.###}, {s_T[2]:0.###})" : "?";
                    var s_Q1 = s_Dofs.TryGetValue("Wep_Bipod1.q", out var s_Q) ? $"q({s_Q[0]:0.##}, {s_Q[1]:0.##}, {s_Q[2]:0.##}, {s_Q[3]:0.##})" : "";
                    s_Report.Add($"{s_Kind} Wep_Bipod1 {s_B1} {s_Q1}");
                }

                s_Lua.AppendLine("  },");
                s_Baked++;
                p_Writer.WriteLine($"PARTPOSES: {s_Name}: {string.Join("; ", s_Report)}");
            }

            s_Lua.AppendLine("}");

            if (p_Destination.Directory != null && !p_Destination.Directory.Exists)
                p_Destination.Directory.Create();

            // LF line ends: the mod emitter ships client scripts LF-normalized, and the Data copy is kept byte-identical to the shipped one
            File.WriteAllText(p_Destination.FullName, s_Lua.ToString().Replace("\r\n", "\n"), new UTF8Encoding(false));
            p_Writer.WriteLine($"PARTPOSES: {s_Seen} weapons seen, {s_Baked} baked -> {p_Destination.FullName}");
            return s_Baked;
        }

        /// <summary>How many fingerprint DOFs the two poses agree on (every component within c_AgreeingTolerance).</summary>
        private static int Agreeing(Dictionary<string, float[]> p_A, Dictionary<string, float[]> p_B)
        {
            var s_Count = 0;
            foreach (var s_Dof in s_FingerprintDofs)
            {
                if (!p_A.TryGetValue(s_Dof, out var s_VA) || !p_B.TryGetValue(s_Dof, out var s_VB))
                    continue;

                if (System.Math.Abs(s_VA[0] - s_VB[0]) <= c_AgreeingTolerance && System.Math.Abs(s_VA[1] - s_VB[1]) <= c_AgreeingTolerance &&
                    System.Math.Abs(s_VA[2] - s_VB[2]) <= c_AgreeingTolerance)
                    s_Count++;
            }

            return s_Count;
        }

        /// <summary>The fingerprint DOF that differs most between two poses (for the log).</summary>
        private static string FarthestDof(Dictionary<string, float[]> p_A, Dictionary<string, float[]> p_B)
        {
            var s_Name = "?";
            var s_Max = -1f;
            foreach (var s_Dof in s_FingerprintDofs)
            {
                if (!p_A.TryGetValue(s_Dof, out var s_VA) || !p_B.TryGetValue(s_Dof, out var s_VB))
                    continue;

                for (var i = 0; i < 3; i++)
                {
                    var s_D = System.Math.Abs(s_VA[i] - s_VB[i]);
                    if (s_D > s_Max)
                    {
                        s_Max = s_D;
                        s_Name = $"{s_Dof} ({s_VA[0]:0.###}, {s_VA[1]:0.###}, {s_VA[2]:0.###}) vs ({s_VB[0]:0.###}, {s_VB[1]:0.###}, {s_VB[2]:0.###})";
                    }
                }
            }

            return s_Name;
        }

        /// <summary>The largest per-component difference over the fingerprint DOFs both poses carry (float.MaxValue when none).</summary>
        private static float Distance(Dictionary<string, float[]> p_A, Dictionary<string, float[]> p_B, string[]? p_Dofs = null)
        {
            var s_Max = -1f;
            foreach (var s_Dof in p_Dofs ?? s_FingerprintDofs)
            {
                if (!p_A.TryGetValue(s_Dof, out var s_VA) || !p_B.TryGetValue(s_Dof, out var s_VB))
                    continue;

                for (var i = 0; i < 3; i++)
                    s_Max = System.Math.Max(s_Max, System.Math.Abs(s_VA[i] - s_VB[i]));
            }

            return s_Max < 0 ? float.MaxValue : s_Max;
        }
    }
}
