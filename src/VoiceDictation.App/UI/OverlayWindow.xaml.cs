using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VoiceDictation.App.Platform;
using VoiceDictation.Core;

namespace VoiceDictation.App.UI;

/// <summary>
/// Status pill that never takes keyboard focus from the app being dictated into. Unless pinned, it hides while idle
/// and appears while recording, transcribing, or showing a message.
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly double[] BarWeights = [0.55, 0.8, 1.0, 0.8, 0.55];
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _dots = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Random _jitter = new();
    private string? _activity;
    private double _level;
    private int _dotCount;
    private bool _paused;
    private bool _pinned;

    public OverlayWindow()
    {
        InitializeComponent();
        _fade.Tick += (_, _) =>
        {
            _fade.Stop();
            MessageText.Visibility = Visibility.Collapsed;
            HeardText.Visibility = Visibility.Collapsed;
            UpdateVisibility();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        };
        SizeChanged += (_, _) => Reposition();
        MinimizeButton.Click += (_, _) => Pinned = false;
        MinimizeButton.Visibility = Visibility.Collapsed;
        HintText.Text = DictationLabels.ListeningHint;
        _dots.Tick += (_, _) =>
        {
            _dotCount = (_dotCount + 1) % 4;
            ModeDots.Text = new string('.', _dotCount);
        };
        ShowActivity(null);
    }

    /// <summary>Raised when <see cref="Pinned"/> changes (from the pill's button or the tray menu).</summary>
    public event Action<bool>? PinnedChanged;

    /// <summary>True: always on screen. False (default): shown only while there is something to show.</summary>
    public bool Pinned
    {
        get => _pinned;
        set
        {
            if (_pinned == value)
            {
                return;
            }

            _pinned = value;
            MinimizeButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdateVisibility();
            PinnedChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Shows what is happening; null returns to the resting label. <see cref="DictationLabels.Listening"/> shows the
    /// level meter and how to finish, <see cref="DictationLabels.Transcribing"/> animates until the text is typed.
    /// </summary>
    public void ShowActivity(string? activity)
    {
        var wasTranscribing = _activity == DictationLabels.Transcribing;
        _activity = activity;
        var listening = activity == DictationLabels.Listening;
        var transcribing = activity == DictationLabels.Transcribing;

        LevelMeter.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        Dot.Visibility = listening ? Visibility.Collapsed : Visibility.Visible;
        HintText.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        if (listening)
        {
            _level = 0;
            SetBars(0);
        }

        if (activity is null)
        {
            ModeText.Text = _paused ? "Paused · dictation keys are off" : "Ready · tap Ctrl+Win to dictate";
            Dot.Fill = Brush(_paused ? "#9AA0A6" : "#81C995");
        }
        else
        {
            ModeText.Text = transcribing ? activity.TrimEnd('…') : activity;
            Dot.Fill = listening ? Brush("#EA4335") : Brush("#FBBC04");
        }

        SetTranscribing(transcribing);
        if (wasTranscribing && !transcribing)
        {
            // The words heard while recording stayed on screen during transcription; the typed text replaces them.
            LiveText.Text = string.Empty;
            LiveText.Visibility = Visibility.Collapsed;
        }

        UpdateVisibility();
    }

    /// <summary>Moves the level meter while recording (<paramref name="level"/> from 0 to 1).</summary>
    public void ShowInputLevel(float level)
    {
        if (_activity != DictationLabels.Listening)
        {
            return;
        }

        // Rise at once, fall back gently, like a VU meter.
        _level = level > _level ? level : (_level * 0.6) + (level * 0.4);
        SetBars(_level);
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (_activity is null)
        {
            ShowActivity(null);
        }
    }

    public void ShowStatus(StatusUpdate update)
    {
        MessageText.Text = update.Message;
        MessageText.Foreground = update.Kind switch
        {
            StatusKind.Warning => Brush("#FDD663"),
            StatusKind.Error => Brush("#F28B82"),
            StatusKind.Success => Brush("#81C995"),
            _ => Brush("#E8EAED"),
        };
        MessageText.Visibility = string.IsNullOrEmpty(update.Message) ? Visibility.Collapsed : Visibility.Visible;

        // Successful insertions stay quiet: the text is already on screen where the cursor is.
        if (update.Kind == StatusKind.Success && update.Message == "Typed.")
        {
            MessageText.Visibility = Visibility.Collapsed;
        }

        HeardText.Text = string.IsNullOrEmpty(update.Heard) || update.Kind == StatusKind.Success ? string.Empty : $"Heard: “{update.Heard}”";
        HeardText.Visibility = HeardText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _fade.Stop();
        _fade.Start();
        UpdateVisibility();
    }

    /// <summary>Shows the "HI + EN" badge while dictation uses Hindi + English (“HI + EN · accurate” for the larger model).</summary>
    public void SetDictationLanguage(bool hinglish, bool accurate)
    {
        LanguageBadge.Visibility = hinglish ? Visibility.Visible : Visibility.Collapsed;
        LanguageBadgeText.Text = accurate ? "HI + EN · accurate" : "HI + EN";
    }

    /// <summary>Shows the live transcript while recording; null hides it. Long text keeps its latest words.</summary>
    public void ShowPreview(string? text)
    {
        const int MaxChars = 150;
        if (string.IsNullOrEmpty(text))
        {
            if (_activity == DictationLabels.Transcribing)
            {
                // Keep the last preview on screen until the transcription is typed.
                return;
            }

            LiveText.Text = string.Empty;
            LiveText.Visibility = Visibility.Collapsed;
            UpdateVisibility();
            return;
        }

        if (text.Length > MaxChars)
        {
            var start = text.Length - MaxChars;
            var space = text.IndexOf(' ', start);
            text = "…" + text[(space > 0 && space < text.Length - 1 ? space + 1 : start)..];
        }

        LiveText.Text = text;
        LiveText.Opacity = _activity == DictationLabels.Transcribing ? 0.6 : 1;
        LiveText.Visibility = Visibility.Visible;
        MessageText.Visibility = Visibility.Collapsed;
        HeardText.Visibility = Visibility.Collapsed;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var busy = _activity is not null
            || LiveText.Visibility == Visibility.Visible
            || MessageText.Visibility == Visibility.Visible
            || HeardText.Visibility == Visibility.Visible;
        if (_pinned || busy)
        {
            if (!IsVisible)
            {
                Show();
            }
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + ((area.Width - ActualWidth) / 2);
        Top = area.Bottom - ActualHeight - 16;
    }

    private void SetBars(double level)
    {
        var bars = LevelMeter.Children;
        for (var i = 0; i < bars.Count && i < BarWeights.Length; i++)
        {
            var wobble = level > 0.05 ? 0.8 + (_jitter.NextDouble() * 0.4) : 1;
            ((FrameworkElement)bars[i]).Height = 4 + (14 * Math.Clamp(level * BarWeights[i] * wobble, 0, 1));
        }
    }

    private void SetTranscribing(bool transcribing)
    {
        ModeDots.Visibility = transcribing ? Visibility.Visible : Visibility.Collapsed;
        if (transcribing)
        {
            LiveText.Opacity = 0.6;
            if (!_dots.IsEnabled)
            {
                _dotCount = 0;
                ModeDots.Text = string.Empty;
                _dots.Start();
                Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
        }
        else if (_dots.IsEnabled)
        {
            _dots.Stop();
            Dot.BeginAnimation(OpacityProperty, null);
            LiveText.Opacity = 1;
        }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
