namespace VoiceDictation.App;

/// <summary>Activity labels for a dictation; the status pill shows each with its own look.</summary>
internal static class DictationLabels
{
    /// <summary>Recording: level meter, live preview and how to finish.</summary>
    public const string Listening = "Listening…";

    /// <summary>Recording has stopped and the speech is being turned into text.</summary>
    public const string Transcribing = "Transcribing…";

    /// <summary>Shown under <see cref="Listening"/>.</summary>
    public const string ListeningHint = "Ctrl+Win to finish · Esc to cancel";
}
