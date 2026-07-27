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
    private const int ChainburstVtableRva = 0x05A68938;
    private const int FactoryResultControllerOffset = 0x18;
    private const int ObjectFinalTransformOffset = 0x120;
    private const int ObjectSizeOffset = 0x1BC;
    private const int ObjectActiveOffset = 0x1D0;
    private const int ControllerVisibilityStateOffset = 0x188;
    private const int TownSlotOffset = 0x340;
    private const int BattleTypeOffset = 0x1A0;
    // Relink's 2560x1440 HUD transform uses a 2/3 scale here, so a 72-unit
    // glyph renders at the requested 48 px while still following native HUD
    // scaling at other resolutions.
    internal const float NativeIconLogicalSize = 72.0f;
    internal const float NativeRightEdgeGap = 48.0f;

    private static readonly int[] TownTargetPointerOffsets = [0x1B8, 0x230];
    // Live Relink 2.0.2 memory confirms these two resolved UIObject pointers are
    // the normal/red full-width HP-row geometry. At 2560x1440 the local node is
    // 1504 units wide and the remote nodes are 816 units wide; projecting their
    // right edges lands at the native long/short bar endpoints. The animated
    // HpGauge01/02/Mask objects at 0x3B0/0x3D0/0x3F0 are 0/512-unit child
    // textures whose local transforms land near the name side instead.
    private static readonly int[] BattleTargetPointerOffsets = [0x250, 0x270];

    private readonly ReloadedHooksApi _hooks;
    private readonly Action<string> _log;
    private readonly IRelinkMemoryReader _memoryReader;
    private readonly object _lifecycleSync = new();
    private readonly ConcurrentDictionary<nint, PartyHudLayout> _controllers = new();
    private readonly ConcurrentDictionary<nint, byte> _chainburstControllers = new();

    private IHook<HudFactoryDelegate>? _townFactoryHook;
    private IHook<HudDestructorDelegate>? _townDestructorHook;
    private IHook<HudFactoryDelegate>? _battleFactoryHook;
    private IHook<HudDestructorDelegate>? _battleDestructorHook;
    private IHook<HudFactoryDelegate>? _chainburstFactoryHook;
    private IHook<HudDestructorDelegate>? _chainburstDestructorHook;
    private nint _moduleBase;
    private nint _townVtable;
    private nint _battleVtable;
    private nint _chainburstVtable;
    private bool _initialized;
    private bool _suspended;
    private int _firstAnchorLogged;
    private int _projectionFailureLogged;
    private int _chainburstSuppressionLogged;

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

    internal static bool IsControllerVisibilityStateVisible(int state) => state == 2;

    internal static bool IsChainburstBlockingState(int state) => state != 0;

    internal void Initialize()
    {
        lock (_lifecycleSync)
        {
            if (_initialized)
                return;

            using var process = Process.GetCurrentProcess();
            var mainModule = process.MainModule ??
                throw new InvalidOperationException("The game module is unavailable.");
            var rvas = StartupPhaseDiagnostic.Run(
                "required-byte-rva-preflight-party-hud",
                _log,
                () => RelinkHudBuildLocator.Resolve(mainModule.FileName));
            _moduleBase = mainModule.BaseAddress;
            _townVtable = _moduleBase + TownVtableRva;
            _battleVtable = _moduleBase + BattleVtableRva;
            _chainburstVtable = _moduleBase + ChainburstVtableRva;

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
                _chainburstFactoryHook = _hooks.CreateHook<HudFactoryDelegate>(
                    ChainburstFactory,
                    _moduleBase + rvas.ChainburstFactory);
                _chainburstFactoryHook.Activate();
                _chainburstDestructorHook = _hooks.CreateHook<HudDestructorDelegate>(
                    ChainburstDestructor,
                    _moduleBase + rvas.ChainburstDestructor);
                _chainburstDestructorHook.Activate();
                Volatile.Write(ref _initialized, true);
                SafeLog(
                    "Relink 2.0.2 native party-HUD tracker attached; lobby/battle mode, " +
                    "resolution, aspect ratio and HUD scale now follow the game's live UI node transforms. " +
                    "Microphone anchors are emitted only while the native party-HUD controller is visible, " +
                    "with the Full Chain illustration explicitly blacklisted.");
            }
            catch
            {
                DisableHooks();
                ClearHooks();
                _controllers.Clear();
                _chainburstControllers.Clear();
                _moduleBase = nint.Zero;
                _townVtable = nint.Zero;
                _battleVtable = nint.Zero;
                _chainburstVtable = nint.Zero;
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

        // Full Chain keeps the party HP rows rendered beneath its illustration.
        // Preserve the HP-HUD whitelist as the default rule, then explicitly ban
        // this one native overlay for its complete opening/visible/closing lifetime.
        if (IsChainburstOverlayActive())
        {
            if (Interlocked.Exchange(ref _chainburstSuppressionLogged, 1) == 0)
                SafeLog("Full Chain overlay active; native party-HUD microphone anchors are suppressed.");
            return Array.Empty<PartyHudAnchor>();
        }

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
                _chainburstFactoryHook?.Enable();
                _chainburstDestructorHook?.Enable();
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

    private nint ChainburstFactory(nint context, nint resultStorage)
    {
        var result = _chainburstFactoryHook!.OriginalFunction(context, resultStorage);
        TryRegisterChainburstFactoryResult(result, resultStorage);
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

    private nint ChainburstDestructor(nint controller, int deleteFlag)
    {
        _chainburstControllers.TryRemove(controller, out _);
        return _chainburstDestructorHook!.OriginalFunction(controller, deleteFlag);
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

    private void TryRegisterChainburstFactoryResult(nint result, nint resultStorage)
    {
        if (Volatile.Read(ref _suspended))
            return;

        try
        {
            var wrapper = result != nint.Zero ? result : resultStorage;
            if (_memoryReader.TryReadPointer(wrapper + FactoryResultControllerOffset, out var controller) &&
                IsExpectedChainburstController(controller))
            {
                _chainburstControllers[controller] = 0;
            }
        }
        catch
        {
            // The transient illustration can be destroyed while the factory result
            // is being published. Its next valid construction repopulates the set.
        }
    }

    private bool IsChainburstOverlayActive()
    {
        foreach (var entry in _chainburstControllers)
        {
            var controller = entry.Key;
            if (!IsExpectedChainburstController(controller))
            {
                _chainburstControllers.TryRemove(controller, out _);
                continue;
            }

            // ControllerChainburst's own visibility query is exactly state != 0.
            // If a live, correctly typed controller cannot be read for one frame,
            // fail closed so its full-screen illustration never leaks HUD glyphs.
            if (!TryReadInt32(controller + ControllerVisibilityStateOffset, out var state) ||
                IsChainburstBlockingState(state))
            {
                return true;
            }
        }

        return false;
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

        // Whitelist rendering by the fully visible party HUD itself. State 1 is
        // opening, 2 is stable/visible and 3 is closing; accepting only state 2
        // prevents early glyphs on load screens and hides them as soon as closing starts.
        if (!TryReadInt32(controller + ControllerVisibilityStateOffset, out var controllerState) ||
            !IsControllerVisibilityStateVisible(controllerState))
        {
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
                    NativeIconLogicalSize,
                    viewportX,
                    viewportY,
                    viewportWidth,
                    viewportHeight,
                    out var iconSize))
            {
                projectionFailed = true;
                continue;
            }

            // Gauge transforms can temporarily report a large logical scale while
            // their parent HUD animation settles. Keep the native center, clamp
            // only the drawn glyph, and allow an edge-adjacent row instead of
            // hiding every otherwise valid battle anchor.
            iconSize = Math.Clamp(iconSize, 18.0f, 64.0f);
            if (center.X < viewportX - iconSize ||
                center.X > viewportX + viewportWidth + iconSize ||
                center.Y < viewportY - iconSize ||
                center.Y > viewportY + viewportHeight + iconSize)
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

    private bool IsExpectedChainburstController(nint controller)
    {
        return controller != nint.Zero &&
               _memoryReader.TryReadPointer(controller, out var vtable) &&
               vtable == _chainburstVtable;
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
        _chainburstDestructorHook?.Disable();
        _chainburstFactoryHook?.Disable();
        _battleDestructorHook?.Disable();
        _battleFactoryHook?.Disable();
        _townDestructorHook?.Disable();
        _townFactoryHook?.Disable();
    }

    private void ClearHooks()
    {
        _chainburstDestructorHook = null;
        _chainburstFactoryHook = null;
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
