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
    private readonly IChatModerationService? _chatModeration;
    private readonly Func<PartyNetworkLocalRole> _getLocalNetworkRole;
    private readonly ConcurrentQueue<IncomingChatMessage> _incoming = new();
    private readonly RecentEchoSuppressor _echoSuppressor = new();
    private readonly PendingFilteredChatQueue _pendingFilteredSends = new();
    private readonly PendingFilteredReceiveQueue _pendingFilteredReceives = new();
    private readonly object _lifecycleSync = new();
    private readonly object _filterPipelineSync = new();
    private readonly LocalChatIdentityCache _localIdentityCache = new();

    private IHook<SendMessageDelegate>? _sendHook;
    private IHook<RpcMessageDelegate>? _rpcHook;
    private IHook<WordFilterCallbackDelegate>? _filteredSendHook;
    private IHook<WordFilterCallbackDelegate>? _filteredReceiveHook;
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
    private int _moderationFailureLogged;
    private int _moderationRewriteFailureLogged;
    private int _filterPipelineGeneration;

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
        Func<PartyNetworkLocalRole>? getLocalNetworkRole,
        IChatModerationService? chatModeration = null)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _configuredGameContext = gameContext;
        _chatBlacklist = chatBlacklist ?? new ChatBlacklist();
        _chatModeration = chatModeration;
        _getLocalNetworkRole = getLocalNetworkRole ?? (() => PartyNetworkLocalRole.Unknown);
    }

    public bool IsInitialized => Volatile.Read(ref _initialized);

    internal bool IsNativeWordFilterSynchronized =>
        IsInitialized && _filteredSendHook is not null && _filteredReceiveHook is not null;

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
                _filteredSendHook = _hooks.CreateHook<WordFilterCallbackDelegate>(
                    FilteredSendMessage,
                    moduleBase + rvas.FilteredSendCallback);
                _filteredSendHook.Activate();

                _filteredReceiveHook = _hooks.CreateHook<WordFilterCallbackDelegate>(
                    FilteredReceiveMessage,
                    moduleBase + rvas.FilteredReceiveCallback);
                _filteredReceiveHook.Activate();

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
                    $"Relink native WordFilter completion callbacks attached: send=0x" +
                    $"{(nuint)(moduleBase + rvas.FilteredSendCallback):X}, receive=0x" +
                    $"{(nuint)(moduleBase + rvas.FilteredReceiveCallback):X}; overlay history now uses " +
                    $"the final sanitized text accepted by Relink.");
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
                _filteredReceiveHook?.Disable();
                _filteredSendHook?.Disable();
                _rpcHook = null;
                _sendHook = null;
                _filteredReceiveHook = null;
                _filteredSendHook = null;
                _playerNameResolver = null;
                _partyMemberIdentityResolver = null;
                _lobbyOwnerTracker = null;
                _memberSlotResolver = null;
                _sendStamp = null;
                _sendFixedPhrase = null;
                _sendEmotion = null;
                _playFixedPhrase = null;
                _playEmotion = null;
                ClearFilterPipelineState();
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
                var pendingToken = EnqueuePendingFilteredSend(
                    message,
                    GetLocalIdentity(),
                    ChatCommunicationCue.None,
                    DateTimeOffset.UtcNow);
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
                    _pendingFilteredSends.Cancel(pendingToken);
                    SafeLog($"Native chat send failed: {exception.Message}");
                    return ChatSendResult.Failed("Relink rejected the native chat call.");
                }
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
        ClearFilterPipelineState();
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
            ClearFilterPipelineState();
            _lobbyOwnerTracker?.Suspend();
            _rpcHook?.Disable();
            _sendHook?.Disable();
            _filteredReceiveHook?.Disable();
            _filteredSendHook?.Disable();
        }
    }

    public void Resume()
    {
        lock (_lifecycleSync)
        {
            _filteredSendHook?.Enable();
            _filteredReceiveHook?.Enable();
            _sendHook?.Enable();
            _rpcHook?.Enable();
            _lobbyOwnerTracker?.Resume();
            Volatile.Write(ref _suspended, false);
        }
    }

    private void SendMessage(nint manager, nint messageView, uint messageHash, nint senderView, int category)
    {
        long pendingToken = 0;
        var forwardedCategory = category;
        if (!Volatile.Read(ref _suspended) &&
            messageHash == RelinkChatPacketDecoder.RawTextHash &&
            TryReadOutgoingText(messageView, out var decodedText))
        {
            var outgoingCue = ChatCommunicationCue.None;
            if (TryReadOutgoingText(senderView, out var presentationLabel))
            {
                outgoingCue = RelinkChatPacketDecoder.ClassifyCommunicationCue(
                    presentationLabel,
                    out _);
            }
            pendingToken = EnqueuePendingFilteredSend(
                decodedText,
                GetLocalIdentity(),
                outgoingCue,
                DateTimeOffset.UtcNow);
            forwardedCategory = RelinkOutgoingChatPolicy.NormalizeForwardedCategory(
                messageHash,
                category,
                outgoingCue);
        }

        try
        {
            _sendHook!.OriginalFunction(manager, messageView, messageHash, senderView, forwardedCategory);
        }
        catch (Exception exception)
        {
            _pendingFilteredSends.Cancel(pendingToken);
            SafeLog($"Native sendMessage hook failed: {exception.Message}");
        }
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

    private void FilteredSendMessage(nint callbackState, nint unused, nint filteredTextView)
    {
        var callbackGeneration = 0;
        var completedAt = DateTimeOffset.UtcNow;
        var finalText = string.Empty;
        var pending = default(PendingFilteredChatQueueEntry);
        long echoToken = 0;
        try
        {
            lock (_filterPipelineSync)
            {
                callbackGeneration = Volatile.Read(ref _filterPipelineGeneration);
                var decoded = TryReadOutgoingText(filteredTextView, out finalText);
                var hasPending = IsInitialized &&
                                 !Volatile.Read(ref _suspended) &&
                                 (decoded
                                     ? _pendingFilteredSends.TryTake(finalText, completedAt, out pending)
                                     : _pendingFilteredSends.TryTakeOldest(completedAt, out pending));
                if (decoded && hasPending)
                    echoToken = _echoSuppressor.Register(finalText, completedAt);
            }
        }
        catch (Exception exception)
        {
            SafeLog($"Relink filtered-send callback sampling failed: {exception.Message}");
        }

        try
        {
            _filteredSendHook!.OriginalFunction(callbackState, unused, filteredTextView);
        }
        catch (Exception exception)
        {
            if (echoToken != 0)
                _echoSuppressor.Cancel(echoToken);
            SafeLog($"Relink filtered-send callback failed: {exception.Message}");
            return;
        }

        try
        {
            lock (_filterPipelineSync)
            {
                if (echoToken == 0)
                    return;
                if (Volatile.Read(ref _suspended) ||
                    callbackGeneration != Volatile.Read(ref _filterPipelineGeneration))
                {
                    _echoSuppressor.Cancel(echoToken);
                    return;
                }

                var communicationCue = pending.ChatCommunicationCue;
                if (callbackState != nint.Zero &&
                    RelinkFilteredChatCallbackDecoder.TryDecodeSendCue(
                        new ReadOnlySpan<byte>(
                            (void*)callbackState,
                            RelinkFilteredChatCallbackDecoder.SendCallbackStateBytes),
                        out var callbackCue))
                {
                    communicationCue = callbackCue;
                }

                CompleteLocalSend(
                    echoToken,
                    finalText,
                    pending.LocalChatIdentity,
                    communicationCue);
            }
        }
        catch (Exception exception)
        {
            _echoSuppressor.Cancel(echoToken);
            SafeLog($"Relink filtered-send history publication failed: {exception.Message}");
        }
    }

    private void FilteredReceiveMessage(nint callbackState, nint unused, nint filteredTextView)
    {
        var callbackGeneration = Volatile.Read(ref _filterPipelineGeneration);
        var decoded = false;
        var pending = default(IncomingChatMessage);
        try
        {
            decoded = TryDecodeFilteredReceive(
                callbackState,
                filteredTextView,
                DateTimeOffset.UtcNow,
                out pending);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _decodeFailureLogged, 1) == 0)
                SafeLog($"Filtered Relink chat decoding failed: {exception.Message}");
        }

        try
        {
            _filteredReceiveHook!.OriginalFunction(callbackState, unused, filteredTextView);
        }
        catch (Exception exception)
        {
            SafeLog($"Relink filtered-receive callback failed: {exception.Message}");
            return;
        }

        try
        {
            lock (_filterPipelineSync)
            {
                if (!decoded ||
                    !IsInitialized ||
                    Volatile.Read(ref _suspended) ||
                    callbackGeneration != Volatile.Read(ref _filterPipelineGeneration) ||
                    !_pendingFilteredReceives.TryTake(
                        pending.SenderId,
                        pending.Category,
                        pending.Metadata,
                        pending.ReceivedAt))
                {
                    return;
                }

                PublishFilteredIncoming(pending);
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _decodeFailureLogged, 1) == 0)
            {
                SafeLog(
                    $"Filtered Relink chat publication failed; further failures are suppressed: " +
                    $"{exception.Message}");
            }
        }
    }

    private static bool TryDecodeFilteredReceive(
        nint callbackState,
        nint filteredTextView,
        DateTimeOffset receivedAt,
        out IncomingChatMessage message)
    {
        message = default;
        if (callbackState == nint.Zero || filteredTextView == nint.Zero)
            return false;

        var view = *(NativeStringView*)filteredTextView;
        if (view.Data == nint.Zero ||
            view.Length == 0 ||
            view.Length > RelinkChatPacketDecoder.MaximumMessageBytes)
        {
            return false;
        }

        return RelinkFilteredChatCallbackDecoder.TryDecodeReceive(
            new ReadOnlySpan<byte>(
                (void*)callbackState,
                RelinkFilteredChatCallbackDecoder.ReceiveCallbackStateBytes),
            new ReadOnlySpan<byte>((void*)view.Data, checked((int)view.Length)),
            receivedAt,
            out message);
    }

    private void RpcMessage(nint chat)
    {
        var trackFilteredReceive = false;
        var filteredReceiveSenderKey = 0u;
        var filteredReceiveCategory = 0u;
        var filteredReceiveMetadata = 0u;
        var rawSenderKey = 0u;
        var hasRawSenderKey = false;
        var remoteMemberSlot = -1;
        var localMemberSlot = -1;
        var remoteResolved = false;
        var isLocal = false;
        var useRewrittenPacket = false;
        Span<byte> rewrittenPacket = stackalloc byte[RelinkChatPacketDecoder.PacketBytesToCopy];
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

                if (!PartyMemberSlotMap.IsValidSlot(localMemberSlot))
                    TryResolveLocalSlot(out localMemberSlot);

                remoteResolved = hasRawSenderKey &&
                                 PartyMemberSlotMap.IsValidSlot(remoteMemberSlot);
                var attributionProven = remoteResolved &&
                                        RelinkChatSenderPolicy.CanApplyModeration(
                                            remoteMemberSlot,
                                            localMemberSlot);
                isLocal = attributionProven &&
                          RelinkChatSenderPolicy.IsLocalRpc(remoteMemberSlot, localMemberSlot);
                var participant = default(ChatModerationParticipant);
                if (attributionProven)
                {
                    participant = ResolveModerationParticipant(
                        rawSenderKey,
                        remoteMemberSlot,
                        localMemberSlot,
                        isLocal);
                    if (ShouldBlockModeratedParticipant(participant))
                        return;
                }

                if (RelinkChatPacketDecoder.TryDecode(
                        packet,
                        DateTimeOffset.UtcNow,
                        out var pending))
                {
                    trackFilteredReceive = true;
                    filteredReceiveSenderKey = pending.SenderId;
                    filteredReceiveCategory = pending.Category;
                    filteredReceiveMetadata = pending.Metadata;
                    if (attributionProven)
                    {
                        pending = isLocal
                            ? pending with
                            {
                                Sender = participant.DisplayName,
                                PlayerNumber = 1,
                                IsLocal = true,
                            }
                            : RelinkChatMessageAttribution.ApplyRemoteIdentity(
                                pending,
                                localMemberSlot,
                                remoteMemberSlot,
                                participant.DisplayName);
                        participant = participant with
                        {
                            DisplayName = string.IsNullOrWhiteSpace(participant.DisplayName)
                                ? string.Empty
                                : pending.Sender,
                            PlayerNumber = pending.PlayerNumber,
                            SenderId = pending.SenderId,
                        };
                    }

                    var moderation = attributionProven
                        ? EvaluateModeration(participant, pending)
                        : ChatModerationDecision.Allow(pending.Text);
                    var moderationResult = RelinkIncomingChatModerationPolicy.Apply(
                        packet,
                        rewrittenPacket,
                        pending,
                        moderation);
                    if (moderationResult.Action == RelinkIncomingChatAction.Block)
                    {
                        if (moderation.Disposition != ChatModerationDisposition.Block &&
                            Interlocked.Exchange(ref _moderationRewriteFailureLogged, 1) == 0)
                        {
                            SafeLog(
                                "Incoming chat moderation matched a message but could not safely encode " +
                                "the filtered replacement into Relink's raw-text packet; the message was blocked.");
                        }
                        return;
                    }
                    useRewrittenPacket = moderationResult.Action == RelinkIncomingChatAction.PassRewritten;
                }
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _decodeFailureLogged, 1) == 0)
                SafeLog($"Incoming Relink chat decoding failed; further failures are suppressed: {exception.Message}");
        }

        var pendingReceiveToken = trackFilteredReceive
            ? EnqueuePendingFilteredReceive(
                filteredReceiveSenderKey,
                filteredReceiveCategory,
                filteredReceiveMetadata,
                DateTimeOffset.UtcNow)
            : 0;
        try
        {
            if (useRewrittenPacket)
            {
                fixed (byte* rewritten = rewrittenPacket)
                    _rpcHook!.OriginalFunction((nint)rewritten);
            }
            else
            {
                _rpcHook!.OriginalFunction(chat);
            }
        }
        catch (Exception exception)
        {
            _pendingFilteredReceives.Cancel(pendingReceiveToken);
            SafeLog($"Native rpcMessage hook failed: {exception.Message}");
        }
    }

    private void PublishFilteredIncoming(IncomingChatMessage pending)
    {
        var rawSenderKey = pending.SenderId;
        var remoteMemberSlot = -1;
        var localMemberSlot = -1;
        var remoteResolved = _memberSlotResolver?.TryResolveSlot(rawSenderKey, out remoteMemberSlot) == true &&
                             PartyMemberSlotMap.IsValidSlot(remoteMemberSlot);
        TryResolveLocalSlot(out localMemberSlot);
        var attributionProven = remoteResolved &&
                                RelinkChatSenderPolicy.CanApplyModeration(
                                    remoteMemberSlot,
                                    localMemberSlot);
        var isLocal = attributionProven &&
                      RelinkChatSenderPolicy.IsLocalRpc(remoteMemberSlot, localMemberSlot);

        if (attributionProven)
        {
            var participant = ResolveModerationParticipant(
                rawSenderKey,
                remoteMemberSlot,
                localMemberSlot,
                isLocal);
            pending = isLocal
                ? pending with
                {
                    Sender = participant.DisplayName,
                    PlayerNumber = 1,
                    IsLocal = true,
                }
                : RelinkChatMessageAttribution.ApplyRemoteIdentity(
                    pending,
                    localMemberSlot,
                    remoteMemberSlot,
                    participant.DisplayName);
        }

        LogAttributionDecision(
            hasRawSenderKey: true,
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

        EnqueueIncoming(pending);
    }

    private ChatModerationParticipant ResolveModerationParticipant(
        uint senderId,
        int memberSlot,
        int localMemberSlot,
        bool isLocal)
    {
        var playerNumber = 0;
        string? playerName = null;
        string? entityId = null;

        if (PartyMemberSlotMap.IsValidSlot(memberSlot))
        {
            if (_playerNameResolver?.TryResolveName(memberSlot, senderId, out var resolvedName) == true &&
                !string.IsNullOrWhiteSpace(resolvedName))
            {
                playerName = resolvedName.Trim();
            }

            if (_partyMemberIdentityResolver?.TryResolveSlot(memberSlot, out var resolvedEntityId) == true &&
                !string.IsNullOrWhiteSpace(resolvedEntityId))
            {
                entityId = resolvedEntityId.Trim();
            }
        }

        if (isLocal)
        {
            var identity = ResolveLocalRpcIdentity(memberSlot, senderId);
            playerName = identity.Sender;
            playerNumber = 1;
        }
        else
        {
            _ = PartyMemberSlotMap.TryGetPlayerNumber(
                localMemberSlot,
                memberSlot,
                out playerNumber);
        }

        return new ChatModerationParticipant(
            playerNumber,
            playerName ?? string.Empty,
            entityId,
            senderId,
            isLocal);
    }

    private bool ShouldBlockModeratedParticipant(in ChatModerationParticipant participant)
    {
        if (_chatModeration is null)
            return false;

        try
        {
            _chatModeration.ObserveParticipant(participant);
            return _chatModeration.IsBlocked(participant);
        }
        catch (Exception exception)
        {
            LogModerationFailure(exception);
            return false;
        }
    }

    private ChatModerationDecision EvaluateModeration(
        in ChatModerationParticipant participant,
        in IncomingChatMessage message)
    {
        if (_chatModeration is null)
            return ChatModerationDecision.Allow(message.Text);

        try
        {
            return _chatModeration.Evaluate(new ChatModerationInput(
                participant,
                message.Text,
                message.ReceivedAt,
                message.CommunicationCue));
        }
        catch (Exception exception)
        {
            LogModerationFailure(exception);
            return ChatModerationDecision.Allow(message.Text);
        }
    }

    private void LogModerationFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _moderationFailureLogged, 1) != 0)
            return;

        SafeLog(
            $"Incoming chat moderation failed open; further failures are suppressed: " +
            $"{exception.GetType().Name}: {exception.Message}");
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
                "Relink local chat history is synchronized with the native WordFilter completion callback; " +
                "later authoritative RPC echoes are identity-only and deduplicated.");
        }
    }

    private void ClearFilterPipelineState()
    {
        lock (_filterPipelineSync)
        {
            _pendingFilteredSends.Clear();
            _pendingFilteredReceives.Clear();
            _echoSuppressor.Clear();
            Interlocked.Increment(ref _filterPipelineGeneration);
        }
    }

    private long EnqueuePendingFilteredSend(
        string text,
        LocalChatIdentity identity,
        ChatCommunicationCue communicationCue,
        DateTimeOffset now)
    {
        lock (_filterPipelineSync)
        {
            if (!IsInitialized || Volatile.Read(ref _suspended))
                return 0;
            return _pendingFilteredSends.Enqueue(text, identity, communicationCue, now);
        }
    }

    private long EnqueuePendingFilteredReceive(
        uint senderKey,
        uint category,
        uint metadata,
        DateTimeOffset now)
    {
        lock (_filterPipelineSync)
        {
            if (!IsInitialized || Volatile.Read(ref _suspended))
                return 0;
            return _pendingFilteredReceives.Enqueue(senderKey, category, metadata, now);
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
    private delegate void WordFilterCallbackDelegate(
        nint callbackState,
        nint unused,
        nint filteredTextView);

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
