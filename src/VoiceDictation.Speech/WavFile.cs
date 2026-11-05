namespace VoiceDictation.Speech;

public static class WavFile
{
    /// <summary>Reads a 16 kHz mono 16-bit PCM WAV file into normalized float samples.</summary>
    public static float[] ReadPcm16Mono16k(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        short channels = 0, bits = 0;
        var rate = 0;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                var format = reader.ReadInt16();
                channels = reader.ReadInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bits = reader.ReadInt16();
                reader.BaseStream.Seek(size - 16, SeekOrigin.Current);
                if (format != 1)
                {
                    throw new InvalidDataException("Only PCM WAV is supported.");
                }
            }
            else if (id == "data")
            {
                if (channels != 1 || rate != 16000 || bits != 16)
                {
                    throw new InvalidDataException($"Expected 16 kHz mono 16-bit, got {rate} Hz {channels} ch {bits}-bit.");
                }

                var samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = reader.ReadInt16() / 32768f;
                }

                return samples;
            }
            else
            {
                reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        throw new InvalidDataException("WAV has no data chunk.");
    }
}
