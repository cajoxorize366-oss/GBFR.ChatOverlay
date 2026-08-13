using System.ComponentModel;

namespace GBFR.ChatOverlay.Configuration;

public enum ChatFilterAction
{
    MaskMatchedWords = 0,
    HideEntireMessage = 1,
}

public enum ChatFilterNotificationMode
{
    LocalOnly = 0,
    PartyChat = 1,
    None = 2,
}

public enum BlockedPlayerIdentityKind
{
    PlayFabEntityId = 0,
}

public enum BlockedPlayerSource
{
    Manual = 0,
    FilterThreshold = 1,
}

public sealed class ChatFilterRuleConfiguration
{
    [Browsable(false)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Browsable(false)]
    public bool Enabled { get; set; } = true;

    [Browsable(false)]
    public string Term { get; set; } = string.Empty;
}

public sealed class BlockedPlayerConfiguration
{
    [Browsable(false)]
    public BlockedPlayerIdentityKind IdentityKind { get; set; } =
        BlockedPlayerIdentityKind.PlayFabEntityId;

    [Browsable(false)]
    public string Identity { get; set; } = string.Empty;

    [Browsable(false)]
    public string LastKnownName { get; set; } = string.Empty;

    [Browsable(false)]
    public BlockedPlayerSource Source { get; set; } = BlockedPlayerSource.Manual;

    [Browsable(false)]
    public string Reason { get; set; } = string.Empty;

    [Browsable(false)]
    public DateTimeOffset BlockedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChatFilterConfiguration
{
    public const string DefaultNotificationTemplate =
        "已将 {player} 屏蔽，原因：触发过滤条件次数过多";

    [Browsable(false)]
    public bool Enabled { get; set; }

    [Browsable(false)]
    public bool UseSteamTextFilter { get; set; } = true;

    [Browsable(false)]
    public ChatFilterAction Action { get; set; } = ChatFilterAction.MaskMatchedWords;

    [Browsable(false)]
    public bool AutoBlockEnabled { get; set; }

    [Browsable(false)]
    public int AutoBlockThreshold { get; set; } = 3;

    [Browsable(false)]
    public int AutoBlockWindowMinutes { get; set; } = 10;

    [Browsable(false)]
    public ChatFilterNotificationMode NotificationMode { get; set; } =
        ChatFilterNotificationMode.LocalOnly;

    [Browsable(false)]
    public string NotificationTemplate { get; set; } = DefaultNotificationTemplate;

    [Browsable(false)]
    public List<ChatFilterRuleConfiguration> Rules { get; set; } = [];

    [Browsable(false)]
    public List<BlockedPlayerConfiguration> BlockedPlayers { get; set; } = [];
}
