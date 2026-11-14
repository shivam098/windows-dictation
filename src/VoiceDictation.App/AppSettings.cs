using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceDictation.App;

internal static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceDictation");

    /// <summary>Where downloaded models go: a "models" folder next to the exe (portable zip with models) or per-user data.</summary>
    public static string ModelsDir { get; } = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "models"))
        ? Path.Combine(AppContext.BaseDirectory, "models")
        : Path.Combine(DataDir, "models");

    /// <summary>Models already downloaded by Voice Assistant are reused (read only) instead of downloading them again.</summary>
    public static string VoiceAssistantModelsDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistant", "models");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Present while the status pill is shown all the time (by default it appears only while it has something to say).</summary>
    public static string OverlayPinnedFlag => Path.Combine(DataDir, "overlay-pinned");

    /// <summary>Present while dictation uses the Hindi + English model.</summary>
    public static string HinglishDictationFlag => Path.Combine(DataDir, "dictation-hinglish");

    /// <summary>Present while Hindi + English dictation uses the larger, more accurate (slower) model.</summary>
    public static string HinglishAccurateFlag => Path.Combine(DataDir, "dictation-hinglish-accurate");

    /// <summary>Present while the hotkeys are paused from the tray.</summary>
    public static string PausedFlag => Path.Combine(DataDir, "paused");

    public static string SettingsFile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceDictation", "settings.json");

    /// <summary>Full path of a model file, looking in <see cref="ModelsDir"/> then Voice Assistant's models; null if absent.</summary>
    public static string? FindModel(string file)
    {
        foreach (var dir in new[] { ModelsDir, VoiceAssistantModelsDir })
        {
            var path = Path.Combine(dir, file);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}

/// <summary>On/off UI choices remembered as empty flag files, so the user's settings.json is never rewritten.</summary>
internal static class StateFlags
{
    public static void Set(string path, bool on)
    {
        try
        {
            if (on)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"couldn't save {Path.GetFileName(path)}: {ex.GetType().Name}");
        }
    }
}

internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ModelFile { get; set; } = "ggml-small.en-q8_0.bin";

    /// <summary>Silero voice-activity model: skips silence and splits long recordings at pauses. Optional but recommended.</summary>
    public string VadModelFile { get; set; } = "ggml-silero-v6.2.0.bin";

    /// <summary>Used when "Hindi + English" is switched on (Alt+Shift+H or the tray); types Hindi in Roman script.</summary>
    public string HinglishModelFile { get; set; } = "ggml-hinglish-swift-q8_0.bin";

    /// <summary>
    /// "Hindi + English (accurate)": a larger model (834 MB, about 10x slower, fewer mistakes). Typed text comes from it;
    /// the live preview keeps using the fast model.
    /// </summary>
    public string HinglishAccurateModelFile { get; set; } = "ggml-hinglish-apex-q8_0.bin";

    public string DictationLanguageHotkey { get; set; } = "Alt+Shift+H";

    public string PasteLastHotkey { get; set; } = "Alt+Shift+Z";

    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount * 2 / 3, 2, 8);

    /// <summary>768 tokens covers 15.36 s of audio and is ~3x faster than the default 30 s window.</summary>
    public int AudioContextSize { get; set; } = 768;

    /// <summary>Optional vocabulary hint. Off by default: Whisper can echo prompt text on near-silent input.</summary>
    public string? VocabularyPrompt { get; set; }

    /// <summary>-1 uses the Windows default recording device.</summary>
    public int MicrophoneDevice { get; set; } = -1;

    /// <summary>
    /// "Paste": insert via the clipboard (reliable everywhere; kept out of Clipboard History and your previous
    /// clipboard is restored). "Type": synthesized keystrokes (some apps drop characters).
    /// </summary>
    public string InsertMethod { get; set; } = "Paste";

    /// <summary>Short tones when recording starts and ends.</summary>
    public bool SoundFeedback { get; set; } = true;

    /// <summary>Show the words in the pill as you speak (uses extra CPU while recording).</summary>
    public bool LivePreview { get; set; } = true;

    /// <summary>
    /// A recording that is still going after this many seconds (Ctrl+Win was never tapped again) is finished and
    /// typed, so the microphone can't stay on by mistake. 0 turns the limit off.
    /// </summary>
    public int MaxDictationSeconds { get; set; } = 300;

    public bool RemoveFillerWords { get; set; } = true;

    public bool SpokenPunctuation { get; set; } = true;

    /// <summary>Whole-word fixes applied to dictated text (personal dictionary), e.g. { "git hub": "GitHub" }.</summary>
    public Dictionary<string, string> Replacements { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["git hub"] = "GitHub",
        ["co-pilot"] = "Copilot",
        ["co pilot"] = "Copilot",
    };

    /// <summary>Say the trigger (or “insert &lt;trigger&gt;”) to type the text, e.g. { "my signature": "Thanks,\nShivam" }.</summary>
    public Dictionary<string, string> Snippets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keys pressed for “new line” per process name. Apps not listed get Shift+Enter, which chat apps treat as a new
    /// line rather than “send”.
    /// </summary>
    public Dictionary<string, string> NewLineKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Notepad"] = "Enter",
        ["WINWORD"] = "Enter",
        ["Code"] = "Enter",
        ["devenv"] = "Enter",
    };

    public static AppSettings LoadOrCreate()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Replacements = new(loaded.Replacements ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    loaded.Snippets = new(loaded.Snippets ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    loaded.NewLineKeys = new(loaded.NewLineKeys ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"settings unreadable, using defaults: {ex.GetType().Name}");
            return new AppSettings();
        }

        var settings = new AppSettings();
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"couldn't write default settings: {ex.GetType().Name}");
        }

        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
    }
}
