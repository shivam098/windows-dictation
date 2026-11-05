using System.Text;
using System.Text.RegularExpressions;

namespace VoiceDictation.Core;

public static partial class TranscriptText
{
    /// <summary>Removes Whisper annotations such as "[BLANK_AUDIO]" or "(music)" and speaker-change dashes.</summary>
    public static string Clean(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return string.Empty;
        }

        var text = AnnotationRegex().Replace(transcript, " ");
        text = WhitespaceRegex().Replace(text, " ").Trim();
        text = text.TrimStart('-', '>', ' ');
        return text.Any(char.IsLetterOrDigit) ? text : string.Empty;
    }

    /// <summary>Lowercase, punctuation-free form used only for command matching ("Co-Pilot." -> "copilot").</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (c is '-' or '\'' or '\u2019')
            {
                // Join hyphenated/apostrophe words so "co-pilot" matches "copilot".
            }
            else
            {
                sb.Append(' ');
            }
        }

        return WhitespaceRegex().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>Space-free key so "git hub copilot" and "GitHub Copilot" compare equal.</summary>
    public static string Compact(string? text) => Normalize(text).Replace(" ", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex AnnotationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
