using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

public static class RelinkChatPacketDecoder
{
    public const int PacketBytesToCopy = 0x1A0;
    public const int MaximumMessageBytes = 0x15D;
    public const uint RawTextHash = 0x887AE0B0;

    private const int SenderIdOffset = 0x18;
    private const int MessageOffset = 0x1C;
    private const int MessageBufferSize = 0x160;
    private const int MessageHashOffset = 0x17C;
    private const int SenderLabelOffset = 0x180;
    private const int SenderLabelBufferSize = 0x18;
    private const int CategoryOffset = 0x198;
    private const int MetadataOffset = 0x19C;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static bool TryReadSenderId(ReadOnlySpan<byte> packet, out uint senderId)
    {
        senderId = 0;
        if (packet.Length < SenderIdOffset + sizeof(uint))
            return false;
        senderId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(SenderIdOffset, sizeof(uint)));
        return true;
    }

    internal static bool TryDecodeOutgoingText(ReadOnlySpan<byte> encoded, out string text)
    {
        text = string.Empty;
        if (encoded.IsEmpty || encoded.Length > MaximumMessageBytes || encoded.Contains((byte)0))
            return false;

        try
        {
            text = StrictUtf8.GetString(encoded);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = string.Empty;
            return false;
        }

        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> packet,
        DateTimeOffset receivedAt,
        out IncomingChatMessage message) =>
        TryDecode(packet, receivedAt, out message, out _);

    internal static bool TryDecode(
        ReadOnlySpan<byte> packet,
        DateTimeOffset receivedAt,
        out IncomingChatMessage message,
        out bool hasExplicitSenderLabel)
    {
        message = default;
        hasExplicitSenderLabel = false;
        if (packet.Length < PacketBytesToCopy)
            return false;

        var messageHash = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(MessageHashOffset, sizeof(uint)));
        if (messageHash != RawTextHash)
            return false;

        if (!TryDecodeNullTerminated(
                packet.Slice(MessageOffset, MessageBufferSize),
                MaximumMessageBytes,
                out var text) ||
            string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        _ = TryReadSenderId(packet, out var senderId);
        var decodedSenderLabel = TryDecodeNullTerminated(
            packet.Slice(SenderLabelOffset, SenderLabelBufferSize),
            SenderLabelBufferSize - 1,
            out var senderLabel);

        var cue = ClassifyCommunicationCue(senderLabel, out var isMachineCue);
        hasExplicitSenderLabel = decodedSenderLabel && !string.IsNullOrWhiteSpace(senderLabel) && !isMachineCue;
        var sender = !hasExplicitSenderLabel
            ? $"Player {senderId:X8}"
            : senderLabel.Trim();
        var category = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(CategoryOffset, sizeof(uint)));
        var metadata = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(MetadataOffset, sizeof(uint)));
        message = new IncomingChatMessage(sender, text, senderId, category, metadata, receivedAt, CommunicationCue: cue);
        return true;
    }

    internal static ChatCommunicationCue ClassifyCommunicationCue(string? senderLabel, out bool isMachineCue)
    {
        isMachineCue = ChatCommunicationCueClassifier.TryClassifySenderLabel(
            senderLabel,
            out var communicationCue);
        return communicationCue;
    }

    private static bool TryDecodeNullTerminated(
        ReadOnlySpan<byte> buffer,
        int maximumLength,
        out string text)
    {
        text = string.Empty;
        var terminator = buffer.IndexOf((byte)0);
        if (terminator < 0 || terminator > maximumLength)
            return false;

        try
        {
            text = StrictUtf8.GetString(buffer[..terminator]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
