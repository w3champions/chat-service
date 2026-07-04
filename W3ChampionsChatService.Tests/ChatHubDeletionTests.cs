using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

public class ChatHubDeletionTests : IntegrationTestBase
{
    private const string ModeratorConnectionId = "AdminConnectionId";
    private const string ModeratorBattleTag = "admin#123";
    private const string AuthorBattleTag = "sender#123";

    private ChatHub _chatHub;
    private MuteRepository _muteRepository;
    private Mock<IHubCallerClients> _clients;
    private Mock<HubCallerContext> _hubCallerContext;
    private ConnectionMapping _connectionMapping;
    private Mock<IClientProxy> _mockAllProxy;
    private Mock<IClientProxy> _mockAllExceptProxy;

    // C4 (Task 3) durable-delete collaborators — shared so the DeleteMessage tests can assert on the
    // real soft-delete (Mongo), the real MessageDeleted fan-out (the capture harness), and the
    // mention-inbox cleanup hook (the capturing spy).
    private SessionRegistry _sessionRegistry;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private ChannelRepository _channelRepository;
    private MessageRepository _messageRepository;
    private HubPushCaptureHarness _pushHarness;
    private CapturingMentionInboxCleaner _mentionCleaner;

    [SetUp]
    public void SetupBeforeEach()
    {
        _muteRepository = new MuteRepository(MongoClient);
        _clients = new Mock<IHubCallerClients>();
        _hubCallerContext = new Mock<HubCallerContext>();
        _mockAllProxy = new Mock<IClientProxy>();
        _mockAllExceptProxy = new Mock<IClientProxy>();

        var chatAuthenticationService = new Mock<IChatAuthenticationService>();
        chatAuthenticationService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync(new ChatUser(ModeratorBattleTag, true, "Admin", new ProfilePicture(), null, null));

        _connectionMapping = new ConnectionMapping();
        _channelRepository = new ChannelRepository(MongoClient);
        _messageRepository = new MessageRepository(MongoClient);
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _focusRegistry = new FocusRegistry();
        _sessionRegistry = new SessionRegistry();
        _pushHarness = new HubPushCaptureHarness();
        _mentionCleaner = new CapturingMentionInboxCleaner();

        // A REAL FanOutEngine wired to the capture harness and the SAME FocusRegistry the hub uses, so
        // the DeleteMessage tests observe the actual MessageDeleted delivery (D4) rather than a stub.
        var fanOutEngine = new FanOutEngine(
            _pushHarness.HubContext,
            _focusRegistry,
            _onlineMemberRegistry,
            new ActivityCoalescer(_pushHarness.HubContext, _onlineMemberRegistry),
            _sessionRegistry);

        var assembler = new SessionStateAssembler(
            new MembershipRepository(MongoClient, _channelRepository),
            _channelRepository,
            _messageRepository,
            _muteRepository,
            chatAuthenticationService.Object,
            _onlineMemberRegistry,
            _connectionMapping);

        _chatHub = new ChatHub(
            _connectionMapping,
            new MuteReconciliationTestHarness(_connectionMapping, _muteRepository).Service,
            new TicketStore(),
            _sessionRegistry,
            new UserDirectoryRepository(MongoClient),
            assembler,
            _focusRegistry,
            _onlineMemberRegistry,
            new MessageRateLimiter(),
            TimeProvider.System,
            _channelRepository,
            new MembershipRepository(MongoClient, _channelRepository),
            new ChannelCreationRateLimiter(),
            _messageRepository,
            fanOutEngine,
            ViewersAccumulatorTestFactory.CreateIgnored(),
            _mentionCleaner,
            RelationshipProviderTestFactory.CreateIgnored(),
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker());

        _clients.Setup(c => c.All).Returns(_mockAllProxy.Object);
        _clients.Setup(c => c.AllExcept(It.IsAny<System.Collections.Generic.IReadOnlyList<string>>())).Returns(_mockAllExceptProxy.Object);
        _chatHub.Clients = _clients.Object;

        _hubCallerContext.Setup(c => c.ConnectionId).Returns(ModeratorConnectionId);
        _chatHub.Context = _hubCallerContext.Object;
        _chatHub.Groups = new Mock<IGroupManager>().Object;

        // Add admin user to connections (legacy purge/ban paths still read GetUser from here).
        var adminUser = new ChatUser(ModeratorBattleTag, true, "Admin", new ProfilePicture(), null, null);
        _connectionMapping.Add(ModeratorConnectionId, "W3C Lounge", adminUser);

        // C4 (D5): the durable DeleteMessage resolves the moderator battleTag fail-closed via
        // ISessionRegistry — NOT the legacy ConnectionMapping.GetUser — so the moderator needs a live
        // session registered under the same connection id.
        _sessionRegistry.Register(
            ModeratorConnectionId,
            new W3CUserAuthentication { BattleTag = ModeratorBattleTag, Name = "Admin" },
            null);
    }

    // -------------------------------------------------------------------------------------------------
    // C4 (Task 3) — durable DeleteMessage. UPGRADE lineage: the legacy in-memory
    // DeleteMessage_ValidMessage_DeletesAndNotifiesCorrectClients / _NonExistentMessage_DoesNotNotifyClients
    // (which drove ChatHistory.DeleteMessage + Clients.AllExcept with a bare message-id string) are
    // superseded here by the durable soft-delete pipeline: MessageRepository.MarkDeleted, the
    // channel-scoped MessageDeletedDto delivered to FOCUSED viewers minus the author's connections (D4),
    // the mention-inbox cleanup hook (D10), and the DM/GroupDm privacy wall (D5).
    // -------------------------------------------------------------------------------------------------

    private async Task<ChatChannel> CreateChannel(ChannelType type = ChannelType.Public)
    {
        var channel = new ChatChannel { Type = type };
        await _channelRepository.Insert(channel);
        return channel;
    }

    // Seeds a durable message via the SAME seq-allocation path the real send pipeline uses, so the
    // channel's LastSeq stays consistent with directly-seeded history.
    private async Task<ChannelMessage> SeedMessage(string channelId, string senderBattleTag, string content, DateTime? expiresAt = null, bool shadow = false)
    {
        var seq = await _channelRepository.AllocateSeq(channelId, DateTime.UtcNow);
        var message = new ChannelMessage
        {
            ChannelId = channelId,
            Seq = seq,
            Sender = new MessageSender { BattleTag = senderBattleTag, Name = senderBattleTag.Split('#')[0] },
            Content = content,
            SentAt = DateTime.UtcNow,
            Shadow = shadow,
            ExpiresAt = expiresAt,
        };
        await _messageRepository.Insert(message);
        return message;
    }

    [Test]
    public async Task DeleteMessage_SoftDeletes_SetsDeletedByAt_DocSurvives()
    {
        var channel = await CreateChannel();
        // A millisecond-precise expiry so the round-trip through Mongo (ms precision) compares exactly.
        var expiry = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "Message to delete", expiresAt: expiry);

        var result = await _chatHub.DeleteMessage(message.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        // NEVER hard-deleted: the doc still exists, now soft-deleted, with ExpiresAt/TTL untouched.
        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNotNull(reloaded, "the message doc must survive — soft-delete only, never a hard delete");
        Assert.IsNotNull(reloaded.Deleted, "Deleted{by,at} must be set");
        Assert.AreEqual(ModeratorBattleTag, reloaded.Deleted.By, "DeletedBy must be the moderator's battleTag");
        Assert.Less(Math.Abs((reloaded.Deleted.At - DateTime.UtcNow).TotalSeconds), 60, "DeletedAt must be ~now");
        Assert.AreEqual(expiry, reloaded.ExpiresAt, "ExpiresAt/TTL must be left untouched by the soft delete");
    }

    [Test]
    public async Task DeleteMessage_EmitsChannelScopedDto_ToFocusedViewers_ExceptAuthorConnections()
    {
        var channel = await CreateChannel();
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "spam");

        // The moderated author is online and focused on the channel — their own connection is EXCLUDED
        // (legacy AllExcept(author) semantics preserved: the moderated user is not tipped off live).
        const string authorConn = "author-conn";
        _connectionMapping.RegisterUser(authorConn, new ChatUser(AuthorBattleTag, false, "Sender", new ProfilePicture(), null, null));
        _focusRegistry.Focus(authorConn, channel.Id, AuthorBattleTag);

        // A focused viewer must RECEIVE the removal.
        const string viewerConn = "viewer-conn";
        _focusRegistry.Focus(viewerConn, channel.Id, "viewer#1");

        // An unfocused member must receive NOTHING (it never had the message focused).
        const string unfocusedConn = "unfocused-conn";

        var result = await _chatHub.DeleteMessage(message.Id);
        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        Assert.AreEqual(1, _pushHarness.SignalCount(viewerConn, ChatEvents.MessageDeleted));
        var dto = _pushHarness.PayloadFor(viewerConn, ChatEvents.MessageDeleted) as MessageDeletedDto;
        Assert.IsNotNull(dto, "the focused viewer must receive a MessageDeletedDto payload");
        Assert.AreEqual(channel.Id, dto.ChannelId);
        Assert.AreEqual(message.Id, dto.MessageId);

        Assert.AreEqual(0, _pushHarness.SignalCount(authorConn, ChatEvents.MessageDeleted),
            "the moderated author's own focused connection must be excluded");
        Assert.AreEqual(0, _pushHarness.SignalCount(unfocusedConn, ChatEvents.MessageDeleted),
            "an unfocused member gets nothing (it never received the message)");
    }

    [Test]
    public async Task DeleteMessage_FocusedModerator_ReceivesSameEvent()
    {
        var channel = await CreateChannel();
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "spam");

        // The moderator is focused on the channel too — there is NO separate mod event: they receive the
        // SAME MessageDeleted and branch client-side on their own permission (flag vs remove).
        _focusRegistry.Focus(ModeratorConnectionId, channel.Id, ModeratorBattleTag);

        var result = await _chatHub.DeleteMessage(message.Id);
        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        Assert.AreEqual(1, _pushHarness.SignalCount(ModeratorConnectionId, ChatEvents.MessageDeleted));
        var dto = _pushHarness.PayloadFor(ModeratorConnectionId, ChatEvents.MessageDeleted) as MessageDeletedDto;
        Assert.IsNotNull(dto, "a focused moderator receives the same MessageDeletedDto");
        Assert.AreEqual(channel.Id, dto.ChannelId);
        Assert.AreEqual(message.Id, dto.MessageId);
    }

    [Test]
    public async Task DeleteMessage_Missing_ReturnsNotFound_NoEvent()
    {
        var result = await _chatHub.DeleteMessage("nonexistent-id");

        Assert.AreEqual(ChatResultCode.NotFound, result.Code);
        Assert.IsEmpty(_pushHarness.AllSignals, "a missing message must emit no removal event");
        Assert.IsEmpty(_mentionCleaner.Calls, "a missing message must not touch the mention inbox");
    }

    [Test]
    public async Task DeleteMessage_AlreadyDeleted_ReturnsOk_Idempotent_NoEvent()
    {
        var channel = await CreateChannel();
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "already gone");
        // Pre-mark as deleted by a DIFFERENT moderator at a fixed instant.
        var originalAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await _messageRepository.MarkDeleted(message.Id, "firstmod#1", originalAt);
        _focusRegistry.Focus("viewer-conn", channel.Id, "viewer#1");

        var result = await _chatHub.DeleteMessage(message.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.IsEmpty(_pushHarness.AllSignals, "an already-deleted message must emit NO event (idempotent)");
        Assert.IsEmpty(_mentionCleaner.Calls, "an already-deleted message must not re-invoke the cleaner");

        // The ORIGINAL deletion marker is preserved (the second delete is a pure no-op on the doc).
        var reloaded = await _messageRepository.Load(message.Id);
        Assert.AreEqual("firstmod#1", reloaded.Deleted.By, "the original deletion attribution must be preserved");
        Assert.AreEqual(originalAt, reloaded.Deleted.At);
    }

    [Test]
    [TestCase(ChannelType.Dm)]
    [TestCase(ChannelType.GroupDm)]
    public async Task DeleteMessage_DmChannel_ReturnsPermissionDenied_NothingDeleted(ChannelType privateType)
    {
        var channel = await CreateChannel(privateType);
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "private message");
        _focusRegistry.Focus("viewer-conn", channel.Id, "viewer#1");

        var result = await _chatHub.DeleteMessage(message.Id);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
        // Privacy wall: a moderator never touches DM/GroupDm content — nothing deleted, no event, no cleanup.
        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNull(reloaded.Deleted, "a moderator must never soft-delete private (DM/GroupDm) content");
        Assert.IsEmpty(_pushHarness.AllSignals);
        Assert.IsEmpty(_mentionCleaner.Calls);
    }

    [Test]
    public async Task DeleteMessage_ClanChannel_ReturnsPermissionDenied_NothingDeleted()
    {
        // Scope wall (spec §10 + plan D5): single-delete honors the SAME include-list as purge
        // (IsPurgeableChannel), so a System/Clan channel is OUT of moderation scope — a moderator can
        // never soft-delete clan content (TTL cleans it), symmetric with the purge wall.
        var channel = await CreateSystemChannel(SystemChannelKind.Clan);
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "clan message");
        _focusRegistry.Focus("viewer-conn", channel.Id, "viewer#1");

        var result = await _chatHub.DeleteMessage(message.Id);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNull(reloaded.Deleted, "a moderator must never soft-delete System/Clan content");
        Assert.IsEmpty(_pushHarness.AllSignals);
        Assert.IsEmpty(_mentionCleaner.Calls);
    }

    [Test]
    public async Task DeleteMessage_LobbyChannel_ReturnsPermissionDenied_NothingDeleted()
    {
        // Scope wall (spec §10 + plan D5): a System/Lobby channel is likewise OUT of moderation scope —
        // symmetric with purge (IsPurgeableChannel excludes System+Lobby). Nothing is deleted.
        var channel = await CreateSystemChannel(SystemChannelKind.Lobby);
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "lobby message");
        _focusRegistry.Focus("viewer-conn", channel.Id, "viewer#1");

        var result = await _chatHub.DeleteMessage(message.Id);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNull(reloaded.Deleted, "a moderator must never soft-delete System/Lobby content");
        Assert.IsEmpty(_pushHarness.AllSignals);
        Assert.IsEmpty(_mentionCleaner.Calls);
    }

    [Test]
    public async Task DeleteMessage_MatchChannel_ReturnsOk_SoftDeletes()
    {
        // The shared scope wall INCLUDES System+Match, so a legit single-delete in a match channel still
        // soft-deletes — proving the include-list wall is not over-tightened onto Public/SemiPublic only.
        var channel = await CreateSystemChannel(SystemChannelKind.Match);
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "match spam");

        var result = await _chatHub.DeleteMessage(message.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNotNull(reloaded.Deleted, "a System/Match message is in moderation scope and must soft-delete");
        Assert.AreEqual(ModeratorBattleTag, reloaded.Deleted.By);
    }

    [Test]
    public async Task DeleteMessage_InvokesMentionInboxCleaner_WithMessageId()
    {
        var channel = await CreateChannel();
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "mention @someone");

        var result = await _chatHub.DeleteMessage(message.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(1, _mentionCleaner.Calls.Count, "the cleaner must be invoked exactly once");
        CollectionAssert.AreEqual(new[] { message.Id }, _mentionCleaner.Calls[0].ToArray(),
            "the cleaner must be called with exactly the deleted message id");
    }

    [Test]
    public async Task DeleteMessage_UserRead_ExcludesIt_ModeratorRead_FlaggedTrue()
    {
        var channel = await CreateChannel();
        var survivor = await SeedMessage(channel.Id, AuthorBattleTag, "still here");
        var doomed = await SeedMessage(channel.Id, AuthorBattleTag, "delete me");

        // Make the moderator a member so we can drive the USER read path (GetMessages → UserVisible).
        _onlineMemberRegistry.Join(channel.Id, ModeratorConnectionId, new MemberState(ModeratorBattleTag, NotificationLevel.Mentions, 0, channel.Type));

        var result = await _chatHub.DeleteMessage(doomed.Id);
        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        // USER read (UserVisible) excludes the soft-deleted row entirely.
        var userRead = await _chatHub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 50);
        Assert.AreEqual(ChatResultCode.Ok, userRead.Code);
        CollectionAssert.AreEqual(new[] { survivor.Id }, userRead.Messages.Select(m => m.Id).ToArray(),
            "the user read must exclude the soft-deleted message");

        // MODERATOR read (LoadForModerator) includes it, flagged deleted with the moderator attribution.
        var modRead = await _messageRepository.LoadForModerator(channel.Id);
        var doomedRow = modRead.Single(m => m.Id == doomed.Id);
        Assert.IsNotNull(doomedRow.Deleted, "the moderator read must include the deleted row with its flag intact");
        Assert.AreEqual(ModeratorBattleTag, doomedRow.Deleted.By);
    }

    [Test]
    public async Task DeleteMessage_LogsModeratorAudit()
    {
        var channel = await CreateChannel();
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "audited");

        var captured = new List<string>();
        var sink = new DelegatingLogSink(evt => captured.Add(evt.RenderMessage()));
        var originalLogger = Serilog.Log.Logger;
        var testLogger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(sink)
            .CreateLogger();
        Serilog.Log.Logger = testLogger;
        try
        {
            var result = await _chatHub.DeleteMessage(message.Id);
            Assert.AreEqual(ChatResultCode.Ok, result.Code);
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            testLogger.Dispose();
        }

        Assert.IsTrue(
            captured.Any(l => l.Contains(ModeratorBattleTag) && l.Contains(message.Id) && l.Contains(channel.Id)),
            "the moderation audit line must record the moderator battleTag, the message id, and the channel id");
    }

    [Test]
    public async Task DeleteMessage_NoSession_ReturnsPermissionDenied_FailClosed()
    {
        var channel = await CreateChannel();
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "orphan");
        // Point the hub at a connection with NO registered session (displaced / never-authenticated).
        var ghost = new Mock<HubCallerContext>();
        ghost.Setup(c => c.ConnectionId).Returns("ghost-conn");
        _chatHub.Context = ghost.Object;

        var result = await _chatHub.DeleteMessage(message.Id);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNull(reloaded.Deleted, "no session → fail closed → nothing deleted");
        Assert.IsEmpty(_pushHarness.AllSignals);
        Assert.IsEmpty(_mentionCleaner.Calls);
    }

    [Test]
    public async Task DeleteMessage_VanishedChannel_ReturnsPermissionDenied_NothingDeleted()
    {
        // C4 (Task 4) directive (c): a message whose ChannelId resolves to NO channel doc is rejected
        // fail-closed — we cannot prove the channel is not private, so no delete slips past the privacy
        // wall on a data-integrity edge (same treatment as the DM/GroupDm wall above). Inserted directly
        // (not via SeedMessage, which would AllocateSeq against a non-existent channel and throw).
        var message = new ChannelMessage
        {
            ChannelId = "vanished-channel-id",
            Seq = 1,
            Sender = new MessageSender { BattleTag = AuthorBattleTag, Name = "Sender" },
            Content = "orphaned by a vanished channel",
            SentAt = DateTime.UtcNow,
        };
        await _messageRepository.Insert(message);
        _focusRegistry.Focus("viewer-conn", message.ChannelId, "viewer#1");

        var result = await _chatHub.DeleteMessage(message.Id);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNull(reloaded.Deleted, "a message whose channel cannot be resolved must never be soft-deleted");
        Assert.IsEmpty(_pushHarness.AllSignals);
        Assert.IsEmpty(_mentionCleaner.Calls);
    }

    [Test]
    public async Task DeleteMessage_CleanerThrows_SoftDeleteAndAuditSurvive_BeforeThePropagation()
    {
        // Directive (b) rationale, locked in: the durable soft-delete AND the audit run BEFORE the mention
        // cleaner, so a throwing cleaner (as a real C6 impl may be) can never leave a committed moderation
        // action un-logged. The cleaner faults AFTER the commit+audit, so the call surfaces the exception —
        // but the row is already soft-deleted and the audit line already written.
        var channel = await CreateChannel();
        var message = await SeedMessage(channel.Id, AuthorBattleTag, "audited then cleaner throws");
        _mentionCleaner.ThrowAfterCapture = true;

        var captured = new List<string>();
        var sink = new DelegatingLogSink(evt => captured.Add(evt.RenderMessage()));
        var originalLogger = Serilog.Log.Logger;
        var testLogger = new Serilog.LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
        Serilog.Log.Logger = testLogger;
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => _chatHub.DeleteMessage(message.Id));
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            testLogger.Dispose();
        }

        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNotNull(reloaded.Deleted, "the soft-delete commits BEFORE the cleaner runs");
        Assert.AreEqual(ModeratorBattleTag, reloaded.Deleted.By);
        Assert.IsTrue(
            captured.Any(l => l.Contains(ModeratorBattleTag) && l.Contains(message.Id) && l.Contains(channel.Id)),
            "the audit line is logged BEFORE the cleaner throws (a committed action is never un-logged)");
    }

    // -------------------------------------------------------------------------------------------------
    // C4 (Task 4) — durable cross-channel PurgeMessagesFromUser (D6). UPGRADE lineage: the legacy
    // in-memory PurgeMessagesFromUser_ExistingUser_DeletesAllMessagesAndNotifiesCorrectClients (which
    // drove ChatHistory.DeleteMessagesFromUser + Clients.AllExcept with a bare List<string> under the
    // SINGULAR "BulkMessageDeleted" string) and PurgeMessagesFromUser_UserWithNoMessages_DoesNotNotifyClients
    // are superseded here by the durable pipeline: LoadPurgeableBySender (collation-insensitive), the
    // eligible-channel-type privacy wall (Public / SemiPublic / System+Match ONLY — DM/GroupDm/Clan/Lobby
    // and unresolvable channels excluded), the conditional bulk soft-delete (MarkDeletedMany), the
    // per-channel BulkMessagesDeletedDto delivered to FOCUSED viewers minus the target's connections,
    // the mention-inbox cleanup hook, and a PurgeMessagesResult(Ok, n) carrying the actual modified count.
    // C4 Task 7 dropped the ChatHistory-direct test cluster that used to follow here (ChatHistory/
    // ChatController/Message.cs are retired) — the durable equivalents above are the regression net.
    // -------------------------------------------------------------------------------------------------

    private async Task<ChatChannel> CreateSystemChannel(SystemChannelKind kind)
    {
        var channel = new ChatChannel { Type = ChannelType.System, SystemKind = kind };
        await _channelRepository.Insert(channel);
        return channel;
    }

    [Test]
    public async Task Purge_SoftDeletesAcross_Public_SemiPublic_Match_Channels()
    {
        var publicChannel = await CreateChannel(ChannelType.Public);
        var semiPublic = await CreateChannel(ChannelType.SemiPublic);
        var matchChannel = await CreateSystemChannel(SystemChannelKind.Match);
        var clanChannel = await CreateSystemChannel(SystemChannelKind.Clan);
        var lobbyChannel = await CreateSystemChannel(SystemChannelKind.Lobby);
        var dm = await CreateChannel(ChannelType.Dm);
        var groupDm = await CreateChannel(ChannelType.GroupDm);

        const string target = "target#123";
        var inPublic = await SeedMessage(publicChannel.Id, target, "p");
        var inSemi = await SeedMessage(semiPublic.Id, target, "s");
        var inMatch = await SeedMessage(matchChannel.Id, target, "m");
        var inClan = await SeedMessage(clanChannel.Id, target, "c");
        var inLobby = await SeedMessage(lobbyChannel.Id, target, "l");
        var inDm = await SeedMessage(dm.Id, target, "d");
        var inGroupDm = await SeedMessage(groupDm.Id, target, "g");

        var result = await _chatHub.PurgeMessagesFromUser(target);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(3, result.MessagesDeleted, "exactly the three eligible-channel rows are soft-deleted");

        // Soft-deleted ONLY in the three eligible channel types...
        Assert.IsNotNull((await _messageRepository.Load(inPublic.Id)).Deleted);
        Assert.IsNotNull((await _messageRepository.Load(inSemi.Id)).Deleted);
        Assert.IsNotNull((await _messageRepository.Load(inMatch.Id)).Deleted);
        Assert.AreEqual(ModeratorBattleTag, (await _messageRepository.Load(inPublic.Id)).Deleted.By,
            "the moderator battleTag is the deletion attribution");
        // ...and NEVER in clan / lobby / dm / groupDm (the privacy + scope wall).
        Assert.IsNull((await _messageRepository.Load(inClan.Id)).Deleted);
        Assert.IsNull((await _messageRepository.Load(inLobby.Id)).Deleted);
        Assert.IsNull((await _messageRepository.Load(inDm.Id)).Deleted);
        Assert.IsNull((await _messageRepository.Load(inGroupDm.Id)).Deleted);
    }

    [Test]
    public async Task Purge_NeverTouches_Dm_GroupDm_Clan_Lobby()
    {
        var dm = await CreateChannel(ChannelType.Dm);
        var groupDm = await CreateChannel(ChannelType.GroupDm);
        var clan = await CreateSystemChannel(SystemChannelKind.Clan);
        var lobby = await CreateSystemChannel(SystemChannelKind.Lobby);

        const string target = "target#123";
        var inDm = await SeedMessage(dm.Id, target, "d");
        var inGroupDm = await SeedMessage(groupDm.Id, target, "g");
        var inClan = await SeedMessage(clan.Id, target, "c");
        var inLobby = await SeedMessage(lobby.Id, target, "l");

        // Directive (c) purge analog: a message whose ChannelId resolves to NO channel doc is likewise
        // never deleted (fail-closed — dropped, not deleted). Inserted directly to sidestep AllocateSeq.
        var orphan = new ChannelMessage
        {
            ChannelId = "vanished-channel-id",
            Seq = 1,
            Sender = new MessageSender { BattleTag = target, Name = "Target" },
            Content = "orphaned",
            SentAt = DateTime.UtcNow,
        };
        await _messageRepository.Insert(orphan);

        var result = await _chatHub.PurgeMessagesFromUser(target);

        // The wall: none of these are purgeable, so nothing is deleted and no event fires.
        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(0, result.MessagesDeleted);
        Assert.IsNull((await _messageRepository.Load(inDm.Id)).Deleted, "DM content is never purged");
        Assert.IsNull((await _messageRepository.Load(inGroupDm.Id)).Deleted, "GroupDm content is never purged");
        Assert.IsNull((await _messageRepository.Load(inClan.Id)).Deleted, "clan content is never purged");
        Assert.IsNull((await _messageRepository.Load(inLobby.Id)).Deleted, "lobby content is never purged");
        Assert.IsNull((await _messageRepository.Load(orphan.Id)).Deleted, "an unresolvable-channel message is never purged");
        Assert.IsEmpty(_pushHarness.AllSignals);
        Assert.IsEmpty(_mentionCleaner.Calls);
    }

    [Test]
    public async Task Purge_EmitsBulkDto_PerAffectedChannel_ToFocusedViewers_ExceptTargetConnections()
    {
        var channelA = await CreateChannel(ChannelType.Public);
        var channelB = await CreateChannel(ChannelType.SemiPublic);
        var channelC = await CreateChannel(ChannelType.Public); // eligible deletions but no focused viewers

        const string target = "target#123";
        var a1 = await SeedMessage(channelA.Id, target, "a1");
        var a2 = await SeedMessage(channelA.Id, target, "a2");
        var b1 = await SeedMessage(channelB.Id, target, "b1");
        await SeedMessage(channelC.Id, target, "c1");

        // The purge target is online and focused on channelA — their own connection is EXCLUDED.
        const string targetConn = "target-conn";
        _connectionMapping.RegisterUser(targetConn, new ChatUser(target, false, "Target", new ProfilePicture(), null, null));
        _focusRegistry.Focus(targetConn, channelA.Id, target);

        // Focused viewers on A and B must RECEIVE the removal for their own channel.
        const string viewerA = "viewer-a";
        const string viewerB = "viewer-b";
        _focusRegistry.Focus(viewerA, channelA.Id, "viewerA#1");
        _focusRegistry.Focus(viewerB, channelB.Id, "viewerB#1");
        // channelC has NO focused viewers.

        var result = await _chatHub.PurgeMessagesFromUser(target);
        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(4, result.MessagesDeleted);

        // viewerA gets one channel-scoped BulkMessagesDeletedDto for channelA carrying BOTH A ids.
        Assert.AreEqual(1, _pushHarness.SignalCount(viewerA, ChatEvents.BulkMessagesDeleted));
        var dtoA = _pushHarness.PayloadFor(viewerA, ChatEvents.BulkMessagesDeleted) as BulkMessagesDeletedDto;
        Assert.IsNotNull(dtoA);
        Assert.AreEqual(channelA.Id, dtoA.ChannelId);
        CollectionAssert.AreEquivalent(new[] { a1.Id, a2.Id }, dtoA.MessageIds.ToArray());

        // viewerB gets one for channelB carrying the B id.
        Assert.AreEqual(1, _pushHarness.SignalCount(viewerB, ChatEvents.BulkMessagesDeleted));
        var dtoB = _pushHarness.PayloadFor(viewerB, ChatEvents.BulkMessagesDeleted) as BulkMessagesDeletedDto;
        Assert.IsNotNull(dtoB);
        Assert.AreEqual(channelB.Id, dtoB.ChannelId);
        CollectionAssert.AreEqual(new[] { b1.Id }, dtoB.MessageIds.ToArray());

        // The target's own focused connection is excluded (not tipped off live).
        Assert.AreEqual(0, _pushHarness.SignalCount(targetConn, ChatEvents.BulkMessagesDeleted));
        // channelC produced eligible deletions but had no focused viewers → NO event for it anywhere.
        Assert.IsFalse(
            _pushHarness.AllSignals.Any(s => (s.Payload as BulkMessagesDeletedDto)?.ChannelId == channelC.Id),
            "a channel with no focused viewers must emit no BulkMessagesDeleted event");
    }

    [Test]
    public async Task Purge_MixedCaseBattleTag_StillPurges()
    {
        var channel = await CreateChannel(ChannelType.Public);
        var message = await SeedMessage(channel.Id, "Target#123", "case test");

        // The moderator supplies a DIFFERENT casing than the stored sender — the collation makes
        // LoadPurgeableBySender match it end-to-end (fixing the legacy case-SENSITIVE purge bug).
        var result = await _chatHub.PurgeMessagesFromUser("TARGET#123");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(1, result.MessagesDeleted);
        Assert.IsNotNull((await _messageRepository.Load(message.Id)).Deleted,
            "a mixed-case battleTag must still purge the stored-casing rows");
    }

    [Test]
    public async Task Purge_SkipsAlreadyDeleted_Idempotent_Rerun()
    {
        var channel = await CreateChannel(ChannelType.Public);
        await SeedMessage(channel.Id, "target#123", "spam");
        _focusRegistry.Focus("viewer-conn", channel.Id, "viewer#1");

        var first = await _chatHub.PurgeMessagesFromUser("target#123");
        Assert.AreEqual(ChatResultCode.Ok, first.Code);
        Assert.AreEqual(1, first.MessagesDeleted);
        var signalsAfterFirst = _pushHarness.AllSignals.Count;

        // Re-running finds no non-deleted rows (LoadPurgeableBySender excludes Deleted != null) → Ok + 0,
        // and emits NO further events (structural idempotency).
        var second = await _chatHub.PurgeMessagesFromUser("target#123");
        Assert.AreEqual(ChatResultCode.Ok, second.Code);
        Assert.AreEqual(0, second.MessagesDeleted, "a re-purge soft-deletes nothing");
        Assert.AreEqual(signalsAfterFirst, _pushHarness.AllSignals.Count, "the re-purge must emit no additional events");
    }

    [Test]
    public async Task Purge_NoMessages_ReturnsOkZero_NoEvents()
    {
        // Another user's message exists, but the purge target has none.
        var channel = await CreateChannel(ChannelType.Public);
        await SeedMessage(channel.Id, "other#456", "innocent");
        _focusRegistry.Focus("viewer-conn", channel.Id, "viewer#1");

        var result = await _chatHub.PurgeMessagesFromUser("nonexistent#123");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(0, result.MessagesDeleted);
        Assert.IsEmpty(_pushHarness.AllSignals, "no eligible messages → no BulkMessagesDeleted events");
        Assert.IsEmpty(_mentionCleaner.Calls, "no eligible messages → the mention cleaner is never invoked");
    }

    [Test]
    public async Task Purge_InvokesMentionInboxCleaner_WithAllDeletedIds()
    {
        var channelA = await CreateChannel(ChannelType.Public);
        var channelB = await CreateChannel(ChannelType.SemiPublic);
        var dm = await CreateChannel(ChannelType.Dm);

        const string target = "target#123";
        var a1 = await SeedMessage(channelA.Id, target, "a1");
        var b1 = await SeedMessage(channelB.Id, target, "b1");
        await SeedMessage(dm.Id, target, "not purged");

        var result = await _chatHub.PurgeMessagesFromUser(target);
        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        Assert.AreEqual(1, _mentionCleaner.Calls.Count, "the cleaner is invoked exactly once with the whole purged batch");
        CollectionAssert.AreEquivalent(new[] { a1.Id, b1.Id }, _mentionCleaner.Calls[0].ToArray(),
            "the cleaner must receive exactly the soft-deleted (eligible-channel) ids — never the DM id that was never touched");
    }

    [Test]
    public async Task Purge_TargetOwnShadowRows_AlsoDeleted()
    {
        var channel = await CreateChannel(ChannelType.Public);
        var normal = await SeedMessage(channel.Id, "target#123", "normal");
        var shadow = await SeedMessage(channel.Id, "target#123", "shadow", shadow: true);

        var result = await _chatHub.PurgeMessagesFromUser("target#123");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(2, result.MessagesDeleted, "the target's own shadow rows are purged like any other row");
        Assert.IsNotNull((await _messageRepository.Load(normal.Id)).Deleted);
        Assert.IsNotNull((await _messageRepository.Load(shadow.Id)).Deleted, "a shadow row from the target is soft-deleted too");
    }

    [Test]
    public async Task Purge_UserReads_ExcludeAcrossChannels_ModeratorReads_Flagged()
    {
        var channelA = await CreateChannel(ChannelType.Public);
        var channelB = await CreateChannel(ChannelType.SemiPublic);

        const string target = "target#123";
        var survivorA = await SeedMessage(channelA.Id, "other#456", "still here A");
        var doomedA = await SeedMessage(channelA.Id, target, "purge me A");
        var doomedB = await SeedMessage(channelB.Id, target, "purge me B");

        // Make the moderator a member of both channels so we can drive the USER read path (UserVisible).
        _onlineMemberRegistry.Join(channelA.Id, ModeratorConnectionId, new MemberState(ModeratorBattleTag, NotificationLevel.Mentions, 0, channelA.Type));
        _onlineMemberRegistry.Join(channelB.Id, ModeratorConnectionId, new MemberState(ModeratorBattleTag, NotificationLevel.Mentions, 0, channelB.Type));

        var result = await _chatHub.PurgeMessagesFromUser(target);
        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        // USER read (UserVisible) excludes the purged rows across BOTH channels.
        var readA = await _chatHub.GetMessages(channelA.Id, beforeSeq: null, aroundSeq: null, limit: 50);
        CollectionAssert.AreEqual(new[] { survivorA.Id }, readA.Messages.Select(m => m.Id).ToArray(),
            "the user read must exclude the purged message in channel A");
        var readB = await _chatHub.GetMessages(channelB.Id, beforeSeq: null, aroundSeq: null, limit: 50);
        Assert.IsEmpty(readB.Messages, "the purged row is excluded from the user read in channel B");

        // MODERATOR read (LoadForModerator) includes the purged rows, flagged with the moderator attribution.
        var modA = await _messageRepository.LoadForModerator(channelA.Id);
        var flaggedA = modA.Single(m => m.Id == doomedA.Id);
        Assert.IsNotNull(flaggedA.Deleted, "the moderator read must include the purged row, flagged");
        Assert.AreEqual(ModeratorBattleTag, flaggedA.Deleted.By);
        var modB = await _messageRepository.LoadForModerator(channelB.Id);
        Assert.IsNotNull(modB.Single(m => m.Id == doomedB.Id).Deleted);
    }

    [Test]
    public async Task Purge_LogsModeratorAudit_WithCount()
    {
        var channel = await CreateChannel(ChannelType.Public);
        for (var i = 0; i < 5; i++)
        {
            await SeedMessage(channel.Id, "target#123", $"m{i}");
        }

        var captured = new List<string>();
        var sink = new DelegatingLogSink(evt => captured.Add(evt.RenderMessage()));
        var originalLogger = Serilog.Log.Logger;
        var testLogger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(sink)
            .CreateLogger();
        Serilog.Log.Logger = testLogger;
        try
        {
            var result = await _chatHub.PurgeMessagesFromUser("target#123");
            Assert.AreEqual(ChatResultCode.Ok, result.Code);
            Assert.AreEqual(5, result.MessagesDeleted);
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            testLogger.Dispose();
        }

        Assert.IsTrue(
            captured.Any(l => l.Contains(ModeratorBattleTag) && l.Contains("target#123") && l.Contains(" 5 ")),
            "the purge audit line must record the moderator battleTag, the target battleTag, and the count");
    }

    [Test]
    public async Task Purge_DocsRemainInMongo_ExpiresAtUntouched()
    {
        var channel = await CreateChannel(ChannelType.Public);
        var expiry = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var message = await SeedMessage(channel.Id, "target#123", "ttl test", expiresAt: expiry);

        var result = await _chatHub.PurgeMessagesFromUser("target#123");
        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        // Soft-delete only: the doc survives (physical removal stays TTL-only) with ExpiresAt untouched.
        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNotNull(reloaded, "the doc must survive — soft-delete only, never a hard delete");
        Assert.IsNotNull(reloaded.Deleted);
        Assert.AreEqual(expiry, reloaded.ExpiresAt, "ExpiresAt/TTL must be left untouched by the purge");
    }

    [Test]
    public async Task Purge_CleanerThrows_BulkSoftDeleteAndAuditSurvive_BeforeThePropagation()
    {
        // Directive (b) rationale, locked in for purge: the conditional bulk soft-delete AND the audit run
        // BEFORE the mention cleaner, so a throwing cleaner can never leave a committed purge un-logged.
        var channel = await CreateChannel(ChannelType.Public);
        var message = await SeedMessage(channel.Id, "target#123", "spam");
        _mentionCleaner.ThrowAfterCapture = true;

        var captured = new List<string>();
        var sink = new DelegatingLogSink(evt => captured.Add(evt.RenderMessage()));
        var originalLogger = Serilog.Log.Logger;
        var testLogger = new Serilog.LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
        Serilog.Log.Logger = testLogger;
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => _chatHub.PurgeMessagesFromUser("target#123"));
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            testLogger.Dispose();
        }

        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNotNull(reloaded.Deleted, "the bulk soft-delete commits BEFORE the cleaner runs");
        Assert.AreEqual(ModeratorBattleTag, reloaded.Deleted.By);
        Assert.IsTrue(
            captured.Any(l => l.Contains(ModeratorBattleTag) && l.Contains("target#123")),
            "the purge audit line is logged BEFORE the cleaner throws (a committed action is never un-logged)");
    }

    [Test]
    [TestCase("target#123", "Inappropriate behavior", false, 1, Description = "Regular ban for 1 day")]
    [TestCase("spammer#456", "Spam", true, 0.25, Description = "Shadow ban for 6 hours")]
    [TestCase("toxic#789", "Toxic behavior", false, 7, Description = "Regular ban for 7 days")]
    [TestCase("shadow#321", "Trolling", true, 3, Description = "Shadow ban for 3 days")]
    public async Task BanUser_ValidRequest_AddsLoungeMute(string battleTag, string reason, bool isShadowBan, double daysToAdd)
    {
        // Arrange
        var endDateTime = DateTime.UtcNow.AddDays(daysToAdd);
        var endDate = endDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Act
        await _chatHub.BanUser(battleTag, reason, isShadowBan, endDate);

        // Assert
        var mute = await _muteRepository.GetMutedPlayer(battleTag);
        Assert.IsNotNull(mute);
        Assert.AreEqual(battleTag, mute.battleTag);
        Assert.AreEqual(reason, mute.reason);
        Assert.AreEqual("admin#123", mute.author);
        Assert.AreEqual(isShadowBan, mute.isShadowBan);

        // Allow for small time differences due to test execution time
        var timeDifference = Math.Abs((mute.endDate - endDateTime).TotalSeconds);
        Assert.IsTrue(timeDifference < 10, $"Expected end date to be close to {endDateTime}, but was {mute.endDate}");
    }

    /// <summary>
    /// Capturing <see cref="IMentionInboxCleaner"/> spy — records each message-id batch the hub asks it
    /// to purge (D10). Task 3 is the FIRST caller of this coordination surface.
    /// </summary>
    private sealed class CapturingMentionInboxCleaner : IMentionInboxCleaner
    {
        public List<IReadOnlyCollection<string>> Calls { get; } = new();

        /// <summary>
        /// When set, <see cref="RemoveForMessages"/> records the batch and THEN throws — simulating a
        /// real (C6) cleaner that can fault, so tests can prove the audit-before-side-effects ordering
        /// (directive (b)): the durable soft-delete + audit must already be committed before the cleaner
        /// runs, so a throwing cleaner can never leave a committed moderation action un-logged.
        /// </summary>
        public bool ThrowAfterCapture { get; set; }

        public Task RemoveForMessages(IReadOnlyCollection<string> messageIds)
        {
            Calls.Add(messageIds);
            if (ThrowAfterCapture)
            {
                throw new InvalidOperationException("simulated mention-cleaner failure");
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>A Serilog sink that forwards each event to a callback (for asserting the audit line).</summary>
    private sealed class DelegatingLogSink(Action<Serilog.Events.LogEvent> onEmit) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => onEmit(logEvent);
    }
}
