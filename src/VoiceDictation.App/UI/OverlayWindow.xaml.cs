using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromSeconds(5) };
    private string? _activity;
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

    /// <summary>Shows what is happening (“Dictating…”, “Transcribing…”); null returns to the resting label.</summary>
    public void ShowActivity(string? activity)
    {
        _activity = activity;
        if (activity is null)
        {
            ModeText.Text = _paused ? "Paused · dictation keys are off" : "Ready · hold Ctrl+Win to dictate";
            Dot.Fill = Brush(_paused ? "#9AA0A6" : "#81C995");
        }
        else
        {
            ModeText.Text = activity;
            Dot.Fill = IsDictating(activity) ? Brush("#EA4335") : Brush("#FBBC04");
        }

        UpdateVisibility();
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

    private static bool IsDictating(string? activity) => activity?.StartsWith("Dictating", StringComparison.Ordinal) == true;

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
