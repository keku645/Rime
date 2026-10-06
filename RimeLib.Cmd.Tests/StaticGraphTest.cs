using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.UiBuilder;
using Xunit;

namespace RimeLib.Cmd.Tests
{
    /// <summary>
    /// The static delivery: the document applied to a screen's partition dump. A small dump in the game's JSON shape (the asset,
    /// a shipped widget with a dynamic binding and two ports, a logic node, one connection) takes a new text widget with an inline
    /// binding, a new DataSetNode wired from the widget and into the shipped node, an edit of the shipped widget, and a removed
    /// shipped wire — and the result is what the Lua would have built at load, under the same guids.
    /// </summary>
    public class StaticGraphTest
    {
        const string Part = "7afa2964-44b1-11e0-b1cf-b357911b235f";
        const string Asset = "dc378fed-67a0-7292-6c08-191f74157e51";
        const string Button = "54dbbbda-301c-4aa7-855d-5b1835476171", ButtonOut = "a59dcc58-2fc1-4fa1-86fd-b2da78ff2aab", ButtonIn = "e24a13ad-34b8-453d-9f98-7dabb47aae9b";
        const string Confirm = "0f1b14b5-0bb4-4925-9d1b-ba97e839905b", ConfirmIn = "9a9892be-79a1-449b-923f-1b64c26396dd";
        const string Binding = "986e55ab-ac88-4fb8-b034-d2047b2886cc", Conn = "b6827086-cb5d-4c18-aff8-cce6ace3fcad";
        const string WidgetPart = "c4a61c0a-9013-11df-999a-b460ed5efba1", WidgetInst = "992ede9c-36f6-f31e-2140-5c1ec7fcf345";
        const string CompPart = "ec71c788-4048-11e0-b340-8fe58fac77af", CompInst = "f4aaf0e8-5d2c-4c83-d920-ea612a5a00a8";

        static JObject R(string p, string i) => new() { ["PartitionGuid"] = p, ["InstanceGuid"] = i };

        static JObject Vanilla()
        {
            var s_Instances = new JObject
            {
                [Button] = new JObject
                {
                    ["$type"] = "WidgetNode", ["Name"] = "Back", ["ParentGraph"] = R(Part, Asset), ["IsRootNode"] = false, ["ParentIsScreen"] = true,
                    ["WidgetAsset"] = R(WidgetPart, WidgetInst), ["FocusIndex"] = 2, ["ZDepthLevel"] = 0, ["VerticalAlign"] = "WVA_Center", ["HorisontalAlign"] = "WHA_Center",
                    ["DataBinding"] = R(Part, Binding), ["WidgetProperties"] = new JArray(new JObject { ["Name"] = "Visible", ["Value"] = "true" }), ["InstanceName"] = "Button_02",
                    ["Inputs"] = new JArray(R(Part, ButtonIn)), ["Outputs"] = new JArray(R(Part, ButtonOut)), ["AlwaysInFocus"] = false,
                },
                [ButtonIn] = new JObject { ["$type"] = "UINodePort", ["Name"] = "Show", ["InstanceName"] = "Button_02", ["Query"] = "UIWidgetEventID_Show", ["AllowManualRemove"] = false },
                [ButtonOut] = new JObject { ["$type"] = "UINodePort", ["Name"] = "Released", ["InstanceName"] = "Button_02", ["Query"] = "UIWidgetEventID_OnItemReleased", ["AllowManualRemove"] = false },
                [Binding] = new JObject
                {
                    ["$type"] = "UIDynamicDataBinding", ["Refresh"] = true,
                    ["Bindings"] = new JArray(new JObject { ["DataName"] = "Text", ["DataCategory"] = R(CompPart, CompInst), ["DataKey"] = 1978414970, ["UseDirectAccess"] = false, ["UpdateOnInitialize"] = true }),
                },
                [Confirm] = new JObject
                {
                    ["$type"] = "InstanceOutputNode", ["Name"] = "Confirm", ["ParentGraph"] = R(Part, Asset), ["IsRootNode"] = false, ["ParentIsScreen"] = true,
                    ["In"] = R(Part, ConfirmIn), ["Id"] = -641143785, ["DestroyGraph"] = true,
                },
                [ConfirmIn] = new JObject { ["$type"] = "UINodePort", ["Name"] = "In", ["InstanceName"] = "Confirm", ["Query"] = "UIWidgetEventID_None", ["AllowManualRemove"] = false },
                [Conn] = new JObject { ["$type"] = "UINodeConnection", ["SourceNode"] = R(Part, Button), ["TargetNode"] = R(Part, Confirm), ["SourcePort"] = R(Part, ButtonOut), ["TargetPort"] = R(Part, ConfirmIn), ["NumScreensToPop"] = 0 },
                [Asset] = new JObject
                {
                    ["$type"] = "UIScreenAsset", ["Name"] = "UI/Flow/Screen/TestScreen",
                    ["Nodes"] = new JArray(R(Part, Button), R(Part, Confirm)), ["GlobalNode"] = null, ["Connections"] = new JArray(R(Part, Conn)),
                    ["AudioEventMappings"] = null, ["BundleAssetName"] = "", ["EventList"] = new JArray(), ["Modal"] = false, ["ProtectScreens"] = false,
                    ["IsWin32UIGraphAsset"] = true, ["IsXenonUIGraphAsset"] = true, ["IsPs3UIGraphAsset"] = true,
                },
            };
            return new JObject { ["PrimaryInstanceGuid"] = Asset, ["Instances"] = s_Instances, ["Name"] = "ui/flow/screen/testscreen", ["PartitionGuid"] = Part };
        }

        static JObject PartitionJson(string p_Name) => p_Name switch
        {
            "ui/assets/textfield" => new JObject { ["PartitionGuid"] = WidgetPart, ["PrimaryInstanceGuid"] = WidgetInst },
            "ui/uicomponents/uicustomizationcomp" => new JObject { ["PartitionGuid"] = CompPart, ["PrimaryInstanceGuid"] = CompInst },
            _ => throw new IOException("no such partition " + p_Name),
        };

        /// <summary>
        /// The import copies are found by guid in the mounter. GUID(System.Guid) clears the low bit of the last byte (the chunk
        /// compression flag), so half of all partitions — kitselector …235f, uicustomizationcomp …77af, the audio event …fba1 —
        /// stopped resolving and their copies were left out of the bundle: the screen came up in the game with only its header and
        /// BACK button (2026-09-16). The resolver's guid keeps every bit.
        /// </summary>
        [Fact]
        public void APartitionGuidWithAnOddLastByteResolvesAsSpelled()
        {
            foreach (var s_Guid in new[] { WidgetPart, CompPart, "5d3ae3f7-44ae-11e0-b1cf-b357911b235f", Part })
            {
                Assert.Equal(s_Guid, RimeUiService.PartitionGuid(s_Guid).ToString());
                Assert.Equal(s_Guid, RimeUiService.PartitionGuid(" " + s_Guid.ToUpperInvariant() + " ").ToString());
            }
            // the trap, so it stays documented: the System.Guid constructor is not a plain copy
            Assert.NotEqual(CompPart, new RimeLib.Frostbite.Core.GUID(Guid.Parse(CompPart)).ToString());
            Assert.EndsWith("ae", new RimeLib.Frostbite.Core.GUID(Guid.Parse(CompPart)).ToString());
        }

        [Fact]
        public void TheDocumentLandsInThePartitionUnderTheLuaGuids()
        {
            var s_Doc = new ScreenDocument { Name = "StaticTest", Superbundle = "statictest/ui", Bundle = "statictest/uibundle", GraphDelivery = "static" };
            var s_Screen = new ScreenEntry { Partition = "ui/flow/screen/testscreen" };
            s_Screen.Fields["Modal"] = true;
            s_Screen.RemovedConnections.Add(new RemovedConnectionEntry { Guid = Conn.ToUpperInvariant(), From = "Button_02.Released", To = "Confirm.In" });
            // a new text widget with a static text (an inline UITextDataBinding), wired from its Released event into the shipped Confirm
            s_Screen.Nodes.Add(new NodeEntry
            {
                InstanceName = "TextField_02", Widget = "ui/assets/textfield", FocusIndex = 5,
                Fields = new Dictionary<string, JToken> { ["DataBinding"] = JObject.Parse("{\"$type\":\"UITextDataBinding\",\"StaticText\":\"I HATE BURGERS\",\"Refresh\":true,\"OverrideDirectAccess\":true}") },
                Properties = { ["TextSize"] = "32" },
                Connections = { new ConnectionEntry { Event = "OnItemReleased", ToNode = "Confirm", ToPort = "In" } },
            });
            // a new DataSetNode fed by the shipped button's shipped port and wired out to Confirm; the shipped button edited (focus, a property, a rewired binding)
            s_Screen.Nodes.Add(new NodeEntry
            {
                InstanceName = "SetButtons", Type = "DataSetNode",
                Fields = new Dictionary<string, JToken>
                {
                    ["Param"] = "ID_M_BACK,IDB_Rright,false,0;",
                    ["DataSource"] = JObject.Parse("{\"DataName\":\"\",\"DataKey\":1631675365,\"DataCategory\":\"ui/uicomponents/uicustomizationcomp\",\"UseDirectAccess\":false,\"UpdateOnInitialize\":true}"),
                },
                Connections = { new ConnectionEntry { FromPort = "Out", ToNode = "Confirm", ToPort = "In" } },
            });
            s_Screen.Nodes.Add(new NodeEntry
            {
                InstanceName = "Button_02", New = false, FocusIndex = 7, ReplaceProperties = false, Properties = { ["Visible"] = "false" },
                Fields = new Dictionary<string, JToken> { ["FocusIndex"] = 7 },
                Binding = new BindingEntry { DataName = "Text", DataKey = 42, Category = "ui/uicomponents/uicustomizationcomp" },
                Connections = { new ConnectionEntry { Event = "OnItemReleased", ToNode = "SetButtons", ToPort = "In" } },
            });
            s_Doc.Screens.Add(s_Screen);

            var s_Log = new StringWriter();
            var s_Out = StaticGraph.Apply(s_Doc, s_Screen, Vanilla(), PartitionJson, s_Log);
            var s_Inst = (JObject)s_Out["Instances"]!;
            var s_Asset = (JObject)s_Inst[Asset]!;
            Assert.Equal(Part, (string?)s_Out["PartitionGuid"]);
            // the removed wire is gone from the instances and from the asset's list; Modal is on
            Assert.Null(s_Inst[Conn]);
            Assert.DoesNotContain(((JArray)s_Asset["Connections"]!), x => (string?)x["InstanceGuid"] == Conn);
            Assert.True((bool)s_Asset["Modal"]!);
            // the new widget, under the Lua's guid, with its widget asset, its inline binding as an instance, its property and its event port
            var s_Key = "StaticTest|ui/flow/screen/testscreen|TextField_02";
            var s_TfGuid = ModEmitter.StableGuid(s_Key, "node").ToLowerInvariant();
            var s_Tf = (JObject?)s_Inst[s_TfGuid] ?? throw new Exception("TextField_02 not in the partition under its guid; instances: " + string.Join(", ", s_Inst.Properties().Select(p => p.Name + ":" + p.Value["$type"])));
            Assert.Equal("WidgetNode", (string?)s_Tf["$type"]);
            Assert.Equal(WidgetInst, (string?)s_Tf["WidgetAsset"]!["InstanceGuid"]);
            Assert.Equal(5, (int)s_Tf["FocusIndex"]!);
            Assert.Contains(((JArray)s_Asset["Nodes"]!), x => (string?)x["InstanceGuid"] == s_TfGuid);
            var s_TfBind = (JObject)s_Inst[(string)s_Tf["DataBinding"]!["InstanceGuid"]!]!;
            Assert.Equal("UITextDataBinding", (string?)s_TfBind["$type"]);
            Assert.Equal("I HATE BURGERS", (string?)s_TfBind["StaticText"]);
            Assert.Equal("", (string?)s_TfBind["TextData"]!["DataName"]);   // the untouched struct fields are there with their zeros
            Assert.Equal("32", (string?)((JArray)s_Tf["WidgetProperties"]!)[0]["Value"]);
            var s_TfOut = (JObject)s_Inst[(string)((JArray)s_Tf["Outputs"]!)[0]["InstanceGuid"]!]!;
            Assert.Equal("UIWidgetEventID_OnItemReleased", (string?)s_TfOut["Query"]);
            // the DataSetNode: typed fields, its In/Out ports born, its DataSource's category a reference, wired out to Confirm.In
            var s_SetGuid = ModEmitter.StableGuid("StaticTest|ui/flow/screen/testscreen|SetButtons", "node").ToLowerInvariant();
            var s_Set = (JObject)s_Inst[s_SetGuid]!;
            Assert.Equal("DataSetNode", (string?)s_Set["$type"]);
            Assert.Equal("ID_M_BACK,IDB_Rright,false,0;", (string?)s_Set["Param"]);
            Assert.Equal(CompInst, (string?)s_Set["DataSource"]!["DataCategory"]!["InstanceGuid"]);
            Assert.Equal(1631675365, (long)s_Set["DataSource"]!["DataKey"]!);
            Assert.False((bool)s_Set["ForceUpdate"]!);
            var s_SetOut = (string)s_Set["Out"]!["InstanceGuid"]!;
            Assert.Equal("UINodePort", (string?)s_Inst[s_SetOut]!["$type"]);
            var s_Conns = ((JArray)s_Asset["Connections"]!).Select(x => (JObject)s_Inst[(string)x["InstanceGuid"]!]!).ToList();
            Assert.Equal(3, s_Conns.Count);
            Assert.Contains(s_Conns, k => (string?)k["SourcePort"]!["InstanceGuid"] == s_SetOut && (string?)k["TargetPort"]!["InstanceGuid"] == ConfirmIn);
            Assert.Contains(s_Conns, k => (string?)k["SourceNode"]!["InstanceGuid"] == s_TfGuid && (string?)k["TargetNode"]!["InstanceGuid"] == Confirm);
            // the shipped button: edited in place — focus, the property merged, the binding's source rewired, its shipped Released port reused for the new wire
            var s_Button = (JObject)s_Inst[Button]!;
            Assert.Equal(7, (int)s_Button["FocusIndex"]!);
            Assert.Equal("false", (string?)((JArray)s_Button["WidgetProperties"]!).First(x => (string?)x["Name"] == "Visible")["Value"]);
            Assert.Equal(42, (long)s_Inst[Binding]!["Bindings"]![0]!["DataKey"]!);
            Assert.Single((JArray)s_Button["Outputs"]!);
            Assert.Contains(s_Conns, k => (string?)k["SourcePort"]!["InstanceGuid"] == ButtonOut && (string?)k["TargetNode"]!["InstanceGuid"] == s_SetGuid);
            // every reference points at an instance of the partition or at another partition; the log tells the story
            foreach (var p in s_Inst.Properties())
                foreach (var r in ((JContainer)p.Value).Descendants().OfType<JObject>().Where(o => o["InstanceGuid"] != null && o["PartitionGuid"] != null && o.Count == 2))
                    if ((string?)r["PartitionGuid"] == Part) Assert.True(s_Inst[(string)r["InstanceGuid"]!] != null, $"{p.Name}: dangling reference {r["InstanceGuid"]}");
            Assert.Contains("[NODE] new WidgetNode TextField_02", s_Log.ToString());
            Assert.Contains("[WIRE] SetButtons.Out -> Confirm.In", s_Log.ToString());
            Assert.Contains("1 shipped wire(s) removed", s_Log.ToString());
            // deterministic: the same document lands on the same guids
            var s_Again = StaticGraph.Apply(s_Doc, s_Screen, Vanilla(), PartitionJson, new StringWriter());
            Assert.Equal(s_Out.ToString(), s_Again.ToString());
        }
    }
}
