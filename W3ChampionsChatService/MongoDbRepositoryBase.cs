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
    private readonly string _databaseName = "W3Champions-Chat-Service";

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

    protected async Task Delete<T>(Expression<Func<T, bool>> deleteQuery)
    {
        var mongoDatabase = CreateClient();
        var mongoCollection = mongoDatabase.GetCollection<T>(typeof(T).Name);
        await mongoCollection.DeleteOneAsync<T>(deleteQuery);
    }

    protected Task Delete<T>(string id) where T : IIdentifiable
    {
        return Delete<T>(x => x.Id == id);
    }
}

public interface IIdentifiable
{
    public string Id { get; }
}
