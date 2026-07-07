using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Unit tests for <see cref="FocusRegistry"/> and <see cref="OnlineMemberRegistry"/> — the two
/// pure in-memory registries the fan-out engine reads on the send path (C3 Task 5). Both are
/// lock-guarded, connection-scoped, and never touch Mongo or SignalR — these tests exercise them
/// in complete isolation from any infrastructure.
/// </summary>
public class FanOutRegistryTests
{
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _memberRegistry;

    [SetUp]
    public void SetUp()
    {
        _focusRegistry = new FocusRegistry();
        _memberRegistry = new OnlineMemberRegistry();
    }

    // ---------------------------------------------------------------------
    // FocusRegistry
    // ---------------------------------------------------------------------

    [Test]
    public void Focus_ThenGetFocusedConnections_ReturnsTheConnection()
    {
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");

        var connections = _focusRegistry.GetFocusedConnections("channel-a");

        Assert.That(connections, Is.EquivalentTo(new[] { "conn-1" }));
    }

    [Test]
    public void Unfocus_RemovesConnection_FromGetFocusedConnections()
    {
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");

        _focusRegistry.Unfocus("conn-1", "channel-a");

        Assert.That(_focusRegistry.GetFocusedConnections("channel-a"), Is.Empty);
    }

    [Test]
    public void GetFocusedConnections_UnknownChannel_ReturnsEmpty()
    {
        Assert.That(_focusRegistry.GetFocusedConnections("no-such-channel"), Is.Empty);
    }

    [Test]
    public void Unfocus_UnknownChannelOrConnection_NoOps()
    {
        Assert.DoesNotThrow(() => _focusRegistry.Unfocus("conn-unknown", "channel-unknown"));
    }

    [Test]
    public void Focus_Idempotent_RefocusingSameChannel_DoesNotDuplicateRosterEntry()
    {
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123"); // re-focus, same channel

        Assert.That(_focusRegistry.GetFocusedConnections("channel-a"), Is.EquivalentTo(new[] { "conn-1" }));
        Assert.That(_focusRegistry.GetRoster("channel-a"), Is.EquivalentTo(new[] { "peter#123" }));
    }

    [Test]
    public void Focus_Refocus_WithDifferentBattleTag_UpdatesRecordedBattleTag()
    {
        // A connection's battleTag can only change via re-Focus (identity is fixed for its session,
        // but the caller is the source of truth) — the LATEST value supplied must win in the roster.
        _focusRegistry.Focus("conn-1", "channel-a", "old#1");
        _focusRegistry.Focus("conn-1", "channel-b", "new#2");

        Assert.That(_focusRegistry.GetRoster("channel-a"), Is.EquivalentTo(new[] { "new#2" }));
        Assert.That(_focusRegistry.GetRoster("channel-b"), Is.EquivalentTo(new[] { "new#2" }));
    }

    [Test]
    public void Roster_IsDistinctBattleTags_OfFocusedConnections()
    {
        // Two connections, same battleTag (e.g. two tabs) — the roster must collapse to one entry.
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");
        _focusRegistry.Focus("conn-2", "channel-a", "peter#123");
        // A distinct battleTag on the same channel adds a second roster entry.
        _focusRegistry.Focus("conn-3", "channel-a", "alice#456");

        var roster = _focusRegistry.GetRoster("channel-a");

        Assert.That(roster, Is.EquivalentTo(new[] { "peter#123", "alice#456" }));
    }

    [Test]
    public void Roster_CollapsesDifferentlyCased_SameBattleTag_ToOneEntry()
    {
        // The DB lowercases stored battleTags while a live one keeps its original casing (matches
        // SessionRegistry/ConnectionMapping semantics) — an ordinal compare would wrongly split one
        // player into two roster entries when two of their connections carry different casing.
        _focusRegistry.Focus("conn-1", "channel-a", "Peter#123");
        _focusRegistry.Focus("conn-2", "channel-a", "peter#123");

        var roster = _focusRegistry.GetRoster("channel-a");

        Assert.That(roster, Has.Count.EqualTo(1));
        Assert.That(roster.Single(), Is.EqualTo("Peter#123").IgnoreCase);
    }

    [Test]
    public void GetRoster_UnknownChannel_ReturnsEmpty()
    {
        Assert.That(_focusRegistry.GetRoster("no-such-channel"), Is.Empty);
    }

    [Test]
    public void GetFocusedChannels_ReturnsDistinctChannelIds_ForThatConnectionOnly()
    {
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");
        _focusRegistry.Focus("conn-1", "channel-b", "peter#123");
        _focusRegistry.Focus("conn-2", "channel-c", "alice#456");

        var channels = _focusRegistry.GetFocusedChannels("conn-1");

        Assert.That(channels, Is.EquivalentTo(new[] { "channel-a", "channel-b" }),
            "Must return only conn-1's own focused channels, never another connection's");
    }

    [Test]
    public void GetFocusedChannels_Refocus_DoesNotDuplicate()
    {
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");

        Assert.That(_focusRegistry.GetFocusedChannels("conn-1"), Has.Count.EqualTo(1));
    }

    [Test]
    public void GetFocusedChannels_UnknownConnection_ReturnsEmpty()
    {
        Assert.That(_focusRegistry.GetFocusedChannels("conn-unknown"), Is.Empty);
    }

    [Test]
    public void GetFocusedChannels_AfterUnfocus_NoLongerIncludesThatChannel()
    {
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");
        _focusRegistry.Focus("conn-1", "channel-b", "peter#123");

        _focusRegistry.Unfocus("conn-1", "channel-a");

        Assert.That(_focusRegistry.GetFocusedChannels("conn-1"), Is.EquivalentTo(new[] { "channel-b" }));
    }

    [Test]
    public void RemoveConnection_ClearsAllEntries()
    {
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");
        _focusRegistry.Focus("conn-1", "channel-b", "peter#123");
        _focusRegistry.Focus("conn-2", "channel-a", "alice#456");

        _focusRegistry.RemoveConnection("conn-1");

        Assert.That(_focusRegistry.GetFocusedConnections("channel-a"), Is.EquivalentTo(new[] { "conn-2" }));
        Assert.That(_focusRegistry.GetFocusedConnections("channel-b"), Is.Empty);
        Assert.That(_focusRegistry.GetRoster("channel-a"), Is.EquivalentTo(new[] { "alice#456" }));
        // conn-1's own membership footprint is gone; a later RemoveConnection for it is a safe no-op.
        Assert.DoesNotThrow(() => _focusRegistry.RemoveConnection("conn-1"));
    }

    [Test]
    public void FocusRegistry_RemoveConnection_UnknownConnection_NoOps()
    {
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");

        Assert.DoesNotThrow(() => _focusRegistry.RemoveConnection("conn-unknown"));

        Assert.That(_focusRegistry.GetFocusedConnections("channel-a"), Is.EquivalentTo(new[] { "conn-1" }));
    }

    [Test]
    public void GetFocusedConnections_ReturnsSnapshot_NotLiveInternalCollection()
    {
        _focusRegistry.Focus("conn-1", "channel-a", "peter#123");

        var snapshot = _focusRegistry.GetFocusedConnections("channel-a");
        _focusRegistry.Focus("conn-2", "channel-a", "alice#456");

        // Mutating the registry after the snapshot was taken must not retroactively change it.
        Assert.That(snapshot, Is.EquivalentTo(new[] { "conn-1" }));
    }

    // ---------------------------------------------------------------------
    // OnlineMemberRegistry
    // ---------------------------------------------------------------------

    [Test]
    public void Seed_AddsManyChannelEntries_ForOneConnection()
    {
        var memberships = new[]
        {
            ("channel-a", new MemberState("peter#123", NotificationLevel.All, 10, ChannelType.Public)),
            ("channel-b", new MemberState("peter#123", NotificationLevel.Mentions, 0, ChannelType.Public)),
        };

        _memberRegistry.Seed("conn-1", memberships);

        var membersA = _memberRegistry.GetMembers("channel-a");
        var membersB = _memberRegistry.GetMembers("channel-b");
        Assert.That(membersA.Select(m => m.BattleTag), Is.EquivalentTo(new[] { "peter#123" }));
        Assert.That(membersB.Single().NotificationLevel, Is.EqualTo(NotificationLevel.Mentions));
    }

    [Test]
    public void Join_AddsSingleMembership()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 0, ChannelType.Public));

        var members = _memberRegistry.GetMembers("channel-a");

        Assert.That(members.Count, Is.EqualTo(1));
        Assert.That(members.Single().BattleTag, Is.EqualTo("peter#123"));
    }

    [Test]
    public void Leave_RemovesSingleMembership()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 0, ChannelType.Public));

        _memberRegistry.Leave("channel-a", "conn-1");

        Assert.That(_memberRegistry.GetMembers("channel-a"), Is.Empty);
    }

    [Test]
    public void Leave_UnknownMembership_NoOps()
    {
        Assert.DoesNotThrow(() => _memberRegistry.Leave("channel-a", "conn-unknown"));
    }

    [Test]
    public void SetLevel_UpdatesNotificationLevel_ForThatMembershipOnly()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 0, ChannelType.Public));
        _memberRegistry.Join("channel-a", "conn-2", new MemberState("alice#456", NotificationLevel.All, 0, ChannelType.Public));

        _memberRegistry.SetNotificationLevel("channel-a", "conn-1", NotificationLevel.None);

        var members = _memberRegistry.GetMembers("channel-a").ToDictionary(m => m.BattleTag);
        Assert.That(members["peter#123"].NotificationLevel, Is.EqualTo(NotificationLevel.None));
        Assert.That(members["alice#456"].NotificationLevel, Is.EqualTo(NotificationLevel.All));
    }

    [Test]
    public void SetLevel_UnknownMembership_NoOps()
    {
        Assert.DoesNotThrow(() => _memberRegistry.SetNotificationLevel("channel-a", "conn-unknown", NotificationLevel.None));
    }

    [Test]
    public void SetLastReadSeq_UpdatesLastReadSeq_ForThatMembershipOnly()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 0, ChannelType.Public));

        _memberRegistry.SetLastReadSeq("channel-a", "conn-1", 42);

        Assert.That(_memberRegistry.GetMembers("channel-a").Single().LastReadSeq, Is.EqualTo(42));
    }

    [Test]
    public void SetLastReadSeq_UnknownMembership_NoOps()
    {
        Assert.DoesNotThrow(() => _memberRegistry.SetLastReadSeq("channel-a", "conn-unknown", 42));
    }

    // ---------------------------------------------------------------------
    // OnlineMemberRegistry.AdvanceLastReadSeq — the monotonic (Math.Max) sibling of SetLastReadSeq;
    // ChatHub.MarkRead (Task 17) calls THIS, never the plain-overwrite SetLastReadSeq, so a stale/
    // out-of-order MarkRead can never regress the in-memory registry (see ChatHub.Messaging.cs's
    // dual-store monotonic invariant doc comment).
    // ---------------------------------------------------------------------

    [Test]
    public void AdvanceLastReadSeq_HigherSeq_Advances()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 5, ChannelType.Public));

        _memberRegistry.AdvanceLastReadSeq("channel-a", "conn-1", 10);

        Assert.That(_memberRegistry.GetMembers("channel-a").Single().LastReadSeq, Is.EqualTo(10));
    }

    [Test]
    public void AdvanceLastReadSeq_LowerSeq_DoesNotRegress()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 10, ChannelType.Public));

        _memberRegistry.AdvanceLastReadSeq("channel-a", "conn-1", 3);

        Assert.That(_memberRegistry.GetMembers("channel-a").Single().LastReadSeq, Is.EqualTo(10),
            "a lower/stale seq must be a no-op — Math.Max never regresses the tracked cursor");
    }

    [Test]
    public void AdvanceLastReadSeq_EqualSeq_IsANoOp()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 10, ChannelType.Public));

        _memberRegistry.AdvanceLastReadSeq("channel-a", "conn-1", 10);

        Assert.That(_memberRegistry.GetMembers("channel-a").Single().LastReadSeq, Is.EqualTo(10));
    }

    [Test]
    public void AdvanceLastReadSeq_UnknownMembership_NoOps()
    {
        Assert.DoesNotThrow(() => _memberRegistry.AdvanceLastReadSeq("channel-a", "conn-unknown", 42));
    }

    [Test]
    public void GetMembers_UnknownChannel_ReturnsEmpty()
    {
        Assert.That(_memberRegistry.GetMembers("no-such-channel"), Is.Empty);
    }

    [Test]
    public void IsMember_TrueForSeededChannel_FalseOtherwise()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 0, ChannelType.Public));

        Assert.IsTrue(_memberRegistry.IsMember("conn-1", "channel-a"),
            "A seeded (connectionId, channelId) pair must report membership via the O(1) reverse-index lookup");
        Assert.IsFalse(_memberRegistry.IsMember("conn-1", "channel-b"),
            "The same connection has no entry for a different channel");
        Assert.IsFalse(_memberRegistry.IsMember("conn-unknown", "channel-a"),
            "An unknown connectionId must not report membership");
    }

    [Test]
    public void GetMembers_ReturnsSnapshot_NotLiveInternalCollection()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 0, ChannelType.Public));

        var snapshot = _memberRegistry.GetMembers("channel-a");
        _memberRegistry.Join("channel-a", "conn-2", new MemberState("alice#456", NotificationLevel.All, 0, ChannelType.Public));

        Assert.That(snapshot.Count, Is.EqualTo(1));
    }

    [Test]
    public void RemoveConnection_DropsAllOfAConnectionsEntries_AcrossChannels()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 0, ChannelType.Public));
        _memberRegistry.Join("channel-b", "conn-1", new MemberState("peter#123", NotificationLevel.All, 0, ChannelType.Public));
        _memberRegistry.Join("channel-a", "conn-2", new MemberState("alice#456", NotificationLevel.All, 0, ChannelType.Public));

        _memberRegistry.RemoveConnection("conn-1");

        Assert.That(_memberRegistry.GetMembers("channel-a").Select(m => m.BattleTag), Is.EquivalentTo(new[] { "alice#456" }));
        Assert.That(_memberRegistry.GetMembers("channel-b"), Is.Empty);
        // Torn-down connection footprint is fully gone; a repeat call is a safe no-op.
        Assert.DoesNotThrow(() => _memberRegistry.RemoveConnection("conn-1"));
    }

    [Test]
    public void OnlineMemberRegistry_RemoveConnection_UnknownConnection_NoOps()
    {
        _memberRegistry.Join("channel-a", "conn-1", new MemberState("peter#123", NotificationLevel.All, 0, ChannelType.Public));

        Assert.DoesNotThrow(() => _memberRegistry.RemoveConnection("conn-unknown"));

        Assert.That(_memberRegistry.GetMembers("channel-a"), Has.Count.EqualTo(1));
    }

    // ---------------------------------------------------------------------
    // Cross-registry: connection scoping
    // ---------------------------------------------------------------------

    [Test]
    public void Registries_AreConnectionScoped_IndependentAcrossBattleTags()
    {
        // Two DIFFERENT battleTags, each on their own connection, both focused + joined on the
        // same channel. Mutating/removing one connection's state must never bleed into the other's,
        // and each registry keys strictly by connectionId (never by battleTag).
        _focusRegistry.Focus("conn-peter", "channel-a", "peter#123");
        _focusRegistry.Focus("conn-alice", "channel-a", "alice#456");
        _memberRegistry.Join("channel-a", "conn-peter", new MemberState("peter#123", NotificationLevel.All, 0, ChannelType.Public));
        _memberRegistry.Join("channel-a", "conn-alice", new MemberState("alice#456", NotificationLevel.Mentions, 5, ChannelType.Public));

        _memberRegistry.SetNotificationLevel("channel-a", "conn-peter", NotificationLevel.None);
        _focusRegistry.Unfocus("conn-peter", "channel-a");

        // Alice's focus + membership state is untouched by Peter's mutations.
        Assert.That(_focusRegistry.GetFocusedConnections("channel-a"), Is.EquivalentTo(new[] { "conn-alice" }));
        var aliceMember = _memberRegistry.GetMembers("channel-a").Single(m => m.BattleTag == "alice#456");
        Assert.That(aliceMember.NotificationLevel, Is.EqualTo(NotificationLevel.Mentions));
        Assert.That(aliceMember.LastReadSeq, Is.EqualTo(5));

        // Removing Peter's connection entirely leaves Alice's entries intact.
        _focusRegistry.RemoveConnection("conn-peter");
        _memberRegistry.RemoveConnection("conn-peter");

        Assert.That(_memberRegistry.GetMembers("channel-a").Select(m => m.BattleTag), Is.EquivalentTo(new[] { "alice#456" }));
    }

    // ---------------------------------------------------------------------
    // Thread-safety smoke test
    // ---------------------------------------------------------------------

    [Test]
    public void ParallelMutateAndRead_AcrossBothRegistries_DoesNotThrow()
    {
        const int connectionCount = 50;
        var connectionIds = Enumerable.Range(0, connectionCount).Select(i => $"conn-{i}").ToList();

        Assert.DoesNotThrowAsync(async () =>
        {
            var tasks = connectionIds.Select(connectionId => Task.Run(() =>
            {
                for (var i = 0; i < 25; i++)
                {
                    var channelId = $"channel-{i % 5}";
                    var battleTag = $"tag-{connectionId}";

                    _focusRegistry.Focus(connectionId, channelId, battleTag);
                    _focusRegistry.GetFocusedConnections(channelId);
                    _focusRegistry.GetRoster(channelId);

                    _memberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.All, i, ChannelType.Public));
                    _memberRegistry.SetNotificationLevel(channelId, connectionId, NotificationLevel.Mentions);
                    _memberRegistry.SetLastReadSeq(channelId, connectionId, i);
                    _memberRegistry.GetMembers(channelId);

                    _focusRegistry.Unfocus(connectionId, channelId);
                    _memberRegistry.Leave(channelId, connectionId);
                }

                _focusRegistry.RemoveConnection(connectionId);
                _memberRegistry.RemoveConnection(connectionId);
            }));

            await Task.WhenAll(tasks);
        });
    }
}
