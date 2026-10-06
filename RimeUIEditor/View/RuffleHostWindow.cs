using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimeUIEditor.View
{
    /// <summary>
    /// The live preview's window: the Ruffle web player inside a WebView2, playing the converted screen with the game's
    /// ActionScript, and the two-way bridge to the editor — every call the ActionScript makes on the engine's components
    /// arrives here as a message ({comp, method, args}); the editor answers by dispatching calls into the movie
    /// ({path, method, a0, a1, a2}). What the game's UI system does around the movies, done by the editor.
    /// </summary>
    public sealed class RuffleHostWindow : Window
    {
        readonly WebView2 m_Web = new();
        readonly TextBlock m_Status = new() { Foreground = Brushes.Silver, Margin = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Func<string, string, JArray, JToken?> m_OnCall;
        readonly ComboBox m_KeyBox = new() { IsEditable = true, Width = 300, Margin = new Thickness(6, 4, 0, 4), FontSize = 11 };
        readonly TextBox m_ValueBox = new() { Width = 70, Margin = new Thickness(2, 4, 0, 4), FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center };
        /// <summary>Back (Esc) pressed: the host fires the controller's Back on the screen on top.</summary>
        public Action? OnBack { get; set; }
        /// <summary>
        /// A key the game maps to an input concept (fb.Input.InputConceptActions value), pressed (true) or released: the arrows =
        /// NavigateUp/Down/Left/Right (0..3), Enter and Space = Activate (6), Esc = Deactivate (7); the host hands it to the widgets
        /// of the screen on top as the engine does. Esc also fires OnBack (the flow graph's Deactivate/Back input).
        /// </summary>
        public Action<int, bool>? OnInputConcept { get; set; }

        static int? ConceptOf(Key p_Key) => p_Key switch
        {
            Key.Up => 0, Key.Down => 1, Key.Left => 2, Key.Right => 3,
            Key.Enter or Key.Space => 6,
            Key.Escape => 7,
            _ => null,
        };

        /// <summary>
        /// A stage point (1280x720 design px) as a screen point (physical px) over the player: the movie is letterboxed inside the
        /// WebView2 (scaled to fit, centred), so a real pointer can be sent where a widget's placement says it is.
        /// </summary>
        public Point StageToScreen(Point p_Stage)
        {
            var s_W = m_Web.ActualWidth; var s_H = m_Web.ActualHeight;
            // fixed: one device pixel per stage unit, centred (DIPs = device px / dpi); fit: scaled to the player, letterboxed
            var s_Scale = FixedStage ? 1.0 / VisualTreeHelper.GetDpi(this).DpiScaleX : System.Math.Min(s_W / 1280.0, s_H / 720.0);
            var s_Ox = (s_W - 1280 * s_Scale) / 2; var s_Oy = (s_H - 720 * s_Scale) / 2;
            return m_Web.PointToScreen(new Point(s_Ox + p_Stage.X * s_Scale, s_Oy + p_Stage.Y * s_Scale));
        }

        /// <summary>
        /// The game's rule for resolutions (measured 2026-09-16 on keku's 720p and 1440p captures: rows, texts, icons — the whole UI — keep
        /// their 720p pixel size, centred; only the 3D behind them grows): the player draws the 1280x720 stage at one device pixel per
        /// stage unit, centred in the window. Off, the stage is scaled to fit the window (a preview, not the game's look).
        /// </summary>
        public bool FixedStage { get; private set; } = true;
        /// <summary>
        /// Device pixels per stage unit of the letterboxed movie: the player's size over the 1280x720 design. The game keeps a loaded
        /// image (a weapon's picture, an icon) at its pixel size whatever the screen's resolution, so the pictures the preview serves are
        /// shrunk by this — at 2560x1440 the stage scale is 2 and a 256 px picture spans 128 stage units, as in the game.
        /// </summary>
        public double StageScale
        {
            get
            {
                if (FixedStage) return 1.0;
                var s_Dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                var s_W = m_Web.ActualWidth * s_Dpi; var s_H = m_Web.ActualHeight * s_Dpi;
                if (s_W < 1 || s_H < 1) return 1.0;
                return System.Math.Min(s_W / 1280.0, s_H / 720.0);
            }
        }

        /// <summary>The player area's size in DIPs (the WebView2 control).</summary>
        public double PlayerWidthDip => m_Web.ActualWidth;
        public double PlayerHeightDip => m_Web.ActualHeight;

        /// <summary>What the page reported of the player's layout: mode, size and offset in device pixels, the page's pixel ratio.</summary>
        public (string Mode, int Width, int Height, int Left, int Top, double Dpr) PlayerLayout { get; private set; } = ("", 0, 0, 0, 0, 1);

        /// <summary>Tells the page how to lay the player out (fixed 1:1 centred, or fit): posted after every navigation and on every change.</summary>
        void PostLayout()
        {
            if (m_Web.CoreWebView2 == null) return;
            m_Web.CoreWebView2.PostWebMessageAsJson(new JObject { ["op"] = "layout", ["mode"] = FixedStage ? "fixed" : "fit" }.ToString(Formatting.None));
        }

        // the resolution the preview stands in for: the player is sized to it (as far as the monitor allows) and the pictures follow the game's rule
        readonly ComboBox m_ResolutionBox = new() { Width = 150, Margin = new Thickness(6, 4, 0, 4), FontSize = 11 };
        sealed record ResolutionChoice(string Label, int Width, int Height) { public override string ToString() => Label; }
        static readonly ResolutionChoice[] c_Resolutions =
        {
            new("720p (1280×720)", 1280, 720), new("1080p (1920×1080)", 1920, 1080), new("1440p (2560×1440)", 2560, 1440), new("4K (3840×2160)", 3840, 2160),
            new("Fit window (scaled)", 0, 0),
        };
        /// <summary>The player was sized for another resolution: the pictures must be served for the new stage scale (the host reloads the page).</summary>
        public Action? OnResolutionChanged { get; set; }

        /// <summary>
        /// Sizes the player's area to a game resolution in device pixels (the window grows around it), as far as the monitor's working area
        /// allows — clamped, the stage scale is what fits, and the log says so. Returns the stage scale reached.
        /// </summary>
        public double SetResolution(int p_Width, int p_Height)
        {
            var s_Dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var s_ChromeW = System.Math.Max(0, ActualWidth - m_Web.ActualWidth); var s_ChromeH = System.Math.Max(0, ActualHeight - m_Web.ActualHeight);
            var s_Screen = Monitors.WorkingAreaOf(this);
            var s_Width = System.Math.Min(p_Width / s_Dpi + s_ChromeW, s_Screen.Width); var s_Height = System.Math.Min(p_Height / s_Dpi + s_ChromeH, s_Screen.Height);
            WindowState = WindowState.Normal;
            Width = s_Width; Height = s_Height;
            if (Left + s_Width > s_Screen.Right) Left = System.Math.Max(s_Screen.Left, s_Screen.Right - s_Width);
            if (Top + s_Height > s_Screen.Bottom) Top = System.Math.Max(s_Screen.Top, s_Screen.Bottom - s_Height);
            UpdateLayout();
            var s_Scale = StageScale;
            var s_Clamped = s_Width < p_Width / s_Dpi + s_ChromeW - 0.5 || s_Height < p_Height / s_Dpi + s_ChromeH - 0.5;
            m_Log($"[preview] resolution {p_Width}x{p_Height}: player {m_Web.ActualWidth * s_Dpi:0}x{m_Web.ActualHeight * s_Dpi:0} px{(s_Clamped ? " (the monitor is smaller: the window is what fits)" : "")}; {(FixedStage ? "the 1280x720 UI is drawn 1:1 in pixels, centred, as the game keeps it" : $"stage scaled {s_Scale:0.00} to fit")}");
            return s_Scale;
        }

        /// <summary>The resolution the toolbar shows (720p, 1080p, 1440p, 4K; 0 = fit the window), picked by its height; the player is sized for it.</summary>
        public void SelectResolution(int p_Height)
        {
            var s_Choice = c_Resolutions.FirstOrDefault(r => r.Height == p_Height);
            if (s_Choice != null) m_ResolutionBox.SelectedItem = s_Choice;
        }

        /// <summary>Runs a script on the page and returns its result as JSON text (a probe of the player's layout, for the seams).</summary>
        public async Task<string> EvalPage(string p_Script)
        {
            if (m_Web.CoreWebView2 == null) return "null";
            return await m_Web.CoreWebView2.ExecuteScriptAsync(p_Script);
        }

        /// <summary>A data key and value typed in the toolbar: the host holds it.</summary>
        public Action<string, string>? OnSetData { get; set; }
        // the profile: the kit and the primary weapon the generated data is built for
        readonly ComboBox m_KitBox = new() { Width = 180, Margin = new Thickness(6, 4, 0, 4), FontSize = 11 };
        readonly ComboBox m_WeaponBox = new() { Width = 130, Margin = new Thickness(2, 4, 0, 4), FontSize = 11 };
        bool m_Filling;
        /// <summary>The profile's kit picked in the toolbar (kit partition name): the host builds every generated value again.</summary>
        public Action<string>? OnKitChanged { get; set; }
        /// <summary>The profile's primary weapon picked in the toolbar (unlock partition name).</summary>
        public Action<string>? OnWeaponChanged { get; set; }

        sealed record KeyChoice(string Key, string Label) { public override string ToString() => Label; }

        /// <summary>The kits the toolbar offers (partition → label) and the one the profile uses.</summary>
        public void SetKitChoices(IEnumerable<(string Key, string Label)> p_Kits, string? p_Selected)
        {
            m_Filling = true;
            try
            {
                m_KitBox.Items.Clear();
                foreach (var (s_Key, s_Label) in p_Kits) m_KitBox.Items.Add(new KeyChoice(s_Key, s_Label));
                m_KitBox.SelectedItem = m_KitBox.Items.OfType<KeyChoice>().FirstOrDefault(c => string.Equals(c.Key, p_Selected, StringComparison.OrdinalIgnoreCase));
            }
            finally { m_Filling = false; }
        }

        /// <summary>The primary weapons of the profile's kit (unlock → name) and the one the profile uses (null = the kit's first).</summary>
        public void SetWeaponChoices(IEnumerable<(string Key, string Label)> p_Weapons, string? p_Selected)
        {
            m_Filling = true;
            try
            {
                m_WeaponBox.Items.Clear();
                foreach (var (s_Key, s_Label) in p_Weapons) m_WeaponBox.Items.Add(new KeyChoice(s_Key, s_Label));
                m_WeaponBox.SelectedItem = m_WeaponBox.Items.OfType<KeyChoice>().FirstOrDefault(c => string.Equals(c.Key, p_Selected, StringComparison.OrdinalIgnoreCase)) ?? (m_WeaponBox.Items.Count > 0 ? m_WeaponBox.Items[0] : null);
            }
            finally { m_Filling = false; }
        }

        /// <summary>The keys the toolbar offers (key → "component.source (node)"); the box also takes a key typed by hand.</summary>
        public void SetKeyChoices(IEnumerable<(string Key, string Label)> p_Keys)
        {
            m_KeyBox.Items.Clear();
            foreach (var (s_Key, s_Label) in p_Keys) m_KeyBox.Items.Add(new KeyChoice(s_Key, s_Label));
            if (m_KeyBox.Items.Count > 0) m_KeyBox.SelectedIndex = 0;
        }

        void SetFromBoxes()
        {
            var s_Key = m_KeyBox.SelectedItem is KeyChoice c ? c.Key : (m_KeyBox.Text ?? "").Trim();
            if (s_Key == "") { m_Status.Text = "pick or type a data key first"; return; }
            OnSetData?.Invoke(s_Key, m_ValueBox.Text ?? "");
        }
        readonly Action<string> m_Log;
        string m_Url;
        bool m_Ready;
        /// <summary>The page the player shows (the shell's URL on the preview's server).</summary>
        public string Url => m_Url;

        /// <summary>Raised on the UI thread when the bridge movie has announced itself (rue("bridge", "ready")).</summary>
        public event Action? BridgeReady;
        /// <summary>The page is about to load again (Reload, a new preview): every clip on the player goes away with it.</summary>
        public event Action? Navigating;
        public bool IsBridgeReady => m_Ready;
        public int Calls { get; private set; }

        public RuffleHostWindow(string p_Url, Func<string, string, JArray, JToken?> p_OnCall, Action<string> p_Log)
        {
            m_Url = p_Url; m_OnCall = p_OnCall; m_Log = p_Log;
            Title = "Preview in Ruffle — live";
            Width = 1320; Height = 800;
            Background = new SolidColorBrush(Color.FromRgb(20, 22, 26));
            var s_Root = new DockPanel { Background = Background };
            var s_Bar = new DockPanel { Background = (Brush)Application.Current.Resources["PanelBrush"], LastChildFill = true };
            var s_Reload = new Button { Content = "Reload", Margin = new Thickness(6, 4, 0, 4), Padding = new Thickness(8, 2, 8, 2), ToolTip = "Load the converted movies again (after a new preview)." };
            s_Reload.Click += (_, _) => Navigate(m_Url);
            var s_DevTools = new Button { Content = "DevTools", Margin = new Thickness(6, 4, 6, 4), Padding = new Thickness(8, 2, 8, 2), ToolTip = "The browser's developer tools: the player's console shows the ActionScript's trace() lines." };
            s_DevTools.Click += (_, _) => { try { m_Web.CoreWebView2?.OpenDevToolsWindow(); } catch { } };
            // Back = the controller's Back/Deactivate on the screen on top (Esc in the game): the flow graph decides what happens
            var s_Back = new Button { Content = "Back (Esc)", Margin = new Thickness(6, 4, 0, 4), Padding = new Thickness(8, 2, 8, 2), ToolTip = "The controller's Back/Deactivate on the screen on top, as Esc in the game: its flow graph decides (save, popup, previous screen…)." };
            s_Back.Click += (_, _) => OnBack?.Invoke();
            // a data key set by hand: what the game's components would answer, chosen here to follow a branch of the graph
            m_KeyBox.ToolTip = "A data key the loaded graphs read or write (component.source); the value typed on the right is what the game's component would answer.";
            m_ValueBox.ToolTip = "The value to hold for the key (1/0 for a comparison's True/False, a text, a number).";
            var s_Set = new Button { Content = "Set", Margin = new Thickness(2, 4, 0, 4), Padding = new Thickness(8, 2, 8, 2), ToolTip = "Holds the value for the key: the page answers getData with it, the widgets bound to it refresh, comparisons in the graph read it." };
            s_Set.Click += (_, _) => SetFromBoxes();
            m_ValueBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SetFromBoxes(); e.Handled = true; } };
            // the profile: the kit and the primary weapon the generated data is built for — every generated value is built again on a change
            m_KitBox.ToolTip = "The profile's kit (Gameplay/Kits): the rows, the header and every value generated from the game's data follow it.";
            m_WeaponBox.ToolTip = "The profile's primary weapon: the accessories screen shows its customization table (optics, rail, barrel, camo).";
            m_KitBox.SelectionChanged += (_, _) => { if (!m_Filling && m_KitBox.SelectedItem is KeyChoice k) OnKitChanged?.Invoke(k.Key); };
            m_WeaponBox.SelectionChanged += (_, _) => { if (!m_Filling && m_WeaponBox.SelectedItem is KeyChoice w) OnWeaponChanged?.Invoke(w.Key); };
            // the resolution the preview stands in for: the player takes that size and the pictures keep their pixel size, as in the game
            m_ResolutionBox.ToolTip = "The game resolution to stand in for: the player takes that size (as far as the monitor allows) and the 1280x720 UI is drawn at its own pixel size, centred — as the game does: at 1440p the whole UI is half the screen it fills at 720p. 'Fit window' scales the stage to the window instead (a preview, not the game's look).";
            foreach (var s_Choice in c_Resolutions) m_ResolutionBox.Items.Add(s_Choice);
            m_ResolutionBox.SelectedIndex = 0;
            m_ResolutionBox.SelectionChanged += (_, _) =>
            {
                if (m_ResolutionBox.SelectedItem is not ResolutionChoice r || !IsLoaded) return;
                FixedStage = r.Width > 0;
                if (r.Width > 0) SetResolution(r.Width, r.Height);
                PostLayout();
                OnResolutionChanged?.Invoke();
            };
            DockPanel.SetDock(s_Back, Dock.Left); DockPanel.SetDock(m_KitBox, Dock.Left); DockPanel.SetDock(m_WeaponBox, Dock.Left); DockPanel.SetDock(m_ResolutionBox, Dock.Left); DockPanel.SetDock(m_KeyBox, Dock.Left); DockPanel.SetDock(m_ValueBox, Dock.Left); DockPanel.SetDock(s_Set, Dock.Left);
            DockPanel.SetDock(s_Reload, Dock.Right); DockPanel.SetDock(s_DevTools, Dock.Right);
            s_Bar.Children.Add(s_Back); s_Bar.Children.Add(m_KitBox); s_Bar.Children.Add(m_WeaponBox); s_Bar.Children.Add(m_ResolutionBox); s_Bar.Children.Add(m_KeyBox); s_Bar.Children.Add(m_ValueBox); s_Bar.Children.Add(s_Set);
            s_Bar.Children.Add(s_DevTools); s_Bar.Children.Add(s_Reload); s_Bar.Children.Add(m_Status);
            // the keyboard as the game reads it on PC: a key becomes an input concept the widgets of the screen on top are handed
            // (the WebView2 raises the window's key events for the keys it gets; a text box in the toolbar keeps its own keys)
            PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.FocusedElement is TextBox || e.IsRepeat) return;
                if (ConceptOf(e.Key) is { } s_Concept) { OnInputConcept?.Invoke(s_Concept, true); e.Handled = true; }
                if (e.Key == Key.Escape) { OnBack?.Invoke(); e.Handled = true; }
            };
            PreviewKeyUp += (_, e) =>
            {
                if (Keyboard.FocusedElement is TextBox) return;
                if (ConceptOf(e.Key) is { } s_Concept) { OnInputConcept?.Invoke(s_Concept, false); e.Handled = true; }
            };
            DockPanel.SetDock(s_Bar, Dock.Top);
            s_Root.Children.Add(s_Bar);
            s_Root.Children.Add(m_Web);
            Content = s_Root;
            m_Status.Text = "starting the player…";
            Loaded += async (_, _) => await StartAsync();
        }

        async Task StartAsync()
        {
            try
            {
                // its own profile folder next to the editor's settings: never the exe's folder
                var s_UserData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimeUIEditor", "webview2");
                Directory.CreateDirectory(s_UserData);
                var s_Env = await CoreWebView2Environment.CreateAsync(null, s_UserData);
                await m_Web.EnsureCoreWebView2Async(s_Env);
                m_Web.CoreWebView2.Settings.AreDevToolsEnabled = true;
                m_Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                m_Web.CoreWebView2.WebMessageReceived += OnMessage;
                // the browser console: Ruffle's own lines (movies loaded, characters missing, script errors) go to the editor's log
                try
                {
                    await m_Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
                    m_Web.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) =>
                    {
                        try
                        {
                            var s_Event = JObject.Parse(e.ParameterObjectAsJson);
                            var s_Text = string.Join(" ", (s_Event["args"] as JArray ?? new JArray()).Select(a => (string?)a["value"] ?? (string?)a["description"] ?? ""));
                            var s_Type = (string?)s_Event["type"] ?? "log";
                            if (s_Type is "error" or "warning" || !s_Text.StartsWith("%c")) m_Log($"[console:{s_Type}] {s_Text.Replace("\n", " ")}");
                        }
                        catch { }
                    };
                }
                catch (Exception s_Ex) { m_Log("[preview] console capture: " + s_Ex.Message); }
                m_Web.CoreWebView2.NavigationCompleted += (_, e) => { if (!e.IsSuccess) { m_Status.Text = "page failed: " + e.WebErrorStatus; m_Log("[preview] page failed: " + e.WebErrorStatus); } else PostLayout(); };
                Navigate(m_Url);
            }
            catch (Exception s_Ex)
            {
                m_Status.Text = "WebView2 not available: " + s_Ex.Message;
                m_Log("[preview] WebView2 not available: " + s_Ex.Message + " — install the Microsoft Edge WebView2 runtime, or set ruffle.exe in Settings for the desktop player");
            }
        }

        public void Navigate(string p_Url)
        {
            m_Url = p_Url; m_Ready = false; Calls = 0;
            if (m_Web.CoreWebView2 == null) return;
            m_Status.Text = "loading " + p_Url;
            Navigating?.Invoke();
            m_Web.CoreWebView2.Navigate(p_Url);
        }

        void OnMessage(object? p_Sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject s_Msg;
            try { s_Msg = JObject.Parse(e.WebMessageAsJson); } catch (Exception s_Ex) { m_Log("[preview] bad message: " + s_Ex.Message); return; }
            var s_Comp = (string?)s_Msg["comp"] ?? ""; var s_Method = (string?)s_Msg["method"] ?? ""; var s_Args = s_Msg["args"] as JArray ?? new JArray();
            if (s_Comp == "page") { m_Status.Text = string.Join(" ", s_Args); m_Log("[preview] " + string.Join(" ", s_Args)); return; }
            if (s_Comp == "rue" && s_Method == "layout" && s_Args.Count >= 6)
            {
                PlayerLayout = ((string?)s_Args[0] ?? "", (int?)s_Args[1] ?? 0, (int?)s_Args[2] ?? 0, (int?)s_Args[3] ?? 0, (int?)s_Args[4] ?? 0, (double?)s_Args[5] ?? 1);
                m_Log($"[preview] player layout {PlayerLayout.Mode}: {PlayerLayout.Width}x{PlayerLayout.Height} px at ({PlayerLayout.Left},{PlayerLayout.Top}), pixel ratio {PlayerLayout.Dpr:0.##}");
                return;
            }
            if (s_Comp == "bridge" && s_Method == "ready")
            {
                m_Ready = true; m_Status.Text = "the game's components are proxied to the editor — every call shows in the log";
                m_Log("[preview] bridge ready: _global.Data proxied to the editor");
                BridgeReady?.Invoke();
                return;
            }
            ++Calls;
            try { m_OnCall(s_Comp, s_Method, s_Args); }
            catch (Exception s_Ex) { m_Log($"[preview] {s_Comp}.{s_Method} handler failed: {s_Ex.Message}"); }
            if (s_Msg["reply"] is { Type: not JTokenType.Null } s_Reply) m_Log($"[preview]   answered from the page: {s_Reply.ToString(Formatting.None)}");
        }

        /// <summary>Pushes data component values (key → value) the movie's getData will answer from, synchronously, on the page.</summary>
        public void SetData(IDictionary<string, object?> p_Values)
        {
            if (m_Web.CoreWebView2 == null) return;
            var s_Data = new JObject();
            foreach (var kv in p_Values) s_Data[kv.Key] = kv.Value == null ? JValue.CreateNull() : kv.Value as JToken ?? JToken.FromObject(kv.Value);
            m_Web.CoreWebView2.PostWebMessageAsJson(new JObject { ["op"] = "data", ["data"] = s_Data }.ToString(Formatting.None));
        }

        /// <summary>Pushes the localised strings (ID_* → text) the movie's formatString will answer from, and the language code.</summary>
        public void SetTexts(IDictionary<string, string> p_Texts, string p_Language)
        {
            if (m_Web.CoreWebView2 == null) return;
            var s_Texts = new JObject();
            foreach (var kv in p_Texts) s_Texts[kv.Key] = kv.Value;
            m_Web.CoreWebView2.PostWebMessageAsJson(new JObject { ["op"] = "texts", ["texts"] = s_Texts, ["language"] = p_Language }.ToString(Formatting.None));
        }

        /// <summary>The rendered page (the player included) as a PNG — a WPF snapshot cannot see inside a WebView2.</summary>
        public async Task CaptureAsync(string p_Path)
        {
            if (m_Web.CoreWebView2 == null) return;
            using var s_Stream = new MemoryStream();
            await m_Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, s_Stream);
            File.WriteAllBytes(p_Path, s_Stream.ToArray());
        }

        /// <summary>Calls path.method(a0, a1, a2) inside the movie ("_level0.sc1.instance1.PageHeader_01", "initialize", {data:{…}}).</summary>
        public void Dispatch(string p_Path, string p_Method, object? p_A0 = null, object? p_A1 = null, object? p_A2 = null, bool p_Echo = false)
        {
            if (m_Web.CoreWebView2 == null) return;
            var s_Msg = new JObject { ["path"] = p_Path, ["method"] = p_Method, ["a0"] = p_A0 == null ? JValue.CreateNull() : JToken.FromObject(p_A0), ["a1"] = p_A1 == null ? JValue.CreateNull() : JToken.FromObject(p_A1), ["a2"] = p_A2 == null ? JValue.CreateNull() : JToken.FromObject(p_A2), ["echo"] = p_Echo };
            m_Web.CoreWebView2.PostWebMessageAsJson(s_Msg.ToString(Formatting.None));
        }

        /// <summary>
        /// Hands an input concept to widgets the way the game does (measured 2026-09-16 on the game's own traffic): the page calls
        /// onInputConceptPressed/Released(concept) on the clips in the order given and, for a press, stops after the first widget that
        /// fires an event back to the engine during its handler (a release reaches them all). The page reports back which clips were
        /// handed the concept as rue.conceptHanded(pressed, concept, [paths]).
        /// </summary>
        public void DispatchConcept(int p_Concept, bool p_Pressed, IEnumerable<string> p_Paths)
        {
            if (m_Web.CoreWebView2 == null) return;
            var s_Msg = new JObject { ["op"] = "concept", ["concept"] = p_Concept, ["pressed"] = p_Pressed, ["paths"] = new JArray(p_Paths) };
            m_Web.CoreWebView2.PostWebMessageAsJson(s_Msg.ToString(Formatting.None));
        }
    }
}
