using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal static class RelinkFilteredChatCallbackDecoder
{
    internal const int SendCallbackStateBytes = 0x60;
    internal const int ReceiveCallbackStateBytes = 0x68;

    private const int SenderKeyOffset = 0x10;
    private const int CategoryOffset = 0x14;
    private const int SenderLabelOffset = 0x18;
    private const int SenderLabelCapacity = 0x40;
    private const int SenderLabelLengthOffset = 0x58;
    private const int MetadataOffset = 0x60;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static bool TryDecodeSendCue(
        ReadOnlySpan<byte> callbackState,
        out ChatCommunicationCue cue)
    {
        cue = ChatCommunicationCue.None;
        if (callbackState.Length < SendCallbackStateBytes ||
            !TryDecodeSenderLabel(callbackState, out var senderLabel))
        {
            return false;
        }

        cue = RelinkChatPacketDecoder.ClassifyCommunicationCue(senderLabel, out _);
        return true;
    }

    internal static bool TryDecodeReceive(
        ReadOnlySpan<byte> callbackState,
        ReadOnlySpan<byte> filteredTextBytes,
        DateTimeOffset receivedAt,
        out IncomingChatMessage message)
    {
        message = default;
        if (callbackState.Length < ReceiveCallbackStateBytes ||
            filteredTextBytes.IsEmpty ||
            filteredTextBytes.Length > RelinkChatPacketDecoder.MaximumMessageBytes ||
            filteredTextBytes.Contains((byte)0))
        {
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(filteredTextBytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!TryDecodeSenderLabel(callbackState, out var senderLabel))
            return false;

        var senderKey = BinaryPrimitives.ReadUInt32LittleEndian(
            callbackState.Slice(SenderKeyOffset, sizeof(uint)));
        var category = BinaryPrimitives.ReadUInt32LittleEndian(
            callbackState.Slice(CategoryOffset, sizeof(uint)));
        var metadata = BinaryPrimitives.ReadUInt32LittleEndian(
            callbackState.Slice(MetadataOffset, sizeof(uint)));
        var cue = RelinkChatPacketDecoder.ClassifyCommunicationCue(senderLabel, out _);

        message = new IncomingChatMessage(
            $"Player {senderKey:X8}",
            text,
            senderKey,
            category,
            metadata,
            receivedAt,
            CommunicationCue: cue);
        return true;
    }

    private static bool TryDecodeSenderLabel(
        ReadOnlySpan<byte> callbackState,
        out string senderLabel)
    {
        senderLabel = string.Empty;
        if (callbackState.Length < SenderLabelLengthOffset + sizeof(ulong))
            return false;

        var senderLabelLength = BinaryPrimitives.ReadUInt64LittleEndian(
            callbackState.Slice(SenderLabelLengthOffset, sizeof(ulong)));
        if (senderLabelLength > SenderLabelCapacity)
            return false;
        if (senderLabelLength == 0)
            return true;

        try
        {
            senderLabel = StrictUtf8.GetString(
                callbackState.Slice(SenderLabelOffset, checked((int)senderLabelLength)));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
