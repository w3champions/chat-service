using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using W3ChampionsChatService.Chats;

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
