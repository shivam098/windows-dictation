using VoiceDictation.Core;

namespace VoiceDictation.Core.Tests;

public class DictationInserterTests
{
    private sealed class FakeDesktop : IDesktop
    {
        public WindowInfo? Foreground { get; set; } = new(1, 1, "slack", "Slack");

        public FieldCheck Field { get; set; } = FieldCheck.Safe;

        public bool? EndsWithText { get; set; } = false;

        public List<string> Actions { get; } = [];

        public WindowInfo? GetForegroundWindow() => Foreground;

        public FieldCheck CheckFocusedField() => Field;

        public bool? FocusedTextEndsWithNonWhitespace() => EndsWithText;

        public void TypeText(string text) => Actions.Add($"type:{text}");

        public void PressKeys(string chord) => Actions.Add($"keys:{chord}");
    }

    private readonly FakeDesktop _desktop = new();
    private readonly List<StatusUpdate> _statuses = [];
    private readonly DictationInserter _inserter;

    public DictationInserterTests()
    {
        _inserter = new DictationInserter(_desktop, new DictationFormatter(), new Dictionary<string, string> { ["notepad"] = "Enter" });
        _inserter.Status += _statuses.Add;
    }

    [Fact]
    public void Inserts_cleaned_text()
    {
        Assert.True(_inserter.Insert("um so the demo is ready comma can you check it question mark"));
        Assert.Equal(["type:So the demo is ready, can you check it?"], _desktop.Actions);
        Assert.Equal(StatusKind.Success, _statuses[^1].Kind);
    }

    [Fact]
    public void Adds_a_space_after_existing_text()
    {
        _desktop.EndsWithText = true;
        _inserter.Insert("Second sentence.");
        Assert.Equal(["type: Second sentence."], _desktop.Actions);
    }

    [Fact]
    public void Chat_apps_get_shift_enter_and_editors_get_enter_for_new_lines()
    {
        _inserter.Insert("first new line second");
        Assert.Equal(["type:First", "keys:Shift+Enter", "type:Second"], _desktop.Actions);

        _desktop.Actions.Clear();
        _desktop.Foreground = new WindowInfo(2, 2, "Notepad", "Untitled - Notepad");
        _inserter.Insert("first new line second");
        Assert.Equal(["type:First", "keys:Enter", "type:Second"], _desktop.Actions);
    }

    [Theory]
    [InlineData(FieldCheck.Password)]
    [InlineData(FieldCheck.Elevated)]
    public void Refuses_password_fields_and_admin_windows(FieldCheck check)
    {
        _desktop.Field = check;
        Assert.False(_inserter.Insert("secret"));
        Assert.Empty(_desktop.Actions);
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    [Fact]
    public void Unknown_fields_are_allowed_because_the_key_press_is_consent()
    {
        _desktop.Field = FieldCheck.Unknown;
        Assert.True(_inserter.Insert("hello"));
    }

    [Fact]
    public void No_window_means_nothing_is_typed()
    {
        _desktop.Foreground = null;
        Assert.False(_inserter.Insert("hello"));
        Assert.Empty(_desktop.Actions);
    }

    [Fact]
    public void Only_filler_words_types_nothing()
    {
        Assert.False(_inserter.Insert("um uh"));
        Assert.Empty(_desktop.Actions);
    }

    [Fact]
    public void Repeat_last_types_the_previous_text_again()
    {
        Assert.False(_inserter.RepeatLast());
        _inserter.Insert("hello there");
        _desktop.Actions.Clear();
        Assert.True(_inserter.RepeatLast());
        Assert.Equal(["type:Hello there"], _desktop.Actions);
    }
}
