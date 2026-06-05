using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace W3ChampionsChatService.Mutes;

/// <summary>
/// Persistence abstraction for lounge mutes. Injected (instead of the concrete
/// <see cref="MuteRepository"/>) so the hub and controller depend on an interface — and so tests
/// can substitute a counting/fake implementation to assert the cache-only hot paths perform
/// ZERO mute-repository reads.
/// </summary>
public interface IMuteRepository
{
    Task AddLoungeMute(LoungeMuteRequest loungeMuteRequest);
    Task<LoungeMute> GetMutedPlayer(string battleTag);
    Task<List<LoungeMute>> GetLoungeMutes();
    Task<DeleteResult> DeleteLoungeMute(string battleTag);
}
