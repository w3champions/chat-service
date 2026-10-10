using System;
using NUnit.Framework;

namespace W3ChampionsChatService.Tests;

public class StartupMongoConnectionStringTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("''")]
    public void ResolveMongoConnectionString_Throws_WhenMissingOrEmpty(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Startup.ResolveMongoConnectionString(raw));
        StringAssert.Contains("MONGO_CONNECTION_STRING", ex.Message);
    }

    [Test]
    public void ResolveMongoConnectionString_ReturnsValue_AndStripsSingleQuotes()
    {
        Assert.AreEqual("mongodb://user:pw@host:27017", Startup.ResolveMongoConnectionString("'mongodb://user:pw@host:27017'"));
    }
}
