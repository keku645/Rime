using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.UiBuilder;
using RimeUIEditor.Model;

namespace RimeUIEditor
{
    /// <summary>
    /// Headless seam: opens EVERY UI graph of the game (241 screens + 80 flow graphs, from the cache, no mount)
    /// through the editor's own model and graph view, and checks that nothing is lost or misplaced:
    ///   . every node-shaped instance of the partition is a node of the model (and known to the type catalogue)
    ///   . every UINodeConnection is a wire whose both ends land on a port of the right node, in the right
    ///     direction (source = output, target = input); endpoints the game itself ships without a port slot are
    ///     counted as dangling, never dropped
    ///   . every port slot in use is a port of the catalogue
    ///   . every widget node of a screen has its clip on the stage
    ///   . the graph view draws one box per node and attaches every wire (UnresolvedWires == 0)
    /// The totals are compared with an independent parser's count of the same data (scratchpad/audit_flow.py).
    /// Usage: RimeUIEditor.exe --graphaudit &lt;out dir&gt;   (RUE_SETTINGS pointing at settings next to a warm cache)
    /// Writes graphaudit.txt + photos of three graphs; exits 0 on PASS.
    /// </summary>
    public partial class MainWindow
    {
        // measured by scratchpad/audit_flow.py + audit_orphans.py (a Python walk of the same partition dumps): the numbers the model must
        // reproduce. Unused ports = port instances no node slot holds AND no connection names (4458 unowned, of which 96 are the ports the
        // 118 dangling endpoints name).
        // base game (ui/flow: 321 graphs, 4594 nodes, 4504 connections, 118 dangling, 4362 unused ports, 1408 widgets) + End Game's ui/xp5/flow
        // (5 graphs: the CTF HUD, its ticket counter screen and 3 graphs — 36 nodes, 25 connections, 1 dangling, 86 unused ports, 11 widgets), both
        // measured by the independent Python oracles (External/keku/audit_*.py)
        const int c_AuditGraphs = 326, c_AuditNodes = 4630, c_AuditConnections = 4529, c_AuditDangling = 119, c_AuditPortRefs = 3, c_AuditUnusedPorts = 4448, c_AuditWidgets = 1419;

        public async Task<int> GraphAudit(string p_OutDir)
        {
            Directory.CreateDirectory(p_OutDir);
            var s_Report = new StringWriter();
            var s_Problems = new List<string>();
            void R(string t) { s_Report.WriteLine(t); Log("[audit] " + t); }
            try
            {
                if (!m_Rime.HasCache) throw new Exception("no warm cache: point RUE_SETTINGS at settings next to one (or Mount game once)");
                var s_Partitions = m_AllScreens.ToList();
                R($"{s_Partitions.Count} UI graphs from the cache ({s_Partitions.Count(p => p.StartsWith("ui/flow/screen/"))} screens, {s_Partitions.Count(p => p.StartsWith("ui/flow/graph/"))} flow graphs)");

                int s_Nodes = 0, s_Conns = 0, s_Dangling = 0, s_PortRefs = 0, s_Unused = 0, s_Widgets = 0, s_WidgetsWithClip = 0, s_ArrayWires = 0;
                var s_Slots = new SortedDictionary<string, int>();                 // "Type.Field in|out" -> ports
                var s_UnknownTypes = new SortedDictionary<string, int>();
                var s_WidgetsWithoutClip = new List<string>();
                var s_DanglingWhere = new SortedDictionary<string, int>();
                string s_MostArrayWires = ""; var s_MostArrayCount = -1;
                var s_Lines = new List<string>();
                foreach (var s_Partition in s_Partitions)
                {
                    ScreenModel s_Model;
                    try { s_Model = ScreenModel.Load(m_Rime, s_Partition, WidgetMovie); }
                    catch (Exception s_Ex) { s_Problems.Add($"{s_Partition}: load failed: {s_Ex.Message}"); continue; }
                    m_Screens[s_Partition] = s_Model;
                    var s_Instances = s_Model.Instances;
                    // the partition's own count, by shape (every node type of the game ends with Node; ports are *NodePort)
                    var s_JsonNodes = s_Instances.Properties().Count(p => p.Value is JObject o && ((string?)o["$type"] ?? "").EndsWith("Node") && !((string?)o["$type"] ?? "").Contains("NodePort"));
                    var s_JsonConns = s_Instances.Properties().Count(p => p.Value is JObject o && (string?)o["$type"] == "UINodeConnection");
                    if (s_Model.AllNodes.Count != s_JsonNodes) s_Problems.Add($"{s_Partition}: model has {s_Model.AllNodes.Count} nodes, partition {s_JsonNodes}");
                    if (s_Model.Wires.Count != s_JsonConns) s_Problems.Add($"{s_Partition}: model has {s_Model.Wires.Count} wires, partition {s_JsonConns} connections");
                    var s_ByGuid = s_Model.AllNodes.ToDictionary(n => n.Guid);
                    foreach (var n in s_Model.AllNodes)
                    {
                        if (n.UnknownType) s_UnknownTypes[n.Type] = s_UnknownTypes.TryGetValue(n.Type, out var c) ? c + 1 : 1;
                        foreach (var p in n.Ports.Where(p => !p.Dangling))
                        {
                            var k = $"{n.Type}.{p.Field} {(p.IsInput ? "in" : "out")}";
                            s_Slots[k] = s_Slots.TryGetValue(k, out var c) ? c + 1 : 1;
                            var f = UiTypeCatalog.Describe(n.Type)?.Field(p.Field);
                            if (f == null || (f.Kind != UiTypeCatalog.Kind.Port && f.Kind != UiTypeCatalog.Kind.PortArray))
                                s_Problems.Add($"{s_Partition}: {n.Label} ({n.Type}).{p.Field} holds a port but the catalogue says {(f == null ? "no such field" : f.Kind.ToString())}");
                        }
                        s_PortRefs += n.PortRefs.Count;
                        foreach (var r in n.PortRefs) if (r.Node == "?") s_Problems.Add($"{s_Partition}: {n.Label}.{r.Field} references a port of no node");
                    }
                    var s_ArrayHere = 0;
                    foreach (var w in s_Model.Wires)
                    {
                        if (!s_ByGuid.TryGetValue(w.FromGuid, out var s_From)) { s_Problems.Add($"{s_Partition}: wire {w.From}.{w.FromPort} -> {w.To}.{w.ToPort}: source node not in the model"); continue; }
                        if (!s_ByGuid.TryGetValue(w.ToGuid, out var s_To)) { s_Problems.Add($"{s_Partition}: wire {w.From}.{w.FromPort} -> {w.To}.{w.ToPort}: target node not in the model"); continue; }
                        var s_FromPort = s_From.Ports.FirstOrDefault(p => p.Guid == w.FromPortGuid);
                        var s_ToPort = s_To.Ports.FirstOrDefault(p => p.Guid == w.ToPortGuid);
                        if (s_FromPort == null) s_Problems.Add($"{s_Partition}: wire {w.From}.{w.FromPort}: source port not on its node");
                        else if (s_FromPort.IsInput) s_Problems.Add($"{s_Partition}: wire {w.From}.{w.FromPort} fires from an INPUT slot ({s_FromPort.Field})");
                        if (s_ToPort == null) s_Problems.Add($"{s_Partition}: wire -> {w.To}.{w.ToPort}: target port not on its node");
                        else if (!s_ToPort.IsInput) s_Problems.Add($"{s_Partition}: wire -> {w.To}.{w.ToPort} lands on an OUTPUT slot ({s_ToPort.Field})");
                        if (w.Dangling) { s_Dangling += (s_FromPort?.Dangling == true ? 1 : 0) + (s_ToPort?.Dangling == true ? 1 : 0); s_DanglingWhere[s_Partition] = s_DanglingWhere.TryGetValue(s_Partition, out var c) ? c + 1 : 1; }
                        if (s_FromPort?.Field == "Outputs" || s_ToPort?.Field == "Inputs") s_ArrayHere++;
                    }
                    if (s_Model.IsScreen && s_ArrayHere > s_MostArrayCount) { s_MostArrayCount = s_ArrayHere; s_MostArrayWires = s_Partition; }
                    s_ArrayWires += s_ArrayHere;
                    // widgets and their clips
                    var s_Clips = s_Model.Placements.Select(p => p.Name).ToHashSet();
                    foreach (var w in s_Model.AllNodes.Where(n => n.IsWidget))
                    {
                        s_Widgets++;
                        if (!s_Model.HasStage) continue;
                        if (s_Clips.Contains(w.Label)) s_WidgetsWithClip++; else s_WidgetsWithoutClip.Add($"{s_Partition}: {w.Label}");
                    }
                    // the graph view: one box per node, every wire on a port
                    m_Current = s_Model;
                    m_Graph.SetGraph(s_Model, null);
                    if (m_Graph.Boxes.Count() != s_Model.AllNodes.Count) s_Problems.Add($"{s_Partition}: graph draws {m_Graph.Boxes.Count()} boxes for {s_Model.AllNodes.Count} nodes");
                    if (m_Graph.Wires.Count() != s_Model.Wires.Count) s_Problems.Add($"{s_Partition}: graph has {m_Graph.Wires.Count()} wires for {s_Model.Wires.Count} connections");
                    if (m_Graph.UnresolvedWires > 0) s_Problems.Add($"{s_Partition}: graph could not attach {m_Graph.UnresolvedWires} wire(s) to a port");
                    s_Nodes += s_Model.AllNodes.Count; s_Conns += s_Model.Wires.Count; s_Unused += s_Model.UnusedPorts;
                    s_Lines.Add($"{s_Partition}\t{s_Model.AllNodes.Count} nodes\t{s_Model.Wires.Count} wires\t{s_Model.Wires.Count(w => w.Dangling)} dangling\t{s_Model.Placements.Count} clips\t{m_Graph.UnresolvedWires} unresolved");
                }
                R($"nodes {s_Nodes} (expected {c_AuditNodes}) · connections {s_Conns} (expected {c_AuditConnections}) · dangling endpoints {s_Dangling} (expected {c_AuditDangling}) · port references {s_PortRefs} (expected {c_AuditPortRefs}) · unused ports {s_Unused} (expected {c_AuditUnusedPorts}) · widgets {s_Widgets} (expected {c_AuditWidgets})");
                R($"widgets on screens with a stage clip: {s_WidgetsWithClip} of {s_WidgetsWithClip + s_WidgetsWithoutClip.Count}; without: {string.Join(", ", s_WidgetsWithoutClip)}");
                R($"wires on array ports (Outputs[]/Inputs[]): {s_ArrayWires}; the screen with most: {s_MostArrayWires} ({s_MostArrayCount})");
                if (s_Partitions.Count != c_AuditGraphs) s_Problems.Add($"{s_Partitions.Count} graphs listed, expected {c_AuditGraphs}");
                if (s_Nodes != c_AuditNodes) s_Problems.Add($"{s_Nodes} nodes, expected {c_AuditNodes}");
                if (s_Conns != c_AuditConnections) s_Problems.Add($"{s_Conns} connections, expected {c_AuditConnections}");
                if (s_Dangling != c_AuditDangling) s_Problems.Add($"{s_Dangling} dangling endpoints, expected {c_AuditDangling}");
                if (s_PortRefs != c_AuditPortRefs) s_Problems.Add($"{s_PortRefs} port references, expected {c_AuditPortRefs}");
                if (s_Unused != c_AuditUnusedPorts) s_Problems.Add($"{s_Unused} unused ports, expected {c_AuditUnusedPorts}");
                if (s_Widgets != c_AuditWidgets) s_Problems.Add($"{s_Widgets} widget nodes, expected {c_AuditWidgets}");
                if (s_WidgetsWithoutClip.Count > 1) s_Problems.Add($"{s_WidgetsWithoutClip.Count} widget nodes without a stage clip (the game ships exactly one: blackstartscreen/TextField_01)");
                if (s_UnknownTypes.Count > 0) s_Problems.Add("node types unknown to the catalogue: " + string.Join(", ", s_UnknownTypes.Select(kv => $"{kv.Key} x{kv.Value}")));
                R("port slots in use (type.field direction: ports):");
                foreach (var kv in s_Slots) R($"  {kv.Key}: {kv.Value}");
                R("dangling connections by partition (the game's own data):");
                foreach (var kv in s_DanglingWhere) R($"  {kv.Key}: {kv.Value}");

                // photos, through the user's path (pick in the list, Graph tab, Fit): a flow graph, a screen with repeated labels, the screen with most array-port wires
                foreach (var (s_Pick, s_File) in new[] { ("ui/flow/graph/frontend/frontendroot", "audit_frontendroot.png"), ("ui/flow/screen/customizesoldierscreen", "audit_customizesoldier.png"), (s_MostArrayWires, "audit_arrayports.png") })
                {
                    if (!s_Partitions.Contains(s_Pick)) { s_Problems.Add($"{s_Pick} not in the list"); continue; }
                    if (!await OpenScreen(s_Pick) || m_Current?.Partition != s_Pick) { s_Problems.Add($"{s_Pick} did not open"); continue; }
                    CentreTabs.SelectedIndex = 1; await Task.Delay(150);
                    GraphViewport.Fit(); await Task.Delay(150);
                    Photograph(Path.Combine(p_OutDir, s_File));
                    R($"photo {s_File}: {s_Pick} — {m_Graph.Boxes.Count()} boxes, {m_Graph.Wires.Count()} wires, {m_Graph.UnresolvedWires} unresolved");
                }
                R("per partition:");
                foreach (var l in s_Lines) R("  " + l);
                foreach (var p in s_Problems) R("PROBLEM: " + p);
                R(s_Problems.Count == 0 ? "PASS" : $"FAIL ({s_Problems.Count} problems)");
                File.WriteAllText(Path.Combine(p_OutDir, "graphaudit.txt"), s_Report.ToString());
                return s_Problems.Count == 0 ? 0 : 1;
            }
            catch (Exception s_Ex)
            {
                R("FAIL: " + s_Ex.Message + "\n" + s_Ex.StackTrace);
                R("--- window log ---");
                R(LogBox.Text);
                File.WriteAllText(Path.Combine(p_OutDir, "graphaudit.txt"), s_Report.ToString());
                return 1;
            }
        }
    }
}
