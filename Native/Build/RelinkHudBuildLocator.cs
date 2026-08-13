namespace GBFR.ChatOverlay.Native;

internal readonly record struct RelinkHudRvas(
    int TownFactory,
    int TownDestructor,
    int BattleFactory,
    int BattleDestructor,
    int ChainburstFactory,
    int ChainburstDestructor,
    int UiManagerSlot,
    int TownVtable,
    int BattleVtable,
    int ChainburstVtable);

internal static class RelinkHudBuildLocator
{
    private const int ExpectedTownFactoryRva = 0x02590020;
    private const int ExpectedTownDestructorRva = 0x025916C0;
    private const int ExpectedBattleFactoryRva = 0x026043B0;
    private const int ExpectedBattleDestructorRva = 0x02605810;
    private const int ExpectedChainburstFactoryRva = 0x0262ACA0;
    private const int ExpectedChainburstDestructorRva = 0x0262BDD0;
    private const int ExpectedUiObjectQueryRva = 0x026193F0;
    private const int ExpectedUiManagerSlotRva = 0x07C00598;
    private const int ExpectedHudFactoryTargetRva = 0x039C98E0;
    private const int ExpectedTownDestructorPrimaryTargetRva = 0x02590DC0;
    private const int ExpectedBattleDestructorPrimaryTargetRva = 0x026053F0;
    private const int ExpectedHudDestructorSharedTargetRva = 0x04712FBC;
    private const int ExpectedChainburstPrimaryVtableRva = 0x05A65B98;
    private const int ExpectedChainburstSecondaryVtableRva = 0x05A65CB8;
    private const int ExpectedChainburstTertiaryVtableRva = 0x05A65CC8;
    private const int ExpectedChainburstDestructorTargetRva = 0x00BB5D40;
    private const int ExpectedTownVtableRva = 0x05A50BD8;
    private const int ExpectedBattleVtableRva = 0x05A60088;
    private const int ExpectedChainburstVtableRva = 0x05A65B98;
    private const int ChainburstFactoryPatternOffset = 0x128;
    private const int UiManagerInstructionOffset = 63;
    private const int ExpectedChainburstFactoryPatternRva =
        ExpectedChainburstFactoryRva + ChainburstFactoryPatternOffset;

    private static readonly SignaturePattern TownFactoryPattern = SignaturePattern.Parse(
        "56 57 48 83 EC 28 48 89 D6 8B 05 ?? ?? ?? ?? " +
        "65 48 8B 0C 25 58 00 00 00 48 8B 04 C1 48 8B 88 30 16 00 00 " +
        "BA 88 05 00 00 41 B8 08 00 00 00 E8 ?? ?? ?? ??");

    private static readonly SignaturePattern BattleFactoryPattern = SignaturePattern.Parse(
        "56 48 83 EC 20 48 89 D6 8B 05 ?? ?? ?? ?? " +
        "65 48 8B 0C 25 58 00 00 00 48 8B 04 C1 48 8B 88 30 16 00 00 " +
        "BA 80 08 00 00 41 B8 08 00 00 00 E8 ?? ?? ?? ??");

    private static readonly SignaturePattern TownDestructorPattern = SignaturePattern.Parse(
        "56 57 48 83 EC 28 89 D7 48 89 CE E8 ?? ?? ?? ?? 85 FF 74 08 " +
        "48 89 F1 E8 ?? ?? ?? ?? 48 89 F0");

    private static readonly SignaturePattern BattleDestructorPattern = SignaturePattern.Parse(
        "56 57 48 83 EC 28 89 D7 48 89 CE E8 ?? ?? ?? ?? 85 FF 74 08 " +
        "48 89 F1 E8 ?? ?? ?? ?? 48 89 F0");

    // The constructor shape is shared by many UI controllers. Resolve and verify
    // all three vtable targets below before accepting this fixed build profile.
    private static readonly SignaturePattern ChainburstFactoryPattern = SignaturePattern.Parse(
        "48 8D 05 ?? ?? ?? ?? 48 89 07 " +
        "48 8D 05 ?? ?? ?? ?? 48 89 47 18 " +
        "48 8D 05 ?? ?? ?? ?? 48 89 47 40");

    private static readonly SignaturePattern ChainburstDestructorPattern = SignaturePattern.Parse(
        "56 57 53 48 83 EC 20 89 D7 48 89 CE 4C 8B 81 60 03 00 00 " +
        "4D 85 C0 74 68 48 8D 9E 60 03 00 00 4C 89 C0 " +
        "48 25 00 00 C0 FF 74 46 65 4C 8B 0C 25 30 00 00 00 " +
        "44 89 C2 81 E2 FF FF 3F 00 0F B6 48 60 48 D3 EA " +
        "4C 3B 48 68 75 56 48 C1 E2 06 80 7C 10 7E 00 75 4B " +
        "48 8B 8C 10 90 00 00 00 49 89 08 4C 89 84 10 90 00 00 00 " +
        "FF 8C 10 88 00 00 00 74 40 C5 F8 57 C0 C5 F8 11 03 " +
        "48 C7 43 10 00 00 00 00 48 89 F1 E8 ?? ?? ?? ??");

    private static readonly SignaturePattern UiObjectQueryPattern = SignaturePattern.Parse(
        "48 81 EC 98 00 00 00 C5 78 29 B4 24 80 00 00 00 " +
        "C5 78 29 6C 24 70 C5 78 29 64 24 60 C5 78 29 5C 24 50 " +
        "C5 78 29 54 24 40 C5 78 29 4C 24 30 C5 78 29 44 24 20 " +
        "C5 F8 29 7C 24 10 C5 F8 29 34 24 48 8B 05 ?? ?? ?? ??");

    internal static RelinkHudRvas Resolve(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        using var preflight = RelinkExecutablePreflight.Open(imagePath);
        preflight.RequirePattern(ExpectedTownFactoryRva, TownFactoryPattern, "town party HUD factory");
        preflight.RequirePattern(ExpectedTownDestructorRva, TownDestructorPattern, "town party HUD destructor");
        preflight.RequirePattern(ExpectedBattleFactoryRva, BattleFactoryPattern, "battle party HUD factory");
        preflight.RequirePattern(
            ExpectedBattleDestructorRva,
            BattleDestructorPattern,
            "battle party HUD destructor");
        preflight.RequirePattern(
            ExpectedChainburstFactoryPatternRva,
            ChainburstFactoryPattern,
            "Full Chain overlay factory");
        preflight.RequirePattern(
            ExpectedChainburstDestructorRva,
            ChainburstDestructorPattern,
            "Full Chain overlay destructor");
        preflight.RequirePattern(ExpectedUiObjectQueryRva, UiObjectQueryPattern, "UI object canvas transform");

        var townFactoryTarget = ResolveRelativeTarget(preflight, ExpectedTownFactoryRva, 47, 51);
        var battleFactoryTarget = ResolveRelativeTarget(preflight, ExpectedBattleFactoryRva, 46, 50);
        var townDestructorPrimaryTarget = ResolveRelativeTarget(
            preflight,
            ExpectedTownDestructorRva,
            12,
            16);
        var townDestructorSharedTarget = ResolveRelativeTarget(
            preflight,
            ExpectedTownDestructorRva,
            24,
            28);
        var battleDestructorPrimaryTarget = ResolveRelativeTarget(
            preflight,
            ExpectedBattleDestructorRva,
            12,
            16);
        var battleDestructorSharedTarget = ResolveRelativeTarget(
            preflight,
            ExpectedBattleDestructorRva,
            24,
            28);
        var chainburstPrimaryVtable = ResolveRelativeTarget(
            preflight,
            ExpectedChainburstFactoryPatternRva,
            3,
            7);
        var chainburstSecondaryVtable = ResolveRelativeTarget(
            preflight,
            ExpectedChainburstFactoryPatternRva,
            13,
            17);
        var chainburstTertiaryVtable = ResolveRelativeTarget(
            preflight,
            ExpectedChainburstFactoryPatternRva,
            24,
            28);
        var chainburstDestructorTarget = ResolveRelativeTarget(
            preflight,
            ExpectedChainburstDestructorRva,
            132,
            136);
        var uiManagerInstructionRva = checked(ExpectedUiObjectQueryRva + UiManagerInstructionOffset);
        var uiManagerDisplacement = preflight.ReadInt32(uiManagerInstructionRva + 3);
        var uiManagerSlot = checked(uiManagerInstructionRva + 7 + uiManagerDisplacement);

        if (townFactoryTarget != ExpectedHudFactoryTargetRva ||
            battleFactoryTarget != ExpectedHudFactoryTargetRva ||
            townDestructorPrimaryTarget != ExpectedTownDestructorPrimaryTargetRva ||
            townDestructorSharedTarget != ExpectedHudDestructorSharedTargetRva ||
            battleDestructorPrimaryTarget != ExpectedBattleDestructorPrimaryTargetRva ||
            battleDestructorSharedTarget != ExpectedHudDestructorSharedTargetRva ||
            chainburstPrimaryVtable != ExpectedChainburstPrimaryVtableRva ||
            chainburstSecondaryVtable != ExpectedChainburstSecondaryVtableRva ||
            chainburstTertiaryVtable != ExpectedChainburstTertiaryVtableRva ||
            chainburstDestructorTarget != ExpectedChainburstDestructorTargetRva ||
            uiManagerSlot != ExpectedUiManagerSlotRva)
        {
            throw new InvalidDataException(
                $"Relink party HUD derived-target validation failed: " +
                $"townFactoryTarget={townFactoryTarget:X}, battleFactoryTarget={battleFactoryTarget:X}, " +
                $"townDestructorTargets={townDestructorPrimaryTarget:X}/{townDestructorSharedTarget:X}, " +
                $"battleDestructorTargets={battleDestructorPrimaryTarget:X}/{battleDestructorSharedTarget:X}, " +
                $"chainburstVtables={chainburstPrimaryVtable:X}/{chainburstSecondaryVtable:X}/" +
                $"{chainburstTertiaryVtable:X}, chainburstDestructorTarget={chainburstDestructorTarget:X}, " +
                $"uiManager={uiManagerSlot:X}.");
        }

        return new RelinkHudRvas(
            ExpectedTownFactoryRva,
            ExpectedTownDestructorRva,
            ExpectedBattleFactoryRva,
            ExpectedBattleDestructorRva,
            ExpectedChainburstFactoryRva,
            ExpectedChainburstDestructorRva,
            uiManagerSlot,
            ExpectedTownVtableRva,
            ExpectedBattleVtableRva,
            ExpectedChainburstVtableRva);
    }

    private static int ResolveRelativeTarget(
        RelinkExecutablePreflight preflight,
        int instructionRva,
        int displacementOffset,
        int instructionEndOffset)
    {
        var displacement = preflight.ReadInt32(checked(instructionRva + displacementOffset));
        return checked(instructionRva + instructionEndOffset + displacement);
    }
}
