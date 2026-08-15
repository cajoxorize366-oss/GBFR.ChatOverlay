using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Core;
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
    public void TryDecode_ReadsRawUtf8MessageAndKeepsOpaqueSenderFallback()
    {
        var packet = CreatePacket("你好，骑空士", "Djeeta", 0x1234, 7, 9);
        var timestamp = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);

        var decoded = RelinkChatPacketDecoder.TryDecode(
            packet,
            timestamp,
            out var message);

        Assert.True(decoded);
        Assert.Equal("你好，骑空士", message.Text);
        Assert.Equal("Player 00001234", message.Sender);
        Assert.Equal(0x1234u, message.SenderId);
        Assert.Equal(7u, message.Category);
        Assert.Equal(9u, message.Metadata);
        Assert.Equal(timestamp, message.ReceivedAt);
    }

    [Theory]
    [InlineData("vo_CMM_chance", ChatCommunicationCue.LinkAttack)]
    [InlineData("vo_CMM_thanks", ChatCommunicationCue.Thanks)]
    [InlineData("vo_CMM_win", ChatCommunicationCue.Victory)]
    [InlineData("vo_CMM_win_3", ChatCommunicationCue.Victory)]
    [InlineData("vo_CMM_chance_start", ChatCommunicationCue.LinkAttack)]
    [InlineData("vo_CMM_thanks_short", ChatCommunicationCue.Thanks)]
    [InlineData("VO_CMM_ChAnCe", ChatCommunicationCue.LinkAttack)]
    [InlineData("vo_CMM_win_quest_clear", ChatCommunicationCue.Victory)]
    [InlineData("PL1800_VO_CMM_CHANCE", ChatCommunicationCue.LinkAttack)]
    [InlineData("pl1800_vo_cmm_thanks", ChatCommunicationCue.Thanks)]
    [InlineData("PL1800_VO_CMM_WIN", ChatCommunicationCue.Victory)]
    [InlineData("PL1800_VO_CMM_WIN_3", ChatCommunicationCue.Victory)]
    [InlineData("PL0_VO_CMM_WIN", ChatCommunicationCue.Victory)]
    [InlineData("\uFEFFPL1800_VO_CMM_WIN_3", ChatCommunicationCue.Victory)]
    [InlineData("PL1800_VO_CMM_SPEC", ChatCommunicationCue.Official)]
    [InlineData("\uFEFFvo_CMM_win_3", ChatCommunicationCue.Victory)]
    [InlineData("\u200Bvo_CMM_chance", ChatCommunicationCue.LinkAttack)]
    [InlineData("\u0001vo_CMM_thanks", ChatCommunicationCue.Thanks)]
    public void TryDecode_MachineCueLabelsAreNotPlayerNames(
        string senderLabel,
        ChatCommunicationCue expectedCue)
    {
        var packet = CreatePacket("hello", senderLabel, 0x1234, 7, 9);

        Assert.True(RelinkChatPacketDecoder.TryDecode(
            packet,
            DateTimeOffset.UtcNow,
            out var message));
        Assert.Equal("Player 00001234", message.Sender);
        Assert.Equal("hello", message.Text);
        Assert.Equal(expectedCue, message.CommunicationCue);
    }

    [Fact]
    public void TryDecode_UnknownMachineCuePrefixUsesGenericOfficialCue()
    {
        var packet = CreatePacket("hello", "vo_CMM_unknown_action", 0x89ABCDEF, 0, 0);

        Assert.True(RelinkChatPacketDecoder.TryDecode(
            packet,
            DateTimeOffset.UtcNow,
            out var message));
        Assert.Equal("Player 89ABCDEF", message.Sender);
        Assert.Equal(ChatCommunicationCue.Official, message.CommunicationCue);
    }

    [Fact]
    public void TryDecode_PlayerNameLikeMachineCueTokenNeverBecomesSender()
    {
        var packet = CreatePacket("hello", "Kuro_vo_CMM_win_3", 0x1234, 7, 9);

        Assert.True(RelinkChatPacketDecoder.TryDecode(
            packet,
            DateTimeOffset.UtcNow,
            out var message));
        Assert.Equal("Player 00001234", message.Sender);
        Assert.Equal(ChatCommunicationCue.None, message.CommunicationCue);
    }

    [Theory]
    [InlineData("Kuro_vo_CMM_win_3")]
    [InlineData("_vo_CMM_emo_win")]
    [InlineData("PLX_VO_CMM_WIN")]
    [InlineData("PL1800X_VO_CMM_WIN")]
    [InlineData("PL1800_VO_CMM")]
    [InlineData("vo_CMM_")]
    public void TryDecode_RejectsEmbeddedAndMalformedMachineCueMarkers(string senderLabel)
    {
        var packet = CreatePacket("hello", senderLabel, 0x1234, 7, 9);

        Assert.True(RelinkChatPacketDecoder.TryDecode(
            packet,
            DateTimeOffset.UtcNow,
            out var message));
        Assert.Equal("Player 00001234", message.Sender);
        Assert.Equal(ChatCommunicationCue.None, message.CommunicationCue);
    }

    [Fact]
    public void TryDecode_MachineCueWithZeroSenderIdNeverLeaksRawLabel()
    {
        var packet = CreatePacket("普通聊天文本", "vo_CMM_win_3", 0, 7, 9);

        Assert.True(RelinkChatPacketDecoder.TryDecode(
            packet,
            DateTimeOffset.UtcNow,
            out var message));
        Assert.Equal("Player 00000000", message.Sender);
        Assert.Equal("普通聊天文本", message.Text);
        Assert.Equal(0u, message.SenderId);
        Assert.Equal(ChatCommunicationCue.Victory, message.CommunicationCue);
    }

    [Theory]
    [InlineData("Djeeta")]
    [InlineData("trick")]
    public void TryDecode_NormalShortLabelsNeverBecomeSender(string senderLabel)
    {
        var packet = CreatePacket("hello", senderLabel, 0x1234, 7, 9);

        Assert.True(RelinkChatPacketDecoder.TryDecode(
            packet,
            DateTimeOffset.UtcNow,
            out var message));
        Assert.Equal("Player 00001234", message.Sender);
        Assert.Equal(ChatCommunicationCue.None, message.CommunicationCue);
    }

    [Fact]
    public void TryDecode_UsesStableFallbackWhenSenderLabelIsEmpty()
    {
        var packet = CreatePacket("hello", string.Empty, 0x89ABCDEF, 0, 0);

        Assert.True(RelinkChatPacketDecoder.TryDecode(
            packet,
            DateTimeOffset.UtcNow,
            out var message));
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

    [Fact]
    public void TryWriteRawText_RewritesOnlyTheMessageBuffer()
    {
        var packet = CreatePacket("original", "Djeeta", 0x1234, 7, 9);
        var beforePrefix = packet.AsSpan(0, 0x1C).ToArray();
        var beforeSuffix = packet.AsSpan(0x17C).ToArray();

        Assert.True(RelinkChatPacketDecoder.TryWriteRawText(packet, "中***文"));
        Assert.True(RelinkChatPacketDecoder.TryDecode(
            packet,
            DateTimeOffset.UtcNow,
            out var decoded));
        Assert.Equal("中***文", decoded.Text);
        Assert.Equal(beforePrefix, packet.AsSpan(0, 0x1C).ToArray());
        Assert.Equal(beforeSuffix, packet.AsSpan(0x17C).ToArray());
        var rewrittenByteCount = Encoding.UTF8.GetByteCount("中***文");
        Assert.All(packet.AsSpan(0x1C + rewrittenByteCount, 0x160 - rewrittenByteCount).ToArray(),
            value => Assert.Equal(0, value));
    }

    [Fact]
    public void TryWriteRawText_RejectsNonRawOversizedAndInvalidUtf16WithoutMutation()
    {
        var nonRaw = CreatePacket("original", "Djeeta", 1, 0, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(nonRaw.AsSpan(0x17C, 4), 0x12345678);
        var nonRawBefore = nonRaw.ToArray();
        Assert.False(RelinkChatPacketDecoder.TryWriteRawText(nonRaw, "masked"));
        Assert.Equal(nonRawBefore, nonRaw);

        var oversized = CreatePacket("original", "Djeeta", 1, 0, 0);
        var oversizedBefore = oversized.ToArray();
        Assert.False(RelinkChatPacketDecoder.TryWriteRawText(
            oversized,
            new string('a', RelinkChatPacketDecoder.MaximumMessageBytes + 1)));
        Assert.Equal(oversizedBefore, oversized);

        var invalidUtf16 = CreatePacket("original", "Djeeta", 1, 0, 0);
        var invalidBefore = invalidUtf16.ToArray();
        Assert.False(RelinkChatPacketDecoder.TryWriteRawText(invalidUtf16, "\uD800"));
        Assert.Equal(invalidBefore, invalidUtf16);
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
