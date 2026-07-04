using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// A <see cref="UserDirectoryRepository"/> spy (subclass over the real Mongo-backed repository — there
/// is no interface seam here, unlike <c>IMuteRepository</c>) that counts <see cref="Load"/> calls, so a
/// test can assert the mention-validation hot path (no <c>&lt;@tag&gt;</c> tokens in the content)
/// performs ZERO directory reads (C6 Task 4 / D2). Mirrors <see cref="CountingMuteRepository"/>'s
/// spy-over-real-repo idiom. Also counts <see cref="Upsert"/>/<see cref="SetLastSeen"/> calls — the
/// two WRITE methods — so a test can assert a read-only surface (e.g. C6 Task 10's
/// GetPresence/GetPresenceDetails) never touches the directory at all.
/// </summary>
internal sealed class CountingUserDirectoryRepository(MongoClient client) : UserDirectoryRepository(client)
{
    public int LoadCallCount { get; private set; }
    public int UpsertCallCount { get; private set; }
    public int SetLastSeenCallCount { get; private set; }

    public override Task<UserDirectoryEntry> Load(string battleTag)
    {
        LoadCallCount++;
        return base.Load(battleTag);
    }

    public override Task Upsert(UserDirectoryEntry entry)
    {
        UpsertCallCount++;
        return base.Upsert(entry);
    }

    public override Task SetLastSeen(string battleTag, string displayBattleTag, string normalizedName, DateTime now)
    {
        SetLastSeenCallCount++;
        return base.SetLastSeen(battleTag, displayBattleTag, normalizedName, now);
    }
}
