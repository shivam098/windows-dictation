namespace VoiceDictation.Core;

/// <summary>A top-level window identity.</summary>
public sealed record WindowInfo(nint Handle, int ProcessId, string ProcessName, string Title);

public enum FieldCheck
{
    /// <summary>Positively confirmed not to be a password field.</summary>
    Safe,
    Password,

    /// <summary>The foreground window runs as administrator; Windows silently drops synthesized input to it.</summary>
    Elevated,

    /// <summary>Accessibility query failed or timed out.</summary>
    Unknown,
}

public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record StatusUpdate(StatusKind Kind, string Message, string? Heard = null);

/// <summary>Operating-system side effects needed to insert text. Implemented with Win32/UI Automation; faked in tests.</summary>
public interface IDesktop
{
    WindowInfo? GetForegroundWindow();

    FieldCheck CheckFocusedField();

    /// <summary>True if the focused text field already ends in a non-space character (so a leading space is needed); null if unknown.</summary>
    bool? FocusedTextEndsWithNonWhitespace();

    void TypeText(string text);

    void PressKeys(string chord);
}

/// <summary>
/// Formats dictated speech and inserts it where the cursor is. The key press that started dictation is the user's
/// consent, so only password fields and administrator windows are refused (see docs/specs in windows-voice-assistant).
/// </summary>
public sealed class DictationInserter
{
    public const string DefaultNewLineKeys = "Shift+Enter";

    private readonly IDesktop _desktop;
    private readonly DictationFormatter _formatter;
    private readonly IReadOnlyDictionary<string, string> _newLineKeys;

    /// <param name="newLineKeysByProcess">
    /// Keys for a line break per process name (editors: Enter). Others get Shift+Enter, which chat apps treat as a new
    /// line instead of “send”.
    /// </param>
    public DictationInserter(IDesktop desktop, DictationFormatter formatter, IReadOnlyDictionary<string, string>? newLineKeysByProcess = null)
    {
        _desktop = desktop;
        _formatter = formatter;
        _newLineKeys = new Dictionary<string, string>(newLineKeysByProcess ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
    }

    public event Action<StatusUpdate>? Status;

    /// <summary>The last inserted text, for “paste last dictation”. Kept only in memory.</summary>
    public string? LastText { get; private set; }

    /// <summary>Formats the transcript and inserts it at the cursor. Returns false if nothing was inserted.</summary>
    public bool Insert(string transcript)
    {
        var formatted = _formatter.Format(transcript);
        if (formatted.Length == 0)
        {
            Report(StatusKind.Info, "I didn't catch anything to type.");
            return false;
        }

        return InsertFormatted(formatted, TranscriptText.Clean(transcript));
    }

    /// <summary>Inserts the last dictated text again.</summary>
    public bool RepeatLast()
    {
        if (string.IsNullOrEmpty(LastText))
        {
            Report(StatusKind.Info, "There's no previous dictation yet.");
            return false;
        }

        return InsertFormatted(LastText, null);
    }

    private bool InsertFormatted(string formatted, string? heard)
    {
        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window to type into.", heard);
            return false;
        }

        var refusal = _desktop.CheckFocusedField() switch
        {
            FieldCheck.Password => "That's a password field — I won't type there.",
            FieldCheck.Elevated => "That window runs as administrator; Windows won't let me type into it.",
            _ => null,
        };
        if (refusal is not null)
        {
            Report(StatusKind.Warning, refusal, heard);
            return false;
        }

        var leadingSpace = _desktop.FocusedTextEndsWithNonWhitespace() == true && formatted[0] != '\n';
        var newLine = _newLineKeys.GetValueOrDefault(window.ProcessName, DefaultNewLineKeys);
        var parts = (leadingSpace ? " " + formatted : formatted).Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                _desktop.PressKeys(newLine);
            }

            if (parts[i].Length > 0)
            {
                _desktop.TypeText(parts[i]);
            }
        }

        LastText = formatted;
        Report(StatusKind.Success, "Typed.", heard);
        return true;
    }

    private void Report(StatusKind kind, string message, string? heard = null) => Status?.Invoke(new StatusUpdate(kind, message, heard));
}
