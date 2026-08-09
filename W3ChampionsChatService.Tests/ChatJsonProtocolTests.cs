using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using NUnit.Framework;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Pins the hub's JSON payload contract. Flair is the dominant payload on the wire (a roster can be
/// several hundred entries, each carrying a ChatProfile), and most users have null clan/colour/icons
/// and null rank — with nulls serialized, the KEY NAMES alone dominate the payload. Null omission is
/// therefore load-bearing for payload size, not cosmetic.
/// </summary>
public class ChatJsonProtocolTests
{
    private static JsonSerializerOptions ConfiguredOptions()
    {
        var options = new JsonHubProtocolOptions();
        ChatJsonProtocol.Configure(options);
        return options.PayloadSerializerOptions;
    }

    [Test]
    public void Configure_OmitsNullFlairFields()
    {
        var viewer = new ChannelViewerDto("peter#123", "Peter", new ChatProfile
        {
            ProfilePicture = new ProfilePicture { Race = AvatarCategory.HU, PictureId = 2, IsClassic = false },
        });

        var json = JsonSerializer.Serialize(viewer, ConfiguredOptions());

        Assert.That(json, Does.Not.Contain("clanId"), "A null clanId must not occupy wire bytes");
        Assert.That(json, Does.Not.Contain("chatColor"));
        Assert.That(json, Does.Not.Contain("chatIcons"));
        Assert.That(json, Does.Not.Contain("leagueName"));
        Assert.That(json, Does.Not.Contain("gamesPlayed"));
    }

    [Test]
    public void Configure_PreservesNonNullFields()
    {
        var viewer = new ChannelViewerDto("peter#123", "Peter", new ChatProfile
        {
            ClanId = "W3C",
            ProfilePicture = new ProfilePicture { Race = AvatarCategory.NE, PictureId = 7, IsClassic = true },
            ChatColor = new ChatColor("chat_color_purple"),
        });

        var json = JsonSerializer.Serialize(viewer, ConfiguredOptions());

        Assert.That(json, Does.Contain("\"clanId\":\"W3C\""));
        Assert.That(json, Does.Contain("\"pictureId\":7"));
        Assert.That(json, Does.Contain("\"isClassic\":true"));
        Assert.That(json, Does.Contain("chat_color_purple"));
    }

    [Test]
    public void Configure_OmittingNullsDoesNotDropFalseOrZero()
    {
        // WhenWritingNull must not be confused with WhenWritingDefault: `isClassic: false` and
        // `pictureId: 0` are MEANINGFUL values the client renders, not absences.
        var viewer = new ChannelViewerDto("peter#123", "Peter", new ChatProfile
        {
            ProfilePicture = new ProfilePicture { Race = AvatarCategory.RnD, PictureId = 0, IsClassic = false },
        });

        var json = JsonSerializer.Serialize(viewer, ConfiguredOptions());

        Assert.That(json, Does.Contain("\"pictureId\":0"));
        Assert.That(json, Does.Contain("\"isClassic\":false"));
        Assert.That(json, Does.Contain("\"race\":0"));
    }
}
