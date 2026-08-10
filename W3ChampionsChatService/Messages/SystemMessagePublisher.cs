using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using Serilog;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Messages;

/// <summary>Outcome of a system-message publish. <see cref="Seq"/> is 0 when <see cref="Code"/> is not Ok.</summary>
public record SystemMessagePublishResult(ChatResultCode Code, string MessageId, long Seq);

/// <summary>
/// The ONE server-authored message insert path — the second (and only other) caller of
/// <see cref="MessageRepository.Insert"/> besides <c>ChatHub.SendMessage</c>.
/// <para>
/// Deliberately skips every stage of the user send pipeline that exists to police a human: no session
/// lookup, no <c>MessageRateLimiter</c>, no mute gate, no mention extraction, no <c>MentionFanOut</c>.
/// It still does the two things the rest of the system depends on: <see cref="ChannelRepository.AllocateSeq"/>
/// (seq-anchored paging, unread, and <c>LastMessageAt</c> all key off it) and
/// <see cref="FanOutEngine.OnMessagePersisted"/>. Durable write strictly precedes the push, like every
/// other write path here.
/// </para>
/// <para>
/// <c>shellExpiresAt</c> is ALWAYS null: retention is deliberately unchanged (design D6), and System
/// channels are creation-anchored — only Dm/GroupDm sends may re-stamp a channel shell's TTL.
/// <c>senderConnectionId</c> is null and <c>isShadow</c> false; with no shadow the null sender id is
/// inert in fan-out (it only ever participates in reference comparisons).
/// </para>
/// <para>
/// IDEMPOTENCY: when <c>dedupeKey</c> is non-null (and non-empty — an empty string is normalized to
/// null up front, exactly like "no key") the publish is at-most-once per (channel, key). The
/// pre-check handles the common retry; the duplicate-key catch handles a genuine concurrent race, since
/// the seq allocation and the insert are not one atomic unit. A race burns a seq number — harmless,
/// because paging is seq-ANCHORED and never assumes contiguity.
/// </para>
/// </summary>
public class SystemMessagePublisher(
    MessageRepository messageRepository,
    ChannelRepository channelRepository,
    FanOutEngine fanOutEngine,
    TimeProvider timeProvider)
{
    public async Task<SystemMessagePublishResult> Publish(ChatChannel channel, SystemMessageBody body, string dedupeKey)
    {
        if (channel == null)
        {
            return new SystemMessagePublishResult(ChatResultCode.NotFound, null, 0);
        }

        // Normalized once, here, so the pre-check, the insert, and the duplicate-key catch all agree
        // on the same value — an empty string must never reach ChannelMessage.DedupeKey (it would be
        // indexed and would silently dedupe every empty-key system message in the channel together).
        dedupeKey = string.IsNullOrEmpty(dedupeKey) ? null : dedupeKey;

        if (dedupeKey != null)
        {
            var existing = await messageRepository.LoadByDedupeKey(channel.Id, dedupeKey);
            if (existing != null)
            {
                return new SystemMessagePublishResult(ChatResultCode.Ok, existing.Id, existing.Seq);
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var seq = await channelRepository.AllocateSeq(channel.Id, now, shellExpiresAt: null);

        var message = new ChannelMessage
        {
            ChannelId = channel.Id,
            Seq = seq,
            Kind = MessageKind.System,
            SystemMessage = body,
            DedupeKey = dedupeKey,
            SentAt = now,
            ExpiresAt = ExpiryCalculator.ForChannelMessage(channel.Type, now),
        };

        try
        {
            await messageRepository.Insert(message);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey && dedupeKey != null)
        {
            // Concurrent publish of the same key won the race. Return ITS message, not an error —
            // the caller asked for "this system message exists", and it does.
            var winner = await messageRepository.LoadByDedupeKey(channel.Id, dedupeKey);
            if (winner != null)
            {
                return new SystemMessagePublishResult(ChatResultCode.Ok, winner.Id, winner.Seq);
            }
            throw;
        }

        await fanOutEngine.OnMessagePersisted(channel, message, senderConnectionId: null, isShadow: false, now);

        Log.Information(
            "System message published {Key} channel={ChannelId} seq={Seq} dedupeKey={DedupeKey}",
            body.Key, channel.Id, seq, dedupeKey);

        return new SystemMessagePublishResult(ChatResultCode.Ok, message.Id, seq);
    }
}
