using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceDictation.App.Platform;

/// <summary>
/// Low-level keyboard hook for Wispr-style dictation keys:
/// hold Ctrl+Win = push-to-talk; Ctrl+Win+Space = toggle hands-free; Esc = cancel while recording.
/// Any other key pressed with Ctrl+Win (e.g. Ctrl+Win+D) is a Windows shortcut, so recording is aborted.
/// Events are raised on the UI thread via the dispatcher; the hook callback itself stays minimal.
/// </summary>
internal sealed class DictationKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_CONTROL = 0x11, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SPACE = 0x20, VK_ESCAPE = 0x1B;

    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly HookProc _proc;
    private readonly nint _hook;
    private bool _combo;
    private bool _comboUsedOtherKey;
    private bool _comboUsedSpace;
    private bool _swallowSpaceUp;

    public DictationKeyHook(System.Windows.Threading.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    /// <summary>Ctrl+Win went down (start recording).</summary>
    public event Action? HoldStarted;

    /// <summary>Ctrl+Win released with nothing else pressed (stop and type).</summary>
    public event Action? HoldEnded;

    /// <summary>Another key was pressed with Ctrl+Win: it was a Windows shortcut, discard the recording.</summary>
    public event Action? HoldAborted;

    /// <summary>Ctrl+Win+Space.</summary>
    public event Action? HandsFreeToggled;

    /// <summary>Esc while <see cref="IsRecording"/>.</summary>
    public event Action? CancelPressed;

    public bool IsInstalled => _hook != 0;

    /// <summary>Set by the host so Esc is only captured while dictation is recording.</summary>
    public volatile bool IsRecording;

    /// <summary>False while the dictation keys are paused: every key passes through untouched.</summary>
    public volatile bool Enabled = true;

    public void Dispose()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
        }
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (!Enabled)
        {
            _combo = false;
            _swallowSpaceUp = false;
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (!Enabled)
        {
            _combo = false;
            _swallowSpaceUp = false;
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((info.flags & LLKHF_INJECTED) != 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var msg = (int)wParam;
        var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        var up = msg is WM_KEYUP or WM_SYSKEYUP;
        var vk = info.vkCode;
        var isCtrl = vk is VK_LCONTROL or VK_RCONTROL or VK_CONTROL;
        var isWin = vk is VK_LWIN or VK_RWIN;

        if (isCtrl || isWin)
        {
            // Read the other modifier's real state: a hook sees no input on the secure desktop (Win+L,
            // Ctrl+Alt+Del, UAC), so remembered key states can go stale.
            var ctrl = isCtrl ? down : IsDown(VK_CONTROL);
            var win = isWin ? down : IsDown(VK_LWIN) || IsDown(VK_RWIN);

            if (down && ctrl && win && !_combo)
            {
                _combo = true;
                _comboUsedOtherKey = false;
                _comboUsedSpace = false;
                Raise(HoldStarted);
            }
            else if (up && _combo)
            {
                _combo = false;
                if (isWin)
                {
                    // Releasing Win after Ctrl+Win would open the Start menu; an unassigned key in between prevents it.
                    KeyboardInput.TapUnassignedKey();
                }

                if (!_comboUsedOtherKey && !_comboUsedSpace)
                {
                    Raise(HoldEnded);
                }
            }

            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (down && _combo)
        {
            if (vk == VK_SPACE)
            {
                _comboUsedSpace = true;
                _swallowSpaceUp = true;
                Raise(HandsFreeToggled);
                return 1;
            }

            if (!_comboUsedOtherKey && !_comboUsedSpace)
            {
                _comboUsedOtherKey = true;
                Raise(HoldAborted);
            }
        }
        else if (down && vk == VK_ESCAPE && IsRecording)
        {
            Raise(CancelPressed);
            return 1;
        }
        else if (up && vk == VK_SPACE && _swallowSpaceUp)
        {
            // Only the key-up matching the swallowed Ctrl+Win+Space key-down.
            _swallowSpaceUp = false;
            return 1;
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

    private void Raise(Action? handler)
    {
        if (handler is not null)
        {
            _dispatcher.BeginInvoke(handler);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
