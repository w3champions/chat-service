using System;
using W3ChampionsChatService.Channels;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Pure expiresAt computation per lifecycle. One TTL index on expiresAt per collection
/// (expireAfterSeconds 0) does the actual deletion; permanent docs omit the field.
/// </summary>
public static class ExpiryCalculator
{
    /// <summary>Messages: 30d for channel messages, 90d for DM + group messages.</summary>
    public static DateTime ForChannelMessage(ChannelType channelType, DateTime sentAt) =>
        channelType is ChannelType.Dm or ChannelType.GroupDm
            ? sentAt + RetentionPeriods.DirectMessages
            : sentAt + RetentionPeriods.ChannelMessages;

    /// <summary>Mention inbox: 30d — always ≤ message TTL so a notification never outlives its message.</summary>
    public static DateTime ForMentionInboxEntry(DateTime createdAt) =>
        createdAt + RetentionPeriods.MentionInbox;

    /// <summary>
    /// Channel-shell expiry. referenceTime semantics per type:
    /// match/lobby = CREATION time (match end is unknown at creation; no extension call),
    /// pending dm = last ACTIVITY, accepted dm/group = last MESSAGE (maintained on each message).
    /// Returns null for permanent channels (public, clan) and for semiPublic (weekly GC job instead).
    /// </summary>
    public static DateTime? ForChannelShell(ChatChannel channel, DateTime referenceTime) => channel.Type switch
    {
        ChannelType.Public or ChannelType.SemiPublic => null,
        ChannelType.System when channel.SystemKind == SystemChannelKind.Clan => null,
        ChannelType.System => referenceTime + RetentionPeriods.MatchChannel,
        ChannelType.Dm when channel.RequestState == DmRequestState.Pending => referenceTime + RetentionPeriods.PendingDmShell,
        ChannelType.Dm or ChannelType.GroupDm => referenceTime + RetentionPeriods.DmShell,
        _ => null,
    };
}
