using System.Windows.Interop;

namespace VoiceDictation.App.Platform;

/// <summary>Registers a system-wide hotkey using a message-only window.</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private static int _nextId = 0x5641;
    private readonly int Id = Interlocked.Increment(ref _nextId);
    private readonly HwndSource _source;
    private readonly bool _registered;

    public GlobalHotkey(string chord, Action pressed)
    {
        _source = new HwndSource(new HwndSourceParameters("VoiceDictationHotkey") { ParentWindow = new nint(-3) });
        _source.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
        {
            if (msg == Win32.WM_HOTKEY && wParam == Id)
            {
                pressed();
                handled = true;
            }

            return 0;
        });

        var (modifiers, vk) = Parse(chord);
        _registered = Win32.RegisterHotKey(_source.Handle, Id, modifiers | Win32.MOD_NOREPEAT, vk);
        Description = chord;
    }

    public bool IsRegistered => _registered;

    public string Description { get; }

    public void Dispose()
    {
        if (_registered)
        {
            Win32.UnregisterHotKey(_source.Handle, Id);
        }

        _source.Dispose();
    }

    private static (uint Modifiers, uint Vk) Parse(string chord)
    {
        uint modifiers = 0;
        uint vk = 0;
        foreach (var part in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Win32.MOD_CONTROL; break;
                case "alt": modifiers |= Win32.MOD_ALT; break;
                case "shift": modifiers |= Win32.MOD_SHIFT; break;
                case "win": modifiers |= Win32.MOD_WIN; break;
                default: vk = KeyboardInput.ParseKey(part); break;
            }
        }

        if (vk == 0 || modifiers == 0)
        {
            throw new ArgumentException($"Hotkey '{chord}' needs a modifier and a key.");
        }

        return (modifiers, vk);
    }
}
