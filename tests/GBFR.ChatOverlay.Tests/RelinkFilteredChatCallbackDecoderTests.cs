using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkFilteredChatCallbackDecoderTests
{
    [Theory]
    [InlineData("vo_CMM_chance", ChatCommunicationCue.LinkAttack)]
    [InlineData("vo_CMM_thanks", ChatCommunicationCue.Thanks)]
    [InlineData("PL1800_VO_CMM_THANKS", ChatCommunicationCue.Thanks)]
    [InlineData("PL1800_VO_CMM_WIN_3", ChatCommunicationCue.Victory)]
    [InlineData("", ChatCommunicationCue.None)]
    public void TryDecodeSendCue_UsesTheCurrentCallbackClosure(
        string senderLabel,
        ChatCommunicationCue expected)
    {
        var state = CreateCallbackState(
            RelinkFilteredChatCallbackDecoder.SendCallbackStateBytes,
            0x1234ABCD,
            7,
            0,
            senderLabel);

        Assert.True(RelinkFilteredChatCallbackDecoder.TryDecodeSendCue(state, out var cue));
        Assert.Equal(expected, cue);
    }

    [Fact]
    public void TryDecodeReceive_ReadsFinalTextAndCapturedMessageMetadata()
    {
        var state = CreateState(
            senderKey: 0x1234ABCD,
            category: 7,
            metadata: 9,
            senderLabel: "vo_CMM_001");
        var receivedAt = new DateTimeOffset(2026, 8, 14, 20, 0, 0, TimeSpan.FromHours(8));

        Assert.True(RelinkFilteredChatCallbackDecoder.TryDecodeReceive(
            state,
            Encoding.UTF8.GetBytes("native filtered text"),
            receivedAt,
            out var message));

        Assert.Equal("Player 1234ABCD", message.Sender);
        Assert.Equal("native filtered text", message.Text);
        Assert.Equal(0x1234ABCDu, message.SenderId);
        Assert.Equal(7u, message.Category);
        Assert.Equal(9u, message.Metadata);
        Assert.Equal(receivedAt, message.ReceivedAt);
        Assert.NotEqual(ChatCommunicationCue.None, message.CommunicationCue);
    }

    [Fact]
    public void TryDecodeReceive_ReadsRelinkCharacterVoiceResourceLabel()
    {
        var state = CreateState(
            senderKey: 0x1234ABCD,
            category: 7,
            metadata: 9,
            senderLabel: "PL1800_VO_CMM_WIN_3");

        Assert.True(RelinkFilteredChatCallbackDecoder.TryDecodeReceive(
            state,
            "native filtered text"u8,
            DateTimeOffset.UtcNow,
            out var message));
        Assert.Equal(ChatCommunicationCue.Victory, message.CommunicationCue);
    }

    [Fact]
    public void TryDecodeReceive_AcceptsCrossPlatformMemberKeyWithoutPlatformMetadata()
    {
        var state = CreateState(
            senderKey: 0xDEADBEEF,
            category: 0,
            metadata: uint.MaxValue,
            senderLabel: string.Empty);

        Assert.True(RelinkFilteredChatCallbackDecoder.TryDecodeReceive(
            state,
            "跨平台文字"u8,
            DateTimeOffset.UtcNow,
            out var message));

        Assert.Equal(0xDEADBEEFu, message.SenderId);
        Assert.Equal(ChatCommunicationCue.None, message.CommunicationCue);
    }

    [Fact]
    public void TryDecodeReceive_RejectsInvalidStateTextAndLabel()
    {
        var validState = CreateState(1, 2, 3, string.Empty);
        var oversizedLabelState = CreateState(1, 2, 3, string.Empty);
        BinaryPrimitives.WriteUInt64LittleEndian(oversizedLabelState.AsSpan(0x58, 8), 0x41);

        Assert.False(RelinkFilteredChatCallbackDecoder.TryDecodeReceive(
            validState.AsSpan(0, validState.Length - 1),
            "text"u8,
            DateTimeOffset.UtcNow,
            out _));
        Assert.False(RelinkFilteredChatCallbackDecoder.TryDecodeReceive(
            validState,
            [0xC3, 0x28],
            DateTimeOffset.UtcNow,
            out _));
        Assert.False(RelinkFilteredChatCallbackDecoder.TryDecodeReceive(
            oversizedLabelState,
            "text"u8,
            DateTimeOffset.UtcNow,
            out _));
    }

    private static byte[] CreateState(
        uint senderKey,
        uint category,
        uint metadata,
        string senderLabel) =>
        CreateCallbackState(
            RelinkFilteredChatCallbackDecoder.ReceiveCallbackStateBytes,
            senderKey,
            category,
            metadata,
            senderLabel);

    private static byte[] CreateCallbackState(
        int stateBytes,
        uint senderKey,
        uint category,
        uint metadata,
        string senderLabel)
    {
        var state = new byte[stateBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(0x10, 4), senderKey);
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(0x14, 4), category);
        if (stateBytes >= 0x64)
            BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(0x60, 4), metadata);

        var labelBytes = Encoding.UTF8.GetBytes(senderLabel);
        Assert.True(labelBytes.Length <= 0x40);
        labelBytes.CopyTo(state.AsSpan(0x18));
        BinaryPrimitives.WriteUInt64LittleEndian(state.AsSpan(0x58, 8), (ulong)labelBytes.Length);
        return state;
    }
}
