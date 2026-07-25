using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace GBFR.ChatOverlay.Native;

public readonly record struct RelinkChatRvas(int SendMessage, int RpcMessage, int ManagerSlot)
{
    public int SenderSlotResolver { get; init; }

    public int LobbyMemberLookup { get; init; }

    public int LobbyMemberManagerSlot { get; init; }
}

public static class RelinkBuildLocator
{
    public const string SupportedSha256 =
        "63340832bcf731fbc97796f686b05c988418e83d451d4a49b2244a85d00e297f";

    private const int ExpectedSendMessageRva = 0x0090A2E0;
    private const int ExpectedRpcMessageRva = 0x00B9D230;
    private const int ExpectedManagerSlotRva = 0x07C25220;
    private const int ExpectedSenderSlotResolverRva = 0x006D2EE0;
    private const int ExpectedLobbyMemberLookupRva = 0x0037CDD0;
    private const int ExpectedLobbyMemberManagerSlotRva = 0x07C23878;

    private static readonly SignaturePattern SendMessagePattern = SignaturePattern.Parse(
        "41 57 41 56 41 55 41 54 56 57 55 53 48 81 EC F8 02 00 00 " +
        "C5 F8 29 B4 24 E0 02 00 00 4D 89 CE 44 89 C5 48 89 D7 48 89 CE");

    private static readonly SignaturePattern RpcMessagePattern = SignaturePattern.Parse(
        "41 57 41 56 41 54 56 57 55 53 48 81 EC 20 01 00 00 48 89 CE " +
        "48 8B 05 C5 75 0B 07 48 83 B8 58 01 00 00 00 48 8B 3D");

    private static readonly SignaturePattern ManagerSlotPattern = SignaturePattern.Parse(
        "48 8B 3D ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? 48 89 44 24 38 " +
        "48 C7 44 24 40 00 00 00 00 48 89 74 24 28 48 89 F1 E8 ?? ?? ?? ??");

    private static readonly SignaturePattern SenderSlotResolverPattern = SignaturePattern.Parse(
        "41 57 41 56 41 54 56 57 55 53 48 83 EC 20 48 89 D6 89 CF 40 B5 01 " +
        "4C 8D 3D ?? ?? ?? ?? 45 31 E4 48 8D 1D ?? ?? ?? ?? EB 25 " +
        "0F 1F 80 00 00 00 00 49 83 FC 03 49 8D 44 24 01 40 0F 92 C5 " +
        "49 83 C7 08 49 89 C4 48 83 F8 04 0F 84 8E 00 00 00 4D 8B 37");

    private static readonly SignaturePattern LobbyMemberLookupCallsitePattern = SignaturePattern.Parse(
        "48 8B 0D ?? ?? ?? ?? 89 F2 E8 ?? ?? ?? ?? 49 89 C6 " +
        "80 B8 BC 5E 00 00 00 74 ?? 49 8B 86 60 5E 00 00");

    public static RelinkChatRvas Resolve(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);

        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, SupportedSha256, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Unsupported Relink executable SHA-256 {actualHash}; expected {SupportedSha256}.");
        }

        stream.Position = 0;
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var textSection = peReader.PEHeaders.SectionHeaders
            .SingleOrDefault(section => string.Equals(section.Name, ".text", StringComparison.Ordinal));
        if (textSection.Name is null)
            throw new InvalidDataException("The Relink executable has no .text section.");

        var text = new byte[textSection.SizeOfRawData];
        stream.Position = textSection.PointerToRawData;
        stream.ReadExactly(text);

        var sendOffset = SendMessagePattern.FindUniqueOffset(text, "sendMessage");
        var rpcOffset = RpcMessagePattern.FindUniqueOffset(text, "rpcMessage");
        var managerInstructionOffset = ManagerSlotPattern.FindUniqueOffset(text, "chat Manager global");
        var senderSlotResolverOffset = SenderSlotResolverPattern.FindUniqueOffset(
            text,
            "incoming chat sender-to-slot resolver");
        var lobbyMemberCallsiteOffset = LobbyMemberLookupCallsitePattern.FindUniqueOffset(
            text,
            "lobby member name lookup callsite");

        var sendRva = checked(textSection.VirtualAddress + sendOffset);
        var rpcRva = checked(textSection.VirtualAddress + rpcOffset);
        var managerInstructionRva = checked(textSection.VirtualAddress + managerInstructionOffset);
        var displacement = BinaryPrimitives.ReadInt32LittleEndian(
            text.AsSpan(managerInstructionOffset + 3, sizeof(int)));
        var managerSlotRva = checked(managerInstructionRva + 7 + displacement);
        var senderSlotResolverRva = checked(textSection.VirtualAddress + senderSlotResolverOffset);
        var lobbyMemberCallsiteRva = checked(textSection.VirtualAddress + lobbyMemberCallsiteOffset);
        var lobbyMemberManagerDisplacement = BinaryPrimitives.ReadInt32LittleEndian(
            text.AsSpan(lobbyMemberCallsiteOffset + 3, sizeof(int)));
        var lobbyMemberManagerSlotRva = checked(
            lobbyMemberCallsiteRva + 7 + lobbyMemberManagerDisplacement);
        var lobbyMemberLookupDisplacement = BinaryPrimitives.ReadInt32LittleEndian(
            text.AsSpan(lobbyMemberCallsiteOffset + 10, sizeof(int)));
        var lobbyMemberLookupRva = checked(
            lobbyMemberCallsiteRva + 14 + lobbyMemberLookupDisplacement);

        if (sendRva != ExpectedSendMessageRva ||
            rpcRva != ExpectedRpcMessageRva ||
            managerSlotRva != ExpectedManagerSlotRva ||
            senderSlotResolverRva != ExpectedSenderSlotResolverRva ||
            lobbyMemberLookupRva != ExpectedLobbyMemberLookupRva ||
            lobbyMemberManagerSlotRva != ExpectedLobbyMemberManagerSlotRva)
        {
            throw new InvalidDataException(
                $"Relink chat signature validation failed: send={sendRva:X}, rpc={rpcRva:X}, " +
                $"manager={managerSlotRva:X}, senderSlot={senderSlotResolverRva:X}, " +
                $"memberLookup={lobbyMemberLookupRva:X}, " +
                $"memberManager={lobbyMemberManagerSlotRva:X}.");
        }

        return new RelinkChatRvas(sendRva, rpcRva, managerSlotRva)
        {
            SenderSlotResolver = senderSlotResolverRva,
            LobbyMemberLookup = lobbyMemberLookupRva,
            LobbyMemberManagerSlot = lobbyMemberManagerSlotRva,
        };
    }
}
