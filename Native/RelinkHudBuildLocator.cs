using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace GBFR.ChatOverlay.Native;

internal readonly record struct RelinkHudRvas(
    int TownFactory,
    int TownDestructor,
    int BattleFactory,
    int BattleDestructor,
    int PauseTopFactory,
    int PauseTopDestructor,
    int UiManagerSlot);

internal static class RelinkHudBuildLocator
{
    private const int ExpectedTownFactoryRva = 0x02594A10;
    private const int ExpectedTownDestructorRva = 0x025960B0;
    private const int ExpectedBattleFactoryRva = 0x02608DA0;
    private const int ExpectedBattleDestructorRva = 0x0260A200;
    private const int ExpectedPauseTopFactoryRva = 0x0318C710;
    private const int ExpectedPauseTopDestructorRva = 0x00BBBF30;
    private const int ExpectedUiObjectQueryRva = 0x0261DDE0;
    private const int ExpectedUiManagerSlotRva = 0x07C02358;
    private const int UiManagerInstructionOffset = 63;
    private const int PauseTopVtableInstructionOffset = 0x192;

    private static readonly SignaturePattern TownFactoryPattern = SignaturePattern.Parse(
        "56 57 48 83 EC 28 48 89 D6 8B 05 ?? ?? ?? ?? " +
        "65 48 8B 0C 25 58 00 00 00 48 8B 04 C1 48 8B 88 30 16 00 00 " +
        "BA 88 05 00 00 41 B8 08 00 00 00 E8 ?? ?? ?? ??");

    private static readonly SignaturePattern BattleFactoryPattern = SignaturePattern.Parse(
        "56 48 83 EC 20 48 89 D6 8B 05 ?? ?? ?? ?? " +
        "65 48 8B 0C 25 58 00 00 00 48 8B 04 C1 48 8B 88 30 16 00 00 " +
        "BA 80 08 00 00 41 B8 08 00 00 00 E8 ?? ?? ?? ??");

    private static readonly SignaturePattern TownDestructorPattern = SignaturePattern.Parse(
        "56 57 48 83 EC 28 89 D7 48 89 CE E8 F0 F6 FF FF 85 FF 74 08 " +
        "48 89 F1 E8 20 00 18 02 48 89 F0");

    private static readonly SignaturePattern BattleDestructorPattern = SignaturePattern.Parse(
        "56 57 48 83 EC 28 89 D7 48 89 CE E8 D0 FB FF FF 85 FF 74 08 " +
        "48 89 F1 E8 D0 BE 10 02 48 89 F0");

    private static readonly SignaturePattern PauseTopFactoryPattern = SignaturePattern.Parse(
        "48 8D 0D FF 5C AE 02 48 89 08 48 8D 0D");

    private static readonly SignaturePattern PauseTopDestructorPattern = SignaturePattern.Parse(
        "56 57 48 83 EC 28 89 D7 48 89 CE E8 E0 F6 FF FF 85 FF 74 08 " +
        "48 89 F1 E8 A0 A1 B5 03 48 89 F0");

    private static readonly SignaturePattern UiObjectQueryPattern = SignaturePattern.Parse(
        "48 81 EC 98 00 00 00 C5 78 29 B4 24 80 00 00 00 " +
        "C5 78 29 6C 24 70 C5 78 29 64 24 60 C5 78 29 5C 24 50 " +
        "C5 78 29 54 24 40 C5 78 29 4C 24 30 C5 78 29 44 24 20 " +
        "C5 F8 29 7C 24 10 C5 F8 29 34 24 48 8B 05 ?? ?? ?? ??");

    internal static RelinkHudRvas Resolve(string imagePath)
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
        if (!string.Equals(actualHash, RelinkBuildLocator.SupportedSha256, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Unsupported Relink executable SHA-256 {actualHash}; " +
                $"expected {RelinkBuildLocator.SupportedSha256}.");
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

        var townFactory = checked(
            textSection.VirtualAddress + TownFactoryPattern.FindUniqueOffset(text, "town party HUD factory"));
        var townDestructor = checked(
            textSection.VirtualAddress + TownDestructorPattern.FindUniqueOffset(text, "town party HUD destructor"));
        var battleFactory = checked(
            textSection.VirtualAddress + BattleFactoryPattern.FindUniqueOffset(text, "battle party HUD factory"));
        var battleDestructor = checked(
            textSection.VirtualAddress + BattleDestructorPattern.FindUniqueOffset(text, "battle party HUD destructor"));
        var pauseTopFactoryVtableOffset = PauseTopFactoryPattern.FindUniqueOffset(
            text,
            "pause-top controller factory vtable assignment");
        var pauseTopFactory = checked(
            textSection.VirtualAddress + pauseTopFactoryVtableOffset - PauseTopVtableInstructionOffset);
        var pauseTopDestructor = checked(
            textSection.VirtualAddress + PauseTopDestructorPattern.FindUniqueOffset(text, "pause-top shared destructor"));
        var uiObjectQueryOffset = UiObjectQueryPattern.FindUniqueOffset(text, "UI object canvas transform");
        var uiObjectQueryRva = checked(textSection.VirtualAddress + uiObjectQueryOffset);
        var uiManagerInstructionRva = checked(uiObjectQueryRva + UiManagerInstructionOffset);
        var uiManagerDisplacement = BinaryPrimitives.ReadInt32LittleEndian(
            text.AsSpan(UiManagerInstructionOffset + uiObjectQueryOffset + 3, sizeof(int)));
        var uiManagerSlot = checked(uiManagerInstructionRva + 7 + uiManagerDisplacement);

        if (townFactory != ExpectedTownFactoryRva ||
            townDestructor != ExpectedTownDestructorRva ||
            battleFactory != ExpectedBattleFactoryRva ||
            battleDestructor != ExpectedBattleDestructorRva ||
            pauseTopFactory != ExpectedPauseTopFactoryRva ||
            pauseTopDestructor != ExpectedPauseTopDestructorRva ||
            uiObjectQueryRva != ExpectedUiObjectQueryRva ||
            uiManagerSlot != ExpectedUiManagerSlotRva)
        {
            throw new InvalidDataException(
                $"Relink party HUD signature validation failed: townFactory={townFactory:X}, " +
                $"townDestructor={townDestructor:X}, battleFactory={battleFactory:X}, " +
                $"battleDestructor={battleDestructor:X}, pauseTopFactory={pauseTopFactory:X}, " +
                $"pauseTopDestructor={pauseTopDestructor:X}, uiObjectQuery={uiObjectQueryRva:X}, " +
                $"uiManager={uiManagerSlot:X}.");
        }

        return new RelinkHudRvas(
            townFactory,
            townDestructor,
            battleFactory,
            battleDestructor,
            pauseTopFactory,
            pauseTopDestructor,
            uiManagerSlot);
    }
}
