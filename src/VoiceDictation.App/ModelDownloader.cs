using System.Security.Cryptography;

namespace VoiceDictation.App;

/// <summary>
/// Downloads missing speech models on first use (portable zips ship without them), verifying SHA-256
/// before use. Hashes are the Hugging Face LFS object ids (or of our own release asset); a mismatched download is deleted.
/// </summary>
internal static class ModelDownloader
{
    private static readonly Dictionary<string, (string Url, string Sha256)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ggml-small.en-q8_0.bin"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q8_0.bin", "67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067"),
        ["ggml-base.en-q8_0.bin"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en-q8_0.bin", "a4d4a0768075e13cfd7e19df3ae2dbc4a68d37d36a7dad45e8410c9a34f8c87e"),
        ["ggml-base.en.bin"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin", "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002"),
        ["ggml-silero-v6.2.0.bin"] = ("https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin", "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987"),

        // Oriserve/Whisper-Hindi2Hinglish-Swift (Apache-2.0), converted with tools/ModelConvert and quantized to q8_0.
        ["ggml-hinglish-swift-q8_0.bin"] = ("https://github.com/shivam098/windows-voice-assistant/releases/download/models-hinglish-swift-1/ggml-hinglish-swift-q8_0.bin", "0cf6e7077971d9aa43963b04bf764a47600e873f7e0f3f0f9742323560171245"),
    };

    public static bool CanDownload(string file) => Known.ContainsKey(file);

    /// <summary>Downloads <paramref name="file"/> into <paramref name="directory"/>; reports whole-percent progress.</summary>
    public static async Task DownloadAsync(string file, string directory, Action<int> progress, CancellationToken cancellationToken)
    {
        var (url, expected) = Known[file];
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, file);
        var partial = target + ".partial";

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(partial);
            var buffer = new byte[1 << 16];
            long received = 0;
            var lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                var percent = total > 0 ? (int)(received * 100 / total) : 0;
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress(percent);
                }
            }
        }

        string actual;
        await using (var stream = File.OpenRead(partial))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }

        if (actual != expected)
        {
            File.Delete(partial);
            throw new InvalidDataException($"Downloaded {file} failed verification and was deleted.");
        }

        File.Move(partial, target, overwrite: true);
    }
}
