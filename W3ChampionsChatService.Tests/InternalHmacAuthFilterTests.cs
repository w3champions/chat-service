using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Internal;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C7 Task 3: the <c>[InternalHmacAuth]</c> resource filter — the auth-realm boundary that verifies
/// the HMAC signature over the RAW request body BEFORE model binding, resolves the caller, enforces a
/// per-endpoint caller allow-list, and rejects with a bare 401. Exercised with the repo's no-TestServer
/// idiom: a hand-built <see cref="DefaultHttpContext"/> wrapped in a <see cref="ResourceExecutingContext"/>
/// (resource filters run after routing but BEFORE model binding — framework-guaranteed) with a
/// <see cref="ResourceExecutionDelegate"/> whose invocation is probed. NUnit constraint style; a shared
/// <see cref="FakeTimeProvider"/> is the trusted-clock seam (same pattern as the hub suites). The pinned
/// M1 byte-compat vectors are REUSED verbatim from <see cref="HmacSignatureVerifierTests"/>.
/// </summary>
[TestFixture]
public class InternalHmacAuthFilterTests
{
    private const string TimestampHeaderName = "X-W3C-Webhook-Timestamp";
    private const string SignatureHeaderName = "X-W3C-Signature";
    private const string ItemKey = "W3C.InternalCaller";

    private const string Secret = "test-secret";
    private const string PinnedTimestamp = "1751500000";

    // Pinned M1 CREATE vector — rawBody bytes EXACTLY (see HmacSignatureVerifierTests vector block).
    private const string CreateBody =
        "{\"kind\":\"match\",\"ref\":\"abc123XYZ0\",\"name\":\"Test Lobby\",\"members\":[\"Foo#1234\",\"Bar#5678\"]}";
    private const string CreateSignature =
        "v1=b0acb9b2ba23a8aaf0076c05cd1c9631ac88364dfcebe61352c220f9009e54cd";

    // Pinned M1 empty-body DELETE vector — signs the string "v1.1751500000." with rawBody = "".
    private const string DeleteSignature =
        "v1=09b6a138e0b80b2d6c4fa412590abcc352953b7e43ba15479020161e944f47a3";

    /// <summary>The instant the pinned vectors were signed at (Δ == 0, trivially inside the window).</summary>
    private static readonly DateTimeOffset PinnedInstant = DateTimeOffset.FromUnixTimeSeconds(1751500000);
    private static readonly TimeSpan Window = ChatLimits.InternalSignatureFreshnessWindow;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static InternalCallerSecrets MmOnly() => new(Secret, null);
    private static InternalCallerSecrets WbOnly() => new(null, Secret);
    private static byte[] CreateBodyBytes() => Encoding.UTF8.GetBytes(CreateBody);

    /// <summary>Match-channel CREATE DTO shape for the model-binding re-read proof.</summary>
    private sealed record ChannelBodyDto(string Kind, string Ref, string Name, string[] Members);

    // ── Success paths ───────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ValidMmSignature_InvokesNext_AndStampsCaller()
    {
        var http = BuildHttpContext(CreateBodyBytes(), PinnedTimestamp, CreateSignature);

        var (ctx, nextCalled) = await RunFilter(http, MmOnly(), new[] { InternalCaller.Mm }, PinnedInstant);

        Assert.That(nextCalled, Is.True, "a valid, allow-listed signature must invoke next()");
        Assert.That(ctx.Result, Is.Null, "next() ran — the filter must not set a short-circuit result");
        Assert.That(http.Items.ContainsKey(ItemKey), Is.True, "the resolved caller must be stashed for the controller");
        Assert.That(http.Items[ItemKey], Is.EqualTo(InternalCaller.Mm));
    }

    [Test]
    public async Task ValidSignature_LeavesBodyReadableForModelBinding()
    {
        // THE load-bearing test: a NON-seekable body proves the EnableBuffering + rewind is doing real
        // work. Without EnableBuffering the forward-only stream could not be rewound and downstream model
        // binding would read nothing; with it, System.Text.Json re-reads the IDENTICAL bytes at position 0.
        var http = BuildHttpContext(CreateBodyBytes(), PinnedTimestamp, CreateSignature, seekable: false);

        var (_, nextCalled) = await RunFilter(http, MmOnly(), new[] { InternalCaller.Mm }, PinnedInstant);

        Assert.That(nextCalled, Is.True);
        Assert.That(http.Request.Body.Position, Is.EqualTo(0), "the filter must rewind the body for model binding");

        var dto = await JsonSerializer.DeserializeAsync<ChannelBodyDto>(http.Request.Body, JsonOptions);

        Assert.That(dto, Is.Not.Null, "model binding must be able to re-read the rewound body");
        Assert.That(dto!.Kind, Is.EqualTo("match"));
        Assert.That(dto.Ref, Is.EqualTo("abc123XYZ0"));
        Assert.That(dto.Name, Is.EqualTo("Test Lobby"));
        Assert.That(dto.Members, Is.EqualTo(new[] { "Foo#1234", "Bar#5678" }));
    }

    [Test]
    public async Task EmptyBodyDelete_PinnedVector_Verifies()
    {
        // Empty-body DELETE: yields byte[0], the verifier signs "v1." + ts + "." with no body. Pins the
        // M1 empty-body vector THROUGH the filter (headers present, zero-length body, next invoked).
        var http = BuildHttpContext(Array.Empty<byte>(), PinnedTimestamp, DeleteSignature,
            method: "DELETE", path: "/internal/channels/abc123XYZ0");

        var (ctx, nextCalled) = await RunFilter(http, MmOnly(), new[] { InternalCaller.Mm }, PinnedInstant);

        Assert.That(nextCalled, Is.True, "the pinned empty-body DELETE vector must verify and invoke next()");
        Assert.That(ctx.Result, Is.Null);
        Assert.That(http.Items[ItemKey], Is.EqualTo(InternalCaller.Mm));
    }

    // ── Rejection paths (all bare-401, next NOT invoked, no caller stashed) ────────────────────────

    [TestCase(null, CreateSignature, TestName = "MissingTimestamp_401_WithoutBodyRead")]
    [TestCase(PinnedTimestamp, null, TestName = "MissingSignature_401_WithoutBodyRead")]
    [TestCase(null, null, TestName = "MissingBothHeaders_401_WithoutBodyRead")]
    public async Task MissingHeaders_401_WithoutBodyRead(string timestamp, string signature)
    {
        // The body stream THROWS if read — proving the header check short-circuits before any body access.
        var http = BuildHttpContext(new ThrowOnReadStream(), timestamp, signature);

        var (ctx, nextCalled) = await RunFilter(http, MmOnly(), new[] { InternalCaller.Mm }, PinnedInstant);

        AssertRejected(ctx, nextCalled, http);
    }

    [Test]
    public async Task WrongSecret_401()
    {
        var wrong = new InternalCallerSecrets("not-the-real-secret", null);
        var http = BuildHttpContext(CreateBodyBytes(), PinnedTimestamp, CreateSignature);

        var (ctx, nextCalled) = await RunFilter(http, wrong, new[] { InternalCaller.Mm }, PinnedInstant);

        AssertRejected(ctx, nextCalled, http);
    }

    [Test]
    public async Task TamperedBody_401()
    {
        var tampered = CreateBodyBytes();
        tampered[10] ^= 0x01;
        var http = BuildHttpContext(tampered, PinnedTimestamp, CreateSignature);

        var (ctx, nextCalled) = await RunFilter(http, MmOnly(), new[] { InternalCaller.Mm }, PinnedInstant);

        AssertRejected(ctx, nextCalled, http);
    }

    [Test]
    public async Task StaleTimestamp_401()
    {
        // now = signedAt + window + 1s → one second past the freshness window.
        var now = PinnedInstant + Window + TimeSpan.FromSeconds(1);
        var http = BuildHttpContext(CreateBodyBytes(), PinnedTimestamp, CreateSignature);

        var (ctx, nextCalled) = await RunFilter(http, MmOnly(), new[] { InternalCaller.Mm }, now);

        AssertRejected(ctx, nextCalled, http);
    }

    [Test]
    public async Task FutureTimestamp_401()
    {
        // now = signedAt − window − 1s → the request is dated too far in the future.
        var now = PinnedInstant - Window - TimeSpan.FromSeconds(1);
        var http = BuildHttpContext(CreateBodyBytes(), PinnedTimestamp, CreateSignature);

        var (ctx, nextCalled) = await RunFilter(http, MmOnly(), new[] { InternalCaller.Mm }, now);

        AssertRejected(ctx, nextCalled, http);
    }

    [Test]
    public async Task CallerNotInAllowList_401()
    {
        // The cross-caller least-privilege case: a cryptographically VALID wb signature presented to an
        // Mm-only filter. The secret verifies (resolves Wb), but Wb ∉ AllowedCallers ⇒ 401, next NOT run.
        var http = BuildHttpContext(CreateBodyBytes(), PinnedTimestamp, CreateSignature);

        var (ctx, nextCalled) = await RunFilter(http, WbOnly(), new[] { InternalCaller.Mm }, PinnedInstant);

        AssertRejected(ctx, nextCalled, http);
    }

    [Test]
    public async Task BodyOverCap_401()
    {
        // One byte over the hard cap ⇒ cannot verify without unbounded buffering ⇒ fail closed (401),
        // rejected before signature verification even runs.
        var oversized = new byte[ChatLimits.InternalMaxBodyBytes + 1];
        var http = BuildHttpContext(oversized, PinnedTimestamp, CreateSignature);

        var (ctx, nextCalled) = await RunFilter(http, MmOnly(), new[] { InternalCaller.Mm }, PinnedInstant);

        AssertRejected(ctx, nextCalled, http);
    }

    [Test]
    public async Task BodyAtCap_IsNotRejectedForSize()
    {
        // The exact-cap boundary is INSIDE the limit: a body of exactly InternalMaxBodyBytes must not be
        // rejected for size. It fails the signature check (random bytes), but the point is it reaches
        // verification at all rather than being size-rejected — proves the cap is inclusive, not off-by-one.
        var atCap = new byte[ChatLimits.InternalMaxBodyBytes];
        var http = BuildHttpContext(atCap, PinnedTimestamp, CreateSignature);

        var (ctx, nextCalled) = await RunFilter(http, MmOnly(), new[] { InternalCaller.Mm }, PinnedInstant);

        // Still a 401 (bad signature over random bytes) but it was read in full and rewound, not size-capped.
        Assert.That(nextCalled, Is.False);
        Assert.That(http.Request.Body.Position, Is.EqualTo(0), "an at-cap body must still be fully read and rewound");
    }

    [TestCase(RejectCase.MissingHeaders)]
    [TestCase(RejectCase.WrongSecret)]
    [TestCase(RejectCase.TamperedBody)]
    [TestCase(RejectCase.StaleTimestamp)]
    [TestCase(RejectCase.FutureTimestamp)]
    [TestCase(RejectCase.CallerNotAllowed)]
    [TestCase(RejectCase.BodyOverCap)]
    public async Task Next_NotInvoked_OnAnyRejection(RejectCase which)
    {
        var (http, secrets, allowed, now) = BuildRejectionScenario(which);

        var (ctx, nextCalled) = await RunFilter(http, secrets, allowed, now);

        AssertRejected(ctx, nextCalled, http);
    }

    // ── Attribute → filter factory wiring (the allow-list must flow from attribute to filter) ──────

    [Test]
    public void Attribute_IsFilterFactory_ResolvesFilter_AndCarriesAllowedCallers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(MmOnly());
        services.AddSingleton(TimeProvider.System);
        services.AddTransient<InternalHmacAuthFilter>();
        using var provider = services.BuildServiceProvider();

        var attribute = new InternalHmacAuthAttribute(InternalCaller.Wb, InternalCaller.Mm);
        var filter = attribute.CreateInstance(provider);

        Assert.That(filter, Is.InstanceOf<InternalHmacAuthFilter>());
        Assert.That(((InternalHmacAuthFilter)filter).AllowedCallers,
            Is.EqualTo(new[] { InternalCaller.Wb, InternalCaller.Mm }),
            "the factory must transfer the attribute's allow-list onto the resolved filter");
        Assert.That(attribute.IsReusable, Is.False, "a fresh filter per request (mirrors UserHasPermissionAttribute)");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    public enum RejectCase
    {
        MissingHeaders,
        WrongSecret,
        TamperedBody,
        StaleTimestamp,
        FutureTimestamp,
        CallerNotAllowed,
        BodyOverCap,
    }

    private static (DefaultHttpContext Http, InternalCallerSecrets Secrets, InternalCaller[] Allowed, DateTimeOffset Now)
        BuildRejectionScenario(RejectCase which)
    {
        switch (which)
        {
            case RejectCase.MissingHeaders:
                return (BuildHttpContext(new ThrowOnReadStream(), null, null), MmOnly(), new[] { InternalCaller.Mm }, PinnedInstant);
            case RejectCase.WrongSecret:
                return (BuildHttpContext(CreateBodyBytes(), PinnedTimestamp, CreateSignature),
                    new InternalCallerSecrets("not-the-real-secret", null), new[] { InternalCaller.Mm }, PinnedInstant);
            case RejectCase.TamperedBody:
                var tampered = CreateBodyBytes();
                tampered[10] ^= 0x01;
                return (BuildHttpContext(tampered, PinnedTimestamp, CreateSignature), MmOnly(), new[] { InternalCaller.Mm }, PinnedInstant);
            case RejectCase.StaleTimestamp:
                return (BuildHttpContext(CreateBodyBytes(), PinnedTimestamp, CreateSignature), MmOnly(),
                    new[] { InternalCaller.Mm }, PinnedInstant + Window + TimeSpan.FromSeconds(1));
            case RejectCase.FutureTimestamp:
                return (BuildHttpContext(CreateBodyBytes(), PinnedTimestamp, CreateSignature), MmOnly(),
                    new[] { InternalCaller.Mm }, PinnedInstant - Window - TimeSpan.FromSeconds(1));
            case RejectCase.CallerNotAllowed:
                return (BuildHttpContext(CreateBodyBytes(), PinnedTimestamp, CreateSignature), WbOnly(),
                    new[] { InternalCaller.Mm }, PinnedInstant);
            case RejectCase.BodyOverCap:
                return (BuildHttpContext(new byte[ChatLimits.InternalMaxBodyBytes + 1], PinnedTimestamp, CreateSignature),
                    MmOnly(), new[] { InternalCaller.Mm }, PinnedInstant);
            default:
                throw new ArgumentOutOfRangeException(nameof(which));
        }
    }

    /// <summary>A bare-401 rejection: next() NOT invoked, the result is a body-free
    /// <see cref="UnauthorizedResult"/>, and no caller was stashed.</summary>
    private static void AssertRejected(ResourceExecutingContext ctx, bool nextCalled, DefaultHttpContext http)
    {
        Assert.That(nextCalled, Is.False, "a rejection must NOT invoke next()");
        Assert.That(ctx.Result, Is.TypeOf<UnauthorizedResult>(), "rejection must be a bare, body-free 401");
        Assert.That(http.Items.ContainsKey(ItemKey), Is.False, "no caller may be stashed on a rejection");
    }

    private static async Task<(ResourceExecutingContext Ctx, bool NextCalled)> RunFilter(
        DefaultHttpContext http, InternalCallerSecrets secrets, InternalCaller[] allowed, DateTimeOffset now)
    {
        var filter = new InternalHmacAuthFilter(secrets, new FakeTimeProvider(now)) { AllowedCallers = allowed };
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var ctx = new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());

        var nextCalled = false;
        ResourceExecutionDelegate next = () =>
        {
            nextCalled = true;
            return Task.FromResult(new ResourceExecutedContext(actionContext, ctx.Filters));
        };

        await filter.OnResourceExecutionAsync(ctx, next);
        return (ctx, nextCalled);
    }

    private static DefaultHttpContext BuildHttpContext(
        byte[] body, string timestamp, string signature, string method = "POST", string path = "/internal/channels", bool seekable = true)
        => BuildHttpContext(seekable ? new MemoryStream(body) : new ForwardOnlyStream(body), timestamp, signature, method, path);

    private static DefaultHttpContext BuildHttpContext(
        Stream body, string timestamp, string signature, string method = "POST", string path = "/internal/channels")
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        if (timestamp != null)
        {
            http.Request.Headers[TimestampHeaderName] = timestamp;
        }
        if (signature != null)
        {
            http.Request.Headers[SignatureHeaderName] = signature;
        }
        http.Request.Body = body;
        return http;
    }

    /// <summary>A forward-only, non-seekable read stream — the production request-body shape. Forces
    /// <c>EnableBuffering</c> to do real work: without it, the body could not be rewound for model binding.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body stream that throws the moment it is read — proves the missing-header check
    /// short-circuits BEFORE any body access.</summary>
    private sealed class ThrowOnReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
            => throw new InvalidOperationException("body must not be read when a required header is missing");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("body must not be read when a required header is missing");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
