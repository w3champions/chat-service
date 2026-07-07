using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Users;
using Serilog;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// D9: the outcome of resolving a ticket-proven identity's chat flair — the <see cref="ChatUser"/>
/// plus whether it was FRESHLY enriched from wb THIS call (a real wb round-trip succeeded this call)
/// or came from a fallback tier (the directory cache, or the plain fallback). <see cref="Chats.ChatHub"/>
/// uses <see cref="FreshFromWb"/> to decide whether the connect-time directory upsert may replace the
/// cached <see cref="ChatProfile"/> — a wb outage must NEVER clobber a good cached Profile with a
/// stale/plain one (the never-clobber invariant).
/// </summary>
public record ChatUserResolution(ChatUser User, bool FreshFromWb);

public interface IChatAuthenticationService
{
    Task<ChatUserResolution> GetUserFromIdentity(W3CUserAuthentication identity);
}

public class ChatAuthenticationService(
    MongoClient mongoClient,
    IWebsiteBackendRepository websiteBackendRepository,
    UserDirectoryRepository userDirectory
) : MongoDbRepositoryBase(mongoClient), IChatAuthenticationService
{
    private readonly IWebsiteBackendRepository _websiteBackendRepository = websiteBackendRepository;
    private readonly UserDirectoryRepository _userDirectory = userDirectory;

    // HARD CUTOVER (C2): the JWT decode is GONE — it happened once, at ticket mint. Here we only
    // ENRICH an already-proven identity snapshot (from the consumed ticket) with wb flair.
    //
    // D9 (§14 row 1): three-tier fallback chain, honored in EXACTLY this order:
    //   1. wb succeeds → a freshly enriched ChatUser, FreshFromWb = true.
    //   2. wb throws → fall back to the directory cache (UserDirectoryRepository.Load(battleTag).Profile);
    //      a hit restores the LAST KNOWN GOOD flair, FreshFromWb = false.
    //   3. wb throws AND no usable cache → the plain battleTag fallback (pre-D9 behavior),
    //      FreshFromWb = false.
    // Decision 11 still holds at every tier: the ticket ALREADY proved identity, so a wb (or cache)
    // outage must NEVER fail a proven-authenticated connect.
    public async Task<ChatUserResolution> GetUserFromIdentity(W3CUserAuthentication identity)
    {
        try
        {
            var userDetails = await _websiteBackendRepository.GetChatDetails(identity.BattleTag);
            var rank = userDetails?.Rank;
            var chatUser = BuildChatUser(
                identity,
                userDetails?.ClanId,
                userDetails?.ProfilePicture,
                userDetails?.ChatColor,
                userDetails?.ChatIcons,
                rank?.LeagueId,
                rank?.LeagueName,
                rank?.LeagueOrder,
                rank?.LeagueDivision,
                rank?.RankNumber,
                rank?.GameMode,
                rank?.GateWay,
                userDetails?.GamesPlayed,
                userDetails?.Season);
            return new ChatUserResolution(chatUser, FreshFromWb: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enrich chat user {BattleTag} from wb — attempting directory-cache fallback", identity.BattleTag);
            return await ResolveFromDirectoryCacheOrPlain(identity);
        }
    }

    // Tier 2 (directory cache) then tier 3 (plain fallback). Isolated in its own try/catch: a directory
    // read failure (e.g. Mongo hiccup piling on top of the wb outage) must degrade to the plain
    // fallback too, never propagate and fail the connect.
    private async Task<ChatUserResolution> ResolveFromDirectoryCacheOrPlain(W3CUserAuthentication identity)
    {
        try
        {
            var cached = await _userDirectory.Load(identity.BattleTag);
            if (cached?.Profile != null)
            {
                Log.Information("Restoring cached directory flair for {BattleTag} after a wb outage", identity.BattleTag);
                var profile = cached.Profile;
                var chatUser = BuildChatUser(
                    identity,
                    profile.ClanId,
                    profile.ProfilePicture,
                    profile.ChatColor,
                    profile.ChatIcons,
                    profile.LeagueId,
                    profile.LeagueName,
                    profile.LeagueOrder,
                    profile.LeagueDivision,
                    profile.RankNumber,
                    profile.GameMode,
                    profile.GateWay,
                    profile.GamesPlayed,
                    profile.Season);
                return new ChatUserResolution(chatUser, FreshFromWb: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Directory-cache fallback lookup failed for {BattleTag}", identity.BattleTag);
        }

        // Never null (Decision 11) — the ticket already proved identity. Full flair is restored on the
        // next successful enrichment.
        return new ChatUserResolution(new ChatUser(identity.BattleTag, identity.IsAdmin, null, new ProfilePicture(), null, null), FreshFromWb: false);
    }

    // Shared builder: admin color/icon forcing applies identically whether the flair came fresh from wb
    // or was restored from the cached Profile, and both call sites populate the SAME set of additive
    // enrichment fields on ChatUser (D9). The admin-icon prepend is IDEMPOTENT (Contains-guarded): the
    // directory-cache tier restores an admin's cached Profile whose ChatIcons ALREADY start with a forced
    // AdminIcon (it was itself produced by this builder on the wb-success path before being cached), so a
    // naive prepend would double it on that fallback. Guarding the prepend (rather than skipping admin
    // forcing on the cache tier) still promotes a user who became admin AFTER their Profile was cached.
    private static ChatUser BuildChatUser(
        W3CUserAuthentication identity,
        string clanId,
        ProfilePicture profilePicture,
        ChatColor chatColor,
        ChatIcon[] chatIcons,
        int? leagueId,
        string leagueName,
        int? leagueOrder,
        int? leagueDivision,
        int? rankNumber,
        int? gameMode,
        int? gateWay,
        int? gamesPlayed,
        int? season)
    {
        var icons = chatIcons ?? [];
        if (identity.IsAdmin)
        {
            chatColor = ChatColor.AdminColor;
            // Idempotent: never prepend a second AdminIcon when restoring an already-admin-forced cached
            // Profile on the wb-outage fallback tier (ChatIcon has value equality by IconId).
            if (!icons.Contains(ChatIcon.AdminIcon))
            {
                icons = [ChatIcon.AdminIcon, .. icons];
            }
        }

        return new ChatUser(identity.BattleTag, identity.IsAdmin, clanId, profilePicture, chatColor, icons)
        {
            LeagueId = leagueId,
            LeagueName = leagueName,
            LeagueOrder = leagueOrder,
            LeagueDivision = leagueDivision,
            RankNumber = rankNumber,
            GameMode = gameMode,
            GateWay = gateWay,
            GamesPlayed = gamesPlayed,
            Season = season,
        };
    }
}
