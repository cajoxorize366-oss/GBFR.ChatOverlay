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
    private const int MaximumAttributionDiagnosticsPerRoom = 32;

    private readonly ReloadedHooksApi _hooks;
    private readonly Action<string> _log;
    private readonly RelinkGameContextProbe? _configuredGameContext;
    private readonly ChatBlacklist _chatBlacklist;
    private readonly Func<PartyNetworkLocalRole> _getLocalNetworkRole;
    private readonly ConcurrentQueue<IncomingChatMessage> _incoming = new();
    private readonly RecentEchoSuppressor _echoSuppressor = new();
    private readonly object _lifecycleSync = new();
    private readonly LocalChatIdentityCache _localIdentityCache = new();

    private IHook<SendMessageDelegate>? _sendHook;
    private IHook<RpcMessageDelegate>? _rpcHook;
    private SendStampDelegate? _sendStamp;
    private SendFixedPhraseDelegate? _sendFixedPhrase;
    private SendEmotionDelegate? _sendEmotion;
    private PlayFixedPhraseDelegate? _playFixedPhrase;
    private PlayEmotionDelegate? _playEmotion;
    private RelinkPlayerNameResolver? _playerNameResolver;
    private RelinkPartyMemberIdentityResolver? _partyMemberIdentityResolver;
    private RelinkLobbyOwnerTracker? _lobbyOwnerTracker;
    private IRelinkPartyMemberSlotResolver? _memberSlotResolver;
    private int _localIdentityLogged;
    private bool _initialized;
    private bool _suspended;
    private int _incomingCount;
    private int _decodeFailureLogged;
    private int _localFallbackLogged;
    private int _attributionDiagnosticsLogged;

    public RelinkChatBridge(
        ReloadedHooksApi hooks,
        Action<string> log,
        RelinkGameContextProbe? gameContext = null,
        ChatBlacklist? chatBlacklist = null)
        : this(hooks, log, gameContext, chatBlacklist, null)
    {
    }

    internal RelinkChatBridge(
        ReloadedHooksApi hooks,
        Action<string> log,
        RelinkGameContextProbe? gameContext,
        ChatBlacklist? chatBlacklist,
        Func<PartyNetworkLocalRole>? getLocalNetworkRole)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _configuredGameContext = gameContext;
        _chatBlacklist = chatBlacklist ?? new ChatBlacklist();
        _getLocalNetworkRole = getLocalNetworkRole ?? (() => PartyNetworkLocalRole.Unknown);
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
                _memberSlotResolver = RelinkPartyMemberSlotResolver.CreateForCurrentProcess(
                    moduleBase,
                    rvas,
                    _log);
                _playerNameResolver = RelinkPlayerNameResolver.CreateForCurrentProcess(
                    moduleBase,
                    rvas,
                    _memberSlotResolver,
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
                        rvas,
                        _memberSlotResolver);
                    _partyMemberIdentityResolver = partyIdentityResolver;
                    var lobbyOwnerTracker = new RelinkLobbyOwnerTracker(
                        _hooks,
                        partyIdentityResolver,
                        new CurrentProcessRelinkMemoryReader(),
                        _getLocalNetworkRole,
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
                    $"Relink 2.0.4 native chat bridge attached: send=0x{(nuint)(moduleBase + rvas.SendMessage):X}, " +
                    $"receive=0x{(nuint)(moduleBase + rvas.RpcMessage):X}.");
                _log(
                    $"Relink incoming player-name resolver attached: senderSlot=0x" +
                    $"{(nuint)(moduleBase + rvas.SenderSlotResolver):X}, memberLookup=0x" +
                    $"{(nuint)(moduleBase + rvas.LobbyMemberLookup):X}; opaque RPC member keys are " +
                    $"mapped to verified four-party member slots before lobby-name lookup.");
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
                _partyMemberIdentityResolver = null;
                _lobbyOwnerTracker = null;
                _memberSlotResolver = null;
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

                CompleteLocalSend(
                    echoToken,
                    message,
                    GetLocalIdentity(),
                    ChatCommunicationCue.None);
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
        TryGetLocalPlayerName(out _);
        return _localIdentityCache.Read();
    }

    internal bool TryGetLocalPlayerName(out string playerName)
    {
        playerName = string.Empty;
        if (TryResolveLocalSlot(out var localMemberSlot) &&
            _playerNameResolver?.TryResolveName(localMemberSlot, 0, out var resolvedName) == true &&
            !string.IsNullOrWhiteSpace(resolvedName))
        {
            playerName = resolvedName.Trim();
            _localIdentityCache.UpdateName(playerName);
            return true;
        }

        return _localIdentityCache.TryReadVerifiedName(out playerName);
    }

    internal bool TryResolveLocalSlot(out int localMemberSlot)
    {
        localMemberSlot = -1;
        return _partyMemberIdentityResolver?.TryResolveLocalMemberSlot(out localMemberSlot) == true &&
               localMemberSlot is >= 0 and <= 3;
    }

    internal bool TryGetRoomIdentitySnapshot(out PartyRoomIdentitySnapshot snapshot)
    {
        snapshot = default;
        try
        {
            if (_lobbyOwnerTracker?.TryGetRoomIdentitySnapshot(out snapshot) != true)
                return false;

            if (snapshot.HostState == PartyRoomHostState.Unknown)
                return true;

            var roomName = snapshot.RoomName;
            if (string.IsNullOrWhiteSpace(roomName))
            {
                if (snapshot.HostState == PartyRoomHostState.LocalHost)
                {
                    roomName = GetLocalIdentity().Sender;
                }
                else if (TryGetHostPlayerNumber(out var hostPlayerNumber) &&
                         hostPlayerNumber is >= 2 and <= 4 &&
                         TryResolveLocalSlot(out var hostLocalSlot) &&
                         PartyMemberSlotMap.TryGetActualSlot(
                             hostLocalSlot,
                             hostPlayerNumber - 1,
                             out var hostActualSlot) &&
                         _playerNameResolver?.TryResolveName(
                             hostActualSlot,
                             0,
                             out var resolvedName) == true &&
                         !string.IsNullOrWhiteSpace(resolvedName))
                {
                    roomName = resolvedName.Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(roomName))
            {
                _lobbyOwnerTracker.CacheRoomName(roomName);
            }

            snapshot = snapshot with { RoomName = roomName };
            return true;
        }
        catch
        {
            snapshot = default;
            return false;
        }
    }

    internal bool TryGetRemotePlayerName(int remotePlayerNumber, out string playerName)
    {
        playerName = string.Empty;
        if (remotePlayerNumber is < 1 or > 3 ||
            !TryResolveLocalSlot(out var localMemberSlot) ||
            !PartyMemberSlotMap.TryGetActualSlot(localMemberSlot, remotePlayerNumber, out var actualSlot))
        {
            return false;
        }

        return _playerNameResolver?.TryResolveName(actualSlot, 0, out playerName) == true;
    }

    internal void ResetLobbyOwner()
    {
        _lobbyOwnerTracker?.Reset();
        _localIdentityCache.Clear();
        Volatile.Write(ref _localIdentityLogged, 0);
        Volatile.Write(ref _attributionDiagnosticsLogged, 0);
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
        var outgoingCue = ChatCommunicationCue.None;
        long echoToken = 0;
        if (!Volatile.Read(ref _suspended) &&
            messageHash == RelinkChatPacketDecoder.RawTextHash &&
            TryReadOutgoingText(messageView, out var decodedText))
        {
            outgoingText = decodedText;
            if (TryReadOutgoingText(senderView, out var presentationLabel))
            {
                outgoingCue = RelinkChatPacketDecoder.ClassifyCommunicationCue(
                    presentationLabel,
                    out _);
            }
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

        CompleteLocalSend(echoToken, outgoingText, GetLocalIdentity(), outgoingCue);
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
        var rawSenderKey = 0u;
        var hasRawSenderKey = false;
        var remoteMemberSlot = -1;
        var localMemberSlot = -1;
        try
        {
            if (chat != nint.Zero)
            {
                var packet = new ReadOnlySpan<byte>((void*)chat, RelinkChatPacketDecoder.PacketBytesToCopy);
                if (RelinkChatPacketDecoder.TryReadSenderId(packet, out rawSenderKey))
                {
                    hasRawSenderKey = true;
                    if (_memberSlotResolver?.TryResolveSlot(rawSenderKey, out remoteMemberSlot) == true)
                    {
                        TryResolveLocalSlot(out localMemberSlot);
                        if (RelinkChatSenderPolicy.ShouldBlockBlacklistedRpc(
                                remoteMemberSlot,
                                localMemberSlot,
                                _chatBlacklist))
                        {
                            // This is the authoritative receive gate. Returning here prevents Relink's
                            // own handler from accepting raw text, stamps and fixed phrases from the player.
                            return;
                        }
                    }
                }
                decoded = RelinkChatPacketDecoder.TryDecode(
                    packet,
                    DateTimeOffset.UtcNow,
                    out pending);
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

        if (!PartyMemberSlotMap.IsValidSlot(localMemberSlot))
            TryResolveLocalSlot(out localMemberSlot);

        var remoteResolved = hasRawSenderKey &&
                             PartyMemberSlotMap.IsValidSlot(remoteMemberSlot);
        var isLocal = PartyMemberSlotMap.IsValidSlot(localMemberSlot) &&
                      remoteResolved &&
                      RelinkChatSenderPolicy.IsLocalRpc(remoteMemberSlot, localMemberSlot);
        LogAttributionDecision(
            hasRawSenderKey,
            rawSenderKey,
            remoteResolved,
            remoteMemberSlot,
            localMemberSlot,
            isLocal,
            pending);
        var publishAuthoritativeEcho = RelinkChatSenderPolicy.TryConsumeAuthoritativeLocalEcho(
            _echoSuppressor,
            isLocal,
            pending.Text,
            pending.ReceivedAt,
            out var wasLocalEcho);
        if (wasLocalEcho)
        {
            ObserveLocalEcho(remoteMemberSlot, pending.SenderId);
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

        if (isLocal)
        {
            var identity = ResolveLocalRpcIdentity(
                remoteMemberSlot,
                pending.SenderId);
            EnqueueIncoming(pending with
            {
                Sender = identity.Sender,
                PlayerNumber = identity.PlayerNumber,
                IsLocal = true,
            });
            return;
        }

        string? resolvedPlayerName = null;
        if (remoteResolved &&
            _playerNameResolver?.TryResolveName(
                remoteMemberSlot,
                pending.SenderId,
                out var playerName) == true)
        {
            resolvedPlayerName = playerName;
        }

        pending = RelinkChatMessageAttribution.ApplyRemoteIdentity(
            pending,
            localMemberSlot,
            remoteResolved ? remoteMemberSlot : -1,
            resolvedPlayerName);

        EnqueueIncoming(pending);
    }

    private void CompleteLocalSend(
        long echoToken,
        string text,
        LocalChatIdentity identity,
        ChatCommunicationCue communicationCue)
    {
        var completedAt = DateTimeOffset.UtcNow;
        if (!_echoSuppressor.TryComplete(echoToken, completedAt))
            return;

        EnqueueIncoming(CreateLocalEchoMessage(
            text,
            identity,
            completedAt,
            communicationCue));
        if (Interlocked.Exchange(ref _localFallbackLogged, 1) == 0)
        {
            SafeLog(
                "Relink local chat history fallback is active: successful native sends are published " +
                "immediately, and any later authoritative RPC echo is identity-only and deduplicated.");
        }
    }

    internal static IncomingChatMessage CreateLocalEchoMessage(
        string text,
        LocalChatIdentity identity,
        DateTimeOffset completedAt,
        ChatCommunicationCue communicationCue) =>
        new(
            identity.Sender,
            text,
            0,
            0,
            0,
            completedAt,
            identity.PlayerNumber,
            IsLocal: true,
            CommunicationCue: communicationCue);

    private LocalChatIdentity ResolveLocalRpcIdentity(
        int memberSlot,
        uint senderId)
    {
        if (memberSlot is < 0 or >= 4)
            return GetLocalIdentity();

        string? playerName = null;
        if (_playerNameResolver?.TryResolveName(memberSlot, senderId, out var resolvedName) == true &&
            !string.IsNullOrWhiteSpace(resolvedName))
            playerName = resolvedName.Trim();

        if (!string.IsNullOrWhiteSpace(playerName))
            _localIdentityCache.UpdateName(playerName);

        return _localIdentityCache.Read();
    }

    private void ObserveLocalEcho(
        int memberSlot,
        uint senderId)
    {
        var identity = ResolveLocalRpcIdentity(
            memberSlot,
            senderId);

        if (Interlocked.Exchange(ref _localIdentityLogged, 1) == 0)
        {
            SafeLog(
                $"Relink local chat identity learned from the authoritative RPC echo: " +
                $"member_slot={memberSlot}, player_number=1, " +
                $"name='{identity.Sender}'.");
        }
    }

    private void LogAttributionDecision(
        bool hasRawSenderKey,
        uint rawSenderKey,
        bool remoteResolved,
        int remoteMemberSlot,
        int localMemberSlot,
        bool isLocal,
        IncomingChatMessage message)
    {
        var ordinal = Interlocked.Increment(ref _attributionDiagnosticsLogged);
        if (ordinal > MaximumAttributionDiagnosticsPerRoom)
            return;

        var playerNumber = isLocal
            ? 1
            : PartyMemberSlotMap.TryGetPlayerNumber(
                localMemberSlot,
                remoteMemberSlot,
                out var remotePlayerNumber)
                ? remotePlayerNumber
                : 0;
        var relation = isLocal
            ? "local"
            : remoteResolved
                ? "remote"
                : "unresolved";
        var memberKey = hasRawSenderKey ? $"0x{rawSenderKey:X8}" : "unavailable";
        var memberIndex = remoteResolved ? remoteMemberSlot.ToString() : "unresolved";
        var localIndex = PartyMemberSlotMap.IsValidSlot(localMemberSlot)
            ? localMemberSlot.ToString()
            : "unresolved";

        SafeLog(
            $"Relink chat attribution #{ordinal}: member_key={memberKey}, " +
            $"member_index={memberIndex}, local_index={localIndex}, relation={relation}, " +
            $"ui_player={playerNumber}, cue={message.CommunicationCue}, " +
            $"category=0x{message.Category:X8}, metadata=0x{message.Metadata:X8}.");
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
