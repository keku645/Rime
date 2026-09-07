using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Rime.Utils.ModMerger;

/// <summary>
/// Folds several shader mods into ONE installable mod. Each input mod stays a normal, self-contained
/// export; this tool exists only for the player who installs several of them at once, because two mods
/// carrying a database for the SAME map fight over it — bundle order decides which one answers and the
/// loser's shaders silently vanish. Maps covered by a single mod pass through untouched; maps covered by
/// several get their databases merged entry by entry (conflicts are refused loudly, never last-one-wins).
///
/// Usage: put the mod folders into the "mods" directory next to this executable and run it. The result
/// lands in "MergedShaderPack", ready to copy into the server's Mods directory.
/// </summary>
internal static class Program
{
    private const string c_OutputName = "MergedShaderPack";

    private static string s_BaseDir = "";
    private static string s_ReplPath = "";
    private static string s_GamePath = "";
    private static string s_WorkDir = "";

    private static int Main()
    {
        Console.WriteLine("== Shader Mod Merger ==");
        Console.WriteLine();

        s_BaseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        s_WorkDir = Path.Combine(s_BaseDir, "work");

        try
        {
            return Run();
        }
        catch (Exception s_Exception)
        {
            Console.WriteLine();
            Console.WriteLine($"FAILED: {s_Exception.Message}");
            return Pause(1);
        }
    }

    private static int Run()
    {
        // The command-line worker does all the heavy lifting; this tool only orchestrates it.
        s_ReplPath = new[] { s_BaseDir, Path.Combine(s_BaseDir, "repl") }
            .Select(p_D => Path.Combine(p_D, "RimeREPL.exe"))
            .FirstOrDefault(File.Exists) ?? "";

        if (s_ReplPath.Length == 0)
        {
            Console.WriteLine("RimeREPL.exe was not found next to this executable (or in a 'repl' folder).");
            Console.WriteLine("This tool ships together with it - keep the two in the same directory.");
            return Pause(1);
        }

        s_GamePath = ResolveGamePath();
        if (s_GamePath.Length == 0)
            return Pause(1);

        var s_ModsDir = Path.Combine(s_BaseDir, "mods");
        Directory.CreateDirectory(s_ModsDir);

        var s_Mods = ScanMods(s_ModsDir);
        if (s_Mods.Count == 0)
        {
            Console.WriteLine($"No mods found. Put the mod folders you want to combine into:");
            Console.WriteLine($"  {s_ModsDir}");
            Console.WriteLine("(each folder is one mod: mod.json + sb\\Win32\\...), then run this again.");
            return Pause(1);
        }

        Console.WriteLine($"Found {s_Mods.Count} mod(s):");
        foreach (var s_Mod in s_Mods)
            Console.WriteLine($"  {s_Mod.Name}: {string.Join(", ", s_Mod.Levels.Select(p_L => p_L.Level))}");
        Console.WriteLine();

        if (Directory.Exists(s_WorkDir))
            Directory.Delete(s_WorkDir, true);
        Directory.CreateDirectory(s_WorkDir);

        var s_OutDir = Path.Combine(s_BaseDir, c_OutputName);
        if (Directory.Exists(s_OutDir))
            Directory.Delete(s_OutDir, true);
        Directory.CreateDirectory(s_OutDir);

        // Group per level: a level served by ONE mod passes through byte-for-byte (no game mount, no
        // rebuild - there is nothing to unify); only levels served by SEVERAL mods get the real merge.
        var s_ByLevel = s_Mods
            .SelectMany(p_M => p_M.Levels.Select(p_L => (Mod: p_M, p_L.Level, p_L.Namespace, p_L.SbPath, p_L.HasVariations)))
            .GroupBy(p_E => p_E.Level, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_G => p_G.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var s_Rows = new List<(string Level, string Super, bool HasVariations)>();

        foreach (var s_Group in s_ByLevel)
        {
            var s_Entries = s_Group.ToList();

            if (s_Entries.Count == 1)
            {
                var s_Single = s_Entries[0];
                var s_Target = Path.Combine(s_OutDir, "sb", "Win32", s_Single.Namespace);
                Directory.CreateDirectory(s_Target);
                File.Copy(s_Single.SbPath, Path.Combine(s_Target, Path.GetFileName(s_Single.SbPath)));
                File.Copy(Path.ChangeExtension(s_Single.SbPath, ".toc"),
                    Path.Combine(s_Target, Path.GetFileName(Path.ChangeExtension(s_Single.SbPath, ".toc"))));

                s_Rows.Add((s_Single.Level, $"{s_Single.Namespace}/{s_Single.Level.ToLowerInvariant()}",
                    s_Single.HasVariations));
                Console.WriteLine($"[{s_Single.Level}] only '{s_Single.Mod.Name}' touches it - passed through.");
                continue;
            }

            Console.WriteLine($"[{s_Group.Key}] {s_Entries.Count} mods touch it - unifying " +
                              $"({string.Join(" + ", s_Entries.Select(p_E => p_E.Mod.Name))})...");

            var s_HasVariations = MergeLevel(s_Group.Key, s_Entries
                .Select(p_E => (p_E.Mod.Name, p_E.SbPath, p_E.HasVariations)).ToList(), s_OutDir);

            s_Rows.Add((s_Group.Key, $"merged/{s_Group.Key.ToLowerInvariant()}", s_HasVariations));
        }

        WriteModFiles(s_OutDir, s_Rows, s_Mods.Select(p_M => p_M.Name).ToList());

        Console.WriteLine();
        Console.WriteLine($"DONE. The combined mod is ready in:");
        Console.WriteLine($"  {s_OutDir}");
        Console.WriteLine($"Copy the '{c_OutputName}' folder into the server's Admin\\Mods directory, add a");
        Console.WriteLine($"'{c_OutputName}' line to ModList.txt, and REMOVE (or comment out with '#') the");
        Console.WriteLine("lines of the mods it replaces - running both would fight over the same maps again.");
        return Pause(0);
    }

    // ── input scanning ─────────────────────────────────────────────────────────────────────────────

    private sealed record ModLevel(string Level, string Namespace, string SbPath, bool HasVariations);

    private sealed record InputMod(string Name, string Folder, List<ModLevel> Levels);

    private static List<InputMod> ScanMods(string p_ModsDir)
    {
        var s_Result = new List<InputMod>();

        foreach (var s_Folder in Directory.EnumerateDirectories(p_ModsDir))
        {
            var s_Name = Path.GetFileName(s_Folder);
            if (!File.Exists(Path.Combine(s_Folder, "mod.json")))
            {
                Console.WriteLine($"  (skipping '{s_Name}': no mod.json - not a mod folder)");
                continue;
            }

            var s_Levels = new List<ModLevel>();
            var s_SbRoot = Path.Combine(s_Folder, "sb", "Win32");
            if (Directory.Exists(s_SbRoot))
                foreach (var s_Sb in Directory.EnumerateFiles(s_SbRoot, "*.sb", SearchOption.AllDirectories))
                {
                    if (!File.Exists(Path.ChangeExtension(s_Sb, ".toc")))
                        continue;

                    var s_Level = Path.GetFileNameWithoutExtension(s_Sb);
                    var s_Namespace = Path.GetFileName(Path.GetDirectoryName(s_Sb))!;
                    var s_Bytes = File.ReadAllBytes(s_Sb);

                    // Only the known export shape merges safely: a database bundle named <ns>/<level>b,
                    // optionally a variation bundle <ns>/<level>v. Anything else is refused rather than
                    // half-merged into something that looks installable and is not.
                    if (!Contains(s_Bytes, $"{s_Namespace}/{s_Level}b"))
                    {
                        Console.WriteLine($"  (skipping '{s_Name}/{s_Level}': not a shader-editor export)");
                        continue;
                    }

                    s_Levels.Add(new ModLevel(s_Level, s_Namespace, s_Sb,
                        Contains(s_Bytes, $"{s_Namespace}/{s_Level}v")));
                }

            if (s_Levels.Count > 0)
                s_Result.Add(new InputMod(s_Name, s_Folder, s_Levels));
            else
                Console.WriteLine($"  (skipping '{s_Name}': no shader superbundles inside)");
        }

        return s_Result;
    }

    private static bool Contains(byte[] p_Haystack, string p_Needle) =>
        ((ReadOnlySpan<byte>) p_Haystack).IndexOf(Encoding.ASCII.GetBytes(p_Needle)) >= 0;

    // ── the per-level unification ──────────────────────────────────────────────────────────────────

    private static bool MergeLevel(string p_Level, List<(string Mod, string SbPath, bool HasVariations)> p_Inputs,
        string p_OutDir)
    {
        var s_Level = p_Level.ToLowerInvariant();
        var s_Work = Path.Combine(s_WorkDir, s_Level);
        Directory.CreateDirectory(s_Work);

        var s_DbName = $"levels/{s_Level}/{s_Level}/shaderdb";

        // Step 1 - one worker run PER INPUT: its database out to a file, its bundle contents inventoried.
        // One at a time on purpose: every input carries a database resource under the SAME name, and two
        // mounted at once would leave "which one answered the dump?" to luck.
        var s_Inventories = new List<(string Mod, List<string> Partitions, List<string> Resources, List<string> Chunks)>();

        for (var i = 0; i < p_Inputs.Count; i++)
        {
            var s_Ns = Path.GetFileName(Path.GetDirectoryName(p_Inputs[i].SbPath))!;
            var s_Script = new StringBuilder();
            s_Script.AppendLine($"mount_game \"{s_GamePath}\" Frostbite2_0 true");
            s_Script.AppendLine("select_game 1");
            if (i == 0)
                s_Script.AppendLine($"dump_resource {s_DbName} \"{Path.Combine(s_Work, "vanilla.bin")}\"");
            s_Script.AppendLine($"mount_standalone_sb Win32/mergein{i}/{s_Level} \"{p_Inputs[i].SbPath}\" true");
            // "true" = the LAST-mounted variant: the database name exists in the game's own mount too, and
            // dumping the first variant silently reads the GAME's copy — a merge of two vanilla databases
            // that reports success and ships a pack with every mod's shaders missing (measured).
            s_Script.AppendLine($"dump_resource {s_DbName} \"{Path.Combine(s_Work, $"db_{i}.bin")}\" true");
            s_Script.AppendLine($"list_bundle_partitions win32/{s_Ns}/{s_Level}v");
            s_Script.AppendLine($"list_bundle_resources win32/{s_Ns}/{s_Level}b");
            s_Script.AppendLine($"list_bundle_chunks win32/{s_Ns}/{s_Level}b");
            s_Script.AppendLine("exit");
            s_Script.AppendLine("exit");

            var s_Output = RunRepl(s_Work, $"inventory_{i}", s_Script.ToString());
            s_Inventories.Add((p_Inputs[i].Mod,
                ParseListing(s_Output, "Partitions:"),
                ParseListing(s_Output, "Resources:"),
                ParseListing(s_Output, "Chunks:")));

            if (!File.Exists(Path.Combine(s_Work, $"db_{i}.bin")))
                throw new Exception($"could not read the shader database out of '{p_Inputs[i].Mod}' " +
                                    $"(see {s_Work}\\inventory_{i}.log)");

            // A mod's database that reads byte-identical to the game's own means the read went to the
            // WRONG copy of the colliding name (or the mod is hollow) — merging it would "succeed" and
            // ship a pack with that mod's shaders silently missing.
            if (File.ReadAllBytes(Path.Combine(s_Work, $"db_{i}.bin"))
                .AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(s_Work, "vanilla.bin"))))
                throw new Exception($"'{p_Inputs[i].Mod}' carries an untouched database for {p_Level} - " +
                                    "it is not a real shader export, or the read failed.");
        }

        // Conflicts that no merge can resolve are refused BEFORE any building starts.
        var s_TextureOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (s_Mod, _, s_Resources, _) in s_Inventories)
            foreach (var s_Resource in s_Resources)
            {
                var s_ResName = s_Resource.Split(" (")[0];
                if (s_ResName.Equals(s_DbName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (s_TextureOwners.TryGetValue(s_ResName, out var s_Owner) && s_Owner != s_Mod)
                    throw new Exception($"'{s_Owner}' and '{s_Mod}' both ship the texture '{s_ResName}' " +
                                        "for the same map - they cannot be combined as they are.");

                s_TextureOwners[s_ResName] = s_Mod;
            }

        // Step 2 - fold the databases into one, pair by pair. The worker refuses loudly on any spot both
        // mods changed differently, and its output says exactly which shader collided.
        var s_MergeScript = new StringBuilder();
        s_MergeScript.AppendLine($"mount_game \"{s_GamePath}\" Frostbite2_0 true");
        s_MergeScript.AppendLine("select_game 1");

        var s_Current = Path.Combine(s_Work, "db_0.bin");
        for (var i = 1; i < p_Inputs.Count; i++)
        {
            var s_Next = Path.Combine(s_Work, $"merged_{i}.bin");
            s_MergeScript.AppendLine(
                $"shader_db_merge {s_DbName} \"{s_Current}\" \"{Path.Combine(s_Work, $"db_{i}.bin")}\" \"{s_Next}\"");
            s_Current = s_Next;
        }

        s_MergeScript.AppendLine("exit");
        s_MergeScript.AppendLine("exit");

        var s_MergeOutput = RunRepl(s_Work, "merge", s_MergeScript.ToString());
        if (!File.Exists(s_Current))
            throw new Exception($"the database merge for {p_Level} failed:\n" +
                                string.Join("\n", s_MergeOutput.Split('\n')
                                    .Where(p_L => p_L.Contains("ERROR")).Take(3)));

        // Step 3 - build the combined superbundle: ONE database bundle (merged database + every input's
        // textures and chunks) and, when any input delivers variations, ONE variation bundle holding the
        // union of their authored partitions. Same variation arriving from two inputs is the same
        // partition name (names embed the object), so the union simply keeps the first.
        var s_Partitions = new List<string>();
        foreach (var (_, s_Parts, _, _) in s_Inventories)
            foreach (var s_Partition in s_Parts)
                if (!s_Partitions.Contains(s_Partition, StringComparer.OrdinalIgnoreCase))
                    s_Partitions.Add(s_Partition);

        var s_Build = new StringBuilder();
        s_Build.AppendLine($"mount_game \"{s_GamePath}\" Frostbite2_0 true");
        s_Build.AppendLine("select_game 1");
        for (var i = 0; i < p_Inputs.Count; i++)
            s_Build.AppendLine($"mount_standalone_sb Win32/mergein{i}/{s_Level} \"{p_Inputs[i].SbPath}\" true");
        s_Build.AppendLine("exit");

        s_Build.AppendLine($"build_sb Win32/merged/{s_Level} Frostbite2_0 \"{Path.Combine(p_OutDir, "sb")}\" true");
        s_Build.AppendLine($"build_bundle Win32/merged/{s_Level}b");
        s_Build.AppendLine($"replace_resource {s_DbName} 1 \"{s_Current}\"");
        foreach (var s_Texture in s_TextureOwners.Keys)
            s_Build.AppendLine($"add_existing_resource \"{s_Texture}\" 1");
        foreach (var (_, _, _, s_Chunks) in s_Inventories)
            foreach (var s_Chunk in s_Chunks)
                s_Build.AppendLine($"add_existing_chunk {s_Chunk} 1");
        s_Build.AppendLine("build");

        if (s_Partitions.Count > 0)
        {
            s_Build.AppendLine($"build_bundle Win32/merged/{s_Level}v");
            foreach (var s_Partition in s_Partitions)
                s_Build.AppendLine($"add_existing_partition \"{s_Partition}\" 1");
            s_Build.AppendLine("build");
        }

        s_Build.AppendLine("build");
        s_Build.AppendLine("exit");

        RunRepl(s_Work, "build", s_Build.ToString());

        var s_OutSb = Path.Combine(p_OutDir, "sb", "Win32", "merged", $"{s_Level}.sb");
        if (!File.Exists(s_OutSb) || new FileInfo(Path.ChangeExtension(s_OutSb, ".toc")).Length == 0)
            throw new Exception($"the combined superbundle for {p_Level} did not build (see {s_Work}\\build.log)");

        Console.WriteLine($"[{p_Level}] unified: {p_Inputs.Count} databases folded, " +
                          $"{s_TextureOwners.Count} texture(s), {s_Partitions.Count} variation partition(s).");
        return s_Partitions.Count > 0;
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────────────────

    private static string RunRepl(string p_WorkDir, string p_Name, string p_Script)
    {
        var s_ScriptPath = Path.Combine(p_WorkDir, $"{p_Name}.txt");
        File.WriteAllText(s_ScriptPath, p_Script);

        var s_Info = new ProcessStartInfo(s_ReplPath, $"\"{s_ScriptPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(s_ReplPath)!,
        };

        using var s_Process = Process.Start(s_Info)!;
        var s_Output = s_Process.StandardOutput.ReadToEnd() + s_Process.StandardError.ReadToEnd();
        s_Process.WaitForExit();

        File.WriteAllText(Path.Combine(p_WorkDir, $"{p_Name}.log"), s_Output);

        // A worker that does not know a command reports it politely and exits 0 - catching that here is
        // the difference between a clear message and a half-built output that looks done.
        if (s_Output.Contains("Command not found"))
            throw new Exception($"the worker next to this tool is too old for this job (see {p_WorkDir}\\{p_Name}.log)");

        if (s_Output.Contains("MERGE FAILED") || s_Output.Contains("ERROR:"))
            throw new Exception("a merge step refused:\n" + string.Join("\n",
                s_Output.Split('\n').Where(p_L => p_L.Contains("ERROR")).Take(3)));

        return s_Output;
    }

    private static List<string> ParseListing(string p_Output, string p_Header)
    {
        var s_Result = new List<string>();
        var s_In = false;

        foreach (var s_Raw in p_Output.Split('\n'))
        {
            var s_Line = s_Raw.TrimEnd('\r');
            if (s_Line == p_Header)
            {
                s_In = true;
                continue;
            }

            if (!s_In)
                continue;

            if (s_Line.StartsWith("- ", StringComparison.Ordinal))
                s_Result.Add(s_Line[2..]);
            else if (s_Line.Trim().Length > 0)
                s_In = false;
        }

        return s_Result;
    }

    private static string ResolveGamePath()
    {
        // Never a guessed directory list: installs live anywhere. The saved answer wins (it is the user's
        // own), then the game's registry entry, then the user is asked once and the answer is saved.
        var s_Store = Path.Combine(s_BaseDir, "game_path.txt");

        var s_Candidates = new List<string>();
        if (File.Exists(s_Store))
            s_Candidates.Add(File.ReadAllText(s_Store).Trim());

        foreach (var s_Key in new[]
                 {
                     @"SOFTWARE\WOW6432Node\EA Games\Battlefield 3",
                     @"SOFTWARE\EA Games\Battlefield 3",
                 })
            try
            {
                using var s_RegistryKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(s_Key);
                if (s_RegistryKey?.GetValue("Install Dir") is string s_Path)
                    s_Candidates.Add(s_Path.TrimEnd('\\', '/'));
            }
            catch
            {
                // Unreadable key: fall through to the next candidate.
            }

        foreach (var s_Candidate in s_Candidates)
            if (s_Candidate.Length > 0 && Directory.Exists(Path.Combine(s_Candidate, "Data", "Win32")))
            {
                File.WriteAllText(s_Store, s_Candidate);
                Console.WriteLine($"Game found at: {s_Candidate}");
                return s_Candidate;
            }

        Console.WriteLine("Where is Battlefield 3 installed? (the folder containing bf3.exe)");
        Console.Write("> ");
        var s_Typed = (Console.ReadLine() ?? "").Trim().Trim('"');

        if (s_Typed.Length > 0 && Directory.Exists(Path.Combine(s_Typed, "Data", "Win32")))
        {
            File.WriteAllText(s_Store, s_Typed);
            return s_Typed;
        }

        Console.WriteLine("That folder does not look like a game install (no Data\\Win32 inside).");
        Console.WriteLine("Run the tool again and type the full path (you can paste it with right-click).");
        return "";
    }

    private static void WriteModFiles(string p_OutDir, List<(string Level, string Super, bool HasVariations)> p_Rows,
        List<string> p_SourceNames)
    {
        var s_Supers = string.Join(",\n", p_Rows.Select(p_R => $"        \"Win32/{p_R.Super}\""));
        var s_Json = $$"""
        {
            "Name": "{{c_OutputName}}",
            "Authors": [
                "Shader Mod Merger"
            ],
            "Description": "Combined shader mods: {{string.Join(", ", p_SourceNames)}}. Maps served by several of them carry one unified shader database instead of fighting over it.",
            "URL": "none",
            "Version": "1.0.0",
            "HasWebUI": false,
            "HasVeniceEXT": true,
            "Tags": [
                "gameplay"
            ],
            "Superbundles": [
        {{s_Supers}}
            ],
            "Dependencies": {
                "veniceext": "^1.0.0"
            }
        }
        """;

        File.WriteAllText(Path.Combine(p_OutDir, "mod.json"), s_Json + Environment.NewLine,
            new UTF8Encoding(false));

        var s_Rows = string.Join("\n", p_Rows.Select(p_R =>
            $"\t[\"{p_R.Level.ToUpperInvariant()}\"] = {{ super = \"{p_R.Super}\", vars = {(p_R.HasVariations ? "true" : "false")} }},"));

        var s_Lua = $$"""
        -- {{c_OutputName}} - generated by the Shader Mod Merger. Do not hand-edit; re-run the merger instead.
        --
        -- One superbundle per level, up to TWO bundles inside it:
        --   <name>b  (database bundle)  - the level's shader database with every combined mod's entries in it.
        --            PREPENDED before the level's own bundle: the shader system resolves by name and bundle
        --            order decides which database answers, so ours must come first.
        --   <name>v  (variation bundle) - the combined mods' variation partitions. Inserted right AFTER the
        --            level's own bundle: it does NOT carry meshes - its entries reference the level's own
        --            resident meshes, and those references only resolve once the level bundle has loaded.

        local SUPERS = {
        {{s_Rows}}
        }

        local IS_SERVER = (ClientUtils == nil)
        local m_Super, m_DbBundle, m_VarBundle = nil, nil, nil

        local function LOG(p_Text)
        	print("[{{c_OutputName}}][" .. (IS_SERVER and "SRV" or "CLI") .. "] " .. tostring(p_Text))
        end

        -- Unconditional mark of life: without it, "nothing happened" cannot be told apart from "never loaded".
        LOG("loaded, targets: " .. table.concat((function()
        	local t = {}
        	for k in pairs(SUPERS) do t[#t + 1] = k end
        	table.sort(t)
        	return t
        end)(), ", "))

        Events:Subscribe("Level:LoadResources", function(p_LevelName)
        	m_Super, m_DbBundle, m_VarBundle = nil, nil, nil

        	local s_Key = string.upper(tostring(p_LevelName):match("[^/]+$") or "")
        	local s_Entry = SUPERS[s_Key]
        	if s_Entry == nil then
        		return
        	end

        	m_Super, m_DbBundle = s_Entry.super, s_Entry.super .. "b"
        	m_VarBundle = s_Entry.vars and (s_Entry.super .. "v") or nil

        	-- Instrumented on purpose: a silently failed mount leaves the injected bundle ids unresolvable
        	-- and the level then hangs in "creating level" forever instead of erroring out.
        	local s_Ok, s_Err = pcall(function() ResourceManager:MountSuperBundle(m_Super) end)
        	LOG("mount '" .. m_Super .. "': " .. (s_Ok and "OK" or ("FAILED " .. tostring(s_Err))))
        end)

        -- Keyed on the level's own bundle being present rather than on list position, so another module
        -- prepending first cannot silently stop this from firing.
        Hooks:Install("ResourceManager:LoadBundles", 100, function(p_Hook, p_Bundles, p_Compartment)
        	if m_DbBundle == nil then
        		return
        	end

        	local s_Level = SharedUtils:GetLevelName()
        	local s_HasLevel, s_HasOurs = false, false
        	for _, l_Bundle in ipairs(p_Bundles) do
        		if l_Bundle == s_Level then
        			s_HasLevel = true
        		end
        		if l_Bundle == m_DbBundle then
        			s_HasOurs = true
        		end
        	end

        	if not s_HasLevel or s_HasOurs then
        		return
        	end

        	-- Database bundle FIRST, then the incoming list, with the variation bundle (when this level has
        	-- one) inserted right AFTER the level's own bundle so its mesh references resolve against content
        	-- that is already loaded.
        	local s_New = { m_DbBundle }
        	for _, l_Bundle in ipairs(p_Bundles) do
        		s_New[#s_New + 1] = l_Bundle
        		if l_Bundle == s_Level and m_VarBundle ~= nil then
        			s_New[#s_New + 1] = m_VarBundle
        		end
        	end

        	LOG("PREPENDED " .. m_DbBundle .. (m_VarBundle ~= nil and (" and inserted " .. m_VarBundle) or "")
        		.. " (comp=" .. tostring(p_Compartment) .. ", n=" .. tostring(#p_Bundles) .. ")")
        	p_Hook:Pass(s_New, p_Compartment)
        end)
        """;

        var s_Ext = Path.Combine(p_OutDir, "ext", "Shared");
        Directory.CreateDirectory(s_Ext);
        File.WriteAllText(Path.Combine(s_Ext, "__init__.lua"), s_Lua.Replace("\r\n", "\n") + "\n",
            new UTF8Encoding(false));
    }

    private static int Pause(int p_Code)
    {
        Console.WriteLine();
        Console.Write("Press any key to close...");
        try { Console.ReadKey(true); } catch { /* redirected input (a test harness) has no key to read */ }
        return p_Code;
    }
}
