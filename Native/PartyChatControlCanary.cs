using System.Diagnostics;
using System.Runtime.InteropServices;
using GBFR.ChatOverlay.Audio;

namespace GBFR.ChatOverlay.Native;

internal enum PartyChatControlCanaryPhase
{
    WaitingForAuthenticatedSession,
    Creating,
    ConfiguringMutedAudio,
    Connecting,
    JoinedMuted,
    VoiceReady,
    Disconnecting,
    Destroying,
    Completed,
    Disabled,
}

/// <summary>
/// Creates one locally owned Party ChatControl on the game's existing authenticated Party session.
/// Stage 2 keeps it muted with no permissions; the opt-in Stage 3 test grants microphone-only
/// permissions to remote ChatControls observed on that same network and uses hold-to-talk mute.
/// Native actions never overlap the game's state-change batch.
/// </summary>
internal sealed class PartyChatControlCanary : IDisposable
{
    private const uint Success = 0;
    private const uint SucceededStateChange = 0;
    private static readonly long VoiceDiagnosticIntervalTicks = Math.Max(1, Stopwatch.Frequency / 4);
    private const PartyChatPermissionOptions MicrophoneVoicePermissions =
        PartyChatPermissionOptions.SendMicrophoneAudio |
        PartyChatPermissionOptions.ReceiveMicrophoneAudio;

    private readonly IPartyChatControlApi _api;
    private readonly Action<string> _log;
    private readonly Action<Action> _schedule;
    private readonly bool _enableVoiceTest;
    private readonly ResolvedAudioEndpointSelection _audioInputSelection;
    private readonly ResolvedAudioEndpointSelection _audioOutputSelection;
    private readonly object _stateSync = new();
    private readonly object _apiCallSync = new();
    private readonly HashSet<nint> _remoteChatControls = [];
    private readonly HashSet<nint> _permissionedRemoteChatControls = [];
    private readonly Dictionary<nint, string> _lastPeerVoiceDiagnosticFingerprints = [];
    private readonly Dictionary<nint, PeerVoiceDiagnosticEvidence> _peerVoiceDiagnosticEvidence = [];
    private readonly GCHandle _createTokenHandle;
    private readonly GCHandle _inputTokenHandle;
    private readonly GCHandle _outputTokenHandle;
    private readonly GCHandle _connectTokenHandle;
    private readonly GCHandle _disconnectTokenHandle;
    private readonly GCHandle _destroyTokenHandle;
    private readonly nint _createToken;
    private readonly nint _inputToken;
    private readonly nint _outputToken;
    private readonly nint _connectToken;
    private readonly nint _disconnectToken;
    private readonly nint _destroyToken;

    private PartyChatControlCanaryPhase _phase =
        PartyChatControlCanaryPhase.WaitingForAuthenticatedSession;
    private nint _manager;
    private nint _network;
    private nint _localUser;
    private nint _localDevice;
    private nint _localChatControl;
    private bool _authenticated;
    private bool _endpointReady;
    private bool _networkLeaving;
    private bool _nativeCallsAllowed = true;
    private bool _suspended;
    private bool _sessionFaulted;
    private bool _createCompleted;
    private bool _createdObserved;
    private bool _inputCompleted;
    private bool _outputCompleted;
    private bool _connectCompleted;
    private bool _joinedObserved;
    private bool _disconnectQueued;
    private bool _leftObserved;
    private bool _destroyQueued;
    private bool _destroyCallBegan;
    private bool _destroyCompleted;
    private bool _destroyedObserved;
    private bool _teardownRequested;
    private bool _preLeaveObserved;
    private bool _pushToTalkPressed;
    private bool _inputUnmuted;
    // Conservative native-state marker. This is set before an unmute call so a concurrent
    // fail-closed path never relies solely on the managed mirror being updated afterwards.
    private bool _microphoneMayBeOpen;
    private PartyAudioInputState? _audioInputState;
    private uint _audioInputStateErrorDetail;
    private PartyAudioOutputState? _audioOutputState;
    private uint _audioOutputStateErrorDetail;
    private bool _voiceDiagnosticRequested;
    private long _nextVoiceDiagnosticTimestamp;
    private string? _lastLocalVoiceDiagnosticFingerprint;
    private bool _voiceDiagnosticSnapshotObserved;
    private bool _diagnosticLocalTalkingObserved;
    private PartyAudioDeviceDiagnostic? _diagnosticInputDevice;
    private PartyAudioDeviceDiagnostic? _diagnosticOutputDevice;
    private bool _pttCycleActive;
    private bool _pttCycleLocalTalkingObserved;
    private bool _voiceDiagnosticSummaryLogged;
    private bool _stateBatchActive;
    private nint _stateBatchManager;
    private int _workScheduled;
    private long _generation;
    private int _disposed;

    public PartyChatControlCanary(
        IPartyChatControlApi api,
        Action<string> log,
        Action<Action>? schedule = null,
        bool enableVoiceTest = false,
        ResolvedAudioEndpointSelection? audioInputSelection = null,
        ResolvedAudioEndpointSelection? audioOutputSelection = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _enableVoiceTest = enableVoiceTest;
        _audioInputSelection = audioInputSelection ??
            ResolvedAudioEndpointSelection.SystemDefault();
        _audioOutputSelection = audioOutputSelection ??
            ResolvedAudioEndpointSelection.SystemDefault();
        _schedule = schedule ?? QueueOnThreadPool;

        _createTokenHandle = AllocateToken("create");
        _inputTokenHandle = AllocateToken("audio-input");
        _outputTokenHandle = AllocateToken("audio-output");
        _connectTokenHandle = AllocateToken("connect");
        _disconnectTokenHandle = AllocateToken("disconnect");
        _destroyTokenHandle = AllocateToken("destroy");
        _createToken = GCHandle.ToIntPtr(_createTokenHandle);
        _inputToken = GCHandle.ToIntPtr(_inputTokenHandle);
        _outputToken = GCHandle.ToIntPtr(_outputTokenHandle);
        _connectToken = GCHandle.ToIntPtr(_connectTokenHandle);
        _disconnectToken = GCHandle.ToIntPtr(_disconnectTokenHandle);
        _destroyToken = GCHandle.ToIntPtr(_destroyTokenHandle);
    }

    internal PartyChatControlCanaryPhase Phase
    {
        get
        {
            lock (_stateSync)
                return _phase;
        }
    }

    internal PartyVoiceUiStatus VoiceUiStatus
    {
        get
        {
            lock (_stateSync)
                return GetVoiceUiStatusLocked();
        }
    }

    internal nint CreateAsyncIdentifier => _createToken;

    internal nint AudioInputAsyncIdentifier => _inputToken;

    internal nint AudioOutputAsyncIdentifier => _outputToken;

    internal nint ConnectAsyncIdentifier => _connectToken;

    internal nint DisconnectAsyncIdentifier => _disconnectToken;

    internal nint DestroyAsyncIdentifier => _destroyToken;

    public void CaptureManager(nint manager, string source)
    {
        if (manager == nint.Zero)
            return;

        lock (_stateSync)
        {
            if (_manager == nint.Zero)
            {
                _manager = manager;
                _nativeCallsAllowed = true;
                _phase = PartyChatControlCanaryPhase.WaitingForAuthenticatedSession;
                _generation++;
                return;
            }

            if (_manager != manager)
            {
                FailClosedLocked(
                    $"manager ownership became ambiguous at {source}: expected {Hex(_manager)}, got {Hex(manager)}");
            }
        }
    }

    public void Observe(nint manager, PartyStateChangeSnapshot state)
    {
        lock (_stateSync)
        {
            if (_disposed != 0 || _suspended || !_nativeCallsAllowed)
                return;
            if (_manager == nint.Zero)
                _manager = manager;
            if (manager == nint.Zero || manager != _manager)
            {
                FailClosedLocked(
                    $"state batch manager mismatch: expected {Hex(_manager)}, got {Hex(manager)}");
                return;
            }

            if (!PartyStateChangeCatalog.IsKnown(state.Type))
            {
                FailClosedLocked($"unexpected Party state-change type {state.Type}");
                return;
            }

            switch ((PartyStateChangeType)state.Type)
            {
                case PartyStateChangeType.AuthenticateLocalUserCompleted:
                    ObserveAuthenticationLocked(state);
                    break;
                case PartyStateChangeType.CreateEndpointCompleted:
                    ObserveEndpointLocked(state);
                    break;
                case PartyStateChangeType.CreateChatControlCompleted:
                    ObserveCreateCompletedLocked(state);
                    break;
                case PartyStateChangeType.ChatControlCreated:
                    ObserveChatControlCreatedLocked(state);
                    break;
                case PartyStateChangeType.SetChatAudioInputCompleted:
                    ObserveAudioCompletedLocked(state, input: true);
                    break;
                case PartyStateChangeType.SetChatAudioOutputCompleted:
                    ObserveAudioCompletedLocked(state, input: false);
                    break;
                case PartyStateChangeType.LocalChatAudioInputChanged:
                    ObserveAudioStateChangedLocked(state, input: true);
                    break;
                case PartyStateChangeType.LocalChatAudioOutputChanged:
                    ObserveAudioStateChangedLocked(state, input: false);
                    break;
                case PartyStateChangeType.ConnectChatControlCompleted:
                    ObserveConnectCompletedLocked(state);
                    break;
                case PartyStateChangeType.ChatControlJoinedNetwork:
                    ObserveJoinedLocked(state);
                    break;
                case PartyStateChangeType.DisconnectChatControlCompleted:
                    ObserveDisconnectCompletedLocked(state);
                    break;
                case PartyStateChangeType.ChatControlLeftNetwork:
                    ObserveLeftLocked(state);
                    break;
                case PartyStateChangeType.DestroyChatControlCompleted:
                    ObserveDestroyCompletedLocked(state);
                    break;
                case PartyStateChangeType.ChatControlDestroyed:
                    ObserveDestroyedLocked(state);
                    break;
                case PartyStateChangeType.LeaveNetworkCompleted:
                case PartyStateChangeType.NetworkDestroyed:
                    ObserveNetworkCleanupLocked(state);
                    break;
                case PartyStateChangeType.LocalUserRemoved:
                case PartyStateChangeType.RemoveLocalUserCompleted:
                    ObserveLocalUserRemovedLocked(state);
                    break;
                case PartyStateChangeType.DestroyLocalUserCompleted:
                    ObserveLocalUserDestroyedLocked(state);
                    break;
            }

            PromoteJoinedLocked();
        }
    }

    /// <summary>
    /// Fences deferred Party calls before Relink starts a shared state-change batch.
    /// </summary>
    public void BeginStateChangeBatch(nint manager)
    {
        lock (_apiCallSync)
        {
            lock (_stateSync)
            {
                if (_disposed != 0 || _suspended || !_nativeCallsAllowed)
                    return;
                if (_stateBatchActive)
                {
                    FailClosedLocked("Relink started a nested Party state-change batch");
                    return;
                }

                _stateBatchActive = true;
                _stateBatchManager = manager;
            }
        }
    }

    public void CancelStateChangeBatch(nint manager)
    {
        lock (_stateSync)
        {
            if (_stateBatchActive && manager == _stateBatchManager)
            {
                _stateBatchActive = false;
                _stateBatchManager = nint.Zero;
            }
        }

        TryScheduleWork();
    }

    /// <summary>
    /// Called only after the game's original PartyFinishProcessingStateChanges returns.
    /// </summary>
    public void OnBatchFinished(nint manager)
    {
        lock (_stateSync)
        {
            if (_stateBatchActive && manager == _stateBatchManager)
            {
                _stateBatchActive = false;
                _stateBatchManager = nint.Zero;
            }

            if (manager == nint.Zero || manager != _manager || _suspended)
                return;

            RequestPeriodicVoiceDiagnosticsLocked();

            if (_destroyedObserved &&
                (!_destroyCallBegan || _destroyCompleted) &&
                _localChatControl != nint.Zero)
            {
                _localChatControl = nint.Zero;
                _localDevice = nint.Zero;
                _pushToTalkPressed = false;
                _inputUnmuted = false;
                _microphoneMayBeOpen = false;
                EnqueueVoiceDiagnosticSummaryLocked("local ChatControl destroyed");
                _remoteChatControls.Clear();
                _permissionedRemoteChatControls.Clear();
                _lastPeerVoiceDiagnosticFingerprints.Clear();
                _peerVoiceDiagnosticEvidence.Clear();
                _destroyQueued = false;
                _destroyCallBegan = false;
                _destroyCompleted = false;
                _phase = PartyChatControlCanaryPhase.Completed;
                _sessionFaulted = true;
                _generation++;
                EnqueueLogLocked(
                    "Stage 2 cleanup complete: local ChatControlDestroyed was returned to PartyFinishProcessingStateChanges.");
            }
        }

        TryScheduleWork();
    }

    public void SetPushToTalkPressed(bool pressed)
    {
        lock (_stateSync)
        {
            if (_disposed != 0)
                return;
            var normalizedPressed = _enableVoiceTest && pressed;
            var changed = normalizedPressed != _pushToTalkPressed;
            _pushToTalkPressed = normalizedPressed;
            if (changed && _enableVoiceTest && _localChatControl != nint.Zero)
                _voiceDiagnosticRequested = true;
        }

        TryScheduleWork();
    }

    public void RequestVoiceDiagnosticSample()
    {
        lock (_stateSync)
        {
            if (_disposed != 0 ||
                !_enableVoiceTest ||
                _suspended ||
                !_nativeCallsAllowed ||
                _localChatControl == nint.Zero)
            {
                return;
            }

            _voiceDiagnosticRequested = true;
        }

        TryScheduleWork();
    }

    /// <summary>
    /// Called by the PartyNetworkLeaveNetwork detour before the game's original function runs.
    /// DestroyChatControl is safe while connected: Party first disconnects it from every network,
    /// then reports the left/completed/destroyed events through the game's normal state-change pump.
    /// </summary>
    public void PrepareForNetworkLeave(nint network)
    {
        lock (_apiCallSync)
        {
            nint localDevice;
            nint localChatControl;

            lock (_stateSync)
            {
                if (_disposed != 0 || _suspended || !_nativeCallsAllowed)
                    return;
                if (_network == nint.Zero || network == nint.Zero || network != _network)
                    return;

                if (_stateBatchActive)
                {
                    _pushToTalkPressed = false;
                    _sessionFaulted = true;
                    _nativeCallsAllowed = false;
                    _phase = PartyChatControlCanaryPhase.Disabled;
                    _generation++;
                    EnqueueVoiceDiagnosticSummaryLocked("network leave during Party state batch");
                    EnqueueLogLocked(
                        "Stage 2 observed PartyNetworkLeaveNetwork while a Party state-change batch was active. " +
                        "The canary issued no overlapping Party calls; Relink's original network leave owns " +
                        "mute/disconnect/destruction and canary calls remain disabled until manager cleanup.");
                    return;
                }

                _preLeaveObserved = true;
                _networkLeaving = true;
                _endpointReady = false;
                _authenticated = false;
                _teardownRequested = _localChatControl != nint.Zero;
                _pushToTalkPressed = false;
                EnqueueVoiceDiagnosticSummaryLocked("Relink network leave");
                _remoteChatControls.Clear();
                _permissionedRemoteChatControls.Clear();
                _lastPeerVoiceDiagnosticFingerprints.Clear();
                _peerVoiceDiagnosticEvidence.Clear();

                if (_localChatControl == nint.Zero)
                {
                    EnqueueLogLocked(
                        $"Stage 2 observed Relink PartyNetworkLeaveNetwork for tracked network={Hex(network)} " +
                        "before a local ChatControl existed; no canary teardown call was needed.");
                    return;
                }

                if (_destroyCallBegan || _destroyedObserved)
                {
                    EnqueueLogLocked(
                        $"Stage 2 observed Relink PartyNetworkLeaveNetwork for tracked network={Hex(network)}; " +
                        "local ChatControl teardown was already in progress.");
                    return;
                }

                if (_localDevice == nint.Zero)
                {
                    FailClosedLocked(
                        "Relink began leaving the tracked network while the owned ChatControl had no local device");
                    return;
                }

                // Invalidate deferred create/connect/disconnect work before queueing the terminal destroy.
                // _apiCallSync ensures no canary native call overlaps this pre-leave operation.
                _generation++;
                _destroyQueued = true;
                _phase = PartyChatControlCanaryPhase.Destroying;
                localDevice = _localDevice;
                localChatControl = _localChatControl;
            }

            uint muteResult;
            uint destroyResult;
            try
            {
                muteResult = _api.SetAudioInputMuted(localChatControl, muted: true);
                destroyResult = _api.DestroyChatControl(localDevice, localChatControl, _destroyToken);
            }
            catch (Exception exception)
            {
                try
                {
                    _ = _api.SetAudioInputMuted(localChatControl, muted: true);
                }
                catch
                {
                    // Relink's original network leave remains the terminal fallback.
                }

                lock (_stateSync)
                {
                    if (_localChatControl == localChatControl && _localDevice == localDevice)
                    {
                        _destroyQueued = false;
                        _teardownRequested = false;
                        _sessionFaulted = true;
                        _nativeCallsAllowed = false;
                        _phase = PartyChatControlCanaryPhase.Disabled;
                        _generation++;
                        EnqueueLogLocked(
                            $"Stage 2 pre-leave native teardown threw {exception.GetType().Name}: " +
                            $"{exception.Message}; Relink's original PartyNetworkLeaveNetwork will continue " +
                            "and Party owns final teardown.");
                    }
                }
                return;
            }

            lock (_stateSync)
            {
                if (_localChatControl != localChatControl || _localDevice != localDevice)
                    return;

                if (muteResult != Success)
                {
                    _sessionFaulted = true;
                    EnqueueLogLocked(
                        $"Stage 2 pre-leave PartyChatControlSetAudioInputMuted(true) returned " +
                        $"0x{muteResult:X8}; destruction was still requested.");
                }
                else
                {
                    _inputUnmuted = false;
                    _microphoneMayBeOpen = false;
                }

                if (destroyResult != Success)
                {
                    _destroyQueued = false;
                    _teardownRequested = false;
                    _sessionFaulted = true;
                    _nativeCallsAllowed = false;
                    _phase = PartyChatControlCanaryPhase.Disabled;
                    _generation++;
                    EnqueueLogLocked(
                        $"Stage 2 pre-leave PartyDeviceDestroyChatControl returned 0x{destroyResult:X8}; " +
                        "Relink's original PartyNetworkLeaveNetwork will continue and Party owns final teardown.");
                    return;
                }

                _destroyCallBegan = true;
                EnqueueLogLocked(
                    $"Stage 2 pre-leave DestroyChatControl queued before Relink PartyNetworkLeaveNetwork: " +
                    $"network={Hex(network)}, chatControl={Hex(localChatControl)}; " +
                    "awaiting local left/completed/destroyed events from the game's state-change pump.");
            }
        }
    }

    /// <summary>
    /// Blocks PartyCleanup long enough to invalidate deferred work and synchronously force input
    /// mute before the manager becomes invalid. PartyCleanup remains the final ownership fallback.
    /// </summary>
    public void BeginManagerCleanup(nint manager)
    {
        lock (_apiCallSync)
        {
            nint chatControl;
            lock (_stateSync)
            {
                if (_manager == nint.Zero || manager != _manager)
                    return;

                _pushToTalkPressed = false;
                _generation++;
                chatControl = _localChatControl;
            }

            uint muteResult = Success;
            Exception? muteException = null;
            if (chatControl != nint.Zero)
            {
                try
                {
                    muteResult = _api.SetAudioInputMuted(chatControl, muted: true);
                }
                catch (Exception exception)
                {
                    muteException = exception;
                }
            }

            lock (_stateSync)
            {
                if (_manager == nint.Zero || manager != _manager)
                    return;

                if (chatControl != nint.Zero && chatControl == _localChatControl)
                {
                    if (muteException is null && muteResult == Success)
                    {
                        _inputUnmuted = false;
                        _microphoneMayBeOpen = false;
                    }
                    else
                    {
                        var failure = muteException is not null
                            ? $"threw {muteException.GetType().Name}: {muteException.Message}"
                            : $"returned 0x{muteResult:X8}";
                        EnqueueLogLocked(
                            $"Stage 3 manager-cleanup emergency mute {failure}; PartyCleanup owns final teardown.");
                    }
                }

                if (_localChatControl != nint.Zero && !_destroyedObserved)
                {
                    EnqueueLogLocked(
                        "Stage 2 manager cleanup reached before local ChatControl teardown completed: " +
                        $"preLeaveObserved={_preLeaveObserved}, leftObserved={_leftObserved}, " +
                        $"destroyQueued={_destroyQueued}, destroyCallBegan={_destroyCallBegan}, " +
                        $"destroyCompleted={_destroyCompleted}, destroyedObserved={_destroyedObserved}. " +
                        "Party owns final teardown.");
                }

                EnqueueVoiceDiagnosticSummaryLocked("Party manager cleanup");

                _nativeCallsAllowed = false;
                _teardownRequested = false;
                _pushToTalkPressed = false;
                if (chatControl == nint.Zero)
                    _inputUnmuted = false;
                _remoteChatControls.Clear();
                _permissionedRemoteChatControls.Clear();
                _lastPeerVoiceDiagnosticFingerprints.Clear();
                _peerVoiceDiagnosticEvidence.Clear();
                _stateBatchActive = false;
                _stateBatchManager = nint.Zero;
                EnqueueLogLocked(
                    "Stage 2 manager cleanup began; canary native calls are now blocked and Party owns final teardown.");
            }
        }
    }

    public void CompleteManagerCleanup(nint manager, bool succeeded)
    {
        lock (_stateSync)
        {
            if (_manager == nint.Zero || manager != _manager)
                return;

            if (!succeeded)
            {
                FailClosedLocked("PartyCleanup failed after canary shutdown began");
                return;
            }

            ResetForNextManagerLocked();
            EnqueueLogLocked("Stage 2 manager cleanup complete; cached user/network/device handles were cleared.");
        }
    }

    public void SuspendBestEffort()
    {
        lock (_apiCallSync)
        {
            nint device;
            nint chatControl;
            bool destroyAlreadyQueued;
            lock (_stateSync)
            {
                _suspended = true;
                _nativeCallsAllowed = false;
                _pushToTalkPressed = false;
                EnqueueVoiceDiagnosticSummaryLocked("Mod suspension");
                _remoteChatControls.Clear();
                _permissionedRemoteChatControls.Clear();
                _lastPeerVoiceDiagnosticFingerprints.Clear();
                _peerVoiceDiagnosticEvidence.Clear();
                _stateBatchActive = false;
                _stateBatchManager = nint.Zero;
                _generation++;
                device = _localDevice;
                chatControl = _localChatControl;
                destroyAlreadyQueued = _destroyQueued;
                if (chatControl != nint.Zero && device != nint.Zero && !destroyAlreadyQueued)
                    _destroyQueued = true;
            }

            if (chatControl == nint.Zero)
                return;

            uint muteResult = uint.MaxValue;
            try
            {
                muteResult = _api.SetAudioInputMuted(chatControl, muted: true);
            }
            catch (Exception exception)
            {
                lock (_stateSync)
                    EnqueueLogLocked($"Stage 3 suspend emergency mute threw {exception.GetType().Name}: {exception.Message}.");
            }

            if (device != nint.Zero && !destroyAlreadyQueued)
            {
                try
                {
                    _ = _api.DestroyChatControl(device, chatControl, _destroyToken);
                }
                catch (Exception exception)
                {
                    lock (_stateSync)
                        EnqueueLogLocked($"Stage 2 suspend ChatControl destruction threw {exception.GetType().Name}: {exception.Message}.");
                }
            }

            if (muteResult == Success)
            {
                lock (_stateSync)
                {
                    _inputUnmuted = false;
                    _microphoneMayBeOpen = false;
                }
            }
        }
    }

    public void DisableFailClosed(string reason)
    {
        lock (_stateSync)
            FailClosedLocked(reason);
    }

    public void ResumeFailClosed()
    {
        lock (_stateSync)
        {
            _suspended = false;
            _nativeCallsAllowed = _manager == nint.Zero;
            if (_manager != nint.Zero)
            {
                _phase = PartyChatControlCanaryPhase.Disabled;
                _sessionFaulted = true;
                EnqueueLogLocked(
                    "Stage 2 canary remains disabled after resume until Party creates a new manager; suspended events were not observed.");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_apiCallSync)
        {
            nint localDevice;
            nint localChatControl;
            bool destroyChatControl;
            lock (_stateSync)
            {
                _nativeCallsAllowed = false;
                _teardownRequested = false;
                _pushToTalkPressed = false;
                EnqueueVoiceDiagnosticSummaryLocked("Mod disposal");
                _remoteChatControls.Clear();
                _permissionedRemoteChatControls.Clear();
                _lastPeerVoiceDiagnosticFingerprints.Clear();
                _peerVoiceDiagnosticEvidence.Clear();
                _stateBatchActive = false;
                _stateBatchManager = nint.Zero;
                _generation++;
                localDevice = _localDevice;
                localChatControl = _localChatControl;
                destroyChatControl = localDevice != nint.Zero &&
                                     localChatControl != nint.Zero &&
                                     !_destroyCallBegan &&
                                     !_destroyedObserved;
            }

            if (localChatControl != nint.Zero)
            {
                try
                {
                    if (_api.SetAudioInputMuted(localChatControl, muted: true) == Success)
                    {
                        lock (_stateSync)
                        {
                            _inputUnmuted = false;
                            _microphoneMayBeOpen = false;
                        }
                    }
                }
                catch
                {
                    // Destruction below is the terminal best-effort privacy boundary.
                }
            }

            if (destroyChatControl)
            {
                try
                {
                    _ = _api.DestroyChatControl(localDevice, localChatControl, _destroyToken);
                }
                catch
                {
                    // Dispose cannot safely re-enter the lifecycle pump; Party/process teardown remains final.
                }
            }
        }

        // Party treats asyncIdentifier as an opaque pointer and may return it in a later state
        // change. The Mod is marked CanUnload=false, so retaining these six tiny GCHandles until
        // process exit is safer than allowing their pointer values to be recycled after an
        // initialization failure while Party may still have an operation in flight.
    }

    private void ObserveAuthenticationLocked(PartyStateChangeSnapshot state)
    {
        if (state.Result != SucceededStateChange ||
            state.Network == nint.Zero ||
            state.LocalUser == nint.Zero)
        {
            EnqueueLogLocked(
                $"Stage 2 prerequisite AuthenticateLocalUserCompleted failed: result={state.Result}, " +
                $"error=0x{state.ErrorDetail:X8}.");
            return;
        }

        if ((_network != nint.Zero && _network != state.Network) ||
            (_localUser != nint.Zero && _localUser != state.LocalUser))
        {
            if (_networkLeaving && _localChatControl == nint.Zero)
            {
                BeginNewSessionLocked(state.Network, state.LocalUser);
            }
            else
            {
                FailClosedLocked(
                    $"authenticated session changed while canary state was active: " +
                    $"network {Hex(_network)} -> {Hex(state.Network)}, " +
                    $"user {Hex(_localUser)} -> {Hex(state.LocalUser)}");
                return;
            }
        }
        else if (_network == nint.Zero && _localUser == nint.Zero)
        {
            BeginNewSessionLocked(state.Network, state.LocalUser);
        }

        _authenticated = true;
        EnqueueLogLocked(
            $"Stage 2 captured authenticated existing session: network={Hex(_network)}, localUser={Hex(_localUser)}.");
    }

    private void ObserveEndpointLocked(PartyStateChangeSnapshot state)
    {
        if (state.Result != SucceededStateChange)
            return;
        if (!_authenticated || state.Network != _network || state.LocalUser != _localUser)
            return;
        if (state.Endpoint == nint.Zero)
        {
            FailClosedLocked("CreateEndpointCompleted succeeded with a null endpoint");
            return;
        }

        _endpointReady = true;
        EnqueueLogLocked(
            $"Stage 2 confirmed Relink's existing gameplay endpoint before canary creation: endpoint={Hex(state.Endpoint)}.");
    }

    private void ObserveCreateCompletedLocked(PartyStateChangeSnapshot state)
    {
        if (state.AsyncIdentifier != _createToken)
            return;

        EnqueueLogLocked(
            $"Stage 2 CreateChatControlCompleted: result={state.Result}, error=0x{state.ErrorDetail:X8}, " +
            $"chatControl={Hex(state.ChatControl)}.");
        if (state.Result != SucceededStateChange ||
            state.LocalDevice != _localDevice ||
            state.LocalUser != _localUser ||
            state.ChatControl != _localChatControl)
        {
            RequestTeardownLocked("CreateChatControlCompleted did not match the owned canary operation");
            return;
        }

        _createCompleted = true;
    }

    private void ObserveChatControlCreatedLocked(PartyStateChangeSnapshot state)
    {
        if (state.ChatControl == nint.Zero)
            return;

        if (state.ChatControl == _localChatControl)
        {
            _createdObserved = true;
            EnqueueLogLocked(
                $"Stage 2 ChatControlCreated (local canary): chatControl={Hex(state.ChatControl)}.");
        }
        else
        {
            EnqueueLogLocked(
                $"Stage 2 ChatControlCreated (remote/other): chatControl={Hex(state.ChatControl)}.");
        }
    }

    private void ObserveAudioCompletedLocked(PartyStateChangeSnapshot state, bool input)
    {
        var expectedToken = input ? _inputToken : _outputToken;
        if (state.AsyncIdentifier != expectedToken)
            return;

        var label = input ? "SetChatAudioInputCompleted" : "SetChatAudioOutputCompleted";
        var selection = input ? _audioInputSelection : _audioOutputSelection;
        var expectedSelectionType = GetPartyAudioSelectionType(selection);
        var expectedContext = selection.UseSystemDefault ? null : selection.DeviceId;
        var contextMatches = expectedContext is null
            ? string.IsNullOrEmpty(state.AudioDeviceSelectionContext)
            : string.Equals(
                state.AudioDeviceSelectionContext,
                expectedContext,
                StringComparison.Ordinal);
        EnqueueLogLocked(
            $"Stage 2 {label}: result={state.Result}, error=0x{state.ErrorDetail:X8}, " +
            $"selectionType={state.Value}, device=\"{selection.DisplayName}\".");
        if (state.Result != SucceededStateChange ||
            state.ChatControl != _localChatControl ||
            state.Value != (uint)expectedSelectionType ||
            !contextMatches)
        {
            RequestTeardownLocked(
                $"{label} did not confirm the owned {expectedSelectionType} device operation");
            return;
        }

        if (input)
            _inputCompleted = true;
        else
            _outputCompleted = true;
    }

    private void ObserveAudioStateChangedLocked(PartyStateChangeSnapshot state, bool input)
    {
        if (state.ChatControl == nint.Zero || state.ChatControl != _localChatControl)
            return;

        if (input)
        {
            if (state.AudioInputState is not { } inputState)
                return;

            _audioInputState = inputState;
            _audioInputStateErrorDetail = state.ErrorDetail;
            EnqueueLogLocked(
                $"Stage 3 Party audio input state: {FormatEnum(inputState)}; " +
                $"errorDetail=0x{state.ErrorDetail:X8}. Expected healthy state: Initialized (1).");
        }
        else
        {
            if (state.AudioOutputState is not { } outputState)
                return;

            _audioOutputState = outputState;
            _audioOutputStateErrorDetail = state.ErrorDetail;
            EnqueueLogLocked(
                $"Stage 3 Party audio output state: {FormatEnum(outputState)}; " +
                $"errorDetail=0x{state.ErrorDetail:X8}. Expected healthy state: Initialized (1).");
        }

        if (_enableVoiceTest)
            _voiceDiagnosticRequested = true;
    }

    private void ObserveConnectCompletedLocked(PartyStateChangeSnapshot state)
    {
        if (state.AsyncIdentifier != _connectToken)
            return;

        EnqueueLogLocked(
            $"Stage 2 ConnectChatControlCompleted: result={state.Result}, error=0x{state.ErrorDetail:X8}, " +
            $"network={Hex(state.Network)}, chatControl={Hex(state.ChatControl)}.");
        if (state.Result != SucceededStateChange ||
            state.Network != _network ||
            state.ChatControl != _localChatControl)
        {
            RequestTeardownLocked("ConnectChatControlCompleted did not match the owned canary operation");
            return;
        }

        _connectCompleted = true;
    }

    private void ObserveJoinedLocked(PartyStateChangeSnapshot state)
    {
        if (state.ChatControl == nint.Zero || state.Network == nint.Zero)
            return;

        if (state.ChatControl == _localChatControl && state.Network == _network)
        {
            _joinedObserved = true;
            EnqueueLogLocked(
                $"Stage 2 ChatControlJoinedNetwork (local canary): network={Hex(state.Network)}, " +
                $"chatControl={Hex(state.ChatControl)}.");
        }
        else
        {
            if (_enableVoiceTest && state.Network == _network)
            {
                var firstRemote = _remoteChatControls.Count == 0;
                if (_remoteChatControls.Add(state.ChatControl) && firstRemote)
                    ResetVoiceDiagnosticEvidenceLocked();
            }
            EnqueueLogLocked(
                $"Stage 2 ChatControlJoinedNetwork (remote/other): network={Hex(state.Network)}, " +
                $"chatControl={Hex(state.ChatControl)}.");
        }
    }

    private void ObserveDisconnectCompletedLocked(PartyStateChangeSnapshot state)
    {
        if (state.AsyncIdentifier != _disconnectToken)
            return;

        EnqueueLogLocked(
            $"Stage 2 DisconnectChatControlCompleted: result={state.Result}, error=0x{state.ErrorDetail:X8}.");
        if (state.Result != SucceededStateChange ||
            state.ChatControl != _localChatControl ||
            state.Network != _network)
        {
            _disconnectQueued = false;
            _joinedObserved = false;
            _teardownRequested = true;
        }
        else
        {
            // A successful disconnect completion means the operation itself is finished. Party normally
            // also supplies ChatControlLeftNetwork; do not leak the local control if that notification is
            // absent or delayed beyond this batch.
            _joinedObserved = false;
            _leftObserved = true;
            _teardownRequested = true;
        }
    }

    private void ObserveLeftLocked(PartyStateChangeSnapshot state)
    {
        if (state.ChatControl == _localChatControl)
        {
            _pushToTalkPressed = false;
            EnqueueVoiceDiagnosticSummaryLocked("local ChatControl left network");
            _remoteChatControls.Clear();
            _permissionedRemoteChatControls.Clear();
            _lastPeerVoiceDiagnosticFingerprints.Clear();
            _peerVoiceDiagnosticEvidence.Clear();
            _leftObserved = true;
            _joinedObserved = false;
            _teardownRequested = true;
            EnqueueLogLocked(
                $"Stage 2 ChatControlLeftNetwork (local canary): reason={state.Reason}, " +
                $"error=0x{state.ErrorDetail:X8}, network={Hex(state.Network)}.");
        }
        else if (state.ChatControl != nint.Zero)
        {
            if (state.Network == _network)
            {
                _remoteChatControls.Remove(state.ChatControl);
                _permissionedRemoteChatControls.Remove(state.ChatControl);
                _lastPeerVoiceDiagnosticFingerprints.Remove(state.ChatControl);
                if (_permissionedRemoteChatControls.Count == 0 &&
                    _phase == PartyChatControlCanaryPhase.VoiceReady)
                {
                    _phase = PartyChatControlCanaryPhase.JoinedMuted;
                }
                if (_remoteChatControls.Count == 0)
                    EnqueueVoiceDiagnosticSummaryLocked("last remote ChatControl left network");
                _peerVoiceDiagnosticEvidence.Remove(state.ChatControl);
            }
            EnqueueLogLocked(
                $"Stage 2 ChatControlLeftNetwork (remote/other): reason={state.Reason}, " +
                $"network={Hex(state.Network)}, chatControl={Hex(state.ChatControl)}.");
        }
    }

    private void ObserveDestroyCompletedLocked(PartyStateChangeSnapshot state)
    {
        if (state.AsyncIdentifier != _destroyToken)
            return;

        EnqueueLogLocked(
            $"Stage 2 DestroyChatControlCompleted: result={state.Result}, error=0x{state.ErrorDetail:X8}.");
        if (state.ChatControl == _localChatControl && state.Result == SucceededStateChange)
        {
            _destroyCompleted = true;
        }
        else
        {
            FailClosedLocked("DestroyChatControlCompleted did not confirm the owned canary operation");
        }
    }

    private void ObserveDestroyedLocked(PartyStateChangeSnapshot state)
    {
        if (state.ChatControl == _localChatControl && _localChatControl != nint.Zero)
        {
            _destroyedObserved = true;
            EnqueueLogLocked(
                $"Stage 2 ChatControlDestroyed (local canary): reason={state.Reason}, " +
                $"error=0x{state.ErrorDetail:X8}.");
        }
        else if (state.ChatControl != nint.Zero)
        {
            _remoteChatControls.Remove(state.ChatControl);
            _permissionedRemoteChatControls.Remove(state.ChatControl);
            _lastPeerVoiceDiagnosticFingerprints.Remove(state.ChatControl);
            if (_permissionedRemoteChatControls.Count == 0 &&
                _phase == PartyChatControlCanaryPhase.VoiceReady)
            {
                _phase = PartyChatControlCanaryPhase.JoinedMuted;
            }
            if (_remoteChatControls.Count == 0)
                EnqueueVoiceDiagnosticSummaryLocked("last remote ChatControl destroyed");
            _peerVoiceDiagnosticEvidence.Remove(state.ChatControl);
            EnqueueLogLocked(
                $"Stage 2 ChatControlDestroyed (remote/other): reason={state.Reason}, " +
                $"chatControl={Hex(state.ChatControl)}.");
        }
    }

    private void ObserveNetworkCleanupLocked(PartyStateChangeSnapshot state)
    {
        if (_network == nint.Zero || state.Network != _network)
            return;
        if ((PartyStateChangeType)state.Type == PartyStateChangeType.LeaveNetworkCompleted &&
            state.Result != SucceededStateChange)
            return;

        _networkLeaving = true;
        _endpointReady = false;
        _authenticated = false;
        _pushToTalkPressed = false;
        EnqueueVoiceDiagnosticSummaryLocked("Party network cleanup state");
        _remoteChatControls.Clear();
        _permissionedRemoteChatControls.Clear();
        _lastPeerVoiceDiagnosticFingerprints.Clear();
        _peerVoiceDiagnosticEvidence.Clear();
        _teardownRequested = _localChatControl != nint.Zero;
    }

    private void ObserveLocalUserRemovedLocked(PartyStateChangeSnapshot state)
    {
        if (_localUser == nint.Zero || state.LocalUser != _localUser)
            return;
        if (state.Network != nint.Zero && _network != nint.Zero && state.Network != _network)
            return;

        _authenticated = false;
        _networkLeaving = true;
        _pushToTalkPressed = false;
        EnqueueVoiceDiagnosticSummaryLocked("local Party user removed");
        _remoteChatControls.Clear();
        _permissionedRemoteChatControls.Clear();
        _lastPeerVoiceDiagnosticFingerprints.Clear();
        _peerVoiceDiagnosticEvidence.Clear();
        _teardownRequested = _localChatControl != nint.Zero;
    }

    private void ObserveLocalUserDestroyedLocked(PartyStateChangeSnapshot state)
    {
        if (_localUser == nint.Zero || state.LocalUser != _localUser ||
            state.Result != SucceededStateChange)
            return;

        _authenticated = false;
        _localUser = nint.Zero;
        if (_localChatControl != nint.Zero && !_destroyedObserved)
        {
            _nativeCallsAllowed = false;
            _sessionFaulted = true;
            _phase = PartyChatControlCanaryPhase.Disabled;
            _generation++;
            EnqueueLogLocked(
                "Stage 2 local user was destroyed before local ChatControl cleanup completed; native calls stopped.");
        }
    }

    private void PromoteJoinedLocked()
    {
        if (_phase == PartyChatControlCanaryPhase.Connecting &&
            _connectCompleted &&
            _joinedObserved)
        {
            _phase = PartyChatControlCanaryPhase.JoinedMuted;
            EnqueueLogLocked(_enableVoiceTest
                ? "Stage 2 muted ChatControl canary joined the existing PartyNetwork. Input remains muted; " +
                  "Stage 3 microphone permissions wait for a remote Mod ChatControl on this same network."
                : "Stage 2 muted ChatControl canary joined the existing PartyNetwork. " +
                  "Input remains muted and chat permissions remain None.");
        }
    }

    private void TryScheduleWork()
    {
        CanaryWorkItem work;
        lock (_stateSync)
        {
            if (_disposed != 0 ||
                _suspended ||
                !_nativeCallsAllowed ||
                _stateBatchActive ||
                _workScheduled != 0)
                return;

            work = GetNextWorkLocked();
            if (work.Kind == CanaryWorkKind.None)
                return;

            _workScheduled = 1;
            switch (work.Kind)
            {
                case CanaryWorkKind.Create:
                    _phase = PartyChatControlCanaryPhase.Creating;
                    break;
                case CanaryWorkKind.Connect:
                    _phase = PartyChatControlCanaryPhase.Connecting;
                    break;
                case CanaryWorkKind.Disconnect:
                    _disconnectQueued = true;
                    _phase = PartyChatControlCanaryPhase.Disconnecting;
                    break;
                case CanaryWorkKind.Destroy:
                    _destroyQueued = true;
                    _phase = PartyChatControlCanaryPhase.Destroying;
                    break;
                case CanaryWorkKind.GrantVoicePermissions:
                case CanaryWorkKind.ApplyPushToTalk:
                case CanaryWorkKind.CaptureVoiceDiagnostics:
                    break;
            }
        }

        try
        {
            _schedule(() => ExecuteWork(work));
        }
        catch (Exception exception)
        {
            lock (_stateSync)
            {
                _workScheduled = 0;
                FailClosedLocked($"could not schedule deferred canary work: {exception.Message}");
            }
        }
    }

    private CanaryWorkItem GetNextWorkLocked()
    {
        if (_destroyedObserved)
            return default;

        if (_teardownRequested && _localChatControl != nint.Zero)
        {
            if (_joinedObserved && !_networkLeaving && !_leftObserved && !_disconnectQueued && _network != nint.Zero)
            {
                return CaptureWorkLocked(CanaryWorkKind.Disconnect);
            }

            if ((!_joinedObserved || _networkLeaving || _leftObserved) && !_destroyQueued && _localDevice != nint.Zero)
                return CaptureWorkLocked(CanaryWorkKind.Destroy);
        }

        if (_sessionFaulted)
            return default;

        if (_phase == PartyChatControlCanaryPhase.WaitingForAuthenticatedSession &&
            _authenticated &&
            _endpointReady &&
            !_networkLeaving &&
            _manager != nint.Zero &&
            _network != nint.Zero &&
            _localUser != nint.Zero)
        {
            return CaptureWorkLocked(CanaryWorkKind.Create);
        }

        if (_phase == PartyChatControlCanaryPhase.ConfiguringMutedAudio &&
            _createCompleted &&
            _createdObserved &&
            _inputCompleted &&
            _outputCompleted &&
            _localChatControl != nint.Zero &&
            !_networkLeaving)
        {
            return CaptureWorkLocked(CanaryWorkKind.Connect);
        }

        if (_enableVoiceTest &&
            (_phase == PartyChatControlCanaryPhase.JoinedMuted ||
             _phase == PartyChatControlCanaryPhase.VoiceReady) &&
            _joinedObserved &&
            !_networkLeaving &&
            _localChatControl != nint.Zero)
        {
            foreach (var remoteChatControl in _remoteChatControls)
            {
                if (!_permissionedRemoteChatControls.Contains(remoteChatControl))
                {
                    return CaptureWorkLocked(
                        CanaryWorkKind.GrantVoicePermissions,
                        targetChatControl: remoteChatControl);
                }
            }

            var shouldUnmute = ShouldInputBeUnmutedLocked();
            if (shouldUnmute != _inputUnmuted)
            {
                return CaptureWorkLocked(
                    CanaryWorkKind.ApplyPushToTalk,
                    unmuteInput: shouldUnmute);
            }

            if (_voiceDiagnosticRequested && _remoteChatControls.Count != 0)
            {
                _voiceDiagnosticRequested = false;
                return CaptureWorkLocked(CanaryWorkKind.CaptureVoiceDiagnostics);
            }
        }

        return default;
    }

    private CanaryWorkItem CaptureWorkLocked(
        CanaryWorkKind kind,
        nint targetChatControl = default,
        bool unmuteInput = false) =>
        new(
            kind,
            _generation,
            _manager,
            _network,
            _localUser,
            _localDevice,
            _localChatControl,
            targetChatControl,
            unmuteInput);

    private void ExecuteWork(CanaryWorkItem work)
    {
        try
        {
            lock (_apiCallSync)
            {
                if (!IsWorkCurrent(work))
                    return;

                switch (work.Kind)
                {
                    case CanaryWorkKind.Create:
                        ExecuteCreate(work);
                        break;
                    case CanaryWorkKind.Connect:
                        ExecuteConnect(work);
                        break;
                    case CanaryWorkKind.Disconnect:
                        ExecuteDisconnect(work);
                        break;
                    case CanaryWorkKind.Destroy:
                        ExecuteDestroy(work);
                        break;
                    case CanaryWorkKind.GrantVoicePermissions:
                        ExecuteGrantVoicePermissions(work);
                        break;
                    case CanaryWorkKind.ApplyPushToTalk:
                        ExecutePushToTalk(work);
                        break;
                    case CanaryWorkKind.CaptureVoiceDiagnostics:
                        ExecuteVoiceDiagnosticsNonFatal(work);
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            if (work.ChatControl != nint.Zero)
            {
                lock (_apiCallSync)
                {
                    try
                    {
                        _ = _api.SetAudioInputMuted(work.ChatControl, muted: true);
                    }
                    catch
                    {
                        // The terminal destroy fallback below remains the final privacy boundary.
                    }
                }
            }

            lock (_stateSync)
            {
                if (work.ChatControl != nint.Zero && work.ChatControl == _localChatControl)
                {
                    RequestEmergencyVoiceTeardownLocked(
                        $"deferred native action {work.Kind} threw {exception.GetType().Name}: {exception.Message}");
                }
                else
                {
                    FailClosedLocked($"deferred native action {work.Kind} threw: {exception.Message}");
                }
            }
        }
        finally
        {
            lock (_stateSync)
                _workScheduled = 0;
            TryScheduleWork();
        }
    }

    private void ExecuteCreate(CanaryWorkItem work)
    {
        var result = _api.GetLocalDevice(work.Manager, out var localDevice);
        if (result != Success || localDevice == nint.Zero)
        {
            FailNativeAction("PartyGetLocalDevice", result, hasOwnedChatControl: false);
            return;
        }

        result = _api.GetLocalChatControlCount(localDevice, out var existingLocalChatControls);
        if (result != Success)
        {
            FailNativeAction("PartyDeviceGetChatControls", result, hasOwnedChatControl: false);
            return;
        }
        if (existingLocalChatControls != 0)
        {
            lock (_stateSync)
            {
                FailClosedLocked(
                    $"local device already owns {existingLocalChatControls} ChatControl(s); ownership is ambiguous");
            }
            return;
        }

        result = _api.CreateChatControl(localDevice, work.LocalUser, _createToken, out var chatControl);
        if (result != Success || chatControl == nint.Zero)
        {
            FailNativeAction("PartyDeviceCreateChatControl", result, hasOwnedChatControl: false);
            return;
        }

        lock (_stateSync)
        {
            if (!IsWorkCurrentLocked(work))
            {
                _ = _api.SetAudioInputMuted(chatControl, muted: true);
                _ = _api.DestroyChatControl(localDevice, chatControl, _destroyToken);
                EnqueueLogLocked(
                    "Stage 2 create completed after its session became stale; the orphaned ChatControl was immediately muted and destroyed.");
                return;
            }
            _localDevice = localDevice;
            _localChatControl = chatControl;
            _inputUnmuted = false;
            _microphoneMayBeOpen = false;
            _pushToTalkPressed = false;
            _remoteChatControls.Clear();
            _permissionedRemoteChatControls.Clear();
            ResetVoiceDiagnosticEvidenceLocked();
        }

        result = _api.SetAudioInputMuted(chatControl, muted: true);
        if (result != Success)
        {
            FailNativeAction("PartyChatControlSetAudioInputMuted(true)", result, hasOwnedChatControl: true);
            return;
        }

        result = _api.GetAudioInputMuted(chatControl, out var muted);
        if (result != Success || !muted)
        {
            FailNativeAction(
                "PartyChatControlGetAudioInputMuted verification",
                result,
                hasOwnedChatControl: true);
            return;
        }

        var inputSelectionType = GetPartyAudioSelectionType(_audioInputSelection);
        result = _api.SetAudioInput(
            chatControl,
            inputSelectionType,
            _audioInputSelection.DeviceId,
            _inputToken);
        if (result != Success)
        {
            FailNativeAction(
                $"PartyChatControlSetAudioInput({inputSelectionType})",
                result,
                hasOwnedChatControl: true);
            return;
        }

        var outputSelectionType = GetPartyAudioSelectionType(_audioOutputSelection);
        result = _api.SetAudioOutput(
            chatControl,
            outputSelectionType,
            _audioOutputSelection.DeviceId,
            _outputToken);
        if (result != Success)
        {
            FailNativeAction(
                $"PartyChatControlSetAudioOutput({outputSelectionType})",
                result,
                hasOwnedChatControl: true);
            return;
        }

        lock (_stateSync)
        {
            if (!IsWorkCurrentLocked(work))
                return;
            _phase = PartyChatControlCanaryPhase.ConfiguringMutedAudio;
            EnqueueLogLocked(
                $"Stage 2 canary creation queued on existing manager/network/device: manager={Hex(work.Manager)}, " +
                $"network={Hex(work.Network)}, localUser={Hex(work.LocalUser)}, device={Hex(localDevice)}, " +
                $"chatControl={Hex(chatControl)}. Input mute was set and verified before audio selection; " +
                $"microphone=\"{_audioInputSelection.DisplayName}\" ({inputSelectionType}), " +
                $"playback=\"{_audioOutputSelection.DisplayName}\" ({outputSelectionType}); " +
                (_enableVoiceTest
                    ? "microphone permissions remain None until a remote Mod ChatControl joins this network."
                    : "PartyChatControlSetPermissions will not be called."));
        }
    }

    private void ExecuteConnect(CanaryWorkItem work)
    {
        var result = _api.ConnectChatControl(work.Network, work.ChatControl, _connectToken);
        if (result != Success)
        {
            FailNativeAction("PartyNetworkConnectChatControl", result, hasOwnedChatControl: true);
            return;
        }

        lock (_stateSync)
        {
            if (IsWorkCurrentLocked(work))
            {
                EnqueueLogLocked(
                    $"Stage 2 ConnectChatControl queued for existing network={Hex(work.Network)}, " +
                    $"chatControl={Hex(work.ChatControl)}; awaiting completion and joined events.");
            }
        }
    }

    private void ExecuteGrantVoicePermissions(CanaryWorkItem work)
    {
        var result = _api.SetPermissions(
            work.ChatControl,
            work.TargetChatControl,
            MicrophoneVoicePermissions);
        if (result != Success)
        {
            _ = _api.SetAudioInputMuted(work.ChatControl, muted: true);
            lock (_stateSync)
            {
                if (IsWorkCurrentLocked(work))
                {
                    RequestEmergencyVoiceTeardownLocked(
                        $"PartyChatControlSetPermissions(microphone send/receive) returned 0x{result:X8}");
                }
            }
            return;
        }

        lock (_stateSync)
        {
            if (!IsWorkCurrentLocked(work))
                return;

            _permissionedRemoteChatControls.Add(work.TargetChatControl);
            _phase = PartyChatControlCanaryPhase.VoiceReady;
            _voiceDiagnosticRequested = true;
            _nextVoiceDiagnosticTimestamp = Stopwatch.GetTimestamp() + VoiceDiagnosticIntervalTicks;
            EnqueueLogLocked(
                $"Stage 3 voice test permissions granted for remote ChatControl={Hex(work.TargetChatControl)} " +
                $"on network={Hex(work.Network)}: SendMicrophoneAudio|ReceiveMicrophoneAudio (0x0005). " +
                "Input remains muted until U is held.");
        }
    }

    private void ExecutePushToTalk(CanaryWorkItem work)
    {
        if (work.UnmuteInput)
        {
            lock (_stateSync)
            {
                if (!IsWorkCurrentLocked(work))
                    return;

                // Set this before touching Party. Another thread can now fail closed without a
                // window where native input is open but the managed state still says muted.
                _microphoneMayBeOpen = true;
            }
        }

        var desiredMuted = !work.UnmuteInput;
        var setResult = _api.SetAudioInputMuted(work.ChatControl, desiredMuted);
        var observedMuted = true;
        var verifyResult = setResult == Success
            ? _api.GetAudioInputMuted(work.ChatControl, out observedMuted)
            : uint.MaxValue;
        if (setResult != Success || verifyResult != Success || observedMuted != desiredMuted)
        {
            var emergencyMuteResult = _api.SetAudioInputMuted(work.ChatControl, muted: true);
            lock (_stateSync)
            {
                if (work.ChatControl == _localChatControl)
                {
                    if (emergencyMuteResult == Success)
                        _microphoneMayBeOpen = false;
                    RequestEmergencyVoiceTeardownLocked(
                        $"push-to-talk mute transition failed: requestedMuted={desiredMuted}, " +
                        $"set=0x{setResult:X8}, verify=0x{verifyResult:X8}, observedMuted={observedMuted}, " +
                        $"emergencyMute=0x{emergencyMuteResult:X8}");
                }
            }
            return;
        }

        var staleUnmute = false;
        lock (_stateSync)
        {
            if (!IsWorkCurrentLocked(work))
            {
                staleUnmute = work.UnmuteInput;
            }
            else
            {
                _inputUnmuted = work.UnmuteInput;
                _microphoneMayBeOpen = work.UnmuteInput;
                _voiceDiagnosticRequested = true;
                if (work.UnmuteInput)
                {
                    _pttCycleActive = true;
                    _pttCycleLocalTalkingObserved = false;
                }
                else if (_pttCycleActive)
                {
                    EnqueueLogLocked(
                        "Stage 3 local microphone capture result for the completed U hold: " +
                        (_pttCycleLocalTalkingObserved
                            ? "PASS - Party GetLocalChatIndicator reached Talking."
                            : "NOT OBSERVED - Party never reported Talking; speak for at least three seconds " +
                              "during the next U hold and check the diagnostic snapshots."));
                    _pttCycleActive = false;
                }
                EnqueueLogLocked(work.UnmuteInput
                    ? "Stage 3 push-to-talk microphone UNMUTED while U is held."
                    : "Stage 3 push-to-talk microphone muted.");
            }
        }

        if (!staleUnmute)
            return;

        var staleMuteResult = _api.SetAudioInputMuted(work.ChatControl, muted: true);
        lock (_stateSync)
        {
            if (staleMuteResult == Success)
            {
                _inputUnmuted = false;
                _microphoneMayBeOpen = false;
            }
            else if (work.ChatControl == _localChatControl)
            {
                RequestEmergencyVoiceTeardownLocked(
                    $"stale push-to-talk unmute could not be reversed: mute=0x{staleMuteResult:X8}");
            }
        }
    }

    private void ExecuteVoiceDiagnosticsNonFatal(CanaryWorkItem work)
    {
        try
        {
            ExecuteVoiceDiagnostics(work);
        }
        catch (Exception exception)
        {
            // These getters are an evidence-only troubleshooting layer. A diagnostic failure must
            // never alter permissions, mute state, connection ownership or teardown behavior.
            lock (_stateSync)
            {
                if (IsWorkCurrentLocked(work))
                {
                    EnqueueLogLocked(
                        $"Stage 3 voice diagnostics were inconclusive because the read-only sampler " +
                        $"threw {exception.GetType().Name}: {Sanitize(exception.Message)}. " +
                        "Voice state was left unchanged.");
                }
            }
        }
    }

    private void ExecuteVoiceDiagnostics(CanaryWorkItem work)
    {
        nint[] remoteChatControls;
        bool pushToTalkPressed;
        bool inputUnmuted;
        PartyAudioInputState? inputState;
        uint inputStateErrorDetail;
        PartyAudioOutputState? outputState;
        uint outputStateErrorDetail;
        lock (_stateSync)
        {
            if (!IsWorkCurrentLocked(work))
                return;

            remoteChatControls = _remoteChatControls.ToArray();
            pushToTalkPressed = _pushToTalkPressed;
            inputUnmuted = _inputUnmuted && _microphoneMayBeOpen;
            inputState = _audioInputState;
            inputStateErrorDetail = _audioInputStateErrorDetail;
            outputState = _audioOutputState;
            outputStateErrorDetail = _audioOutputStateErrorDetail;
        }

        var inputMuted = CaptureDiagnostic(
            "PartyChatControlGetAudioInputMuted",
            () =>
            {
                var result = _api.GetAudioInputMuted(work.ChatControl, out var value);
                return (result, value);
            });
        var localIndicator = CaptureDiagnostic(
            "PartyChatControlGetLocalChatIndicator",
            () =>
            {
                var result = _api.GetLocalChatIndicator(work.ChatControl, out var value);
                return (result, value);
            });
        var inputDevice = CaptureDiagnostic(
            "PartyChatControlGetAudioInput",
            () =>
            {
                var result = _api.GetAudioInput(
                    work.ChatControl,
                    out var selectionType,
                    out var selectionContext,
                    out var deviceId);
                return (result, new PartyAudioDeviceDiagnostic(selectionType, selectionContext, deviceId));
            });
        var outputDevice = CaptureDiagnostic(
            "PartyChatControlGetAudioOutput",
            () =>
            {
                var result = _api.GetAudioOutput(
                    work.ChatControl,
                    out var selectionType,
                    out var selectionContext,
                    out var deviceId);
                return (result, new PartyAudioDeviceDiagnostic(selectionType, selectionContext, deviceId));
            });

        var localFingerprint =
            $"inputState={FormatAudioState(inputState, inputStateErrorDetail)}, " +
            $"outputState={FormatAudioState(outputState, outputStateErrorDetail)}, " +
            $"pttKeyHeld={pushToTalkPressed}, nativeInputUnmuted={inputUnmuted}, " +
            $"inputMuted={FormatDiagnostic(inputMuted, static value => value.ToString())}, " +
            $"localIndicator={FormatDiagnostic(localIndicator, FormatEnum)}, " +
            $"inputDevice={FormatDiagnostic(inputDevice, FormatAudioDevice)}, " +
            $"outputDevice={FormatDiagnostic(outputDevice, FormatAudioDevice)}, " +
            $"diagnosis={DiagnoseLocalVoicePath(inputState, outputState, pushToTalkPressed, inputUnmuted, inputMuted, localIndicator)}";

        lock (_stateSync)
        {
            if (!IsWorkCurrentLocked(work))
                return;

            _voiceDiagnosticSnapshotObserved = true;
            if (inputDevice.Succeeded)
                _diagnosticInputDevice = inputDevice.Value;
            if (outputDevice.Succeeded)
                _diagnosticOutputDevice = outputDevice.Value;
            if (localIndicator.Succeeded &&
                inputUnmuted &&
                localIndicator.Value == PartyLocalChatControlChatIndicator.Talking)
            {
                _diagnosticLocalTalkingObserved = true;
                if (_pttCycleActive)
                    _pttCycleLocalTalkingObserved = true;
            }

            if (!string.Equals(
                    _lastLocalVoiceDiagnosticFingerprint,
                    localFingerprint,
                    StringComparison.Ordinal))
            {
                _lastLocalVoiceDiagnosticFingerprint = localFingerprint;
                EnqueueLogLocked($"Stage 3 voice diagnostics LOCAL: {localFingerprint}.");
            }
        }

        foreach (var remoteChatControl in remoteChatControls)
        {
            var permissions = CaptureDiagnostic(
                "PartyChatControlGetPermissions",
                () =>
                {
                    var result = _api.GetPermissions(
                        work.ChatControl,
                        remoteChatControl,
                        out var value);
                    return (result, value);
                });
            var remoteIndicator = CaptureDiagnostic(
                "PartyChatControlGetChatIndicator",
                () =>
                {
                    var result = _api.GetChatIndicator(
                        work.ChatControl,
                        remoteChatControl,
                        out var value);
                    return (result, value);
                });
            var incomingMuted = CaptureDiagnostic(
                "PartyChatControlGetIncomingAudioMuted",
                () =>
                {
                    var result = _api.GetIncomingAudioMuted(
                        work.ChatControl,
                        remoteChatControl,
                        out var value);
                    return (result, value);
                });
            var renderVolume = CaptureDiagnostic(
                "PartyChatControlGetAudioRenderVolume",
                () =>
                {
                    var result = _api.GetAudioRenderVolume(
                        work.ChatControl,
                        remoteChatControl,
                        out var value);
                    return (result, value);
                });

            var peerFingerprint =
                $"permissions={FormatDiagnostic(permissions, FormatPermissions)}, " +
                $"remoteIndicator={FormatDiagnostic(remoteIndicator, FormatEnum)}, " +
                $"incomingMuted={FormatDiagnostic(incomingMuted, static value => value.ToString())}, " +
                $"renderVolume={FormatDiagnostic(renderVolume, FormatVolume)}, " +
                $"diagnosis={DiagnoseRemoteVoicePath(permissions, remoteIndicator, incomingMuted, renderVolume)}";

            lock (_stateSync)
            {
                if (!IsWorkCurrentLocked(work) || !_remoteChatControls.Contains(remoteChatControl))
                    continue;

                if (!_peerVoiceDiagnosticEvidence.TryGetValue(
                        remoteChatControl,
                        out var evidence))
                {
                    evidence = new PeerVoiceDiagnosticEvidence();
                    _peerVoiceDiagnosticEvidence.Add(remoteChatControl, evidence);
                }
                if (permissions.Succeeded && HasMicrophoneVoicePermissions(permissions.Value))
                    evidence.PermissionsReadyObserved = true;
                if (remoteIndicator.Succeeded &&
                    remoteIndicator.Value == PartyChatControlChatIndicator.Talking)
                {
                    evidence.RemoteTalkingObserved = true;
                }
                if (incomingMuted.Succeeded && !incomingMuted.Value)
                    evidence.IncomingUnmutedObserved = true;
                if (renderVolume.Succeeded &&
                    float.IsFinite(renderVolume.Value) &&
                    renderVolume.Value > 0.0f)
                {
                    evidence.PositiveRenderVolumeObserved = true;
                }

                if (!_lastPeerVoiceDiagnosticFingerprints.TryGetValue(
                        remoteChatControl,
                        out var previousFingerprint) ||
                    !string.Equals(previousFingerprint, peerFingerprint, StringComparison.Ordinal))
                {
                    _lastPeerVoiceDiagnosticFingerprints[remoteChatControl] = peerFingerprint;
                    EnqueueLogLocked(
                        $"Stage 3 voice diagnostics PEER {Hex(remoteChatControl)}: {peerFingerprint}.");
                }
            }
        }
    }

    private void ExecuteDisconnect(CanaryWorkItem work)
    {
        var muteResult = _api.SetAudioInputMuted(work.ChatControl, muted: true);
        if (muteResult != Success)
        {
            // Do not wait on an asynchronous disconnect while input may still be open. Party's
            // DestroyChatControl contract disconnects the control from every network as part of
            // terminal destruction, which is the shortest available fail-closed path here.
            var destroyResult = _api.DestroyChatControl(
                work.LocalDevice,
                work.ChatControl,
                _destroyToken);
            lock (_stateSync)
            {
                if (!IsWorkCurrentLocked(work))
                    return;

                _disconnectQueued = false;
                _pushToTalkPressed = false;
                EnqueueVoiceDiagnosticSummaryLocked("voice disconnect mute failure");
                _remoteChatControls.Clear();
                _permissionedRemoteChatControls.Clear();
                _lastPeerVoiceDiagnosticFingerprints.Clear();
                _peerVoiceDiagnosticEvidence.Clear();
                _sessionFaulted = true;
                _joinedObserved = false;
                _networkLeaving = true;
                _teardownRequested = true;

                if (destroyResult == Success)
                {
                    _destroyQueued = true;
                    _destroyCallBegan = true;
                    _phase = PartyChatControlCanaryPhase.Destroying;
                    EnqueueLogLocked(
                        $"Stage 3 disconnect mute returned 0x{muteResult:X8}; " +
                        "local ChatControl destruction was queued immediately instead of waiting unmuted.");
                }
                else
                {
                    _destroyQueued = false;
                    _teardownRequested = false;
                    _nativeCallsAllowed = false;
                    _phase = PartyChatControlCanaryPhase.Disabled;
                    _generation++;
                    EnqueueLogLocked(
                        $"Stage 3 disconnect mute returned 0x{muteResult:X8} and emergency " +
                        $"PartyDeviceDestroyChatControl returned 0x{destroyResult:X8}; " +
                        "canary calls stopped and Party owns final cleanup.");
                }
            }
            return;
        }

        var result = _api.DisconnectChatControl(work.Network, work.ChatControl, _disconnectToken);
        if (result != Success)
        {
            lock (_stateSync)
            {
                if (IsWorkCurrentLocked(work))
                {
                    _disconnectQueued = false;
                    _joinedObserved = false;
                    _networkLeaving = true;
                    _teardownRequested = true;
                    EnqueueLogLocked(
                        $"Stage 2 PartyNetworkDisconnectChatControl returned 0x{result:X8}; " +
                        "falling back to local ChatControl destruction while still muted.");
                }
            }
            return;
        }

        lock (_stateSync)
        {
            if (IsWorkCurrentLocked(work))
            {
                _inputUnmuted = false;
                _microphoneMayBeOpen = false;
                EnqueueLogLocked("Stage 2 DisconnectChatControl queued; awaiting left-network event.");
            }
        }
    }

    private void ExecuteDestroy(CanaryWorkItem work)
    {
        var muteResult = _api.SetAudioInputMuted(work.ChatControl, muted: true);
        var result = _api.DestroyChatControl(work.LocalDevice, work.ChatControl, _destroyToken);
        if (result != Success)
        {
            lock (_stateSync)
            {
                if (IsWorkCurrentLocked(work))
                {
                    _destroyQueued = false;
                    FailClosedLocked($"PartyDeviceDestroyChatControl returned 0x{result:X8}");
                }
            }
            return;
        }

        lock (_stateSync)
        {
            if (IsWorkCurrentLocked(work))
            {
                if (muteResult == Success)
                {
                    _inputUnmuted = false;
                    _microphoneMayBeOpen = false;
                }
                _destroyCallBegan = true;
                EnqueueLogLocked("Stage 2 DestroyChatControl queued; awaiting completed and destroyed events.");
            }
        }
    }

    private void RequestPeriodicVoiceDiagnosticsLocked()
    {
        if (!_enableVoiceTest ||
            _phase != PartyChatControlCanaryPhase.VoiceReady ||
            !_joinedObserved ||
            _networkLeaving ||
            _localChatControl == nint.Zero ||
            _remoteChatControls.Count == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now < _nextVoiceDiagnosticTimestamp)
            return;

        _nextVoiceDiagnosticTimestamp = now + VoiceDiagnosticIntervalTicks;
        _voiceDiagnosticRequested = true;
    }

    private void EnqueueVoiceDiagnosticSummaryLocked(string reason)
    {
        if (!_enableVoiceTest || _voiceDiagnosticSummaryLogged)
            return;

        _voiceDiagnosticSummaryLogged = true;
        var peerEvidence = _peerVoiceDiagnosticEvidence.ToArray();
        var completePeer = peerEvidence.FirstOrDefault(
            static pair => pair.Value.CompleteRemotePathObserved);
        var anyPermissions = peerEvidence.Any(static pair => pair.Value.PermissionsReadyObserved);
        var anyIncomingUnmuted = peerEvidence.Any(static pair => pair.Value.IncomingUnmutedObserved);
        var anyPositiveRenderVolume = peerEvidence.Any(
            static pair => pair.Value.PositiveRenderVolumeObserved);
        var anyRemoteTalking = peerEvidence.Any(static pair => pair.Value.RemoteTalkingObserved);
        var verdict = !_voiceDiagnosticSnapshotObserved
            ? "INCONCLUSIVE_NO_DIAGNOSTIC_SNAPSHOT"
            : _audioInputState is null
                ? "INCONCLUSIVE_INPUT_STATE_NOT_OBSERVED"
                : _audioInputState != PartyAudioInputState.Initialized
                    ? $"FAIL_INPUT_STATE_{_audioInputState}"
                    : _audioOutputState is null
                        ? "INCONCLUSIVE_OUTPUT_STATE_NOT_OBSERVED"
                        : _audioOutputState != PartyAudioOutputState.Initialized
                            ? $"FAIL_OUTPUT_STATE_{_audioOutputState}"
                            : !_diagnosticLocalTalkingObserved
                                ? "FAIL_LOCAL_TALKING_NOT_OBSERVED"
                                : peerEvidence.Length == 0
                                    ? "INCONCLUSIVE_NO_PEER_DIAGNOSTIC_EVIDENCE"
                                    : !anyPermissions
                                        ? "FAIL_MICROPHONE_PERMISSIONS_NOT_READ_BACK"
                                        : !anyIncomingUnmuted
                                            ? "FAIL_INCOMING_AUDIO_REMAINED_MUTED_OR_UNREADABLE"
                                            : !anyPositiveRenderVolume
                                                ? "FAIL_RENDER_VOLUME_NOT_POSITIVE_OR_UNREADABLE"
                                                : !anyRemoteTalking
                                                    ? "FAIL_REMOTE_TALKING_NOT_OBSERVED"
                                                    : completePeer.Value is null
                                                        ? "FAIL_NO_SINGLE_PEER_COMPLETED_REMOTE_PATH"
                                                        : "PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH";

        var peerEvidenceText = peerEvidence.Length == 0
            ? "none"
            : string.Join(
                ", ",
                peerEvidence.Select(static pair =>
                    $"{Hex(pair.Key)}[permissions={pair.Value.PermissionsReadyObserved}," +
                    $"incomingUnmuted={pair.Value.IncomingUnmutedObserved}," +
                    $"positiveVolume={pair.Value.PositiveRenderVolumeObserved}," +
                    $"remoteTalking={pair.Value.RemoteTalkingObserved}," +
                    $"complete={pair.Value.CompleteRemotePathObserved}]"));

        EnqueueLogLocked(
            $"Stage 3 voice diagnostic SUMMARY ({reason}): verdict={verdict}; " +
            $"inputState={FormatAudioStateWithoutLookup(_audioInputState, _audioInputStateErrorDetail)}, " +
            $"outputState={FormatAudioStateWithoutLookup(_audioOutputState, _audioOutputStateErrorDetail)}, " +
            $"partySelectedInput={(_diagnosticInputDevice is { } input ? FormatAudioDevice(input) : "NotRead")}, " +
            $"partySelectedOutput={(_diagnosticOutputDevice is { } output ? FormatAudioDevice(output) : "NotRead")}, " +
            $"localTalkingObserved={_diagnosticLocalTalkingObserved}, " +
            $"completePeer={(completePeer.Value is null ? "none" : Hex(completePeer.Key))}, " +
            $"peerEvidence={peerEvidenceText}. " +
            "PASS proves Party observed both directions and an enabled render path; physical audibility " +
            "still depends on the selected Windows endpoint and its mixer.");
    }

    private void ResetVoiceDiagnosticEvidenceLocked()
    {
        _voiceDiagnosticRequested = false;
        _nextVoiceDiagnosticTimestamp = 0;
        _lastLocalVoiceDiagnosticFingerprint = null;
        _lastPeerVoiceDiagnosticFingerprints.Clear();
        _peerVoiceDiagnosticEvidence.Clear();
        _voiceDiagnosticSnapshotObserved = false;
        _diagnosticLocalTalkingObserved = false;
        _diagnosticInputDevice = null;
        _diagnosticOutputDevice = null;
        _pttCycleActive = false;
        _pttCycleLocalTalkingObserved = false;
        _voiceDiagnosticSummaryLogged = false;
    }

    private void ResetVoiceDiagnosticStateLocked()
    {
        _audioInputState = null;
        _audioInputStateErrorDetail = 0;
        _audioOutputState = null;
        _audioOutputStateErrorDetail = 0;
        ResetVoiceDiagnosticEvidenceLocked();
    }

    private static DiagnosticQuery<T> CaptureDiagnostic<T>(
        string operation,
        Func<(uint Result, T Value)> query)
    {
        try
        {
            var (result, value) = query();
            return new DiagnosticQuery<T>(operation, result, value, ExceptionMessage: null);
        }
        catch (Exception exception)
        {
            return new DiagnosticQuery<T>(
                operation,
                uint.MaxValue,
                default!,
                $"{exception.GetType().Name}: {Sanitize(exception.Message)}");
        }
    }

    private string FormatDiagnostic<T>(DiagnosticQuery<T> query, Func<T, string> formatValue)
    {
        if (query.Succeeded)
            return formatValue(query.Value);
        if (query.ExceptionMessage is not null)
            return $"{query.Operation} THREW {query.ExceptionMessage}";

        return $"{query.Operation} ERROR 0x{query.Result:X8} ({DescribePartyError(query.Result)})";
    }

    private string FormatAudioState<TEnum>(TEnum? state, uint errorDetail)
        where TEnum : struct, Enum
    {
        var stateText = state is { } value ? FormatEnum(value) : "NotObserved";
        return errorDetail == Success
            ? $"{stateText}, errorDetail=0x{errorDetail:X8}"
            : $"{stateText}, errorDetail=0x{errorDetail:X8} ({DescribePartyError(errorDetail)})";
    }

    private static string FormatAudioStateWithoutLookup<TEnum>(TEnum? state, uint errorDetail)
        where TEnum : struct, Enum =>
        $"{(state is { } value ? FormatEnum(value) : "NotObserved")}, errorDetail=0x{errorDetail:X8}";

    private string DescribePartyError(uint error)
    {
        try
        {
            var result = _api.GetErrorMessage(error, out var message);
            return result == Success && !string.IsNullOrWhiteSpace(message)
                ? Sanitize(message)
                : $"PartyGetErrorMessage unavailable: 0x{result:X8}";
        }
        catch (Exception exception)
        {
            return $"PartyGetErrorMessage threw {exception.GetType().Name}: {Sanitize(exception.Message)}";
        }
    }

    private static string DiagnoseLocalVoicePath(
        PartyAudioInputState? inputState,
        PartyAudioOutputState? outputState,
        bool pushToTalkPressed,
        bool inputUnmuted,
        DiagnosticQuery<bool> inputMuted,
        DiagnosticQuery<PartyLocalChatControlChatIndicator> localIndicator)
    {
        if (inputState is null || outputState is null)
            return "INCONCLUSIVE_WAITING_FOR_AUDIO_STATE_EVENTS";
        if (inputState != PartyAudioInputState.Initialized)
            return $"FAIL_LOCAL_INPUT_{inputState}";
        if (outputState != PartyAudioOutputState.Initialized)
            return $"FAIL_LOCAL_OUTPUT_{outputState}";
        if (!inputMuted.Succeeded || !localIndicator.Succeeded)
            return "INCONCLUSIVE_LOCAL_GETTER_ERROR";
        if (localIndicator.Value == PartyLocalChatControlChatIndicator.NoAudioInput)
            return "FAIL_LOCAL_NO_AUDIO_INPUT";
        if (pushToTalkPressed && (!inputUnmuted || inputMuted.Value))
            return "FAIL_PTT_DID_NOT_OPEN_PARTY_INPUT";
        if (inputUnmuted &&
            localIndicator.Value == PartyLocalChatControlChatIndicator.AudioInputMuted)
        {
            return "FAIL_LOCAL_INDICATOR_STILL_MUTED";
        }
        if (!inputUnmuted &&
            localIndicator.Value == PartyLocalChatControlChatIndicator.Talking)
        {
            return "FAIL_LOCAL_TALKING_WHILE_INPUT_EXPECTED_MUTED";
        }
        if (localIndicator.Value == PartyLocalChatControlChatIndicator.Talking)
            return "PASS_LOCAL_MICROPHONE_SIGNAL_CAPTURED";
        if (inputUnmuted)
            return "WAITING_FOR_LOCAL_SPEECH_SIGNAL";

        return "READY_INPUT_SAFELY_MUTED";
    }

    private static string DiagnoseRemoteVoicePath(
        DiagnosticQuery<PartyChatPermissionOptions> permissions,
        DiagnosticQuery<PartyChatControlChatIndicator> remoteIndicator,
        DiagnosticQuery<bool> incomingMuted,
        DiagnosticQuery<float> renderVolume)
    {
        if (!permissions.Succeeded ||
            !remoteIndicator.Succeeded ||
            !incomingMuted.Succeeded ||
            !renderVolume.Succeeded)
        {
            return "INCONCLUSIVE_REMOTE_GETTER_ERROR";
        }
        if (!HasMicrophoneVoicePermissions(permissions.Value))
            return "FAIL_PERMISSION_READBACK_MISSING_SEND_OR_RECEIVE_MICROPHONE_AUDIO";
        var indicatorDiagnosis = remoteIndicator.Value switch
        {
            PartyChatControlChatIndicator.IncomingVoiceDisabled => "FAIL_INCOMING_VOICE_DISABLED",
            PartyChatControlChatIndicator.IncomingCommunicationsMuted =>
                "FAIL_INCOMING_COMMUNICATIONS_MUTED",
            PartyChatControlChatIndicator.NoRemoteInput => "FAIL_REMOTE_HAS_NO_AUDIO_INPUT",
            PartyChatControlChatIndicator.RemoteAudioInputMuted => "REMOTE_MICROPHONE_IS_MUTED",
            _ => null,
        };
        if (indicatorDiagnosis is not null)
            return indicatorDiagnosis;
        if (incomingMuted.Value)
            return "FAIL_REMOTE_AUDIO_MUTED_LOCALLY";
        if (!float.IsFinite(renderVolume.Value) || renderVolume.Value <= 0.0f)
            return "FAIL_REMOTE_RENDER_VOLUME_NOT_POSITIVE";

        return remoteIndicator.Value == PartyChatControlChatIndicator.Talking
            ? "PASS_REMOTE_AUDIO_RECEIVED_BY_PARTY"
            : "READY_NO_REMOTE_SPEECH_DETECTED";
    }

    private static bool HasMicrophoneVoicePermissions(PartyChatPermissionOptions permissions) =>
        (permissions & MicrophoneVoicePermissions) == MicrophoneVoicePermissions;

    private static string FormatPermissions(PartyChatPermissionOptions permissions) =>
        $"0x{(uint)permissions:X4} " +
        $"(sendMicrophone={permissions.HasFlag(PartyChatPermissionOptions.SendMicrophoneAudio)}, " +
        $"receiveMicrophone={permissions.HasFlag(PartyChatPermissionOptions.ReceiveMicrophoneAudio)})";

    private static string FormatAudioDevice(PartyAudioDeviceDiagnostic device) =>
        $"selection={FormatEnum(device.SelectionType)}, context={Quote(device.SelectionContext)}, " +
        $"selectedDeviceId={Quote(device.DeviceId)}";

    private static string FormatVolume(float volume) =>
        volume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatEnum<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        $"{value} ({Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture)})";

    private static string Quote(string? value) =>
        value is null ? "<null>" : $"\"{Sanitize(value)}\"";

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');

    private void FailNativeAction(string operation, uint error, bool hasOwnedChatControl)
    {
        lock (_stateSync)
        {
            EnqueueLogLocked($"Stage 2 {operation} returned 0x{error:X8}; canary is failing closed.");
            _sessionFaulted = true;
            if (hasOwnedChatControl && _localChatControl != nint.Zero)
            {
                _pushToTalkPressed = false;
                _teardownRequested = true;
                _joinedObserved = false;
                _networkLeaving = true;
            }
            else
            {
                _phase = PartyChatControlCanaryPhase.Disabled;
            }
        }
    }

    private void RequestTeardownLocked(string reason)
    {
        _pushToTalkPressed = false;
        EnqueueLogLocked(
            $"Stage 2 canary validation failed: {reason}; cleanup requested with microphone mute enforced.");
        _sessionFaulted = true;
        _teardownRequested = _localChatControl != nint.Zero;
        if (!_teardownRequested)
            _phase = PartyChatControlCanaryPhase.Disabled;
    }

    private void RequestEmergencyVoiceTeardownLocked(string reason)
    {
        _pushToTalkPressed = false;
        _inputUnmuted = false;
        EnqueueVoiceDiagnosticSummaryLocked("Stage 3 fail-closed teardown");
        _remoteChatControls.Clear();
        _permissionedRemoteChatControls.Clear();
        _lastPeerVoiceDiagnosticFingerprints.Clear();
        _peerVoiceDiagnosticEvidence.Clear();
        _sessionFaulted = true;
        _teardownRequested = _localChatControl != nint.Zero;
        _joinedObserved = false;
        _networkLeaving = true;
        EnqueueLogLocked(
            $"Stage 3 voice test failed closed: {reason}; microphone was re-muted best-effort and " +
            "local ChatControl destruction was requested.");
        if (!_teardownRequested)
            _phase = PartyChatControlCanaryPhase.Disabled;
    }

    private void FailClosedLocked(string reason)
    {
        _sessionFaulted = true;
        _pushToTalkPressed = false;
        _phase = PartyChatControlCanaryPhase.Disabled;
        _generation++;
        if ((_inputUnmuted || _microphoneMayBeOpen) &&
            _localChatControl != nint.Zero &&
            !_suspended)
        {
            _teardownRequested = true;
            _joinedObserved = false;
            _networkLeaving = true;
            EnqueueLogLocked(
                $"Stage 3 voice test failed closed while the microphone was open: {reason}; " +
                "emergency mute and local ChatControl destruction were requested.");
        }
        else
        {
            _nativeCallsAllowed = false;
            _teardownRequested = false;
            EnqueueLogLocked($"Stage 2 canary disabled (fail-closed): {reason}.");
        }
    }

    private bool IsWorkCurrent(CanaryWorkItem work)
    {
        lock (_stateSync)
            return IsWorkCurrentLocked(work);
    }

    private bool IsWorkCurrentLocked(CanaryWorkItem work)
    {
        if (_disposed != 0 ||
            _suspended ||
            !_nativeCallsAllowed ||
            _stateBatchActive ||
            work.Generation != _generation ||
            work.Manager != _manager ||
            (work.Network != nint.Zero && work.Network != _network) ||
            (work.LocalUser != nint.Zero && work.LocalUser != _localUser) ||
            (work.LocalDevice != nint.Zero && work.LocalDevice != _localDevice) ||
            (work.ChatControl != nint.Zero && work.ChatControl != _localChatControl))
        {
            return false;
        }

        if (work.Kind == CanaryWorkKind.GrantVoicePermissions &&
            !_remoteChatControls.Contains(work.TargetChatControl))
        {
            return false;
        }

        return work.Kind != CanaryWorkKind.ApplyPushToTalk ||
               work.UnmuteInput == ShouldInputBeUnmutedLocked();
    }

    private bool ShouldInputBeUnmutedLocked() =>
        _enableVoiceTest &&
        _pushToTalkPressed &&
        !_sessionFaulted &&
        !_networkLeaving &&
        _joinedObserved &&
        _phase == PartyChatControlCanaryPhase.VoiceReady &&
        _permissionedRemoteChatControls.Count != 0;

    private PartyVoiceUiStatus GetVoiceUiStatusLocked()
    {
        if (!_enableVoiceTest)
            return PartyVoiceUiStatus.Disabled;

        if (_sessionFaulted || _phase == PartyChatControlCanaryPhase.Disabled)
            return new PartyVoiceUiStatus(PartyVoiceUiState.Faulted);

        if (_disposed != 0 || _suspended || !_nativeCallsAllowed)
            return PartyVoiceUiStatus.Unavailable;

        if (_phase == PartyChatControlCanaryPhase.VoiceReady)
        {
            if (_permissionedRemoteChatControls.Count == 0)
                return new PartyVoiceUiStatus(PartyVoiceUiState.WaitingForPeer);

            // _inputUnmuted changes only after Party accepts the transition and the mute readback
            // confirms the requested native state. A raw key-down can never report Speaking.
            return _inputUnmuted && _microphoneMayBeOpen
                ? new PartyVoiceUiStatus(PartyVoiceUiState.Speaking)
                : new PartyVoiceUiStatus(PartyVoiceUiState.Ready);
        }

        return _phase switch
        {
            PartyChatControlCanaryPhase.WaitingForAuthenticatedSession or
            PartyChatControlCanaryPhase.Completed =>
                new PartyVoiceUiStatus(PartyVoiceUiState.WaitingForSession),
            PartyChatControlCanaryPhase.Creating or
            PartyChatControlCanaryPhase.ConfiguringMutedAudio or
            PartyChatControlCanaryPhase.Connecting =>
                new PartyVoiceUiStatus(PartyVoiceUiState.Connecting),
            PartyChatControlCanaryPhase.JoinedMuted =>
                new PartyVoiceUiStatus(PartyVoiceUiState.WaitingForPeer),
            PartyChatControlCanaryPhase.Disconnecting or
            PartyChatControlCanaryPhase.Destroying =>
                new PartyVoiceUiStatus(PartyVoiceUiState.Disconnecting),
            _ => PartyVoiceUiStatus.Unavailable,
        };
    }

    private void BeginNewSessionLocked(nint network, nint localUser)
    {
        _network = network;
        _localUser = localUser;
        _localDevice = nint.Zero;
        _localChatControl = nint.Zero;
        _authenticated = true;
        _endpointReady = false;
        _networkLeaving = false;
        _sessionFaulted = false;
        _createCompleted = false;
        _createdObserved = false;
        _inputCompleted = false;
        _outputCompleted = false;
        _connectCompleted = false;
        _joinedObserved = false;
        _disconnectQueued = false;
        _leftObserved = false;
        _destroyQueued = false;
        _destroyCallBegan = false;
        _destroyCompleted = false;
        _destroyedObserved = false;
        _teardownRequested = false;
        _preLeaveObserved = false;
        _pushToTalkPressed = false;
        _inputUnmuted = false;
        _microphoneMayBeOpen = false;
        _stateBatchActive = false;
        _stateBatchManager = nint.Zero;
        _remoteChatControls.Clear();
        _permissionedRemoteChatControls.Clear();
        ResetVoiceDiagnosticStateLocked();
        _phase = PartyChatControlCanaryPhase.WaitingForAuthenticatedSession;
        _generation++;
    }

    private void ResetForNextManagerLocked()
    {
        _manager = nint.Zero;
        _network = nint.Zero;
        _localUser = nint.Zero;
        _localDevice = nint.Zero;
        _localChatControl = nint.Zero;
        _authenticated = false;
        _endpointReady = false;
        _networkLeaving = false;
        _nativeCallsAllowed = true;
        _sessionFaulted = false;
        _createCompleted = false;
        _createdObserved = false;
        _inputCompleted = false;
        _outputCompleted = false;
        _connectCompleted = false;
        _joinedObserved = false;
        _disconnectQueued = false;
        _leftObserved = false;
        _destroyQueued = false;
        _destroyCallBegan = false;
        _destroyCompleted = false;
        _destroyedObserved = false;
        _teardownRequested = false;
        _preLeaveObserved = false;
        _pushToTalkPressed = false;
        _inputUnmuted = false;
        _microphoneMayBeOpen = false;
        _stateBatchActive = false;
        _stateBatchManager = nint.Zero;
        _remoteChatControls.Clear();
        _permissionedRemoteChatControls.Clear();
        ResetVoiceDiagnosticStateLocked();
        _phase = PartyChatControlCanaryPhase.WaitingForAuthenticatedSession;
        _generation++;
    }

    private void EnqueueLogLocked(string message) => _log(message);

    private static PartyAudioDeviceSelectionType GetPartyAudioSelectionType(
        ResolvedAudioEndpointSelection selection) =>
        selection.UseSystemDefault
            ? PartyAudioDeviceSelectionType.SystemDefault
            : PartyAudioDeviceSelectionType.Manual;

    private static string Hex(nint value) => $"0x{(nuint)value:X}";

    private static GCHandle AllocateToken(string operation) =>
        GCHandle.Alloc(new CanaryAsyncToken(operation), GCHandleType.Normal);

    private static void QueueOnThreadPool(Action action) =>
        ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!).Invoke(), action);

    private sealed record CanaryAsyncToken(string Operation);

    private sealed class PeerVoiceDiagnosticEvidence
    {
        public bool PermissionsReadyObserved { get; set; }

        public bool IncomingUnmutedObserved { get; set; }

        public bool PositiveRenderVolumeObserved { get; set; }

        public bool RemoteTalkingObserved { get; set; }

        public bool CompleteRemotePathObserved =>
            PermissionsReadyObserved &&
            IncomingUnmutedObserved &&
            PositiveRenderVolumeObserved &&
            RemoteTalkingObserved;
    }

    private readonly record struct PartyAudioDeviceDiagnostic(
        PartyAudioDeviceSelectionType SelectionType,
        string? SelectionContext,
        string? DeviceId);

    private readonly record struct DiagnosticQuery<T>(
        string Operation,
        uint Result,
        T Value,
        string? ExceptionMessage)
    {
        public bool Succeeded => Result == Success && ExceptionMessage is null;
    }

    private enum CanaryWorkKind
    {
        None,
        Create,
        Connect,
        Disconnect,
        Destroy,
        GrantVoicePermissions,
        ApplyPushToTalk,
        CaptureVoiceDiagnostics,
    }

    private readonly record struct CanaryWorkItem(
        CanaryWorkKind Kind,
        long Generation,
        nint Manager,
        nint Network,
        nint LocalUser,
        nint LocalDevice,
        nint ChatControl,
        nint TargetChatControl,
        bool UnmuteInput);
}
