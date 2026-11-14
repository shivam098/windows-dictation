namespace VoiceDictation.Core;

/// <summary>A key as far as the dictation keys care.</summary>
public enum DictationKey
{
    Ctrl,
    Win,
    Space,
    Escape,
    Other,
}

/// <summary>What a key event asks dictation to do.</summary>
public enum DictationKeyCommand
{
    None,

    /// <summary>Start recording.</summary>
    Start,

    /// <summary>Stop recording, then transcribe and type.</summary>
    Finish,

    /// <summary>Stop recording and throw it away (Esc, or Ctrl+Win was the start of a Windows shortcut).</summary>
    Cancel,
}

/// <param name="Command">What dictation should do.</param>
/// <param name="Swallow">True when the key must not reach the focused app.</param>
/// <param name="SuppressStartMenu">True when Win is released after Ctrl+Win: without another key in between, Windows would open Start.</param>
public readonly record struct DictationKeyResult(DictationKeyCommand Command, bool Swallow = false, bool SuppressStartMenu = false);

/// <summary>
/// Tap-to-dictate keys, kept apart from the Windows keyboard hook so they can be tested.
/// Tap Ctrl+Win (press and release) to start recording; releasing the keys does nothing, so they never have to be
/// held. Tap Ctrl+Win again to finish: recording stops when the keys are released, so the text is typed with no
/// modifier down. Ctrl+Win+Space does the same as Ctrl+Win. Esc cancels while recording.
/// Another key pressed with the Ctrl+Win that started recording (e.g. Ctrl+Win+D) is a Windows shortcut, so that
/// recording is cancelled; one pressed with the Ctrl+Win meant to finish leaves the recording running.
/// </summary>
public sealed class TapToDictateKeys
{
    private bool _combo;
    private bool _comboStarted;
    private bool _finishPending;
    private bool _comboUsedOtherKey;
    private bool _swallowSpaceUp;

    /// <summary>
    /// True from <see cref="DictationKeyCommand.Start"/> until <see cref="DictationKeyCommand.Finish"/> or
    /// <see cref="DictationKeyCommand.Cancel"/>. The host clears it when a recording stops on its own (error, time limit).
    /// </summary>
    public bool IsRecording { get; set; }

    /// <summary>Forgets any keys in progress (the hook was paused); <see cref="IsRecording"/> is unchanged.</summary>
    public void Reset()
    {
        _combo = false;
        _comboStarted = false;
        _finishPending = false;
        _comboUsedOtherKey = false;
        _swallowSpaceUp = false;
    }

    /// <param name="key">The key that went down or up.</param>
    /// <param name="down">True for key-down (including auto-repeat), false for key-up.</param>
    /// <param name="ctrlHeld">For Ctrl/Win events: whether Ctrl is down after this event.</param>
    /// <param name="winHeld">For Ctrl/Win events: whether a Win key is down after this event.</param>
    public DictationKeyResult Process(DictationKey key, bool down, bool ctrlHeld = false, bool winHeld = false)
    {
        switch (key)
        {
            case DictationKey.Ctrl or DictationKey.Win:
                if (down && ctrlHeld && winHeld && !_combo)
                {
                    _combo = true;
                    _comboUsedOtherKey = false;
                    if (IsRecording)
                    {
                        _comboStarted = false;
                        _finishPending = true;
                        return new(DictationKeyCommand.None);
                    }

                    // Start right away so the first words aren't lost; releasing the keys won't stop it.
                    IsRecording = true;
                    _comboStarted = true;
                    _finishPending = false;
                    return new(DictationKeyCommand.Start);
                }

                if (!down && _combo)
                {
                    _combo = false;
                    var suppressStart = key == DictationKey.Win;
                    if (_finishPending && !_comboUsedOtherKey && IsRecording)
                    {
                        _finishPending = false;
                        IsRecording = false;
                        return new(DictationKeyCommand.Finish, SuppressStartMenu: suppressStart);
                    }

                    _finishPending = false;
                    return new(DictationKeyCommand.None, SuppressStartMenu: suppressStart);
                }

                return new(DictationKeyCommand.None);

            case DictationKey.Space when down && _combo:
                // Ctrl+Win+Space is an alias for Ctrl+Win (it used to toggle hands-free).
                _swallowSpaceUp = true;
                return new(DictationKeyCommand.None, Swallow: true);

            case DictationKey.Space when !down && _swallowSpaceUp:
                // Only the key-up matching the swallowed key-down.
                _swallowSpaceUp = false;
                return new(DictationKeyCommand.None, Swallow: true);

            case DictationKey.Escape or DictationKey.Other when down && _combo:
                if (_comboUsedOtherKey)
                {
                    return new(DictationKeyCommand.None);
                }

                _comboUsedOtherKey = true;
                _finishPending = false;
                if (_comboStarted && IsRecording)
                {
                    _comboStarted = false;
                    IsRecording = false;
                    return new(DictationKeyCommand.Cancel);
                }

                return new(DictationKeyCommand.None);

            case DictationKey.Escape when down && IsRecording:
                IsRecording = false;
                _finishPending = false;
                return new(DictationKeyCommand.Cancel, Swallow: true);

            default:
                return new(DictationKeyCommand.None);
        }
    }
}
