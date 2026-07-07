using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace W3ChampionsChatService;

public class MongoDbRepositoryBase(MongoClient mongoClient)
{
    private readonly MongoClient _mongoClient = mongoClient;
    public const string DatabaseName = "W3Champions-Chat-Service";
    private readonly string _databaseName = DatabaseName;

    protected IMongoDatabase CreateClient()
    {
        var database = _mongoClient.GetDatabase(_databaseName);
        return database;
    }

    protected Task<T> LoadFirst<T>(Expression<Func<T, bool>> expression)
    {
        var mongoCollection = CreateCollection<T>();
        return mongoCollection.FindSync(expression).FirstOrDefaultAsync();
    }

    protected Task<T> LoadFirst<T>(string id) where T : IIdentifiable
    {
        return LoadFirst<T>(x => x.Id == id);
    }

    protected Task Insert<T>(T element)
    {
        var mongoCollection = CreateCollection<T>();
        return mongoCollection.InsertOneAsync(element);
    }

    protected async Task<List<T>> LoadAll<T>(Expression<Func<T, bool>> expression = null, int? limit = null)
    {
        if (expression == null) expression = l => true;
        var mongoCollection = CreateCollection<T>();
        var elements = await mongoCollection.Find(expression).Limit(limit).ToListAsync();
        return elements;
    }

    protected IMongoCollection<T> CreateCollection<T>(string collectionName = null)
    {
        var mongoDatabase = CreateClient();
        var mongoCollection = mongoDatabase.GetCollection<T>((collectionName ?? typeof(T).Name));
        return mongoCollection;
    }

    public async Task Upsert<T>(T insertObject, Expression<Func<T, bool>> identityQuerry)
    {
        var mongoDatabase = CreateClient();
        var mongoCollection = mongoDatabase.GetCollection<T>(typeof(T).Name);
        await mongoCollection.FindOneAndReplaceAsync(
            identityQuerry,
            insertObject,
            new FindOneAndReplaceOptions<T> { IsUpsert = true });
    }

    public Task Upsert<T>(T insertObject) where T : IIdentifiable
    {
        return Upsert(insertObject, x => x.Id == insertObject.Id);
    }

    protected Task UpsertMany<T>(List<T> insertObject) where T : IIdentifiable
    {
        if (!insertObject.Any()) return Task.CompletedTask;

        var collection = CreateCollection<T>();
        var bulkOps = insertObject
            .Select(record => new ReplaceOneModel<T>(Builders<T>.Filter
            .Where(x => x.Id == record.Id), record)
            {
                IsUpsert = true
            })
            .Cast<WriteModel<T>>().ToList();
        return collection.BulkWriteAsync(bulkOps);
    }

    protected async Task<DeleteResult> Delete<T>(Expression<Func<T, bool>> deleteQuery)
    {
        var mongoDatabase = CreateClient();
        var mongoCollection = mongoDatabase.GetCollection<T>(typeof(T).Name);
        return await mongoCollection.DeleteOneAsync<T>(deleteQuery);
    }

    protected Task Delete<T>(string id) where T : IIdentifiable
    {
        return Delete<T>(x => x.Id == id);
    }

    /// <summary>
    /// Shared retry-once wrapper for the find-or-create / insert-if-absent upsert idiom used across
    /// <see cref="Channels.ChannelRepository"/> (FindOrCreateSemiPublic/FindOrCreateDm/FindOrCreateSystem)
    /// and <see cref="Memberships.MembershipRepository"/> (InsertIfAbsent): a $setOnInsert upsert backed
    /// by a unique index, where a GENUINE concurrent race — two callers upserting the same not-yet-existing
    /// key at once — can make the losing call's insert half violate that unique index. That surfaces as a
    /// <see cref="MongoCommandException"/> with <c>Code == 11000</c> ("DuplicateKey") from the single
    /// findAndModify command itself — NOT a <see cref="MongoWriteException"/>, which only wraps the
    /// insert/update/delete write-command family, not findAndModify. Re-running <paramref name="operation"/>
    /// EXACTLY ONCE resolves it as a plain match: by the time the retry runs, the winner's row is already
    /// visible, so the same filter now matches instead of upserting. Not a generic retry policy — it exists
    /// only to absorb this one race, so it retries once and lets any other exception (or a second 11000,
    /// which would indicate something other than this race) propagate.
    /// </summary>
    protected static async Task<T> RetryOnceOnDuplicateKey<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (MongoCommandException ex) when (ex.Code == 11000)
        {
            return await operation();
        }
    }
}

public interface IIdentifiable
{
    public string Id { get; }
}
