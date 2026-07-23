using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GBFR.ChatOverlay.Core;
using Reloaded.Hooks.Definitions;
using ReloadedHooksApi = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace GBFR.ChatOverlay.Native;

public sealed unsafe class RelinkChatBridge : IChatTransport, IIncomingChatSource
{
    private const int MaximumQueuedIncomingMessages = 512;

    private readonly ReloadedHooksApi _hooks;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<IncomingChatMessage> _incoming = new();
    private readonly RecentEchoSuppressor _echoSuppressor = new();
    private readonly object _lifecycleSync = new();

    private IHook<SendMessageDelegate>? _sendHook;
    private IHook<RpcMessageDelegate>? _rpcHook;
    private nint _managerSlot;
    private bool _initialized;
    private bool _suspended;
    private int _incomingCount;
    private int _decodeFailureLogged;

    public RelinkChatBridge(ReloadedHooksApi hooks, Action<string> log)
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
            var imagePath = mainModule.FileName;
            var moduleBase = mainModule.BaseAddress;
            var rvas = RelinkBuildLocator.Resolve(imagePath);

            _managerSlot = moduleBase + rvas.ManagerSlot;
            try
            {
                _sendHook = _hooks.CreateHook<SendMessageDelegate>(
                    SendMessage,
                    moduleBase + rvas.SendMessage);
                _sendHook.Activate();

                _rpcHook = _hooks.CreateHook<RpcMessageDelegate>(
                    RpcMessage,
                    moduleBase + rvas.RpcMessage);
                _rpcHook.Activate();

                Volatile.Write(ref _initialized, true);
                _log(
                    $"Relink 2.0.2 native chat bridge attached: send=0x{(nuint)(moduleBase + rvas.SendMessage):X}, " +
                    $"receive=0x{(nuint)(moduleBase + rvas.RpcMessage):X}.");
            }
            catch
            {
                _rpcHook?.Disable();
                _sendHook?.Disable();
                _rpcHook = null;
                _sendHook = null;
                _managerSlot = nint.Zero;
                throw;
            }
        }
    }

    public ChatSendResult Send(string message)
    {
        if (!IsInitialized || Volatile.Read(ref _suspended))
            return ChatSendResult.Unavailable("The Relink native chat bridge is not active.");
        if (message.IndexOf('\0') >= 0)
            return ChatSendResult.Rejected("Chat messages cannot contain NUL characters.");

        var byteCount = Encoding.UTF8.GetByteCount(message);
        if (byteCount > RelinkChatPacketDecoder.MaximumMessageBytes)
        {
            return ChatSendResult.Rejected(
                $"Relink allows at most {RelinkChatPacketDecoder.MaximumMessageBytes} UTF-8 bytes per message.");
        }

        lock (_lifecycleSync)
        {
            if (!IsInitialized || _suspended)
                return ChatSendResult.Unavailable("The Relink native chat bridge is not active.");

            var manager = Marshal.ReadIntPtr(_managerSlot);
            if (manager == nint.Zero)
                return ChatSendResult.Unavailable("Relink's chat Manager is not ready in the current game state.");

            var utf8 = new byte[byteCount + 1];
            Encoding.UTF8.GetBytes(message, utf8);
            var empty = stackalloc byte[1];
            empty[0] = 0;

            fixed (byte* messageBytes = utf8)
            {
                var messageView = new NativeStringView((nint)messageBytes, (nuint)byteCount);
                var emptyView = new NativeStringView((nint)empty, 0);
                var echoToken = _echoSuppressor.Register(message, DateTimeOffset.UtcNow);
                try
                {
                    _sendHook!.OriginalFunction(
                        manager,
                        (nint)(&messageView),
                        RelinkChatPacketDecoder.RawTextHash,
                        (nint)(&emptyView),
                        -1);
                }
                catch (Exception exception)
                {
                    _echoSuppressor.Cancel(echoToken);
                    SafeLog($"Native chat send failed: {exception.Message}");
                    return ChatSendResult.Failed("Relink rejected the native chat call.");
                }
            }
        }

        return ChatSendResult.Sent();
    }

    public bool TryRead(out IncomingChatMessage message)
    {
        if (!_incoming.TryDequeue(out message))
            return false;

        Interlocked.Decrement(ref _incomingCount);
        return true;
    }

    public void Suspend()
    {
        lock (_lifecycleSync)
        {
            Volatile.Write(ref _suspended, true);
            _rpcHook?.Disable();
            _sendHook?.Disable();
        }
    }

    public void Resume()
    {
        lock (_lifecycleSync)
        {
            _sendHook?.Enable();
            _rpcHook?.Enable();
            Volatile.Write(ref _suspended, false);
        }
    }

    private void SendMessage(nint manager, nint messageView, uint messageHash, nint senderView, int category)
    {
        try
        {
            _sendHook!.OriginalFunction(manager, messageView, messageHash, senderView, category);
        }
        catch (Exception exception)
        {
            SafeLog($"Native sendMessage hook failed: {exception.Message}");
        }
    }

    private void RpcMessage(nint chat)
    {
        IncomingChatMessage pending = default;
        var decoded = false;
        try
        {
            if (chat != nint.Zero)
            {
                var packet = new ReadOnlySpan<byte>((void*)chat, RelinkChatPacketDecoder.PacketBytesToCopy);
                decoded = RelinkChatPacketDecoder.TryDecode(packet, DateTimeOffset.UtcNow, out pending);
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _decodeFailureLogged, 1) == 0)
                SafeLog($"Incoming Relink chat decoding failed; further failures are suppressed: {exception.Message}");
        }

        try
        {
            _rpcHook!.OriginalFunction(chat);
        }
        catch (Exception exception)
        {
            SafeLog($"Native rpcMessage hook failed: {exception.Message}");
            return;
        }

        if (decoded && !_echoSuppressor.TryConsume(pending.Text, pending.ReceivedAt))
            EnqueueIncoming(pending);
    }

    private void EnqueueIncoming(IncomingChatMessage message)
    {
        Interlocked.Increment(ref _incomingCount);
        _incoming.Enqueue(message);
        while (Volatile.Read(ref _incomingCount) > MaximumQueuedIncomingMessages &&
               _incoming.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _incomingCount);
        }
    }

    private void SafeLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Never allow a logger failure to escape a native hook callback.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeStringView(nint data, nuint length)
    {
        public readonly nint Data = data;
        public readonly nuint Length = length;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SendMessageDelegate(
        nint manager,
        nint messageView,
        uint messageHash,
        nint senderView,
        int category);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RpcMessageDelegate(nint chat);
}
