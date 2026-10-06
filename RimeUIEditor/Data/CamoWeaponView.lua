-- CamoWeaponView -- the weapon-only view of the camo screen: while the camo screen is up, the weapon the player is customizing is
-- shown ALONE in front of the camera, with everything the player has on it (its accessories and its camo), and a left-button drag
-- over it turns it; the customization mannequin is parked out of frame meanwhile and comes back when the screen goes.
--
-- The weapon is composed from the game's own data, the way the engine dresses the mannequin's weapon (measured in the EBX):
--   . SoldierWeaponData.weaponStates[1].mesh3p (a skinned mesh) placed with mesh3pTransforms (its base pose), with the camo as the
--     entity's variation (EntityCreationParams.variationNameHash = the camo unlock's BlueprintAndVariationPair.variation.nameHash)
--     -- the VU-BattleRoyale mod draws its loot weapons exactly so;
--   . every accessory is a SocketData of the weapon: its unlock, its objects (WeaponRegularSocketObjectData.asset3p = the accessory's
--     mesh), the BONE it hangs from (boneName: Wep_Root for most, Wep_Extra1 = the rail on some, e.g. the M240's optics) and its place
--     relative to that bone (weaponStates[1].mesh3pRigidMeshSocketObjectTransforms, one per socket object). Every weapon is skinned to
--     ONE skeleton (Animations/Skeletons/Weapon/WeaponSke01, 25 bones) whose BIND pose is SkeletonAsset.modelPose. Where each bone
--     really is on the weapon comes from weaponStates[1].mesh3pTransforms, a per-bone DELTA on the bind pose (bind x delta: right for
--     rail optics, magazines...) -- except the BIPOD bones a skinned foregrip / bipod hangs from, which the game POSES by what is
--     mounted: the EBX deltas are the weapon with nothing on it (the L85A2's leave Wep_Bipod1 at the muzzle, where its bipod goes),
--     and mounting a foregrip or a bipod plays a pose on the weapon-parts rig that moves those bones (the L85A2's foregrip pose puts
--     Wep_Bipod1 under the handguard at z 0.553; the M240's bipod pose turns it 90 degrees about X with the legs 30 degrees apart, as
--     the customization screen shows it). Which pose: the equipped unlocks' WeaponAnimTypeModifier (weaponModifierData), bipod over
--     foregrip over nothing. The poses live in the game's animation banks, which Lua cannot read, so they are baked per weapon into
--     CamoWeaponPoses.lua (Rime: dump_weapon_part_poses) as the base-pose entries the bipod bones take; a weapon or kind that is not
--     baked keeps the EBX deltas. (The mannequin's own Wep_* bones from the ragdoll component are NOT the drawn pose -- measured: the
--     M240's rail bone 22 cm off, the L85A2's rounds 27 cm from its magazine -- that route stays behind LIVE_POSE, off.)
--     A socket is shown when its unlock is what the player has equipped (the force/default flags are not "always on": measured). The
--     mannequin's own weapon bus holds no socket entities (measured: one entity, the weapon), so nothing can be copied from it.
-- What the player has equipped comes from the accessories screen: the camo row reports the equipped identifiers of every row when it
-- opens this screen ("WVL<weapon,acc1,acc2,acc3,camo>" through the AS2 channel), and the camo screen reports each pick ("WVC<id>").
-- The mannequin is parked by the same live component fields the ShowRoom mod moved it with (UICustomizationComp.soldierOffset).
-- The camera the weapon is placed against is the one the engine places the MANNEQUIN against, measured from the mannequin itself
-- on entering: its world position with soldierOffset (0,0,0), (1,0,0), (0,1,0), (0,0,-1) gives that camera's position and axes
-- (ClientUtils:GetCameraTransform answers the game's camera, which need not be the customization scene's: v16 placed the weapon
-- against it and nothing showed); both frames go to the log.
-- The mouse: InputManager:GetCursorPosition() works in the menus; the button does not reach InputManager there, so the screen's own
-- ActionScript reports the press and the release over the 3D half ("WV1"/"WV0"; a probe on IsMouseButtonDown rides along).
--
-- The weapon turns about its own centre -- the centre of its base mesh's bounding box, read from the spawned entity (aabb); its mesh
-- origin is at the butt -- yaw with the drag's x and pitch with its y (the newer game's limits), and zooms with Page Up / Page Down.
-- The mouse wheel does not reach the movie in the menus (measured) nor the raw mouse axes; the menu input concepts (MenuZoomIn/Out,
-- MapZoom, Zoom...) and the engine's UI input events are probed (first move of each to the log) and drive the zoom once one is known.
-- The drag is the LEFT button's; the right button's press reaches the movie but its release never does, and the game's ConceptZoom
-- (the right button's concept) is not computed in the menus either (both measured) -- so the zoom hold is a KEY: Shift while moving
-- the mouse forward / back (Page Up / Page Down step it too).
--
-- Keys while the camo screen is up: Page Up / Page Down zoom, Shift + drag zooms, F8 prints the state. Console: camoview.

-- ---- knobs -------------------------------------------------------------------------------------------------------------------
local ENABLED = true                     -- false = nothing of this (the mannequin stays where the game keeps it)
local PROP = true                        -- true = the weapon alone in front of the camera; false = the mannequin brought up instead (v15)
local PROP_AT = Vec3(0.30, -0.02, -1.85) -- where the weapon's CENTRE goes, relative to the camera, in the game's own soldierOffset convention:
                                         -- x along the camera's first axis (0.95 puts the mannequin on the right), y up, z along the camera's
                                         -- forward axis, which POINTS BACKWARD in this engine (the game's -5.0 is five metres in front);
                                         -- z is the zoom's starting distance
local ZOOM_MIN, ZOOM_MAX = 1.0, 3.5      -- the distance the zoom moves between (metres)
local ZOOM_STEP = 0.15                   -- metres per Page Up / Page Down press, and per wheel notch once the wheel's axis is known
local ZOOM_SENS = 0.006                  -- metres per cursor pixel while zooming by holding (mouse forward = closer)
local ZOOM_HOLD_KEY = InputDeviceKeys.IDK_LeftShift             -- held = mouse forward / back zooms (the right button cannot be read in the menus: measured)
local WHEEL_AXIS = nil                   -- InputDeviceAxes.IDA_… that the wheel moves in the menus (nil = unknown: the probe logs each axis' first move)
local WHEEL_CONCEPT = nil                -- InputConceptIdentifiers.Concept… whose level the wheel moves in the menus (nil = unknown: probed)
local PROP_YAW_START_DEG = 90            -- the weapon's turn on entering: 0 = pointing away from the camera, 90 = seen from its side
local PROP_PITCH_START_DEG = 0           -- the weapon's tilt on entering (positive = muzzle up)
local PITCH_LIMIT_DEG = 45               -- the drag's tilt stays within this (the newer game's WeaponMaxAngleZ)
local PITCH_SIGN = 1                     -- -1 flips the tilt's direction (drag up = the side facing you goes up, or down)
local PIVOT = nil                        -- the point of the weapon that sits at PROP_AT and the turns go about, in the weapon's own space;
                                         -- nil = measured per weapon: the base mesh's bounding-box centre for x and z, PIVOT_Y for the height
local PIVOT_Y = 0.0                      -- the pivot's height in the weapon's space: the bore line (the box's centre hangs with the magazine)
local PARK_OFFSET = Vec3(0.0, -4.0, 4.0) -- where the mannequin waits while the weapon shows: below and behind the camera
local MOVE_TOGGLE = false                -- Disable+Enable each entity after moving it (MapEditor moves a dragged object by its transform alone; the toggle is its commit)
local ACCESSORY_CAMO = false             -- true = the accessories get the camo variation too (a mesh without that variation draws NOTHING)
local ACCESSORY_CAMO_MESHES = nil        -- ACCESSORY_CAMO false: only accessories whose mesh name contains one of these get the
                                         -- camo variation (nil = none). A probe: with database entries shipped for these meshes under the
                                         -- weapon's camo they draw camouflaged; without entries they draw NOTHING -- "ingested or not".
                                         -- ⛔ 2026-09-18: left at nil on purpose. An accessory that wears a camo now does it the way the
                                         -- ENGINE builds a socket object -- its own mesh asset read under variation 0 -- so this view has to
                                         -- spawn it the same way: with the weapon's camo hash it would look for an entry nothing ships and
                                         -- draw NOTHING, which reads exactly like a camo that failed. { "acog", "foregrip" } was the probe
                                         -- of the phase-E boots and its question is answered.
local MANNEQUIN_OFFSET = Vec3(0.30, -1.25, -1.6)   -- PROP false: where the mannequin's feet go (v15's weapon close-up)
local MANNEQUIN_YAW_START_DEG = -60      -- PROP false: the mannequin's turn on entering, relative to the game's own pose
local ROT_SENS = 0.002                   -- radians per cursor pixel while dragging (the newer game's MouseRotationSpeed)
local INERTIA = 0.9                      -- the turn keeps going after the release, this much per frame (0 = stops dead)
local DRAG_LEFT = 0.38                   -- the InputManager probe only counts presses right of this fraction of the window
local DRAG_BUTTON = InputDeviceMouseButtons.IDB_Button_0   -- the probe's button (the left one), should InputManager ever see the mouse in the menus
local DRAG_CODES = { [""] = true, ["1"] = true }   -- the button codes that drag: the left one (GFx: 1 left, 2 right, 3 middle; "" = no code reported)
local RETRY_S = 0.5                      -- the weapon is built again this often while it could not be (the mannequin still dressing)
local CALIBRATE = false                  -- true = the camera measured from the mannequin (4 steps, the mannequin seen jumping for half a second);
                                         -- false = ClientUtils:GetCameraTransform(), measured identical to it in the customization scene (v17)
local CALIB_STEP_MS = 120                -- a step waits this long for the engine to move the mannequin before reading it
local CALIB_TIMEOUT_MS = 3000            -- after this without a usable mannequin the API's camera is used instead

local SKELETON = "Animations/Skeletons/Weapon/WeaponSke01"   -- the one skeleton every weapon's 3p mesh and sockets use
local BONES = { exportAnimation = 1, Wep_Root = 2, Wep_Extra1 = 3, Wep_Trigger = 4, Wep_Slide = 5, Wep_Grenade1 = 6, Wep_Grenade2 = 7, Wep_Mag = 8,
	Wep_Mag_Ammo = 9, Wep_Physic1 = 10, Wep_Physic2 = 11, Wep_Physic3 = 12, Wep_Belt1 = 13, Wep_Belt2 = 14, Wep_Belt3 = 15, Wep_Belt4 = 16, Wep_Belt5 = 17,
	Wep_Bipod1 = 18, Wep_Bipod2 = 19, Wep_Bipod3 = 20, IK_Joint_LeftHand = 21, IK_Joint_RightHand = 22, Wep_Extra2 = 23, Wep_Extra3 = 24, Wep_Aim = 25 }
                                         -- bone name -> 1-based index into weaponStates[1].mesh3pTransforms (WeaponSke01's order, from the EBX;
                                         -- replaced by the resident skeleton's own list when it can be read)
local BIND = { [2] = Vec3(0, 0, 0), [3] = Vec3(0, 0.146, 0.191), [4] = Vec3(0, -0.059, 0.172), [5] = Vec3(0, 0.077, 0.403), [6] = Vec3(0, -0.023, 0.577),
	[7] = Vec3(0, -0.045, 0.577), [8] = Vec3(0, -0.093, 0.107), [9] = Vec3(0, -0.133, 0.107), [10] = Vec3(0, 0.114, 0.02), [11] = Vec3(0, 0.023, 0.342),
	[12] = Vec3(0, -0.037, 0.438), [13] = Vec3(0, -0.049, 0.296), [14] = Vec3(0, -0.059, 0.296), [15] = Vec3(0, -0.069, 0.296), [16] = Vec3(0, -0.079, 0.296),
	[17] = Vec3(0, -0.089, 0.296), [18] = Vec3(0, 0, 0.803), [19] = Vec3(-0.02, -0.04, 0.803), [20] = Vec3(0.02, -0.04, 0.803), [23] = Vec3(0, 0.146, 0.229),
	[24] = Vec3(0, 0.146, 0.268), [25] = Vec3(0, 0, 0.1) }
                                         -- the skeleton's bind pose (modelPose) per bone index, translations (its rotations are identity), from the
                                         -- EBX; replaced by the resident skeleton's own modelPose when it can be read
local m_BindPose = nil                   -- LinearTransform per bone index, as read from the game (nil = the table above)
local PART_POSES = true                  -- true = the bipod bones take the game's own pose for what is mounted (CamoWeaponPoses.lua); false = EBX deltas
local PART_POSES_ON_BASE = true          -- true = the base mesh takes those bones too (a weapon whose bipod is part of its own mesh); false = accessories only
local BIPOD_AS_SHOWN = true              -- true = a mounted bipod as the customization screen poses it (the M240's legs down, 30 degrees apart); false = as
                                         -- the soldier stands with it (folded: the stand pose with nothing mounted)
local SOLDIER_WEAPON_BONE0 = 154         -- the 3p soldier skeleton's bone index of the weapon skeleton's bone 1 (Wep_Root = 155), minus one
local LIVE_POSE = false                  -- true = the mannequin's Wep_Bipod1..3 (anchored on Wep_Mag) pose the skinned socket objects (v25-v31: refuted, the
                                         -- ragdoll's Wep_* bones are not the drawn pose); false = the baked poses / EBX
local LIVE_ANCHOR_MAX = 1.5              -- the anchor (the hand's place on the mesh) must be shorter than this, or the frame is not sane
local LIVE_BONES = { 18, 19, 20 }        -- the bones the mannequin drives (Wep_Bipod1, Wep_Bipod2, Wep_Bipod3: foregrips and bipods hang there)
local LIVE_BIPOD_LIFT = 0.04             -- metres added upward to those bones (the anchor by Wep_Mag leaves the L85A2's foregrip 4 cm low: v28)
local LIVE_PROBE = false                 -- log the mannequin's weapon bones (translation + forward axis) next to bind + EBX delta
local SOLDIER_SKELETON = "Animations/Skeletons/VeniceAntSke01"   -- the 3p soldier skeleton: its Wep_* bind poses, for the probe
local SCREEN = "customizecamoscreen"     -- the screen the view belongs to (its partition name, any case)

-- The accessory screens: each one is about ONE accessory, the one its row has on, so the view shows THAT piece alone --
-- no mannequin and no weapon around it (keku 2026-09-18: "cuando se abra esta ventana no haya maniquí sino solo el
-- accesorio el cual el camo se pondrá sobre este"). The number is the accessory slot the screen belongs to, which is how
-- the loadout that comes over the channel names it (accessories[1..3] = optics, underbarrel, accessory).
local ACCESSORY_SCREENS = {
	["customizeopticscreen"] = 1,
	["customizeunderbarrelscreen"] = 2,
	["customizeaccessoryscreen"] = 3,
}

-- The PISTOL's window (keku 2026-10-06: the pistols get a camo window "como con todos los camos", opened from the LOADOUT screen's
-- SIDEARM row): the view shows that pistol ALONE, nothing hung on it and no mannequin, framed by its size as a piece is. Which
-- pistol is the window's word ("WVAS,<unlock>": the item worn, or the one picked there, "WVC<unlock>"), looked up in the kits'
-- weapon tables -- the mannequin holds the PRIMARY on that screen, so it cannot say. A camo of a pistol is an unlock of its own in
-- the same table, whose weapon is the pistol made with the camo's mesh, so the same lookup draws it.
-- (and the crossbow's, opened from either GADGET row -- keku 2026-10-06: the crossbow is a gadget; its windows work as the
-- pistol's, "WVAG1,<unlock>" / "WVAG2,<unlock>")
local SIDEARM_SCREENS = { "customizesidearmscreen", "customizegadget1screen", "customizegadget2screen" }
local SIDEARM_SLOTS = { S = true, G1 = true, G2 = true }
local SIDEARM_KITS = { "US", "RU" }
local SIDEARM_CLASSES = { "Assault", "Engineer", "Support", "Recon" }
local SIDEARM_KIT_SUFFIXES = { "", "_GM", "_XP4", "_GM_XP4", "_XP4_SCV" }   -- the kits' names in the base game and Aftermath (SoldierSkins.lua)

-- how much closer the camera sits for a piece the size of a scope than for a whole weapon
local ACCESSORY_DISTANCE = 1.0           -- the NEAREST a window opens, and what it opens at when the size cannot be
                                         -- read. It is where the zoom's old floor used to drag the piece on the first
                                         -- input, which is the place keku calls the right one (2026-09-19: "hasta que
                                         -- no pulso shift no se colocan a la posicion que deberia ser"). A piece bigger
                                         -- than that gets more room; none ever gets less.
local ACCESSORY_FRAME = 3.2              -- times the piece's radius: how much room it gets around it in the window
local COMP = "UI/UIComponents/UICustomizationComp"
local IGNORED_SCREENS = { "chatscreen", "emptyscreen" }   -- pushed over ours without leaving it

-- ---- state -------------------------------------------------------------------------------------------------------------------
local m_Comp = nil          -- UICustomizationCompData, writable
local m_Default = nil       -- its values as the game had them (a clone)
local m_Active = false
local m_Update = nil        -- the Client:UpdateInput subscription while active
local m_Yaw = 0.0
local m_Pitch = 0.0
local m_Pending = 0.0       -- the turn still to apply (inertia)
local m_PendingPitch = 0.0
local m_Distance = 1.85     -- the zoom: how far the weapon's centre is from the camera
local m_ZoomMin, m_ZoomMax = 1.0, 3.5   -- the range the zoom moves in, rebuilt per view (a piece has its own)
local m_Slot = 0            -- 0 = the camo screen (the whole weapon); 1..3 = an accessory screen (that accessory alone)
local m_Sidearm = false     -- the pistol's window is up (m_Slot 0): the view shows m_SidearmUnlock's weapon alone
local m_SidearmUnlock = 0   -- the pistol unlock the window is about (the one worn, then each pick)
local m_Pivot = Vec3(0, 0.03, 0.4)   -- the weapon's centre in its own space (measured per weapon)
local m_FramedFor = nil     -- the piece the camera was framed for: a camo of it must not move the camera again
-- ⛔⛔ AND ITS PIVOT HAS TO BE REMEMBERED WITH IT (keku, 2026-09-21, with a picture: *"al aplicar el camo el modelo se
-- reposiciona mas arriba, luego aunque le quite el camo sigue la mira arriba"*). Every rebuild of the weapon sets the
-- pivot to the WEAPON's centre (see "pivot (the weapon's centre)"), and the piece's block puts it on the PIECE -- but
-- that block is the one "framed once per piece" skips, so on a camo change the pivot silently stayed on the weapon:
-- about 7 cm below a scope, which is the piece drifting up the window and never coming back (removing the camo is
-- another rebuild). Keeping the camera means keeping the POINT IT LOOKS AT, not only the zoom and the angle.
local m_FramedPivot = nil   -- the pivot that belongs to m_FramedFor, restored on every rebuild of that same piece
local m_ButtonCodes = {}    -- the button codes the screen's Mouse listener reported (probe)
local m_UnknownFrames = {}  -- AS2 frames nobody handles, logged once each (a new trace must never vanish in silence)
local m_CanaryWrote = false -- the mailbox probe: say ONCE what the client script managed to write
local m_ReportDone = {}     -- "<weapon>|<slot>" whose socket report has already been written (it is the same every build)
local m_AsCode = ""         -- the button code of the drag in progress
local m_AxisSeen = {}       -- the wheel probe: axes that moved
local m_ConceptSeen = {}    -- the wheel probe: concepts whose level moved
local m_ZoomHold = false    -- the zoom key is held
local m_BonesRead = false   -- the skeleton's bone list was read from the game
local m_Live = {}           -- the mannequin's weapon, as the engine dressed it: socket object instance guid (string) -> local transform
local m_PartPoses = nil     -- CamoWeaponPoses.lua: weapon name -> kind -> bone index -> 12 numbers (a base-pose entry), when the module is there
local m_SidearmPieces = nil -- CamoSidearmPieces.lua: pistol 3P mesh leaf -> { centre, radius, hide }, when the module is there
local m_AccessoryBones = nil-- CamoAccessoryBones.lua: accessory mesh (lower) -> the skeleton bones its skin uses, when the module is there
local m_PartKind = "none"   -- what the equipped unlocks mount on the weapon: "bipod", "fg" or "noaddon" (for the log / the state)
local m_PartPose = nil      -- the baked pose of the mounted kind for the weapon shown (bone index -> 12 numbers), nil = EBX only
local m_LivePose = nil      -- bone index -> LinearTransform in the weapon's space (relative to Wep_Root), from the mannequin's skeleton
local m_LiveDelta = nil     -- bone index -> that pose on the bind pose (bind^-1 x pose): a skinned mesh's base pose
local m_UiEventSeen = {}    -- the wheel probe: UI input events the engine sent
local m_LastCursor = nil
local m_AsButton = false    -- the screen's ActionScript says the button is down over the 3D half
local m_ImButton = false    -- InputManager says the button is down (the probe)
local m_Sources = { as = 0, im = 0, imAny = 0 }
local m_Loadout = nil       -- { weapon = id, accessories = { id, ... }, camo = id } as the accessories screen reported it
local m_Parts = {}          -- the spawned weapon: { entity, data, transform (local), isBase, name }
local m_Keep = {}           -- the entity data kept alive while its entity lives
local m_RetryAt = 0
local m_WeaponName = nil    -- the blueprint the weapon was built from
local m_CamoHash = 0
local m_Frame = nil         -- the customization camera as calibrated (LinearTransform), nil until measured
local m_Calib = nil         -- the calibration in progress: { step, at, started, points }
local State = nil
local Calibrate = nil       -- defined below the placement that calls it

local function LOG(p_Text)
	print("[CamoView] " .. tostring(p_Text))
	-- (no event to other mods any more, 2026-09-24: nobody listened, and an event that crosses mods takes every mod's
	-- scripting lock while ours is held -- the same cycle froze the server in "creating level", run 134)
	-- ...and to the camo framework's database, which is on disk and survives the session: what this view does is only
	-- visible in the client's console, and a console cannot be read afterwards (the framework's server side files it).
	pcall(function() NetEvents:Send("WeaponCamo:Log", "[view] " .. tostring(p_Text)) end)
end

---An identifier as the unlocks carry it (unsigned 32-bit): ActionScript numbers past 2^31 arrive negative.
local function Unsigned(p_Text)
	local s_Value = tonumber(p_Text) or 0

	if s_Value < 0 then
		s_Value = s_Value + 4294967296
	end

	return s_Value
end

-- ---- maths ---------------------------------------------------------------------------------------------------------------------
local function Scaled(p_Vec, p_Factor)
	return Vec3(p_Vec.x * p_Factor, p_Vec.y * p_Factor, p_Vec.z * p_Factor)
end

local function Sum(a, b)
	return Vec3(a.x + b.x, a.y + b.y, a.z + b.z)
end

---A vector turned about a unit axis (Rodrigues).
local function Rotated(p_Vec, p_Axis, p_Angle)
	local s_Cos, s_Sin = math.cos(p_Angle), math.sin(p_Angle)
	local s_Dot = p_Axis.x * p_Vec.x + p_Axis.y * p_Vec.y + p_Axis.z * p_Vec.z
	local s_Cross = Vec3(p_Axis.y * p_Vec.z - p_Axis.z * p_Vec.y, p_Axis.z * p_Vec.x - p_Axis.x * p_Vec.z, p_Axis.x * p_Vec.y - p_Axis.y * p_Vec.x)
	return Vec3(p_Vec.x * s_Cos + s_Cross.x * s_Sin + p_Axis.x * s_Dot * (1 - s_Cos),
		p_Vec.y * s_Cos + s_Cross.y * s_Sin + p_Axis.y * s_Dot * (1 - s_Cos),
		p_Vec.z * s_Cos + s_Cross.z * s_Sin + p_Axis.z * s_Dot * (1 - s_Cos))
end

---The transform a skinned part needs to reach a bone's pose: basePoseTransforms are MODEL-SPACE transforms applied to vertices
---that sit at their bone's rest position (measured: a translation-only entry moves the part by that much; a rotation in it turned
---the M240's bipod legs about the mesh origin and threw them a metre away), so a bone at pose (R, t) with rest position r needs
---[R | t - r.R]: the part turns about its own bone and lands at t (the skinning law of the animation notes: v' = v.R + (T - rest.R)).
local function SkinTransform(p_Pose, p_Rest)
	local s_Turned = Vec3(p_Rest.x * p_Pose.left.x + p_Rest.y * p_Pose.up.x + p_Rest.z * p_Pose.forward.x,
		p_Rest.x * p_Pose.left.y + p_Rest.y * p_Pose.up.y + p_Rest.z * p_Pose.forward.y,
		p_Rest.x * p_Pose.left.z + p_Rest.y * p_Pose.up.z + p_Rest.z * p_Pose.forward.z)
	return LinearTransform(p_Pose.left, p_Pose.up, p_Pose.forward, Vec3(p_Pose.trans.x - s_Turned.x, p_Pose.trans.y - s_Turned.y, p_Pose.trans.z - s_Turned.z))
end

---A local transform expressed in a parent's frame (the parent's axes carry the local's).
local function Compose(p_Parent, p_Local)
	local function axis(v)
		return Sum(Sum(Scaled(p_Parent.left, v.x), Scaled(p_Parent.up, v.y)), Scaled(p_Parent.forward, v.z))
	end

	return LinearTransform(axis(p_Local.left), axis(p_Local.up), axis(p_Local.forward), Sum(p_Parent.trans, axis(p_Local.trans)))
end

---The weapon's root in front of the camera, turned by the drag: the camera's frame moved by PROP_AT, its forward/left rotated by the
---yaw around the camera's up, then tilted around its left.
local function RootTransform()
	local s_Cam = m_Frame or ClientUtils:GetCameraTransform()

	if s_Cam == nil then
		return nil
	end

	-- the centre of what is shown: PROP_AT with the zoom's distance along the camera's forward axis (negative = in front).
	-- ⛔ PROP_AT's sideways offset is a DISTANCE, not an angle, so it only means the same thing on screen at the distance it
	-- was measured at (-PROP_AT.z). Held fixed, a piece 0.55 m away lands ~29° off axis and out of the view (keku's boot
	-- 2026-09-18), and -- measured 2026-09-19 -- ANY change of distance slides it across the screen: *"cuando hago zoom
	-- in/out el accesorio se recoloca a una posición nueva"*, because the scale was pinned to a constant instead of to the
	-- distance in force. It scales with m_Distance, always and for everything: at the design distance the factor is 1, so
	-- the weapon is framed exactly as it was, and zooming now keeps whatever is shown in its place on the screen.
	local s_K = m_Distance / -PROP_AT.z
	local s_AtX, s_AtY = PROP_AT.x * s_K, PROP_AT.y * s_K

	local s_Pos = Sum(Sum(Sum(s_Cam.trans, Scaled(s_Cam.left, s_AtX)), Scaled(s_Cam.up, s_AtY)), Scaled(s_Cam.forward, -m_Distance))
	-- yaw about the camera's up, then the tilt about the SCREEN's horizontal axis (the camera's left) so that dragging up always
	-- raises the side facing the viewer, whichever way the weapon is turned: a proper frame (left x up = forward, as the engine's)
	local s_Cos, s_Sin = math.cos(m_Yaw), math.sin(m_Yaw)
	local s_Forward = Sum(Scaled(s_Cam.forward, s_Cos), Scaled(s_Cam.left, s_Sin))
	local s_Left = Sum(Scaled(s_Cam.left, s_Cos), Scaled(s_Cam.forward, -s_Sin))
	local s_Up = s_Cam.up

	if m_Pitch ~= 0 then
		local s_Angle = m_Pitch * PITCH_SIGN
		s_Forward = Rotated(s_Forward, s_Cam.left, s_Angle)
		s_Left = Rotated(s_Left, s_Cam.left, s_Angle)
		s_Up = Rotated(s_Up, s_Cam.left, s_Angle)
	end

	return LinearTransform(s_Left, s_Up, s_Forward, s_Pos)
end

-- ---- the customization component (the mannequin's place) ---------------------------------------------------------------------
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

local function PlaceMannequin()
	local s_Comp = Comp()

	if s_Comp == nil then
		return
	end

	if PROP then
		if CALIBRATE and not Calibrate(s_Comp) then
			return
		end

		s_Comp.soldierOffset = Vec3(PARK_OFFSET.x, PARK_OFFSET.y, PARK_OFFSET.z)
	else
		s_Comp.soldierOffset = Vec3(MANNEQUIN_OFFSET.x, MANNEQUIN_OFFSET.y, MANNEQUIN_OFFSET.z)
		s_Comp.soldierRotation = Vec3(0.0, m_Yaw, 0.0)
	end
end

local function RestoreMannequin()
	if m_Comp == nil or m_Default == nil then
		return
	end

	pcall(function()
		m_Comp.soldierOffset = Vec3(m_Default.soldierOffset.x, m_Default.soldierOffset.y, m_Default.soldierOffset.z)
		m_Comp.soldierRotation = Vec3(m_Default.soldierRotation.x, m_Default.soldierRotation.y, m_Default.soldierRotation.z)
	end)
end

-- ---- the mannequin itself (its place measures the customization camera) ---------------------------------------------------------
local function MannequinPosition()
	local s_Pos = nil

	pcall(function()
		local s_Iter = EntityManager:GetIterator("ClientSoldierEntity")
		local s_Entity = s_Iter:Next()

		while s_Entity ~= nil and s_Pos == nil do
			local s_Soldier = SoldierEntity(s_Entity)

			if s_Soldier ~= nil and s_Soldier.player == nil then
				local s_Transform = s_Soldier.worldTransform
				s_Pos = Vec3(s_Transform.trans.x, s_Transform.trans.y, s_Transform.trans.z)
			end

			s_Entity = s_Iter:Next()
		end
	end)

	return s_Pos
end

local CALIB_OFFSETS = { Vec3(0, 0, 0), Vec3(1, 0, 0), Vec3(0, 1, 0), Vec3(0, 0, -1) }

local function Normalised(p_Vec)
	local s_Len = math.sqrt(p_Vec.x * p_Vec.x + p_Vec.y * p_Vec.y + p_Vec.z * p_Vec.z)

	if s_Len < 0.0001 then
		return nil
	end

	return Vec3(p_Vec.x / s_Len, p_Vec.y / s_Len, p_Vec.z / s_Len), s_Len
end

local function Describe(p_Transform)
	if p_Transform == nil then
		return "nil"
	end

	return string.format("pos (%.2f, %.2f, %.2f) fwd (%.2f, %.2f, %.2f) left (%.2f, %.2f, %.2f) up (%.2f, %.2f, %.2f)",
		p_Transform.trans.x, p_Transform.trans.y, p_Transform.trans.z, p_Transform.forward.x, p_Transform.forward.y, p_Transform.forward.z,
		p_Transform.left.x, p_Transform.left.y, p_Transform.left.z, p_Transform.up.x, p_Transform.up.y, p_Transform.up.z)
end

---One step of the calibration per call: writes the step's offset, and once the engine has moved the mannequin there reads its place.
---Returns true when the frame is known (measured, or the API's after the timeout).
Calibrate = function(p_Comp)
	if m_Frame ~= nil then
		return true
	end

	local s_Now = SharedUtils:GetTimeMS()

	if m_Calib == nil then
		m_Calib = { step = 1, at = s_Now + CALIB_STEP_MS, started = s_Now, points = {} }
	end

	local s_Offset = CALIB_OFFSETS[m_Calib.step]
	p_Comp.soldierOffset = Vec3(s_Offset.x, s_Offset.y, s_Offset.z)

	if s_Now - m_Calib.started > CALIB_TIMEOUT_MS then
		m_Frame = ClientUtils:GetCameraTransform()
		LOG("camera calibration timed out (no mannequin moving): using the API's camera " .. Describe(m_Frame))
		return m_Frame ~= nil
	end

	if s_Now < m_Calib.at then
		return false
	end

	local s_Pos = MannequinPosition()

	if s_Pos == nil then
		m_Calib.at = s_Now + CALIB_STEP_MS
		return false
	end

	-- the step is done when the mannequin is a metre from the origin point along the step's axis (the first point is taken as is)
	if m_Calib.step > 1 then
		local s_Origin = m_Calib.points[1]
		local _, s_Dist = Normalised(Vec3(s_Pos.x - s_Origin.x, s_Pos.y - s_Origin.y, s_Pos.z - s_Origin.z))

		if s_Dist == nil or s_Dist < 0.7 or s_Dist > 1.3 then
			m_Calib.at = s_Now + CALIB_STEP_MS
			return false
		end
	end

	m_Calib.points[m_Calib.step] = s_Pos
	m_Calib.step = m_Calib.step + 1
	m_Calib.at = s_Now + CALIB_STEP_MS

	if m_Calib.step <= #CALIB_OFFSETS then
		return false
	end

	local s_P = m_Calib.points
	local s_Left = Normalised(Vec3(s_P[2].x - s_P[1].x, s_P[2].y - s_P[1].y, s_P[2].z - s_P[1].z))
	local s_Up = Normalised(Vec3(s_P[3].x - s_P[1].x, s_P[3].y - s_P[1].y, s_P[3].z - s_P[1].z))
	-- the engine's forward axis points BACKWARD (offset z -1 put the mannequin in front): origin minus the front point, or the frame
	-- comes out mirrored and every mesh in it draws inside out (v17)
	local s_Forward = Normalised(Vec3(s_P[1].x - s_P[4].x, s_P[1].y - s_P[4].y, s_P[1].z - s_P[4].z))

	if s_Left == nil or s_Up == nil or s_Forward == nil then
		m_Frame = ClientUtils:GetCameraTransform()
		LOG("camera calibration degenerate: using the API's camera " .. Describe(m_Frame))
		return m_Frame ~= nil
	end

	m_Frame = LinearTransform(s_Left, s_Up, s_Forward, s_P[1])
	LOG("customization camera measured from the mannequin: " .. Describe(m_Frame))
	LOG("the API's camera for comparison: " .. Describe(ClientUtils:GetCameraTransform()))
	return true
end

-- ---- the mannequin's weapon (which weapon is being customized) ----------------------------------------------------------------
---The customization mannequin's weapon blueprint name ("Weapons/M416/M416"): the one soldier without a player behind it. Nil when
---it has none right now (it loses it for a few frames while re-dressing).
local function MenuWeapon()
	local s_Name = nil

	pcall(function()
		local s_Iter = EntityManager:GetIterator("ClientSoldierEntity")
		local s_Entity = s_Iter:Next()

		while s_Entity ~= nil and s_Name == nil do
			local s_Soldier = SoldierEntity(s_Entity)

			if s_Soldier ~= nil and s_Soldier.player == nil and s_Soldier.weaponsComponent ~= nil then
				local s_Current = s_Soldier.weaponsComponent.currentWeapon

				if s_Current ~= nil then
					s_Name = tostring(SoldierWeapon(s_Current).name)
				end
			end

			s_Entity = s_Iter:Next()
		end
	end)

	return s_Name
end

---The weapon blueprint of a sidearm unlock, found by its identifier in the kits' weapon tables (the SIDEARM row's own list), its
---name, and the identifier of the attachment that VARIANT mounts (0 = none); nil and why when no table lists it. The blueprint is
---returned as an object, not looked up by name: a pistol made with a camo's mesh is a copy of the game's and has no partition.
---⭐ A pistol's variants (keku 2026-10-06: *"hay varias versiones de cada arma, como la magnum con mira, m1911, m1911 táctica…
---cuando el usuario abra estas variaciones también debe aparecer el accesorio asociado"*) are unlocks of their own on the SAME
---blueprint, and the attachment is their `Extra` -- an unlock that the blueprint's sockets name (measured in the EBX: Taurus44
---Scoped → U_Taurus44_PKA, M1911 Tactical → U_M1911_Tactical_Unlock, M93R Laser → U_M93R_TargetPointer, 13 such variants).
local function SidearmBlueprint(p_Id)
	if p_Id == nil or p_Id == 0 then
		return nil, "the pistol's window has not said which pistol yet"
	end

	local s_Blueprint, s_Kits, s_Extra = nil, 0, 0

	for _, l_Team in ipairs(SIDEARM_KITS) do
		for _, l_Class in ipairs(SIDEARM_CLASSES) do
			for _, l_Suffix in ipairs(SIDEARM_KIT_SUFFIXES) do
				if s_Blueprint == nil then
					pcall(function()
						local l_Found = ResourceManager:SearchForDataContainer("Gameplay/Kits/" .. l_Team .. l_Class .. l_Suffix)

						if l_Found == nil then
							return
						end

						s_Kits = s_Kits + 1

						for _, l_Part in pairs(VeniceSoldierCustomizationAsset(l_Found).weaponTable.unlockParts) do
							for _, l_Unlock in pairs(l_Part.selectableUnlocks) do
								if (UnlockAssetBase(l_Unlock).identifier & 0xFFFFFFFF) == p_Id then
									local l_Weapon = SoldierWeaponUnlockAsset(l_Unlock)
									s_Blueprint = l_Weapon.weapon
									-- (a field the type does not declare reads nil without an error: the build's log says what came)
									pcall(function()
										if l_Weapon.extra ~= nil then
											s_Extra = UnlockAssetBase(l_Weapon.extra).identifier & 0xFFFFFFFF
										end
									end)
									return
								end
							end
						end
					end)
				end
			end
		end
	end

	if s_Blueprint == nil then
		return nil, "unlock " .. tostring(p_Id) .. " is in no weapon table of the " .. s_Kits .. " kit(s) of this level"
	end

	return s_Blueprint, tostring(s_Blueprint.name), s_Extra
end

-- ---- the weapon, composed ------------------------------------------------------------------------------------------------------
local function Equipped(p_Id)
	if m_Loadout == nil or p_Id == nil then
		return false
	end

	for _, l_Id in ipairs(m_Loadout.accessories) do
		if l_Id == p_Id then
			return true
		end
	end

	return m_Loadout.camo == p_Id
end

---The variation hash of a camo unlock of this weapon (0 = none: the weapon as it comes).
local function CamoHash(p_WeaponData, p_CamoId)
	local s_Hash = 0

	if p_CamoId == nil or p_CamoId == 0 then
		return 0
	end

	pcall(function()
		local s_Asset = p_WeaponData.customization

		if s_Asset == nil then
			return
		end

		local s_Table = VeniceSoldierWeaponCustomizationAsset(s_Asset).customization

		if s_Table == nil then
			return
		end

		for _, l_Group in pairs(s_Table.unlockParts) do
			for _, l_UnlockBase in pairs(l_Group.selectableUnlocks) do
				local l_Unlock = UnlockAsset(l_UnlockBase)

				if l_Unlock ~= nil and l_Unlock.identifier == p_CamoId then
					for _, l_Linked in pairs(l_Unlock.linkedTo) do
						if l_Linked:Is("BlueprintAndVariationPair") then
							local l_Pair = BlueprintAndVariationPair(l_Linked)

							if l_Pair.variation ~= nil then
								s_Hash = l_Pair.variation.nameHash
								return
							end
						end
					end
				end
			end
		end
	end)

	return s_Hash
end

local function Destroy(p_Part)
	pcall(function()
		if p_Part.entity ~= nil then
			p_Part.entity:Destroy()
		end
	end)
	p_Part.entity = nil
end

local function DestroyAll()
	for _, l_Part in ipairs(m_Parts) do
		Destroy(l_Part)
	end

	m_Parts = {}
	m_Keep = {}
end

---A part's local transform with the weapon's pivot moved to the origin (the root is placed and turned at the pivot).
local function Pivoted(p_Local)
	return LinearTransform(p_Local.left, p_Local.up, p_Local.forward, Vec3(p_Local.trans.x - m_Pivot.x, p_Local.trans.y - m_Pivot.y, p_Local.trans.z - m_Pivot.z))
end

---The centre of a spawned mesh in the weapon's space, from the entity's own bounding box (nil when the engine has none yet).
---⛔ THE SIZE TEST IS ON THE LONGEST SIDE, NOT ON X (measured 2026-09-20: the iron sights stayed off centre
---although the view was told to centre on them). The old test asked whether the box was 5 cm WIDE -- fair for
---a rifle's body, which is what it was written for, and false for every thin part: a sight is a blade a
---couple of centimetres across, so the centre came back nil and nothing moved. What a window frames is small
---by definition, so it asks with a threshold of its own.
local function BoundsCentre(p_Part, p_Min)
	local s_Centre = nil

	pcall(function()
		local s_Box = SpatialEntity(p_Part.entity).aabb
		local s_Longest = s_Box == nil and 0 or math.max(s_Box.max.x - s_Box.min.x,
			math.max(s_Box.max.y - s_Box.min.y, s_Box.max.z - s_Box.min.z))

		if s_Box ~= nil and s_Longest > (p_Min or 0.05) then
			-- the box is the mesh's own (local); the part sits at its raw local transform, so the centre is in the weapon's space
			s_Centre = Vec3((s_Box.min.x + s_Box.max.x) * 0.5 + p_Part.raw.trans.x, (s_Box.min.y + s_Box.max.y) * 0.5 + p_Part.raw.trans.y,
				(s_Box.min.z + s_Box.max.z) * 0.5 + p_Part.raw.trans.z)
			p_Part.box = string.format("(%.2f, %.2f, %.2f)..(%.2f, %.2f, %.2f)", s_Box.min.x, s_Box.min.y, s_Box.min.z, s_Box.max.x, s_Box.max.y, s_Box.max.z)
		end
	end)

	return s_Centre
end

---How big a spawned mesh is: half its bounding box's longest side, in metres (nil when the engine has no box yet).
---What a window has to frame is the PIECE, and the pieces of a weapon are not one size: a reflex sight is 6 cm and a
---bipod is 30. Opening every one of them at the same distance puts half of them in your face (keku 2026-09-19:
---"aparecen mas cerca de lo normal").
local function BoundsRadius(p_Part)
	local s_Radius = nil

	pcall(function()
		local s_Box = SpatialEntity(p_Part.entity).aabb

		if s_Box ~= nil then
			local l_X, l_Y, l_Z = s_Box.max.x - s_Box.min.x, s_Box.max.y - s_Box.min.y, s_Box.max.z - s_Box.min.z
			local l_Longest = math.max(l_X, math.max(l_Y, l_Z))

			if l_Longest > 0.01 then
				s_Radius = l_Longest * 0.5
			end
		end
	end)

	return s_Radius
end

---Spawns one mesh of the weapon at a local transform (relative to the weapon's origin) with a variation.
---A base-pose entry as a NEW LinearTransform: a baked entry (12 numbers: left, up, forward, translation) or a copy of a transform
---(the EBX's, or one of ours). Made right before it is added: a transform already added to one entity's basePoseTransforms and
---added again to another's exploded the mesh (v32, on every camo change).
local function FreshTransform(p_Entry)
	if type(p_Entry) == "table" then
		return LinearTransform(Vec3(p_Entry[1], p_Entry[2], p_Entry[3]), Vec3(p_Entry[4], p_Entry[5], p_Entry[6]), Vec3(p_Entry[7], p_Entry[8], p_Entry[9]),
			Vec3(p_Entry[10], p_Entry[11], p_Entry[12]))
	end

	if p_Entry == nil then
		return LinearTransform()
	end

	return LinearTransform(Vec3(p_Entry.left.x, p_Entry.left.y, p_Entry.left.z), Vec3(p_Entry.up.x, p_Entry.up.y, p_Entry.up.z),
		Vec3(p_Entry.forward.x, p_Entry.forward.y, p_Entry.forward.z), Vec3(p_Entry.trans.x, p_Entry.trans.y, p_Entry.trans.z))
end

---The base pose list of a weapon state: its EBX deltas, with the baked entries of the mounted kind (bone index -> 12 numbers) in
---their bones' places when given. Entries are descriptors; Spawn makes the transforms.
local function PoseList(p_State, p_KindPose)
	local s_List = {}

	for l_Index = 1, 25 do
		local l_Entry = nil
		pcall(function() l_Entry = p_State.mesh3pTransforms[l_Index] end)

		if p_KindPose ~= nil and p_KindPose[l_Index] ~= nil then
			l_Entry = p_KindPose[l_Index]
		end

		s_List[l_Index] = l_Entry or LinearTransform()
	end

	return s_List
end

local function Spawn(p_Name, p_Mesh, p_Local, p_Hash, p_BasePose, p_IsBase)
	local s_Root = RootTransform()

	if s_Root == nil then
		return nil, "no camera"
	end

	local s_Part = { name = p_Name, raw = p_Local, transform = Pivoted(p_Local), isBase = p_IsBase }
	local s_Ok, s_Err = pcall(function()
		local s_Data = StaticModelEntityData()
		s_Data.mesh = MeshAsset(p_Mesh)

		if p_BasePose ~= nil then
			-- in bone order (a typed array of the game or a plain table of ours, both 1-based); a fresh transform per entry
			for l_Index = 1, #p_BasePose do
				s_Data.basePoseTransforms:add(FreshTransform(p_BasePose[l_Index]))
			end
		end

		local s_Params = EntityCreationParams()
		s_Params.variationNameHash = p_Hash
		s_Params.transform = Compose(s_Root, s_Part.transform)

		local s_Entity = EntityManager:CreateEntity(s_Data, s_Params)

		if s_Entity == nil then
			error("CreateEntity returned nil")
		end

		s_Entity:Init(Realm.Realm_Client, true)
		s_Part.entity = s_Entity
		s_Part.data = s_Data
		m_Keep[#m_Keep + 1] = s_Data
	end)

	if not s_Ok then
		return nil, tostring(s_Err)
	end

	m_Parts[#m_Parts + 1] = s_Part
	return s_Part, nil
end

---The name a mesh asset carries, for the log.
local function NameOf(p_Asset)
	local s_Name = "?"
	pcall(function() s_Name = tostring(Asset(p_Asset).name) end)
	return s_Name
end

---What a PIECE is, for the camera: the leaf of its mesh name, lower case.
---⛔ The leaf and not the path: a camo of a piece is a CLONE of the same mesh under another folder
---("C/<CAMO>/…/kobra_1p_Mesh" against "weapons/accessories/kobra/kobra_1p_Mesh"), so the leaf is what says
---"this is the same object" while the unlock id and the full path both change with every camo.
local function PieceKey(p_Asset)
	local s_Name = string.lower(NameOf(p_Asset))
	return string.match(s_Name, "([^/]+)$") or s_Name
end

---Whether a mesh of an attachment is LIGHT rather than body: the beam of a flashlight or a laser, the reticle
---inside a sight. They are drawn, but they are not what the window is looking at.
---⛔ keku, 2026-09-20: *"arregla la posicion del preview en la linterna y la mira laser, ya que tiene en cuenta
---los 2 cuerpos a la vez, pon el pivote solo en el dispositivo en si"*. A flashlight's beam is metres long, so a
---box drawn around the device AND its light centres the view on the middle of the light and frames the piece to
---the size of the cone: the device ends up a speck in the corner.
---📐 The rule is measured, not guessed: of the 179 accessory meshes in the game, 24 wear no weapon preset, and
---the ones that are not solid geometry are exactly those whose name carries "beam" or "reticule" (4 beams and 12
---reticles; the other 8 are the flip-up sights, which ARE bodies and stay in the box).
local function IsLightMesh(p_Key)
	return string.find(p_Key, "beam", 1, true) ~= nil or string.find(p_Key, "reticule", 1, true) ~= nil
end

---The mannequin's weapon entity (a SoldierWeapon), or nil.
local function MenuWeaponEntity()
	local s_Weapon = nil

	pcall(function()
		local s_Iter = EntityManager:GetIterator("ClientSoldierEntity")
		local s_Entity = s_Iter:Next()

		while s_Entity ~= nil and s_Weapon == nil do
			local s_Soldier = SoldierEntity(s_Entity)

			if s_Soldier ~= nil and s_Soldier.player == nil and s_Soldier.weaponsComponent ~= nil then
				s_Weapon = s_Soldier.weaponsComponent.currentWeapon
			end

			s_Entity = s_Iter:Next()
		end
	end)

	return s_Weapon
end

---The baked weapon-part poses, loaded once; nil when the module is not shipped.
local function PartPoses()
	if m_PartPoses == nil then
		local s_Ok, s_Module = pcall(require, "CamoWeaponPoses")

		if s_Ok and type(s_Module) == "table" then
			m_PartPoses = s_Module
		else
			m_PartPoses = false
			LOG("CamoWeaponPoses.lua is not shipped (" .. tostring(s_Module) .. "): the bipod bones keep the EBX deltas")
		end
	end

	return m_PartPoses or nil
end

---The pistols as this view draws them (CamoSidearmPieces.lua, baked by External/keku/sidearm_pieces.py), loaded once; nil when the
---module is not shipped. Per 3P mesh leaf: the drawn pistol's centre and radius, and the bones whose piece the EBX deltas park
---outside it (keku 2026-10-06, a photo: the Taurus' speedloader floating behind the grip, and the pistol turning about a point
---ahead of it -- its authored box is the BIND pose's, which on a pistol is not where it is drawn).
local function SidearmPieces()
	if m_SidearmPieces == nil then
		local s_Ok, s_Module = pcall(require, "CamoSidearmPieces")

		if s_Ok and type(s_Module) == "table" then
			m_SidearmPieces = s_Module
		else
			m_SidearmPieces = false
			LOG("CamoSidearmPieces.lua is not shipped (" .. tostring(s_Module) .. "): a pistol keeps the box's centre and every piece")
		end
	end

	return m_SidearmPieces or nil
end

---A base-pose entry that draws its bone's piece as nothing (every axis zero: its vertices collapse onto the bone), at the place the
---entry had -- what the game does with a part it parks: it never shows it.
local function Collapsed(p_Entry)
	local s_T = { 0, 0, 0 }

	if type(p_Entry) == "table" and #p_Entry >= 12 then
		s_T = { p_Entry[10], p_Entry[11], p_Entry[12] }
	elseif p_Entry ~= nil then
		pcall(function() s_T = { p_Entry.trans.x, p_Entry.trans.y, p_Entry.trans.z } end)
	end

	return { 0, 0, 0, 0, 0, 0, 0, 0, 0, s_T[1], s_T[2], s_T[3] }
end

---The bone table of every accessory mesh in the game (mesh name in lower case -> the skeleton bone indices its skin uses),
---read from the meshes themselves and generated; nil when the module is not shipped.
local function AccessoryBones()
	if m_AccessoryBones == nil then
		local s_Ok, s_Module = pcall(require, "CamoAccessoryBones")

		if s_Ok and type(s_Module) == "table" then
			m_AccessoryBones = s_Module
		else
			m_AccessoryBones = false
			LOG("CamoAccessoryBones.lua is not shipped (" .. tostring(s_Module) .. "): a skinned piece is framed by the " ..
				"bipod bones, which is where this game hangs them")
		end
	end

	return m_AccessoryBones or nil
end

---WHERE A PIECE ACTUALLY IS, for every accessory of every weapon and without one line about any of them in particular
---(keku 2026-09-19: *"arregla el posicionamiento de los accesorios de raíz, no toques uno por uno, fuerza que todos estén
---en su sitio pase lo que pase"*). A socket object reaches the screen in one of two ways, and the question is the same:
---which point of the weapon's model space does its geometry sit on?
---  * RIGID: it carries its own transform, so that point is the transform.
---  * SKINNED: it spawns at the ORIGIN and its SKIN puts it on the bones it is weighted to, so the point is where those
---    bones are in this pose. Which bones is a property of the mesh -- read from the game for every accessory mesh, never
---    guessed here. A base-pose entry is [R | t - rest*R], so a bone's own position comes back by composing the entry
---    with the bind translation.
---Measured 2026-09-19 on keku's M240: the bipod and the foregrip spawned right and the view framed (0,0,0) while their
---bone sat at z 0.93 with the camera 0.55 m away -- the pieces were a metre off screen, not missing.
local function AnchorOf(p_Mesh, p_Skinned, p_Local, p_Pose, p_Report)
	local s_Fallback = Vec3(p_Local.trans.x, p_Local.trans.y, p_Local.trans.z)

	if not p_Skinned then
		return s_Fallback
	end

	local s_Table = AccessoryBones()
	local s_Bones = s_Table ~= nil and s_Table[string.lower(NameOf(p_Mesh))] or nil

	-- No entry for this mesh: the bones a skinned socket object hangs from in this game (Wep_Bipod1..3). A net, not a rule.
	if s_Bones == nil then
		s_Bones = { BONES.Wep_Bipod1, BONES.Wep_Bipod2, BONES.Wep_Bipod3 }
	end

	local s_Sum, s_Count = Vec3(0, 0, 0), 0

	for _, l_Bone in ipairs(s_Bones) do
		local l_Entry = p_Pose ~= nil and p_Pose[l_Bone] or nil
		local l_Rest = (m_BindPose ~= nil and m_BindPose[l_Bone] ~= nil) and m_BindPose[l_Bone].trans or BIND[l_Bone]

		-- the root is the weapon's own origin: it says nothing about where a piece hangs
		if l_Bone ~= BONES.Wep_Root and l_Entry ~= nil and l_Rest ~= nil then
			-- ⛔ A POSE ENTRY IS A DESCRIPTOR, NOT A TRANSFORM (12 numbers: left, up, forward, translation) -- handing it to
			-- Compose read a nil axis and took the whole build down with it (measured 2026-09-19: the bipod's window logged
			-- its pivot and never reached "weapon built"). FreshTransform is what turns either form into one.
			local l_Ok, l_At = pcall(function()
				return Compose(FreshTransform(l_Entry),
					LinearTransform(Vec3(1, 0, 0), Vec3(0, 1, 0), Vec3(0, 0, 1), l_Rest)).trans
			end)

			if l_Ok and l_At ~= nil then
				s_Sum = Sum(s_Sum, l_At)
				s_Count = s_Count + 1
			end
		end
	end

	if s_Count == 0 then
		return s_Fallback
	end

	local s_At = Vec3(s_Sum.x / s_Count, s_Sum.y / s_Count, s_Sum.z / s_Count)
	p_Report[#p_Report + 1] = string.format("  skinned on %d bone(s): this pose puts them at (%.3f, %.3f, %.3f)",
		s_Count, s_At.x, s_At.y, s_At.z)
	return s_At
end

---What the equipped unlocks mount on the weapon, the way the game picks its weapon-part pose: the WeaponAnimTypeModifier among the
---modifiers of each equipped unlock (weaponModifierData), bipod over foregrip over nothing. Returns the kind and the unlock that set it.
local function AnimKind(p_Data)
	local s_Kind, s_By = "noaddon", nil
	local s_Rank = { noaddon = 0, fg = 1, bipod = 2 }

	pcall(function()
		for _, l_Entry in pairs(p_Data.weaponModifierData) do
			local l_UnlockId = nil
			pcall(function()
				if l_Entry.unlockAsset ~= nil then
					l_UnlockId = UnlockAsset(l_Entry.unlockAsset).identifier
				end
			end)

			if l_UnlockId ~= nil and Equipped(l_UnlockId) then
				for _, l_Modifier in pairs(l_Entry.modifiers) do
					pcall(function()
						if l_Modifier:Is("WeaponAnimTypeModifier") then
							local l_Type = WeaponAnimTypeModifier(l_Modifier).weaponAnimType
							local l_Found = nil

							if l_Type == WeaponAnimType.WeaponAnimType_Bipod then
								l_Found = "bipod"
							elseif l_Type == WeaponAnimType.WeaponAnimType_Foregrip then
								l_Found = "fg"
							end

							if l_Found ~= nil and s_Rank[l_Found] > s_Rank[s_Kind] then
								s_Kind, s_By = l_Found, l_UnlockId
							end
						end
					end)
				end
			end
		end
	end)

	return s_Kind, s_By
end


---The mannequin's soldier entity, or nil.
local function MannequinSoldier()
	local s_Found = nil

	pcall(function()
		local s_Iter = EntityManager:GetIterator("ClientSoldierEntity")
		local s_Entity = s_Iter:Next()

		while s_Entity ~= nil and s_Found == nil do
			local s_Soldier = SoldierEntity(s_Entity)

			if s_Soldier ~= nil and s_Soldier.player == nil then
				s_Found = s_Soldier
			end

			s_Entity = s_Iter:Next()
		end
	end)

	return s_Found
end

---Reads the weapon's bones as the mannequin animates them: each Wep_* bone's world transform from the soldier's skeleton, expressed
---relative to Wep_Root (the weapon mesh's own space). Fills m_LivePose (the pose) and m_LiveDelta (the pose on the bind pose).
local function ReadLivePose(p_Report, p_State)
	m_LivePose = nil
	m_LiveDelta = nil

	if not LIVE_POSE and not LIVE_PROBE then
		return
	end

	local s_Soldier = MannequinSoldier()

	if s_Soldier == nil then
		p_Report[#p_Report + 1] = "live pose: no mannequin"
		return
	end

	pcall(function()
		local s_Ragdoll = s_Soldier.ragdollComponent

		if s_Ragdoll == nil then
			p_Report[#p_Report + 1] = "live pose: the mannequin has no ragdoll component"
			return
		end

		-- the frame the bones are read in: the soldier's Wep_Root bone (the hand on the weapon)
		local s_RootQuat = s_Ragdoll:GetActiveWorldTransform(SOLDIER_WEAPON_BONE0 + 2)

		if s_RootQuat == nil then
			p_Report[#p_Report + 1] = "live pose: the mannequin's Wep_Root is not readable"
			return
		end

		local s_Root = s_RootQuat:ToLinearTransform()
		local s_Inverse = s_Root:Inverse()
		local s_Hand = nil
		local s_Pose = {}
		local s_Delta = {}
		local s_Count = 0

		for l_Index = 2, 25 do
			local l_Quat = s_Ragdoll:GetActiveWorldTransform(SOLDIER_WEAPON_BONE0 + l_Index)

			if l_Quat ~= nil then
				local l_World = l_Quat:ToLinearTransform()
				local l_Local = Compose(s_Inverse, l_World)
				s_Pose[l_Index] = l_Local

				local l_Bind = (m_BindPose ~= nil and m_BindPose[l_Index]) or (BIND[l_Index] ~= nil and LinearTransform(Vec3(1, 0, 0), Vec3(0, 1, 0), Vec3(0, 0, 1), BIND[l_Index])) or LinearTransform()
				s_Delta[l_Index] = Compose(l_Bind:Inverse(), l_Local)
				s_Count = s_Count + 1
			end
		end

		if s_Count >= 20 then
			-- the anchor: where the hand (the frame the bones came in) sits on the mesh = the EBX's Wep_Mag minus the mannequin's
			-- Wep_Mag (the magazine's EBX delta has been right on every weapon seen); sane only when the mannequin's Wep_Mag axes are the
			-- weapon's (no rotation between the hand and the weapon) and the anchor is a weapon's length at most
			local s_Anchor = nil
			local s_Why = "no Wep_Mag"
			pcall(function()
				local l_Ebx = p_State.mesh3pTransforms[8]
				local l_Bind = (m_BindPose ~= nil and m_BindPose[8]) or LinearTransform(Vec3(1, 0, 0), Vec3(0, 1, 0), Vec3(0, 0, 1), BIND[8])
				local l_Expected = l_Ebx ~= nil and Compose(l_Bind, l_Ebx) or l_Bind
				local l_Live = s_Pose[8]

				if l_Live == nil then
					return
				end

				local l_Aligned = l_Live.forward.z > 0.95 and l_Live.up.y > 0.95
				local l_Anchor = Vec3(l_Expected.trans.x - l_Live.trans.x, l_Expected.trans.y - l_Live.trans.y, l_Expected.trans.z - l_Live.trans.z)
				local _, l_Len = Normalised(l_Anchor)

				if not l_Aligned then
					s_Why = string.format("the mannequin's Wep_Mag axes are not the weapon's (fwd z %.2f, up y %.2f)", l_Live.forward.z, l_Live.up.y)
				elseif l_Len == nil or l_Len > LIVE_ANCHOR_MAX then
					s_Why = string.format("the anchor is %.2f m long", l_Len or 0)
				else
					s_Anchor = l_Anchor
					s_Hand = l_Anchor
				end
			end)

			if LIVE_POSE and s_Anchor ~= nil then
				-- only the bipod bones: the mannequin's PLACE plus the anchor, with the weapon's own axes -- the mannequin's bone rotations
				-- are not what the engine draws (v30: the right place, the wrong turn on the L85A2's foregrip and the M240's bipod legs);
				-- the foregrip mesh hangs from Wep_Bipod1 (its bone table: Wep_Root, Wep_Bipod1), the bipod meshes from Wep_Bipod1..3
				m_LivePose = {}
				m_LiveDelta = {}

				for _, l_Index in ipairs(LIVE_BONES) do
					local l_Live = s_Pose[l_Index]

					if l_Live ~= nil then
						local l_Mesh = LinearTransform(Vec3(1, 0, 0), Vec3(0, 1, 0), Vec3(0, 0, 1),
							Vec3(l_Live.trans.x + s_Anchor.x, l_Live.trans.y + s_Anchor.y + LIVE_BIPOD_LIFT, l_Live.trans.z + s_Anchor.z))
						local l_Bind = (m_BindPose ~= nil and m_BindPose[l_Index]) or (BIND[l_Index] ~= nil and LinearTransform(Vec3(1, 0, 0), Vec3(0, 1, 0), Vec3(0, 0, 1), BIND[l_Index])) or LinearTransform()
						m_LivePose[l_Index] = l_Mesh
						m_LiveDelta[l_Index] = SkinTransform(l_Mesh, l_Bind.trans)
					end
				end
			end

			p_Report[#p_Report + 1] = string.format("live pose: %s; the hand (soldier Wep_Root) sits at %s of the mesh",
				s_Anchor ~= nil and (LIVE_POSE and "the mannequin's bipod bones pose the skinned accessories" or "anchored (LIVE_POSE off)") or ("EBX only: " .. s_Why),
				s_Hand and string.format("(%.3f, %.3f, %.3f)", s_Hand.x, s_Hand.y, s_Hand.z) or "?")

			if m_LivePose ~= nil and m_LivePose[18] ~= nil then
				p_Report[#p_Report + 1] = string.format("  Wep_Bipod1 on the mesh: (%.3f, %.3f, %.3f) (the EBX said (%.3f, %.3f, %.3f))", m_LivePose[18].trans.x, m_LivePose[18].trans.y, m_LivePose[18].trans.z,
					(function() local b = (m_BindPose ~= nil and m_BindPose[18]) or LinearTransform(Vec3(1, 0, 0), Vec3(0, 1, 0), Vec3(0, 0, 1), BIND[18]); local e = p_State.mesh3pTransforms[18]; local t = e ~= nil and Compose(b, e) or b; return t.trans.x, t.trans.y, t.trans.z end)())
			end

			-- the probe: each bone as the mannequin animates it (relative to Wep_Root), the soldier skeleton's own bind for it, and
			-- the weapon skeleton's bind + the weapon's EBX delta -- the three must agree for the live pose to be usable as is
			local s_SoldierBind = nil
			pcall(function()
				local l_Raw = ResourceManager:SearchForDataContainer(SOLDIER_SKELETON)
				if l_Raw ~= nil then
					local l_Skeleton = SkeletonAsset(l_Raw)
					local l_Root = l_Skeleton.modelPose[SOLDIER_WEAPON_BONE0 + 2]
					local l_Inverse = l_Root:Inverse()
					s_SoldierBind = {}
					for l_Index = 2, 25 do
						local l_Bone = l_Skeleton.modelPose[SOLDIER_WEAPON_BONE0 + l_Index]
						if l_Bone ~= nil then s_SoldierBind[l_Index] = Compose(l_Inverse, l_Bone) end
					end
				end
			end)

			local function tf(t) return t and string.format("(%.3f, %.3f, %.3f) fwd (%.2f, %.2f, %.2f) up (%.2f, %.2f, %.2f)", t.trans.x, t.trans.y, t.trans.z, t.forward.x, t.forward.y, t.forward.z, t.up.x, t.up.y, t.up.z) or "?" end
			p_Report[#p_Report + 1] = string.format("live pose probe: %d weapon bones read from the mannequin's skeleton, relative to its Wep_Root (the hand)", s_Count)

			for _, l_Index in ipairs({ 3, 8, 9, 18, 23 }) do
				local l_Name = "bone " .. l_Index
				for l_BoneName, l_BoneIndex in pairs(BONES) do if l_BoneIndex == l_Index then l_Name = l_BoneName end end
				local l_Bind = (m_BindPose ~= nil and m_BindPose[l_Index]) or (BIND[l_Index] ~= nil and LinearTransform(Vec3(1, 0, 0), Vec3(0, 1, 0), Vec3(0, 0, 1), BIND[l_Index])) or LinearTransform()
				local l_Ebx = nil
				pcall(function() local e = p_State.mesh3pTransforms[l_Index]; l_Ebx = e ~= nil and Compose(l_Bind, e) or l_Bind end)
				p_Report[#p_Report + 1] = string.format("  %s: mannequin %s | EBX bind+delta %s | soldier bind %s", l_Name, tf(s_Pose[l_Index]), tf(l_Ebx), s_SoldierBind and tf(s_SoldierBind[l_Index]) or "?")
			end
		else
			p_Report[#p_Report + 1] = string.format("live pose: only %d weapon bones readable: the EBX deltas stand in", s_Count)
		end
	end)
end

---Reads where the engine put every entity of the mannequin's weapon, relative to the weapon: the socket objects among them are
---where our copies go. Keyed by the entity's data instance guid (a socket object's data is its WeaponSocketObjectData).
local function ReadLiveWeapon(p_Report)
	m_Live = {}
	local s_Weapon = MenuWeaponEntity()

	if s_Weapon == nil then
		p_Report[#p_Report + 1] = "live weapon: none on the mannequin right now"
		return
	end

	pcall(function()
		local s_World = SpatialEntity(s_Weapon).transform
		local s_Inverse = s_World:Inverse()
		local s_Bus = s_Weapon.bus
		local s_Count, s_Spatial = 0, 0

		if s_Bus == nil then
			p_Report[#p_Report + 1] = "live weapon: no entity bus"
			return
		end

		for _, l_Entity in pairs(s_Bus.entities) do
			s_Count = s_Count + 1
			local l_Type = "?"
			local l_DataType = "?"
			local l_Guid = nil
			pcall(function() l_Type = l_Entity.typeInfo.name end)
			pcall(function() l_DataType = l_Entity.data.typeInfo.name; l_Guid = tostring(l_Entity.data.instanceGuid) end)

			if l_Entity:Is("SpatialEntity") then
				s_Spatial = s_Spatial + 1
				local l_Transform = SpatialEntity(l_Entity).transform
				local l_Local = Compose(s_Inverse, l_Transform)

				if l_Guid ~= nil then
					m_Live[l_Guid] = l_Local
				end

				p_Report[#p_Report + 1] = string.format("live weapon entity %s (data %s %s) at (%.3f, %.3f, %.3f) of the weapon", l_Type, l_DataType, tostring(l_Guid),
					l_Local.trans.x, l_Local.trans.y, l_Local.trans.z)
			else
				p_Report[#p_Report + 1] = string.format("live weapon entity %s (data %s): not spatial", l_Type, l_DataType)
			end
		end

		p_Report[#p_Report + 1] = string.format("live weapon: %d entities in its bus, %d spatial, weapon at (%.2f, %.2f, %.2f)", s_Count, s_Spatial,
			s_World.trans.x, s_World.trans.y, s_World.trans.z)
	end)
end

---The skeleton's own bone order, when the asset is resident (it ships with every weapon); the built-in table otherwise.
local function ReadBones()
	if m_BonesRead then
		return
	end

	pcall(function()
		local s_Raw = ResourceManager:SearchForDataContainer(SKELETON)

		if s_Raw == nil then
			return
		end

		local s_Skeleton = SkeletonAsset(s_Raw)
		local s_Names = {}
		local s_Count = 0

		for i, l_Name in pairs(s_Skeleton.boneNames) do
			s_Names[tostring(l_Name)] = i
			s_Count = s_Count + 1
		end

		if s_Count > 0 then
			local s_Same = true
			for l_Name, l_Index in pairs(BONES) do
				if s_Names[l_Name] ~= l_Index then s_Same = false end
			end
			BONES = s_Names
			m_BonesRead = true

			local s_Poses = {}
			local s_PoseCount = 0
			pcall(function()
				for i, l_Pose in pairs(s_Skeleton.modelPose) do
					s_Poses[i] = l_Pose
					s_PoseCount = s_PoseCount + 1
				end
			end)

			if s_PoseCount == s_Count then
				m_BindPose = s_Poses
			end

			LOG(string.format("skeleton %s: %d bones read from the game%s, bind pose %s", SKELETON, s_Count, s_Same and " (as built in)" or " (DIFFERENT from the built-in order: the game's wins)",
				m_BindPose ~= nil and "read" or "built in"))
		end
	end)
end

---Builds the weapon in front of the camera: the base mesh with the camo, then every socket the loadout enables.
local function BuildWeapon()
	DestroyAll()

	local s_Name, s_Raw = nil, nil
	local s_Mounted = {}   -- the pistol's window: the unlocks whose sockets this variant mounts (itself and its Extra)

	if m_Sidearm then
		-- the pistol's window: the pistol it is about, from the kits' tables (the mannequin holds the primary here)
		local s_Extra = 0
		s_Raw, s_Name, s_Extra = SidearmBlueprint(m_SidearmUnlock)
		s_Mounted[m_SidearmUnlock & 0xFFFFFFFF] = true

		if s_Extra ~= nil and s_Extra ~= 0 then
			s_Mounted[s_Extra] = true
		end

		-- "extra 0" on a variant (a Scoped, a Tactical) = the Extra field did not read: the attachment would be missing
		LOG(string.format("the pistol unlock %s mounts its own sockets and its extra %s", tostring(m_SidearmUnlock), tostring(s_Extra)))

		if s_Raw == nil then
			return false, s_Name
		end
	else
		s_Name = MenuWeapon()

		if s_Name == nil then
			return false, "the mannequin holds no weapon yet"
		end

		s_Raw = ResourceManager:SearchForDataContainer(s_Name)

		if s_Raw == nil then
			return false, "weapon blueprint " .. s_Name .. " not resident"
		end
	end

	local s_Data = nil
	local s_Ok, s_Err = pcall(function()
		s_Data = SoldierWeaponData(SoldierWeaponBlueprint(s_Raw).object)
	end)

	if not s_Ok or s_Data == nil then
		return false, "weapon data of " .. s_Name .. ": " .. tostring(s_Err)
	end

	local s_State = s_Data.weaponStates[1]

	if s_State == nil or s_State.mesh3p == nil then
		return false, "weapon " .. s_Name .. " has no 3p mesh in its first state"
	end

	-- ⛔ A PISTOL'S CAMO IS A COPY OF THE PISTOL (SidearmTwins.lua: blueprint "<the game's>/CamoTwin_<KEY>", its meshes the package's
	-- clones "C/<KEY>/…_3p_mesh"), so the tables this view keys by the weapon's name or its mesh's leaf -- the part poses, the drawn
	-- centre and the parked pieces -- are its ORIGINAL's (keku 2026-10-06, run 347: "se ve mal el arma… mal posicionada" -- the
	-- copy fell to the authored box, 0.55 m across, because "_3p_mesh" is in no table)
	local s_TableName = (string.gsub(s_Name, "/CamoTwin_[^/]*$", ""))
	local s_PieceMesh = s_State.mesh3p

	if s_TableName ~= s_Name then
		pcall(function()
			s_PieceMesh = SoldierWeaponData(SoldierWeaponBlueprint(ResourceManager:SearchForDataContainer(s_TableName)).object).weaponStates[1].mesh3p
		end)
		LOG("the pistol " .. s_Name .. " is a camo copy: its poses and pieces are " .. s_TableName .. "'s (" .. PieceKey(s_PieceMesh) .. ")")
	end

	m_WeaponName = s_Name
	-- a pistol's camo is its own unlock (a weapon made with the camo's mesh), never a variation: the primary's camo is not its
	m_CamoHash = m_Sidearm and 0 or CamoHash(s_Data, m_Loadout and m_Loadout.camo or nil)
	ReadBones()
	local s_Report = {}
	ReadLivePose(s_Report, s_State)

	-- the weapon's centre: its origin is at the butt and the barrel runs along +z, so half the farthest socket along z (the muzzle
	-- devices) is close to the middle; an override when the data misleads
	if PIVOT ~= nil then
		m_Pivot = Vec3(PIVOT.x, PIVOT.y, PIVOT.z)
	else
		local s_FarthestZ = 0
		pcall(function()
			for _, l_Entry in pairs(s_State.mesh3pRigidMeshSocketObjectTransforms) do
				if l_Entry.transform.trans.z > s_FarthestZ then
					s_FarthestZ = l_Entry.transform.trans.z
				end
			end
		end)

		if s_FarthestZ < 0.3 then
			s_FarthestZ = 0.8
		end

		m_Pivot = Vec3(0, 0.03, s_FarthestZ * 0.5)
	end

	LOG(string.format("pivot (the weapon's centre) at (%.2f, %.2f, %.2f) of its own space; %.2f m from the camera", m_Pivot.x, m_Pivot.y, m_Pivot.z, m_Distance))

	-- the base pose: the weapon's own EBX deltas (right on every weapon seen for everything but the bipod bones), and for the bipod
	-- bones the game's own pose for what is mounted, when it is baked; the mannequin's bones only behind LIVE_POSE
	local s_Kind, s_KindBy = AnimKind(s_Data)
	m_PartKind = s_Kind
	m_PartPose = nil

	if PART_POSES then
		local s_Table = PartPoses()
		local s_Weapon = s_Table ~= nil and s_Table[string.lower(s_TableName)] or nil
		local s_Wanted = (s_Kind == "bipod" and not BIPOD_AS_SHOWN) and "noaddon" or s_Kind
		local s_Pose = s_Weapon ~= nil and (s_Weapon[s_Wanted] or s_Weapon[s_Kind]) or nil

		if s_Pose ~= nil then
			m_PartPose = s_Pose
			local s_B1 = FreshTransform(s_Pose[18] or (s_State.mesh3pTransforms[18]))
			s_Report[#s_Report + 1] = string.format("part pose: %s (unlock %s) from the baked table: Wep_Bipod1 entry trans (%.3f, %.3f, %.3f) fwd (%.2f, %.2f, %.2f) up (%.2f, %.2f, %.2f)%s",
				s_Kind, tostring(s_KindBy), s_B1.trans.x, s_B1.trans.y, s_B1.trans.z, s_B1.forward.x, s_B1.forward.y, s_B1.forward.z, s_B1.up.x, s_B1.up.y, s_B1.up.z,
				PART_POSES_ON_BASE and " (base mesh too)" or "")
		else
			s_Report[#s_Report + 1] = string.format("part pose: %s (unlock %s) is not baked for %s (%s): the EBX deltas", s_Kind, tostring(s_KindBy), s_Name,
				s_Table == nil and "no module" or (s_Weapon == nil and "weapon not in the table" or "kind not in the table"))
		end
	end

	local s_BasePose = PoseList(s_State, PART_POSES_ON_BASE and m_PartPose or nil)
	local s_AccessoryPose = PoseList(s_State, m_PartPose)

	if m_LiveDelta ~= nil then
		s_AccessoryPose = PoseList(s_State, nil)
		for l_Index = 1, 25 do
			s_AccessoryPose[l_Index] = m_LiveDelta[l_Index] or s_AccessoryPose[l_Index]
		end
	end

	-- ON AN ACCESSORY SCREEN THE WEAPON ITSELF IS NOT BUILT: the screen is about one piece, so only that piece is spawned
	-- (and centred, below). The accessory is the one that row has on, which came over the channel with the loadout.
	local s_Only = nil

	if m_Slot > 0 then
		s_Only = m_Loadout ~= nil and m_Loadout.accessories[m_Slot] or nil

		if s_Only == nil or s_Only == 0 then
			return false, "the accessory row " .. m_Slot .. " has nothing on (nothing to show)"
		end
	end

	-- ⭐ THE PISTOL AS IT IS DRAWN (keku 2026-10-06, a photo: "las armas están desmontadas y la hitbox para girar no está
	-- centrada"). Measured offline (External/keku/sidearm_pieces.py, one bone moved at a time): the EBX deltas assemble every pistol
	-- but park the Taurus' speedloader (Wep_Mag_Ammo) behind its grip, where the game -- whose idle pose sends it a metre back --
	-- never shows it; and a pistol's authored box is the BIND pose's (the Taurus' runs to z 0.43, the drawn pistol to 0.26), so
	-- the box's centre left it turning about a point ~12 cm ahead of it. The baked table gives the drawn centre and size, and the
	-- bones whose piece is parked, which are collapsed here (every axis zero: the piece draws as nothing).
	local s_Baked = nil

	if m_Sidearm then
		local s_Table = SidearmPieces()
		s_Baked = s_Table ~= nil and s_Table[PieceKey(s_PieceMesh)] or nil

		if s_Baked ~= nil then
			for _, l_Bone in ipairs(s_Baked.hide or {}) do
				s_BasePose[l_Bone] = Collapsed(s_BasePose[l_Bone])
			end
		else
			LOG("the pistol mesh " .. PieceKey(s_PieceMesh) .. " is not in the baked table: the box's centre and every piece")
		end
	end

	local s_Identity = LinearTransform()
	local s_Base, s_BaseErr = nil, nil

	if m_Slot == 0 then
		s_Base, s_BaseErr = Spawn("base", s_State.mesh3p, s_Identity, m_CamoHash, s_BasePose, true)

		if s_Base == nil then
			return false, "base mesh: " .. tostring(s_BaseErr)
		end
	end

	-- the pistol turns about its DRAWN centre (the table's), and the box is never asked again (MoveWeapon would move it back)
	if s_Baked ~= nil and s_Base ~= nil then
		m_Pivot = Vec3(s_Baked.centre[1], s_Baked.centre[2], s_Baked.centre[3])
		s_Base.fixedPivot = true
		LOG(string.format("the pistol %s turns about its drawn centre (%.3f, %.3f, %.3f); %d parked piece(s) hidden", s_Name,
			m_Pivot.x, m_Pivot.y, m_Pivot.z, #(s_Baked.hide or {})))
	end

	-- ⭐ THE PISTOL IS THE PIECE: framed by its own size once per pistol, as an accessory window frames its piece -- a camo of
	-- the same pistol (another unlock, the same mesh's leaf) keeps the camera where the player left it. A box that is not
	-- readable yet is measured on the next frames (MoveWeapon).
	if m_Sidearm and s_Base ~= nil then
		local s_Key = PieceKey(s_State.mesh3p)

		if m_FramedFor ~= s_Key then
			m_FramedFor = s_Key
			local s_Radius = s_Baked ~= nil and s_Baked.radius or BoundsRadius(s_Base)

			if s_Radius ~= nil then
				m_Distance = math.max(ACCESSORY_DISTANCE, math.min(2.5, s_Radius * ACCESSORY_FRAME))
				m_ZoomMin, m_ZoomMax = m_Distance * 0.35, m_Distance * 2.5
				LOG(string.format("the pistol %s (unlock %s) measures %.2f m across: framed at %.2f m, zoom %.2f..%.2f", s_Name,
					tostring(m_SidearmUnlock), s_Radius * 2, m_Distance, m_ZoomMin, m_ZoomMax))
			else
				s_Base.frameLater = true
			end
		end
	end

	-- the real centre: the base mesh's bounding box, from the entity the engine just made (a pistol of the table has its own)
	if PIVOT == nil and s_Base ~= nil and not s_Base.fixedPivot then
		local s_Centre = BoundsCentre(s_Base)

		if s_Centre ~= nil then
			m_Pivot = Vec3(s_Centre.x, PIVOT_Y, s_Centre.z)
			LOG(string.format("pivot moved to the base mesh's bounding-box centre (%.2f, %.2f, %.2f) at height %.2f, box %s", s_Centre.x, s_Centre.y, s_Centre.z, PIVOT_Y, tostring(s_Base.box)))
		else
			LOG("the base mesh's bounding box is not readable yet: the socket estimate stays (it is read again on the next frames)")
		end
	end

	local s_Shown, s_Skipped = 0, 0

	for i, l_Socket in pairs(s_Data.sockets) do
		local l_UnlockId = nil
		pcall(function()
			if l_Socket.unlockAsset ~= nil then
				l_UnlockId = UnlockAsset(l_Socket.unlockAsset).identifier
			end
		end)

		local l_Forced = l_Socket.forceSocketEntitiesEnabled or l_Socket.defaultEnableSocketEntities   -- logged, not obeyed
		local l_Enabled = Equipped(l_UnlockId)

		-- an accessory screen shows ITS accessory and nothing else of the weapon
		if m_Slot > 0 then
			l_Enabled = l_UnlockId ~= nil and l_UnlockId == s_Only
		end

		-- the pistol's window shows the pistol as its VARIANT is (what the loadout says is the primary's, not the pistol's): the
		-- sockets of its own unlock and of its Extra -- the Taurus' PK-A, the M1911 Tactical's light and suppressor, the Glock 18's
		-- fire-mode dial
		if m_Sidearm then
			l_Enabled = l_UnlockId ~= nil and s_Mounted[l_UnlockId & 0xFFFFFFFF] == true
		end

		-- the object of the socket that has a 3p mesh -- the first one; on the pistol's window EVERY one, since a pistol's socket
		-- carries the whole attachment in several objects (the PK-A socket: the rail and the sight; the M1911 Tactical's: the light
		-- and the suppressor -- measured in the EBX)
		local l_Objects = {}
		pcall(function()
			for _, l_Candidate in pairs(l_Socket.availableObjects) do
				local l_Socketed = WeaponSocketObjectData(l_Candidate)

				-- (on the pistol's window never a PREFAB: a socket's light is one -- ANPEQ2/Pistol_Laser, Flashlight_3p, the six
				-- PrefabBlueprint objects the accessory catalogue counts -- and cannot be spawned as a mesh. Asked as "not a prefab",
				-- which needs no answer about the meshes' own types)
				local l_IsMesh = true

				if m_Sidearm and l_Socketed ~= nil and l_Socketed.asset3p ~= nil then
					pcall(function() l_IsMesh = not l_Socketed.asset3p:Is("PrefabBlueprint") end)
				end

				if l_Socketed ~= nil and l_Socketed.asset3p ~= nil and l_IsMesh then
					l_Objects[#l_Objects + 1] = { l_Socketed, l_Socketed.asset3p }

					if not m_Sidearm then
						break
					end
				end
			end
		end)

		if #l_Objects == 0 then
			l_Objects[1] = { nil, nil }
		end

		for _, l_Pair in ipairs(l_Objects) do
			local l_Object, l_Mesh = l_Pair[1], l_Pair[2]

			local l_Line = string.format("socket %d %s unlock=%s bone=%s slot=%s %s%s", i, l_Mesh ~= nil and NameOf(l_Mesh) or "(no 3p mesh)",
				tostring(l_UnlockId), tostring(l_Socket.boneName), tostring(l_Socket.gearSlot), l_Forced and "forced " or "", l_Enabled and "SHOWN" or "hidden")
			s_Report[#s_Report + 1] = l_Line

			if l_Enabled and l_Mesh ~= nil then
				-- its place on the weapon: the state's 3p transform for this socket object, else the object's own, else the socket's
				local l_Local, l_Source = nil, "none"
				pcall(function()
					for _, l_Entry in pairs(s_State.mesh3pRigidMeshSocketObjectTransforms) do
						if l_Entry.socketObject ~= nil and l_Entry.socketObject.instanceGuid == l_Object.instanceGuid then
							l_Local = l_Entry.transform
							l_Source = "state3p"
							break
						end
					end
				end)

				if l_Local == nil then
					pcall(function() l_Local = WeaponRegularSocketObjectData(l_Object).transform; l_Source = "object" end)
				end

				if l_Local == nil then
					l_Local = l_Socket.transform
					l_Source = "socket"
				end

				s_Report[#s_Report + 1] = string.format("  placed by %s at (%.3f, %.3f, %.3f) fwd (%.2f, %.2f, %.2f)", l_Source,
					l_Local.trans.x, l_Local.trans.y, l_Local.trans.z, l_Local.forward.x, l_Local.forward.y, l_Local.forward.z)

				-- where the engine put this very socket object on the mannequin's weapon, when it is there
				local l_LiveLocal = nil
				pcall(function() l_LiveLocal = m_Live[tostring(l_Object.instanceGuid)] end)

				-- the socket hangs from a bone: its place is relative to that bone's base pose (the M240's optics ride Wep_Extra1, the rail)
				local l_BoneName = tostring(l_Socket.boneName or "")
				local l_BoneIndex = BONES[l_BoneName]

				if l_LiveLocal ~= nil then
					l_Local = l_LiveLocal
					s_Report[#s_Report + 1] = string.format("  placed as on the mannequin's weapon: (%.3f, %.3f, %.3f)", l_Local.trans.x, l_Local.trans.y, l_Local.trans.z)
				elseif l_BoneName ~= "" and l_BoneIndex ~= nil and m_LivePose ~= nil and m_LivePose[l_BoneIndex] ~= nil then
					local l_BonePose = m_LivePose[l_BoneIndex]
					l_Local = Compose(l_BonePose, l_Local)
					s_Report[#s_Report + 1] = string.format("  through bone %s (index %d) as the mannequin animates it: (%.3f, %.3f, %.3f)", l_BoneName, l_BoneIndex,
						l_BonePose.trans.x, l_BonePose.trans.y, l_BonePose.trans.z)
				elseif l_BoneName ~= "" and l_BoneIndex ~= nil then
					-- the bone's pose on this weapon: the skeleton's bind pose, then the weapon's own delta for that bone
					local l_Bind = nil
					if m_BindPose ~= nil then
						l_Bind = m_BindPose[l_BoneIndex]
					elseif BIND[l_BoneIndex] ~= nil then
						l_Bind = LinearTransform(Vec3(1, 0, 0), Vec3(0, 1, 0), Vec3(0, 0, 1), BIND[l_BoneIndex])
					else
						l_Bind = LinearTransform()
					end

					local l_Delta = nil
					pcall(function() l_Delta = s_State.mesh3pTransforms[l_BoneIndex] end)

					local l_BonePose = l_Delta ~= nil and Compose(l_Bind, l_Delta) or l_Bind
					l_Local = Compose(l_BonePose, l_Local)
					s_Report[#s_Report + 1] = string.format("  through bone %s (index %d): bind (%.3f, %.3f, %.3f) + delta %s = (%.3f, %.3f, %.3f)", l_BoneName, l_BoneIndex,
						l_Bind.trans.x, l_Bind.trans.y, l_Bind.trans.z, l_Delta ~= nil and string.format("(%.3f, %.3f, %.3f)", l_Delta.trans.x, l_Delta.trans.y, l_Delta.trans.z) or "none",
						l_BonePose.trans.x, l_BonePose.trans.y, l_BonePose.trans.z)
				elseif l_BoneName ~= "" then
					s_Report[#s_Report + 1] = "  bone " .. l_BoneName .. " is not in the skeleton's list: placed from the root"
				end

				-- a skinned socket object (bipods, foregrips) is skinned to the weapon's skeleton: it gets the weapon's bone poses and sits
				-- at the origin (its skin places it); a rigid one carries its own base pose only when its data has one
				local l_Pose = nil
				local l_Skinned = false
				pcall(function() l_Skinned = l_Mesh:Is("SkinnedMeshAsset") end)

				if l_Skinned then
					l_Pose = s_AccessoryPose
					l_Local = LinearTransform()
					s_Report[#s_Report + 1] = "  skinned to the weapon's skeleton: " .. (m_LiveDelta ~= nil and "the EBX deltas with the mannequin's bipod bones" or (m_PartPose ~= nil and ("the EBX deltas with the game's " .. s_Kind .. " pose on the bipod bones") or "the weapon's EBX deltas")) .. ", at the origin"
				else
					pcall(function()
						local l_Transforms = WeaponRegularSocketObjectData(l_Object).mesh3pTransforms

						if l_Transforms ~= nil and #l_Transforms > 0 then
							l_Pose = l_Transforms
						end
					end)
				end

				local l_CamoHash = 0
				if ACCESSORY_CAMO then
					l_CamoHash = m_CamoHash
				elseif ACCESSORY_CAMO_MESHES ~= nil then
					local l_MeshName = string.lower(NameOf(l_Mesh))
					for _, l_Needle in ipairs(ACCESSORY_CAMO_MESHES) do
						if string.find(l_MeshName, l_Needle, 1, true) ~= nil then
							l_CamoHash = m_CamoHash
						end
					end
				end

				if l_CamoHash ~= 0 then
					s_Report[#s_Report + 1] = string.format("  spawned WITH the camo variation %d (a probe: camouflaged = its database entries are in; nothing = they are not)", l_CamoHash)
				end

				local l_Part, l_Err = Spawn("socket " .. i, l_Mesh, l_Local, l_CamoHash, l_Pose, false)

				-- the piece alone: the view turns about IT, not about the middle of a weapon that is not there
				-- ⛔ AND IT IS FRAMED ONCE PER PIECE, NOT ONCE PER REBUILD (keku, 2026-09-20: *"cuando cambio de skin
				-- en la ventana UI no se debe reiniciar la posicion de la camara o zoom, debe quedarse como estaba
				-- antes del cambio"*). Picking a camo rebuilds the piece -- the twin is another unlock, with another
				-- mesh -- and the framing that runs here threw away wherever the player had turned and zoomed it.
				-- The key is the mesh's LEAF NAME, which a camo twin shares with the plain piece (the clone is the
				-- same object under another path), so a different ACCESSORY still reframes and a different CAMO of
				-- the same one does not.
				-- the light a device throws is drawn, but it is not the device: a beam metres long would own the box
				if l_Part ~= nil and m_Slot > 0 and not IsLightMesh(PieceKey(l_Mesh)) then
					-- what this window is ABOUT, and whether its geometry can be measured (see MoveWeapon) -- always,
					-- because the rebuilt part has to be recognised whether or not the camera moves for it
					l_Part.piece = true
					l_Part.skinned = l_Skinned

					local l_Key = PieceKey(l_Mesh)
					l_Part.key = l_Key

					-- a camo of the SAME piece keeps the camera; another piece earns a new frame
					l_Part.framed = m_FramedFor == l_Key
					l_Part.centred = m_FramedFor == l_Key

					-- ...and keeping the camera includes THE POINT IT LOOKS AT: this rebuild has already reset the pivot
					-- to the weapon's centre, and the block below -- the one that would put it back on the piece -- is
					-- exactly what "framed once" skips. Without this the piece climbs out of the middle of the window on
					-- the first camo and never comes back.
					if l_Part.centred and m_FramedPivot ~= nil then
						m_Pivot = m_FramedPivot
						s_Report[#s_Report + 1] = string.format(
							"  the same piece rebuilt (a camo change): the view keeps its pivot (%.3f, %.3f, %.3f)",
							m_Pivot.x, m_Pivot.y, m_Pivot.z)
					end
				end

				if l_Part ~= nil and m_Slot > 0 and not IsLightMesh(PieceKey(l_Mesh)) and
					m_FramedFor ~= PieceKey(l_Mesh) then
					m_FramedFor = PieceKey(l_Mesh)
					m_Pivot = AnchorOf(l_Mesh, l_Skinned, l_Local, l_Pose, s_Report)
					m_FramedPivot = m_Pivot                    -- what a later rebuild of this same piece restores
					s_Report[#s_Report + 1] = string.format("  the screen's own accessory: the view centres on (%.3f, %.3f, %.3f)",
						m_Pivot.x, m_Pivot.y, m_Pivot.z)

					-- ⛔ THE DISTANCE IS THE PIECE'S, NOT A CONSTANT (keku 2026-09-19: *"aparecen mas cerca de lo normal y hasta
					-- que no pulso shift no se colocan a la posicion que deberia ser"*). Every window opened at 0.55 m, tuned on a
					-- scope, so a bipod or a suppressor filled the screen; and 0.55 was OUTSIDE the zoom's own range (1.0..3.5), so
					-- the first zoom input clamped the distance and the piece JUMPED -- that jump was the "shift puts it right".
					-- Now the piece is measured and framed by its own size, and the zoom range is built around that distance, so
					-- nothing moves until the player moves it.
					local l_Radius = BoundsRadius(l_Part)

					if l_Radius ~= nil then
						l_Part.framed = true
						m_Distance = math.max(ACCESSORY_DISTANCE, math.min(2.5, l_Radius * ACCESSORY_FRAME))
						m_ZoomMin, m_ZoomMax = m_Distance * 0.35, m_Distance * 2.5
						s_Report[#s_Report + 1] = string.format("  it measures %.2f m across: framed at %.2f m, zoom %.2f..%.2f",
							l_Radius * 2, m_Distance, m_ZoomMin, m_ZoomMax)
					end
				end

				if l_Part ~= nil then
					s_Shown = s_Shown + 1
					l_Part.skinned = l_Skinned
					l_Part.name = "socket " .. i .. " " .. NameOf(l_Mesh)
				else
					s_Report[#s_Report + 1] = "  FAILED: " .. tostring(l_Err)
				end
			elseif l_Enabled then
				s_Skipped = s_Skipped + 1
			end
		end
	end

	LOG(string.format("weapon built: %s camo hash %d, %d socket mesh(es) shown, %d enabled without a 3p mesh, %d socket(s) in the data",
		s_Name, m_CamoHash, s_Shown, s_Skipped, #s_Report))

	-- ⛔⛔ THE REPORT IS WRITTEN ONCE PER WEAPON, NOT ONCE PER BUILD (keku's boot 2026-09-19: *"al estar mucho rato por el
	-- menú probando camos me ha acabado echando del servidor -- connection with the server timed out"*). Every one of these
	-- lines is a NetEvent from his client to the server: the view rebuilds on every pick, every arrow and every screen, and
	-- 40 builds x ~30 sockets were 2178 rows in one session. What the lines say does not change between builds of the same
	-- weapon (the sockets are the weapon's), so the first build of each weapon says it and the rest are silent; a build that
	-- FAILED always speaks. The budget of a probe is bytes x rate, and this one had no rate. [[keku-instrumentation-cost-budget]]
	local s_Key = tostring(s_Name) .. "|" .. tostring(m_Slot)

	if not m_ReportDone[s_Key] then
		m_ReportDone[s_Key] = true

		for _, l_Line in ipairs(s_Report) do
			LOG("  " .. l_Line)
		end
	else
		for _, l_Line in ipairs(s_Report) do
			if string.find(l_Line, "FAILED", 1, true) ~= nil then
				LOG("  " .. l_Line)
			end
		end
	end

	return true, nil
end

---The camo changed: only the base mesh is rebuilt, with the new variation.
local function RebuildBase()
	local s_Base = nil

	for _, l_Part in ipairs(m_Parts) do
		if l_Part.isBase and l_Part.entity ~= nil then
			s_Base = l_Part
		end
	end

	if s_Base == nil then
		BuildWeapon()
		return
	end

	local s_Raw = m_WeaponName and ResourceManager:SearchForDataContainer(m_WeaponName) or nil

	if s_Raw == nil then
		return
	end

	pcall(function()
		local s_Data = SoldierWeaponData(SoldierWeaponBlueprint(s_Raw).object)
		local s_State = s_Data.weaponStates[1]
		local s_Hash = CamoHash(s_Data, m_Loadout and m_Loadout.camo or nil)

		if s_Hash == m_CamoHash then
			return
		end

		Destroy(s_Base)
		m_CamoHash = s_Hash

		local s_New, s_Err = Spawn("base", s_State.mesh3p, LinearTransform(), m_CamoHash, PoseList(s_State, PART_POSES_ON_BASE and m_PartPose or nil), true)
		LOG(string.format("camo %s -> variation hash %d: base mesh %s", tostring(m_Loadout and m_Loadout.camo), m_CamoHash, s_New ~= nil and "rebuilt" or ("FAILED: " .. tostring(s_Err))))
	end)
end

local function MoveWeapon()
	local s_Root = RootTransform()

	if s_Root == nil then
		return
	end

	-- the pivot from the box, when the base was spawned before the engine had one (an entity's aabb is its asset's authored box,
	-- not the drawn pose: it cannot measure where an accessory landed)
	-- ⛔ THE WINDOW'S OWN PIECE IS CENTRED ON ITS GEOMETRY, NOT ON WHERE IT HANGS (keku 2026-09-20, with a
	-- picture: "hay algunas que salen centradas y otras que se quedan muy separadas, quizá porque siguen el
	-- bounding box del arma en si, no el suyo propio"). A rigid socket object is framed on its local transform
	-- -- the point of the weapon it is bolted to. The shared attachments are authored around that point, so
	-- they look centred; a weapon's OWN iron sights are authored in the WEAPON's space, geometry included, so
	-- their transform is the origin and the piece sits a hand's width away from the middle of the view.
	-- The box answers that, once the engine has one. Skinned pieces keep their bones: their box is the
	-- authored one and says nothing about the pose they are drawn in.
	for _, l_Part in ipairs(m_Parts) do
		if l_Part.piece and not l_Part.skinned and not l_Part.centred then
			-- 5 mm: a part the engine has a box for is worth centring on, however small it is
			local l_Centre = BoundsCentre(l_Part, 0.005)

			-- ⛔ AND IT SAYS SO EITHER WAY. Without this the failure was silent for a whole boot: the box was
			-- not measurable, nothing moved, and the picture looked exactly like a fix that had not shipped.
			if l_Centre == nil then
				l_Part.tries = (l_Part.tries or 0) + 1

				if l_Part.tries == 120 then
					LOG("the piece has no usable box after 2 seconds: the view stays on where it hangs -- " ..
						tostring(l_Part.name))
				end
			end

			if l_Centre ~= nil then
				local l_Moved = math.sqrt((l_Centre.x - m_Pivot.x) ^ 2 + (l_Centre.y - m_Pivot.y) ^ 2 +
					(l_Centre.z - m_Pivot.z) ^ 2)

				l_Part.centred = true
				LOG(string.format("the piece's own centre is (%.3f, %.3f, %.3f), %.0f cm from where it hangs (%.3f, %.3f, %.3f) -- the view moves to it, box %s",
					l_Centre.x, l_Centre.y, l_Centre.z, l_Moved * 100, m_Pivot.x, m_Pivot.y, m_Pivot.z, tostring(l_Part.box)))
				m_Pivot = l_Centre
				m_FramedPivot = l_Centre                   -- the refined one: this is what a camo change restores

				-- and the distance, if the box was not there to measure it when the piece spawned
				if not l_Part.framed then
					local l_Radius = BoundsRadius(l_Part)

					if l_Radius ~= nil then
						l_Part.framed = true
						m_Distance = math.max(ACCESSORY_DISTANCE, math.min(2.5, l_Radius * ACCESSORY_FRAME))
						m_ZoomMin, m_ZoomMax = m_Distance * 0.35, m_Distance * 2.5
						LOG(string.format("it measures %.2f m across: framed at %.2f m, zoom %.2f..%.2f",
							l_Radius * 2, m_Distance, m_ZoomMin, m_ZoomMax))
					end
				end
			end
		end
	end

	for _, l_Part in ipairs(m_Parts) do
		if l_Part.entity ~= nil and l_Part.box == nil and l_Part.isBase and PIVOT == nil and not l_Part.fixedPivot then
			local l_Centre = BoundsCentre(l_Part)

			if l_Centre ~= nil then
				m_Pivot = Vec3(l_Centre.x, PIVOT_Y, l_Centre.z)
				LOG(string.format("pivot moved to the base mesh's bounding-box centre (%.2f, %.2f, %.2f) at height %.2f, box %s", l_Centre.x, l_Centre.y, l_Centre.z, PIVOT_Y, tostring(l_Part.box)))
			end
		end

		-- the pistol's window: its distance, once the box the spawn could not read is there
		if l_Part.entity ~= nil and l_Part.isBase and l_Part.frameLater then
			local l_Radius = BoundsRadius(l_Part)

			if l_Radius ~= nil then
				l_Part.frameLater = false
				m_Distance = math.max(ACCESSORY_DISTANCE, math.min(2.5, l_Radius * ACCESSORY_FRAME))
				m_ZoomMin, m_ZoomMax = m_Distance * 0.35, m_Distance * 2.5
				LOG(string.format("the pistol measures %.2f m across: framed at %.2f m, zoom %.2f..%.2f", l_Radius * 2, m_Distance, m_ZoomMin, m_ZoomMax))
			end
		end
	end

	for _, l_Part in ipairs(m_Parts) do
		if l_Part.entity ~= nil then
			pcall(function()
				local l_Spatial = SpatialEntity(l_Part.entity)
				l_Spatial.transform = Compose(s_Root, Pivoted(l_Part.raw))

				if MOVE_TOGGLE then
					l_Spatial:FireEvent("Disable")
					l_Spatial:FireEvent("Enable")
				end
			end)
		end
	end
end

-- ---- the frame -------------------------------------------------------------------------------------------------------------------
local function Dragging()
	return m_AsButton or m_ImButton
end

local function OnUpdateInput(p_Delta)
	if not m_Active then
		return
	end

	local s_Cursor = InputManager:GetCursorPosition()

	-- the probe: does InputManager see the button in the menus? (right half only, so a click on the panel is not a turn)
	local s_Down = false
	pcall(function()
		s_Down = InputManager:IsMouseButtonDown(DRAG_BUTTON)
	end)

	if s_Down and not m_ImButton then
		local s_Size = ClientUtils:GetWindowSize()
		local s_Right = s_Size == nil or s_Cursor.x >= s_Size.x * DRAG_LEFT
		m_Sources.imAny = m_Sources.imAny + 1

		if m_Sources.imAny == 1 then
			LOG(string.format("InputManager saw a press at cursor (%.0f, %.0f), window %s", s_Cursor.x, s_Cursor.y, s_Size == nil and "nil" or string.format("%.0f x %.0f", s_Size.x, s_Size.y)))
		end

		if s_Right then
			m_ImButton = true
			m_Sources.im = m_Sources.im + 1
			if m_Sources.im == 1 then
				LOG("press seen by InputManager:IsMouseButtonDown (the probe) -- the channel is not needed")
			end
		end
	elseif not s_Down and m_ImButton then
		m_ImButton = false
	end

	-- the hold that turns mouse movement into zoom: the key
	m_ZoomHold = InputManager:IsKeyDown(ZOOM_HOLD_KEY)

	if m_ZoomHold and m_LastCursor ~= nil then
		-- forward (the cursor going up the screen) = closer
		m_Distance = math.max(m_ZoomMin, math.min(m_ZoomMax, m_Distance + (s_Cursor.y - m_LastCursor.y) * ZOOM_SENS))
		m_Pending = m_Pending * INERTIA
		m_PendingPitch = m_PendingPitch * INERTIA
	elseif Dragging() and m_LastCursor ~= nil then
		m_Pending = (s_Cursor.x - m_LastCursor.x) * ROT_SENS
		m_PendingPitch = (s_Cursor.y - m_LastCursor.y) * ROT_SENS
	else
		m_Pending = m_Pending * INERTIA
		m_PendingPitch = m_PendingPitch * INERTIA
	end

	m_LastCursor = s_Cursor

	if m_Pending ~= 0.0 then
		m_Yaw = m_Yaw + m_Pending
		while m_Yaw > math.pi do m_Yaw = m_Yaw - 2.0 * math.pi end
		while m_Yaw < -math.pi do m_Yaw = m_Yaw + 2.0 * math.pi end
	end

	if m_PendingPitch ~= 0.0 then
		local s_Limit = math.rad(PITCH_LIMIT_DEG)
		m_Pitch = math.max(-s_Limit, math.min(s_Limit, m_Pitch + m_PendingPitch))
	end

	-- the zoom: the keys, and the wheel once its axis is known (the probe below finds it)
	local s_Zoom = 0
	if InputManager:WentKeyDown(InputDeviceKeys.IDK_PageUp) then s_Zoom = -1 end
	if InputManager:WentKeyDown(InputDeviceKeys.IDK_PageDown) then s_Zoom = 1 end

	if WHEEL_CONCEPT ~= nil then
		pcall(function()
			local l_Level = InputManager:GetLevel(WHEEL_CONCEPT)
			if l_Level > 0 then s_Zoom = -1 elseif l_Level < 0 then s_Zoom = 1 end
		end)
	else
		-- the probe: does any menu/zoom concept move with the wheel? (first move of each, once)
		pcall(function()
			for l_Name, l_Concept in pairs({ MenuZoomIn = InputConceptIdentifiers.ConceptMenuZoomIn, MenuZoomOut = InputConceptIdentifiers.ConceptMenuZoomOut,
				MapZoom = InputConceptIdentifiers.ConceptMapZoom, MapInnerZoom = InputConceptIdentifiers.ConceptMapInnerZoom, Zoom = InputConceptIdentifiers.ConceptZoom,
				FreeCameraSwitchSpeed = InputConceptIdentifiers.ConceptFreeCameraSwitchSpeed, FreeCameraIncreaseSpeed = InputConceptIdentifiers.ConceptFreeCameraIncreaseSpeed,
				FreeCameraDecreaseSpeed = InputConceptIdentifiers.ConceptFreeCameraDecreaseSpeed, NextPosition = InputConceptIdentifiers.ConceptNextPosition,
				Fire = InputConceptIdentifiers.ConceptFire, ZoomAim = InputConceptIdentifiers.ConceptZoom, Yaw = InputConceptIdentifiers.ConceptYaw, Pitch = InputConceptIdentifiers.ConceptPitch }) do
				local l_Level = InputManager:GetLevel(l_Concept)

				if l_Level ~= 0 and not m_ConceptSeen[l_Name] then
					m_ConceptSeen[l_Name] = true
					LOG(string.format("input concept %s moved (%.3f) -- the engine still reports it in the menus", l_Name, l_Level))
				end
			end
		end)
	end

	if WHEEL_AXIS ~= nil then
		pcall(function()
			local l_Level = InputManager:GetMouseLevel(WHEEL_AXIS)
			if l_Level > 0 then s_Zoom = -1 elseif l_Level < 0 then s_Zoom = 1 end
		end)
	else
		-- the probe: which axis does the wheel move? the first move of each axis is logged once (no axis is the mouse's own x/y
		-- unless it moves with the cursor, which the log will show)
		pcall(function()
			for l_Name, l_Axis in pairs({ Axis0X = InputDeviceAxes.IDA_Axis0X, Axis0Y = InputDeviceAxes.IDA_Axis0Y, Axis1X = InputDeviceAxes.IDA_Axis1X,
				Axis1Y = InputDeviceAxes.IDA_Axis1Y, Axis2X = InputDeviceAxes.IDA_Axis2X, Axis2Y = InputDeviceAxes.IDA_Axis2Y }) do
				local l_Level = InputManager:GetMouseLevel(l_Axis)

				if l_Level ~= 0 and not m_AxisSeen[l_Name] then
					m_AxisSeen[l_Name] = true
					LOG(string.format("mouse axis %s moved (%.3f) -- if that was the wheel, it is the zoom's axis", l_Name, l_Level))
				end
			end
		end)
	end

	if s_Zoom ~= 0 then
		m_Distance = math.max(m_ZoomMin, math.min(m_ZoomMax, m_Distance + s_Zoom * ZOOM_STEP))
	end

	if PROP then
		if #m_Parts == 0 and (m_Frame ~= nil or not CALIBRATE) then
			local s_Now = SharedUtils:GetTimeMS()

			if s_Now >= m_RetryAt then
				m_RetryAt = s_Now + RETRY_S * 1000
				local s_Ok, s_Why = BuildWeapon()

				if not s_Ok then
					LOG("weapon not built yet: " .. tostring(s_Why) .. " (retrying)")
				end
			end
		else
			MoveWeapon()
		end
	end

	PlaceMannequin()

	if InputManager:WentKeyDown(InputDeviceKeys.IDK_F8) then
		LOG(State())
	end
end

State = function()
	Comp()
	local s_Def = m_Default
	local s_Alive = 0

	for _, l_Part in ipairs(m_Parts) do
		if l_Part.entity ~= nil then
			s_Alive = s_Alive + 1
		end
	end

	local s_Codes = {}
	for l_Code, _ in pairs(m_ButtonCodes) do s_Codes[#s_Codes + 1] = tostring(l_Code) end

	return string.format("active=%s prop=%s weapon=%s camo=%s hash=%d parts=%d yaw=%.0f pitch=%.0f deg distance=%.2f pivot=(%.2f, %.2f, %.2f) at=(%.2f, %.2f) loadout=%s game=(%.2f, %.2f, %.2f | yaw %.2f) drag as=%s im=%s presses(as=%d im=%d imAny=%d) buttons=[%s] zoomHold=%s frame=%s",
		tostring(m_Active), tostring(PROP), tostring(m_WeaponName), tostring(m_Loadout and m_Loadout.camo), m_CamoHash, s_Alive, math.deg(m_Yaw), math.deg(m_Pitch), m_Distance,
		m_Pivot.x, m_Pivot.y, m_Pivot.z, PROP_AT.x, PROP_AT.y, m_Loadout and (tostring(m_Loadout.weapon) .. "/" .. table.concat(m_Loadout.accessories, ",") .. "/" .. tostring(m_Loadout.camo)) or "none",
		s_Def and s_Def.soldierOffset.x or 0, s_Def and s_Def.soldierOffset.y or 0, s_Def and s_Def.soldierOffset.z or 0, s_Def and s_Def.soldierRotation.y or 0,
		tostring(m_AsButton), tostring(m_ImButton), m_Sources.as, m_Sources.im, m_Sources.imAny, table.concat(s_Codes, ","), tostring(m_ZoomHold), Describe(m_Frame))
end

local function Enter()
	if m_Active or not ENABLED then
		return
	end

	if Comp() == nil then
		return
	end

	m_Active = true
	m_Yaw = PROP and math.rad(PROP_YAW_START_DEG) or ((m_Default ~= nil and m_Default.soldierRotation.y or -0.45) + math.rad(MANNEQUIN_YAW_START_DEG))
	m_Pitch = math.rad(PROP_PITCH_START_DEG)
	m_Pending = 0.0
	m_PendingPitch = 0.0
	-- a scope (or a pistol) fills the view from much closer than a rifle does
	local s_Piece = m_Slot > 0 or m_Sidearm
	m_Distance = s_Piece and ACCESSORY_DISTANCE or -PROP_AT.z
	m_ZoomMin, m_ZoomMax = s_Piece and ACCESSORY_DISTANCE * 0.35 or ZOOM_MIN, s_Piece and ACCESSORY_DISTANCE * 2.5 or ZOOM_MAX
	m_LastCursor = nil
	m_AsButton = false
	m_ImButton = false
	m_RetryAt = 0
	m_Frame = nil
	m_Calib = nil
	-- a window that OPENS frames its piece; only a camo change inside it leaves the camera alone
	m_FramedFor = nil
	m_FramedPivot = nil
	PlaceMannequin()

	if PROP and not CALIBRATE then
		local s_Ok, s_Why = BuildWeapon()

		if not s_Ok then
			LOG("weapon not built on entering: " .. tostring(s_Why) .. " (retrying each " .. RETRY_S .. " s)")
		end
	end

	if m_Update == nil then
		m_Update = Events:Subscribe("Client:UpdateInput", OnUpdateInput)
	end

	LOG("weapon view ON: " .. State())
end

local function Leave(p_Why)
	if not m_Active then
		return
	end

	m_Active = false

	if m_Update ~= nil then
		m_Update:Unsubscribe()
		m_Update = nil
	end

	DestroyAll()
	RestoreMannequin()
	m_Frame = nil
	m_Calib = nil
	LOG("weapon view OFF (" .. tostring(p_Why) .. "): weapon gone, mannequin back to the game's place")
end

-- ---- THE MAILBOX: the camo table, handed to the screens AT RUN TIME ----------------------------------------------------------
-- ⭐ WHY (keku, 2026-09-20): the table is compiled into the movie today, so a camo baked afterwards does not exist for
-- the menu until the menu is rebuilt -- and the plan is the opposite (the studio drops a package in a folder and the
-- framework detects it). The framework already builds the table every level; this puts it where the screen can read
-- it, through the engine's own path: the `CamoTableSet` node of each screen writes it into our data key when the
-- screen is entered, and the grid's binding delivers it to the script.
-- ⛔ The carrier's way -- a description -- cannot do this: one the mod ADDS is not in the UI's item index (measured,
-- and it is why the table went into the movie in the first place).
local m_MailText = nil        -- the table as the framework built it this level
local m_MailNodes = {}        -- the writer nodes found THIS LEVEL (dropped at Level:Destroy, see there)
local m_MailSaid = false
local m_MailScans = 0         -- scans reported, capped: the reading is worth a few lines, not one per screen push

---Writes the table into a writer node. Says what it did the first time, and never again: this runs per screen.
local function MailWrite(p_Node, p_Where)
	if m_MailText == nil or p_Node == nil then
		return false
	end

	local s_Ok = pcall(function()
		p_Node:MakeWritable()
		p_Node.param = m_MailText
	end)

	if not m_MailSaid then
		m_MailSaid = true
		LOG("the mailbox: the table (" .. string.len(m_MailText) .. " characters) " ..
			(s_Ok and "written into" or "could NOT be written into") .. " the screen's writer node (" ..
			tostring(p_Where) .. ")")
	end

	return s_Ok
end

---Every writer node of a screen graph, cast and remembered.
---⛔ THE CAST IS THE SCREEN'S OWN TYPE, NOT `UIGraphAsset` (boot 45, and it cost the whole reading): `nodes` is
---declared on `UIScreenAsset`, and reading a field the cast type does not declare returns **nil in silence**, so
---`#s_Graph.nodes` threw inside the pcall and the scan vanished without a word. The value that did arrive at the
---script was `EnterScreen` -- 11 characters, the event's own payload, which is what a DataSetNode writes when its
---`Param` is empty -- i.e. the ROUTE worked and only the content was missing.
---⛔ And it SAYS what it found, every time: a scan that only speaks on success cannot be told from one that never ran.
local function MailScan(p_Graph, p_Where)
	local s_Ok = pcall(function()
		local s_Graph = _G[tostring(p_Graph.typeInfo.name)](p_Graph)
		local s_Name = "?"
		pcall(function() s_Name = tostring(s_Graph.name) end)
		local s_Found = 0

		for i = 1, #s_Graph.nodes do
			-- ⛔⛔ CAST FIRST, THEN READ THE NAME. Third time today the same law: a field the cast type does not
			-- declare reads nil IN SILENCE, so asking a raw DataContainer for `instanceName` compares "nil" against
			-- the name forever (boot 46: "writer node not here" on a screen whose 19 nodes included it). The
			-- generated code does exactly this, and I did not copy it.
			local l_Node = nil
			pcall(function() l_Node = _G[tostring(s_Graph.nodes[i].typeInfo.name)](s_Graph.nodes[i]) end)

			local l_Name = nil
			pcall(function() l_Name = tostring(l_Node.instanceName) end)

			if l_Name == nil or l_Name == "" or l_Name == "nil" then
				pcall(function() l_Name = tostring(l_Node.name) end)
			end

			if l_Name == "CamoTableSet" and l_Node ~= nil then
				s_Found = s_Found + 1
				m_MailNodes[#m_MailNodes + 1] = l_Node
				MailWrite(l_Node, p_Where)
			end
		end

		-- a scan that FINDS always speaks; the empty ones are capped so the log does not fill with screens
		if s_Found > 0 or m_MailScans < 4 then
			m_MailScans = m_MailScans + 1
			LOG("the mailbox: scanned " .. s_Name .. " (" .. #s_Graph.nodes .. " nodes) -- writer node " ..
				(s_Found > 0 and ("FOUND x" .. s_Found) or "not here") .. ", table " ..
				(m_MailText == nil and "not known yet" or (string.len(m_MailText) .. " characters")))
		end
	end)

	if not s_Ok and m_MailScans < 6 then
		m_MailScans = m_MailScans + 1
		LOG("the mailbox: the scan FAILED on " .. tostring(p_Where) .. " -- nothing was written")
	end
end

-- ⛔ AND EARLY, NOT ONLY ON THE PUSH: the graph is INSTANTIATED BEFORE `UI:PushScreen` reaches a hook (measured
-- 2026-09-15 with the crash that had no dump), so a param written at push time is already too late for that first
-- entry -- the writer would fire once with nothing. The screen's asset is caught as its partition loads instead,
-- which is well before anyone enters it; the push scan stays as the second chance.
Events:Subscribe("Partition:Loaded", function(p_Partition)
	-- ⛔ THIS CAP GATES THE SCAN, NOT JUST THE LOG: a scan that FINDS always counts, so the ceiling has to clear
	-- every writer there is (five since 2026-09-21: the four camo screens and the rows screen's own mailbox) plus
	-- the four empty scans that are allowed to speak. At 8 the fifth screen could fall outside it and be left to
	-- the push, which is too late for the first entry -- the writer would fire with the event's payload instead.
	-- ⛔ 2026-10-06: NINE writers now (the camo screen, three accessory windows, the rows screen, the pistol's and the two
	-- gadget windows -- eight FOUND in run 341 -- and the loadout's own mailbox), so 9 + 4 = 13 would already pass 12.
	if m_MailScans > 20 then
		return
	end

	for _, l_Instance in pairs(p_Partition.instances) do
		local s_Is = false
		pcall(function() s_Is = l_Instance:Is("UIScreenAsset") end)

		if s_Is then
			MailScan(l_Instance, "as its partition loaded")
		end
	end
end)

-- The framework says what the table is for this level (the event crosses mods and never leaves the client).
Events:Subscribe("WeaponCamo:Table", function(p_Text)
	if type(p_Text) ~= "string" then
		return
	end

	m_MailText = p_Text

	-- whatever was already found gets it now; the rest get it as their screen turns up
	for _, l_Node in ipairs(m_MailNodes) do
		MailWrite(l_Node, "the table arrived")
	end
end)

-- ---- the gate: the screen the game pushes -----------------------------------------------------------------------------------
Hooks:Install("UI:PushScreen", 999, function(p_Hook, p_Screen, p_Priority, p_ParentGraph)
	-- the screen on its way in: if it carries the writer node, it leaves with the table in it
	MailScan(p_Screen, "on push")
	local s_Name = ""

	pcall(function()
		s_Name = string.lower(tostring(UIGraphAsset(p_Screen).name))
	end)

	-- the pistol's (or the crossbow's) window: that weapon alone (which one, its script says as its data comes: "WVAS,<unlock>")
	for _, l_Screen in ipairs(SIDEARM_SCREENS) do
		if string.find(s_Name, l_Screen, 1, true) ~= nil then
			m_Slot = 0
			m_Sidearm = true
			Enter()
			return
		end
	end

	-- an accessory screen first: it shows its own piece alone, so which one it is has to be known before Enter builds
	for l_Screen, l_Slot in pairs(ACCESSORY_SCREENS) do
		if string.find(s_Name, l_Screen, 1, true) ~= nil then
			m_Slot = l_Slot
			m_Sidearm = false
			Enter()
			return
		end
	end

	if string.find(s_Name, SCREEN, 1, true) ~= nil then
		m_Slot = 0
		m_Sidearm = false
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

-- the screens' ActionScript, through the AS2 channel: the loadout on opening (WVL), a camo pick (WVC), the drag (WV1/WV0)
Events:Subscribe("AS2:Frame", function(p_Text)
	if type(p_Text) ~= "string" then
		return
	end

	if string.sub(p_Text, 1, 3) == "WV1" then
		local s_Code = string.sub(p_Text, 4)
		m_Sources.as = m_Sources.as + 1

		if s_Code ~= "" and not m_ButtonCodes[s_Code] then
			m_ButtonCodes[s_Code] = true
			LOG("press with button code " .. s_Code .. " seen through the AS2 channel (the screen's Mouse listener)")
		elseif m_Sources.as == 1 then
			LOG("press seen through the AS2 channel (the screen's Mouse listener)")
		end

		if DRAG_CODES[s_Code] then
			m_AsButton = true
			m_AsCode = s_Code
		end
		-- any other button (the right one) does nothing: its release never reaches the movie
	elseif p_Text == "WV0" then
		m_AsButton = false
		m_AsCode = ""
	elseif string.sub(p_Text, 1, 3) == "WVL" then
		local s_Ids = {}

		for l_Field in string.gmatch(string.sub(p_Text, 4) .. ",", "([^,]*),") do
			s_Ids[#s_Ids + 1] = Unsigned(l_Field)
		end

		m_Loadout = { weapon = s_Ids[1] or 0, accessories = { s_Ids[2] or 0, s_Ids[3] or 0, s_Ids[4] or 0 }, camo = s_Ids[5] or 0 }
		LOG(string.format("loadout from the accessories screen: weapon %d, accessories %d/%d/%d, camo %d", m_Loadout.weapon,
			m_Loadout.accessories[1], m_Loadout.accessories[2], m_Loadout.accessories[3], m_Loadout.camo))

		if m_Active and PROP and (m_Frame ~= nil or not CALIBRATE) then
			BuildWeapon()
		end
	elseif string.sub(p_Text, 1, 3) == "WVR" then
		-- a trace from the row's script: "<slot>,<when>,<current>/<count>,<id at current>,<id>" -- what the widget holds
		-- when the screen opens and when it comes back. Straight to the log, which is on disk.
		LOG("row trace " .. string.sub(p_Text, 4))
	elseif string.sub(p_Text, 1, 3) == "WVA" then
		-- "WVA<slot>,<unlock>": the accessory screen saying which piece it is about. It comes from the engine's own data
		-- (the item marked as worn), so it wins over the row's reading that arrived with the loadout.
		local s_Slot, s_Id, s_SlotText = nil, nil, nil

		for l_Field in string.gmatch(string.sub(p_Text, 4) .. ",", "([^,]*),") do
			if s_SlotText == nil then
				s_SlotText = l_Field
				s_Slot = tonumber(l_Field)
			elseif s_Id == nil then
				s_Id = Unsigned(l_Field)
			end
		end

		-- "WVAS,<unlock>": the pistol's window saying which pistol it is about (the one worn, from the engine's data)
		if SIDEARM_SLOTS[s_SlotText] and s_Id ~= nil and s_Id ~= 0 then
			local s_Was = m_SidearmUnlock
			m_SidearmUnlock = s_Id

			if m_Active and PROP and m_Sidearm and s_Was ~= s_Id then
				LOG("the pistol's window says the sidearm worn is unlock " .. s_Id .. " (was " .. tostring(s_Was) .. ")")
				local s_Ok, s_Why = BuildWeapon()

				if not s_Ok then
					LOG("after the pistol's window's word: " .. tostring(s_Why))
				end
			end
		elseif s_Slot ~= nil and s_Slot >= 1 and s_Slot <= 3 and s_Id ~= nil and s_Id ~= 0 then
			if m_Loadout == nil then
				m_Loadout = { weapon = 0, accessories = { 0, 0, 0 }, camo = 0 }
			end

			local s_Was = m_Loadout.accessories[s_Slot]
			m_Loadout.accessories[s_Slot] = s_Id

			if m_Active and PROP and s_Was ~= s_Id then
				LOG("the screen says row " .. s_Slot .. " wears unlock " .. s_Id .. " (the row read " .. tostring(s_Was) .. ")")
				local s_Ok, s_Why = BuildWeapon()

				if not s_Ok then
					LOG("after the screen's word: " .. tostring(s_Why))
				end
			end
		end
	elseif string.sub(p_Text, 1, 3) == "WVC" then
		local s_Id = Unsigned(string.sub(p_Text, 4))

		if m_Loadout == nil then
			m_Loadout = { weapon = 0, accessories = { 0, 0, 0 }, camo = s_Id }
		end

		if m_Sidearm then
			-- ON THE PISTOL'S WINDOW A PICK IS ANOTHER PISTOL UNLOCK (the plain one, or a camo of it): the view builds that one
			m_SidearmUnlock = s_Id
			LOG("the sidearm is now unlock " .. s_Id .. " (picked on its own window)")
			-- ⭐ and the pick goes to the server, the vehicle windows' way (ChooseCamo in CamoVehicleView.lua): the game does not store a
			-- camo copy as the player's pistol, so the framework (ext/Shared/SidearmTwins.lua) tells the server (pistol, camo), and the
			-- server hands him that pistol's camo copy at every deploy -- from this window too
			pcall(function() Events:Dispatch("SidearmCamo:Picked", s_Id) end)

			if m_Active and PROP then
				local s_Ok, s_Why = BuildWeapon()

				if not s_Ok then
					LOG("after the pick: " .. tostring(s_Why))
				end
			end
		elseif m_Slot > 0 then
			-- ON AN ACCESSORY SCREEN A PICK IS A DIFFERENT ACCESSORY, not a different camo of the weapon: the row now
			-- wears that unlock, so the view remembers it (the screen sends the pick before the engine has told anyone)
			-- and builds that piece instead. Without this the view kept showing the piece the row had on when it opened.
			m_Loadout.accessories[m_Slot] = s_Id
			LOG("accessory row " .. m_Slot .. " now wears unlock " .. s_Id .. " (picked on its own screen)")

			if m_Active and PROP then
				local s_Ok, s_Why = BuildWeapon()

				if not s_Ok then
					LOG("after the pick: " .. tostring(s_Why))
				end
			end
		else
			m_Loadout.camo = s_Id

			if m_Active and PROP then
				RebuildBase()
			end
		end
	elseif string.sub(p_Text, 1, 3) == "WVF" then
		-- "WVF<slot>,<accessories>,<twins>,<shown>[,blank,<id>…]": what the row's fold ended up with. An item with no name
		-- reads on screen exactly like "that unlock has no description", so its ID is the only thing that tells them apart.
		LOG("row fold " .. string.sub(p_Text, 4))
	elseif string.sub(p_Text, 1, 3) == "TBL" or string.sub(p_Text, 1, 3) == "UPD" or
		string.sub(p_Text, 1, 3) == "FAM" or string.sub(p_Text, 1, 3) == "MRW" or
		string.sub(p_Text, 1, 3) == "MGR" or string.sub(p_Text, 1, 3) == "FSL" or string.sub(p_Text, 1, 3) == "FSH" or
		string.sub(p_Text, 1, 3) == "MPL" or string.sub(p_Text, 1, 3) == "MPN" or string.sub(p_Text, 1, 3) == "MPF" then
		-- "MPL<entries>,<new>,<length>": a rows screen PULLED the table out of the data store itself (CamoRow.as camoPullTable) --
		-- the loadout's first entry, whose mailbox never answers (runs 342 and 347: MRW only on coming back from a window)
		-- "FSH<opening cell>,<cell a hover tried to take it to>": a stale mouse-over on the cell the pointer happened
		-- to be on when the window appeared, put back. Once per screen.
		-- "FSL<cells>,<cell of the one worn|-1>,<what the row opened with>": where an ACCESSORY's camo window put its
		-- cursor. -1 means neither the engine's mark nor the row's identifier matched a cell of this window, and the
		-- cursor fell back to cell 0 ("Sin camuflaje") -- which is what he saw before.
		-- "MGR<entries>,<new>,<delivered>,<redrawn>" is the same receipt from a GRID screen (the weapon's camos and the
		-- three accessory ones): `redrawn` 1 means the table landed after the cells were named and they were named
		-- again. Its four fields mean what MRW's do, and the two together cover every screen the table reaches.
		-- "MRW<entries>,<new>,<delivered>,<reapplied>,<rows folded again>" comes from the ROWS screen, whose mailbox is a clip of our
		-- own: how many entries the table holds once the delivery is parsed, how many of them the baked one did
		-- NOT have, HOW MUCH TEXT the delivery carried, and whether the camo row had to be dressed again because
		-- its items had already arrived. `delivered` is what makes the line stand alone -- with entries alone,
		-- "the whole table arrived" and "nothing arrived, the entries were already baked" read identically.
		-- `new` 0 with `delivered` large means the two tables simply agreed (nothing was baked after the menu).
		-- ⭐ THE MAILBOX'S RECEIPT. "TBL<entries>,<length>,<shape>" = the table ARRIVED at the screen's script through
		-- our own data key and parsed; "UPD<name>,<length>" = the census of what the engine delivers to that widget,
		-- which is what names the next step if ours never shows up. Both travel by the prefix below.
		LOG("the mailbox " .. p_Text)
	elseif string.sub(p_Text, 1, 3) == "CAN" or string.sub(p_Text, 1, 3) == "CNP" or
		string.sub(p_Text, 1, 3) == "CTL" or string.sub(p_Text, 1, 3) == "MRK" then
		-- ⭐ THE MAILBOX CANARY. "CAN<n>,<where>,<len>,<whole>,<look>": `where` is WHICH of the places the screen's
		-- script asks held the value (0 = none), `len` how much arrived, `whole` whether the end marker came with
		-- it, `look` 0 for the script's load and 1 for the pass where the items arrive. "CNP<place>,<keys>,<props>"
		-- is the census of each place, and "CTL<where>,<len>,<isKitCell>" is the CONTROL: DICE's own property on
		-- that same node. Without the control a silent "nothing" has two readings -- the route does not work, or
		-- our write never happened -- and a test like that measures nothing.
		-- ⛔ And what the client script wrote is said HERE, not where it is written: the canary is installed while
		-- the client's scripts LOAD, and a net event sent at that moment reaches nobody (measured on the first
		-- boot: not one line arrived).
		if not m_CanaryWrote then
			m_CanaryWrote = true
			LOG("the mailbox canary was installed by the client script: " .. tostring(_G.CAMO_CANARY_WROTE) ..
				" propert(ies), sizes " .. tostring(_G.CAMO_CANARY_SIZES))
		end

		LOG("the mailbox canary " .. p_Text)
	elseif string.sub(p_Text, 1, 3) == "VGR" or string.sub(p_Text, 1, 3) == "VVC" or string.sub(p_Text, 1, 3) == "VRW" or
		string.sub(p_Text, 1, 3) == "VOR" then
		-- the VEHICLE window's words (its list, a pick, the TIERRA / AIRE rows' label and their rows of our own): CamoVehicleView.lua
		-- logs them
		return
	else
		-- ⛔ A FRAME NOBODY HANDLES USED TO VANISH HERE, so a trace added to the scripts looked like a trace that never ran
		-- (measured 2026-09-19: the table's own "TBL" line was nowhere in the log and I read that as "the carrier was never
		-- parsed"). Anything unknown is logged once, with its text.
		if not m_UnknownFrames[string.sub(p_Text, 1, 3)] then
			m_UnknownFrames[string.sub(p_Text, 1, 3)] = true
			LOG("unhandled AS2 frame: " .. p_Text)
		end
	end
end)

-- the probe: the input events the engine hands the UI (concepts 0-3 arrows, 6 Activate, 7/8 Back… are known; a wheel would be new)
Hooks:Install("UI:InputConceptEvent", 1, function(p_Hook, p_EventType, p_Action)
	if not m_Active then
		return
	end

	local s_Key = tostring(p_Action) .. "/" .. tostring(p_EventType)

	if not m_UiEventSeen[s_Key] then
		m_UiEventSeen[s_Key] = true
		LOG("UI input event action=" .. tostring(p_Action) .. " type=" .. tostring(p_EventType) .. " (first time while the view is up)")
	end
end)

Events:Subscribe("Extension:Unloading", function()
	Leave("extension unloading")
end)

Events:Subscribe("Level:Destroy", function()
	Leave("level destroyed")
	m_Comp = nil
	m_Default = nil
	m_Loadout = nil
	m_SidearmUnlock = 0
	-- ⛔ the camo screens load again with every level (their partitions' loads are scanned again each level), so their writer
	-- nodes belong to the level that ends: a node kept past it would be written into over freed memory and finalised over it
	-- later (keku 2026-09-26, a client dying at a round change). Dropped here and found again as the next level's screens load;
	-- the scan cap counts per level (five screens find a writer each level: kept, it stopped the scan from the third level on)
	m_MailNodes = {}
	m_MailSaid = false
	m_MailScans = 0
end)

Console:Register("camoview", "the camo screen's weapon view: state", function()
	return State()
end)

LOG("loaded: the camo screen shows the weapon alone with what it wears; drag with the left button over it to turn it (F8: state)")
