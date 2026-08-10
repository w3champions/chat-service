using System;
using System.Threading.Tasks;
using Serilog;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// The user-directory write shared by the connect path (<c>ChatHub.UpsertDirectory</c>) and the live
/// flair-refresh path (<c>FanOut.FlairRefresher</c>).
/// <para>
/// Extracted so the NEVER-CLOBBER rule exists once: identity fields are always refreshed, but
/// <see cref="UserDirectoryEntry.Profile"/> is replaced ONLY when the resolution came fresh from
/// website-backend. Two copies of this rule would be two chances to get it wrong, and the failure
/// mode — overwriting a good cached profile with a degraded one — is invisible until a user complains
/// that their avatar reverted.
/// </para>
/// <para>
/// Non-fatal by design: a directory write failure is logged and swallowed. Neither caller should fail
/// because a cache update did.
/// </para>
/// </summary>
public static class UserDirectoryUpsert
{
    public static async Task Apply(
        UserDirectoryRepository userDirectory,
        string battleTag,
        ChatUserResolution resolution,
        DateTime now)
    {
        try
        {
            var entry = await userDirectory.Load(battleTag)
                ?? new UserDirectoryEntry { BattleTag = battleTag };
            entry.DisplayBattleTag = battleTag;
            entry.NormalizedName = battleTag?.Trim().ToLowerInvariant();
            entry.LastSeenAt = now;
            if (resolution.FreshFromWb)
            {
                entry.Profile = ChatProfileMapper.FromChatUser(resolution.User);
            }
            await userDirectory.Upsert(entry);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to upsert user_directory entry for {BattleTag}", battleTag);
        }
    }
}
