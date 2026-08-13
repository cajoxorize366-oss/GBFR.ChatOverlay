using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal enum RelinkIncomingChatAction
{
    PassOriginal = 0,
    PassRewritten = 1,
    Block = 2,
}

internal readonly record struct RelinkIncomingChatModerationResult(
    RelinkIncomingChatAction Action,
    IncomingChatMessage Message);

internal static class RelinkIncomingChatModerationPolicy
{
    internal static RelinkIncomingChatModerationResult Apply(
        ReadOnlySpan<byte> originalPacket,
        Span<byte> rewrittenPacket,
        in IncomingChatMessage message,
        in ChatModerationDecision decision)
    {
        if (decision.Disposition == ChatModerationDisposition.Block)
        {
            return new RelinkIncomingChatModerationResult(
                RelinkIncomingChatAction.Block,
                message);
        }

        if (string.Equals(decision.Text, message.Text, StringComparison.Ordinal))
        {
            return new RelinkIncomingChatModerationResult(
                RelinkIncomingChatAction.PassOriginal,
                message);
        }

        if (originalPacket.Length < RelinkChatPacketDecoder.PacketBytesToCopy ||
            rewrittenPacket.Length < RelinkChatPacketDecoder.PacketBytesToCopy)
        {
            return new RelinkIncomingChatModerationResult(
                decision.Matched
                    ? RelinkIncomingChatAction.Block
                    : RelinkIncomingChatAction.PassOriginal,
                message);
        }

        originalPacket[..RelinkChatPacketDecoder.PacketBytesToCopy].CopyTo(rewrittenPacket);
        if (!RelinkChatPacketDecoder.TryWriteRawText(rewrittenPacket, decision.Text))
        {
            return new RelinkIncomingChatModerationResult(
                decision.Matched
                    ? RelinkIncomingChatAction.Block
                    : RelinkIncomingChatAction.PassOriginal,
                message);
        }

        return new RelinkIncomingChatModerationResult(
            RelinkIncomingChatAction.PassRewritten,
            message with { Text = decision.Text });
    }
}
