using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CapslockPlus
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const uint VK_CAPITAL = 0x14;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private readonly HookProc _hookProc;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private IntPtr _hook;
        private bool _capsDown;
        private bool _disposed;
        private readonly HashSet<uint> _mappedKeys = new();

        public TrayApplicationContext()
        {
            _hookProc = HookCallback;

            try
            {
                IntPtr module = GetModuleHandle(null);
                if (module == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, module, 0);
                if (_hook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                _trayMenu = new ContextMenuStrip();
                _trayMenu.Items.Add("退出", null, (_, _) => ExitThread());

                _trayIcon = new NotifyIcon
                {
                    Icon = SystemIcons.Application,
                    Text = "CapslockPlus - Caps Lock + J/K/L/U",
                    ContextMenuStrip = _trayMenu,
                    Visible = true
                };
            }
            catch
            {
                if (_hook != IntPtr.Zero)
                    UnhookWindowsHookEx(_hook);

                _trayMenu?.Dispose();
                throw;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var keyInfo = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int message = wParam.ToInt32();
                bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                bool isUp = message == WM_KEYUP || message == WM_SYSKEYUP;

                if (keyInfo.vkCode == VK_CAPITAL)
                {
                    if (isDown) _capsDown = true;
                    if (isUp) _capsDown = false;

                    // Caps Lock 只作为组合键，不切换大小写。
                    return (IntPtr)1;
                }

                if (TryGetArrow(keyInfo.vkCode, out ushort arrow))
                {
                    if (isDown && _capsDown)
                    {
                        _mappedKeys.Add(keyInfo.vkCode);
                        SendArrow(arrow, keyUp: false);
                        return (IntPtr)1;
                    }

                    if (isUp && _mappedKeys.Remove(keyInfo.vkCode))
                    {
                        SendArrow(arrow, keyUp: true);
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool TryGetArrow(uint key, out ushort arrow)
        {
            arrow = key switch
            {
                0x4A => 0x25, // J -> 左
                0x4B => 0x28, // K -> 下
                0x4C => 0x27, // L -> 右
                0x55 => 0x26, // U -> 上
                _ => 0
            };

            return arrow != 0;
        }

        private static void SendArrow(ushort key, bool keyUp)
        {
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                data = new INPUTUNION
                {
                    keyboard = new KEYBDINPUT
                    {
                        wVk = key,
                        wScan = 0,
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };

            if (SendInput(1, ref input, Marshal.SizeOf<INPUT>()) != 1)
                Debug.WriteLine($"SendInput failed: {Marshal.GetLastWin32Error()}");
        }

        protected override void ExitThreadCore()
        {
            if (!_disposed)
            {
                _disposed = true;

                foreach (uint sourceKey in _mappedKeys)
                {
                    if (TryGetArrow(sourceKey, out ushort arrow))
                        SendArrow(arrow, keyUp: true);
                }

                _mappedKeys.Clear();

                if (_hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }

                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayMenu.Dispose();
            }

            base.ExitThreadCore();
        }

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public KEYBDINPUT keyboard;

            [FieldOffset(0)]
            public MOUSEINPUT mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook, HookProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, ref INPUT input, int size);
    }
}