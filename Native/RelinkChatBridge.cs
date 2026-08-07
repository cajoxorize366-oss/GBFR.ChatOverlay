using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using Reloaded.Hooks.Definitions;
using ReloadedHooksApi = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace GBFR.ChatOverlay.Native;

public sealed unsafe class RelinkChatBridge :
    IChatTransport,
    IIncomingChatSource,
    IAuthoritativeLocalEchoTransport
{
    private const int MaximumQueuedIncomingMessages = 512;

    private readonly ReloadedHooksApi _hooks;
    private readonly Action<string> _log;
    private readonly RelinkGameContextProbe? _configuredGameContext;
    private readonly ChatBlacklist _chatBlacklist;
    private readonly ConcurrentQueue<IncomingChatMessage> _incoming = new();
    private readonly RecentEchoSuppressor _echoSuppressor = new();
    private readonly object _lifecycleSync = new();

    private IHook<SendMessageDelegate>? _sendHook;
    private IHook<RpcMessageDelegate>? _rpcHook;
    private SendStampDelegate? _sendStamp;
    private SendFixedPhraseDelegate? _sendFixedPhrase;
    private SendEmotionDelegate? _sendEmotion;
    private PlayFixedPhraseDelegate? _playFixedPhrase;
    private PlayEmotionDelegate? _playEmotion;
    private RelinkPlayerNameResolver? _playerNameResolver;
    private RelinkLobbyOwnerTracker? _lobbyOwnerTracker;
    private string? _localPlayerName;
    private int _localPlayerNumber;
    private int _localIdentityLogged;
    private bool _initialized;
    private bool _suspended;
    private int _incomingCount;
    private int _decodeFailureLogged;
    private int _localFallbackLogged;

    public RelinkChatBridge(
        ReloadedHooksApi hooks,
        Action<string> log,
        RelinkGameContextProbe? gameContext = null,
        ChatBlacklist? chatBlacklist = null)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _configuredGameContext = gameContext;
        _chatBlacklist = chatBlacklist ?? new ChatBlacklist();
    }

    public bool IsInitialized => Volatile.Read(ref _initialized);

    public RelinkGameContextProbe? GameContext { get; private set; }

    public void Initialize()
    {
        lock (_lifecycleSync)
        {
            if (_initialized)
                return;

            var gameContext = _configuredGameContext ??
                RelinkGameContextProbe.CreateForCurrentProcess(_log);
            var moduleBase = gameContext.ModuleBase;
            var rvas = gameContext.ChatRvas;

            GameContext = gameContext;
            try
            {
                _playerNameResolver = RelinkPlayerNameResolver.CreateForCurrentProcess(
                    moduleBase,
                    rvas,
                    _log);
                _sendStamp = Marshal.GetDelegateForFunctionPointer<SendStampDelegate>(
                    moduleBase + rvas.SendStamp);
                _sendFixedPhrase = Marshal.GetDelegateForFunctionPointer<SendFixedPhraseDelegate>(
                    moduleBase + rvas.SendFixedPhrase);
                _sendEmotion = Marshal.GetDelegateForFunctionPointer<SendEmotionDelegate>(
                    moduleBase + rvas.SendEmotion);
                _playFixedPhrase = Marshal.GetDelegateForFunctionPointer<PlayFixedPhraseDelegate>(
                    moduleBase + rvas.PlayFixedPhrase);
                _playEmotion = Marshal.GetDelegateForFunctionPointer<PlayEmotionDelegate>(
                    moduleBase + rvas.PlayEmotion);
                _sendHook = _hooks.CreateHook<SendMessageDelegate>(
                    SendMessage,
                    moduleBase + rvas.SendMessage);
                _sendHook.Activate();

                _rpcHook = _hooks.CreateHook<RpcMessageDelegate>(
                    RpcMessage,
                    moduleBase + rvas.RpcMessage);
                _rpcHook.Activate();

                try
                {
                    var partyIdentityResolver = RelinkPartyMemberIdentityResolver.CreateForCurrentProcess(
                        moduleBase,
                        rvas);
                    var lobbyOwnerTracker = new RelinkLobbyOwnerTracker(
                        _hooks,
                        partyIdentityResolver,
                        new CurrentProcessRelinkMemoryReader(),
                        _log);
                    lobbyOwnerTracker.Initialize(moduleBase, rvas);
                    _lobbyOwnerTracker = lobbyOwnerTracker;
                }
                catch (Exception exception)
                {
                    _lobbyOwnerTracker?.Disable();
                    _lobbyOwnerTracker = null;
                    SafeLog(
                        $"Relink lobby-owner marker unavailable; native chat remains active: " +
                        $"{exception.Message}");
                }

                Volatile.Write(ref _initialized, true);
                _log(
                    $"Relink 2.0.3 native chat bridge attached: send=0x{(nuint)(moduleBase + rvas.SendMessage):X}, " +
                    $"receive=0x{(nuint)(moduleBase + rvas.RpcMessage):X}.");
                _log(
                    $"Relink incoming player-name resolver attached: senderSlot=0x" +
                    $"{(nuint)(moduleBase + rvas.SenderSlotResolver):X}, memberLookup=0x" +
                    $"{(nuint)(moduleBase + rvas.LobbyMemberLookup):X}; empty RPC sender labels now use " +
                    $"the verified four-slot lobby member table.");
                _log(
                    $"Relink official communication actions attached: stamp=0x" +
                    $"{(nuint)(moduleBase + rvas.SendStamp):X}, fixed=0x" +
                    $"{(nuint)(moduleBase + rvas.SendFixedPhrase):X}, emotion=0x" +
                    $"{(nuint)(moduleBase + rvas.SendEmotion):X}.");
            }
            catch
            {
                _lobbyOwnerTracker?.Disable();
                _rpcHook?.Disable();
                _sendHook?.Disable();
                _rpcHook = null;
                _sendHook = null;
                _playerNameResolver = null;
                _lobbyOwnerTracker = null;
                _sendStamp = null;
                _sendFixedPhrase = null;
                _sendEmotion = null;
                _playFixedPhrase = null;
                _playEmotion = null;
                GameContext = null;
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

            if (GameContext?.TryGetHudChatManager(out var manager) != true)
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

                CompleteLocalSend(echoToken, message, GetLocalIdentity());
            }
        }

        return ChatSendResult.Sent();
    }

    public ChatSendResult SendOfficialQuickAction(QuickActionKind kind, int officialId)
    {
        if (kind == QuickActionKind.CustomText)
            return ChatSendResult.Rejected("Custom text must use the normal chat transport.");
        if (!CommunicationCatalog.TryGetEntry(kind, officialId, out var entry))
            return ChatSendResult.Rejected("The selected official communication entry is invalid.");
        if (!IsInitialized || Volatile.Read(ref _suspended))
            return ChatSendResult.Unavailable("The Relink native chat bridge is not active.");

        lock (_lifecycleSync)
        {
            if (!IsInitialized || _suspended)
                return ChatSendResult.Unavailable("The Relink native chat bridge is not active.");

            try
            {
                SafeLog(
                    $"Invoking official communication: kind={kind}, id={entry.Id}, " +
                    $"native_value={entry.NativeValue}.");
                switch (kind)
                {
                    case QuickActionKind.Stamp:
                        if (GameContext?.TryGetHudChatManager(out var stampManager) != true)
                        {
                            return ChatSendResult.Unavailable(
                                "Relink's communication Manager is not ready in the current game state.");
                        }
                        SafeLog($"Official stamp Manager=0x{(nuint)stampManager:X}.");
                        _sendStamp!(stampManager, entry.NativeValue);
                        break;

                    case QuickActionKind.FixedPhrase:
                        if (GameContext?.TryGetHudChatManager(out var phraseManager) != true)
                        {
                            return ChatSendResult.Unavailable(
                                "Relink's communication Manager is not ready in the current game state.");
                        }

                        // -1 is the game's explicit no-character-voice sentinel. The fixed phrase
                        // itself is sent normally without guessing the current character voice ID.
                        _sendFixedPhrase!(phraseManager, entry.NativeValue, -1, 0);
                        _playFixedPhrase!(entry.NativeValue);
                        break;

                    case QuickActionKind.Emotion:
                        _sendEmotion!(entry.NativeValue);
                        _playEmotion!(entry.NativeValue);
                        break;

                    default:
                        return ChatSendResult.Rejected("Unsupported quick action type.");
                }
                SafeLog(
                    $"Official communication native call returned: kind={kind}, id={entry.Id}.");
            }
            catch (Exception exception)
            {
                SafeLog($"Native official communication send failed: {exception.Message}");
                return ChatSendResult.Failed("Relink rejected the official communication call.");
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

    internal bool TryGetHostPlayerNumber(out int playerNumber)
    {
        if (_lobbyOwnerTracker?.TryGetHostPlayerNumber(out playerNumber) == true)
            return true;
        playerNumber = 0;
        return false;
    }

    internal LocalChatIdentity GetLocalIdentity()
    {
        var cachedName = Volatile.Read(ref _localPlayerName);
        var cachedNumber = Volatile.Read(ref _localPlayerNumber);
        if (!string.IsNullOrWhiteSpace(cachedName))
            return new LocalChatIdentity(cachedName, cachedNumber);

        if (_playerNameResolver?.TryResolveName(0, 0, out var resolvedName) == true)
        {
            Volatile.Write(ref _localPlayerName, resolvedName);
            return new LocalChatIdentity(resolvedName, cachedNumber);
        }

        return new LocalChatIdentity("Local", cachedNumber);
    }

    internal bool TryGetRemotePlayerName(int remotePlayerNumber, out string playerName)
    {
        playerName = string.Empty;
        return remotePlayerNumber is >= 1 and <= 3 &&
               _playerNameResolver?.TryResolveName(remotePlayerNumber, 0, out playerName) == true;
    }

    internal void ResetLobbyOwner()
    {
        _lobbyOwnerTracker?.Reset();
        Volatile.Write(ref _localPlayerName, null);
        Volatile.Write(ref _localPlayerNumber, 0);
        Volatile.Write(ref _localIdentityLogged, 0);
    }

    public void Suspend()
    {
        lock (_lifecycleSync)
        {
            Volatile.Write(ref _suspended, true);
            _lobbyOwnerTracker?.Suspend();
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
            _lobbyOwnerTracker?.Resume();
            Volatile.Write(ref _suspended, false);
        }
    }

    private void SendMessage(nint manager, nint messageView, uint messageHash, nint senderView, int category)
    {
        string? outgoingText = null;
        long echoToken = 0;
        if (!Volatile.Read(ref _suspended) &&
            messageHash == RelinkChatPacketDecoder.RawTextHash &&
            TryReadOutgoingText(messageView, out var decodedText))
        {
            outgoingText = decodedText;
            echoToken = _echoSuppressor.Register(outgoingText, DateTimeOffset.UtcNow);
        }

        try
        {
            _sendHook!.OriginalFunction(manager, messageView, messageHash, senderView, category);
        }
        catch (Exception exception)
        {
            if (echoToken != 0)
                _echoSuppressor.Cancel(echoToken);
            SafeLog($"Native sendMessage hook failed: {exception.Message}");
            return;
        }

        if (outgoingText is null)
            return;

        CompleteLocalSend(echoToken, outgoingText, ResolveLocalIdentity(senderView));
    }

    private static bool TryReadOutgoingText(nint messageView, out string text)
    {
        text = string.Empty;
        if (messageView == nint.Zero)
            return false;

        var view = *(NativeStringView*)messageView;
        if (view.Data == nint.Zero ||
            view.Length == 0 ||
            view.Length > RelinkChatPacketDecoder.MaximumMessageBytes)
        {
            return false;
        }

        return RelinkChatPacketDecoder.TryDecodeOutgoingText(
            new ReadOnlySpan<byte>((void*)view.Data, checked((int)view.Length)),
            out text);
    }

    private void RpcMessage(nint chat)
    {
        IncomingChatMessage pending = default;
        var decoded = false;
        var hasExplicitSenderLabel = false;
        var senderId = 0u;
        var memberSlot = -1;
        try
        {
            if (chat != nint.Zero)
            {
                var packet = new ReadOnlySpan<byte>((void*)chat, RelinkChatPacketDecoder.PacketBytesToCopy);
                if (RelinkChatPacketDecoder.TryReadSenderId(packet, out senderId))
                {
                    var resolvedMember =
                        _playerNameResolver?.TryResolveMemberSlot(senderId, out memberSlot) == true;
                    if ((resolvedMember && _chatBlacklist.IsMemberSlotMuted(memberSlot)) ||
                        (!resolvedMember && _chatBlacklist.AreAllRemotePlayersMuted))
                    {
                        // This is the authoritative receive gate. Returning here prevents Relink's
                        // own handler from accepting raw text, stamps and fixed phrases from the player.
                        return;
                    }
                }
                decoded = RelinkChatPacketDecoder.TryDecode(
                    packet,
                    DateTimeOffset.UtcNow,
                    out pending,
                    out hasExplicitSenderLabel);
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

        if (!decoded)
            return;

        if (memberSlot < 0)
            _playerNameResolver?.TryResolveMemberSlot(pending.SenderId, out memberSlot);

        var publishAuthoritativeEcho = _echoSuppressor.TryConsume(
            pending.Text,
            pending.ReceivedAt,
            out var wasLocalEcho);
        if (wasLocalEcho)
        {
            ObserveLocalEcho(memberSlot, pending.SenderId, pending.Sender, hasExplicitSenderLabel);
            if (publishAuthoritativeEcho)
            {
                var identity = GetLocalIdentity();
                EnqueueIncoming(pending with
                {
                    Sender = identity.Sender,
                    PlayerNumber = identity.PlayerNumber,
                    IsLocal = true,
                });
            }
            return;
        }

        if (!hasExplicitSenderLabel &&
            memberSlot >= 0 &&
            _playerNameResolver?.TryResolveName(memberSlot, pending.SenderId, out var playerName) == true)
        {
            pending = pending with { Sender = playerName };
        }

        if (memberSlot >= 0)
            pending = pending with { PlayerNumber = memberSlot + 1 };

        EnqueueIncoming(pending);
    }

    private void CompleteLocalSend(long echoToken, string text, LocalChatIdentity identity)
    {
        var completedAt = DateTimeOffset.UtcNow;
        if (!_echoSuppressor.TryComplete(echoToken, completedAt))
            return;

        EnqueueIncoming(new IncomingChatMessage(
            identity.Sender,
            text,
            0,
            0,
            0,
            completedAt,
            identity.PlayerNumber,
            IsLocal: true));
        if (Interlocked.Exchange(ref _localFallbackLogged, 1) == 0)
        {
            SafeLog(
                "Relink local chat history fallback is active: successful native sends are published " +
                "immediately, and any later authoritative RPC echo is identity-only and deduplicated.");
        }
    }

    private LocalChatIdentity ResolveLocalIdentity(nint senderView)
    {
        if (TryReadOutgoingText(senderView, out var senderName) &&
            !string.IsNullOrWhiteSpace(senderName))
        {
            Volatile.Write(ref _localPlayerName, senderName.Trim());
        }

        return GetLocalIdentity();
    }

    private void ObserveLocalEcho(
        int memberSlot,
        uint senderId,
        string decodedSender,
        bool hasExplicitSenderLabel)
    {
        if (memberSlot is < 0 or >= 4)
            return;

        string? playerName = null;
        if (_playerNameResolver?.TryResolveName(memberSlot, senderId, out var resolvedName) == true)
            playerName = resolvedName;
        else if (hasExplicitSenderLabel && !string.IsNullOrWhiteSpace(decodedSender))
            playerName = decodedSender.Trim();

        if (!string.IsNullOrWhiteSpace(playerName))
            Volatile.Write(ref _localPlayerName, playerName);
        Volatile.Write(ref _localPlayerNumber, memberSlot + 1);

        if (Interlocked.Exchange(ref _localIdentityLogged, 1) == 0)
        {
            SafeLog(
                $"Relink local chat identity learned from the authoritative RPC echo: " +
                $"member_slot={memberSlot}, player_number={memberSlot + 1}, " +
                $"name='{playerName ?? "unavailable"}'.");
        }
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SendStampDelegate(nint manager, int stampId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SendFixedPhraseDelegate(
        nint manager,
        int phraseId,
        int voiceId,
        int flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SendEmotionDelegate(int animationId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PlayFixedPhraseDelegate(int phraseId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PlayEmotionDelegate(int animationId);
}
