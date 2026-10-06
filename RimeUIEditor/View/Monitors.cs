using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace RimeUIEditor.View
{
    /// <summary>
    /// The monitors, in screen pixels, and window placement on them: the headless seams put their windows on the
    /// secondary monitor (RUE_WINDOW_SCREEN=secondary) so a test never lands on the one the user works on, and the
    /// live preview opens its player on the monitor that holds the editor — where the user is working.
    /// </summary>
    public static class Monitors
    {
        public record Screen(int Left, int Top, int Width, int Height, bool Primary)
        {
            public int Right => Left + Width;
            public int Bottom => Top + Height;
            public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX { public int Size; public RECT Monitor; public RECT Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
        delegate bool MonitorEnumProc(IntPtr p_Monitor, IntPtr p_Dc, ref RECT p_Rect, IntPtr p_Data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr p_Dc, IntPtr p_Clip, MonitorEnumProc p_Proc, IntPtr p_Data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr p_Monitor, ref MONITORINFOEX p_Info);
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr p_Window, int x, int y, int w, int h, bool p_Repaint);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr p_Window, out RECT p_Rect);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct WINDOWPLACEMENT { public int Length, Flags, ShowCmd; public POINT MinPosition, MaxPosition; public RECT NormalPosition; }
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr p_Window, ref WINDOWPLACEMENT p_Placement);
        delegate bool EnumWindowsProc(IntPtr p_Window, IntPtr p_Data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc p_Proc, IntPtr p_Data);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr p_Window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr p_Window, out uint p_ProcessId);

        /// <summary>The visible top-level windows of a process (a player's main window may never be reported as MainWindowHandle).</summary>
        static List<IntPtr> WindowsOf(int p_ProcessId)
        {
            var s_Out = new List<IntPtr>();
            try
            {
                EnumWindows((h, _) =>
                {
                    if (IsWindowVisible(h)) { GetWindowThreadProcessId(h, out var s_Pid); if (s_Pid == (uint)p_ProcessId) s_Out.Add(h); }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return s_Out;
        }

        /// <summary>Every monitor's working area (taskbar excluded), in screen pixels.</summary>
        public static List<Screen> All()
        {
            var s_Out = new List<Screen>();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref RECT _, IntPtr _) =>
                {
                    var s_Info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
                    if (GetMonitorInfo(m, ref s_Info))
                        s_Out.Add(new Screen(s_Info.Work.Left, s_Info.Work.Top, s_Info.Work.Right - s_Info.Work.Left, s_Info.Work.Bottom - s_Info.Work.Top, (s_Info.Flags & 1) != 0));
                    return true;
                }, IntPtr.Zero);
            }
            catch { /* no user32: a single unknown screen */ }
            return s_Out;
        }

        /// <summary>The monitor that is not the primary one; null with a single monitor.</summary>
        public static Screen? Secondary() => All().Find(s => !s.Primary);

        /// <summary>The monitor a window (by its HWND) sits on, by its top-left corner; null when unknown.</summary>
        public static Screen? Of(IntPtr p_Window)
        {
            if (p_Window == IntPtr.Zero || !GetWindowRect(p_Window, out var r)) return null;
            return All().Find(s => s.Contains(r.Left + 8, r.Top + 8));
        }

        /// <summary>The working area of the monitor a WPF window sits on, in the window's own units (DIPs): what a resize may fill.</summary>
        public static Rect WorkingAreaOf(Window p_Window)
        {
            var s_Screen = Of(new WindowInteropHelper(p_Window).Handle);
            if (s_Screen == null) return SystemParameters.WorkArea;
            var s_Dpi = System.Windows.Media.VisualTreeHelper.GetDpi(p_Window).DpiScaleX;
            return new Rect(s_Screen.Left / s_Dpi, s_Screen.Top / s_Dpi, s_Screen.Width / s_Dpi, s_Screen.Height / s_Dpi);
        }

        /// <summary>True when the seams were asked to keep their windows off the primary monitor (RUE_WINDOW_SCREEN=secondary).</summary>
        public static bool SeamsOnSecondary => string.Equals(Environment.GetEnvironmentVariable("RUE_WINDOW_SCREEN"), "secondary", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The monitor the live preview's player opens on: the editor's own, wherever the user has put the editor (it opened on the
        /// other monitor once, and the user had moved the editor there) — or the secondary one for a seam asked to keep off the primary.
        /// Null when the editor's monitor is unknown: the window then opens where Windows puts it.
        /// </summary>
        public static Screen? ForPreview(IntPtr p_Editor, bool p_Seam) => p_Seam ? Secondary() : Of(p_Editor);

        /// <summary>Puts a WPF window on a monitor (centred, shrunk to fit) as soon as it has a handle; nothing with a null monitor.</summary>
        public static void Place(Window p_Window, Screen? p_Screen)
        {
            if (p_Screen == null) return;
            void Move()
            {
                var s_Handle = new WindowInteropHelper(p_Window).Handle;
                if (s_Handle == IntPtr.Zero || !GetWindowRect(s_Handle, out var r)) return;
                var w = System.Math.Min(r.Right - r.Left, p_Screen.Width); var h = System.Math.Min(r.Bottom - r.Top, p_Screen.Height);
                p_Window.WindowState = WindowState.Normal;   // a restored placement may have asked for maximised: the seam's window is the size it asks
                SetPixels(p_Window, s_Handle, p_Screen.Left + (p_Screen.Width - w) / 2, p_Screen.Top + (p_Screen.Height - h) / 2, w, h);
            }
            p_Window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (new WindowInteropHelper(p_Window).Handle != IntPtr.Zero) Move();
            else p_Window.SourceInitialized += (_, _) => Move();
        }

        /// <summary>
        /// Gives a WPF window a rectangle in screen pixels: moved now, and told the same rectangle in its own units, because WPF applies
        /// its Left/Top/Width/Height again when the window shows (a window moved only through user32 came back at its XAML size).
        /// </summary>
        static void SetPixels(Window p_Window, IntPtr p_Handle, int x, int y, int w, int h)
        {
            MoveWindow(p_Handle, x, y, w, h, true);
            var s_Dpi = System.Windows.Media.VisualTreeHelper.GetDpi(p_Window);
            p_Window.Left = x / s_Dpi.DpiScaleX; p_Window.Top = y / s_Dpi.DpiScaleY;
            p_Window.Width = w / s_Dpi.DpiScaleX; p_Window.Height = h / s_Dpi.DpiScaleY;
        }

        /// <summary>The seams' placement: the secondary monitor when asked for, else nothing.</summary>
        public static void PlaceForSeam(Window p_Window) { if (SeamsOnSecondary) Place(p_Window, Secondary()); }

        /// <summary>
        /// Where a window is, for the settings: its normal rectangle in screen pixels (the restored one while it is maximised) and
        /// whether it is maximised. Null before the window has a handle — the caller keeps what it had.
        /// </summary>
        public static EditorSettings.WindowPlacement? PlacementOf(Window p_Window)
        {
            var s_Handle = new WindowInteropHelper(p_Window).Handle;
            if (s_Handle == IntPtr.Zero) return null;
            var s_Placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(s_Handle, ref s_Placement)) return null;
            // the normal rectangle comes in workspace coordinates: from the corner of the primary monitor's working area
            var s_Primary = All().Find(s => s.Primary);
            var r = s_Placement.NormalPosition;
            return new EditorSettings.WindowPlacement
            {
                Left = r.Left + (s_Primary?.Left ?? 0), Top = r.Top + (s_Primary?.Top ?? 0),
                Width = r.Right - r.Left, Height = r.Bottom - r.Top,
                Maximized = p_Window.WindowState == WindowState.Maximized,
            };
        }

        /// <summary>
        /// Puts a window back where the settings say, as soon as it has a handle: on the monitor that holds the saved rectangle's
        /// centre when that monitor is still there (else nothing, Windows places it), shrunk and shifted to that monitor's working area,
        /// maximised again when it was. A seam's own placement, registered after this one, still wins.
        /// </summary>
        public static void Restore(Window p_Window, EditorSettings.WindowPlacement? p_Placement)
        {
            if (p_Placement == null || p_Placement.Width < 200 || p_Placement.Height < 100) return;
            var s_Screen = All().Find(s => s.Contains(p_Placement.Left + p_Placement.Width / 2, p_Placement.Top + p_Placement.Height / 2));
            if (s_Screen == null) return;
            void Move()
            {
                var s_Handle = new WindowInteropHelper(p_Window).Handle;
                if (s_Handle == IntPtr.Zero) return;
                var w = System.Math.Min(p_Placement.Width, s_Screen.Width); var h = System.Math.Min(p_Placement.Height, s_Screen.Height);
                var x = System.Math.Clamp(p_Placement.Left, s_Screen.Left, s_Screen.Right - w); var y = System.Math.Clamp(p_Placement.Top, s_Screen.Top, s_Screen.Bottom - h);
                SetPixels(p_Window, s_Handle, x, y, w, h);
                if (p_Placement.Maximized) p_Window.WindowState = WindowState.Maximized;
            }
            p_Window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (new WindowInteropHelper(p_Window).Handle != IntPtr.Zero) Move();
            else p_Window.SourceInitialized += (_, _) => Move();
        }

        /// <summary>
        /// Moves another process's main window onto a monitor once it exists (polled for up to p_Timeout): on a background thread,
        /// or on the caller's when p_Wait (a seam that exits right after launching would otherwise take the mover with it).
        /// </summary>
        public static void MoveProcessWindow(Process p_Process, Screen? p_Screen, TimeSpan p_Timeout, bool p_Wait = false)
        {
            if (p_Screen == null) return;
            if (p_Wait) { Poll(); return; }
            var s_Thread = new Thread(Poll) { IsBackground = true, Name = "window placement" };
            s_Thread.Start();
            void Poll()
            {
                var s_Deadline = DateTime.UtcNow + p_Timeout;
                while (DateTime.UtcNow < s_Deadline)
                {
                    try
                    {
                        if (p_Process.HasExited) return;
                        foreach (var s_Handle in WindowsOf(p_Process.Id))
                        {
                            if (!GetWindowRect(s_Handle, out var r) || r.Right - r.Left < 200 || r.Bottom - r.Top < 100) continue;   // the real window, not a tooltip
                            var w = System.Math.Min(r.Right - r.Left, p_Screen.Width); var h = System.Math.Min(r.Bottom - r.Top, p_Screen.Height);
                            MoveWindow(s_Handle, p_Screen.Left + (p_Screen.Width - w) / 2, p_Screen.Top + (p_Screen.Height - h) / 2, w, h, true);
                            return;
                        }
                    }
                    catch { return; }
                    Thread.Sleep(200);
                }
            }
        }
    }
}
