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

    [Test]
    public void CounterpartOf_ReturnsTheOtherHalf()
    {
        var pairKey = DmPairKey.For("Peter#123", "Wolf#456");

        Assert.AreEqual("wolf#456", DmPairKey.CounterpartOf(pairKey, "Peter#123"));
        Assert.AreEqual("peter#123", DmPairKey.CounterpartOf(pairKey, "Wolf#456"));
    }

    // 2026-08-04 follow-up (Minor finding): CounterpartOf's parts[0] comparison must be
    // StringComparison.OrdinalIgnoreCase (the pre-hoist ResolveDmCounterpart behavior), not a plain
    // ordinal ==. parts[0] is normally lowercased by DmPairKey.For and the incoming battleTag is
    // lowercased via ToLowerInvariant before comparing — but ToLowerInvariant is not guaranteed to fold
    // every code point identically to what an OrdinalIgnoreCase match tolerates, so an ordinal-only
    // comparison could spuriously miss and hand back the CALLER'S OWN tag as its "counterpart" instead
    // of the real one. Constructs a pair-key with an uppercase first half directly (bypassing
    // DmPairKey.For's own lowercasing) to pin that a casing mismatch on parts[0] itself still resolves
    // correctly — this regresses under a plain ordinal `==`.
    [Test]
    public void CounterpartOf_MatchesFirstHalfCaseInsensitively()
    {
        const string pairKey = "PETER#123|wolf#456";

        Assert.AreEqual("wolf#456", DmPairKey.CounterpartOf(pairKey, "peter#123"),
            "an OrdinalIgnoreCase match against parts[0] must resolve to the real counterpart, not fall " +
            "through to returning the caller's own tag");
    }
}
