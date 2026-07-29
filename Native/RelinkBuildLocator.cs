namespace GBFR.ChatOverlay.Native;

public readonly record struct RelinkChatRvas(int SendMessage, int RpcMessage, int ManagerSlot)
{
    public int SenderSlotResolver { get; init; }

    public int LobbyMemberLookup { get; init; }

    public int LobbyMemberManagerSlot { get; init; }

    public int PartyMemberIdentityManagerSlot { get; init; }

    public int SendStamp { get; init; }

    public int SendFixedPhrase { get; init; }

    public int SendEmotion { get; init; }

    public int PlayFixedPhrase { get; init; }

    public int PlayEmotion { get; init; }
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
    private const int ExpectedPartyMemberIdentityManagerSlotRva = 0x07C4A168;
    private const int ExpectedManagerInstructionRva = 0x025FAD2A;
    private const int ExpectedLobbyMemberCallsiteRva = 0x003CEE70;
    private const int ExpectedPartyMemberIdentityCallsiteRva = 0x003CE3FC;
    private const int ExpectedSendStampRva = 0x00908F50;
    private const int ExpectedSendFixedPhraseRva = 0x00909DE0;
    private const int ExpectedSendEmotionRva = 0x00908C90;
    private const int ExpectedPlayFixedPhraseRva = 0x006E93C0;
    private const int ExpectedPlayEmotionRva = 0x006E84F0;

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

    private static readonly SignaturePattern PartyMemberIdentityCallsitePattern = SignaturePattern.Parse(
        "48 8B 0D ?? ?? ?? ?? 44 8B 85 14 08 00 00 48 8D 05 ?? ?? ?? ?? " +
        "48 89 85 30 17 00 00 48 89 8D 38 17 00 00 48 8D 95 B0 15 00 00 " +
        "4C 8D 8D 30 17 00 00 E8 ?? ?? ?? ??");

    private static readonly SignaturePattern SendStampPattern = SignaturePattern.Parse(
        "55 41 57 41 56 56 57 53 48 83 EC 68 48 8D 6C 24 60 " +
        "48 C7 45 00 FE FF FF FF 89 D7 48 89 CE");

    private static readonly SignaturePattern SendFixedPhrasePattern = SignaturePattern.Parse(
        "41 57 41 56 41 54 56 57 55 53 48 81 EC C0 00 00 00 " +
        "44 89 CB 44 89 C6 89 D5 48 89 CF");

    private static readonly SignaturePattern SendEmotionPattern = SignaturePattern.Parse(
        "56 57 48 81 EC B8 01 00 00 89 CE 48 8D 7C 24 2D " +
        "48 8B 05 69 BB 34 07 8B 40 04");

    private static readonly SignaturePattern PlayFixedPhrasePattern = SignaturePattern.Parse(
        "55 41 57 41 56 41 55 41 54 56 57 53 48 81 EC 08 01 00 00 " +
        "48 8D AC 24 80 00 00 00 C5 F8 29 7D 70");

    private static readonly SignaturePattern PlayEmotionPattern = SignaturePattern.Parse(
        "55 41 57 41 56 41 55 41 54 56 57 53 48 81 EC 68 01 00 00 " +
        "48 8D AC 24 80 00 00 00 C5 78 29 85 D0 00 00 00");

    public static RelinkChatRvas Resolve(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        using var preflight = RelinkExecutablePreflight.Open(imagePath);
        preflight.RequirePattern(ExpectedSendMessageRva, SendMessagePattern, "chat sendMessage");
        preflight.RequirePattern(ExpectedRpcMessageRva, RpcMessagePattern, "chat rpcMessage");
        preflight.RequirePattern(
            ExpectedManagerInstructionRva,
            ManagerSlotPattern,
            "chat Manager global");
        preflight.RequirePattern(
            ExpectedSenderSlotResolverRva,
            SenderSlotResolverPattern,
            "incoming chat sender-to-slot resolver");
        preflight.RequirePattern(
            ExpectedLobbyMemberCallsiteRva,
            LobbyMemberLookupCallsitePattern,
            "lobby member name lookup callsite");
        preflight.RequirePattern(
            ExpectedPartyMemberIdentityCallsiteRva,
            PartyMemberIdentityCallsitePattern,
            "party member EntityId lookup callsite");
        preflight.RequirePattern(ExpectedSendStampRva, SendStampPattern, "communication stamp send");
        preflight.RequirePattern(
            ExpectedSendFixedPhraseRva,
            SendFixedPhrasePattern,
            "communication fixed-phrase send");
        preflight.RequirePattern(ExpectedSendEmotionRva, SendEmotionPattern, "communication emotion send");
        preflight.RequirePattern(
            ExpectedPlayFixedPhraseRva,
            PlayFixedPhrasePattern,
            "communication fixed-phrase local play");
        preflight.RequirePattern(ExpectedPlayEmotionRva, PlayEmotionPattern, "communication emotion local play");

        var sendRva = ExpectedSendMessageRva;
        var rpcRva = ExpectedRpcMessageRva;
        var displacement = preflight.ReadInt32(ExpectedManagerInstructionRva + 3);
        var managerSlotRva = checked(ExpectedManagerInstructionRva + 7 + displacement);
        var senderSlotResolverRva = ExpectedSenderSlotResolverRva;
        var lobbyMemberManagerDisplacement = preflight.ReadInt32(ExpectedLobbyMemberCallsiteRva + 3);
        var lobbyMemberManagerSlotRva = checked(
            ExpectedLobbyMemberCallsiteRva + 7 + lobbyMemberManagerDisplacement);
        var lobbyMemberLookupDisplacement = preflight.ReadInt32(ExpectedLobbyMemberCallsiteRva + 10);
        var lobbyMemberLookupRva = checked(
            ExpectedLobbyMemberCallsiteRva + 14 + lobbyMemberLookupDisplacement);
        var partyMemberIdentityManagerDisplacement = preflight.ReadInt32(
            ExpectedPartyMemberIdentityCallsiteRva + 3);
        var partyMemberIdentityManagerSlotRva = checked(
            ExpectedPartyMemberIdentityCallsiteRva + 7 + partyMemberIdentityManagerDisplacement);

        if (sendRva != ExpectedSendMessageRva ||
            rpcRva != ExpectedRpcMessageRva ||
            managerSlotRva != ExpectedManagerSlotRva ||
            senderSlotResolverRva != ExpectedSenderSlotResolverRva ||
            lobbyMemberLookupRva != ExpectedLobbyMemberLookupRva ||
            lobbyMemberManagerSlotRva != ExpectedLobbyMemberManagerSlotRva ||
            partyMemberIdentityManagerSlotRva != ExpectedPartyMemberIdentityManagerSlotRva)
        {
            throw new InvalidDataException(
                $"Relink chat signature validation failed: send={sendRva:X}, rpc={rpcRva:X}, " +
                $"manager={managerSlotRva:X}, senderSlot={senderSlotResolverRva:X}, " +
                $"memberLookup={lobbyMemberLookupRva:X}, " +
                $"memberManager={lobbyMemberManagerSlotRva:X}, " +
                $"partyMemberIdentityManager={partyMemberIdentityManagerSlotRva:X}.");
        }

        return new RelinkChatRvas(sendRva, rpcRva, managerSlotRva)
        {
            SenderSlotResolver = senderSlotResolverRva,
            LobbyMemberLookup = lobbyMemberLookupRva,
            LobbyMemberManagerSlot = lobbyMemberManagerSlotRva,
            PartyMemberIdentityManagerSlot = partyMemberIdentityManagerSlotRva,
            SendStamp = ExpectedSendStampRva,
            SendFixedPhrase = ExpectedSendFixedPhraseRva,
            SendEmotion = ExpectedSendEmotionRva,
            PlayFixedPhrase = ExpectedPlayFixedPhraseRva,
            PlayEmotion = ExpectedPlayEmotionRva,
        };
    }
}
