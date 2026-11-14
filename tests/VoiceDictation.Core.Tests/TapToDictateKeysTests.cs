using VoiceDictation.Core;

namespace VoiceDictation.Core.Tests;

public class TapToDictateKeysTests
{
    private static DictationKeyResult PressCtrlWin(TapToDictateKeys keys)
    {
        Assert.Equal(DictationKeyCommand.None, keys.Process(DictationKey.Ctrl, down: true, ctrlHeld: true, winHeld: false).Command);
        return keys.Process(DictationKey.Win, down: true, ctrlHeld: true, winHeld: true);
    }

    private static DictationKeyResult ReleaseCtrlWin(TapToDictateKeys keys)
    {
        var win = keys.Process(DictationKey.Win, down: false, ctrlHeld: true, winHeld: false);
        var ctrl = keys.Process(DictationKey.Ctrl, down: false, ctrlHeld: false, winHeld: false);
        Assert.Equal(DictationKeyCommand.None, ctrl.Command);
        return win;
    }

    private static List<DictationKeyCommand> Tap(TapToDictateKeys keys) =>
        [PressCtrlWin(keys).Command, ReleaseCtrlWin(keys).Command];

    [Fact]
    public void Tap_starts_recording_and_releasing_the_keys_does_not_stop_it()
    {
        var keys = new TapToDictateKeys();

        Assert.Equal(DictationKeyCommand.Start, PressCtrlWin(keys).Command);
        var release = ReleaseCtrlWin(keys);

        Assert.Equal(DictationKeyCommand.None, release.Command);
        Assert.True(release.SuppressStartMenu);
        Assert.True(keys.IsRecording);
    }

    [Fact]
    public void Second_tap_finishes_when_the_keys_are_released()
    {
        var keys = new TapToDictateKeys();
        Tap(keys);

        Assert.Equal(DictationKeyCommand.None, PressCtrlWin(keys).Command);
        Assert.True(keys.IsRecording);
        var release = ReleaseCtrlWin(keys);

        Assert.Equal(DictationKeyCommand.Finish, release.Command);
        Assert.True(release.SuppressStartMenu);
        Assert.False(keys.IsRecording);
        Assert.Equal(new[] { DictationKeyCommand.Start, DictationKeyCommand.None }, Tap(keys));
    }

    [Fact]
    public void Holding_the_keys_with_auto_repeat_starts_once_and_never_stops()
    {
        var keys = new TapToDictateKeys();

        Assert.Equal(DictationKeyCommand.Start, PressCtrlWin(keys).Command);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(DictationKeyCommand.None, keys.Process(DictationKey.Win, down: true, ctrlHeld: true, winHeld: true).Command);
        }

        Assert.Equal(DictationKeyCommand.None, ReleaseCtrlWin(keys).Command);
        Assert.True(keys.IsRecording);
    }

    [Fact]
    public void Ctrl_win_space_starts_and_finishes_like_ctrl_win_and_the_space_is_swallowed()
    {
        var keys = new TapToDictateKeys();

        Assert.Equal(DictationKeyCommand.Start, PressCtrlWin(keys).Command);
        Assert.Equal(new DictationKeyResult(DictationKeyCommand.None, Swallow: true), keys.Process(DictationKey.Space, down: true));
        Assert.Equal(new DictationKeyResult(DictationKeyCommand.None, Swallow: true), keys.Process(DictationKey.Space, down: false));
        Assert.Equal(DictationKeyCommand.None, ReleaseCtrlWin(keys).Command);
        Assert.True(keys.IsRecording);

        PressCtrlWin(keys);
        Assert.True(keys.Process(DictationKey.Space, down: true).Swallow);
        Assert.Equal(DictationKeyCommand.Finish, ReleaseCtrlWin(keys).Command);
        Assert.True(keys.Process(DictationKey.Space, down: false).Swallow);
        Assert.False(keys.IsRecording);
    }

    [Fact]
    public void Space_without_ctrl_win_passes_through()
    {
        var keys = new TapToDictateKeys();
        Tap(keys);

        Assert.Equal(default, keys.Process(DictationKey.Space, down: true));
        Assert.Equal(default, keys.Process(DictationKey.Space, down: false));
    }

    [Fact]
    public void Esc_cancels_while_recording_and_is_swallowed()
    {
        var keys = new TapToDictateKeys();
        Tap(keys);

        Assert.Equal(new DictationKeyResult(DictationKeyCommand.Cancel, Swallow: true), keys.Process(DictationKey.Escape, down: true));
        Assert.False(keys.IsRecording);
        Assert.Equal(default, keys.Process(DictationKey.Escape, down: false));
        Assert.Equal(DictationKeyCommand.Start, PressCtrlWin(keys).Command);
    }

    [Fact]
    public void Esc_passes_through_when_not_recording()
    {
        var keys = new TapToDictateKeys();

        Assert.Equal(default, keys.Process(DictationKey.Escape, down: true));
    }

    [Fact]
    public void Windows_shortcut_with_the_starting_ctrl_win_cancels_that_recording()
    {
        var keys = new TapToDictateKeys();

        Assert.Equal(DictationKeyCommand.Start, PressCtrlWin(keys).Command);
        var shortcut = keys.Process(DictationKey.Other, down: true);

        Assert.Equal(DictationKeyCommand.Cancel, shortcut.Command);
        Assert.False(shortcut.Swallow);
        Assert.False(keys.IsRecording);
        Assert.Equal(DictationKeyCommand.None, keys.Process(DictationKey.Other, down: true).Command);
        Assert.Equal(DictationKeyCommand.None, ReleaseCtrlWin(keys).Command);
    }

    [Fact]
    public void Windows_shortcut_while_recording_keeps_recording()
    {
        var keys = new TapToDictateKeys();
        Tap(keys);

        PressCtrlWin(keys);
        Assert.Equal(DictationKeyCommand.None, keys.Process(DictationKey.Other, down: true).Command);
        Assert.Equal(DictationKeyCommand.None, ReleaseCtrlWin(keys).Command);

        Assert.True(keys.IsRecording);
        Assert.Equal(new[] { DictationKeyCommand.None, DictationKeyCommand.Finish }, Tap(keys));
    }

    [Fact]
    public void Esc_while_finishing_ctrl_win_is_held_keeps_recording()
    {
        var keys = new TapToDictateKeys();
        Tap(keys);

        PressCtrlWin(keys);
        Assert.False(keys.Process(DictationKey.Escape, down: true).Swallow);
        Assert.Equal(DictationKeyCommand.None, ReleaseCtrlWin(keys).Command);
        Assert.True(keys.IsRecording);
    }

    [Fact]
    public void Recording_that_stopped_on_its_own_starts_again_on_the_next_tap()
    {
        var keys = new TapToDictateKeys();
        Tap(keys);

        keys.IsRecording = false;

        Assert.Equal(new[] { DictationKeyCommand.Start, DictationKeyCommand.None }, Tap(keys));
    }

    [Fact]
    public void Stop_on_its_own_while_finishing_keys_are_held_does_not_finish_twice()
    {
        var keys = new TapToDictateKeys();
        Tap(keys);

        PressCtrlWin(keys);
        keys.IsRecording = false;

        Assert.Equal(DictationKeyCommand.None, ReleaseCtrlWin(keys).Command);
    }

    [Fact]
    public void Ctrl_or_win_alone_does_nothing()
    {
        var keys = new TapToDictateKeys();

        Assert.Equal(default, keys.Process(DictationKey.Ctrl, down: true, ctrlHeld: true, winHeld: false));
        Assert.Equal(default, keys.Process(DictationKey.Ctrl, down: false, ctrlHeld: false, winHeld: false));
        Assert.Equal(default, keys.Process(DictationKey.Win, down: true, ctrlHeld: false, winHeld: true));
        Assert.Equal(default, keys.Process(DictationKey.Win, down: false, ctrlHeld: false, winHeld: false));
        Assert.False(keys.IsRecording);
    }

    [Fact]
    public void Releasing_ctrl_before_win_does_not_ask_to_suppress_start()
    {
        var keys = new TapToDictateKeys();
        PressCtrlWin(keys);

        var ctrlUp = keys.Process(DictationKey.Ctrl, down: false, ctrlHeld: false, winHeld: true);

        Assert.False(ctrlUp.SuppressStartMenu);
        Assert.Equal(default, keys.Process(DictationKey.Win, down: false, ctrlHeld: false, winHeld: false));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.0005f, 0f)]
    [InlineData(0.5f, 1f)]
    public void Audio_level_maps_quiet_to_zero_and_loud_to_one(float amplitude, float expected)
    {
        var samples = Enumerable.Range(0, 480).Select(i => i % 2 == 0 ? amplitude : -amplitude).ToArray();

        Assert.Equal(expected, AudioLevel.Of(samples), 3);
    }

    [Fact]
    public void Audio_level_is_between_zero_and_one_for_speech()
    {
        var samples = Enumerable.Range(0, 480).Select(i => i % 2 == 0 ? 0.03f : -0.03f).ToArray();

        Assert.InRange(AudioLevel.Of(samples), 0.3f, 0.8f);
    }
}
