using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Primitives;
using Serilog;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// C7's auth-realm boundary for the <c>/internal/*</c> REST surface: an <see cref="IAsyncResourceFilter"/>
/// (resource filters run after routing but BEFORE model binding — framework-guaranteed) that verifies the
/// HMAC signature over the RAW request-body bytes, resolves the caller from which secret verified, enforces
/// the per-endpoint caller allow-list, and rejects with a bare, body-free 401. Registered
/// <c>AddTransient</c> and attached via <see cref="InternalHmacAuthAttribute"/> (an <c>IFilterFactory</c>
/// that stamps <see cref="AllowedCallers"/>); it is disjoint from the SignalR/JWT realm and must attach
/// ONLY to <c>/internal/*</c> controllers.
///
/// <para>Fail-closed contract: every rejection sets <see cref="ResourceExecutingContext.Result"/> to a bare
/// <see cref="UnauthorizedResult"/> and returns WITHOUT calling <c>next()</c>, logging exactly one Serilog
/// warning carrying the request path plus a reason CATEGORY only — never the signature, timestamp value,
/// secret, or body bytes. The resolved caller is stashed in <c>HttpContext.Items["W3C.InternalCaller"]</c>
/// ONLY on full success.</para>
/// </summary>
public class InternalHmacAuthFilter(InternalCallerSecrets secrets, TimeProvider timeProvider) : IAsyncResourceFilter
{
    /// <summary><c>HttpContext.Items</c> key under which the resolved caller is stashed on success, for the
    /// downstream <c>/internal/*</c> controller (Task 9) to read. Set ONLY after a full verify + allow-list
    /// pass — its presence is proof the request is authenticated.</summary>
    public const string InternalCallerItemKey = "W3C.InternalCaller";

    private const string TimestampHeader = "X-W3C-Webhook-Timestamp";
    private const string SignatureHeader = "X-W3C-Signature";

    // Rejection reason CATEGORIES — coarse buckets only, never carrying signature/timestamp/secret/body.
    private const string ReasonMissingHeaders = "missing_headers";
    private const string ReasonBodyOverCap = "body_over_cap";
    private const string ReasonSignatureInvalid = "signature_invalid";
    private const string ReasonCallerNotAllowed = "caller_not_allowed";

    private readonly InternalCallerSecrets _secrets = secrets;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>The callers this endpoint permits (least privilege) — set by
    /// <see cref="InternalHmacAuthAttribute.CreateInstance"/> per request. A valid signature from a caller
    /// NOT in this set is still rejected.</summary>
    public IReadOnlyList<InternalCaller> AllowedCallers { get; set; } = Array.Empty<InternalCaller>();

    /// <summary>Reads the resolved caller stashed on success (see <see cref="InternalCallerItemKey"/>) for
    /// structured logging only — never for authorization (the filter already enforced the allow-list).
    /// Returns <c>null</c> if absent.</summary>
    public static object ResolveCaller(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(InternalCallerItemKey, out var caller) ? caller : null;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;

        // 1. Both headers must be present (and non-blank) BEFORE we touch the body — a missing header can
        //    never verify, so fail closed without reading the body at all.
        if (!TryGetHeader(request, TimestampHeader, out var timestamp)
            || !TryGetHeader(request, SignatureHeader, out var signature))
        {
            Reject(context, ReasonMissingHeaders);
            return;
        }

        // 2. Buffer the raw body so downstream System.Text.Json model binding can re-read the IDENTICAL
        //    bytes after we rewind. Read is HARD-capped: an over-cap body cannot be verified without
        //    unbounded buffering, so it fails closed (never read unbounded into memory).
        request.EnableBuffering(bufferThreshold: ChatLimits.InternalMaxBodyBytes);
        var rawBody = await TryReadCappedBodyAsync(request.Body);
        if (rawBody is null)
        {
            Reject(context, ReasonBodyOverCap);
            return;
        }

        // Rewind so model binding re-reads from the start of the exact bytes the MAC was verified over.
        request.Body.Position = 0;

        // 3. Verify the signature over the raw bytes. Try-pattern gating: on false the out-caller defaults
        //    to a REAL caller (enum 0) — it MUST NOT be read here. Only a true return yields a trusted caller.
        if (!HmacSignatureVerifier.TryResolveCaller(
                rawBody, timestamp, signature, _timeProvider.GetUtcNow().UtcDateTime, _secrets, out var caller))
        {
            Reject(context, ReasonSignatureInvalid);
            return;
        }

        // 4. Least privilege: a valid signature from a caller this endpoint does not permit is rejected.
        if (!AllowedCallers.Contains(caller))
        {
            Reject(context, ReasonCallerNotAllowed);
            return;
        }

        // 5. Full success — stash the trusted caller for the controller, then proceed to model binding.
        context.HttpContext.Items[InternalCallerItemKey] = caller;
        await next();
    }

    private static bool TryGetHeader(HttpRequest request, string name, out string value)
    {
        if (request.Headers.TryGetValue(name, out var values) && !StringValues.IsNullOrEmpty(values))
        {
            value = values.ToString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Reads the request body into a byte[], hard-stopping at <see cref="ChatLimits.InternalMaxBodyBytes"/>.
    /// Returns the exact bytes (empty for a body-less DELETE) when within the cap, or <c>null</c> when the
    /// body exceeds it (never buffers past the cap). Reads through a fixed 8&#160;KB scratch buffer, so
    /// memory is bounded by the cap regardless of a spoofed/absent Content-Length.
    /// </summary>
    private static async Task<byte[]> TryReadCappedBodyAsync(Stream body)
    {
        using var buffered = new MemoryStream();
        var scratch = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(scratch)) > 0)
        {
            if (buffered.Length + read > ChatLimits.InternalMaxBodyBytes)
            {
                return null;
            }

            buffered.Write(scratch, 0, read);
        }

        return buffered.ToArray();
    }

    /// <summary>Fail closed: bare, body-free 401 (no leakage) + exactly one warning carrying only the path
    /// and a reason category. <c>next()</c> is NOT invoked by the caller after this.</summary>
    private static void Reject(ResourceExecutingContext context, string reason)
    {
        Log.Warning(
            "Internal HMAC auth rejected ({Reason}) for {RequestPath}",
            reason,
            context.HttpContext.Request.Path.ToString());
        context.Result = new UnauthorizedResult();
    }
}
