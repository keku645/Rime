using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Cmd.UiBuilder;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Builds a complete VU UI mod from a screen document (JSON): edits the screens' Scaleform stages, generates the VeniceEXT " +
                        "that adds/edits/wires the EBX widget nodes at runtime, writes mod.json, and builds the superbundle. " +
                        "Document format: { name, superbundle, bundle, screens:[{ partition (a ui/flow/screen or ui/flow/graph asset), movie?, stage:[gfx_stage_edit ops], " +
                        "nodes:[{ instanceName, new, type (any UINodeData type, default WidgetNode), widget, properties:{}, binding:{dataName,dataKey,categoryFromNode|category}, " +
                        "fields:{Name:json} (typed by the game's type info; a port reference such as JumpNode.TargetPort = \"Node.port\"; " +
                        "a reference field may hold an instance inline as {\"$type\":\"UITextDataBinding\", ...fields} — created with a stable guid, or edited in place when the node already carries one of that type), " +
                        "ports:[{field,name,event?,inputEvent?}] (named array ports; inputEvent = a UIInputAction makes a UIInputEventNodePort), " +
                        "connections:[{event|fromPort,toNode,toEvent|toPort}] }] }], movies:[{resource,file}] }.")]
    public class UiBuildModCommand : Command
    {
        [CommandArgument(Description = "The screen document (.json).")]
        public FileInfo? Document { get; set; }

        [CommandArgument(Description = "The Mods folder the mod is written into (a folder named after the document's name is created/overwritten).")]
        public DirectoryInfo? ModsRoot { get; set; }

        [CommandArgument(Description = "Only write the mod folder and recipe; do not build the superbundle.", Optional = true)]
        public bool NoBuild { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            try
            {
                var s_Game = (GameContext)p_Context;
                var s_Doc = JsonConvert.DeserializeObject<ScreenDocument>(File.ReadAllText(Document!.FullName))
                            ?? throw new Exception("empty document");
                if (string.IsNullOrWhiteSpace(s_Doc.Name) || string.IsNullOrWhiteSpace(s_Doc.Superbundle) || string.IsNullOrWhiteSpace(s_Doc.Bundle))
                    throw new Exception("document needs name, superbundle and bundle");

                var s_Temp = Path.Combine(Path.GetTempPath(), "rime_ui_build_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(s_Temp);

                JObject PartitionJson(string p_Name)
                {
                    var s_File = new FileInfo(Path.Combine(s_Temp, p_Name.Replace('/', '_') + ".json"));
                    s_Game.DumpPartitionJson(p_Name, s_File, Formatting.None);
                    return JObject.Parse(File.ReadAllText(s_File.FullName));
                }

                byte[] ResourceBytes(string p_Name)
                {
                    var s_File = new FileInfo(Path.Combine(s_Temp, p_Name.Replace('/', '_') + ".gfx"));
                    s_Game.DumpResource(p_Name, s_File);
                    return File.ReadAllBytes(s_File.FullName);
                }

                string? PartitionNameByGuid(string p_Guid) =>
                    s_Game.GetMounter().TryGetPartitionByGuid(RimeUiService.PartitionGuid(p_Guid), out var s_Name, out _) ? s_Name : null;

                var s_Emitter = new ModEmitter(s_Doc, Document.DirectoryName!, ModsRoot!.FullName, PartitionJson, ResourceBytes, p_Writer, null, PartitionNameByGuid);
                s_Emitter.Emit();

                if (NoBuild)
                {
                    p_Writer.WriteLine($"recipe written (not run): {s_Emitter.RecipePath}");
                    return true;
                }

                // Drive the build the way the REPL would: build_sb (base context) -> build_bundle -> replace_resource... -> build -> build
                var s_Ctx = p_Context.Parent ?? throw new Exception("no base context to build from");
                foreach (var s_Line in s_Emitter.RecipeLines())
                {
                    p_Writer.WriteLine("> " + s_Line);
                    if (!s_Ctx.ProcessCommand(s_Line, p_Writer, out var s_Next) || s_Next == null)
                        throw new Exception("recipe step failed: " + s_Line);
                    s_Ctx = s_Next;
                }
                p_Writer.WriteLine($"UI mod built: {s_Emitter.ModDir}");
                return true;
            }
            catch (Exception s_Exception)
            {
                p_Writer.WriteLine("Failed to build UI mod: " + s_Exception.Message);
                return false;
            }
        }
    }
}
