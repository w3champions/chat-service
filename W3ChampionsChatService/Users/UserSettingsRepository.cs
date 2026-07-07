using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Users;

/// <summary>
/// Durable per-user settings store (dmPrivacy, notification defaults, sounds).
/// <para>
/// BATTLETAG KEY CONVENTION (C5 T4): the persisted <see cref="UserSettings.BattleTag"/> is ALWAYS stored
/// lowercased, and every read/write lowercases its incoming <c>battleTag</c> argument before building the
/// Mongo filter (Mongo <c>$eq</c> is case-SENSITIVE — there is no collation or CI index). This conforms
/// user_settings to the same lowercased-key convention the rest of the DM machinery already assumes:
/// <see cref="Chats.ChatHub"/>'s pending-phase dmPrivacy recheck (<c>ApplyPrivateLaneGates</c>) reads via
/// <c>ResolveDmCounterpart</c>, which returns the LOWERCASED half of the pair-key, plus
/// <see cref="Memberships.MembershipRepository"/> and <see cref="Mutes.MuteRepository"/> (both already
/// lowercase their keys). Without it, a recipient's dmPrivacy stored under their JWT-cased identity (e.g.
/// "Wolf#456") is invisible to the lowercased recheck read — <see cref="LoadOrDefault"/> silently misses
/// and falls back to the <see cref="DmPrivacy.Everyone"/> default, letting a stranger initiator's
/// pending-phase sends through past a tightened Nobody/Friends setting (the confirmed MEDIUM
/// dmPrivacy-enforcement bypass this fix closes).
/// </para>
/// </summary>
public class UserSettingsRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<UserSettings> Settings =>
        CreateCollection<UserSettings>(ChatCollections.UserSettings);

    /// <summary>Lowercases a battleTag to the durable settings key convention (see the class doc).</summary>
    private static string NormalizeTag(string battleTag) => battleTag.ToLowerInvariant();

    // Persists a lowercased-BattleTag COPY without mutating the caller's object (immutability) — mirrors
    // MembershipRepository.WithNormalizedBattleTag. NOTE: keep this field list in sync with UserSettings —
    // a new field must be copied here too.
    private static UserSettings WithNormalizedBattleTag(UserSettings settings) =>
        new UserSettings
        {
            BattleTag = NormalizeTag(settings.BattleTag),
            DmPrivacy = settings.DmPrivacy,
            DefaultNotificationLevel = settings.DefaultNotificationLevel,
            SoundsEnabled = settings.SoundsEnabled,
        };

    public Task Upsert(UserSettings settings)
    {
        var normalized = WithNormalizedBattleTag(settings);
        return Settings.ReplaceOneAsync(
            s => s.BattleTag == normalized.BattleTag, normalized, new ReplaceOptions { IsUpsert = true });
    }

    public async Task<UserSettings> LoadOrDefault(string battleTag)
    {
        var tag = NormalizeTag(battleTag);
        return await Settings.Find(s => s.BattleTag == tag).FirstOrDefaultAsync()
            ?? new UserSettings { BattleTag = tag };
    }
}
