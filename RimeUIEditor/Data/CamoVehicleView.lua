-- CamoVehicleView -- the 3D half of a VEHICLE's camo window (the CAMUFLAJE button of a TIERRA / AIRE row, keku 2026-09-23:
-- *"del mismo modo dejamos rotar el vehículo"*): while the window is up the game's own vehicle stands where the weapon windows
-- show the weapon -- the right half of the screen, clear of the list -- and a left-button drag over it turns it.
--
-- ⭐ HOW (v21): NOTHING is moved. The ShowRoom camera's forward is tilted, and the game itself places its vehicle off the middle.
-- Read in the client (2026-09-23):
--   · the customization's update places its vehicle, 1 frame in 8, from the ACTIVE camera's transform taken RAW:
--     camera + z·forward + y·up (z and y = the vehicle's own VehicleHudData.customizationOffset; its x only drives the slide-in),
--     turned by the yaw of that forward plus vehicleRotation;
--   · the camera takes that transform straight from its data (times the prefab's transform: linear, nothing normalised);
--   · the picture is drawn from the same transform, but its view matrix is built as the transpose of it (the inverse of a
--     transform whose rows are square to each other): what is drawn in the middle is the direction square to the camera's left
--     and up rows -- the forward row only scales the depth.
-- So with the camera's data forward tilted toward its left row (forward + a·left, left and up untouched), the game puts its
-- vehicle z·a to the side along that tilted forward, and the picture still looks where it looked: the vehicle is DRAWN off the
-- middle, by the game's own placement -- nothing of ours fights it, so nothing flickers. What it costs: the depth of the picture
-- is tilted with the forward (a point's depth grows by a·its sideways distance), a light keystone across the screen.
-- The camera's data is put back as the game had it when the window goes.
-- 📐 v21 MEASURED (keku 2026-09-23 22:40): *"ya no parpadea"* -- but the vehicle's edges smeared sideways, *"como si fuera motion
-- blur parado"*. Why: the camera's motion blur rebuilds each pixel's world place from its depth with the camera's transform taken
-- as the view's inverse -- the raw, tilted one, which is not (the view is its transpose) -- so a still pixel comes out metres to
-- the side and gets a speed. So the motion blur is switched off while the camera is tilted, and put back as it was.
-- 📐 MEASURED (keku 2026-09-26): the same for the SHADOW MAPS -- *"en el selector de camos de los vehículos se proyecta una sombra
-- gigante detrás siempre"*, a dark column behind the vehicle in this window only; `camovehicle render shadowmapsEnable off` took it
-- away. So they are switched off while the camera is tilted too, and put back as they were when the window goes.
--
-- ⛔ WHAT WAS TRIED BEFORE, AND WHY IT IS GONE (his boots, 2026-09-23):
--   (1) a copy of the vehicle made of entities of ours: every entity made from Lua with a vehicle's composite mesh crashed the
--       client (an access violation in lua_gettop), 3 of 3;
--   (2) the camera TURNED (its data, a camera of our own, the view's free camera through spectating): the game places its vehicle
--       on the active camera's axis, and the picture is drawn from that same camera -- the vehicle follows any turn;
--   (3) the vehicle MOVED after the game places it, from every hook VU has (and from the server, and from a ping of the window's
--       ActionScript): the game's placement falls after all of them and right before the picture, 1 frame in 8 -- it flickered;
--   (4) the customization component switched off through its data: the component keeps a copy of its update settings.
-- ⭐ THE ZOOM (keku 2026-09-23, once v21 held: *"de la misma manera ahora podemos activar de nuevo el zoom"*), the same way: the
-- game's placement reads the vehicle's customizationOffset every time it places it, so the offset's z and y are scaled (the
-- vehicle's own VehicleEntityData, put back when it goes) and the game itself brings its vehicle closer -- the tilt's sideways
-- offset is z·tilt, so it scales with it and the vehicle keeps its spot on the screen. The weapon view's keys: Page Up / Page
-- Down, and Shift held while moving the mouse (forward = closer); the wheel scrolls the list. It moves at the placement's pace.
--
-- WHICH vehicle is never named here: a map fields its own and the mode its own (keku: *"dependiendo del mapa y el modo de juego
-- habrán unos vehículos u otros"*); the one the game spawned for the row is found as the ClientVehicleEntity nearest the camera
-- (measured: 21-34 m; the map's own ones 60+ m away), only to read its offset and to check where it ends up.
--
-- The press and the release of the drag come from the window's ActionScript through the AS2 channel ("WV1" / "WV0", the weapon
-- window's words): the mouse button does not reach InputManager in the menus (measured on the weapon view).
--
-- F8 while the window is up prints the state. Console: camovehicle [on | off | <tangent> | blur on | blur off | shadows on |
-- shadows off | render [<switch> on|off]].

-- ---- knobs -------------------------------------------------------------------------------------------------------------------
local ENABLED = true                     -- false = nothing of this (the game shows its vehicle as it always does)
local AT_X = 0.30 / 1.85                 -- where the vehicle goes on the screen, as the tangent of its angle off the axis: the weapon
                                         -- view's PROP_AT (0.30 m to the right at 1.85 m)
local SCENE_CAMERA = "528655FC-2653-4D5B-B55D-E6CBF997FC19"   -- Gameplay/Logic/ShowRoom: CustomizeCamera (one instance, every level)
local CHECK_AFTER_MS = 2500              -- where the vehicle stands is checked this long after it is found: past the game's slide-in
                                         -- (x·300·e^(-5t) to the side: the M1A2 was still 180 m out at 1.2 s)
local COMP = "UI/UIComponents/UICustomizationComp"
local GAME_PITCH_START = 0.1             -- the game's turn of its vehicle: the tilt on entering (ShowRoom's)
local SCREENS = {                        -- the windows this view belongs to (partition names, any case) and their kind
	["customizelandcamoscreen"] = "land",
	["customizeaircamoscreen"] = "air",
}
local ROT_SENS = 0.002                   -- radians of turn per cursor pixel while dragging (the weapon view's)
local PITCH_SENS = 0.002                 -- radians of tilt per cursor pixel while dragging
local PITCH_MIN, PITCH_MAX = -0.35, 0.60 -- the tilt stays within this (radians; positive = the side facing you goes up)
local INERTIA = 0.9                      -- the turn keeps going after the release, this much per frame (0 = stops dead)
local FIND_RADIUS = 60.0                 -- metres from the camera a vehicle may be to be the customization's own (a map's vehicles are far)
local IGNORED_SCREENS = { "chatscreen", "emptyscreen" }   -- pushed over ours without leaving it
local QUIET_BLUR = true                  -- the motion blur off while the camera is tilted (it smears the still vehicle; see the header)
local QUIET_SHADOWS = true               -- the shadow maps off while the camera is tilted (they cast a dark column; see the header)
local ZOOM_NEAR, ZOOM_FAR = 0.45, 1.35   -- the zoom, as a factor of the game's own distance (0.45 = 2.2 times as big)
local ZOOM_STEP = 0.08                   -- factor per Page Up / Page Down press (the weapon view's 0.15 m of 1.85)
local ZOOM_SENS = 0.0032                 -- factor per cursor pixel while zooming by holding (the weapon view's 0.006 m of 1.85)
local ZOOM_HOLD_KEY = InputDeviceKeys.IDK_LeftShift   -- held = mouse forward / back zooms (the weapon view's)

-- ---- state -------------------------------------------------------------------------------------------------------------------
local m_Active = false
local m_Kind = ""           -- "land" / "air"
local m_Subs = {}           -- the event subscriptions while active
local m_Yaw = 0.0           -- our turn of the vehicle, added to the game's
local m_Pitch = 0.0         -- and its tilt
local m_Pending = 0.0       -- the turn still to apply (inertia)
local m_PendingPitch = 0.0
local m_LastCursor = nil
local m_AsButton = false    -- the window's ActionScript says the button is down over the 3D half
local m_FindSaid = false
-- the game's vehicle, while it is there
local m_Vehicle = nil       -- its entity
local m_VehicleId = nil     -- its instance id (a new spawn is a new id)
local m_Name = nil          -- its blueprint ("vehicles/ah1z/ah1z")
local m_Offset = nil        -- its VehicleHudData.customizationOffset (a copy)
local m_CheckAt = nil       -- when where it stands is checked (ms)
-- the ShowRoom camera's data
local m_On = true           -- the tilt is wanted (console: camovehicle on / off; outlives the window)
local m_Tan = AT_X          -- how far off the middle, as a tangent (console: camovehicle <tangent>)
local m_CamData = nil       -- its CameraEntityData, writable, while tilted
local m_CamHome = nil       -- its transform as the game had it
local m_Tilt = 0.0          -- the tilt in force: forward = home forward + m_Tilt · home left
-- the motion blur
local m_Quiet = QUIET_BLUR  -- it is switched off while tilted (console: camovehicle blur on / off; outlives the window)
local m_Blur = nil          -- WorldRenderSettings, writable, while it is switched off
local m_BlurWas = nil       -- its motionBlurEnable as it was
-- the shadow maps
local m_NoShadows = QUIET_SHADOWS   -- they are switched off while tilted (console: camovehicle shadows on / off; outlives the window)
local m_Shadows = nil       -- WorldRenderSettings, writable, while they are switched off
local m_ShadowsWas = nil    -- its shadowmapsEnable as it was
-- the zoom
local m_Zoom = 1.0          -- the factor on the game's distance in force (1 = as the game has it)
local m_ZoomData = nil      -- the vehicle's VehicleEntityData, writable, while its offset is scaled
local State = nil

local function LOG(p_Text)
	print("[CamoVehicle] " .. tostring(p_Text))
	-- (no event to other mods: see CamoWeaponView.lua's LOG)
	-- to the camo framework's database too: it is on disk and survives the session (the console cannot be read afterwards)
	pcall(function() NetEvents:Send("WeaponCamo:Log", "[vehicle] " .. tostring(p_Text)) end)
end

-- ---- vectors -----------------------------------------------------------------------------------------------------------------
local function Sum(a, b) return Vec3(a.x + b.x, a.y + b.y, a.z + b.z) end
local function Diff(a, b) return Vec3(a.x - b.x, a.y - b.y, a.z - b.z) end
local function Scaled(a, k) return Vec3(a.x * k, a.y * k, a.z * k) end
local function Dot(a, b) return a.x * b.x + a.y * b.y + a.z * b.z end
local function Length(a) return math.sqrt(Dot(a, a)) end

---A transform as a fresh value of its own (what the data hands over is not kept).
local function Copy(p_T)
	return LinearTransform(Vec3(p_T.left.x, p_T.left.y, p_T.left.z), Vec3(p_T.up.x, p_T.up.y, p_T.up.z),
		Vec3(p_T.forward.x, p_T.forward.y, p_T.forward.z), Vec3(p_T.trans.x, p_T.trans.y, p_T.trans.z))
end

-- ---- the game's turn of its vehicle (the drag) ---------------------------------------------------------------------------------
local m_Comp = nil          -- UICustomizationCompData, writable
local m_Default = nil       -- its values as the game had them (a clone)

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

---Our turn on the game's own: yaw added to the game's, the tilt from ShowRoom's start (x = pitch·cos(yaw), y = yaw, z = pitch·sin(yaw)).
local function ApplyGameTurn()
	if m_Comp == nil or m_Default == nil then
		return
	end

	pcall(function()
		local l_Yaw = m_Default.vehicleRotation.y + m_Yaw
		local l_Pitch = GAME_PITCH_START + m_Pitch
		m_Comp.vehicleRotation = Vec3(l_Pitch * math.cos(l_Yaw), l_Yaw, l_Pitch * math.sin(l_Yaw))
	end)
end

local function RestoreGameTurn()
	if m_Comp == nil or m_Default == nil then
		return
	end

	pcall(function()
		m_Comp.vehicleRotation = Vec3(m_Default.vehicleRotation.x, m_Default.vehicleRotation.y, m_Default.vehicleRotation.z)
	end)
end

-- ---- the ShowRoom camera's tilt ----------------------------------------------------------------------------------------------------
---The ShowRoom camera's entity (the ClientCameraEntity whose data is its CustomizeCamera), or nil.
local function SceneCamera()
	local s_Found = nil

	pcall(function()
		local s_Iter = EntityManager:GetIterator("ClientCameraEntity")
		local s_Entity = s_Iter:Next()

		while s_Entity ~= nil and s_Found == nil do
			local l_Guid = ""
			pcall(function() l_Guid = string.upper(tostring(s_Entity.data.instanceGuid)) end)

			if l_Guid == SCENE_CAMERA then
				s_Found = s_Entity
			end

			s_Entity = s_Iter:Next()
		end
	end)

	return s_Found
end

---The motion blur put back as it was.
local function BlurBack(p_Why)
	if m_Blur == nil then
		return
	end

	local s_Ok, s_Err = pcall(function() m_Blur.motionBlurEnable = m_BlurWas end)
	LOG("the motion blur put back to " .. tostring(m_BlurWas) .. " (" .. tostring(p_Why) .. ")" .. (s_Ok and "" or (" -- ⛔ could not be: " .. tostring(s_Err))))
	m_Blur, m_BlurWas = nil, nil
end

---The motion blur switched off (only when wanted, and once: what it was is kept to put it back).
local function BlurOff()
	if not m_Quiet then
		BlurBack("wanted on")
		return
	end

	if m_Blur ~= nil then
		return
	end

	local s_Ok, s_Err = pcall(function()
		local l_Raw = ResourceManager:GetSettings("WorldRenderSettings")

		if l_Raw == nil then
			error("no WorldRenderSettings")
		end

		local l_Settings = WorldRenderSettings(l_Raw)
		l_Settings:MakeWritable()
		m_BlurWas = l_Settings.motionBlurEnable
		l_Settings.motionBlurEnable = false
		m_Blur = l_Settings
	end)

	LOG(s_Ok and ("the motion blur switched off while the camera is tilted (it was " .. tostring(m_BlurWas) .. ")")
		or ("the motion blur cannot be switched off: " .. tostring(s_Err)))
end

---The shadow maps put back as they were.
local function ShadowsBack(p_Why)
	if m_Shadows == nil then
		return
	end

	local s_Ok, s_Err = pcall(function() m_Shadows.shadowmapsEnable = m_ShadowsWas end)
	LOG("the shadow maps put back to " .. tostring(m_ShadowsWas) .. " (" .. tostring(p_Why) .. ")" .. (s_Ok and "" or (" -- ⛔ could not be: " .. tostring(s_Err))))
	m_Shadows, m_ShadowsWas = nil, nil
end

---The shadow maps switched off (only when wanted, and once: what they were is kept to put them back).
local function ShadowsOff()
	if not m_NoShadows then
		ShadowsBack("wanted on")
		return
	end

	if m_Shadows ~= nil then
		return
	end

	local s_Ok, s_Err = pcall(function()
		local l_Raw = ResourceManager:GetSettings("WorldRenderSettings")

		if l_Raw == nil then
			error("no WorldRenderSettings")
		end

		local l_Settings = WorldRenderSettings(l_Raw)
		l_Settings:MakeWritable()
		m_ShadowsWas = l_Settings.shadowmapsEnable
		l_Settings.shadowmapsEnable = false
		m_Shadows = l_Settings
	end)

	LOG(s_Ok and ("the shadow maps switched off while the camera is tilted (they were " .. tostring(m_ShadowsWas) .. ")")
		or ("the shadow maps cannot be switched off: " .. tostring(s_Err)))
end

-- ⭐ THE RENDER SWITCHES, live (keku 2026-09-26: *"en el selector de camos de los vehículos se proyecta una sombra gigante detrás
-- siempre"* -- a dark column behind the vehicle, only in this window). The tilt's known cost: what rebuilds a pixel's world place from
-- the camera's RAW (skewed) transform lands beside the truth -- the motion blur did (see the header). Which pass draws the column is
-- not known yet: `camovehicle render` lists these, `camovehicle render <switch> on|off` flips one while you look, to find it. Written
-- straight into the game's WorldRenderSettings and left as set (a restart puts them back).
local RENDER_SWITCHES = { "shadowmapsEnable", "applyShadowmapsEnable", "generateShadowmapsEnable", "cloudShadowEnable",
	"outdoorLightTilingEnable", "outdoorLightTileRenderEnable", "dxDeferredCsPathEnable", "skyFogEnable", "zBufferShadowTestEnable",
	"shadowmapCullVolumeEnable", "motionBlurEnable" }

---The game's WorldRenderSettings, writable, or nil and why.
local function RenderSettings()
	local s_Settings, s_Err = nil, nil
	local s_Ok, s_Raised = pcall(function()
		local l_Raw = ResourceManager:GetSettings("WorldRenderSettings")

		if l_Raw == nil then
			error("no WorldRenderSettings")
		end

		s_Settings = WorldRenderSettings(l_Raw)
		s_Settings:MakeWritable()
	end)

	if not s_Ok then
		s_Err = tostring(s_Raised)
		s_Settings = nil
	end

	return s_Settings, s_Err
end

---`camovehicle render [<switch> on|off]`: the list with the values now, or one flipped (and said as it was -> as it is).
local function RenderCommand(p_Switch, p_Word)
	local s_Settings, s_Err = RenderSettings()

	if s_Settings == nil then
		return "no render settings: " .. tostring(s_Err)
	end

	if p_Switch == nil or p_Switch == "" then
		local s_Lines = {}

		for _, l_Name in ipairs(RENDER_SWITCHES) do
			local l_Value = "?"
			pcall(function() l_Value = tostring(s_Settings[l_Name]) end)
			s_Lines[#s_Lines + 1] = l_Name .. "=" .. l_Value
		end

		return "render switches: " .. table.concat(s_Lines, " ") .. " -- camovehicle render <switch> on|off"
	end

	-- the switch as written, or matched without regard to case
	local s_Name = nil

	for _, l_Name in ipairs(RENDER_SWITCHES) do
		if string.lower(l_Name) == string.lower(p_Switch) then
			s_Name = l_Name
		end
	end

	s_Name = s_Name or p_Switch

	if p_Word ~= "on" and p_Word ~= "off" then
		return "use: camovehicle render " .. s_Name .. " on / off"
	end

	local s_Was = "?"
	pcall(function() s_Was = tostring(s_Settings[s_Name]) end)
	local s_Ok, s_Raised = pcall(function() s_Settings[s_Name] = (p_Word == "on") end)
	local s_Now = "?"
	pcall(function() s_Now = tostring(s_Settings[s_Name]) end)
	local s_Line = string.format("render %s: %s -> %s%s", s_Name, s_Was, s_Now, s_Ok and "" or (" -- ⛔ " .. tostring(s_Raised)))
	LOG(s_Line)
	return s_Line
end

---The camera's data put back as the game had it.
local function Untilt(p_Why)
	BlurBack(p_Why)
	ShadowsBack(p_Why)

	if m_CamData == nil or m_CamHome == nil then
		return
	end

	local s_Ok, s_Err = pcall(function() m_CamData.transform = Copy(m_CamHome) end)
	LOG("the ShowRoom camera put back (" .. tostring(p_Why) .. ")" .. (s_Ok and "" or (" -- ⛔ could not be: " .. tostring(s_Err))))
	m_CamData, m_CamHome, m_Tilt = nil, nil, 0.0
end

---The tilt that puts the vehicle m_Tan off the middle toward the screen's right. The game puts it at z along the forward, and the
---tilt adds z·tilt along the camera's left -- which is the screen's right (the camera's forward points back: v12 moved the vehicle
---along +left and keku saw it on the right) -- so tilt = m_Tan·|z| / z. Before the vehicle is known, z is taken as negative
---(every vehicle measured: AH-1Z -26, M1A2 -22).
local function WantedTilt()
	if m_Offset ~= nil and m_Offset.z > 0.0 then
		return m_Tan
	end

	return -m_Tan
end

---Tilts the ShowRoom camera's forward toward its left (left, up and place untouched), taking its data the first time.
local function Tilt()
	if not m_On then
		Untilt("switched off")
		return
	end

	local s_Tilt = WantedTilt()

	if m_CamData ~= nil and s_Tilt == m_Tilt then
		BlurOff()
		ShadowsOff()
		return
	end

	local s_Ok, s_Err = pcall(function()
		if m_CamData == nil then
			local l_Entity = SceneCamera()

			if l_Entity == nil then
				error("no ClientCameraEntity carries the ShowRoom's CustomizeCamera")
			end

			local l_Data = CameraEntityData(l_Entity.data)
			l_Data:MakeWritable()
			m_CamHome = Copy(l_Data.transform)
			m_CamData = l_Data
		end

		local l_Home = Copy(m_CamHome)
		m_CamData.transform = LinearTransform(l_Home.left, l_Home.up, Sum(l_Home.forward, Scaled(l_Home.left, s_Tilt)), l_Home.trans)
		m_Tilt = s_Tilt
	end)

	if not s_Ok then
		LOG("the ShowRoom camera cannot be tilted: " .. tostring(s_Err) .. " -- the vehicle stays in the middle")
		m_CamData, m_CamHome, m_Tilt = nil, nil, 0.0
		return
	end

	LOG(string.format("the ShowRoom camera's forward tilted %.3f toward its left (%.1f deg): the game places its vehicle off the middle",
		m_Tilt, math.deg(math.atan(m_Tilt))))
	BlurOff()
	ShadowsOff()
end

-- ---- the vehicle CAMOS -----------------------------------------------------------------------------------------------------------
-- keku chose "way 1" (2026-09-23, *"hay que hacerlo como con los accesorios"*): each player sees HIS camo on every vehicle of that
-- kind -- on this client only. Measured first: the variation a vehicle is made with does not reach its mesh on the client (the
-- ShowRoom's M1A2 handed a hash with no entry stayed visible), so a camo cannot ride on a hash. The accessories' recipe instead:
-- the body mesh gets a SECOND IDENTITY per camo (a clone under a name of its own, whose hash-0 entry is painted -- baked into the
-- camo's package, which the framework mounts), and the vehicle's data is pointed at the clone BEFORE the vehicle is made. Measured in game
-- (keku, 2026-09-23: *"aparece el camo directo, funciona"*): the ShowRoom's M1A2 came up wearing Berkut.
-- ⭐ SELECTABLE IN THE WINDOW, AT ONCE (keku: *"ahora debes hacerlo que se pueda seleccionar en la ventana de camos"* and *"no
-- queremos que salgan al cabo de 1s, debe ser todo instantáneo"*):
--   · a camo is chosen per vehicle CLASS -- the TIERRA / AIRE row, what the window's name binding carries the instant it opens
--     (UIKitComp.SelectedVehicleName = the class SID, ID_EOR_SCORINGBUCKET_VEHICLEMBT for an MBT); it paints every vehicle of the
--     class it has a clone for (the MBT row is the M1A2 or the T-90, depending on the team);
--   · the window's table (its own mailbox, VehicleTableSet, see make_camomenu_doc.py) is written as the level loads and at every
--     change, never waiting for the vehicle: every camo with the classes it has a clone for, and what each class wears;
--   · a pick ("VVC<id>,<class>") points the class's vehicles at the camo's clones (or back at their own mesh), and the window's
--     graph chains the glitch and UnspawnVehicle, and the window a moment later SpawnVehicle ("VRS") -- the ShowRoom's vehicle
--     is made again, wearing it. ⛔ Neither SpawnVehicle alone (run 110) nor SetCustomization ["0"] (run 111) makes it again (the
--     old one stayed, old mesh), and a spawn in the same chain as the unspawn does nothing (run 112: gone, nothing back);
--   · the choice is this player's: kept by the server in mod.db (vehcamo) and handed back when he joins.
-- Vehicles made before a switch keep the mesh they were made with. Console: camovehicle camo on | off (all of it, this client).
-- ⭐ NOTHING OF A CAMO IS WRITTEN HERE (keku: *"el camo studio al bakear los camos la UI debe identificarlo y que aparezca
-- automaticamente/dinamicamente"*): the list is read from the camo packages' index, which the studio regenerates on every bake
-- and register. A package that paints vehicles lists them (`vehicles = { { blueprint, class, clone }, … }`); the framework mounts
-- it like any package, and its camo shows up in the window of each class it lists -- no build of the window, no list to edit.
local NO_PICTURE = "UI/Art/Persistence/WeaponAccessory/NoSelection"   -- a package with no thumbnail of its own (the rows' fallback)
local VEHICLE_CAMO_LIST = {}   -- { id, name, family, thumb, desc, clones = { blueprint -> clone }, hides = { blueprint -> guids } }
local VEHICLE_CLASS_OF = {}    -- blueprint (lower case) -> the class SID its customization row goes by

do
	-- the table's separators (";" between entries, "~" between fields) cannot travel inside a field
	local function Field(p_Text)
		return (string.gsub(tostring(p_Text or ""), "[~;]", " "))
	end

	local s_Ok, s_Index = pcall(require, "__shared/Camos/index")
	local s_Said = {}

	for _, l_Entry in ipairs((s_Ok and type(s_Index) == "table") and s_Index or {}) do
		if type(l_Entry) == "table" and type(l_Entry.vehicles) == "table" and type(l_Entry.identifier) == "number" and
			l_Entry.identifier ~= 0 then
			local l_Camo = {
				id = l_Entry.identifier,     -- the package's identifier: the cell's, and what travels as "VVC<id>" and is kept
				name = Field(l_Entry.name or l_Entry.key),
				-- the family button it is filed under, chosen at the bake (keku 2026-09-24: *"que me deje elegir en qué categoría
				-- de camo irá"*); "" = the window guesses it from the name, as it does for a weapon camo baked without one
				family = Field(l_Entry.family),
				thumb = Field(type(l_Entry.thumbnail) == "string" and l_Entry.thumbnail ~= "" and l_Entry.thumbnail or NO_PICTURE),
				desc = Field(l_Entry.text),
				clones = {},
				-- blueprint (lower case) -> the stock parts its pieces replace (upper-case guids): what the preview BY PIECES hides
				hides = {},
			}
			local l_Count = 0

			for _, l_Vehicle in ipairs(l_Entry.vehicles) do
				if type(l_Vehicle) == "table" and type(l_Vehicle.blueprint) == "string" and type(l_Vehicle.clone) == "string" and
					type(l_Vehicle.class) == "string" and l_Vehicle.class ~= "" then
					l_Camo.clones[l_Vehicle.blueprint] = l_Vehicle.clone
					VEHICLE_CLASS_OF[string.lower(l_Vehicle.blueprint)] = l_Vehicle.class
					l_Count = l_Count + 1

					if type(l_Vehicle.pieces) == "table" and #l_Vehicle.pieces > 0 then
						local l_Hides = {}

						for _, l_Piece in ipairs(l_Vehicle.pieces) do
							for l_Guid in string.gmatch(tostring(type(l_Piece) == "table" and l_Piece.hide or ""), "[^,%s]+") do
								l_Hides[#l_Hides + 1] = string.upper(l_Guid)
							end
						end

						l_Camo.hides[string.lower(l_Vehicle.blueprint)] = l_Hides
					end
				end
			end

			if l_Count > 0 then
				VEHICLE_CAMO_LIST[#VEHICLE_CAMO_LIST + 1] = l_Camo
				s_Said[#s_Said + 1] = string.format("%s (%d, %d vehicle(s))", l_Camo.name, l_Camo.id, l_Count)
			end
		end
	end

	LOG("vehicle camos from the index: " .. (s_Ok and (#s_Said > 0 and table.concat(s_Said, ", ") or "none")
		or ("no index (" .. tostring(s_Index) .. ")")))
end
local VEHICLE_TABLE_NODE = "VehicleTableSet"   -- the vehicle windows' writer node (make_camomenu_doc.py)
local m_CamoOn = true
local m_Worn = {}           -- class SID -> camo id: what this player chose for that class
local m_WornAsked = false   -- the server was asked for them (once per session is enough: they stay here across levels)
local m_CamoSwapped = {}    -- blueprint (lower case) -> { data = VehicleEntityData, home = its own mesh }
local m_TableNodes = {}     -- the windows' writer nodes, kept referenced
local m_TableText = "@camotable;"
local m_TableSaid = nil     -- the last table text said in the log (said when it changes, not per write)

local function CamoById(p_Id)
	for _, l_Camo in ipairs(VEHICLE_CAMO_LIST) do
		if l_Camo.id == p_Id then
			return l_Camo
		end
	end

	return nil
end

local function ClassOf(p_Blueprint)
	return p_Blueprint ~= nil and VEHICLE_CLASS_OF[string.lower(p_Blueprint)] or nil
end

---The clone of a camo for a blueprint (any case), or nil when that camo has none for it.
local function CloneOf(p_Camo, p_Blueprint)
	if p_Camo == nil or p_Blueprint == nil then
		return nil
	end

	for l_Blueprint, l_Clone in pairs(p_Camo.clones) do
		if string.lower(l_Blueprint) == string.lower(p_Blueprint) then
			-- "" = the camo paints this vehicle (pieces) but ships no preview clone of its body
			return l_Clone ~= "" and l_Clone or nil
		end
	end

	return nil
end

---Every blueprint some camo has a clone for, as the catalog spells it.
local function CamoBlueprints()
	local s_Out, s_Seen = {}, {}

	for _, l_Camo in ipairs(VEHICLE_CAMO_LIST) do
		for l_Blueprint, _ in pairs(l_Camo.clones) do
			if not s_Seen[string.lower(l_Blueprint)] then
				s_Seen[string.lower(l_Blueprint)] = true
				s_Out[#s_Out + 1] = l_Blueprint
			end
		end
	end

	return s_Out
end

---Points one vehicle's data at what this player wears on its class: the clone of the camo, or its own mesh. Said in the log with
---what was read back. Vehicles already made are not touched (they keep the mesh they were made with).
local function ApplyCamo(p_Blueprint)
	local s_Key = string.lower(p_Blueprint)
	local s_Class = ClassOf(p_Blueprint)
	-- ⛔ stands down while the vehicles wear their DRIVER's camo (ext/Shared/VehicleCamos.lua): the camo is drawn by pieces switched
	-- by the driver's unlock, for everyone, and this swap would paint the stock body underneath them with THIS player's camo
	local s_TestOn = rawget(_G, "VEHICLE_CAMO_PIECES") == true
	local s_Camo = (m_CamoOn and s_Class ~= nil and not s_TestOn) and CamoById(m_Worn[s_Class]) or nil
	local s_Clone = CloneOf(s_Camo, p_Blueprint)
	local s_Swap = m_CamoSwapped[s_Key]

	if s_Clone == nil then
		if s_Swap ~= nil then
			local s_Ok = pcall(function() s_Swap.data.mesh = s_Swap.home end)
			m_CamoSwapped[s_Key] = nil
			LOG("vehicle camo " .. p_Blueprint .. ": back on its own mesh" .. (s_Ok and "" or " (⛔ the write failed)"))
		end

		return
	end

	local s_Line = nil
	local s_Ok, s_Err = pcall(function()
		local l_Data, l_Home = nil, nil

		if s_Swap ~= nil then
			l_Data, l_Home = s_Swap.data, s_Swap.home
		else
			local l_Raw = ResourceManager:SearchForDataContainer(p_Blueprint)

			if l_Raw == nil then
				s_Line = "not in this level"
				return
			end

			l_Data = VehicleEntityData(VehicleBlueprint(l_Raw).object)
			l_Data:MakeWritable()
			l_Home = l_Data.mesh
		end

		local l_Mesh = ResourceManager:SearchForDataContainer(s_Clone)

		if l_Mesh == nil then
			error("the clone " .. s_Clone .. " is not loaded (did its camo package mount?)")
		end

		l_Data.mesh = CompositeMeshAsset(l_Mesh)
		m_CamoSwapped[s_Key] = { data = l_Data, home = l_Home }
		s_Line = string.format("%s -> %s (read back: %s)", s_Camo.name, s_Clone, tostring(l_Data.mesh and l_Data.mesh.name))
	end)

	LOG("vehicle camo " .. p_Blueprint .. ": " .. (s_Ok and tostring(s_Line) or ("⛔ " .. tostring(s_Err))))
end

local function ApplyAllCamos()
	for _, l_Blueprint in ipairs(CamoBlueprints()) do
		ApplyCamo(l_Blueprint)
	end
end

---Every vehicle back on its own mesh (the data is the level's: put back before it goes).
local function UncamoAll(p_Why)
	local s_Count = 0

	for _, l_Swap in pairs(m_CamoSwapped) do
		pcall(function() l_Swap.data.mesh = l_Swap.home end)
		s_Count = s_Count + 1
	end

	if s_Count > 0 then
		LOG(string.format("vehicle camo off (%s): %d vehicle(s) back on their own mesh", tostring(p_Why), s_Count))
	end

	m_CamoSwapped = {}
end

---The windows' table, whole: every camo with the classes it has a clone for ("<id>~<name>~<family>~<thumbnail>~<text>~@V:<class>,…")
---and what each class wears ("W~<id>~<class>"). Nothing in it waits for a vehicle, so a window lists its camos the instant it opens.
---The family is the weapon table's third field, read the same way by the window's script: the button the camo is filed under.
local function TableFor()
	local s_Parts = {}

	for _, l_Camo in ipairs(VEHICLE_CAMO_LIST) do
		local l_Classes, l_Seen = {}, {}

		for l_Blueprint, _ in pairs(l_Camo.clones) do
			local l_Class = ClassOf(l_Blueprint)

			if l_Class ~= nil and not l_Seen[l_Class] then
				l_Seen[l_Class] = true
				l_Classes[#l_Classes + 1] = l_Class
			end
		end

		if #l_Classes > 0 then
			table.sort(l_Classes)
			s_Parts[#s_Parts + 1] = string.format("%d~%s~%s~%s~%s~@V:%s", l_Camo.id, l_Camo.name, l_Camo.family, l_Camo.thumb,
				l_Camo.desc, table.concat(l_Classes, ","))
		end
	end

	if m_CamoOn then
		for l_Class, l_Id in pairs(m_Worn) do
			s_Parts[#s_Parts + 1] = string.format("W~%d~%s", l_Id, l_Class)
		end
	end

	return "@camotable;" .. table.concat(s_Parts, ";")
end

---Puts the table into the windows' writer nodes: what a window receives when it is entered.
local function WriteVehicleTable()
	local s_Text = TableFor()
	m_TableText = s_Text
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
		LOG(string.format("vehicle camo table (%d characters) into %d of %d window writer(s): %s", string.len(s_Text), s_Written,
			#m_TableNodes, s_Text))
	end
end

---A vehicle window's graph: its writer node kept (once: by instance guid) and given the table, for THIS LEVEL: ⛔ the windows load
---again with every level (their partitions' loads are seen again each level -- "they outlive the levels" was never so), and a node
---kept past its level was written into and finalised over freed memory, while the next level's node, of the same guid, was
---never kept (keku 2026-09-26, a client dying at a round change). Both lists are emptied at Level:Destroy.
---⛔ Cast to the screen's own type before reading its nodes, and each node before reading its name (the weapon view's mailbox
---scan paid for both: a field the cast type does not declare reads nil in silence).
local m_TableNodeIds = {}   -- instance guid -> true: the nodes already kept

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

			if l_NodeName == VEHICLE_TABLE_NODE and l_Node ~= nil then
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

	if s_New > 0 then
		LOG(string.format("vehicle camo: window writer node found (%s): %d, %d new, %d kept in all", tostring(p_Where), s_Found,
			s_New, #m_TableNodes))
		WriteVehicleTable()
	end
end

Events:Subscribe("Partition:Loaded", function(p_Partition)
	local s_Name = ""
	pcall(function() s_Name = string.lower(tostring(p_Partition.name)) end)

	if string.find(s_Name, "customizelandcamoscreen", 1, true) == nil and string.find(s_Name, "customizeaircamoscreen", 1, true) == nil then
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

-- the player's choices: asked of the server once (they stay here across levels) and handed back as "class=id;…"
local function AskWorn(p_Why)
	if m_WornAsked then
		return
	end

	local s_Ok = pcall(function() NetEvents:Send("VehicleCamo:Ask") end)

	if s_Ok then
		m_WornAsked = true
		LOG("vehicle camo: the server asked for this player's choices (" .. tostring(p_Why) .. ")")
	end
end

NetEvents:Subscribe("VehicleCamo:Worn", function(p_Text)
	m_Worn = {}
	local s_Count = 0

	for l_Pair in string.gmatch(tostring(p_Text or ""), "[^;]+") do
		local l_Class, l_Id = string.match(l_Pair, "^(.-)=(%d+)")

		-- only a class (an old row keyed by blueprint, from the first build, names no class and is skipped)
		if l_Class ~= nil and string.find(l_Class, "ID_", 1, true) == 1 and CamoById(tonumber(l_Id)) ~= nil then
			m_Worn[l_Class] = tonumber(l_Id)
			s_Count = s_Count + 1
		end
	end

	LOG("vehicle camo: " .. s_Count .. " choice(s) from the server: " .. tostring(p_Text))
	ApplyAllCamos()
	WriteVehicleTable()
end)

Events:Subscribe("Extension:Loaded", function()
	AskWorn("the extension loaded")
end)

Events:Subscribe("Level:Loaded", function()
	AskWorn("a level loaded")
end)

Events:Subscribe("Level:RegisterEntityResources", function()
	ApplyAllCamos()
	WriteVehicleTable()
end)

Events:Subscribe("Level:Destroy", function()
	UncamoAll("level destroyed")
	-- the windows' writer nodes belong to the level that ends (see ScanWindow): found again as the next level's windows load
	m_TableNodes = {}
	m_TableNodeIds = {}
end)

---A pick in a vehicle window: that CLASS wears the camo from now on (nil / an unknown id = its own paint). The window's graph
---applies the customization right after and the ShowRoom's vehicle comes back, so its data has to be pointed now.
local function ChooseCamo(p_Id, p_Class)
	if p_Class == nil or p_Class == "" then
		LOG("vehicle camo: a pick with no class (" .. tostring(p_Id) .. ") -- the window did not know its vehicle class; nothing chosen")
		return
	end

	local s_Camo = CamoById(p_Id)
	m_Worn[p_Class] = s_Camo ~= nil and s_Camo.id or nil

	for _, l_Blueprint in ipairs(CamoBlueprints()) do
		if ClassOf(l_Blueprint) == p_Class then
			ApplyCamo(l_Blueprint)
		end
	end

	WriteVehicleTable()
	pcall(function() NetEvents:Send("VehicleCamo:Choose", p_Class, s_Camo ~= nil and s_Camo.id or 0) end)
	LOG("vehicle camo chosen for " .. p_Class .. ": " .. (s_Camo ~= nil and s_Camo.name or "its own paint"))
end

-- ---- the PREVIEW while the vehicles wear their driver's camo (2026-09-24, keku: *"en la ventana de camo, al seleccionar berkut
-- sigue apareciendo el original"*) ------------------------------------------------------------------------------------------------
-- With the camo drawn by the driver's pieces (ext/Shared/VehicleCamos.lua) the class-wide swap above stands down: it would paint
-- every vehicle of the class on this screen with this player's camo. The ShowRoom's vehicle alone gets it, the way the swap
-- always worked (the data's mesh is read when the vehicle is MADE, and a vehicle keeps the mesh it was made with): while one of the
-- vehicle screens is up (the TIERRA / AIRE rows and the camo windows), the vehicle being made has its data pointed at the camo's
-- body clone for that one creation, and put back the moment it is made. Where it is made cannot tell it apart (the ShowRoom's
-- vehicle slides in from hundreds of metres out), so every vehicle made through here is said in the log with where it stood.
local VEHICLE_SCREENS = { "customizelandscreen", "customizeairscreen", "customizelandcamoscreen", "customizeaircamoscreen" }
local m_VehicleScreen = false   -- one of them is the screen up (set by the push hook below)

local function PreviewOn()
	return m_VehicleScreen and m_CamoOn and rawget(_G, "VEHICLE_CAMO_PIECES") == true
end

-- ⭐ BY PIECES (keku 2026-09-24: *"aún nos queda que en la ventana preview de camos de vehículo se vea el camo"*). A vehicle whose
-- body ships no preview clone (its LOD data lives only inside its expansion levels -- the XP vehicles, run 149) is dressed in the
-- window with the SAME pieces a match draws on the driver's vehicle. ext/Shared/VehicleCamos.lua gives every camo, besides the
-- driver's decision, a PREVIEW decision on the client: asked when the vehicle appears (its OnSpawned, what its headlights go on) and,
-- true, it hides the stock parts the pieces replace and turns the camo's switches on. It is FALSE in the data; the ShowRoom's
-- vehicle is made with it TRUE -- the data flipped for that one creation, the way the body clone's mesh is -- and put back a moment
-- later. Nothing is wired here: a vehicle's bus keeps a table of POINTERS into its blueprint's connections, built from the data, so
-- a connection added once a vehicle of the blueprint exists is never seen and moves the array under that table (the first try,
-- 2026-09-24 22:11: added at the ShowRoom's creation, the Stryker and the HIMARS came out stock).
local PREVIEW_MODES = { auto = true, pieces = true, clone = true }
local m_PreviewMode = "auto"          -- auto: the body clone where the camo ships one, the pieces otherwise; pieces: always; clone: never
local PREVIEW_HOLD = 1.5              -- seconds the preview decision stays TRUE in the data once the vehicle is made
local m_PreviewFlip = nil             -- { data = CompareBoolEntityData (writable), name, left = seconds } while one is flipped

local function GuidText(p_Instance)
	local s_Id = ""
	pcall(function() s_Id = string.upper(tostring(p_Instance.instanceGuid)) end)
	return s_Id
end

---Puts the flipped preview decision back to FALSE (a vehicle of the map made from then on does nothing with it).
local function UnflipPreview(p_Why)
	if m_PreviewFlip == nil then
		return
	end

	local s_Flip = m_PreviewFlip
	m_PreviewFlip = nil
	local s_Ok, s_Err = pcall(function() s_Flip.data.bool = false end)
	LOG(string.format("vehicle camo preview (pieces): %s's preview decision back to false (%s)%s", s_Flip.name, tostring(p_Why),
		s_Ok and "" or (" -- ⛔ " .. tostring(s_Err))))
end

Events:Subscribe("Engine:Update", function(p_Delta)
	if m_PreviewFlip == nil then
		return
	end

	m_PreviewFlip.left = m_PreviewFlip.left - (tonumber(p_Delta) or 0)

	if m_PreviewFlip.left <= 0 then
		UnflipPreview("its spawn is past")
	end
end)

---The ShowRoom's vehicle dressed with the camo's PIECES (see above): the camo's preview decision (VehicleCamos.lua's
---CA3C0DE5-<ordinal>-4008-B000-<the camo's identifier>, among the vehicle's logic) is TRUE for this creation. Says what it did.
local function DressByPieces(p_Hook, p_Name, p_Data, p_Camo, p_Where, p_How)
	if p_Camo.hides[string.lower(p_Name)] == nil then
		LOG(string.format("vehicle camo preview: %s wears %s, which ships no clone of its body and no pieces for it -- shown as the game has it",
			p_Name, p_Camo.name))
		return
	end

	UnflipPreview("another vehicle is being made")

	local s_Suffix = string.format("-%012X", p_Camo.id)
	local s_Decision = nil
	local s_Ok, s_Err = pcall(function()
		local l_Data = p_Data()

		for i = 1, #l_Data.components do
			local l_Item = l_Data.components[i]
			local l_Id = GuidText(l_Item)
			local l_Is = false
			pcall(function() l_Is = l_Item:Is("CompareBoolEntityData") end)

			if l_Is and string.find(l_Id, "CA3C0DE5-", 1, true) == 1 and string.sub(l_Id, 15, 19) == "4008-" and
				string.sub(l_Id, -13) == s_Suffix then
				s_Decision = CompareBoolEntityData(l_Item)
			end
		end

		if s_Decision ~= nil then
			s_Decision:MakeWritable()
			s_Decision.bool = true
		end
	end)

	if not s_Ok or s_Decision == nil then
		LOG(string.format("vehicle camo preview (pieces): %s wears %s -- %s", p_Name, p_Camo.name, not s_Ok and ("⛔ " .. tostring(s_Err)) or
			"no preview decision of it on this vehicle's data (did ext/Shared/VehicleCamos.lua dress it on this level?) -- shown as the game has it"))
		return
	end

	m_PreviewFlip = { data = s_Decision, name = p_Name, left = PREVIEW_HOLD }
	local s_Made = p_Hook:Call()
	LOG(string.format("vehicle camo preview (pieces): %s made %s wearing %s (%s): its preview decision true for this creation -- the " ..
		"stock parts hidden and the pieces on when it appears", p_Name, p_How, p_Camo.name, p_Where))
	p_Hook:Return(s_Made)
end

---A vehicle being made through a hook: when this player wears a camo on its class, its data points at the camo's body clone for
---this one creation (the hook's Call makes it) and is put back right after. p_Data() hands its VehicleEntityData; p_How says how
---the game makes it (both ways are hooked: which one the ShowRoom uses is what the log says).
local function MakeWearing(p_Hook, p_Name, p_Data, p_Transform, p_How)
	local s_Class = ClassOf(p_Name)
	local s_Camo = s_Class ~= nil and CamoById(m_Worn[s_Class]) or nil
	local s_Clone = CloneOf(s_Camo, p_Name)

	if s_Camo == nil then
		return
	end

	-- no body clone (or the pieces asked for): dressed by its pieces
	if s_Clone == nil or m_PreviewMode == "pieces" then
		if m_PreviewMode ~= "clone" then
			local s_At = "?"
			pcall(function()
				s_At = string.format("%.0f m from the camera", Length(Diff(p_Transform.trans, ClientUtils:GetCameraTransform().trans)))
			end)
			DressByPieces(p_Hook, p_Name, p_Data, s_Camo, s_At, p_How)
		end

		return
	end

	local s_Data, s_Home, s_Mesh = nil, nil, nil
	local s_Ok, s_Err = pcall(function()
		s_Data = p_Data()
		s_Data:MakeWritable()
		s_Home = s_Data.mesh
		s_Mesh = ResourceManager:SearchForDataContainer(s_Clone)

		if s_Mesh == nil then
			error("the clone " .. s_Clone .. " is not loaded (did its camo package mount?)")
		end
	end)

	if not s_Ok then
		LOG("vehicle camo preview " .. p_Name .. ": ⛔ " .. tostring(s_Err))
		return
	end

	local s_Where = "?"
	pcall(function()
		s_Where = string.format("%.0f m from the camera", Length(Diff(p_Transform.trans, ClientUtils:GetCameraTransform().trans)))
	end)

	s_Data.mesh = CompositeMeshAsset(s_Mesh)
	local s_Made = p_Hook:Call()
	s_Data.mesh = s_Home
	LOG(string.format("vehicle camo preview: %s made %s wearing %s (%s; its data back on its own mesh: %s)", p_Name, p_How,
		s_Camo.name, s_Where, tostring(s_Data.mesh ~= nil and s_Data.mesh.name)))
	p_Hook:Return(s_Made)
end

Hooks:Install("EntityFactory:CreateFromBlueprint", 999, function(p_Hook, p_Blueprint, p_Transform, p_Variation, p_Parent)
	if not PreviewOn() then
		return
	end

	local s_IsVehicle = false
	pcall(function() s_IsVehicle = p_Blueprint:Is("VehicleBlueprint") end)

	if not s_IsVehicle then
		return
	end

	local s_Name, s_Parent = "?", "none"
	pcall(function() s_Name = tostring(Blueprint(p_Blueprint).name) end)
	pcall(function() s_Parent = p_Parent ~= nil and tostring(p_Parent.typeInfo.name) or "none" end)
	MakeWearing(p_Hook, s_Name, function() return VehicleEntityData(VehicleBlueprint(p_Blueprint).object) end, p_Transform,
		"from its blueprint (parent " .. s_Parent .. ")")
end)

Hooks:Install("EntityFactory:Create", 999, function(p_Hook, p_Data, p_Transform)
	if not PreviewOn() then
		return
	end

	local s_IsVehicle = false
	pcall(function() s_IsVehicle = p_Data:Is("VehicleEntityData") end)

	if not s_IsVehicle then
		return
	end

	local s_Name = "?"
	pcall(function() s_Name = tostring(p_Data.partition.name) end)
	MakeWearing(p_Hook, s_Name, function() return VehicleEntityData(p_Data) end, p_Transform, "from its data")
end)

-- ---- the game's vehicle ------------------------------------------------------------------------------------------------------------
local function NameOf(p_Entity)
	local s_Name = "?"

	pcall(function()
		s_Name = tostring(p_Entity.data.partition.name)
	end)

	return s_Name
end

---The ClientVehicleEntity nearest the camera: (entity, distance, how many there are).
local function Nearest(p_From)
	local s_Best, s_BestDist, s_Count = nil, nil, 0

	pcall(function()
		local s_Iter = EntityManager:GetIterator("ClientVehicleEntity")
		local s_Entity = s_Iter:Next()

		while s_Entity ~= nil do
			s_Count = s_Count + 1
			local l_Dist = Length(Diff(SpatialEntity(s_Entity).transform.trans, p_From))

			if s_BestDist == nil or l_Dist < s_BestDist then
				s_Best, s_BestDist = s_Entity, l_Dist
			end

			s_Entity = s_Iter:Next()
		end
	end)

	return s_Best, s_BestDist, s_Count
end

-- ---- the zoom ---------------------------------------------------------------------------------------------------------------------
---Writes an offset on the zoom's vehicle data: in place, and the struct handed back too (whichever way the binding takes it).
local function WriteOffset(p_Offset)
	pcall(function() m_ZoomData.hudData.customizationOffset = p_Offset end)
	pcall(function()
		local l_Hud = m_ZoomData.hudData
		l_Hud.customizationOffset = p_Offset
		m_ZoomData.hudData = l_Hud
	end)
end

---The vehicle's customization offset put back as the game had it.
local function Unzoom()
	if m_ZoomData ~= nil and m_Offset ~= nil then
		WriteOffset(Vec3(m_Offset.x, m_Offset.y, m_Offset.z))
	end

	m_ZoomData = nil
end

---The zoom in force written on the held vehicle's offset: its z and y scaled (the slide-in's x left as it is). The game reads it
---at its next placement.
local function Zoom()
	if m_Vehicle == nil or m_Offset == nil then
		return
	end

	local s_Ok, s_Err = pcall(function()
		if m_ZoomData == nil then
			local l_Data = VehicleEntityData(m_Vehicle.data)
			l_Data:MakeWritable()
			m_ZoomData = l_Data
		end
	end)

	if not s_Ok then
		LOG("the vehicle's offset cannot be written (no zoom): " .. tostring(s_Err))
		m_ZoomData = nil
		return
	end

	local s_Want = m_Offset.z * m_Zoom
	WriteOffset(Vec3(m_Offset.x, m_Offset.y * m_Zoom, s_Want))
	local s_Read = nil
	pcall(function() s_Read = m_ZoomData.hudData.customizationOffset.z end)

	if s_Read == nil or math.abs(s_Read - s_Want) > 0.001 then
		LOG(string.format("the zoom did not take: z %.2f asked, %s read back", s_Want, tostring(s_Read)))
	end
end

local function Release()
	Unzoom()
	m_Vehicle, m_VehicleId, m_Offset, m_CheckAt = nil, nil, nil, nil
end

---Takes the game's vehicle: its customization offset read (the tilt's sign comes from it), the check scheduled; its end is
---watched (the game spawns it again as the window opens) so it is never read dead.
local function Take(p_Entity, p_Dist, p_Count)
	local s_Id = nil
	pcall(function() s_Id = p_Entity.instanceId end)

	local s_Ok, s_Err = pcall(function()
		local l_Offset = VehicleEntityData(p_Entity.data).hudData.customizationOffset
		m_Offset = Vec3(l_Offset.x, l_Offset.y, l_Offset.z)
	end)

	if not s_Ok then
		LOG("the game's vehicle has no customization offset to read: " .. tostring(s_Err))
		return
	end

	m_Vehicle, m_VehicleId = p_Entity, s_Id
	m_Name = NameOf(p_Entity)
	m_CheckAt = SharedUtils:GetTimeMS() + CHECK_AFTER_MS
	Tilt()

	-- a vehicle the game spawns anew (another row) comes in at the zoom in force
	if m_Zoom ~= 1.0 then
		Zoom()
	end

	pcall(function()
		p_Entity:RegisterDeinitCallback(function()
			if m_VehicleId == s_Id then
				LOG("the game's vehicle went away (" .. tostring(m_Name) .. ")")
				Release()
			end
		end)
	end)

	LOG(string.format("the game's vehicle: %s, %.1f m from the camera (%d vehicle entities in the level); its customization offset (%.2f, %.2f, %.2f)",
		tostring(m_Name), p_Dist, p_Count, m_Offset.x, m_Offset.y, m_Offset.z))
end

---Finds the game's vehicle when none is held (every frame then: the game's new spawn is taken the frame it appears).
local function Find()
	if m_Vehicle ~= nil then
		return
	end

	local s_Cam = ClientUtils:GetCameraTransform()

	if s_Cam == nil then
		return
	end

	local s_Entity, s_Dist, s_Count = Nearest(s_Cam.trans)

	if s_Entity == nil or s_Dist > FIND_RADIUS then
		if not m_FindSaid then
			m_FindSaid = true
			LOG(string.format("the game's vehicle is not in the scene (%d ClientVehicleEntity in the level, nearest %s)", s_Count,
				s_Dist == nil and "none" or string.format("%.1f m", s_Dist)))
		end

		return
	end

	m_FindSaid = false
	Take(s_Entity, s_Dist, s_Count)
end

-- ---- the check -------------------------------------------------------------------------------------------------------------------
---Where a world point is DRAWN across the screen, as the game projects it: 0 = the middle, 1 = the right edge and minus 1 the left
---one (nil when it is not on the screen).
local function ScreenX(p_World)
	local s_X = nil

	pcall(function()
		local l_Size = ClientUtils:GetWindowSize()
		local l_At = ClientUtils:WorldToScreen(p_World)

		if l_Size ~= nil and l_At ~= nil and l_Size.x > 0 then
			s_X = (l_At.x - l_Size.x * 0.5) / (l_Size.x * 0.5)
		end
	end)

	return s_X
end

---Once per vehicle, past the slide-in: where the game put it -- at the tilted camera's place (the game read the tilt) or at the
---untilted one's -- and where it is projected. What keku sees is the verdict; this says why.
local function Check()
	m_CheckAt = nil

	if m_Vehicle == nil or m_Offset == nil then
		return
	end

	pcall(function()
		local l_Cam = ClientUtils:GetCameraTransform()
		local l_Pos = SpatialEntity(m_Vehicle).transform.trans
		-- the camera as read here: its forward carries the tilt if this reads the camera's transform raw (left·forward = tilt)
		local l_Read = Dot(l_Cam.left, l_Cam.forward)
		local l_Forward = Diff(l_Cam.forward, Scaled(l_Cam.left, l_Read))
		local l_K = m_ZoomData ~= nil and m_Zoom or 1.0
		local l_Straight = Sum(Sum(l_Cam.trans, Scaled(l_Forward, m_Offset.z * l_K)), Scaled(l_Cam.up, m_Offset.y * l_K))
		local l_Tilted = Sum(l_Straight, Scaled(l_Cam.left, m_Offset.z * l_K * m_Tilt))
		local l_Seen = ScreenX(l_Pos)

		LOG(string.format("check (%s): tilt in force %.3f, the camera read here carries %.3f; the vehicle %.2f m from the tilted place, %.2f m from the middle's; projected at %s across the screen (0 = middle, 1 = right edge; the weapon's spot is about +0.45)",
			tostring(m_Name), m_Tilt, l_Read, Length(Diff(l_Pos, l_Tilted)), Length(Diff(l_Pos, l_Straight)),
			l_Seen ~= nil and string.format("%+.2f", l_Seen) or "?"))
	end)
end

-- ---- the frame -------------------------------------------------------------------------------------------------------------------
local function OnUpdateInput(p_Delta)
	if not m_Active then
		return
	end

	local s_Cursor = InputManager:GetCursorPosition()
	local s_Zoom = 0.0

	-- the hold that turns mouse movement into zoom (the weapon view's): forward (the cursor going up the screen) = closer
	if InputManager:IsKeyDown(ZOOM_HOLD_KEY) and m_LastCursor ~= nil then
		s_Zoom = (s_Cursor.y - m_LastCursor.y) * ZOOM_SENS
		m_Pending = m_Pending * INERTIA
		m_PendingPitch = m_PendingPitch * INERTIA
	elseif m_AsButton and m_LastCursor ~= nil then
		m_Pending = (s_Cursor.x - m_LastCursor.x) * ROT_SENS
		m_PendingPitch = (s_Cursor.y - m_LastCursor.y) * PITCH_SENS
	else
		m_Pending = m_Pending * INERTIA
		m_PendingPitch = m_PendingPitch * INERTIA
	end

	if InputManager:WentKeyDown(InputDeviceKeys.IDK_PageUp) then s_Zoom = s_Zoom - ZOOM_STEP end
	if InputManager:WentKeyDown(InputDeviceKeys.IDK_PageDown) then s_Zoom = s_Zoom + ZOOM_STEP end

	if s_Zoom ~= 0.0 then
		local l_Zoom = math.max(ZOOM_NEAR, math.min(ZOOM_FAR, m_Zoom + s_Zoom))

		if l_Zoom ~= m_Zoom then
			m_Zoom = l_Zoom
			Zoom()
		end
	end

	-- a turn that has run down stops (inertia alone never reaches zero)
	if math.abs(m_Pending) < 0.00001 then m_Pending = 0.0 end
	if math.abs(m_PendingPitch) < 0.00001 then m_PendingPitch = 0.0 end

	m_LastCursor = s_Cursor

	local s_Turned = m_Pending ~= 0.0 or m_PendingPitch ~= 0.0
	m_Yaw = m_Yaw + m_Pending

	while m_Yaw > math.pi do m_Yaw = m_Yaw - 2.0 * math.pi end
	while m_Yaw < -math.pi do m_Yaw = m_Yaw + 2.0 * math.pi end

	m_Pitch = math.max(PITCH_MIN, math.min(PITCH_MAX, m_Pitch + m_PendingPitch))

	if s_Turned then
		ApplyGameTurn()
	end

	Find()

	if m_CheckAt ~= nil and SharedUtils:GetTimeMS() >= m_CheckAt then
		Check()
	end

	if InputManager:WentKeyDown(InputDeviceKeys.IDK_F8) then
		LOG(State())
	end
end

State = function()
	return string.format("active=%s kind=%s vehicle=%s offset=%s tilt=%s (wanted %.3f) motion blur=%s shadow maps=%s zoom=%.2f%s yaw=%.0f pitch=%.0f drag=%s",
		tostring(m_Active), m_Kind, tostring(m_Name),
		m_Offset ~= nil and string.format("(%.2f, %.2f, %.2f)", m_Offset.x, m_Offset.y, m_Offset.z) or "?",
		m_CamData ~= nil and string.format("%.3f", m_Tilt) or "none", m_On and m_Tan or 0.0,
		m_Blur ~= nil and "OFF (ours)" or (m_Quiet and "as the game has it (goes off with the tilt)" or "left alone"),
		m_Shadows ~= nil and "OFF (ours)" or (m_NoShadows and "as the game has them (go off with the tilt)" or "left alone"),
		m_Zoom, m_ZoomData ~= nil and " (written)" or "", math.deg(m_Yaw), math.deg(m_Pitch), tostring(m_AsButton))
end

local function Enter(p_Kind)
	if not ENABLED then
		return
	end

	if m_Active then
		m_Kind = p_Kind
		return
	end

	m_Active = true
	m_Kind = p_Kind
	m_Yaw, m_Pitch = 0.0, 0.0
	m_Zoom = 1.0
	m_Pending, m_PendingPitch = 0.0, 0.0
	m_LastCursor = nil
	m_AsButton = false
	m_FindSaid = false
	m_Name = nil
	Release()

	if Comp() ~= nil then
		ApplyGameTurn()
	end

	-- the tilt goes on at once: it changes nothing drawn but where the game places its vehicle
	Tilt()

	if #m_Subs == 0 then
		m_Subs[#m_Subs + 1] = Events:Subscribe("Client:UpdateInput", OnUpdateInput)
	end

	LOG("vehicle view ON (" .. m_Kind .. "): " .. State())
end

local function Leave(p_Why)
	if not m_Active then
		return
	end

	m_Active = false

	for _, l_Sub in ipairs(m_Subs) do
		pcall(function() l_Sub:Unsubscribe() end)
	end

	m_Subs = {}
	UnflipPreview("leaving the window")
	Untilt("leaving the window")
	RestoreGameTurn()
	LOG("vehicle view OFF (" .. tostring(p_Why) .. "): " .. State())
	Release()
end

-- ---- the gate: the screen the game pushes -----------------------------------------------------------------------------------
Hooks:Install("UI:PushScreen", 998, function(p_Hook, p_Screen, p_Priority, p_ParentGraph)
	local s_Name = ""

	pcall(function()
		s_Name = string.lower(tostring(UIGraphAsset(p_Screen).name))
	end)

	-- the preview's gate: is a vehicle screen the one up? (a screen pushed over it without leaving it changes nothing)
	local s_Over = false

	for _, l_Ignored in ipairs(IGNORED_SCREENS) do
		if string.find(s_Name, l_Ignored, 1, true) ~= nil then
			s_Over = true
		end
	end

	if not s_Over then
		local l_Was = m_VehicleScreen
		m_VehicleScreen = false

		for _, l_Screen in ipairs(VEHICLE_SCREENS) do
			if string.find(s_Name, l_Screen, 1, true) ~= nil then
				m_VehicleScreen = true
			end
		end

		if l_Was ~= m_VehicleScreen then
			LOG("vehicle camo preview " .. (m_VehicleScreen and "ON" or "OFF") .. " (screen " .. s_Name .. ")")
		end
	end

	for l_Screen, l_Kind in pairs(SCREENS) do
		if string.find(s_Name, l_Screen, 1, true) ~= nil then
			-- the window's writer node, if its partition's load was missed (a second chance, as the weapon view has)
			ScanWindow(p_Screen, "on push")
			Enter(l_Kind)
			return
		end
	end

	for _, l_Ignored in ipairs(IGNORED_SCREENS) do
		if string.find(s_Name, l_Ignored, 1, true) ~= nil then
			return
		end
	end

	Leave("screen " .. s_Name)
end)

-- the window's ActionScript: the drag (WV1 / WV0), its list (VGR), a pick (VVC) and its respawn (VRS), the game's vehicle taken
-- away (VHD), the rows (VRW) and the rows of the vehicles without a customization (VOR)
Events:Subscribe("AS2:Frame", function(p_Text)
	if type(p_Text) ~= "string" then
		return
	end

	local s_Tag = string.sub(p_Text, 1, 3)

	if not m_Active and s_Tag ~= "VRW" and s_Tag ~= "VOR" then
		return
	end

	if s_Tag == "WV1" then
		-- the left button only ("" = no code reported, "1" = left: GFx codes); the right one's release never reaches the movie
		local s_Code = string.sub(p_Text, 4)
		m_AsButton = s_Code == "" or s_Code == "1"
	elseif p_Text == "WV0" then
		m_AsButton = false
	elseif s_Tag == "VGR" then
		LOG("the window's list: " .. string.sub(p_Text, 4) .. " (cells, delivered by the game)")
	elseif s_Tag == "VVC" then
		-- "VVC<id>,<class SID>": a pick, for the window's vehicle class
		local l_Id, l_Class = string.match(string.sub(p_Text, 4), "^(%d+),?(.*)$")
		ChooseCamo(tonumber(l_Id), l_Class)
	elseif s_Tag == "VTB" then
		-- the window's receipt: "<camos>,<worn>,<class>" as it made its list
		LOG("vehicle camo list made by the window: " .. string.sub(p_Text, 4))
	elseif s_Tag == "VCL" then
		-- "VCL<how>,<class>": the window learned its vehicle class (from its name binding, or the data component)
		LOG("vehicle camo window class: " .. string.sub(p_Text, 4))
	elseif s_Tag == "VHD" then
		LOG("⛔ the window asked the game to take its vehicle away: the menu was built with _camoHideMs on -- this view needs it -1")
	elseif s_Tag == "VRS" then
		-- a pick's second half: the window selected its row again and asked the game to spawn its vehicle (the pick's chain took
		-- it away). "VRS<row when the window opened>,<row now>,<ask n>": a different "now" = the unspawn cleared the selection; the
		-- ask is repeated (a spawn while the old vehicle is still there does nothing), "held now" says whether it was still there
		local l_Opened, l_Now, l_Ask = string.match(string.sub(p_Text, 4), "^([^,]*),?([^,]*),?(.*)$")
		LOG(string.format("the window asked the game to spawn its vehicle again (a pick, ask %s): row %s selected again (it read %s now); held now: %s",
			tostring(l_Ask), tostring(l_Opened), tostring(l_Now), m_Vehicle ~= nil and tostring(m_Name) or "none"))
	elseif s_Tag == "VRW" then
		LOG("TIERRA / AIRE rows set up, their second button reads: " .. string.sub(p_Text, 4))
	elseif s_Tag == "VOR" then
		-- "VOR<land|air>,<ours>/<rows>,<sid>=<icon frame>;…": the rows of the vehicles without a customization of their own (their asset
		-- is ours: ext/Shared/VehicleRows.lua), dressed by the rows' script -- only CAMUFLAJE, the minimap icon ("" = no frame)
		LOG("TIERRA / AIRE rows of vehicles without a customization, dressed: " .. string.sub(p_Text, 4))
	end
end)

Events:Subscribe("Extension:Unloading", function()
	Leave("extension unloading")
end)

Events:Subscribe("Level:Destroy", function()
	Leave("level destroyed")
	UnflipPreview("level destroyed")
end)

-- in the game: camovehicle on | off | <tangent> (how far off the middle; the weapon's spot is 0.162) | blur on | blur off (the
-- motion blur left alone / switched off while tilted); no argument = the state. The choice outlives the window; with the window
-- up it applies at once.
Console:Register("camovehicle", "vehicle camo view: 'on' / 'off' (the ShowRoom camera tilted or as the game has it), a tangent (0.162 = the weapon's spot), 'blur on' / 'blur off' (the motion blur while tilted), 'shadows on' / 'shadows off' (the shadow maps while tilted), 'preview auto|pieces|clone' (how the camo window dresses its vehicle), 'render [<switch> on|off]' (a render switch, live)",
	function(p_Args)
		local s_Verb = p_Args ~= nil and p_Args[1] ~= nil and string.lower(tostring(p_Args[1])) or ""

		if s_Verb == "" then
			return State()
		end

		local s_Tan = tonumber(s_Verb)

		if s_Verb == "on" or s_Verb == "off" then
			m_On = s_Verb == "on"
		elseif s_Verb == "camo" then
			local l_Word = p_Args[2] ~= nil and string.lower(tostring(p_Args[2])) or ""

			if l_Word ~= "on" and l_Word ~= "off" then
				return "use: camo on / camo off (every vehicle camo of this client; vehicles made from then on -- reopen the window)"
			end

			m_CamoOn = l_Word == "on"

			if m_CamoOn then
				ApplyAllCamos()
			else
				UncamoAll("console")
			end

			local s_Count = 0

			for _ in pairs(m_CamoSwapped) do
				s_Count = s_Count + 1
			end

			return "vehicle camo " .. l_Word .. " (" .. s_Count .. " vehicle(s) on a clone)"
		elseif s_Verb == "preview" then
			-- how the camo window dresses its vehicle: auto (the body clone where there is one, the pieces otherwise), pieces
			-- (always the match's pieces), clone (never the pieces) -- for the next vehicle the window makes (pick a camo again)
			local l_Word = p_Args[2] ~= nil and string.lower(tostring(p_Args[2])) or ""

			if not PREVIEW_MODES[l_Word] then
				return "use: preview auto / preview pieces / preview clone (now " .. m_PreviewMode .. ")"
			end

			m_PreviewMode = l_Word
			return "vehicle camo preview: " .. m_PreviewMode .. " (for the next vehicle the window makes: pick the camo again)"
		elseif s_Verb == "blur" then
			local l_Word = p_Args[2] ~= nil and string.lower(tostring(p_Args[2])) or ""

			if l_Word ~= "on" and l_Word ~= "off" then
				return "use: blur on (the motion blur left alone) / blur off (switched off while tilted)"
			end

			m_Quiet = l_Word == "off"
		elseif s_Verb == "shadows" then
			local l_Word = p_Args[2] ~= nil and string.lower(tostring(p_Args[2])) or ""

			if l_Word ~= "on" and l_Word ~= "off" then
				return "use: shadows on (the shadow maps left alone) / shadows off (switched off while tilted)"
			end

			m_NoShadows = l_Word == "off"
		elseif s_Verb == "render" then
			-- a render switch, live (see RENDER_SWITCHES): the list, or one on / off
			return RenderCommand(p_Args[2] ~= nil and tostring(p_Args[2]) or nil, p_Args[3] ~= nil and string.lower(tostring(p_Args[3])) or nil)
		elseif s_Tan ~= nil and s_Tan >= 0.0 and s_Tan <= 0.6 then
			m_On, m_Tan = true, s_Tan
		else
			return "unknown: " .. s_Verb .. " -- use on, off, a tangent from 0 to 0.6, blur on / blur off, shadows on / shadows off, render"
		end

		if m_Active then
			Tilt()
		end

		return State()
	end)
