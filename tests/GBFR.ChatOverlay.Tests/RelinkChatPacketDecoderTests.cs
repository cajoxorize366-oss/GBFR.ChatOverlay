using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkChatPacketDecoderTests
{
    [Fact]
    public void TryDecodeOutgoingText_AcceptsStrictUtf8FromTheNativeGameInput()
    {
        var encoded = Encoding.UTF8.GetBytes("电脑自带输入法");

        Assert.True(RelinkChatPacketDecoder.TryDecodeOutgoingText(encoded, out var text));
        Assert.Equal("电脑自带输入法", text);
    }

    [Fact]
    public void TryDecodeOutgoingText_RejectsInvalidUtf8AndEmbeddedNul()
    {
        Assert.False(RelinkChatPacketDecoder.TryDecodeOutgoingText([0xC3, 0x28], out _));
        Assert.False(RelinkChatPacketDecoder.TryDecodeOutgoingText("a\0b"u8, out _));
    }

    [Fact]
    public void TryDecode_ReadsRawUtf8MessageAndSenderLabel()
    {
        var packet = CreatePacket("你好，骑空士", "Djeeta", 0x1234, 7, 9);
        var timestamp = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);

        var decoded = RelinkChatPacketDecoder.TryDecode(
            packet,
            timestamp,
            out var message,
            out var hasExplicitSenderLabel);

        Assert.True(decoded);
        Assert.True(hasExplicitSenderLabel);
        Assert.Equal("你好，骑空士", message.Text);
        Assert.Equal("Djeeta", message.Sender);
        Assert.Equal(0x1234u, message.SenderId);
        Assert.Equal(7u, message.Category);
        Assert.Equal(9u, message.Metadata);
        Assert.Equal(timestamp, message.ReceivedAt);
    }

    [Fact]
    public void TryDecode_UsesStableFallbackWhenSenderLabelIsEmpty()
    {
        var packet = CreatePacket("hello", string.Empty, 0x89ABCDEF, 0, 0);

        Assert.True(RelinkChatPacketDecoder.TryDecode(
            packet,
            DateTimeOffset.UtcNow,
            out var message,
            out var hasExplicitSenderLabel));
        Assert.False(hasExplicitSenderLabel);
        Assert.Equal("Player 89ABCDEF", message.Sender);
    }

    [Fact]
    public void TryDecode_RejectsHashedQuickMessageUntilResolverExists()
    {
        var packet = CreatePacket("ignored", "Rackam", 1, 0, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x17C, 4), 0x12345678);

        Assert.False(RelinkChatPacketDecoder.TryDecode(packet, DateTimeOffset.UtcNow, out _));
        Assert.True(RelinkChatPacketDecoder.TryReadSenderId(packet, out var senderId));
        Assert.Equal(1u, senderId);
    }

    [Fact]
    public void TryDecode_RejectsInvalidUtf8AndMissingTerminator()
    {
        var invalid = CreatePacket("hello", "Io", 1, 0, 0);
        invalid[0x1C] = 0xC3;
        invalid[0x1D] = 0x28;
        invalid[0x1E] = 0;
        Assert.False(RelinkChatPacketDecoder.TryDecode(invalid, DateTimeOffset.UtcNow, out _));

        var unterminated = CreatePacket("hello", "Io", 1, 0, 0);
        unterminated.AsSpan(0x1C, 0x160).Fill((byte)'A');
        Assert.False(RelinkChatPacketDecoder.TryDecode(unterminated, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void TryDecode_RejectsShortPacket()
    {
        var packet = new byte[RelinkChatPacketDecoder.PacketBytesToCopy - 1];

        Assert.False(RelinkChatPacketDecoder.TryDecode(packet, DateTimeOffset.UtcNow, out _));
    }

    private static byte[] CreatePacket(
        string text,
        string sender,
        uint senderId,
        uint category,
        uint metadata)
    {
        var packet = new byte[RelinkChatPacketDecoder.PacketBytesToCopy];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x18, 4), senderId);
        Encoding.UTF8.GetBytes(text).CopyTo(packet.AsSpan(0x1C));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(0x17C, 4),
            RelinkChatPacketDecoder.RawTextHash);
        Encoding.UTF8.GetBytes(sender).CopyTo(packet.AsSpan(0x180));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x198, 4), category);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x19C, 4), metadata);
        return packet;
    }
}
