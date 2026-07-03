using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Mentions;

public class MentionInboxRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<MentionInboxEntry> Inbox =>
        CreateCollection<MentionInboxEntry>(ChatCollections.MentionInbox);

    public Task Insert(MentionInboxEntry entry) => Inbox.InsertOneAsync(entry);

    public Task<List<MentionInboxEntry>> LoadForUser(string battleTag) =>
        Inbox.Find(e => e.BattleTag == battleTag).SortByDescending(e => e.CreatedAt).ToListAsync();
}
