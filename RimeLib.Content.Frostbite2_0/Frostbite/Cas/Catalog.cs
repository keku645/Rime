using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using RimeLib.Frostbite.Core;
using RimeLib.Frostbite.Fs;
using RimeLib.IO;

namespace RimeLib.Content.Frostbite2_0.Frostbite.Cas
{
    /// <summary>
    /// Structure for content addressable storage catalogs
    /// </summary>
    public class Catalog
    {
        /// <summary>
        /// Entries by hash within this catalog
        /// </summary>
        public ConcurrentDictionary<Sha1, CatalogEntry> Entries { get; } = new ConcurrentDictionary<Sha1, CatalogEntry>();

        /// <summary>
        /// Authoritative catalog
        /// </summary>
        public Catalog? AuthoritativeCatalog { get; set; }

        /// <summary>
        /// Path to this catalog
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// Optional fileNumber -> path override, for catalogues whose entries do NOT point at
        /// cas_NN.cas files.
        ///
        /// An "index in place" catalogue addresses content where it already lives -- a superbundle
        /// the game already ships -- so nothing named cas_NN.cas exists for it. The engine does not
        /// care, because the runtime is told which file to put in each slot, but this reader resolved
        /// the file number by naming convention and therefore could not read such an entry back.
        /// That made add_cas_chunk (which sizes a chunk by reading it) and probe_catalog's read-back
        /// fail with "Could not find file cas_NN.cas".
        ///
        /// Populated from a "casfilemap.txt" sitting next to the .cat, if present:
        ///     &lt;fileNumber&gt;\t&lt;path&gt;      (# comments allowed)
        /// which is exactly what build_noncas_index emits.
        /// </summary>
        public Dictionary<uint, string> FileNumberToPath { get; } = new Dictionary<uint, string>();

        /// <summary>
        /// Where the game is installed, used to resolve RELATIVE entries in casfilemap.txt.
        ///
        /// The map stores paths relative to the install on purpose -- an absolute path baked in by the
        /// machine that generated the index names a drive and folder that do not exist on the machine
        /// that runs it. The engine-side loader is told the root explicitly; this reader is the same
        /// tool that mounted the game, so it just remembers where that was.
        /// </summary>
        public static string? GameInstallRoot { get; set; }

        public const ulong c_Nyan = 0x6E61794E6E61794E;

        /// <summary>
        /// Constructor that parses a catalog from an opened reader
        /// </summary>
        /// <param name="p_Path">Path to the catalog file</param>
        public Catalog(string p_Path)
        {
            Path = p_Path;
            // FileShare.Read: two RimeREPL processes may parse the same game catalog at once
            // (a bake and a verify run) — without it the second mount_game dies with
            // "cas.cat is being used by another process" and every later command cascades.
            using var s_Reader = new RimeReader(File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.Read));
            ParseHeader(s_Reader);
            LoadFileNumberMap();
        }

        /// <summary>
        /// Reads the optional casfilemap.txt next to the catalogue. Absent is the normal case.
        /// </summary>
        private void LoadFileNumberMap()
        {
            var s_Directory = System.IO.Path.GetDirectoryName(Path);
            if (string.IsNullOrEmpty(s_Directory))
                return;

            var s_MapPath = System.IO.Path.Join(s_Directory, "casfilemap.txt");
            if (!File.Exists(s_MapPath))
                return;

            foreach (var s_Line in File.ReadAllLines(s_MapPath))
            {
                var s_Trimmed = s_Line.Trim();
                if (s_Trimmed.Length == 0 || s_Trimmed.StartsWith("#"))
                    continue;

                var s_Tab = s_Trimmed.IndexOf('\t');
                if (s_Tab <= 0)
                    continue;

                if (!uint.TryParse(s_Trimmed.Substring(0, s_Tab).Trim(), out var s_FileNumber))
                    continue;

                var s_Entry = s_Trimmed.Substring(s_Tab + 1).Trim();

                // Relative entries hang off the install; an already-absolute one lies outside it and
                // is taken as written. Falling back to the .cat's own directory keeps a hand-made map
                // working when no game is mounted.
                if (!System.IO.Path.IsPathRooted(s_Entry))
                {
                    var s_Base = !string.IsNullOrEmpty(GameInstallRoot) ? GameInstallRoot : s_Directory;
                    s_Entry = System.IO.Path.GetFullPath(System.IO.Path.Join(s_Base, s_Entry));
                }

                FileNumberToPath[s_FileNumber] = s_Entry;
            }
        }

        /// <summary>
        /// Constructor that creates a new catalog from scratch
        /// </summary>
        public Catalog()
        {
            Path = string.Empty;
            AuthoritativeCatalog = null;
        }

        /// <summary>
        /// Parses the catalog header information
        /// </summary>
        /// <param name="p_Reader">Reader opened to the position of the catalog header</param>
        protected void ParseHeader(RimeReader p_Reader)
        {
            // read FileObfuscation, and use the output stream
            FileObfuscation.Deserialize(p_Reader, out var s_FixedReader, out _, true);
            
            /*
            var s_Magic = p_Reader.ReadUInt32();

            switch (s_Magic)
            {
                case 0x01CED100:
                case 0x00CED100:
                    var s_Signature = p_Reader.ReadBytes(292);
                    p_Reader.EnableDeobfuscation();
                    break;

                default:
                    p_Reader.Seek(-4, SeekOrigin.Current);
                    break;
            }
            */

            var s_NyanNyan01 = s_FixedReader.ReadUInt64();
            var s_NyanNyan02 = s_FixedReader.ReadUInt64();

            // NyanNyanNyanNyan
            if (s_NyanNyan01 != 0x6E61794E6E61794E ||
                s_NyanNyan02 != 0x6E61794E6E61794E)
                throw new Exception("The provided file is not a valid catalog file.");

            ParseEntries(s_FixedReader);
        }

        /// <summary>
        /// Parses the catalog entries
        /// </summary>
        /// <param name="p_Reader">Reader opened to the position of the catalog entries</param>
        protected void ParseEntries(RimeReader p_Reader)
        {
            while (p_Reader.Length - p_Reader.Position > 0)
            {
                var s_Entry = new CatalogEntry(p_Reader, this) { ContainedCatalog = this };
                Entries.TryAdd(s_Entry.Hash, s_Entry);
            }
        }

        /// <summary>
        /// Does this catalog contain a certain hash
        /// </summary>
        /// <param name="p_Hash">Hash to check for</param>
        /// <returns>True if this catalog contains the specified hash, false otherwise</returns>
        public bool ContainsEntry(Sha1 p_Hash)
        {
            return Entries.ContainsKey(p_Hash);
        }

        /// <summary>
        /// Opens a reader for a specific entry.
        /// </summary>
        /// <param name="p_Hash">Hash of the entry</param>
        /// <returns>A reader that can be used to read the contents of the entry</returns>
        public RimeReader ReadEntry(Sha1 p_Hash)
        {
            // If we have an authoritative catalog check that first.
            if (AuthoritativeCatalog != null && AuthoritativeCatalog.ContainsEntry(p_Hash))
                return AuthoritativeCatalog.ReadEntry(p_Hash);

            // Otherwise check if we have this entry.
            if (!ContainsEntry(p_Hash))
                throw new Exception("Tried opening a reader for a catalog entry with a nonexistent hash.");

            // Get the entry.
            var s_Entry = this[p_Hash];

            // Where the bytes actually live. An in-place catalogue overrides the file number with a
            // real path (see FileNumberToPath); otherwise it is the usual cas_NN.cas beside the .cat.
            if (!FileNumberToPath.TryGetValue(s_Entry.FileNumber, out var s_Path))
                s_Path = System.IO.Path.Join(System.IO.Path.GetDirectoryName(Path), $"cas_{s_Entry.FileNumber:D2}.cas");

            // Open a reader.
            var s_Reader = new RimeReader(File.Open(s_Path, FileMode.Open, FileAccess.Read, FileShare.Read));
            s_Reader.Seek(s_Entry.FileOffset, SeekOrigin.Begin);

            // Wrap in a limited reader.
            s_Reader = new LimitedRimeReader(s_Reader, s_Entry.FileSize);

            return s_Reader;
        }

        /// <summary>
        /// Indexer via hash
        /// </summary>
        /// <param name="p_Hash">Hash</param>
        /// <returns>CatalogEntry if hash is found, null otherwise</returns>
        public CatalogEntry this[Sha1 p_Hash]
        {
            get
            {
                if (AuthoritativeCatalog != null && AuthoritativeCatalog.ContainsEntry(p_Hash))
                    return AuthoritativeCatalog[p_Hash];

                if (!Entries.TryGetValue(p_Hash, out var s_Entry))
                    throw new Exception("Tried retrieving a catalog entry with an nonexistent hash.");

                return s_Entry;
            }
            set
            {
                value.ContainedCatalog = this;
                Entries.AddOrUpdate(p_Hash, value, (p_K, p_V) => value);
            }
        }
    }
}
