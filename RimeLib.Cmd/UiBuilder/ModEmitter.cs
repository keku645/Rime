using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.Scaleform;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// Turns a ScreenDocument into a complete VU mod folder: mod.json, ext/Shared (delivery), ext/Client
    /// (generated graph edits), src/ (edited movies + the Rime recipe that builds the superbundle).
    /// Guids for everything created at runtime are derived deterministically from (mod, screen, node, role),
    /// so rebuilding the same document yields the same mod and two mods never collide.
    /// </summary>
    public class ModEmitter
    {
        readonly ScreenDocument m_Doc;
        readonly string m_DocDir;
        readonly string m_ModDir;
        readonly Func<string, JObject> m_PartitionJson;   // partition name -> dump_partition_json content
        readonly Func<string, byte[]> m_ResourceBytes;    // resource name -> bytes of its first variant
        readonly TextWriter m_Log;
        readonly string m_WorkDir;                        // the build's intermediates (built movies, new partitions, the recipe): NOT part of the mod
        readonly List<string> m_Recipe = new();           // the build steps as they run

        public List<(string Resource, string File)> Movies { get; } = new();

        readonly Func<string, string?>? m_PartitionNameByGuid;   // static delivery: the partitions a shipped screen imports, by guid

        /// <param name="p_WorkDir">Where the built movies, the new screens' partitions and the recipe are written — the files the
        /// superbundle is built FROM. Outside the mod folder: the mod ships mod.json, ext/ and sb/ only. Null = a folder under %TEMP%.</param>
        /// <param name="p_PartitionNameByGuid">Static delivery needs the NAME of every partition a shipped screen imports (its widget assets,
        /// data components, audio mapping): copies of them ride in the bundle in front of the screen so its imports resolve.</param>
        public ModEmitter(ScreenDocument p_Doc, string p_DocDir, string p_ModsRoot,
                          Func<string, JObject> p_PartitionJson, Func<string, byte[]> p_ResourceBytes, TextWriter p_Log, string? p_WorkDir = null,
                          Func<string, string?>? p_PartitionNameByGuid = null)
        {
            m_Doc = p_Doc;
            m_DocDir = p_DocDir;
            // merged: the files go into the HOST mod's folder (see ScreenDocument.MergeInto); the document's own name
            // still names the superbundle, the work folder and the pictures, so nothing else moves
            m_ModDir = Path.Combine(p_ModsRoot, p_Doc.IsMerged ? p_Doc.MergeInto.Trim() : p_Doc.Name);
            m_PartitionJson = p_PartitionJson;
            m_ResourceBytes = p_ResourceBytes;
            m_Log = p_Log;
            m_WorkDir = p_WorkDir ?? Path.Combine(Path.GetTempPath(), "rime_ui_build", p_Doc.Name);
            m_PartitionNameByGuid = p_PartitionNameByGuid;
        }

        public string ModDir => m_ModDir;
        public string SbDir => Path.Combine(m_ModDir, "sb");
        /// <summary>The build's intermediates (what the .sb was built from); not shipped.</summary>
        public string WorkDir => m_WorkDir;
        public string RecipePath => Path.Combine(m_WorkDir, "rime_build.txt");

        /// <summary>
        /// What the mod does, from the document — the default mod.json description (a description must state what the files
        /// actually do: which shipped screens are edited, which screens are new, how it is delivered).
        /// </summary>
        public static string DescribeDocument(ScreenDocument p_Doc)
        {
            static string Short(ScreenEntry s) => s.Partition.Split('/').Last();
            var s_Edited = p_Doc.Screens.Where(s => !s.New && (s.Nodes.Count > 0 || s.Stage.Count > 0 || s.Fields.Count > 0 || s.RemovedConnections.Count > 0)).Select(Short).ToList();
            var s_Added = p_Doc.Screens.Where(s => s.New).Select(Short).ToList();
            var s_Text = new StringBuilder("UI mod made with the Rime UI editor.");
            if (s_Edited.Count > 0) s_Text.Append($" Edits {s_Edited.Count} shipped screen(s): {string.Join(", ", s_Edited)}.");
            if (s_Added.Count > 0) s_Text.Append($" Adds {s_Added.Count} new screen(s): {string.Join(", ", s_Added)}.");
            if (p_Doc.Images.Count > 0) s_Text.Append($" Ships {p_Doc.Images.Count} picture(s) as UI textures (UI/Art/{p_Doc.ImageFolder}/*).");
            s_Text.Append(p_Doc.Images.Count > 0
                ? $" Ships two superbundles (Win32/{p_Doc.Superbundle}, Win32/{p_Doc.ChunksSuperbundle}) mounted on every level; the screen graphs are edited by VeniceEXT as they load."
                : $" Ships one superbundle (Win32/{p_Doc.Superbundle}) mounted on every level; the screen graphs are edited by VeniceEXT as they load.");
            return s_Text.ToString();
        }

        /// <summary>The guid everything the mod creates is born with, from (mod, screen, node, role): the same for a rebuild, never shared by two mods.</summary>
        public static string StableGuid(params string[] p_Parts)
        {
            var s_Hash = MD5.HashData(Encoding.UTF8.GetBytes(string.Join("|", p_Parts)));
            s_Hash[6] = (byte)((s_Hash[6] & 0x0F) | 0x40); // version 4 shape, so it can never collide with shipped v1 guids
            s_Hash[8] = (byte)((s_Hash[8] & 0x3F) | 0x80);
            return new Guid(s_Hash).ToString().ToUpperInvariant();
        }

        static string LuaString(string p_Value) => "\"" + p_Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>The movies and partitions a new screen adds to the bundle (resource / partition name, file).</summary>
        public List<(string Resource, string File)> NewMovies { get; } = new();
        public List<(string Partition, string File)> NewPartitions { get; } = new();
        /// <summary>Static delivery: the edited partitions of shipped screens the bundle carries under their own names (partition name, file).</summary>
        public List<(string Partition, string File)> StaticPartitions { get; } = new();
        /// <summary>
        /// Static delivery: vanilla copies of every partition the shipped partitions import (transitively, under ui/), in dependency
        /// order, placed in the bundle BEFORE them. Measured in game (2026-09-16): a partition loaded from our bundle links its imports
        /// against what is loaded at that moment and never later — the screen's own widgets vanished because their assets were
        /// still in the game's later bundle; with the copies in front, every import resolves against ours and the game's copies are
        /// skipped as already loaded.
        /// </summary>
        public List<(string Partition, string File)> DependencyPartitions { get; } = new();
        /// <summary>The document's pictures as converted for the bundle (see ImageTextures).</summary>
        public List<ImageTextures.Prepared> Pictures { get; } = new();

        /// <summary>
        /// A recorder riding inside this mod's own build (the editor's Record data…, but on the mod as it ships): Inject puts it on a
        /// screen movie after the document's ops (movie, screen name); ClientLua is appended to ext/Client (the receiver), ServerLua is
        /// ext/Server (keeps the records); Note goes into mod.json's description. So what the game hands the EDITED screen — its new
        /// nodes included — is recorded, with nothing else installed.
        /// </summary>
        public sealed record Recorder(Action<GfxMovie, string> Inject, string ClientLua, string ServerLua, string Note);
        public Recorder? Record { get; set; }
        /// <summary>The direct imports of each static screen (name, partition guid, primary instance guid) — the client's probe reports which of them were loaded.</summary>
        public Dictionary<string, List<(string Name, string Partition, string Instance)>> StaticImports { get; } = new(StringComparer.OrdinalIgnoreCase);

        readonly Dictionary<string, JObject> m_NewJson = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A flow graph entry (a UIGraphAsset: StateNodes/DialogNodes chaining screens, no stage) as opposed to a screen.</summary>
        bool IsGraphAsset(ScreenEntry p_Screen)
        {
            try
            {
                var s_Json = JsonOf(p_Screen.Partition);
                return (s_Json["Instances"] as JObject)?[(string)s_Json["PrimaryInstanceGuid"]!]?["$type"]?.ToString() == "UIGraphAsset";
            }
            catch { return false; }
        }

        /// <summary>Whether an entry ships as a static partition: its own delivery when it says, the document's otherwise.</summary>
        bool IsStatic(ScreenEntry p_Screen) => p_Screen.Delivery != null ? string.Equals(p_Screen.Delivery, "static", StringComparison.OrdinalIgnoreCase) : m_Doc.IsStatic;

        /// <summary>A new screen's partition JSON (cloned from its template under its own guids), built once per emit.</summary>
        JObject NewScreenJson(ScreenEntry p_Screen)
        {
            if (m_NewJson.TryGetValue(p_Screen.Partition, out var s_Json)) return s_Json;
            var s_Name = p_Screen.Title ?? p_Screen.Partition.Split('/').Last();
            s_Json = NewScreen.PartitionJson(p_Screen.Partition, s_Name, m_PartitionJson(p_Screen.Template));
            m_NewJson[p_Screen.Partition] = s_Json;
            return s_Json;
        }

        /// <summary>The partition's JSON: the game's dump, or the generated one for a screen the document creates.</summary>
        JObject JsonOf(string p_PartitionName)
        {
            var s_New = m_Doc.Screens.FirstOrDefault(s => s.New && string.Equals(s.Partition, p_PartitionName, StringComparison.OrdinalIgnoreCase));
            return s_New != null ? NewScreenJson(s_New) : m_PartitionJson(p_PartitionName);
        }

        (string Partition, string Instance) Guids(string p_PartitionName)
        {
            var s_Json = JsonOf(p_PartitionName);
            return ((string)s_Json["PartitionGuid"]!, (string)s_Json["PrimaryInstanceGuid"]!);
        }

        public void Emit()
        {
            Directory.CreateDirectory(Path.Combine(m_ModDir, "ext", "Shared"));
            Directory.CreateDirectory(Path.Combine(m_ModDir, "ext", "Client"));
            Directory.CreateDirectory(m_WorkDir);
            Directory.CreateDirectory(SbDir);
            // a mod built before this kept its intermediates in src/: gone now, the mod is mod.json + ext/ + sb/
            // ⛔ never in a merged build: that folder would be the HOST's, and nothing of the host is ours to delete
            var s_OldSrc = Path.Combine(m_ModDir, "src");
            if (!m_Doc.IsMerged && Directory.Exists(s_OldSrc)) { Directory.Delete(s_OldSrc, true); m_Log.WriteLine("removed the old src/ folder of the mod (build files now live outside the mod)"); }
            if (m_Doc.IsMerged)
            {
                if (!File.Exists(Path.Combine(m_ModDir, "mod.json")))
                    throw new DirectoryNotFoundException($"mergeInto '{m_Doc.MergeInto}': no mod.json in {m_ModDir} — the host mod has to exist already");
                // ⛔ THE HOST'S TWO REQUIRE LINES ARE PRINTED, AND THEY ARE NOT SYMMETRIC: a CLIENT module is required by
                // its bare name, a SHARED one needs the `__shared/` prefix EVEN FROM A SHARED SCRIPT (VU's own guide says
                // so, and without it the engine looks in ext/client or ext/server and this build mounts nothing —
                // in silence). Printing them is what keeps that from being re-derived wrong.
                m_Log.WriteLine($"MERGED BUILD: the files go into '{m_Doc.MergeInto}' — ext/Client/{m_Doc.ClientModule}.lua and " +
                                $"ext/Shared/{m_Doc.SharedModule}.lua, mod.json merged, nothing of the host deleted. " +
                                "Its own init files must carry, once: " +
                                $"ext/Client/__init__.lua -> require('{m_Doc.ClientModule}')  |  " +
                                $"ext/Shared/__init__.lua -> require('__shared/{m_Doc.SharedModule}')");
            }
            m_Log.WriteLine($"build files (not part of the mod): {m_WorkDir}");

            // ---- movies: vanilla screen movie + stage ops -> <work>/<name>.gfx. A NEW screen starts from its template's movie and
            // is always written (even without ops), with its partition (the template's, under fresh guids and the new name) next to it
            var s_NewApplied = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var s_Screen in m_Doc.Screens)
            {
                var s_MovieName = s_Screen.Movie ?? ("ui/assets/" + s_Screen.Partition.Split('/').Last());
                if (s_Screen.Stage.Count == 0 && !s_Screen.New && Record == null)   // with a recorder every screen of the document ships its movie (the recorder rides in it)
                    continue;
                // a flow graph (UIGraphAsset) has no movie: nothing to ship or record for it
                if (s_Screen.Stage.Count == 0 && !s_Screen.New && IsGraphAsset(s_Screen)) continue;
                var s_Source = s_Screen.New ? "ui/assets/" + s_Screen.Template.Split('/').Last() : s_MovieName;
                var s_Movie = GfxMovie.Load(m_ResourceBytes(s_Source));
                foreach (var s_Op in s_Screen.Stage)
                    ApplyOp(s_Movie, s_Op);
                // the recorder, when one rides: after the document's own frame scripts, so its wrappers see the edited screen
                if (Record != null) { Record.Inject(s_Movie, s_MovieName.Split('/').Last()); m_Log.WriteLine($"movie {s_MovieName}: the recorder rides in front of its frame"); }
                var s_File = Path.Combine(m_WorkDir, s_MovieName.Split('/').Last() + ".gfx");
                s_Movie.Save(s_File);
                var s_Check = GfxMovie.Load(s_File);
                m_Log.WriteLine($"movie {s_MovieName}: {s_Screen.Stage.Count} stage op(s){(s_Screen.New ? $" on the template {s_Source}" : "")} -> {s_File} ({s_Check.ToPayload().Length} bytes)");
                m_Log.Write(s_Check.Describe());
                if (s_Screen.New)
                {
                    NewMovies.Add((s_MovieName, s_File));
                    // the clone of the template; with static delivery the document's graph edits are already in it
                    var s_Json = IsStatic(s_Screen) && s_Screen.HasGraphEdits ? StaticGraph.Apply(m_Doc, s_Screen, NewScreenJson(s_Screen), JsonOf, m_Log) : NewScreenJson(s_Screen);
                    var s_JsonFile = Path.Combine(m_WorkDir, s_Screen.Partition.Split('/').Last() + ".partition.json");
                    File.WriteAllText(s_JsonFile, s_Json.ToString(Formatting.Indented));
                    NewPartitions.Add((s_Screen.Partition, s_JsonFile));
                    s_NewApplied[s_Screen.Partition] = s_Json;   // what the bundle carries: its imports are the ones to copy (the widgets the document dropped on it)
                    m_Log.WriteLine($"new screen {s_Screen.Partition}: partition {s_Json["PartitionGuid"]} ({((JObject)s_Json["Instances"]!).Count} instances, cloned from {s_Screen.Template}) -> {s_JsonFile}");
                }
                else Movies.Add((s_MovieName, s_File));
            }
            // static delivery: a shipped screen with graph edits ships as a whole partition, the game's dump with the document applied,
            // under the screen's own name and guid — the bundle's copy stands in for the game's, no runtime edit needed
            if (m_Doc.IsStatic)
            {
                var s_Shipped = new List<(ScreenEntry Screen, JObject Json)>();
                foreach (var s_Screen in m_Doc.Screens.Where(s => !s.New && s.HasGraphEdits && IsStatic(s)))
                {
                    var s_Json = StaticGraph.Apply(m_Doc, s_Screen, m_PartitionJson(s_Screen.Partition), JsonOf, m_Log);
                    var s_JsonFile = Path.Combine(m_WorkDir, s_Screen.Partition.Split('/').Last() + ".partition.json");
                    File.WriteAllText(s_JsonFile, s_Json.ToString(Formatting.Indented));
                    StaticPartitions.Add((s_Screen.Partition, s_JsonFile));
                    s_Shipped.Add((s_Screen, s_Json));
                    m_Log.WriteLine($"static partition {s_Screen.Partition}: {((JObject)s_Json["Instances"]!).Count} instances -> {s_JsonFile}");
                }
                // a new screen's imports are read off the partition as SHIPPED (the document's nodes applied): the template clone alone names
                // only the template's — measured 2026-09-17: the camo screen's grid came up with NO widget asset (NumEvents 0, its events
                // never reaching the engine) because ui/assets/grid, which only the new screen imported, never travelled in the bundle
                foreach (var s_Screen in m_Doc.Screens.Where(s => s.New))
                    s_Shipped.Add((s_Screen, s_NewApplied.TryGetValue(s_Screen.Partition, out var s_Applied) ? s_Applied : NewScreenJson(s_Screen)));
                CollectDependencies(s_Shipped);
            }
            foreach (var s_Extra in m_Doc.Movies)
            {
                if (string.IsNullOrWhiteSpace(s_Extra.File))
                {
                    // no file: the game's own movie of that resource, edited by the entry's stage ops (a widget's movie, not a screen's)
                    if (s_Extra.Stage.Count == 0) throw new Exception($"movie {s_Extra.Resource}: neither a file nor stage ops");
                    var s_Movie = GfxMovie.Load(m_ResourceBytes(s_Extra.Resource));
                    foreach (var s_Op in s_Extra.Stage)
                        ApplyOp(s_Movie, s_Op);
                    var s_Out = Path.Combine(m_WorkDir, s_Extra.Resource.Split('/').Last() + ".gfx");
                    s_Movie.Save(s_Out);
                    var s_Reread = GfxMovie.Load(s_Out);
                    Movies.Add((s_Extra.Resource, s_Out));
                    m_Log.WriteLine($"movie {s_Extra.Resource}: {s_Extra.Stage.Count} stage op(s) on the game's own -> {s_Out} ({s_Reread.ToPayload().Length} bytes)");
                    m_Log.Write(s_Reread.Describe());
                    continue;
                }
                var s_Src = Path.IsPathRooted(s_Extra.File) ? s_Extra.File : Path.Combine(m_DocDir, s_Extra.File);
                var s_Dst = Path.Combine(m_WorkDir, Path.GetFileName(s_Src));
                if (Path.GetFullPath(s_Src) != Path.GetFullPath(s_Dst))
                    File.Copy(s_Src, s_Dst, true);
                var s_Shipped = GfxMovie.Load(s_Dst); // must parse
                if (s_Extra.Stage.Count > 0)
                {
                    foreach (var s_Op in s_Extra.Stage)
                        ApplyOp(s_Shipped, s_Op);
                    s_Shipped.Save(s_Dst);
                    GfxMovie.Load(s_Dst);
                }
                Movies.Add((s_Extra.Resource, s_Dst));
                m_Log.WriteLine($"movie {s_Extra.Resource}: " + (s_Extra.Stage.Count > 0 ? $"{s_Extra.Stage.Count} stage op(s) on {s_Src}" : $"shipped as-is from {s_Src}"));
            }

            // ---- pictures: each a texture of the game (DXT5 header resource + partition in the bundle, pixels in the chunk store)
            Pictures.Clear();
            Pictures.AddRange(ImageTextures.Prepare(m_Doc, m_DocDir, m_WorkDir, m_Log));

            // ---- ext/Client data table
            var s_Lua = new StringBuilder();
            s_Lua.AppendLine("local SCREENS = {");
            foreach (var s_Screen in m_Doc.Screens)
            {
                var (s_PartGuid, s_InstGuid) = Guids(s_Screen.Partition);
                // a screen (UIScreenAsset, has a stage) or a flow graph (UIGraphAsset: StateNodes/DialogNodes chaining screens, no stage)
                var s_ScreenJson = JsonOf(s_Screen.Partition);
                var s_AssetType = (s_ScreenJson["Instances"] as JObject)?[s_InstGuid]?["$type"]?.ToString() ?? "UIScreenAsset";
                var s_IsScreen = s_AssetType != "UIGraphAsset";
                // every partition the screen's nodes reference: widgets, data categories, assets in typed fields
                var s_Assets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in s_Screen.Nodes)
                {
                    if (n.Widget != null) s_Assets.Add(n.Widget);
                    if (n.Binding?.Category != null) s_Assets.Add(n.Binding.Category);
                    CollectRefs(n.Type, n.Fields, s_Assets);
                }
                CollectRefs(s_AssetType, s_Screen.Fields, s_Assets);
                s_Lua.AppendLine("\t{");
                s_Lua.AppendLine($"\t\tname = {LuaString(s_Screen.Partition)}, isScreen = {(s_IsScreen ? "true" : "false")},");
                s_Lua.AppendLine($"\t\tpartition = Guid(\"{s_PartGuid.ToUpperInvariant()}\"), instance = Guid(\"{s_InstGuid.ToUpperInvariant()}\"),");
                // static delivery: the partition in the bundle already carries every edit; the client only reports what loaded — our nodes,
                // and whether each import of the screen (widget assets, data components) was loaded at that moment
                if (IsStatic(s_Screen) != m_Doc.IsStatic) m_Log.WriteLine($"{s_Screen.Partition}: delivered {(IsStatic(s_Screen) ? "static" : "live (Lua edits the game's partition at load)")}, unlike the document's other screens");
                s_Lua.AppendLine($"\t\tstatic = {(IsStatic(s_Screen) ? "true" : "false")}, probe = {{ {string.Join(", ", s_Screen.Nodes.Select(n => LuaString(n.InstanceName)))} }},");
                s_Lua.AppendLine("\t\timports = { " + string.Join(", ", (StaticImports.TryGetValue(s_Screen.Partition, out var s_Imports) ? s_Imports : new List<(string, string, string)>())
                    .Where(i => i.Item3 != "").Select(i => $"{{ name = {LuaString(i.Item1)}, partition = Guid(\"{i.Item2.ToUpperInvariant()}\"), instance = Guid(\"{i.Item3.ToUpperInvariant()}\") }}")) + " },");
                // the asset's own fields (Modal, ProtectScreens…), typed by its type
                var s_AssetInfo = UiTypeCatalog.Describe(s_AssetType);
                s_Lua.AppendLine("\t\tfields = {");
                foreach (var (s_Name, s_Value) in s_Screen.Fields)
                {
                    var f = s_AssetInfo?.Field(s_Name);
                    if (f == null) m_Log.WriteLine($"WARNING: {s_AssetType} has no field '{s_Name}' (screen {s_Screen.Partition}) -- emitted by JSON kind");
                    if (s_Name is "Nodes" or "Connections") { m_Log.WriteLine($"WARNING: screen field '{s_Name}' is the graph itself: use nodes/connections"); continue; }
                    s_Lua.AppendLine("\t\t\t" + EmitField(f?.LuaName ?? UiTypeCatalog.LuaName(s_Name), s_Value, f, m_Doc.Name + "|" + s_Screen.Partition + "|screen|" + s_Name) + ",");
                }
                s_Lua.AppendLine("\t\t},");
                // shipped wires the document disconnects: erased from the asset's connections by instance guid (Unwire in the client, [UNWIRE] in its log)
                s_Lua.AppendLine("\t\tremovedConnections = {");
                foreach (var r in s_Screen.RemovedConnections)
                {
                    if (!Guid.TryParse(r.Guid, out var s_ConnGuid)) { m_Log.WriteLine($"WARNING: removed connection {r.From} -> {r.To} of {s_Screen.Partition}: '{r.Guid}' is not a guid -- skipped"); continue; }
                    s_Lua.AppendLine($"\t\t\t{{ guid = \"{s_ConnGuid.ToString("D").ToUpperInvariant()}\", from = {LuaString(r.From)}, to = {LuaString(r.To)} }},");
                }
                s_Lua.AppendLine("\t\t},");
                s_Lua.AppendLine("\t\tassets = {");
                foreach (var s_Asset in s_Assets)
                {
                    var (s_WPart, s_WInst) = Guids(s_Asset);
                    s_Lua.AppendLine($"\t\t\t[{LuaString(s_Asset)}] = {{ partition = Guid(\"{s_WPart.ToUpperInvariant()}\"), instance = Guid(\"{s_WInst.ToUpperInvariant()}\") }},");
                }
                s_Lua.AppendLine("\t\t},");
                s_Lua.AppendLine("\t\tnodes = {");
                foreach (var s_Node in s_Screen.Nodes)
                {
                    var s_Key = m_Doc.Name + "|" + s_Screen.Partition + "|" + s_Node.InstanceName;
                    var s_Type = string.IsNullOrEmpty(s_Node.Type) ? "WidgetNode" : s_Node.Type;
                    var s_Info = UiTypeCatalog.Describe(s_Type);
                    if (s_Info == null || !s_Info.IsNode) m_Log.WriteLine($"WARNING: node {s_Node.InstanceName}: '{s_Type}' is not a known UI node type");
                    s_Lua.AppendLine("\t\t\t{");
                    s_Lua.AppendLine($"\t\t\t\tinstanceName = {LuaString(s_Node.InstanceName)}, new = {(s_Node.New ? "true" : "false")}, type = {LuaString(s_Type)},");
                    s_Lua.AppendLine($"\t\t\t\twidget = {(s_Node.Widget == null ? "nil" : LuaString(s_Node.Widget))}, focusIndex = {s_Node.FocusIndex}, zDepthLevel = {s_Node.ZDepthLevel},");
                    s_Lua.AppendLine($"\t\t\t\tguid = \"{StableGuid(s_Key, "node")}\", bindingGuid = \"{StableGuid(s_Key, "binding")}\",");
                    s_Lua.AppendLine($"\t\t\t\treplaceProperties = {(s_Node.ReplaceProperties ? "true" : "false")},");
                    s_Lua.AppendLine("\t\t\t\tproperties = { " + string.Join(", ", s_Node.Properties.Select(kv => $"{{ {LuaString(kv.Key)}, {LuaString(kv.Value)} }}")) + " },");
                    if (s_Node.Binding != null)
                    {
                        var b = s_Node.Binding;
                        s_Lua.AppendLine($"\t\t\t\tbinding = {{ dataName = {LuaString(b.DataName)}, dataKey = {b.DataKey.ToString(CultureInfo.InvariantCulture)}, " +
                                         $"categoryFromNode = {(b.CategoryFromNode == null ? "nil" : LuaString(b.CategoryFromNode))}, category = {(b.Category == null ? "nil" : LuaString(b.Category))}, " +
                                         $"useDirectAccess = {(b.UseDirectAccess ? "true" : "false")}, updateOnInitialize = {(b.UpdateOnInitialize ? "true" : "false")} }},");
                    }
                    // typed fields (any node type)
                    s_Lua.AppendLine("\t\t\t\tfields = {");
                    foreach (var (s_Name, s_Value) in s_Node.Fields)
                    {
                        var f = s_Info?.Field(s_Name);
                        if (f == null) { m_Log.WriteLine($"WARNING: {s_Type} has no field '{s_Name}' (node {s_Node.InstanceName}) -- emitted by JSON kind"); }
                        if (f != null && UiTypeCatalog.IsPortReference(s_Type, s_Name))
                        {
                            // "Node.port": resolved at runtime to that node's port (JumpNode.TargetPort)
                            var s_Spec = (string?)s_Value ?? "";
                            var s_Dot = s_Spec.IndexOf('.');
                            if (s_Dot <= 0) { if (s_Spec != "") m_Log.WriteLine($"WARNING: {s_Node.InstanceName}.{s_Name} = '{s_Spec}' is not Node.port"); continue; }
                            s_Lua.AppendLine($"\t\t\t\t\t{{ name = {LuaString(f.LuaName)}, kind = \"portref\", node = {LuaString(s_Spec[..s_Dot])}, port = {LuaString(PortSpec(s_Spec[(s_Dot + 1)..]))} }},");
                            continue;
                        }
                        if (f != null && (f.Kind == UiTypeCatalog.Kind.Port || f.Kind == UiTypeCatalog.Kind.PortArray)) { m_Log.WriteLine($"WARNING: {s_Node.InstanceName}.{s_Name} is a port: use 'ports'/'connections'"); continue; }
                        s_Lua.AppendLine("\t\t\t\t\t" + EmitField(f?.LuaName ?? UiTypeCatalog.LuaName(s_Name), s_Value, f, s_Key + "|" + s_Name) + ",");
                    }
                    s_Lua.AppendLine("\t\t\t\t},");
                    // the single-port fields the type owns (created for new nodes) and the named array ports asked for
                    s_Lua.AppendLine("\t\t\t\tsinglePorts = { " + string.Join(", ", (s_Info?.OwnPorts ?? Enumerable.Empty<UiTypeCatalog.FieldInfo>())
                        .Select(p => $"{{ field = {LuaString(p.LuaName)}, name = {LuaString(p.Name)}, guid = \"{StableGuid(s_Key, "port", p.Name)}\" }}")) + " },");
                    s_Lua.AppendLine("\t\t\t\tports = { " + string.Join(", ", s_Node.Ports.Select(p =>
                        $"{{ field = {LuaString(UiTypeCatalog.LuaName(p.Field))}, name = {LuaString(p.Name)}, instanceName = {(p.InstanceName == null ? "nil" : LuaString(p.InstanceName))}, event = {(p.Event == null ? "nil" : LuaString(p.Event))}, inputEvent = {(p.InputEvent == null ? "nil" : LuaString(p.InputEvent))}, guid = \"{StableGuid(s_Key, "aport", p.Field, p.Name)}\" }}")) + " },");
                    s_Lua.AppendLine("\t\t\t\tconnections = {");
                    foreach (var c in s_Node.Connections)
                    {
                        var s_From = c.Event ?? c.FromPort ?? "Out";
                        var s_To = c.ToEvent ?? c.ToPort ?? c.ToField ?? "inValue";
                        var s_CKey = s_Key + "|" + s_From + ">" + c.ToNode + "." + s_To;
                        s_Lua.AppendLine($"\t\t\t\t\t{{ event = {(c.Event == null ? "nil" : LuaString(c.Event))}, fromPort = {(c.FromPort == null ? "nil" : LuaString(PortSpec(c.FromPort)))}, " +
                                         $"toNode = {LuaString(c.ToNode)}, toEvent = {(c.ToEvent == null ? "nil" : LuaString(c.ToEvent))}, toPort = {(c.ToPort == null && c.ToField == null ? "nil" : LuaString(PortSpec(c.ToPort ?? c.ToField!)))}, " +
                                         $"portGuid = \"{StableGuid(s_CKey, "port")}\", targetPortGuid = \"{StableGuid(s_CKey, "tport")}\", connGuid = \"{StableGuid(s_CKey, "conn")}\", pop = {c.Pop} }},");
                    }
                    s_Lua.AppendLine("\t\t\t\t},");
                    s_Lua.AppendLine("\t\t\t},");
                }
                s_Lua.AppendLine("\t\t},");
                s_Lua.AppendLine("\t},");
            }
            s_Lua.AppendLine("}");

            // where the generated Lua lands: its own init, or a module the host mod requires (a merged build)
            var s_ClientFile = m_Doc.IsMerged ? m_Doc.ClientModule + ".lua" : "__init__.lua";
            var s_SharedFile = m_Doc.IsMerged ? m_Doc.SharedModule + ".lua" : "__init__.lua";
            var s_Client = LuaTemplates.Client.Replace("{{SCREENS}}", s_Lua.ToString().TrimEnd()).Replace("{{MOD}}", m_Doc.Name);
            // the chunk store rides only when there are pictures: an empty superbundle declared in mod.json is the same endless load as a missing one
            var s_Chunks = Pictures.Count > 0 ? m_Doc.ChunksSuperbundle : "";
            var s_Shared = LuaTemplates.Shared.Replace("{{MOD}}", m_Doc.Name).Replace("{{SUPERBUNDLE}}", m_Doc.Superbundle).Replace("{{BUNDLE}}", m_Doc.Bundle).Replace("{{CHUNKS}}", s_Chunks);
            if (Record != null) s_Client = s_Client.TrimEnd() + "\n\n-- ---- the recorder's receiver (Record data… riding in this mod) ----\n" + Record.ClientLua;
            // ---- the ActionScript → Lua channel: its receiver rides in the mod (the recorder's receiver already decodes and dispatches when one rides)
            if (m_Doc.As2Channel)
            {
                if (Record == null) { s_Client = s_Client.TrimEnd() + "\n" + LuaTemplates.As2Channel; m_Log.WriteLine("AS2 channel: the receiver rides in ext/Client (hook UI:EnableTypingMode; every decoded frame is the event AS2:Frame)"); }
                else m_Log.WriteLine("AS2 channel: the recorder's receiver decodes the frames and dispatches AS2:Frame (no second receiver)");
            }
            // ---- the document's own client modules: copied beside __init__.lua and required from it (a missing file stops the build)
            if (m_Doc.ClientScripts.Count > 0)
            {
                var s_Requires = new StringBuilder("\n\n-- ---- the document's client modules ----\n");
                foreach (var s_Script in m_Doc.ClientScripts)
                {
                    var s_Src = Path.IsPathRooted(s_Script) ? s_Script : Path.Combine(m_DocDir, s_Script);
                    if (!File.Exists(s_Src)) throw new FileNotFoundException($"client script {s_Script}: not found at {s_Src}");
                    var s_Name = Path.GetFileNameWithoutExtension(s_Src);
                    if (!Regex.IsMatch(s_Name, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new InvalidDataException($"client script {s_Script}: the file name must be a Lua identifier (it is required by name)");
                    var s_Dst = Path.Combine(m_ModDir, "ext", "Client", s_Name + ".lua");
                    WriteLf(s_Dst, File.ReadAllText(s_Src, Encoding.UTF8));
                    s_Requires.AppendLine($"require(\"{s_Name}\")");
                    m_Log.WriteLine($"client script {s_Name}.lua: shipped from {s_Src} ({new FileInfo(s_Src).Length} bytes), required by ext/Client/{s_ClientFile}");
                }
                s_Client = s_Client.TrimEnd() + s_Requires.ToString();
            }
            WriteLf(Path.Combine(m_ModDir, "ext", "Client", s_ClientFile), s_Client);
            WriteLf(Path.Combine(m_ModDir, "ext", "Shared", s_SharedFile), s_Shared);
            var s_ServerLua = Path.Combine(m_ModDir, "ext", "Server", "__init__.lua");
            if (Record != null) { Directory.CreateDirectory(Path.GetDirectoryName(s_ServerLua)!); WriteLf(s_ServerLua, Record.ServerLua); }
            // ⛔ in a merged build an ext/Server is the HOST's own, not a leftover recorder of ours: it is left alone
            else if (!m_Doc.IsMerged && File.Exists(s_ServerLua))
            {
                File.Delete(s_ServerLua);
                var s_ServerDir = Path.GetDirectoryName(s_ServerLua)!;
                if (!Directory.EnumerateFileSystemEntries(s_ServerDir).Any()) Directory.Delete(s_ServerDir);
                m_Log.WriteLine("removed ext/Server (a previous build's recorder)");
            }

            // ---- mod.json (no BOM: VU does not detect a mod whose mod.json starts with one)
            // ⭐ MERGED: the host's mod.json is the host's. Only the Superbundles list is touched, and only to make sure
            // OUR two entries are in it — every other entry (the camo packages the studio declares there, and which it
            // rewrites on every bake) is kept in place. Both tools therefore write the same file without erasing each
            // other: the studio keeps what is not a camo package, this keeps what is not ours.
            if (m_Doc.IsMerged)
            {
                var s_HostPath = Path.Combine(m_ModDir, "mod.json");
                var s_Host = JObject.Parse(File.ReadAllText(s_HostPath, Encoding.UTF8));
                var s_Ours = new List<string> { "Win32/" + m_Doc.Superbundle };
                if (s_Chunks != "") s_Ours.Add("Win32/" + s_Chunks);
                var s_List = s_Host["Superbundles"] as JArray ?? new JArray();
                var s_Had = s_List.Select(p_E => (string?)p_E ?? "").ToList();
                var s_Added = 0;
                foreach (var s_Name in s_Ours)
                    if (!s_Had.Any(p_E => string.Equals(p_E, s_Name, StringComparison.OrdinalIgnoreCase))) { s_List.Add(s_Name); s_Added++; }
                // an entry of OURS that no longer exists (the chunk store when the pictures go) must not be left declared:
                // a declared superbundle that is not there is the same endless load as a missing one
                for (var i = s_List.Count - 1; i >= 0; i--)
                {
                    var s_Name = (string?)s_List[i] ?? "";
                    if (s_Name.StartsWith("Win32/" + m_Doc.Superbundle.Split('/')[0] + "/", StringComparison.OrdinalIgnoreCase) &&
                        !s_Ours.Any(p_O => string.Equals(p_O, s_Name, StringComparison.OrdinalIgnoreCase)))
                    { m_Log.WriteLine($"mod.json: dropped our stale entry {s_Name}"); s_List.RemoveAt(i); }
                }
                s_Host["Superbundles"] = s_List;
                if (s_Host["HasVeniceEXT"] == null) s_Host["HasVeniceEXT"] = true;
                WriteLf(s_HostPath, s_Host.ToString(Formatting.Indented) + "\n");

                // ⛔ A MERGED BUILD NOBODY REQUIRES IS A MOD THAT BUILDS, INSTALLS AND DOES NOTHING — and there is no
                // symptom to read: the screens are simply not there. So the host's two init files are checked for their
                // require, and a missing one STOPS the build with the line to paste, instead of being a warning nobody
                // reads. (The prefix rule is VU's: bare name in the realm's own folder, `__shared/` for a shared one.)
                foreach (var (s_Realm, s_Module, s_Path) in new[]
                         {
                             ("Client", m_Doc.ClientModule, Path.Combine(m_ModDir, "ext", "Client", "__init__.lua")),
                             ("Shared", "__shared/" + m_Doc.SharedModule, Path.Combine(m_ModDir, "ext", "Shared", "__init__.lua")),
                         })
                {
                    var s_Line = $"require('{s_Module}')";
                    var s_Text = File.Exists(s_Path) ? File.ReadAllText(s_Path, Encoding.UTF8) : "";
                    if (s_Text.Contains(s_Line, StringComparison.Ordinal) ||
                        s_Text.Contains(s_Line.Replace('\'', '"'), StringComparison.Ordinal)) continue;
                    throw new InvalidDataException(
                        $"mergeInto '{m_Doc.MergeInto}': its ext/{s_Realm}/__init__.lua does not require this build's module. " +
                        $"Add this line to it (once, at the end): {s_Line}");
                }
                m_Log.WriteLine($"mod.json of '{m_Doc.MergeInto}': merged -- {s_List.Count} superbundle(s) declared, " +
                                $"{s_Added} added by this build, the host's own fields untouched");
            }
            else
            {
                var s_ModJson = new JObject
                {
                    ["Name"] = m_Doc.Name,
                    ["Authors"] = new JArray(m_Doc.Authors),
                    ["Description"] = (string.IsNullOrWhiteSpace(m_Doc.Description) ? DescribeDocument(m_Doc) : m_Doc.Description) + (Record != null ? " " + Record.Note : ""),
                    ["URL"] = "none",
                    ["Version"] = string.IsNullOrWhiteSpace(m_Doc.Version) ? "1.0.0" : m_Doc.Version.Trim(),
                    ["HasWebUI"] = false,
                    ["HasVeniceEXT"] = true,
                    ["Superbundles"] = s_Chunks == "" ? new JArray("Win32/" + m_Doc.Superbundle) : new JArray("Win32/" + m_Doc.Superbundle, "Win32/" + s_Chunks),
                    ["Tags"] = new JArray("gameplay"),
                    ["Dependencies"] = new JObject { ["veniceext"] = "^1.0.0" },
                };
                WriteLf(Path.Combine(m_ModDir, "mod.json"), s_ModJson.ToString(Formatting.Indented) + "\n");
            }

            // ---- recipe (in the work folder, also runnable by hand: RimeREPL.exe <work>\rime_build.txt after mount_game)
            m_Recipe.Clear();
            m_Recipe.Add($"build_sb Win32/{m_Doc.Superbundle} Frostbite2_0 \"{SbDir}\"");
            m_Recipe.Add($"build_bundle Win32/{m_Doc.Bundle}");
            foreach (var (s_Resource, s_File) in Movies)
                m_Recipe.Add($"replace_resource {s_Resource} 1 \"{s_File}\"");
            // a new screen: its movie and its partition are new entries of the bundle
            foreach (var (s_Resource, s_File) in NewMovies)
                m_Recipe.Add($"add_resource {s_Resource} SwfMovie \"{s_File}\"");
            // static delivery: what the shipped partitions import goes FIRST (a partition links its imports at load against what is loaded then)
            foreach (var (s_Partition, s_File) in DependencyPartitions)
                m_Recipe.Add($"add_json_partition {s_Partition} \"{s_File}\"");
            foreach (var (s_Partition, s_File) in NewPartitions)
                m_Recipe.Add($"add_json_partition {s_Partition} \"{s_File}\"");
            // static delivery: the edited copies of shipped screens, under their own names
            foreach (var (s_Partition, s_File) in StaticPartitions)
                m_Recipe.Add($"add_json_partition {s_Partition} \"{s_File}\"");
            // pictures: the texture's header resource (UI group, streaming + on-demand as the game's own UI textures, no sRGB) and its
            // TextureAsset partition in the bundle; the chunk the resource names is taken out of the bundle again — its pixels travel in
            // the chunk store below, the way the game's UI textures and the camo thumbnails ship (a chunk inside a bundle is discarded
            // unless something is waiting for it)
            foreach (var s_Picture in Pictures)
            {
                m_Recipe.Add($"add_dds_texture \"{s_Picture.TextureName}\" \"{s_Picture.Dds}\" false true false \"UI\" \"{ImageTextures.GroupDonor}\" true \"{s_Picture.ChunkGuid}\"");
                m_Recipe.Add($"add_json_partition \"{s_Picture.TextureName}\" \"{s_Picture.PartitionJson}\"");
                m_Recipe.Add($"remove_chunk {s_Picture.ChunkGuid}");
            }
            m_Recipe.Add("build");
            m_Recipe.Add("build");
            if (Pictures.Count > 0)
            {
                m_Recipe.Add($"build_sb Win32/{s_Chunks} Frostbite2_0 \"{SbDir}\" false");
                foreach (var s_Picture in Pictures)
                    m_Recipe.Add($"add_chunk {s_Picture.ChunkGuid} \"{s_Picture.PixelsBin}\" \"{s_Picture.TextureName.ToLowerInvariant()}\"");
                m_Recipe.Add("build");
            }
            var s_Recipe = new StringBuilder();
            foreach (var s_Line in m_Recipe)
                s_Recipe.AppendLine(s_Line);
            WriteLf(RecipePath, s_Recipe.ToString());
            m_Log.WriteLine($"mod folder written: {m_ModDir} (mod.json, ext/, sb/)");
        }

        /// <summary>The build steps as they run. Emit() first.</summary>
        public IEnumerable<string> RecipeLines() => m_Recipe.Count > 0 ? m_Recipe : File.ReadAllLines(RecipePath).Where(l => !string.IsNullOrWhiteSpace(l));

        // ------------------------------------------------------------------------------------------ static delivery: imports

        /// <summary>The partitions a dump references, other than itself (every {PartitionGuid, InstanceGuid} pair), by partition guid.</summary>
        static IEnumerable<string> ImportsOf(JObject p_Json)
        {
            var s_Self = (string)p_Json["PartitionGuid"]!;
            var s_Out = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ((JContainer)p_Json["Instances"]!).Descendants().OfType<JObject>())
            {
                if (r["PartitionGuid"] is not { Type: JTokenType.String } g || r["InstanceGuid"] == null || r.Count != 2) continue;
                var s_Guid = (string)g!;
                if (!string.Equals(s_Guid, s_Self, StringComparison.OrdinalIgnoreCase)) s_Out.Add(s_Guid);
            }
            return s_Out;
        }

        /// <summary>
        /// Vanilla copies of what the shipped partitions import, transitively, dependency-first: written to the work folder and listed
        /// in DependencyPartitions. Partitions the document itself ships are not copied (they are in the bundle already); a partition
        /// outside ui/ (a sound, a texture) is left to the game and logged — a UI screen never imports one in the shipped data.
        /// </summary>
        void CollectDependencies(List<(ScreenEntry Screen, JObject Json)> p_Shipped)
        {
            var s_Own = new HashSet<string>(m_Doc.Screens.Select(s => s.Partition), StringComparer.OrdinalIgnoreCase);
            var s_Done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var s_Order = new List<string>();
            void Visit(string p_Guid, int p_Depth)
            {
                if (p_Depth > 6) { m_Log.WriteLine($"WARNING: import chain deeper than 6 at {p_Guid} -- stopped"); return; }
                var s_Name = m_PartitionNameByGuid?.Invoke(p_Guid);
                if (s_Name == null)
                {
                    var s_Doc = m_Doc.Screens.FirstOrDefault(s => s.New && string.Equals(NewScreen.PartitionGuid(s.Partition), p_Guid, StringComparison.OrdinalIgnoreCase));
                    if (s_Doc != null) return;   // one of the document's own new screens
                    // a shipped partition binds its imports against what is resident when it loads and never repairs them: every
                    // widget whose asset is missing then comes up empty in the game (measured: a screen with only its header and
                    // BACK button). A build that cannot name an import must not ship.
                    throw new Exception($"import {p_Guid} of a shipped partition: no partition of that guid is known, so its copy cannot travel in the bundle " +
                                        "and the screen would load without it. Build with the game mounted (every mounted partition resolves by guid).");
                }
                if (s_Own.Contains(s_Name) || s_Done.Contains(s_Name)) return;
                s_Done.Add(s_Name);
                if (!s_Name.StartsWith("ui/", StringComparison.OrdinalIgnoreCase)) { m_Log.WriteLine($"  import {s_Name}: outside ui/, left to the game"); return; }
                JObject s_Json;
                try { s_Json = m_PartitionJson(s_Name); }
                catch (Exception s_Ex) { m_Log.WriteLine($"WARNING: import {s_Name} could not be read ({s_Ex.Message}) -- not copied"); return; }
                foreach (var s_Inner in ImportsOf(s_Json)) Visit(s_Inner, p_Depth + 1);
                var s_File = Path.Combine(m_WorkDir, "dep_" + s_Name.Replace('/', '_') + ".json");
                File.WriteAllText(s_File, s_Json.ToString(Formatting.Indented));
                s_Order.Add(s_Name);
                DependencyPartitions.Add((s_Name, s_File));
            }
            foreach (var (s_Screen, s_Json) in p_Shipped)
            {
                var s_Direct = new List<(string, string, string)>();
                foreach (var s_Guid in ImportsOf(s_Json))
                {
                    var s_Name = m_PartitionNameByGuid?.Invoke(s_Guid);
                    if (s_Name != null)
                    {
                        JObject? s_Dep = null;
                        try { s_Dep = m_PartitionJson(s_Name); } catch { /* reported by Visit */ }
                        s_Direct.Add((s_Name, s_Guid, (string?)s_Dep?["PrimaryInstanceGuid"] ?? ""));
                    }
                    Visit(s_Guid, 1);
                }
                StaticImports[s_Screen.Partition] = s_Direct;
                m_Log.WriteLine($"static {s_Screen.Partition}: {s_Direct.Count} direct import(s): {string.Join(", ", s_Direct.Select(d => d.Item1))}");
            }
            if (s_Order.Count > 0) m_Log.WriteLine($"dependency copies in the bundle, first to last: {string.Join(", ", s_Order)}");
            // the guard: every widget of a shipped screen must find its asset in the bundle (own or copied) — a widget whose asset does not
            // bind still draws (the movie has its SWF) but has NO EVENTS (NumEvents 0: nothing it fires reaches the graph), which no
            // preview can show
            foreach (var (s_Screen, s_Json) in p_Shipped)
                foreach (var s_Node in ((JObject)s_Json["Instances"]!).Properties().Select(x => x.Value).OfType<JObject>().Where(n => (string?)n["$type"] == "WidgetNode"))
                {
                    var s_AssetGuid = (string?)s_Node["WidgetAsset"]?["PartitionGuid"];
                    var s_AssetName = s_AssetGuid != null ? m_PartitionNameByGuid?.Invoke(s_AssetGuid) : null;
                    if (s_AssetName == null || s_Own.Contains(s_AssetName) || s_Done.Contains(s_AssetName)) continue;
                    throw new Exception($"{s_Screen.Partition}: the widget {s_Node["InstanceName"]} uses {s_AssetName}, which is neither shipped nor copied into the bundle: in the game it would come up without its asset (no events)");
                }
        }

        // ------------------------------------------------------------------------------------------ typed fields

        /// <summary>"Outputs:0" / "In" / "inValue" → Lua spelling of the field, keeping the ":name" part.</summary>
        static string PortSpec(string p_Spec)
        {
            var i = p_Spec.IndexOf(':');
            return i < 0 ? UiTypeCatalog.LuaName(p_Spec) : UiTypeCatalog.LuaName(p_Spec[..i]) + ":" + p_Spec[(i + 1)..];
        }

        /// <summary>Partition names referenced by typed fields (Ref kind, or structs/lists containing refs).</summary>
        static void CollectRefs(string p_Type, Dictionary<string, JToken> p_Fields, ISet<string> p_Out)
        {
            var s_Info = UiTypeCatalog.Describe(p_Type);
            foreach (var (s_Name, s_Value) in p_Fields)
                CollectRefs(s_Info?.Field(s_Name), s_Value, p_Out);
        }

        /// <summary>A reference field whose value is an object with $type: an instance described inline (a data binding), not an asset.</summary>
        static string? InstanceType(JToken v) => v is JObject o && o["$type"]?.Type == JTokenType.String ? (string?)o["$type"] : null;

        static void CollectRefs(UiTypeCatalog.FieldInfo? f, JToken v, ISet<string> p_Out)
        {
            if (f == null) return;
            switch (f.Kind)
            {
                case UiTypeCatalog.Kind.Ref:
                    if (v.Type == JTokenType.String && !string.IsNullOrEmpty((string?)v)) p_Out.Add((string)v!);
                    else if (InstanceType(v) is { } s_Type && v is JObject s_Inst)
                    {
                        var s_Info = UiTypeCatalog.Describe(s_Type);
                        foreach (var p in s_Inst.Properties().Where(p => p.Name != "$type")) CollectRefs(s_Info?.Field(p.Name), p.Value, p_Out);
                    }
                    break;
                case UiTypeCatalog.Kind.Struct:
                    if (v is JObject o)
                    {
                        var s_Struct = UiTypeCatalog.Describe(f.TypeName);
                        foreach (var p in o.Properties()) CollectRefs(s_Struct?.Field(p.Name), p.Value, p_Out);
                    }
                    break;
                case UiTypeCatalog.Kind.List:
                    if (v is JArray a)
                        foreach (var item in a)
                            CollectRefs(new UiTypeCatalog.FieldInfo { Name = f.Name, Kind = f.ElementKind, TypeName = f.TypeName }, item, p_Out);
                    break;
            }
        }

        /// <summary>One Lua table describing a field value: { name, kind, value | enum | type+fields | items | asset | instance }.</summary>
        static string EmitField(string p_LuaName, JToken v, UiTypeCatalog.FieldInfo? f, string p_Key = "")
        {
            return "{ name = " + LuaString(p_LuaName) + ", " + EmitValue(v, f, p_Key) + " }";
        }

        static string EmitValue(JToken v, UiTypeCatalog.FieldInfo? f, string p_Key = "")
        {
            var inv = CultureInfo.InvariantCulture;
            var s_Kind = f?.Kind ?? GuessKind(v);
            // a reference field holding an object with $type is an instance described inline (a widget's data binding):
            // created in Lua born with a stable guid, or edited in place when the node already carries one of that type
            if ((s_Kind == UiTypeCatalog.Kind.Ref || f == null) && InstanceType(v) is { } s_InstType && v is JObject s_Inst)
            {
                var s_Info = UiTypeCatalog.Describe(s_InstType);
                var s_Parts = new List<string>();
                foreach (var p in s_Inst.Properties().Where(p => p.Name != "$type"))
                {
                    var sf = s_Info?.Field(p.Name);
                    if (sf != null && (sf.Kind == UiTypeCatalog.Kind.Port || sf.Kind == UiTypeCatalog.Kind.PortArray)) continue;
                    s_Parts.Add(EmitField(sf?.LuaName ?? UiTypeCatalog.LuaName(p.Name), p.Value, sf, p_Key + "|" + p.Name));
                }
                return "kind = \"instance\", type = " + LuaString(s_InstType) + ", guid = \"" + StableGuid(p_Key, "instance", s_InstType) + "\", fields = { " + string.Join(", ", s_Parts) + " }";
            }
            switch (s_Kind)
            {
                case UiTypeCatalog.Kind.Bool:
                    return "kind = \"bool\", value = " + (v.Type == JTokenType.Boolean ? ((bool)v ? "true" : "false") : (string.Equals((string?)v, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false"));
                case UiTypeCatalog.Kind.Int:
                    return "kind = \"number\", value = " + (v.Type == JTokenType.Integer ? ((long)v).ToString(inv) : (long.TryParse((string?)v, NumberStyles.Integer, inv, out var l) ? l.ToString(inv) : "0"));
                case UiTypeCatalog.Kind.Float:
                    return "kind = \"number\", value = " + (v.Type is JTokenType.Float or JTokenType.Integer ? ((double)v).ToString("0.0###", inv) : (double.TryParse((string?)v, NumberStyles.Float, inv, out var d) ? d.ToString("0.0###", inv) : "0.0"));
                case UiTypeCatalog.Kind.String:
                    return "kind = \"string\", value = " + LuaString((string?)v ?? "");
                case UiTypeCatalog.Kind.Enum:
                {
                    var s_Member = (string?)v ?? "";
                    var s_Members = UiTypeCatalog.EnumMembers(f!.TypeName);
                    if (!s_Members.Contains(s_Member) && s_Members.Contains(f.TypeName + "_" + s_Member)) s_Member = f.TypeName + "_" + s_Member;
                    return "kind = \"enum\", enum = " + LuaString(f.TypeName) + ", value = " + LuaString(s_Member);
                }
                case UiTypeCatalog.Kind.Ref:
                    return "kind = \"ref\", asset = " + LuaString((string?)v ?? "") + (f != null && f.TypeName != "" ? ", type = " + LuaString(f.TypeName) : "");
                case UiTypeCatalog.Kind.Struct:
                {
                    var s_Struct = UiTypeCatalog.Describe(f!.TypeName);
                    var s_Parts = new List<string>();
                    if (v is JObject o)
                        foreach (var p in o.Properties().Where(p => p.Name != "$type"))
                        {
                            var sf = s_Struct?.Field(p.Name);
                            s_Parts.Add(EmitField(sf?.LuaName ?? UiTypeCatalog.LuaName(p.Name), p.Value, sf, p_Key + "|" + p.Name));
                        }
                    return "kind = \"struct\", type = " + LuaString(f.TypeName) + ", fields = { " + string.Join(", ", s_Parts) + " }";
                }
                case UiTypeCatalog.Kind.List:
                {
                    var s_Element = f == null ? null : new UiTypeCatalog.FieldInfo { Name = f.Name, Kind = f.ElementKind, TypeName = f.TypeName };
                    var s_Items = new List<string>();
                    if (v is JArray a) { var i = 0; foreach (var item in a) s_Items.Add("{ " + EmitValue(item, s_Element, p_Key + "|" + (i++).ToString(inv)) + " }"); }
                    return "kind = \"list\", items = { " + string.Join(", ", s_Items) + " }";
                }
                default:
                    return "kind = \"string\", value = " + LuaString(v.Type == JTokenType.String ? (string)v! : v.ToString(Formatting.None));
            }
        }

        static UiTypeCatalog.Kind GuessKind(JToken v) => v.Type switch
        {
            JTokenType.Boolean => UiTypeCatalog.Kind.Bool,
            JTokenType.Integer => UiTypeCatalog.Kind.Int,
            JTokenType.Float => UiTypeCatalog.Kind.Float,
            JTokenType.Array => UiTypeCatalog.Kind.List,
            _ => UiTypeCatalog.Kind.String,
        };

        static void WriteLf(string p_Path, string p_Text)
        {
            File.WriteAllText(p_Path, p_Text.Replace("\r\n", "\n"), new UTF8Encoding(false));
        }

        public static void ApplyOp(GfxMovie p_Movie, string p_Op)
        {
            var f = p_Op.Split(':');
            var inv = CultureInfo.InvariantCulture;
            switch (f[0])
            {
                case "move": p_Movie.Move(f[1], double.Parse(f[2], inv), double.Parse(f[3], inv)); break;
                case "scale": p_Movie.Scale(f[1], double.Parse(f[2], inv), double.Parse(f[3], inv)); break;
                case "char": p_Movie.SetCharacter(f[1], ushort.Parse(f[2], inv)); break;
                case "depth": p_Movie.SetDepth(f[1], ushort.Parse(f[2], inv)); break;
                case "remove": p_Movie.Remove(f[1]); break;
                case "vars": p_Movie.SetClipVars(f[1], GfxMovie.ParseClipVars(p_Op[(p_Op.IndexOf(':', p_Op.IndexOf(':') + 1) + 1)..])); break;
                case "import": p_Movie.AddImport(f[1], ushort.Parse(f[2], inv), f[3]); break;
                case "add":
                    p_Movie.AddPlacement(ushort.Parse(f[1], inv), f[2], ushort.Parse(f[3], inv), ushort.Parse(f[4], inv),
                        double.Parse(f[5], inv), double.Parse(f[6], inv), double.Parse(f[7], inv), double.Parse(f[8], inv));
                    break;
                case "clone":
                    // clone:<clip>:<newName>:<depth>:<xPx>:<yPx> — the same widget again beside it (character, scale, construct variables)
                    if (f.Length < 6) throw new Exception("clone op: expected clone:<clip>:<newName>:<depth>:<xPx>:<yPx>");
                    p_Movie.ClonePlacement(f[1], f[2], ushort.Parse(f[3], inv), double.Parse(f[4], inv), double.Parse(f[5], inv));
                    break;
                case "script":
                {
                    // script:<name>:<var=value|var=value or empty>:<base64 of the compiled DoAction body> — a frame action on the screen's
                    // main timeline (a compiled .as, see External/keku/compile_as.py) with its string parameters assigned in front
                    var s_First = p_Op.IndexOf(':'); var s_Second = p_Op.IndexOf(':', s_First + 1); var s_Third = p_Op.IndexOf(':', s_Second + 1);
                    if (s_Third < 0) throw new Exception("script op: expected script:<name>:<vars>:<base64>");
                    var s_Name = p_Op[(s_First + 1)..s_Second];
                    var s_VarText = p_Op[(s_Second + 1)..s_Third];
                    // ⛔ the parameters are STRINGS and go as written: a typed round trip (ParseClipVars + ToString) spelled a decimal in the
                    // machine's culture -- "25.2" arrived "25,2" on a Spanish Windows, which AS2's Number() reads as NaN (measured 2026-09-26,
                    // the vehicle rows' icon height) -- and "true" as "True"
                    var s_Vars = s_VarText.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(s_Part =>
                    {
                        var s_Eq = s_Part.IndexOf('=');
                        if (s_Eq <= 0) throw new Exception($"script variable '{s_Part}' is not name=value");
                        return (s_Part[..s_Eq].Trim(), s_Part[(s_Eq + 1)..]);
                    }).ToList();
                    p_Movie.AddFrameScript(s_Name, s_Vars, Convert.FromBase64String(p_Op[(s_Third + 1)..]));
                    break;
                }
                case "textsize":
                    // textsize:<clip>:<px> — the text widget's size in px (0 = its row type's own): a construct variable + one frame action per movie
                    if (f.Length < 3) throw new Exception("textsize op: expected textsize:<clip>:<px>");
                    FrameScripts.ApplyTextSize(p_Movie, f[1], f[2]);
                    break;
                default: throw new Exception($"unknown stage op '{f[0]}'");
            }
        }
    }
}
