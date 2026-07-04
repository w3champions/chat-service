using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

public class UserRepositoriesTests : IntegrationTestBase
{
    [Test]
    public async Task DirectoryEntry_UpsertTwice_UpdatesInPlace()
    {
        var repo = new UserDirectoryRepository(MongoClient);
        var firstSeen = DateTime.UtcNow.AddDays(-1);
        await repo.Upsert(new UserDirectoryEntry
        {
            BattleTag = "Peter#123",
            NormalizedName = "peter#123",
            LastSeenAt = firstSeen,
            Profile = new ChatProfile { ClanId = "W3C" },
        });

        var lastSeen = DateTime.UtcNow;
        await repo.Upsert(new UserDirectoryEntry
        {
            BattleTag = "Peter#123",
            NormalizedName = "peter#123",
            LastSeenAt = lastSeen,
            Profile = new ChatProfile { ClanId = "W3C" },
        });

        var loaded = await repo.Load("Peter#123");
        Assert.AreEqual("W3C", loaded.Profile.ClanId);
        Assert.IsTrue((loaded.LastSeenAt - lastSeen).Duration() < TimeSpan.FromSeconds(1));
    }

    // ── C6 T2 (D8) — lowercased keying, DisplayBattleTag, the case-sensitivity fixes ───────────

    [Test]
    public async Task Directory_Load_IsCaseInsensitive()
    {
        var repo = new UserDirectoryRepository(MongoClient);
        await repo.Upsert(new UserDirectoryEntry
        {
            BattleTag = "Peter#123",
            DisplayBattleTag = "Peter#123",
            NormalizedName = "peter#123",
            LastSeenAt = DateTime.UtcNow,
        });

        var loaded = await repo.Load("PETER#123");

        Assert.IsNotNull(loaded, "a differently-cased lookup must still hit the row");
        Assert.AreEqual("Peter#123", loaded.DisplayBattleTag, "the original JWT casing survives on DisplayBattleTag");
    }

    [Test]
    public async Task Directory_Upsert_SameUserDifferentCasing_SingleDocument()
    {
        var repo = new UserDirectoryRepository(MongoClient);
        await repo.Upsert(new UserDirectoryEntry
        {
            BattleTag = "Peter#123",
            DisplayBattleTag = "Peter#123",
            NormalizedName = "peter#123",
            LastSeenAt = DateTime.UtcNow.AddDays(-1),
        });
        await repo.Upsert(new UserDirectoryEntry
        {
            BattleTag = "PETER#123",
            DisplayBattleTag = "PETER#123",
            NormalizedName = "peter#123",
            LastSeenAt = DateTime.UtcNow,
        });

        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var count = await db.GetCollection<UserDirectoryEntry>(ChatCollections.UserDirectory)
            .CountDocumentsAsync(FilterDefinition<UserDirectoryEntry>.Empty);
        Assert.AreEqual(1, count, "a differently-cased upsert of the same user must update in place, never duplicate");

        var loaded = await repo.Load("peter#123");
        Assert.AreEqual("PETER#123", loaded.DisplayBattleTag, "the second (latest) upsert's casing wins");
    }

    [Test]
    public async Task Directory_SetLastSeen_PreservesCachedProfile()
    {
        // The disconnect-upsert clobber guard: SetLastSeen must never overwrite a previously-cached
        // enrichment Profile with null — only the full-replace Upsert (the connect-time write) may.
        var repo = new UserDirectoryRepository(MongoClient);
        var firstSeen = DateTime.UtcNow.AddDays(-2);
        await repo.Upsert(new UserDirectoryEntry
        {
            BattleTag = "Peter#123",
            DisplayBattleTag = "Peter#123",
            NormalizedName = "peter#123",
            LastSeenAt = firstSeen,
            Profile = new ChatProfile { ClanId = "W3C", RankNumber = 5 },
        });

        var disconnectAt = DateTime.UtcNow;
        await repo.SetLastSeen("PETER#123", "Peter#123", "peter#123", disconnectAt);

        var loaded = await repo.Load("Peter#123");
        Assert.IsNotNull(loaded.Profile, "SetLastSeen must never clobber a cached Profile");
        Assert.AreEqual("W3C", loaded.Profile.ClanId);
        Assert.AreEqual(5, loaded.Profile.RankNumber);
        Assert.IsTrue((loaded.LastSeenAt - disconnectAt).Duration() < TimeSpan.FromSeconds(1),
            "LastSeenAt must advance to the disconnect time");
    }

    [Test]
    public async Task Directory_SearchByNormalizedPrefix_MatchesNameAndNameHashPrefixes_RespectsCutoffAndLimit()
    {
        var repo = new UserDirectoryRepository(MongoClient);
        var now = DateTime.UtcNow;
        var cutoff = now - ChatLimits.MentionCandidateActivityWindow;

        await repo.Upsert(new UserDirectoryEntry
        {
            BattleTag = "Peter#123",
            DisplayBattleTag = "Peter#123",
            NormalizedName = "peter#123",
            LastSeenAt = now.AddDays(-1),
        });
        await repo.Upsert(new UserDirectoryEntry
        {
            BattleTag = "Petra#456",
            DisplayBattleTag = "Petra#456",
            NormalizedName = "petra#456",
            LastSeenAt = now.AddDays(-2),
        });
        await repo.Upsert(new UserDirectoryEntry
        {
            BattleTag = "Wolf#789",
            DisplayBattleTag = "Wolf#789",
            NormalizedName = "wolf#789",
            LastSeenAt = now.AddDays(-1),
        });
        // Stale — outside the 90d activity window; must be excluded regardless of prefix match.
        await repo.Upsert(new UserDirectoryEntry
        {
            BattleTag = "Peterson#999",
            DisplayBattleTag = "Peterson#999",
            NormalizedName = "peterson#999",
            LastSeenAt = cutoff.AddDays(-1),
        });

        var byNamePrefix = await repo.SearchByNormalizedPrefix("pet", cutoff, 10);
        CollectionAssert.AreEquivalent(
            new[] { "peter#123", "petra#456" },
            byNamePrefix.Select(e => e.NormalizedName).ToArray());

        var byFullTagPrefix = await repo.SearchByNormalizedPrefix("peter#1", cutoff, 10);
        CollectionAssert.AreEqual(new[] { "peter#123" }, byFullTagPrefix.Select(e => e.NormalizedName).ToArray());

        var limited = await repo.SearchByNormalizedPrefix("pet", cutoff, 1);
        Assert.AreEqual(1, limited.Count, "limit must be respected");
    }

    [Test]
    public async Task Directory_Indexes_ContainNormalizedNameLastSeenAt()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var indexes = await (await db.GetCollection<UserDirectoryEntry>(ChatCollections.UserDirectory).Indexes.ListAsync()).ToListAsync();

        var index = indexes.Single(i => i["name"] == "ix_normalizedName_lastSeenAt");
        Assert.AreEqual(1, index["key"]["NormalizedName"].ToInt32());
        Assert.AreEqual(-1, index["key"]["LastSeenAt"].ToInt32());
        Assert.IsFalse(index.Contains("unique"), "the index must be non-unique (defensive per D8)");
    }

    [Test]
    public async Task UserSettings_LoadOrDefault_ReturnsSpecDefaultsWhenAbsent()
    {
        var repo = new UserSettingsRepository(MongoClient);

        var settings = await repo.LoadOrDefault("Nobody#111");

        // C5 T4: the settings key is stored/read lowercased (case-insensitive dmPrivacy recheck) — the
        // default-on-miss BattleTag reflects the normalized key, not the caller's verbatim casing.
        Assert.AreEqual("nobody#111", settings.BattleTag);
        Assert.AreEqual(DmPrivacy.Everyone, settings.DmPrivacy);
        Assert.AreEqual(NotificationLevel.All, settings.DefaultNotificationLevel);
        Assert.IsTrue(settings.SoundsEnabled);
    }

    [Test]
    public async Task UserSettings_RoundTrip()
    {
        var repo = new UserSettingsRepository(MongoClient);
        await repo.Upsert(new UserSettings { BattleTag = "Peter#123", DmPrivacy = DmPrivacy.Friends, SoundsEnabled = false });

        var loaded = await repo.LoadOrDefault("Peter#123");

        Assert.AreEqual(DmPrivacy.Friends, loaded.DmPrivacy);
        Assert.IsFalse(loaded.SoundsEnabled);
    }
}
