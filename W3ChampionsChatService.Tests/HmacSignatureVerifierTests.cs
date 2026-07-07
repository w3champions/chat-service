using System;
using System.Text;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Internal;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C7 Task 2: the pure HMAC signature verifier that the whole internal-API auth boundary
/// (Task 3's <c>[InternalHmacAuth]</c> filter) rests on. The verifier is deliberately kept
/// standalone and side-effect-free so it can be exhaustively unit-tested here without HTTP.
/// NUnit constraint style (repo convention — no FluentAssertions).
/// </summary>
[TestFixture]
public class HmacSignatureVerifierTests
{
    // ── PUBLISHED C7 internal-API HMAC byte-compatibility vectors (M1 / W2 REUSE) ─────────────
    //
    // Scheme (pinned cross-repo contract):
    //   signature = "v1=" + hex( HMAC_SHA256( key = UTF8(secret),
    //                                          msg = UTF8("v1." + timestamp + ".") ++ rawBodyBytes ) )
    //   • secret is a RAW UTF-8 string key — never hex/base64-decoded.
    //   • the MAC is taken over the EXACT raw request-body bytes — never a re-serialized body.
    //   • an empty-body DELETE signs the string "v1." + timestamp + "." (rawBody = "").
    //   • freshness: reject when |now − timestamp| > 300s (ChatLimits.InternalSignatureFreshnessWindow);
    //     the exact window edge (|Δ| == 300s) is ACCEPTED (inclusive).
    //   • hex is compared case-INSENSITIVELY (W2 is C# — Convert.ToHexString emits UPPERCASE; the
    //     security is entirely in the FixedTimeEquals compare on the DECODED bytes, so casing is
    //     irrelevant). The header must still be "v1=" + exactly 64 hex chars.
    //
    // Fixed inputs:  secret = "test-secret",  timestamp = "1751500000"  (unix seconds).
    //
    // Vector 1 — CREATE (POST /internal/channels), rawBody bytes EXACTLY:
    //   {"kind":"match","ref":"abc123XYZ0","name":"Test Lobby","members":["Foo#1234","Bar#5678"]}
    //   X-W3C-Signature: v1=b0acb9b2ba23a8aaf0076c05cd1c9631ac88364dfcebe61352c220f9009e54cd
    //
    // Vector 2 — empty-body DELETE (DELETE /internal/channels/{ref}), signing string "v1.1751500000.":
    //   X-W3C-Signature: v1=09b6a138e0b80b2d6c4fa412590abcc352953b7e43ba15479020161e944f47a3
    //
    // Independently recomputed via `openssl dgst -sha256 -hmac test-secret` — both MATCH M1's pinned
    // values (matchmaking-service src/app/services/chat/chat.client.ts:167). Any change to these
    // vectors is a CROSS-REPO BREAKING CHANGE: coordinate with M1 (matchmaking) and W2
    // (website-backend) before editing.
    // ──────────────────────────────────────────────────────────────────────────────────────────

    private const string Secret = "test-secret";
    private const string PinnedTimestamp = "1751500000";

    private const string CreateBody =
        "{\"kind\":\"match\",\"ref\":\"abc123XYZ0\",\"name\":\"Test Lobby\",\"members\":[\"Foo#1234\",\"Bar#5678\"]}";
    private const string CreateSignature =
        "v1=b0acb9b2ba23a8aaf0076c05cd1c9631ac88364dfcebe61352c220f9009e54cd";
    private const string DeleteSignature =
        "v1=09b6a138e0b80b2d6c4fa412590abcc352953b7e43ba15479020161e944f47a3";

    /// <summary>The instant the pinned vectors were signed at (Δ == 0, trivially inside the window).</summary>
    private static readonly DateTime PinnedInstant = DateTimeOffset.FromUnixTimeSeconds(1751500000).UtcDateTime;
    private static readonly TimeSpan Window = ChatLimits.InternalSignatureFreshnessWindow;

    private static byte[] CreateBodyBytes() => Encoding.UTF8.GetBytes(CreateBody);
    private static byte[] EmptyBody() => Array.Empty<byte>();
    private static InternalCallerSecrets MmOnly() => new(Secret, null);

    // ── Pinned M1 byte-compatibility vectors (must verify EXACTLY) ──────────────────────────────

    [Test]
    public void PinnedCreateVector_VerifiesAndResolvesMm()
    {
        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, CreateSignature, PinnedInstant, MmOnly(), out var caller);

        Assert.That(ok, Is.True, "M1's pinned CREATE vector must verify byte-for-byte");
        Assert.That(caller, Is.EqualTo(InternalCaller.Mm));
    }

    [Test]
    public void PinnedEmptyBodyDeleteVector_VerifiesAndResolvesMm()
    {
        // Empty-body DELETE signs "v1." + timestamp + "." with rawBody = "" (no body, no Content-Type).
        var ok = HmacSignatureVerifier.TryResolveCaller(
            EmptyBody(), PinnedTimestamp, DeleteSignature, PinnedInstant, MmOnly(), out var caller);

        Assert.That(ok, Is.True, "M1's pinned empty-body DELETE vector must verify byte-for-byte");
        Assert.That(caller, Is.EqualTo(InternalCaller.Mm));
    }

    // ── Caller resolution (which secret verifies picks the caller — there is no caller-id header) ──

    [Test]
    public void WbSecret_ResolvesWb()
    {
        // Same secret string, but registered under Wb — the caller is resolved from WHICH secret
        // verified, so the identical signature now resolves Wb rather than Mm.
        var wbOnly = new InternalCallerSecrets(null, Secret);

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, CreateSignature, PinnedInstant, wbOnly, out var caller);

        Assert.That(ok, Is.True);
        Assert.That(caller, Is.EqualTo(InternalCaller.Wb));
    }

    [Test]
    public void BothConfigured_ResolvesTheMatchingCaller()
    {
        // Mm is tried first and does NOT match (wrong key); Wb holds the real signer. Proves the
        // verifier walks every configured secret and returns the one that actually verifies rather
        // than the first configured entry.
        var both = new InternalCallerSecrets("some-other-mm-secret", Secret);

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, CreateSignature, PinnedInstant, both, out var caller);

        Assert.That(ok, Is.True);
        Assert.That(caller, Is.EqualTo(InternalCaller.Wb));
    }

    // ── Coordinator OVERRIDE of the brief (binding): accept case-insensitive hex ────────────────

    [Test]
    public void UppercaseHex_AlsoVerifies()
    {
        // COORDINATOR OVERRIDE of the brief's UppercaseHex_Rejects: the pinned contract says only
        // "v1=hex(...)", not lowercase-only. W2 is C# and .NET's Convert.ToHexString emits UPPERCASE
        // hex by default — strict lowercase rejection would break W2 for zero security benefit, since
        // the security lives entirely in the FixedTimeEquals compare on the DECODED MAC bytes and hex
        // casing does not change the decoded value. So a valid signature presented in UPPERCASE hex
        // must STILL verify.
        var upperHexSignature = "v1=" + CreateSignature.Substring(3).ToUpperInvariant();

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, upperHexSignature, PinnedInstant, MmOnly(), out var caller);

        Assert.That(ok, Is.True, "uppercase hex decodes to the same MAC bytes and must verify (W2 interop)");
        Assert.That(caller, Is.EqualTo(InternalCaller.Mm));
    }

    // ── Freshness boundary (inclusive edge) ─────────────────────────────────────────────────────

    [Test]
    public void TimestampAtExactWindowEdge_Verifies()
    {
        // ts == now − 300s: |Δ| == the freshness window exactly → ACCEPTED (inclusive edge).
        var now = PinnedInstant + Window;

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, CreateSignature, now, MmOnly(), out var caller);

        Assert.That(ok, Is.True, "|now − timestamp| == window is inclusive and must verify");
        Assert.That(caller, Is.EqualTo(InternalCaller.Mm));
    }

    // ── Rejection matrix (all must return false) ────────────────────────────────────────────────

    [Test]
    public void WrongSecret_Rejects()
    {
        var wrong = new InternalCallerSecrets("not-the-real-secret", null);

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, CreateSignature, PinnedInstant, wrong, out var caller);

        Assert.That(ok, Is.False);
        Assert.That(caller, Is.EqualTo(default(InternalCaller)));
    }

    [Test]
    public void TamperedBody_Rejects()
    {
        // Flip a single body byte — the signature was computed over the original bytes, so the MAC
        // no longer matches.
        var tampered = CreateBodyBytes();
        tampered[10] ^= 0x01;

        var ok = HmacSignatureVerifier.TryResolveCaller(
            tampered, PinnedTimestamp, CreateSignature, PinnedInstant, MmOnly(), out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void StaleTimestamp_PastBeyondWindow_Rejects()
    {
        // ts = now − 301s (one second past the window).
        var now = PinnedInstant + Window + TimeSpan.FromSeconds(1);

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, CreateSignature, now, MmOnly(), out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void FutureTimestamp_BeyondWindow_Rejects()
    {
        // ts = now + 301s (the request is dated too far in the future).
        var now = PinnedInstant - Window - TimeSpan.FromSeconds(1);

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, CreateSignature, now, MmOnly(), out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void ReplayOutsideWindow_Rejects()
    {
        // A signature that WAS valid at PinnedInstant, replayed by an attacker an hour later: the
        // timestamp is now far outside the freshness window, so it must be rejected even though the
        // MAC itself is still arithmetically correct.
        var muchLater = PinnedInstant + TimeSpan.FromHours(1);

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, CreateSignature, muchLater, MmOnly(), out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void MalformedTimestamp_Rejects()
    {
        // Non-numeric timestamp fails long.TryParse and is rejected before any MAC work.
        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), "not-a-number", CreateSignature, PinnedInstant, MmOnly(), out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void MissingV1Prefix_Rejects()
    {
        // The bare 64-hex digest without the mandatory "v1=" scheme prefix.
        var noPrefix = CreateSignature.Substring(3);

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, noPrefix, PinnedInstant, MmOnly(), out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void NonHexSignature_Rejects()
    {
        // Correct "v1=" prefix and correct length (64), but the payload is not valid hex.
        var nonHex = "v1=" + new string('g', 64);

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, nonHex, PinnedInstant, MmOnly(), out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void NoConfiguredSecrets_Rejects()
    {
        // No secret configured for any caller ⇒ nothing can verify, even a perfectly valid signature.
        var none = new InternalCallerSecrets(null, null);

        var ok = HmacSignatureVerifier.TryResolveCaller(
            CreateBodyBytes(), PinnedTimestamp, CreateSignature, PinnedInstant, none, out var caller);

        Assert.That(ok, Is.False);
        Assert.That(caller, Is.EqualTo(default(InternalCaller)));
    }
}
