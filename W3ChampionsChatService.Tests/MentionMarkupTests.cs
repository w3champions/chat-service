using System.Linq;
using NUnit.Framework;
using W3ChampionsChatService.Mentions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C6 Task 1 (D1) — pure mention-markup parser pins. Markup is <c>&lt;@BattleTag#123&gt;</c>
/// (spec §7); <see cref="MentionMarkup.ExtractTags"/> only EXTRACTS distinct tags — the 5-mention
/// cap (<see cref="Domain.ChatLimits.MaxMentionsPerMessage"/>) is enforced by the caller (D2, T2),
/// never here.
/// </summary>
public class MentionMarkupTests
{
    [Test]
    public void ExtractTags_SingleValidToken_ReturnsTag()
    {
        var tags = MentionMarkup.ExtractTags("hey <@Peter#123> check this out");

        Assert.That(tags, Is.EqualTo(new[] { "Peter#123" }));
    }

    [Test]
    public void ExtractTags_DuplicateTags_DedupedCaseInsensitive_FirstOrderPreserved()
    {
        var tags = MentionMarkup.ExtractTags("<@Peter#123> ping <@PETER#123> and <@peter#123> again");

        Assert.That(tags, Is.EqualTo(new[] { "Peter#123" }),
            "case-insensitive duplicates of the same tag must collapse to ONE entry, keeping the " +
            "first occurrence's casing");
    }

    [Test]
    public void ExtractTags_SixDistinctTags_ReturnsAllSix()
    {
        // The parser never caps — enforcing MaxMentionsPerMessage (5) is the caller's job (D2).
        var content = "<@A#1> <@B#2> <@C#3> <@D#4> <@E#5> <@F#6>";

        var tags = MentionMarkup.ExtractTags(content);

        Assert.That(tags, Is.EqualTo(new[] { "A#1", "B#2", "C#3", "D#4", "E#5", "F#6" }));
    }

    [Test]
    public void ExtractTags_MalformedTokens_Ignored()
    {
        // <@> — empty name part (below the 1-char minimum).
        Assert.That(MentionMarkup.ExtractTags("<@>"), Is.Empty);

        // <@NoSuffix> — no trailing #digits.
        Assert.That(MentionMarkup.ExtractTags("<@NoSuffix>"), Is.Empty);

        // <@a#b> — non-numeric suffix.
        Assert.That(MentionMarkup.ExtractTags("<@a#b>"), Is.Empty);

        // Unclosed — no trailing '>' at all.
        Assert.That(MentionMarkup.ExtractTags("<@Peter#123"), Is.Empty);

        // Nested — the inner '<' is excluded from the name-part character class, so neither the
        // outer nor a phantom inner token matches anywhere in the string.
        Assert.That(MentionMarkup.ExtractTags("<@<Peter#123>>"), Is.Empty);
    }

    [Test]
    public void ExtractTags_PlainTextAndEmails_NoFalsePositives()
    {
        var tags = MentionMarkup.ExtractTags("contact user@host#1 or visit example.com, no markup here");

        Assert.That(tags, Is.Empty, "an email-like token outside <@...> must never be mistaken for a mention");
    }

    [Test]
    public void ExtractTags_AdjacentTokens_AllFound()
    {
        var tags = MentionMarkup.ExtractTags("<@Peter#123><@Wolf#456>");

        Assert.That(tags, Is.EqualTo(new[] { "Peter#123", "Wolf#456" }));
    }

    [Test]
    public void ExtractTags_NullOrEmptyContent_ReturnsEmpty()
    {
        Assert.That(MentionMarkup.ExtractTags(null), Is.Empty);
        Assert.That(MentionMarkup.ExtractTags(""), Is.Empty);
    }

    [Test]
    public void ExtractTags_NoMarkup_ReturnsEmpty()
    {
        Assert.That(MentionMarkup.ExtractTags("just a plain message with no mentions"), Is.Empty);
    }

    [Test]
    public void ExtractTags_ReturnsDistinctOrderPreservingList_NotJustAnySet()
    {
        // Guards the "first-occurrence order" contract explicitly, independent of the dedupe test
        // above (which only exercises a single repeated tag).
        var tags = MentionMarkup.ExtractTags("<@Zed#9> <@Ann#1> <@Zed#9> <@Mid#5>");

        Assert.That(tags.ToList(), Is.EqualTo(new[] { "Zed#9", "Ann#1", "Mid#5" }));
    }
}
