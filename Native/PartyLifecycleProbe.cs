using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GBFR.ChatOverlay.Audio;
using Reloaded.Hooks.Definitions;
using ReloadedHooksApi = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace GBFR.ChatOverlay.Native;

public sealed class PartyLifecycleProbe
{
    public const string SupportedPartySha256 =
        "3f0c6abbb735d81fa766a105982bda73f1d2c2cf01109fa2e7cf64813a52ce55";

    private const string PartyModuleName = "PartyWin.dll";
    private const int MaximumStateChangesPerBatch = 4_096;
    private const int MaximumPendingLogs = 512;

    private readonly ReloadedHooksApi _hooks;
    private readonly Action<string> _log;
    private readonly bool _enableLifecycleLogging;
    private readonly bool _enableMutedChatControlCanary;
    private readonly bool _enableVoiceTest;
    private readonly ResolvedAudioEndpointSelection _audioInputSelection;
    private readonly ResolvedAudioEndpointSelection _audioOutputSelection;
    private readonly object _lifecycleSync = new();
    private readonly ConcurrentQueue<string> _pendingLogs = new();

    private IHook<PartyInitializeDelegate>? _initializeHook;
    private IHook<PartyCleanupDelegate>? _cleanupHook;
    private IHook<PartyNetworkLeaveNetworkDelegate>? _leaveNetworkHook;
    private IHook<PartyStartProcessingStateChangesDelegate>? _startProcessingHook;
    private IHook<PartyFinishProcessingStateChangesDelegate>? _finishProcessingHook;
    private PartyChatControlCanary? _chatControlCanary;
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
    private nint _audioWorkStartPendingManager;

    public PartyLifecycleProbe(
        ReloadedHooksApi hooks,
        Action<string> log,
        bool enableLifecycleLogging = true,
        bool enableMutedChatControlCanary = false,
        bool enableVoiceTest = false,
        ResolvedAudioEndpointSelection? audioInputSelection = null,
        ResolvedAudioEndpointSelection? audioOutputSelection = null)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _enableLifecycleLogging = enableLifecycleLogging;
        _enableMutedChatControlCanary = enableMutedChatControlCanary;
        _enableVoiceTest = enableVoiceTest;
        _audioInputSelection = audioInputSelection ??
            ResolvedAudioEndpointSelection.SystemDefault();
        _audioOutputSelection = audioOutputSelection ??
            ResolvedAudioEndpointSelection.SystemDefault();
    }

    public bool IsInitialized => Volatile.Read(ref _initialized);

    public bool IsVoiceTestAvailable => IsInitialized && _enableVoiceTest && _chatControlCanary is not null;

    public bool IsVoicePushToTalkReady =>
        IsVoiceTestAvailable &&
        !Volatile.Read(ref _suspended) &&
        _chatControlCanary?.IsRemotePushToTalkReady == true;

    internal PartyVoiceUiStatus VoiceUiStatus
    {
        get
        {
            if (!_enableVoiceTest)
                return PartyVoiceUiStatus.Disabled;
            if (!IsInitialized || Volatile.Read(ref _suspended))
                return PartyVoiceUiStatus.Unavailable;

            return _chatControlCanary?.VoiceUiStatus ?? PartyVoiceUiStatus.Unavailable;
        }
    }

    public void Initialize()
    {
        lock (_lifecycleSync)
        {
            if (_initialized)
                return;

            using var process = Process.GetCurrentProcess();
            var mainModule = process.MainModule ??
                throw new InvalidOperationException("The game module is unavailable.");
            ValidateFileHash(mainModule.FileName, RelinkBuildLocator.SupportedSha256, "Relink executable");

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

            ValidateFileHash(partyModule.FileName, SupportedPartySha256, PartyModuleName);
            var module = partyModule.BaseAddress;

            try
            {
                if (_enableMutedChatControlCanary)
                {
                    try
                    {
                        var partyApi = new PartyNativeApi(module);
                        _chatControlCanary = new PartyChatControlCanary(
                            partyApi,
                            EnqueueLog,
                            enableVoiceTest: _enableVoiceTest,
                            audioInputSelection: _audioInputSelection,
                            audioOutputSelection: _audioOutputSelection);
                        if (_enableVoiceTest)
                        {
                            _audioWorkPump = new PartyAudioWorkPump(
                                partyApi,
                                EnqueueLog,
                                reason => _chatControlCanary?.DisableFailClosed(reason));
                        }
                    }
                    catch (Exception exception)
                    {
                        _chatControlCanary = null;
                        EnqueueLog(
                            $"Stage 2 muted ChatControl canary unavailable; lifecycle observation remains active: " +
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

                if (_chatControlCanary is not null)
                {
                    _leaveNetworkHook = _hooks.CreateHook<PartyNetworkLeaveNetworkDelegate>(
                        PartyNetworkLeaveNetwork,
                        NativeLibrary.GetExport(module, "PartyNetworkLeaveNetwork"));
                    _leaveNetworkHook.Activate();
                }

                _startProcessingHook = _hooks.CreateHook<PartyStartProcessingStateChangesDelegate>(
                    PartyStartProcessingStateChanges,
                    NativeLibrary.GetExport(module, "PartyStartProcessingStateChanges"));
                _startProcessingHook.Activate();

                _finishProcessingHook = _hooks.CreateHook<PartyFinishProcessingStateChangesDelegate>(
                    PartyFinishProcessingStateChanges,
                    NativeLibrary.GetExport(module, "PartyFinishProcessingStateChanges"));
                _finishProcessingHook.Activate();

                Volatile.Write(ref _initialized, true);
                _log(_chatControlCanary is null
                    ? $"Party lifecycle probe attached at 0x{(nuint)module:X}; observation only, no Party calls or sends."
                    : _enableVoiceTest
                        ? $"Party lifecycle/Stage 3 voice test attached at 0x{(nuint)module:X}; " +
                          "one ChatControl may join the existing PartyNetwork. U unmutes Party's native selected " +
                          "microphone path directly; no audio-manipulation capture stream is configured, and input " +
                          "stays muted unless U is held."
                        : $"Party lifecycle/Stage 2 canary attached at 0x{(nuint)module:X}; " +
                          "one muted ChatControl may join the existing PartyNetwork, with no chat permissions granted.");
            }
            catch
            {
                DisableHooks();
                ClearHooks();
                _audioWorkPump?.Dispose();
                _audioWorkPump = null;
                _chatControlCanary?.Dispose();
                _chatControlCanary = null;
                throw;
            }
        }
    }

    public void Suspend()
    {
        lock (_lifecycleSync)
        {
            Volatile.Write(ref _suspended, true);
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            DisableHooks();
        }
        _audioWorkPump?.DetachManager(nint.Zero, "Mod suspension");
        _chatControlCanary?.SuspendBestEffort();
    }

    public void Resume()
    {
        lock (_lifecycleSync)
        {
            Volatile.Write(ref _suspended, false);
            try
            {
                _initializeHook?.Enable();
                _cleanupHook?.Enable();
                _leaveNetworkHook?.Enable();
                _startProcessingHook?.Enable();
                _finishProcessingHook?.Enable();
                _chatControlCanary?.ResumeFailClosed();
            }
            catch
            {
                Volatile.Write(ref _suspended, true);
                DisableHooks();
                _audioWorkPump?.DetachManager(nint.Zero, "failed Mod resume");
                _chatControlCanary?.SuspendBestEffort();
                throw;
            }
        }
    }

    public void SetPushToTalkPressed(bool pressed)
    {
        try
        {
            _chatControlCanary?.SetPushToTalkPressed(pressed);
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
            _chatControlCanary?.RequestVoiceDiagnosticSample();
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _diagnosticRequestFailureLogged, 1) == 0)
            {
                EnqueueLog(
                    $"Stage 3 voice diagnostic request failed without changing voice state; " +
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
        Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
        _audioWorkPump?.DetachManager(
            nint.Zero,
            $"PartyCleanup for manager 0x{(nuint)handle:X}");
        _chatControlCanary?.BeginManagerCleanup(handle);
        var result = _cleanupHook!.OriginalFunction(handle);
        if (Volatile.Read(ref _suspended))
        {
            if (result == 0)
                Interlocked.CompareExchange(ref _partyHandle, nint.Zero, handle);
            _chatControlCanary?.CompleteManagerCleanup(handle, succeeded: result == 0);
            return result;
        }

        try
        {
            if (result == 0)
            {
                Interlocked.CompareExchange(ref _partyHandle, nint.Zero, handle);
                _chatControlCanary?.CompleteManagerCleanup(handle, succeeded: true);
                EnqueueLog($"PartyCleanup completed for manager 0x{(nuint)handle:X}.");
            }
            else
            {
                _chatControlCanary?.CompleteManagerCleanup(handle, succeeded: false);
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
        if (!Volatile.Read(ref _suspended))
        {
            try
            {
                // This detour runs before Party's original LeaveNetwork body. Queueing destruction here
                // gives the game's normal state-change pump time to return the local left/destroy events.
                _chatControlCanary?.PrepareForNetworkLeave(network);
            }
            catch (Exception exception)
            {
                LogInspectionFailureOnce(exception);
            }
        }

        return _leaveNetworkHook!.OriginalFunction(network, asyncIdentifier);
    }

    private uint PartyStartProcessingStateChanges(
        nint handle,
        nint stateChangeCountOutput,
        nint stateChangesOutput)
    {
        _chatControlCanary?.BeginStateChangeBatch(handle);
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
            _chatControlCanary?.CancelStateChangeBatch(handle);
            throw;
        }

        if (Volatile.Read(ref _suspended))
        {
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            _chatControlCanary?.CancelStateChangeBatch(handle);
            return result;
        }

        if (result != 0)
        {
            Interlocked.Exchange(ref _audioWorkStartPendingManager, nint.Zero);
            _chatControlCanary?.CancelStateChangeBatch(handle);
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
            _chatControlCanary?.CancelStateChangeBatch(handle);
            throw;
        }
        if (!Volatile.Read(ref _suspended) && result == 0)
        {
            try
            {
                _chatControlCanary?.OnBatchFinished(handle);
                var pendingManager = Interlocked.Exchange(
                    ref _audioWorkStartPendingManager,
                    nint.Zero);
                if (pendingManager == handle)
                {
                    EnsureAudioWorkPump(handle, "PartyFinishProcessingStateChanges");
                }
                else if (pendingManager != nint.Zero)
                {
                    _chatControlCanary?.DisableFailClosed(
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
            _chatControlCanary?.CancelStateChangeBatch(handle);
        }
        if (!Volatile.Read(ref _suspended) &&
            result != 0 &&
            Interlocked.Exchange(ref _finishFailureLogged, 1) == 0)
        {
            _chatControlCanary?.DisableFailClosed(
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
            _chatControlCanary?.DisableFailClosed(
                "Party returned null state-change output storage");
            return;
        }

        var count = unchecked((uint)Marshal.ReadInt32(stateChangeCountOutput));
        if (count == 0)
            return;
        if (count > MaximumStateChangesPerBatch)
        {
            _chatControlCanary?.DisableFailClosed(
                $"Party state batch count {count} exceeded the safety limit");
            EnqueueLog($"Party state batch count {count} exceeds the probe safety limit; batch ignored.");
            return;
        }

        var stateChanges = Marshal.ReadIntPtr(stateChangesOutput);
        if (stateChanges == nint.Zero)
        {
            _chatControlCanary?.DisableFailClosed(
                "Party returned a non-empty state batch with a null array");
            return;
        }

        for (var index = 0u; index < count; index++)
        {
            var stateChange = Marshal.ReadIntPtr(stateChanges, checked((int)(index * (uint)nint.Size)));
            if (stateChange == nint.Zero)
            {
                _chatControlCanary?.DisableFailClosed(
                    $"Party state batch entry {index} was null");
                continue;
            }

            var snapshot = PartyStateChangeReader.Read(stateChange);
            if (_enableLifecycleLogging && PartyStateChangeCatalog.IsLifecycle(snapshot.Type))
            {
                EnqueueLog(
                    $"Party lifecycle state {PartyStateChangeCatalog.GetName(snapshot.Type)} ({snapshot.Type}).");
            }

            _chatControlCanary?.Observe(handle, snapshot);
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
            _chatControlCanary?.CaptureManager(handle, source);
            return true;
        }
        if (previous == handle)
        {
            _chatControlCanary?.CaptureManager(handle, source);
            return false;
        }

        EnqueueLog(
            $"Party manager ownership conflict at {source}: retained 0x{(nuint)previous:X}, " +
            $"rejected 0x{(nuint)handle:X}; Stage 2 will fail closed.");
        _chatControlCanary?.CaptureManager(handle, source);
        return false;
    }

    private void EnsureAudioWorkPump(nint handle, string source)
    {
        if (!_enableVoiceTest || handle == nint.Zero || Volatile.Read(ref _suspended))
            return;

        _audioWorkPump?.AttachManager(handle, source);
    }

    private void LogInspectionFailureOnce(Exception exception)
    {
        _chatControlCanary?.DisableFailClosed(
            $"Party state inspection threw {exception.GetType().Name}: {exception.Message}");
        if (Interlocked.Exchange(ref _inspectionFailureLogged, 1) == 0)
        {
            EnqueueLog(
                $"Party lifecycle inspection failed; further inspection errors are suppressed: {exception.Message}");
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

    private void DisableHooks()
    {
        _finishProcessingHook?.Disable();
        _startProcessingHook?.Disable();
        _leaveNetworkHook?.Disable();
        _cleanupHook?.Disable();
        _initializeHook?.Disable();
    }

    private void ClearHooks()
    {
        _finishProcessingHook = null;
        _startProcessingHook = null;
        _leaveNetworkHook = null;
        _cleanupHook = null;
        _initializeHook = null;
    }

    private static void ValidateFileHash(string path, string expectedHash, string label)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Unsupported {label} SHA-256 {actualHash}; expected {expectedHash}.");
        }
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
