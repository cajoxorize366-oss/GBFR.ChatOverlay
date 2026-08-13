using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GBFR.ChatOverlay.Audio;
using GBFR.ChatOverlay.Core;
using Reloaded.Hooks.Definitions;
using ReloadedHooksApi = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace GBFR.ChatOverlay.Native;

public sealed class PartyLifecycleProbe : IDisposable
{
    public const string SupportedPartySha256 =
        "3f0c6abbb735d81fa766a105982bda73f1d2c2cf01109fa2e7cf64813a52ce55";
    private const string SupportedPartyFileVersion = "1.10.2509.24002";
    private const string SupportedPartyProductVersion = "1.10.12";

    private const string PartyModuleName = "PartyWin.dll";
    private const int MaximumStateChangesPerBatch = 4_096;
    private const int MaximumPendingLogs = 512;

    private readonly ReloadedHooksApi _hooks;
    private readonly Action<string> _log;
    private readonly bool _enableLifecycleLogging;
    private readonly bool _enableVoice;
    private readonly ResolvedAudioEndpointSelection _audioInputSelection;
    private readonly ResolvedAudioEndpointSelection _audioOutputSelection;
    private readonly Action _invalidateRoomIdentity;
    private readonly Func<PartyRoomIdentitySnapshot>? _roomIdentityReader;
    private readonly object _lifecycleSync = new();
    private readonly ConcurrentQueue<string> _pendingLogs = new();
    private readonly PartyRoomSessionTracker _onlineRoom = new();

    private IHook<PartyInitializeDelegate>? _initializeHook;
    private IHook<PartyCleanupDelegate>? _cleanupHook;
    private IHook<PartyNetworkLeaveNetworkDelegate>? _leaveNetworkHook;
    private IHook<PartyStartProcessingStateChangesDelegate>? _startProcessingHook;
    private IHook<PartyFinishProcessingStateChangesDelegate>? _finishProcessingHook;
    private PartyVoiceSession? _voiceSession;
    private PartyPlayerMuteController? _playerMuteController;
    private RelinkPartyMemberIdentityResolver? _partyIdentityResolver;
    private PartyAudioWorkPump? _audioWorkPump;
    private nint _partyHandle;
    private bool _initialized;
    private bool _suspended;
    private int _pendingLogCount;
    private int _logDrainScheduled;
    private int _inspectionFailureLogged;
    private int _startFailureLogged;
    private int _finishFailureLogged;
    private int _diagnosticRequestFailureLogged;
    private int _identitySnapshotFailureLogged;
    private nint _audioWorkStartPendingManager;
    private int _disposed;

    public PartyLifecycleProbe(
        ReloadedHooksApi hooks,
        Action<string> log,
        bool enableLifecycleLogging = true,
        bool enableVoice = false,
        ResolvedAudioEndpointSelection? audioInputSelection = null,
        ResolvedAudioEndpointSelection? audioOutputSelection = null,
        Action? invalidateRoomIdentity = null)
        : this(
            hooks,
            log,
            enableLifecycleLogging,
            enableVoice,
            audioInputSelection,
            audioOutputSelection,
            invalidateRoomIdentity,
            null)
    {
    }

    internal PartyLifecycleProbe(
        ReloadedHooksApi hooks,
        Action<string> log,
        bool enableLifecycleLogging,
        bool enableVoice,
        ResolvedAudioEndpointSelection? audioInputSelection,
        ResolvedAudioEndpointSelection? audioOutputSelection,
        Action? invalidateRoomIdentity = null,
        Func<PartyRoomIdentitySnapshot>? roomIdentityReader = null)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _enableLifecycleLogging = enableLifecycleLogging;
        _enableVoice = enableVoice;
        _audioInputSelection = audioInputSelection ??
            ResolvedAudioEndpointSelection.SystemDefault();
        _audioOutputSelection = audioOutputSelection ??
            ResolvedAudioEndpointSelection.SystemDefault();
        _invalidateRoomIdentity = invalidateRoomIdentity ?? (() => { });
        _roomIdentityReader = roomIdentityReader;
        _onlineRoom.ConfigureSnapshotReaders(
            ReadRoomIdentitySnapshotSafely,
            GetEstablishedVoiceParticipantCount);
    }

    public bool IsInitialized => Volatile.Read(ref _initialized);

    internal string? ModulePath { get; private set; }

    public bool IsOnlineRoomActive =>
        IsInitialized && !Volatile.Read(ref _suspended) && _onlineRoom.IsActive;

    internal PartyNetworkLocalRole LocalNetworkRole =>
        IsOnlineRoomActive
            ? _onlineRoom.LocalNetworkRole
            : PartyNetworkLocalRole.Unknown;

    public bool IsVoiceAvailable => IsInitialized && _enableVoice && _voiceSession is not null;

    public bool IsVoicePushToTalkReady =>
        IsVoiceAvailable &&
        !Volatile.Read(ref _suspended) &&
        _voiceSession?.IsRemotePushToTalkReady == true;

    internal int EstablishedVoiceParticipantCount
    {
        get
        {
            if (!_enableVoice || !IsInitialized || Volatile.Read(ref _suspended))
                return 0;

            try
            {
                return _voiceSession?.EstablishedVoiceParticipantCount ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    internal bool TryReadRoomTransition(out PartyRoomTransition transition)
    {
        transition = default;
        if (!IsInitialized)
            return false;
        return _onlineRoom.TryReadTransition(out transition);
    }

    internal bool TryReadMemberTransition(out PartyMemberTransition transition)
    {
        transition = default;
        if (!IsInitialized || Volatile.Read(ref _suspended))
            return false;
        return _onlineRoom.TryReadMemberTransition(out transition);
    }

    internal PartyVoiceUiStatus VoiceUiStatus
    {
        get
        {
            if (!_enableVoice)
                return PartyVoiceUiStatus.Disabled;
            if (!IsInitialized || Volatile.Read(ref _suspended))
                return PartyVoiceUiStatus.Unavailable;

            return _voiceSession?.VoiceUiStatus ?? PartyVoiceUiStatus.Unavailable;
        }
    }

    internal IReadOnlyList<PartyPlayerMuteSlotStatus> GetPlayerMuteSlots()
    {
        if (!IsInitialized || Volatile.Read(ref _suspended) || !_onlineRoom.IsActive)
        {
            return PartyPlayerMuteSlotStatus.Unavailable(
                "仅在联机房间中可用。 / Available only in an online room.");
        }

        try
        {
            return _playerMuteController?.GetSnapshot() ??
                   PartyPlayerMuteSlotStatus.Unavailable(
                       "玩家禁言服务不可用。 / Player mute service is unavailable.");
        }
        catch (Exception exception)
        {
            LogInspectionFailureOnce(exception);
            return PartyPlayerMuteSlotStatus.Unavailable(
                "玩家身份读取失败。 / Player identity lookup failed.");
        }
    }

    internal PartyVoiceIndicatorSnapshot GetVoiceIndicatorSnapshot()
    {
        if (!IsInitialized || Volatile.Read(ref _suspended) || !_onlineRoom.IsActive)
            return PartyVoiceIndicatorSnapshot.Unavailable;

        try
        {
            if (_partyIdentityResolver?.TryResolveCoherentSnapshot(out var identitySnapshot) != true)
                return PartyVoiceIndicatorSnapshot.Unavailable;

            var entitySnapshot = _voiceSession?.GetVoiceEntitySnapshot() ??
                                 PartyVoiceEntitySnapshot.Empty;
            return MapVoiceIndicatorSnapshot(identitySnapshot, entitySnapshot);
        }
        catch (Exception exception)
        {
            LogIdentitySnapshotFailureOnce("voice indicator", exception);
            return PartyVoiceIndicatorSnapshot.Unavailable;
        }
    }

    internal static IReadOnlyList<int> MapRemotePlayers(
        RelinkPartyMemberIdentitySnapshot snapshot,
        IReadOnlyCollection<string>? entityIds)
    {
        if (entityIds is null || entityIds.Count == 0 ||
            !PartyRoomIdentitySnapshotResolver.TryNormalizeSnapshot(
                snapshot.EntityIds,
                snapshot.LocalMemberSlot,
                out _))
        {
            return Array.Empty<int>();
        }

        var entityIdSlots = snapshot.EntityIds;
        var selected = entityIds.ToHashSet(StringComparer.Ordinal);
        if (selected.Count != entityIds.Count)
            return Array.Empty<int>();
        var result = new List<int>(3);
        for (var actualSlot = 0; actualSlot < PartyMemberSlotMap.MemberCount; actualSlot++)
        {
            if (actualSlot == snapshot.LocalMemberSlot ||
                string.IsNullOrEmpty(entityIdSlots[actualSlot]) ||
                !selected.Contains(entityIdSlots[actualSlot]) ||
                !PartyMemberSlotMap.TryGetRemoteOrdinal(
                    snapshot.LocalMemberSlot,
                    actualSlot,
                    out var remoteOrdinal))
            {
                continue;
            }

            result.Add(remoteOrdinal);
        }

        return result;
    }

    internal static IReadOnlyList<int> MapOccupiedRemotePlayers(
        RelinkPartyMemberIdentitySnapshot snapshot)
    {
        if (!PartyRoomIdentitySnapshotResolver.TryNormalizeSnapshot(
                snapshot.EntityIds,
                snapshot.LocalMemberSlot,
                out _))
        {
            return Array.Empty<int>();
        }

        var entityIdSlots = snapshot.EntityIds;
        var result = new List<int>(3);
        for (var actualSlot = 0; actualSlot < PartyMemberSlotMap.MemberCount; actualSlot++)
        {
            if (actualSlot == snapshot.LocalMemberSlot ||
                string.IsNullOrEmpty(entityIdSlots[actualSlot]) ||
                !PartyMemberSlotMap.TryGetRemoteOrdinal(
                    snapshot.LocalMemberSlot,
                    actualSlot,
                    out var remoteOrdinal))
            {
                continue;
            }

            result.Add(remoteOrdinal);
        }

        return result;
    }

    internal static IReadOnlyList<int> MapTalkingRemotePlayers(
        RelinkPartyMemberIdentitySnapshot snapshot,
        IReadOnlyCollection<string>? talkingEntityIds) =>
        MapRemotePlayers(snapshot, talkingEntityIds);

    internal static PartyVoiceIndicatorSnapshot MapVoiceIndicatorSnapshot(
        RelinkPartyMemberIdentitySnapshot identitySnapshot,
        PartyVoiceEntitySnapshot entitySnapshot)
    {
        if (!PartyRoomIdentitySnapshotResolver.TryNormalizeSnapshot(
                identitySnapshot.EntityIds,
                identitySnapshot.LocalMemberSlot,
                out _))
        {
            return PartyVoiceIndicatorSnapshot.Unavailable;
        }

        return new PartyVoiceIndicatorSnapshot(
            true,
            MapRemotePlayers(
                identitySnapshot,
                entitySnapshot.EstablishedRemoteEntityIds ?? Array.Empty<string>()),
            MapOccupiedRemotePlayers(identitySnapshot),
            MapRemotePlayers(
                identitySnapshot,
                entitySnapshot.TalkingRemoteEntityIds ?? Array.Empty<string>()));
    }

    internal PartyPlayerMuteOperationResult SetPlayerMuted(int playerNumber, bool muted)
    {
        if (!IsInitialized || Volatile.Read(ref _suspended) || !_onlineRoom.IsActive)
        {
            return new PartyPlayerMuteOperationResult(
                false,
                "仅在联机房间中可修改玩家禁言。 / Player mute can be changed only in an online room.");
        }

        try
        {
            return _playerMuteController?.SetPlayerMuted(playerNumber, muted) ??
                   new PartyPlayerMuteOperationResult(
                       false,
                       "玩家禁言服务不可用。 / Player mute service is unavailable.");
        }
        catch (Exception exception)
        {
            LogInspectionFailureOnce(exception);
            return new PartyPlayerMuteOperationResult(
                false,
                "玩家禁言操作失败。 / Player mute operation failed.");
        }
    }

    public void Initialize()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_initialized)
                return;

            using var process = Process.GetCurrentProcess();
            var mainModule = process.MainModule ??
                throw new InvalidOperationException("The game module is unavailable.");
            var expectedPartyPath = Path.Combine(
                Path.GetDirectoryName(mainModule.FileName) ??
                    throw new InvalidOperationException("The game directory is unavailable."),
                PartyModuleName);
            var partyModules = process.Modules
                .Cast<ProcessModule>()
                .Where(module => string.Equals(module.ModuleName, PartyModuleName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (partyModules.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected one loaded {PartyModuleName} module, found {partyModules.Length}.");
            }

            var partyModule = partyModules[0];
            if (!string.Equals(
                    Path.GetFullPath(partyModule.FileName),
                    Path.GetFullPath(expectedPartyPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Loaded {PartyModuleName} is outside the verified game directory: {partyModule.FileName}.");
            }

            var partyVersion = FileVersionInfo.GetVersionInfo(partyModule.FileName);
            if (!string.Equals(
                    partyVersion.FileVersion,
                    SupportedPartyFileVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    partyVersion.ProductVersion,
                    SupportedPartyProductVersion,
                    StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"Unsupported {PartyModuleName} version file={partyVersion.FileVersion ?? "unknown"}, " +
                    $"product={partyVersion.ProductVersion ?? "unknown"}; expected file=" +
                    $"{SupportedPartyFileVersion}, product={SupportedPartyProductVersion}.");
            }

            ModulePath = partyModule.FileName;
            var module = partyModule.BaseAddress;

            try
            {
                PartyNativeApi? partyApi = null;
                try
                {
                    partyApi = new PartyNativeApi(module);
                }
                catch (Exception exception)
                {
                    EnqueueLog(
                        $"Party ChatControl API unavailable; player mute and voice remain disabled: " +
                        exception.Message);
                }

                if (partyApi is not null)
                {
                    try
                    {
                        var relinkRvas = RelinkBuildLocator.Resolve(mainModule.FileName);
                        var identityResolver = RelinkPartyMemberIdentityResolver.CreateForCurrentProcess(
                            mainModule.BaseAddress,
                            relinkRvas);
                        _partyIdentityResolver = identityResolver;
                        _playerMuteController = new PartyPlayerMuteController(
                            partyApi,
                            identityResolver,
                            EnqueueLog);
                    }
                    catch (Exception exception)
                    {
                        _playerMuteController = null;
                        EnqueueLog(
                            $"Exact Relink-slot/Party-EntityId player mute mapping unavailable: " +
                            exception.Message);
                    }
                }

                _onlineRoom.ConfigureMemberTracking(
                    partyApi,
                    () =>
                    {
                        if (_partyIdentityResolver?.TryResolveCoherentSnapshot(out var snapshot) == true)
                            return snapshot;
                        return default;
                    });

                if (_enableVoice && partyApi is not null)
                {
                    try
                    {
                        _voiceSession = new PartyVoiceSession(
                            partyApi,
                            EnqueueLog,
                            audioInputSelection: _audioInputSelection,
                            audioOutputSelection: _audioOutputSelection);
                        _audioWorkPump = new PartyAudioWorkPump(
                            partyApi,
                            EnqueueLog,
                            reason => _voiceSession?.DisableFailClosed(reason));
                    }
                    catch (Exception exception)
                    {
                        _voiceSession = null;
                        EnqueueLog(
                            $"Party voice session unavailable; lifecycle observation remains active: " +
                            exception.Message);
                    }
                }

                _initializeHook = _hooks.CreateHook<PartyInitializeDelegate>(
                    PartyInitialize,
                    NativeLibrary.GetExport(module, "PartyInitialize"));
                _initializeHook.Activate();

                _cleanupHook = _hooks.CreateHook<PartyCleanupDelegate>(
                    PartyCleanup,
                    NativeLibrary.GetExport(module, "PartyCleanup"));
                _cleanupHook.Activate();

                _leaveNetworkHook = _hooks.CreateHook<PartyNetworkLeaveNetworkDelegate>(
                    PartyNetworkLeaveNetwork,
                    NativeLibrary.GetExport(module, "PartyNetworkLeaveNetwork"));
                _leaveNetworkHook.Activate();

                _startProcessingHook = _hooks.CreateHook<PartyStartProcessingStateChangesDelegate>(
                    PartyStartProcessingStateChanges,
                    NativeLibrary.GetExport(module, "PartyStartProcessingStateChanges"));
                _startProcessingHook.Activate();

                _finishProcessingHook = _hooks.CreateHook<PartyFinishProcessingStateChangesDelegate>(
                    PartyFinishProcessingStateChanges,
                    NativeLibrary.GetExport(module, "PartyFinishProcessingStateChanges"));
                _finishProcessingHook.Activate();

                Volatile.Write(ref _initialized, true);
                _log(_voiceSession is null
                    ? _playerMuteController is null
                        ? $"Party lifecycle probe attached at 0x{(nuint)module:X}; observation only, no Party calls or sends."
                        : $"Party lifecycle/player mute attached at 0x{(nuint)module:X}; " +
                          "players 2-4 are correlated by the verified Relink slot EntityId before Party audio is changed."
                    : $"Party lifecycle/voice session attached at 0x{(nuint)module:X}; " +
                      "one ChatControl may join the existing PartyNetwork. Push-to-talk unmutes Party's native " +
                      "selected microphone path directly, and input stays muted unless the configured key is held.");
            }
            catch
            {
                DisableHooks();
                _onlineRoom.ResetMemberTransitions();
                _audioWorkPump?.Dispose();
                _audioWorkPump = null;
                _voiceSession?.Dispose();
                _voiceSession = null;
                _playerMuteController?.Reset();
                _playerMuteController = null;
                _partyIdentityResolver = null;
                _onlineRoom.Reset();
                InvalidateRoomIdentitySafely();
                throw;
            }
        }
    }

    public void Suspend()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_lifecycleSync)
        {
            Volatile.Write(ref _suspended, true);
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            DisableHooks();
            _onlineRoom.ResetMemberTransitions();
        }
        _audioWorkPump?.DetachManager(nint.Zero, "Mod suspension");
        _voiceSession?.SuspendBestEffort();
        _playerMuteController?.Reset();
    }

    public void Resume()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            Volatile.Write(ref _suspended, false);
            try
            {
                _initializeHook?.Enable();
                _cleanupHook?.Enable();
                _leaveNetworkHook?.Enable();
                _startProcessingHook?.Enable();
                _finishProcessingHook?.Enable();
                _voiceSession?.ResumeFailClosed();
            }
            catch
            {
                Volatile.Write(ref _suspended, true);
                DisableHooks();
                _onlineRoom.ResetMemberTransitions();
                _audioWorkPump?.DetachManager(nint.Zero, "failed Mod resume");
                _voiceSession?.SuspendBestEffort();
                throw;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        PartyAudioWorkPump? audioWorkPump;
        PartyVoiceSession? voiceSession;
        lock (_lifecycleSync)
        {
            Volatile.Write(ref _suspended, true);
            Volatile.Write(ref _initialized, false);
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            Interlocked.Exchange(ref _partyHandle, nint.Zero);
            RunDisposeStep("finish-processing hook", () => _finishProcessingHook?.Disable());
            RunDisposeStep("start-processing hook", () => _startProcessingHook?.Disable());
            RunDisposeStep("leave-network hook", () => _leaveNetworkHook?.Disable());
            RunDisposeStep("cleanup hook", () => _cleanupHook?.Disable());
            RunDisposeStep("initialize hook", () => _initializeHook?.Disable());
            // Reloaded cannot prove that every detour has returned here. Retain the disabled hook
            // objects so an in-flight callback can still reach its OriginalFunction safely.
            _onlineRoom.ResetMemberTransitions();
            _onlineRoom.Reset();
            _playerMuteController?.Reset();
            _playerMuteController = null;
            _partyIdentityResolver = null;
            audioWorkPump = _audioWorkPump;
            _audioWorkPump = null;
            voiceSession = _voiceSession;
            _voiceSession = null;
        }

        RunDisposeStep("Party audio work pump", () => audioWorkPump?.Dispose());
        RunDisposeStep("Party voice session", () => voiceSession?.Dispose());
        InvalidateRoomIdentitySafely();
    }

    public void SetPushToTalkPressed(bool pressed)
    {
        try
        {
            _voiceSession?.SetPushToTalkPressed(pressed);
        }
        catch (Exception exception)
        {
            LogInspectionFailureOnce(exception);
        }
    }

    public void RequestVoiceDiagnosticSample()
    {
        try
        {
            _voiceSession?.RequestVoiceDiagnosticSample();
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _diagnosticRequestFailureLogged, 1) == 0)
            {
                EnqueueLog(
                    $"Party voice diagnostic request failed without changing voice state; " +
                    $"further request failures are suppressed: {exception.Message}");
            }
        }
    }

    private uint PartyInitialize(nint titleId, nint handleOutput)
    {
        var result = _initializeHook!.OriginalFunction(titleId, handleOutput);
        if (Volatile.Read(ref _suspended))
            return result;

        try
        {
            if (result == 0 && handleOutput != nint.Zero)
            {
                var handle = Marshal.ReadIntPtr(handleOutput);
                InvalidateRoomIdentitySafely();
                _onlineRoom.Reset();
                _playerMuteController?.Reset();
                if (CapturePartyHandle(handle, "PartyInitialize"))
                    EnsureAudioWorkPump(handle, "PartyInitialize");
            }
            else if (result != 0)
            {
                EnqueueLog($"PartyInitialize returned error 0x{result:X8}.");
            }
        }
        catch (Exception exception)
        {
            LogInspectionFailureOnce(exception);
        }

        return result;
    }

    private uint PartyCleanup(nint handle)
    {
        var roomIdentity = ReadRoomIdentitySnapshotSafely();
        InvalidateRoomIdentitySafely();
        Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
        _audioWorkPump?.DetachManager(
            nint.Zero,
            $"PartyCleanup for manager 0x{(nuint)handle:X}");
        _playerMuteController?.Reset();
        _voiceSession?.BeginManagerCleanup(handle);
        var result = _cleanupHook!.OriginalFunction(handle);
        if (Volatile.Read(ref _suspended))
        {
            if (result == 0)
            {
                Interlocked.CompareExchange(ref _partyHandle, nint.Zero, handle);
                _onlineRoom.ResetPreservingTransitions(roomIdentity);
            }
            _voiceSession?.CompleteManagerCleanup(handle, succeeded: result == 0);
            return result;
        }

        try
        {
            if (result == 0)
            {
                Interlocked.CompareExchange(ref _partyHandle, nint.Zero, handle);
                _onlineRoom.ResetPreservingTransitions(roomIdentity);
                _voiceSession?.CompleteManagerCleanup(handle, succeeded: true);
                EnqueueLog($"PartyCleanup completed for manager 0x{(nuint)handle:X}.");
            }
            else
            {
                _voiceSession?.CompleteManagerCleanup(handle, succeeded: false);
                EnqueueLog($"PartyCleanup returned error 0x{result:X8}.");
            }
        }
        catch (Exception exception)
        {
            LogInspectionFailureOnce(exception);
        }

        return result;
    }

    private uint PartyNetworkLeaveNetwork(nint network, nint asyncIdentifier)
    {
        var roomIdentity = ReadRoomIdentitySnapshotSafely();
        if (!Volatile.Read(ref _suspended))
        {
            try
            {
                // This detour runs before Party's original LeaveNetwork body. Queueing destruction here
                // gives the game's normal state-change pump time to return the local left/destroy events.
                _playerMuteController?.PrepareForNetworkLeave(network);
                _voiceSession?.PrepareForNetworkLeave(network);
            }
            catch (Exception exception)
            {
                LogInspectionFailureOnce(exception);
            }
        }

        var result = _leaveNetworkHook!.OriginalFunction(network, asyncIdentifier);
        if (result == 0)
        {
            InvalidateRoomIdentitySafely();
            if (!Volatile.Read(ref _suspended))
                _onlineRoom.MarkNetworkLeaveQueued(network, roomIdentity);
        }
        return result;
    }

    private uint PartyStartProcessingStateChanges(
        nint handle,
        nint stateChangeCountOutput,
        nint stateChangesOutput)
    {
        _onlineRoom.BeginStateChangeBatch(handle);
        _playerMuteController?.BeginStateChangeBatch(handle);
        _voiceSession?.BeginStateChangeBatch(handle);
        uint result;
        try
        {
            result = _startProcessingHook!.OriginalFunction(
                handle,
                stateChangeCountOutput,
                stateChangesOutput);
        }
        catch
        {
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            _playerMuteController?.CancelStateChangeBatch(handle);
            _voiceSession?.CancelStateChangeBatch(handle);
            _onlineRoom.CancelStateChangeBatch(handle);
            throw;
        }

        if (Volatile.Read(ref _suspended))
        {
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            _playerMuteController?.CancelStateChangeBatch(handle);
            _voiceSession?.CancelStateChangeBatch(handle);
            _onlineRoom.CancelStateChangeBatch(handle);
            return result;
        }

        if (result != 0)
        {
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            _playerMuteController?.CancelStateChangeBatch(handle);
            _voiceSession?.CancelStateChangeBatch(handle);
            _onlineRoom.CancelStateChangeBatch(handle);
            if (Interlocked.Exchange(ref _startFailureLogged, 1) == 0)
            {
                EnqueueLog(
                    $"PartyStartProcessingStateChanges returned error 0x{result:X8}; further errors are suppressed.");
            }
            return result;
        }

        try
        {
            if (CapturePartyHandle(handle, "PartyStartProcessingStateChanges"))
                Interlocked.Exchange(ref _audioWorkStartPendingManager, handle);
            InspectStateChanges(handle, stateChangeCountOutput, stateChangesOutput);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            LogInspectionFailureOnce(exception);
        }

        return result;
    }

    private uint PartyFinishProcessingStateChanges(
        nint handle,
        uint stateChangeCount,
        nint stateChanges)
    {
        uint result;
        try
        {
            result = _finishProcessingHook!.OriginalFunction(handle, stateChangeCount, stateChanges);
        }
        catch
        {
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            _playerMuteController?.CancelStateChangeBatch(handle);
            _voiceSession?.CancelStateChangeBatch(handle);
            _onlineRoom.CancelStateChangeBatch(handle);
            throw;
        }
        if (!Volatile.Read(ref _suspended) && result == 0)
        {
            try
            {
                _onlineRoom.OnBatchFinished(handle);
                _playerMuteController?.OnBatchFinished(handle);
                _voiceSession?.OnBatchFinished(handle);
                var pendingManager = Interlocked.Exchange(
                    ref _audioWorkStartPendingManager,
                    nint.Zero);
                if (pendingManager == handle)
                {
                    EnsureAudioWorkPump(handle, "PartyFinishProcessingStateChanges");
                }
                else if (pendingManager != nint.Zero)
                {
                    _voiceSession?.DisableFailClosed(
                        $"Party audio work start manager mismatch: pending 0x{(nuint)pendingManager:X}, " +
                        $"finished 0x{(nuint)handle:X}");
                    EnqueueLog(
                        $"Party audio work pump rejected stale manager 0x{(nuint)pendingManager:X} " +
                        $"after finishing state changes for 0x{(nuint)handle:X}.");
                }
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
                LogInspectionFailureOnce(exception);
            }
        }
        else
        {
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            if (result != 0)
                _playerMuteController?.Reset();
            else
                _playerMuteController?.CancelStateChangeBatch(handle);
            _voiceSession?.CancelStateChangeBatch(handle);
            _onlineRoom.CancelStateChangeBatch(handle);
        }
        if (!Volatile.Read(ref _suspended) &&
            result != 0 &&
            Interlocked.Exchange(ref _finishFailureLogged, 1) == 0)
        {
            _voiceSession?.DisableFailClosed(
                $"PartyFinishProcessingStateChanges returned 0x{result:X8}");
            EnqueueLog(
                $"PartyFinishProcessingStateChanges returned error 0x{result:X8}; further errors are suppressed.");
        }
        return result;
    }

    private void InspectStateChanges(
        nint handle,
        nint stateChangeCountOutput,
        nint stateChangesOutput)
    {
        if (stateChangeCountOutput == nint.Zero || stateChangesOutput == nint.Zero)
        {
            InvalidateRoomIdentitySafely();
            _onlineRoom.Reset();
            _playerMuteController?.Reset();
            _voiceSession?.DisableFailClosed(
                "Party returned null state-change output storage");
            return;
        }

        var count = unchecked((uint)Marshal.ReadInt32(stateChangeCountOutput));
        if (count == 0)
            return;
        if (count > MaximumStateChangesPerBatch)
        {
            InvalidateRoomIdentitySafely();
            _onlineRoom.Reset();
            _playerMuteController?.Reset();
            _voiceSession?.DisableFailClosed(
                $"Party state batch count {count} exceeded the safety limit");
            EnqueueLog($"Party state batch count {count} exceeds the probe safety limit; batch ignored.");
            return;
        }

        var stateChanges = Marshal.ReadIntPtr(stateChangesOutput);
        if (stateChanges == nint.Zero)
        {
            InvalidateRoomIdentitySafely();
            _onlineRoom.Reset();
            _playerMuteController?.Reset();
            _voiceSession?.DisableFailClosed(
                "Party returned a non-empty state batch with a null array");
            return;
        }

        for (var index = 0u; index < count; index++)
        {
            var stateChange = Marshal.ReadIntPtr(stateChanges, checked((int)(index * (uint)nint.Size)));
            if (stateChange == nint.Zero)
            {
                InvalidateRoomIdentitySafely();
                _onlineRoom.Reset();
                _playerMuteController?.Reset();
                _voiceSession?.DisableFailClosed(
                    $"Party state batch entry {index} was null");
                return;
            }

            var snapshot = PartyStateChangeReader.Read(stateChange);
            var roomWasActive = _onlineRoom.IsActive;
            var previousLocalNetworkRole = _onlineRoom.LocalNetworkRole;
            _onlineRoom.Observe(snapshot);
            var currentLocalNetworkRole = _onlineRoom.LocalNetworkRole;
            if (currentLocalNetworkRole != previousLocalNetworkRole)
            {
                EnqueueLog(
                    $"Party local network role changed: " +
                    $"{previousLocalNetworkRole} -> {currentLocalNetworkRole}.");
            }
            if (roomWasActive && !_onlineRoom.IsActive)
                InvalidateRoomIdentitySafely();
            if (_enableLifecycleLogging && PartyStateChangeCatalog.IsLifecycle(snapshot.Type))
            {
                EnqueueLog(
                    $"Party lifecycle state {PartyStateChangeCatalog.GetName(snapshot.Type)} ({snapshot.Type}).");
            }

            _playerMuteController?.Observe(snapshot);
            _voiceSession?.Observe(handle, snapshot);
            if (snapshot.Type == (uint)PartyStateChangeType.LocalUserKicked)
            {
                try
                {
                    _voiceSession?.DisableFailClosed("local Party user kicked");
                }
                catch (Exception exception)
                {
                    EnqueueLog(
                        $"Party voice kick cleanup failed closed: " +
                        $"{exception.GetType().Name}: {exception.Message}");
                }
            }
        }
    }

    private bool CapturePartyHandle(nint handle, string source)
    {
        if (handle == nint.Zero)
            return false;

        var previous = Interlocked.CompareExchange(ref _partyHandle, handle, nint.Zero);
        if (previous == nint.Zero)
        {
            EnqueueLog(
                $"Party manager captured from {source}: 0x{(nuint)handle:X}.");
            _voiceSession?.CaptureManager(handle, source);
            return true;
        }
        if (previous == handle)
        {
            _voiceSession?.CaptureManager(handle, source);
            return false;
        }

        EnqueueLog(
            $"Party manager ownership conflict at {source}: retained 0x{(nuint)previous:X}, " +
            $"rejected 0x{(nuint)handle:X}; the voice session will fail closed.");
        _onlineRoom.Reset();
        InvalidateRoomIdentitySafely();
        _playerMuteController?.Reset();
        _voiceSession?.CaptureManager(handle, source);
        return false;
    }

    private void EnsureAudioWorkPump(nint handle, string source)
    {
        if (!_enableVoice || handle == nint.Zero || Volatile.Read(ref _suspended))
            return;

        _audioWorkPump?.AttachManager(handle, source);
    }

    private void LogInspectionFailureOnce(Exception exception)
    {
        InvalidateRoomIdentitySafely();
        _onlineRoom.Reset();
        _playerMuteController?.Reset();
        _voiceSession?.DisableFailClosed(
            $"Party state inspection threw {exception.GetType().Name}: {exception.Message}");
        if (Interlocked.Exchange(ref _inspectionFailureLogged, 1) == 0)
        {
            EnqueueLog(
                $"Party lifecycle inspection failed; further inspection errors are suppressed: {exception.Message}");
        }
    }

    private void LogIdentitySnapshotFailureOnce(string operation, Exception exception)
    {
        if (Interlocked.Exchange(ref _identitySnapshotFailureLogged, 1) == 0)
        {
            EnqueueLog(
                $"Read-only {operation} identity lookup failed; further failures are suppressed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void EnqueueLog(string message)
    {
        Interlocked.Increment(ref _pendingLogCount);
        _pendingLogs.Enqueue(message);
        while (Volatile.Read(ref _pendingLogCount) > MaximumPendingLogs &&
               _pendingLogs.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingLogCount);
        }

        if (Interlocked.CompareExchange(ref _logDrainScheduled, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(static state => ((PartyLifecycleProbe)state!).DrainLogs(), this);
    }

    private void DrainLogs()
    {
        do
        {
            while (_pendingLogs.TryDequeue(out var message))
            {
                Interlocked.Decrement(ref _pendingLogCount);
                SafeLog(message);
            }

            Volatile.Write(ref _logDrainScheduled, 0);
        }
        while (!_pendingLogs.IsEmpty &&
               Interlocked.CompareExchange(ref _logDrainScheduled, 1, 0) == 0);
    }

    private void SafeLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Never allow a logger failure to escape the asynchronous probe drain.
        }
    }

    private PartyRoomIdentitySnapshot ReadRoomIdentitySnapshotSafely()
    {
        try
        {
            return _roomIdentityReader?.Invoke() ?? default;
        }
        catch
        {
            return default;
        }
    }

    private int GetEstablishedVoiceParticipantCount()
    {
        try
        {
            return EstablishedVoiceParticipantCount;
        }
        catch
        {
            return 0;
        }
    }

    private void InvalidateRoomIdentitySafely()
    {
        try
        {
            _invalidateRoomIdentity();
        }
        catch (Exception exception)
        {
            EnqueueLog(
                $"Room identity invalidation failed closed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void RunDisposeStep(string component, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            SafeLog(
                $"Party lifecycle disposal of {component} failed; continuing teardown: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void DisableHooks()
    {
        _finishProcessingHook?.Disable();
        _startProcessingHook?.Disable();
        _leaveNetworkHook?.Disable();
        _cleanupHook?.Disable();
        _initializeHook?.Disable();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyInitializeDelegate(nint titleId, nint handleOutput);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyCleanupDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyNetworkLeaveNetworkDelegate(nint network, nint asyncIdentifier);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyStartProcessingStateChangesDelegate(
        nint handle,
        nint stateChangeCountOutput,
        nint stateChangesOutput);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyFinishProcessingStateChangesDelegate(
        nint handle,
        uint stateChangeCount,
        nint stateChanges);
}
