using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Mentions;

public class MentionInboxRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<MentionInboxEntry> Inbox =>
        CreateCollection<MentionInboxEntry>(ChatCollections.MentionInbox);

    // virtual: lets fault-isolation tests substitute a throwing insert to prove MentionFanOut's
    // per-target try/catch (a single failed insert must not break the other targets or the sender ack).
    public virtual Task Insert(MentionInboxEntry entry) => Inbox.InsertOneAsync(entry);

    public Task<List<MentionInboxEntry>> LoadForUser(string battleTag) =>
        Inbox.Find(e => e.BattleTag == battleTag).SortByDescending(e => e.CreatedAt).ToListAsync();
}
