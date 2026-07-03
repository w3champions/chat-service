using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Mutes;

namespace W3ChampionsChatService.Tests;

// LEGACY: ChatBanRoomScopeTests.CountingMuteRepository @778aec9
/// <summary>
/// An <see cref="IMuteRepository"/> spy (decorator over a real <see cref="MuteRepository"/>) that counts
/// <see cref="IMuteRepository.GetMutedPlayer"/> calls, so a test can assert the send/join hot paths perform
/// ZERO mute-repository reads. On the new pipeline the only mute-repository read is the connect-time resolve
/// in <c>SessionStateAssembler</c>; enforcement on send/join reads the per-connection
/// <c>ConnectionMapping</c> cache exclusively (zero DB). Ported verbatim from the pre-cutover suite.
/// </summary>
internal sealed class CountingMuteRepository(MongoClient client) : IMuteRepository
{
    private readonly MuteRepository _inner = new(client);

    public int GetMutedPlayerCallCount { get; private set; }

    public Task<LoungeMute> GetMutedPlayer(string battleTag)
    {
        GetMutedPlayerCallCount++;
        return _inner.GetMutedPlayer(battleTag);
    }

    public Task AddLoungeMute(LoungeMuteRequest loungeMuteRequest) => _inner.AddLoungeMute(loungeMuteRequest);
    public Task<List<LoungeMute>> GetLoungeMutes() => _inner.GetLoungeMutes();
    public Task<DeleteResult> DeleteLoungeMute(string battleTag) => _inner.DeleteLoungeMute(battleTag);
}
