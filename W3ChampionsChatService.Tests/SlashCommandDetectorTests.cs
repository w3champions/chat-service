using NUnit.Framework;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// The canonical slash-command case table (design §4). This table is mirrored by the launcher's own
/// detector at <c>launcher-e/src/helpers/chat-command.helper.ts</c> — behaviourally, on all printable
/// input, NOT byte-for-byte: .NET and JavaScript disagree about <c>\s</c> for U+0085 and U+FEFF, which
/// is why the two detectors differ on one position for two code points. See the divergence note on
/// <see cref="SlashCommandDetector"/> for the exact set and why both directions degrade safely.
/// A change here without the matching change there means the client and the server disagree about what
/// a command is.
/// </summary>
public class SlashCommandDetectorTests
{
    [TestCase("/w Grubby hi")]
    [TestCase("/whisper Grubby hi")]
    [TestCase("/r thanks")]
    [TestCase("/reply ok")]
    [TestCase("/join channel")]
    [TestCase("/me dances")]
    [TestCase("/stats")]
    [TestCase("/ю привет")]
    public void IsSlashCommand_CommandShapedContent_True(string content)
    {
        Assert.That(SlashCommandDetector.IsSlashCommand(content), Is.True);
    }

    [TestCase("/usr/local/bin")]
    [TestCase("//note")]
    [TestCase("/")]
    [TestCase("/ 10 gold")]
    [TestCase("/10 min")]
    [TestCase("10/10 game")]
    [TestCase("gg /w hi")]
    [TestCase("gg wp")]
    public void IsSlashCommand_OrdinaryContent_False(string content)
    {
        Assert.That(SlashCommandDetector.IsSlashCommand(content), Is.False);
    }

    [TestCase(null)]
    [TestCase("")]
    public void IsSlashCommand_NullOrEmpty_False(string content)
    {
        Assert.That(SlashCommandDetector.IsSlashCommand(content), Is.False);
    }

    [Test]
    public void IsSlashCommand_LeadingWhitespace_False_BecauseCallerTrimsFirst()
    {
        Assert.That(
            SlashCommandDetector.IsSlashCommand("   /w hi"),
            Is.False,
            "the detector is anchored and deliberately does NOT trim — SendMessage step 2 trims the " +
            "content before step 4.5 calls this. Do not add a Trim() here; add it at the caller if a " +
            "new caller ever needs it.");
    }
}
