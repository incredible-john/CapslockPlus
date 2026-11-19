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
        private Icon? _appIcon;
        private IntPtr _hook;
        private bool _capsDown;
        private bool _disposed;
        private readonly HashSet<uint> _mappedKeys = new();
        private readonly HashSet<uint> _shiftMappedKeys = new();

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

                _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                    ?? throw new InvalidOperationException("Unable to load the application icon.");

                _trayMenu = new ContextMenuStrip();
                _trayMenu.Items.Add("退出", null, (_, _) => ExitThread());

                _trayIcon = new NotifyIcon
                {
                    Icon = _appIcon,
                    Text = "CapslockPlus - Caps Lock + S/F/E/D/P/;/U/O",
                    ContextMenuStrip = _trayMenu,
                    Visible = true
                };
            }
            catch
            {
                if (_hook != IntPtr.Zero)
                    UnhookWindowsHookEx(_hook);

                _appIcon?.Dispose();
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

                if (TryGetMappedKey(keyInfo.vkCode, out ushort mappedKey, out bool requiresShift))
                {
                    if (isDown && _capsDown)
                    {
                        _mappedKeys.Add(keyInfo.vkCode);
                        if (requiresShift && _shiftMappedKeys.Add(keyInfo.vkCode) && _shiftMappedKeys.Count == 1)
                            SendMappedKey(0xA0, keyUp: false);

                        SendMappedKey(mappedKey, keyUp: false);
                        return (IntPtr)1;
                    }

                    if (isUp && _mappedKeys.Remove(keyInfo.vkCode))
                    {
                        SendMappedKey(mappedKey, keyUp: true);
                        if (_shiftMappedKeys.Remove(keyInfo.vkCode) && _shiftMappedKeys.Count == 0)
                            SendMappedKey(0xA0, keyUp: true);

                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool TryGetMappedKey(uint key, out ushort mappedKey, out bool requiresShift)
        {
            mappedKey = key switch
            {
                0x53 => 0x25, // S -> 左
                0x46 => 0x27, // F -> 右
                0x45 => 0x26, // E -> 上
                0x44 => 0x28, // D -> 下
                0x50 => 0x24, // P -> Home
                0xBA => 0x23, // ; -> End
                0x55 => 0x24, // U -> Shift+Home
                0x4F => 0x23, // O -> Shift+End
                _ => 0
            };
            requiresShift = key == 0x55 || key == 0x4F;

            return mappedKey != 0;
        }

        private static void SendMappedKey(ushort key, bool keyUp)
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
                    if (TryGetMappedKey(sourceKey, out ushort mappedKey, out _))
                        SendMappedKey(mappedKey, keyUp: true);
                }

                if (_shiftMappedKeys.Count > 0)
                    SendMappedKey(0xA0, keyUp: true);

                _mappedKeys.Clear();
                _shiftMappedKeys.Clear();

                if (_hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }

                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _appIcon?.Dispose();
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