using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C3 (Task 18) tests for the <see cref="FanOutEngine.PushChannelAdded"/> / <see cref="FanOutEngine.PushChannelRemoved"/>
/// emit helpers — CONTRACT COMPLETENESS only. There are no production callers in C3: C5/C7 wire the
/// actual channel-add/remove triggers later. Pure in-memory: a real <see cref="SessionRegistry"/>,
/// <see cref="FocusRegistry"/> and <see cref="OnlineMemberRegistry"/>, plus a
/// <see cref="HubPushCaptureHarness"/> capturing every push. No Mongo, no live hub.
/// </summary>
public class ChannelEventEmitterTests
{
    private const string ChannelId = "channel-added-removed";
    private const string BattleTag = "Viewer#1";
    private const string ConnectionId = "conn-viewer";

    private static ChatChannel Channel() =>
        new ChatChannel { Id = ChannelId, Type = ChannelType.Public };

    private static ChannelMembership Membership(NotificationLevel level = NotificationLevel.All, long lastReadSeq = 3) =>
        new ChannelMembership
        {
            ChannelId = ChannelId,
            BattleTag = BattleTag,
            Role = MembershipRole.Member,
            NotificationLevel = level,
            LastReadSeq = lastReadSeq,
        };

    private static (HubPushCaptureHarness harness, FocusRegistry focus, OnlineMemberRegistry members, SessionRegistry sessions, FanOutEngine engine)
        NewFixture()
    {
        var harness = new HubPushCaptureHarness();
        var focus = new FocusRegistry();
        var members = new OnlineMemberRegistry();
        var sessions = new SessionRegistry();
        var coalescer = new ActivityCoalescer(harness.HubContext, members);
        var engine = new FanOutEngine(harness.HubContext, focus, members, coalescer, sessions, new PresenceInterestRegistry(), new ViewersAccumulator(harness.HubContext, focus, new ViewerResolver(new SessionRegistry(), new ConnectionMapping())), TimeProvider.System);
        return (harness, focus, members, sessions, engine);
    }

    // Registers a live session (SessionRegistry.Register) for battleTag under connectionId — the same
    // in-memory idiom ViewersAccumulatorTests/ChatHubFocusTests use.
    private static void RegisterOnline(SessionRegistry sessions, string connectionId, string battleTag) =>
        sessions.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    [Test]
    public async Task PushChannelAdded_SendsChannelMembershipAndFocusFlag_ToUsersLiveConnection()
    {
        var (harness, _, _, sessions, engine) = NewFixture();
        RegisterOnline(sessions, ConnectionId, BattleTag);
        var channel = Channel();
        var membership = Membership();

        await engine.PushChannelAdded(channel, membership, focus: true);

        Assert.AreEqual(1, harness.SignalCount(ConnectionId, ChatEvents.ChannelAdded));
        var dto = harness.PayloadFor(ConnectionId, ChatEvents.ChannelAdded) as ChannelAddedDto;
        Assert.IsNotNull(dto, "the user's live connection must receive a ChannelAddedDto payload");
        Assert.AreSame(channel, dto.Channel, "the raw ChatChannel is forwarded unchanged (no boundary-private fields to strip)");
        Assert.AreEqual(membership.NotificationLevel, dto.Membership.NotificationLevel);
        Assert.AreEqual(membership.LastReadSeq, dto.Membership.LastReadSeq);
        Assert.AreEqual(membership.Role, dto.Membership.Role);
        Assert.IsTrue(dto.Focus, "the focus flag must pass through unchanged as a client directive");
    }

    [Test]
    public async Task PushChannelAdded_SeedsOnlineMemberRegistry()
    {
        var (_, _, members, sessions, engine) = NewFixture();
        RegisterOnline(sessions, ConnectionId, BattleTag);
        var membership = Membership(level: NotificationLevel.Mentions, lastReadSeq: 7);

        await engine.PushChannelAdded(Channel(), membership, focus: false);

        Assert.IsTrue(members.IsMember(ConnectionId, ChannelId),
            "the online-member registry must be seeded so activity fan-out (OnMessagePersisted) starts immediately, without waiting for a reconnect");
        Assert.IsTrue(members.TryGetMember(ChannelId, ConnectionId, out var state));
        Assert.AreEqual(BattleTag, state.BattleTag);
        Assert.AreEqual(NotificationLevel.Mentions, state.NotificationLevel);
        Assert.AreEqual(7, state.LastReadSeq);
    }

    [Test]
    public async Task PushChannelRemoved_SendsChannelId_CleansRegistriesAndFocus()
    {
        var (harness, focus, members, sessions, engine) = NewFixture();
        RegisterOnline(sessions, ConnectionId, BattleTag);
        // Seed both registries (including focus) so the cleanup is actually observable.
        members.Join(ChannelId, ConnectionId, new MemberState(BattleTag, NotificationLevel.All, 0, ChannelType.Public));
        focus.Focus(ConnectionId, ChannelId, BattleTag);

        await engine.PushChannelRemoved(ChannelId, BattleTag);

        Assert.AreEqual(1, harness.SignalCount(ConnectionId, ChatEvents.ChannelRemoved));
        var dto = harness.PayloadFor(ConnectionId, ChatEvents.ChannelRemoved) as ChannelRemovedDto;
        Assert.IsNotNull(dto, "the user's live connection must receive a ChannelRemovedDto payload");
        Assert.AreEqual(ChannelId, dto.ChannelId);

        Assert.IsFalse(members.IsMember(ConnectionId, ChannelId), "the online-member entry for the removed channel must be cleaned up");
        Assert.IsFalse(focus.GetFocusedChannels(ConnectionId).Contains(ChannelId), "the focus entry for the removed channel must be cleaned up");
    }

    [Test]
    public async Task Push_OfflineUser_IsNoOp()
    {
        var (harness, focus, members, _, engine) = NewFixture();
        // Deliberately no RegisterOnline call — BattleTag has no live session for either push.

        await engine.PushChannelAdded(Channel(), Membership(), focus: true);
        await engine.PushChannelRemoved(ChannelId, BattleTag);

        Assert.IsEmpty(harness.AllSignals, "an offline user must receive nothing from either push helper");
        Assert.IsFalse(members.IsMember(ConnectionId, ChannelId), "PushChannelAdded must not seed any registry for an offline user");
        Assert.IsFalse(focus.GetFocusedChannels(ConnectionId).Contains(ChannelId), "PushChannelRemoved must not touch focus state for an offline user");
    }
}
