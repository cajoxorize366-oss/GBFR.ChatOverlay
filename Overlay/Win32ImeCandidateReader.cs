using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Overlay;

internal static class Win32ImeCandidateReader
{
    internal const uint ImnChangeCandidate = 0x0003;
    internal const uint ImnCloseCandidate = 0x0004;
    internal const uint ImnOpenCandidate = 0x0005;
    internal const uint ImnSetCandidatePosition = 0x0009;

    private const uint DefaultCandidateMask = 0x0001;
    private const int CandidateListCount = 4;
    private const uint MaximumCandidateListBytes = 1_048_576;

    internal static bool IsRefreshNotification(uint notification) =>
        notification is ImnOpenCandidate or ImnChangeCandidate or ImnSetCandidatePosition;

    internal static bool TryReadFirstCandidateList(
        nint windowHandle,
        uint candidateMask,
        out ImeCandidateSnapshot? snapshot,
        out string failure)
    {
        snapshot = null;
        failure = "no candidate list was exposed by IMM32";
        if (windowHandle == nint.Zero)
        {
            failure = "the game window handle was unavailable";
            return false;
        }

        var inputContext = ImmGetContext(windowHandle);
        if (inputContext == nint.Zero)
        {
            failure = "ImmGetContext returned no input context";
            return false;
        }

        try
        {
            var effectiveMask = candidateMask == 0 ? DefaultCandidateMask : candidateMask;
            for (uint listIndex = 0; listIndex < CandidateListCount; listIndex++)
            {
                if ((effectiveMask & (1u << checked((int)listIndex))) == 0)
                    continue;

                var requiredBytes = ImmGetCandidateListW(inputContext, listIndex, nint.Zero, 0);
                if (requiredBytes == 0)
                    continue;
                if (requiredBytes > MaximumCandidateListBytes)
                {
                    failure = $"candidate list {listIndex} requested {requiredBytes} bytes";
                    continue;
                }

                var buffer = GC.AllocateUninitializedArray<byte>(checked((int)requiredBytes));
                uint copiedBytes;
                unsafe
                {
                    fixed (byte* bufferPointer = buffer)
                    {
                        copiedBytes = ImmGetCandidateListW(
                            inputContext,
                            listIndex,
                            (nint)bufferPointer,
                            requiredBytes);
                    }
                }

                if (copiedBytes == 0 || copiedBytes > buffer.Length)
                {
                    failure = $"candidate list {listIndex} could not be copied";
                    continue;
                }

                if (!ImeCandidateListParser.TryParse(
                        buffer.AsSpan(0, checked((int)copiedBytes)),
                        listIndex,
                        out var parsed))
                {
                    failure = $"candidate list {listIndex} returned a malformed buffer";
                    continue;
                }

                if (parsed is not null && parsed.Count > 0)
                {
                    snapshot = parsed;
                    failure = string.Empty;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            ImmReleaseContext(windowHandle, inputContext);
        }
    }

    [DllImport("imm32.dll")]
    private static extern nint ImmGetContext(nint windowHandle);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(nint windowHandle, nint inputContext);

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern uint ImmGetCandidateListW(
        nint inputContext,
        uint listIndex,
        nint candidateList,
        uint bufferLength);
}
