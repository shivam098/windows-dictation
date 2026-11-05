using System.Diagnostics;
using System.Windows.Automation;
using VoiceDictation.Core;

namespace VoiceDictation.App.Platform;

/// <summary>Inserts text where the cursor is, using Win32 input and UI Automation for the field checks.</summary>
internal sealed class WindowsDesktop : IDesktop
{
    private readonly int _ownProcessId = Environment.ProcessId;

    /// <summary>"Paste" (reliable in every editor) or "Type" (synthesized keystrokes).</summary>
    public static string InsertMethod { get; set; } = "Paste";

    public WindowInfo? GetForegroundWindow()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == 0)
        {
            return null;
        }

        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root != 0)
        {
            hwnd = root;
        }

        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == _ownProcessId)
        {
            return null;
        }

        return new WindowInfo(hwnd, (int)pid, ProcessName((int)pid), Win32.GetWindowTitle(hwnd));
    }

    public FieldCheck CheckFocusedField()
    {
        if (GetForegroundWindow() is { } foreground && Win32.IsProcessElevated(foreground.ProcessId) == true)
        {
            return FieldCheck.Elevated;
        }

        try
        {
            var check = Task.Run(() => Check(Focused()));
            return check.Wait(800) ? check.Result : FieldCheck.Unknown;
        }
        catch (AggregateException)
        {
            return FieldCheck.Unknown;
        }
    }

    public bool? FocusedTextEndsWithNonWhitespace()
    {
        try
        {
            var check = Task.Run(() => HasTrailingText(Focused()));
            return check.Wait(600) ? check.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    public void TypeText(string text)
    {
        text = text.Replace("\r\n", " ").Replace('\n', ' ');
        KeyboardInput.WaitForModifiersReleased();
        if (string.Equals(InsertMethod, "Paste", StringComparison.OrdinalIgnoreCase))
        {
            ClipboardPaste.Paste(text);
        }
        else
        {
            KeyboardInput.TypeUnicode(text);
        }
    }

    public void PressKeys(string chord)
    {
        KeyboardInput.WaitForModifiersReleased();
        KeyboardInput.PressChord(chord);
    }

    private static FieldCheck Check(AutomationElement? element)
    {
        if (element is null)
        {
            return FieldCheck.Unknown;
        }

        try
        {
            return element.Current.IsPassword ? FieldCheck.Password : FieldCheck.Safe;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return FieldCheck.Unknown;
        }
    }

    private static AutomationElement? Focused()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static bool? HasTrailingText(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                var value = ((ValuePattern)pattern).Current.Value;
                return value.Length > 0 && !char.IsWhiteSpace(value[^1]);
            }

            return null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
