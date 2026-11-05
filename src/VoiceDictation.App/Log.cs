namespace VoiceDictation.App;

/// <summary>
/// Operational log. Never pass transcripts, typed text, window titles, or audio here —
/// only states, timings, and error types.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            foreach (var old in Directory.GetFiles(AppPaths.LogsDir, "*.log").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)))
            {
                File.Delete(old);
            }

            _file = Path.Combine(AppPaths.LogsDir, $"{DateTime.Now:yyyyMMdd}.log");
        }
        catch (IOException)
        {
            _file = null;
        }
        catch (UnauthorizedAccessException)
        {
            _file = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        if (_file is null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                if (new FileInfo(_file) is { Exists: true, Length: > 5_000_000 })
                {
                    return;
                }

                File.AppendAllText(_file, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
