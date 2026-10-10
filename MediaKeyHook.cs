using System.Runtime.InteropServices;

namespace SonarController
{
    /// <summary>
    /// Intercepts the standard multimedia volume keys system-wide and redirects
    /// them to the Windows playback device Sonar is sending audio out to.
    ///
    /// IMPORTANT: a low-level keyboard hook is only delivered to the thread that
    /// installed it, and Windows silently discards a hook whose thread stops
    /// pumping messages. It must therefore be created on - and disposed from - the
    /// thread running the WinForms message loop, which is why the tray context
    /// owns it rather than the bridge worker.
    /// </summary>
    internal sealed class MediaKeyHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int HC_ACTION = 0;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_VOLUME_MUTE = 0xAD;
        private const int VK_VOLUME_DOWN = 0xAE;
        private const int VK_VOLUME_UP = 0xAF;

        private readonly AudioDeviceVolume _volume;

        // Kept in a field so the delegate is not collected while the OS holds it.
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hook;
        private bool _disposed;

        public MediaKeyHook(AudioDeviceVolume volume)
        {
            _volume = volume ?? throw new ArgumentNullException(nameof(volume));
            _proc = HookCallback;

            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

            if (_hook == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()}).");
            }

            Log.Info("Media key hook installed.");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode != HC_ACTION)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            var message = (int)wParam;
            var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
            var isUp = message is WM_KEYUP or WM_SYSKEYUP;

            if (!isDown && !isUp)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var vk = (int)info.VkCode;

            if (vk is not (VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE))
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            // With nothing to steer, leave the keys entirely alone so Windows
            // keeps handling them against the default device as usual.
            if (!_volume.HasTargets)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            // Swallow the release as well as the press: the OS reacts to the pair,
            // so letting the release through would leave it half-handled.
            if (isUp)
                return 1;

            switch (vk)
            {
                case VK_VOLUME_UP:
                    _volume.StepUp();
                    break;
                case VK_VOLUME_DOWN:
                    _volume.StepDown();
                    break;
                default:
                    _volume.ToggleMute();
                    break;
            }

            // Reported as handled, which is what suppresses the shell's own
            // volume OSD and its adjustment of the default device.
            return 1;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_hook == IntPtr.Zero)
                return;

            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;

            Log.Info("Media key hook removed.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}