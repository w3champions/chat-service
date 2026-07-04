using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C3 (Task 19) hard-cutover guardrail. Asserts the OLD chat protocol surface is fully gone from
/// <see cref="ChatHub"/> after the cutover — a reflection-only regression fence so no future edit can
/// silently reintroduce a legacy method. The NEW two-arg <c>SendMessage(channelId, content)</c> and the
/// moderation trio (DeleteMessage/PurgeMessagesFromUser/BanUser) are deliberately NOT asserted-away here.
/// </summary>
[TestFixture]
public class OldProtocolRemovedTests
{
    private static readonly Type HubType = typeof(ChatHub);

    [Test]
    public void OldProtocol_MethodsAreGone()
    {
        // Public legacy methods must not exist.
        Assert.That(PublicMethod("SwitchRoom"), Is.Null, "Legacy public SwitchRoom must be deleted.");
        Assert.That(PublicMethod("UpdateUserProfilePicture"), Is.Null,
            "Legacy public UpdateUserProfilePicture must be deleted.");

        // The OLD single-string-arg SendMessage(string) overload must be gone; the NEW
        // SendMessage(string channelId, string content) overload must remain.
        var sendOverloads = HubType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "SendMessage")
            .ToList();

        Assert.That(
            sendOverloads.Any(m => IsSingleStringOverload(m)),
            Is.False,
            "Legacy single-string SendMessage(string) overload must be deleted.");
        Assert.That(
            sendOverloads.Any(m => IsTwoStringOverload(m)),
            Is.True,
            "New SendMessage(string channelId, string content) overload must remain.");

        // Non-public legacy methods must not exist.
        Assert.That(NonPublicMethod("LoginAsAuthenticated"), Is.Null,
            "Legacy internal LoginAsAuthenticated must be deleted.");
        Assert.That(NonPublicMethod("ProcessChatCommand"), Is.Null,
            "Legacy private ProcessChatCommand must be deleted.");
    }

    /// <summary>
    /// C3 (Task 21) hub-surface guardrail. Complements <see cref="OldProtocol_MethodsAreGone"/> (which
    /// asserts specific LEGACY methods are gone) with a POSITIVE inventory: the full set of public
    /// methods PHYSICALLY DECLARED on <see cref="ChatHub"/> — <see cref="BindingFlags.DeclaredOnly"/>
    /// excludes members merely INHERITED from <see cref="Microsoft.AspNetCore.SignalR.Hub"/> (e.g. the
    /// Clients/Context/Groups property accessors), so this only sees what ChatHub itself contributes.
    /// A partial class compiles into ONE type, so this sees every method across all three
    /// declaration files (ChatHub.cs, ChatHub.Channels.cs, ChatHub.Messaging.cs) together. Asserts the
    /// name set is EXACTLY: the eight new-protocol client→server methods (Task 9-13/16-17) + the three
    /// kept legacy moderation methods (DeleteMessage/PurgeMessagesFromUser/BanUser, still
    /// [UserHasPermission(Moderation)]-gated) + the two Hub lifecycle overrides
    /// (OnConnectedAsync/OnDisconnectedAsync, `public override` — client-callable indirectly via the
    /// SignalR connection lifecycle, not by name, but still part of the public surface). Also asserts
    /// the RAW count matches the expected count (not just the deduped name set) so a same-named
    /// overload sneaking back in (e.g. a second SendMessage) fails loudly too. A future accidental
    /// public method addition OR removal on ChatHub fails this test.
    /// </summary>
    [Test]
    public void HubSurface_ExactlyMatchesPinnedSet()
    {
        var declaredPublicMethods = HubType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // excludes property/event accessors; ChatHub declares none
            .ToList();

        var expected = new HashSet<string>
        {
            // Hub lifecycle overrides (public override, physically declared in ChatHub.cs)
            nameof(ChatHub.OnConnectedAsync),
            nameof(ChatHub.OnDisconnectedAsync),
            // New protocol (C3): FocusChannel/UnfocusChannel (ChatHub.Channels.cs), JoinChannel/
            // LeaveChannel/SetNotificationLevel (ChatHub.Channels.cs), SendMessage(channelId, content)/
            // GetMessages/MarkRead (ChatHub.Messaging.cs).
            "FocusChannel",
            "UnfocusChannel",
            "JoinChannel",
            "LeaveChannel",
            "SetNotificationLevel",
            "SendMessage",
            "GetMessages",
            "MarkRead",
            // Legacy moderation trio (kept, ChatHub.cs)
            "DeleteMessage",
            "PurgeMessagesFromUser",
            "BanUser",
        };

        var actualNames = declaredPublicMethods.Select(m => m.Name).ToHashSet();

        Assert.That(actualNames, Is.EquivalentTo(expected),
            "ChatHub's public client-callable method surface must be EXACTLY the pinned new-protocol " +
            "set + the legacy moderation trio + the two Hub lifecycle overrides. A diff here means a " +
            "method was added or removed without updating this guardrail (and very likely without " +
            "updating the client contract too).");
        Assert.That(declaredPublicMethods.Count, Is.EqualTo(expected.Count),
            "no method NAME above should have more than one overload — a same-named overload " +
            "sneaking back in (e.g. a second SendMessage) would collapse into the same set entry " +
            "above and hide behind this raw-count check instead.");
    }

    /// <summary>
    /// C4 Task 9 (final sweep) guardrail. Message deletion in this service is TTL-only physical
    /// removal — moderator delete/purge only ever soft-deletes (sets <c>Deleted{By,At}</c> via
    /// <see cref="MessageRepository.MarkDeleted"/> / <see cref="MessageRepository.MarkDeletedMany"/>).
    /// This is a reflection pin on <see cref="MessageRepository"/>'s PUBLIC surface: no method whose
    /// name contains a delete/remove/drop verb is allowed to exist EXCEPT the two known soft-delete
    /// methods. A future public <c>DeleteMessagePermanently</c> (or similarly named hard-delete API)
    /// added to <see cref="MessageRepository"/> fails this test loudly instead of silently
    /// reintroducing a physical-delete path that TTL/retention assumptions don't expect.
    /// </summary>
    [Test]
    public void ModerationNeverHardDeletes()
    {
        var allowedSoftDeleteNames = new HashSet<string> { "MarkDeleted", "MarkDeletedMany" };

        var suspiciousMethods = typeof(MessageRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => ContainsDeleteVerb(m.Name))
            .Where(m => !allowedSoftDeleteNames.Contains(m.Name))
            .Select(m => m.Name)
            .ToList();

        Assert.That(suspiciousMethods, Is.Empty,
            "MessageRepository must expose NO message hard-delete API. Found suspicious public " +
            $"method(s): [{string.Join(", ", suspiciousMethods)}]. Physical message removal is " +
            "TTL-only (ExpiresAt) — use MarkDeleted/MarkDeletedMany (soft-delete, $set) instead.");
    }

    private static bool ContainsDeleteVerb(string methodName) =>
        methodName.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
        methodName.Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
        methodName.Contains("Drop", StringComparison.OrdinalIgnoreCase);

    private static MethodInfo PublicMethod(string name) =>
        HubType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);

    private static MethodInfo NonPublicMethod(string name) =>
        HubType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool IsSingleStringOverload(MethodInfo m)
    {
        var p = m.GetParameters();
        return p.Length == 1 && p[0].ParameterType == typeof(string);
    }

    private static bool IsTwoStringOverload(MethodInfo m)
    {
        var p = m.GetParameters();
        return p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string);
    }
}
