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
    private ChatHistory _chatHistory;
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
        _chatHistory = new ChatHistory();

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
            _muteRepository,
            chatAuthenticationService.Object,
            _onlineMemberRegistry,
            _connectionMapping);

        _chatHub = new ChatHub(
            _connectionMapping,
            _chatHistory,
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
            _mentionCleaner);

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
        _onlineMemberRegistry.Join(channel.Id, ModeratorConnectionId, new MemberState(ModeratorBattleTag, NotificationLevel.Mentions, 0));

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

    // -------------------------------------------------------------------------------------------------
    // LEGACY (untouched by Task 3): the PurgeMessagesFromUser section is rewritten by Task 4; the
    // ChatHistory-direct cluster is dropped by Task 7. Both still drive the legacy in-memory ChatHistory
    // + Clients.AllExcept path and are left verbatim below.
    // -------------------------------------------------------------------------------------------------

    [Test]
    [TestCase(true, Description = "Target user is connected - should exclude them from notification")]
    [TestCase(false, Description = "Target user is not connected - should send to all")]
    public async Task PurgeMessagesFromUser_ExistingUser_DeletesAllMessagesAndNotifiesCorrectClients(bool targetUserIsConnected)
    {
        // Arrange
        var targetUser = new ChatUser("target#123", false, "Target", new ProfilePicture(), null, null);
        var otherUser = new ChatUser("other#456", false, "Other", new ProfilePicture(), null, null);

        var message1 = new ChatMessage(targetUser, "Message 1");
        var message2 = new ChatMessage(otherUser, "Message 2");
        var message3 = new ChatMessage(targetUser, "Message 3");

        _chatHistory.AddMessage("W3C Lounge", message1);
        _chatHistory.AddMessage("W3C Lounge", message2);
        _chatHistory.AddMessage("room2", message3);

        if (targetUserIsConnected)
        {
            _connectionMapping.Add("TargetConnectionId", "W3C Lounge", targetUser);
        }

        // Act
        await _chatHub.PurgeMessagesFromUser("target#123");

        // Assert
        var loungeMessages = _chatHistory.GetMessages("W3C Lounge");
        var room2Messages = _chatHistory.GetMessages("room2");

        Assert.AreEqual(1, loungeMessages.Count, "Only other user's message should remain in lounge");
        Assert.AreEqual("other#456", loungeMessages[0].User.BattleTag);
        Assert.AreEqual(0, room2Messages.Count, "Target user's message should be deleted from room2");

        // Verify AllExcept was called with correct exclusion list
        var expectedExcludedIds = targetUserIsConnected ? new[] { "TargetConnectionId" } : new string[0];
        _clients.Verify(c => c.AllExcept(
            It.Is<System.Collections.Generic.IReadOnlyList<string>>(list =>
                list.Count == expectedExcludedIds.Length &&
                expectedExcludedIds.All(id => list.Contains(id)))),
            Times.Once);

        _mockAllExceptProxy.Verify(p => p.SendCoreAsync("BulkMessageDeleted",
            It.Is<object[]>(args => args.Length == 1 &&
                args[0] != null &&
                args[0].GetType() == typeof(System.Collections.Generic.List<string>)),
            default), Times.Once);

        // Verify All proxy was NOT called (since we now always use AllExcept)
        _mockAllProxy.Verify(p => p.SendCoreAsync("BulkMessageDeleted", It.IsAny<object[]>(), default),
            Times.Never);
    }

    [Test]
    public async Task PurgeMessagesFromUser_UserWithNoMessages_DoesNotNotifyClients()
    {
        // Arrange
        var user = new ChatUser("other#456", false, "Other", new ProfilePicture(), null, null);
        var message = new ChatMessage(user, "Message");
        _chatHistory.AddMessage("W3C Lounge", message);

        // Act
        await _chatHub.PurgeMessagesFromUser("nonexistent#123");

        // Assert
        var messages = _chatHistory.GetMessages("W3C Lounge");
        Assert.AreEqual(1, messages.Count, "Original message should remain");

        _mockAllProxy.Verify(p => p.SendCoreAsync("BulkMessageDeleted", It.IsAny<object[]>(), default),
            Times.Never);
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

    [Test]
    [TestCase("test#123", "Test message", "room1", Description = "Delete message from room1")]
    [TestCase("user#456", "Another message", "room2", Description = "Delete message from room2")]
    [TestCase("admin#789", "Admin message", "admin-room", Description = "Delete admin message")]
    public void ChatHistory_DeleteMessage_ReturnsDeletedMessage(string battleTag, string messageText, string room)
    {
        // Arrange
        var user = new ChatUser(battleTag, false, "Test", new ProfilePicture(), null, null);
        var message = new ChatMessage(user, messageText);
        _chatHistory.AddMessage(room, message);

        // Act
        var deletedMessage = _chatHistory.DeleteMessage(message.Id);

        // Assert
        Assert.IsNotNull(deletedMessage);
        Assert.AreEqual(message.Id, deletedMessage.Id);
        Assert.AreEqual(messageText, deletedMessage.Message);
        Assert.AreEqual(battleTag, deletedMessage.User.BattleTag);
        Assert.AreEqual(0, _chatHistory.GetMessages(room).Count);
    }

    [Test]
    [TestCase("nonexistent-id")]
    [TestCase("")]
    [TestCase("invalid-guid")]
    public void ChatHistory_DeleteMessage_NonExistentMessage_ReturnsNull(string messageId)
    {
        // Act
        var deletedMessage = _chatHistory.DeleteMessage(messageId);

        // Assert
        Assert.IsNull(deletedMessage);
    }

    [Test]
    public void ChatHistory_DeleteMessagesFromUser_ReturnsDeletedMessagesList()
    {
        // Arrange
        var user1 = new ChatUser("test#123", false, "Test1", new ProfilePicture(), null, null);
        var user2 = new ChatUser("other#456", false, "Test2", new ProfilePicture(), null, null);
        var message1 = new ChatMessage(user1, "Message 1");
        var message2 = new ChatMessage(user2, "Message 2");
        var message3 = new ChatMessage(user1, "Message 3");

        _chatHistory.AddMessage("room1", message1);
        _chatHistory.AddMessage("room1", message2);
        _chatHistory.AddMessage("room2", message3);

        // Act
        var deletedMessages = _chatHistory.DeleteMessagesFromUser("test#123");

        // Assert
        Assert.AreEqual(2, deletedMessages.Count);
        Assert.IsTrue(deletedMessages.Any(m => m.Id == message1.Id));
        Assert.IsTrue(deletedMessages.Any(m => m.Id == message3.Id));
        Assert.AreEqual(1, _chatHistory.GetMessages("room1").Count);
        Assert.AreEqual("other#456", _chatHistory.GetMessages("room1")[0].User.BattleTag);
        Assert.AreEqual(0, _chatHistory.GetMessages("room2").Count);
    }

    /// <summary>
    /// Capturing <see cref="IMentionInboxCleaner"/> spy — records each message-id batch the hub asks it
    /// to purge (D10). Task 3 is the FIRST caller of this coordination surface.
    /// </summary>
    private sealed class CapturingMentionInboxCleaner : IMentionInboxCleaner
    {
        public List<IReadOnlyCollection<string>> Calls { get; } = new();

        public Task RemoveForMessages(IReadOnlyCollection<string> messageIds)
        {
            Calls.Add(messageIds);
            return Task.CompletedTask;
        }
    }

    /// <summary>A Serilog sink that forwards each event to a callback (for asserting the audit line).</summary>
    private sealed class DelegatingLogSink(Action<Serilog.Events.LogEvent> onEmit) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => onEmit(logEvent);
    }
}
