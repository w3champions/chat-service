using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C6 Task 5 (D3/D4): direct unit tests of <see cref="MentionFanOut.NotifyAsync"/> — the mention
/// fan-out's eligibility LEAK BOUNDARY and its per-target fault isolation. Real Mongo-backed
/// <see cref="MembershipRepository"/> + <see cref="MentionInboxRepository"/> (Testcontainers), a real
/// <see cref="SessionRegistry"/> for the live-push resolution, and a <see cref="HubPushCaptureHarness"/>
/// standing in for the SignalR delivery channel so every targeted <c>MentionNotified</c> is captured
/// per connection. A <see cref="FakeTimeProvider"/>-derived <c>now</c> makes the CreatedAt/ExpiresAt
/// assertions deterministic. NUnit constraint style.
/// <para>
/// The five eligibility rules (D3): (a) message NOT shadow; (b) target ≠ sender (case-insensitive);
/// (c) target has a <c>channel_memberships</c> row for THIS channel — the Dm/GroupDm excerpt PRIVACY
/// WALL; (d) membership <c>NotificationLevel != None</c>; (e) membership is NOT currently
/// decline-suppressed (<c>DeclinedUntil</c> unset or already elapsed vs. <c>now</c>). Every
/// negative-eligibility test below ALSO mentions an eligible CONTROL member in the SAME call and
/// asserts the control DID get an entry + event — so the test fails against a do-nothing stub AND
/// against an over-permissive filter, not just one of the two.
/// </para>
/// </summary>
public class MentionFanOutTests : IntegrationTestBase
{
    private const string ChannelId = "chan-1";
    private const string Sender = "Peter#123";
    private const string SenderName = "Peter";

    private static readonly DateTimeOffset FixedNow = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MentionInboxRepository _mentionInboxRepository;
    private SessionRegistry _sessionRegistry;
    private HubPushCaptureHarness _harness;
    private MentionFanOut _fanOut;
    private FakeTimeProvider _time;
    private UserDirectoryRepository _userDirectory;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _time = new FakeTimeProvider(FixedNow);
        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _mentionInboxRepository = new MentionInboxRepository(MongoClient);
        _sessionRegistry = new SessionRegistry();
        _harness = new HubPushCaptureHarness();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _fanOut = new MentionFanOut(_harness.HubContext, _sessionRegistry, _membershipRepository, _mentionInboxRepository, _userDirectory);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private void RegisterSession(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    private Task SeedMembership(
        string battleTag,
        NotificationLevel level = NotificationLevel.All,
        string channelId = ChannelId,
        DateTime? declinedUntil = null) =>
        _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            NotificationLevel = level,
            JoinedAt = Now,
            DeclinedUntil = declinedUntil,
        });

    private Task SeedDirectory(string battleTag) =>
        _userDirectory.Upsert(new UserDirectoryEntry
        {
            BattleTag = battleTag.ToLowerInvariant(),
            DisplayBattleTag = battleTag,
            NormalizedName = battleTag.ToLowerInvariant(),
            LastSeenAt = Now,
        });

    private static ChatChannel Channel(ChannelType type = ChannelType.Public) =>
        new ChatChannel { Id = ChannelId, Type = type };

    private ChannelMessage Message(
        string content = "hey there",
        long seq = 7,
        DateTime? expiresAt = null,
        bool shadow = false,
        string senderTag = Sender,
        string senderName = SenderName) =>
        new ChannelMessage
        {
            ChannelId = ChannelId,
            Seq = seq,
            Sender = new MessageSender { BattleTag = senderTag, Name = senderName },
            Content = content,
            SentAt = Now,
            Shadow = shadow,
            ExpiresAt = expiresAt ?? ExpiryCalculator.ForChannelMessage(ChannelType.Public, Now),
        };

    private Task<List<MentionInboxEntry>> InboxOf(string battleTag) =>
        _mentionInboxRepository.LoadForUser(battleTag.ToLowerInvariant());

    private async Task<long> TotalInboxCount() =>
        await MongoClient
            .GetDatabase(MongoDbRepositoryBase.DatabaseName)
            .GetCollection<MentionInboxEntry>(ChatCollections.MentionInbox)
            .CountDocumentsAsync(FilterDefinition<MentionInboxEntry>.Empty);

    private int MentionEventCount(string connectionId) =>
        _harness.SignalCount(connectionId, ChatEvents.MentionNotified);

    // ---------------------------------------------------------------------------------------------
    // Acceptance 1 — entry fields + targeted event, exact targeting
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Notify_Member_CreatesInboxEntry_WithAllFields_AndTargetedEvent()
    {
        // Mixed-case target proves the entry BattleTag is stored LOWERCASED while author fields keep
        // the sender's DISPLAY casing.
        const string Target = "Wolf#456";
        await SeedMembership(Target, NotificationLevel.All);
        RegisterSession("conn-wolf", Target);

        var message = Message(content: "hey there", seq: 42);
        await _fanOut.NotifyAsync(Channel(), message, new[] { Target }, Now);

        // Durable entry — one, with every field per D4/D5.
        var entries = await InboxOf(Target);
        Assert.That(entries, Has.Count.EqualTo(1), "exactly one inbox entry for the mentioned member");
        var entry = entries[0];
        Assert.That(entry.BattleTag, Is.EqualTo("wolf#456"), "entry BattleTag is stored lowercased (D8 key convention)");
        Assert.That(entry.ChannelId, Is.EqualTo(ChannelId));
        Assert.That(entry.MessageId, Is.EqualTo(message.Id));
        Assert.That(entry.Seq, Is.EqualTo(42L), "the mentioning message's per-channel seq rides the entry (D5)");
        Assert.That(entry.AuthorBattleTag, Is.EqualTo(Sender), "author battleTag keeps the sender's display casing");
        Assert.That(entry.AuthorName, Is.EqualTo(SenderName));
        Assert.That(entry.Excerpt, Is.EqualTo("hey there"), "short content is the excerpt verbatim");
        Assert.That(entry.CreatedAt, Is.EqualTo(Now).Within(TimeSpan.FromSeconds(1)));
        Assert.That(entry.ExpiresAt, Is.Not.Null);
        Assert.That(entry.ExpiresAt.Value, Is.EqualTo(Now.AddDays(30)).Within(TimeSpan.FromSeconds(1)),
            "mention-inbox expiry is CreatedAt + 30d (ExpiryCalculator.ForMentionInboxEntry — the C1 amendment-1 wiring)");

        // Targeted event — ONLY to the target's connection, carrying the entry id + author + excerpt.
        Assert.That(MentionEventCount("conn-wolf"), Is.EqualTo(1), "exactly one MentionNotified to the target");
        var dto = (MentionNotifiedDto)_harness.PayloadFor("conn-wolf", ChatEvents.MentionNotified);
        Assert.That(dto.EntryId, Is.EqualTo(entry.Id), "the event carries the just-inserted entry's id (insert-before-push)");
        Assert.That(dto.ChannelId, Is.EqualTo(ChannelId));
        Assert.That(dto.MessageId, Is.EqualTo(message.Id));
        Assert.That(dto.Seq, Is.EqualTo(42L));
        Assert.That(dto.AuthorBattleTag, Is.EqualTo(Sender));
        Assert.That(dto.AuthorName, Is.EqualTo(SenderName));
        Assert.That(dto.Excerpt, Is.EqualTo("hey there"));
    }

    [Test]
    public async Task Notify_ThirdPartyMember_NotMentioned_GetsNothing()
    {
        // Two eligible, online members; only ONE is mentioned. Targeting must be exact — never a
        // broadcast to the channel's members.
        await SeedMembership("wolf#456", NotificationLevel.All);
        RegisterSession("conn-wolf", "wolf#456");
        await SeedMembership("frank#789", NotificationLevel.All);
        RegisterSession("conn-frank", "frank#789");

        await _fanOut.NotifyAsync(Channel(), Message(), new[] { "wolf#456" }, Now);

        Assert.That(await InboxOf("wolf#456"), Has.Count.EqualTo(1), "the mentioned member gets an entry");
        Assert.That(MentionEventCount("conn-wolf"), Is.EqualTo(1), "the mentioned member gets the event");
        Assert.That(await InboxOf("frank#789"), Is.Empty, "an un-mentioned member gets NO entry");
        Assert.That(MentionEventCount("conn-frank"), Is.EqualTo(0), "an un-mentioned member captures ZERO MentionNotified");
    }

    // ---------------------------------------------------------------------------------------------
    // Focus is irrelevant / offline path
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Notify_OfflineMember_EntryOnly_NoEvent()
    {
        // Eligible member (durable row, level All) but NO live session → durable entry, no live push.
        await SeedMembership("wolf#456", NotificationLevel.All);
        // deliberately NO RegisterSession for wolf

        await _fanOut.NotifyAsync(Channel(), Message(), new[] { "wolf#456" }, Now);

        Assert.That(await InboxOf("wolf#456"), Has.Count.EqualTo(1),
            "an offline eligible member still gets the durable inbox entry (SessionState/GetMentionInbox surface it later)");
        Assert.That(_harness.AllSignals, Is.Empty, "no live connection to push to → zero events");
    }

    // ---------------------------------------------------------------------------------------------
    // Eligibility leak boundary (each pairs the ineligible target with an eligible control)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Notify_NonMember_NoEntryNoEvent_ControlMemberStillNotified()
    {
        // stranger#1 has NO membership row AND no user_directory row, so it stays ineligible even
        // now that Public rooms are membership-independent (§4): the wall this documents for Public
        // is directory-resolvability, not membership — stranger#1 has neither.
        RegisterSession("conn-stranger", "stranger#1");
        await SeedMembership("wolf#456", NotificationLevel.All);
        RegisterSession("conn-wolf", "wolf#456");

        await _fanOut.NotifyAsync(Channel(), Message(), new[] { "stranger#1", "wolf#456" }, Now);

        Assert.That(await InboxOf("stranger#1"), Is.Empty, "a non-member gets NO entry");
        Assert.That(MentionEventCount("conn-stranger"), Is.EqualTo(0), "a non-member gets NO event");
        Assert.That(await InboxOf("wolf#456"), Has.Count.EqualTo(1), "the member control still gets an entry");
        Assert.That(MentionEventCount("conn-wolf"), Is.EqualTo(1), "the member control still gets the event");
    }

    [Test]
    public async Task Notify_InGroupDm_NonMemberTarget_NoEntry_ControlMemberStillNotified()
    {
        // The excerpt PRIVACY WALL, spelled out for a GroupDm: an inbox entry carries a content excerpt,
        // so a non-participant of a private conversation must get NO entry even though they are mentioned
        // in the content.
        RegisterSession("conn-outsider", "outsider#1");
        await SeedMembership("wolf#456", NotificationLevel.All);
        RegisterSession("conn-wolf", "wolf#456");

        await _fanOut.NotifyAsync(Channel(ChannelType.GroupDm), Message(content: "secret plans"), new[] { "outsider#1", "wolf#456" }, Now);

        Assert.That(await InboxOf("outsider#1"), Is.Empty,
            "a non-member of a GroupDm gets NO entry — a private conversation's excerpt must never reach a non-participant");
        Assert.That(MentionEventCount("conn-outsider"), Is.EqualTo(0), "and no event");
        Assert.That(await InboxOf("wolf#456"), Has.Count.EqualTo(1), "the participant control still gets an entry");
        Assert.That(MentionEventCount("conn-wolf"), Is.EqualTo(1));
    }

    [Test]
    public async Task Notify_TargetLevelNone_NoEntryNoEvent_ControlMemberStillNotified()
    {
        // "none: silence" (spec §7) outranks mentions — an explicit opt-out suppresses the mention too.
        await SeedMembership("silent#1", NotificationLevel.None);
        RegisterSession("conn-silent", "silent#1");
        await SeedMembership("wolf#456", NotificationLevel.Mentions);
        RegisterSession("conn-wolf", "wolf#456");

        await _fanOut.NotifyAsync(Channel(), Message(), new[] { "silent#1", "wolf#456" }, Now);

        Assert.That(await InboxOf("silent#1"), Is.Empty, "a NotificationLevel.None member is silenced for mentions too");
        Assert.That(MentionEventCount("conn-silent"), Is.EqualTo(0));
        Assert.That(await InboxOf("wolf#456"), Has.Count.EqualTo(1),
            "a NotificationLevel.Mentions member control still gets the mention");
        Assert.That(MentionEventCount("conn-wolf"), Is.EqualTo(1));
    }

    [Test]
    public async Task Notify_DeclineSuppressedPendingDmRecipient_NoEntryNoEvent_ControlMemberStillNotified()
    {
        // C6 whole-branch review (Important): a pending-Dm recipient who DECLINED must not be pinged by
        // the initiator's mentions during the C5 24h soft-suppression window. A decline sets ONLY
        // DeclinedUntil (ChatHub.Dm.DeclineRequest) and never lowers the membership level — the recipient
        // membership was materialized at NotificationLevel.All — so without the decline gate the four
        // legacy rules would let each pending <@recipient> mention leak an entry + push straight through
        // the window (contradicting "a declined request never pings them", and inconsistent with
        // SessionStateAssembler.BuildPendingDmTray which hides the same DeclinedUntil-active Dm).
        // The control (a member with NO decline window) proves the gate is decline-SCOPED, not a blanket
        // break of the whole fan-out.
        await SeedMembership("recipient#1", NotificationLevel.All, declinedUntil: Now.AddHours(1));
        RegisterSession("conn-recipient", "recipient#1");
        await SeedMembership("wolf#456", NotificationLevel.All);
        RegisterSession("conn-wolf", "wolf#456");

        await _fanOut.NotifyAsync(Channel(ChannelType.Dm), Message(), new[] { "recipient#1", "wolf#456" }, Now);

        Assert.That(await InboxOf("recipient#1"), Is.Empty,
            "a decline-suppressed pending-Dm recipient gets NO entry during the 24h window");
        Assert.That(MentionEventCount("conn-recipient"), Is.EqualTo(0),
            "and NO event — a declined request never pings them (C5 guarantee)");
        Assert.That(await InboxOf("wolf#456"), Has.Count.EqualTo(1),
            "a member with no decline window (control) still gets an entry");
        Assert.That(MentionEventCount("conn-wolf"), Is.EqualTo(1), "and still gets the event");
    }

    [Test]
    public async Task Notify_ExpiredDeclineSuppression_NotifiesNormally()
    {
        // The suppression is TEMPORAL, not permanent — an ELAPSED DeclinedUntil (window already closed)
        // must NOT keep suppressing the mention (guards the fix against over-shooting into a permanent
        // mute of that Dm). Mirrors BuildPendingDmTray's `DeclinedUntil > now` boundary against the SAME
        // now the send read.
        await SeedMembership("recipient#1", NotificationLevel.All, declinedUntil: Now.AddHours(-1));
        RegisterSession("conn-recipient", "recipient#1");

        await _fanOut.NotifyAsync(Channel(ChannelType.Dm), Message(), new[] { "recipient#1" }, Now);

        Assert.That(await InboxOf("recipient#1"), Has.Count.EqualTo(1),
            "once the decline window has elapsed the mention notifies normally — the entry is created");
        Assert.That(MentionEventCount("conn-recipient"), Is.EqualTo(1),
            "and the live event is delivered");
    }

    [Test]
    public async Task Notify_SelfMention_NoEntryNoEvent_ControlMemberStillNotified()
    {
        // The sender mentions themselves (and is even a member + online) — no self-notification.
        await SeedMembership(Sender, NotificationLevel.All);
        RegisterSession("conn-self", Sender);
        await SeedMembership("wolf#456", NotificationLevel.All);
        RegisterSession("conn-wolf", "wolf#456");

        // Case-insensitive self-match: the mention tag casing differs from the sender's display casing.
        await _fanOut.NotifyAsync(Channel(), Message(), new[] { "PETER#123", "wolf#456" }, Now);

        Assert.That(await InboxOf(Sender), Is.Empty, "no self-mention entry (case-insensitive)");
        Assert.That(MentionEventCount("conn-self"), Is.EqualTo(0), "no self-mention event");
        Assert.That(await InboxOf("wolf#456"), Has.Count.EqualTo(1), "the other mentioned member still gets an entry");
        Assert.That(MentionEventCount("conn-wolf"), Is.EqualTo(1));
    }

    // ---------------------------------------------------------------------------------------------
    // Follow-up spec §4 — PUBLIC rooms are mentionable without joining (membership-independent fan-out)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task PublicChannel_NonMember_WithDirectoryRow_GetsEntryAndPush()
    {
        await SeedDirectory("wolf#456");
        RegisterSession("conn-wolf", "wolf#456");
        // Deliberately NO membership row: follow-up spec §4 — public rooms are mentionable without joining.

        await _fanOut.NotifyAsync(Channel(), Message(), new[] { "wolf#456" }, Now);

        Assert.That(await InboxOf("wolf#456"), Has.Count.EqualTo(1),
            "a directory-resolvable NON-member of a PUBLIC room gets a mention-inbox entry");
        Assert.That(MentionEventCount("conn-wolf"), Is.EqualTo(1), "and the targeted MentionNotified push");
    }

    [Test]
    public async Task PublicChannel_NonMember_WithoutDirectoryRow_GetsNothing()
    {
        await SeedMembership("control#1");
        // "ghost#999" has neither a membership nor a user_directory row — an unresolvable tag.
        await _fanOut.NotifyAsync(Channel(), Message(), new[] { "ghost#999", "control#1" }, Now);

        Assert.That(await InboxOf("ghost#999"), Is.Empty,
            "an unresolvable tag still notifies nobody (garbage `<@…>` markup stays inert)");
        Assert.That(await InboxOf("control#1"), Has.Count.EqualTo(1), "the eligible control member still fires");
    }

    [Test]
    public async Task SemiPublicChannel_NonMember_EvenWithDirectoryRow_GetsNothing()
    {
        await SeedDirectory("wolf#456");
        RegisterSession("conn-wolf", "wolf#456");
        await SeedMembership("control#1", channelId: ChannelId);

        await _fanOut.NotifyAsync(Channel(ChannelType.SemiPublic), Message(), new[] { "wolf#456", "control#1" }, Now);

        Assert.That(await InboxOf("wolf#456"), Is.Empty,
            "§4 widens PUBLIC rooms only — SemiPublic keeps the membership wall");
        Assert.That(MentionEventCount("conn-wolf"), Is.EqualTo(0),
            "the wall blocks the live push too, not just the inbox entry, even though wolf is online");
        Assert.That(await InboxOf("control#1"), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task PublicChannel_JoinedMember_WithNotificationLevelNone_StaysSilenced()
    {
        await SeedDirectory("silent#1");
        await SeedMembership("silent#1", NotificationLevel.None);
        await SeedMembership("control#1");

        await _fanOut.NotifyAsync(Channel(), Message(), new[] { "silent#1", "control#1" }, Now);

        Assert.That(await InboxOf("silent#1"), Is.Empty,
            "lock-in: join + NotificationLevel.None remains the opt-out even now that non-members are mentionable");
        Assert.That(await InboxOf("control#1"), Has.Count.EqualTo(1),
            "the eligible control member still gets the mention — proves this isn't vacuously true against a do-nothing fan-out");
    }

    // ---------------------------------------------------------------------------------------------
    // Shadow (defense-in-depth in-method guard; the call-site skip is covered end-to-end in
    // ChatHubSendMessageTests.ShadowSender_MentionsOthers_...)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Notify_ShadowMessage_NoOp_ForEveryone()
    {
        await SeedMembership("wolf#456", NotificationLevel.All);
        RegisterSession("conn-wolf", "wolf#456");

        await _fanOut.NotifyAsync(Channel(), Message(shadow: true), new[] { "wolf#456" }, Now);

        Assert.That(await TotalInboxCount(), Is.EqualTo(0),
            "a shadow message must produce ZERO inbox entries (defense-in-depth guard; primary guard is the call-site skip)");
        Assert.That(_harness.AllSignals, Is.Empty, "and ZERO events");
    }

    // ---------------------------------------------------------------------------------------------
    // TTL bound (C1 amendment 1 + acceptance 3's TTL leg)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Notify_ExpiryNeverExceedsMessageTtl_ChannelAndDm()
    {
        await SeedMembership("wolf#456", NotificationLevel.All);

        // Channel message: message TTL 30d, entry TTL 30d — equal, entry never exceeds.
        var channelMsg = Message(content: "in a channel", expiresAt: ExpiryCalculator.ForChannelMessage(ChannelType.Public, Now));
        await _fanOut.NotifyAsync(Channel(), channelMsg, new[] { "wolf#456" }, Now);
        var channelEntry = (await InboxOf("wolf#456")).Single();
        Assert.That(channelEntry.ExpiresAt.Value, Is.EqualTo(Now.AddDays(30)).Within(TimeSpan.FromSeconds(1)));
        Assert.That(channelEntry.ExpiresAt.Value, Is.LessThanOrEqualTo(channelMsg.ExpiresAt.Value),
            "channel message: entry 30d == message 30d — never exceeds");

        // DM message: message TTL 90d, but the mention entry is STILL capped at 30d.
        await _mentionInboxRepository.LoadForUser("wolf#456"); // (no-op read; keep intent explicit)
        await SeedMembership("wolf#456", NotificationLevel.All, channelId: "dm-1");
        var dmChannel = new ChatChannel { Id = "dm-1", Type = ChannelType.Dm };
        var dmMsg = new ChannelMessage
        {
            ChannelId = "dm-1",
            Seq = 3,
            Sender = new MessageSender { BattleTag = Sender, Name = SenderName },
            Content = "in a dm",
            SentAt = Now,
            ExpiresAt = ExpiryCalculator.ForChannelMessage(ChannelType.Dm, Now),
        };
        await _fanOut.NotifyAsync(dmChannel, dmMsg, new[] { "wolf#456" }, Now);

        var dmEntry = (await InboxOf("wolf#456")).Single(e => e.ChannelId == "dm-1");
        Assert.That(dmMsg.ExpiresAt.Value, Is.EqualTo(Now.AddDays(90)).Within(TimeSpan.FromSeconds(1)),
            "the DM MESSAGE lives 90d");
        Assert.That(dmEntry.ExpiresAt.Value, Is.EqualTo(Now.AddDays(30)).Within(TimeSpan.FromSeconds(1)),
            "but the mention ENTRY is still capped at 30d");
        Assert.That(dmEntry.ExpiresAt.Value, Is.LessThan(dmMsg.ExpiresAt.Value),
            "DM message: entry 30d < message 90d — the notification never outlives its message");
    }

    [Test]
    public async Task Notify_LongContent_ExcerptBoundedTo120Chars()
    {
        await SeedMembership("wolf#456", NotificationLevel.All);
        RegisterSession("conn-wolf", "wolf#456");
        var longContent = new string('x', 130);

        await _fanOut.NotifyAsync(Channel(), Message(content: longContent), new[] { "wolf#456" }, Now);

        var entry = (await InboxOf("wolf#456")).Single();
        Assert.That(entry.Excerpt.Length, Is.EqualTo(ChatLimits.DmPreviewExcerptLength), "excerpt is bounded to ~120 chars");
        Assert.That(entry.Excerpt, Is.EqualTo(longContent.Substring(0, ChatLimits.DmPreviewExcerptLength)));
    }

    // ---------------------------------------------------------------------------------------------
    // Per-target fault isolation (dead socket end-to-end lives in ChatHubSendMessageTests; the failed
    // INSERT leg is exercised here directly)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Notify_FailedInsertForOneTarget_OtherTargetsStillDelivered_NoThrow()
    {
        await SeedMembership("wolf#456", NotificationLevel.All);
        RegisterSession("conn-wolf", "wolf#456");
        await SeedMembership("frank#789", NotificationLevel.All);
        RegisterSession("conn-frank", "frank#789");

        // A repository whose Insert throws for wolf ONLY — simulating a single-target Mongo write failure.
        var throwingInbox = new ThrowingInsertRepository(MongoClient, "wolf#456");
        var faultyFanOut = new MentionFanOut(_harness.HubContext, _sessionRegistry, _membershipRepository, throwingInbox, _userDirectory);

        Assert.DoesNotThrowAsync(() => faultyFanOut.NotifyAsync(Channel(), Message(), new[] { "wolf#456", "frank#789" }, Now),
            "a single target's failed insert must be fault-isolated — NotifyAsync must not throw");

        // wolf's insert failed → no entry, and (insert-before-push) no event either.
        Assert.That(await InboxOf("wolf#456"), Is.Empty, "the failed-insert target has no entry");
        Assert.That(MentionEventCount("conn-wolf"), Is.EqualTo(0), "and no event (insert failed before the push)");
        // frank is unaffected — entry + event.
        Assert.That(await InboxOf("frank#789"), Has.Count.EqualTo(1), "the OTHER target still gets its entry");
        Assert.That(MentionEventCount("conn-frank"), Is.EqualTo(1), "the OTHER target still gets its event");
    }

    // ---------------------------------------------------------------------------------------------
    // Fan-out breadth
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Notify_FiveTargets_AllReceiveEntryAndEvent()
    {
        var targets = Enumerable.Range(1, ChatLimits.MaxMentionsPerMessage).Select(i => $"target{i}#{i}").ToList();
        foreach (var t in targets)
        {
            await SeedMembership(t, NotificationLevel.All);
            RegisterSession($"conn-{t}", t);
        }

        await _fanOut.NotifyAsync(Channel(), Message(), targets, Now);

        foreach (var t in targets)
        {
            Assert.That(await InboxOf(t), Has.Count.EqualTo(1), $"{t} must get an entry");
            Assert.That(MentionEventCount($"conn-{t}"), Is.EqualTo(1), $"{t} must get the event");
        }
        Assert.That(await TotalInboxCount(), Is.EqualTo(ChatLimits.MaxMentionsPerMessage),
            "exactly five entries — one per eligible target, no duplicates");
    }

    // A MentionInboxRepository whose Insert throws for one specific (lowercased) battleTag, to prove
    // MentionFanOut's per-target fault isolation on the insert leg.
    private sealed class ThrowingInsertRepository(MongoClient client, string throwForBattleTagLower)
        : MentionInboxRepository(client)
    {
        public override Task Insert(MentionInboxEntry entry) =>
            entry.BattleTag == throwForBattleTagLower
                ? Task.FromException(new InvalidOperationException("simulated inbox insert failure"))
                : base.Insert(entry);
    }
}
