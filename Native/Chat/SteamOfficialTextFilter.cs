using System.Runtime.InteropServices;
using System.Text;
using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal sealed class SteamOfficialTextFilter : IOfficialTextFilter
{
    private const int ChatFilterContext = 2;
    private const ulong SourceSteamId = 0;
    private const uint FilterOptions = 0;
    private const int MaxInputUtf8Bytes = 2_048;
    private const int MaxOutputUtf8Bytes = checked((MaxInputUtf8Bytes * 3) + 1);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object _sync = new();
    private readonly Func<SteamOfficialTextFilterExports?> _resolveExports;
    private SteamOfficialTextFilterExports? _exports;
    private OfficialTextFilterStatus _status;

    internal SteamOfficialTextFilter()
        : this(SteamOfficialTextFilterBindings.Resolve)
    {
    }

    internal SteamOfficialTextFilter(Func<SteamOfficialTextFilterExports?> resolveExports)
    {
        _resolveExports = resolveExports ??
                          throw new ArgumentNullException(nameof(resolveExports));
        _status = OfficialTextFilterStatus.Unavailable(
            "Steam supplementary text filter has not been refreshed.");
    }

    public OfficialTextFilterStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public OfficialTextFilterStatus Refresh()
    {
        lock (_sync)
        {
            SteamOfficialTextFilterExports? exports = null;
            try
            {
                exports = _resolveExports();
            }
            catch
            {
                exports = null;
            }

            if (exports is null)
            {
                _exports = null;
                _status = OfficialTextFilterStatus.Unavailable(
                    "Steam supplementary text filter exports are unavailable.");
                return _status;
            }

            _exports = exports;
            try
            {
                if (exports.InitFilterText(FilterOptions))
                {
                    _status = new OfficialTextFilterStatus(
                        OfficialTextFilterState.Ready,
                        "Steam supplementary text filter is ready.");
                }
                else
                {
                    _status = new OfficialTextFilterStatus(
                        OfficialTextFilterState.Passthrough,
                        "Steam supplementary text filter is unavailable for the game language; passthrough is active.");
                }
            }
            catch
            {
                _exports = null;
                _status = OfficialTextFilterStatus.Unavailable(
                    "Steam supplementary text filter initialization failed.");
            }

            return _status;
        }
    }

    public OfficialTextFilterResult Filter(string text)
    {
        if (text is null)
        {
            return new OfficialTextFilterResult(string.Empty, 0, false);
        }

        if (text.Length > MaxInputUtf8Bytes)
        {
            return FailOpen(text);
        }

        lock (_sync)
        {
            if (_exports is null)
            {
                return FailOpen(text);
            }

            byte[] inputBytes;
            try
            {
                inputBytes = StrictUtf8.GetBytes(text);
            }
            catch (EncoderFallbackException)
            {
                return FailOpen(text);
            }

            if (inputBytes.Length > MaxInputUtf8Bytes)
            {
                return FailOpen(text);
            }

            if (Array.IndexOf(inputBytes, (byte)0) >= 0)
            {
                return FailOpen(text);
            }

            var outputCapacity = checked((inputBytes.Length * 3) + 1);
            if (outputCapacity > MaxOutputUtf8Bytes)
            {
                return FailOpen(text);
            }

            var inputPointer = IntPtr.Zero;
            var outputPointer = IntPtr.Zero;

            try
            {
                inputPointer = Marshal.AllocHGlobal(inputBytes.Length + 1);
                Marshal.Copy(inputBytes, 0, inputPointer, inputBytes.Length);
                Marshal.WriteByte(inputPointer, inputBytes.Length, 0);

                outputPointer = Marshal.AllocHGlobal(outputCapacity);
                for (var i = 0; i < outputCapacity; i++)
                {
                    Marshal.WriteByte(outputPointer, i, 0xAA);
                }

                var filteredCharacterCount = _exports.FilterText(
                    ChatFilterContext,
                    SourceSteamId,
                    inputPointer,
                    outputPointer,
                    (uint)outputCapacity);

                var outputBytes = new byte[outputCapacity];
                Marshal.Copy(outputPointer, outputBytes, 0, outputCapacity);

                var nulIndex = Array.IndexOf(outputBytes, (byte)0);
                if (nulIndex < 0)
                {
                    return FailOpen(text);
                }

                if (filteredCharacterCount < 0 || filteredCharacterCount > text.Length)
                {
                    return FailOpen(text);
                }

                var filteredText = StrictUtf8.GetString(outputBytes, 0, nulIndex);
                return new OfficialTextFilterResult(
                    filteredText,
                    filteredCharacterCount,
                    true);
            }
            catch
            {
                return FailOpen(text);
            }
            finally
            {
                if (inputPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(inputPointer);
                }

                if (outputPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(outputPointer);
                }
            }
        }
    }

    private static OfficialTextFilterResult FailOpen(string text) =>
        new(text, 0, false);
}

internal sealed class SteamOfficialTextFilterExports
{
    internal SteamOfficialTextFilterExports(
        Func<uint, bool> initFilterText,
        Func<int, ulong, IntPtr, IntPtr, uint, int> filterText)
    {
        InitFilterText = initFilterText ??
                         throw new ArgumentNullException(nameof(initFilterText));
        FilterText = filterText ??
                     throw new ArgumentNullException(nameof(filterText));
    }

    internal Func<uint, bool> InitFilterText { get; }

    internal Func<int, ulong, IntPtr, IntPtr, uint, int> FilterText { get; }
}

internal static class SteamOfficialTextFilterBindings
{
    private const string SteamUtilsV010Export = "SteamAPI_SteamUtils_v010";
    private const string InitFilterTextExport = "SteamAPI_ISteamUtils_InitFilterText";
    private const string FilterTextExport = "SteamAPI_ISteamUtils_FilterText";

    private static readonly string[] LibraryNames = ["steam_api64.dll", "steam_api.dll"];
    private static readonly object NativeSync = new();
    private static SteamOfficialTextFilterExports? _cachedExports;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetSteamUtilsDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool InitFilterTextDelegate(nint utils, uint filterOptions);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FilterTextDelegate(
        nint utils,
        int context,
        ulong sourceSteamId,
        IntPtr inputMessage,
        IntPtr outputMessage,
        uint outputByteCapacity);

    internal static Func<SteamOfficialTextFilterExports?> Resolve { get; set; } =
        ResolveNative;

    internal static SteamOfficialTextFilterExports? ResolveNative()
    {
        lock (NativeSync)
        {
            if (_cachedExports is not null)
                return _cachedExports;

            _cachedExports = TryResolve(GetModuleHandleW, GetProcAddress);
            return _cachedExports;
        }
    }

    internal static SteamOfficialTextFilterExports? TryResolve(
        Func<string, nint> moduleHandleLookup,
        Func<nint, string, nint> exportLookup)
    {
        return TryResolve(moduleHandleLookup, exportLookup, ResolveExports);
    }

    internal static SteamOfficialTextFilterExports? TryResolve(
        Func<string, nint> moduleHandleLookup,
        Func<nint, string, nint> exportLookup,
        Func<nint, nint, nint, SteamOfficialTextFilterExports?> exportsFactory)
    {
        ArgumentNullException.ThrowIfNull(moduleHandleLookup);
        ArgumentNullException.ThrowIfNull(exportLookup);
        ArgumentNullException.ThrowIfNull(exportsFactory);

        foreach (var libraryName in LibraryNames)
        {
            nint libraryHandle;
            try
            {
                libraryHandle = moduleHandleLookup(libraryName);
            }
            catch
            {
                continue;
            }

            if (libraryHandle == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var getUtilsAddress = exportLookup(
                    libraryHandle,
                    SteamUtilsV010Export);
                if (getUtilsAddress == IntPtr.Zero)
                {
                    continue;
                }

                var initAddress = exportLookup(
                    libraryHandle,
                    InitFilterTextExport);
                var filterAddress = exportLookup(
                    libraryHandle,
                    FilterTextExport);
                if (initAddress == IntPtr.Zero || filterAddress == IntPtr.Zero)
                {
                    continue;
                }

                var exports = exportsFactory(getUtilsAddress, initAddress, filterAddress);
                if (exports is null)
                {
                    continue;
                }

                return exports;
            }
            catch
            {
                // The module handle is borrowed; it must not be freed here.
            }
        }

        return null;
    }

    private static SteamOfficialTextFilterExports? ResolveExports(
        nint getUtilsAddress,
        nint initAddress,
        nint filterAddress)
    {
        var getUtils =
            Marshal.GetDelegateForFunctionPointer<GetSteamUtilsDelegate>(
                getUtilsAddress);
        var utils = getUtils();
        if (utils == IntPtr.Zero)
        {
            return null;
        }

        var init =
            Marshal.GetDelegateForFunctionPointer<InitFilterTextDelegate>(
                initAddress);
        var filter =
            Marshal.GetDelegateForFunctionPointer<FilterTextDelegate>(
                filterAddress);

        return new SteamOfficialTextFilterExports(
            options => init(utils, options),
            (context, source, input, output, capacity) =>
                filter(utils, context, source, input, output, capacity));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string moduleName);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Ansi,
        ExactSpelling = true,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern nint GetProcAddress(
        nint module,
        [MarshalAs(UnmanagedType.LPStr)] string procedureName);
}
