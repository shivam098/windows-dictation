using VoiceDictation.Core;

namespace VoiceDictation.Core.Tests;

public class DictationFormatterTests
{
    private readonly DictationFormatter _formatter = new(new DictationOptions
    {
        Replacements = new Dictionary<string, string> { ["git hub"] = "GitHub", ["kube"] = "Kubernetes" },
        Snippets = new Dictionary<string, string> { ["my signature"] = "Thanks,\nShivam" },
    });

    [Theory]
    [InlineData("Um, I think we should, uh, ship it.", "I think we should, ship it.")]
    [InlineData("Uh so the build is green.", "So the build is green.")]
    [InlineData("Hmm. Let me check.", "Let me check.")]
    [InlineData("The umbrella is here.", "The umbrella is here.")]
    public void Removes_filler_words_only(string input, string expected) => Assert.Equal(expected, _formatter.Format(input));

    [Theory]
    [InlineData("Hello comma how are you question mark", "Hello, how are you?")]
    [InlineData("Wait exclamation mark that worked", "Wait! That worked")]
    [InlineData("Notes colon buy milk", "Notes: buy milk")]
    [InlineData("The trial period ended period", "The trial period ended.")]
    public void Spoken_punctuation(string input, string expected) => Assert.Equal(expected, _formatter.Format(input));

    [Fact]
    public void Spoken_new_lines_become_line_breaks()
    {
        Assert.Equal("First item\nSecond item\n\nNext section", _formatter.Format("First item new line second item new paragraph next section"));
    }

    // Rule-based (no language model): "scratch that" removes the phrase spoken just before it,
    // back to the previous sentence or comma boundary.
    [Theory]
    [InlineData("Ship it today. Ship it tomorrow, scratch that, ship it Friday.", "Ship it today. Ship it Friday.")]
    [InlineData("I'll call you tomorrow. Scratch that. I'll email you.", "I'll email you.")]
    [InlineData("Send it to John scratch that send it to Sarah.", "Send it to Sarah.")]
    public void Inline_scratch_that_drops_the_previous_clause(string input, string expected) =>
        Assert.Equal(expected, _formatter.Format(input));

    [Fact]
    public void Replacements_are_whole_word_and_case_insensitive()
    {
        Assert.Equal("Push to GitHub and deploy to Kubernetes.", _formatter.Format("Push to Git Hub and deploy to kube."));
        Assert.Equal("Kubeflow stays.", _formatter.Format("Kubeflow stays."));
    }

    [Theory]
    [InlineData("My signature.")]
    [InlineData("Insert my signature")]
    public void Snippets_expand_whole_phrase(string input) => Assert.Equal("Thanks,\nShivam", _formatter.Format(input));

    [Fact]
    public void Continuation_lowercases_common_words_but_not_names()
    {
        Assert.Equal("and then deploy it.", _formatter.Format("And then deploy it.", continuesSentence: true));
        Assert.Equal("GitHub is down.", _formatter.Format("GitHub is down.", continuesSentence: true));
        Assert.Equal("I think so.", _formatter.Format("I think so.", continuesSentence: true));
    }

    [Fact]
    public void Search_fields_drop_the_trailing_full_stop()
    {
        Assert.Equal("Weather in Bangalore", _formatter.Format("Weather in Bangalore.", searchField: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Um.")]
    [InlineData("[BLANK_AUDIO]")]
    public void Nothing_left_returns_empty(string input) => Assert.Equal(string.Empty, _formatter.Format(input));
}
