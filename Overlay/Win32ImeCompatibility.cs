using System.Globalization;
using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Overlay;

/// <summary>
/// Bridges Win32's ANSI/DBCS IME messages into Dear ImGui's UTF-8 input queue
/// and keeps third-party IME composition/candidate windows beside the chat box.
/// </summary>
internal static class Win32ImeCompatibility
{
    internal const uint WmImeStartComposition = 0x010D;
    internal const uint WmImeEndComposition = 0x010E;
    internal const uint WmImeComposition = 0x010F;
    internal const uint WmImeSetContext = 0x0281;
    internal const uint WmImeNotify = 0x0282;
    internal const uint WmImeControl = 0x0283;
    internal const uint WmImeCompositionFull = 0x0284;
    internal const uint WmImeSelect = 0x0285;
    internal const uint WmImeChar = 0x0286;
    internal const uint WmImeRequest = 0x0288;

    private const uint LocaleIDefaultAnsiCodePage = 0x00001004;
    private const uint MbErrInvalidChars = 0x00000008;
    private const uint CfsForcePosition = 0x0020;
    private const uint CfsExclude = 0x0080;
    private const uint IaceDefault = 0x0010;

    internal static bool IsImeUiMessage(uint message) =>
        message is WmImeStartComposition or
            WmImeEndComposition or
            WmImeComposition or
            WmImeSetContext or
            WmImeNotify or
            WmImeControl or
            WmImeCompositionFull or
            WmImeSelect or
            WmImeRequest;

    internal static bool IsUnicodeWindow(nint windowHandle) => IsWindowUnicode(windowHandle);

    internal static nint CallDefaultWindowProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam) =>
        IsUnicodeWindow(windowHandle)
            ? DefWindowProcW(windowHandle, message, wParam, lParam)
            : DefWindowProcA(windowHandle, message, wParam, lParam);

    internal static uint GetActiveInputCodePage()
    {
        // WM_IME_CHAR bytes are produced by the active input locale. This must
        // not use the process ACP: Sogou can emit CP936 while Windows itself is
        // configured with a Western system locale/ACP.
        var keyboardLayout = GetKeyboardLayout(0);
        var localeId = unchecked((uint)(nuint)keyboardLayout) & 0xFFFF;
        Span<char> codePageText = stackalloc char[16];
        unsafe
        {
            fixed (char* codePageTextPointer = codePageText)
            {
                var characterCount = GetLocaleInfoW(
                    localeId,
                    LocaleIDefaultAnsiCodePage,
                    codePageTextPointer,
                    codePageText.Length);
                if (characterCount > 1 &&
                    uint.TryParse(
                        codePageText[..(characterCount - 1)],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var codePage) &&
                    codePage != 0)
                {
                    return codePage;
                }
            }
        }

        return GetACP();
    }

    /// <summary>
    /// Decodes an ANSI WM_IME_CHAR value. For DBCS windows Win32 packs the
    /// lead byte into the high byte and the trail byte into the low byte.
    /// </summary>
    internal static bool TryDecodePackedAnsiCharacter(
        uint rawCharacter,
        uint codePage,
        out string text)
    {
        Span<byte> bytes = stackalloc byte[2];
        var packed = unchecked((ushort)rawCharacter);
        var highByte = (byte)(packed >> 8);
        var byteCount = 1;
        if (highByte != 0)
        {
            bytes[0] = highByte;
            bytes[1] = (byte)packed;
            byteCount = 2;
        }
        else
        {
            bytes[0] = (byte)packed;
        }

        return TryDecode(bytes[..byteCount], codePage, out text);
    }

    /// <summary>
    /// Reassembles the two WM_CHAR messages emitted by DefWindowProcA for one
    /// DBCS character. An empty successful result means a lead byte is pending.
    /// </summary>
    internal static bool TryConsumeAnsiWindowCharacter(
        uint rawCharacter,
        uint codePage,
        ref int pendingLeadByte,
        out string text)
    {
        if (rawCharacter > byte.MaxValue)
        {
            pendingLeadByte = -1;
            return TryDecodePackedAnsiCharacter(rawCharacter, codePage, out text);
        }

        var currentByte = (byte)rawCharacter;
        if (pendingLeadByte >= 0)
        {
            Span<byte> bytes = stackalloc byte[2]
            {
                (byte)pendingLeadByte,
                currentByte,
            };
            pendingLeadByte = -1;
            return TryDecode(bytes, codePage, out text);
        }

        if (IsDBCSLeadByteEx(codePage, currentByte))
        {
            pendingLeadByte = currentByte;
            text = string.Empty;
            return true;
        }

        Span<byte> singleByte = stackalloc byte[1] { currentByte };
        return TryDecode(singleByte, codePage, out text);
    }

    internal static bool UpdateCandidatePlacement(
        nint windowHandle,
        float screenLeft,
        float screenTop,
        float screenRight,
        float screenBottom,
        out bool attachedDefaultContext)
    {
        attachedDefaultContext = false;
        if (windowHandle == nint.Zero)
            return false;

        // Dear ImGui documents GetItemRectMin/Max as screen-space coordinates;
        // IMM32 expects both forms in game-window client coordinates.
        var topLeft = new NativePoint((int)MathF.Round(screenLeft), (int)MathF.Round(screenTop));
        var bottomRight = new NativePoint((int)MathF.Round(screenRight), (int)MathF.Round(screenBottom));
        if (!ScreenToClient(windowHandle, ref topLeft) ||
            !ScreenToClient(windowHandle, ref bottomRight))
        {
            return false;
        }

        var inputContext = ImmGetContext(windowHandle);
        if (inputContext == nint.Zero)
        {
            attachedDefaultContext = ImmAssociateContextEx(windowHandle, nint.Zero, IaceDefault);
            inputContext = ImmGetContext(windowHandle);
            if (inputContext == nint.Zero)
            {
                if (attachedDefaultContext)
                    ImmAssociateContextEx(windowHandle, nint.Zero, 0);
                attachedDefaultContext = false;
                return false;
            }
        }

        try
        {
            var composition = new CompositionForm
            {
                Style = CfsForcePosition,
                CurrentPosition = new NativePoint(topLeft.X + 4, topLeft.Y + 4),
            };
            var candidate = new CandidateForm
            {
                Index = 0,
                Style = CfsExclude,
                CurrentPosition = new NativePoint(topLeft.X, bottomRight.Y + 2),
                Area = new NativeRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y),
            };

            var compositionUpdated = ImmSetCompositionWindow(inputContext, ref composition);
            var candidateUpdated = ImmSetCandidateWindow(inputContext, ref candidate);
            return compositionUpdated || candidateUpdated;
        }
        finally
        {
            ImmReleaseContext(windowHandle, inputContext);
        }
    }

    internal static void DetachDefaultContext(nint windowHandle)
    {
        if (windowHandle != nint.Zero)
            ImmAssociateContextEx(windowHandle, nint.Zero, 0);
    }

    private static bool TryDecode(ReadOnlySpan<byte> bytes, uint codePage, out string text)
    {
        Span<char> characters = stackalloc char[4];
        unsafe
        {
            fixed (byte* bytePointer = bytes)
            fixed (char* characterPointer = characters)
            {
                var count = MultiByteToWideChar(
                    codePage,
                    MbErrInvalidChars,
                    bytePointer,
                    bytes.Length,
                    characterPointer,
                    characters.Length);
                if (count > 0)
                {
                    text = new string(characters[..count]);
                    return true;
                }
            }
        }

        text = string.Empty;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        internal int X = x;
        internal int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        internal int Left = left;
        internal int Top = top;
        internal int Right = right;
        internal int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionForm
    {
        internal uint Style;
        internal NativePoint CurrentPosition;
        internal NativeRect Area;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CandidateForm
    {
        internal uint Index;
        internal uint Style;
        internal NativePoint CurrentPosition;
        internal NativeRect Area;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowUnicode(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcA(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsDBCSLeadByteEx(uint codePage, byte testCharacter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetLocaleInfoW(
        uint locale,
        uint localeType,
        char* localeData,
        int dataCharacterCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe int MultiByteToWideChar(
        uint codePage,
        uint flags,
        byte* multiByteText,
        int byteCount,
        char* wideText,
        int wideCharacterCount);

    [DllImport("imm32.dll")]
    private static extern nint ImmGetContext(nint windowHandle);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(nint windowHandle, nint inputContext);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmAssociateContextEx(nint windowHandle, nint inputContext, uint flags);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetCompositionWindow(nint inputContext, ref CompositionForm compositionForm);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetCandidateWindow(nint inputContext, ref CandidateForm candidateForm);
}
