using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatModerationServiceTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_LocalAndCueMessagesAreAllowedWithoutCounting()
    {
        var service = new ChatModerationService();
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 100;
        service.ApplyConfiguration(configuration);

        var local = Evaluate(
            service,
            new ChatModerationParticipant(1, "Local", "local", IsLocal: true),
            "bad");
        var cue = Evaluate(
            service,
            Remote("remote"),
            "bad",
            cue: ChatCommunicationCue.Victory);
        var genericOfficial = Evaluate(
            service,
            Remote("remote"),
            "bad",
            cue: ChatCommunicationCue.Official);

        Assert.Equal(ChatModerationDisposition.Allow, local.Disposition);
        Assert.Equal("bad", local.Text);
        Assert.False(local.Matched);
        Assert.Equal(ChatModerationDisposition.Allow, cue.Disposition);
        Assert.False(cue.Matched);
        Assert.Equal(ChatModerationDisposition.Allow, genericOfficial.Disposition);
        Assert.False(genericOfficial.Matched);
        Assert.Equal(0, service.GetSnapshot().SessionFilteredMessageCount);
        Assert.False(service.TryReadEvent(out _));
    }

    [Fact]
    public void Evaluate_UnicodeFormKcAndCaseInsensitiveRulesMaskAndCountOnce()
    {
        var service = new ChatModerationService();
        service.ApplyConfiguration(CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "r1", Term = "abc" }));

        var decision = Evaluate(service, Remote("remote"), "ＡＢＣ abc");

        Assert.Equal(ChatModerationDisposition.Mask, decision.Disposition);
        Assert.Equal("*** ***", decision.Text);
        Assert.True(decision.Matched);
        Assert.Equal(["r1"], decision.MatchedRuleIds);
        Assert.Equal(1, service.GetSnapshot().SessionFilteredMessageCount);
        Assert.Equal(1, Assert.Single(service.GetSnapshot().Rules).SessionHitCount);
    }

    [Fact]
    public void Evaluate_OverlappingRulesMergeBeforeMasking()
    {
        var service = new ChatModerationService();
        service.ApplyConfiguration(CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "r1", Term = "abc" },
            new ChatFilterRuleConfiguration { Id = "r2", Term = "bcd" }));

        var decision = Evaluate(service, Remote("remote"), "abcd");

        Assert.Equal("****", decision.Text);
        Assert.Contains("r1", decision.MatchedRuleIds);
        Assert.Contains("r2", decision.MatchedRuleIds);
    }

    [Fact]
    public void Evaluate_SameRuleOverlappingOccurrencesAllMasked()
    {
        var service = new ChatModerationService();
        service.ApplyConfiguration(CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "r1", Term = "aa" }));

        var decision = Evaluate(service, Remote("remote"), "aaa");

        Assert.Equal("***", decision.Text);
        Assert.Equal(["r1"], decision.MatchedRuleIds);
        Assert.Equal(1, service.GetSnapshot().SessionFilteredMessageCount);
    }

    [Fact]
    public void Evaluate_HideEntireMessageReturnsBlock()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.Action = ChatFilterAction.HideEntireMessage;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);

        var decision = Evaluate(service, Remote("remote"), "bad words");

        Assert.Equal(ChatModerationDisposition.Block, decision.Disposition);
        Assert.True(decision.Matched);
        Assert.Equal(1, service.GetSnapshot().SessionFilteredMessageCount);
    }

    [Fact]
    public void OfficialFilter_AppliesAfterCustomMaskingAndReportsHit()
    {
        var official = new StubOfficialFilter(new OfficialTextFilterResult("***", 1, true));
        var service = new ChatModerationService(official);
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.UseSteamTextFilter = true;
        service.ApplyConfiguration(configuration);

        var decision = Evaluate(service, Remote("remote"), "bad");

        Assert.Equal("***", Assert.Single(official.Inputs));
        Assert.Equal(ChatModerationDisposition.Mask, decision.Disposition);
        Assert.True(decision.Matched);
        Assert.True(decision.OfficialFilterMatched);
        Assert.Equal(1, service.GetSnapshot().SessionFilteredMessageCount);
    }

    [Fact]
    public void OfficialFilter_FailureFailsOpenWithoutCounting()
    {
        var official = new StubOfficialFilter(new OfficialTextFilterResult("", 0, false));
        var service = new ChatModerationService(official);
        var configuration = CreateConfiguration();
        configuration.UseSteamTextFilter = true;
        service.ApplyConfiguration(configuration);

        var decision = Evaluate(service, Remote("remote"), "bad");

        Assert.Equal(ChatModerationDisposition.Allow, decision.Disposition);
        Assert.Equal("bad", decision.Text);
        Assert.False(decision.Matched);
        Assert.False(decision.OfficialFilterMatched);
        Assert.Equal(0, service.GetSnapshot().SessionFilteredMessageCount);
    }

    [Fact]
    public void OfficialFilter_FilterExceptionFailsOpenWithoutLosingCustomMasking()
    {
        var official = new ThrowingOfficialFilter(throwOnFilter: true);
        var service = new ChatModerationService(official);
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.UseSteamTextFilter = true;
        service.ApplyConfiguration(configuration);

        var matched = Evaluate(service, Remote("remote"), "bad");

        Assert.Equal(ChatModerationDisposition.Mask, matched.Disposition);
        Assert.Equal("***", matched.Text);
        Assert.True(matched.Matched);
        Assert.False(matched.OfficialFilterMatched);
        Assert.Equal(1, service.GetSnapshot().SessionFilteredMessageCount);

        var clean = Evaluate(service, Remote("remote"), "clean");

        Assert.Equal(ChatModerationDisposition.Allow, clean.Disposition);
        Assert.Equal("clean", clean.Text);
        Assert.False(clean.Matched);
        Assert.False(clean.OfficialFilterMatched);
        Assert.Equal(1, service.GetSnapshot().SessionFilteredMessageCount);
    }

    [Fact]
    public void OfficialFilter_RefreshExceptionReturnsUnavailableAndStoresStatus()
    {
        var official = new ThrowingOfficialFilter(throwOnRefresh: true);
        var service = new ChatModerationService(official);

        var status = service.RefreshOfficialFilter();

        Assert.Equal(OfficialTextFilterState.Unavailable, status.State);
        Assert.Equal(
            OfficialTextFilterState.Unavailable,
            service.GetSnapshot().OfficialFilter.State);
    }

    [Fact]
    public void Evaluate_CountsMessageAndRulesOncePerMessage()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "r1", Term = "bad" },
            new ChatFilterRuleConfiguration { Id = "r2", Term = "word" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 100;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);

        var decision = Evaluate(service, Remote("remote"), "bad word bad");

        Assert.Equal(2, decision.MatchedRuleIds.Count);
        Assert.Equal(1, service.GetSnapshot().SessionFilteredMessageCount);
        Assert.Equal(1, Assert.Single(service.GetSnapshot().Rules, status => status.RuleId == "r1").SessionHitCount);
        Assert.Equal(1, Assert.Single(service.GetSnapshot().Rules, status => status.RuleId == "r2").SessionHitCount);
        Assert.Equal(1, Assert.Single(service.GetSnapshot().Players).WindowHitCount);
    }

    [Fact]
    public void AutoBlock_WindowExpiryResetsHitCount()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        configuration.AutoBlockWindowMinutes = 10;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);

        Evaluate(service, Remote("remote"), "bad", Start);
        var second = Evaluate(service, Remote("remote"), "bad", Start.AddMinutes(11));
        var third = Evaluate(service, Remote("remote"), "bad", Start.AddMinutes(22));

        Assert.True(second.Matched);
        Assert.True(third.Matched);
        Assert.False(second.AutoBlocked);
        Assert.False(third.AutoBlocked);
        Assert.Equal(1, Assert.Single(service.GetSnapshot().Players).WindowHitCount);
        Assert.False(service.IsBlocked(Remote("remote")));
        Assert.False(service.TryReadEvent(out _));
    }

    [Fact]
    public void AutoBlock_UsesSlidingWindowInsteadOfLastHitCount()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 3;
        configuration.AutoBlockWindowMinutes = 10;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);

        Evaluate(service, Remote("remote"), "bad", Start);
        Evaluate(service, Remote("remote"), "bad", Start.AddMinutes(9));
        var nearThreshold = Evaluate(service, Remote("remote"), "bad", Start.AddMinutes(18));

        Assert.False(nearThreshold.AutoBlocked);
        Assert.False(service.IsBlocked(Remote("remote")));
        Assert.False(service.TryReadEvent(out _));

        var threshold = Evaluate(service, Remote("remote"), "bad", Start.AddMinutes(19));

        Assert.True(threshold.AutoBlocked);
        Assert.True(service.IsBlocked(Remote("remote")));
        Assert.True(service.TryReadEvent(out var moderationEvent));
        Assert.Equal(3, moderationEvent.HitCount);
    }

    [Fact]
    public void AutoBlock_ThresholdBlocksOnceAndQueuesSingleEvent()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        configuration.AutoBlockWindowMinutes = 10;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);

        Evaluate(service, Remote("remote"), "bad", Start);
        var threshold = Evaluate(service, Remote("remote"), "bad", Start.AddMinutes(5));
        var after = Evaluate(service, Remote("remote"), "bad", Start.AddMinutes(6));

        Assert.True(threshold.AutoBlocked);
        Assert.True(service.TryReadEvent(out var moderationEvent));
        Assert.Equal(2, moderationEvent.HitCount);
        Assert.Equal(2, moderationEvent.Threshold);
        Assert.True(moderationEvent.PersistIdentity);
        Assert.False(service.TryReadEvent(out _));
        Assert.Equal(ChatModerationDisposition.Block, after.Disposition);
        Assert.True(service.IsBlocked(Remote("remote")));
    }

    [Fact]
    public void AutoBlock_QueuesFifoEventsForDistinctParticipants()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        configuration.AutoBlockWindowMinutes = 10;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);
        var first = Remote("entity-first");
        var second = Remote("entity-second");

        Evaluate(service, first, "bad", Start);
        Evaluate(service, first, "bad", Start.AddMinutes(1));
        Evaluate(service, second, "bad", Start.AddMinutes(2));
        Evaluate(service, second, "bad", Start.AddMinutes(3));

        Assert.True(service.TryReadEvent(out var firstEvent));
        Assert.Equal("entity-first", firstEvent.Participant.EntityId);
        Assert.True(service.TryReadEvent(out var secondEvent));
        Assert.Equal("entity-second", secondEvent.Participant.EntityId);
        Assert.False(service.TryReadEvent(out _));
        Assert.True(service.IsBlocked(first));
        Assert.True(service.IsBlocked(second));
    }

    [Fact]
    public void ClearRoom_DiscardsUnreadEvents()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        configuration.AutoBlockWindowMinutes = 10;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);
        var first = Remote("entity-first");
        var second = Remote("entity-second");

        Evaluate(service, first, "bad", Start);
        Evaluate(service, first, "bad", Start.AddMinutes(1));
        Evaluate(service, second, "bad", Start.AddMinutes(2));
        Evaluate(service, second, "bad", Start.AddMinutes(3));

        service.ClearRoom();

        Assert.False(service.TryReadEvent(out _));
    }

    [Fact]
    public void AutoBlock_EventDoesNotPersistWithoutEntityId()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        configuration.AutoBlockWindowMinutes = 10;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);
        var participant = new ChatModerationParticipant(2, "Remote", EntityId: null);

        Evaluate(service, participant, "bad", Start);
        Evaluate(service, participant, "bad", Start.AddMinutes(1));

        Assert.True(service.TryReadEvent(out var moderationEvent));
        Assert.False(moderationEvent.PersistIdentity);
        Assert.False(service.SetBlocked(participant, true, persistent: true));
        service.ClearRoom();
        Assert.False(service.IsBlocked(participant));
    }

    [Fact]
    public void SetBlocked_PersistentOnlyAcceptsEntityIdAndRoomCancelCannotBypassIt()
    {
        var service = new ChatModerationService();
        service.ApplyConfiguration(CreateConfiguration());
        var participant = Remote("entity-1");
        var anonymous = new ChatModerationParticipant(2, "Remote", EntityId: null, SenderId: 10);

        Assert.False(service.SetBlocked(anonymous, true, persistent: true));
        Assert.True(service.SetBlocked(participant, true, persistent: true));
        Assert.True(service.SetBlocked(participant, false, persistent: false));
        Assert.True(service.IsBlocked(participant));

        Assert.True(service.SetBlocked(participant, false, persistent: true));
        Assert.False(service.IsBlocked(participant));
    }

    [Fact]
    public void ObserveParticipant_UpdatesSameIdentityAndRetainsSameNameDifferentIdentities()
    {
        var service = new ChatModerationService();
        var first = new ChatModerationParticipant(2, "Kuro", "entity-1", SenderId: 1);
        var second = new ChatModerationParticipant(3, "Kuro", "entity-2", SenderId: 2);

        service.ObserveParticipant(first);
        service.ObserveParticipant(second);
        service.ObserveParticipant(first with { DisplayName = "Ren", PlayerNumber = 4 });

        var players = service.GetSnapshot().Players;
        Assert.Equal(2, players.Count);
        Assert.Equal("Ren", Assert.Single(players, player => player.Participant.EntityId == "entity-1").Participant.DisplayName);
        Assert.Equal(4, Assert.Single(players, player => player.Participant.EntityId == "entity-1").Participant.PlayerNumber);
        Assert.Equal("Kuro", Assert.Single(players, player => player.Participant.EntityId == "entity-2").Participant.DisplayName);
    }

    [Fact]
    public void ObserveParticipant_DoesNotStoreOrCoalesceByNameWithoutStableIdentity()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        configuration.AutoBlockWindowMinutes = 10;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);
        var anonymous = new ChatModerationParticipant(0, "Ghost", EntityId: null, SenderId: 0);

        service.ObserveParticipant(anonymous);

        Assert.Empty(service.GetSnapshot().Players);

        var first = Evaluate(service, anonymous, "bad", Start);
        var second = Evaluate(service, anonymous, "bad", Start.AddMinutes(1));

        Assert.False(first.AutoBlocked);
        Assert.False(second.AutoBlocked);
        Assert.False(service.IsBlocked(anonymous));
        Assert.Empty(service.GetSnapshot().Players);
        Assert.False(service.TryReadEvent(out _));
    }

    [Fact]
    public void ObserveParticipant_RejectsOutOfRangePlayerNumberAndNormalizesEntityId()
    {
        var service = new ChatModerationService();

        service.ObserveParticipant(new ChatModerationParticipant(99, "Invalid", null));
        service.ObserveParticipant(new ChatModerationParticipant(2, "First", " entity-1 "));
        service.ObserveParticipant(new ChatModerationParticipant(3, "Updated", "entity-1"));

        var player = Assert.Single(service.GetSnapshot().Players);
        Assert.Equal("Updated", player.Participant.DisplayName);
        Assert.True(service.SetBlocked(player.Participant, true, persistent: true));
        Assert.True(service.IsBlocked(new ChatModerationParticipant(2, "Other", " entity-1 ")));
    }

    [Fact]
    public void ObserveParticipant_UpgradesSenderIdentityWithoutSplittingHitsOrRoomBlock()
    {
        var service = new ChatModerationService();
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 100;
        service.ApplyConfiguration(configuration);
        var senderIdentity = new ChatModerationParticipant(
            2,
            "Remote",
            EntityId: null,
            SenderId: 0x10);

        Evaluate(service, senderIdentity, "bad", Start);
        Assert.True(service.SetBlocked(senderIdentity, blocked: true, persistent: false));

        var entityIdentity = senderIdentity with
        {
            DisplayName = "Resolved",
            EntityId = " entity-1 ",
        };
        service.ObserveParticipant(entityIdentity);

        var player = Assert.Single(service.GetSnapshot().Players);
        Assert.Equal("entity-1", player.Participant.EntityId);
        Assert.Equal(0x10u, player.Participant.SenderId);
        Assert.Equal("Resolved", player.Participant.DisplayName);
        Assert.Equal(1, player.WindowHitCount);
        Assert.True(player.IsRoomBlocked);
        Assert.True(service.IsBlocked(entityIdentity));
    }

    [Fact]
    public void ObserveParticipant_SlotOnlyStateDoesNotTransferToStrongerIdentityWithoutEvidence()
    {
        var service = new ChatModerationService();
        service.ApplyConfiguration(CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" }));
        var slotOnly = new ChatModerationParticipant(2, "Old", EntityId: null);
        Evaluate(service, slotOnly, "bad", Start);
        Assert.True(service.SetBlocked(slotOnly, blocked: true, persistent: false));

        var resolved = new ChatModerationParticipant(
            2,
            "New",
            "entity-new",
            SenderId: 0x20);
        service.ObserveParticipant(resolved);

        var player = Assert.Single(service.GetSnapshot().Players);
        Assert.Equal("entity-new", player.Participant.EntityId);
        Assert.Equal(0x20u, player.Participant.SenderId);
        Assert.Equal(0, player.WindowHitCount);
        Assert.False(player.IsRoomBlocked);
        Assert.False(service.IsBlocked(resolved));
        Assert.False(service.IsBlocked(slotOnly));
    }

    [Fact]
    public void ObserveParticipant_WeakerFallbackDoesNotOverwriteResolvedIdentityName()
    {
        var service = new ChatModerationService();
        service.ObserveParticipant(new ChatModerationParticipant(
            2,
            "Resolved Name",
            "entity-1",
            SenderId: 0x10));

        service.ObserveParticipant(new ChatModerationParticipant(
            2,
            "玩家 2",
            EntityId: null));

        var player = Assert.Single(service.GetSnapshot().Players);
        Assert.Equal("Resolved Name", player.Participant.DisplayName);
        Assert.Equal("entity-1", player.Participant.EntityId);
        Assert.Equal(0x10u, player.Participant.SenderId);
    }

    [Fact]
    public void Evaluate_WeakerFallbackUsesResolvedIdentityHitWindowAndBlockState()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);
        var resolved = new ChatModerationParticipant(
            2,
            "Resolved Name",
            "entity-1",
            SenderId: 0x10);
        var fallback = new ChatModerationParticipant(
            2,
            "玩家 2",
            EntityId: null,
            SenderId: 0x10);

        var first = Evaluate(service, resolved, "bad", Start);
        var second = Evaluate(service, fallback, "bad", Start.AddMinutes(1));

        Assert.False(first.AutoBlocked);
        Assert.True(second.AutoBlocked);
        var player = Assert.Single(service.GetSnapshot().Players);
        Assert.Equal("entity-1", player.Participant.EntityId);
        Assert.Equal("Resolved Name", player.Participant.DisplayName);
        Assert.Equal(2, player.WindowHitCount);
        Assert.True(player.IsRoomBlocked);
        Assert.True(service.IsBlocked(fallback));
        Assert.True(service.TryReadEvent(out var moderationEvent));
        Assert.Equal("entity-1", moderationEvent.Participant.EntityId);
        Assert.True(moderationEvent.PersistIdentity);
    }

    [Fact]
    public void Evaluate_DifferentSenderInSameSlotDoesNotInheritResolvedIdentityState()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);
        var oldParticipant = new ChatModerationParticipant(
            2,
            "Old",
            "entity-old",
            SenderId: 0x10);
        Evaluate(service, oldParticipant, "bad", Start);
        Assert.True(service.SetBlocked(oldParticipant, blocked: true, persistent: false));

        var replacement = new ChatModerationParticipant(
            2,
            "Replacement",
            EntityId: null,
            SenderId: 0x20);
        var decision = Evaluate(service, replacement, "bad", Start.AddMinutes(1));

        Assert.Equal(ChatModerationDisposition.Mask, decision.Disposition);
        Assert.False(decision.AutoBlocked);
        var player = Assert.Single(service.GetSnapshot().Players);
        Assert.Equal(0x20u, player.Participant.SenderId);
        Assert.Null(player.Participant.EntityId);
        Assert.Equal(1, player.WindowHitCount);
        Assert.False(player.IsRoomBlocked);
        Assert.False(service.IsBlocked(replacement));
        Assert.True(service.IsBlocked(oldParticipant));
        Assert.False(service.TryReadEvent(out _));
    }

    [Fact]
    public void ObserveParticipant_DifferentStrongIdentityInSameSlotReplacesCurrentRowWithoutInheritance()
    {
        var service = new ChatModerationService();
        service.ApplyConfiguration(CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" }));
        var oldParticipant = new ChatModerationParticipant(2, "Old", "entity-old");
        Evaluate(service, oldParticipant, "bad", Start);
        Assert.True(service.SetBlocked(oldParticipant, blocked: true, persistent: false));

        var replacement = new ChatModerationParticipant(2, "New", "entity-new");
        service.ObserveParticipant(replacement);

        var player = Assert.Single(service.GetSnapshot().Players);
        Assert.Equal("entity-new", player.Participant.EntityId);
        Assert.Equal(0, player.WindowHitCount);
        Assert.False(player.IsRoomBlocked);
        Assert.False(service.IsBlocked(replacement));
        Assert.True(service.IsBlocked(oldParticipant));
    }

    [Fact]
    public void Evaluate_SlotOnlyFallbackAfterReplacementDoesNotAccrueToCurrentMember()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);
        var oldParticipant = new ChatModerationParticipant(2, "Old", "entity-old");
        Evaluate(service, oldParticipant, "bad", Start);
        Assert.True(service.SetBlocked(oldParticipant, blocked: true, persistent: false));
        var replacement = new ChatModerationParticipant(2, "New", "entity-new");
        service.ObserveParticipant(replacement);

        var decision = Evaluate(
            service,
            new ChatModerationParticipant(2, "玩家 2", EntityId: null),
            "bad",
            Start.AddMinutes(1));

        Assert.True(decision.Matched);
        Assert.False(decision.AutoBlocked);
        var player = Assert.Single(service.GetSnapshot().Players);
        Assert.Equal("entity-new", player.Participant.EntityId);
        Assert.Equal("New", player.Participant.DisplayName);
        Assert.Equal(0, player.WindowHitCount);
        Assert.False(player.IsRoomBlocked);
        Assert.False(service.IsBlocked(replacement));
        Assert.True(service.IsBlocked(oldParticipant));
        Assert.False(service.TryReadEvent(out _));
    }

    [Fact]
    public void ForgetParticipant_RemovesOnlyThatPlayerAndKeepsPersistentBlockAndPendingEvents()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        configuration.AutoBlockWindowMinutes = 10;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);
        var forgotten = Remote("entity-forgotten");
        var retained = Remote("entity-retained", playerNumber: 3);

        Evaluate(service, forgotten, "bad", Start);
        Evaluate(service, forgotten, "bad", Start.AddMinutes(1));
        Evaluate(service, retained, "bad", Start.AddMinutes(2));

        Assert.True(service.SetBlocked(forgotten, true, persistent: true));
        service.ForgetParticipant(forgotten);

        var players = service.GetSnapshot().Players;
        Assert.DoesNotContain(
            "entity-forgotten",
            players.Select(player => player.Participant.EntityId));
        Assert.Single(players, player => player.Participant.EntityId == "entity-retained");
        Assert.True(service.IsBlocked(forgotten));
        Assert.True(service.TryReadEvent(out var moderationEvent));
        Assert.Equal("entity-forgotten", moderationEvent.Participant.EntityId);
    }

    [Fact]
    public void ForgetParticipant_ClearsTemporaryBlockAndHitsBeforePlayerSlotIsReused()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        configuration.AutoBlockWindowMinutes = 10;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);
        var departed = new ChatModerationParticipant(
            2,
            "Departed",
            EntityId: null,
            SenderId: 0x10);

        Evaluate(service, departed, "bad", Start);
        Assert.True(service.SetBlocked(departed, blocked: true, persistent: false));

        // Membership transitions know the relative player slot even when the native
        // sender key and PlayFab identity are unavailable at departure time.
        service.ForgetParticipant(new ChatModerationParticipant(2, "Departed", EntityId: null));

        var replacement = new ChatModerationParticipant(
            2,
            "Replacement",
            EntityId: null,
            SenderId: 0x20);
        Assert.False(service.IsBlocked(replacement));
        var firstHit = Evaluate(service, replacement, "bad", Start.AddMinutes(1));

        Assert.False(firstHit.AutoBlocked);
        var player = Assert.Single(service.GetSnapshot().Players);
        Assert.Equal(0x20u, player.Participant.SenderId);
        Assert.Equal(1, player.WindowHitCount);
        Assert.False(player.IsRoomBlocked);
        Assert.False(service.TryReadEvent(out _));
    }

    [Fact]
    public void ForgetParticipant_LateEntityLeaveDoesNotRemoveDifferentSenderInReusedSlot()
    {
        var service = new ChatModerationService();
        var replacement = new ChatModerationParticipant(
            2,
            "Replacement",
            EntityId: null,
            SenderId: 0x20);
        service.ObserveParticipant(replacement);

        service.ForgetParticipant(new ChatModerationParticipant(
            2,
            "Departed",
            "entity-old"));

        var player = Assert.Single(service.GetSnapshot().Players);
        Assert.Equal(0x20u, player.Participant.SenderId);
        Assert.Equal("Replacement", player.Participant.DisplayName);
    }

    [Fact]
    public void ApplyConfiguration_DeepCopiesAndAppliesBlockedPlayersImmediately()
    {
        var service = new ChatModerationService();
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.BlockedPlayers.Add(Blocked("entity-1"));
        service.ApplyConfiguration(configuration);

        configuration.Rules[0].Term = "changed";
        configuration.BlockedPlayers.Clear();

        var blocked = Evaluate(service, Remote("entity-1"), "bad");
        Assert.Equal(ChatModerationDisposition.Block, blocked.Disposition);

        service.ApplyConfiguration(new ChatFilterConfiguration { Enabled = false });
        Assert.False(service.IsBlocked(Remote("entity-1")));
    }

    [Fact]
    public void ApplyConfiguration_RemovingPersistentEntityRemovesRoomAndAutoBlock()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        configuration.AutoBlockWindowMinutes = 10;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);

        Evaluate(service, Remote("entity-1"), "bad", Start);
        var threshold = Evaluate(service, Remote("entity-1"), "bad", Start.AddMinutes(1));

        Assert.True(threshold.AutoBlocked);
        Assert.True(service.IsBlocked(Remote("entity-1")));

        var persisted = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        persisted.AutoBlockEnabled = true;
        persisted.AutoBlockThreshold = 2;
        persisted.AutoBlockWindowMinutes = 10;
        persisted.BlockedPlayers.Add(Blocked("entity-1"));
        service.ApplyConfiguration(persisted);
        Assert.True(service.IsBlocked(Remote("entity-1")));

        service.ApplyConfiguration(CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" }));

        Assert.False(service.IsBlocked(Remote("entity-1")));
        var player = Assert.Single(
            service.GetSnapshot().Players,
            status => status.Participant.EntityId == "entity-1");
        Assert.False(player.IsRoomBlocked);
        Assert.False(player.IsPersistentlyBlocked);
    }

    [Fact]
    public void ClearRoom_ResetsRoomStateAndCountersButKeepsPersistentBlocks()
    {
        var service = new ChatModerationService();
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        configuration.AutoBlockWindowMinutes = 10;
        configuration.BlockedPlayers.Add(Blocked("persistent"));
        service.ApplyConfiguration(configuration);

        Evaluate(service, Remote("room"), "bad", Start);
        Evaluate(service, Remote("room"), "bad", Start.AddMinutes(1));
        Assert.True(service.TryReadEvent(out _));

        service.ClearRoom();

        Assert.Empty(service.GetSnapshot().Players);
        var rulesAfterClear = service.GetSnapshot().Rules;
        Assert.Equal(0, Assert.Single(rulesAfterClear).SessionHitCount);
        Assert.Equal(0, service.GetSnapshot().SessionFilteredMessageCount);
        Assert.False(service.IsBlocked(Remote("room")));
        Assert.True(service.IsBlocked(Remote("persistent")));
        Assert.False(service.TryReadEvent(out _));
    }

    [Fact]
    public void Preview_IsSideEffectFree()
    {
        var configuration = CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" });
        configuration.AutoBlockEnabled = true;
        configuration.AutoBlockThreshold = 2;
        var service = new ChatModerationService();
        service.ApplyConfiguration(configuration);

        var preview = service.Preview("bad bad");

        Assert.Equal(ChatModerationDisposition.Mask, preview.Disposition);
        Assert.True(preview.Matched);
        Assert.Equal(["bad"], preview.MatchedRuleIds);
        Assert.Equal(0, service.GetSnapshot().SessionFilteredMessageCount);
        Assert.Empty(service.GetSnapshot().Players);
        Assert.False(service.TryReadEvent(out _));
    }

    [Fact]
    public void ConcurrentEvaluate_IsThreadSafe()
    {
        var service = new ChatModerationService();
        service.ApplyConfiguration(CreateConfiguration(
            new ChatFilterRuleConfiguration { Id = "bad", Term = "bad" }));

        Parallel.For(0, 200, index =>
            Evaluate(service, Remote($"remote-{index}", playerNumber: 0), "bad"));

        var snapshot = service.GetSnapshot();
        Assert.Equal(200, snapshot.SessionFilteredMessageCount);
        Assert.Equal(200, Assert.Single(snapshot.Rules).SessionHitCount);
        Assert.Equal(200, snapshot.Players.Count);
    }

    private static ChatFilterConfiguration CreateConfiguration(
        params ChatFilterRuleConfiguration[] rules) => new()
    {
        Enabled = true,
        UseSteamTextFilter = false,
        Action = ChatFilterAction.MaskMatchedWords,
        AutoBlockEnabled = false,
        AutoBlockThreshold = 3,
        AutoBlockWindowMinutes = 10,
        Rules = rules.ToList(),
        BlockedPlayers = [],
    };

    private static BlockedPlayerConfiguration Blocked(string identity) => new()
    {
        IdentityKind = BlockedPlayerIdentityKind.PlayFabEntityId,
        Identity = identity,
    };

    private static ChatModerationParticipant Remote(
        string entityId,
        uint senderId = 0,
        int playerNumber = 2,
        string displayName = "Remote") =>
        new(playerNumber, displayName, entityId, senderId);

    private static ChatModerationDecision Evaluate(
        ChatModerationService service,
        ChatModerationParticipant participant,
        string text,
        DateTimeOffset? receivedAt = null,
        ChatCommunicationCue cue = ChatCommunicationCue.None)
    {
        return service.Evaluate(new ChatModerationInput(
            participant,
            text,
            receivedAt ?? Start,
            cue));
    }

    private sealed class StubOfficialFilter : IOfficialTextFilter
    {
        private readonly OfficialTextFilterResult _result;
        private readonly OfficialTextFilterStatus _status;

        public StubOfficialFilter(
            OfficialTextFilterResult result,
            OfficialTextFilterStatus? status = null)
        {
            _result = result;
            _status = status
                ?? new OfficialTextFilterStatus(OfficialTextFilterState.Ready, "ready");
        }

        public List<string> Inputs { get; } = [];

        public OfficialTextFilterStatus Status => _status;

        public OfficialTextFilterStatus Refresh() => _status;

        public OfficialTextFilterResult Filter(string text)
        {
            Inputs.Add(text);
            return _result;
        }
    }

    private sealed class ThrowingOfficialFilter : IOfficialTextFilter
    {
        private readonly bool _throwOnFilter;
        private readonly bool _throwOnRefresh;

        public ThrowingOfficialFilter(
            bool throwOnFilter = false,
            bool throwOnRefresh = false)
        {
            _throwOnFilter = throwOnFilter;
            _throwOnRefresh = throwOnRefresh;
        }

        public OfficialTextFilterStatus Status =>
            new(OfficialTextFilterState.Ready, "ready");

        public OfficialTextFilterStatus Refresh()
        {
            if (_throwOnRefresh)
                throw new InvalidOperationException("refresh failed");

            return Status;
        }

        public OfficialTextFilterResult Filter(string text)
        {
            if (_throwOnFilter)
                throw new InvalidOperationException("filter failed");

            return new OfficialTextFilterResult(text, 0, true);
        }
    }
}
