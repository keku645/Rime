# Rime UI Editor

Composes Battlefield 3 UI screens and builds them as a Venice Unleashed mod. The game is opened read-only,
in-process (the same Rime commands the REPL runs); everything ships as a mod.

## What it does

- lists every screen (`ui/flow/screen/*`, 241) and every flow graph (`ui/flow/graph/*`, the 80 `UIGraphAsset`s
  whose StateNodes/DialogNodes chain the screens) of the mounted game and shows its **stage** (the placed
  widgets, with real bounds where the symbol has static geometry, dashed boxes for widgets that build
  themselves at runtime; a flow graph has no stage) and its **graph** (every node, its ports, its wiring) read
  from the shipped EBX;
- adds widgets from the **palette** (`ui/assets/*`): an import + a placement on the stage + a new graph node;
- lets you move placements (drag), set properties, the data binding (DataName / DataKey / the component taken
  from an existing node) and connections (`event -> node.event` for widgets, `event -> node#field` for logic
  nodes such as `Confirm#inValue`);
- **Build mod** writes the complete mod folder (`mod.json`, `ext/Shared`, `ext/Client`, `src/*.gfx`, `.sb`)
  through `ui_build_mod`; add the mod's name to `ModList.txt` and play.

The document is plain JSON (`Examples/CamoGridExample.json`) and can be edited by hand or built from the
REPL: `ui_build_mod <doc.json> <Mods folder>`. A node's `fields` are typed by the game's type information;
a reference field may hold an instance inline (`"DataBinding": {"$type": "UITextDataBinding",
"StaticText": "ID_M_YES", …}`) — the client edits the node's existing instance in place when it is of that
type, or creates one born with a stable guid.

## What you see

- **Stage** tab: the screen's movie drawn as the game draws frame 1 — shapes, gradients, the atlas bitmaps
  (DefineSubImage rectangles of the movie's `<movie>_iN` texture), edit text with the real glyphs of the game's
  font library (via the FontMap: `$MENU_bold` → Purista EA Semibold) and `ID_*` strings resolved through the
  text database (English by default — *Settings… ▸ Text language*). What the game fills in at runtime (list
  rows, data-bound labels) stays empty, as in Flash. *Art* and *Outlines* toggles on the bar.
- **Text widgets as the game builds them.** A `TextField` (and a `ScrollingTextField`) has no text field of
  its own: its constructor attaches the movie's `mc_<m_rowType>` symbol — the `m_rowType` clip variable of
  the placement (`bold1` = `$Menu_bold` 20 px, `medium1` = `$Menu_medium` 20 px, `baseText0` 32 px…) — and
  at load reads the widget's placed size, resets the widget's scale to 100 % and gives that size to the
  row's text field as its **box** (the font keeps the row's size; `m_align` sets the alignment; `m_useBorder`
  frames the box). The stage draws exactly that: change the row type and the text changes style, scale the
  placement and the box grows while the letters do not. A placement without `m_rowType`, or with a type the
  movie does not export, draws nothing — the game attaches no row either (the log says so). The movie also
  carries a `previewInstance` (what the game's own editor showed, always the 32 px `baseText0` row with the
  text blanked); the stage no longer draws it for these widgets, so what you see is what the game shows.
- **Preview** (toolbar): the open screen as the game shows it — the art alone on the 1280x720 stage, no
  outlines, boxes, handles or grid, fitted to its own window and following every change of the document.
  *Save PNG…* writes the stage at 1:1, to put next to a capture of the game. (List rows and data-fed texts stay
  empty there as well: they come from the game's data at runtime.)
- **Preview in Ruffle** (toolbar): the open screen with your edits, played inside the editor by
  [Ruffle](https://ruffle.rs) (an open-source Flash player, its web build shipped with the editor and hosted in a
  WebView2 window) running **the game's own ActionScript** — the widget classes construct themselves from their
  clip variables, hover and press animate, the texts come out of the game's fonts. The editor does what the game's
  UI system does around the movies: it converts the screen and every widget it imports to plain SWF (the atlas
  rectangles become bitmaps, `ID_*` texts are localised, each widget is made self-contained by inlining what it
  imports), writes a shell movie that loads a **bridge** and the two ActionScript class libraries before the
  screen (as the game does), registers every imported widget under its class, hands each widget with a static
  binding its `initialize({data})` (page header, static texts) and serves the folder over 127.0.0.1. The bridge
  stands in for the engine's components (`_global.Data.UIWidgetEventComp`, `UIDataInterfaceComp`,
  `UILocalizeComp`… — the fifteen the game registers, with their methods): every call the ActionScript makes on
  them lands in the editor's log (`[game] Button_02 fired OnItemReleased`, `[game]
  UIDataInterfaceComp.getData(-1791838705) = FrontEndComp.InFrontend`), the data and texts the editor pushes to
  the page answer `getData` and `formatString` on the spot (the whole text database goes to the page, keyed by
  the game's hash of the id), and the editor can call into any widget (`initialize`, `update…Data`,
  `handleInEvent`). *Reload* plays the converted movies again; *DevTools* opens the browser console with the
  player's own messages. Without a WebView2 runtime, *Settings… ▸ Ruffle player* names a desktop `ruffle.exe`
  for the same preview without the bridge. The shell and the bridge are written as version-10 movies (still
  AVM1): the player converts a value the page pushes into a movie under the stage's root movie and a value it
  returns to a movie's call under the calling movie, and below version 9 it turns an empty string into the text
  `null` — the rows' empty category labels showed as a small "null" until then.
  `RimeUIEditor.exe --ruffle <ui/assets/screen> <out dir> [document.json]` does the conversion alone.
  Behind the player the editor plays the **engine's part** (`[host]` lines in the Output): when the screen
  reports itself loaded it calls the screen's `initializeScreen` with one record per widget, the way the game's
  UI system does (`NumEvents`/`Event_i` from the widget's event contract, its properties, its alignment — applied by
  the widget's own code against the stage rectangles the shell defines at the design size — and the static texts
  of its binding), then fires the screen's entry points; a widget that asks for the focus gets it the way the
  engine's focus manager gives it (`onFocused`, and `onUnfocused` on the one that had it), so the highlighted row
  looks as in the game; every event a widget fires walks the screen graph from that
  port — a wire into a widget becomes `handleInEvent`, a DataSetNode writes the data table and refreshes the
  widgets bound to that key, a ComparisonLogicNode picks the output named by the value (`1`/`0`/`true`/`false`
  land on `True`/`False`), an ActionNode is logged under its `UIAction` name (the engine's actions have no
  stand-in outside the game).
  **Screens chain as in the game.** The host finds the flow graph that shows the screen (the `UIGraphAsset`
  whose StateNode points at it) and enters the screen through that StateNode's port; a screen output
  (`Confirm`, `AccessoryChanged`…) continues in the flow graph from the StateNode's output of that name: a wire
  into another StateNode **pushes that screen into the player** (converted on the spot, loaded as a new clip,
  initialised and entered), a wire's `NumScreensToPop` pops that many screens first, a DialogNode pushes its
  popup screen with the dialog's title, text and buttons as data, and the StateNode's `Initialized` output
  fires once every widget of the screen reports its enter complete. **Back (Esc)** in the player window is the
  controller's Back/Deactivate on the screen on top (its StateNode's input-event output). A comparison whose
  key holds no value stops the walk and says which key it is: the game's component would answer it; the
  player's toolbar lets you **set any data key the loaded graphs read** (component.source, a value, *Set*) to
  follow a branch, and the widgets bound to it refresh. The engine's actions that turn a screen's selections
  into the player's profile have a stand-in: a pick in an accessory row (its arrows) or in a loadout row
  writes the keys the screen's DataSetNodes name, the flow graph's action reads them into the profile, the
  generated values are built again and the rows show the pick (the weapon row's slot shows the optic, the
  sidearm row the pistol; a new primary weapon starts its accessories from none). A DialogNode's popup comes up
  over the screen with the dialog's title, text and buttons, and a button pressed on it continues in the flow graph
  with the button's label (Confirm with locked items: the game's "items locked" popup, OK back to the loadout). A
  StateNode's Show/Hide shows or hides a screen already on the player. What is not simulated: the other actions
  (spawn, post-process, store to the server) and the data the components compute beyond what the generator builds.
  **Icons are the game's own textures.** A widget that shows an image (kit rows, cells, dog tags, awards…)
  asks the engine to stream a texture by path (`UI/Art/…`) and then loads it as `img://path`; the preview
  answers the request (`loadTextureAsyncDone`), the converted scripts load `./img/path` instead, and the
  preview's server turns the game's texture into a PNG on the spot — from the cache (a mount caches every
  `ui/art` texture, 2 609 of them) or from the mounted game. Text ids (`ID_…`) inside the data handed to a
  widget are localised on the way, as the engine's text translator does.
  **The data the components compute is generated from the game's own assets.** The engine's UI data
  components (`UICustomizationComp`, `UIKitComp`…) read the same EBX the editor can read: the kit
  (`Gameplay/Kits/<kit>`: its weapon table, one row per category — primary, secondary, gadgets,
  specialization — each a list of unlock assets), a weapon's customization table
  (`Weapons/<w>/<w>_Customization`: one part per accessory row — optics, rail, barrel, camo) and the UI
  metadata tables (`UI/UI*MetaData`: one description per item, keyed by the unlock's identifier, with its
  name and description ids, its texture paths and, for weapons, the ammo, rate of fire, range and fire
  modes the info box shows). The preview's host builds every value a screen's widgets or graphs read from
  those, for a **synthetic profile** (everything unlocked; the US assault kit and its first primary weapon
  by default — the player's toolbar picks another kit or primary weapon, and every generated value is built
  again with the widgets bound to it refreshed, as the components do when the player changes kit), in the
  shapes the widgets' ActionScript reads — checked against a record the game itself handed the accessories
  screen: an item's `Index` is the unlock's identifier, a weapon's fire modes are the info box's frame labels
  (`fltSingleFire`, `fltBurstFire`, `fltAutomaticFire`), the weapon row holds the kit's whole primary list with
  the equipped one selected, `HasFocus` follows the node's focus index — and logs where each came from
  (`[host] generated UICustomizationComp.WeaponAccessory4 ← weapons/m416/m416_customization part 3 (camo)`).
  A recording of the screen from the game (above) still wins where it carries a channel; the generator
  fills what it lacks. A mount caches the kits, the customization tables and the unlock assets, so this
  works without the game afterwards.
- **Record data…** / **Import recording…** (toolbar): widgets fed from the game's data at runtime (kit rows,
  button bars, info boxes) only show content once the game has been asked what it hands the screen. *Record
  data…* writes the `RimeUiRecorder` mod for the open screen into the Mods folder: the screen's shipped movie
  with a small recorder in front of its frame (it wraps `initializeScreen` and `refreshScreen` on the screen's
  own clip and sends every record — each widget's init data, every later data refresh, the answers the
  components gave meanwhile — into the client console as bits over the chat's typing-mode call, decoded by the
  mod's Lua into `[AS2] #R…` lines), plus the receiver and the delivery. Installing it is your step: a line
  `RimeUiRecorder` in `ModList.txt`, a `#` in front of any other mod that ships the same screen (one movie per
  screen reaches the game), restart, open the screen, hover and click what you want recorded, copy the whole
  client console. The console's scrollback is short, so the mod also keeps the records for you: **F5** in the
  game (or the console command `uirecdump`) sends every decoded record to the server, which writes them into the
  mod's own database, `Admin/Mods/RimeUiRecorder/mod.db` (table `uirec`, one row per record, a second F5 rewrites
  the same ids); `External/keku/uirec_db.py <mod.db> <out.txt>` turns the latest run back into the lines the
  importer reads. *Import recording…* reads the clipboard (or a text file) into
  `%LOCALAPPDATA%\RimeUIEditor\preview\data\<screen>.json`; from then on *Preview in Ruffle* initialises the
  screen with the game's own records (`Align` left out: the player has no safe-area rectangle), answers
  `getData` with what the game answered, and refreshes a widget with the payload the game sent it on that
  channel. The recorder also rides in the preview itself: what the host hands the screen comes back as an echo
  (`[host] recorder echo …`), which is how the self-test checks the whole round trip without the game.
- **Graph** tab: every node of the screen (widgets and logic) with its ports, the shipped wiring in blue, the
  document's in orange. Ports are the game's: single ports (In, Out, True, False, Show, Hide) and the named
  array ports (a ComparisonLogicNode's `0`/`1`/`ID_M_YES`, a StateNode's `EnterScreen`/`Initialized`, a widget's
  events); a StateNode port that listens to a controller action shows it (`Back [Back]`). Every shipped
  connection is drawn on its own port (by the port's identity, never by position). Wiring the game itself
  ships broken — a connection naming a port that sits in no slot of its node, 118 such endpoints in the whole
  game — is drawn dashed red-brown on an italic red port, never dropped; a `JumpNode` shows the port it
  jumps to as a note (`TargetPort → Confirm.In`). Drag a box to move it; **drag from a yellow output dot to a
  blue input dot to wire** (recorded on the document; a shipped source node becomes an edit); *Add node*
  creates any node type the game's type information knows (DataSetNode, ActionNode, DialogNode,
  ComparisonLogicNode…), with its ports.
- **Properties**: a property grid in the style of the game's own editor. Pick a node (stage, layers or graph)
  and every field of its type is there, grouped by the type that declares it (`UINodeData`, then
  `WidgetNode` / `StateNode` / `DialogNode`…), showing **the value that screen ships** — check boxes for
  bools, drop-downs for enums, `…` pickers for references (widget assets, components, screens), structs
  and lists expandable with `X` / `+` / `x` buttons, and a widget's **data binding as a whole instance** of
  any of the 12 binding types (switch the type from the drop-down). Edit any value: the first edit puts the
  node into the document as an edit of the shipped one, later edits change it there; names in bold are what
  the document overrides. A widget also shows its **Placement** (local X/Y and scale — typed values become
  stage ops), its **WidgetProperties** (the `p_*` and setup values the screen sets), **the properties the
  game sets on that widget elsewhere** (measured over every screen: how often and with which values — `+`
  adds one with the most common value), its **event contract** (what the widget fires ▶ and receives ◀,
  from the widget asset), its ports and its wiring. The footer names the type and whether the node is
  shipped, new or an edit. `JumpNode.TargetPort` takes `Node.port`; array ports are typed in the
  *Document ▸ Named ports* list (`Outputs`/`Inputs`, name, `@Event`, `^Back` for a controller action).
- **Data keys by name**: a binding source's `DataKey` is shown and chosen as the **source of its component**
  (`UICustomizationComp.CustomizeInfo`) — the number the game stores is the engine's hash of
  `UI_<COMPONENT>_<SOURCE>` upper-cased, computed by the editor (`DataKeys`), and a key naming a source the
  component no longer has (the game ships 35 of those) says so. `DataCategory` picks among the data
  components; `DataName` suggests the channels the widget's code accepts.
- **Widget settings from the widget's own code**: each widget's script reads its `WidgetProperties` at
  initialise (`initData.<Name>`) — the editor lists every such setting with its declared default and how it
  is parsed (bool / number / flag / string), what the shipped screens set it to, and adds one with `+`;
  plus the **data channels** (`update<Name>Data`) and the **event contract**. The measurement lives in
  `Data/widget_settings.json` (made by `External/keku/as_widget_settings.py` from the decompiled scripts).
- **Screen fields**: the screen asset's own fields (`Modal`, `ProtectScreens`, `BundleAssetName`, the
  platform flags…) with the shipped values, editable — they travel in the document's screen entry and are
  written on the live asset. The movie's size (1280/720) is shown too.
- **Data Explorer**: a folder tree of the UI database (`ui/flow/screen`, `ui/flow/graph`, `ui/assets`,
  `ui/uicomponents`…) with a Name/Type list; the search box finds any partition by name. Click a screen or
  flow graph to open it, double-click a widget asset to add it to the open screen.
- **Tabs**: every opened screen is a tab above the stage (• marks one the document changes); × or a middle
  click closes it. The explorer's **type drop-down** lists every partition of one type whatever its folder
  (all 241 screens as `UIScreenAsset` — the HUD screens live flat under `ui/flow/screen` next to the menus:
  `hudscreen`, `hudmpscreen`, `hudconquestscreen`…; the flow graphs as `UIGraphAsset`; the widgets, the
  data components); the search box narrows it further.
  The tabs and the screen on the stage are remembered: the next start reopens them. With nothing open the
  stage says what to do (open a screen from the Data Explorer); the log says what each open did
  (`opened ui/flow/screen/…: 10 clips in Layers, 25 nodes on the graph`).
- **Drag & drop**, with the targets the game's own data allows: a widget asset (from the explorer or the
  palette) onto the **stage** lands where you drop it (its row in *Layers* is selected at once, its
  properties are on the right); a node type from the **Nodes** toolbox onto the **graph** lands there (onto
  the stage: it goes to the graph in a fresh column and the Graph tab comes up); a widget dropped on the
  graph gets its box where you dropped it and its clip at the stage's default spot; a screen dropped on the
  graph of a *flow graph* becomes a `StateNode` showing it (a popup becomes a `DialogNode`; a screen refuses
  it — the game's screens never hold one); a data component dropped on the graph becomes a `DataSetNode`
  bound to it; any partition dropped on a reference row of the properties fills it when the type fits. A
  label follows the mouse saying what is dragged and what it will make; the stage or graph that will take
  the drop shows a blue frame; every drag event is written to the log as `[drop] …` (what was dragged, which
  target accepted or refused it and where, what the drop made), so a drop that goes nowhere can be read off
  the log. In the explorer a press selects a row and the *release* opens it, so a screen dragged onto a flow
  graph does not replace the graph it was meant to land on.
- **Wiring, only where the game's data allows it.** A widget box shows *every* event of its widget's
  contract: the ports the node has (solid dots) and the rest of the contract hollow — wiring a hollow one
  creates the port (the client adds it to the node, `[PORT]` in its log). While a wire is dragged from an
  output, every input says whether it can take it: compatible inputs light up (bigger, bright), the rest go
  dark, and the tooltip gives the reason. The rules are measured over the game's 4385 shipped connections:
  a wire goes from an output slot to an input slot of *another* node (no connection ever joins a node to
  itself), never into `DataInputs` (never wired in the game — an ActionNode's parameters go in `Params`),
  never onto a `JumpNode.TargetPort` (a reference, set in its properties); everything else is allowed and the
  log says how often the game wires that pair of slots (`WidgetNode.Outputs → DataSetNode.In 309×`) or that
  it never does. A refused wire is logged with its reason. The *Document ▸ Connections* rows of a node offer
  every compatible wire as a drop-down (the game's most-wired pairs first) and `+` adds the first one.
- **Resize and turn on the stage**: click a widget (or any placement) and it grows handles — eight on its
  box and a knob above it. Drag a corner or an edge to scale it (the opposite corner / edge stays put, so
  the box follows the mouse; hold **Shift** to keep the proportions); drag the knob to turn it about its
  origin (the orange dot; **Shift** snaps to 15°). The numbers next to the box say what the release will
  write: a `scale:` op (plus a `move:` when the origin has to follow) or a `rotate:` op, one undo step each.
  The same values are typed in *Placement ▸ Scale X / Scale Y / Rotation*. Scale is measured along the
  placement's own axes, so a turned widget keeps its size; a rotate op keeps the scale.
- **Clip variables (set at construct)**: every widget FrostEd places carries per-instance parameters baked
  into the placement's clip actions and read by the widget's constructor before anything else — a
  TextField's `m_rowType` (its text style: `bold1`, `medium1`…) and `m_align`, a Button's `buttonType` and
  `buttonText`, a KitSelector's `m_viewType`, a Grid's `i_cellType`, a TabBar's `m_tabsDataStr`… (measured
  over every screen movie: 44 widgets, 100 % of their placements). A widget dropped on the stage gets the
  usual set of its widget (the values most of the shipped placements use) as a `vars:` op; the *Clip
  variables* category shows them, offers the shipped values, and lets you change them or add the usual ones
  a clip lacks. Without `m_rowType` a TextField attaches no text row and shows nothing — a round in the game
  taught that.
- **Layers**: every stage placement with an eye (drawn or not) and a lock (not pickable on the stage) —
  view toggles, not part of the mod.
- **All clips** outlines every clip inside the widgets too (their text fields, panels, icons — the widget
  movie's own placements, measured by the renderer), like the game's editor; **Rotation** draws each
  placement's local x axis with its angle. The status bar at the bottom shows where you are and the counts.
- Texts whose `ID_*` the text database does not know are drawn **red**, the way the game's editor flags a
  missing string.
- **Static text**: a widget whose code takes a `Text` channel (TextField, buttons, headers…) shows a *Static
  text* row next to its binding. Type a literal (`I hate burgers`) or an `ID_` string the game localises: it
  becomes a `UITextDataBinding` with that `StaticText` on the node — the way every shipped text widget
  carries its text — and the stage draws it. A `TextData` entry in WidgetProperties does nothing: the
  widget's code never reads one.
- **Multi-selection** on the stage: Ctrl+click adds a clip to the selection (or takes it out); a left drag on
  empty stage draws a band and selects every clip that lies inside it. Dragging any selected clip moves the
  lot (one undo step); Delete, Ctrl+C / Ctrl+V and Ctrl+D act on all of them. The handles show for a single
  selection only.
- **Multi-selection** on the graph works the same way: Ctrl+click adds a box, a band on empty graph selects
  the boxes inside it, dragging any selected box moves the lot (one undo step), Delete / Ctrl+C / Ctrl+D act
  on the set. The stage and the graph mirror each other's selection (the widgets among the selected boxes are
  the selected clips).
- **Disconnect a wire**: hold **Alt** and click a wire on the graph (it lights up under the pointer). A wire of
  the document simply leaves its node's connections. A *shipped* wire is listed under *Screen ▸ Removed
  connections* (the `x` there puts it back) and is no longer drawn; the mod erases that `UINodeConnection`
  from the screen's list at load, by its instance guid, before it applies the document's nodes — so the same
  two ports can be wired again by a new connection. The client log shows `[UNWIRE] A.port -> B.port -> true
  (46 -> 45)`. A shipped node's *Connections* rows mark such a wire "(disconnected by the document)". One
  undo step each. ⚠ Erasing a shipped connection at load has not run in the game yet.
- **New screen** (toolbar): a screen made from nothing. The game's smallest screen
  (`ui/flow/screen/emptyscreen`: a 1280×720 movie with the screen's ActionScript class, an InputListener and
  the root sprite; a 3-instance partition) is cloned under your name and fresh guids (`UI/Flow/Screen/MyScreen`,
  partition `ui/flow/screen/myscreen`), opened as a tab, listed in the explorer, the type filter and the Screen
  pickers. Drop widgets on it, wire its graph. *Build mod* writes `src/myscreen.gfx` and
  `src/myscreen.partition.json` and adds both to the bundle (`add_resource` + `add_json_partition`); the Lua
  edits it like any screen. To **show it from the game**, put a `StateNode` for it on the flow graph that
  drives the place you come from (drop the new screen from the explorer onto the graph, e.g.
  `ui/flow/graph/spawn/customizationgraph`) and wire an output of the previous screen's `StateNode` — the
  event a button fires reaches the graph through the screen's `InstanceOutputNode` — into the new
  `StateNode`'s `In`; wire its own outputs back the same way. That is how every shipped screen is chained.
  *New mod* starts a fresh document (every edit and new screen forgotten). ⚠ A screen made from nothing has
  not run in the game yet.
- **Copy / paste / duplicate / delete**: Ctrl+C copies the selected clip or box (a shipped one as the node
  the document would add: widget, properties, binding, fields), Ctrl+V pastes it next to the original under a
  fresh name (one undo step), Ctrl+D does both, Delete removes a document node with its stage ops (a shipped
  clip gets a `remove:` op — the mod cannot delete a shipped node, only take its clip off the stage).
- **Undo / Redo**: every change to the document — a drag on the stage or the graph, a widget or node added, a
  wire, a field edited, a node removed — is one step. **Ctrl+Z** / **Ctrl+Y**, or the *Undo* / *Redo* buttons
  (their tooltips count the steps). New / Open start a fresh history. Inside a text box Ctrl+Z is the text
  box's own undo until the box loses focus.

## Cache

The first mount saves everything the editor reads under the cache folder (*Settings… ▸ Cache folder*, one
sub-folder per game install, ~65 MB): partition dumps, movies, atlas textures, fonts, text databases. Later
runs need no mount to browse, draw and edit; *Build mod* mounts by itself (the superbundle is built against
the game's bundles). *Mount game* takes minutes: the button reads *Mounting…* meanwhile (the editor stays
usable, the screens come from the cache) and *Mounted* when done; the log says so at both ends. *Clear cache* in Settings, then mount again, refills it. The widget catalogue
(`widget_catalog.json`: each widget asset's event contract, the properties the screens set on it and the
binding types feeding it, every data component's sources with their keys, and every ui/ partition's type —
measured over all 326 UI graphs — the base game's 321 under `ui/flow` and End Game's 5 under `ui/xp5/flow`) is
built once in the background and kept there.

## The window

Same feel as the shader editor: every divider is draggable (the three columns, screens/layers, properties/
palette, and the output log at the bottom), each pane with a minimum size. On the stage the **wheel zooms
toward the cursor**, the **right or middle button drags the view**, **F** frames the whole stage and
**Ctrl+0** is 1:1; the Zoom slider and the *Fit* / *1:1* buttons do the same around the view centre. The
wheel step is *Settings… ▸ Stage ▸ Zoom speed*. The stage keeps stage pixels underneath: dragging a
placement is unaffected by the zoom, and the grid behind it is drawn in stage pixels (10 / 100).

## Folders

The first run asks for the two folders it needs, prefilled with a guess: the **game folder** (from the
installer's registry key) and the **mods folder** where *Build mod…* writes each mod (the VU server's
`Documents\Battlefield 3\Server\Admin\Mods` when it exists). Both can be changed any time in **Settings…**.
Settings live in `%LOCALAPPDATA%\RimeUIEditor\settings.json` (`RUE_SETTINGS` overrides the path).

## Build mod… (shipping)

The button asks first and works after, the way the shader editor's bake does. The dialog shows the screens
the document touches (what is about to ship) and asks:

- **Mod**: name (the folder under Mods and the line in ModList.txt), version, authors, description — what
  `mod.json` will say. The description is prefilled from the document (which shipped screens are edited,
  which are new, how it is delivered); make it say what the files really do.
- **Bundle**: the mod's own superbundle (`sb\Win32\<superbundle>.sb`, declared in `mod.json`, mounted on
  every level by `ext/Shared`) and the bundle inside it. They follow the name until you change them; one
  superbundle name per mod, never shared with another mod. The edited screens keep their **original resource
  names** inside that bundle, so they replace the game's: at load, `ext/Shared` puts the bundle in front of
  the level's main pass (where widget movies bind) and after the level's `_UiPlaying` pass (where screen
  movies resolve) — the delivery measured in game; nothing of the game's own bundles is renamed or rebuilt.
  Under it, **Ship the graph edits inside the bundle** picks how the graph edits reach the game:
  - *on* (static): every edited screen ships as a whole partition — the game's own, with your nodes,
    wires, bindings, properties, removed wires and asset fields applied, under the screen's own name and
    guid — added to the bundle next to the movies (`add_json_partition`); a screen made from nothing ships
    its clone the same way. Vanilla copies of everything those partitions import (the widget assets, the
    data components, the audio mapping — transitively, under `ui/`) ride in the bundle in front of them:
    measured in game, a partition loaded from the bundle links its imports against what is loaded at that
    moment and never later, and the bundle's copy of a partition takes the place of the game's when it loads
    first. `ext/Client` then edits nothing: it only logs what loaded (`[PROBE] <screen>: N nodes, M
    connections; ours: TextField_02=true(asset true)`, `[PROBE] imports loaded: ui/assets/kitselector=true …`,
    `[PROBE] widgets: …`). The partitions are written with the game's own field layouts (the fidelity map
    covers the UI types; a vanilla screen goes through the writer byte for byte). ⚠ The round with the
    dependency copies has not run in the game yet.
  - *off* (runtime): `ext/Client` edits each screen's live graph at load (`[NODE]`, `[WIRE]`, `[UNWIRE]`…)
    — the delivery verified in game so far.
- **Where**: the Mods folder (the server's `Admin\Mods`, so it is served straight away).

Then the game is mounted if it is not yet (minutes), the document is saved, and the mod folder is written —
only what the game needs:

    <Mods>\<Name>\
        mod.json                     name, version, authors, description, the superbundle, VeniceEXT dependency (no BOM)
        ext\Shared\__init__.lua      mounts the superbundle at the right moments of a level load
        ext\Client\__init__.lua      edits the screen graphs as they load (nodes, properties, bindings, ports, wires, removed wires)
        sb\Win32\<superbundle>.sb    the bundle: the edited screen movies, the new screens' movies and partitions

The files the bundle was built from (the built `.gfx` movies, the new screens' `.partition.json`, the Rime
recipe `rime_build.txt`) are kept in the editor's build folder, `%LOCALAPPDATA%\RimeUIEditor\build\<Name>\`,
never in the mod (a mod built by an older version had them in `src\`; the next build removes that folder).
The **document** is the editor's own project file (a `.json`): your screens, nodes, wires and stage ops —
this session's changes, what the mod is built from. **Save as…** is where you decide where it lives;
**Save** (Ctrl+S) then writes there without asking and stays disabled until that first Save as…; **Open…**
brings a saved one back (the last one is reopened at start). A build never asks where to save: a saved
document is refreshed on its file, an unsaved one just builds. The mod folder does not need the document.

The Output panel lists every step and what is left to you: a line with the mod's name in the server's
`ModList.txt`, a server restart (it reads the list and the mod files at start; the client downloads the
mod on join), and — if the game shows the old mod — deleting the client's cached copy under
`%LOCALAPPDATA%\VeniceUnleashed\mods\<name>`. The tool never touches those files. A headless run
(`--selftest`) asks nothing.

## Self-test

    RimeUIEditor.exe --selftest <bf3 path> <mods root> <reference screen .gfx> <out dir>

`RimeUIEditor.exe --dialogshot <out dir>` photographs the first-run, Settings and Build dialogs.

`RimeUIEditor.exe --render <movie resource> <out.png> [scale]` draws frame 1 of a movie from the cache the
way the stage does (a screen with the texts its bindings fix) and prints every text drawn with its font, size
and box (`drawn: instance1/KitInfoBox_01/ammoHeader = "AMMO" 20px Purista EA Semibold box 328x35 …`).

Drives the window through the user's path (mount, pick a screen, add a Grid from the palette, configure it,
wire it on the graph, undo and redo it all, build) and compares the produced screen movie byte for byte with
a reference that ran in game. It also drops a text field and checks what the stage draws for it against the
game's rules (the `bold1` row at 20 px in the widget's 422x42 box, 844x84 and still 20 px at scale 2, the
text typed inside the DataBinding instance redrawing the stage — compared by pixels), and photographs the
Preview window (`selftest_preview.png`, `selftest_preview_stage.png`). Writes `selftest.png` (the window) and `selftest.txt`; exit code 0 = pass. Set
`RUE_SETTINGS` to a scratch file so the machine's own settings are not touched. With `RUE_SELFTEST_QUICK=1`
and a warm cache the editing steps run in seconds without a mount (the build and the byte comparison are
skipped).

    RimeUIEditor.exe --graphaudit <out dir>

Opens every UI graph of the game (241 screens + 80 flow graphs, from the cache, seconds) through the editor's
model and graph view and checks that nothing is lost or misplaced: every node-shaped instance is a node,
every connection a wire on the right ports in the right direction, every port slot known to the type
catalogue, every widget node on its stage clip, every wire attached in the view. The totals (4630 nodes,
4529 connections, 119 dangling endpoints, 3 port references, 1419 widgets, End Game's CTF graphs included) are the ones an independent
parser measured on the same data. Writes `graphaudit.txt` and photos of three graphs; exit code 0 = pass.

## Building

    dotnet build RimeUIEditor/RimeUIEditor.csproj -c Release -p:SolutionDir="<path to Rime-src>/"

(The trailing slash matters.) Output: `RimeUIEditor/bin/Release/net8.0-windows/RimeUIEditor.exe`, with the
Frostbite support assemblies next to it.
