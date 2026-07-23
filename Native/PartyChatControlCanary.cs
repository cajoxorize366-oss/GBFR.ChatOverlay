using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Native;

internal enum PartyChatControlCanaryPhase
{
    WaitingForAuthenticatedSession,
    Creating,
    ConfiguringMutedAudio,
    Connecting,
    JoinedMuted,
    Disconnecting,
    Destroying,
    Completed,
    Disabled,
}

/// <summary>
/// Stage 2 validation only: creates one locally owned Party ChatControl on the game's existing
/// authenticated Party session, verifies input mute before device selection, and never grants
/// any chat permissions. All native actions run after the host finishes the observed state batch.
/// </summary>
internal sealed class PartyChatControlCanary : IDisposable
{
    private const uint Success = 0;
    private const uint SucceededStateChange = 0;
    private const uint SystemDefaultAudioDevice = 1;

    private readonly IPartyChatControlApi _api;
    private readonly Action<string> _log;
    private readonly Action<Action> _schedule;
    private readonly object _stateSync = new();
    private readonly object _apiCallSync = new();
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
    private int _workScheduled;
    private long _generation;
    private int _disposed;

    public PartyChatControlCanary(
        IPartyChatControlApi api,
        Action<string> log,
        Action<Action>? schedule = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _log = log ?? throw new ArgumentNullException(nameof(log));
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
    /// Called only after the game's original PartyFinishProcessingStateChanges returns.
    /// </summary>
    public void OnBatchFinished(nint manager)
    {
        lock (_stateSync)
        {
            if (manager == nint.Zero || manager != _manager || _suspended)
                return;

            if (_destroyedObserved &&
                (!_destroyCallBegan || _destroyCompleted) &&
                _localChatControl != nint.Zero)
            {
                _localChatControl = nint.Zero;
                _localDevice = nint.Zero;
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

                _preLeaveObserved = true;
                _networkLeaving = true;
                _endpointReady = false;
                _authenticated = false;
                _teardownRequested = _localChatControl != nint.Zero;

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
    /// Blocks PartyCleanup only long enough to prevent it racing a canary native call.
    /// No Party API is called from this method.
    /// </summary>
    public void BeginManagerCleanup(nint manager)
    {
        lock (_apiCallSync)
        {
            lock (_stateSync)
            {
                if (_manager != nint.Zero && manager == _manager)
                {
                    if (_localChatControl != nint.Zero && !_destroyedObserved)
                    {
                        EnqueueLogLocked(
                            "Stage 2 manager cleanup reached before local ChatControl teardown completed: " +
                            $"preLeaveObserved={_preLeaveObserved}, leftObserved={_leftObserved}, " +
                            $"destroyQueued={_destroyQueued}, destroyCallBegan={_destroyCallBegan}, " +
                            $"destroyCompleted={_destroyCompleted}, destroyedObserved={_destroyedObserved}. " +
                            "Party owns final teardown.");
                    }

                    _nativeCallsAllowed = false;
                    _teardownRequested = false;
                    _generation++;
                    EnqueueLogLocked(
                        "Stage 2 manager cleanup began; canary native calls are now blocked and Party owns final teardown.");
                }
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
                _generation++;
                device = _localDevice;
                chatControl = _localChatControl;
                destroyAlreadyQueued = _destroyQueued;
                if (chatControl != nint.Zero && device != nint.Zero && !destroyAlreadyQueued)
                    _destroyQueued = true;
            }

            if (chatControl == nint.Zero)
                return;

            _ = _api.SetAudioInputMuted(chatControl, muted: true);
            if (device != nint.Zero && !destroyAlreadyQueued)
                _ = _api.DestroyChatControl(device, chatControl, _destroyToken);
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
            lock (_stateSync)
            {
                _nativeCallsAllowed = false;
                _teardownRequested = false;
                _generation++;
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
        EnqueueLogLocked(
            $"Stage 2 {label}: result={state.Result}, error=0x{state.ErrorDetail:X8}, " +
            $"selectionType={state.Value}.");
        if (state.Result != SucceededStateChange ||
            state.ChatControl != _localChatControl ||
            state.Value != SystemDefaultAudioDevice)
        {
            RequestTeardownLocked($"{label} did not confirm the owned system-default operation");
            return;
        }

        if (input)
            _inputCompleted = true;
        else
            _outputCompleted = true;
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
            _leftObserved = true;
            _joinedObserved = false;
            _teardownRequested = true;
            EnqueueLogLocked(
                $"Stage 2 ChatControlLeftNetwork (local canary): reason={state.Reason}, " +
                $"error=0x{state.ErrorDetail:X8}, network={Hex(state.Network)}.");
        }
        else if (state.ChatControl != nint.Zero)
        {
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
            EnqueueLogLocked(
                "Stage 2 muted ChatControl canary joined the existing PartyNetwork. " +
                "Input remains muted and chat permissions remain None.");
        }
    }

    private void TryScheduleWork()
    {
        CanaryWorkItem work;
        lock (_stateSync)
        {
            if (_disposed != 0 || _suspended || !_nativeCallsAllowed || _workScheduled != 0)
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

        return default;
    }

    private CanaryWorkItem CaptureWorkLocked(CanaryWorkKind kind) =>
        new(
            kind,
            _generation,
            _manager,
            _network,
            _localUser,
            _localDevice,
            _localChatControl);

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
                }
            }
        }
        catch (Exception exception)
        {
            lock (_stateSync)
                FailClosedLocked($"deferred native action {work.Kind} threw: {exception.Message}");
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

        result = _api.SetSystemDefaultAudioInput(chatControl, _inputToken);
        if (result != Success)
        {
            FailNativeAction("PartyChatControlSetAudioInput(system-default)", result, hasOwnedChatControl: true);
            return;
        }

        result = _api.SetSystemDefaultAudioOutput(chatControl, _outputToken);
        if (result != Success)
        {
            FailNativeAction("PartyChatControlSetAudioOutput(system-default)", result, hasOwnedChatControl: true);
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
                $"chatControl={Hex(chatControl)}. Input mute was set and verified before system-default I/O selection; " +
                "PartyChatControlSetPermissions is not bound or called.");
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

    private void ExecuteDisconnect(CanaryWorkItem work)
    {
        _ = _api.SetAudioInputMuted(work.ChatControl, muted: true);
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
                EnqueueLogLocked("Stage 2 DisconnectChatControl queued; awaiting left-network event.");
        }
    }

    private void ExecuteDestroy(CanaryWorkItem work)
    {
        _ = _api.SetAudioInputMuted(work.ChatControl, muted: true);
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
                _destroyCallBegan = true;
                EnqueueLogLocked("Stage 2 DestroyChatControl queued; awaiting completed and destroyed events.");
            }
        }
    }

    private void FailNativeAction(string operation, uint error, bool hasOwnedChatControl)
    {
        lock (_stateSync)
        {
            EnqueueLogLocked($"Stage 2 {operation} returned 0x{error:X8}; canary is failing closed.");
            _sessionFaulted = true;
            if (hasOwnedChatControl && _localChatControl != nint.Zero)
            {
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
        EnqueueLogLocked($"Stage 2 canary validation failed: {reason}; cleanup requested while muted.");
        _sessionFaulted = true;
        _teardownRequested = _localChatControl != nint.Zero;
        if (!_teardownRequested)
            _phase = PartyChatControlCanaryPhase.Disabled;
    }

    private void FailClosedLocked(string reason)
    {
        _sessionFaulted = true;
        _nativeCallsAllowed = false;
        _teardownRequested = false;
        _phase = PartyChatControlCanaryPhase.Disabled;
        _generation++;
        EnqueueLogLocked($"Stage 2 canary disabled (fail-closed): {reason}.");
    }

    private bool IsWorkCurrent(CanaryWorkItem work)
    {
        lock (_stateSync)
            return IsWorkCurrentLocked(work);
    }

    private bool IsWorkCurrentLocked(CanaryWorkItem work) =>
        _disposed == 0 &&
        !_suspended &&
        _nativeCallsAllowed &&
        work.Generation == _generation &&
        work.Manager == _manager &&
        (work.Network == nint.Zero || work.Network == _network) &&
        (work.LocalUser == nint.Zero || work.LocalUser == _localUser) &&
        (work.LocalDevice == nint.Zero || work.LocalDevice == _localDevice) &&
        (work.ChatControl == nint.Zero || work.ChatControl == _localChatControl);

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
        _phase = PartyChatControlCanaryPhase.WaitingForAuthenticatedSession;
        _generation++;
    }

    private void EnqueueLogLocked(string message) => _log(message);

    private static string Hex(nint value) => $"0x{(nuint)value:X}";

    private static GCHandle AllocateToken(string operation) =>
        GCHandle.Alloc(new CanaryAsyncToken(operation), GCHandleType.Normal);

    private static void QueueOnThreadPool(Action action) =>
        ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!).Invoke(), action);

    private sealed record CanaryAsyncToken(string Operation);

    private enum CanaryWorkKind
    {
        None,
        Create,
        Connect,
        Disconnect,
        Destroy,
    }

    private readonly record struct CanaryWorkItem(
        CanaryWorkKind Kind,
        long Generation,
        nint Manager,
        nint Network,
        nint LocalUser,
        nint LocalDevice,
        nint ChatControl);
}
