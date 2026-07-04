using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Unit tests for <see cref="FanOutEngine.OnMessagePersisted"/> — the C3 (Task 12) focused
/// <c>MessageReceived</c> delivery path. Pure in-memory: a real <see cref="FocusRegistry"/> is seeded
/// directly (focus some connections, leave others unfocused) and a <see cref="HubPushCaptureHarness"/>
/// captures every push. No Mongo, no live hub.
/// <para>
/// The pinned guardrail lives here: full <c>MessageReceived</c> payloads reach FOCUSED connections
/// ONLY; unfocused members never see one (their notification is the coalesced <c>ChannelActivity</c> —
/// Task 13, not this engine). A shadow message is delivered to the author's own focused connection and
/// nobody else, and the user-facing DTO always reads <c>deleted:false</c>/<c>shadow:false</c> — even a
/// shadow author's own echo — so the author never learns they are shadow-banned (the illusion).
/// </para>
/// </summary>
public class FanOutEngineTests
{
    private const string ChannelId = "channel-1";
    private const string AuthorConnection = "conn-author";
    private const string OtherFocusedConnection = "conn-other-focused";
    private const string UnfocusedMemberConnection = "conn-unfocused-member";
    private const string ModeratorConnection = "conn-moderator";
    private const string AuthorBattleTag = "Author#1";
    private const string ModeratorBattleTag = "Mod#7";

    // C5 (Task 4, D4) pending-Dm activity suppression fixtures.
    private const string DmInitiator = "Initiator#1";
    private const string DmRecipient = "Recipient#2";
    private const string InitiatorConnection = "conn-dm-initiator";
    private const string RecipientConnection = "conn-dm-recipient";

    // A fixed instant for the threaded-in server clock. These Task-12 tests seed only the FocusRegistry
    // (not the OnlineMemberRegistry), so the Task-13 activity routing finds no members to offer — the
    // MessageReceived assertions here are unaffected by the routing extension.
    private static readonly DateTime Now = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    private static ChatChannel Channel() =>
        new ChatChannel { Id = ChannelId, Type = ChannelType.Public };

    // A 1:1 Dm channel in a given consent state, initiated by DmInitiator (C5 T4 suppression tests).
    private static ChatChannel DmChannel(DmRequestState state) =>
        new ChatChannel { Id = ChannelId, Type = ChannelType.Dm, RequestState = state, RequestInitiatedBy = DmInitiator };

    // The Task-13 activity routing gives FanOutEngine two more deps, and Task 18 adds a third
    // (ISessionRegistry, for the ChannelAdded/ChannelRemoved emit helpers — unused by these
    // OnMessagePersisted tests, so a throwaway instance is enough). A helper keeps every test's
    // construction terse; the OnlineMemberRegistry stays empty in these tests so no activity is routed.
    private static FanOutEngine NewEngine(HubPushCaptureHarness harness, FocusRegistry focusRegistry) =>
        NewEngine(harness, focusRegistry, new SessionRegistry());

    // C4 (Task 5) overload: a seeded SessionRegistry lets the moderator-flagged live-delivery tests
    // (D8) resolve a focused connection's permission via ISessionRegistry.TryGetByConnectionId, exactly
    // as OnMessagePersisted does at runtime. The OnlineMemberRegistry stays empty (no activity routing).
    private static FanOutEngine NewEngine(HubPushCaptureHarness harness, FocusRegistry focusRegistry, ISessionRegistry sessionRegistry)
    {
        var onlineMemberRegistry = new OnlineMemberRegistry();
        var coalescer = new ActivityCoalescer(harness.HubContext, onlineMemberRegistry);
        return new FanOutEngine(harness.HubContext, focusRegistry, onlineMemberRegistry, coalescer, sessionRegistry);
    }

    // A SessionRegistry seeded with the given (connection, battleTag, isModerator) entries. A moderator
    // entry mirrors ChatSession.HasPermission's conjunct exactly: IsAdmin AND Permissions⊇{Moderation}.
    private static SessionRegistry SessionsWith(params (string ConnectionId, string BattleTag, bool IsModerator)[] entries)
    {
        var registry = new SessionRegistry();
        foreach (var (connectionId, battleTag, isModerator) in entries)
        {
            registry.Register(connectionId, Identity(battleTag, isModerator), null);
        }
        return registry;
    }

    private static W3CUserAuthentication Identity(string battleTag, bool isModerator) =>
        new W3CUserAuthentication
        {
            BattleTag = battleTag,
            Name = battleTag.Split('#')[0],
            IsAdmin = isModerator,
            Permissions = isModerator ? new HashSet<EPermission> { EPermission.Moderation } : new HashSet<EPermission>(),
        };

    private static ChannelMessage Message(bool shadowFlag = false, MessageDeletion deletion = null) =>
        new ChannelMessage
        {
            Id = "message-1",
            ChannelId = ChannelId,
            Seq = 42,
            Sender = new MessageSender { BattleTag = AuthorBattleTag, Name = "Author" },
            Content = "hello world",
            SentAt = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc),
            // These domain flags are deliberately set on some tests to prove the DTO FORCES both false
            // for user-facing delivery regardless of the persisted value.
            Shadow = shadowFlag,
            Deleted = deletion,
        };

    [Test]
    public async Task OnMessagePersisted_FocusedViewers_ReceiveFullMessageReceived()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(OtherFocusedConnection, ChannelId, "Viewer#2");
        var engine = NewEngine(harness, focusRegistry);

        await engine.OnMessagePersisted(Channel(), Message(), AuthorConnection, isShadow: false, Now);

        // Every focused connection receives exactly one full MessageReceived payload.
        Assert.AreEqual(1, harness.SignalCount(AuthorConnection, ChatEvents.MessageReceived));
        Assert.AreEqual(1, harness.SignalCount(OtherFocusedConnection, ChatEvents.MessageReceived));

        var dto = harness.PayloadFor(OtherFocusedConnection, ChatEvents.MessageReceived) as MessageDto;
        Assert.IsNotNull(dto, "focused viewer must receive a MessageDto payload");
        Assert.AreEqual("message-1", dto.Id);
        Assert.AreEqual(ChannelId, dto.ChannelId);
        Assert.AreEqual(42, dto.Seq);
        Assert.AreEqual("hello world", dto.Content);
        Assert.AreEqual(AuthorBattleTag, dto.Sender.BattleTag);
        Assert.AreEqual(new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc), dto.SentAt);
    }

    [Test]
    public async Task OnMessagePersisted_UnfocusedConnections_NeverReceiveMessageReceived()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // AuthorConnection is focused; UnfocusedMemberConnection is a channel member but NOT focused,
        // so it is absent from the focused index entirely.
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        var engine = NewEngine(harness, focusRegistry);

        await engine.OnMessagePersisted(Channel(), Message(), AuthorConnection, isShadow: false, Now);

        // Guardrail: the unfocused member receives ZERO MessageReceived signals. Full payloads go to
        // focused connections only; the unfocused member's notification is ChannelActivity (Task 13).
        Assert.AreEqual(0, harness.SignalCount(UnfocusedMemberConnection, ChatEvents.MessageReceived));
        // Sanity: fan-out actually ran (the focused connection did receive it).
        Assert.AreEqual(1, harness.SignalCount(AuthorConnection, ChatEvents.MessageReceived));
    }

    [Test]
    public async Task MessageDto_CarriesModeratorFlagSlots_AlwaysFalseUserFacing()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        var engine = NewEngine(harness, focusRegistry);

        // A shadow author's own echo: the persisted message is flagged Shadow AND has a Deleted marker,
        // yet the user-facing DTO must FORCE both false so the author never learns they are shadow-banned.
        var flagged = Message(
            shadowFlag: true,
            deletion: new MessageDeletion { By = "moderator#1", At = DateTime.UtcNow });

        await engine.OnMessagePersisted(Channel(), flagged, AuthorConnection, isShadow: true, Now);

        var dto = harness.PayloadFor(AuthorConnection, ChatEvents.MessageReceived) as MessageDto;
        Assert.IsNotNull(dto, "shadow author's own focused connection must receive its echo");
        Assert.IsFalse(dto.Shadow, "shadow flag must read false user-facing (the illusion), even for the shadow author's own echo");
        Assert.IsFalse(dto.Deleted, "deleted flag must read false user-facing in C3 (populated by C4)");
    }

    [Test]
    public async Task Shadow_DeliversToAuthorFocusedConnectionsOnly()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // Both the shadow author AND a second member are focused on the same channel.
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(OtherFocusedConnection, ChannelId, "Viewer#2");
        var engine = NewEngine(harness, focusRegistry);

        await engine.OnMessagePersisted(Channel(), Message(shadowFlag: true), AuthorConnection, isShadow: true, Now);

        // The author's own focused connection receives the echo (with shadow:false — the illusion)...
        Assert.AreEqual(1, harness.SignalCount(AuthorConnection, ChatEvents.MessageReceived));
        var dto = harness.PayloadFor(AuthorConnection, ChatEvents.MessageReceived) as MessageDto;
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.Shadow);
        // ...and NO other focused connection sees a shadow post. Pinned shadow-ban integrity constraint.
        Assert.AreEqual(0, harness.SignalCount(OtherFocusedConnection, ChatEvents.MessageReceived));
    }

    [Test]
    public async Task Shadow_AuthorNotFocused_ReachesNobody()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // Only a NON-author connection is focused; the shadow author is not focused on the channel.
        focusRegistry.Focus(OtherFocusedConnection, ChannelId, "Viewer#2");
        var engine = NewEngine(harness, focusRegistry);

        await engine.OnMessagePersisted(Channel(), Message(shadowFlag: true), AuthorConnection, isShadow: true, Now);

        // A shadow message whose author is not focused simply reaches no one — the intersection of the
        // focused set and the author's connection is empty. The other focused member never sees it.
        Assert.AreEqual(0, harness.SignalCount(OtherFocusedConnection, ChatEvents.MessageReceived));
        Assert.AreEqual(0, harness.SignalCount(AuthorConnection, ChatEvents.MessageReceived));
        Assert.IsEmpty(harness.AllSignals);
    }

    [Test]
    public async Task Sender_OwnFocusedConnection_ReceivesEcho()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        var engine = NewEngine(harness, focusRegistry);

        await engine.OnMessagePersisted(Channel(), Message(), AuthorConnection, isShadow: false, Now);

        // The sender's own focused connection receives the echo (non-shadow). The client dedups this
        // echo against its own ack {messageId, seq} — that dedup is client-side and out of scope here.
        Assert.AreEqual(1, harness.SignalCount(AuthorConnection, ChatEvents.MessageReceived));
    }

    [Test]
    public async Task OnMessagePersisted_OneRecipientSendThrows_OthersStillReceive_NoExceptionPropagates()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // Two focused connections on the same channel; neither is the sender, so both are ordinary
        // non-shadow recipients — isolates the fault-tolerance behavior from shadow-routing.
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(OtherFocusedConnection, ChannelId, "Viewer#2");
        var engine = NewEngine(harness, focusRegistry);

        // Simulate AuthorConnection's SendAsync throwing (e.g. its connection was torn down mid-loop),
        // via the harness's mock client for that connectionId.
        harness.ThrowOnSend(AuthorConnection);

        // Must not throw: a single recipient's failed send is fault-isolated inside OnMessagePersisted,
        // never propagating up to the already-succeeded persist/ack in SendMessage. Awaiting directly
        // (rather than via Assert.DoesNotThrowAsync) means an unhandled exception here fails the test
        // with the real stack trace.
        await engine.OnMessagePersisted(Channel(), Message(), AuthorConnection, isShadow: false, Now);

        // The failing connection recorded no signal (its SendAsync faulted before capture)...
        Assert.AreEqual(0, harness.SignalCount(AuthorConnection, ChatEvents.MessageReceived));
        // ...but the OTHER focused connection still received its full MessageReceived push.
        Assert.AreEqual(1, harness.SignalCount(OtherFocusedConnection, ChatEvents.MessageReceived));
        var dto = harness.PayloadFor(OtherFocusedConnection, ChatEvents.MessageReceived) as MessageDto;
        Assert.IsNotNull(dto);
        Assert.AreEqual("message-1", dto.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // C4 (Task 5, D8) moderator-flagged LIVE delivery. A shadow post still reaches nobody but its own
    // author (the illusion echo) AND now every FOCUSED moderator — who receives it REAL-flagged (via
    // MessageDto.ForModerator, Shadow == true). The author-echo branch outranks the moderator branch, so
    // a shadow author who is themselves a moderator still gets the unflagged echo. Focused-only still
    // holds for moderators; a focused NON-moderator still receives NOTHING; and a shadow message still
    // routes ZERO activity (the coalescer guard is untouched). Resolution is in-memory (SessionRegistry).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Shadow_FocusedModerator_ReceivesFlaggedMessageReceived()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // The shadow author AND a focused moderator (a DIFFERENT connection) are both on the channel.
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(ModeratorConnection, ChannelId, ModeratorBattleTag);
        var sessions = SessionsWith((ModeratorConnection, ModeratorBattleTag, true));
        var engine = NewEngine(harness, focusRegistry, sessions);

        await engine.OnMessagePersisted(Channel(), Message(shadowFlag: true), AuthorConnection, isShadow: true, Now);

        // The focused moderator receives the shadow post with the REAL shadow flag (ForModerator).
        Assert.AreEqual(1, harness.SignalCount(ModeratorConnection, ChatEvents.MessageReceived));
        var dto = harness.PayloadFor(ModeratorConnection, ChatEvents.MessageReceived) as MessageDto;
        Assert.IsNotNull(dto, "a focused moderator must receive the shadow MessageReceived");
        Assert.IsTrue(dto.Shadow, "a focused moderator sees the REAL shadow flag, not the illusion");
    }

    [Test]
    public async Task Shadow_FocusedNonModerator_StillReceivesNothing()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // The shadow author AND a focused NON-moderator (a registered plain user) are both on the channel.
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(OtherFocusedConnection, ChannelId, "Viewer#2");
        var sessions = SessionsWith((OtherFocusedConnection, "Viewer#2", false));
        var engine = NewEngine(harness, focusRegistry, sessions);

        await engine.OnMessagePersisted(Channel(), Message(shadowFlag: true), AuthorConnection, isShadow: true, Now);

        // The C3 pin: a focused NON-moderator receives ZERO signals for a shadow post — the moderator
        // branch must never leak a shadow message to an ordinary focused member.
        Assert.AreEqual(0, harness.SignalCount(OtherFocusedConnection, ChatEvents.MessageReceived));
        // Sanity: fan-out ran — the author still got their own (illusion) echo.
        Assert.AreEqual(1, harness.SignalCount(AuthorConnection, ChatEvents.MessageReceived));
    }

    [Test]
    public async Task Shadow_AuthorEcho_StillForcedFalse_EvenIfAuthorIsModerator()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // The shadow author is THEMSELVES a moderator, focused on the channel.
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        var sessions = SessionsWith((AuthorConnection, AuthorBattleTag, true));
        var engine = NewEngine(harness, focusRegistry, sessions);

        await engine.OnMessagePersisted(Channel(), Message(shadowFlag: true), AuthorConnection, isShadow: true, Now);

        // Illusion outranks the flag: the author's OWN echo is forced non-shadow even though the author
        // is a moderator (the author-echo branch must be evaluated BEFORE the moderator branch).
        Assert.AreEqual(1, harness.SignalCount(AuthorConnection, ChatEvents.MessageReceived));
        var dto = harness.PayloadFor(AuthorConnection, ChatEvents.MessageReceived) as MessageDto;
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.Shadow, "a shadow author's own echo is forced false even when the author is a moderator");
    }

    [Test]
    public async Task Shadow_UnfocusedModerator_ReceivesNothing()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // The author is focused; the moderator has a live session but is NOT focused on the channel.
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        var sessions = SessionsWith((ModeratorConnection, ModeratorBattleTag, true));
        var engine = NewEngine(harness, focusRegistry, sessions);

        await engine.OnMessagePersisted(Channel(), Message(shadowFlag: true), AuthorConnection, isShadow: true, Now);

        // Focused-only holds for moderators too: an UNFOCUSED moderator is absent from the focused index,
        // so it is never a delivery target — the moderator flag never reaches a non-viewer.
        Assert.AreEqual(0, harness.SignalCount(ModeratorConnection, ChatEvents.MessageReceived));
    }

    [Test]
    public async Task Shadow_ModeratorDelivery_GeneratesNoActivityOffers()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        var sessions = SessionsWith((ModeratorConnection, ModeratorBattleTag, true));
        // Explicit construction so we can seed the OnlineMemberRegistry the engine actually uses: an
        // UNFOCUSED level-All member who, for a NON-shadow message, WOULD receive a ChannelActivity.
        var members = new OnlineMemberRegistry();
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, members, new ActivityCoalescer(harness.HubContext, members), sessions);

        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(ModeratorConnection, ChannelId, ModeratorBattleTag);
        members.Join(ChannelId, UnfocusedMemberConnection, new MemberState("Bystander#9", NotificationLevel.All, 0));

        await engine.OnMessagePersisted(Channel(), Message(shadowFlag: true), AuthorConnection, isShadow: true, Now);

        // The moderator still got the flagged live message...
        Assert.AreEqual(1, harness.SignalCount(ModeratorConnection, ChatEvents.MessageReceived));
        // ...but a shadow message generates ZERO ChannelActivity for anyone (coalescer guard untouched),
        // even now that a moderator receives it live.
        Assert.AreEqual(0, harness.SignalCount(UnfocusedMemberConnection, ChatEvents.ChannelActivity));
        Assert.IsFalse(harness.AllSignals.Any(s => s.Method == ChatEvents.ChannelActivity),
            "a shadow message must generate zero ChannelActivity offers, even with a moderator focused");
    }

    [Test]
    public async Task NonShadow_ModeratorReceivesPlainForUserDelivery()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        focusRegistry.Focus(ModeratorConnection, ChannelId, ModeratorBattleTag);
        var sessions = SessionsWith((ModeratorConnection, ModeratorBattleTag, true));
        var engine = NewEngine(harness, focusRegistry, sessions);

        // A NON-shadow send. The persisted domain flags are deliberately set (Shadow + a Deleted marker)
        // so ForUserDelivery and ForModerator would project DIFFERENTLY — a moderator receiving the plain
        // ForUserDelivery reads both as false, proving the non-shadow path never routes through the flag.
        var flaggedDomain = Message(shadowFlag: true, deletion: new MessageDeletion { By = "mod#1", At = DateTime.UtcNow });
        await engine.OnMessagePersisted(Channel(), flaggedDomain, AuthorConnection, isShadow: false, Now);

        Assert.AreEqual(1, harness.SignalCount(ModeratorConnection, ChatEvents.MessageReceived));
        var dto = harness.PayloadFor(ModeratorConnection, ChatEvents.MessageReceived) as MessageDto;
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.Shadow, "a non-shadow live message is delivered plain (ForUserDelivery) even to a moderator");
        Assert.IsFalse(dto.Deleted, "a non-shadow live message is delivered plain (ForUserDelivery) even to a moderator");
    }

    // ---------------------------------------------------------------------------------------------
    // C4 (Task 3) PushMessageDeleted — the moderation removal emit helper (D4). Delivers the FINAL
    // channel-scoped MessageDeletedDto to the channel's FOCUSED connections, MINUS the excluded set
    // (the moderated author's own connections, computed by the hub). Mirrors OnMessagePersisted's
    // focused-only targeting + per-recipient fault isolation.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task PushMessageDeleted_DeliversChannelScopedDto_ToFocusedConnections_ExceptExcluded()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // The moderated author AND a viewer are both focused on the channel; a third connection is a
        // member but NOT focused (absent from the focused index entirely).
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(OtherFocusedConnection, ChannelId, "Viewer#2");
        var engine = NewEngine(harness, focusRegistry);

        await engine.PushMessageDeleted(ChannelId, "message-1", new[] { AuthorConnection });

        // The focused viewer receives exactly one channel-scoped MessageDeletedDto.
        Assert.AreEqual(1, harness.SignalCount(OtherFocusedConnection, ChatEvents.MessageDeleted));
        var dto = harness.PayloadFor(OtherFocusedConnection, ChatEvents.MessageDeleted) as MessageDeletedDto;
        Assert.IsNotNull(dto, "a focused viewer must receive a MessageDeletedDto payload");
        Assert.AreEqual(ChannelId, dto.ChannelId);
        Assert.AreEqual("message-1", dto.MessageId);

        // The excluded (author) connection is skipped — the moderated user is not tipped off live.
        Assert.AreEqual(0, harness.SignalCount(AuthorConnection, ChatEvents.MessageDeleted));
        // An unfocused connection never receives the removal (it never received the message either).
        Assert.AreEqual(0, harness.SignalCount(UnfocusedMemberConnection, ChatEvents.MessageDeleted));
    }

    [Test]
    public async Task PushMessageDeleted_OneRecipientSendThrows_OthersStillReceive_NoExceptionPropagates()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // Two focused connections, neither excluded — isolates the fault-tolerance behavior.
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(OtherFocusedConnection, ChannelId, "Viewer#2");
        var engine = NewEngine(harness, focusRegistry);

        // AuthorConnection's SendAsync faults (e.g. its connection was torn down mid-loop).
        harness.ThrowOnSend(AuthorConnection);

        // Must not throw: a single recipient's failed send is fault-isolated inside PushMessageDeleted,
        // never propagating up to the hub's already-committed soft-delete + audit.
        await engine.PushMessageDeleted(ChannelId, "message-1", Array.Empty<string>());

        // The failing connection recorded no signal...
        Assert.AreEqual(0, harness.SignalCount(AuthorConnection, ChatEvents.MessageDeleted));
        // ...but the OTHER focused connection still received its removal push.
        Assert.AreEqual(1, harness.SignalCount(OtherFocusedConnection, ChatEvents.MessageDeleted));
    }

    // ---------------------------------------------------------------------------------------------
    // C4 (Task 4) PushBulkMessagesDeleted — the moderator-purge removal emit helper (D6). Mirrors
    // PushMessageDeleted exactly, but carries a BATCH of message ids as a channel-scoped
    // BulkMessagesDeletedDto to the channel's FOCUSED connections, MINUS the excluded set (the purge
    // target's own connections, computed by the hub). Focused-only targeting + per-recipient fault
    // isolation; a channel with no focused viewers emits nothing.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task PushBulkMessagesDeleted_DeliversChannelScopedDto_ToFocusedConnections_ExceptExcluded()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // The purge target AND a viewer are both focused on the channel; a third connection is a member
        // but NOT focused (absent from the focused index entirely).
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(OtherFocusedConnection, ChannelId, "Viewer#2");
        var engine = NewEngine(harness, focusRegistry);

        var messageIds = new[] { "message-1", "message-2" };
        await engine.PushBulkMessagesDeleted(ChannelId, messageIds, new[] { AuthorConnection });

        // The focused viewer receives exactly one channel-scoped BulkMessagesDeletedDto with all ids.
        Assert.AreEqual(1, harness.SignalCount(OtherFocusedConnection, ChatEvents.BulkMessagesDeleted));
        var dto = harness.PayloadFor(OtherFocusedConnection, ChatEvents.BulkMessagesDeleted) as BulkMessagesDeletedDto;
        Assert.IsNotNull(dto, "a focused viewer must receive a BulkMessagesDeletedDto payload");
        Assert.AreEqual(ChannelId, dto.ChannelId);
        CollectionAssert.AreEqual(messageIds, dto.MessageIds.ToArray());

        // The excluded (target) connection is skipped — the purged user is not tipped off live.
        Assert.AreEqual(0, harness.SignalCount(AuthorConnection, ChatEvents.BulkMessagesDeleted));
        // An unfocused connection never receives the removal (it never received the messages either).
        Assert.AreEqual(0, harness.SignalCount(UnfocusedMemberConnection, ChatEvents.BulkMessagesDeleted));
    }

    [Test]
    public async Task PushBulkMessagesDeleted_NoFocusedViewers_EmitsNothing()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // Nobody is focused on the channel — the purge produced eligible ids here but no live viewer.
        var engine = NewEngine(harness, focusRegistry);

        await engine.PushBulkMessagesDeleted(ChannelId, new[] { "message-1" }, Array.Empty<string>());

        Assert.IsEmpty(harness.AllSignals, "a channel with no focused viewers must emit no BulkMessagesDeleted event");
    }

    [Test]
    public async Task PushBulkMessagesDeleted_OneRecipientSendThrows_OthersStillReceive_NoExceptionPropagates()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // Two focused connections, neither excluded — isolates the fault-tolerance behavior.
        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(OtherFocusedConnection, ChannelId, "Viewer#2");
        var engine = NewEngine(harness, focusRegistry);

        // AuthorConnection's SendAsync faults (e.g. its connection was torn down mid-loop).
        harness.ThrowOnSend(AuthorConnection);

        // Must not throw: a single recipient's failed send is fault-isolated inside PushBulkMessagesDeleted,
        // never propagating up to the hub's already-committed bulk soft-delete + audit.
        await engine.PushBulkMessagesDeleted(ChannelId, new[] { "message-1" }, Array.Empty<string>());

        // The failing connection recorded no signal...
        Assert.AreEqual(0, harness.SignalCount(AuthorConnection, ChatEvents.BulkMessagesDeleted));
        // ...but the OTHER focused connection still received its removal push.
        Assert.AreEqual(1, harness.SignalCount(OtherFocusedConnection, ChatEvents.BulkMessagesDeleted));
    }

    // ---------------------------------------------------------------------------------------------
    // C5 (Task 4, D4) pending-Dm activity suppression. While a 1:1 request is unresolved (Pending) the
    // RECIPIENT (any member != RequestInitiatedBy) receives ZERO ChannelActivity — their only signals are
    // the targeted RequestReceived + the tray. The initiator's own delivery is unaffected, an ACCEPTED Dm
    // resumes activity, and a recipient who DELIBERATELY focused the pending window still gets the live
    // MessageReceived (focused delivery is never suppressed).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task PendingDm_RecipientGetsNoActivity_InitiatorEchoUnaffected()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // The initiator (sender) is focused → still receives its own echo. The recipient is an unfocused
        // level-All member who, for a NON-pending channel, WOULD be offered a ChannelActivity.
        focusRegistry.Focus(InitiatorConnection, ChannelId, DmInitiator);
        var members = new OnlineMemberRegistry();
        members.Join(ChannelId, InitiatorConnection, new MemberState(DmInitiator, NotificationLevel.All, 0));
        members.Join(ChannelId, RecipientConnection, new MemberState(DmRecipient, NotificationLevel.All, 0));
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, members, new ActivityCoalescer(harness.HubContext, members), new SessionRegistry());

        await engine.OnMessagePersisted(DmChannel(DmRequestState.Pending), Message(), InitiatorConnection, isShadow: false, Now);

        // The initiator's focused connection still gets its echo — suppression targets only the recipient.
        Assert.AreEqual(1, harness.SignalCount(InitiatorConnection, ChatEvents.MessageReceived));
        // The pending recipient gets ZERO activity (D4) and no full payload (unfocused).
        Assert.AreEqual(0, harness.SignalCount(RecipientConnection, ChatEvents.ChannelActivity));
        Assert.AreEqual(0, harness.SignalCount(RecipientConnection, ChatEvents.MessageReceived));
        Assert.IsFalse(harness.AllSignals.Any(s => s.Method == ChatEvents.ChannelActivity),
            "a pending Dm recipient receives zero ChannelActivity while the request is unresolved");
    }

    [Test]
    public async Task AcceptedDm_RecipientGetsActivity()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        var members = new OnlineMemberRegistry();
        // An unfocused level-All recipient of an ACCEPTED Dm — suppression is lifted, so activity resumes.
        members.Join(ChannelId, RecipientConnection, new MemberState(DmRecipient, NotificationLevel.All, 0));
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, members, new ActivityCoalescer(harness.HubContext, members), new SessionRegistry());

        await engine.OnMessagePersisted(DmChannel(DmRequestState.Accepted), Message(), InitiatorConnection, isShadow: false, Now);

        // Accepted → no suppression → the first offer emits a ChannelActivity immediately.
        Assert.AreEqual(1, harness.SignalCount(RecipientConnection, ChatEvents.ChannelActivity));
    }

    [Test]
    public async Task FocusedPendingRecipient_StillGetsMessageReceived()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        // The recipient DELIBERATELY opened (focused) the pending window.
        focusRegistry.Focus(RecipientConnection, ChannelId, DmRecipient);
        var members = new OnlineMemberRegistry();
        members.Join(ChannelId, RecipientConnection, new MemberState(DmRecipient, NotificationLevel.All, 0));
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, members, new ActivityCoalescer(harness.HubContext, members), new SessionRegistry());

        await engine.OnMessagePersisted(DmChannel(DmRequestState.Pending), Message(), InitiatorConnection, isShadow: false, Now);

        // Focused delivery is NEVER suppressed — the recipient sees the live message.
        Assert.AreEqual(1, harness.SignalCount(RecipientConnection, ChatEvents.MessageReceived));
    }
}
