namespace W3ChampionsChatService.Domain;

/// <summary>
/// Spec §5 collection names. All NEW collections use these explicit names via
/// MongoDbRepositoryBase.CreateCollection&lt;T&gt;(name); legacy collections
/// (e.g. LoungeMute) keep the typeof(T).Name convention untouched.
/// </summary>
public static class ChatCollections
{
    public const string Channels = "channels";
    public const string ChannelMemberships = "channel_memberships";
    public const string Messages = "messages";
    public const string UserDirectory = "user_directory";
    public const string UserSettings = "user_settings";
    public const string MentionInbox = "mention_inbox";

    // PR36 follow-up (D2): one doc per (battleTag, channelId) carrying the last EXPLICITLY-set
    // NotificationLevel for a name-joinable room — survives a hard-delete leave/rejoin cycle
    // independently of ChannelMembership's own lifecycle (see NotificationPreferenceRepository).
    public const string NotificationPreferences = "notification_prefs";
}
