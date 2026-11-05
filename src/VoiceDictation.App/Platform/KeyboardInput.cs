using System.Runtime.InteropServices;

namespace VoiceDictation.App.Platform;

/// <summary>Synthesizes keyboard input with SendInput. Scan codes are included so Remote Desktop forwards keys correctly.</summary>
internal static class KeyboardInput
{
    // An unassigned virtual key: sending it makes this process the last input source, which Windows
    // requires before it allows SetForegroundWindow, without triggering any application shortcut.
    private const ushort UnassignedVk = 0xE8;

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0x11, ["Control"] = 0x11, ["Shift"] = 0x10, ["Alt"] = 0x12, ["Win"] = 0x5B,
        ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Space"] = 0x20, ["Home"] = 0x24, ["End"] = 0x23,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
    };

    private static readonly HashSet<ushort> Extended = [0x2E, 0x24, 0x23, 0x26, 0x28, 0x25, 0x27, 0x21, 0x22, 0x5B];

    public static ushort ParseKey(string name)
    {
        if (Named.TryGetValue(name, out var vk))
        {
            return vk;
        }

        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            return char.ToUpperInvariant(name[0]);
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
        {
            return (ushort)(0x70 + f - 1);
        }

        throw new ArgumentException($"Unknown key '{name}'.");
    }

    public static IReadOnlyList<ushort> ParseChord(string chord)
    {
        var keys = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseKey).ToList();
        if (keys.Count == 0)
        {
            throw new ArgumentException("Empty key chord.");
        }

        return keys;
    }

    public static void PressChord(string chord)
    {
        var keys = ParseChord(chord);
        var inputs = new List<Win32.INPUT>();
        foreach (var k in keys)
        {
            inputs.Add(Key(k, up: false));
        }

        for (var i = keys.Count - 1; i >= 0; i--)
        {
            inputs.Add(Key(keys[i], up: true));
        }

        Send(inputs);
    }

    public static void Repeat(ushort vk, int count)
    {
        var inputs = new List<Win32.INPUT>(count * 2);
        for (var i = 0; i < count; i++)
        {
            inputs.Add(Key(vk, false));
            inputs.Add(Key(vk, true));
        }

        Send(inputs);
    }

    public static void TypeUnicode(string text)
    {
        var batch = Math.Max(1, TypingChunk);
        for (var start = 0; start < text.Length; start += batch)
        {
            var inputs = new List<Win32.INPUT>();
            foreach (var ch in text.AsSpan(start, Math.Min(batch, text.Length - start)))
            {
                inputs.Add(Unicode(ch, false));
                inputs.Add(Unicode(ch, true));
            }

            Send(inputs);
            if (start + batch < text.Length && TypingDelayMs > 0)
            {
                Thread.Sleep(TypingDelayMs);
            }
        }
    }

    public static void TapUnassignedKey() => Repeat(UnassignedVk, 1);

    /// <summary>
    /// Waits (up to <paramref name="timeoutMs"/>) until the user has released Ctrl/Alt/Shift/Win, so injected
    /// text or Ctrl+V isn't combined with keys still held from a hotkey (e.g. Alt+Shift+Z, Ctrl+Win).
    /// </summary>
    public static void WaitForModifiersReleased(int timeoutMs = 1000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline &&
               (IsDown(0x10) || IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C)))
        {
            Thread.Sleep(15);
        }
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>Characters per SendInput call and pause between calls. Some editors (Notepad) drop fast bursts.</summary>
    public static int TypingChunk { get; set; } = 48;

    public static int TypingDelayMs { get; set; } = 8;

    private static Win32.INPUT Key(ushort vk, bool up)
    {
        var flags = up ? Win32.KEYEVENTF_KEYUP : 0;
        if (Extended.Contains(vk))
        {
            flags |= Win32.KEYEVENTF_EXTENDEDKEY;
        }

        return new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            U = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT { wVk = vk, wScan = (ushort)Win32.MapVirtualKeyW(vk, Win32.MAPVK_VK_TO_VSC), dwFlags = flags },
            },
        };
    }

    private static Win32.INPUT Unicode(char ch, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = Win32.KEYEVENTF_UNICODE | (up ? Win32.KEYEVENTF_KEYUP : 0) },
        },
    };

    private static void Send(List<Win32.INPUT> inputs)
    {
        var array = inputs.ToArray();
        var sent = Win32.SendInput((uint)array.Length, array, Marshal.SizeOf<Win32.INPUT>());
        if (sent != array.Length)
        {
            throw new InvalidOperationException($"Windows blocked keyboard input (sent {sent}/{array.Length}, error {Marshal.GetLastPInvokeError()}).");
        }
    }
}
