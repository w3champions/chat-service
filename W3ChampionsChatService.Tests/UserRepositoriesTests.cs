using System;
using System.Threading.Tasks;
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

    [Test]
    public async Task UserSettings_LoadOrDefault_ReturnsSpecDefaultsWhenAbsent()
    {
        var repo = new UserSettingsRepository(MongoClient);

        var settings = await repo.LoadOrDefault("Nobody#111");

        Assert.AreEqual("Nobody#111", settings.BattleTag);
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
