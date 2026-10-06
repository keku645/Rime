using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// A screen made from nothing: the game's smallest screen (ui/flow/screen/emptyscreen — a 1280x720 movie carrying the
    /// screen's ActionScript class registration, an InputListener and the root sprite "instance1"; a partition with the
    /// UIScreenAsset, that one WidgetNode and its ports) cloned under a fresh name and fresh guids. Every instance guid
    /// is remapped deterministically from the new partition name, so rebuilds are stable and two new screens never
    /// collide; references into the template partition follow the remap, references to other partitions stay.
    /// </summary>
    public static class NewScreen
    {
        public const string DefaultTemplate = "ui/flow/screen/emptyscreen";

        /// <summary>"MyScreen" → ui/flow/screen/myscreen (letters, digits and _ only).</summary>
        public static string PartitionName(string p_Name) => "ui/flow/screen/" + Clean(p_Name).ToLowerInvariant();

        /// <summary>The asset's own Name, cased as typed: UI/Flow/Screen/MyScreen.</summary>
        public static string AssetName(string p_Name) => "UI/Flow/Screen/" + Clean(p_Name);

        public static string MovieName(string p_Name) => "ui/assets/" + Clean(p_Name).ToLowerInvariant();

        public static string Clean(string p_Name)
        {
            var s = new string((p_Name ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            return s.Length == 0 ? "NewScreen" : s;
        }

        /// <summary>A version-4-shaped guid derived from the parts (MD5), upper-case dashed as the dumps spell them.</summary>
        public static string StableGuid(params string[] p_Parts)
        {
            var s_Hash = MD5.HashData(Encoding.UTF8.GetBytes(string.Join("|", p_Parts)));
            s_Hash[6] = (byte)((s_Hash[6] & 0x0F) | 0x40);
            s_Hash[8] = (byte)((s_Hash[8] & 0x3F) | 0x80);
            return new Guid(s_Hash).ToString().ToLowerInvariant();
        }

        public static string PartitionGuid(string p_Partition) => StableGuid(p_Partition, "partition");

        /// <summary>
        /// The new screen's partition in the dump's JSON shape, from the template's dump: PartitionGuid and every
        /// InstanceGuid remapped, in-partition references (ParentGraph, ports, bindings, Nodes) following, the asset's
        /// Name set to the new one. The result is what add_json_partition ships and what the editor opens.
        /// </summary>
        public static JObject PartitionJson(string p_Partition, string p_Name, JObject p_Template)
        {
            var s_OldPartition = (string)p_Template["PartitionGuid"]!;
            var s_NewPartition = PartitionGuid(p_Partition);
            var s_Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in ((JObject)p_Template["Instances"]!).Properties()) s_Map[p.Name] = StableGuid(p_Partition, "instance", p.Name);
            string Remap(string g) => s_Map.TryGetValue(g, out var n) ? n : g;
            JToken Walk(JToken t)
            {
                switch (t)
                {
                    case JObject o:
                    {
                        var r = new JObject();
                        var s_IsRef = o["PartitionGuid"] != null && o["InstanceGuid"] != null && o.Count == 2;
                        foreach (var p in o.Properties())
                        {
                            if (s_IsRef && p.Name == "PartitionGuid" && string.Equals((string?)p.Value, s_OldPartition, StringComparison.OrdinalIgnoreCase)) r[p.Name] = s_NewPartition;
                            else if (s_IsRef && p.Name == "InstanceGuid" && string.Equals((string?)o["PartitionGuid"], s_OldPartition, StringComparison.OrdinalIgnoreCase)) r[p.Name] = Remap((string)p.Value!);
                            else r[p.Name] = Walk(p.Value);
                        }
                        return r;
                    }
                    case JArray a: return new JArray(a.Select(Walk));
                    default: return t.DeepClone();
                }
            }
            var s_Instances = new JObject();
            foreach (var p in ((JObject)p_Template["Instances"]!).Properties()) s_Instances[Remap(p.Name)] = Walk(p.Value);
            var s_Primary = Remap((string)p_Template["PrimaryInstanceGuid"]!);
            ((JObject)s_Instances[s_Primary]!)["Name"] = AssetName(p_Name);
            return new JObject
            {
                ["PrimaryInstanceGuid"] = s_Primary,
                ["Instances"] = s_Instances,
                ["Name"] = p_Partition,
                ["PartitionGuid"] = s_NewPartition,
            };
        }
    }
}
