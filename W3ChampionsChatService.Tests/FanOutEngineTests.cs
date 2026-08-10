using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
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
        // The accumulator's resolver shares the SAME sessionRegistry the caller passed in (SessionsWith(...)
        // for the moderator-flagged tests, or a throwaway from the 2-arg overload) — no ConnectionMapping
        // exists in this pure in-memory fixture, so flair is always null; no test here asserts on it.
        return new FanOutEngine(harness.HubContext, focusRegistry, onlineMemberRegistry, coalescer, sessionRegistry, new PresenceInterestRegistry(), new ViewersAccumulator(harness.HubContext, focusRegistry, new ViewerResolver(sessionRegistry, new ConnectionMapping())), TimeProvider.System);
    }

    // Post-game chat Plan A Task 6 overload: the match-channel preview tests need to Join members into
    // the OnlineMemberRegistry themselves (a different channel/member combination per test), but neither
    // existing NewEngine overload exposes the registry it builds — each constructs it as a private local.
    // This overload takes a CALLER-OWNED OnlineMemberRegistry instead of constructing one, so a test can
    // seed it (via Join) both before and after this call returns. It also diverges from the ISessionRegistry
    // overload above in a second way: the ViewersAccumulator here is built with
    // ViewersAccumulatorTestFactory.EmptyViewerResolver() rather than a real ViewerResolver over that
    // overload's SessionRegistry — safe because no test in this group asserts on viewers or flair, only
    // on activity/preview.
    private static FanOutEngine NewEngine(HubPushCaptureHarness harness, FocusRegistry focusRegistry, OnlineMemberRegistry onlineMemberRegistry) =>
        new FanOutEngine(
            harness.HubContext,
            focusRegistry,
            onlineMemberRegistry,
            new ActivityCoalescer(harness.HubContext, onlineMemberRegistry),
            new SessionRegistry(),
            new PresenceInterestRegistry(),
            new ViewersAccumulator(harness.HubContext, focusRegistry, ViewersAccumulatorTestFactory.EmptyViewerResolver()),
            TimeProvider.System);

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

    private static ChannelMessage Message(bool shadowFlag = false, MessageDeletion deletion = null, string content = "hello world") =>
        new ChannelMessage
        {
            Id = "message-1",
            ChannelId = ChannelId,
            Seq = 42,
            Sender = new MessageSender { BattleTag = AuthorBattleTag, Name = "Author" },
            Content = content,
            SentAt = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc),
            // These domain flags are deliberately set on some tests to prove the DTO FORCES both false
            // for user-facing delivery regardless of the persisted value.
            Shadow = shadowFlag,
            Deleted = deletion,
        };

    // C5 (Task 9, D15) DM activity preview fixture helper: a full engine wired to fresh registries, with
    // ONE unfocused level-All recipient of an ACCEPTED Dm already joined — the exact recipient whose
    // coalesced ChannelActivity should carry the preview.
    private static (HubPushCaptureHarness harness, FanOutEngine engine) NewDmPreviewFixture()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        var members = new OnlineMemberRegistry();
        members.Join(ChannelId, RecipientConnection, new MemberState(DmRecipient, NotificationLevel.All, 0, ChannelType.Dm));
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, members, new ActivityCoalescer(harness.HubContext, members), new SessionRegistry(), new PresenceInterestRegistry(), new ViewersAccumulator(harness.HubContext, focusRegistry, ViewersAccumulatorTestFactory.EmptyViewerResolver()), TimeProvider.System);
        return (harness, engine);
    }

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
        // The accumulator's resolver shares the SAME sessions registry (SessionsWith(...) above) the engine
        // itself uses.
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, members, new ActivityCoalescer(harness.HubContext, members), sessions, new PresenceInterestRegistry(), new ViewersAccumulator(harness.HubContext, focusRegistry, new ViewerResolver(sessions, new ConnectionMapping())), TimeProvider.System);

        focusRegistry.Focus(AuthorConnection, ChannelId, AuthorBattleTag);
        focusRegistry.Focus(ModeratorConnection, ChannelId, ModeratorBattleTag);
        members.Join(ChannelId, UnfocusedMemberConnection, new MemberState("Bystander#9", NotificationLevel.All, 0, ChannelType.Public));

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
        members.Join(ChannelId, InitiatorConnection, new MemberState(DmInitiator, NotificationLevel.All, 0, ChannelType.Dm));
        members.Join(ChannelId, RecipientConnection, new MemberState(DmRecipient, NotificationLevel.All, 0, ChannelType.Dm));
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, members, new ActivityCoalescer(harness.HubContext, members), new SessionRegistry(), new PresenceInterestRegistry(), new ViewersAccumulator(harness.HubContext, focusRegistry, ViewersAccumulatorTestFactory.EmptyViewerResolver()), TimeProvider.System);

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
        members.Join(ChannelId, RecipientConnection, new MemberState(DmRecipient, NotificationLevel.All, 0, ChannelType.Dm));
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, members, new ActivityCoalescer(harness.HubContext, members), new SessionRegistry(), new PresenceInterestRegistry(), new ViewersAccumulator(harness.HubContext, focusRegistry, ViewersAccumulatorTestFactory.EmptyViewerResolver()), TimeProvider.System);

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
        members.Join(ChannelId, RecipientConnection, new MemberState(DmRecipient, NotificationLevel.All, 0, ChannelType.Dm));
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, members, new ActivityCoalescer(harness.HubContext, members), new SessionRegistry(), new PresenceInterestRegistry(), new ViewersAccumulator(harness.HubContext, focusRegistry, ViewersAccumulatorTestFactory.EmptyViewerResolver()), TimeProvider.System);

        await engine.OnMessagePersisted(DmChannel(DmRequestState.Pending), Message(), InitiatorConnection, isShadow: false, Now);

        // Focused delivery is NEVER suppressed — the recipient sees the live message.
        Assert.AreEqual(1, harness.SignalCount(RecipientConnection, ChatEvents.MessageReceived));
    }

    // ---------------------------------------------------------------------------------------------
    // C5 (Task 9, D15) DM activity preview. An accepted Dm message's routed ChannelActivity carries an
    // ActivityPreviewDto — sender battleTag/name REUSED from the same MessageDto the focused-delivery
    // path already built (no extra lookup), a bounded excerpt, and the channel's own class so the client
    // routes on ChannelType rather than on the slot being present. GroupDm/Public activity always
    // carries Preview: null (they are not preview-eligible), and a pending Dm still produces ZERO
    // activity at all (D4 wall re-asserted now that previews are in play — no activity means no preview
    // can leak an unsurfaced request).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task DmActivity_CarriesPreview_SenderAndExcerpt()
    {
        var (harness, engine) = NewDmPreviewFixture();

        await engine.OnMessagePersisted(DmChannel(DmRequestState.Accepted), Message(content: "hey there"), InitiatorConnection, isShadow: false, Now);

        var dto = harness.PayloadFor(RecipientConnection, ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.IsNotNull(dto, "an accepted Dm's unfocused level-All recipient must receive a ChannelActivity");
        var preview = dto.Preview as ActivityPreviewDto;
        Assert.IsNotNull(preview, "an accepted Dm's ChannelActivity must carry an ActivityPreviewDto, not a plain null");
        Assert.AreEqual(AuthorBattleTag, preview.SenderBattleTag, "the preview must reuse the persisted message's sender, not re-derive one");
        Assert.AreEqual("Author", preview.SenderName);
        Assert.AreEqual("hey there", preview.Excerpt);
        Assert.AreEqual(ChannelType.Dm, preview.ChannelType, "the preview must NAME its channel class so a client routes on the class, never on the slot's presence");
        Assert.IsNull(preview.SystemKind, "a Dm has no SystemKind — it must not be invented");
    }

    [Test]
    public async Task Excerpt_TruncatedAt120()
    {
        var (harness, engine) = NewDmPreviewFixture();
        var longContent = new string('x', ChatLimits.DmPreviewExcerptLength + 80);

        await engine.OnMessagePersisted(DmChannel(DmRequestState.Accepted), Message(content: longContent), InitiatorConnection, isShadow: false, Now);

        var preview = (harness.PayloadFor(RecipientConnection, ChatEvents.ChannelActivity) as ChannelActivityDto)?.Preview as ActivityPreviewDto;
        Assert.IsNotNull(preview);
        Assert.AreEqual(ChatLimits.DmPreviewExcerptLength, preview.Excerpt.Length, "content over the cap must be truncated to exactly DmPreviewExcerptLength chars");
        Assert.AreEqual(longContent.Substring(0, ChatLimits.DmPreviewExcerptLength), preview.Excerpt, "the excerpt must be the first N chars — no word-boundary trimming");

        // Content AT/UNDER the cap passes through whole, with no padding.
        var (shortHarness, shortEngine) = NewDmPreviewFixture();
        await shortEngine.OnMessagePersisted(DmChannel(DmRequestState.Accepted), Message(content: "short"), InitiatorConnection, isShadow: false, Now);
        var shortPreview = (shortHarness.PayloadFor(RecipientConnection, ChatEvents.ChannelActivity) as ChannelActivityDto)?.Preview as ActivityPreviewDto;
        Assert.IsNotNull(shortPreview);
        Assert.AreEqual("short", shortPreview.Excerpt, "content at/under the cap must pass through unchanged, with no padding");
    }

    [Test]
    public async Task Excerpt_DoesNotSplitSurrogatePair()
    {
        var (harness, engine) = NewDmPreviewFixture();
        // 119 ASCII chars + one supplementary-plane emoji (a UTF-16 surrogate pair) straddles the
        // DmPreviewExcerptLength (120) boundary: the emoji occupies UTF-16 code units 119-120. A naive
        // Substring(0, 120) would cut the pair in half, leaving a lone high surrogate at the end.
        var content = new string('x', ChatLimits.DmPreviewExcerptLength - 1) + "😀" + "trailing content that will be cut off";

        await engine.OnMessagePersisted(DmChannel(DmRequestState.Accepted), Message(content: content), InitiatorConnection, isShadow: false, Now);

        var preview = (harness.PayloadFor(RecipientConnection, ChatEvents.ChannelActivity) as ChannelActivityDto)?.Preview as ActivityPreviewDto;
        Assert.IsNotNull(preview);
        var excerpt = preview.Excerpt;

        Assert.IsFalse(char.IsHighSurrogate(excerpt[^1]), "the excerpt must never end in a lone (unpaired) high surrogate");
        for (var i = 0; i < excerpt.Length; i++)
        {
            if (char.IsHighSurrogate(excerpt[i]))
            {
                Assert.IsTrue(i + 1 < excerpt.Length && char.IsLowSurrogate(excerpt[i + 1]), $"unpaired high surrogate at index {i} — malformed UTF-16");
            }
            else if (char.IsLowSurrogate(excerpt[i]))
            {
                Assert.IsTrue(i > 0 && char.IsHighSurrogate(excerpt[i - 1]), $"unpaired low surrogate at index {i} — malformed UTF-16");
            }
        }

        // The straddling emoji is dropped WHOLE rather than split — the excerpt is 119 chars (the
        // 119 leading 'x's), not 120-with-a-broken-half.
        Assert.AreEqual(ChatLimits.DmPreviewExcerptLength - 1, excerpt.Length, "a surrogate pair straddling the boundary must be dropped whole, not split");
        Assert.AreEqual(new string('x', ChatLimits.DmPreviewExcerptLength - 1), excerpt);
    }

    [Test]
    public async Task Excerpt_Exactly120Chars_FullContentNoTruncation()
    {
        var (harness, engine) = NewDmPreviewFixture();
        var exactContent = new string('x', ChatLimits.DmPreviewExcerptLength);

        await engine.OnMessagePersisted(DmChannel(DmRequestState.Accepted), Message(content: exactContent), InitiatorConnection, isShadow: false, Now);

        var preview = (harness.PayloadFor(RecipientConnection, ChatEvents.ChannelActivity) as ChannelActivityDto)?.Preview as ActivityPreviewDto;
        Assert.IsNotNull(preview);
        Assert.AreEqual(ChatLimits.DmPreviewExcerptLength, preview.Excerpt.Length, "content exactly at the cap must pass through whole, unchanged");
        Assert.AreEqual(exactContent, preview.Excerpt, "content exactly at the cap must equal the full content — the <= boundary must not truncate");
    }

    [Test]
    public async Task GroupPublicAndSemiPublicActivity_CarryNoPreview()
    {
        const string GroupMemberConn = "conn-group-member";
        const string GroupMemberTag = "GroupMember#1";
        var groupHarness = new HubPushCaptureHarness();
        var groupFocus = new FocusRegistry();
        var groupMembers = new OnlineMemberRegistry();
        groupMembers.Join(ChannelId, GroupMemberConn, new MemberState(GroupMemberTag, NotificationLevel.All, 0, ChannelType.GroupDm));
        var groupEngine = new FanOutEngine(
            groupHarness.HubContext, groupFocus, groupMembers, new ActivityCoalescer(groupHarness.HubContext, groupMembers), new SessionRegistry(), new PresenceInterestRegistry(), new ViewersAccumulator(groupHarness.HubContext, groupFocus, ViewersAccumulatorTestFactory.EmptyViewerResolver()), TimeProvider.System);
        var groupChannel = new ChatChannel { Id = ChannelId, Type = ChannelType.GroupDm };

        await groupEngine.OnMessagePersisted(groupChannel, Message(), AuthorConnection, isShadow: false, Now);

        var groupDto = groupHarness.PayloadFor(GroupMemberConn, ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.IsNotNull(groupDto, "an unfocused level-All group member must still receive plain activity");
        Assert.IsNull(groupDto.Preview, "GroupDm is not preview-eligible — widening it is a deliberate future opt-in, not a default");

        const string PublicMemberConn = "conn-public-member";
        const string PublicMemberTag = "PublicMember#1";
        var publicHarness = new HubPushCaptureHarness();
        var publicFocus = new FocusRegistry();
        var publicMembers = new OnlineMemberRegistry();
        publicMembers.Join(ChannelId, PublicMemberConn, new MemberState(PublicMemberTag, NotificationLevel.All, 0, ChannelType.Public));
        var publicEngine = new FanOutEngine(
            publicHarness.HubContext, publicFocus, publicMembers, new ActivityCoalescer(publicHarness.HubContext, publicMembers), new SessionRegistry(), new PresenceInterestRegistry(), new ViewersAccumulator(publicHarness.HubContext, publicFocus, ViewersAccumulatorTestFactory.EmptyViewerResolver()), TimeProvider.System);

        await publicEngine.OnMessagePersisted(Channel(), Message(), AuthorConnection, isShadow: false, Now);

        var publicDto = publicHarness.PayloadFor(PublicMemberConn, ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.IsNotNull(publicDto, "an unfocused level-All public member must still receive plain activity");
        Assert.IsNull(publicDto.Preview, "a busy lounge must keep its badge-only treatment");

        const string SemiPublicMemberConn = "conn-semipublic-member";
        const string SemiPublicMemberTag = "SemiPublicMember#1";
        var semiHarness = new HubPushCaptureHarness();
        var semiFocus = new FocusRegistry();
        var semiMembers = new OnlineMemberRegistry();
        semiMembers.Join(ChannelId, SemiPublicMemberConn, new MemberState(SemiPublicMemberTag, NotificationLevel.All, 0, ChannelType.SemiPublic));
        var semiEngine = new FanOutEngine(
            semiHarness.HubContext, semiFocus, semiMembers, new ActivityCoalescer(semiHarness.HubContext, semiMembers), new SessionRegistry(), new PresenceInterestRegistry(), new ViewersAccumulator(semiHarness.HubContext, semiFocus, ViewersAccumulatorTestFactory.EmptyViewerResolver()), TimeProvider.System);
        var semiChannel = new ChatChannel { Id = ChannelId, Type = ChannelType.SemiPublic };

        await semiEngine.OnMessagePersisted(semiChannel, Message(), AuthorConnection, isShadow: false, Now);

        var semiDto = semiHarness.PayloadFor(SemiPublicMemberConn, ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.IsNotNull(semiDto, "an unfocused level-All semiPublic member must still receive plain activity");
        Assert.IsNull(semiDto.Preview, "SemiPublic is not preview-eligible either");
    }

    [Test]
    public async Task PendingDm_NoActivityHenceNoPreview()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        focusRegistry.Focus(InitiatorConnection, ChannelId, DmInitiator);
        var members = new OnlineMemberRegistry();
        members.Join(ChannelId, InitiatorConnection, new MemberState(DmInitiator, NotificationLevel.All, 0, ChannelType.Dm));
        members.Join(ChannelId, RecipientConnection, new MemberState(DmRecipient, NotificationLevel.All, 0, ChannelType.Dm));
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, members, new ActivityCoalescer(harness.HubContext, members), new SessionRegistry(), new PresenceInterestRegistry(), new ViewersAccumulator(harness.HubContext, focusRegistry, ViewersAccumulatorTestFactory.EmptyViewerResolver()), TimeProvider.System);

        await engine.OnMessagePersisted(DmChannel(DmRequestState.Pending), Message(content: "a pending message"), InitiatorConnection, isShadow: false, Now);

        // Re-asserts the D4 wall now that previews are in play: a pending Dm produces ZERO
        // ChannelActivity to the recipient — there is no activity event at all for a preview to ride on,
        // so an unsurfaced request's content can never leak via a preview.
        Assert.IsFalse(harness.AllSignals.Any(s => s.Method == ChatEvents.ChannelActivity),
            "a pending Dm must produce zero ChannelActivity — no preview can ever surface for an unsurfaced request");
    }

    // ---------------------------------------------------------------------------------------------
    // C7 (Task 5) FORCED-REMOVAL viewer-roster routing — PushChannelRemoved. Discharges the C3
    // amendment: a forced removal of a currently-FOCUSED viewer of a ROSTER-PARTICIPATING channel
    // (Public/SemiPublic/System) must emit a ViewersChanged{left} to the channel's REMAINING focused
    // viewers — routed through ViewersAccumulator.RecordChange BEFORE FocusRegistry.Unfocus (pre-window
    // baseline VIEWING), mirroring ChatHub.LeaveChannel. A PRIVATE lane (Dm/GroupDm) records nothing
    // (D11), an offline target no-ops, and an unfocused member nets to no delta. A SHARED
    // ViewersAccumulator + FakeTimeProvider + real registries drive FlushDue directly (no timers),
    // exactly like ViewersAccumulatorTests.
    // ---------------------------------------------------------------------------------------------

    private const string VictimBattleTag = "Victim#9";
    private const string VictimConnection = "conn-victim";
    private const string SurvivorBattleTag = "Survivor#3";
    private const string SurvivorConnection = "conn-survivor";
    private static readonly TimeSpan ViewersFlush = ChatLimits.ViewersChangedFlush;

    // Builds a forced-removal fixture: a SURVIVOR is always a focused viewer of ChannelId (the remaining
    // viewer that should — or, for a private lane, should NOT — receive the ViewersChanged{left}). The
    // engine's clock is a FakeTimeProvider pinned at Now, so the accumulator window opens at Now and a
    // FlushDue(Now + ViewersFlush) is due. victimChannelType is the type the D11 guard reads from the
    // victim's OnlineMemberRegistry entry; focusVictim seeds the victim into the live roster; a false
    // registerVictimOnline models an OFFLINE target (no live session ⇒ PushChannelRemoved early-returns).
    private static (HubPushCaptureHarness harness, FocusRegistry focus, OnlineMemberRegistry members, SessionRegistry sessions, ViewersAccumulator accumulator, FanOutEngine engine)
        NewForcedRemovalFixture(ChannelType victimChannelType, bool focusVictim, bool registerVictimOnline = true)
    {
        var harness = new HubPushCaptureHarness();
        var focus = new FocusRegistry();
        var members = new OnlineMemberRegistry();
        var sessions = new SessionRegistry();
        // The comment above this fixture claims "real registries drive FlushDue directly" — the resolver
        // must share this SAME sessions registry (seeded via Register just below) for that to hold, even
        // though these forced-removal assertions only check Left/Joined battleTags, not display name/flair.
        var accumulator = new ViewersAccumulator(harness.HubContext, focus, new ViewerResolver(sessions, new ConnectionMapping()));
        var time = new FakeTimeProvider(new DateTimeOffset(Now, TimeSpan.Zero));
        var engine = new FanOutEngine(
            harness.HubContext, focus, members, new ActivityCoalescer(harness.HubContext, members), sessions, new PresenceInterestRegistry(), accumulator, time);

        sessions.Register(SurvivorConnection, Identity(SurvivorBattleTag, false), null);
        members.Join(ChannelId, SurvivorConnection, new MemberState(SurvivorBattleTag, NotificationLevel.All, 0, victimChannelType));
        focus.Focus(SurvivorConnection, ChannelId, SurvivorBattleTag);

        if (registerVictimOnline)
        {
            sessions.Register(VictimConnection, Identity(VictimBattleTag, false), null);
            members.Join(ChannelId, VictimConnection, new MemberState(VictimBattleTag, NotificationLevel.All, 0, victimChannelType));
            if (focusVictim)
            {
                focus.Focus(VictimConnection, ChannelId, VictimBattleTag);
            }
        }

        return (harness, focus, members, sessions, accumulator, engine);
    }

    private static bool ContainsTag(IEnumerable<string> tags, string battleTag) =>
        tags.Any(t => string.Equals(t, battleTag, StringComparison.OrdinalIgnoreCase));

    [Test]
    public async Task PushChannelRemoved_FocusedSystemChannelViewer_EmitsViewersLeftToRemainingViewers()
    {
        var (harness, _, _, _, accumulator, engine) = NewForcedRemovalFixture(ChannelType.System, focusVictim: true);

        await engine.PushChannelRemoved(ChannelId, VictimBattleTag);
        await accumulator.FlushDue(Now + ViewersFlush);

        // The removed viewer is told to drop the channel...
        Assert.AreEqual(1, harness.SignalCount(VictimConnection, ChatEvents.ChannelRemoved));
        // ...and the REMAINING focused viewer receives a ViewersChanged reporting the victim as `left`.
        var batch = harness.PayloadFor(SurvivorConnection, ChatEvents.ViewersChanged) as ViewersChangedDto;
        Assert.IsNotNull(batch, "a forced removal of a focused System-channel viewer must emit ViewersChanged to remaining viewers");
        Assert.AreEqual(ChannelId, batch.ChannelId);
        Assert.IsTrue(ContainsTag(batch.Left, VictimBattleTag), "the removed viewer must be reported as `left`");
        Assert.IsEmpty(batch.Joined);
    }

    [Test]
    public async Task PushChannelRemoved_RecordsChangeBeforeUnfocus_BaselineIsViewing()
    {
        var (harness, focus, _, _, accumulator, engine) = NewForcedRemovalFixture(ChannelType.System, focusVictim: true);

        await engine.PushChannelRemoved(ChannelId, VictimBattleTag);

        // Ordering proof, part 1: FocusRegistry.Unfocus already ran (the victim is no longer viewing)...
        Assert.IsFalse(ContainsTag(focus.GetRoster(ChannelId), VictimBattleTag), "PushChannelRemoved must Unfocus the removed viewer");
        // ...and exactly one roster change was recorded (the victim's) — captured BEFORE that Unfocus.
        Assert.AreEqual(1, accumulator.PendingChangeCount(ChannelId), "the forced removal must record exactly one roster change");

        // Ordering proof, part 2: because RecordChange ran BEFORE Unfocus, the captured pre-window baseline
        // was VIEWING, so the flush computes a `left`. A post-Unfocus RecordChange would have captured a
        // not-viewing baseline and netted to NO delta — this `left` IS the RecordChange-before-Unfocus proof.
        await accumulator.FlushDue(Now + ViewersFlush);
        var batch = harness.PayloadFor(SurvivorConnection, ChatEvents.ViewersChanged) as ViewersChangedDto;
        Assert.IsNotNull(batch, "baseline VIEWING must yield a `left` at flush — proving RecordChange ran before Unfocus");
        Assert.IsTrue(ContainsTag(batch.Left, VictimBattleTag));
    }

    [Test]
    public async Task PushChannelRemoved_GroupDmMember_DoesNotRecordViewerChange()
    {
        var (harness, _, _, _, accumulator, engine) = NewForcedRemovalFixture(ChannelType.GroupDm, focusVictim: true);

        await engine.PushChannelRemoved(ChannelId, VictimBattleTag);
        await accumulator.FlushDue(Now + ViewersFlush);
        await accumulator.FlushDue(Now + ViewersFlush + ViewersFlush);

        // The victim still gets the ChannelRemoved + registry/focus cleanup (regression parity with the
        // live RemoveGroupMember caller)...
        Assert.AreEqual(1, harness.SignalCount(VictimConnection, ChatEvents.ChannelRemoved));
        // ...but a PRIVATE lane never enters the viewer-roster system (D11): no RecordChange, hence no
        // ViewersChanged to any remaining member.
        Assert.AreEqual(0, accumulator.PendingChangeCount(ChannelId), "a GroupDm forced removal must record no viewer change (D11 private-lane guard)");
        Assert.IsFalse(harness.AllSignals.Any(s => s.Method == ChatEvents.ViewersChanged),
            "a forced private-lane removal must emit no ViewersChanged to any remaining member");
    }

    [Test]
    public async Task PushChannelRemoved_OfflineUser_StillNoOps()
    {
        var (harness, _, _, _, accumulator, engine) = NewForcedRemovalFixture(ChannelType.System, focusVictim: false, registerVictimOnline: false);

        await engine.PushChannelRemoved(ChannelId, VictimBattleTag);
        await accumulator.FlushDue(Now + ViewersFlush);

        // Offline target (no live session) → GetByBattleTag null → early return: nothing pushed, nothing
        // recorded, the remaining viewer sees no ViewersChanged.
        Assert.IsEmpty(harness.AllSignals, "an offline forced-removal target must produce no pushes at all");
        Assert.AreEqual(0, accumulator.PendingChangeCount(ChannelId), "an offline forced removal records no viewer change");
    }

    [Test]
    public async Task PushChannelRemoved_UnfocusedMember_EmitsNoViewersChanged()
    {
        var (harness, _, _, _, accumulator, engine) = NewForcedRemovalFixture(ChannelType.System, focusVictim: false);

        await engine.PushChannelRemoved(ChannelId, VictimBattleTag);
        await accumulator.FlushDue(Now + ViewersFlush);

        // The victim still gets its ChannelRemoved...
        Assert.AreEqual(1, harness.SignalCount(VictimConnection, ChatEvents.ChannelRemoved));
        // ...but an UNFOCUSED member was never in the roster, so the current-vs-baseline delta is empty
        // (not-viewing == not-viewing): removing a non-viewer changes no roster, so no ViewersChanged fires.
        Assert.IsFalse(harness.AllSignals.Any(s => s.Method == ChatEvents.ViewersChanged),
            "removing an unfocused member emits no ViewersChanged — it was never a viewer");
    }

    // ---------------------------------------------------------------------------------------------
    // Post-game chat Plan A Task 6: match-channel activity preview. A player who closes the post-match
    // score screen quickly needs a sender + excerpt on the coalesced ChannelActivity so the client's
    // one-time nudge toast has something to render. The preview NAMES its channel class, which is what
    // lets the client tell a post-game nudge from a DM toast — routing on the slot's mere presence is
    // what broke while the payload was Dm-only, and these tests exist so nobody reintroduces it. Every
    // other non-Dm class, INCLUDING System+Clan, stays preview-free.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MatchChannelActivity_CarriesPreviewNamingSystemMatch()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        var onlineMemberRegistry = new OnlineMemberRegistry();
        var engine = NewEngine(harness, focusRegistry, onlineMemberRegistry);

        var channel = new ChatChannel
        {
            Id = "chan-match",
            Type = ChannelType.System,
            SystemKind = SystemChannelKind.Match,
        };
        // An UNFOCUSED, level-All member is the only one who receives ChannelActivity.
        onlineMemberRegistry.Join(channel.Id, "conn-bob", new MemberState("Bob#1", NotificationLevel.All, 0, ChannelType.System));

        var message = new ChannelMessage
        {
            Id = "m1",
            ChannelId = channel.Id,
            Seq = 1,
            Sender = new MessageSender { BattleTag = "Alice#1", Name = "Alice" },
            Content = "gg wp",
            SentAt = Now,
        };

        await engine.OnMessagePersisted(channel, message, senderConnectionId: "conn-alice", isShadow: false, Now);

        var activity = harness.PayloadFor("conn-bob", ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.That(activity, Is.Not.Null, "an unfocused level-All member receives coalesced activity");

        var preview = activity.Preview as ActivityPreviewDto;
        Assert.That(preview, Is.Not.Null,
            "post-game chat needs a preview so the client can raise its one-time nudge toast");
        Assert.That(preview.SenderBattleTag, Is.EqualTo("Alice#1"), "the client routes the nudge toast by battleTag, not display name — SenderBattleTag must be populated from dto.Sender.BattleTag");
        Assert.That(preview.SenderName, Is.EqualTo("Alice"), "SenderName must reuse dto.Sender.Name from the same MessageDto built for focused delivery, not a fresh lookup");
        Assert.That(preview.Excerpt, Is.EqualTo("gg wp"), "Excerpt must be the message content bounded by Excerpts.Bounded, unchanged for content under the cap");

        // THE guard against the bug the kind-carrying shape exists to prevent: without these two fields
        // a client can only infer "a preview is present, so this must be a DM" and raises a DM-grade
        // toast + chat sound + OS notification for every post-game message. Every auto-joined match
        // member is NotificationLevel.All, so that inference floods the entire lobby.
        Assert.That(preview.ChannelType, Is.EqualTo(ChannelType.System), "the client routes the nudge on the room's class, so the class must ride the preview itself — never be inferred from its presence");
        Assert.That(preview.SystemKind, Is.EqualTo(SystemChannelKind.Match), "System alone is ambiguous — SystemKind is what separates a match room from a clan room on the client");
    }

    [Test]
    public async Task PublicChannelActivity_StillCarriesNoPreview()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        var onlineMemberRegistry = new OnlineMemberRegistry();
        var engine = NewEngine(harness, focusRegistry, onlineMemberRegistry);

        var channel = new ChatChannel { Id = "chan-public", Type = ChannelType.Public };
        onlineMemberRegistry.Join(channel.Id, "conn-bob", new MemberState("Bob#1", NotificationLevel.All, 0, ChannelType.Public));

        var message = new ChannelMessage
        {
            Id = "m1",
            ChannelId = channel.Id,
            Seq = 1,
            Sender = new MessageSender { BattleTag = "Alice#1", Name = "Alice" },
            Content = "hello lounge",
            SentAt = Now,
        };

        await engine.OnMessagePersisted(channel, message, senderConnectionId: "conn-alice", isShadow: false, Now);

        var activity = harness.PayloadFor("conn-bob", ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.That(activity, Is.Not.Null, "an unfocused level-All public member still receives coalesced activity");
        Assert.That(activity.Preview, Is.Null,
            "the preview widening is scoped to match channels — a busy lounge must keep its badge-only treatment");
    }

    [Test]
    public async Task ClanChannelActivity_CarriesNoPreview()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        var onlineMemberRegistry = new OnlineMemberRegistry();
        var engine = NewEngine(harness, focusRegistry, onlineMemberRegistry);

        var channel = new ChatChannel
        {
            Id = "chan-clan",
            Type = ChannelType.System,
            SystemKind = SystemChannelKind.Clan,
        };
        onlineMemberRegistry.Join(channel.Id, "conn-bob", new MemberState("Bob#1", NotificationLevel.All, 0, ChannelType.System));

        var message = new ChannelMessage
        {
            Id = "m1",
            ChannelId = channel.Id,
            Seq = 1,
            Sender = new MessageSender { BattleTag = "Alice#1", Name = "Alice" },
            Content = "clan night",
            SentAt = Now,
        };

        await engine.OnMessagePersisted(channel, message, senderConnectionId: "conn-alice", isShadow: false, Now);

        var activity = harness.PayloadFor("conn-bob", ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.That(activity, Is.Not.Null, "an unfocused level-All clan-channel member still receives coalesced activity");
        Assert.That(activity.Preview, Is.Null,
            "the eligibility test is System AND SystemKind.Match — a clan room must not slip in on ChannelType alone");
    }

    [Test]
    public void SystemMessageInMatchChannel_ProducesNoPreview_AndDoesNotThrow()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        var onlineMemberRegistry = new OnlineMemberRegistry();
        var engine = NewEngine(harness, focusRegistry, onlineMemberRegistry);

        var channel = new ChatChannel
        {
            Id = "chan-match",
            Type = ChannelType.System,
            SystemKind = SystemChannelKind.Match,
        };
        onlineMemberRegistry.Join(channel.Id, "conn-bob", new MemberState("Bob#1", NotificationLevel.All, 0, ChannelType.System));

        var systemMessage = new ChannelMessage
        {
            Id = "m1",
            ChannelId = channel.Id,
            Seq = 1,
            Kind = MessageKind.System,
            SystemMessage = new SystemMessageBody { Key = "match_intro", FallbackText = "Match on Amazonia" },
            SentAt = Now,
        };

        Assert.DoesNotThrowAsync(async () =>
            await engine.OnMessagePersisted(channel, systemMessage, senderConnectionId: null, isShadow: false, Now),
            "a system message has a null Sender — the preview build must not dereference it");

        var activity = harness.PayloadFor("conn-bob", ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.That(activity, Is.Not.Null, "a system message still routes coalesced activity to an unfocused level-All member — only the preview is withheld");
        Assert.That(activity.Preview, Is.Null, "there is no sender to preview — the MessageKind.User conjunct is what stops the null Sender being dereferenced");
    }

    [Test]
    public async Task SystemMessageAfterUserMessage_InsideCoalesceWindow_DoesNotClearPendingPreview()
    {
        var harness = new HubPushCaptureHarness();
        var focusRegistry = new FocusRegistry();
        var onlineMemberRegistry = new OnlineMemberRegistry();
        var coalescer = new ActivityCoalescer(harness.HubContext, onlineMemberRegistry);
        var engine = new FanOutEngine(
            harness.HubContext, focusRegistry, onlineMemberRegistry, coalescer, new SessionRegistry(),
            new PresenceInterestRegistry(),
            new ViewersAccumulator(harness.HubContext, focusRegistry, ViewersAccumulatorTestFactory.EmptyViewerResolver()),
            TimeProvider.System);

        var channel = new ChatChannel { Id = "chan-match", Type = ChannelType.System, SystemKind = SystemChannelKind.Match };
        onlineMemberRegistry.Join(channel.Id, "conn-bob", new MemberState("Bob#1", NotificationLevel.All, 0, ChannelType.System));

        // #1 opens the 10s coalescing window and emits immediately.
        await engine.OnMessagePersisted(
            channel,
            new ChannelMessage { Id = "m1", ChannelId = channel.Id, Seq = 1, Sender = new MessageSender { BattleTag = "Alice#1", Name = "Alice" }, Content = "gg wp", SentAt = Now },
            senderConnectionId: "conn-alice", isShadow: false, Now);

        // #2 lands 1s later — INSIDE the window, so it coalesces into the pending rather than emitting.
        // mm may publish a system message at any instant (Plan B already names a second trigger), and a
        // system message offers a null preview. Unconditional latest-wins would blank the user message's
        // pending preview and the flush would emit a bare badge — the post-game message goes unnoticed,
        // the exact failure this feature exists to prevent.
        await engine.OnMessagePersisted(
            channel,
            new ChannelMessage { Id = "m2", ChannelId = channel.Id, Seq = 2, Kind = MessageKind.System, SystemMessage = new SystemMessageBody { Key = "match_intro", FallbackText = "Match on Amazonia" }, SentAt = Now.AddSeconds(1) },
            senderConnectionId: null, isShadow: false, Now.AddSeconds(1));

        await coalescer.FlushDue(Now + ChatLimits.ChannelActivityCoalesce);

        var flushed = harness.AllSignals
            .Where(s => s.ConnectionId == "conn-bob" && s.Method == ChatEvents.ChannelActivity)
            .Select(s => s.Payload)
            .OfType<ChannelActivityDto>()
            .Last();
        Assert.That(flushed.LastSeq, Is.EqualTo(2), "sanity: the flush is the coalesced burst's drain, carrying the latest seq");
        var preview = flushed.Preview as ActivityPreviewDto;
        Assert.That(preview, Is.Not.Null,
            "a preview-free system message inside the window must not blank the user message's pending preview");
        Assert.That(preview.Excerpt, Is.EqualTo("gg wp"), "the surviving preview must be the last one that actually HAD content");
    }
}
