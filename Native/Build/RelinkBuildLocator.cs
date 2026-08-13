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

    public int LobbyOwnerImportThunk { get; init; }
}

public static class RelinkBuildLocator
{
    public const string SupportedSha256 =
        "f827f3c13caa90b290fab2fe7e28165a80448fde0a3f7a96d79dac6b8343ff2a";

    private const int ExpectedSendMessageRva = 0x009049F0;
    private const int ExpectedRpcMessageRva = 0x00B97950;
    private const int ExpectedManagerSlotRva = 0x07C23460;
    private const int ExpectedSenderSlotResolverRva = 0x006CD520;
    private const int ExpectedLobbyMemberLookupRva = 0x003760A0;
    private const int ExpectedLobbyMemberManagerSlotRva = 0x07C21AB8;
    private const int ExpectedPartyMemberIdentityManagerSlotRva = 0x07C483A8;
    private const int ExpectedManagerInstructionRva = 0x025F633A;
    private const int ExpectedLobbyMemberCallsiteRva = 0x003C81B0;
    private const int ExpectedPartyMemberIdentityCallsiteRva = 0x003C773C;
    private const int ExpectedLocalMemberSlotCallsiteRva = 0x009035D0;
    private const int ExpectedLocalMemberSlotCallRva = 0x006CD520;
    private const int ExpectedSendStampRva = 0x00903660;
    private const int ExpectedSendFixedPhraseRva = 0x009044F0;
    private const int ExpectedSendEmotionRva = 0x009033A0;
    private const int ExpectedPlayFixedPhraseRva = 0x006E3A00;
    private const int ExpectedPlayEmotionRva = 0x006E2B30;
    private const int ExpectedLobbyOwnerImportThunkRva = 0x049AD680;

    private static readonly SignaturePattern SendMessagePattern = SignaturePattern.Parse(
        "41 57 41 56 41 55 41 54 56 57 55 53 48 81 EC F8 02 00 00 " +
        "C5 F8 29 B4 24 E0 02 00 00 4D 89 CE 44 89 C5 48 89 D7 48 89 CE");

    private static readonly SignaturePattern RpcMessagePattern = SignaturePattern.Parse(
        "41 57 41 56 41 54 56 57 55 53 48 81 EC 20 01 00 00 48 89 CE " +
        "48 8B 05 ?? ?? ?? ?? 48 83 B8 58 01 00 00 00 48 8B 3D");

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

    private static readonly SignaturePattern LocalMemberSlotCallsitePattern = SignaturePattern.Parse(
        "48 8B 05 ?? ?? ?? ?? 0F B6 88 E8 CC 06 00 48 C1 E1 02 48 81 C9 28 C8 06 00 " +
        "8B 0C 08 C7 44 24 20 00 00 00 00 48 8D 54 24 20 E8 ?? ?? ?? ??");

    private static readonly SignaturePattern SendStampPattern = SignaturePattern.Parse(
        "55 41 57 41 56 56 57 53 48 83 EC 68 48 8D 6C 24 60 " +
        "48 C7 45 00 FE FF FF FF 89 D7 48 89 CE");

    private static readonly SignaturePattern SendFixedPhrasePattern = SignaturePattern.Parse(
        "41 57 41 56 41 54 56 57 55 53 48 81 EC C0 00 00 00 " +
        "44 89 CB 44 89 C6 89 D5 48 89 CF");

    private static readonly SignaturePattern SendEmotionPattern = SignaturePattern.Parse(
        "56 57 48 81 EC B8 01 00 00 89 CE 48 8D 7C 24 2D " +
        "48 8B 05 ?? ?? ?? ?? 8B 40 04");

    private static readonly SignaturePattern PlayFixedPhrasePattern = SignaturePattern.Parse(
        "55 41 57 41 56 41 55 41 54 56 57 53 48 81 EC 08 01 00 00 " +
        "48 8D AC 24 80 00 00 00 C5 F8 29 7D 70");

    private static readonly SignaturePattern PlayEmotionPattern = SignaturePattern.Parse(
        "55 41 57 41 56 41 55 41 54 56 57 53 48 81 EC 68 01 00 00 " +
        "48 8D AC 24 80 00 00 00 C5 78 29 85 D0 00 00 00");

    private static readonly SignaturePattern LobbyOwnerImportThunkPattern = SignaturePattern.Parse(
        "FF 25 42 4C 91 01");

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
        preflight.RequirePattern(
            ExpectedLocalMemberSlotCallsiteRva,
            LocalMemberSlotCallsitePattern,
            "authoritative local member slot callsite");
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
        preflight.RequirePattern(
            ExpectedLobbyOwnerImportThunkRva,
            LobbyOwnerImportThunkPattern,
            "PlayFab lobby-owner import thunk");

        var displacement = preflight.ReadInt32(ExpectedManagerInstructionRva + 3);
        var managerSlotRva = checked(ExpectedManagerInstructionRva + 7 + displacement);
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
        var localMemberSlotManagerDisplacement = preflight.ReadInt32(
            ExpectedLocalMemberSlotCallsiteRva + 3);
        var localMemberSlotManagerSlotRva = checked(
            ExpectedLocalMemberSlotCallsiteRva + 7 + localMemberSlotManagerDisplacement);
        var localMemberSlotCallDisplacement = preflight.ReadInt32(
            ExpectedLocalMemberSlotCallsiteRva + 42);
        var localMemberSlotCallRva = checked(
            ExpectedLocalMemberSlotCallsiteRva + 46 + localMemberSlotCallDisplacement);

        if (managerSlotRva != ExpectedManagerSlotRva ||
            lobbyMemberLookupRva != ExpectedLobbyMemberLookupRva ||
            lobbyMemberManagerSlotRva != ExpectedLobbyMemberManagerSlotRva ||
            partyMemberIdentityManagerSlotRva != ExpectedPartyMemberIdentityManagerSlotRva ||
            localMemberSlotManagerSlotRva != ExpectedPartyMemberIdentityManagerSlotRva ||
            localMemberSlotCallRva != ExpectedLocalMemberSlotCallRva)
        {
            throw new InvalidDataException(
                $"Relink chat derived-target validation failed: manager={managerSlotRva:X}, " +
                $"memberLookup={lobbyMemberLookupRva:X}, " +
                $"memberManager={lobbyMemberManagerSlotRva:X}, " +
                $"partyMemberIdentityManager={partyMemberIdentityManagerSlotRva:X}, " +
                $"localSlotManager={localMemberSlotManagerSlotRva:X}, " +
                $"localSlotCall={localMemberSlotCallRva:X}.");
        }

        return new RelinkChatRvas(ExpectedSendMessageRva, ExpectedRpcMessageRva, managerSlotRva)
        {
            SenderSlotResolver = ExpectedSenderSlotResolverRva,
            LobbyMemberLookup = lobbyMemberLookupRva,
            LobbyMemberManagerSlot = lobbyMemberManagerSlotRva,
            PartyMemberIdentityManagerSlot = partyMemberIdentityManagerSlotRva,
            SendStamp = ExpectedSendStampRva,
            SendFixedPhrase = ExpectedSendFixedPhraseRva,
            SendEmotion = ExpectedSendEmotionRva,
            PlayFixedPhrase = ExpectedPlayFixedPhraseRva,
            PlayEmotion = ExpectedPlayEmotionRva,
            LobbyOwnerImportThunk = ExpectedLobbyOwnerImportThunkRva,
        };
    }
}
