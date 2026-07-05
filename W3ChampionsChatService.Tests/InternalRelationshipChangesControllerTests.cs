using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Internal;
using W3ChampionsChatService.Relationships;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C7 Task 10 — the website-backend change-ping surface (<c>POST /internal/relationship-changes</c>),
/// the FIRST production caller of C5's <see cref="IRelationshipProvider.Invalidate"/> seam. Constructed
/// directly (the no-TestServer controller idiom shared with <see cref="InternalChannelsControllerTests"/>)
/// against a Moq <see cref="IRelationshipProvider"/> double — no Mongo/Testcontainers, since the contract
/// under test is purely "which <c>Invalidate</c> calls fire, and when". Coverage pins:
/// <list type="bullet">
/// <item>a valid change-ping drops the cache entry for BOTH actor AND target, exactly once each — even
/// when they are equal (idempotent; the provider's global <c>_version</c> stamp makes the double drop
/// race-safe);</item>
/// <item>every one of the four EXACT wire literals is accepted, and an unknown / wrong-CASE type is a 400
/// that calls <c>Invalidate</c> for nobody (the literal strings are the contract, not an enum converter);</item>
/// <item>a blank or control-char-bearing actor/target is a 400 with NO invalidation — the log-injection
/// guard the Task 9 review flagged on <c>ref</c>, held here for a trusted-but-signed caller too.</item>
/// </list>
/// The H1 dynamic realm-disjointness sweep in <see cref="InternalChannelsControllerTests"/> now also
/// covers this controller automatically; the reflection test here is the Task-10-specific Wb-only pin.
/// </summary>
public class InternalRelationshipChangesControllerTests
{
    private Mock<IRelationshipProvider> _relationshipProvider;
    private InternalRelationshipChangesController _controller;

    [SetUp]
    public void SetupBeforeEach()
    {
        _relationshipProvider = new Mock<IRelationshipProvider>();
        _controller = new InternalRelationshipChangesController(_relationshipProvider.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static InternalRelationshipChangeRequest ValidRequest(
        string type = "block", string actor = "Actor#1", string target = "Target#2") =>
        new() { Type = type, Actor = actor, Target = target };

    private static void AssertBadRequest(IActionResult result)
    {
        var badRequest = result as BadRequestObjectResult;
        Assert.That(badRequest, Is.Not.Null, "validation failures must be a 400 (BadRequestObjectResult)");
        Assert.That(badRequest.Value, Is.InstanceOf<ErrorResult>(), "a 400 body must be the generic ErrorResult shape");
    }

    private void AssertNoInvalidation() =>
        _relationshipProvider.Verify(p => p.Invalidate(It.IsAny<string>()), Times.Never,
            "a rejected change-ping must NOT drop any cache entry");

    // ── valid change-pings ──────────────────────────────────────────────────────────────────────

    [Test]
    public void Post_Block_InvalidatesActorAndTarget_ExactlyOnceEach()
    {
        var result = _controller.Post(ValidRequest());

        Assert.That(result, Is.InstanceOf<OkResult>(), "a valid change-ping returns a body-free 200");
        _relationshipProvider.Verify(p => p.Invalidate("Actor#1"), Times.Once);
        _relationshipProvider.Verify(p => p.Invalidate("Target#2"), Times.Once);
        _relationshipProvider.VerifyNoOtherCalls();
    }

    [TestCase("block")]
    [TestCase("unblock")]
    [TestCase("friendAdd")]
    [TestCase("friendRemove")]
    public void Post_EachValidType_Accepted(string type)
    {
        var result = _controller.Post(ValidRequest(type: type));

        Assert.That(result, Is.InstanceOf<OkResult>(), $"'{type}' is an exact wire literal and must be accepted");
        _relationshipProvider.Verify(p => p.Invalidate("Actor#1"), Times.Once);
        _relationshipProvider.Verify(p => p.Invalidate("Target#2"), Times.Once);
    }

    [Test]
    public void Post_ActorEqualsTarget_InvalidatedTwiceHarmlessly()
    {
        // Both invalidations always fire, even for a self-ping — idempotent by the provider's global
        // _version stamp; the controller never special-cases actor == target.
        var result = _controller.Post(ValidRequest(actor: "Self#1", target: "Self#1"));

        Assert.That(result, Is.InstanceOf<OkResult>());
        _relationshipProvider.Verify(p => p.Invalidate("Self#1"), Times.Exactly(2),
            "actor and target are invalidated independently, so an equal pair drops the same entry twice (harmless)");
    }

    // ── type validation (exact, case-sensitive, no enum converter) ───────────────────────────────

    [TestCase("Block")]        // wrong case — case-sensitivity guard
    [TestCase("BLOCK")]
    [TestCase("friendadd")]    // wrong case
    [TestCase("friend_add")]
    [TestCase("remove")]
    [TestCase("")]
    [TestCase((string)null)]
    [TestCase("  block  ")]     // no trimming — the literal must match exactly
    public void Post_UnknownType_400_NoInvalidateCalls(string type)
    {
        var result = _controller.Post(ValidRequest(type: type));

        AssertBadRequest(result);
        AssertNoInvalidation();
    }

    // ── actor/target: non-blank ──────────────────────────────────────────────────────────────────

    [TestCase(null, "Target#2")]
    [TestCase("Actor#1", null)]
    [TestCase("", "Target#2")]
    [TestCase("Actor#1", "")]
    [TestCase("   ", "Target#2")]
    [TestCase("Actor#1", "   ")]
    public void Post_BlankActorOrTarget_400_NoInvalidateCalls(string actor, string target)
    {
        var result = _controller.Post(ValidRequest(actor: actor, target: target));

        AssertBadRequest(result);
        AssertNoInvalidation();
    }

    // ── actor/target: no control characters (log-injection guard) ────────────────────────────────

    [TestCase("Actor\n#1", "Target#2")]  // newline in actor
    [TestCase("Actor#1", "Target\n#2")]  // newline in target
    [TestCase("Actor\r#1", "Target#2")]  // carriage return
    [TestCase("Actor#1", "Target\t#2")]  // tab
    [TestCase("Actor\0#1", "Target#2")] // NUL
    [TestCase("Actor#1 \u2028[FATAL] fake alert", "Target#2")]  // U+2028 LINE SEPARATOR — not char.IsControl, but must still be rejected
    [TestCase("Actor#1", "Target#2 \u2029[FATAL] fake alert")] // U+2029 PARAGRAPH SEPARATOR — same class of gap
    public void Post_ControlCharInActorOrTarget_400_NoInvalidateCalls(string actor, string target)
    {
        var result = _controller.Post(ValidRequest(actor: actor, target: target));

        AssertBadRequest(result);
        AssertNoInvalidation();
    }

    [Test]
    public void Post_NullBody_400_NoInvalidateCalls()
    {
        var result = _controller.Post(null);

        AssertBadRequest(result);
        AssertNoInvalidation();
    }

    // ── H1 realm-disjointness (Task-10-specific Wb-only pin) ─────────────────────────────────────

    [Test]
    public void Controller_DeclaresHmacAttribute_WbOnly_NoUserHasPermission()
    {
        var type = typeof(InternalRelationshipChangesController);

        var hmac = type.GetCustomAttribute<InternalHmacAuthAttribute>();
        Assert.That(hmac, Is.Not.Null,
            "the change-ping controller must be HMAC-gated at class level (H1)");
        Assert.That(hmac.AllowedCallers, Is.EqualTo(new[] { InternalCaller.Wb }),
            "InternalRelationshipChangesController must allow EXACTLY Wb (least privilege) — the change-ping is website-backend's");

        Assert.That(type.GetCustomAttribute<UserHasPermissionAttribute>(), Is.Null,
            "internal/* controllers live in the HMAC realm, never the JWT/permission realm");
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.That(method.GetCustomAttribute<UserHasPermissionAttribute>(), Is.Null,
                $"{type.Name}.{method.Name} must not carry [UserHasPermission] — the two auth realms must stay disjoint");
        }
    }
}
