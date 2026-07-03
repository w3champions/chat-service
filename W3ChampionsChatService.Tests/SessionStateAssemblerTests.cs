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

        var (dto, _) = await _assembler.AssembleAndSeed(identity, "conn-1", DateTime.UtcNow);

        Assert.AreEqual(2, dto.Channels.Count);
        var a = dto.Channels.Single(c => c.Channel.Id == chanA.Id);
        Assert.AreEqual("General", a.Channel.Name);
        Assert.AreEqual(ChannelType.Public, a.Channel.Type);
        Assert.AreEqual(NotificationLevel.Mentions, a.Membership.NotificationLevel);
        Assert.AreEqual(4L, a.Membership.LastReadSeq);
        Assert.AreEqual(MembershipRole.Owner, a.Membership.Role);
        Assert.AreEqual(6L, a.UnreadCount, "unreadCount = channel.LastSeq(10) - membership.LastReadSeq(4)");
        Assert.IsTrue(a.HasUnread);

        var b = dto.Channels.Single(c => c.Channel.Id == chanB.Id);
        Assert.AreEqual(0L, b.UnreadCount, "unreadCount must clamp to 0, never go negative");
        Assert.IsFalse(b.HasUnread);
        Assert.AreEqual(MembershipRole.Member, b.Membership.Role);
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
