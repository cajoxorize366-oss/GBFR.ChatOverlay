using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal readonly record struct PartyPlayerMuteSlotStatus(
    int PlayerNumber,
    bool IsAvailable,
    bool IsMuted,
    string Detail)
{
    internal static IReadOnlyList<PartyPlayerMuteSlotStatus> Unavailable(string detail) =>
        Enumerable.Range(2, 3)
            .Select(player => new PartyPlayerMuteSlotStatus(player, false, false, detail))
            .ToArray();
}

internal readonly record struct PartyPlayerMuteOperationResult(bool Succeeded, string Message);

/// <summary>
/// Correlates Relink's fixed party slots with Party ChatControls by their exact EntityId.
/// Native ChatControl pointers are retained only between joined/left lifecycle notifications.
/// </summary>
internal sealed class PartyPlayerMuteController
{
    private const uint Success = 0;
    private const long RefreshIntervalMilliseconds = 250;

    private readonly IPartyChatControlApi _api;
    private readonly IRelinkPartyMemberIdentitySnapshotResolver _identityResolver;
    private readonly Action<string> _log;
    private readonly object _sync = new();
    private readonly HashSet<nint> _localChatControls = [];
    private readonly Dictionary<nint, string> _remoteEntityByChatControl = [];
    private readonly Dictionary<string, HashSet<nint>> _remoteChatControlsByEntity =
        new(StringComparer.Ordinal);
    private readonly Dictionary<nint, nint> _pendingJoinedNetworkByChatControl = [];
    private readonly Dictionary<string, MuteReadback> _muteReadbacks =
        new(StringComparer.Ordinal);

    private nint _network;
    private nint _stateBatchManager;
    private bool _stateBatchActive;
    private long _generation;

    internal PartyPlayerMuteController(
        IPartyChatControlApi api,
        IRelinkPartyMemberIdentitySnapshotResolver identityResolver,
        Action<string> log)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal IReadOnlyList<PartyPlayerMuteSlotStatus> GetSnapshot()
    {
        if (!_identityResolver.TryResolveCoherentSnapshot(out var snapshot) ||
            !PartyRoomIdentitySnapshotResolver.TryNormalizeSnapshot(
                snapshot.EntityIds,
                snapshot.LocalMemberSlot,
                out _))
        {
            return PartyPlayerMuteSlotStatus.Unavailable(
                "Party 身份快照不可用。 / Party identity snapshot is unavailable.");
        }

        lock (_sync)
        {
            var result = new PartyPlayerMuteSlotStatus[3];
            for (var index = 0; index < result.Length; index++)
            {
                var playerNumber = index + 2;
                var remoteOrdinal = index + 1;
                if (!PartyMemberSlotMap.TryGetActualSlot(
                        snapshot.LocalMemberSlot,
                        remoteOrdinal,
                        out var actualSlot))
                {
                    result[index] = new PartyPlayerMuteSlotStatus(
                        playerNumber,
                        false,
                        false,
                        "Party 玩家映射不可用。 / Party player mapping is unavailable.");
                    continue;
                }

                result[index] = CreateStatusLocked(playerNumber, snapshot.EntityIds[actualSlot]);
            }
            return result;
        }
    }

    internal PartyPlayerMuteOperationResult SetPlayerMuted(int playerNumber, bool muted)
    {
        if (playerNumber is < 2 or > 4)
        {
            return new PartyPlayerMuteOperationResult(
                false,
                "无效的玩家槽位。 / Invalid player slot.");
        }
        var remoteOrdinal = playerNumber - 1;
        if (!_identityResolver.TryResolveCoherentSnapshot(out var snapshot) ||
            !PartyRoomIdentitySnapshotResolver.TryNormalizeSnapshot(
                snapshot.EntityIds,
                snapshot.LocalMemberSlot,
                out _) ||
            !PartyMemberSlotMap.TryGetActualSlot(
                snapshot.LocalMemberSlot,
                remoteOrdinal,
                out var actualSlot) ||
            string.IsNullOrEmpty(snapshot.EntityIds[actualSlot]))
        {
            return new PartyPlayerMuteOperationResult(
                false,
                $"玩家 {playerNumber} 的游戏身份尚未就绪。 / Player {playerNumber} identity is not ready.");
        }

        var entityId = snapshot.EntityIds[actualSlot];

        lock (_sync)
        {
            if (_stateBatchActive)
            {
                return new PartyPlayerMuteOperationResult(
                    false,
                    "Party 正在处理状态更新，请稍后重试。 / Party is processing state changes; try again shortly.");
            }
            if (!TryGetPairsLocked(entityId, out var localControls, out var targetControls, out var unavailable))
                return new PartyPlayerMuteOperationResult(false, unavailable);

            uint firstFailure = Success;
            try
            {
                foreach (var localChatControl in localControls)
                {
                    foreach (var targetChatControl in targetControls)
                    {
                        var setResult = _api.SetIncomingAudioMuted(
                            localChatControl,
                            targetChatControl,
                            muted);
                        if (setResult != Success && firstFailure == Success)
                            firstFailure = setResult;
                    }
                }
            }
            catch (Exception exception)
            {
                InvalidateReadbacksLocked();
                LogSafely(
                    $"Player {playerNumber} incoming-audio mute failed with " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return new PartyPlayerMuteOperationResult(
                    false,
                    $"玩家 {playerNumber} 禁言操作失败。 / Player {playerNumber} mute operation failed.");
            }

            InvalidateReadbacksLocked();
            if (firstFailure != Success)
            {
                LogSafely(
                    $"Player {playerNumber} incoming-audio mute returned 0x{firstFailure:X8}.");
                return new PartyPlayerMuteOperationResult(
                    false,
                    $"玩家 {playerNumber} 禁言操作失败（0x{firstFailure:X8}）。 / " +
                    $"Player {playerNumber} mute operation failed (0x{firstFailure:X8}).");
            }

            if (!TryReadMuteStateLocked(entityId, forceRefresh: true, out var observedMuted) ||
                observedMuted != muted)
            {
                return new PartyPlayerMuteOperationResult(
                    false,
                    $"玩家 {playerNumber} 的 Party 回读未确认更改。 / " +
                    $"Party readback did not confirm the change for Player {playerNumber}.");
            }

            LogSafely(
                $"Player {playerNumber} incoming Party audio {(muted ? "muted" : "unmuted")} " +
                "after exact Relink-slot/EntityId correlation.");
            return new PartyPlayerMuteOperationResult(
                true,
                muted
                    ? $"已禁言玩家 {playerNumber}。 / Player {playerNumber} muted."
                    : $"已取消禁言玩家 {playerNumber}。 / Player {playerNumber} unmuted.");
        }
    }

    internal void Observe(PartyStateChangeSnapshot state)
    {
        lock (_sync)
        {
            switch ((PartyStateChangeType)state.Type)
            {
                case PartyStateChangeType.ChatControlJoinedNetwork:
                    QueueJoinedLocked(state.Network, state.ChatControl);
                    break;
                case PartyStateChangeType.ChatControlLeftNetwork:
                    if (_network == state.Network)
                    {
                        _pendingJoinedNetworkByChatControl.Remove(state.ChatControl);
                        RemoveChatControlLocked(state.ChatControl);
                    }
                    break;
                case PartyStateChangeType.ChatControlDestroyed:
                    _pendingJoinedNetworkByChatControl.Remove(state.ChatControl);
                    RemoveChatControlLocked(state.ChatControl);
                    break;
                case PartyStateChangeType.NetworkDestroyed:
                    if (_network == state.Network)
                        ResetLocked();
                    break;
            }
        }
    }

    internal void BeginStateChangeBatch(nint manager)
    {
        lock (_sync)
        {
            if (_stateBatchActive && _stateBatchManager != manager)
                ResetLocked();
            _stateBatchActive = true;
            _stateBatchManager = manager;
        }
    }

    internal void CancelStateChangeBatch(nint manager)
    {
        lock (_sync)
        {
            if (!_stateBatchActive || (_stateBatchManager != nint.Zero && _stateBatchManager != manager))
                return;
            _stateBatchActive = false;
            _stateBatchManager = nint.Zero;
            _pendingJoinedNetworkByChatControl.Clear();
        }
    }

    internal void OnBatchFinished(nint manager)
    {
        lock (_sync)
        {
            if (!_stateBatchActive || _stateBatchManager != manager)
            {
                ResetLocked();
                return;
            }

            _stateBatchActive = false;
            _stateBatchManager = nint.Zero;
            var joined = _pendingJoinedNetworkByChatControl.ToArray();
            _pendingJoinedNetworkByChatControl.Clear();
            foreach (var pair in joined)
                ClassifyJoinedLocked(pair.Value, pair.Key);
        }
    }

    internal void PrepareForNetworkLeave(nint network)
    {
        lock (_sync)
        {
            if (network == nint.Zero || _network == network)
                ResetLocked();
        }
    }

    internal void Reset()
    {
        lock (_sync)
            ResetLocked();
    }

    private PartyPlayerMuteSlotStatus CreateStatusLocked(int playerNumber, string? entityId)
    {
        if (string.IsNullOrEmpty(entityId))
        {
            return new PartyPlayerMuteSlotStatus(
                playerNumber,
                false,
                false,
                "该游戏槽位当前没有可验证的成员身份。 / No verified game identity is present in this slot.");
        }
        if (_network == nint.Zero || _localChatControls.Count == 0)
        {
            return new PartyPlayerMuteSlotStatus(
                playerNumber,
                false,
                false,
                "正在等待本地 Party 语音控制加入房间。 / Waiting for the local Party ChatControl.");
        }
        if (!_remoteChatControlsByEntity.TryGetValue(entityId, out var targets) || targets.Count == 0)
        {
            return new PartyPlayerMuteSlotStatus(
                playerNumber,
                false,
                false,
                "游戏槽位已识别，正在等待对应的 Party 语音身份。 / Waiting for the matching Party voice identity.");
        }
        if (!TryReadMuteStateLocked(entityId, forceRefresh: false, out var muted))
        {
            return new PartyPlayerMuteSlotStatus(
                playerNumber,
                false,
                false,
                "EntityId 已精确匹配，但 Party 禁言状态回读失败。 / EntityId matched, but Party mute readback failed.");
        }

        return new PartyPlayerMuteSlotStatus(
            playerNumber,
            true,
            muted,
            muted
                ? "EntityId 已精确匹配；当前已禁言。 / Exact EntityId match; currently muted."
                : "EntityId 已精确匹配；当前可以听见。 / Exact EntityId match; currently audible.");
    }

    private void QueueJoinedLocked(nint network, nint chatControl)
    {
        if (network == nint.Zero || chatControl == nint.Zero)
            return;
        if (_network == nint.Zero)
            _network = network;
        if (_network != network)
            return;

        _pendingJoinedNetworkByChatControl[chatControl] = network;
    }

    private void ClassifyJoinedLocked(nint network, nint chatControl)
    {
        if (network == nint.Zero || chatControl == nint.Zero || _network != network)
            return;

        try
        {
            var localityResult = _api.IsLocal(chatControl, out var isLocal);
            if (localityResult != Success)
            {
                LogSafely($"PartyChatControlIsLocal returned 0x{localityResult:X8}; joined control ignored.");
                return;
            }

            if (isLocal)
            {
                // Locality is immutable for a live Party object, but remove any stale remote
                // classification before accepting a repeated/reordered lifecycle notification.
                RemoveChatControlLocked(chatControl);
                if (_localChatControls.Add(chatControl))
                    InvalidateReadbacksLocked();
                return;
            }

            var entityResult = _api.GetEntityId(chatControl, out var entityId);
            if (entityResult != Success || string.IsNullOrWhiteSpace(entityId))
            {
                LogSafely($"PartyChatControlGetEntityId returned 0x{entityResult:X8}; remote control ignored.");
                return;
            }

            RemoveChatControlLocked(chatControl);
            _remoteEntityByChatControl[chatControl] = entityId;
            if (!_remoteChatControlsByEntity.TryGetValue(entityId, out var controls))
            {
                controls = [];
                _remoteChatControlsByEntity[entityId] = controls;
            }
            controls.Add(chatControl);
            InvalidateReadbacksLocked();
        }
        catch (Exception exception)
        {
            LogSafely(
                $"Party ChatControl identity inspection failed with " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private bool TryReadMuteStateLocked(string entityId, bool forceRefresh, out bool muted)
    {
        muted = false;
        var now = Environment.TickCount64;
        if (_stateBatchActive)
        {
            if (_muteReadbacks.TryGetValue(entityId, out var batchCached) &&
                batchCached.Generation == _generation &&
                batchCached.Succeeded)
            {
                muted = batchCached.Muted;
                return true;
            }
            return false;
        }
        if (!forceRefresh &&
            _muteReadbacks.TryGetValue(entityId, out var cached) &&
            cached.Generation == _generation &&
            now - cached.Timestamp < RefreshIntervalMilliseconds)
        {
            muted = cached.Muted;
            return cached.Succeeded;
        }

        if (!TryGetPairsLocked(entityId, out var localControls, out var targetControls, out _))
        {
            _muteReadbacks[entityId] = new MuteReadback(_generation, now, false, false);
            return false;
        }

        var allMuted = true;
        try
        {
            foreach (var localChatControl in localControls)
            {
                foreach (var targetChatControl in targetControls)
                {
                    var readResult = _api.GetIncomingAudioMuted(
                        localChatControl,
                        targetChatControl,
                        out var pairMuted);
                    if (readResult != Success)
                    {
                        _muteReadbacks[entityId] = new MuteReadback(_generation, now, false, false);
                        return false;
                    }
                    allMuted &= pairMuted;
                }
            }
        }
        catch
        {
            _muteReadbacks[entityId] = new MuteReadback(_generation, now, false, false);
            return false;
        }

        muted = allMuted;
        _muteReadbacks[entityId] = new MuteReadback(_generation, now, true, muted);
        return true;
    }

    private bool TryGetPairsLocked(
        string entityId,
        out nint[] localControls,
        out nint[] targetControls,
        out string unavailable)
    {
        localControls = _localChatControls.ToArray();
        targetControls = _remoteChatControlsByEntity.TryGetValue(entityId, out var targets)
            ? targets.ToArray()
            : [];
        if (_network != nint.Zero && localControls.Length != 0 && targetControls.Length != 0)
        {
            unavailable = string.Empty;
            return true;
        }

        unavailable =
            "对应的本地或远端 Party 语音控制尚未就绪。 / " +
            "The matching local or remote Party ChatControl is not ready.";
        return false;
    }

    private void RemoveChatControlLocked(nint chatControl)
    {
        var changed = _localChatControls.Remove(chatControl);
        if (_remoteEntityByChatControl.Remove(chatControl, out var entityId))
        {
            changed = true;
            if (_remoteChatControlsByEntity.TryGetValue(entityId, out var controls))
            {
                controls.Remove(chatControl);
                if (controls.Count == 0)
                    _remoteChatControlsByEntity.Remove(entityId);
            }
        }
        if (changed)
            InvalidateReadbacksLocked();
    }

    private void ResetLocked()
    {
        _network = nint.Zero;
        _localChatControls.Clear();
        _remoteEntityByChatControl.Clear();
        _remoteChatControlsByEntity.Clear();
        _pendingJoinedNetworkByChatControl.Clear();
        _stateBatchManager = nint.Zero;
        _stateBatchActive = false;
        InvalidateReadbacksLocked();
    }

    private void InvalidateReadbacksLocked()
    {
        _generation++;
        _muteReadbacks.Clear();
    }

    private void LogSafely(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Party lifecycle and UI callbacks must never fail because a logger failed.
        }
    }

    private readonly record struct MuteReadback(
        long Generation,
        long Timestamp,
        bool Succeeded,
        bool Muted);
}
