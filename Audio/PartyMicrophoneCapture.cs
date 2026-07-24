using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GBFR.ChatOverlay.Audio;

internal readonly record struct PartyMicrophoneCaptureFrame(
    byte[] Buffer,
    int SampleCount,
    float Peak);

internal interface IPartyMicrophoneCaptureBackend : IDisposable
{
    event Action<PartyMicrophoneCaptureFrame>? FrameReady;

    event Action<Exception>? Faulted;

    string CaptureFormatDescription { get; }

    void Start();

    /// <summary>Stops producing frames immediately and releases Core Audio asynchronously.</summary>
    void StopImmediately();
}

internal interface IPartyMicrophoneCaptureBackendFactory
{
    IPartyMicrophoneCaptureBackend Create(ResolvedAudioEndpointSelection inputSelection);
}

internal sealed class WasapiPartyMicrophoneCaptureBackendFactory :
    IPartyMicrophoneCaptureBackendFactory
{
    private readonly Action<string> _log;

    public WasapiPartyMicrophoneCaptureBackendFactory(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public IPartyMicrophoneCaptureBackend Create(ResolvedAudioEndpointSelection inputSelection) =>
        new WasapiPartyMicrophoneCaptureBackend(inputSelection, _log);
}

/// <summary>
/// Captures the selected Windows microphone in WASAPI shared mode, converts it to Party's Windows
/// capture-sink format (24 kHz mono float), and emits exact 40 ms frames from a dedicated MTA
/// sender thread. Endpoint shutdown never blocks DirectInput or Party's state-change pump.
/// </summary>
internal sealed class WasapiPartyMicrophoneCaptureBackend : IPartyMicrophoneCaptureBackend
{
    internal const int PartySampleRate = 24_000;
    internal const int PartyFrameDurationMilliseconds = 40;
    internal const int PartySamplesPerFrame =
        PartySampleRate * PartyFrameDurationMilliseconds / 1_000;
    internal const int PartyBytesPerFrame = PartySamplesPerFrame * sizeof(float);

    private static readonly TimeSpan CaptureStopWait = TimeSpan.FromMilliseconds(1_500);
    private static readonly TimeSpan SenderStopWait = TimeSpan.FromMilliseconds(1_500);

    private readonly ResolvedAudioEndpointSelection _inputSelection;
    private readonly Action<string> _log;
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _recordingStopped = new(initialState: false);
    private readonly ManualResetEventSlim _stopSignal = new(initialState: false);
    private readonly AutoResetEvent _samplesAvailable = new(initialState: false);

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _captureDevice;
    private WasapiCapture? _capture;
    private WaveToMonoSampleDecoder? _decoder;
    private BoundedMonoSampleProvider? _monoSamples;
    private WdlResamplingSampleProvider? _resampler;
    private Thread? _senderThread;
    private string _captureFormatDescription = "not started";
    private int _started;
    private int _stopRequested;
    private int _cleanupStarted;
    private int _faultSignaled;

    public WasapiPartyMicrophoneCaptureBackend(
        ResolvedAudioEndpointSelection inputSelection,
        Action<string>? log = null)
    {
        _inputSelection = inputSelection;
        _log = log ?? (_ => { });
    }

    public event Action<PartyMicrophoneCaptureFrame>? FrameReady;

    public event Action<Exception>? Faulted;

    public string CaptureFormatDescription => Volatile.Read(ref _captureFormatDescription);

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopRequested) != 0, this);
            if (Volatile.Read(ref _started) != 0)
                return;

            _enumerator = new MMDeviceEnumerator();
            _captureDevice = ResolveDevice(_enumerator, _inputSelection);
            if ((_captureDevice.State & DeviceState.Active) == 0)
            {
                throw new InvalidOperationException(
                    $"The selected microphone endpoint is not active: " +
                    $"{_captureDevice.FriendlyName} ({_captureDevice.State}).");
            }

            _capture = new WasapiCapture(
                _captureDevice,
                useEventSync: true,
                audioBufferMillisecondsLength: PartyFrameDurationMilliseconds);
            _decoder = new WaveToMonoSampleDecoder(_capture.WaveFormat);
            _monoSamples = new BoundedMonoSampleProvider(
                _capture.WaveFormat.SampleRate,
                capacitySamples: checked(_capture.WaveFormat.SampleRate * 2));
            _resampler = new WdlResamplingSampleProvider(_monoSamples, PartySampleRate);
            _captureFormatDescription =
                $"{_capture.WaveFormat.SampleRate} Hz, {_capture.WaveFormat.Channels} channel(s), " +
                $"{_capture.WaveFormat.BitsPerSample}-bit {_decoder.Encoding}";

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _senderThread = new Thread(SenderThreadMain)
            {
                IsBackground = true,
                Name = "GBFR Party microphone capture sender",
            };
            _senderThread.SetApartmentState(ApartmentState.MTA);
            _senderThread.Start();

            try
            {
                _capture.StartRecording();
                Volatile.Write(ref _started, 1);
                if (Volatile.Read(ref _stopRequested) != 0)
                    throw new OperationCanceledException("The U hold ended while microphone capture was starting.");
            }
            catch
            {
                StopImmediately();
                throw;
            }
        }
    }

    public void StopImmediately()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
            return;

        Volatile.Read(ref _monoSamples)?.Clear();
        _stopSignal.Set();
        _samplesAvailable.Set();
        if (Interlocked.CompareExchange(ref _cleanupStarted, 1, 0) != 0)
            return;

        var cleanupThread = new Thread(CleanupThreadMain)
        {
            IsBackground = true,
            Name = "GBFR Party microphone capture cleanup",
        };
        cleanupThread.Start();
    }

    public void Dispose() => StopImmediately();

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (Volatile.Read(ref _stopRequested) != 0 || args.BytesRecorded <= 0)
            return;

        try
        {
            var decoder = Volatile.Read(ref _decoder);
            var samples = Volatile.Read(ref _monoSamples);
            if (decoder is null || samples is null)
                return;

            var mono = decoder.Decode(args.Buffer.AsSpan(0, args.BytesRecorded));
            if (mono.Length == 0 || Volatile.Read(ref _stopRequested) != 0)
                return;

            samples.Add(mono);
            _samplesAvailable.Set();
        }
        catch (Exception exception)
        {
            SignalFault(exception);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        _recordingStopped.Set();
        if (Volatile.Read(ref _stopRequested) == 0)
        {
            SignalFault(args.Exception ??
                new InvalidOperationException("The Party microphone capture endpoint stopped unexpectedly."));
        }
    }

    private void SenderThreadMain()
    {
        var resampled = new float[PartySamplesPerFrame * 4];
        var frame = new float[PartySamplesPerFrame];
        var frameSamples = 0;
        var cadence = new PartyFrameCadence(
            Stopwatch.Frequency,
            PartyFrameDurationMilliseconds);

        try
        {
            while (Volatile.Read(ref _stopRequested) == 0)
            {
                _samplesAvailable.WaitOne(100);
                if (Volatile.Read(ref _stopRequested) != 0)
                    break;

                var resampler = Volatile.Read(ref _resampler);
                if (resampler is null)
                    continue;

                while (Volatile.Read(ref _stopRequested) == 0)
                {
                    var read = resampler.Read(resampled, 0, resampled.Length);
                    if (read <= 0)
                        break;

                    var sourceOffset = 0;
                    while (sourceOffset < read && Volatile.Read(ref _stopRequested) == 0)
                    {
                        var copy = Math.Min(PartySamplesPerFrame - frameSamples, read - sourceOffset);
                        Array.Copy(resampled, sourceOffset, frame, frameSamples, copy);
                        sourceOffset += copy;
                        frameSamples += copy;
                        if (frameSamples != PartySamplesPerFrame)
                            continue;

                        if (!WaitForFrameCadence(cadence))
                            return;
                        PublishFrame(frame);
                        // Base the next deadline on the completed callback. If the callback or the
                        // thread was delayed, this deliberately starts a fresh 40 ms interval
                        // instead of replaying missed deadlines in a burst.
                        cadence.MarkPublished(Stopwatch.GetTimestamp());
                        frameSamples = 0;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            SignalFault(exception);
        }
    }

    private bool WaitForFrameCadence(PartyFrameCadence cadence)
    {
        while (Volatile.Read(ref _stopRequested) == 0)
        {
            var remainingTicks = cadence.GetRemainingTicks(Stopwatch.GetTimestamp());
            if (remainingTicks <= 0)
                return true;

            var wait = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
            if (_stopSignal.Wait(wait))
                return false;
        }

        return false;
    }

    private void PublishFrame(float[] samples)
    {
        var bytes = new byte[PartyBytesPerFrame];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        var peak = 0f;
        foreach (var sample in samples)
        {
            if (float.IsFinite(sample))
                peak = Math.Max(peak, Math.Abs(sample));
        }

        FrameReady?.Invoke(new PartyMicrophoneCaptureFrame(
            bytes,
            PartySamplesPerFrame,
            Math.Clamp(peak, 0f, 1f)));
    }

    private void CleanupThreadMain()
    {
        WasapiCapture? capture;
        Thread? sender;
        lock (_sync)
        {
            capture = _capture;
            sender = _senderThread;
        }

        var captureStopped = capture is null || Volatile.Read(ref _started) == 0;
        var senderStopped = sender is null;
        try
        {
            if (capture is not null)
            {
                try
                {
                    capture.StopRecording();
                }
                catch (Exception exception)
                {
                    SafeLog($"Stage 3 Party microphone capture stop request failed: {exception.Message}");
                }

                captureStopped = captureStopped || _recordingStopped.Wait(CaptureStopWait);
                if (!captureStopped)
                {
                    SafeLog(
                        "Stage 3 Party microphone capture cleanup did not observe RecordingStopped " +
                        $"within {CaptureStopWait.TotalMilliseconds:0} ms.");
                }
            }

            _samplesAvailable.Set();
            senderStopped = sender is null ||
                            sender == Thread.CurrentThread ||
                            sender.Join(SenderStopWait);
            if (!senderStopped)
            {
                SafeLog(
                    "Stage 3 Party microphone sender did not exit within " +
                    $"{SenderStopWait.TotalMilliseconds:0} ms; its submission gate remains closed.");
            }
        }
        finally
        {
            if (!captureStopped || !senderStopped)
            {
                // Do not Dispose a native endpoint or clear a provider while a capture callback or
                // sender may still be using it. The detached backend is quarantined and the process
                // will reclaim these references; its submission gate was closed synchronously.
                SafeLog(
                    "Stage 3 Party microphone cleanup quarantined native resources because " +
                    $"captureStopped={captureStopped}, senderStopped={senderStopped}. " +
                    "No further frames can pass the closed submission gate.");
            }
            else
            {
                lock (_sync)
                {
                    if (_capture is not null)
                    {
                        _capture.DataAvailable -= OnDataAvailable;
                        _capture.RecordingStopped -= OnRecordingStopped;
                    }

                    try
                    {
                        _capture?.Dispose();
                        _captureDevice?.Dispose();
                        _enumerator?.Dispose();
                    }
                    catch (Exception exception)
                    {
                        SafeLog($"Stage 3 Party microphone endpoint disposal failed: {exception.Message}");
                    }

                    _capture = null;
                    _captureDevice = null;
                    _enumerator = null;
                    _decoder = null;
                    _monoSamples = null;
                    _resampler = null;
                    Volatile.Write(ref _started, 0);
                }

                SafeLog("Stage 3 Party microphone capture cleanup complete; no further frames can be submitted.");
            }
        }
    }

    private void SignalFault(Exception exception)
    {
        if (Interlocked.Exchange(ref _faultSignaled, 1) != 0)
            return;

        try
        {
            Faulted?.Invoke(exception);
        }
        catch
        {
            // A managed subscriber cannot destabilize the WASAPI or sender thread.
        }

        StopImmediately();
    }

    private void SafeLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Audio teardown cannot depend on the logger.
        }
    }

    private static MMDevice ResolveDevice(
        MMDeviceEnumerator enumerator,
        ResolvedAudioEndpointSelection selection)
    {
        if (selection.UseSystemDefault)
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        if (string.IsNullOrWhiteSpace(selection.DeviceId))
            throw new InvalidOperationException("A manual microphone selection has no device ID.");
        return enumerator.GetDevice(selection.DeviceId);
    }
}

/// <summary>
/// Maintains a minimum monotonic interval between Party sink submissions. A late publication
/// establishes a new deadline, so buffered microphone data can never trigger catch-up bursts.
/// </summary>
internal sealed class PartyFrameCadence
{
    private readonly long _intervalTicks;
    private long _nextPublishTimestamp;

    public PartyFrameCadence(long timestampFrequency, int frameDurationMilliseconds)
    {
        if (timestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        if (frameDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameDurationMilliseconds));

        _intervalTicks = Math.Max(
            1,
            checked((timestampFrequency * frameDurationMilliseconds + 999) / 1_000));
    }

    public long GetRemainingTicks(long timestamp)
    {
        if (_nextPublishTimestamp == 0 || timestamp >= _nextPublishTimestamp)
            return 0;
        return _nextPublishTimestamp - timestamp;
    }

    public void MarkPublished(long timestamp) =>
        _nextPublishTimestamp = timestamp > long.MaxValue - _intervalTicks
            ? long.MaxValue
            : timestamp + _intervalTicks;
}

internal sealed class WaveToMonoSampleDecoder
{
    private static readonly Guid PcmSubFormat =
        new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid IeeeFloatSubFormat =
        new("00000003-0000-0010-8000-00AA00389B71");

    private readonly WaveFormat _format;
    private readonly int _bytesPerSample;

    public WaveToMonoSampleDecoder(WaveFormat format)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
        Encoding = ResolveEncoding(format);
        _bytesPerSample = format.BitsPerSample / 8;
        if (format.SampleRate <= 0 || format.Channels <= 0 || format.BlockAlign <= 0 ||
            _bytesPerSample <= 0 || _bytesPerSample * format.Channels > format.BlockAlign)
        {
            throw new NotSupportedException($"Invalid microphone format: {format}.");
        }

        _ = ReadSample(ReadOnlySpan<byte>.Empty, 0, validateOnly: true);
    }

    public WaveFormatEncoding Encoding { get; }

    public float[] Decode(ReadOnlySpan<byte> data)
    {
        var frameCount = data.Length / _format.BlockAlign;
        if (frameCount == 0)
            return [];

        var output = new float[frameCount];
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var frameOffset = frameIndex * _format.BlockAlign;
            var sum = 0f;
            for (var channel = 0; channel < _format.Channels; channel++)
            {
                sum += ReadSample(
                    data,
                    frameOffset + channel * _bytesPerSample,
                    validateOnly: false);
            }

            var mono = sum / _format.Channels;
            output[frameIndex] = float.IsFinite(mono) ? Math.Clamp(mono, -1f, 1f) : 0f;
        }

        return output;
    }

    private float ReadSample(ReadOnlySpan<byte> data, int offset, bool validateOnly)
    {
        if (validateOnly)
        {
            return (Encoding, _format.BitsPerSample) switch
            {
                (WaveFormatEncoding.IeeeFloat, 32 or 64) => 0f,
                (WaveFormatEncoding.Pcm, 8 or 16 or 24 or 32) => 0f,
                _ => throw new NotSupportedException(
                    $"Unsupported microphone format: {Encoding}, {_format.BitsPerSample} bits."),
            };
        }

        return (Encoding, _format.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) =>
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[offset..])),
            (WaveFormatEncoding.IeeeFloat, 64) =>
                (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data[offset..])),
            (WaveFormatEncoding.Pcm, 8) => (data[offset] - 128) / 128f,
            (WaveFormatEncoding.Pcm, 16) =>
                BinaryPrimitives.ReadInt16LittleEndian(data[offset..]) / 32768f,
            (WaveFormatEncoding.Pcm, 24) => ReadPcm24(data[offset..]),
            (WaveFormatEncoding.Pcm, 32) =>
                BinaryPrimitives.ReadInt32LittleEndian(data[offset..]) / 2147483648f,
            _ => throw new InvalidOperationException("The microphone format was not validated."),
        };
    }

    private static float ReadPcm24(ReadOnlySpan<byte> data)
    {
        var sample = data[0] | (data[1] << 8) | (data[2] << 16);
        if ((sample & 0x00800000) != 0)
            sample |= unchecked((int)0xFF000000);
        return sample / 8388608f;
    }

    private static WaveFormatEncoding ResolveEncoding(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible)
            return format.Encoding;
        if (format is not WaveFormatExtensible extensible)
            return format.Encoding;
        if (extensible.SubFormat == PcmSubFormat)
            return WaveFormatEncoding.Pcm;
        if (extensible.SubFormat == IeeeFloatSubFormat)
            return WaveFormatEncoding.IeeeFloat;
        return format.Encoding;
    }
}

internal sealed class BoundedMonoSampleProvider : ISampleProvider
{
    private readonly float[] _buffer;
    private readonly object _sync = new();
    private int _readOffset;
    private int _count;

    public BoundedMonoSampleProvider(int sampleRate, int capacitySamples)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (capacitySamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacitySamples));

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels: 1);
        _buffer = new float[capacitySamples];
    }

    public WaveFormat WaveFormat { get; }

    public void Add(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return;

        lock (_sync)
        {
            if (samples.Length >= _buffer.Length)
            {
                samples[^_buffer.Length..].CopyTo(_buffer);
                _readOffset = 0;
                _count = _buffer.Length;
                return;
            }

            var overflow = Math.Max(0, _count + samples.Length - _buffer.Length);
            if (overflow != 0)
            {
                _readOffset = (_readOffset + overflow) % _buffer.Length;
                _count -= overflow;
            }

            var writeOffset = (_readOffset + _count) % _buffer.Length;
            var first = Math.Min(samples.Length, _buffer.Length - writeOffset);
            samples[..first].CopyTo(_buffer.AsSpan(writeOffset, first));
            if (first < samples.Length)
                samples[first..].CopyTo(_buffer.AsSpan(0, samples.Length - first));
            _count += samples.Length;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The requested sample range is outside the destination buffer.");

        lock (_sync)
        {
            var read = Math.Min(count, _count);
            var first = Math.Min(read, _buffer.Length - _readOffset);
            Array.Copy(_buffer, _readOffset, buffer, offset, first);
            if (first < read)
                Array.Copy(_buffer, 0, buffer, offset + first, read - first);
            _readOffset = (_readOffset + read) % _buffer.Length;
            _count -= read;
            return read;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _readOffset = 0;
            _count = 0;
        }
    }
}
