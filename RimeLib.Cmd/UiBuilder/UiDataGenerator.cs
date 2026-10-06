using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// Stands in for the engine's UI data components with data built from the game's own EBX, for a synthetic player profile
    /// (everything unlocked, a chosen kit and weapon). What the components compute at runtime is read here from the same assets
    /// they read: the kits (Gameplay/Kits/&lt;kit&gt;: the weapon table with one CustomizationUnlockParts per row — primary, secondary,
    /// gadgets, specialization — each a list of UnlockAssets), a weapon's customization table (Weapons/&lt;w&gt;/&lt;w&gt;_Customization:
    /// one part per accessory row — optics, rail, barrel, camo), and the UI metadata tables (UI/UI*MetaData: one description per
    /// item keyed by the unlock's Identifier = the fb hash of the unlock asset's name, with its name/description sids, texture
    /// paths and, for weapons, the ammo / rate of fire / range / fire modes the info box shows). The payload shapes are the ones
    /// the widgets' ActionScript reads (KitView: an array of slots; KitSelectorSlot: ItemCategory + Items[{Label, ItemImage,
    /// Index, DefaultSelected, Locked, Description}]; KitInfoBox: Description {Type 0: weapon stats | Type 1: text}).
    /// </summary>
    public sealed class UiDataGenerator
    {
        /// <summary>The player the preview pretends to be.</summary>
        public sealed class Profile
        {
            public string Kit = "gameplay/kits/usassault";
            /// <summary>The primary weapon's unlock (weapons/m416/u_m416); null = the first primary of the kit.</summary>
            public string? Weapon;
            /// <summary>Selected unlock per row category or part (sid or "part N") → unlock partition name; missing = the row's "none"/first entry.</summary>
            public Dictionary<string, string> Selected = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>The customization tab the flow graph put the profile on (CustSelectedVehicleCategory: 0 = land, 1 = air; null = the kits).</summary>
            public int? VehicleCategory;
        }

        public sealed record Row(string Category, List<string> Unlocks);

        readonly RimeUiService m_Rime;
        readonly Action<string> m_Log;
        Dictionary<uint, JObject>? m_Meta;
        readonly Dictionary<string, (string Cased, uint Id)> m_Unlocks = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> m_Warned = new(StringComparer.OrdinalIgnoreCase);

        public Profile Current { get; set; } = new();

        static readonly string[] c_MetaTables =
        {
            "ui/uiweaponaccessorymetadata", "ui/uiweaponmetadata", "ui/uikititemmetadata", "ui/uispecializationmetadata", "ui/uicamometadata",
            "ui/uivehiclemetadata", "ui/uivehicleaccessorymetadata", "ui/uivehicleweaponmetadata",
        };

        public UiDataGenerator(RimeUiService p_Rime, Action<string> p_Log) { m_Rime = p_Rime; m_Log = p_Log; }

        /// <summary>Whether the profile's kit is to be had (the cache or the mounted game).</summary>
        public bool Available => m_Rime.HasPartition(Current.Kit);

        /// <summary>Every soldier kit the game ships (Gameplay/Kits/*: VeniceSoldierCustomizationAsset): partition name and label sid, as the player's toolbar lists them.</summary>
        public List<(string Partition, string LabelSid)> Kits()
        {
            var s_Kits = new List<(string, string)>();
            List<string> s_Names;
            try { s_Names = m_Rime.Partitions("gameplay/kits/").ToList(); } catch { return s_Kits; }
            foreach (var s_Name in s_Names)
            {
                var s_Asset = Primary(Partition(s_Name) ?? new JObject());
                if (s_Asset == null || (string?)s_Asset["$type"] != "VeniceSoldierCustomizationAsset") continue;
                s_Kits.Add((s_Name, (string?)s_Asset["LabelSid"] ?? ""));
            }
            return s_Kits;
        }

        /// <summary>The primary weapons of the profile's kit, in the table's order: unlock partition name and the weapon's name sid from the UI metadata.</summary>
        public List<(string Unlock, string NameSid)> PrimaryWeapons()
        {
            var s_Row = Kit()?.Rows.FirstOrDefault(r => r.Category == "ID_M_SOLDIER_PRIMARY");
            if (s_Row == null) return new List<(string, string)>();
            return s_Row.Unlocks.Select(u => (u, (string?)Describe(u)?["Name"] ?? u.Split('/').Last())).ToList();
        }

        /// <summary>The profile's primary weapon as generated: the chosen one, or the kit's first primary.</summary>
        public string? CurrentWeapon => PrimaryWeapon();

        /// <summary>
        /// The player picked an item in an accessory row of the accessories screen (the row's arrows): part 0..3 = WeaponAccessory1..4, and
        /// the item's Index in that row (as the row's items carry it). True when the selection changed.
        /// </summary>
        public bool SelectAccessory(int p_Part, int p_Index)
        {
            var s_Rows = AccessoryRows(out _);
            if (s_Rows == null || p_Part < 0 || p_Part >= s_Rows.Count) return false;
            var s_Pos = PositionOf(s_Rows[p_Part].Unlocks, p_Index);
            if (s_Pos < 0 || s_Pos == SelectedIndex("part " + p_Part, s_Rows[p_Part].Unlocks)) return false;
            Current.Selected["part " + p_Part] = s_Rows[p_Part].Unlocks[s_Pos];
            return true;
        }

        /// <summary>
        /// The player picked an item in a row of the loadout (the row's category sid — ID_M_SOLDIER_PRIMARY, …SECONDARY, …GADGET1/2,
        /// …SPECIALIZATION — and the item's Index in it); a new primary weapon starts its accessories from "none" again. True when the selection changed.
        /// </summary>
        public bool SelectInRow(string p_Category, int p_Index)
        {
            var s_Row = Kit()?.Rows.FirstOrDefault(r => r.Category == p_Category);
            if (s_Row == null) return false;
            var s_Pos = PositionOf(s_Row.Unlocks, p_Index);
            if (s_Pos < 0) return false;
            var s_Unlock = s_Row.Unlocks[s_Pos];
            if (p_Category == "ID_M_SOLDIER_PRIMARY")
            {
                if (string.Equals(PrimaryWeapon(), s_Unlock, StringComparison.OrdinalIgnoreCase)) return false;
                Current.Weapon = s_Unlock;
                foreach (var k in Current.Selected.Keys.Where(k => k.StartsWith("part ", StringComparison.OrdinalIgnoreCase)).ToList()) Current.Selected.Remove(k);
            }
            else if (s_Pos == SelectedIndex(p_Category, s_Row.Unlocks, false)) return false;
            Current.Selected[p_Category] = s_Unlock;
            return true;
        }

        /// <summary>
        /// The soldier kits of the profile's team, in the game's order (assault, engineer, support, recon): the base assets
        /// Gameplay/Kits/&lt;team&gt;&lt;kit&gt; of the team the profile's kit belongs to — what the KITS screen lists, one row per kit.
        /// </summary>
        public List<string> TeamKits()
        {
            var s_Team = Current.Kit.Split('/').Last().StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "us";
            var s_All = Kits().Select(k => k.Partition).ToList();
            return new[] { "assault", "engineer", "support", "recon" }
                .Select(k => s_All.FirstOrDefault(p => string.Equals(p, "gameplay/kits/" + s_Team + k, StringComparison.OrdinalIgnoreCase)))
                .Where(p => p != null).Select(p => p!).ToList();
        }

        /// <summary>The player picked a kit row on the KITS screen (UIKitComp.SelectedKit = the row's index): the profile moves to that kit, its weapon and picks start over. True when the kit changed.</summary>
        public bool SelectKitIndex(int p_Index)
        {
            var s_Kits = TeamKits();
            if (p_Index < 0 || p_Index >= s_Kits.Count || string.Equals(s_Kits[p_Index], Current.Kit, StringComparison.OrdinalIgnoreCase)) return false;
            Current.Kit = s_Kits[p_Index]; Current.Weapon = null; Current.Selected.Clear();
            return true;
        }

        /// <summary>The player picked a soldier camo on the APPEARANCE screen (CustSelectedAppearance = the item's Index in the row). True when the selection changed.</summary>
        public bool SelectAppearance(int p_Index)
        {
            var s_Row = AppearanceRow();
            if (s_Row == null) return false;
            var s_Pos = PositionOf(s_Row.Unlocks, p_Index);
            if (s_Pos < 0 || s_Pos == SelectedIndex("appearance", s_Row.Unlocks)) return false;
            Current.Selected["appearance"] = s_Row.Unlocks[s_Pos];
            return true;
        }

        // ------------------------------------------------------------------------------------------ EBX access

        static JObject? Inst(JObject p_Partition, JToken? p_Ref)
        {
            var s_Guid = (string?)p_Ref?["InstanceGuid"];
            if (s_Guid == null || p_Partition["Instances"] is not JObject s_Instances) return null;
            return s_Instances[s_Guid] as JObject ?? s_Instances[s_Guid.ToLowerInvariant()] as JObject ?? s_Instances[s_Guid.ToUpperInvariant()] as JObject;
        }

        static JObject? Primary(JObject p_Partition) =>
            (p_Partition["PrimaryInstanceGuid"] is { } s_Guid ? Inst(p_Partition, new JObject { ["InstanceGuid"] = s_Guid }) : null)
            ?? (p_Partition["Instances"] as JObject)?.Properties().Select(p => p.Value).OfType<JObject>().FirstOrDefault(o => ((string?)o["$type"] ?? "").EndsWith("Asset", StringComparison.Ordinal));

        JObject? Partition(string p_Name)
        {
            try { return m_Rime.PartitionJson(p_Name); } catch { return null; }
        }

        /// <summary>The unlock partition behind a reference: its cased name (as the asset names itself) and its Identifier (the fb hash of that name).</summary>
        (string Name, string Cased, uint Id)? Unlock(JToken? p_Ref)
        {
            var s_Guid = (string?)p_Ref?["PartitionGuid"];
            if (s_Guid == null) return null;
            var s_Name = m_Rime.PartitionNameByGuid(s_Guid);
            if (s_Name == null) return null;
            if (!m_Unlocks.TryGetValue(s_Name, out var s_Entry))
            {
                var s_Json = Partition(s_Name);
                var s_Cased = (string?)Primary(s_Json ?? new JObject())?["Name"] ?? s_Name;
                s_Entry = (s_Cased, unchecked((uint)DataKeys.FbHash(s_Cased)));
                m_Unlocks[s_Name] = s_Entry;
            }
            return (s_Name, s_Entry.Cased, s_Entry.Id);
        }

        /// <summary>The rows of a customization table: one per CustomizationUnlockParts (its category sid and the unlock partition names it lists).</summary>
        List<Row> Rows(JObject p_Partition, JToken? p_TableRef)
        {
            var s_Rows = new List<Row>();
            var s_Table = Inst(p_Partition, p_TableRef);
            foreach (var s_PartRef in s_Table?["UnlockParts"] as JArray ?? new JArray())
            {
                var s_Part = Inst(p_Partition, s_PartRef);
                if (s_Part == null) continue;
                var s_Unlocks = new List<string>();
                foreach (var s_Ref in s_Part["SelectableUnlocks"] as JArray ?? new JArray())
                {
                    var u = Unlock(s_Ref);
                    if (u != null) s_Unlocks.Add(u.Value.Name);
                }
                s_Rows.Add(new Row((string?)s_Part["UICategorySid"] ?? "", s_Unlocks));
            }
            return s_Rows;
        }

        Dictionary<uint, JObject> Meta()
        {
            if (m_Meta != null) return m_Meta;
            m_Meta = new Dictionary<uint, JObject>();
            foreach (var s_Table in c_MetaTables)
            {
                var s_Json = Partition(s_Table);
                if (s_Json?["Instances"] is not JObject s_Instances) continue;
                foreach (var s_Desc in s_Instances.Properties().Select(p => p.Value).OfType<JObject>())
                    foreach (var s_Id in s_Desc["ItemIds"] as JArray ?? new JArray())
                        if (s_Id.Type is JTokenType.Integer) m_Meta[unchecked((uint)(long)s_Id)] = s_Desc;
            }
            m_Log($"[data] {m_Meta.Count} item descriptions from the UI metadata tables");
            return m_Meta;
        }

        JObject? Describe(string p_Unlock)
        {
            var s_Json = Partition(p_Unlock);
            var s_Cased = (string?)Primary(s_Json ?? new JObject())?["Name"] ?? p_Unlock;
            return Meta().TryGetValue(unchecked((uint)DataKeys.FbHash(s_Cased)), out var d) ? d : null;
        }

        readonly Dictionary<string, int> m_Ids = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The unlock's Identifier as the game's records carry it in every item's Index (measured 2026-09-16 on the game's own
        /// initializeScreen: "292665415" for the AK-74M, "-872939660" for the Kobra) — the fb hash of the asset's cased name as a signed
        /// 32-bit number; a row's pick sends it back, and the engine's action matches the unlock by it.
        /// </summary>
        int Identifier(string p_Unlock)
        {
            if (!m_Ids.TryGetValue(p_Unlock, out var s_Id))
            {
                var s_Cased = (string?)Primary(Partition(p_Unlock) ?? new JObject())?["Name"] ?? p_Unlock;
                s_Id = unchecked((int)(uint)DataKeys.FbHash(s_Cased));
                m_Ids[p_Unlock] = s_Id;
            }
            return s_Id;
        }

        /// <summary>The position in a row of the value a pick carries: the item's Index (the unlock's Identifier) or, for a value that is no identifier of the row, a plain position.</summary>
        int PositionOf(List<string> p_Unlocks, int p_Value)
        {
            var s_Pos = p_Unlocks.FindIndex(u => Identifier(u) == p_Value);
            return s_Pos >= 0 ? s_Pos : (p_Value >= 0 && p_Value < p_Unlocks.Count ? p_Value : -1);
        }

        // ------------------------------------------------------------------------------------------ the profile's kit and weapon

        /// <summary>The profile's kit asset: its label sid and its rows (category → unlocks): the weapon table's parts, then the specialization table's.</summary>
        (string Label, List<Row> Rows)? Kit() => KitOf(Current.Kit);

        (string Label, List<Row> Rows)? KitOf(string p_Kit)
        {
            var s_Json = Partition(p_Kit);
            var s_Asset = Primary(s_Json ?? new JObject());
            if (s_Json == null || s_Asset == null) return null;
            var s_Rows = Rows(s_Json, s_Asset["WeaponTable"]);
            // the kit's fixed gadget: the parts between GADGET1 and GADGET2 carry no UI category (the medic bag as ID_WEAPON_CATEGORYGADGET1,
            // the ammo bag as "") and the game lists them FIRST in the gadget-1 row (measured 2026-09-16 on the game's loadout record: the
            // assault's row = medic bag + the 10 selectable ones; the support's KITS slot = the ammo bag while its GADGET1 part is empty).
            // The unlabelled parts after GADGET2 (grenade, knife) are no row's.
            var s_Gadget1 = s_Rows.FindIndex(r => r.Category == "ID_M_SOLDIER_GADGET1");
            var s_Gadget2 = s_Rows.FindIndex(r => r.Category == "ID_M_SOLDIER_GADGET2");
            if (s_Gadget1 >= 0 && s_Gadget2 > s_Gadget1)
            {
                var s_Fixed = s_Rows.Skip(s_Gadget1 + 1).Take(s_Gadget2 - s_Gadget1 - 1).SelectMany(r => r.Unlocks).ToList();
                if (s_Fixed.Count > 0) s_Rows[s_Gadget1] = new Row(s_Rows[s_Gadget1].Category, s_Fixed.Concat(s_Rows[s_Gadget1].Unlocks).ToList());
            }
            // the specialization row is a table of its own (one part, UICategorySid ID_M_SOLDIER_SPECIALIZATION); the loadout screen's
            // fifth row stayed empty while only the weapon table was read
            s_Rows.AddRange(Rows(s_Json, s_Asset["SpecializationTable"]));
            return ((string?)s_Asset["LabelSid"] ?? "", s_Rows);
        }

        /// <summary>The profile's kit's appearance row: the visual table's one part (the soldier camos, UICamoMetaData) — what the APPEARANCE screen's row lists.</summary>
        Row? AppearanceRow()
        {
            var s_Json = Partition(Current.Kit);
            var s_Asset = Primary(s_Json ?? new JObject());
            if (s_Json == null || s_Asset == null) return null;
            return Rows(s_Json, s_Asset["VisualTable"]).FirstOrDefault(r => r.Unlocks.Count > 0);
        }

        /// <summary>The unlock of the profile's primary weapon: the chosen one, or the kit's first primary.</summary>
        string? PrimaryWeapon()
        {
            if (!string.IsNullOrEmpty(Current.Weapon)) return Current.Weapon;
            return Kit()?.Rows.FirstOrDefault(r => r.Category == "ID_M_SOLDIER_PRIMARY")?.Unlocks.FirstOrDefault();
        }

        /// <summary>A weapon's accessory rows: the customization table under the weapon's folder (Weapons/&lt;w&gt;/&lt;x&gt;_Customization).</summary>
        (string Table, List<Row> Rows)? WeaponRows(string p_WeaponUnlock)
        {
            var s_Folder = p_WeaponUnlock.Contains('/') ? p_WeaponUnlock[..p_WeaponUnlock.LastIndexOf('/')] : p_WeaponUnlock;
            var s_Table = m_Rime.Partitions(s_Folder + "/").FirstOrDefault(n => n.EndsWith("_customization", StringComparison.OrdinalIgnoreCase));
            if (s_Table == null) return null;
            var s_Json = Partition(s_Table);
            var s_Asset = Primary(s_Json ?? new JObject());
            if (s_Json == null || s_Asset == null) return null;
            return (s_Table, Rows(s_Json, s_Asset["Customization"]));
        }

        // ------------------------------------------------------------------------------------------ payloads

        /// <summary>The "nothing equipped" entry of a row: Weapons/Common/No*, the weapon camo's DefaultCamo, and the vehicle tables' Persistence/Unlocks/Vehicles/No* (NoPassive, NoPilotStance…).</summary>
        static bool IsNone(string p_Unlock) => p_Unlock.Contains("/common/no", StringComparison.OrdinalIgnoreCase) || p_Unlock.EndsWith("/defaultcamo", StringComparison.OrdinalIgnoreCase)
                                                 || (p_Unlock.Contains("/unlocks/vehicles/", StringComparison.OrdinalIgnoreCase) && p_Unlock.Split('/').Last().StartsWith("no", StringComparison.OrdinalIgnoreCase));
        static bool IsCamo(string p_Unlock) => p_Unlock.Contains("camo", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The row's selected entry: the profile's pick, else the row's default — "none" for an accessory row (a weapon starts bare), the
        /// first real entry for a kit or vehicle row (a kit always carries a gadget; measured 2026-09-16: the game's kit and vehicle rows
        /// list their "none" entry LAST and came selected on a real item in every row recorded).
        /// </summary>
        int SelectedIndex(string p_Key, List<string> p_Unlocks, bool p_NoneDefault = true)
        {
            if (Current.Selected.TryGetValue(p_Key, out var s_Chosen))
            {
                var s_Index = p_Unlocks.FindIndex(u => string.Equals(u, s_Chosen, StringComparison.OrdinalIgnoreCase));
                if (s_Index >= 0) return s_Index;
            }
            if (p_NoneDefault)
            {
                var s_None = p_Unlocks.FindIndex(IsNone);
                return s_None >= 0 ? s_None : 0;
            }
            var s_Real = p_Unlocks.FindIndex(u => !IsNone(u));
            return s_Real >= 0 ? s_Real : 0;
        }

        /// <summary>The row an item belongs to decides which fields the game puts on it (measured 2026-09-16 on the game's own records).</summary>
        enum ItemKind
        {
            /// <summary>An accessory row of the accessories screen: HeaderIcon and ShowPlus on every item; the weapon row's items add the stats.</summary>
            Accessory,
            /// <summary>A loadout row (primary, secondary, gadgets, specialization): HeaderIcon, ShowPlus, ServiceStars and BarValues on every item; the primaries add ShowButton1Plus and their slot icons.</summary>
            Soldier,
            /// <summary>A vehicle part row (passive, active, stance; pilot / gunner): ShowPlus but no HeaderIcon, no ServiceStars, no BarValues.</summary>
            Vehicle,
        }

        /// <summary>
        /// One item of a row as the slot reads it and as the game's own records carry it (measured 2026-09-16 on the game's initializeScreen
        /// of the accessories, loadout and vehicle screens): Index = the unlock's Identifier as a string; the fields per row kind as
        /// ItemKind says; Description Type 0 (weapon: Index, Range, RateOfFire, Category, FireModes as the info box's frame labels
        /// fltSingleFire/fltBurstFire/fltAutomaticFire, Icon, Ammo, Label, Description, Image) or Type 1 (Icon, Index, Category,
        /// Description, Label, Image). Text ids are left as ids: the host localises them on the way to the widget.
        /// </summary>
        JObject Item(string p_Unlock, bool p_Selected, bool p_Stats, ItemKind p_Kind = ItemKind.Accessory)
        {
            var d = Describe(p_Unlock);
            var s_Label = (string?)d?["Name"] ?? "";
            if (d == null)
            {
                s_Label = p_Unlock.Split('/').Last();
                if (m_Warned.Add(p_Unlock)) m_Log($"[data] no UI description for {p_Unlock} (Identifier not in the metadata tables): shown by name");
            }
            var s_Id = Identifier(p_Unlock);
            var s_Image = (string?)d?["TexturePath"] ?? "";
            var s_Icon = (string?)d?["IconTexturePath"] ?? "";
            var s_Item = new JObject
            {
                ["Label"] = s_Label,
                ["ItemImage"] = s_Image,
                ["Index"] = s_Id.ToString(),
                ["DefaultSelected"] = p_Selected,
                ["Locked"] = false,
                ["ShowPlus"] = false,
            };
            if (p_Kind != ItemKind.Vehicle) s_Item["HeaderIcon"] = "";
            if (p_Kind == ItemKind.Soldier) { s_Item["ServiceStars"] = 0; s_Item["BarValues"] = new JArray(0, 0); }
            if (p_Stats && d != null)
            {
                var s_Modes = new JArray();
                if ((bool?)d["FireModeSingle"] == true) s_Modes.Add("fltSingleFire");
                if ((bool?)d["FireModeBurst"] == true) s_Modes.Add("fltBurstFire");
                if ((bool?)d["FireModeAuto"] == true) s_Modes.Add("fltAutomaticFire");
                s_Item["ServiceStars"] = 0;
                s_Item["ShowButton1Plus"] = false;
                s_Item["BarValues"] = new JArray(0, 0);
                s_Item["Description"] = new JObject
                {
                    ["Type"] = 0, ["Index"] = s_Id, ["Range"] = (string?)d["Range"] ?? "", ["RateOfFire"] = d["RateOfFire"]?.ToString() ?? "",
                    ["Category"] = (string?)d["Category"] ?? "", ["FireModes"] = s_Modes, ["Icon"] = s_Icon, ["Ammo"] = (string?)d["Ammo"] ?? "",
                    ["Label"] = s_Label, ["Description"] = (string?)d["Description"] ?? "", ["Image"] = s_Image,
                };
            }
            else s_Item["Description"] = new JObject
            {
                ["Icon"] = s_Icon, ["Type"] = 1, ["Index"] = s_Id, ["Category"] = (string?)d?["Category"] ?? "",
                ["Description"] = (string?)d?["Description"] ?? "", ["Label"] = s_Label, ["Image"] = s_Image,
            };
            // every weapon item carries its small slot icons: the row's ActionScript hides the slot images when SlotsData is missing but
            // never touches the lock placeholders next to them (its initialise sets _visible on the array, not on each clip), so the
            // loadout's primary weapon showed three padlocks until the slots came with the item; an empty list hides both
            if (p_Stats) s_Item["SlotsData"] = Slots(p_Unlock);
            return s_Item;
        }

        /// <summary>The small slot icons of a weapon: the selected unlock of every accessory part (the "none" one by default), in the table's order; empty for a weapon without a customization table.</summary>
        JArray Slots(string p_Weapon)
        {
            var s_Slots = new JArray();
            var s_Rows = AccessoryRowsOf(p_Weapon, out _);
            if (s_Rows == null) return s_Slots;
            foreach (var (s_Row, i) in s_Rows.Select((r, i) => (r, i)))
            {
                if (s_Row.Unlocks.Count == 0) continue;
                var s_Selected = s_Row.Unlocks[SelectedIndex("part " + i, s_Row.Unlocks)];
                var d = Describe(s_Selected);
                s_Slots.Add(new JObject { ["Locked"] = false, ["Label"] = (string?)d?["Name"] ?? "", ["ItemImage"] = (string?)d?["IconTexturePath"] ?? "" });
            }
            return s_Slots;
        }

        JObject RowPayload(Row p_Row, string p_Key, bool p_Stats, ItemKind p_Kind = ItemKind.Accessory, bool p_NoneDefault = true)
        {
            var s_Selected = SelectedIndex(p_Key, p_Row.Unlocks, p_NoneDefault);
            var s_Items = new JArray(p_Row.Unlocks.Select((u, i) => Item(u, i == s_Selected, p_Stats, p_Kind)));
            // one slot object, as the game hands it (the row's ActionScript takes an object or an array of them)
            return new JObject { ["ShowStepPlus"] = false, ["Items"] = s_Items, ["ItemCategory"] = p_Row.Category };
        }

        /// <summary>
        /// The value of a component's data source for the profile, or null when this generator has nothing for it. p_How says where it came from.
        /// </summary>
        public JToken? Generate(string p_Component, string p_Source, out string p_How)
        {
            p_How = "";
            var s_Comp = p_Component.Split('/').Last().ToLowerInvariant();
            switch (s_Comp, p_Source)
            {
                // what the widgets' base code asks the engine about the machine (fb.HelperFunctions): PC — a Button with HideOnConsole
                // hides itself and a kit row skips its mouse handlers when the platform is not "win32" (an unanswered key reads as console)
                case ("uisettingscomp", "Platform"): p_How = "the profile plays on PC"; return "win32";
                case ("uisettingscomp", "Ps3EnterButton"): p_How = "no console"; return "";
                case ("frontendcomp", "InFrontend"): p_How = "the profile customizes from the spawn screen"; return new JValue(false);
                case ("frontendcomp", "IsPro"): p_How = "the profile is not premium"; return new JValue(false);
                case ("uicustomizationcomp", "CustomizationFrontend"): p_How = "the profile customizes from the spawn screen"; return "0";
                case ("uicustomizationcomp", "SaveAccessoriesEnabled"):
                case ("uicustomizationcomp", "SaveLoadoutEnabled"):
                case ("uicustomizationcomp", "SaveAppearanceEnabled"):
                case ("uicustomizationcomp", "SaveVehicleAccessoriesEnabled"): p_How = "the profile may save"; return "True";
                case ("uikitcomp", "SelectedKit"):
                {
                    // the KITS screen's row index of the profile's kit (GetKit → KitView_01.Activate(index); a row's Selected writes it back)
                    var s_Index = TeamKits().FindIndex(k => string.Equals(k, Current.Kit, StringComparison.OrdinalIgnoreCase));
                    p_How = "the profile's kit's row on the KITS screen"; return (s_Index < 0 ? 0 : s_Index).ToString();
                }
                case ("uikitcomp", "SelectedKitName"):
                {
                    var s_Kit = Kit();
                    if (s_Kit == null) return null;
                    p_How = Current.Kit; return s_Kit.Value.Label;
                }
                // the KITS screen (customizesoldierscreen): one KitViewSlot row per kit of the team, the tab bars, the page header
                case ("uicustomizationcomp", "KitsData"): return KitsData(out p_How);
                // the two tab bars' payloads as the game hands them (measured 2026-09-16): the deploy bar gets PlusTabs only, the customize bar both
                case ("uicustomizationcomp", "DeployTabs"):
                    p_How = "no premium tab (TabBar.updateSetupData: PlusTabs)";
                    return new JObject { ["PlusTabs"] = new JArray(false, false, false) };
                case ("uicustomizationcomp", "CustomizationTabs"):
                    p_How = "every tab enabled, none premium (TabBar.updateSetupData: EnabledTabs / PlusTabs)";
                    return new JObject { ["EnabledTabs"] = new JArray(true, true, true), ["PlusTabs"] = new JArray(false, false, false) };
                // the page header of the KITS / LAND / AIR screens (measured 2026-09-16 on the game's records): one path for the three, the
                // sub header names the tab and the team (ID_M_KITS_US, ID_M_LAND_US, ID_M_AIR_US)
                case ("uicustomizationcomp", "CustomizationHeader"): p_How = "the customize screens' title path"; return "ID_M_CUST_KITS_TITLE_PATH";
                case ("uicustomizationcomp", "CustomizationSubHeader"):
                {
                    var s_Team = Current.Kit.Split('/').Last().StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "RU" : "US";
                    p_How = "the tab's name for the profile's team";
                    return (Current.VehicleCategory == 0 ? "ID_M_LAND_" : Current.VehicleCategory == 1 ? "ID_M_AIR_" : "ID_M_KITS_") + s_Team;
                }
                // the LAND / AIR screens (one row per vehicle class) and their sub screens (the selected vehicle's parts)
                case ("uicustomizationcomp", "LandVehiclesData"): return VehiclesData(false, out p_How);
                case ("uicustomizationcomp", "AirVehiclesData"): return VehiclesData(true, out p_How);
                case ("uicustomizationcomp", "CustSelectedVehicle"): p_How = "the selected row of the category's vehicle list"; return SelectedVehicle(Current.VehicleCategory == 1).ToString();
                case ("uicustomizationcomp", "LandVehicleProperty1"): return VehiclePart(false, null, 0, out p_How);
                case ("uicustomizationcomp", "LandVehicleProperty2"): return VehiclePart(false, null, 1, out p_How);
                case ("uicustomizationcomp", "LandVehicleProperty3"): return VehiclePart(false, null, 2, out p_How);
                case ("uicustomizationcomp", "AirVehiclePilotProperty1"): return VehiclePart(true, "PILOT", 0, out p_How);
                case ("uicustomizationcomp", "AirVehiclePilotProperty2"): return VehiclePart(true, "PILOT", 1, out p_How);
                case ("uicustomizationcomp", "AirVehiclePilotProperty3"): return VehiclePart(true, "PILOT", 2, out p_How);
                case ("uicustomizationcomp", "AirVehicleGunnerProperty1"): return VehiclePart(true, "GUNNER", 0, out p_How);
                case ("uicustomizationcomp", "AirVehicleGunnerProperty2"): return VehiclePart(true, "GUNNER", 1, out p_How);
                case ("uicustomizationcomp", "AirVehicleGunnerProperty3"): return VehiclePart(true, "GUNNER", 2, out p_How);
                // a boolean, as the component hands it: the rows bound to it set their _visible from the value itself (a "True" string left them hidden)
                case ("uicustomizationcomp", "HasGunnerSeat"): p_How = "whether the selected air vehicle has gunner parts"; return new JValue(HasGunnerSeat());
                case ("uikitcomp", "SelectedVehicleName"):
                {
                    var s_Air = Current.VehicleCategory == 1;
                    var s_Classes = VehicleClasses(s_Air);
                    var s_Selected = SelectedVehicle(s_Air);
                    if (s_Selected >= s_Classes.Count) return null;
                    p_How = s_Classes[s_Selected].Partition; return s_Classes[s_Selected].NameSid;
                }
                // the APPEARANCE screen: the kit's visual table (soldier camos) as one row
                case ("uicustomizationcomp", "KitAppearance"):
                {
                    var s_Row = AppearanceRow();
                    if (s_Row == null) return null;
                    p_How = $"{Current.Kit} visual table ({s_Row.Unlocks.Count} soldier camos)";
                    return RowPayload(s_Row, "appearance", false);
                }
                case ("uispawnlogiccomp", "TimeToRespawn"): p_How = "no spawn timer outside the game"; return "0";
                case ("uisquadcomp", "ActiveSquadBoosters"): p_How = "no squad outside the game"; return new JArray();
                case ("uicustomizationcomp", "KitPrimaryWeapon"): return KitRow("ID_M_SOLDIER_PRIMARY", true, out p_How);
                case ("uicustomizationcomp", "KitSecondaryWeapon"): return KitRow("ID_M_SOLDIER_SECONDARY", true, out p_How);
                case ("uicustomizationcomp", "KitGadget1"): return KitRow("ID_M_SOLDIER_GADGET1", false, out p_How);
                case ("uicustomizationcomp", "KitGadget2"): return KitRow("ID_M_SOLDIER_GADGET2", false, out p_How);
                case ("uicustomizationcomp", "KitSpecialization"): return KitRow("ID_M_SOLDIER_SPECIALIZATION", false, out p_How);
                case ("uicustomizationcomp", "WeaponAccessoryMain"): return WeaponMain(out p_How);
                case ("uicustomizationcomp", "WeaponAccessory1"): return WeaponAccessory(0, out p_How);
                case ("uicustomizationcomp", "WeaponAccessory2"): return WeaponAccessory(1, out p_How);
                case ("uicustomizationcomp", "WeaponAccessory3"): return WeaponAccessory(2, out p_How);
                case ("uicustomizationcomp", "WeaponAccessory4"): return WeaponAccessory(3, out p_How);
                default: return null;
            }
        }

        JToken? KitRow(string p_Category, bool p_Stats, out string p_How)
        {
            p_How = "";
            var s_Kit = Kit();
            var s_Row = s_Kit?.Rows.FirstOrDefault(r => r.Category == p_Category);
            if (s_Row == null) return null;
            if (p_Category == "ID_M_SOLDIER_PRIMARY" && !string.IsNullOrEmpty(Current.Weapon)) Current.Selected[p_Category] = Current.Weapon;
            p_How = $"{Current.Kit} row {p_Category} ({s_Row.Unlocks.Count} unlocks)";
            return RowPayload(s_Row, p_Category, p_Stats, ItemKind.Soldier, false);
        }

        /// <summary>The frame of the shared icon clip (iconsShared: assault, engineer, medic, recon) a kit row's HeaderIcon names — the support kit's is "medic" (measured 2026-09-16).</summary>
        static string KitIcon(string p_LabelSid) => p_LabelSid.ToUpperInvariant() switch
        {
            "ID_M_ASSAULT" => "assault", "ID_M_ENGINEER" => "engineer", "ID_M_SUPPORT" => "medic", "ID_M_RECON" => "recon", _ => "",
        };

        /// <summary>
        /// The KITS screen's rows as the game hands them (measured 2026-09-16 on the game's initializeScreen of customizesoldierscreen):
        /// one item per kit of the team, in the order assault, engineer, support, recon — {ServiceStars 0, BarValues [0,0],
        /// ShowButton1Plus/ShowButton2Plus false, ItemImage = the row's index (a number, no picture), Description "", Label = the kit's
        /// sid, SlotsData = five {ItemImage, Label} (primary, secondary, gadget 1, gadget 2, specialization), HeaderIcon = the kit's icon
        /// frame, Index = the row's index (a number)}. No DefaultSelected: UIKitComp.SelectedKit is the selected row (GetKit → KitView_01.
        /// Activate(index), a row's Selected writes it back).
        /// </summary>
        JToken? KitsData(out string p_How)
        {
            p_How = "";
            var s_Kits = TeamKits();
            if (s_Kits.Count == 0) return null;
            var s_Items = new JArray();
            foreach (var (s_KitName, i) in s_Kits.Select((k, i) => (k, i)))
            {
                var s_Kit = KitOf(s_KitName);
                var s_Current = string.Equals(s_KitName, Current.Kit, StringComparison.OrdinalIgnoreCase);
                var s_Label = s_Kit?.Label ?? s_KitName.Split('/').Last();
                s_Items.Add(new JObject
                {
                    ["ServiceStars"] = 0, ["BarValues"] = new JArray(0, 0), ["ShowButton1Plus"] = false, ["ShowButton2Plus"] = false,
                    ["ItemImage"] = i, ["Description"] = "", ["Label"] = s_Label,
                    ["SlotsData"] = KitSlots(s_KitName, s_Kit, s_Current),
                    ["HeaderIcon"] = KitIcon(s_Label), ["Index"] = i,
                });
            }
            p_How = $"the {s_Kits.Count} kits of the profile's team, each with its five loadout slots (the shape of the game's own record)";
            return s_Items;
        }

        /// <summary>The five slots of a KITS row: the item each loadout row holds (primary, secondary, gadget 1, gadget 2, specialization) — its name sid and small icon; the profile's picks for its own kit, the rows' first real entries for the others.</summary>
        JArray KitSlots(string p_KitName, (string Label, List<Row> Rows)? p_Kit, bool p_Current)
        {
            var s_Slots = new JArray();
            foreach (var s_Category in new[] { "ID_M_SOLDIER_PRIMARY", "ID_M_SOLDIER_SECONDARY", "ID_M_SOLDIER_GADGET1", "ID_M_SOLDIER_GADGET2", "ID_M_SOLDIER_SPECIALIZATION" })
            {
                var s_Row = p_Kit?.Rows.FirstOrDefault(r => r.Category == s_Category);
                if (s_Row == null || s_Row.Unlocks.Count == 0) { s_Slots.Add(new JObject { ["ItemImage"] = "", ["Label"] = "" }); continue; }
                string s_Unlock;
                if (p_Current && s_Category == "ID_M_SOLDIER_PRIMARY" && PrimaryWeapon() is { } s_Primary) s_Unlock = s_Primary;
                else s_Unlock = s_Row.Unlocks[p_Current ? SelectedIndex(s_Category, s_Row.Unlocks, false) : System.Math.Max(0, s_Row.Unlocks.FindIndex(u => !IsNone(u)))];
                var d = Describe(s_Unlock);
                s_Slots.Add(new JObject { ["ItemImage"] = (string?)d?["IconTexturePath"] ?? "", ["Label"] = (string?)d?["Name"] ?? "" });
            }
            return s_Slots;
        }

        /// <summary>The accessory rows of the profile's weapon, in the table's order with the camo row taken out: 0..2 = the others, 3 = the camo.</summary>
        List<Row>? AccessoryRows(out string p_Table)
        {
            p_Table = "";
            var s_Weapon = PrimaryWeapon();
            return s_Weapon == null ? null : AccessoryRowsOf(s_Weapon, out p_Table);
        }

        /// <summary>A weapon's accessory rows in the table's order with the camo row last (0..2 = the others, 3 = the camo).</summary>
        List<Row>? AccessoryRowsOf(string p_Weapon, out string p_Table)
        {
            p_Table = "";
            var s_Rows = WeaponRows(p_Weapon);
            if (s_Rows == null) return null;
            p_Table = s_Rows.Value.Table;
            var s_Camo = s_Rows.Value.Rows.FirstOrDefault(r => r.Unlocks.Count > 0 && r.Unlocks.All(IsCamo));
            var s_Others = s_Rows.Value.Rows.Where(r => r != s_Camo).ToList();
            while (s_Others.Count < 3) s_Others.Add(new Row("", new List<string>()));
            var s_Ordered = s_Others.Take(3).ToList();
            s_Ordered.Add(s_Camo ?? new Row("", new List<string>()));
            return s_Ordered;
        }

        JToken? WeaponAccessory(int p_Index, out string p_How)
        {
            p_How = "";
            var s_Rows = AccessoryRows(out var s_Table);
            if (s_Rows == null || p_Index >= s_Rows.Count) return null;
            var s_Row = s_Rows[p_Index];
            p_How = $"{s_Table} part {p_Index}{(p_Index == 3 ? " (camo)" : "")} ({s_Row.Unlocks.Count} unlocks)";
            var s_Payload = RowPayload(s_Row, "part " + p_Index, false);
            // A seam's stand-in for an unlock the client's description index does not know: the game still builds its item, with no
            // name and no picture (0x76C140) — the empty box of keku's 2026-09-29 photo. Only while RUE_SEAM_BLANK_CAMO names an id.
            if (p_Index == 3 && int.TryParse(Environment.GetEnvironmentVariable("RUE_SEAM_BLANK_CAMO"), out var s_BlankId) && s_Payload["Items"] is JArray s_Items)
            {
                s_Items.Add(new JObject
                {
                    ["Locked"] = false, ["Label"] = "", ["ItemImage"] = "", ["Index"] = s_BlankId, ["DefaultSelected"] = false, ["ShowPlus"] = false,
                    ["Description"] = new JObject { ["Icon"] = "", ["Type"] = 1, ["Index"] = s_BlankId, ["Category"] = "", ["Description"] = "", ["Label"] = "", ["Image"] = "" },
                });
                p_How += $" + a blank item {s_BlankId} (RUE_SEAM_BLANK_CAMO)";
            }
            return s_Payload;
        }

        /// <summary>
        /// The weapon row of the accessories screen: the kit's whole primary list, the equipped weapon selected, every weapon with its stats
        /// and the small icons of the accessories it carries (measured 2026-09-16 on the game's own record: 28 items, not one — the row's
        /// arrows change the weapon there too).
        /// </summary>
        JToken? WeaponMain(out string p_How)
        {
            var s_Row = KitRow("ID_M_SOLDIER_PRIMARY", true, out p_How);
            if (s_Row != null) p_How += $", {PrimaryWeapon()} selected with its slot icons";
            return s_Row;
        }

        // ------------------------------------------------------------------------------------------ vehicles (LAND / AIR screens)

        /// <summary>A vehicle class the game customizes: Gameplay/Vehicles/&lt;X&gt;Customization (VeniceVehicleCustomizationAsset) — its name sid, category and parts (one row each).</summary>
        public sealed record VehicleClass(string Partition, string NameSid, bool Air, List<Row> Parts);

        /// <summary>
        /// The classes the game's LAND and AIR screens list, in their order, with the frame of the shared icon clip each row's HeaderIcon
        /// names (measured 2026-09-16 on the game's records: LAND = MBT, IFV; AIR = JET, ATTACK HELI — the other customization assets
        /// the game ships, AAV, LBT, ARTILLERY, SCOUT, were not listed on the recorded profile; their icon frames exist in iconsShared).
        /// </summary>
        static readonly (string Asset, string Icon, bool Listed)[] c_VehicleClasses =
        {
            ("mbt", "mbt", true), ("ifv", "ifv", true), ("aav", "mobileaa", false), ("lbt", "lbt", false), ("artillery", "", false),
            ("jet", "jets", true), ("atkhel", "aheli", true), ("scout", "sheli", false),
        };

        static string VehicleKey(string p_Partition) => p_Partition.Split('/').Last().Replace("customization", "", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Rows of assets that are not the game's (keku 2026-09-26: the vehicles without a customization of their own, whose asset the
        /// CamoFramework adds at the END of the side's list), by name SID and category. The game builds such a row like any other
        /// (measured, sub_883080): the label is the asset's NameSid, no icon frame for a SID outside its eight classes, six empty slots
        /// (the asset has no parts). Empty = the game's rows alone.
        /// </summary>
        public List<(string NameSid, bool Air)> ExtraVehicleRows { get; } = new();

        /// <summary>The vehicle classes of a category as the game lists them: the listed ones, in the game's order, then the extra rows.</summary>
        public List<VehicleClass> VehicleClasses(bool p_Air)
        {
            var s_Out = GameVehicleClasses(p_Air);
            foreach (var (s_Sid, s_Air) in ExtraVehicleRows)
                if (s_Air == p_Air) s_Out.Add(new VehicleClass("(extra row) " + s_Sid, s_Sid, s_Air, new List<Row>()));
            return s_Out;
        }

        List<VehicleClass> GameVehicleClasses(bool p_Air)
        {
            var s_Out = new List<VehicleClass>();
            List<string> s_Names;
            try { s_Names = m_Rime.Partitions("gameplay/vehicles/").ToList(); } catch { return s_Out; }
            foreach (var s_Class in c_VehicleClasses.Where(c => c.Listed))
            {
                var s_Name = s_Names.FirstOrDefault(n => string.Equals(VehicleKey(n), s_Class.Asset, StringComparison.OrdinalIgnoreCase) && n.EndsWith("customization", StringComparison.OrdinalIgnoreCase));
                if (s_Name == null) continue;
                var s_Json = Partition(s_Name);
                var s_Asset = Primary(s_Json ?? new JObject());
                if (s_Json == null || s_Asset == null || (string?)s_Asset["$type"] != "VeniceVehicleCustomizationAsset") continue;
                var s_Air = string.Equals((string?)s_Asset["Category"], "VehicleCategory_Air", StringComparison.OrdinalIgnoreCase);
                if (s_Air != p_Air) continue;
                s_Out.Add(new VehicleClass(s_Name, (string?)s_Asset["NameSid"] ?? s_Name.Split('/').Last(), s_Air, Rows(s_Json, s_Asset["Customization"])));
            }
            return s_Out;
        }

        int SelectedVehicle(bool p_Air) => Current.Selected.TryGetValue(p_Air ? "vehicle air" : "vehicle land", out var s) && int.TryParse(s, out var i) ? i : 0;

        /// <summary>The player picked a vehicle row on the LAND/AIR screen (CustSelectedVehicle = the row's index, in the category the flow graph set). True when it changed.</summary>
        public bool SelectVehicle(int p_Index)
        {
            var s_Air = Current.VehicleCategory == 1;
            if (p_Index < 0 || p_Index >= VehicleClasses(s_Air).Count || p_Index == SelectedVehicle(s_Air)) return false;
            Current.Selected[s_Air ? "vehicle air" : "vehicle land"] = p_Index.ToString();
            foreach (var k in Current.Selected.Keys.Where(k => k.StartsWith("vehicle part ", StringComparison.OrdinalIgnoreCase)).ToList()) Current.Selected.Remove(k);
            return true;
        }

        /// <summary>
        /// The LAND/AIR screen's rows as the game hands them (measured 2026-09-16 on the game's records of customizelandscreen /
        /// customizeairscreen): one item per listed class — {ServiceStars 0, BarValues [0,0], ShowButton1Plus false, ItemImage "" (no
        /// picture), Index = the row's index as a string, Label = the class's name sid (ID_EOR_SCORINGBUCKET_VEHICLE…), SlotsData = six
        /// {ItemImage, Label}: the pilot's three parts then the gunner's three, empty where the class has none (a jet, a tank), HeaderIcon =
        /// the class's icon frame, Locked false}. No DefaultSelected: CustSelectedVehicle is the selected row.
        /// </summary>
        JToken? VehiclesData(bool p_Air, out string p_How)
        {
            p_How = "";
            var s_Classes = VehicleClasses(p_Air);
            if (s_Classes.Count == 0) return null;
            var s_Items = new JArray();
            foreach (var (c, i) in s_Classes.Select((c, i) => (c, i)))
            {
                var s_Slots = new JArray();
                foreach (var s_Seat in p_Air ? new[] { "PILOT", "GUNNER" } : new string?[] { null })
                {
                    var s_Parts = SeatParts(c, s_Seat).Where(x => x.Part.Unlocks.Count > 0).ToList();
                    for (var k = 0; k < 3; ++k)
                    {
                        if (k >= s_Parts.Count) { s_Slots.Add(new JObject { ["ItemImage"] = "", ["Label"] = "" }); continue; }
                        var (s_Part, n) = s_Parts[k];
                        var s_Desc = Describe(s_Part.Unlocks[SelectedIndex($"vehicle part {c.Partition} {n}", s_Part.Unlocks, false)]);
                        s_Slots.Add(new JObject { ["ItemImage"] = (string?)s_Desc?["IconTexturePath"] ?? "", ["Label"] = (string?)s_Desc?["Name"] ?? "" });
                    }
                }
                while (s_Slots.Count < 6) s_Slots.Add(new JObject { ["ItemImage"] = "", ["Label"] = "" });
                var s_Icon = c_VehicleClasses.FirstOrDefault(v => string.Equals(v.Asset, VehicleKey(c.Partition), StringComparison.OrdinalIgnoreCase)).Icon ?? "";
                s_Items.Add(new JObject
                {
                    ["ServiceStars"] = 0, ["BarValues"] = new JArray(0, 0), ["ShowButton1Plus"] = false, ["ItemImage"] = "", ["Index"] = i.ToString(),
                    ["Label"] = c.NameSid, ["SlotsData"] = s_Slots, ["HeaderIcon"] = s_Icon, ["Locked"] = false,
                });
            }
            p_How = $"the {s_Classes.Count} {(p_Air ? "air" : "land")} vehicle classes the game lists (Gameplay/Vehicles/*Customization), each with its six part slots (the shape of the game's own record)";
            return s_Items;
        }

        /// <summary>
        /// The parts of a seat: an attack helicopter names its parts ID_VEHICLE_PILOT_* / ID_VEHICLE_GUNNER_*; a jet (and every land
        /// vehicle) has plain ID_VEHICLE_PASSIVE/ACTIVE/STANCE parts, which are the pilot's — and no gunner (measured 2026-09-16: the
        /// jet's three rows carry ID_VEHICLE_PASSIVE… and its gunner rows come hidden with no data).
        /// </summary>
        static List<(Row Part, int N)> SeatParts(VehicleClass p_Class, string? p_Seat)
        {
            var s_All = p_Class.Parts.Select((p, n) => (Part: p, N: n)).ToList();
            if (p_Seat == null) return s_All;
            var s_HasSeats = s_All.Any(x => x.Part.Category.Contains("GUNNER", StringComparison.OrdinalIgnoreCase));
            if (!s_HasSeats) return string.Equals(p_Seat, "GUNNER", StringComparison.OrdinalIgnoreCase) ? new List<(Row, int)>() : s_All;
            return s_All.Where(x => x.Part.Category.Contains(p_Seat, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>A part row of the selected vehicle of a category: p_Seat picks the air parts (PILOT / GUNNER), p_Index counts within them.</summary>
        JToken? VehiclePart(bool p_Air, string? p_Seat, int p_Index, out string p_How)
        {
            p_How = "";
            var s_Classes = VehicleClasses(p_Air);
            var s_Selected = SelectedVehicle(p_Air);
            if (s_Selected >= s_Classes.Count) return null;
            var c = s_Classes[s_Selected];
            var s_Parts = SeatParts(c, p_Seat);
            if (p_Index >= s_Parts.Count) return null;
            var (s_Part, s_N) = s_Parts[p_Index];
            p_How = $"{c.Partition} part {s_N} {s_Part.Category} ({s_Part.Unlocks.Count} unlocks)";
            return RowPayload(s_Part, $"vehicle part {c.Partition} {s_N}", false, ItemKind.Vehicle, false);
        }

        /// <summary>The selected air vehicle's gunner parts, if any (the air sub screen shows its gunner rows only then).</summary>
        bool HasGunnerSeat()
        {
            var s_Classes = VehicleClasses(true);
            var s_Selected = SelectedVehicle(true);
            return s_Selected < s_Classes.Count && SeatParts(s_Classes[s_Selected], "GUNNER").Count > 0;
        }
    }
}
