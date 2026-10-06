using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace RimeUIEditor.View
{
    /// <summary>
    /// The real mouse, for the seam that runs the OLE drag gesture the way the user does (SendInput: moves and
    /// button presses the system delivers like a physical mouse, so DoDragDrop, the drop targets and the cursor
    /// feedback all run for real). Screen coordinates are physical pixels, as PointToScreen returns them.
    /// </summary>
    public static class NativeInput
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }

        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint p_Count, INPUT[] p_Inputs, int p_Size);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p_Point);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int p_X, int p_Y);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int p_Index);

        const uint c_Move = 0x0001, c_LeftDown = 0x0002, c_LeftUp = 0x0004, c_Absolute = 0x8000, c_VirtualDesk = 0x4000;
        const int c_XVirtual = 76, c_YVirtual = 77, c_CxVirtual = 78, c_CyVirtual = 79;

        static void Send(uint p_Flags, int p_Dx, int p_Dy)
        {
            var s_Input = new INPUT { type = 0, mi = new MOUSEINPUT { dx = p_Dx, dy = p_Dy, dwFlags = p_Flags } };
            if (SendInput(1, new[] { s_Input }, Marshal.SizeOf<INPUT>()) != 1) throw new Exception("SendInput failed: " + Marshal.GetLastWin32Error());
        }

        /// <summary>Moves the pointer to a screen point (absolute over the virtual desktop, so a second monitor works too).</summary>
        public static void MoveTo(Point p_Screen)
        {
            var s_X0 = GetSystemMetrics(c_XVirtual); var s_Y0 = GetSystemMetrics(c_YVirtual);
            var s_W = System.Math.Max(1, GetSystemMetrics(c_CxVirtual)); var s_H = System.Math.Max(1, GetSystemMetrics(c_CyVirtual));
            var s_Dx = (int)System.Math.Round((p_Screen.X - s_X0) * 65535.0 / s_W); var s_Dy = (int)System.Math.Round((p_Screen.Y - s_Y0) * 65535.0 / s_H);
            Send(c_Move | c_Absolute | c_VirtualDesk, s_Dx, s_Dy);
        }

        public static void LeftDown() => Send(c_LeftDown, 0, 0);
        public static void LeftUp() => Send(c_LeftUp, 0, 0);

        // the keyboard: INPUT with its KEYBDINPUT member (the union sits at offset 8 on x64, the whole struct is 40 bytes as for the mouse)
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUTK { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendInput")] static extern uint SendInputK(uint p_Count, INPUTK[] p_Inputs, int p_Size);
        const uint c_KeyUp = 0x0002;
        public const ushort VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28, VkReturn = 0x0D, VkEscape = 0x1B;

        /// <summary>A key pressed and released by virtual-key code, as the keyboard would deliver it to the active window.</summary>
        public static void KeyPress(ushort p_Vk)
        {
            if (IntPtr.Size != 8) throw new Exception("KeyPress is laid out for the 64-bit INPUT structure");
            var s_Down = new INPUTK { type = 1, ki = new KEYBDINPUT { wVk = p_Vk } };
            var s_Up = new INPUTK { type = 1, ki = new KEYBDINPUT { wVk = p_Vk, dwFlags = c_KeyUp } };
            if (SendInputK(1, new[] { s_Down }, 40) != 1) throw new Exception("SendInput (key down) failed: " + Marshal.GetLastWin32Error());
            System.Threading.Thread.Sleep(40);
            if (SendInputK(1, new[] { s_Up }, 40) != 1) throw new Exception("SendInput (key up) failed: " + Marshal.GetLastWin32Error());
        }
    }
}
