using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C3 Task 7 — <see cref="SessionStateAssembler"/>: builds the <see cref="SessionStateDto"/>
/// snapshot pushed on every (re)connect (spec acceptance 8) and seeds the
/// <see cref="OnlineMemberRegistry"/> + legacy <see cref="ConnectionMapping"/> mute cache.
/// BOUNDARY-PRIVACY CRITICAL: the identity's raw permission snapshot and the mute shadow flag must
/// never reach the DTO — several tests below assert that directly.
/// </summary>
public class SessionStateAssemblerTests : IntegrationTestBase
{
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private MuteRepository _muteRepository;
    private Mock<IChatAuthenticationService> _chatAuthService;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private ConnectionMapping _connectionMapping;
    private SessionStateAssembler _assembler;

    [SetUp]
    public void SetupBeforeEach()
    {
        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _connectionMapping = new ConnectionMapping();

        // Echoes the identity back into a ChatUser carrying stable, assertable flair — the mute/flair
        // services aren't the thing under test here (per the brief), so this is a real MuteRepository
        // (Testcontainers Mongo) but a mocked IChatAuthenticationService.
        _chatAuthService = new Mock<IChatAuthenticationService>();
        _chatAuthService
            .Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .Returns((W3CUserAuthentication id) => Task.FromResult(new ChatUser(
                id.BattleTag,
                id.IsAdmin,
                "ClanX",
                new ProfilePicture { Race = AvatarCategory.HU, PictureId = 7, IsClassic = true },
                new ChatColor("chat_color_red"),
                new[] { new ChatIcon("chat_icon_star") })));

        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            _muteRepository,
            _chatAuthService.Object,
            _onlineMemberRegistry,
            _connectionMapping);
    }

    private static W3CUserAuthentication Identity(string battleTag, string name = null, bool isAdmin = false, params EPermission[] perms) =>
        new()
        {
            BattleTag = battleTag,
            Name = name ?? battleTag,
            IsAdmin = isAdmin,
            Permissions = perms.ToHashSet(),
        };

    private async Task<ChatChannel> InsertChannel(ChannelType type, string name, long lastSeq)
    {
        var channel = new ChatChannel { Type = type, Name = name, NormalizedName = ChannelNames.Normalize(name), LastSeq = lastSeq };
        await _channelRepository.Insert(channel);
        return channel;
    }

    private Task InsertMembership(string channelId, string battleTag, long lastReadSeq, NotificationLevel level = NotificationLevel.All, MembershipRole role = MembershipRole.Member) =>
        _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            LastReadSeq = lastReadSeq,
            NotificationLevel = level,
            Role = role,
            JoinedAt = DateTime.UtcNow,
        });

    // Seeds a durable message row with an explicit Seq and visibility class, so a test can assert the
    // D7 count-based unread against a KNOWN mix of visible / foreign-shadow / soft-deleted rows. Seq is
    // set explicitly (not via AllocateSeq) so the row's position relative to a member's read cursor is
    // deterministic and independent of channel.LastSeq — the whole point of D7 is that unread no longer
    // derives from LastSeq.
    private Task InsertMessage(string channelId, string senderBattleTag, long seq, bool shadow = false, bool deleted = false) =>
        _messageRepository.Insert(new ChannelMessage
        {
            ChannelId = channelId,
            Seq = seq,
            Sender = new MessageSender { BattleTag = senderBattleTag, Name = senderBattleTag.Split('#')[0] },
            Content = $"msg-{seq}",
            SentAt = DateTime.UtcNow,
            Shadow = shadow,
            Deleted = deleted ? new MessageDeletion { By = "admin#1", At = DateTime.UtcNow } : null,
        });

    // Inserts a 1:1 Dm shell directly (no NormalizedName — Dm channels carry none) with the pair-key,
    // request state, and initiator the tray keys on. RequestedAt drives the tray entry's timestamp.
    private async Task<ChatChannel> InsertDmChannel(string initiator, string recipient, DmRequestState state, DateTime requestedAt)
    {
        var channel = new ChatChannel
        {
            Type = ChannelType.Dm,
            PairKey = DmPairKey.For(initiator, recipient),
            RequestState = state,
            RequestInitiatedBy = initiator,
            LastSeq = 1,
            LastMessageAt = requestedAt,
        };
        await _channelRepository.Insert(channel);
        return channel;
    }

    private Task FullBan(string battleTag, DateTime endDate) =>
        _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = battleTag,
            endDate = endDate.ToString("O"),
            author = "admin#1",
            reason = "bad behavior",
            isShadowBan = false,
        });

    private Task ShadowBan(string battleTag, DateTime endDate) =>
        _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = battleTag,
            endDate = endDate.ToString("O"),
            author = "admin#1",
            reason = "spam",
            isShadowBan = true,
        });

    [Test]
    public async Task Assemble_ReturnsMemberships_WithChannelMetadata_AndUnreadMath()
    {
        var chanA = await InsertChannel(ChannelType.Public, "General", lastSeq: 10);
        var chanB = await InsertChannel(ChannelType.SemiPublic, "MyClan", lastSeq: 3);
        var identity = Identity("Peter#123");
        await InsertMembership(chanA.Id, identity.BattleTag, lastReadSeq: 4, level: NotificationLevel.Mentions, role: MembershipRole.Owner);
        await InsertMembership(chanB.Id, identity.BattleTag, lastReadSeq: 5, level: NotificationLevel.All, role: MembershipRole.Member); // LastReadSeq > LastSeq: clamp

        // D7 (Amendment 3): unread is now the COUNT of user-visible rows after the read cursor, so the
        // channels must carry real message rows for it to be non-zero (the old LastSeq−LastReadSeq
        // formula ignored the rows entirely). Every row here is plain/visible, so the count still equals
        // the old seq delta — pinning that D7 is a REFINEMENT of the formula, not a rewrite.
        for (var seq = 1L; seq <= 10; seq++)
        {
            await InsertMessage(chanA.Id, identity.BattleTag, seq);
        }
        for (var seq = 1L; seq <= 3; seq++)
        {
            await InsertMessage(chanB.Id, identity.BattleTag, seq);
        }

        var (dto, _) = await _assembler.AssembleAndSeed(identity, "conn-1", DateTime.UtcNow);

        Assert.AreEqual(2, dto.Channels.Count);
        var a = dto.Channels.Single(c => c.Channel.Id == chanA.Id);
        Assert.AreEqual("General", a.Channel.Name);
        Assert.AreEqual(ChannelType.Public, a.Channel.Type);
        Assert.AreEqual(NotificationLevel.Mentions, a.Membership.NotificationLevel);
        Assert.AreEqual(4L, a.Membership.LastReadSeq);
        Assert.AreEqual(MembershipRole.Owner, a.Membership.Role);
        Assert.AreEqual(6L, a.UnreadCount, "D7: unread = count of visible rows after LastReadSeq(4) = 6 (equals the old LastSeq(10)−LastReadSeq(4) on a clean channel)");
        Assert.IsTrue(a.HasUnread);

        var b = dto.Channels.Single(c => c.Channel.Id == chanB.Id);
        Assert.AreEqual(0L, b.UnreadCount, "no visible rows after LastReadSeq(5) (max seq is 3) — unread is naturally 0, never negative");
        Assert.IsFalse(b.HasUnread);
        Assert.AreEqual(MembershipRole.Member, b.Membership.Role);
    }

    // ---------------------------------------------------------------------------------------------
    // D7 (Amendment 3) — connect-time unread is the COUNT of USER-VISIBLE rows after the read cursor,
    // NOT channel.LastSeq − membership.LastReadSeq. This deliberately REVISES C3's provisional formula
    // so a shadow-banned author's message (or a soft-deleted message) generates NO phantom unread for
    // OTHER members on reconnect (pinned acceptance 2 — the reconnect leg of "shadow messages generate
    // NO unread for others").
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Assemble_Unread_ExcludesForeignShadowRows()
    {
        // Member B reconnects after A's shadow send. The shadow row advanced channel.LastSeq (so the old
        // LastSeq−LastReadSeq formula would report 1 phantom unread), but it is invisible to B — the
        // count-based unread must be 0.
        var chan = await InsertChannel(ChannelType.Public, "General", lastSeq: 1);
        var viewer = Identity("Bravo#2");
        await InsertMembership(chan.Id, viewer.BattleTag, lastReadSeq: 0);
        await InsertMessage(chan.Id, "Alpha#1", seq: 1, shadow: true);

        var (dto, _) = await _assembler.AssembleAndSeed(viewer, "conn-b", DateTime.UtcNow);

        var c = dto.Channels.Single(x => x.Channel.Id == chan.Id);
        Assert.AreEqual(0L, c.UnreadCount, "a foreign author's shadow row must generate NO unread for other members");
        Assert.IsFalse(c.HasUnread);
    }

    [Test]
    public async Task Assemble_Unread_ExcludesSoftDeletedRows()
    {
        // A moderator soft-deleted the only message after B's cursor. It still advanced LastSeq (old
        // formula → 1 phantom), but a soft-deleted row is invisible to everyone — unread must be 0.
        var chan = await InsertChannel(ChannelType.Public, "General", lastSeq: 1);
        var viewer = Identity("Bravo#2");
        await InsertMembership(chan.Id, viewer.BattleTag, lastReadSeq: 0);
        await InsertMessage(chan.Id, "Alpha#1", seq: 1, deleted: true);

        var (dto, _) = await _assembler.AssembleAndSeed(viewer, "conn-b", DateTime.UtcNow);

        var c = dto.Channels.Single(x => x.Channel.Id == chan.Id);
        Assert.AreEqual(0L, c.UnreadCount, "a soft-deleted row must generate NO phantom unread after a purge");
        Assert.IsFalse(c.HasUnread);
    }

    [Test]
    public async Task Assemble_Unread_IncludesViewersOwnShadowRows()
    {
        // Symmetric illusion: a shadow-banned author must still see their OWN message as delivered, so
        // their own shadow row DOES count toward THEIR own unread (CountUserVisibleAfter includes it via
        // the sender == viewer disjunct).
        var author = Identity("Alpha#1");
        var chan = await InsertChannel(ChannelType.Public, "General", lastSeq: 1);
        await InsertMembership(chan.Id, author.BattleTag, lastReadSeq: 0);
        await InsertMessage(chan.Id, author.BattleTag, seq: 1, shadow: true);

        var (dto, _) = await _assembler.AssembleAndSeed(author, "conn-a", DateTime.UtcNow);

        var c = dto.Channels.Single(x => x.Channel.Id == chan.Id);
        Assert.AreEqual(1L, c.UnreadCount, "the viewer's OWN shadow rows count toward their own unread — the symmetric illusion");
        Assert.IsTrue(c.HasUnread);
    }

    [Test]
    public async Task Assemble_Unread_PlainMessages_MatchesLastSeqMath()
    {
        // Equivalence pin: on a CLEAN channel (no shadow/deleted rows) the count-based unread equals the
        // old channel.LastSeq − membership.LastReadSeq — proving D7 is a refinement, not a rewrite.
        const long lastSeq = 5;
        const long lastReadSeq = 2;
        var chan = await InsertChannel(ChannelType.Public, "General", lastSeq: lastSeq);
        var viewer = Identity("Peter#123");
        await InsertMembership(chan.Id, viewer.BattleTag, lastReadSeq: lastReadSeq);
        for (var seq = 1L; seq <= lastSeq; seq++)
        {
            await InsertMessage(chan.Id, viewer.BattleTag, seq);
        }

        var (dto, _) = await _assembler.AssembleAndSeed(viewer, "conn-1", DateTime.UtcNow);

        var c = dto.Channels.Single(x => x.Channel.Id == chan.Id);
        Assert.AreEqual(lastSeq - lastReadSeq, c.UnreadCount, "on a clean channel the count-based unread equals LastSeq − LastReadSeq");
        Assert.AreEqual(3L, c.UnreadCount);
        Assert.IsTrue(c.HasUnread);
    }

    [Test]
    public async Task Assemble_Unread_ClampedNonNegative()
    {
        // A read cursor ahead of every existing row (e.g. after MarkRead clamped to a LastSeq whose top
        // rows are invisible) yields a count of 0 — never a negative unread.
        var chan = await InsertChannel(ChannelType.Public, "General", lastSeq: 3);
        var viewer = Identity("Peter#123");
        await InsertMembership(chan.Id, viewer.BattleTag, lastReadSeq: 5);
        for (var seq = 1L; seq <= 3; seq++)
        {
            await InsertMessage(chan.Id, viewer.BattleTag, seq);
        }

        var (dto, _) = await _assembler.AssembleAndSeed(viewer, "conn-1", DateTime.UtcNow);

        var c = dto.Channels.Single(x => x.Channel.Id == chan.Id);
        Assert.AreEqual(0L, c.UnreadCount, "no rows after the cursor → unread 0, never negative");
        Assert.IsFalse(c.HasUnread);
    }

    [Test]
    public async Task Assemble_AlwaysIncludesPublicCatalog()
    {
        // decision 1: the catalog is present for client fallback EVEN for a channel the caller has
        // never joined (no membership row at all).
        var pub = await InsertChannel(ChannelType.Public, "W3C Lounge", lastSeq: 0);
        var identity = Identity("Peter#123");

        var (dto, _) = await _assembler.AssembleAndSeed(identity, "conn-1", DateTime.UtcNow);

        Assert.IsNotEmpty(dto.PublicCatalog);
        Assert.IsTrue(dto.PublicCatalog.Any(c => c.Id == pub.Id));
    }

    [Test]
    public async Task Assemble_Stubs_PendingDmRequestsEmpty_MentionUnreadZero()
    {
        var identity = Identity("Peter#123");

        var (dto, _) = await _assembler.AssembleAndSeed(identity, "conn-1", DateTime.UtcNow);

        Assert.IsEmpty(dto.PendingDmRequests);
        Assert.AreEqual(0, dto.MentionUnreadCount);
    }

    // ---------------------------------------------------------------------------------------------
    // C5 T6 — the PendingDmRequests tray. Built from the ALREADY-LOADED memberships + channels (zero
    // extra Mongo reads): a Dm && Pending && RequestInitiatedBy != viewer && not decline-suppressed
    // ⇒ one PendingDmRequestDto. Pending-recipient channels stay in Channels too (D4 dual-listing).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task SessionState_Tray_PendingListedForRecipient_NotForInitiator()
    {
        const string initiator = "Peter#123";
        const string recipient = "Wolf#456";
        var requestedAt = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
        var channel = await InsertDmChannel(initiator, recipient, DmRequestState.Pending, requestedAt);
        await InsertMembership(channel.Id, initiator, lastReadSeq: 0);
        await InsertMembership(channel.Id, recipient, lastReadSeq: 0);

        // The RECIPIENT sees the request in their tray (it names the initiator, RequestedAt = LastMessageAt).
        var (recipientDto, _) = await _assembler.AssembleAndSeed(Identity(recipient), "conn-recip", requestedAt);
        Assert.AreEqual(1, recipientDto.PendingDmRequests.Count);
        Assert.AreEqual(channel.Id, recipientDto.PendingDmRequests[0].ChannelId);
        Assert.AreEqual(initiator, recipientDto.PendingDmRequests[0].FromBattleTag);
        Assert.AreEqual(requestedAt, recipientDto.PendingDmRequests[0].RequestedAt);

        // The INITIATOR never sees their OWN outgoing request in the tray (it is not a request TO them).
        var (initiatorDto, _) = await _assembler.AssembleAndSeed(Identity(initiator), "conn-init", requestedAt);
        Assert.IsEmpty(initiatorDto.PendingDmRequests, "the initiator never sees their own outgoing request in the tray");
    }

    [Test]
    public async Task PendingChannel_AppearsInChannelsList_ForBothParties()
    {
        const string initiator = "Peter#123";
        const string recipient = "Wolf#456";
        var requestedAt = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
        var channel = await InsertDmChannel(initiator, recipient, DmRequestState.Pending, requestedAt);
        await InsertMembership(channel.Id, initiator, lastReadSeq: 0);
        await InsertMembership(channel.Id, recipient, lastReadSeq: 0);

        var (initiatorDto, _) = await _assembler.AssembleAndSeed(Identity(initiator), "conn-init", requestedAt);
        var (recipientDto, _) = await _assembler.AssembleAndSeed(Identity(recipient), "conn-recip", requestedAt);

        // D4 dual-listing: the pending DM is a normal channel for BOTH parties (needed so FocusChannel /
        // GetMessages / registry membership work), in addition to riding the recipient's tray.
        Assert.IsTrue(initiatorDto.Channels.Any(c => c.Channel.Id == channel.Id), "the pending DM appears in the initiator's Channels");
        Assert.IsTrue(recipientDto.Channels.Any(c => c.Channel.Id == channel.Id), "the pending DM appears in the recipient's Channels");
        // Both parties may see the consent metadata (D3 — RequestInitiatedBy is safe to expose).
        var recipientChannel = recipientDto.Channels.Single(c => c.Channel.Id == channel.Id);
        Assert.AreEqual(DmRequestState.Pending, recipientChannel.Channel.RequestState);
        Assert.AreEqual(initiator, recipientChannel.Channel.RequestInitiatedBy);
    }

    [Test]
    public async Task SessionState_Tray_ExcludesDeclineSuppressed_ReappearsAfterWindow()
    {
        const string initiator = "Peter#123";
        const string recipient = "Wolf#456";
        var now = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
        var channel = await InsertDmChannel(initiator, recipient, DmRequestState.Pending, now);
        await InsertMembership(channel.Id, initiator, lastReadSeq: 0);
        // The recipient declined — DeclinedUntil is 24h out. (DeclinedUntil never leaves this row — D3.)
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = recipient,
            LastReadSeq = 0,
            NotificationLevel = NotificationLevel.All,
            Role = MembershipRole.Member,
            JoinedAt = now,
            DeclinedUntil = now.AddHours(24),
        });

        // Inside the window: suppressed from the tray (but still in Channels).
        var (inside, _) = await _assembler.AssembleAndSeed(Identity(recipient), "conn-1", now.AddHours(1));
        Assert.IsEmpty(inside.PendingDmRequests, "a decline-suppressed request is hidden from the tray inside the window");
        Assert.IsTrue(inside.Channels.Any(c => c.Channel.Id == channel.Id), "the decline-suppressed DM stays a normal channel");

        // After the window (DeclinedUntil <= now): reappears in the tray.
        var (after, _) = await _assembler.AssembleAndSeed(Identity(recipient), "conn-2", now.AddHours(25));
        Assert.AreEqual(1, after.PendingDmRequests.Count, "the request reappears in the tray once the decline window elapses");
        Assert.AreEqual(channel.Id, after.PendingDmRequests[0].ChannelId);
    }

    [Test]
    public async Task Assemble_OwnProfile_IsProjection_NeverIdentitySnapshot()
    {
        var identity = Identity("Peter#123", name: "PeterDisplay", isAdmin: true, EPermission.Moderation, EPermission.Queue);

        var (dto, _) = await _assembler.AssembleAndSeed(identity, "conn-1", DateTime.UtcNow);

        // Reflection guard: OwnProfileDto must never expose the raw permission set or any
        // Identity/Context-shaped member — only a projected string list.
        var properties = typeof(OwnProfileDto).GetProperties();
        Assert.IsFalse(properties.Any(p => p.PropertyType == typeof(IReadOnlySet<EPermission>)),
            "OwnProfileDto must never expose the raw IReadOnlySet<EPermission> snapshot");
        Assert.IsFalse(properties.Any(p => p.PropertyType == typeof(W3CUserAuthentication)),
            "OwnProfileDto must never embed the identity object wholesale");
        Assert.IsFalse(properties.Any(p => p.Name.Contains("Context", StringComparison.OrdinalIgnoreCase)),
            "OwnProfileDto must never expose a Context/HubCallerContext member");
        Assert.IsFalse(properties.Any(p => p.Name.Contains("Identity", StringComparison.OrdinalIgnoreCase)),
            "OwnProfileDto must never expose an Identity member");
        var permissionsProp = properties.Single(p => p.Name == "Permissions");
        Assert.IsTrue(typeof(IEnumerable<string>).IsAssignableFrom(permissionsProp.PropertyType),
            "Permissions must be projected to a string list");

        // Functional: only the chat-relevant permission ("Moderation") survives; "Queue" is dropped.
        Assert.AreEqual(new[] { "Moderation" }, dto.OwnProfile.Permissions);
        Assert.AreEqual("Peter#123", dto.OwnProfile.BattleTag);
        Assert.AreEqual("PeterDisplay", dto.OwnProfile.Name);
        Assert.IsTrue(dto.OwnProfile.IsAdmin);
        Assert.IsNotNull(dto.OwnProfile.Flair);
        Assert.AreEqual("ClanX", dto.OwnProfile.Flair.ClanId);
    }

    [Test]
    public async Task Assemble_MuteState_FullBan_EndDateOnly_ShadowInvisible()
    {
        var fullEnd = DateTime.UtcNow.AddDays(1);
        var fullIdentity = Identity("fullbanned#1");
        await FullBan(fullIdentity.BattleTag, fullEnd);

        var (fullDto, fullStatus) = await _assembler.AssembleAndSeed(fullIdentity, "conn-full", DateTime.UtcNow);

        Assert.AreEqual(MuteStatus.Full, fullStatus);
        Assert.IsNotNull(fullDto.MuteState, "A full ban must surface a non-null muteState");
        var persistedFull = await _muteRepository.GetMutedPlayer(fullIdentity.BattleTag);
        Assert.AreEqual(persistedFull.endDate, fullDto.MuteState.EndDate);
        // SECURITY: only endDate — no reason/isShadowBan leak.
        var props = typeof(MuteStateDto).GetProperties();
        Assert.AreEqual(1, props.Length);
        Assert.AreEqual("EndDate", props[0].Name);

        var shadowEnd = DateTime.UtcNow.AddDays(1);
        var shadowIdentity = Identity("shadow#1");
        await ShadowBan(shadowIdentity.BattleTag, shadowEnd);

        var (shadowDto, shadowStatus) = await _assembler.AssembleAndSeed(shadowIdentity, "conn-shadow", DateTime.UtcNow);

        Assert.AreEqual(MuteStatus.Shadow, shadowStatus);
        Assert.IsNull(shadowDto.MuteState, "A shadow ban must NEVER surface to the client — muteState must be null");

        // Invisible in the DTO must NOT mean invisible server-side: the legacy mute cache
        // (ConnectionMapping, consulted by the real message-send enforcement path) must still carry
        // the REAL Shadow status/endDate for this connection.
        var persistedShadow = await _muteRepository.GetMutedPlayer(shadowIdentity.BattleTag);
        Assert.IsTrue(_connectionMapping.TryGetMute("conn-shadow", out var cachedShadow),
            "AssembleAndSeed must seed the legacy mute cache even for a shadow ban");
        Assert.AreEqual(MuteStatus.Shadow, cachedShadow.Status,
            "The legacy mute cache must carry the real Shadow status even though the DTO hides it");
        Assert.AreEqual(persistedShadow.endDate, cachedShadow.EndDate);
    }

    [Test]
    public async Task Assemble_MuteState_NoActiveMute_ReturnsNull()
    {
        var identity = Identity("clean#1");

        var (dto, status) = await _assembler.AssembleAndSeed(identity, "conn-1", DateTime.UtcNow);

        Assert.AreEqual(MuteStatus.None, status);
        Assert.IsNull(dto.MuteState);
    }

    [Test]
    public async Task Assemble_MuteState_Expired_TreatedAsNoMute()
    {
        var identity = Identity("expired#1");
        await FullBan(identity.BattleTag, DateTime.UtcNow.AddMinutes(-10));

        var (dto, status) = await _assembler.AssembleAndSeed(identity, "conn-1", DateTime.UtcNow);

        Assert.AreEqual(MuteStatus.None, status, "An expired mute must be treated as no mute");
        Assert.IsNull(dto.MuteState);
    }

    [Test]
    public async Task Assemble_FullBan_FiltersPublicCatalog()
    {
        await InsertChannel(ChannelType.Public, "W3C Lounge", lastSeq: 0);
        var identity = Identity("fullbanned#2");
        await FullBan(identity.BattleTag, DateTime.UtcNow.AddDays(1));

        var (dto, status) = await _assembler.AssembleAndSeed(identity, "conn-1", DateTime.UtcNow);

        Assert.AreEqual(MuteStatus.Full, status);
        Assert.IsEmpty(dto.PublicCatalog, "A full-ban connect must filter the public catalog to empty");
    }

    [Test]
    public async Task Assemble_SeedsOnlineMemberRegistry()
    {
        var chan = await InsertChannel(ChannelType.Public, "General", lastSeq: 20);
        var identity = Identity("Peter#123");
        await InsertMembership(chan.Id, identity.BattleTag, lastReadSeq: 7, level: NotificationLevel.Mentions);

        await _assembler.AssembleAndSeed(identity, "conn-x", DateTime.UtcNow);

        var members = _onlineMemberRegistry.GetMembers(chan.Id);
        var member = members.Single(m => m.BattleTag == identity.BattleTag);
        Assert.AreEqual(NotificationLevel.Mentions, member.NotificationLevel);
        Assert.AreEqual(7L, member.LastReadSeq);
    }

    [Test]
    public async Task Assemble_SeedsLegacyMuteCache_RegisterUserAndSetMute()
    {
        // Unmuted connect: RegisterUser must make GetUser resolve, SetMute must seed None.
        var identity = Identity("Peter#123");

        await _assembler.AssembleAndSeed(identity, "conn-y", DateTime.UtcNow);

        var registeredUser = _connectionMapping.GetUser("conn-y");
        Assert.IsNotNull(registeredUser, "AssembleAndSeed must RegisterUser on the legacy ConnectionMapping");
        Assert.AreEqual(identity.BattleTag, registeredUser.BattleTag);
        Assert.IsTrue(_connectionMapping.TryGetMute("conn-y", out var cachedNone));
        Assert.AreEqual(MuteStatus.None, cachedNone.Status);

        // Full-ban connect: SetMute must seed the REAL status/endDate (not the client-hidden shadow
        // rule — the server-side enforcement cache always carries the true status).
        var bannedEnd = DateTime.UtcNow.AddDays(2);
        var bannedIdentity = Identity("banned#9");
        await FullBan(bannedIdentity.BattleTag, bannedEnd);

        await _assembler.AssembleAndSeed(bannedIdentity, "conn-z", DateTime.UtcNow);

        Assert.IsTrue(_connectionMapping.TryGetMute("conn-z", out var cachedFull));
        Assert.AreEqual(MuteStatus.Full, cachedFull.Status);
        var persisted = await _muteRepository.GetMutedPlayer(bannedIdentity.BattleTag);
        Assert.AreEqual(persisted.endDate, cachedFull.EndDate);

        // Proves the acceptance criterion directly: MuteReconciliationService.GetConnectionIdsForUser
        // (which scans ConnectionMapping's _users map) can now reach this connection.
        var reachable = _connectionMapping.GetConnectionIdsForUser(bannedIdentity.BattleTag);
        Assert.Contains("conn-z", reachable);
    }
}
