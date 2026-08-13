using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkIncomingChatModerationPolicyTests
{
    [Fact]
    public void Apply_BlockSkipsPacketRewrite()
    {
        var packet = CreatePacket("blocked");
        var rewritten = new byte[RelinkChatPacketDecoder.PacketBytesToCopy];
        var message = Decode(packet);
        var decision = new ChatModerationDecision(
            ChatModerationDisposition.Block,
            "blocked",
            true,
            false,
            false,
            []);

        var result = RelinkIncomingChatModerationPolicy.Apply(
            packet,
            rewritten,
            message,
            decision);

        Assert.Equal(RelinkIncomingChatAction.Block, result.Action);
        Assert.All(rewritten, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Apply_MaskBuildsCopyAndReturnsSameTextForOverlay()
    {
        var packet = CreatePacket("bad 文本");
        var original = packet.ToArray();
        var rewritten = new byte[RelinkChatPacketDecoder.PacketBytesToCopy];
        var message = Decode(packet);
        var decision = new ChatModerationDecision(
            ChatModerationDisposition.Mask,
            "*** 文本",
            true,
            false,
            false,
            ["rule"]);

        var result = RelinkIncomingChatModerationPolicy.Apply(
            packet,
            rewritten,
            message,
            decision);

        Assert.Equal(RelinkIncomingChatAction.PassRewritten, result.Action);
        Assert.Equal("*** 文本", result.Message.Text);
        Assert.Equal(original, packet);
        Assert.Equal("*** 文本", Decode(rewritten).Text);
        Assert.Equal(
            original.AsSpan(0x17C).ToArray(),
            rewritten.AsSpan(0x17C).ToArray());
    }

    [Fact]
    public void Apply_ConfirmedMatchWithUnencodableReplacementBlocksOriginal()
    {
        var packet = CreatePacket("original");
        var rewritten = new byte[RelinkChatPacketDecoder.PacketBytesToCopy];
        var message = Decode(packet);
        var decision = new ChatModerationDecision(
            ChatModerationDisposition.Mask,
            new string('x', RelinkChatPacketDecoder.MaximumMessageBytes + 1),
            true,
            true,
            false,
            []);

        var result = RelinkIncomingChatModerationPolicy.Apply(
            packet,
            rewritten,
            message,
            decision);

        Assert.Equal(RelinkIncomingChatAction.Block, result.Action);
        Assert.Equal("original", result.Message.Text);
    }

    private static IncomingChatMessage Decode(byte[] packet)
    {
        Assert.True(RelinkChatPacketDecoder.TryDecode(
            packet,
            DateTimeOffset.UtcNow,
            out var message));
        return message;
    }

    private static byte[] CreatePacket(string text)
    {
        var packet = new byte[RelinkChatPacketDecoder.PacketBytesToCopy];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x18, 4), 0x1234);
        Encoding.UTF8.GetBytes(text).CopyTo(packet.AsSpan(0x1C));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(0x17C, 4),
            RelinkChatPacketDecoder.RawTextHash);
        return packet;
    }
}
