using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using RimeShaderEditor.Emit;
using RimeShaderEditor.Graph;

namespace RimeShaderEditor;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int p_ProcessId);

    protected override void OnStartup(StartupEventArgs p_Args)
    {
        base.OnStartup(p_Args);

        var s_Arguments = p_Args.Args;
        if (s_Arguments.Length > 0 && s_Arguments[0].StartsWith("--"))
        {
            AttachConsole(-1);
            Shutdown(RunHeadless(s_Arguments));
            return;
        }

        new MainWindow().Show();
    }

    private static int RunHeadless(string[] p_Arguments)
    {
        try
        {
            switch (p_Arguments[0])
            {
                case "--emit":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --emit <graph.json> <out.hlsl>");
                        return 2;
                    }

                    return Emit(ShaderGraph.FromJson(File.ReadAllText(p_Arguments[1])), p_Arguments[2]);

                case "--selftest":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --selftest <out.hlsl> <out.json>");
                        return 2;
                    }

                    var s_Graph = SampleGraphs.VanillaEquivalent();
                    File.WriteAllText(p_Arguments[2], s_Graph.ToJson());
                    return Emit(s_Graph, p_Arguments[1]);

                case "--nodes":
                    foreach (var s_Def in Graph.Palette.All
                                 .OrderBy(p_D => p_D.Category).ThenBy(p_D => p_D.Kind))
                    {
                        var s_Params = s_Def.Params.Count == 0
                            ? "-"
                            : string.Join(" ", s_Def.Params.Select(p_P => $"{p_P.Name}({p_P.Kind})"));
                        var s_Editable = s_Def.Inputs.Where(p_P => p_P.IsEditable).Select(p_P => p_P.Name).ToList();
                        var s_Frozen = s_Def.Inputs.Where(p_P => !p_P.IsEditable).Select(p_P => p_P.Name).ToList();

                        Console.Out.WriteLine(
                            $"{s_Def.Category,-10} {s_Def.Kind,-20} params={s_Params}" +
                            $" | editable={(s_Editable.Count == 0 ? "-" : string.Join(",", s_Editable))}" +
                            $" | expr={(s_Frozen.Count == 0 ? "-" : string.Join(",", s_Frozen))}");
                    }

                    return 0;

                case "--nodetest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --nodetest <work-dir>");
                        return 2;
                    }

                    return NodeTest(p_Arguments[1]);

                case "--textures":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --textures <target-shader> <cache-dir> [game-path]");
                        return 2;
                    }

                    return LoadTextures(p_Arguments[1], p_Arguments[2],
                        p_Arguments.Length > 3 ? p_Arguments[3] : null);

                case "--ddstest":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --ddstest <in.dds> <out.png>");
                        return 2;
                    }

                    return DdsTest(p_Arguments[1], p_Arguments[2]);

                default:
                    Console.Error.WriteLine($"Unknown option '{p_Arguments[0]}'.");
                    return 2;
            }
        }
        catch (Exception s_Exception)
        {
            Console.Error.WriteLine($"FAILED: {s_Exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Puts every palette node into a one-node graph, emits it and runs it through fxc. A node that compiles
    /// nowhere is worse than a missing node, so the whole palette is checked rather than just the sample graph.
    /// </summary>
    private static int NodeTest(string p_WorkDir)
    {
        var s_Fxc = RimeShaderEditor.MainWindow.FindFxc();
        if (s_Fxc == null)
        {
            Console.Error.WriteLine("ERROR: fxc.exe not found.");
            return 1;
        }

        Directory.CreateDirectory(p_WorkDir);
        var s_Failures = 0;
        var s_Tested = 0;

        foreach (var s_Def in Graph.Palette.All.OrderBy(p_D => p_D.Kind))
        {
            if (!s_Def.IsRoot && s_Def.Outputs.Count == 0)
                continue;

            var s_Graph = new Graph.ShaderGraph { Name = $"nodetest_{s_Def.Kind}" };

            // Root nodes are exercised on their own; everything else is wired into a StandardRoot so the
            // emitter actually reaches it (unreachable nodes are not emitted at all).
            if (s_Def.IsRoot)
            {
                s_Graph.Nodes.Add(new Graph.GraphNode { Kind = s_Def.Kind, X = 100, Y = 0 });
            }
            else
            {
                var s_Node = new Graph.GraphNode { Kind = s_Def.Kind, X = -300, Y = 0 };
                var s_Root = new Graph.GraphNode { Kind = "StandardRoot", X = 100, Y = 0 };
                s_Graph.Nodes.Add(s_Node);
                s_Graph.Nodes.Add(s_Root);
                s_Graph.Connect(s_Node.Id, s_Def.Outputs[0].Name, s_Root.Id, "Diffuse");
            }

            s_Tested++;
            var s_Emit = new HlslEmitter().Emit(s_Graph);
            if (!s_Emit.Ok)
            {
                Console.Out.WriteLine($"FAIL {s_Def.Kind}: {string.Join("; ", s_Emit.Errors)}");
                s_Failures++;
                continue;
            }

            var s_HlslPath = Path.Combine(p_WorkDir, $"{s_Def.Kind}.hlsl");
            File.WriteAllText(s_HlslPath, s_Emit.Hlsl);

            var s_Process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(s_Fxc)
            {
                Arguments = $"/nologo /T ps_5_0 /E main /O1 /Fo \"{Path.ChangeExtension(s_HlslPath, ".dxbc")}\" \"{s_HlslPath}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (s_Process == null)
            {
                Console.Out.WriteLine($"FAIL {s_Def.Kind}: could not start fxc");
                s_Failures++;
                continue;
            }

            var s_Output = s_Process.StandardOutput.ReadToEnd() + s_Process.StandardError.ReadToEnd();
            s_Process.WaitForExit();

            if (s_Process.ExitCode != 0)
            {
                Console.Out.WriteLine($"FAIL {s_Def.Kind}: {s_Output.Trim().Replace("\r\n", " | ")}");
                s_Failures++;
            }
        }

        Console.Out.WriteLine($"NODETEST: {s_Tested - s_Failures}/{s_Tested} nodes compiled.");
        return s_Failures == 0 ? 0 : 1;
    }

    /// <summary>Runs the exact thumbnail pipeline the button uses, so it can be verified without the GUI.</summary>
    private static int LoadTextures(string p_Target, string p_CacheDir, string? p_GamePath)
    {
        var s_GamePath = p_GamePath ?? EditorSettings.DetectGamePath();
        if (string.IsNullOrWhiteSpace(s_GamePath))
        {
            Console.Error.WriteLine("ERROR: BF3 path not given and not detectable from the registry.");
            return 1;
        }

        var s_Repl = RimeShaderEditor.MainWindow.FindRimeRepl();
        if (s_Repl == null)
        {
            Console.Error.WriteLine("ERROR: RimeREPL.exe not found.");
            return 1;
        }

        Directory.CreateDirectory(p_CacheDir);

        // Same map filename the window uses, so a cache warmed from the command line is picked up by the GUI.
        var s_MapPath = Path.Combine(p_CacheDir,
            RimeShaderEditor.MainWindow.SlotMapFileName(p_Target));

        var s_Log = RimeShaderEditor.MainWindow.LoadTexturesCore(s_Repl, s_GamePath,
            RimeShaderEditor.MainWindow.ShaderDbForTarget(p_Target), p_Target, p_CacheDir, s_MapPath);

        foreach (var s_Line in s_Log)
            Console.Out.WriteLine(s_Line);

        return 0;
    }

    /// <summary>Decodes a DDS through the preview path and re-encodes it as PNG, so the result can be eyeballed.</summary>
    private static int DdsTest(string p_Input, string p_OutputPath)
    {
        var s_Image = View.DdsImage.Load(p_Input);
        if (s_Image == null)
        {
            Console.Error.WriteLine($"ERROR: could not decode {p_Input}");
            return 1;
        }

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Image));
        using var s_Stream = File.Create(p_OutputPath);
        s_Encoder.Save(s_Stream);

        Console.Out.WriteLine($"Decoded {s_Image.PixelWidth}x{s_Image.PixelHeight} -> {p_OutputPath}");
        return 0;
    }

    private static int Emit(ShaderGraph p_Graph, string p_OutputPath)
    {
        var s_Result = new HlslEmitter().Emit(p_Graph);
        foreach (var s_Error in s_Result.Errors)
            Console.Error.WriteLine($"ERROR: {s_Error}");

        if (!s_Result.Ok)
            return 1;

        File.WriteAllText(p_OutputPath, s_Result.Hlsl);
        Console.Out.WriteLine($"Wrote {p_OutputPath} ({s_Result.Hlsl.Length} chars, {p_Graph.Nodes.Count} nodes).");
        return 0;
    }
}
