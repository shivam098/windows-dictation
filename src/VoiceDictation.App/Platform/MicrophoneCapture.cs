using NAudio.Wave;

namespace VoiceDictation.App.Platform;

/// <summary>Captures 16 kHz mono audio from the microphone. Audio stays in memory and is never written to disk.</summary>
internal sealed class MicrophoneCapture : IDisposable
{
    private readonly int _device;
    private WaveInEvent? _waveIn;

    public MicrophoneCapture(int device)
    {
        _device = device;
    }

    /// <summary>Raised on the capture thread with normalized samples.</summary>
    public event Action<float[]>? Samples;

    /// <summary>Raised when capture stops unexpectedly (for example, the microphone was unplugged).</summary>
    public event Action<Exception>? Failed;

    public bool IsRunning => _waveIn is not null;

    public void Start()
    {
        if (_waveIn is not null)
        {
            return;
        }

        var waveIn = new WaveInEvent
        {
            DeviceNumber = _device,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 30,
            NumberOfBuffers = 4,
        };
        waveIn.DataAvailable += OnData;
        waveIn.RecordingStopped += OnStopped;
        try
        {
            waveIn.StartRecording();
        }
        catch
        {
            waveIn.DataAvailable -= OnData;
            waveIn.RecordingStopped -= OnStopped;
            waveIn.Dispose();
            throw;
        }

        _waveIn = waveIn;
    }

    public void Stop()
    {
        var waveIn = _waveIn;
        _waveIn = null;
        if (waveIn is null)
        {
            return;
        }

        waveIn.DataAvailable -= OnData;
        waveIn.RecordingStopped -= OnStopped;
        try
        {
            waveIn.StopRecording();
        }
        catch (NAudio.MmException)
        {
        }

        waveIn.Dispose();
    }

    public void Dispose() => Stop();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var count = e.BytesRecorded / 2;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        }

        Samples?.Invoke(samples);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && ReferenceEquals(sender, _waveIn))
        {
            Stop();
            Failed?.Invoke(e.Exception);
        }
    }
}
