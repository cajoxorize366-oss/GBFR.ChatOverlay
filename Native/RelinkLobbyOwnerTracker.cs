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
    private const long HostSlotRefreshIntervalMilliseconds = 500;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static ReadOnlySpan<byte> ExpectedImportThunk => [0xFF, 0x25, 0x42, 0x49, 0x91, 0x01];

    private readonly ReloadedHooksApi _hooks;
    private readonly IRelinkPartyMemberIdentitySnapshotResolver _identityResolver;
    private readonly IRelinkMemoryReader _memory;
    private readonly Action<string> _log;
    private readonly object _lifecycleSync = new();
    private readonly object _stateSync = new();

    private IHook<PFLobbyGetOwnerDelegate>? _ownerHook;
    private string? _ownerEntityId;
    private int _hostPlayerNumber;
    private long _nextHostSlotRefreshMilliseconds;
    private int _initialized;
    private int _suspended;

    internal RelinkLobbyOwnerTracker(
        ReloadedHooksApi hooks,
        IRelinkPartyMemberIdentitySnapshotResolver identityResolver,
        IRelinkMemoryReader memory,
        Action<string> log)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
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
                "Relink PFLobbyGetOwner import thunk did not match the verified 2.0.3 build.");
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
        string? ownerEntityId;
        var now = Environment.TickCount64;
        lock (_stateSync)
        {
            if (Volatile.Read(ref _initialized) == 0 ||
                Volatile.Read(ref _suspended) != 0 ||
                string.IsNullOrEmpty(_ownerEntityId))
            {
                playerNumber = 0;
                return false;
            }

            if (now < _nextHostSlotRefreshMilliseconds)
            {
                if (_hostPlayerNumber is >= 1 and <= 4)
                {
                    playerNumber = _hostPlayerNumber;
                    return true;
                }

                playerNumber = 0;
                return false;
            }

            ownerEntityId = _ownerEntityId;
        }

        if (!_identityResolver.TryResolveSnapshot(out var memberEntityIds) ||
            !PartyHostSlotResolver.TryResolvePlayerNumber(
                ownerEntityId,
                memberEntityIds,
                out playerNumber))
        {
            lock (_stateSync)
            {
                if (string.Equals(_ownerEntityId, ownerEntityId, StringComparison.Ordinal))
                {
                    _hostPlayerNumber = 0;
                    _nextHostSlotRefreshMilliseconds = now + HostSlotRefreshIntervalMilliseconds;
                }
            }
            playerNumber = 0;
            return false;
        }

        lock (_stateSync)
        {
            if (Volatile.Read(ref _suspended) != 0 ||
                !string.Equals(_ownerEntityId, ownerEntityId, StringComparison.Ordinal))
            {
                playerNumber = 0;
                return false;
            }

            _hostPlayerNumber = playerNumber;
            _nextHostSlotRefreshMilliseconds = now + HostSlotRefreshIntervalMilliseconds;
            return true;
        }
    }

    internal void Reset()
    {
        lock (_stateSync)
        {
            _ownerEntityId = null;
            _hostPlayerNumber = 0;
            _nextHostSlotRefreshMilliseconds = 0;
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
            Reset();
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
                Reset();
                return result;
            }

            CaptureOwnerEntityId(entityId);
        }
        catch (Exception exception)
        {
            Reset();
            SafeLog(
                $"Relink lobby-owner capture failed closed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }

        return result;
    }

    private void CaptureOwnerEntityId(string? entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId) ||
            entityId.Length > MaximumEntityIdBytes ||
            entityId.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            Reset();
            return;
        }

        lock (_stateSync)
        {
            if (string.Equals(_ownerEntityId, entityId, StringComparison.Ordinal))
                return;
            _ownerEntityId = entityId;
            _hostPlayerNumber = 0;
            _nextHostSlotRefreshMilliseconds = 0;
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFLobbyGetOwnerDelegate(nint lobby, nint ownerOutput);
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
