using System;
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C2 (hard cutover) + D9 (C6 Task 3): <see cref="ChatAuthenticationService.GetUserFromIdentity"/>
/// enriches a ticket-proven identity via the website backend — the JWT decode is gone (it happened at
/// mint). Decision 11: because the ticket ALREADY proved identity, an enrichment failure must NEVER
/// fail a proven-authenticated connect. D9 extends this to a THREE-tier fallback chain (§14 row 1):
/// wb success (FRESH) → directory-cache restore (a prior good Profile) → plain battleTag fallback
/// (never null) — see <see cref="ChatUserResolution"/>. This file also pins the tolerant
/// <see cref="ChatDetailsDto"/> deserialization contract (today's legacy 4-field wb payload vs. the
/// full W1-shaped payload, exact field names verbatim).
/// <para>
/// Extends <see cref="IntegrationTestBase"/> (real ephemeral Testcontainers Mongo) because the
/// directory-cache fallback tier now performs real <see cref="UserDirectoryRepository"/> reads.
/// </para>
/// </summary>
public class ChatAuthenticationServiceTests : IntegrationTestBase
{
    private UserDirectoryRepository _userDirectory;

    [SetUp]
    public void SetupBeforeEach()
    {
        _userDirectory = new UserDirectoryRepository(MongoClient);
    }

    private ChatAuthenticationService BuildService(IWebsiteBackendRepository wb) =>
        new(MongoClient, wb, _userDirectory);

    [Test]
    public async Task Enrichment_WbFails_NoCache_PlainFallback()
    {
        var wb = new Mock<IWebsiteBackendRepository>();
        wb.Setup(r => r.GetChatDetails(It.IsAny<string>()))
            .ThrowsAsync(new Exception("wb outage"));
        var service = BuildService(wb.Object);
        var identity = new W3CUserAuthentication { BattleTag = "peter#123", Name = "peter", IsAdmin = false };

        var resolution = await service.GetUserFromIdentity(identity);

        Assert.IsNotNull(resolution.User, "A wb enrichment failure must NOT fail a proven-authenticated connect");
        Assert.IsFalse(resolution.FreshFromWb, "no wb success and no directory cache — this is not a fresh enrichment");
        Assert.AreEqual("peter#123", resolution.User.BattleTag, "The fallback carries the identity's battleTag");
        Assert.IsFalse(resolution.User.IsAdmin, "The fallback carries the identity's admin flag");
        Assert.IsNotNull(resolution.User.ProfilePicture, "The fallback supplies a non-null placeholder ProfilePicture");
    }

    [Test]
    public async Task Enrichment_WbFails_FallsBackToDirectoryCache_FlairRestored()
    {
        // §14 row 1 pin: a wb outage falls back to the directory cache — the LAST KNOWN GOOD Profile —
        // rather than degrading straight to the plain fallback.
        await _userDirectory.Upsert(new UserDirectoryEntry
        {
            BattleTag = "peter#123",
            DisplayBattleTag = "Peter#123",
            NormalizedName = "peter#123",
            LastSeenAt = DateTime.UtcNow.AddDays(-1),
            Profile = new ChatProfile
            {
                ClanId = "W3C",
                ChatColor = new ChatColor("chat_color_blue"),
                ChatIcons = new[] { new ChatIcon("chat_icon_star") },
                LeagueId = 3,
                LeagueName = "Diamond",
                LeagueOrder = 5,
                LeagueDivision = 2,
                RankNumber = 14,
                GameMode = 1,
                GateWay = 20,
                GamesPlayed = 42,
                Season = 22,
            },
        });

        var wb = new Mock<IWebsiteBackendRepository>();
        wb.Setup(r => r.GetChatDetails(It.IsAny<string>()))
            .ThrowsAsync(new Exception("wb outage"));
        var service = BuildService(wb.Object);
        var identity = new W3CUserAuthentication { BattleTag = "Peter#123", Name = "Peter", IsAdmin = false };

        var resolution = await service.GetUserFromIdentity(identity);

        Assert.IsFalse(resolution.FreshFromWb, "a directory-cache restore is NOT a fresh wb enrichment");
        Assert.IsNotNull(resolution.User);
        Assert.AreEqual("Peter#123", resolution.User.BattleTag);
        Assert.AreEqual("W3C", resolution.User.ClanTag, "the cached ClanId flows onto the restored ChatUser");
        Assert.AreEqual(3, resolution.User.LeagueId);
        Assert.AreEqual("Diamond", resolution.User.LeagueName);
        Assert.AreEqual(5, resolution.User.LeagueOrder);
        Assert.AreEqual(2, resolution.User.LeagueDivision);
        Assert.AreEqual(14, resolution.User.RankNumber);
        Assert.AreEqual(1, resolution.User.GameMode);
        Assert.AreEqual(20, resolution.User.GateWay);
        Assert.AreEqual(42, resolution.User.GamesPlayed);
        Assert.AreEqual(22, resolution.User.Season);
    }

    [Test]
    public async Task GetUserFromIdentity_Admin_ForcesAdminColorAndIcon()
    {
        var wb = new Mock<IWebsiteBackendRepository>();
        wb.Setup(r => r.GetChatDetails("admin#1"))
            .ReturnsAsync(new ChatDetailsDto("clan-x", new ProfilePicture(), new ChatColor("custom"),
                new[] { new ChatIcon("icon-a") }));
        var service = BuildService(wb.Object);
        var identity = new W3CUserAuthentication { BattleTag = "admin#1", Name = "admin", IsAdmin = true };

        var resolution = await service.GetUserFromIdentity(identity);

        Assert.IsTrue(resolution.FreshFromWb, "a successful wb round-trip is a fresh enrichment");
        var user = resolution.User;
        Assert.IsNotNull(user);
        Assert.AreEqual("admin#1", user.BattleTag);
        Assert.IsTrue(user.IsAdmin);
        Assert.AreEqual(ChatColor.AdminColor, user.ChatColor, "Admins are forced to the admin chat color");
        Assert.AreEqual(ChatIcon.AdminIcon, user.ChatIcons[0], "Admins get the admin icon prepended");
        Assert.AreEqual("clan-x", user.ClanTag, "The wb clan id flows through to the ChatUser");
    }

    // ── D9: tolerant ChatDetailsDto deserialization (W1 amendment — exact field names) ────────────

    [Test]
    public void ChatDetails_DeserializesLegacyPayload_EnrichmentNull()
    {
        // Today's actual wb payload — 4 legacy fields only. Must deserialize fine with every new
        // enrichment field null (the tolerant stub pin — never blocks on wb not having shipped W1 yet).
        const string json = """
        {
            "clanId": "W3C",
            "profilePicture": { "race": 1, "pictureId": 3, "isClassic": true },
            "chatColor": { "colorId": "chat_color_blue" },
            "chatIcons": [ { "iconId": "chat_icon_star" } ]
        }
        """;

        var dto = JsonConvert.DeserializeObject<ChatDetailsDto>(json);

        Assert.IsNotNull(dto);
        Assert.AreEqual("W3C", dto.ClanId);
        Assert.AreEqual("chat_color_blue", dto.ChatColor.ColorId);
        Assert.AreEqual(1, dto.ChatIcons.Length);
        Assert.IsNull(dto.Rank, "Rank must be null when wb hasn't shipped W1 yet");
        Assert.IsNull(dto.GamesPlayed, "GamesPlayed must be null, not 0 — absent means unknown, not zero");
        Assert.IsNull(dto.Season);
    }

    [Test]
    public void ChatDetails_DeserializesW1Payload_AllFields()
    {
        // The full W1-shape payload — exact field names verbatim (deliberately NOT spec §4's naming;
        // this divergence is the W1 amendment, not a bug).
        const string json = """
        {
            "clanId": "W3C",
            "profilePicture": { "race": 1, "pictureId": 3, "isClassic": true },
            "chatColor": { "colorId": "chat_color_blue" },
            "chatIcons": [ { "iconId": "chat_icon_star" } ],
            "rank": {
                "leagueId": 3,
                "leagueName": "Diamond",
                "leagueOrder": 5,
                "leagueDivision": 2,
                "rankNumber": 14,
                "gameMode": 1,
                "gateWay": 20
            },
            "gamesPlayed": 42,
            "season": 22
        }
        """;

        var dto = JsonConvert.DeserializeObject<ChatDetailsDto>(json);

        Assert.IsNotNull(dto);
        Assert.IsNotNull(dto.Rank);
        Assert.AreEqual(3, dto.Rank.LeagueId);
        Assert.AreEqual("Diamond", dto.Rank.LeagueName);
        Assert.AreEqual(5, dto.Rank.LeagueOrder);
        Assert.AreEqual(2, dto.Rank.LeagueDivision);
        Assert.AreEqual(14, dto.Rank.RankNumber);
        Assert.AreEqual(1, dto.Rank.GameMode);
        Assert.AreEqual(20, dto.Rank.GateWay);
        Assert.AreEqual(42, dto.GamesPlayed);
        Assert.AreEqual(22, dto.Season);
    }
}
