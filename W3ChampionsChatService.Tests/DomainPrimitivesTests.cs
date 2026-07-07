using NUnit.Framework;
using W3ChampionsChatService.Channels;

namespace W3ChampionsChatService.Tests;

public class DomainPrimitivesTests
{
    [Test]
    public void Normalize_LowercasesAndTrims()
    {
        Assert.AreEqual("w3c lounge", ChannelNames.Normalize("  W3C Lounge "));
    }

    [Test]
    public void Normalize_NullIsNull()
    {
        Assert.IsNull(ChannelNames.Normalize(null));
    }

    [Test]
    public void PairKey_IsOrderIndependent()
    {
        Assert.AreEqual(DmPairKey.For("Peter#123", "Wolf#456"), DmPairKey.For("Wolf#456", "Peter#123"));
    }

    [Test]
    public void PairKey_IsCaseInsensitiveAndSortedWithPipeSeparator()
    {
        Assert.AreEqual("peter#123|wolf#456", DmPairKey.For("WOLF#456", "peter#123"));
    }
}
