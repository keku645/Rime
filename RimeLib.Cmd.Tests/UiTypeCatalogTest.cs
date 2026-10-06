using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.UiBuilder;
using Xunit;

namespace RimeLib.Cmd.Tests
{
    /// <summary>
    /// The UI object model comes from the game's type information, and the emitter turns a document's typed
    /// fields into the Lua the client runs: names spelled the VEXT way (In → inValue), enums by type and member,
    /// structs and lists nested, references collected as assets to load.
    /// </summary>
    public class UiTypeCatalogTest
    {
        static UiTypeCatalogTest()
        {
            // the catalogue reflects over the serialization assembly; make sure it is loaded in the test host
            _ = typeof(fb.DialogNode);
        }

        [Fact]
        public void DescribesDialogNodeFromTheGameTypes()
        {
            var t = UiTypeCatalog.Describe("DialogNode")!;
            Assert.True(t.IsNode);
            Assert.Equal("StateNode", t.BaseName);
            Assert.Equal(UiTypeCatalog.Kind.String, t.Field("DialogTitle")!.Kind);
            var s_Buttons = t.Field("Buttons")!;
            Assert.Equal(UiTypeCatalog.Kind.List, s_Buttons.Kind);
            Assert.Equal(UiTypeCatalog.Kind.Struct, s_Buttons.ElementKind);
            Assert.Equal("UIPopupButton", s_Buttons.TypeName);
            Assert.Equal(UiTypeCatalog.Kind.Ref, t.Field("Screen")!.Kind);
            Assert.Equal(UiTypeCatalog.Kind.Port, t.Field("In")!.Kind);
            Assert.Equal("inValue", t.Field("In")!.LuaName);
            Assert.Equal(UiTypeCatalog.Kind.PortArray, t.Field("Outputs")!.Kind);
            Assert.Contains("Name", t.Fields.Select(f => f.Name)); // inherited from UINodeData, listed first
            Assert.Equal("Name", t.Fields[0].Name);
            Assert.Equal(UiTypeCatalog.Kind.Enum, UiTypeCatalog.Describe("UIPopupButton")!.Field("InputConcept")!.Kind);
        }

        /// <summary>
        /// DataKey = fb hash of "UI_&lt;component&gt;_&lt;source&gt;" upper-cased — the numbers measured in the shipped screens
        /// (the accessories screen's SetButtons node, its KitSelector bindings) and the constants the game's own
        /// ActionScript passes to UIDataInterfaceComp.getData (fb.HelperFunctions).
        /// </summary>
        [Fact]
        public void DataKeysAreTheUpperCasedComponentSourceHash()
        {
            Assert.Equal(-641143785, DataKeys.FbHash("Confirm"));                                              // the InstanceOutputNode id hash, same function
            Assert.Equal(1631675365, DataKeys.Compute("UI/UIComponents/UICustomizationComp", "ButtonLayout"));
            Assert.Equal(-991137952, DataKeys.Compute("UI/UIComponents/UICustomizationComp", "CustSelectedAccessory1"));
            Assert.Equal(-991137947, DataKeys.Compute("UICustomizationComp", "CustSelectedAccessory4"));
            Assert.Equal(-668821714, DataKeys.Compute("UI/UIComponents/UISettingsComp", "Platform"));           // fb.HelperFunctions.getPlatform
            Assert.Equal(-1791838705, DataKeys.Compute("UI/UIComponents/FrontEndComp", "InFrontend"));          // getIsInFrontend
            Assert.Equal(213350777, DataKeys.Compute("UI/UIComponents/UIVehicleComp", "ShortName"));            // getIsVehicleRussian
            Assert.Equal("UICustomizationComp", DataKeys.ComponentShortName("UI/UIComponents/UICustomizationComp"));
            var s_Table = DataKeys.Table("UI/UIComponents/UICustomizationComp", new[] { "ButtonLayout", "CustSelectedAccessory1" });
            Assert.Equal("ButtonLayout", s_Table[1631675365]);
        }

        [Fact]
        public void LuaSpellingFollowsVext()
        {
            Assert.Equal("inValue", UiTypeCatalog.LuaName("In"));
            Assert.Equal("trueValue", UiTypeCatalog.LuaName("True"));
            Assert.Equal("falseValue", UiTypeCatalog.LuaName("False"));
            Assert.Equal("out", UiTypeCatalog.LuaName("Out"));
            Assert.Equal("dialogTitle", UiTypeCatalog.LuaName("DialogTitle"));
            Assert.Equal("dataSourceInfo", UiTypeCatalog.LuaName("DataSourceInfo"));
        }

        [Fact]
        public void CatalogueListsNodeTypesBindingsAndEnums()
        {
            var s_Nodes = UiTypeCatalog.NodeTypes().ToList();
            foreach (var n in new[] { "WidgetNode", "DataSetNode", "DataGetNode", "ActionNode", "StateNode", "DialogNode", "ComparisonLogicNode", "BinaryLogicNode", "InstanceInputNode", "InstanceOutputNode", "SplitterNode", "JumpNode", "RefreshNode" })
                Assert.Contains(n, s_Nodes);
            Assert.Contains("UIListDataBinding", UiTypeCatalog.BindingTypes());
            Assert.Contains("UIInputAction_Activate", UiTypeCatalog.EnumMembers("UIInputAction"));
            Assert.Contains("UIWidgetEventID_OnItemOver", UiTypeCatalog.EnumMembers("UIWidgetEventID"));
            Assert.Equal(UiTypeCatalog.Kind.Struct, UiTypeCatalog.Describe("DataSetNode")!.Field("DataSource")!.Kind);
            Assert.Equal("UIDataSourceInfo", UiTypeCatalog.Describe("DataSetNode")!.Field("DataSource")!.TypeName);
        }

        /// <summary>
        /// The port slots the shipped UI data actually uses (measured over every ui/flow partition of the game:
        /// 4594 nodes, 4504 connections) must be what the catalogue calls ports, with the direction the connections
        /// give them: a slot only ever named as a connection target is an input, a source is an output.
        /// JumpNode.TargetPort is the one CtrRef&lt;UINodePort&gt; that is not the node's own port — it points at
        /// another node's port (all 3 shipped JumpNodes reference an ActionNode's In).
        /// </summary>
        [Fact]
        public void PortSlotsMatchTheShippedData()
        {
            var s_Measured = new (string Type, string Field, bool Array, bool Input)[]
            {
                ("ActionNode", "In", false, true), ("ActionNode", "Out", false, false),
                ("BinaryLogicNode", "In", false, true), ("BinaryLogicNode", "True", false, false), ("BinaryLogicNode", "False", false, false),
                ("ComparisonLogicNode", "In", false, true), ("ComparisonLogicNode", "Outputs", true, false),
                ("DataGetNode", "In", false, true), ("DataGetNode", "Out", false, false),
                ("DataIncrementNode", "In", false, true), ("DataIncrementNode", "Out", false, false),
                ("DataSetNode", "In", false, true), ("DataSetNode", "Out", false, false),
                ("DataStepNode", "In", false, true), ("DataStepNode", "Out", false, false),
                ("DataToggleNode", "In", false, true), ("DataToggleNode", "Out", false, false),
                ("DialogNode", "In", false, true), ("DialogNode", "Show", false, true), ("DialogNode", "Hide", false, true), ("DialogNode", "Inputs", true, true), ("DialogNode", "Outputs", true, false),
                ("InstanceInputNode", "Out", false, false), ("InstanceOutputNode", "In", false, true),
                ("JumpNode", "In", false, true),
                ("OperandLogicNode", "In", false, true), ("OperandLogicNode", "True", false, false), ("OperandLogicNode", "False", false, false),
                ("RefreshNode", "In", false, true), ("RefreshNode", "Out", false, false),
                ("SplitterNode", "In", false, true), ("SplitterNode", "Outputs", true, false),
                ("StateNode", "In", false, true), ("StateNode", "Show", false, true), ("StateNode", "Hide", false, true), ("StateNode", "Inputs", true, true), ("StateNode", "Outputs", true, false),
                ("WidgetNode", "Inputs", true, true), ("WidgetNode", "Outputs", true, false),
            };
            var s_Problems = new List<string>();
            foreach (var (s_Type, s_Field, s_Array, s_Input) in s_Measured)
            {
                var t = UiTypeCatalog.Describe(s_Type);
                var f = t?.Field(s_Field);
                if (f == null) { s_Problems.Add($"{s_Type}.{s_Field}: not in the catalogue"); continue; }
                var s_Expected = s_Array ? UiTypeCatalog.Kind.PortArray : UiTypeCatalog.Kind.Port;
                if (f.Kind != s_Expected) s_Problems.Add($"{s_Type}.{s_Field}: catalogue says {f.Kind}, data says {s_Expected}");
                if (UiTypeCatalog.PortIsInput(s_Field) != s_Input) s_Problems.Add($"{s_Type}.{s_Field}: direction {(s_Input ? "input" : "output")} in the data");
                if (UiTypeCatalog.IsPortReference(s_Type, s_Field)) s_Problems.Add($"{s_Type}.{s_Field}: is an owned port in the data, not a reference");
            }
            Assert.True(UiTypeCatalog.IsPortReference("JumpNode", "TargetPort"));
            Assert.Equal(UiTypeCatalog.Kind.Port, UiTypeCatalog.Describe("JumpNode")!.Field("TargetPort")!.Kind);
            // every port slot the catalogue knows, so a type the data never uses is still visible in the test output
            var s_All = string.Join("\n", UiTypeCatalog.NodeTypes().Select(n =>
                n + ": " + string.Join(", ", UiTypeCatalog.Describe(n)!.Fields.Where(f => f.Kind is UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray)
                    .Select(f => f.Name + (f.Kind == UiTypeCatalog.Kind.PortArray ? "[]" : "") + (UiTypeCatalog.IsPortReference(n, f.Name) ? " (ref)" : UiTypeCatalog.PortIsInput(f.Name) ? " in" : " out")))));
            Assert.True(s_Problems.Count == 0, string.Join("\n", s_Problems) + "\n--- catalogue port slots ---\n" + s_All);
            // the slots the catalogue has beyond the measured ones are not silently wrong: they must at least be ports of a known direction
            foreach (var n in UiTypeCatalog.NodeTypes())
                foreach (var f in UiTypeCatalog.Describe(n)!.Fields.Where(f => f.Kind is UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray))
                    Assert.True(UiTypeCatalog.IsPortReference(n, f.Name) || f.Name is "In" or "Out" or "True" or "False" or "Show" or "Hide" or "Inputs" or "Outputs" or "DataInputs",
                        $"{n}.{f.Name}: a port slot with no measured direction\n--- catalogue port slots ---\n{s_All}");
        }

        /// <summary>
        /// A screen made from nothing is the template partition under fresh, stable guids: the asset renamed, every
        /// instance remapped, in-partition references following, references to other partitions untouched; the emitter
        /// ships its movie and its partition as new entries of the bundle and the Lua addresses it by the new guids.
        /// </summary>
        [Fact]
        public void NewScreenClonesTheTemplateUnderItsOwnGuids()
        {
            var s_Template = JObject.Parse("{\"PrimaryInstanceGuid\":\"aaaa0001-0000-0000-0000-000000000001\",\"Instances\":{" +
                "\"aaaa0001-0000-0000-0000-000000000001\":{\"$type\":\"UIScreenAsset\",\"Name\":\"UI/Flow/Screen/EmptyScreen\",\"Nodes\":[{\"PartitionGuid\":\"pppp0000-0000-0000-0000-000000000000\",\"InstanceGuid\":\"aaaa0002-0000-0000-0000-000000000002\"}],\"AudioEventMappings\":{\"PartitionGuid\":\"6a08b142-79de-11df-9746-96335f6d5957\",\"InstanceGuid\":\"41b4640b-1973-8469-a254-b1b23e4e6a04\"},\"Modal\":false}," +
                "\"aaaa0002-0000-0000-0000-000000000002\":{\"$type\":\"WidgetNode\",\"Name\":\"InputListener\",\"ParentGraph\":{\"PartitionGuid\":\"pppp0000-0000-0000-0000-000000000000\",\"InstanceGuid\":\"aaaa0001-0000-0000-0000-000000000001\"},\"WidgetAsset\":{\"PartitionGuid\":\"59eff55f-0865-11e0-b50f-e068fe717e00\",\"InstanceGuid\":\"1db93750-a520-ee75-2c5e-736a458a5c91\"},\"InstanceName\":\"InputListener_01\"}}," +
                "\"Name\":\"ui/flow/screen/emptyscreen\",\"PartitionGuid\":\"pppp0000-0000-0000-0000-000000000000\"}");
            var s_New = NewScreen.PartitionJson("ui/flow/screen/mymenu", "MyMenu", s_Template);
            var s_Again = NewScreen.PartitionJson("ui/flow/screen/mymenu", "MyMenu", s_Template);
            Assert.Equal(s_New.ToString(), s_Again.ToString());   // stable
            Assert.NotEqual("pppp0000-0000-0000-0000-000000000000", (string)s_New["PartitionGuid"]!);
            Assert.Equal("ui/flow/screen/mymenu", (string)s_New["Name"]!);
            var s_Instances = (JObject)s_New["Instances"]!;
            Assert.Equal(2, s_Instances.Count);
            var s_Primary = (string)s_New["PrimaryInstanceGuid"]!;
            Assert.NotEqual("aaaa0001-0000-0000-0000-000000000001", s_Primary);
            Assert.Equal("UI/Flow/Screen/MyMenu", (string)s_Instances[s_Primary]!["Name"]!);
            var s_NodeRef = (JObject)s_Instances[s_Primary]!["Nodes"]![0]!;
            Assert.Equal((string)s_New["PartitionGuid"]!, (string)s_NodeRef["PartitionGuid"]!);
            Assert.True(s_Instances.ContainsKey((string)s_NodeRef["InstanceGuid"]!));   // the node's new guid
            var s_Node = (JObject)s_Instances[(string)s_NodeRef["InstanceGuid"]!]!;
            Assert.Equal(s_Primary, (string)s_Node["ParentGraph"]!["InstanceGuid"]!);
            Assert.Equal("59eff55f-0865-11e0-b50f-e068fe717e00", (string)s_Node["WidgetAsset"]!["PartitionGuid"]!);   // another partition: untouched
            Assert.Equal("6a08b142-79de-11df-9746-96335f6d5957", (string)s_Instances[s_Primary]!["AudioEventMappings"]!["PartitionGuid"]!);
            Assert.NotEqual(NewScreen.PartitionGuid("ui/flow/screen/mymenu"), NewScreen.PartitionGuid("ui/flow/screen/other"));
            Assert.Equal("ui/flow/screen/mymenu2", NewScreen.PartitionName("My Menu-2"));   // only letters, digits and _ survive
        }

        [Fact]
        public void EmitterWritesTypedFieldsPortsAndAssets()
        {
            var s_Dir = Path.Combine(Path.GetTempPath(), "rime_ui_emit_test");
            if (Directory.Exists(s_Dir)) Directory.Delete(s_Dir, true);
            var s_Doc = new ScreenDocument { Name = "EmitTest", Superbundle = "emittest/ui", Bundle = "emittest/uibundle" };
            var s_Screen = new ScreenEntry { Partition = "ui/flow/screen/testscreen" };
            s_Screen.Nodes.Add(new NodeEntry
            {
                InstanceName = "Ask", Type = "DialogNode",
                Fields = new Dictionary<string, JToken>
                {
                    ["DialogTitle"] = "ID_M_POPUP_QUIT_TO_MENU",
                    ["Screen"] = "ui/flow/screen/popups/popupgeneric",
                    ["Buttons"] = JArray.Parse("[{\"InputConcept\":\"Activate\",\"Label\":\"ID_M_YES\"},{\"InputConcept\":\"UIInputAction_Deactivate\",\"Label\":\"ID_M_NO\"}]"),
                    ["RenderToTexture"] = false,
                },
                Ports = { new PortEntry { Field = "Outputs", Name = "ID_M_YES" } },
                Connections = { new ConnectionEntry { FromPort = "Outputs:ID_M_YES", ToNode = "Quit", ToPort = "In" } },
            });
            s_Screen.Nodes.Add(new NodeEntry
            {
                InstanceName = "SetIt", Type = "DataSetNode",
                Fields = new Dictionary<string, JToken>
                {
                    ["Param"] = "1",
                    ["DataSource"] = JObject.Parse("{\"DataName\":\"\",\"DataKey\":123,\"DataCategory\":\"ui/uicomponents/uicustomizationcomp\",\"UseDirectAccess\":false,\"UpdateOnInitialize\":true}"),
                },
                Connections = { new ConnectionEntry { FromPort = "Out", ToNode = "Ask", ToPort = "Show" } },
            });
            // a JumpNode points at another node's port (a reference, not a port of its own); a StateNode output that listens to a controller action
            s_Screen.Nodes.Add(new NodeEntry
            {
                InstanceName = "JumpBack", Type = "JumpNode",
                Fields = new Dictionary<string, JToken> { ["TargetPort"] = "SetIt.In" },
            });
            s_Screen.Nodes.Add(new NodeEntry
            {
                InstanceName = "Page", Type = "StateNode",
                Fields = new Dictionary<string, JToken> { ["Screen"] = "ui/flow/screen/popups/popupgeneric" },
                Ports = { new PortEntry { Field = "Outputs", Name = "Back", InputEvent = "Back" }, new PortEntry { Field = "Inputs", Name = "EnterScreen" } },
                Connections = { new ConnectionEntry { FromPort = "Outputs:Back", ToNode = "Quit", ToPort = "In" } },
            });
            // a widget whose binding is described inline (any binding type): an instance the client edits in place or creates
            s_Screen.Nodes.Add(new NodeEntry
            {
                InstanceName = "TextField_09", Type = "WidgetNode", New = false,
                Fields = new Dictionary<string, JToken>
                {
                    ["DataBinding"] = JObject.Parse("{\"$type\":\"UITextDataBinding\",\"StaticText\":\"ID_M_YES\",\"Refresh\":true,\"TextData\":{\"DataName\":\"\",\"DataCategory\":\"ui/uicomponents/uicustomizationcomp\",\"DataKey\":5,\"UseDirectAccess\":false,\"UpdateOnInitialize\":true}}"),
                },
            });
            // the screen asset's own fields
            s_Screen.Fields["Modal"] = true;
            s_Screen.Fields["BundleAssetName"] = "";
            // a shipped wire the document disconnects (Alt+click in the editor): named by the connection's instance guid
            s_Screen.RemovedConnections.Add(new RemovedConnectionEntry { Guid = "77777777-7777-7777-7777-777777777777", From = "Button_02.OnItemReleased", To = "Confirm.In" });
            s_Doc.Screens.Add(s_Screen);
            var s_Guids = new Dictionary<string, (string, string)>
            {
                ["ui/flow/screen/testscreen"] = ("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"),
                ["ui/flow/screen/popups/popupgeneric"] = ("33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444"),
                ["ui/uicomponents/uicustomizationcomp"] = ("55555555-5555-5555-5555-555555555555", "66666666-6666-6666-6666-666666666666"),
            };
            JObject Json(string n) => new() { ["PartitionGuid"] = s_Guids[n].Item1, ["PrimaryInstanceGuid"] = s_Guids[n].Item2 };
            var s_Log = new StringWriter();
            var s_Emitter = new ModEmitter(s_Doc, s_Dir, s_Dir, Json, _ => throw new IOException("no movies in this test"), s_Log);
            s_Emitter.Emit();
            var s_Lua = File.ReadAllText(Path.Combine(s_Dir, "EmitTest", "ext", "Client", "__init__.lua"));
            Assert.Contains("type = \"DialogNode\"", s_Lua);
            Assert.Contains("{ name = \"dialogTitle\", kind = \"string\", value = \"ID_M_POPUP_QUIT_TO_MENU\" }", s_Lua);
            Assert.Contains("{ name = \"screen\", kind = \"ref\", asset = \"ui/flow/screen/popups/popupgeneric\", type = \"UIScreenAsset\" }", s_Lua);
            Assert.Contains("kind = \"list\", items = { { kind = \"struct\", type = \"UIPopupButton\", fields = { { name = \"inputConcept\", kind = \"enum\", enum = \"UIInputAction\", value = \"UIInputAction_Activate\" }, { name = \"label\", kind = \"string\", value = \"ID_M_YES\" } } }", s_Lua);
            Assert.Contains("{ name = \"renderToTexture\", kind = \"bool\", value = false }", s_Lua);
            Assert.Contains("{ field = \"inValue\", name = \"In\"", s_Lua);   // single ports of a DialogNode (StateNode): In, Show, Hide
            Assert.Contains("{ field = \"show\", name = \"Show\"", s_Lua);
            Assert.Contains("ports = { { field = \"outputs\", name = \"ID_M_YES\"", s_Lua);
            Assert.Contains("fromPort = \"outputs:ID_M_YES\"", s_Lua);
            Assert.Contains("toPort = \"inValue\"", s_Lua);
            // every connection carries a guid for the target's input port too: a widget target whose contract has the event
            // but no shipped port for it gets one created (the client's Wire does it, [PORT] in the log)
            Assert.Contains("targetPortGuid = \"", s_Lua);
            Assert.Contains("s_Target.inputs:add(l_In)", s_Lua);
            Assert.Contains("toPort = \"show\"", s_Lua);
            Assert.Contains("{ name = \"dataSource\", kind = \"struct\", type = \"UIDataSourceInfo\", fields = {", s_Lua);
            Assert.Contains("{ name = \"dataCategory\", kind = \"ref\", asset = \"ui/uicomponents/uicustomizationcomp\", type = \"UIComponentData\" }", s_Lua);
            Assert.Contains("[\"ui/uicomponents/uicustomizationcomp\"] = { partition = Guid(\"55555555-5555-5555-5555-555555555555\")", s_Lua);
            Assert.Contains("[\"ui/flow/screen/popups/popupgeneric\"] = { partition = Guid(\"33333333-3333-3333-3333-333333333333\")", s_Lua);
            // the port reference is resolved at runtime, never created as a port; the JumpNode's own port is In only
            Assert.Contains("{ name = \"targetPort\", kind = \"portref\", node = \"SetIt\", port = \"inValue\" }", s_Lua);
            Assert.DoesNotContain("field = \"targetPort\", name = \"TargetPort\"", s_Lua);
            // a controller-action port carries its UIInputAction
            Assert.Contains("{ field = \"outputs\", name = \"Back\", event = nil, inputEvent = \"Back\"", s_Lua);
            // a disconnected shipped wire travels by guid and the client erases it from the screen's connections before the nodes are applied
            Assert.Contains("removedConnections = {", s_Lua);
            Assert.Contains("{ guid = \"77777777-7777-7777-7777-777777777777\", from = \"Button_02.OnItemReleased\", to = \"Confirm.In\" }", s_Lua);
            Assert.Contains("p_Screen.connections:erase(s_Index - 1)", s_Lua);
            Assert.Contains("[UNWIRE]", s_Lua);
            Assert.True(s_Lua.IndexOf("Unwire(s_Screen, p_ScreenData)", System.StringComparison.Ordinal) < s_Lua.IndexOf("ApplyNode(p_ScreenData, s_Screen, l_Node)", System.StringComparison.Ordinal), "the wires must be erased before the nodes are applied");
            Assert.Contains("{ field = \"inputs\", name = \"EnterScreen\", event = nil, inputEvent = nil", s_Lua);
            Assert.Contains("fromPort = \"outputs:Back\"", s_Lua);
            Assert.Contains("isScreen = true", s_Lua);
            // the inline binding: an instance with a stable guid, its fields typed by the binding type (a struct with a component reference inside)
            Assert.Contains("{ name = \"dataBinding\", kind = \"instance\", type = \"UITextDataBinding\", guid = \"", s_Lua);
            Assert.Contains("{ name = \"staticText\", kind = \"string\", value = \"ID_M_YES\" }", s_Lua);
            Assert.Contains("{ name = \"textData\", kind = \"struct\", type = \"UIDataSourceInfo\", fields = { { name = \"dataName\", kind = \"string\", value = \"\" }, { name = \"dataCategory\", kind = \"ref\", asset = \"ui/uicomponents/uicustomizationcomp\", type = \"UIComponentData\" }, { name = \"dataKey\", kind = \"number\", value = 5 }", s_Lua);
            Assert.Contains("WriteInstance(p_Node, l_Field)", s_Lua);
            Assert.Contains("MakeWritable", s_Lua);
            // the screen's own fields, written on the asset before the nodes
            Assert.Contains("\t\tfields = {\n\t\t\t{ name = \"modal\", kind = \"bool\", value = true },\n\t\t\t{ name = \"bundleAssetName\", kind = \"string\", value = \"\" },", s_Lua.Replace("\r\n", "\n"));
            Assert.Contains("WriteFields(s_Screen, p_ScreenData.fields)", s_Lua);
            Assert.DoesNotContain("WARNING", s_Log.ToString());
        }
    }
}
