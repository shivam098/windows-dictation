using System.Diagnostics;
using System.Runtime.InteropServices;
using VoiceDictation.Core;

namespace VoiceDictation.App.Platform;

/// <summary>
/// Low-level keyboard hook for tap-to-dictate keys (see <see cref="TapToDictateKeys"/>): tap Ctrl+Win to start
/// recording, tap it again to finish; Ctrl+Win+Space does the same; Esc cancels while recording.
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
    private readonly TapToDictateKeys _keys = new();
    private bool _enabled = true;

    public DictationKeyHook(System.Windows.Threading.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    /// <summary>Ctrl+Win (or Ctrl+Win+Space) tapped while not recording: start recording.</summary>
    public event Action? StartPressed;

    /// <summary>Ctrl+Win (or Ctrl+Win+Space) tapped again while recording: stop, transcribe and type.</summary>
    public event Action? FinishPressed;

    /// <summary>Esc while recording, or the Ctrl+Win that started it was part of a Windows shortcut: discard the recording.</summary>
    public event Action? CancelPressed;

    public bool IsInstalled => _hook != 0;

    /// <summary>
    /// Whether the keys treat dictation as recording (the next tap finishes it, Esc cancels it). The keys update it
    /// themselves; the host clears it when a recording stops on its own. Use only on the UI thread.
    /// </summary>
    public bool IsRecording
    {
        get => _keys.IsRecording;
        set => _keys.IsRecording = value;
    }

    /// <summary>False while the dictation keys are paused: every key passes through untouched. Use only on the UI thread.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _keys.Reset();
        }
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
        }
    }

    // Runs on the thread that installed the hook (the UI thread), so it shares _keys with the host safely.
    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code < 0 || !_enabled)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((info.flags & LLKHF_INJECTED) != 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var msg = (int)wParam;
        var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        if (!down && msg is not (WM_KEYUP or WM_SYSKEYUP))
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var key = info.vkCode switch
        {
            VK_LCONTROL or VK_RCONTROL or VK_CONTROL => DictationKey.Ctrl,
            VK_LWIN or VK_RWIN => DictationKey.Win,
            VK_SPACE => DictationKey.Space,
            VK_ESCAPE => DictationKey.Escape,
            _ => DictationKey.Other,
        };

        var ctrl = false;
        var win = false;
        if (key is DictationKey.Ctrl or DictationKey.Win)
        {
            // Read the other modifier's real state: a hook sees no input on the secure desktop (Win+L,
            // Ctrl+Alt+Del, UAC), so remembered key states can go stale.
            ctrl = key == DictationKey.Ctrl ? down : IsDown(VK_CONTROL);
            win = key == DictationKey.Win ? down : IsDown(VK_LWIN) || IsDown(VK_RWIN);
        }

        var result = _keys.Process(key, down, ctrl, win);
        if (result.SuppressStartMenu)
        {
            // Releasing Win after Ctrl+Win would open the Start menu; an unassigned key in between prevents it.
            KeyboardInput.TapUnassignedKey();
        }

        Raise(result.Command switch
        {
            DictationKeyCommand.Start => StartPressed,
            DictationKeyCommand.Finish => FinishPressed,
            DictationKeyCommand.Cancel => CancelPressed,
            _ => null,
        });

        return result.Swallow ? 1 : CallNextHookEx(_hook, code, wParam, lParam);
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
