using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Audio;

internal sealed record InGameAudioSettingsSnapshot(
    IReadOnlyList<AudioEndpointInfo> Microphones,
    IReadOnlyList<AudioEndpointInfo> Speakers,
    string MicrophoneDeviceId,
    string SpeakerDeviceId,
    float MicrophoneInputGain,
    float SpeakerVolume,
    LocalMicrophoneMonitorState SelfTestState,
    float PeakLevel,
    bool IsSelfTestRequested);

/// <summary>
/// Owns the local Discord-style audio settings surface. Device and level changes are persisted
/// immediately; the local test is rebuilt immediately while Party keeps its startup-validated
/// device selection until the next restart.
/// </summary>
internal sealed class InGameAudioSettingsController : IDisposable
{
    private readonly IAudioEndpointCatalog _catalog;
    private readonly ILocalAudioMonitorBackendFactory _backendFactory;
    private readonly Action<Action<Config>> _updateConfiguration;
    private readonly Action<string> _log;
    private readonly object _sync = new();
    private readonly object _rebuildSync = new();

    private IReadOnlyList<AudioEndpointInfo> _microphones = Array.Empty<AudioEndpointInfo>();
    private IReadOnlyList<AudioEndpointInfo> _speakers = Array.Empty<AudioEndpointInfo>();
    private string _microphoneDeviceId;
    private string _speakerDeviceId;
    private float _microphoneInputGain;
    private float _speakerVolume;
    private LocalMicrophoneMonitor? _monitor;
    private bool _selfTestRequested;
    private bool _suspended;
    private bool _disposed;
    private int _refreshQueued;
    private Timer? _levelSaveTimer;
    private bool _levelsDirty;

    internal InGameAudioSettingsController(
        Config configuration,
        Action<Action<Config>> updateConfiguration,
        Action<string> log,
        IAudioEndpointCatalog? catalog = null,
        ILocalAudioMonitorBackendFactory? backendFactory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _updateConfiguration = updateConfiguration ??
            throw new ArgumentNullException(nameof(updateConfiguration));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _catalog = catalog ?? new WindowsAudioEndpointCatalog();
        _backendFactory = backendFactory ?? new WasapiLocalAudioMonitorBackendFactory(log);
        _microphoneDeviceId = NormalizeDeviceId(configuration.VoiceMicrophoneDeviceId);
        _speakerDeviceId = NormalizeDeviceId(configuration.VoicePlaybackDeviceId);
        _microphoneInputGain = Math.Clamp((float)configuration.MicrophoneSelfTestInputGain, 0f, 2f);
        _speakerVolume = Math.Clamp((float)configuration.MicrophoneSelfMonitorVolume, 0f, 0.5f);
        RefreshEndpointsCore();
        RebuildMonitor();
    }

    internal InGameAudioSettingsSnapshot GetSnapshot()
    {
        LocalMicrophoneMonitor? monitor;
        IReadOnlyList<AudioEndpointInfo> microphones;
        IReadOnlyList<AudioEndpointInfo> speakers;
        string microphoneId;
        string speakerId;
        float inputGain;
        float speakerVolume;
        bool requested;
        lock (_sync)
        {
            monitor = _monitor;
            microphones = _microphones;
            speakers = _speakers;
            microphoneId = _microphoneDeviceId;
            speakerId = _speakerDeviceId;
            inputGain = _microphoneInputGain;
            speakerVolume = _speakerVolume;
            requested = _selfTestRequested;
        }

        var state = monitor?.State ?? LocalMicrophoneMonitorState.Idle;
        var activeRequest = requested && state is
            LocalMicrophoneMonitorState.Starting or
            LocalMicrophoneMonitorState.Monitoring or
            LocalMicrophoneMonitorState.SignalDetected;
        return new InGameAudioSettingsSnapshot(
            microphones,
            speakers,
            microphoneId,
            speakerId,
            inputGain,
            speakerVolume,
            state,
            monitor?.PeakLevel ?? 0.0f,
            activeRequest);
    }

    internal void RefreshEndpointsAsync()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;

        _ = ThreadPool.QueueUserWorkItem(
            _ =>
            {
                try
                {
                    RefreshEndpointsCore();
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshQueued, 0);
                }
            });
    }

    internal void SelectMicrophone(string deviceId)
    {
        deviceId = NormalizeDeviceId(deviceId);
        lock (_sync)
        {
            if (_disposed || string.Equals(_microphoneDeviceId, deviceId, StringComparison.Ordinal))
                return;
            _microphoneDeviceId = deviceId;
        }

        Persist(configuration => configuration.VoiceMicrophoneDeviceId = deviceId);
        RebuildMonitor();
    }

    internal void SelectSpeaker(string deviceId)
    {
        deviceId = NormalizeDeviceId(deviceId);
        lock (_sync)
        {
            if (_disposed || string.Equals(_speakerDeviceId, deviceId, StringComparison.Ordinal))
                return;
            _speakerDeviceId = deviceId;
        }

        Persist(configuration => configuration.VoicePlaybackDeviceId = deviceId);
        RebuildMonitor();
    }

    internal void SetMicrophoneInputGain(float value)
    {
        value = Math.Clamp(value, 0.0f, 2.0f);
        lock (_sync)
        {
            if (_disposed || Math.Abs(_microphoneInputGain - value) < 0.001f)
                return;
            _microphoneInputGain = value;
        }

        ScheduleLevelSave();
        LocalMicrophoneMonitor? monitor;
        lock (_sync)
            monitor = _monitor;
        monitor?.UpdateLevels(value, GetSpeakerVolume());
    }

    internal void SetSpeakerVolume(float value)
    {
        value = Math.Clamp(value, 0.0f, 0.5f);
        lock (_sync)
        {
            if (_disposed || Math.Abs(_speakerVolume - value) < 0.001f)
                return;
            _speakerVolume = value;
        }

        ScheduleLevelSave();
        LocalMicrophoneMonitor? monitor;
        lock (_sync)
            monitor = _monitor;
        monitor?.UpdateLevels(GetInputGain(), value);
    }

    internal void SetSelfTestPressed(bool pressed)
    {
        LocalMicrophoneMonitor? monitor;
        lock (_sync)
        {
            if (_disposed)
                return;
            _selfTestRequested = pressed && !_suspended;
            monitor = _monitor;
        }
        monitor?.SetPressed(pressed && !_suspended);
    }

    internal void ApplyConfiguration(Config configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var rebuild = false;
        var levelsChanged = false;
        var inputGain = Math.Clamp((float)configuration.MicrophoneSelfTestInputGain, 0f, 2f);
        var speakerVolume = Math.Clamp((float)configuration.MicrophoneSelfMonitorVolume, 0f, 0.5f);
        lock (_sync)
        {
            if (_disposed)
                return;

            var microphoneId = NormalizeDeviceId(configuration.VoiceMicrophoneDeviceId);
            var speakerId = NormalizeDeviceId(configuration.VoicePlaybackDeviceId);
            rebuild = !string.Equals(_microphoneDeviceId, microphoneId, StringComparison.Ordinal) ||
                      !string.Equals(_speakerDeviceId, speakerId, StringComparison.Ordinal);
            levelsChanged = Math.Abs(_microphoneInputGain - inputGain) >= 0.001f ||
                            Math.Abs(_speakerVolume - speakerVolume) >= 0.001f;
            _microphoneDeviceId = microphoneId;
            _speakerDeviceId = speakerId;
            _microphoneInputGain = inputGain;
            _speakerVolume = speakerVolume;
        }

        if (rebuild)
            RebuildMonitor();
        else if (levelsChanged)
        {
            LocalMicrophoneMonitor? monitor;
            lock (_sync)
                monitor = _monitor;
            monitor?.UpdateLevels(inputGain, speakerVolume);
        }
    }

    internal void Suspend()
    {
        LocalMicrophoneMonitor? monitor;
        lock (_sync)
        {
            _suspended = true;
            _selfTestRequested = false;
            monitor = _monitor;
        }
        monitor?.Suspend();
    }

    internal void Resume()
    {
        LocalMicrophoneMonitor? monitor;
        lock (_sync)
        {
            _suspended = false;
            monitor = _monitor;
        }
        monitor?.Resume();
    }

    internal void FlushPendingLevelSave()
    {
        lock (_sync)
        {
            if (_disposed || !_levelsDirty)
                return;
            _levelSaveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        PersistLevels();
    }

    public void Dispose()
    {
        FlushPendingLevelSave();
        LocalMicrophoneMonitor? monitor;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _selfTestRequested = false;
            monitor = _monitor;
            _monitor = null;
            _levelSaveTimer?.Dispose();
            _levelSaveTimer = null;
        }
        monitor?.Dispose();
    }

    private void RefreshEndpointsCore()
    {
        try
        {
            var microphones = _catalog.GetActiveEndpoints(AudioEndpointFlow.Capture);
            var speakers = _catalog.GetActiveEndpoints(AudioEndpointFlow.Render);
            lock (_sync)
            {
                if (_disposed)
                    return;
                _microphones = microphones;
                _speakers = speakers;
            }
        }
        catch (Exception exception)
        {
            SafeLog($"In-game audio endpoint refresh failed: {exception.Message}");
        }
    }

    private void RebuildMonitor()
    {
        lock (_rebuildSync)
        {
            LocalMicrophoneMonitor? previous;
            LocalMicrophoneMonitor? replacement;
            bool requested;
            bool suspended;
            string microphoneId;
            string speakerId;
            float inputGain;
            float speakerVolume;
            IReadOnlyList<AudioEndpointInfo> microphones;
            IReadOnlyList<AudioEndpointInfo> speakers;
            lock (_sync)
            {
                if (_disposed)
                    return;
                previous = _monitor;
                _monitor = null;
                requested = _selfTestRequested;
                suspended = _suspended;
                microphoneId = _microphoneDeviceId;
                speakerId = _speakerDeviceId;
                inputGain = _microphoneInputGain;
                speakerVolume = _speakerVolume;
                microphones = _microphones;
                speakers = _speakers;
            }

            previous?.SetPressed(false);
            previous?.Dispose();
            replacement = new LocalMicrophoneMonitor(
                _backendFactory,
                Resolve(microphoneId, microphones),
                Resolve(speakerId, speakers),
                inputGain,
                speakerVolume,
                _log);
            if (suspended)
                replacement.Suspend();

            lock (_sync)
            {
                if (_disposed)
                {
                    replacement.Dispose();
                    return;
                }
                _monitor = replacement;
            }

            if (requested && !suspended)
                replacement.SetPressed(true);
        }
    }

    private static ResolvedAudioEndpointSelection Resolve(
        string deviceId,
        IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        if (AudioEndpointSelectionValues.IsSystemDefault(deviceId))
            return ResolvedAudioEndpointSelection.SystemDefault();
        var endpoint = endpoints.FirstOrDefault(
            candidate => string.Equals(candidate.Id, deviceId, StringComparison.Ordinal));
        return endpoint is null
            ? ResolvedAudioEndpointSelection.SystemDefault(fellBack: true)
            : new ResolvedAudioEndpointSelection(false, endpoint.Id, endpoint.FriendlyName, false);
    }

    private void Persist(Action<Config> update)
    {
        try
        {
            _updateConfiguration(update);
        }
        catch (Exception exception)
        {
            SafeLog($"In-game audio setting could not be persisted: {exception.Message}");
        }
    }

    private void ScheduleLevelSave()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _levelSaveTimer ??= new Timer(
                static state => ((InGameAudioSettingsController)state!).PersistLevels(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _levelsDirty = true;
            _levelSaveTimer.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
    }

    private void PersistLevels()
    {
        float inputGain;
        float speakerVolume;
        lock (_sync)
        {
            if (_disposed || !_levelsDirty)
                return;
            inputGain = _microphoneInputGain;
            speakerVolume = _speakerVolume;
            _levelsDirty = false;
        }

        Persist(configuration =>
        {
            configuration.MicrophoneSelfTestInputGain = inputGain;
            configuration.MicrophoneSelfMonitorVolume = speakerVolume;
        });
    }

    private void SafeLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // UI/audio state must not depend on the logger.
        }
    }

    private static string NormalizeDeviceId(string? deviceId) =>
        AudioEndpointSelectionValues.IsSystemDefault(deviceId)
            ? AudioEndpointSelectionValues.SystemDefault
            : deviceId!.Trim();

    private float GetInputGain()
    {
        lock (_sync)
            return _microphoneInputGain;
    }

    private float GetSpeakerVolume()
    {
        lock (_sync)
            return _speakerVolume;
    }
}
