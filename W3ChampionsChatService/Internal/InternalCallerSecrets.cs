using System.Collections.Generic;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// C7's env-only per-caller HMAC secret surface (brief Design decision 2: "secrets are env — only
/// limits are hard-coded"). <c>Startup</c> constructs the single production instance from
/// <c>Environment.GetEnvironmentVariable("INTERNAL_SECRET_MM"/"INTERNAL_SECRET_WB")</c> — there is
/// NO fallback default: an unset or blank secret means that caller is disabled, never silently
/// defaulted to an empty or guessable key. Tests construct this directly with literal strings and
/// never touch env vars — the same seam pattern this suite already uses for
/// <see cref="System.TimeProvider"/>.
/// </summary>
public class InternalCallerSecrets
{
    /// <summary>The callers with a configured (non-null, non-whitespace) secret, in Mm-then-Wb
    /// order. Task 2's <c>HmacSignatureVerifier</c> tries each configured entry in turn to resolve
    /// the caller from which secret verifies the request — there is no caller-id header.</summary>
    public IReadOnlyList<(InternalCaller Caller, string Secret)> Configured { get; }

    public InternalCallerSecrets(string mmSecret, string wbSecret)
    {
        var configured = new List<(InternalCaller Caller, string Secret)>();

        if (!string.IsNullOrWhiteSpace(mmSecret))
        {
            configured.Add((InternalCaller.Mm, mmSecret));
        }

        if (!string.IsNullOrWhiteSpace(wbSecret))
        {
            configured.Add((InternalCaller.Wb, wbSecret));
        }

        Configured = configured;
    }
}
