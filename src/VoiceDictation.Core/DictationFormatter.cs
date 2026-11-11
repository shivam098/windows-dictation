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
            text = RemoveFillers(text);
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

    /// <summary>
    /// Drops sounds and discourse fillers that carry no meaning, in English and Hinglish: “um/uh”; tags such as
    /// “thik hai na?” and “…, thik hai?”; “basically”; “you know” / “I mean” between commas; a run of two or more fillers
    /// opening a sentence or clause (“Haan to like …”, “Okay so …”, “Thik hai to like …”); “like” before “for example”;
    /// a phrase said twice in a row (“for example, for example”); and sentences that are only fillers (“Ok.”) when
    /// something else was said. Single words that may carry meaning (“to”, “so”, “haan”, “like”, “right”) are kept.
    /// </summary>
    internal static string RemoveFillers(string text)
    {
        text = FillerRegex().Replace(text, " ");
        text = RemoveThikHaiNa(text);
        text = CommaTagRegex().Replace(text, ". ");
        text = BasicallyRegex().Replace(text, " ");
        text = YouKnowRegex().Replace(text, string.Empty);
        text = SpacesRegex().Replace(text, " ").Trim();
        text = OpeningRunRegex().Replace(text, string.Empty);
        text = LikeBeforeExampleRegex().Replace(text, string.Empty);
        for (var i = 0; i < 3; i++)
        {
            var deduped = RepeatedPhraseRegex().Replace(text, m => IsDeliberateRepeat(m.Groups[1].Value) ? m.Value : m.Groups[1].Value);
            if (deduped == text)
            {
                break;
            }

            text = deduped;
        }

        var sentences = SentenceSplitRegex().Split(text);
        var kept = sentences.Where(s => !FillerSentenceRegex().IsMatch(s.Trim())).ToList();
        return kept.Count > 0 && kept.Count < sentences.Length ? string.Join(' ', kept) : text;
    }

    // Words that end a clause; “… mein thik hai na?” is a tag, “Papa ki tabiyat thik hai na?” a real question.
    private static readonly HashSet<string> ClauseEnders = new(StringComparer.OrdinalIgnoreCase)
    {
        "hai", "hain", "ho", "hoga", "hogi", "honge", "tha", "thi", "the", "hoon", "hu", "hun", "chahiye", "chahie", "chaahie",
        "chaahiye", "mein", "me", "par", "pe", "ko", "se", "karna", "karo", "karenge", "karunga", "karungi", "kar", "dena",
        "lena", "do", "lo", "jao", "aana", "jaana", "milenge", "milte", "sakte", "sakta", "sakti",
    };

    private static readonly HashSet<string> ProtectingWords = new(StringComparer.OrdinalIgnoreCase) { "sab", "kya", "bilkul", "ekdum", "bhi", "kuch" };

    private static readonly HashSet<string> SpokenSymbolWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "line", "paragraph", "comma", "full", "stop", "question", "exclamation", "mark", "point", "colon", "semicolon", "period",
    };

    private static readonly HashSet<string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "zero", "oh", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "double", "triple",
    };

    /// <summary>
    /// “thik hai na” as a tag: inside a sentence (“… hota hai thik hai na to …”), as a sentence of its own, after a comma,
    /// or after a word that ends a clause (“… mein thik hai na?”). Kept when it is the question itself (“Papa ki tabiyat
    /// thik hai na?”, “Kya sab thik hai na?”) or when it is all that was said.
    /// </summary>
    private static string RemoveThikHaiNa(string text) => TagRegex().Replace(text, m =>
    {
        var before = text[..m.Index].TrimEnd();
        var prevWord = PreviousWordRegex().Match(before).Value;
        if (ProtectingWords.Contains(prevWord))
        {
            return m.Value;
        }

        var p = m.Groups["p"].Value;
        var after = text[(m.Index + m.Length)..].TrimStart();
        if (p is "" or "," && after.Length > 0 && char.IsLetterOrDigit(after[0]))
        {
            return ", "; // mid-sentence
        }

        var ownSentence = before.Length == 0 || before[^1] is '.' or '!' or '?' or '\n';
        if (ownSentence)
        {
            return before.Length == 0 && after.Length == 0 ? m.Value : " ";
        }

        return before[^1] == ',' || ClauseEnders.Contains(prevWord) ? ". " : m.Value;
    });

    /// <summary>Repeats that are meant: spoken punctuation (“new line new line”) and digits (“1 2 1 2”, “one two one two”).</summary>
    private static bool IsDeliberateRepeat(string phrase) =>
        phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => SpokenSymbolWords.Contains(w) || NumberWords.Contains(w) || w.All(char.IsDigit));

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

    // Fillers that may open a sentence or clause. “to/toh” only counts inside a run that doesn't end with it, so
    // “Ok to proceed” and “To do that” stay.
    private const string OpeningFiller = @"(?:haan|han|haa|so|ok|okay|ach+ha|acha|accha|achcha|th(?:i|ee)k\s+hai|matlab|chalo|alright|like)";

    // “thik hai na” as a tag (not “sab thik hai na?” = “is everything fine?”).
    [GeneratedRegex(@"\b(?:th(?:i|ee|ea)k|thick)\s+(?:hai|he|hain)\s+(?:na+|nah)(?![\p{L}'])\s*(?<p>[?.!,]?)", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[\p{L}]+$")]
    private static partial Regex PreviousWordRegex();

    [GeneratedRegex(@",\s*(?:th(?:i|ee|ea)k|thick)\s+(?:hai|he)\s*\?", RegexOptions.IgnoreCase)]
    private static partial Regex CommaTagRegex();

    [GeneratedRegex(@",?\s*\bbasically\b,?", RegexOptions.IgnoreCase)]
    private static partial Regex BasicallyRegex();

    [GeneratedRegex(@",\s*you know\s*(?=[,.!?]|$)|(?<=^|[.!?]\s+|,\s+)(?:you know|i mean),\s*", RegexOptions.IgnoreCase)]
    private static partial Regex YouKnowRegex();

    [GeneratedRegex(@"(?<=^|[.!?]\s+|,\s+|\n)(?:(?:" + OpeningFiller + @"|toh?)(?![\w'-]),?\s+)+" + OpeningFiller + @"(?![\w'-]),?\s+(?=[\p{L}\p{N}])(?!toh?\b|i\s+(?:said|told|mentioned|meant|was\s+saying)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex OpeningRunRegex();

    [GeneratedRegex(@"(?<=^|[.!?]\s+|,\s+)like,?\s+(?=for example\b|jaise\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LikeBeforeExampleRegex();

    // The same 2–4 words said twice in a row (“for example, for example”). Single words are left alone: Hindi doubles
    // words on purpose (“dheere dheere”).
    [GeneratedRegex(@"\b((?:[\p{L}\p{N}']+\s+){1,3}[\p{L}\p{N}']+),?\s+\1\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedPhraseRegex();

    [GeneratedRegex(@"(?<=[.!?])[ \t]+")]
    private static partial Regex SentenceSplitRegex();

    [GeneratedRegex(@"^(?:(?:ok|okay|th(?:i|ee)k\s+hai(?:\s+na)?|ach+ha|acha|accha|achcha|hmm+|alright|chalo)[\s,]*)+[.!?]*$", RegexOptions.IgnoreCase)]
    private static partial Regex FillerSentenceRegex();

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
