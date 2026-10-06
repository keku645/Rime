using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.Contexts;
using RimeLib.Cmd.Scaleform;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// In-process facade over the REPL contexts for UI tooling (the editor): mount a game once, then read
    /// partitions/resources instantly and build a mod from a document. Everything goes through the same
    /// commands the REPL runs, so the editor can never do something the command line cannot reproduce.
    ///
    /// The mount takes minutes, so everything the editor reads can be kept in a disk cache (one folder per
    /// game install): partition dumps, movies, atlas textures, fonts, localization chunks, the partition
    /// list and the guid→name map. With a warm cache the editor works without mounting at all; only a build
    /// needs the real game (the superbundle is built against the mounted bundles).
    /// </summary>
    public class RimeUiService
    {
        readonly BaseContext m_Base = new();
        GameContext? m_Game;
        readonly string m_Temp = Path.Combine(Path.GetTempPath(), "rime_ui_" + Guid.NewGuid().ToString("N"));

        string? m_CacheDir;
        List<string>? m_CachedPartitions;                    // every mounted partition name, from the cache index
        Dictionary<string, string>? m_GuidToName;            // partition guid (upper, dashed) -> name
        readonly Dictionary<string, JObject> m_JsonMemo = new(StringComparer.OrdinalIgnoreCase);

        public bool IsMounted => m_Game != null;
        public string? CacheDir => m_CacheDir;
        public bool HasCache => m_CachedPartitions != null;

        /// <summary>Mounts a Frostbite2_0 game (minutes). Throws with the REPL's message on failure.</summary>
        public void Mount(string p_GamePath, TextWriter p_Log)
        {
            Directory.CreateDirectory(m_Temp);
            ExecutionContext s_Ctx = m_Base;
            Run(ref s_Ctx, $"mount_game \"{p_GamePath}\" Frostbite2_0 true", p_Log);
            Run(ref s_Ctx, "select_game 1", p_Log);
            m_Game = s_Ctx as GameContext ?? throw new Exception("select_game did not yield a game context");
        }

        static void Run(ref ExecutionContext p_Ctx, string p_Line, TextWriter p_Log)
        {
            var s_Out = new StringWriter();
            var s_Ok = p_Ctx.ProcessCommand(p_Line, s_Out, out var s_Next);
            p_Log.Write(s_Out.ToString());
            if (!s_Ok || s_Next == null)
                throw new Exception($"'{p_Line}' failed: {s_Out}");
            p_Ctx = s_Next;
        }

        GameContext Game => m_Game ?? throw new Exception("no game mounted" + (m_CacheDir != null ? " and not in the cache" : ""));

        // ------------------------------------------------------------------------------------------ cache

        /// <summary>The cache folder for a game install: one per path, so two installs never mix.</summary>
        public static string CacheDirFor(string p_CacheRoot, string p_GamePath)
        {
            var s_Key = Path.GetFullPath(p_GamePath).TrimEnd('\\', '/').ToLowerInvariant();
            var s_Hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s_Key)))[..12].ToLowerInvariant();
            return Path.Combine(p_CacheRoot, s_Hash);
        }

        /// <summary>
        /// Points reads at a cache folder. Returns true when it already holds an index (partition list), i.e.
        /// the editor can list and open screens without a mount. Reads that miss the cache fall through to
        /// the mounted game and are stored on the way back.
        /// </summary>
        public bool UseCache(string p_CacheDir)
        {
            m_CacheDir = p_CacheDir;
            Directory.CreateDirectory(p_CacheDir);
            var s_Index = Path.Combine(p_CacheDir, "partitions.txt");
            m_CachedPartitions = File.Exists(s_Index) ? File.ReadAllLines(s_Index).Where(l => l.Length > 0).ToList() : null;
            var s_Guids = Path.Combine(p_CacheDir, "guids.json");
            m_GuidToName = File.Exists(s_Guids)
                ? JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(s_Guids)) ?? new Dictionary<string, string>()
                : new Dictionary<string, string>();
            return m_CachedPartitions != null;
        }

        /// <summary>
        /// What a warm cache must hold for this build, bumped whenever WarmCache learns a new category (2: the ui/art icons and the
        /// customization partitions — kits, weapons, unlocks, vehicles — the preview's data generator reads). A cache written by an older
        /// build lacks them and looks warm all the same: the editor compares this against the manifest and says so.
        /// </summary>
        public const int CacheVersion = 2;

        /// <summary>The version the cache's manifest declares (0 when it predates the versioning).</summary>
        public int CachedVersion => (int?)CacheManifest()?["cacheVersion"] ?? 0;

        /// <summary>What the cache says about itself (written by WarmCache), or null.</summary>
        public JObject? CacheManifest()
        {
            if (m_CacheDir == null) return null;
            var s_File = Path.Combine(m_CacheDir, "manifest.json");
            return File.Exists(s_File) ? JObject.Parse(File.ReadAllText(s_File)) : null;
        }

        static string SafeName(string p_Name) => p_Name.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

        string? CachePath(string p_Kind, string p_Name, string p_Ext) =>
            m_CacheDir == null ? null : Path.Combine(m_CacheDir, p_Kind, SafeName(p_Name.ToLowerInvariant()) + p_Ext);

        void SaveGuids()
        {
            if (m_CacheDir == null || m_GuidToName == null) return;
            File.WriteAllText(Path.Combine(m_CacheDir, "guids.json"), JsonConvert.SerializeObject(m_GuidToName, Formatting.Indented));
        }

        /// <summary>
        /// Fills the cache with everything the editor reads: every ui/ and localization/ partition dump, every
        /// SwfMovie under ui/, the atlas textures those movies name, and the partition list. Idempotent — files
        /// already there are kept. Progress goes to p_Progress(done, total, what).
        /// </summary>
        public void WarmCache(TextWriter p_Log, Action<int, int, string>? p_Progress = null)
        {
            if (m_CacheDir == null) throw new Exception("no cache folder set");
            var s_Game = Game;
            var s_All = s_Game.GetMountedPartitions().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllLines(Path.Combine(m_CacheDir, "partitions.txt"), s_All);
            m_CachedPartitions = s_All;

            var s_Wanted = s_All.Where(n => n.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) || n.StartsWith("localization/", StringComparison.OrdinalIgnoreCase)).ToList();
            var s_Movies = s_Game.GetMountedResourceVariations()
                .Where(kv => kv.Key.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) && kv.Value.FirstVariant.GetResourceType().ToString() == "SwfMovie")
                .Select(kv => kv.Key).OrderBy(n => n).ToList();
            // the UI's own textures (icons of weapons, accessories, camos, awards, ranks… under ui/art) — what the widgets load by path at runtime
            var s_Icons = s_Game.GetMountedResourceVariations()
                .Where(kv => kv.Key.StartsWith("ui/art/", StringComparison.OrdinalIgnoreCase) && kv.Value.FirstVariant.GetResourceType().ToString().Contains("Texture", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).OrderBy(n => n).ToList();
            // what the UI's data components read at runtime, so the preview can build their data without a mount: the kits (weapon tables),
            // every weapon's customization table, the unlock assets they list (their cased names give the item identifiers), the common
            // "none" entries and the soldier unlocks
            var s_Custom = s_All.Where(n =>
                n.StartsWith("gameplay/kits/", StringComparison.OrdinalIgnoreCase) || n.StartsWith("weapons/common/", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("persistence/unlocks/", StringComparison.OrdinalIgnoreCase) ||
                // the vehicle customization assets (LAND/AIR screens: one per vehicle class, their parts)
                n.StartsWith("gameplay/vehicles/", StringComparison.OrdinalIgnoreCase) ||
                (n.StartsWith("weapons/", StringComparison.OrdinalIgnoreCase) && (n.EndsWith("_customization", StringComparison.OrdinalIgnoreCase) || n.Contains("/u_", StringComparison.OrdinalIgnoreCase)))).ToList();
            var s_Total = s_Wanted.Count + s_Movies.Count + s_Icons.Count + s_Custom.Count;
            var s_Done = 0;
            var s_Textures = 0;
            var s_IconsDone = 0;
            var s_CustomDone = 0;
            var s_Missing = new List<string>();
            foreach (var s_Name in s_Custom)
            {
                try { PartitionJson(s_Name); ++s_CustomDone; }
                catch (Exception s_Ex) { s_Missing.Add(s_Name + ": " + s_Ex.Message); }
                if (++s_Done % 100 == 0) p_Progress?.Invoke(s_Done, s_Total, s_Name);
            }

            foreach (var s_Name in s_Wanted)
            {
                try { PartitionJson(s_Name); }
                catch (Exception s_Ex) { s_Missing.Add(s_Name + ": " + s_Ex.Message); }
                if (++s_Done % 50 == 0) p_Progress?.Invoke(s_Done, s_Total, s_Name);
            }
            foreach (var s_Name in s_Movies)
            {
                try
                {
                    var s_Bytes = ResourceBytes(s_Name);
                    // the movie's atlas (the DefineExternalImage2 file name -> texture resource next to the movie)
                    foreach (var s_Tex in GfxMovie.Load(s_Bytes).ExternalImages())
                    {
                        var s_Resource = AtlasResourceName(s_Name, s_Tex.FileName);
                        try { TextureDds(s_Resource); ++s_Textures; }
                        catch (Exception s_Ex) { s_Missing.Add(s_Resource + ": " + s_Ex.Message); }
                    }
                }
                catch (Exception s_Ex) { s_Missing.Add(s_Name + ": " + s_Ex.Message); }
                if (++s_Done % 20 == 0) p_Progress?.Invoke(s_Done, s_Total, s_Name);
            }
            foreach (var s_Name in s_Icons)
            {
                try { TextureDds(s_Name); ++s_IconsDone; }
                catch (Exception s_Ex) { s_Missing.Add(s_Name + ": " + s_Ex.Message); }
                if (++s_Done % 100 == 0) p_Progress?.Invoke(s_Done, s_Total, s_Name);
            }
            // localization chunks (the text databases) so strings can be read without a mount
            foreach (var s_Name in s_Wanted.Where(n => n.StartsWith("localization/", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var s_Json = PartitionJson(s_Name);
                    foreach (var s_Chunk in s_Json.Descendants().OfType<JProperty>().Where(p => p.Name is "BinaryChunk" or "HistogramChunk"))
                    {
                        var s_Guid = ChunkGuid(s_Chunk.Value);
                        if (s_Guid != null) ChunkBytes(s_Guid);
                    }
                }
                catch (Exception s_Ex) { s_Missing.Add(s_Name + " chunks: " + s_Ex.Message); }
            }
            SaveGuids();
            var s_Manifest = new JObject
            {
                ["written"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), ["cacheVersion"] = CacheVersion,
                ["partitions"] = s_Wanted.Count, ["movies"] = s_Movies.Count, ["textures"] = s_Textures, ["icons"] = s_IconsDone, ["customization"] = s_CustomDone, ["allPartitions"] = s_All.Count,
                ["missing"] = new JArray(s_Missing),
            };
            File.WriteAllText(Path.Combine(m_CacheDir, "manifest.json"), s_Manifest.ToString(Formatting.Indented));
            p_Progress?.Invoke(s_Total, s_Total, "done");
            p_Log.WriteLine($"cache written to {m_CacheDir}: {s_Wanted.Count} partitions, {s_Movies.Count} movies, {s_Textures} atlas textures, {s_IconsDone} ui/art textures (icons)" +
                            (s_Missing.Count > 0 ? $", {s_Missing.Count} not available (see manifest.json)" : ""));
        }

        /// <summary>The engine's rule (fb::UIImageCreator): "&lt;movie dir&gt;/&lt;movie&gt;/&lt;file without extension&gt;".</summary>
        public static string AtlasResourceName(string p_MovieResource, string p_FileName)
        {
            var s_Dir = p_MovieResource.Contains('/') ? p_MovieResource[..p_MovieResource.LastIndexOf('/')] : "";
            var s_Movie = p_MovieResource.Split('/').Last();
            var s_File = Path.GetFileNameWithoutExtension(p_FileName);
            return (s_Dir.Length > 0 ? s_Dir + "/" : "") + s_Movie + "/" + s_File;
        }

        static string? ChunkGuid(JToken p_Value)
        {
            // the JSON dump writes a chunk reference either as a bare guid string or as an object with a guid field
            if (p_Value.Type == JTokenType.String) return (string?)p_Value;
            if (p_Value is JObject o)
                foreach (var k in new[] { "Guid", "guid", "Id", "ChunkId" })
                    if (o[k]?.Type == JTokenType.String) return (string?)o[k];
            return null;
        }

        // ------------------------------------------------------------------------------------------ reads

        public IEnumerable<string> Partitions(string p_Prefix)
        {
            var s_Names = m_Game != null ? Game.GetMountedPartitions() : (m_CachedPartitions ?? throw new Exception("no game mounted and no cache"));
            return s_Names.Where(n => n.StartsWith(p_Prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n);
        }

        /// <summary>
        /// A partition guid as the mounter keys it, from its string spelling. Never build it through GUID(System.Guid): that
        /// constructor clears the low bit of the last byte (Frostbite's chunk compression flag), so every partition whose guid ends
        /// in an odd byte (ui/assets/kitselector …235f, ui/uicomponents/uicustomizationcomp …77af) would stop resolving — and a
        /// screen shipped without those import copies comes up empty in the game.
        /// </summary>
        public static RimeLib.Frostbite.Core.GUID PartitionGuid(string p_Guid) => new(p_Guid.Trim());

        /// <summary>Partition name for a partition guid (as the JSON dumps reference other partitions), or null.</summary>
        public string? PartitionNameByGuid(string p_Guid)
        {
            var s_Key = p_Guid.ToUpperInvariant();
            if (m_GuidToName != null && m_GuidToName.TryGetValue(s_Key, out var s_Cached)) return s_Cached;
            if (m_Game == null) return null;
            if (!Game.GetMounter().TryGetPartitionByGuid(PartitionGuid(p_Guid), out var s_Name, out _)) return null;
            if (m_GuidToName != null) { m_GuidToName[s_Key] = s_Name; }
            return s_Name;
        }

        public JObject PartitionJson(string p_Name)
        {
            if (m_JsonMemo.TryGetValue(p_Name, out var s_Memo)) return s_Memo;
            var s_Cache = CachePath("partitions", p_Name, ".json");
            JObject s_Json;
            if (s_Cache != null && File.Exists(s_Cache))
                s_Json = JObject.Parse(File.ReadAllText(s_Cache));
            else
            {
                var s_File = new FileInfo(s_Cache ?? Path.Combine(m_Temp, SafeName(p_Name) + ".json"));
                Game.DumpPartitionJson(p_Name, s_File, Formatting.None);
                s_Json = JObject.Parse(File.ReadAllText(s_File.FullName));
            }
            // remember the guid so references resolve later without a mount
            if (m_GuidToName != null && s_Json["PartitionGuid"]?.Type == JTokenType.String)
                m_GuidToName[((string)s_Json["PartitionGuid"]!).ToUpperInvariant()] = p_Name;
            m_JsonMemo[p_Name] = s_Json;
            return s_Json;
        }

        public bool HasResource(string p_Name)
        {
            try { ResourceBytes(p_Name); return true; } catch { return false; }
        }

        /// <summary>Whether a partition's dump is to be had: in the cache, or from the mounted game.</summary>
        public bool HasPartition(string p_Name)
        {
            if (m_JsonMemo.ContainsKey(p_Name)) return true;
            var s_Cache = CachePath("partitions", p_Name, ".json");
            if (s_Cache != null && File.Exists(s_Cache)) return true;
            if (m_Game == null) return false;
            try { PartitionJson(p_Name); return true; } catch { return false; }
        }

        public byte[] ResourceBytes(string p_Name)
        {
            var s_Cache = CachePath("resources", p_Name, ".bin");
            if (s_Cache != null && File.Exists(s_Cache)) return File.ReadAllBytes(s_Cache);
            var s_File = new FileInfo(s_Cache ?? Path.Combine(m_Temp, SafeName(p_Name) + ".bin"));
            Game.DumpResource(p_Name, s_File);
            return File.ReadAllBytes(s_File.FullName);
        }

        /// <summary>Whether a texture resource can be had: already in the cache, or in the mounted game (fetched and cached on the way).</summary>
        public bool HasTexture(string p_Name)
        {
            var s_Cache = CachePath("textures", p_Name, ".dds");
            if (s_Cache != null && File.Exists(s_Cache)) return true;
            if (m_Game == null) return false;
            try { TextureDds(p_Name); return true; } catch { return false; }
        }

        /// <summary>A texture resource converted to DDS (the game's own converter), cached.</summary>
        public byte[] TextureDds(string p_Name)
        {
            var s_Cache = CachePath("textures", p_Name, ".dds");
            if (s_Cache != null && File.Exists(s_Cache)) return File.ReadAllBytes(s_Cache);
            var s_File = new FileInfo(s_Cache ?? Path.Combine(m_Temp, SafeName(p_Name) + ".dds"));
            Game.DumpTexture(p_Name, s_File);
            return File.ReadAllBytes(s_File.FullName);
        }

        /// <summary>
        /// Raw bytes of a chunk (localization databases live in chunks), cached under the guid as the EBX names
        /// it. The low bit of a chunk guid's last byte is Frostbite's compression flag: the JSON dump clears it
        /// (RimeLib's GUID(Guid) constructor does), while the mounter keys the chunk by the flagged guid — so both
        /// spellings are tried.
        /// </summary>
        public byte[] ChunkBytes(string p_Guid)
        {
            var s_Key = p_Guid.ToLowerInvariant();
            var s_Cache = CachePath("chunks", s_Key, ".bin");
            if (s_Cache != null && File.Exists(s_Cache)) return File.ReadAllBytes(s_Cache);
            var s_File = new FileInfo(s_Cache ?? Path.Combine(m_Temp, SafeName(s_Key) + ".bin"));
            var s_Bytes = Guid.Parse(p_Guid).ToByteArray();
            var s_Flagged = (byte[])s_Bytes.Clone(); s_Flagged[15] ^= 1;
            try { Game.DumpChunk(new RimeLib.Frostbite.Core.GUID(new Guid(s_Bytes).ToString("D")), s_File); }
            catch { Game.DumpChunk(new RimeLib.Frostbite.Core.GUID(new Guid(s_Flagged).ToString("D")), s_File); }
            return File.ReadAllBytes(s_File.FullName);
        }

        // ------------------------------------------------------------------------------------------ build

        /// <summary>Writes the mod folder (mod.json, ext/, sb/) and builds its superbundle. Returns the mod folder. Needs the mount. The build's intermediates go to p_WorkDir (null = under %TEMP%), never into the mod.</summary>
        public string BuildMod(ScreenDocument p_Doc, string p_DocDir, string p_ModsRoot, TextWriter p_Log, string? p_WorkDir = null, ModEmitter.Recorder? p_Record = null)
        {
            if (m_Game == null) throw new Exception("building needs the game mounted (the superbundle is built against its bundles)");
            var s_Emitter = new ModEmitter(p_Doc, p_DocDir, p_ModsRoot, PartitionJson, ResourceBytes, p_Log, p_WorkDir, PartitionNameByGuid) { Record = p_Record };
            s_Emitter.Emit();
            RunRecipe(s_Emitter.RecipeLines(), p_Log);
            return s_Emitter.ModDir;
        }

        /// <summary>Runs build recipe lines (build_sb, build_bundle, replace_resource, add_json_partition, build…) against the mounted game, each logged with its output. Throws on the first failing line.</summary>
        public void RunRecipe(IEnumerable<string> p_Lines, TextWriter p_Log)
        {
            if (m_Game == null) throw new Exception("building needs the game mounted (the superbundle is built against its bundles)");
            ExecutionContext s_Ctx = m_Base;
            foreach (var s_Line in p_Lines)
            {
                p_Log.WriteLine("> " + s_Line);
                Run(ref s_Ctx, s_Line, p_Log);
            }
        }
    }
}
