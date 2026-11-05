using System.Text.RegularExpressions;

namespace VoiceDictation.Core;

public sealed record DictationOptions
{
    public bool RemoveFillerWords { get; init; } = true;

    /// <summary>Convert spoken “comma”, “question mark”, “new line”… into symbols.</summary>
    public bool SpokenPunctuation { get; init; } = true;

    /// <summary>Whole-word, case-insensitive fixes applied to dictated text (e.g. "git hub" → "GitHub").</summary>
    public IReadOnlyDictionary<string, string> Replacements { get; init; } = new Dictionary<string, string>();

    /// <summary>Saying the trigger (or “insert &lt;trigger&gt;”) as a whole phrase types the expansion instead.</summary>
    public IReadOnlyDictionary<string, string> Snippets { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Local, rule-based clean-up of dictated text: filler words, spoken punctuation, inline “scratch that”,
/// personal replacements, snippets, and capitalisation across pause-split phrases.
/// May return text containing '\n' (the caller presses the app's new-line keys for those).
/// </summary>
public sealed partial class DictationFormatter
{
    private static readonly HashSet<string> ContinuationWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "as", "at", "but", "by", "for", "from", "if", "in", "into", "is", "it", "of", "on", "or",
        "so", "than", "that", "the", "then", "this", "to", "was", "when", "where", "which", "while", "with", "because",
        "before", "after", "about", "are", "be", "can", "could", "should", "would", "will", "we", "you", "they", "he",
        "she", "my", "our", "your", "their", "its", "not", "also", "just", "maybe", "please", "there", "what", "how",
    };

    private readonly DictationOptions _options;
    private readonly List<(Regex Pattern, string Replacement)> _replacements;
    private readonly Dictionary<string, string> _snippets;

    public DictationFormatter(DictationOptions? options = null)
    {
        _options = options ?? new DictationOptions();
        _replacements = _options.Replacements
            .Where(r => !string.IsNullOrWhiteSpace(r.Key))
            .OrderByDescending(r => r.Key.Length)
            .Select(r => (new Regex($@"(?<![\w]){Regex.Escape(r.Key.Trim())}(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), r.Value))
            .ToList();
        _snippets = _options.Snippets
            .Where(s => !string.IsNullOrWhiteSpace(s.Key))
            .GroupBy(s => TranscriptText.Normalize(s.Key))
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
    }

    /// <param name="continuesSentence">The previously typed text did not end a sentence, so don't start with a capital.</param>
    /// <param name="searchField">Typing into a search or address box: drop a trailing full stop.</param>
    public string Format(string transcript, bool continuesSentence = false, bool searchField = false)
    {
        var text = TranscriptText.Clean(transcript);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (TryExpandSnippet(text, out var expansion))
        {
            return expansion;
        }

        if (_options.RemoveFillerWords)
        {
            text = FillerRegex().Replace(text, " ");
        }

        text = ApplyScratchThat(text);
        if (_options.SpokenPunctuation)
        {
            text = ApplySpokenPunctuation(text);
        }

        foreach (var (pattern, replacement) in _replacements)
        {
            text = pattern.Replace(text, replacement);
        }

        text = Tidy(text);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        text = continuesSentence ? LowercaseContinuation(text) : char.ToUpperInvariant(text[0]) + text[1..];
        if (searchField)
        {
            text = text.TrimEnd('.', ' ');
        }

        return text;
    }

    private bool TryExpandSnippet(string text, out string expansion)
    {
        var key = TranscriptText.Normalize(text);
        if (key.StartsWith("insert ", StringComparison.Ordinal))
        {
            key = key["insert ".Length..];
        }

        return _snippets.TryGetValue(key, out expansion!);
    }

    /// <summary>“Let's meet at 2, scratch that, 3 pm” → “Let's meet at 3 pm”: drops the clause before the marker.</summary>
    private static string ApplyScratchThat(string text)
    {
        while (true)
        {
            var match = ScratchRegex().Match(text);
            if (!match.Success)
            {
                return text;
            }

            var before = text[..match.Index].TrimEnd();
            var boundary = before.LastIndexOfAny(['.', '!', '?', ',', ';', ':', '\n']);
            var kept = boundary < 0 ? string.Empty : before[..(boundary + 1)];
            if (kept.EndsWith(','))
            {
                kept = kept[..^1];
            }

            text = kept + " " + text[(match.Index + match.Length)..].TrimStart(' ', ',', '.');
        }
    }

    private static string ApplySpokenPunctuation(string text)
    {
        text = NewParagraphRegex().Replace(text, "\n\n");
        text = NewLineRegex().Replace(text, "\n");
        text = SymbolRegex().Replace(text, m => m.Groups["word"].Value.ToLowerInvariant() switch
        {
            "comma" => ",",
            "full stop" => ".",
            "question mark" => "?",
            "exclamation mark" or "exclamation point" => "!",
            "colon" => ":",
            "semicolon" => ";",
            _ => m.Value,
        });
        return TrailingPeriodRegex().Replace(text, ".");
    }

    private static string Tidy(string text)
    {
        text = SpacesRegex().Replace(text, " ");
        text = SpaceBeforePunctuationRegex().Replace(text, "$1");
        text = DoublePunctuationRegex().Replace(text, "$1");
        text = SpaceAroundNewlineRegex().Replace(text, "\n");
        text = text.Trim(' ', ',');
        text = text.TrimStart('.', ' ');

        // Capitalise the first letter of each sentence and line.
        return SentenceStartRegex().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
    }

    private static string LowercaseContinuation(string text)
    {
        var firstWord = FirstWordRegex().Match(text).Value;
        return firstWord.Length > 1 && char.IsUpper(firstWord[0]) && firstWord[1..].All(char.IsLower) &&
               ContinuationWords.Contains(firstWord.ToLowerInvariant())
            ? char.ToLowerInvariant(text[0]) + text[1..]
            : text;
    }

    [GeneratedRegex(@"(?<![\w'-])(?:u+m+|u+h+|uhm|e+r+m+|e+r|a+h+|h+m+|m+h*m+)(?![\w'-])[,.]?", RegexOptions.IgnoreCase)]
    private static partial Regex FillerRegex();

    [GeneratedRegex(@"[,.;]?\s*\bscratch that\b[,.!]?", RegexOptions.IgnoreCase)]
    private static partial Regex ScratchRegex();

    [GeneratedRegex(@"[,.]?\s*\bnew paragraph\b[,.]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NewParagraphRegex();

    [GeneratedRegex(@"[,.]?\s*\bnew line\b[,.]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NewLineRegex();

    [GeneratedRegex(@"[,.]?\s*\b(?<word>comma|full stop|question mark|exclamation mark|exclamation point|colon|semicolon)\b[,.]?", RegexOptions.IgnoreCase)]
    private static partial Regex SymbolRegex();

    [GeneratedRegex(@"[,]?\s*\bperiod[.!?]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingPeriodRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"[ \t]+([,.;:!?])")]
    private static partial Regex SpaceBeforePunctuationRegex();

    [GeneratedRegex(@"([,.;:!?])[,.;:]+")]
    private static partial Regex DoublePunctuationRegex();

    [GeneratedRegex(@"[ \t]*\n[ \t]*")]
    private static partial Regex SpaceAroundNewlineRegex();

    [GeneratedRegex(@"((?:^|[.!?]\s+|\n))(\p{Ll})")]
    private static partial Regex SentenceStartRegex();

    [GeneratedRegex(@"^[\p{L}']+")]
    private static partial Regex FirstWordRegex();
}
