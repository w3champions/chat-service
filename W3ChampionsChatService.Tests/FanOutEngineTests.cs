using System;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;

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
    private const string AuthorBattleTag = "Author#1";

    private static ChatChannel Channel() =>
        new ChatChannel { Id = ChannelId, Type = ChannelType.Public };

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
        var engine = new FanOutEngine(harness.HubContext, focusRegistry);

        await engine.OnMessagePersisted(Channel(), Message(), AuthorConnection, isShadow: false);

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
        var engine = new FanOutEngine(harness.HubContext, focusRegistry);

        await engine.OnMessagePersisted(Channel(), Message(), AuthorConnection, isShadow: false);

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
        var engine = new FanOutEngine(harness.HubContext, focusRegistry);

        // A shadow author's own echo: the persisted message is flagged Shadow AND has a Deleted marker,
        // yet the user-facing DTO must FORCE both false so the author never learns they are shadow-banned.
        var flagged = Message(
            shadowFlag: true,
            deletion: new MessageDeletion { By = "moderator#1", At = DateTime.UtcNow });

        await engine.OnMessagePersisted(Channel(), flagged, AuthorConnection, isShadow: true);

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
        var engine = new FanOutEngine(harness.HubContext, focusRegistry);

        await engine.OnMessagePersisted(Channel(), Message(shadowFlag: true), AuthorConnection, isShadow: true);

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
        var engine = new FanOutEngine(harness.HubContext, focusRegistry);

        await engine.OnMessagePersisted(Channel(), Message(shadowFlag: true), AuthorConnection, isShadow: true);

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
        var engine = new FanOutEngine(harness.HubContext, focusRegistry);

        await engine.OnMessagePersisted(Channel(), Message(), AuthorConnection, isShadow: false);

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
        var engine = new FanOutEngine(harness.HubContext, focusRegistry);

        // Simulate AuthorConnection's SendAsync throwing (e.g. its connection was torn down mid-loop),
        // via the harness's mock client for that connectionId.
        harness.ThrowOnSend(AuthorConnection);

        // Must not throw: a single recipient's failed send is fault-isolated inside OnMessagePersisted,
        // never propagating up to the already-succeeded persist/ack in SendMessage. Awaiting directly
        // (rather than via Assert.DoesNotThrowAsync) means an unhandled exception here fails the test
        // with the real stack trace.
        await engine.OnMessagePersisted(Channel(), Message(), AuthorConnection, isShadow: false);

        // The failing connection recorded no signal (its SendAsync faulted before capture)...
        Assert.AreEqual(0, harness.SignalCount(AuthorConnection, ChatEvents.MessageReceived));
        // ...but the OTHER focused connection still received its full MessageReceived push.
        Assert.AreEqual(1, harness.SignalCount(OtherFocusedConnection, ChatEvents.MessageReceived));
        var dto = harness.PayloadFor(OtherFocusedConnection, ChatEvents.MessageReceived) as MessageDto;
        Assert.IsNotNull(dto);
        Assert.AreEqual("message-1", dto.Id);
    }
}
