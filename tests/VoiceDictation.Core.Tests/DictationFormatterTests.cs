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

    [Fact]
    public void Cleans_a_long_hinglish_dictation()
    {
        const string spoken = "Haan to like English mein bole ki Indian mein bole. Bolte jao. Bekhate hain. Ok. Thik hai to like ek data scrapping tool banaana hai jismen basically for example koi bhi jaise business category hota hai thik hai na to like for example ham kisi business ko search karte hain thik hai na to business ko jo aap search kar rahe hain to aas paas ke jitne bhi uski areas hai. Thik hai na? To vah us area ke saare saare business listing ka vah data nikaal le. For example, like for example there is a place a, thik hai? To vahaan par, To us jagah par usi business se related aur bhi kitne businesses hain vah pata hona chaahie plus vah business jo place a hai vah kis rank par vah kis rank par hai Google business mein thik hai na? Vah pata hona chaahie and it should be. Grid format ki is area mein aap 20th number par aate hain, is number is area mein first number par aata hai, is tarike se ek final result hona chaahie. Ok.";
        const string clean = "English mein bole ki Indian mein bole. Bolte jao. Bekhate hain. Ek data scrapping tool banaana hai jismen for example koi bhi jaise business category hota hai, for example ham kisi business ko search karte hain, to business ko jo aap search kar rahe hain to aas paas ke jitne bhi uski areas hai. To vah us area ke saare saare business listing ka vah data nikaal le. For example there is a place a. To vahaan par, To us jagah par usi business se related aur bhi kitne businesses hain vah pata hona chaahie plus vah business jo place a hai vah kis rank par hai Google business mein. Vah pata hona chaahie and it should be. Grid format ki is area mein aap 20th number par aate hain, is number is area mein first number par aata hai, is tarike se ek final result hona chaahie.";
        Assert.Equal(clean, _formatter.Format(spoken));
    }

    [Theory]
    [InlineData("Dear John new line new line thanks for the update", "Dear John\n\nThanks for the update")]
    [InlineData("My PIN is one two one two", "My PIN is one two one two")]
    [InlineData("The code is 1 2 1 2", "The code is 1 2 1 2")]
    [InlineData("Papa ki tabiyat thik hai na?", "Papa ki tabiyat thik hai na?")]
    [InlineData("Ghar pe sab log thik hain na?", "Ghar pe sab log thik hain na?")]
    [InlineData("Kal milte hain thik hai na?", "Kal milte hain.")]
    [InlineData("So like I said we need more tests.", "So like I said we need more tests.")]
    [InlineData("So okay to proceed with the merge?", "So okay to proceed with the merge?")]
    [InlineData("Haan. Main aa raha hoon.", "Haan. Main aa raha hoon.")]
    [InlineData("Thik hai na?", "Thik hai na?")]
    public void Filler_removal_keeps_deliberate_repeats_and_real_questions(string input, string expected) =>
        Assert.Equal(expected, _formatter.Format(input));

    [Theory]
    [InlineData("Haan to like English mein bolo. Bolte jao.", "English mein bolo. Bolte jao.")]
    [InlineData("Ok. Thik hai to like ek data scrapping tool banaana hai.", "Ek data scrapping tool banaana hai.")]
    [InlineData("Category hota hai thik hai na to like for example ham search karte hain.", "Category hota hai, for example ham search karte hain.")]
    [InlineData("Uski ranking pata honi chahiye thik hai na? Vah important hai.", "Uski ranking pata honi chahiye. Vah important hai.")]
    [InlineData("There is a place A, thik hai? To vahaan par dekho.", "There is a place A. To vahaan par dekho.")]
    [InlineData("For example, like for example there is a cafe.", "For example there is a cafe.")]
    [InlineData("Jismen basically sab listings hon.", "Jismen sab listings hon.")]
    [InlineData("Okay so let's ship it.", "Let's ship it.")]
    [InlineData("So, you know, the build is green.", "So, the build is green.")]
    [InlineData("I mean, it works.", "It works.")]
    [InlineData("Vah kis rank par vah kis rank par hai.", "Vah kis rank par hai.")]
    [InlineData("Final result hona chahiye. Ok.", "Final result hona chahiye.")]
    public void Removes_english_and_hinglish_discourse_fillers(string input, string expected) =>
        Assert.Equal(expected, _formatter.Format(input));

    [Theory]
    [InlineData("Ok to proceed with the merge?")]
    [InlineData("To do that we need more data.")]
    [InlineData("Kya sab thik hai na?")]
    [InlineData("Do you know him?")]
    [InlineData("Right now the build is green.")]
    [InlineData("Dheere dheere chalo.")]
    [InlineData("Haan, main aa raha hoon.")]
    [InlineData("I mean it.")]
    [InlineData("I like it, you know the drill.")]
    [InlineData("Ok.")]
    [InlineData("Yes. I'll be there.")]
    [InlineData("Haan to main kal aaunga.")]
    public void Keeps_words_that_carry_meaning(string input) => Assert.Equal(input, _formatter.Format(input));

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
