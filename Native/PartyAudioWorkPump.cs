using System.Diagnostics;

namespace GBFR.ChatOverlay.Native;

/// <summary>
/// Supplies Party's real-time audio processing task only when the host title
/// configured that task for manual work. Automatic mode remains entirely owned
/// by Party. The pump never changes the process-global work mode.
/// </summary>
internal sealed class PartyAudioWorkPump : IDisposable
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(40);

    private readonly IPartyAudioWorkApi _api;
    private readonly Action<string> _log;
    private readonly Action<string> _failClosed;
    private readonly TimeSpan _interval;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Thread? _thread;
    private nint _manager;
    private bool _disposed;
    private int _workFailureSignaled;

    internal PartyAudioWorkPump(
        IPartyAudioWorkApi api,
        Action<string> log,
        Action<string> failClosed,
        TimeSpan? interval = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _failClosed = failClosed ?? throw new ArgumentNullException(nameof(failClosed));
        _interval = interval ?? DefaultInterval;
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
    }

    internal void AttachManager(nint manager, string source)
    {
        if (manager == nint.Zero)
            throw new ArgumentException("The Party manager handle is null.", nameof(manager));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_manager == manager)
                return;
            if (_manager != nint.Zero)
            {
                throw new InvalidOperationException(
                    $"Party audio work pump already owns manager 0x{(nuint)_manager:X}; " +
                    $"refusing manager 0x{(nuint)manager:X} from {source}.");
            }

            _manager = manager;
            Interlocked.Exchange(ref _workFailureSignaled, 0);
            var audioResult = _api.GetWorkMode(PartyThreadId.Audio, out var audioMode);
            var networkingResult = _api.GetWorkMode(PartyThreadId.Networking, out var networkingMode);
            if (audioResult != 0)
            {
                SignalFailure(
                    $"PartyGetWorkMode(Audio) returned 0x{audioResult:X8}; " +
                    "the Mod cannot safely determine who owns Party audio processing, so voice remains " +
                    "fail-closed until this Party manager is cleaned up.");
                return;
            }

            var networkingText = networkingResult == 0
                ? $"{networkingMode} ({(uint)networkingMode})"
                : $"query-error 0x{networkingResult:X8}";
            _log(
                $"Party work modes captured from {source}: Audio={audioMode} ({(uint)audioMode}), " +
                $"Networking={networkingText}.");

            if (audioMode == PartyWorkMode.Automatic)
            {
                _log(
                    "Party audio processing is Automatic; Party's internal real-time audio thread remains the sole owner.");
                return;
            }

            if (audioMode != PartyWorkMode.Manual)
            {
                SignalFailure(
                    $"Party returned unknown Audio work mode {(uint)audioMode}; voice remains fail-closed " +
                    "until this Party manager is cleaned up.");
                return;
            }

            var cancellation = new CancellationTokenSource();
            var thread = new Thread(() => Run(manager, cancellation.Token))
            {
                IsBackground = true,
                Name = "GBFR Party Audio Work",
                Priority = ThreadPriority.AboveNormal,
            };
            _cancellation = cancellation;
            _thread = thread;
            thread.Start();
            _log(
                $"Party Audio work mode is Manual; started the Mod-owned PartyDoWork(Audio) pump at " +
                $"{_interval.TotalMilliseconds:0.###} ms intervals. The global work mode was not changed.");
        }
    }

    internal void DetachManager(nint manager, string reason)
    {
        CancellationTokenSource? cancellation;
        Thread? thread;
        nint ownedManager;
        lock (_sync)
        {
            ownedManager = _manager;
            if (ownedManager == nint.Zero)
                return;
            if (manager != nint.Zero && manager != ownedManager)
            {
                _log(
                    $"Party audio work pump ignored detach for manager 0x{(nuint)manager:X}; " +
                    $"owned manager is 0x{(nuint)ownedManager:X}.");
                return;
            }

            _manager = nint.Zero;
            cancellation = _cancellation;
            thread = _thread;
            _cancellation = null;
            _thread = null;
            cancellation?.Cancel();
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                _log(
                    "PartyDoWork(Audio) did not return within two seconds; waiting before Party cleanup " +
                    "to prevent a concurrent native cleanup call.");
                thread.Join();
            }
        }

        cancellation?.Dispose();
        if (thread is not null)
        {
            _log(
                $"PartyDoWork(Audio) pump stopped for manager 0x{(nuint)ownedManager:X} before {reason}.");
        }
    }

    private void Run(nint manager, CancellationToken cancellationToken)
    {
        var intervalTicks = Math.Max(
            1L,
            (long)Math.Round(_interval.TotalSeconds * Stopwatch.Frequency));
        var nextDue = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
        {
            uint result;
            try
            {
                result = _api.DoWork(manager, PartyThreadId.Audio);
            }
            catch (Exception exception)
            {
                SignalFailure(
                    $"PartyDoWork(Audio) threw {exception.GetType().Name}: {exception.Message}");
                return;
            }

            if (result != 0)
            {
                SignalFailure(
                    $"PartyDoWork(Audio) returned 0x{result:X8}; the manual Party audio task stopped and " +
                    "voice remains fail-closed until this Party manager is cleaned up.");
                return;
            }

            nextDue += intervalTicks;
            var now = Stopwatch.GetTimestamp();
            if (nextDue <= now)
            {
                // Never issue a burst of catch-up calls after a delayed frame.
                nextDue = now + intervalTicks;
            }

            var remaining = nextDue - now;
            var delay = TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency);
            if (cancellationToken.WaitHandle.WaitOne(delay))
                return;
        }
    }

    private void SignalFailure(string message)
    {
        if (Interlocked.Exchange(ref _workFailureSignaled, 1) != 0)
            return;

        _log(message);
        _failClosed(message);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        DetachManager(nint.Zero, "Mod disposal");
    }
}
