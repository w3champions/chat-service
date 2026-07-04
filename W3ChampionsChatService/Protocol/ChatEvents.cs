namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Server→client SignalR event-name vocabulary (program contract §1, spec §11, C3-plan.md
/// decision 6). C3 defines the full pinned set now so C4–C7 emit through one vocabulary instead of
/// re-inventing names ad hoc.
/// <see cref="BulkMessagesDeleted"/> (plural) is the NEW pinned name. The un-ported legacy
/// moderation trio (<c>Chats/ChatHub.cs</c> — kept verbatim until C4) still emits the OLD string
/// <c>"BulkMessageDeleted"</c> (singular) directly — do NOT touch that legacy call site here; C4
/// ports it onto this constant (C3-plan.md Open question 5).
/// </summary>
public static class ChatEvents
{
    public const string SessionState = nameof(SessionState);
    public const string MessageReceived = nameof(MessageReceived);
    public const string ChannelActivity = nameof(ChannelActivity);
    public const string ViewersChanged = nameof(ViewersChanged);
    public const string ChannelAdded = nameof(ChannelAdded);
    public const string ChannelRemoved = nameof(ChannelRemoved);
    public const string MessageDeleted = nameof(MessageDeleted);
    public const string BulkMessagesDeleted = nameof(BulkMessagesDeleted);
    public const string PlayerBannedFromChat = nameof(PlayerBannedFromChat);
    public const string ConnectionDisplaced = nameof(ConnectionDisplaced);
    public const string ThrottleNotice = nameof(ThrottleNotice);

    /// <summary>C5: targeted push to the RECIPIENT of a new/resurfaced pending Dm request (a
    /// consent-state transition, not fired on every pending message — the tray is already live via
    /// SessionState). Carries a <c>PendingDmRequestDto</c>.</summary>
    public const string RequestReceived = nameof(RequestReceived);
}
