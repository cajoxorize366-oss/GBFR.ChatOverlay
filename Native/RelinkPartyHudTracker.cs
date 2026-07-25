using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using ReloadedHooksApi = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace GBFR.ChatOverlay.Native;

internal enum PartyHudLayout
{
    OnlineLobby,
    Battle,
}

internal readonly record struct PartyHudAnchor(
    int SlotIndex,
    bool IsLocal,
    PartyHudLayout Layout,
    float CenterX,
    float CenterY,
    float IconSize);

internal sealed class RelinkPartyHudTracker
{
    private const int TownVtableRva = 0x05A53978;
    private const int BattleVtableRva = 0x05A62E28;
    private const int FactoryResultControllerOffset = 0x18;
    private const int ObjectFinalTransformOffset = 0x120;
    private const int ObjectSizeOffset = 0x1BC;
    private const int ObjectActiveOffset = 0x1D0;
    private const int TownSlotOffset = 0x340;
    private const int BattleTypeOffset = 0x1A0;
    private const float NativeIconSize = 36.0f;
    private const float NativeRightEdgeGap = 18.0f;

    private static readonly int[] TownTargetPointerOffsets = [0x1B8, 0x230];
    // ControllerPlParameter01 stores Root/Name at 0x160/0x180, then its Type
    // integer at 0x1A0 before the remaining 0x20-byte object refs. Therefore
    // HpGauge01/HpGauge02 resolve at 0x370/0x390. The old 0x3B0/0x3D0 pair was
    // actually HpGaugeMask/HpGaugeEff01, which anchored beside the name and
    // could expose an effect transform with a wildly oversized scale.
    private static readonly int[] BattleTargetPointerOffsets = [0x370, 0x390];

    private readonly ReloadedHooksApi _hooks;
    private readonly Action<string> _log;
    private readonly IRelinkMemoryReader _memoryReader;
    private readonly object _lifecycleSync = new();
    private readonly ConcurrentDictionary<nint, PartyHudLayout> _controllers = new();

    private IHook<HudFactoryDelegate>? _townFactoryHook;
    private IHook<HudDestructorDelegate>? _townDestructorHook;
    private IHook<HudFactoryDelegate>? _battleFactoryHook;
    private IHook<HudDestructorDelegate>? _battleDestructorHook;
    private nint _moduleBase;
    private nint _townVtable;
    private nint _battleVtable;
    private bool _initialized;
    private bool _suspended;
    private int _firstAnchorLogged;
    private int _projectionFailureLogged;

    internal RelinkPartyHudTracker(
        ReloadedHooksApi hooks,
        Action<string> log,
        IRelinkMemoryReader? memoryReader = null)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _memoryReader = memoryReader ?? new CurrentProcessRelinkMemoryReader();
    }

    internal bool IsInitialized => Volatile.Read(ref _initialized);

    internal static ReadOnlySpan<int> BattleAnchorPointerOffsets => BattleTargetPointerOffsets;

    internal void Initialize()
    {
        lock (_lifecycleSync)
        {
            if (_initialized)
                return;

            using var process = Process.GetCurrentProcess();
            var mainModule = process.MainModule ??
                throw new InvalidOperationException("The game module is unavailable.");
            var rvas = RelinkHudBuildLocator.Resolve(mainModule.FileName);
            _moduleBase = mainModule.BaseAddress;
            _townVtable = _moduleBase + TownVtableRva;
            _battleVtable = _moduleBase + BattleVtableRva;

            try
            {
                _townFactoryHook = _hooks.CreateHook<HudFactoryDelegate>(
                    TownFactory,
                    _moduleBase + rvas.TownFactory);
                _townFactoryHook.Activate();
                _townDestructorHook = _hooks.CreateHook<HudDestructorDelegate>(
                    TownDestructor,
                    _moduleBase + rvas.TownDestructor);
                _townDestructorHook.Activate();
                _battleFactoryHook = _hooks.CreateHook<HudFactoryDelegate>(
                    BattleFactory,
                    _moduleBase + rvas.BattleFactory);
                _battleFactoryHook.Activate();
                _battleDestructorHook = _hooks.CreateHook<HudDestructorDelegate>(
                    BattleDestructor,
                    _moduleBase + rvas.BattleDestructor);
                _battleDestructorHook.Activate();

                Volatile.Write(ref _initialized, true);
                SafeLog(
                    "Relink 2.0.2 native party-HUD tracker attached; lobby/battle mode, " +
                    "resolution, aspect ratio and HUD scale now follow the game's live UI node transforms.");
            }
            catch
            {
                DisableHooks();
                ClearHooks();
                _controllers.Clear();
                _moduleBase = nint.Zero;
                _townVtable = nint.Zero;
                _battleVtable = nint.Zero;
                throw;
            }
        }
    }

    internal IReadOnlyList<PartyHudAnchor> GetAnchors(
        float viewportX,
        float viewportY,
        float viewportWidth,
        float viewportHeight)
    {
        if (!IsInitialized || Volatile.Read(ref _suspended))
            return Array.Empty<PartyHudAnchor>();

        var candidates = new List<AnchorCandidate>(4);
        var sawProjectionFailure = false;
        foreach (var entry in _controllers)
        {
            if (!TryCreateAnchor(
                    entry.Key,
                    entry.Value,
                    viewportX,
                    viewportY,
                    viewportWidth,
                    viewportHeight,
                    out var candidate,
                    out var stale,
                    out var projectionFailed))
            {
                if (stale)
                    _controllers.TryRemove(entry.Key, out _);
                sawProjectionFailure |= projectionFailed;
                continue;
            }

            candidates.Add(candidate);
        }

        if (sawProjectionFailure && Interlocked.Exchange(ref _projectionFailureLogged, 1) == 0)
        {
            SafeLog(
                "A native party-HUD node exposed an invalid clip transform; that icon was hidden fail-closed. " +
                "Further transform failures are suppressed.");
        }

        if (candidates.Count == 0)
            return Array.Empty<PartyHudAnchor>();

        candidates.Sort(static (left, right) =>
        {
            var layoutOrder = left.Layout.CompareTo(right.Layout);
            if (layoutOrder != 0)
                return layoutOrder;
            return left.CenterY.CompareTo(right.CenterY);
        });

        var anchors = new PartyHudAnchor[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            anchors[index] = new PartyHudAnchor(
                index,
                candidate.IsLocal,
                candidate.Layout,
                candidate.CenterX,
                candidate.CenterY,
                candidate.IconSize);
        }

        if (Interlocked.Exchange(ref _firstAnchorLogged, 1) == 0)
        {
            SafeLog(
                $"Native party-HUD microphone anchors are live: layout={anchors[0].Layout}, " +
                $"activeRows={anchors.Length}, viewport={viewportWidth:0.#}x{viewportHeight:0.#}.");
        }

        return anchors;
    }

    internal void Suspend()
    {
        lock (_lifecycleSync)
        {
            Volatile.Write(ref _suspended, true);
            DisableHooks();
        }
    }

    internal void Resume()
    {
        lock (_lifecycleSync)
        {
            try
            {
                _townFactoryHook?.Enable();
                _townDestructorHook?.Enable();
                _battleFactoryHook?.Enable();
                _battleDestructorHook?.Enable();
                Volatile.Write(ref _suspended, false);
            }
            catch
            {
                Volatile.Write(ref _suspended, true);
                DisableHooks();
                throw;
            }
        }
    }

    private nint TownFactory(nint context, nint resultStorage)
    {
        var result = _townFactoryHook!.OriginalFunction(context, resultStorage);
        TryRegisterFactoryResult(result, resultStorage, PartyHudLayout.OnlineLobby);
        return result;
    }

    private nint BattleFactory(nint context, nint resultStorage)
    {
        var result = _battleFactoryHook!.OriginalFunction(context, resultStorage);
        TryRegisterFactoryResult(result, resultStorage, PartyHudLayout.Battle);
        return result;
    }

    private nint TownDestructor(nint controller, int deleteFlag)
    {
        _controllers.TryRemove(controller, out _);
        return _townDestructorHook!.OriginalFunction(controller, deleteFlag);
    }

    private nint BattleDestructor(nint controller, int deleteFlag)
    {
        _controllers.TryRemove(controller, out _);
        return _battleDestructorHook!.OriginalFunction(controller, deleteFlag);
    }

    private void TryRegisterFactoryResult(
        nint result,
        nint resultStorage,
        PartyHudLayout layout)
    {
        if (Volatile.Read(ref _suspended))
            return;

        try
        {
            var wrapper = result != nint.Zero ? result : resultStorage;
            if (_memoryReader.TryReadPointer(wrapper + FactoryResultControllerOffset, out var controller) &&
                IsExpectedController(controller, layout))
            {
                _controllers[controller] = layout;
            }
        }
        catch
        {
            // The UI can be destroyed while its factory result is being published.
            // The next valid factory call will repopulate the tracker.
        }
    }

    private bool TryCreateAnchor(
        nint controller,
        PartyHudLayout layout,
        float viewportX,
        float viewportY,
        float viewportWidth,
        float viewportHeight,
        out AnchorCandidate candidate,
        out bool stale,
        out bool projectionFailed)
    {
        candidate = default;
        stale = false;
        projectionFailed = false;
        if (!IsExpectedController(controller, layout))
        {
            stale = true;
            return false;
        }

        var targetPointerOffsets = layout == PartyHudLayout.OnlineLobby
            ? TownTargetPointerOffsets
            : BattleTargetPointerOffsets;
        foreach (var pointerOffset in targetPointerOffsets)
        {
            if (!_memoryReader.TryReadPointer(controller + pointerOffset, out var target) ||
                target == nint.Zero ||
                !TryReadByte(target + ObjectActiveOffset, out var active) ||
                active == 0 ||
                !TryReadVector2(target + ObjectSizeOffset, out var nativeSize) ||
                !float.IsFinite(nativeSize.X) ||
                MathF.Abs(nativeSize.X) < 16.0f ||
                MathF.Abs(nativeSize.X) > 4_096.0f ||
                !TryReadMatrix(target + ObjectFinalTransformOffset, out var finalTransform))
            {
                continue;
            }

            var localPoint = new Vector2(
                (MathF.Abs(nativeSize.X) * 0.5f) + NativeRightEdgeGap,
                0.0f);
            if (!RelinkUiProjection.TryProject(
                    finalTransform,
                    localPoint,
                    viewportX,
                    viewportY,
                    viewportWidth,
                    viewportHeight,
                    out var center) ||
                !RelinkUiProjection.TryMeasureLogicalLength(
                    finalTransform,
                    NativeIconSize,
                    viewportX,
                    viewportY,
                    viewportWidth,
                    viewportHeight,
                    out var iconSize))
            {
                projectionFailed = true;
                continue;
            }

            // A valid party-row icon remains small relative to the current game
            // viewport. Reject effect/animation transforms instead of clamping a
            // bad scale into a still-huge 96 px icon at the edge of the screen.
            var maximumIconSize = Math.Clamp(viewportHeight * 0.05f, 32.0f, 96.0f);
            if (iconSize > maximumIconSize)
            {
                projectionFailed = true;
                continue;
            }

            var minimumIconSize = Math.Clamp(viewportHeight * 0.012f, 12.0f, 24.0f);
            iconSize = Math.Max(iconSize, minimumIconSize);
            var radius = iconSize * 0.5f;
            if (center.X < viewportX + radius ||
                center.X > viewportX + viewportWidth - radius ||
                center.Y < viewportY + radius ||
                center.Y > viewportY + viewportHeight - radius)
            {
                projectionFailed = true;
                continue;
            }

            var stateOffset = layout == PartyHudLayout.OnlineLobby
                ? TownSlotOffset
                : BattleTypeOffset;
            var isLocal = TryReadInt32(controller + stateOffset, out var state) && state == 0;
            candidate = new AnchorCandidate(layout, isLocal, center.X, center.Y, iconSize);
            return true;
        }

        return false;
    }

    private bool IsExpectedController(nint controller, PartyHudLayout layout)
    {
        if (controller == nint.Zero ||
            !_memoryReader.TryReadPointer(controller, out var vtable))
        {
            return false;
        }

        return vtable == (layout == PartyHudLayout.OnlineLobby ? _townVtable : _battleVtable);
    }

    private bool TryReadByte(nint address, out byte value)
    {
        Span<byte> bytes = stackalloc byte[1];
        if (!_memoryReader.TryReadBytes(address, bytes))
        {
            value = 0;
            return false;
        }

        value = bytes[0];
        return true;
    }

    private bool TryReadInt32(nint address, out int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (!_memoryReader.TryReadBytes(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private bool TryReadVector2(nint address, out Vector2 value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float) * 2];
        if (!_memoryReader.TryReadBytes(address, bytes))
        {
            value = default;
            return false;
        }

        value = new Vector2(
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[sizeof(float)..])));
        return true;
    }

    private bool TryReadMatrix(nint address, out Matrix4x4 matrix)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float) * 16];
        if (!_memoryReader.TryReadBytes(address, bytes))
        {
            matrix = default;
            return false;
        }

        Span<float> values = stackalloc float[16];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(index * sizeof(float), sizeof(float))));
        }

        matrix = new Matrix4x4(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
        return true;
    }

    private void DisableHooks()
    {
        _battleDestructorHook?.Disable();
        _battleFactoryHook?.Disable();
        _townDestructorHook?.Disable();
        _townFactoryHook?.Disable();
    }

    private void ClearHooks()
    {
        _battleDestructorHook = null;
        _battleFactoryHook = null;
        _townDestructorHook = null;
        _townFactoryHook = null;
    }

    private void SafeLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Never allow a logger failure to escape a native hook or Present callback.
        }
    }

    private readonly record struct AnchorCandidate(
        PartyHudLayout Layout,
        bool IsLocal,
        float CenterX,
        float CenterY,
        float IconSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HudFactoryDelegate(nint context, nint resultStorage);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HudDestructorDelegate(nint controller, int deleteFlag);
}
