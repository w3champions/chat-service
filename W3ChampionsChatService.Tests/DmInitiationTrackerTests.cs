using System;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C5 Task 3 (D7): the in-memory 8h stranger-DM initiation cap tracker. Pure unit tests — a
/// <see cref="FakeTimeProvider"/> supplies the explicit <c>now</c> each method takes (the tracker never
/// reads a clock itself, mirroring <see cref="ChannelCreationRateLimiter"/>). NUnit constraint style.
/// <para>
/// The tracker is deliberately decision-agnostic: it counts EVERY recorded initiation (including
/// blocked-target and declined ones — the hub Records those the same way) until it ages out at
/// <see cref="ChatLimits.StrangerDmInitiationWindow"/>; only an explicit <see cref="DmInitiationTracker.MarkAccepted"/>
/// frees a pair early. The cap itself (<see cref="ChatLimits.StrangerDmInitiationCap"/>) is applied by the
/// caller against <see cref="DmInitiationTracker.CountActive"/>.
/// </para>
/// </summary>
[TestFixture]
public class DmInitiationTrackerTests
{
    private const string Initiator = "peter#123";
    private static readonly DateTimeOffset T0 = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private static string Target(int i) => $"target{i}#0";

    [Test]
    public void Record_CountsActiveInitiations()
    {
        var tracker = new DmInitiationTracker();
        var now = T0.UtcDateTime;

        tracker.Record(Initiator, Target(1), now);
        tracker.Record(Initiator, Target(2), now);
        tracker.Record(Initiator, Target(3), now);

        Assert.That(tracker.CountActive(Initiator, now), Is.EqualTo(3));
        Assert.That(tracker.CountActive("someone#else", now), Is.EqualTo(0),
            "the count is per-initiator — an unrelated initiator has no active initiations");
    }

    [Test]
    public void TenthInitiationCountsToCap_EleventhWouldBeRejected()
    {
        var tracker = new DmInitiationTracker();
        var now = T0.UtcDateTime;

        for (var i = 0; i < ChatLimits.StrangerDmInitiationCap; i++)
        {
            tracker.Record(Initiator, Target(i), now);
        }

        // The cap is >= (the caller rejects the ELEVENTH when CountActive already equals the cap).
        Assert.That(tracker.CountActive(Initiator, now), Is.EqualTo(ChatLimits.StrangerDmInitiationCap));
        Assert.That(tracker.CountActive(Initiator, now), Is.GreaterThanOrEqualTo(ChatLimits.StrangerDmInitiationCap));
        Assert.That(tracker.RetryAfterSeconds(Initiator, now), Is.GreaterThan(0),
            "at the cap the retry-after is the seconds until the oldest event ages out");
    }

    [Test]
    public void TryRecord_BelowCap_AdmitsAndCounts()
    {
        var tracker = new DmInitiationTracker();
        var now = T0.UtcDateTime;
        const int cap = ChatLimits.StrangerDmInitiationCap;

        for (var i = 0; i < cap; i++)
        {
            Assert.That(tracker.TryRecord(Initiator, Target(i), now, cap), Is.True,
                $"initiation #{i + 1} is within the cap → admitted");
        }

        Assert.That(tracker.CountActive(Initiator, now), Is.EqualTo(cap),
            "every admitted initiation is recorded");
    }

    [Test]
    public void TryRecord_AtCap_RejectsWithoutAppending()
    {
        var tracker = new DmInitiationTracker();
        var now = T0.UtcDateTime;
        const int cap = ChatLimits.StrangerDmInitiationCap;

        for (var i = 0; i < cap; i++)
        {
            Assert.That(tracker.TryRecord(Initiator, Target(i), now, cap), Is.True);
        }

        // At the cap, further attempts are rejected AND must NOT append — the count never grows past the cap
        // no matter how many times TryRecord is called (the atomic check-and-record closes the TOCTOU).
        for (var i = 0; i < 5; i++)
        {
            Assert.That(tracker.TryRecord(Initiator, $"over{i}#0", now, cap), Is.False,
                "an initiation at/over the cap is rejected");
        }

        Assert.That(tracker.CountActive(Initiator, now), Is.EqualTo(cap),
            "rejected initiations are not appended — the count stays pinned at the cap");
    }

    [Test]
    public void MarkAccepted_FreesCapacityInstantly()
    {
        var tracker = new DmInitiationTracker();
        var now = T0.UtcDateTime;

        tracker.Record(Initiator, Target(1), now);
        tracker.Record(Initiator, Target(2), now);
        tracker.Record(Initiator, Target(3), now);

        tracker.MarkAccepted(Initiator, Target(2));

        Assert.That(tracker.CountActive(Initiator, now), Is.EqualTo(2),
            "accepting a pair frees its slot instantly, well before the 8h window");
    }

    [Test]
    public void MarkAccepted_RemovesAllEventsForThePair_CaseInsensitive()
    {
        var tracker = new DmInitiationTracker();
        var now = T0.UtcDateTime;

        // Two initiations to the same pair (e.g. a 30d-expiry resurrection), then accept.
        tracker.Record(Initiator, Target(1), now);
        tracker.Record(Initiator, Target(1), now.AddHours(1));
        tracker.Record(Initiator, Target(2), now);

        tracker.MarkAccepted(Initiator, "TARGET1#0");

        Assert.That(tracker.CountActive(Initiator, now.AddHours(1)), Is.EqualTo(1),
            "MarkAccepted removes EVERY event for the pair (case-insensitively), leaving only the other target");
    }

    [Test]
    public void EventsOlderThan8h_DoNotCount()
    {
        var tracker = new DmInitiationTracker();
        var time = new FakeTimeProvider(T0);

        tracker.Record(Initiator, Target(1), time.GetUtcNow().UtcDateTime);

        time.Advance(ChatLimits.StrangerDmInitiationWindow + TimeSpan.FromMinutes(1));
        Assert.That(tracker.CountActive(Initiator, time.GetUtcNow().UtcDateTime), Is.EqualTo(0),
            "an initiation older than the 8h window has aged out (the expiry-frees-capacity pin)");
        Assert.That(tracker.RetryAfterSeconds(Initiator, time.GetUtcNow().UtcDateTime), Is.EqualTo(0),
            "no active events => no retry-after");

        // A fresh initiation after the window counts again (a re-counted new shell).
        tracker.Record(Initiator, Target(1), time.GetUtcNow().UtcDateTime);
        Assert.That(tracker.CountActive(Initiator, time.GetUtcNow().UtcDateTime), Is.EqualTo(1));
    }

    [Test]
    public void DeclinedAndBlockedInitiations_StillCount()
    {
        // The tracker cannot see WHY an initiation was recorded — blocked-target and later-declined
        // initiations are Recorded identically and count until they age out at 8h.
        var tracker = new DmInitiationTracker();
        var time = new FakeTimeProvider(T0);

        tracker.Record(Initiator, Target(1), time.GetUtcNow().UtcDateTime); // stranger, target blocked the caller
        tracker.Record(Initiator, Target(2), time.GetUtcNow().UtcDateTime); // stranger, later declined
        tracker.Record(Initiator, Target(3), time.GetUtcNow().UtcDateTime);

        time.Advance(ChatLimits.StrangerDmInitiationWindow - TimeSpan.FromMinutes(1)); // still inside the window

        Assert.That(tracker.CountActive(Initiator, time.GetUtcNow().UtcDateTime), Is.EqualTo(3),
            "blocked/declined initiations still count against the cap until the 8h window elapses");
    }

    [Test]
    public void RetryAfterSeconds_ReflectsOldestEventExpiry()
    {
        var tracker = new DmInitiationTracker();
        var now = T0.UtcDateTime;

        tracker.Record(Initiator, Target(1), now);                 // oldest
        tracker.Record(Initiator, Target(2), now.AddHours(2));     // newer

        var probe = now.AddHours(1);
        var expected = (now + ChatLimits.StrangerDmInitiationWindow - probe).TotalSeconds;

        Assert.That(tracker.RetryAfterSeconds(Initiator, probe), Is.EqualTo(expected).Within(1.0),
            "retry-after counts down to when the OLDEST active event ages out");
    }

    [Test]
    public void RetryAfterSeconds_ZeroWhenNoEvents()
    {
        var tracker = new DmInitiationTracker();
        Assert.That(tracker.RetryAfterSeconds(Initiator, T0.UtcDateTime), Is.EqualTo(0));
    }
}
