using System.Runtime.InteropServices;
using System.Text;
using Reloaded.Hooks.Definitions;
using ReloadedHooksApi = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace GBFR.ChatOverlay.Native;

/// <summary>
/// Captures PlayFab's current lobby-owner EntityId from Relink's verified
/// PFLobbyGetOwner import thunk and maps it to the same four-slot
/// member_entity_id table used by the game.
/// </summary>
internal sealed class RelinkLobbyOwnerTracker
{
    internal const int MaximumEntityIdBytes = 512;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static ReadOnlySpan<byte> ExpectedImportThunk => [0xFF, 0x25, 0x42, 0x4C, 0x91, 0x01];

    private readonly ReloadedHooksApi _hooks;
    private readonly IRelinkPartyMemberIdentitySnapshotResolver _identityResolver;
    private readonly IRelinkMemoryReader _memory;
    private readonly Action<string> _log;
    private readonly Func<PartyNetworkLocalRole> _networkRoleReader;
    private readonly object _lifecycleSync = new();
    private readonly object _stateSync = new();

    private IHook<PFLobbyGetOwnerDelegate>? _ownerHook;
    private readonly PartyLobbyOwnerBinding _binding = new();
    private int _initialized;
    private int _suspended;
    private PartyNetworkLocalRole _lastLoggedRole = (PartyNetworkLocalRole)(-1);
    private int _lastLoggedHostPlayerNumber = -1;

    internal RelinkLobbyOwnerTracker(
        ReloadedHooksApi hooks,
        IRelinkPartyMemberIdentitySnapshotResolver identityResolver,
        IRelinkMemoryReader memory,
        Func<PartyNetworkLocalRole>? networkRoleReader,
        Action<string> log)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _networkRoleReader = networkRoleReader ?? (() => PartyNetworkLocalRole.Unknown);
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal RelinkLobbyOwnerTracker(
        ReloadedHooksApi hooks,
        IRelinkPartyMemberIdentitySnapshotResolver identityResolver,
        IRelinkMemoryReader memory,
        Action<string> log)
        : this(hooks, identityResolver, memory, null, log)
    {
    }

    internal void Initialize(nint moduleBase, RelinkChatRvas rvas)
    {
        if (moduleBase == nint.Zero || rvas.LobbyOwnerImportThunk <= 0)
            throw new InvalidOperationException("Relink lobby-owner RVA is unavailable.");

        Span<byte> importThunk = stackalloc byte[6];
        if (!_memory.TryReadBytes(moduleBase + rvas.LobbyOwnerImportThunk, importThunk) ||
            !importThunk.SequenceEqual(ExpectedImportThunk))
        {
            throw new InvalidDataException(
                "Relink PFLobbyGetOwner import thunk did not match the verified 2.0.4 build.");
        }

        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _initialized) != 0)
                return;

            try
            {
                _ownerHook = _hooks.CreateHook<PFLobbyGetOwnerDelegate>(
                    PFLobbyGetOwner,
                    moduleBase + rvas.LobbyOwnerImportThunk);
                _ownerHook.Activate();
                Volatile.Write(ref _initialized, 1);
                _log(
                    $"Relink PlayFab lobby-owner tracker attached: getOwner=0x" +
                    $"{(nuint)(moduleBase + rvas.LobbyOwnerImportThunk):X}.");
            }
            catch
            {
                _ownerHook?.Disable();
                _ownerHook = null;
                Reset();
                throw;
            }
        }
    }

    internal bool TryGetHostPlayerNumber(out int playerNumber)
    {
        if (Volatile.Read(ref _initialized) == 0 ||
            Volatile.Read(ref _suspended) != 0)
        {
            playerNumber = 0;
            return false;
        }

        // Party room transitions can read room identity while holding their own state lock.
        // Snapshot the role before taking _stateSync so every path keeps Party -> owner lock order.
        var role = ReadNetworkRoleSafely();
        lock (_stateSync)
        {
            if (Volatile.Read(ref _initialized) == 0 ||
                Volatile.Read(ref _suspended) != 0)
            {
                playerNumber = 0;
                return false;
            }

            try
            {
                playerNumber = 0;
                var resolved = RelinkLobbyOwnerHostRefresh.TryRefreshHostPlayerNumber(
                    _identityResolver,
                    _binding,
                    role,
                    out playerNumber);
                LogHostResolutionIfChangedLocked(role, resolved ? playerNumber : 0);
                return resolved;
            }
            catch (Exception exception)
            {
                playerNumber = 0;
                SafeLog(
                    $"Relink lobby-owner host slot refresh failed closed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }
    }

    internal bool TryGetRoomIdentitySnapshot(out PartyRoomIdentitySnapshot snapshot)
    {
        snapshot = default;
        if (Volatile.Read(ref _initialized) == 0 ||
            Volatile.Read(ref _suspended) != 0)
        {
            return false;
        }

        // Keep the same lock order as TryGetHostPlayerNumber and PartyRoomSessionTracker.
        var role = ReadNetworkRoleSafely();
        lock (_stateSync)
        {
            if (Volatile.Read(ref _initialized) == 0 ||
                Volatile.Read(ref _suspended) != 0)
            {
                return false;
            }

            try
            {
                if (!_identityResolver.TryResolveCoherentSnapshot(out var identitySnapshot))
                {
                    snapshot = new PartyRoomIdentitySnapshot(null, PartyRoomHostState.Unknown);
                    return true;
                }

                snapshot = _binding.ResolveSnapshot(identitySnapshot, role);
                return true;
            }
            catch (Exception exception)
            {
                snapshot = default;
                SafeLog(
                    $"Relink lobby-owner snapshot failed closed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }
    }

    internal void CacheRoomName(string? roomName)
    {
        lock (_stateSync)
        {
            _binding.CacheRoomName(roomName);
        }
    }

    internal void Reset()
    {
        lock (_stateSync)
        {
            _binding.Reset();
            _lastLoggedRole = (PartyNetworkLocalRole)(-1);
            _lastLoggedHostPlayerNumber = -1;
        }
    }

    internal void Suspend()
    {
        lock (_lifecycleSync)
        {
            Volatile.Write(ref _suspended, 1);
            _ownerHook?.Disable();
            Reset();
        }
    }

    internal void Resume()
    {
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _initialized) == 0)
                return;
            _ownerHook?.Enable();
            Volatile.Write(ref _suspended, 0);
        }
    }

    internal void Disable()
    {
        lock (_lifecycleSync)
        {
            Volatile.Write(ref _suspended, 1);
            _ownerHook?.Disable();
            _ownerHook = null;
            Volatile.Write(ref _initialized, 0);
            Reset();
        }
    }

    private int PFLobbyGetOwner(nint lobby, nint ownerOutput)
    {
        int result;
        try
        {
            result = _ownerHook!.OriginalFunction(lobby, ownerOutput);
        }
        catch (Exception exception)
        {
            ObserveOwnerCandidate(lobby, null);
            SafeLog(
                $"Relink PFLobbyGetOwner original call failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return unchecked((int)0x80004005);
        }

        if (Volatile.Read(ref _suspended) != 0)
            return result;

        try
        {
            if (result < 0 ||
                !TryReadOwnerEntityId(_memory, ownerOutput, out var entityId))
            {
                ObserveOwnerCandidate(lobby, null);
                return result;
            }

            ObserveOwnerCandidate(lobby, entityId);
        }
        catch (Exception exception)
        {
            ObserveOwnerCandidate(lobby, null);
            SafeLog(
                $"Relink lobby-owner capture failed closed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }

        return result;
    }

    private void ObserveOwnerCandidate(nint lobby, string? ownerEntityId)
    {
        try
        {
            lock (_stateSync)
            {
                _binding.ObserveOwner(lobby, ownerEntityId);
            }
        }
        catch (Exception exception)
        {
            SafeLog(
                $"Relink lobby-owner candidate update failed closed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// PFLobbyGetOwner writes a library-owned <c>const PFEntityKey*</c> into the
    /// caller's <c>const PFEntityKey**</c> output. PFEntityKey's first field is
    /// its null-terminated UTF-8 <c>id</c> pointer.
    /// </summary>
    internal static bool TryReadOwnerEntityId(
        IRelinkMemoryReader memory,
        nint ownerOutput,
        out string value)
    {
        ArgumentNullException.ThrowIfNull(memory);
        value = string.Empty;
        return ownerOutput != nint.Zero &&
               memory.TryReadPointer(ownerOutput, out var owner) &&
               owner != nint.Zero &&
               memory.TryReadPointer(owner, out var ownerId) &&
               TryReadNullTerminatedUtf8(memory, ownerId, out value);
    }

    private static bool TryReadNullTerminatedUtf8(
        IRelinkMemoryReader memory,
        nint address,
        out string value)
    {
        value = string.Empty;
        if (address == nint.Zero)
            return false;

        Span<byte> encoded = stackalloc byte[MaximumEntityIdBytes];
        Span<byte> single = stackalloc byte[1];
        var length = 0;
        for (; length < encoded.Length; length++)
        {
            if (!memory.TryReadBytes(address + length, single))
                return false;
            if (single[0] == 0)
                break;
            encoded[length] = single[0];
        }

        if (length == 0 || length == encoded.Length)
            return false;

        try
        {
            value = StrictUtf8.GetString(encoded[..length]);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['\0', '\r', '\n']) < 0;
    }

    private void SafeLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Never allow a logger failure to escape a native callback.
        }
    }

    private PartyNetworkLocalRole ReadNetworkRoleSafely()
    {
        try
        {
            return _networkRoleReader.Invoke();
        }
        catch
        {
            return PartyNetworkLocalRole.Unknown;
        }
    }

    private void LogHostResolutionIfChangedLocked(
        PartyNetworkLocalRole role,
        int hostPlayerNumber)
    {
        if (_lastLoggedRole == role && _lastLoggedHostPlayerNumber == hostPlayerNumber)
            return;

        _lastLoggedRole = role;
        _lastLoggedHostPlayerNumber = hostPlayerNumber;
        SafeLog(
            $"Relink host identity changed: local_role={role}, " +
            $"host_ui_player={(hostPlayerNumber is >= 1 and <= 4 ? hostPlayerNumber : 0)}.");
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFLobbyGetOwnerDelegate(nint lobby, nint ownerOutput);
}

internal static class RelinkLobbyOwnerHostRefresh
{
    internal static bool TryRefreshHostPlayerNumber(
        IRelinkPartyMemberIdentitySnapshotResolver identityResolver,
        PartyLobbyOwnerBinding binding,
        PartyNetworkLocalRole role,
        out int playerNumber)
    {
        playerNumber = 0;
        if (identityResolver is null ||
            binding is null ||
            !identityResolver.TryResolveCoherentSnapshot(out var snapshot))
        {
            return false;
        }

        return binding.TryResolveHostPlayerNumber(snapshot, role, out playerNumber);
    }

    internal static bool TryRefreshHostPlayerNumber(
        IRelinkPartyMemberIdentitySnapshotResolver identityResolver,
        PartyLobbyOwnerBinding binding,
        out int playerNumber) =>
        TryRefreshHostPlayerNumber(
            identityResolver,
            binding,
            PartyNetworkLocalRole.Unknown,
            out playerNumber);
}

internal static class PartyHostSlotResolver
{
    internal static bool TryResolvePlayerNumber(
        string? ownerEntityId,
        IReadOnlyList<string>? memberEntityIds,
        out int playerNumber)
    {
        playerNumber = 0;
        if (string.IsNullOrWhiteSpace(ownerEntityId) || memberEntityIds is null || memberEntityIds.Count != 4)
            return false;

        var match = 0;
        for (var index = 0; index < memberEntityIds.Count; index++)
        {
            var candidate = memberEntityIds[index];
            if (candidate.Length == 0)
                continue;
            if (string.IsNullOrWhiteSpace(candidate))
                return false;
            if (!string.Equals(candidate, ownerEntityId, StringComparison.Ordinal))
                continue;
            if (match != 0)
                return false;
            match = index + 1;
        }

        if (match == 0)
            return false;
        playerNumber = match;
        return true;
    }
}
