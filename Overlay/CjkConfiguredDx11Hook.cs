using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DearImguiSharp;
using Reloaded.Imgui.Hook;
using Reloaded.Imgui.Hook.Direct3D11;
using Reloaded.Imgui.Hook.Implementations;

namespace GBFR.ChatOverlay.Overlay;

/// <summary>
/// Uses the same Reloaded DX11 initialization order proven by the Extra Sigil
/// Slots overlay: configure and build the font atlas before the native DX11
/// backend begins receiving Present callbacks, and keep custom glyph ranges
/// pinned for the complete backend lifetime.
/// </summary>
internal sealed unsafe class CjkConfiguredDx11Hook : IImguiHook
{
    private readonly ImguiHookDx11 _inner = new();
    private readonly Action<string> _log;
    private ushort[]? _glyphRanges;
    private GCHandle _glyphRangesHandle;
    private ImFont? _font;
    private bool _disposed;

    internal CjkConfiguredDx11Hook(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsApiSupported() => _inner.IsApiSupported();

    public void Initialize()
    {
        ConfigureCjkFont();
        _inner.Initialize();
    }

    public void Disable() => _inner.Disable();

    public void Enable() => _inner.Enable();

    private void ConfigureCjkFont()
    {
        try
        {
            var fontPath = FindCjkFont();
            if (fontPath is null)
            {
                _log("No CJK system font was found; ImGui will use its Latin default font.");
                return;
            }

            _glyphRanges = BuildGlyphRanges();
            _glyphRangesHandle = GCHandle.Alloc(_glyphRanges, GCHandleType.Pinned);
            var glyphRanges = (ushort*)_glyphRangesHandle.AddrOfPinnedObject();
            ref var firstGlyphRange = ref Unsafe.AsRef<ushort>(glyphRanges);

            var io = ImguiHook.IO;
            var atlas = io.Fonts;
            _font = ImGui.ImFontAtlasAddFontFromFileTTF(
                atlas,
                fontPath,
                18.0f,
                null!,
                ref firstGlyphRange);
            if (_font is null || !ImGui.ImFontAtlasBuild(atlas))
                throw new InvalidOperationException("Dear ImGui rejected the CJK font atlas.");

            io.FontDefault = _font;
            _log(
                $"CJK font loaded before DX11 hook initialization: {Path.GetFileName(fontPath)}, " +
                $"{(_glyphRanges.Length - 1) / 2} glyph ranges.");
        }
        catch (Exception exception)
        {
            if (_glyphRangesHandle.IsAllocated)
                _glyphRangesHandle.Free();
            _glyphRanges = null;
            _font = null;
            _log($"CJK font setup failed; continuing with the default font: {exception}");
        }
    }

    private static string? FindCjkFont()
    {
        var fontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates =
        [
            Path.Combine(fontsDirectory, "msyh.ttc"),
            Path.Combine(fontsDirectory, "msyhl.ttc"),
            Path.Combine(fontsDirectory, "msyhbd.ttc"),
            Path.Combine(fontsDirectory, "simhei.ttf"),
            Path.Combine(fontsDirectory, "simsun.ttc"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static ushort[] BuildGlyphRanges() =>
    [
        0x0020, 0x00FF, // Basic Latin and Latin-1 Supplement
        0x2000, 0x206F, // General Punctuation
        0x3000, 0x30FF, // CJK Symbols, Hiragana and Katakana
        0x31F0, 0x31FF, // Katakana Phonetic Extensions
        0x3400, 0x4DBF, // CJK Unified Ideographs Extension A
        0x4E00, 0x9FFF, // CJK Unified Ideographs
        0xF900, 0xFAFF, // CJK Compatibility Ideographs
        0xFF00, 0xFFEF, // Halfwidth and Fullwidth Forms
        0xFFFD, 0xFFFD, // Replacement character
        0,
    ];

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _inner.Dispose();
        _font = null;
        if (_glyphRangesHandle.IsAllocated)
            _glyphRangesHandle.Free();
        _glyphRanges = null;
    }
}
