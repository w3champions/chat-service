using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The hub's JSON payload contract, extracted from <c>Startup</c> as a named unit so it can be unit
/// tested directly (see <c>ChatJsonProtocolTests</c>) instead of only through a DI bootstrap.
/// </summary>
public static class ChatJsonProtocol
{
    /// <summary>
    /// Omits null properties from every hub payload.
    /// <para>
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> — deliberately NOT
    /// <c>WhenWritingDefault</c>, which would also drop <c>false</c> and <c>0</c>. Those are
    /// meaningful for flair: <c>isClassic: false</c> and <c>race: 0</c> (RnD) are real values the
    /// client renders, and dropping them would silently change avatars.
    /// </para>
    /// <para>
    /// Safe for the launcher: every flair field is already declared optional in
    /// <c>chat-protocol.types.ts</c> and every client read path uses optional chaining with a
    /// fallback, so an absent key and an explicit null are indistinguishable there.
    /// </para>
    /// <para>
    /// Rule for new nullable wire fields: because this omits null properties, a <c>null</c> reaching
    /// the launcher is indistinguishable from an absent key. If a field's null value is meaningful
    /// (not just "no value"), either pin it with
    /// <c>[property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c> — as
    /// <see cref="MentionInboxEntryDto.ReadAt"/> is pinned, since null there means "unread" — or make
    /// sure the client reads it with a truthy/nullish check rather than a strict <c>=== null</c>.
    /// This is not a hypothetical: <c>MentionInboxEntryDto.ReadAt</c> shipped unpinned once and the
    /// launcher's strict <c>=== null</c> checks silently broke the unread badge and mention
    /// acknowledgement.
    /// </para>
    /// </summary>
    public static void Configure(JsonHubProtocolOptions options)
    {
        options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }
}
