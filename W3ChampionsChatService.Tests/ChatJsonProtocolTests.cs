using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Pins the hub's JSON payload contract. Flair is the dominant payload on the wire (a roster can be
/// several hundred entries, each carrying a ChatProfile), and most users have null clan/colour/icons
/// and null rank — with nulls serialized, the KEY NAMES alone dominate the payload. Null omission is
/// therefore load-bearing for payload size, not cosmetic.
/// </summary>
public class ChatJsonProtocolTests
{
    private static JsonSerializerOptions ConfiguredOptions()
    {
        var options = new JsonHubProtocolOptions();
        ChatJsonProtocol.Configure(options);
        return options.PayloadSerializerOptions;
    }

    [Test]
    public void Configure_OmitsNullFlairFields()
    {
        var viewer = new ChannelViewerDto("peter#123", "Peter", new ChatProfile
        {
            ProfilePicture = new ProfilePicture { Race = AvatarCategory.HU, PictureId = 2, IsClassic = false },
        });

        var json = JsonSerializer.Serialize(viewer, ConfiguredOptions());

        Assert.That(json, Does.Not.Contain("clanId"), "A null clanId must not occupy wire bytes");
        Assert.That(json, Does.Not.Contain("chatColor"));
        Assert.That(json, Does.Not.Contain("chatIcons"));
        Assert.That(json, Does.Not.Contain("leagueName"));
        Assert.That(json, Does.Not.Contain("gamesPlayed"));
    }

    [Test]
    public void Configure_PreservesNonNullFields()
    {
        var viewer = new ChannelViewerDto("peter#123", "Peter", new ChatProfile
        {
            ClanId = "W3C",
            ProfilePicture = new ProfilePicture { Race = AvatarCategory.NE, PictureId = 7, IsClassic = true },
            ChatColor = new ChatColor("chat_color_purple"),
        });

        var json = JsonSerializer.Serialize(viewer, ConfiguredOptions());

        Assert.That(json, Does.Contain("\"clanId\":\"W3C\""));
        Assert.That(json, Does.Contain("\"pictureId\":7"));
        Assert.That(json, Does.Contain("\"isClassic\":true"));
        Assert.That(json, Does.Contain("chat_color_purple"));
    }

    [Test]
    public void Configure_OmittingNullsDoesNotDropFalseOrZero()
    {
        // WhenWritingNull must not be confused with WhenWritingDefault: `isClassic: false` and
        // `pictureId: 0` are MEANINGFUL values the client renders, not absences.
        var viewer = new ChannelViewerDto("peter#123", "Peter", new ChatProfile
        {
            ProfilePicture = new ProfilePicture { Race = AvatarCategory.RnD, PictureId = 0, IsClassic = false },
        });

        var json = JsonSerializer.Serialize(viewer, ConfiguredOptions());

        Assert.That(json, Does.Contain("\"pictureId\":0"));
        Assert.That(json, Does.Contain("\"isClassic\":false"));
        Assert.That(json, Does.Contain("\"race\":0"));
    }

    // ── Final-review Finding 2a — the config must actually be wired into the app ──────────────────
    //
    // The three tests above build a FRESH JsonHubProtocolOptions and call ChatJsonProtocol.Configure
    // directly — they stay green even if `.AddJsonProtocol(ChatJsonProtocol.Configure)` were deleted
    // from Startup.ConfigureServices. This test instead runs the REAL composition root (mirrors
    // StartupDependencyInjectionTests.BuildProvider) and resolves the JSON hub protocol's options
    // straight from the container, so it fails if the wiring is ever removed.
    //
    // RED/GREEN verified by hand: temporarily deleting the `.AddJsonProtocol(ChatJsonProtocol.Configure)`
    // line from Startup.cs made this test fail with
    //   Expected: WhenWritingNull
    //   But was:  Never
    // (SignalR's own unconfigured JsonHubProtocolOptions default). Restoring the line made it pass again.
    [Test]
    public void Configure_IsWiredIntoStartup()
    {
        var services = new ServiceCollection();
        // AddSignalR needs ILoggerFactory to construct during resolution — mirrors
        // StartupDependencyInjectionTests.BuildProvider.
        services.AddLogging();
        new Startup().ConfigureServices(services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var options = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;

        Assert.AreEqual(
            JsonIgnoreCondition.WhenWritingNull,
            options.PayloadSerializerOptions.DefaultIgnoreCondition,
            "Startup.ConfigureServices MUST wire ChatJsonProtocol.Configure via .AddJsonProtocol — " +
            "deleting that call leaves this option at SignalR's unconfigured default");
    }

    // ── Final-review Finding 2b — a nullable-field sweep beyond ChannelViewerDto ───────────────────
    //
    // ChannelViewerDto was the only type ever round-tripped through the configured options anywhere in
    // the suite — the structural gap that let Finding 1 (MentionInboxEntryDto.ReadAt) ship unnoticed.
    // Each test below round-trips a DTO with a meaningful-when-null field and records, in a comment,
    // why omission is (or is not) safe for that field's client reader.

    [Test]
    public void Configure_MentionInboxEntry_KeepsReadAtOnWireEvenWhenNull_RegressionForFinding1()
    {
        // Finding-1 regression test. Unlike every other DTO in this sweep, omission here is NOT safe:
        // the launcher's chat-mentions.ts / MentionInboxTray.tsx originally used strict `=== null` /
        // `!== null` comparisons against `readAt`, which silently broke (unread badge stuck at 0, toasts
        // suppressed, auto-ack a no-op) once null properties stopped riding the wire. MentionInboxEntryDto
        // now pins ReadAt with [JsonIgnore(Condition = Never)] specifically so a null keeps meaning
        // "unread" ON the wire, in addition to the client now also tolerating an absent key (belt + braces).
        var entry = new MentionInboxEntryDto(
            "id1", "chan1", "msg1", 1, "Peter#123", "Peter", "hi", DateTime.UtcNow, ReadAt: null);

        var json = JsonSerializer.Serialize(entry, ConfiguredOptions());

        Assert.That(json, Does.Contain("\"readAt\":null"),
            "an unread entry's readAt must stay ON the wire as an explicit null, never be omitted");
    }

    [Test]
    public void Configure_PresenceDetails_OmitsNullLastSeenAt()
    {
        var detail = new PresenceDetailsDto("Peter#123", Online: true, LastSeenAt: null);

        var json = JsonSerializer.Serialize(detail, ConfiguredOptions());

        // This assertion only pins that omission happens — it does NOT guarantee client safety, and
        // would keep passing even if a client reader regressed to a strict `=== null` check. Safety
        // today rests entirely on the client: presence-source.ts reads `detail?.lastSeenAt ?? null`
        // and ManageFriends.tsx guards with `lastSeenAt && (...)` — both treat an absent key and an
        // explicit null identically, so omitting a null lastSeenAt changes nothing observable there.
        Assert.That(json, Does.Not.Contain("lastSeenAt"));
    }

    [Test]
    public void Configure_ChannelActivity_OmitsNullPreview()
    {
        var activity = new ChannelActivityDto("chan1", LastSeq: 5, Preview: null);

        var json = JsonSerializer.Serialize(activity, ConfiguredOptions());

        // This assertion only pins that omission happens — it does NOT guarantee client safety, and
        // would keep passing even if a client reader regressed to a strict `=== null` check. Safety
        // today rests entirely on the client: chat-messages.ts gates every read with
        // `if (activity.preview) { ... }` / `if (!activity.preview) return;` — truthy checks, so an
        // absent key and an explicit null are indistinguishable to every reader there.
        Assert.That(json, Does.Not.Contain("preview"));
    }

    [Test]
    public void Configure_MatchChannelActivity_PreviewCarriesItsChannelClassOnTheWire()
    {
        // Post-game chat Plan A Task 6, asserted on the actual wire bytes rather than on the C# record.
        // The preview is no longer Dm-only, so `preview` being present says nothing about which kind of
        // room produced it — channelType/systemKind must ride WITH it or a client is back to inferring
        // "a preview means a DM" and raising a DM-grade toast for every post-game message.
        var activity = new ChannelActivityDto(
            "chan-match",
            LastSeq: 5,
            Preview: new ActivityPreviewDto(
                "Alice#1", "Alice", "gg wp", ChannelType.System, SystemChannelKind.Match));

        var json = JsonSerializer.Serialize(activity, ConfiguredOptions());

        Assert.That(json, Does.Contain("\"preview\""),
            "a match channel's activity must carry a preview so the client has a sender + excerpt to render its nudge");
        Assert.That(json, Does.Contain("\"channelType\""),
            "the preview must name its channel class on the wire — a client must never infer it from the field's presence");
        Assert.That(json, Does.Contain("\"systemKind\""),
            "systemKind is what separates a match room from a clan room on the client");
    }

    [Test]
    public void Configure_SessionState_OmitsNullMuteState()
    {
        var ownProfile = new OwnProfileDto("Peter#123", "Peter", IsAdmin: false, Flair: new ChatProfile(), Permissions: Array.Empty<string>());
        var session = new SessionStateDto(
            Channels: Array.Empty<ChannelDto>(),
            PublicCatalog: Array.Empty<ChatChannel>(),
            PendingDmRequests: Array.Empty<PendingDmRequestDto>(),
            MentionUnreadCount: 0,
            OwnProfile: ownProfile,
            MuteState: null);

        var json = JsonSerializer.Serialize(session, ConfiguredOptions());

        // This assertion only pins that omission happens — it does NOT guarantee client safety, and
        // would keep passing even if a client reader regressed to a strict `=== null` check. Safety
        // today rests entirely on the client: chat-core.ts's applySessionState guards with
        // `if (snapshot.muteState) { ... }` — a truthy check, so an absent key and an explicit null
        // behave identically there.
        Assert.That(json, Does.Not.Contain("muteState"));
    }
}
