using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// C7's pure, side-effect-free HMAC request-signature verifier — the single point on which the whole
/// internal-API auth boundary (Task 3's <c>[InternalHmacAuth]</c> resource filter) rests. It takes the
/// raw request-body bytes, the two <c>X-W3C-Webhook-Timestamp</c> / <c>X-W3C-Signature</c> header
/// values, the current UTC time and the configured per-caller secrets, and reports whether the request
/// carries a valid signature — and if so, which caller's secret verified it.
///
/// <para>Pinned cross-repo scheme (M1's shipped wire truth — see the vector block in
/// <c>HmacSignatureVerifierTests</c>): <c>signature = "v1=" + hex(HMAC_SHA256(key = UTF8(secret),
/// msg = UTF8("v1." + timestamp + ".") ++ rawBodyBytes))</c>. The secret is a RAW UTF-8 string key
/// (never hex/base64-decoded); the MAC is taken over the EXACT raw body bytes (never a re-serialized
/// body); an empty-body DELETE signs <c>"v1." + timestamp + "."</c> with an empty body.</para>
///
/// <para>Order of checks (freshness is deliberately gated BEFORE any MAC work, so a stale/replayed
/// request is dropped without spending a hash): parse timestamp → freshness window → signature format
/// + hex-decode → constant-time MAC compare against each configured secret.</para>
/// </summary>
public static class HmacSignatureVerifier
{
    /// <summary>The mandatory scheme prefix on the <c>X-W3C-Signature</c> header value.</summary>
    private const string SignaturePrefix = "v1=";

    /// <summary>The message scheme prefix the MAC is computed over: <c>"v1." + timestamp + "."</c>
    /// concatenated with the raw body bytes. Distinct from <see cref="SignaturePrefix"/> — the shared
    /// <c>v1</c> is coincidental scheme-versioning, not the same token.</summary>
    private const string MessageSchemePrefix = "v1.";

    /// <summary>An HMAC-SHA256 digest is 32 bytes ⇒ exactly 64 hex characters.</summary>
    private const int HexDigestLength = 64;

    /// <summary>Upper bound (unix seconds) of <see cref="DateTimeOffset.FromUnixTimeSeconds"/>'s valid
    /// range (9999-12-31T23:59:59Z). An attacker-supplied timestamp above this would throw inside the
    /// freshness conversion; we reject it as unfresh instead (it is astronomically outside any ±300s
    /// window anyway). Combined with the sign-rejecting parse below, this keeps the freshness math
    /// total and non-throwing on hostile input.</summary>
    private const long MaxUnixSeconds = 253402300799L;

    /// <summary>
    /// Verifies the request signature and, on success, resolves which caller signed it (the caller is
    /// identified by WHICH configured secret verifies — there is no caller-id header). Returns
    /// <c>false</c> for every rejection path (bad timestamp, stale/future/replayed, malformed or
    /// non-hex signature, wrong secret, tampered body, or no configured secrets), leaving
    /// <paramref name="caller"/> at its default.
    /// </summary>
    /// <param name="rawBody">The exact raw request-body bytes (empty for a body-less DELETE). Never
    /// re-serialize the body before calling — the MAC is over these bytes verbatim.</param>
    /// <param name="timestampHeader">The <c>X-W3C-Webhook-Timestamp</c> value (unix seconds string).</param>
    /// <param name="signatureHeader">The <c>X-W3C-Signature</c> value (<c>"v1=" + 64 hex chars</c>).</param>
    /// <param name="nowUtc">The current UTC time to measure freshness against.</param>
    /// <param name="secrets">The configured per-caller secret registry.</param>
    /// <param name="caller">On success, the caller whose secret verified the signature.</param>
    public static bool TryResolveCaller(
        byte[] rawBody,
        string timestampHeader,
        string signatureHeader,
        DateTime nowUtc,
        InternalCallerSecrets secrets,
        out InternalCaller caller)
    {
        caller = default;

        if (secrets is null)
        {
            return false;
        }

        // 1. Timestamp: invariant parse, digits only (NumberStyles.None rejects sign/whitespace/decimals,
        //    which also rejects negative timestamps outright). Keep the RAW header string for the message
        //    below — signing is over the exact string the caller sent, not a re-stringified parse.
        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return false;
        }

        // 2. Freshness — BEFORE any MAC work. |now − timestamp| must be within the window; the exact
        //    edge (== window) is inclusive.
        if (unixSeconds > MaxUnixSeconds)
        {
            return false;
        }

        var signedAtUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        var nowNormalized = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var skew = (nowNormalized - signedAtUtc).Duration();
        if (skew > ChatLimits.InternalSignatureFreshnessWindow)
        {
            return false;
        }

        // 3. Signature format: "v1=" + exactly 64 hex chars. Hex is decoded case-INSENSITIVELY —
        //    Convert.ToHexString (used by the C# caller W2) emits UPPERCASE, and casing does not change
        //    the decoded MAC bytes that FixedTimeEquals actually compares, so rejecting on case would
        //    break W2 for zero security gain. The header must still be exactly the prefix + 64 hex chars.
        if (!TryDecodeSignature(signatureHeader, out var providedMac))
        {
            return false;
        }

        // 4. Build the message bytes: UTF8("v1." + timestamp + ".") ++ rawBodyBytes. Operate on the exact
        //    body bytes; treat a null body as empty (a body-less DELETE).
        var body = rawBody ?? Array.Empty<byte>();
        var prefix = Encoding.UTF8.GetBytes($"{MessageSchemePrefix}{timestampHeader}.");
        var message = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, message, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, message, prefix.Length, body.Length);

        // 5. Constant-time MAC compare against each configured secret; first match resolves the caller.
        //    No configured secrets ⇒ the loop never runs ⇒ reject.
        foreach (var (candidate, secret) in secrets.Configured)
        {
            var key = Encoding.UTF8.GetBytes(secret);
            var computedMac = HMACSHA256.HashData(key, message);
            if (CryptographicOperations.FixedTimeEquals(computedMac, providedMac))
            {
                caller = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates the <c>"v1=" + 64 hex chars</c> shape and hex-decodes the 32 MAC bytes
    /// case-insensitively. Returns <c>false</c> (never throws) on a null value, a missing/short/long
    /// header, or any non-hex character.
    /// </summary>
    private static bool TryDecodeSignature(string signatureHeader, out byte[] mac)
    {
        mac = Array.Empty<byte>();

        if (signatureHeader is null
            || signatureHeader.Length != SignaturePrefix.Length + HexDigestLength
            || !signatureHeader.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hex = signatureHeader.AsSpan(SignaturePrefix.Length);
        foreach (var c in hex)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        // Length + hex-digit validity are already proven, so this cannot throw. Convert.FromHexString is
        // case-insensitive, giving the W2/Convert.ToHexString uppercase interop for free.
        mac = Convert.FromHexString(hex);
        return true;
    }
}
