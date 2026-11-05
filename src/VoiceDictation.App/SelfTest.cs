using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using VoiceDictation.Core;

namespace VoiceDictation.App;

/// <summary>
/// Runs a script of transcripts through the real inserter and Windows input (no microphone), so dictation into
/// real apps can be verified end to end. Script lines:
///   ptt: &lt;transcript&gt;      as if dictated with Ctrl+Win
///   again                  paste the last dictation again (Alt+Shift+Z)
///   wait &lt;ms&gt;
///   fg                     record the foreground process name
///   read                   record the focused control's type, text length and hash (not its text)
///   expect: &lt;text&gt;         PASS/FAIL: does the focused control contain the script's phrase (\n = line break)
///   expectnot: &lt;text&gt;      PASS/FAIL: the focused control must NOT contain the phrase (e.g. refused password field)
///   keys: &lt;chord&gt;          press keys, e.g. "keys: Tab" to move to the next field
///   expectstatus: &lt;text&gt;   PASS/FAIL: does the latest status message contain the phrase
///   expecttitle: &lt;text&gt;    PASS/FAIL: does the foreground window title contain the phrase (title not written)
///   expectfocus: &lt;name&gt;    PASS/FAIL: does the focused control's accessible name contain the phrase
///   insert Paste|Type      switch the insertion method
///   stoponfail             stop the script at the first FAIL (use before steps that type)
///   clipboard              record a fingerprint (formats, hash, length) of the clipboard, not its text
///   # comment
/// Results are written to the output file; it never contains user text, window titles, or clipboard contents.
/// </summary>
internal static class SelfTest
{
    public static async Task RunAsync(string scriptPath, string outputPath, Dispatcher ui)
    {
        var output = new StringBuilder();
        var settings = AppSettings.LoadOrCreate();
        using var host = new DictationHost(settings, ui, persistState: false);
        var lastStatus = string.Empty;
        host.Status += s =>
        {
            lastStatus = s.Message;
            output.AppendLine($"   {s.Kind}: {Redact(s.Message)}");
        };

        // The same on-screen feedback as the app, so screenshots of a self-test show what a user would see.
        var pill = new UI.OverlayWindow { Pinned = true };
        host.Status += pill.ShowStatus;
        host.Activity += pill.ShowActivity;
        try
        {
            var stopOnFail = false;
            host.StartForSelfTest();
            foreach (var raw in await File.ReadAllLinesAsync(scriptPath).ConfigureAwait(false))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                output.AppendLine($"> {line}");
                if (stopOnFail && output.ToString().Contains("   FAIL:", StringComparison.Ordinal))
                {
                    // A failed check means the expected window/box isn't in front: typing on could go somewhere else.
                    output.AppendLine("!! stopped after a failed check");
                    break;
                }

                if (line == "stoponfail")
                {
                    stopOnFail = true;
                }
                else if (line.StartsWith("ptt:", StringComparison.Ordinal))
                {
                    var text = line[4..].Trim();
                    await host.RunOnQueueAsync(() => { host.Inserter.Insert(text); return Task.CompletedTask; }).ConfigureAwait(false);
                }
                else if (line == "again")
                {
                    await host.RunOnQueueAsync(() => { host.Inserter.RepeatLast(); return Task.CompletedTask; }).ConfigureAwait(false);
                }
                else if (line.StartsWith("wait ", StringComparison.Ordinal))
                {
                    await Task.Delay(int.Parse(line[5..])).ConfigureAwait(false);
                }
                else if (line.StartsWith("typing ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    Platform.KeyboardInput.TypingChunk = int.Parse(parts[1]);
                    Platform.KeyboardInput.TypingDelayMs = int.Parse(parts[2]);
                }
                else if (line.StartsWith("insert ", StringComparison.Ordinal))
                {
                    Platform.WindowsDesktop.InsertMethod = line[7..].Trim();
                }
                else if (line == "clipboard")
                {
                    output.AppendLine($"   clipboard fingerprint: {ClipboardFingerprint()}");
                }
                else if (line == "fg")
                {
                    // Process name only: window titles can contain the user's document or page names.
                    var fg = new Platform.WindowsDesktop().GetForegroundWindow();
                    output.AppendLine($"   foreground: {fg?.ProcessName}");
                }
                else if (line == "read")
                {
                    var text = ReadFocusedText();
                    output.AppendLine($"   focused: {text.Type} len={text.Value.Length} hash={Hash(text.Value)}");
                }
                else if (line.StartsWith("expectfocus:", StringComparison.Ordinal))
                {
                    // PASS/FAIL: is the focused control the one with this accessible name (a label from the test page).
                    var expected = line[12..].Trim();
                    var name = FocusedName();
                    var pass = name.Contains(expected, StringComparison.OrdinalIgnoreCase);
                    output.AppendLine($"   {(pass ? "PASS" : "FAIL")}: focused control {(pass ? "has" : "does not have")} the expected name");
                }
                else if (line.StartsWith("expecttitle:", StringComparison.Ordinal))
                {
                    // PASS/FAIL only: the title itself is never written.
                    var expected = line[12..].Trim();
                    var title = new Platform.WindowsDesktop().GetForegroundWindow()?.Title ?? string.Empty;
                    var pass = title.Contains(expected, StringComparison.OrdinalIgnoreCase);
                    output.AppendLine($"   {(pass ? "PASS" : "FAIL")}: foreground title {(pass ? "contains" : "does not contain")} the expected phrase");
                }
                else if (line.StartsWith("keys:", StringComparison.Ordinal))
                {
                    new Platform.WindowsDesktop().PressKeys(line[5..].Trim());
                }
                else if (line.StartsWith("expectstatus:", StringComparison.Ordinal))
                {
                    // Status posts arrive through the UI dispatcher; give them a moment.
                    await Task.Delay(300).ConfigureAwait(false);
                    var pass = lastStatus.Contains(line[13..].Trim(), StringComparison.OrdinalIgnoreCase);
                    output.AppendLine($"   {(pass ? "PASS" : "FAIL")}: last status {(pass ? "contains" : "does not contain")} the phrase");
                }
                else if (line.StartsWith("expectnot:", StringComparison.Ordinal))
                {
                    var unexpected = line[10..].Trim();
                    var actual = ReadFocusedText().Value;
                    var pass = !actual.Contains(unexpected, StringComparison.Ordinal);
                    output.AppendLine($"   {(pass ? "PASS" : "FAIL")}: focused text {(pass ? "does not contain" : "contains")} the phrase (field len={actual.Length})");
                }
                else if (line.StartsWith("expect:", StringComparison.Ordinal))
                {
                    // Records only whether the script's own phrase is present, never the field's other contents.
                    var expected = line[7..].Trim().Replace("\\n", "\n", StringComparison.Ordinal);
                    var actual = ReadFocusedText().Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                    var pass = actual.Contains(expected, StringComparison.Ordinal);
                    output.AppendLine($"   {(pass ? "PASS" : "FAIL")}: focused text {(pass ? "contains" : "does not contain")} the expected phrase (field len={actual.Length})");
                }

                await Task.Delay(400).ConfigureAwait(false);
            }

            // Let queued UI status posts land.
            await Task.Delay(500).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            output.AppendLine($"!! {ex.GetType().Name}: {ex.Message}");
        }

        await File.WriteAllTextAsync(outputPath, output.ToString()).ConfigureAwait(false);
    }

    /// <summary>Formats + a hash of the text, so restoration can be verified without recording the user's clipboard.</summary>
    private static string ClipboardFingerprint()
    {
        var result = string.Empty;
        var t = new Thread(() =>
        {
            var data = System.Windows.Clipboard.GetDataObject();
            var formats = data is null ? "(empty)" : string.Join(",", data.GetFormats(false).Take(6));
            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
            result = $"formats=[{formats}] textHash={hash} len={text.Length}";
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    /// <summary>Names, titles and heard text appear in “quotes” in status messages; keep them out of the output.</summary>
    private static string Redact(string message) =>
        System.Text.RegularExpressions.Regex.Replace(message, "“[^”]*”", "“…”");

    private static string FocusedName()
    {
        try
        {
            var task = Task.Run(() => AutomationElement.FocusedElement?.Current.Name ?? string.Empty);
            return task.Wait(2000) ? task.Result : string.Empty;
        }
        catch (AggregateException)
        {
            return string.Empty;
        }
    }

    private static (string Type, string Value) ReadFocusedText()
    {
        try
        {
            var task = Task.Run(() =>
            {
                var e = AutomationElement.FocusedElement;
                var type = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal);
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var v))
                {
                    return (type, ((ValuePattern)v).Current.Value);
                }

                if (e.TryGetCurrentPattern(TextPattern.Pattern, out var t))
                {
                    return (type, ((TextPattern)t).DocumentRange.GetText(4000));
                }

                return (type, string.Empty);
            });
            return task.Wait(2000) ? task.Result : ("(timeout)", string.Empty);
        }
        catch (Exception ex)
        {
            return ($"(error {ex.GetType().Name})", string.Empty);
        }
    }
}
