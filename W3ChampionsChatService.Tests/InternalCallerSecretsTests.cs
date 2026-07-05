using System;
using System.Collections.Generic;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Internal;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C7 Task 1 (brief Design decision 2): the env-only per-caller HMAC secret registry. Tests
/// construct <see cref="InternalCallerSecrets"/> directly with literal strings — the registry never
/// reads env vars itself (only <c>Startup</c> does), the same seam pattern this suite already uses
/// for <see cref="TimeProvider"/>. NUnit constraint style.
/// </summary>
[TestFixture]
public class InternalCallerSecretsTests
{
    [Test]
    public void Configured_IsEmpty_WhenNothingSet()
    {
        var secrets = new InternalCallerSecrets(null, null);

        Assert.That(secrets.Configured, Is.Empty);
    }

    [Test]
    public void Configured_OmitsCallersWithNullOrWhitespaceSecrets()
    {
        var mmNullOnly = new InternalCallerSecrets(null, "wb-secret");
        Assert.That(mmNullOnly.Configured, Is.EqualTo(new List<(InternalCaller, string)>
        {
            (InternalCaller.Wb, "wb-secret")
        }), "a null Mm secret omits Mm but keeps the configured Wb entry");

        var mmWhitespaceOnly = new InternalCallerSecrets("   ", "wb-secret");
        Assert.That(mmWhitespaceOnly.Configured, Is.EqualTo(new List<(InternalCaller, string)>
        {
            (InternalCaller.Wb, "wb-secret")
        }), "a whitespace-only Mm secret is treated the same as null/unset");

        var wbNullOnly = new InternalCallerSecrets("mm-secret", null);
        Assert.That(wbNullOnly.Configured, Is.EqualTo(new List<(InternalCaller, string)>
        {
            (InternalCaller.Mm, "mm-secret")
        }), "a null Wb secret omits Wb but keeps the configured Mm entry");

        var wbWhitespaceOnly = new InternalCallerSecrets("mm-secret", "   ");
        Assert.That(wbWhitespaceOnly.Configured, Is.EqualTo(new List<(InternalCaller, string)>
        {
            (InternalCaller.Mm, "mm-secret")
        }), "a whitespace-only Wb secret is treated the same as null/unset");
    }

    [Test]
    public void Configured_ListsMmAndWb_WhenBothSet()
    {
        var secrets = new InternalCallerSecrets("mm-secret", "wb-secret");

        Assert.That(secrets.Configured, Is.EqualTo(new List<(InternalCaller, string)>
        {
            (InternalCaller.Mm, "mm-secret"),
            (InternalCaller.Wb, "wb-secret")
        }));
    }

    [Test]
    public void InternalSignatureFreshnessWindow_Equals300Seconds()
    {
        // Pinned default (brief Design decision 2) — M1/W2 build against this exact value as a
        // cross-repo contract.
        Assert.That(ChatLimits.InternalSignatureFreshnessWindow, Is.EqualTo(TimeSpan.FromSeconds(300)));
    }

    [Test]
    public void Ctor_Throws_WhenBothSecretsIdentical()
    {
        // Security guard: an identical Mm/Wb secret (e.g. ops copy-pasting one vault entry into both
        // env vars) would collapse caller identity in HmacSignatureVerifier's "first secret that
        // verifies" resolution — every request signed with the shared secret would resolve as Mm.
        // Fail hard at construction rather than silently misresolving at request time.
        Assert.That(
            () => new InternalCallerSecrets("shared-secret", "shared-secret"),
            Throws.InvalidOperationException);
    }

    [Test]
    public void Ctor_DoesNotThrow_WhenSecretsDifferOnlyByCase()
    {
        // Ordinal comparison — "abc" and "ABC" are distinct secrets, not a collapse. This must not
        // throw and both callers must remain configured.
        InternalCallerSecrets secrets = null;
        Assert.That(() => secrets = new InternalCallerSecrets("shared-secret", "SHARED-SECRET"), Throws.Nothing);

        Assert.That(secrets.Configured, Is.EqualTo(new List<(InternalCaller, string)>
        {
            (InternalCaller.Mm, "shared-secret"),
            (InternalCaller.Wb, "SHARED-SECRET")
        }));
    }
}
