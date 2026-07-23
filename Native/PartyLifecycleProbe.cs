using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private readonly object _lifecycleSync = new();
    private readonly ConcurrentQueue<string> _pendingLogs = new();

    private IHook<PartyInitializeDelegate>? _initializeHook;
    private IHook<PartyCleanupDelegate>? _cleanupHook;
    private IHook<PartyStartProcessingStateChangesDelegate>? _startProcessingHook;
    private IHook<PartyFinishProcessingStateChangesDelegate>? _finishProcessingHook;
    private nint _partyHandle;
    private bool _initialized;
    private bool _suspended;
    private int _pendingLogCount;
    private int _logDrainScheduled;
    private int _inspectionFailureLogged;
    private int _startFailureLogged;
    private int _finishFailureLogged;

    public PartyLifecycleProbe(ReloadedHooksApi hooks, Action<string> log)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsInitialized => Volatile.Read(ref _initialized);

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
                _initializeHook = _hooks.CreateHook<PartyInitializeDelegate>(
                    PartyInitialize,
                    NativeLibrary.GetExport(module, "PartyInitialize"));
                _initializeHook.Activate();

                _cleanupHook = _hooks.CreateHook<PartyCleanupDelegate>(
                    PartyCleanup,
                    NativeLibrary.GetExport(module, "PartyCleanup"));
                _cleanupHook.Activate();

                _startProcessingHook = _hooks.CreateHook<PartyStartProcessingStateChangesDelegate>(
                    PartyStartProcessingStateChanges,
                    NativeLibrary.GetExport(module, "PartyStartProcessingStateChanges"));
                _startProcessingHook.Activate();

                _finishProcessingHook = _hooks.CreateHook<PartyFinishProcessingStateChangesDelegate>(
                    PartyFinishProcessingStateChanges,
                    NativeLibrary.GetExport(module, "PartyFinishProcessingStateChanges"));
                _finishProcessingHook.Activate();

                Volatile.Write(ref _initialized, true);
                _log(
                    $"Party lifecycle probe attached at 0x{(nuint)module:X}; observation only, no Party calls or sends.");
            }
            catch
            {
                DisableHooks();
                ClearHooks();
                throw;
            }
        }
    }

    public void Suspend()
    {
        lock (_lifecycleSync)
        {
            Volatile.Write(ref _suspended, true);
            DisableHooks();
        }
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
                _startProcessingHook?.Enable();
                _finishProcessingHook?.Enable();
            }
            catch
            {
                Volatile.Write(ref _suspended, true);
                DisableHooks();
                throw;
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
                CapturePartyHandle(Marshal.ReadIntPtr(handleOutput), "PartyInitialize");
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
        var result = _cleanupHook!.OriginalFunction(handle);
        if (Volatile.Read(ref _suspended))
            return result;

        try
        {
            if (result == 0)
            {
                Interlocked.CompareExchange(ref _partyHandle, nint.Zero, handle);
                EnqueueLog($"PartyCleanup completed for manager 0x{(nuint)handle:X}.");
            }
            else
            {
                EnqueueLog($"PartyCleanup returned error 0x{result:X8}.");
            }
        }
        catch (Exception exception)
        {
            LogInspectionFailureOnce(exception);
        }

        return result;
    }

    private uint PartyStartProcessingStateChanges(
        nint handle,
        nint stateChangeCountOutput,
        nint stateChangesOutput)
    {
        var result = _startProcessingHook!.OriginalFunction(
            handle,
            stateChangeCountOutput,
            stateChangesOutput);

        if (Volatile.Read(ref _suspended))
            return result;

        if (result != 0)
        {
            if (Interlocked.Exchange(ref _startFailureLogged, 1) == 0)
            {
                EnqueueLog(
                    $"PartyStartProcessingStateChanges returned error 0x{result:X8}; further errors are suppressed.");
            }
            return result;
        }

        try
        {
            CapturePartyHandle(handle, "PartyStartProcessingStateChanges");
            InspectStateChanges(stateChangeCountOutput, stateChangesOutput);
        }
        catch (Exception exception)
        {
            LogInspectionFailureOnce(exception);
        }

        return result;
    }

    private uint PartyFinishProcessingStateChanges(
        nint handle,
        uint stateChangeCount,
        nint stateChanges)
    {
        var result = _finishProcessingHook!.OriginalFunction(handle, stateChangeCount, stateChanges);
        if (!Volatile.Read(ref _suspended) &&
            result != 0 &&
            Interlocked.Exchange(ref _finishFailureLogged, 1) == 0)
        {
            EnqueueLog(
                $"PartyFinishProcessingStateChanges returned error 0x{result:X8}; further errors are suppressed.");
        }
        return result;
    }

    private void InspectStateChanges(nint stateChangeCountOutput, nint stateChangesOutput)
    {
        if (stateChangeCountOutput == nint.Zero || stateChangesOutput == nint.Zero)
            return;

        var count = unchecked((uint)Marshal.ReadInt32(stateChangeCountOutput));
        if (count == 0)
            return;
        if (count > MaximumStateChangesPerBatch)
        {
            EnqueueLog($"Party state batch count {count} exceeds the probe safety limit; batch ignored.");
            return;
        }

        var stateChanges = Marshal.ReadIntPtr(stateChangesOutput);
        if (stateChanges == nint.Zero)
            return;

        for (var index = 0u; index < count; index++)
        {
            var stateChange = Marshal.ReadIntPtr(stateChanges, checked((int)(index * (uint)nint.Size)));
            if (stateChange == nint.Zero)
                continue;

            var stateType = unchecked((uint)Marshal.ReadInt32(stateChange));
            if (PartyStateChangeCatalog.IsLifecycle(stateType))
            {
                EnqueueLog(
                    $"Party lifecycle state {PartyStateChangeCatalog.GetName(stateType)} ({stateType}).");
            }
        }
    }

    private void CapturePartyHandle(nint handle, string source)
    {
        if (handle == nint.Zero)
            return;

        var previous = Interlocked.Exchange(ref _partyHandle, handle);
        if (previous != handle)
        {
            EnqueueLog(
                $"Party manager captured from {source}: 0x{(nuint)handle:X}" +
                (previous == nint.Zero ? "." : $" (replaced 0x{(nuint)previous:X})."));
        }
    }

    private void LogInspectionFailureOnce(Exception exception)
    {
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
        _cleanupHook?.Disable();
        _initializeHook?.Disable();
    }

    private void ClearHooks()
    {
        _finishProcessingHook = null;
        _startProcessingHook = null;
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
