-- CamoSoldierView -- the client's side of the SOLDIER's window (keku 2026-09-28: our window in place of APARIENCIA, tabs CONJUNTO ·
-- CABEZA · TORSO · PIERNAS, each part with any other: "todo con todo"). Shipped by the menu's build (make_camomenu_doc.py
-- clientScripts), required by its client script.
--
-- What it does:
--   · the window's MAILBOX: its writer node (SkinTableSet, in ui/flow/screen/customizeskinscreen) gets the table the window lists
--     from -- the level's kits (base / Aftermath), the skins the framework dresses this level and what this player wears on each part
--     of each soldier -- as the level loads and at every change, so the window has it the instant it opens (keku's rule for the
--     vehicle window: "debe ser todo instantáneo");
--   · a PICK in the window ("SKN<part>,<id>,<soldier>", through the AS2 channel) is sent to the server, which keeps it (mod.db),
--     fills this player's slot of that soldier with the parts' pieces and shows it on the ShowRoom's mannequin; the window itself
--     stores our row in this client's profile the game's way, so the deploy sends that slot (ext/Shared/SoldierSkins.lua);
--   · the choices come back from the server once per session ("SoldierSkin:Worn"), so the window marks what is worn;
--   · the DRAG turns the mannequin round, as the weapon and vehicle windows turn theirs (keku 2026-09-28: *"añade la opción de rotar
--     el personaje, pero sólo en 360º, mira cómo lo hacemos con las armas"*): the press and the release come from the window's
--     ActionScript ("WV1" / "WV0": the mouse button does not reach InputManager in the menus), the cursor from InputManager; the
--     turn is the ShowRoom's own soldierRotation (UICustomizationCompData, read live by the game: the weapon view's v14-v15, "ya
--     gira"), its yaw only, all the way round; the game's value back when the window goes.
-- Nothing of a skin is written here: the list is what the framework built from the studio's index (Camos/soldiers.lua), published as
-- _G.WEAPON_CAMO_SKIN_LIST, and the level's pack as _G.WEAPON_CAMO_SKIN_PACK.

local TABLE_NODE = "SkinTableSet"                   -- the window's writer node (make_camomenu_doc.py SOLDIER_TABLE_NODE)
local WINDOW_PARTITION = "customizeskinscreen"      -- its screen (make_camomenu_doc.py SOLDIER_PARTITION, the file's name)
local PARTS = { "head", "torso", "legs" }
-- the turn (the weapon and vehicle views' own values)
local TURN = true                                   -- false = the mannequin stays as the game places it
local ROT_SENS = 0.002                              -- radians of turn per cursor pixel while dragging
local INERTIA = 0.9                                 -- the turn keeps going after the release, this much per frame (0 = stops dead)
local COMP = "UI/UIComponents/UICustomizationComp"
local IGNORED_SCREENS = { "chatscreen", "emptyscreen" }   -- pushed over the window without leaving it

local m_Worn = {}            -- soldier -> { head = id, torso = id, legs = id }: this player's picks (a part not there wears the game's look)
local m_WornAsked = false    -- the server was asked for them (once per session is enough: they stay here across levels)
local m_TableNodes = {}      -- the window's writer nodes of THIS level, kept referenced
local m_TableNodeIds = {}    -- instance guid -> true
local m_TableSaid = nil      -- the last table said in the log (said when it changes)
local m_ShowAsked = -100000  -- when the server was last asked to dress the mannequin for an opening of the window (ms)

local function LOG(p_Text)
	-- (the console only when the framework's switch says so: see WEAPON_CAMO_PRINT in Shared/__init__.lua)
	if _G.WEAPON_CAMO_PRINT then
		print("[CamoSoldier] " .. tostring(p_Text))
	end
	-- to the camo framework's database too: it is on disk and survives the session
	pcall(function() NetEvents:Send("WeaponCamo:Log", "[soldier] " .. tostring(p_Text)) end)
end

-- the table's separators (";" between records, "~" between fields) cannot travel inside a field
local function Field(p_Text)
	return (string.gsub(tostring(p_Text or ""), "[~;]", " "))
end

---The window's table, whole: "@camotable;K~<pack>;S~<id>~<name>~<family>~<thumbnail>~<text>~<soldier>~<parts>;
---SP~<id>~<part>~<thumbnail>~<family>~<name>~<text>;…;SW~<soldier>~<part>~<id>;…" -- SP: a skin part's OWN cell on its tab (keku 2026-09-28:
---each part can be a different thing -- its picture, family, name and INFO text), after its S
local function TableFor()
	local s_Parts = { "K~" .. Field(rawget(_G, "WEAPON_CAMO_SKIN_PACK") or "base") }

	-- each stock look's NAME (the same in every language: the family buttons match it, the localised label says nothing to them)
	for l_Row, l_Look in pairs(rawget(_G, "WEAPON_CAMO_SKIN_ROWS") or {}) do
		s_Parts[#s_Parts + 1] = string.format("R~%d~%s", l_Row, Field(l_Look))
	end

	for _, l_Skin in ipairs(rawget(_G, "WEAPON_CAMO_SKIN_LIST") or {}) do
		if type(l_Skin) == "table" and type(l_Skin.id) == "number" and l_Skin.id ~= 0 then
			s_Parts[#s_Parts + 1] = string.format("S~%d~%s~%s~%s~%s~%s~%s", l_Skin.id, Field(l_Skin.name), Field(l_Skin.family),
				Field(l_Skin.thumb), Field(l_Skin.desc), Field(l_Skin.soldier), Field(table.concat(l_Skin.parts or {}, ",")))

			for _, l_Part in ipairs(PARTS) do
				local l_Thumb = type(l_Skin.partThumbs) == "table" and l_Skin.partThumbs[l_Part] or nil
				local l_Family = type(l_Skin.partFamilies) == "table" and l_Skin.partFamilies[l_Part] or nil
				local l_Name = type(l_Skin.partNames) == "table" and l_Skin.partNames[l_Part] or nil
				local l_Text = type(l_Skin.partTexts) == "table" and l_Skin.partTexts[l_Part] or nil

				if l_Thumb ~= nil or l_Family ~= nil or l_Name ~= nil or l_Text ~= nil then
					s_Parts[#s_Parts + 1] = string.format("SP~%d~%s~%s~%s~%s~%s", l_Skin.id, l_Part, Field(l_Thumb), Field(l_Family), Field(l_Name),
						Field(l_Text))
				end
			end
		end
	end

	for l_Soldier, l_Parts in pairs(m_Worn) do
		for _, l_Part in ipairs(PARTS) do
			if l_Parts[l_Part] ~= nil then
				s_Parts[#s_Parts + 1] = string.format("SW~%s~%s~%d", l_Soldier, l_Part, l_Parts[l_Part])
			end
		end
	end

	return "@camotable;" .. table.concat(s_Parts, ";")
end

---Puts the table into the window's writer nodes: what the window receives when it is entered.
local function WriteTable()
	local s_Text = TableFor()
	local s_Written = 0

	for _, l_Node in ipairs(m_TableNodes) do
		local s_Ok = pcall(function()
			l_Node:MakeWritable()
			l_Node.param = s_Text
		end)

		if s_Ok then
			s_Written = s_Written + 1
		end
	end

	if m_TableSaid ~= s_Text then
		m_TableSaid = s_Text
		LOG(string.format("soldier window table (%d characters) into %d of %d writer(s): %s", string.len(s_Text), s_Written,
			#m_TableNodes, s_Text))
	end
end

---The window's graph: its writer node kept for THIS level and given the table (the screens load again with every level; a node
---kept past its level is written into over freed memory -- the vehicle window paid for it, 2026-09-26). ⛔ Cast to the screen's own
---type before reading its nodes, and each node before reading its name: a field the cast type does not declare reads nil in silence.
local function ScanWindow(p_Asset, p_Where)
	local s_Found, s_New = 0, 0

	pcall(function()
		local l_Graph = _G[tostring(p_Asset.typeInfo.name)](p_Asset)

		for i = 1, #l_Graph.nodes do
			local l_Node = nil
			pcall(function() l_Node = _G[tostring(l_Graph.nodes[i].typeInfo.name)](l_Graph.nodes[i]) end)
			local l_NodeName = nil
			pcall(function() l_NodeName = tostring(l_Node.instanceName) end)

			if l_NodeName == nil or l_NodeName == "" or l_NodeName == "nil" then
				pcall(function() l_NodeName = tostring(l_Node.name) end)
			end

			if l_NodeName == TABLE_NODE and l_Node ~= nil then
				s_Found = s_Found + 1
				local l_Id = tostring(l_Node.instanceGuid)

				if not m_TableNodeIds[l_Id] then
					m_TableNodeIds[l_Id] = true
					m_TableNodes[#m_TableNodes + 1] = l_Node
					s_New = s_New + 1
				end
			end
		end
	end)

	-- (said even when nothing was found: a scan that finds nothing must not read like a window that works)
	LOG(string.format("soldier window writer node (%s): %d found, %d new, %d kept", tostring(p_Where), s_Found, s_New, #m_TableNodes))

	if s_New > 0 then
		WriteTable()
	end
end

Events:Subscribe("Partition:Loaded", function(p_Partition)
	local s_Name = ""
	pcall(function() s_Name = string.lower(tostring(p_Partition.name)) end)

	if string.find(s_Name, WINDOW_PARTITION, 1, true) == nil then
		return
	end

	for _, l_Instance in pairs(p_Partition.instances) do
		local s_Is = false
		pcall(function() s_Is = l_Instance:Is("UIScreenAsset") end)

		if s_Is then
			ScanWindow(l_Instance, "as " .. s_Name .. " loaded")
		end
	end
end)

-- the player's choices: asked of the server once (they stay here across levels), handed back as "<soldier>.<part>=<id>;…"
local function AskWorn(p_Why)
	if m_WornAsked then
		return
	end

	if pcall(function() NetEvents:Send("SoldierSkin:Ask") end) then
		m_WornAsked = true
		LOG("the server asked for this player's soldier looks (" .. tostring(p_Why) .. ")")
	end
end

NetEvents:Subscribe("SoldierSkin:Worn", function(p_Text)
	m_Worn = {}
	local s_Count = 0

	for l_Pair in string.gmatch(tostring(p_Text or ""), "[^;]+") do
		local l_Soldier, l_Part, l_Id = string.match(l_Pair, "^([%w_]+)%.(%a+)=(%d+)$")

		if l_Soldier ~= nil and tonumber(l_Id) ~= nil and tonumber(l_Id) ~= 0 then
			m_Worn[l_Soldier] = m_Worn[l_Soldier] or {}
			m_Worn[l_Soldier][l_Part] = tonumber(l_Id)
			s_Count = s_Count + 1
		end
	end

	LOG("soldier looks from the server: " .. s_Count .. " part(s): " .. tostring(p_Text))
	WriteTable()
end)

Events:Subscribe("Extension:Loaded", function()
	AskWorn("the extension loaded")
end)

Events:Subscribe("Level:Loaded", function()
	AskWorn("a level loaded")
	-- the framework built this level's skins and pack as the level registered its resources: the table carries them
	WriteTable()
end)

Events:Subscribe("Level:RegisterEntityResources", function()
	WriteTable()
end)

Events:Subscribe("Level:Destroy", function()
	-- the window's writer nodes belong to the level that ends (see ScanWindow): found again as the next level's window loads
	m_TableNodes = {}
	m_TableNodeIds = {}
end)

---A pick in the window: that soldier wears it on that part from now on ("all" = the three; id 0 on "all" = the picks of ours off:
---the game's own look). Kept here (the window's marks), sent to the server (kept in mod.db, the soldier dressed).
local function Choose(p_Part, p_Id, p_Soldier)
	if p_Soldier == nil or p_Soldier == "" then
		LOG("a pick with no soldier (" .. tostring(p_Part) .. "," .. tostring(p_Id) .. ") -- the window did not know which one; nothing chosen")
		return
	end

	m_Worn[p_Soldier] = m_Worn[p_Soldier] or {}

	for _, l_Part in ipairs(PARTS) do
		if p_Part == "all" or p_Part == l_Part then
			m_Worn[p_Soldier][l_Part] = (p_Id ~= nil and p_Id ~= 0) and p_Id or nil
		end
	end

	WriteTable()
	pcall(function() NetEvents:Send("SoldierSkin:Choose", p_Soldier, p_Part, p_Id or 0) end)
	LOG(string.format("soldier look chosen: %s %s = %s", p_Soldier, tostring(p_Part), (p_Id == nil or p_Id == 0) and "the game's own" or tostring(p_Id)))
end

-- ---- the turn of the mannequin (the drag) -------------------------------------------------------------------------------------
local m_Active = false       -- the soldier window is the screen up
local m_Sub = nil            -- the frame's subscription while it is
local m_Comp = nil           -- UICustomizationCompData, writable
local m_Default = nil        -- its values as the game had them (a clone)
local m_Yaw = 0.0            -- our turn, added to the game's
local m_Pending = 0.0        -- the turn still to apply (inertia)
local m_LastCursor = nil
local m_AsButton = false     -- the window's ActionScript says the button is down right of the panel

local function Comp()
	if m_Comp ~= nil then
		return m_Comp
	end

	local s_Ok, s_Err = pcall(function()
		local s_Raw = ResourceManager:SearchForDataContainer(COMP)

		if s_Raw == nil then
			error("no " .. COMP .. " loaded")
		end

		local s_Comp = UICustomizationCompData(s_Raw)
		s_Comp:MakeWritable()
		m_Default = UICustomizationCompData(s_Comp:Clone())
		m_Comp = s_Comp
	end)

	if not s_Ok then
		LOG("the customization component is not reachable: " .. tostring(s_Err))
	end

	return m_Comp
end

---Our yaw on the game's own, round the vertical only (the game's tilt and roll kept).
local function ApplyTurn()
	if m_Comp == nil or m_Default == nil then
		return
	end

	pcall(function()
		m_Comp.soldierRotation = Vec3(m_Default.soldierRotation.x, m_Default.soldierRotation.y + m_Yaw, m_Default.soldierRotation.z)
	end)
end

local function RestoreTurn()
	if m_Comp == nil or m_Default == nil then
		return
	end

	pcall(function()
		m_Comp.soldierRotation = Vec3(m_Default.soldierRotation.x, m_Default.soldierRotation.y, m_Default.soldierRotation.z)
	end)
end

local function OnUpdateInput(p_Delta)
	if not m_Active then
		return
	end

	local s_Cursor = InputManager:GetCursorPosition()

	if m_AsButton and m_LastCursor ~= nil then
		m_Pending = (s_Cursor.x - m_LastCursor.x) * ROT_SENS
	else
		m_Pending = m_Pending * INERTIA
	end

	-- a turn that has run down stops (inertia alone never reaches zero)
	if math.abs(m_Pending) < 0.00001 then
		m_Pending = 0.0
	end

	m_LastCursor = s_Cursor

	if m_Pending ~= 0.0 then
		m_Yaw = m_Yaw + m_Pending

		-- all the way round, kept within one turn
		while m_Yaw > math.pi do m_Yaw = m_Yaw - 2.0 * math.pi end
		while m_Yaw < -math.pi do m_Yaw = m_Yaw + 2.0 * math.pi end

		ApplyTurn()
	end
end

local function Enter()
	if not TURN or m_Active then
		return
	end

	m_Active = true
	m_Yaw, m_Pending = 0.0, 0.0
	m_LastCursor = nil
	m_AsButton = false

	if Comp() ~= nil then
		ApplyTurn()
	end

	if m_Sub == nil then
		m_Sub = Events:Subscribe("Client:UpdateInput", OnUpdateInput)
	end

	LOG(string.format("the mannequin's turn ON (the game's yaw %.0f deg)", m_Default ~= nil and math.deg(m_Default.soldierRotation.y) or 0))
end

local function Leave(p_Why)
	if not m_Active then
		return
	end

	m_Active = false

	if m_Sub ~= nil then
		pcall(function() m_Sub:Unsubscribe() end)
		m_Sub = nil
	end

	RestoreTurn()
	LOG(string.format("the mannequin's turn OFF (%s): turned %.0f deg, the game's value back", tostring(p_Why), math.deg(m_Yaw)))
	m_AsButton = false
end

-- the gate: the screen the game pushes (a screen pushed over the window without leaving it changes nothing)
Hooks:Install("UI:PushScreen", 997, function(p_Hook, p_Screen, p_Priority, p_ParentGraph)
	local s_Name = ""

	pcall(function()
		s_Name = string.lower(tostring(UIGraphAsset(p_Screen).name))
	end)

	if string.find(s_Name, WINDOW_PARTITION, 1, true) ~= nil then
		Enter()
		return
	end

	for _, l_Ignored in ipairs(IGNORED_SCREENS) do
		if string.find(s_Name, l_Ignored, 1, true) ~= nil then
			return
		end
	end

	Leave("screen " .. s_Name)
end)

Events:Subscribe("Extension:Unloading", function()
	Leave("extension unloading")
end)

Events:Subscribe("Level:Destroy", function()
	Leave("level destroyed")
	-- the component is the level's: taken again on the next window
	m_Comp, m_Default = nil, nil
end)

-- the window's ActionScript: its list (SGR), its table (STB), which soldier (SKK), a tab (SKT), a pick (SKN) and the drag (WV1 / WV0)
Events:Subscribe("AS2:Frame", function(p_Text)
	if type(p_Text) ~= "string" then
		return
	end

	local s_Tag = string.sub(p_Text, 1, 3)

	if s_Tag == "WV1" then
		-- the left button only ("" = no code reported, "1" = left: GFx codes), and only while this window is up (the weapon and
		-- vehicle windows send the same words)
		local s_Code = string.sub(p_Text, 4)
		m_AsButton = m_Active and (s_Code == "" or s_Code == "1")
	elseif p_Text == "WV0" then
		m_AsButton = false
	elseif s_Tag == "SKN" then
		-- "SKN<part>,<id>,<soldier>"
		local l_Part, l_Id, l_Soldier = string.match(string.sub(p_Text, 4), "^(%a+),(%d+),?(.*)$")
		Choose(l_Part, tonumber(l_Id), l_Soldier)
	elseif s_Tag == "SGR" then
		LOG("soldier window list: " .. string.sub(p_Text, 4) .. " (the game's looks, the tab's cells, the soldier)")
		-- the window got its rows = it has just opened: the game's way in (UpdateSoldierLoadout) gave the mannequin the player's own
		-- look again, so the server dresses it with his picks a moment later (once per opening: the rows come again after a pick)
		local l_Now = SharedUtils:GetTimeMS()

		if l_Now - m_ShowAsked > 3000 then
			m_ShowAsked = l_Now
			pcall(function() NetEvents:Send("SoldierSkin:Show") end)
		end
	elseif s_Tag == "STB" then
		LOG("soldier window table read: " .. string.sub(p_Text, 4) .. " (skins of this soldier, the soldier, head/torso/legs worn)")
	elseif s_Tag == "SKK" then
		LOG("soldier window is about: " .. string.sub(p_Text, 4))
	elseif s_Tag == "SKT" then
		LOG("soldier window tab: " .. string.sub(p_Text, 4) .. " (tab, cells)")
	end
end)

LOG("on: the soldier window's mailbox (" .. TABLE_NODE .. ") and its picks")
