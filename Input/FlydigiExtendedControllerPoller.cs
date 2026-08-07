using HidSharp;

namespace GBFR.ChatOverlay.Input;

internal readonly record struct FlydigiExtendedControllerSnapshot(
    bool ApiAvailable,
    bool IsConnected,
    bool AccessBlocked,
    bool TakeoverStatusKnown,
    bool TakeoverAllowed,
    bool AcquisitionStatusKnown,
    bool AcquisitionSucceeded,
    ExtendedControllerButtons Buttons,
    ulong Sequence)
{
    internal bool IsReady =>
        IsConnected &&
        TakeoverStatusKnown &&
        TakeoverAllowed &&
        AcquisitionStatusKnown &&
        AcquisitionSucceeded;
}

internal sealed class FlydigiExtendedControllerPoller : IDisposable
{
    private const int VendorId = 0x37D7;
    private const int Vader5ProductId = 0x2401;
    private const byte Magic1 = 0x5A;
    private const byte Magic2 = 0xA5;
    private const byte GetInfoCommand = 0x01;
    private const byte GetStatusCommand = 0x10;
    private const byte AcquireControllerCommand = 0x1C;
    private const byte InputReportCommand = 0xEF;
    private const long HeartbeatMilliseconds = 30_000;
    private const long AcquisitionRetryMilliseconds = 2_000;
    private const int ReconnectDelayMilliseconds = 1_000;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _sync = new();
    private Task? _worker;
    private bool _apiAvailable = OperatingSystem.IsWindows();
    private bool _connected;
    private bool _accessBlocked;
    private bool _takeoverStatusKnown;
    private bool _takeoverAllowed;
    private bool _acquisitionStatusKnown;
    private bool _acquisitionSucceeded;
    private ExtendedControllerButtons _buttons;
    private ulong _sequence;
    private int _disposed;

    internal FlydigiExtendedControllerPoller()
    {
    }

    internal FlydigiExtendedControllerSnapshot Poll()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return default;
        EnsureWorkerStarted();
        lock (_sync)
        {
            return new FlydigiExtendedControllerSnapshot(
                _apiAvailable,
                _connected,
                _accessBlocked,
                _takeoverStatusKnown,
                _takeoverAllowed,
                _acquisitionStatusKnown,
                _acquisitionSucceeded,
                _buttons,
                _sequence);
        }
    }

    private void EnsureWorkerStarted()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) == 0)
                _worker ??= Task.Run(Run);
        }
    }

    private void Run()
    {
        if (!OperatingSystem.IsWindows())
            return;

        while (!_cancellation.IsCancellationRequested)
        {
            HidDevice? device = null;
            var opened = false;
            try
            {
                device = DeviceList.Local
                    .GetHidDevices(VendorId, Vader5ProductId)
                    .FirstOrDefault(IsProtocolInterface);
                if (device is null)
                {
                    UpdateState(
                        apiAvailable: true,
                        connected: false,
                        accessBlocked: false,
                        takeoverStatusKnown: false,
                        takeoverAllowed: false,
                        acquisitionStatusKnown: false,
                        acquisitionSucceeded: false,
                        ExtendedControllerButtons.None);
                    WaitForReconnect();
                    continue;
                }
                if (!device.TryOpen(out var stream))
                {
                    UpdateState(
                        apiAvailable: true,
                        connected: false,
                        accessBlocked: true,
                        takeoverStatusKnown: false,
                        takeoverAllowed: false,
                        acquisitionStatusKnown: false,
                        acquisitionSucceeded: false,
                        ExtendedControllerButtons.None);
                    WaitForReconnect();
                    continue;
                }
                opened = true;

                using (stream)
                    ReadDevice(device, stream);
            }
            catch
            {
                UpdateState(
                    apiAvailable: true,
                    connected: false,
                    accessBlocked: device is not null && !opened,
                    takeoverStatusKnown: false,
                    takeoverAllowed: false,
                    acquisitionStatusKnown: false,
                    acquisitionSucceeded: false,
                    ExtendedControllerButtons.None);
                WaitForReconnect();
            }
        }
    }

    private void ReadDevice(HidDevice device, HidStream stream)
    {
        stream.ReadTimeout = 250;
        UpdateState(
            apiAvailable: true,
            connected: true,
            accessBlocked: false,
            takeoverStatusKnown: false,
            takeoverAllowed: false,
            acquisitionStatusKnown: false,
            acquisitionSucceeded: false,
            ExtendedControllerButtons.None);
        SendDiscovery(stream);
        var nextHeartbeat = Environment.TickCount64 + HeartbeatMilliseconds;
        var acquireRequested = false;
        var buffer = new byte[device.GetMaxInputReportLength()];

        while (!_cancellation.IsCancellationRequested)
        {
            if (Environment.TickCount64 >= nextHeartbeat)
            {
                if (IsTakeoverAllowed())
                {
                    SendAcquireHeartbeat(stream);
                    acquireRequested = true;
                }
                else
                {
                    SendDiscovery(stream);
                    acquireRequested = false;
                }
                nextHeartbeat = Environment.TickCount64 + HeartbeatMilliseconds;
            }

            try
            {
                var count = stream.Read(buffer, 0, buffer.Length);
                if (count <= 0)
                    continue;
                var command = HandleReport(buffer.AsSpan(0, count));
                if (command == AcquireControllerCommand && IsAcquisitionRejected())
                    nextHeartbeat = Environment.TickCount64 + AcquisitionRetryMilliseconds;
                if (!IsTakeoverAllowed())
                {
                    acquireRequested = false;
                }
                else if (!acquireRequested)
                {
                    SendAcquireHeartbeat(stream);
                    acquireRequested = true;
                    nextHeartbeat = Environment.TickCount64 + HeartbeatMilliseconds;
                }
            }
            catch (TimeoutException)
            {
            }
        }
    }

    private byte HandleReport(ReadOnlySpan<byte> report)
    {
        if (!TryNormalizeReport(report, out var payload))
            return 0;

        switch (payload[2])
        {
            case GetStatusCommand when TryParseTakeoverStatus(payload, out var takeoverAllowed):
                UpdateState(
                    apiAvailable: true,
                    connected: true,
                    accessBlocked: false,
                    takeoverStatusKnown: true,
                    takeoverAllowed,
                    acquisitionStatusKnown: takeoverAllowed && _acquisitionStatusKnown,
                    acquisitionSucceeded: takeoverAllowed && _acquisitionSucceeded,
                    takeoverAllowed && _acquisitionSucceeded
                        ? _buttons
                        : ExtendedControllerButtons.None);
                break;
            case AcquireControllerCommand when TryParseAcquisitionStatus(payload, out var acquisitionSucceeded):
                UpdateState(
                    apiAvailable: true,
                    connected: true,
                    accessBlocked: false,
                    _takeoverStatusKnown,
                    _takeoverAllowed,
                    acquisitionStatusKnown: true,
                    acquisitionSucceeded,
                    _takeoverAllowed && acquisitionSucceeded
                        ? _buttons
                        : ExtendedControllerButtons.None);
                break;
            case InputReportCommand when payload.Length > 14:
                if (!ShouldAcceptInputReport(
                        _takeoverStatusKnown,
                        _takeoverAllowed,
                        _acquisitionStatusKnown,
                        _acquisitionSucceeded))
                {
                    UpdateState(
                        apiAvailable: true,
                        connected: true,
                        accessBlocked: false,
                        _takeoverStatusKnown,
                        _takeoverAllowed,
                        _acquisitionStatusKnown,
                        _acquisitionSucceeded,
                        ExtendedControllerButtons.None);
                    break;
                }
                UpdateState(
                    apiAvailable: true,
                    connected: true,
                    accessBlocked: false,
                    takeoverStatusKnown: true,
                    takeoverAllowed: true,
                    acquisitionStatusKnown: true,
                    acquisitionSucceeded: true,
                    ParseButtons(payload));
                break;
        }
        return payload[2];
    }

    internal static ExtendedControllerButtons ParseButtons(ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= 14)
            return ExtendedControllerButtons.None;

        var buttons = (ExtendedControllerButtons)payload[13];
        if ((payload[14] & 0x01) != 0)
            buttons |= ExtendedControllerButtons.Circle;
        return buttons;
    }

    internal static bool TryParseTakeoverStatus(
        ReadOnlySpan<byte> payload,
        out bool takeoverAllowed)
    {
        takeoverAllowed = false;
        if (payload.Length <= 9 || payload[2] != GetStatusCommand)
            return false;
        takeoverAllowed = payload[9] == 1;
        return true;
    }

    internal static bool TryParseAcquisitionStatus(
        ReadOnlySpan<byte> payload,
        out bool acquisitionSucceeded)
    {
        acquisitionSucceeded = false;
        if (payload.Length <= 6 || payload[2] != AcquireControllerCommand)
            return false;
        acquisitionSucceeded = payload[5] == 1 || payload[6] != 0;
        return true;
    }

    internal static bool ShouldAcceptInputReport(
        bool takeoverStatusKnown,
        bool takeoverAllowed,
        bool acquisitionStatusKnown,
        bool acquisitionSucceeded) =>
        takeoverStatusKnown &&
        takeoverAllowed &&
        acquisitionStatusKnown &&
        acquisitionSucceeded;

    internal static bool TryNormalizeReport(
        ReadOnlySpan<byte> report,
        out ReadOnlySpan<byte> payload)
    {
        payload = report;
        if (payload.Length > 0 && payload[0] != Magic1)
            payload = payload[1..];
        return payload.Length >= 3 && payload[0] == Magic1 && payload[1] == Magic2;
    }

    private static bool IsProtocolInterface(HidDevice device) =>
        device.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase) &&
        device.GetMaxInputReportLength() >= 33 &&
        device.GetMaxOutputReportLength() >= 33;

    private bool IsTakeoverAllowed()
    {
        lock (_sync)
            return _takeoverStatusKnown && _takeoverAllowed;
    }

    private bool IsAcquisitionRejected()
    {
        lock (_sync)
            return _acquisitionStatusKnown && !_acquisitionSucceeded;
    }

    private static void SendDiscovery(HidStream stream)
    {
        WriteCommand(stream, GetInfoCommand, 2, 0);
        WriteCommand(stream, GetStatusCommand);
    }

    private static void SendAcquireHeartbeat(HidStream stream)
    {
        WriteCommand(stream, GetStatusCommand);
        WriteCommand(stream, AcquireControllerCommand, 23, 1, (byte)'S', (byte)'D', (byte)'L');
        WriteCommand(stream, GetInfoCommand, 2, 0);
    }

    private static void WriteCommand(HidStream stream, byte command, params byte[] payload)
    {
        var report = new byte[33];
        report[1] = Magic1;
        report[2] = Magic2;
        report[3] = command;
        payload.CopyTo(report, 4);
        stream.Write(report);
    }

    private void UpdateState(
        bool apiAvailable,
        bool connected,
        bool accessBlocked,
        bool takeoverStatusKnown,
        bool takeoverAllowed,
        bool acquisitionStatusKnown,
        bool acquisitionSucceeded,
        ExtendedControllerButtons buttons)
    {
        lock (_sync)
        {
            if (_apiAvailable == apiAvailable &&
                _connected == connected &&
                _accessBlocked == accessBlocked &&
                _takeoverStatusKnown == takeoverStatusKnown &&
                _takeoverAllowed == takeoverAllowed &&
                _acquisitionStatusKnown == acquisitionStatusKnown &&
                _acquisitionSucceeded == acquisitionSucceeded &&
                _buttons == buttons)
            {
                return;
            }

            _apiAvailable = apiAvailable;
            _connected = connected;
            _accessBlocked = accessBlocked;
            _takeoverStatusKnown = takeoverStatusKnown;
            _takeoverAllowed = takeoverAllowed;
            _acquisitionStatusKnown = acquisitionStatusKnown;
            _acquisitionSucceeded = acquisitionSucceeded;
            _buttons = buttons;
            _sequence++;
        }
    }

    private void WaitForReconnect() =>
        _cancellation.Token.WaitHandle.WaitOne(ReconnectDelayMilliseconds);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cancellation.Cancel();
        Task? worker;
        lock (_sync)
            worker = _worker;
        var workerCompleted = worker is null;
        try
        {
            workerCompleted = worker?.Wait(1_000) ?? true;
        }
        catch
        {
        }
        if (workerCompleted)
            _cancellation.Dispose();
    }
}
